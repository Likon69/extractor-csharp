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
        string compiledPath = Path.Combine(_vmapDir, "vmaps", spawn.Name.Replace('/', Path.DirectorySeparatorChar) + ext);
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
        // Mangos C++:
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
        // G3D's Vector3 * Matrix3 is row-vector (same as System.Numerics
        // Vector3.Transform(v, M) which is v * M). So no transpose is needed —
        // the same M, used the same way, gives the same world-space vertex.
        //
        // Mangos's fromEulerAnglesXYZ takes (-iRot.z, -iRot.x, -iRot.y) as
        // (xAngle, yAngle, zAngle). System.Numerics CreateRotationX/Y/Z take
        // the angle in radians. We use the same sign convention as Mangos.
        float ax = -spawn.Rot[2] * MathF.PI / 180f; // xAngle = -iRot.z
        float ay = -spawn.Rot[0] * MathF.PI / 180f; // yAngle = -iRot.x
        float az = -spawn.Rot[1] * MathF.PI / 180f; // zAngle = -iRot.y
        // fromEulerAnglesXYZ (Mangos): rotate about X, then Y, then Z.
        // In System.Numerics row-vector convention, that is:
        //   M = CreateRotationZ(z) * CreateRotationY(y) * CreateRotationX(x)
        var rotMatrix = Matrix4x4.CreateRotationZ(az)
                      * Matrix4x4.CreateRotationY(ay)
                      * Matrix4x4.CreateRotationX(ax);

        // iPos in the .vmtile is stored by the vmap-extractor as
        // (pos.z, pos.x, pos.y) — MaNGOS vec3d.h::fixCoords (z, x, y).
        // No -32G offset is applied to iPos (TileAssembler.cpp:135-138 has the
        // offset line commented out). The mmap-extractor must convert this
        // to standard (x, y, z) world coordinates before handing geometry
        // off to the native Recast DLL, which expects (x, y, z).
        Vector3 position = new(spawn.Pos[1], spawn.Pos[2], spawn.Pos[0]);
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

            // copyIndices(flip = isM2). For WMO flip=false (raw order); for M2
            // flip=true (swap idx1 <-> idx2 to compensate for M2's flipped
            // bounding mesh winding on disk).
            bool flip = spawn.IsM2;
            int triCount = grp.Triangles.Length;
            if (flip)
            {
                for (int t = 0; t < triCount; t++)
                {
                    var tri = grp.Triangles[t];
                    outTris.Add(vertBase + (int)tri.I0);
                    outTris.Add(vertBase + (int)tri.I2);
                    outTris.Add(vertBase + (int)tri.I1);
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
