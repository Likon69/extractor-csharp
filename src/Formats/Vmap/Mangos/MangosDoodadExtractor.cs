using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;
using MaNGOS.Extractor.Formats.Adt.Models;
using MaNGOS.Extractor.Formats.Wmo.Math;
using MaNGOS.Extractor.Formats.Wmo.Models;

namespace MaNGOS.Extractor.Formats.Vmap.Mangos;

/// <summary>
/// Faithful port of MaNGOS vmap-extractor/model.cpp::Doodad::ExtractSet.
///
/// For every WMO placement found in an ADT's MODF chunk, the C++ also
/// writes one dir_bin M2 record per doodad the WMO references. The doodad
/// is transformed from the WMO's local frame into world space using the
/// WMO's position/rotation (MaNGOS coord convention fixCoords = (z,x,y)).
/// The doodad's own quaternion is composed on top of the WMO rotation and
/// converted back to the ZYX-with-axis-swap Euler angles the runtime
/// expects (ModelInstance.cpp::fromWMORot).
///
/// This writer is the only thing that produces these "doodad" entries in
/// <c>Buildings/dir_bin</c>; without it, the mmap-extractor never knows
/// the M2 collision meshes embedded inside a WMO exist.
/// </summary>
public static class MangosDoodadExtractor
{
    /// <summary>
    /// Counter shared with the C++ <c>uniqueObjectIds</c> map. Reset by the
    /// extractor host between runs so each invocation starts at 1. The C++
    /// inserts in encounter order across all maps / ADTs / WMOs, so byte-for-
    /// byte parity requires running in the exact same order; the C# mirrors
    /// the C++ within a single map pass.
    /// </summary>
    public static uint GlobalUniqueIdCounter = 0;

    private static readonly Dictionary<(string Wmo, ushort DoodadId), uint> _uniqueIds = new();

    /// <summary>
    /// Reset the global counter (call once per extractor run, before the
    /// first map).
    /// </summary>
    public static void ResetUniqueIds()
    {
        GlobalUniqueIdCounter = 0;
        _uniqueIds.Clear();
    }

    /// <summary>
    /// MaNGOS vmapexport.cpp::GenerateUniqueObjectId: deterministic id
    /// derived from (wmoUniformName, doodadId). Insertion order in the map
    /// is the same as the C++ map, so the returned counter matches.
    /// </summary>
    public static uint GenerateUniqueObjectId(string wmoUniformName, ushort doodadId)
    {
        var key = (wmoUniformName, doodadId);
        if (_uniqueIds.TryGetValue(key, out var existing))
            return existing;
        GlobalUniqueIdCounter++;
        _uniqueIds[key] = GlobalUniqueIdCounter;
        return GlobalUniqueIdCounter;
    }

