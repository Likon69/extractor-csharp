using System.IO;
using System.Numerics;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Formats.Vmap.Mangos;

namespace MaNGOS.Extractor.MmapExtractor;

/// <summary>
/// Mangos-faithful model-geometry loader for the mmap-extractor.
///
/// Replaces the raw ADT/WMO/M2 reading path with the OFFICIAL Mangos pipeline:
///   1. Read the per-tile `.vmtile` (produced by the vmap-extractor) to get
///      the list of WMO/M2 placements in that tile (WMO + M2 in the same file).
///   2. For each ModelSpawn, load the corresponding `.vmo` (WMO) or `.vmd`
///      (M2) compiled collision file. These files contain the BSP/MOPY-filtered
///      collision mesh, exactly as the vmap-extractor wrote them.
///   3. Apply TerrainBuilder::transform() = v * rotation * scale + position,
///      then flip X and Y axes — same convention as the C++ mmap-extractor.
///
/// This guarantees the mmap output is BIT-IDENTICAL to a Mangos extraction
/// when the vmap-extractor has been run against the same client data, with
/// the same BSP/MOPY filter rules.
/// </summary>
internal sealed class MangosVmapGeometryLoader
{
    private readonly string _vmapDir;          // directory containing vmaps/MapName/
    private readonly string _mapName;
    private readonly uint _mapId;
    private readonly ILogger _logger;

    // Cache compiled models — one entry per (model path) shared across all
    // tiles in the run. Two WMO/M2 placements in different ADT tiles can
    // point at the same source file, and the .vmo/.vmd is the same on disk.
    private readonly Dictionary<string, MangosVmoReader.WorldModelData> _modelCache = new(StringComparer.OrdinalIgnoreCase);

    // Cache which placements were already appended to a tile — mirrors the
    // seenUniqueIds set used in the previous raw-based implementation.
    private readonly HashSet<uint> _seenPlacementIds = new();

    public MangosVmapGeometryLoader(
        string vmapDir,
        string mapName,
        uint mapId,
        ILogger logger)
    {
        _vmapDir = vmapDir;
        _mapName = mapName;
        _mapId = mapId;
        _logger = logger;
    }

