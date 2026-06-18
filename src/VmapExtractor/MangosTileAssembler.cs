using System.IO;
using MaNGOS.Extractor.Formats.M2;
using MaNGOS.Extractor.Formats.Vmap.Mangos;
using MaNGOS.Extractor.Formats.Vmap.Mangos.G3d;
using Microsoft.Extensions.Logging;

namespace MaNGOS.Extractor.VmapExtractor;

/// <summary>
/// Phase-2 of the Mangos vmap-extractor: a faithful C# port of
/// <c>TileAssembler</c> (mangostwo-server/src/game/vmap/TileAssembler.{h,cpp}).
///
/// The C++ extractor runs in two phases:
///   1. <c>vmapexport.cpp</c> walks the ADT/WDT and appends every WMO/M2
///      placement to <c>Buildings/dir_bin</c>, plus dumps raw model geometry
///      into <c>Buildings/{uniform_name}</c>.
///   2. <c>TileAssembler::convertWorld2</c> reads <c>dir_bin</c> back,
///      computes per-model bounds for M2s (<c>calculateTransformedBound</c>),
///      builds the per-map BIH tree (<c>BIH::build</c>), and writes the
///      final <c>.vmtree</c> (BIH + global spawns) and <c>.vmtile</c> files
///      (per-tile spawns with BIH-referenced indices).
///
/// The C# <c>MangosVmapExtractorService</c> already does phase 1 inline while
/// walking the ADT. This class implements phase 2 over the already-written
/// <c>dir_bin</c>, so the C# output matches the C++ byte-for-byte.
/// </summary>
internal sealed class MangosTileAssembler
{
    // MOD_* flags (MaNGOS ModelInstance.h:45-50). Mirrored here to avoid a
    // cross-dependency on MangosVmapBuildingWriter (which is itself a port of
    // the C++ writer); keeping the constants local makes this class readable.
    private const uint MOD_M2 = 0x1;
    private const uint MOD_WORLDSPAWN = 0x2;
    private const uint MOD_HAS_BOUND = 0x4;

    // MaNGOS StaticMapTree::packTileID (MapTree.h:108) — tileX << 16 | tileY.
    // The "global" tile (65, 65) is reserved for WDT-level worldspawn entries
    // (maps without terrain).
    private const uint GlobalTileX = 65u;
    private const uint GlobalTileY = 65u;

    // TileAssembler.cpp:137 — worldspawn bound offset. The C++ uses
    // 533.33333f * 32f for both X and Y (float literals).
    private const float WorldspawnOffsetXY = 533.33333f * 32f;

    // G3D::pi() — MaNGOS converts degrees to radians with pi * deg / 180.
    private const float Pi = 3.14159265358979323846f;

    private readonly string _sourceDir;   // dir_bin + Buildings/ location
    private readonly string _destDir;     // vmaps/ output root
    private readonly ILogger _logger;
    private readonly M2Parser _m2Parser;

    // For each uniform M2 name, the model-local bounding vertices (read once
    // from Buildings/{uniform}). Cached to avoid re-reading the .vmd for every
    // M2 placement of the same model.
    private readonly Dictionary<string, G3dVector3[]> _m2VerticesCache =
        new(StringComparer.OrdinalIgnoreCase);

