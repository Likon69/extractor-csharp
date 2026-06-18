using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MaNGOS.Extractor.Formats.Vmap.Mangos;

/// <summary>
/// Helpers for writing the MaNGOS vmap-extractor intermediate artifacts:
///   - <c>Buildings/{md5-name}</c>: the same .vmo/.vmd bytes, but with the
///     legacy <c>GetUniformName</c> filename so the TileAssembler can pick
///     them up and copy them into <c>vmaps/&lt;path&gt;.vmo</c>.
///   - <c>Buildings/dir_bin</c>: a binary index of every WMO/M2 placement,
///     written incrementally per ADT tile (MaNGOS opens it in "ab" mode).
///   - <c>vmaps/temp_gameobject_models</c>: a per-displayId list of every
///     gameobject model that successfully extracted (MaNGOS C++ writes this
///     from <c>model.cpp::ExtractGameobjectModels</c>).
///
/// All formats mirror the C++ byte-for-byte; see MaNGOS <c>vmapexport.cpp</c>,
/// <c>wmo.cpp::WMOInstance</c>, <c>model.cpp::ModelInstance</c>, and
/// <c>vec3d.h::fixCoords</c>.
/// </summary>
public static class MangosVmapBuildingWriter
{
    // ────────────────────────────────────────────────────────────────────
    //  Filename helpers (MaNGOS vmapexport.cpp::GetUniformName)
    //
    //  C++: returns md5(directory_part) + "-" + filename, all lowercase.
    //  When the path has no directory, md5("\\") is used.
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MaNGOS vmapexport.cpp::GetUniformName — md5 hex of the directory part,
    /// then "-", then the file's basename, all lowercase. Empty directory
    /// becomes md5("\\").
    /// </summary>
    public static string GetUniformName(string path)
    {
        // Lowercase first, like the C++ does before splitting
        string lower = path.ToLowerInvariant();

        int sep = lower.LastIndexOfAny(new[] { '/', '\\' });
        string dirPart, filePart;
        if (sep >= 0)
        {
            dirPart  = lower.Substring(0, sep);
            filePart = lower.Substring(sep + 1);
        }
        else
        {
            dirPart  = "";
            filePart = lower;
        }

        string dirMd5 = string.IsNullOrEmpty(dirPart) ? Md5Hex("\\") : Md5Hex(dirPart);
        return dirMd5 + "-" + filePart;
    }