    /// <summary>
    /// Append the WMO/M2 collision mesh of every WMO/M2 placement that lives
    /// in the 3x3 ADT neighborhood (centerX/Y ± 1) into the output geometry.
    /// The placement uniqueness is per-game-instance (the ModelSpawn's Id),
    /// so a model referenced from two ADT tiles contributes twice.
    /// </summary>
    public void AppendNeighborhoodCollision(
        int centerX, int centerY,
        List<float> outVerts,
        List<int> outTris,
        List<byte> outAreas)
    {
        int wmoCount = 0, m2Count = 0, loadedVmo = 0, loadedVmd = 0, skipped = 0;

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int adjX = centerX + dx;
                int adjY = centerY + dy;
                string? vmtilePath = MangosVmtileLoader.FindVmtilePath(_vmapDir, _mapId, _mapName, adjX, adjY);
                if (vmtilePath == null) continue;

                var entries = MangosVmtileLoader.LoadVmtileSpawns(vmtilePath);
                if (entries.Count == 0) continue;

                _logger.LogInformation(
                    "[Mmap-Vmap] ADT ({CX},{CY}): .vmtile {TX}_{TY} → {Count} placements",
                    centerX, centerY, adjX, adjY, entries.Count);

                foreach (var (spawn, _) in entries)
                {
                    if (!_seenPlacementIds.Add(spawn.Id)) continue; // already contributed

                    if (spawn.IsM2) m2Count++; else wmoCount++;

                    if (!AppendSpawnCollision(spawn, outVerts, outTris, outAreas, out bool addedVmo))
                    {
                        skipped++;
                        continue;
                    }
                    if (addedVmo) loadedVmo++; else loadedVmd++;
                }
            }
        }

        int totalTris = outTris.Count / 3;
        _logger.LogInformation(
            "[Mmap-Vmap] ADT ({CX},{CY}): WMO/M2 from .vmtile: {Wmo} WMO + {M2} M2 placements, " +
            "{Vmo} .vmo + {Vmd} .vmd loaded, {Skip} skipped, {Tris} tris appended",
            centerX, centerY, wmoCount, m2Count, loadedVmo, loadedVmd, skipped, totalTris);
    }

    /// <summary>
    /// Append WDT-level worldspawn collision from the per-map .vmtree GOBJ chunk.
    /// This is what MaNGOS loads through VMapManager for WMO-only instances that
    /// have no ADT tiles and therefore no per-tile .vmtile files.
    /// </summary>
    public bool AppendGlobalCollision(
        List<float> outVerts,
        List<int> outTris,
        List<byte> outAreas)
    {
        string vmtreePath = Path.Combine(_vmapDir, "vmaps", MangosVmapTree.GetFileName(_mapId));
        if (!File.Exists(vmtreePath))
            return false;

        var (validMagic, _, globalSpawns) = MangosVmapTree.Read(vmtreePath);
        if (!validMagic || globalSpawns.Count == 0)
            return false;

        int wmoCount = 0, m2Count = 0, loadedVmo = 0, loadedVmd = 0, skipped = 0;
        foreach (var spawn in globalSpawns)
        {
            if (!_seenPlacementIds.Add(spawn.Id)) continue;

            if (spawn.IsM2) m2Count++; else wmoCount++;
            if (!AppendSpawnCollision(spawn, outVerts, outTris, outAreas, out bool addedVmo))
            {
                skipped++;
                continue;
            }
            if (addedVmo) loadedVmo++; else loadedVmd++;
        }

        _logger.LogInformation(
            "[Mmap-Vmap] {MapName} global .vmtree: {Wmo} WMO + {M2} M2 placements, " +
            "{Vmo} .vmo + {Vmd} .vmd loaded, {Skip} skipped, {Tris} tris appended",
            _mapName, wmoCount, m2Count, loadedVmo, loadedVmd, skipped, outTris.Count / 3);

        return outVerts.Count > 0;
    }

    /// <summary>
    /// Append a single ModelSpawn's collision mesh to the output geometry.
    /// Returns true if the geometry was added (or there was nothing to add).
    /// `addedFromCompiledFile` is true for WMO, false for M2.
    /// </summary>
    private bool AppendSpawnCollision(
        MangosModelSpawn spawn,
        List<float> outVerts,
        List<int> outTris,
        List<byte> outAreas,
        out bool isWmo)
    {
        isWmo = !spawn.IsM2;

        if (string.IsNullOrEmpty(spawn.Name)) return false;

        string ext = spawn.IsM2 ? MangosVmoReader.VmdExtension : MangosVmoReader.VmoExtension;
        // The C# vmap-extractor writes uniform names WITH the original extension (.wmo / .m2)
        // and then replaces it when writing the .vmo / .vmd file. So the on-disk file is
        // `ae376af...-foo.vmo` (no .wmo). Strip the trailing extension from spawn.Name before
        // appending the compiled extension so the path matches what the vmap-extractor wrote.
        string nameNoExt = Path.GetFileNameWithoutExtension(spawn.Name);
        string compiledPath = Path.Combine(_vmapDir, "vmaps", nameNoExt + ext);
        if (!File.Exists(compiledPath))
        {
            _logger.LogDebug("[Mmap-Vmap] Compiled collision file missing: {Path}", compiledPath);
            return false;
        }

        if (!_modelCache.TryGetValue(compiledPath, out var model))
        {
            model = MangosVmoReader.Read(compiledPath);
            if (model == null || !model.Valid)
            {
                _logger.LogDebug("[Mmap-Vmap] Could not read compiled file: {Path}", compiledPath);
                _modelCache[compiledPath] = null!;
                return false;
            }
            _modelCache[compiledPath] = model;
        }
        if (model == null) return false;

        if (model.Groups.Count == 0) return true; // valid file but no geometry — counts as "added" nothing

        // Build the placement transform — exact port of TerrainBuilder::loadVMap + transform.
        //
        // Mangos C++ (TerrainBuilder.cpp):
        //   float scale = instance.iScale;
        //   G3D::Matrix3 rotation = G3D::Matrix3::fromEulerAnglesXYZ(
        //       G3D::pi() * instance.iRot.z / -180.f,
        //       G3D::pi() * instance.iRot.x / -180.f,
        //       G3D::pi() * instance.iRot.y / -180.f);
        //   Vector3 position = instance.iPos;
        //   position.x -= 32 * GRID_SIZE;
        //   position.y -= 32 * GRID_SIZE;
        //   for v: v_world = v * rotation * scale + position; v.x *= -1; v.y *= -1;
        //
        // Both G3D's `Vector3 * Matrix3` operator and System.Numerics'
        // `Vector3.Transform(v, M)` are row-vector: the leftmost matrix in a
        // product is applied FIRST to the vector. So for rotMatrix = Rx*Ry*Rz
        // applied via Vector3.Transform, the order is Rx → Ry → Rz, which is
        // exactly the order G3D's v*M applies (via the M^T effect). The
        // composition below matches the C++ bit-for-bit for any iRot.
        float ax = spawn.Rot[2] * MathF.PI / 180f; // Rx angle = iRot.z (applied first)
        float ay = spawn.Rot[0] * MathF.PI / 180f; // Ry angle = iRot.x (applied second)
        float az = spawn.Rot[1] * MathF.PI / 180f; // Rz angle = iRot.y (applied third)
        var rotMatrix = Matrix4x4.CreateRotationX(ax)
                      * Matrix4x4.CreateRotationY(ay)
                      * Matrix4x4.CreateRotationZ(az);

        // iPos in the .vmtile is stored by the vmap-extractor as
        // (pos.z, pos.x, pos.y) — MaNGOS vec3d.h::fixCoords (z, x, y).
        // No -32G offset is applied at storage time (TileAssembler.cpp:135-138
        // has the offset line commented out). The C++ mmap-extractor applies
        // the -32G offset itself (TerrainBuilder.cpp::loadVMap):
        //   position.x -= 32 * GRID_SIZE;  // x-axis (which is world Z in fixCoords)
        //   position.y -= 32 * GRID_SIZE;  // y-axis (which is world X in fixCoords)
        // We must do the same in fixCoords space (no permutation), and let
        // copyVertices() at the end (with v.x *= -1, v.y *= -1, then (v.y,v.z,v.x))
        // produce the final standard (x,y,z) world coords.
        Vector3 position = new(
            spawn.Pos[0] - 32f * WowConstants.TileSize, // pos.x in fixCoords = origZ - 32G
            spawn.Pos[1] - 32f * WowConstants.TileSize, // pos.y in fixCoords = origX - 32G
            spawn.Pos[2]);                               // pos.z in fixCoords = origY (no offset)
        float scale = spawn.Scale;

        for (int g = 0; g < model.Groups.Count; g++)
        {
            var grp = model.Groups[g];
            if (grp.Vertices.Length == 0 || grp.Triangles.Length == 0) continue;

            int vertBase = outVerts.Count / 3;

            // transform() — applied to each model-space vertex.
            for (int v = 0; v < grp.Vertices.Length; v++)
            {
                var lv = grp.Vertices[v];
                // v_world = v * M * scale + position  (row-vector convention)
                var wv = Vector3.Transform(lv, rotMatrix) * scale + position;
                // Flip X and Y axes (same as original C++ transform())
                wv.X *= -1f;
                wv.Y *= -1f;

                // copyVertices() ordering: dest = (v.y, v.z, v.x)
                outVerts.Add(wv.Y);
                outVerts.Add(wv.Z);
                outVerts.Add(wv.X);
            }

            // copyIndices(flip = isM2). For WMO flip=false (raw (A,B,C) order); for M2
            // flip=true we must emit the same triangle order the C++ does.
            //
            // Background: C++ writes (idx0, idx2, idx1) per triangle to .vmd (swap
            // I1↔I2), then copyIndices(flip=true) writes (idx2, idx1, idx0), which
            // for an original (A, B, C) triangle becomes (B, C, A). That's a cyclic
            // permutation by 2 (right shift), which PRESERVES the original M2 winding.
            // The C# .vmd is written WITHOUT the per-triangle swap, so for input
            // (I0=A, I1=B, I2=C) we must emit (I1, I2, I0) = (B, C, A) to match.
            //
            // Emitting (I0, I2, I1) = (A, C, B) instead is a TRANSPOSITION
            // (I1↔I2), which flips the winding — Recast then sees the M2 top face
            // as a ceiling (normal −Z) and the bot walks at the base of the M2
            // instead of on top of it. That was a real divergence; fixed here.
            bool flip = spawn.IsM2;
            int triCount = grp.Triangles.Length;
            if (flip)
            {
                for (int t = 0; t < triCount; t++)
                {
                    var tri = grp.Triangles[t];
                    outTris.Add(vertBase + (int)tri.I1);
                    outTris.Add(vertBase + (int)tri.I2);
                    outTris.Add(vertBase + (int)tri.I0);
                    outAreas.Add(1); // NAV_GROUND
                }
            }
            else
            {
                for (int t = 0; t < triCount; t++)
                {
                    var tri = grp.Triangles[t];
                    outTris.Add(vertBase + (int)tri.I0);
                    outTris.Add(vertBase + (int)tri.I1);
                    outTris.Add(vertBase + (int)tri.I2);
                    outAreas.Add(1); // NAV_GROUND
                }
            }
        }

        return true;
    }
}