    public MangosTileAssembler(string sourceDir, string destDir, M2Parser m2Parser, ILogger logger)
    {
        _sourceDir = sourceDir;
        _destDir = destDir;
        _m2Parser = m2Parser;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Public entry point — convert one map's dir_bin into .vmtree + .vmtile
    //  Mirrors TileAssembler::convertWorld2 for a single map (the C++ loops
    //  over all maps in one run; the C# processes maps one at a time).
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read this map's entries from <c>dir_bin</c>, build the BIH, and write
    /// the per-map <c>.vmtree</c> plus all per-tile <c>.vmtile</c> files.
    /// </summary>
    /// <param name="mapId">Map id used for the output filenames (NNN.vmtree,
    ///   NNN_XX_YY.vmtile).</param>
    /// <param name="mapName">Map internal name (used for the output path
    ///   <c>vmaps/{mapName}/</c>).</param>
    /// <returns>true on success.</returns>
    public bool AssembleMap(uint mapId, string mapName)
    {
        // readMapSpawns — TileAssembler.cpp:279-341 (filtered to this map only)
        if (!TryReadMapSpawns(mapId, out var uniqueEntries, out var tileEntries))
        {
            _logger.LogWarning("[TileAssembler] No spawns for map {MapId} in dir_bin", mapId);
            // Fall back to writing an empty vmtree so downstream consumers
            // don't crash on a missing file.
            WriteEmptyVmtree(mapId, mapName, isTiled: true);
            return false;
        }

        // convertWorld2 — TileAssembler.cpp:116-197 (build mapSpawns + bound fixes)
        var mapSpawns = new List<MangosModelSpawn>(uniqueEntries.Count);
        foreach (var entry in uniqueEntries.Values)
        {
            // Copy into a local so we can mutate it (struct semantics: the
            // foreach variable is treated as const by the C# compiler, but a
            // local copy can be modified and re-added).
            var spawn = entry;

            if ((spawn.Flags & MOD_M2) != 0)
            {
                // M2 spawns have no bound in dir_bin — recompute from the model
                // geometry. C++ aborts the whole map on failure (break). We
                // match that: skip the rest of the spawns and proceed with what
                // we have so far.
                if (!CalculateTransformedBound(ref spawn))
                {
                    _logger.LogWarning("[TileAssembler] calculateTransformedBound failed for M2 {Name}, aborting spawn accumulation", spawn.Name);
                    break;
                }
            }
            else if ((spawn.Flags & MOD_WORLDSPAWN) != 0)
            {
                // WMO maps and terrain maps use different origin — shift the
                // bound (NOT iPos, that line is commented out in the C++).
                // TileAssembler.cpp:137.
                var offset = new G3dVector3(WorldspawnOffsetXY, WorldspawnOffsetXY, 0f);
                var shifted = new G3dAaBox
                {
                    Lo = new G3dVector3(spawn.BoundLow![0], spawn.BoundLow[1], spawn.BoundLow[2]) + offset,
                    Hi = new G3dVector3(spawn.BoundHigh![0], spawn.BoundHigh[1], spawn.BoundHigh[2]) + offset,
                };
                spawn.BoundLow = new[] { shifted.Lo.X, shifted.Lo.Y, shifted.Lo.Z };
                spawn.BoundHigh = new[] { shifted.Hi.X, shifted.Hi.Y, shifted.Hi.Z };
            }
            mapSpawns.Add(spawn);
        }

        // BIH::build — TileAssembler.cpp:143-145
        var bih = new Bih();
        bih.Build(mapSpawns, GetSpawnBounds);

        // modelNodeIdx — TileAssembler.cpp:148-152. Maps spawn.ID → its index
        // in mapSpawns (= BIH objects[] order). Used by .vmtile to reference
        // the BIH leaf that owns this spawn.
        var modelNodeIdx = new Dictionary<uint, uint>(mapSpawns.Count);
        for (int i = 0; i < mapSpawns.Count; i++)
        {
            modelNodeIdx[mapSpawns[i].Id] = (uint)i;
        }

        // isTiled — TileAssembler.cpp:170-172. globalRange = TileEntries.equal_range((65,65)).
        // isTiled = (globalRange is empty) → true means "no worldspawn, has terrain".
        uint globalTileId = PackTileId(GlobalTileX, GlobalTileY);
        bool isTiled = !tileEntries.Any(kv => kv.TileId == globalTileId);

        // Global spawns (those on tile 65,65) — TileAssembler.cpp:187-195.
        var globalSpawns = new List<MangosModelSpawn>();
        foreach (var te in tileEntries)
        {
            if (te.TileId == globalTileId && uniqueEntries.TryGetValue(te.SpawnId, out var gs))
            {
                globalSpawns.Add(gs);
            }
        }

        // Write the .vmtree — TileAssembler.cpp:155-197.
        // The C++ writes the file directly under iDestDir (output/vmaps/),
        // using the zero-padded mapId as the filename. No per-map subdirectory.
        string vmtreePath = Path.Combine(_destDir, MangosVmapTree.GetFileName(mapId));
        Directory.CreateDirectory(_destDir); // ensure vmaps/ exists on first run
        MangosVmapTree.Write(vmtreePath, isTiled, bih, globalSpawns);

        // Write the per-tile .vmtile files — TileAssembler.cpp:202-246
        WriteVmtiles(mapId, mapName, uniqueEntries, tileEntries, modelNodeIdx, globalTileId);

        return true;
    }

    private void WriteEmptyVmtree(uint mapId, string mapName, bool isTiled)
    {
        string vmtreePath = Path.Combine(_destDir, MangosVmapTree.GetFileName(mapId));
        Directory.CreateDirectory(_destDir); // ensure vmaps/ exists on first run
        var bih = new Bih(); // InitEmpty state — treeSize=3, count=0
        MangosVmapTree.Write(vmtreePath, isTiled, bih, Array.Empty<MangosModelSpawn>());
    }

    // ─────────────────────────────────────────────────────────────────────
    //  readMapSpawns — TileAssembler.cpp:279-341
    //  Reads dir_bin sequentially and partitions by mapId. We keep only the
    //  entries for the requested mapId (the C++ stores all maps then iterates;
    //  the C# processes maps one at a time, so we filter as we read).
    // ─────────────────────────────────────────────────────────────────────

    private bool TryReadMapSpawns(uint mapId,
        out SortedDictionary<uint, MangosModelSpawn> uniqueEntries,
        out List<(uint TileId, uint SpawnId)> tileEntries)
    {
        uniqueEntries = new SortedDictionary<uint, MangosModelSpawn>();
        tileEntries = new List<(uint, uint)>();

        string dirBinPath = Path.Combine(_sourceDir, "dir_bin");
        if (!File.Exists(dirBinPath)) return false;

        using var fs = File.OpenRead(dirBinPath);
        using var br = new BinaryReader(fs);

        // TileAssembler.cpp:296-335 — loop until EOF.
        while (br.BaseStream.Position + 12 <= br.BaseStream.Length)
        {
            // Read mapID, tileX, tileY. The C++ checks check == 0 after the
            // first read to detect EOF; we check remaining bytes upfront.
            uint recMapId = br.ReadUInt32();
            uint tileX = br.ReadUInt32();
            uint tileY = br.ReadUInt32();

            // ModelSpawn::ReadFromFile — ModelInstance.cpp:207-253
            if (!TryReadSpawn(br, out var spawn))
            {
                // Corrupt record — stop reading this map (the C++ breaks).
                break;
            }

            // readMapSpawns is for ALL maps; we keep only the current one.
            if (recMapId != mapId) continue;

            // UniqueEntries.insert({ID, spawn}) — std::map dedupes by key.
            // The C++ uses insert() which does NOT overwrite existing entries;
            // we mimic that semantics.
            if (!uniqueEntries.ContainsKey(spawn.Id))
            {
                uniqueEntries.Add(spawn.Id, spawn);
            }
            // TileEntries.insert({packTileID(tileX,tileY), spawn.ID}) — multimap.
            // Ordered by tileId then insertion order. We keep insertion order
            // within a tile via a List, which matches multimap iteration.
            tileEntries.Add((PackTileId(tileX, tileY), spawn.Id));
        }

        return uniqueEntries.Count > 0;
    }

    /// <summary>ModelSpawn::ReadFromFile — ModelInstance.cpp:207-253.</summary>
    private static bool TryReadSpawn(BinaryReader br, out MangosModelSpawn spawn)
    {
        spawn = default;
        if (br.BaseStream.Position + 4 > br.BaseStream.Length) return false;

        uint flags = br.ReadUInt32();
        if (br.BaseStream.Position + 2 + 4 + 3 * 4 + 3 * 4 + 4 > br.BaseStream.Length) return false;

        ushort adtId = br.ReadUInt16();
        uint id = br.ReadUInt32();
        var pos = new float[3];
        for (int i = 0; i < 3; i++) pos[i] = br.ReadSingle();
        var rot = new float[3];
        for (int i = 0; i < 3; i++) rot[i] = br.ReadSingle();
        float scale = br.ReadSingle();

        float[]? boundLow = null, boundHigh = null;
        if ((flags & MOD_HAS_BOUND) != 0)
        {
            boundLow = new float[3];
            boundHigh = new float[3];
            for (int i = 0; i < 3; i++) boundLow[i] = br.ReadSingle();
            for (int i = 0; i < 3; i++) boundHigh[i] = br.ReadSingle();
        }

        if (br.BaseStream.Position + 4 > br.BaseStream.Length) return false;
        uint nameLen = br.ReadUInt32();
        if (nameLen > 1024) return false;
        if (br.BaseStream.Position + nameLen > br.BaseStream.Length) return false;
        var nameBytes = br.ReadBytes((int)nameLen);
        string name = System.Text.Encoding.ASCII.GetString(nameBytes);

        spawn = new MangosModelSpawn
        {
            Flags = flags,
            AdtId = adtId,
            Id = id,
            Pos = pos,
            Rot = rot,
            Scale = scale,
            BoundLow = boundLow,
            BoundHigh = boundHigh,
            Name = name,
        };
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  calculateTransformedBound — TileAssembler.cpp:351-414
    //  Loads the raw M2 vertices, applies scale then rotation
    //  (ModelPosition::transform), computes the AABB, then adds iPos and
    //  sets MOD_HAS_BOUND. The M2 vertices are loaded from Buildings/ via
    //  the M2Parser (which reads the bounding collision mesh).
    // ─────────────────────────────────────────────────────────────────────

    private bool CalculateTransformedBound(ref MangosModelSpawn spawn)
    {
        // Resolve the model's local-space bounding vertices. spawn.Name holds
        // the uniform name (md5-basename) written into dir_bin by the phase-1
        // writer. We can read the M2 directly from the MPQ via the parser.
        if (!_m2VerticesCache.TryGetValue(spawn.Name, out var vertices))
        {
            // Reverse-resolve the MPQ path from the uniform name: the uniform
            // name is md5(dir) + "-" + basename; we cannot trivially invert
            // the md5, so we re-read the .vmd collision mesh from Buildings/.
            // That mesh is in local M2 space — exactly what calculateTransformed
            // bound needs. If the .vmd is missing, fall back to skipping the
            // bound (the spawn stays without MOD_HAS_BOUND, like a failed
            // raw_model.Read in the C++).
            var wm = MangosVmoReader.Read(Path.Combine(_sourceDir, spawn.Name));
            if (wm == null || wm.Groups.Count == 0 || wm.Groups[0].Vertices.Length == 0)
            {
                // No geometry — leave the spawn without a bound (C++ returns
                // false here, caller breaks; we keep the spawn but unbound).
                return false;
            }
            var vs = new G3dVector3[wm.Groups[0].Vertices.Length];
            for (int i = 0; i < vs.Length; i++)
            {
                var v = wm.Groups[0].Vertices[i];
                vs[i] = new G3dVector3(v.X, v.Y, v.Z);
            }
            vertices = vs;
            _m2VerticesCache[spawn.Name] = vs;
        }

        // ModelPosition::init — TileAssembler.cpp:357-360
        //   iRotation = Matrix3::fromEulerAnglesZYX(pi*y/180, pi*x/180, pi*z/180)
        // We precompute the combined Z*Y*X matrix once for this spawn and
        // apply it to every vertex.
        float yRad = Pi * spawn.Rot[1] / 180f;
        float xRad = Pi * spawn.Rot[0] / 180f;
        float zRad = Pi * spawn.Rot[2] / 180f;
        GetRotationMatrixZyx(yRad, xRad, zRad, out float m00, out float m01, out float m02,
                                            out float m10, out float m11, out float m12,
                                            out float m20, out float m21, out float m22);

        // TileAssembler.cpp:376-408 — accumulate the AABB over all vertices,
        // transformed by ModelPosition::transform (scale, then rotation).
        bool boundEmpty = true;
        float blo0 = 0, blo1 = 0, blo2 = 0, bhi0 = 0, bhi1 = 0, bhi2 = 0;
        for (int i = 0; i < vertices.Length; i++)
        {
            // out = pIn * iScale  (then out = iRotation * out)
            float sx = vertices[i].X * spawn.Scale;
            float sy = vertices[i].Y * spawn.Scale;
            float sz = vertices[i].Z * spawn.Scale;
            // iRotation * out  (Matrix3 * Vector3, row-major)
            float tx = m00 * sx + m01 * sy + m02 * sz;
            float ty = m10 * sx + m11 * sy + m12 * sz;
            float tz = m20 * sx + m21 * sy + m22 * sz;

            if (boundEmpty)
            {
                blo0 = bhi0 = tx;
                blo1 = bhi1 = ty;
                blo2 = bhi2 = tz;
                boundEmpty = false;
            }
            else
            {
                if (tx < blo0) blo0 = tx; else if (tx > bhi0) bhi0 = tx;
                if (ty < blo1) blo1 = ty; else if (ty > bhi1) bhi1 = ty;
                if (tz < blo2) blo2 = tz; else if (tz > bhi2) bhi2 = tz;
            }
        }

        // spawn.iBound = modelBound + spawn.iPos  (TileAssembler.cpp:411)
        spawn.BoundLow = new[]
        {
            blo0 + spawn.Pos[0],
            blo1 + spawn.Pos[1],
            blo2 + spawn.Pos[2],
        };
        spawn.BoundHigh = new[]
        {
            bhi0 + spawn.Pos[0],
            bhi1 + spawn.Pos[1],
            bhi2 + spawn.Pos[2],
        };
        spawn.Flags |= MOD_HAS_BOUND;
        return true;
    }

    /// <summary>
    /// Compute ZYX Euler rotation matrix as M = kZMat * (kYMat * kXMat), where
    /// each kXMat/kYMat/kZMat is built exactly as in
    /// <c>Matrix3::fromEulerAnglesZYX</c> (Matrix3.cpp:1655-1672). The nine
    /// out parameters receive the row-major 3x3 result elt[row][col].
    /// </summary>
    private static void GetRotationMatrixZyx(
        float fYAngle, float fPAngle, float fRAngle,
        out float m00, out float m01, out float m02,
        out float m10, out float m11, out float m12,
        out float m20, out float m21, out float m22)
    {
        // kZMat = [cos -sin 0; sin cos 0; 0 0 1]
        float zc = MathF.Cos(fYAngle), zs = MathF.Sin(fYAngle);
        // kYMat = [cos 0 sin; 0 1 0; -sin 0 cos]
        float yc = MathF.Cos(fPAngle), ys = MathF.Sin(fPAngle);
        // kXMat = [1 0 0; 0 cos -sin; 0 sin cos]
        float xc = MathF.Cos(fRAngle), xs = MathF.Sin(fRAngle);

        // kYMat * kXMat — row-major
        float yx00 = yc * 1f + 0f * 0f + ys * 0f;       // = yc
        float yx01 = yc * 0f + 0f * xc + ys * xs;       // = ys*xs
        float yx02 = yc * 0f + 0f * (-xs) + ys * xc;    // = ys*xc
        float yx10 = 0f;                                // row 1 of kYMat = [0 1 0]
        float yx11 = xc;
        float yx12 = -xs;
        float yx20 = -ys * 1f + 0f + yc * 0f;           // = -ys
        float yx21 = -ys * 0f + 0f + yc * xs;           // = yc*xs
        float yx22 = -ys * 0f + 0f + yc * xc;           // = yc*xc

        // kZMat * (kYMat * kXMat) — row-major
        m00 = zc * yx00 + (-zs) * yx10 + 0f * yx20;
        m01 = zc * yx01 + (-zs) * yx11 + 0f * yx21;
        m02 = zc * yx02 + (-zs) * yx12 + 0f * yx22;
        m10 = zs * yx00 + zc * yx10 + 0f * yx20;
        m11 = zs * yx01 + zc * yx11 + 0f * yx21;
        m12 = zs * yx02 + zc * yx12 + 0f * yx22;
        m20 = 0f * yx00 + 0f * yx10 + 1f * yx20;
        m21 = 0f * yx01 + 0f * yx11 + 1f * yx21;
        m22 = 0f * yx02 + 0f * yx12 + 1f * yx22;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Write the .vmtile files — TileAssembler.cpp:202-246
    // ─────────────────────────────────────────────────────────────────────

    private void WriteVmtiles(uint mapId, string mapName,
        SortedDictionary<uint, MangosModelSpawn> uniqueEntries,
        List<(uint TileId, uint SpawnId)> tileEntries,
        Dictionary<uint, uint> modelNodeIdx,
        uint globalTileId)
    {
        // Per TileAssembler.cpp:202-246: the .vmtile files are written
        // directly under iDestDir (output/vmaps/), no per-map subdirectory.
        string vmapsMapDir = _destDir;
        Directory.CreateDirectory(vmapsMapDir);

        // Group tileEntries by TileId, preserving insertion order within each
        // group (multimap semantics). Skip worldspawn entries (those are written
        // into the GOBJ chunk of the .vmtree, not into a .vmtile).
        // TileAssembler.cpp:204-246 iterates the multimap; for each tileId it
        // writes nSpawns = count(tileId), then each (spawn, modelNodeIdx).
        var byTile = new Dictionary<uint, List<uint>>();
        var tileOrder = new List<uint>();
        foreach (var te in tileEntries)
        {
            if (te.TileId == globalTileId) continue; // worldspawn — in GOBJ
            if (!uniqueEntries.TryGetValue(te.SpawnId, out var spawn)) continue;
            if ((spawn.Flags & MOD_WORLDSPAWN) != 0) continue; // TileAssembler.cpp:207-210

            if (!byTile.TryGetValue(te.TileId, out var bucket))
            {
                bucket = new List<uint>();
                byTile[te.TileId] = bucket;
                tileOrder.Add(te.TileId);
            }
            bucket.Add(te.SpawnId);
        }

        foreach (var tileId in tileOrder)
        {
            UnpackTileId(tileId, out uint tx, out uint ty);
            var entries = new List<(MangosModelSpawn Spawn, uint ReferencedVal)>();
            foreach (var spawnId in byTile[tileId])
            {
                var spawn = uniqueEntries[spawnId];
                uint referencedVal = modelNodeIdx.TryGetValue(spawn.Id, out var rv) ? rv : 0u;
                entries.Add((spawn, referencedVal));
            }

            string vmtilePath = Path.Combine(vmapsMapDir,
                MangosVmapTile.GetFileName(mapId, (int)tx, (int)ty));
            MangosVmapTile.Write(vmtilePath, entries);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  BoundsTrait<ModelSpawn*>::getBounds — TileAssembler.cpp:40-43
    //  out = spawn->getBounds() = spawn.iBound
    // ─────────────────────────────────────────────────────────────────────

    private static void GetSpawnBounds(MangosModelSpawn spawn, out G3dAaBox outBounds)
    {
        // By the time the BIH is built, every spawn must have a bound (M2 got
        // MOD_HAS_BOUND from CalculateTransformedBound, WMO came from dir_bin).
        // A missing bound would make the BIH build degenerate — we still guard
        // against it defensively.
        if (spawn.BoundLow == null || spawn.BoundHigh == null)
        {
            outBounds = new G3dAaBox
            {
                Lo = new G3dVector3(0, 0, 0),
                Hi = new G3dVector3(0, 0, 0),
            };
            return;
        }
        outBounds = new G3dAaBox
        {
            Lo = new G3dVector3(spawn.BoundLow[0], spawn.BoundLow[1], spawn.BoundLow[2]),
            Hi = new G3dVector3(spawn.BoundHigh[0], spawn.BoundHigh[1], spawn.BoundHigh[2]),
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    //  packTileID / unpackTileID — MapTree.h:108, 116
    // ─────────────────────────────────────────────────────────────────────

    private static uint PackTileId(uint tileX, uint tileY) => (tileX << 16) | tileY;

    private static void UnpackTileId(uint id, out uint tileX, out uint tileY)
    {
        tileX = id >> 16;
        tileY = id & 0xFF;
    }
}