    private static string Md5Hex(string s)
    {
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(Encoding.ASCII.GetBytes(s), hash);
        var sb = new StringBuilder(32);
        for (int i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    // ────────────────────────────────────────────────────────────────────
    //  fixCoords — MaNGOS vec3d.h: return (v.z, v.x, v.y). No negation, no
    //  32G offset on the position itself; the TileAssembler adds the offset
    //  to the bbox only (see TileAssembler.cpp:135-138, where the iPos
    //  += Vector3(533.33333f*32, ...) line is commented out as a TODO).
    // ────────────────────────────────────────────────────────────────────

    public static (float X, float Y, float Z) FixCoords(float x, float y, float z)
        => (z, x, y);

    // ────────────────────────────────────────────────────────────────────
    //  dir_bin record flags (MaNGOS ModelInstance.h)
    // ────────────────────────────────────────────────────────────────────

    public const uint MOD_M2          = 1u << 0; // 0x00000001
    public const uint MOD_WORLDSPAWN  = 1u << 1; // 0x00000002
    public const uint MOD_HAS_BOUND   = 1u << 2; // 0x00000004

    // The C++ vmap-extractor writes the special "worldspawn" tile (65, 65)
    // for WMOs and M2s that are referenced from the WDT (not from any ADT).
    public const uint WorldspawnTileX = 65u;
    public const uint WorldspawnTileY = 65u;

    // ────────────────────────────────────────────────────────────────────
    //  dir_bin writer
    //
    //  MaNGOS C++ opens Buildings/dir_bin in "ab" (append-binary) mode from
    //  ADT::init (adtfile.cpp:66) and writes one record per placement, in
    //  the order the placements are encountered in MDDF then MODF.
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Append a WMO placement record to <paramref name="dirBinPath"/>.
    /// Mirrors the write block in MaNGOS <c>wmo.cpp::WMOInstance</c> lines
    /// 654-668. Includes the WMO's local AABB (bound_min / bound_max) in
    /// fixCoords space and the WmoInstName as a length-prefixed cstring
    /// (no null terminator on disk).
    /// </summary>
    public static void AppendWmoRecord(
        string dirBinPath,
        uint mapId, uint tileX, uint tileY,
        uint adtId, uint uniqueId,
        float posX, float posY, float posZ,        // raw WoW world position
        float rotX, float rotY, float rotZ,        // raw WoW rotation
        float scale,                                // 1.0 for WMO
        float boundMinX, float boundMinY, float boundMinZ,
        float boundMaxX, float boundMaxY, float boundMaxZ,
        string wmoInstName)                         // uniform name (md5-file)
    {
        // MaNGOS: "if (x == 0 && z == 0) { pos.x = pos.z = 533.33333f * 32; }"
        // (handles terrain maps where ADT-side pos is zero on the X/Z axes).
        if (posX == 0f && posZ == 0f)
        {
            const float HalfWorld = 533.33333f * 32f;
            posX = HalfWorld;
            posZ = HalfWorld;
        }

        var (fx, fy, fz) = FixCoords(posX, posY, posZ);
        var (loX, loY, loZ) = FixCoords(boundMinX, boundMinY, boundMinZ);
        var (hiX, hiY, hiZ) = FixCoords(boundMaxX, boundMaxY, boundMaxZ);

        uint flags = MOD_HAS_BOUND;
        if (tileX == WorldspawnTileX && tileY == WorldspawnTileY)
            flags |= MOD_WORLDSPAWN;

        using var fs = new FileStream(dirBinPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var bw = new BinaryWriter(fs);
        bw.Write(mapId);
        bw.Write(tileX);
        bw.Write(tileY);
        bw.Write(flags);
        bw.Write((ushort)adtId);
        bw.Write(uniqueId);
        bw.Write(fx); bw.Write(fy); bw.Write(fz);
        bw.Write(rotX); bw.Write(rotY); bw.Write(rotZ);
        bw.Write(scale);
        bw.Write(loX); bw.Write(loY); bw.Write(loZ);
        bw.Write(hiX); bw.Write(hiY); bw.Write(hiZ);
        byte[] nameBytes = Encoding.ASCII.GetBytes(wmoInstName);
        bw.Write((uint)nameBytes.Length);
        bw.Write(nameBytes);
    }

    /// <summary>
    /// Append an M2 placement record to <paramref name="dirBinPath"/>.
    /// Mirrors the write block in MaNGOS <c>model.cpp::ModelInstance</c>:
    /// 50 bytes + length-prefixed uniform name. No bbox is recorded for M2s
    /// (MaNGOS computes it at runtime from the .vmd file via
    /// <c>calculateTransformedBound</c>).
    /// </summary>
    public static void AppendM2Record(
        string dirBinPath,
        uint mapId, uint tileX, uint tileY,
        uint adtId, uint uniqueId,
        float posX, float posY, float posZ,        // raw WoW world position
        float rotX, float rotY, float rotZ,        // raw WoW rotation
        float scale,                                // MDDF scale (already /1024 for WotLK)
        string m2InstName)                          // uniform name (md5-file)
    {
        var (fx, fy, fz) = FixCoords(posX, posY, posZ);

        uint flags = MOD_M2;
        if (tileX == WorldspawnTileX && tileY == WorldspawnTileY)
            flags |= MOD_WORLDSPAWN;

        using var fs = new FileStream(dirBinPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var bw = new BinaryWriter(fs);
        bw.Write(mapId);
        bw.Write(tileX);
        bw.Write(tileY);
        bw.Write(flags);
        bw.Write((ushort)adtId);
        bw.Write(uniqueId);
        bw.Write(fx); bw.Write(fy); bw.Write(fz);
        bw.Write(rotX); bw.Write(rotY); bw.Write(rotZ);
        bw.Write(scale);
        byte[] nameBytes = Encoding.ASCII.GetBytes(m2InstName);
        bw.Write((uint)nameBytes.Length);
        bw.Write(nameBytes);
    }

    // ────────────────────────────────────────────────────────────────────
    //  temp_gameobject_models writer
    //
    //  MaNGOS C++ (model.cpp:309) opens Buildings/temp_gameobject_models in
    //  "wb" once and writes for every GameObjectDisplayInfo row that
    //  successfully extracted: displayId (u32) | path_length (u32) | name.
    //  The C# vmap-extractor also writes a copy under vmaps/ to match the
    //  layout the user has in their reference output.
    // ────────────────────────────────────────────────────────────────────

    public sealed class GameObjectModelsWriter : IDisposable
    {
        private readonly Stream _fs;
        private readonly BinaryWriter _bw;

        public GameObjectModelsWriter(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            // The C++ opens with "wb" (truncates if exists). The user expects
            // this file to be regenerated each run.
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _bw = new BinaryWriter(_fs);
        }

        public void Append(uint displayId, string uniformName)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(uniformName);
            _bw.Write(displayId);
            _bw.Write((uint)nameBytes.Length);
            _bw.Write(nameBytes);
        }

        public void Dispose()
        {
            _bw.Dispose();
            _fs.Dispose();
        }
    }
}