    /// <summary>
    /// Append the doodad set of one WMO placement to <paramref name="dirBinPath"/>.
    /// Mirrors <c>model.cpp::Doodad::ExtractSet</c> lines 514-612.
    /// </summary>
    /// <param name="dirBinPath">Path to Buildings/dir_bin (append mode).</param>
    /// <param name="mapId">Map ID of the ADT.</param>
    /// <param name="tileX">Tile X of the ADT.</param>
    /// <param name="tileY">Tile Y of the ADT.</param>
    /// <param name="wmoUniformName">Output of <c>GetUniformName(wmoPath)</c>.</param>
    /// <param name="modf">MODF placement (carries wmoPos / wmoRot / doodadset).</param>
    /// <param name="root">Parsed WMO root (provides Paths / Sets / Spawns).</param>
    /// <param name="doodadReferences">Merged set of uint16 indices into Spawns from every loaded group.</param>
    /// <param name="tryExtractModel">
    /// Callback that resolves a raw doodad model path (from the MODN block)
    /// to a uniform file name AND writes the .vmd to Buildings/ + vmaps/.
    /// Returns the uniform name on success, or null if the doodad's M2 file
    /// is missing / failed to extract (the C++ then skips the record).
    /// </param>
    public static void ExtractSet(
        string dirBinPath,
        uint mapId, uint tileX, uint tileY,
        string wmoUniformName,
        AdtModf modf,
        WmoRootFile root,
        ushort[] doodadReferences,
        Func<string, string?> tryExtractModel)
    {
        if (root.DoodadSets.Length == 0
            || root.DoodadPlacements.Length == 0
            || doodadReferences.Length == 0)
            return;

        // WMO transform in the server's coordinate system. wmoPos has already
        // been run through fixCoords (see WMOInstance ctor); wmoRot is the
        // raw triplet of Euler angles (degrees) consumed by fromWMORot using
        // the ZYX-with-axis-swap convention that matches ModelInstance.cpp.
        //
        // WMOInstance ctor (wmo.cpp:629-643) applies a worldspawn fix BEFORE
        // fixCoords: if raw pos.x == 0 && pos.z == 0, set both to 533.33333*32
        // so the WMO is anchored at the centre of the map. Mirror that here
        // so the doodad's worldPos stays consistent with the WMO's iPos written
        // by MangosVmapExtractorService.BuildWmoSpawn.
        float wmoRawX = modf.PositionX;
        float wmoRawY = modf.PositionY;
        float wmoRawZ = modf.PositionZ;
        if (wmoRawX == 0f && wmoRawZ == 0f)
        {
            const float HalfWorld = 533.33333f * 32f;
            wmoRawX = HalfWorld;
            wmoRawZ = HalfWorld;
        }
        var wmoPos = FixCoords(wmoRawX, wmoRawY, wmoRawZ);
        var wmoRot = WmoRotationMath.FromWmoRot(modf.RotationX, modf.RotationY, modf.RotationZ);

        // Pick the active doodad set. 0 is the default; only extract set 0
        // unless a valid, in-range alternative is requested by the WMO
        // instance (the doodadset field is the upper 16 bits of the packed
        // flags+doodadset word in MODF, exposed as AdtModf.DoodadSet).
        uint activeSetIndex = 0;
        if (modf.DoodadSet > 0 && (uint)modf.DoodadSet < (uint)root.DoodadSets.Length)
            activeSetIndex = modf.DoodadSet;
        var activeSet = root.DoodadSets[activeSetIndex];
        uint setEnd = activeSet.FirstDoodadIndex + activeSet.DoodadCount;

        ushort doodadId = 0;
        foreach (var refIdx in doodadReferences)
        {
            if (refIdx < activeSet.FirstDoodadIndex || refIdx >= setEnd) continue;
            if (refIdx >= root.DoodadPlacements.Length) continue;

            var doodad = root.DoodadPlacements[refIdx];

            // Resolve the doodad's model file name from the MODN block using
            // NameIndex as a byte offset into the raw MODN bytes (the C++
            // stores the raw block in WMODoodadData::Paths and reads it with
            // &doodadData.Paths[doodad.NameIndex()]).
            string? modelPath = ReadCString(root.DoodadPaths, doodad.NameIndex);
            if (string.IsNullOrEmpty(modelPath)) continue;

            // Extract the underlying M2 (handles .mdl/.mdx -> .m2 conversion,
            // uniform naming, existence check, 1-vertex filter). The callback
            // is TryBuildM2Async from MangosVmapExtractorService.
            string? fixedName = tryExtractModel(modelPath);
            if (string.IsNullOrEmpty(fixedName)) continue;

            // World position = WMO world position + WMO rotation * doodad local pos.
            var localPos = new Vector3(doodad.PositionX, doodad.PositionY, doodad.PositionZ);
            var worldOffset = WmoRotationMath.Mul(wmoRot, localPos);
            var worldPos = new Vector3(
                wmoPos.X + worldOffset.X,
                wmoPos.Y + worldOffset.Y,
                wmoPos.Z + worldOffset.Z);

            // World rotation: apply the doodad's quaternion on top of the
            // WMO's rotation. In matrix form: R_total = R_doodad * R_wmo,
            // which means "first rotate by wmo, then by doodad" when reading
            // right-to-left. We then convert back to the ZYX-with-axis-swap
            // Euler convention so the downstream ModelSpawn reader
            // interprets it correctly.
            var dMat = WmoRotationMath.QuatToMat3(
                doodad.RotationX, doodad.RotationY, doodad.RotationZ, doodad.RotationW);
            var finalMat = WmoRotationMath.Mul(dMat, wmoRot);
            var finalRot = WmoRotationMath.ToWmoRot(finalMat);

            float scale = doodad.Scale;
            uint uniqueId = GenerateUniqueObjectId(wmoUniformName, doodadId);
            doodadId++;

            // Write the entry in dir_bin in exactly the same format as
            // ModelInstance: no bound (it's an M2), MOD_M2 flag, no
            // nameSet/adtId. Flags = MOD_M2 + MOD_WORLDSPAWN for the
            // special global tile (MaNGOS C++ uses 65,65 for WMO worldspawn).
            uint flags = MangosVmapBuildingWriter.MOD_M2;
            if (tileX == MangosVmapBuildingWriter.WorldspawnTileX
                && tileY == MangosVmapBuildingWriter.WorldspawnTileY)
            {
                flags |= MangosVmapBuildingWriter.MOD_WORLDSPAWN;
            }
            const ushort adtId = 0;

            byte[] nameBytes = Encoding.ASCII.GetBytes(fixedName);
            using var fs = new FileStream(dirBinPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var bw = new BinaryWriter(fs);
            bw.Write(mapId);
            bw.Write(tileX);
            bw.Write(tileY);
            bw.Write(flags);
            bw.Write(adtId);
            bw.Write(uniqueId);
            bw.Write(worldPos.X); bw.Write(worldPos.Y); bw.Write(worldPos.Z);
            bw.Write(finalRot.RotX); bw.Write(finalRot.RotY); bw.Write(finalRot.RotZ);
            bw.Write(scale);
            bw.Write((uint)nameBytes.Length);
            bw.Write(nameBytes);
        }
    }

    private static Vector3 FixCoords(float x, float y, float z) => new(z, x, y);

    /// <summary>
    /// Read a null-terminated ASCII string from <paramref name="data"/>
    /// starting at <paramref name="offset"/>. Mirrors the C++ pattern
    /// <c>&amp;doodadData.Paths[doodad.NameIndex()]</c>: NameIndex is a byte
    /// offset into the raw MODN block, not an array index.
    /// </summary>
    private static string? ReadCString(byte[] data, uint offset)
    {
        if (data == null || offset >= data.Length) return null;
        int end = (int)offset;
        while (end < data.Length && data[end] != 0) end++;
        if (end == offset) return null;
        return Encoding.ASCII.GetString(data, (int)offset, end - (int)offset);
    }
}
