using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;
using MaNGOS.Extractor.Core.Models;
using MaNGOS.Extractor.Formats.Adt.Models;
using MaNGOS.Extractor.Formats.Adt.Parsing;
using MaNGOS.Extractor.Formats.Dbc;
using MaNGOS.Extractor.Formats.M2;
using MaNGOS.Extractor.Formats.Wdt;
using MaNGOS.Extractor.Formats.Wmo.Models;
using MaNGOS.Extractor.Formats.Wmo.Parsing;
using MaNGOS.Extractor.MmapExtractor.Recast;
using MaNGOS.Extractor.UI;

namespace MaNGOS.Extractor.MmapExtractor;

public sealed class MmapExtractorService
{
    private readonly IArchiveReader _archive;
    private readonly WdtReader _wdtReader;
    private readonly AdtParser _adtParser;
    private readonly WmoParser _wmoParser;
    private readonly M2Parser _m2Parser;
    private readonly ILogger _logger;
    private readonly string _outputDir;
    private readonly RecastConfig _recastConfig;
    private readonly int _maxDegreeOfParallelism;
    private readonly GoSpawn[] _goSpawns;
    private readonly OffMeshConnection[] _offMeshConnections;
    private readonly IReadOnlyDictionary<uint, GameObjectModelRef> _gameObjectModels;
    private readonly ConcurrentDictionary<uint, GameObjectModelData?> _gameObjectModelCache = new();
    private readonly string? _offMeshPath;
    private readonly string? _roadMapsDir;
    private readonly string? _vmapDir;  // directory containing vmaps/MapName/ (output of vmap-extractor)
    private readonly string? _mapsDir;  // directory containing maps/{id:D3}{y:D2}{x:D2}.map (output of map-extractor)
    private readonly bool _usesLiquids;

    // --- Spatial filtering toggle (optimisation: reduce triCount per BuildTile call) ---
    // When true, each sub-tile receives only the triangles whose AABB overlaps the
    // sub-tile's expanded bbox (bbox + borderMeters). This is bit-identical to the
    // unfiltered path because:
    //   - rcClearUnwalkableTriangles marks triangles independently (no neighbour context),
    //   - rcRasterizeTriangles does its own overlapBounds culling per triangle, so any
    //     triangle fully outside the heightfield bbox contributes zero spans anyway.
    // Keeping triangles slightly beyond the strict heightfield bbox (via borderMeters)
    // is a safe superset of the strict requirement. Static so it can be flipped at
    // runtime from tests / CLI without touching the constructor.
    public static bool EnableSpatialFilter = true;

    private readonly record struct BuildSubTileResult(RecastBuildResult Status, byte[]? Data)
    {
        public bool IsFailure => (int)Status < 0;
        public bool HasData => Data is { Length: > 0 };
    }

    public MmapExtractorService(
        IArchiveReader archive,
        ILoggerFactory loggerFactory,
        string outputDir,
        RecastConfig recastConfig,
        int maxDegreeOfParallelism = 1,
        string? goSpawnsPath = null,
        string? offMeshPath = null,
        string? roadMapsDir = null,
        string? vmapDir = null,
        string? mapsDir = null,
        bool usesLiquids = true)
    {
        _archive = archive;
        _logger = loggerFactory.CreateLogger<MmapExtractorService>();
        _wdtReader = new WdtReader(archive);
        _adtParser = new AdtParser(archive, loggerFactory.CreateLogger<AdtParser>());
        _wmoParser = new WmoParser(archive, loggerFactory.CreateLogger<WmoParser>());
        _m2Parser = new M2Parser(archive);
        _outputDir = outputDir;
        _recastConfig = recastConfig;
        _maxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism);
        _offMeshPath = offMeshPath;
        _roadMapsDir = roadMapsDir;
        _vmapDir = vmapDir;
        _mapsDir = mapsDir;
        _usesLiquids = usesLiquids;

        // --- Log Recast config ---
        _logger.LogInformation("[Mmap] Recast config: cellSize={CellSize:F6} cellHeight={CellHeight:F3} " +
            "walkableSlope={Slope:F1}° walkableHeight={WH} walkableRadius={WR} walkableClimb={WC} threads={Threads}",
            recastConfig.CellSize, recastConfig.CellHeight, recastConfig.WalkableSlopeAngle,
            recastConfig.WalkableHeight, recastConfig.WalkableRadius, recastConfig.WalkableClimb,
            maxDegreeOfParallelism);

        // --- Log OffMesh connections ---
        if (!string.IsNullOrEmpty(offMeshPath) && File.Exists(offMeshPath))
        {
            _offMeshConnections = LoadOffMeshConnections(offMeshPath);
            _logger.LogInformation("[Mmap] OffMesh connections: {Path} ({Count} valid entries)", offMeshPath, _offMeshConnections.Length);
        }
        else if (!string.IsNullOrEmpty(offMeshPath))
        {
            _logger.LogWarning("[Mmap] OffMesh file not found: {Path}", offMeshPath);
            _offMeshConnections = Array.Empty<OffMeshConnection>();
        }
        else
        {
            _logger.LogInformation("[Mmap] No OffMesh connection file specified");
            _offMeshConnections = Array.Empty<OffMeshConnection>();
        }

        // --- Load and log GameObject spawns ---
        _goSpawns = LoadGoSpawns(goSpawnsPath);
        _gameObjectModels = LoadGameObjectModels();
        if (!string.IsNullOrEmpty(_roadMapsDir))
            _logger.LogInformation("[Mmap] Road mask directory: {Path}", _roadMapsDir);
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);
    }

    private IReadOnlyDictionary<uint, GameObjectModelRef> LoadGameObjectModels()
    {
        const string dbcPath = "DBFilesClient\\GameObjectDisplayInfo.dbc";
        if (!_archive.TryReadFile(dbcPath, out var dbcData))
        {
            _logger.LogWarning("[Mmap] GameObjectDisplayInfo.dbc not found; GameObject mesh rasterization disabled");
            return new Dictionary<uint, GameObjectModelRef>();
        }

        var dbcReader = DbcReader<GameObjectDisplayInfoRow>.Parse(dbcData.Span);
        var rows = dbcReader.Rows.ToArray();
        var models = new Dictionary<uint, GameObjectModelRef>(rows.Length);

        foreach (var row in rows)
        {
            string modelPath = dbcReader.GetString(row, 1)?.Replace('/', '\\') ?? string.Empty;
            if (string.IsNullOrWhiteSpace(modelPath))
                continue;

            string ext = Path.GetExtension(modelPath).ToLowerInvariant();
            if (ext is ".mdx" or ".mdl")
            {
                modelPath = Path.ChangeExtension(modelPath, ".m2");
                ext = ".m2";
            }

            bool isWmo = ext == ".wmo";
            if (!isWmo && ext != ".m2")
                continue;

            models[row.Id] = new GameObjectModelRef(modelPath, isWmo);
        }

        _logger.LogInformation("[Mmap] GameObject display map loaded: {Count} model references", models.Count);
        return models;
    }

    private GoSpawn[] LoadGoSpawns(string? goSpawnsPath)
    {
        if (string.IsNullOrEmpty(goSpawnsPath) || !File.Exists(goSpawnsPath))
        {
            if (!string.IsNullOrEmpty(goSpawnsPath))
                _logger.LogWarning("[Mmap] GameObject spawns file not found: {Path}", goSpawnsPath);
            else
                _logger.LogInformation("[Mmap] No GameObject spawns file specified");
            return Array.Empty<GoSpawn>();
        }

        var spawns = GoSpawnsReader.Read(goSpawnsPath);
        if (spawns.Length == 0)
        {
            _logger.LogWarning("[Mmap] GameObject spawns file is empty or invalid: {Path}", goSpawnsPath);
            return spawns;
        }

        // Count per-map distribution
        var mapGroups = spawns.GroupBy(s => s.MapId).OrderBy(g => g.Key).ToList();
        _logger.LogInformation("[Mmap] GameObject spawns loaded: {Path} → {Total} spawns across {Maps} maps",
            goSpawnsPath, spawns.Length, mapGroups.Count);
        foreach (var grp in mapGroups.Take(20))
        {
            _logger.LogInformation("[Mmap]   map={MapId}: {Count} spawns", grp.Key, grp.Count());
        }
        if (mapGroups.Count > 20)
            _logger.LogInformation("[Mmap]   ... and {More} more maps", mapGroups.Count - 20);

        return spawns;
    }

    public async Task<int> ExtractMapAsync(
        uint mapId,
        string mapName,
        IProgress<TileProgressEvent>? progress,
        CancellationToken ct = default,
        int? onlyTileX = null,
        int? onlyTileY = null)
    {
        _logger.LogInformation("Starting mmap extraction for {MapName} (id={MapId}) with {Threads} threads",
            mapName, mapId, _maxDegreeOfParallelism);

        if (!await _wdtReader.LoadAsync(mapName, ct))
        {
            _logger.LogError("Failed to load WDT for map: {MapName}", mapName);
            return 0;
        }

        var tiles = _wdtReader.GetExistingTiles();
        if (onlyTileX.HasValue && onlyTileY.HasValue)
            tiles = tiles.Where(t => t.X == onlyTileX.Value && t.Y == onlyTileY.Value).ToList();

        if (tiles.Count == 0 && !onlyTileX.HasValue && !onlyTileY.HasValue)
            tiles = DiscoverGlobalVmapTiles(mapId, mapName);

        _logger.LogInformation("Found {Count} ADT tiles for map {MapName} (id={MapId})", tiles.Count, mapName, mapId);

        // Log GO spawns for this specific map
        int goCount = _goSpawns.Count(s => s.MapId == mapId);
        if (goCount > 0)
            _logger.LogInformation("[Mmap] {GoCount} GameObject spawns on map {MapName} (id={MapId})", goCount, mapName, mapId);

        // Compute the navmesh origin: bmin of the max-index tile, matching C++ MapBuilder.
        int maxAdtX = tiles.Count > 0 ? tiles.Max(t => t.X) : 31;
        int maxAdtY = tiles.Count > 0 ? tiles.Max(t => t.Y) : 31;

        // Write mmap header upfront so the map file exists even if extraction is cancelled later.
        WriteMmapHeader(mapId, (uint)tiles.Count, maxAdtX, maxAdtY);

        int successCount = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxDegreeOfParallelism,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(tiles, options, async (tile, token) =>
        {
            var (tileX, tileY) = tile;
            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY, TileStatus.Processing, ExtractionPhase.Mmap));

            bool success = await ProcessTileAsync(mapId, mapName, tileX, tileY, maxAdtX, maxAdtY, token);

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY,
                success ? TileStatus.Done : TileStatus.Failed,
                ExtractionPhase.Mmap));

            if (success)
                Interlocked.Increment(ref successCount);
        });

        _logger.LogInformation("Mmap extraction complete for {MapName}: {Success}/{Total} tiles",
            mapName, successCount, tiles.Count);
        // Refresh header at end in case future metadata changes.
        WriteMmapHeader(mapId, (uint)tiles.Count, maxAdtX, maxAdtY);
        return successCount;
    }

    private List<(int X, int Y)> DiscoverGlobalVmapTiles(uint mapId, string mapName)
    {
        var tiles = new List<(int X, int Y)>();
        if (string.IsNullOrEmpty(_vmapDir))
            return tiles;

        var verts = new List<float>();
        var tris = new List<int>();
        var areas = new List<byte>();
        var loader = new MangosVmapGeometryLoader(_vmapDir, mapName, mapId, _logger);
        if (!loader.AppendGlobalCollision(verts, tris, areas) || verts.Count == 0)
            return tiles;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < verts.Count; i += 3)
        {
            float x = verts[i];
            float z = verts[i + 2];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }

        int maxTileX = ClampGridCoord((int)(32 - minX / WowConstants.TileSize));
        int maxTileY = ClampGridCoord((int)(32 - minZ / WowConstants.TileSize));
        int minTileX = ClampGridCoord((int)(32 - maxX / WowConstants.TileSize));
        int minTileY = ClampGridCoord((int)(32 - maxZ / WowConstants.TileSize));

        for (int x = minTileX; x <= maxTileX; x++)
            for (int y = minTileY; y <= maxTileY; y++)
                tiles.Add((x, y));

        _logger.LogInformation(
            "[Mmap] Map {MapName} has no ADT tiles; global vmtree bounds X=[{MinX:F2},{MaxX:F2}] Z=[{MinZ:F2},{MaxZ:F2}] -> {Count} fallback tile(s)",
            mapName, minX, maxX, minZ, maxZ, tiles.Count);

        return tiles;
    }

    private static int ClampGridCoord(int value)
        => Math.Clamp(value, 0, WowConstants.GridSize - 1);

    private void WriteMmapHeader(uint mapId, uint tileCount, int maxAdtX, int maxAdtY)
    {
        // Origin = bmin of the tile at (maxAdtX, maxAdtY) — the most-positive-index corner.
        // Matches C++ MapBuilder: rcVcopy(navMeshParams->orig, bmin) for the max-extent tile.
        float orig0 = (31 - maxAdtX) * WowConstants.TileSize;
        float orig2 = (31 - maxAdtY) * WowConstants.TileSize;

        string mmapPath = Path.Combine(_outputDir, $"{mapId:D3}.mmap");
        uint detourTileCount = tileCount * WowConstants.SubTilesPerAdtSide * WowConstants.SubTilesPerAdtSide;
        _logger.LogInformation("[Mmap] Writing header: {Path} (adts={TileCount}, detourTiles={DetourTileCount}, tileSize={TileSize}, orig=({Orig0},{Orig2}))",
            mmapPath, tileCount, detourTileCount, WowConstants.SubTileSize, orig0, orig2);
        using var stream = new FileStream(mmapPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write(orig0);                      // orig[0] = (31 - maxAdtX) * TileSize
        writer.Write(0f);                         // orig[1]
        writer.Write(orig2);                      // orig[2] = (31 - maxAdtY) * TileSize
        writer.Write(WowConstants.SubTileSize);   // tileWidth  = 133.333333f
        writer.Write(WowConstants.SubTileSize);   // tileHeight = 133.333333f
        writer.Write((int)detourTileCount);       // maxTiles   = ADT count * 16
        writer.Write(1 << 20);                    // maxPolys   = 1 << DT_POLY_BITS
    }

    private async Task<bool> ProcessTileAsync(uint mapId, string mapName, int tileX, int tileY, int maxAdtX, int maxAdtY, CancellationToken ct)
    {
        _logger.LogInformation("[Mmap] Processing ADT ({TileX},{TileY}) for map {MapName} (id={MapId})",
            tileX, tileY, mapName, mapId);

        // Skip already-built tiles unless this tile has offmesh connections (must always rebuild).
        bool hasOffmesh = _offMeshConnections.Any(c => c.MapId == (int)mapId && c.TileX == tileX && c.TileY == tileY);
        string mmtilePath = Path.Combine(_outputDir, $"{mapId:D3}{tileY:D2}{tileX:D2}.mmtile");
        if (!hasOffmesh && File.Exists(mmtilePath))
        {
            _logger.LogInformation("[Mmap] Skipping ADT ({TileX},{TileY}) — file exists and no offmesh", tileX, tileY);
            return true;
        }

        var geometry = await LoadTileGeometryAsync(mapId, mapName, tileX, tileY, ct);
        if (geometry.Vertices == null || geometry.Vertices.Length == 0 ||
            geometry.Indices == null || geometry.Areas == null)
        {
            _logger.LogWarning("[Mmap] No geometry for ADT ({TileX},{TileY})", tileX, tileY);
            return false;
        }

        _logger.LogInformation("[Mmap] ADT ({TileX},{TileY}) geometry: {Verts} vertices, {Tris} triangles, {Areas} area entries",
            tileX, tileY, geometry.VertexCount, geometry.IndexCount / 3, geometry.Areas.Length);

        // Log area type distribution
        var areaDist = geometry.Areas.GroupBy(a => a).OrderByDescending(g => g.Count()).Take(8).ToList();
        foreach (var grp in areaDist)
            _logger.LogDebug("[Mmap] ADT ({TileX},{TileY}) areaType={AreaType}: {Count} triangles", tileX, tileY, grp.Key, grp.Count());

        // Log geometry bounds
        float geoMinX = float.MaxValue, geoMaxX = float.MinValue;
        float geoMinY = float.MaxValue, geoMaxY = float.MinValue;
        float geoMinZ = float.MaxValue, geoMaxZ = float.MinValue;
        for (int i = 0; i < geometry.Vertices.Length; i += 3)
        {
            float vx = geometry.Vertices[i], vy = geometry.Vertices[i + 1], vz = geometry.Vertices[i + 2];
            if (vx < geoMinX) geoMinX = vx; if (vx > geoMaxX) geoMaxX = vx;
            if (vy < geoMinY) geoMinY = vy; if (vy > geoMaxY) geoMaxY = vy;
            if (vz < geoMinZ) geoMinZ = vz; if (vz > geoMaxZ) geoMaxZ = vz;
        }
        _logger.LogInformation("[Mmap] ADT ({TileX},{TileY}) geometry bounds: " +
            "X=[{MinX:F2}, {MaxX:F2}] Y=[{MinY:F2}, {MaxY:F2}] Z=[{MinZ:F2}, {MaxZ:F2}]",
            tileX, tileY, geoMinX, geoMaxX, geoMinY, geoMaxY, geoMinZ, geoMaxZ);

        try
        {
            float bminX = (31 - tileX) * WowConstants.TileSize;
            float bminZ = (31 - tileY) * WowConstants.TileSize;

            var subTiles = BuildNavMeshSubTilesSync(mapId, geometry, tileX, tileY, maxAdtX, maxAdtY, bminX, bminZ, ct);
            var failedSubTiles = subTiles.Where(r => r.IsFailure).ToArray();
            if (failedSubTiles.Length > 0)
            {
                var failures = string.Join(", ", failedSubTiles
                    .GroupBy(r => r.Status)
                    .Select(g => $"{g.Key}={g.Count()}"));
                _logger.LogWarning("[Mmap] BuildTile failed for ADT ({TileX},{TileY}) sub-tiles: {Failures}",
                    tileX, tileY, failures);
                return false;
            }

            if (subTiles.All(r => !r.HasData))
            {
                _logger.LogInformation("[Mmap] No polygons to build for ADT ({TileX},{TileY}); skipping .mmtile like MaNGOS",
                    tileX, tileY);
                return true;
            }

            var navData = subTiles.Select(r => r.Data).ToArray();
            await WriteMmtileAsync(mapId, tileY, tileX, navData, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to build mmtile: {TileX},{TileY}", tileX, tileY);
            return false;
        }
    }

    private async Task<TileGeometry> LoadTileGeometryAsync(uint mapId, string mapName, int centerX, int centerY, CancellationToken ct)
    {
        var tiles = new List<(int X, int Y, AdtFile[] Tile)>();
        int loadedCount = 0, skippedCount = 0;

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int adjX = centerX + dx;
                int adjY = centerY + dy;
                if (adjX < 0 || adjX >= 64 || adjY < 0 || adjY >= 64)
                {
                    skippedCount++;
                    continue;
                }

                string adtPath = $"World\\Maps\\{mapName}\\{mapName}_{adjX}_{adjY}.adt";

                var result = await _adtParser.ParseAsync(adtPath, mapId, adjX, adjY, ct);
                if (!result.Success)
                {
                    skippedCount++;
                    continue;
                }
                var adt = result.Tile;
                if (adt == null) { skippedCount++; continue; }
                tiles.Add((adjX, adjY, new[] { adt }));
                loadedCount++;
            }
        }

        _logger.LogInformation("[Mmap] LoadTileGeometry adt=({CX},{CY}): {Loaded} ADTs loaded, {Skipped} skipped",
            centerX, centerY, loadedCount, skippedCount);

        var geo = tiles.Count > 0
            ? ExtrudeTileGeometry(mapId, centerX, centerY, tiles, LoadRoadMask(mapId, centerX, centerY))
            : new TileGeometry(Array.Empty<float>(), Array.Empty<int>(), Array.Empty<byte>());

        // Add WMO and M2 building/object geometry to the navmesh.
        // Same intent as the original 9d63265 raw-ADT path (AppendWmoGeometryAsync + AppendM2Geometry),
        // but reads from the EXTRACTED .vmtile + .vmo/.vmd files produced by the vmap-extractor
        // (Mangos-faithful pipeline: C++ TerrainBuilder::loadVMap → C# MangosVmapGeometryLoader).
        // The .vmtile already contains WMO root + WMO doodads + M2 placements, so this
        // single load covers all three sources.
        if (!string.IsNullOrEmpty(_vmapDir))
        {
            var modelVerts = new List<float>();
            var modelTris  = new List<int>();
            var modelAreas = new List<byte>();

            var loader = new MangosVmapGeometryLoader(_vmapDir, mapName, mapId, _logger);
            if (tiles.Count > 0)
                loader.AppendNeighborhoodCollision(centerX, centerY, modelVerts, modelTris, modelAreas);
            else
                loader.AppendGlobalCollision(modelVerts, modelTris, modelAreas);

            if (modelVerts.Count > 0)
            {
                _logger.LogInformation(
                    "[Mmap] ADT ({CX},{CY}): WMO/M2 from .vmtile+.vmo/.vmd: {TotalTris} tris, {TotalVerts} verts",
                    centerX, centerY,
                    modelTris.Count / 3,
                    modelVerts.Count / 3);

                var combinedVerts = new float[geo.Vertices.Length + modelVerts.Count];
                var combinedIdx   = new int[geo.Indices.Length + modelTris.Count];
                var combinedAreas = new byte[geo.Areas.Length + modelAreas.Count];
                geo.Vertices.CopyTo(combinedVerts, 0);
                modelVerts.ToArray().CopyTo(combinedVerts, geo.Vertices.Length);
                geo.Indices.CopyTo(combinedIdx, 0);
                int vertBase = geo.Vertices.Length / 3;
                for (int i = 0; i < modelTris.Count; i++)
                    combinedIdx[geo.Indices.Length + i] = modelTris[i] + vertBase;
                geo.Areas.CopyTo(combinedAreas, 0);
                modelAreas.ToArray().CopyTo(combinedAreas, geo.Areas.Length);
                geo = new TileGeometry(combinedVerts, combinedIdx, combinedAreas);
            }
        }
        else
        {
            _logger.LogWarning(
                "[Mmap] ADT ({CX},{CY}): no vmapDir configured — WMO/M2 geometry will be missing. " +
                "Run the vmap-extractor (MangosVmapExtractorService) first and pass its --vmap-dir.",
                centerX, centerY);
        }

        // Add GameObject spawns using the real model collision mesh, same intent as old MaNGOS loadGameObjects().
        if (_goSpawns.Length > 0)
        {
            float wowXMax = (32f - centerY) * WowConstants.TileSize;
            float wowXMin = (32f - centerY - 1f) * WowConstants.TileSize;
            float wowYMax = (32f - centerX) * WowConstants.TileSize;
            float wowYMin = (32f - centerX - 1f) * WowConstants.TileSize;

            wowXMin -= WowConstants.TileSize;
            wowXMax += WowConstants.TileSize;
            wowYMin -= WowConstants.TileSize;
            wowYMax += WowConstants.TileSize;

            var goVerts = new List<float>();
            var goIndices = new List<int>();
            var goAreas = new List<byte>();
            int goCandidateCount = 0;
            int goRasterizedCount = 0;
            int skippedMissingModel = 0;
            int skippedFileNotFound = 0;  // model file absent from MPQ — real issue
            int skippedNoBounds = 0;      // M2 has no bounding mesh — normal for decorative objects
            int skippedTooSmall = 0;

            foreach (var go in _goSpawns)
            {
                if (go.MapId != mapId)
                    continue;
                if (go.PosX < wowXMin || go.PosX > wowXMax
                    || go.PosY < wowYMin || go.PosY > wowYMax)
                    continue;

                goCandidateCount++;

                if (!_gameObjectModels.TryGetValue(go.DisplayId, out var modelRef))
                {
                    skippedMissingModel++;
                    continue;
                }

                var modelData = _gameObjectModelCache.GetOrAdd(go.DisplayId, static (displayId, state) =>
                    state!.Owner.LoadGameObjectModel(state.Models[displayId]),
                    (Owner: this, Models: _gameObjectModels));

                if (modelData == null)
                {
                    skippedFileNotFound++;
                    continue;
                }
                if (modelData.Vertices.Length == 0)
                {
                    skippedNoBounds++;
                    continue;
                }

                Vector3 extent = modelData.BoundsMax - modelData.BoundsMin;
                float maxExtent = MathF.Max(MathF.Abs(extent.X), MathF.Max(MathF.Abs(extent.Y), MathF.Abs(extent.Z))) * go.Scale;
                if (maxExtent < 0.1f)
                {
                    skippedTooSmall++;
                    continue;
                }

                AppendGameObjectGeometry(go, modelData, goVerts, goIndices, goAreas);
                goRasterizedCount++;
            }

            if (goCandidateCount > 0)
            {
                _logger.LogInformation("[Mmap] ADT ({CX},{CY}): GO candidates={Candidates} rasterized={Rasterized} " +
                    "skippedMissingModel={Missing} skippedNoBounds={NoBounds} skippedTooSmall={TooSmall} skippedFileNotFound={NotFound}",
                    centerX, centerY, goCandidateCount, goRasterizedCount,
                    skippedMissingModel, skippedNoBounds, skippedTooSmall, skippedFileNotFound);
                if (skippedFileNotFound > 0)
                    _logger.LogWarning("[Mmap] ADT ({CX},{CY}): {Count} GO models could not be loaded from MPQ (check model paths)",
                        centerX, centerY, skippedFileNotFound);
            }

            if (goVerts.Count > 0)
            {
                var combinedVerts = new float[geo.Vertices.Length + goVerts.Count];
                var combinedIdx = new int[geo.Indices.Length + goIndices.Count];
                var combinedAreas = new byte[geo.Areas.Length + goAreas.Count];
                geo.Vertices.CopyTo(combinedVerts, 0);
                goVerts.ToArray().CopyTo(combinedVerts, geo.Vertices.Length);
                geo.Indices.CopyTo(combinedIdx, 0);
                int vertBase = geo.Vertices.Length / 3;
                for (int i = 0; i < goIndices.Count; i++) combinedIdx[geo.Indices.Length + i] = goIndices[i] + vertBase;
                geo.Areas.CopyTo(combinedAreas, 0);
                goAreas.ToArray().CopyTo(combinedAreas, geo.Areas.Length);
                geo = new TileGeometry(combinedVerts, combinedIdx, combinedAreas);

                _logger.LogInformation("[Mmap] ADT ({CX},{CY}): combined geometry after GO merge: " +
                    "{TotalVerts} verts ({GOVerts} from GO), {TotalTris} tris ({GOTris} from GO)",
                    centerX, centerY,
                    geo.VertexCount, goVerts.Count / 3,
                    geo.IndexCount / 3, goIndices.Count / 3);
            }
        }

        return geo;
    }

    private byte[]? LoadRoadMask(uint mapId, int tileX, int tileY)
    {
        if (string.IsNullOrEmpty(_roadMapsDir))
            return null;

        string roadPath = Path.Combine(_roadMapsDir, $"{mapId:D3}{tileY:D2}{tileX:D2}.road");
        if (!File.Exists(roadPath))
            return null;

        byte[] mask = File.ReadAllBytes(roadPath);
        // New format: 4×4 sub-zones per chunk × 256 chunks = 4096 bits = 512 bytes.
        // The previous format was 256 bytes (1 bit per chunk). A 256-byte file
        // is treated as an all-or-nothing fallback: every sub-zone in every
        // chunk is marked road. This way, old single-bit roadmasks keep working
        // — the whole chunk becomes a road again, which matches the old
        // behaviour instead of suddenly disappearing.
        if (mask.Length != 512 && mask.Length != 256)
        {
            _logger.LogWarning("[Mmap] Road mask has unexpected size: {Path} ({Length} bytes, expected 512 or 256)",
                roadPath, mask.Length);
            return null;
        }

        if (mask.Length == 256)
        {
            // Legacy fallback: expand to 512 bytes (every sub-zone in every
            // chunk set to 1 wherever the legacy chunk bit was 1).
            byte[] expanded = new byte[512];
            for (int ci = 0; ci < 256; ci++)
            {
                if (mask[ci] != 0)
                {
                    // 4×4 sub-zones = 16 bits = 2 bytes
                    expanded[ci * 2 + 0] = 0xFF;
                    expanded[ci * 2 + 1] = 0xFF;
                }
            }
            mask = expanded;
        }

        int roadSubZones = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            byte b = mask[i];
            while (b != 0) { roadSubZones++; b &= (byte)(b - 1); }
        }
        if (roadSubZones > 0)
            _logger.LogInformation("[Mmap] ADT ({TileX},{TileY}): loaded road mask {RoadSubZones}/4096 sub-zones",
                tileX, tileY, roadSubZones);

        return roadSubZones > 0 ? mask : null;
    }

    private static TileGeometry ExtrudeTileGeometry(uint mapId, int centerX, int centerY, List<(int X, int Y, AdtFile[] Tile)> tiles, byte[]? centerRoadMask)
    {
        var vertices = new List<float>();
        var indices = new List<int>();
        var areas = new List<byte>();

        foreach (var (adjX, adjY, tileArr) in tiles)
        {
            var adt = tileArr[0];
            int vertexOffset = vertices.Count / 3;

            for (int chunkIdx = 0; chunkIdx < 256; chunkIdx++)
            {
                int chunkX = chunkIdx % 16;
                int chunkY = chunkIdx / 16;

                // Skip absent chunks. The ADT parser leaves _heights at 0 for
                // chunks that have no MCNK entry in MCIN — without this guard
                // the loop would still emit 145 vertices (9x9 V9 + 8x8 V8) at
                // Y=0 for every absent chunk, which is the kill-plane
                // artefact visible in BG maps like Eye of the Storm (map
                // 566, mostly empty 3x3 neighbourhoods) where the extractor
                // would otherwise build a vast flat walkable surface at Y=0.
                if (!adt.GetChunkPresent(chunkIdx))
                    continue;

                float tileOriginX = (32 - adjX) * WowConstants.TileSize;
                float tileOriginZ = (32 - adjY) * WowConstants.TileSize;

                float chunkOriginX = tileOriginX - chunkX * WowConstants.ChunkSize;
                float chunkOriginZ = tileOriginZ - chunkY * WowConstants.ChunkSize;

                var chunkHeights = adt.GetChunkHeights(chunkIdx);

                float v9Step = WowConstants.ChunkSize / (WowConstants.MCNKVerticesSide - 1);

                for (int z = 0; z < 9; z++)
                {
                    for (int x = 0; x < 9; x++)
                    {
                        float h = AdtMcvt.GetV9(chunkHeights, z, x);
                        vertices.Add(chunkOriginX - x * v9Step);
                        vertices.Add(h);
                        vertices.Add(chunkOriginZ - z * v9Step);
                    }
                }

                int v8BaseOffset = vertices.Count / 3;
                for (int z = 0; z < 8; z++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        float h = AdtMcvt.GetV8(chunkHeights, z, x);
                        vertices.Add(chunkOriginX - (x + 0.5f) * v9Step);
                        vertices.Add(h);
                        vertices.Add(chunkOriginZ - (z + 0.5f) * v9Step);
                    }
                }

                byte chunkAreaType = adt.GetChunkAreaType(chunkIdx);
                // Per-sub-triangle road lookup. The road mask is 4×4 sub-zones
                // per chunk packed into 16 bits (2 bytes) — same bit layout as
                // the MCNK holes bitmask below. A sub-triangle (z, x) belongs
                // to sub-zone (z/2, x/2). The bit is the same for all 4 sub-tris
                // that share a sub-zone, so the bot follows the centerline of
                // a 2×2-quad strip of road at the alpha map's natural resolution.
                bool isCenterChunk = adjX == centerX && adjY == centerY;
                int chunkMaskByteBase = chunkIdx * 2;
                byte baseAreaType = chunkAreaType;
                // MCNK.holes: WoW terrain holes are a 4×4 bitmask per chunk
                // (16 bits → uint16 / lower 16 of uint32). Each bit covers a
                // 2×2 zone of the chunk's 8×8 sub-triangles. The C++ map
                // extractor writes the bitmask into map_fileheader.holes[i][j]
                // and the C++ mmap extractor reads it back to OMIT those
                // sub-triangles from the heightfield (otherwise the navmesh
                // sees solid terrain at cave entrances and never links to the
                // WMO interior). Mirrors MapBuilder.cpp:280-290 "if (holes
                // & mask) continue;". adt.GetChunkHoles() returns the raw
                // MCNK.holes bitmask for this chunk.
                uint chunkHoles = adt.GetChunkHoles(chunkIdx); // ushort promoted to uint for the bit test below
                for (int z = 0; z < 8; z++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        // MCNK.holes layout: 4×4 grid, each bit = a 2×2
                        // zone of sub-triangles at index (z/2, x/2). Bit
                        // set → that zone is a hole, skip all 4 sub-tris.
                        int holeBit = (z / 2) * 4 + (x / 2);
                        if (chunkHoles != 0 && (chunkHoles & (1u << holeBit)) != 0)
                            continue; // cave entrance / pit: no terrain tris here

                        // Road mask: same 4×4 layout as holes. Bit set at
                        // (z/2, x/2) → sub-triangle inherits NAV_ROAD area.
                        // This is the per-sub-triangle precision that lets
                        // the bot path along the center of a road strip
                        // instead of the entire chunk (33.33 yards wide).
                        byte areaType = baseAreaType;
                        if (isCenterChunk && centerRoadMask != null)
                        {
                            int roadByte = chunkMaskByteBase + (holeBit >> 3);
                            int roadBit  = holeBit & 7;
                            if ((centerRoadMask[roadByte] & (1 << roadBit)) != 0)
                                areaType = 4; // NAV_ROAD
                        }

                        int v9_00 = vertexOffset + z * 9 + x;
                        int v9_01 = vertexOffset + z * 9 + x + 1;
                        int v9_10 = vertexOffset + (z + 1) * 9 + x;
                        int v9_11 = vertexOffset + (z + 1) * 9 + x + 1;
                        int v8c = v8BaseOffset + z * 8 + x;

                        indices.Add(v9_00); indices.Add(v8c); indices.Add(v9_01);
                        indices.Add(v9_01); indices.Add(v8c); indices.Add(v9_11);
                        indices.Add(v9_11); indices.Add(v8c); indices.Add(v9_10);
                        indices.Add(v9_10); indices.Add(v8c); indices.Add(v9_00);

                        areas.Add(areaType); areas.Add(areaType);
                        areas.Add(areaType); areas.Add(areaType);
                    }
                }
                vertexOffset = vertices.Count / 3;
            }
        }

        return new TileGeometry(vertices.ToArray(), indices.ToArray(), areas.ToArray());
    }

    private BuildSubTileResult[] BuildNavMeshSubTilesSync(
        uint mapId, TileGeometry geo, int adtX, int adtY, int maxAdtX, int maxAdtY,
        float adtMinX, float adtMinZ, CancellationToken ct = default)
    {
        // Filter offmesh connections for this ADT tile once — same as C++ loadOffMeshConnections(mapID, tileX, tileY)
        var tileConns = _offMeshConnections
            .Where(c => c.MapId == (int)mapId && c.TileX == adtX && c.TileY == adtY)
            .ToArray();

        float[] omVerts  = tileConns.Length > 0 ? new float[tileConns.Length * 6]  : Array.Empty<float>();
        float[] omRads   = tileConns.Length > 0 ? new float[tileConns.Length]       : Array.Empty<float>();
        byte[]  omDirs   = tileConns.Length > 0 ? new byte[tileConns.Length]        : Array.Empty<byte>();
        byte[]  omAreas  = tileConns.Length > 0 ? new byte[tileConns.Length]        : Array.Empty<byte>();
        ushort[] omFlags = tileConns.Length > 0 ? new ushort[tileConns.Length]      : Array.Empty<ushort>();
        for (int k = 0; k < tileConns.Length; k++)
        {
            var c = tileConns[k];
            omVerts[k * 6 + 0] = c.V0X; omVerts[k * 6 + 1] = c.V0Y; omVerts[k * 6 + 2] = c.V0Z;
            omVerts[k * 6 + 3] = c.V1X; omVerts[k * 6 + 4] = c.V1Y; omVerts[k * 6 + 5] = c.V1Z;
            omRads[k]   = c.Radius;
            omDirs[k]   = c.Direction;
            omAreas[k]  = c.Area;
            omFlags[k]  = c.Flags;
        }

        var subTiles = new BuildSubTileResult[WowConstants.SubTilesPerAdtSide * WowConstants.SubTilesPerAdtSide];

        int totalSlots = WowConstants.SubTilesPerAdtSide * WowConstants.SubTilesPerAdtSide;

        // Border expansion (world units) applied to each sub-tile bbox before rasterization
        // and (when filtering is enabled) before the spatial pre-filter. Mirrors the same
        // borderMeters computed inside BuildNavMeshTileSync, so the filter bbox is a safe
        // superset of the triangles Recast would actually use.
        int borderSize = Math.Max(7, _recastConfig.WalkableRadius + 3);
        float borderMeters = borderSize * _recastConfig.CellSize;

        // Sub-tiles are built sequentially within each ADT worker.
        // Parallelism is controlled at the outer tile level (Parallel.ForEachAsync);
        // parallelizing here too would multiply the thread count by itself.
        for (int slot = 0; slot < totalSlots; slot++)
        {
            ct.ThrowIfCancellationRequested();
            int subX = slot % WowConstants.SubTilesPerAdtSide;
            int subY = slot / WowConstants.SubTilesPerAdtSide;

            float minX = adtMinX + subX * WowConstants.SubTileSize;
            float maxX = minX + WowConstants.SubTileSize;
            float minZ = adtMinZ + subY * WowConstants.SubTileSize;
            float maxZ = minZ + WowConstants.SubTileSize;

            int detourTileX = (maxAdtX - adtX) * WowConstants.SubTilesPerAdtSide + subX;
            int detourTileY = (maxAdtY - adtY) * WowConstants.SubTilesPerAdtSide + subY;

            // Spatial pre-filter: cut the geometry to the triangles that can possibly
            // contribute to this sub-tile. The vertex array is shared unchanged (the
            // compacted tris still index into geo.Vertices); only tris + areas are
            // compacted. See BuildFilteredGeometryForSubTile for the bit-identical proof.
            int[] trisUsed = geo.Indices;
            byte[] areasUsed = geo.Areas;
            int triCountUsed = geo.IndexCount / 3;

            if (EnableSpatialFilter)
            {
                var (fTris, fAreas, fCount) = BuildFilteredGeometryForSubTile(
                    geo,
                    minX - borderMeters, maxX + borderMeters,
                    minZ - borderMeters, maxZ + borderMeters);

                if (fCount > 0)
                {
                    trisUsed = fTris;
                    areasUsed = fAreas;
                    triCountUsed = fCount;

                    _logger.LogDebug("[Mmap] Spatial filter sub-tile ({SX},{SY}) adt=({AX},{AY}): " +
                        "{Kept}/{Total} triangles kept ({Pct:F1}%)",
                        subX, subY, adtX, adtY, fCount, geo.IndexCount / 3,
                        100.0 * fCount / (geo.IndexCount / 3));
                }
                // If fCount == 0 the sub-tile has no geometry — let BuildNavMeshTileSync
                // run on the full arrays and produce the same empty result as before,
                // to stay bit-identical with the unfiltered path in degenerate cases.
            }

            subTiles[slot] = BuildNavMeshTileSync(
                geo, trisUsed, areasUsed, triCountUsed,
                detourTileX, detourTileY,
                minX, minZ, maxX, maxZ,
                omVerts, omRads, omDirs, omAreas, omFlags);
        }

        return subTiles;
    }

    /// <summary>
    /// Spatially filters the ADT geometry down to the triangles that can possibly
    /// contribute to a given sub-tile. Returns a compacted (tris, areas) pair along
    /// with the reduced triCount; the vertex array is SHARED UNCHANGED with the
    /// caller (indices in the compacted tris still reference the original verts).
    ///
    /// This is the hot-loop optimisation: instead of feeding 1M+ triangles to every
    /// one of the 36 internal mini-tile builds in BuildTile (each of which re-runs
    /// rcClearUnwalkableTriangles + 2× memcpy + 2× rewrite loop over triCount, none
    /// of which has any bbox culling), we cut triCount to the triangles whose AABB
    /// overlaps the sub-tile's expanded bbox before the P/Invoke.
    ///
    /// Bit-identical guarantee (verified against Recast source):
    ///   - rcClearUnwalkableTriangles (Recast.cpp:360) computes each triangle's
    ///     normal independently and never reads neighbour triangles → filtering
    ///     out triangle X cannot change the area assignment of triangle Y.
    ///   - rcRasterizeTriangles → rasterizeTri (RecastRasterization.cpp:325) already
    ///     early-outs via overlapBounds(triBB, hfBB) for any triangle outside the
    ///     heightfield bbox, so a triangle we keep but that falls outside the
    ///     heightfield still produces zero spans — exactly as before.
    /// The filter bbox uses the same borderMeters expansion as the heightfield
    /// (BuildNavMeshTileSync), so it is a safe superset of the triangles Recast
    /// would actually use.
    ///
    /// Returns triCount == 0 if no triangle qualifies (caller keeps original arrays
    /// in that case via the EnableSpatialFilter branch in BuildNavMeshSubTilesSync).
    /// </summary>
    private static (int[] filteredTris, byte[] filteredAreas, int triCount) BuildFilteredGeometryForSubTile(
        TileGeometry geo, float filterMinX, float filterMaxX, float filterMinZ, float filterMaxZ)
    {
        int[] srcTris = geo.Indices;
        byte[] srcAreas = geo.Areas;
        float[] srcVerts = geo.Vertices;

        int triTotal = srcTris.Length / 3;
        // Worst case: all triangles qualify. Allocate once, no List<int> boxing.
        int[] outTris = new int[srcTris.Length];
        byte[] outAreas = new byte[triTotal];
        int kept = 0;

        for (int t = 0; t < triTotal; t++)
        {
            int i0 = srcTris[t * 3 + 0];
            int i1 = srcTris[t * 3 + 1];
            int i2 = srcTris[t * 3 + 2];

            // Compute the triangle's XZ AABB and test against the filter bbox.
            // Vertices are packed (x,y,z) → stride 3; X is [0], Z is [2].
            float vx0 = srcVerts[i0 * 3 + 0];
            float vz0 = srcVerts[i0 * 3 + 2];
            float vx1 = srcVerts[i1 * 3 + 0];
            float vz1 = srcVerts[i1 * 3 + 2];
            float vx2 = srcVerts[i2 * 3 + 0];
            float vz2 = srcVerts[i2 * 3 + 2];

            float tMinX = vx0; if (vx1 < tMinX) tMinX = vx1; if (vx2 < tMinX) tMinX = vx2;
            float tMaxX = vx0; if (vx1 > tMaxX) tMaxX = vx1; if (vx2 > tMaxX) tMaxX = vx2;
            float tMinZ = vz0; if (vz1 < tMinZ) tMinZ = vz1; if (vz2 < tMinZ) tMinZ = vz2;
            float tMaxZ = vz0; if (vz1 > tMaxZ) tMaxZ = vz1; if (vz2 > tMaxZ) tMaxZ = vz2;

            // overlapBounds: aMin <= bMax && aMax >= bMin (per axis).
            if (tMinX > filterMaxX || tMaxX < filterMinX ||
                tMinZ > filterMaxZ || tMaxZ < filterMinZ)
            {
                continue; // triangle entirely outside the sub-tile's expanded bbox
            }

            outTris[kept * 3 + 0] = i0;
            outTris[kept * 3 + 1] = i1;
            outTris[kept * 3 + 2] = i2;
            outAreas[kept] = srcAreas[t];
            kept++;
        }

        return (outTris, outAreas, kept);
    }

    private unsafe BuildSubTileResult BuildNavMeshTileSync(
        TileGeometry geo, int[] tris, byte[] areas, int triCount,
        int adtX, int adtY,
        float minX, float minZ, float maxX, float maxZ,
        float[] omVerts, float[] omRads, byte[] omDirs, byte[] omAreas, ushort[] omFlags)
    {
        // vertCount is the ORIGINAL vertex count — the (possibly filtered) tris array
        // still indexes into geo.Vertices, so the DLL sees the full vertex range even
        // when triCount has been cut by the spatial filter.
        int vertCount = geo.VertexCount;
        if (triCount <= 0 || tris.Length == 0)
            return new BuildSubTileResult(RecastBuildResult.Empty, null);

        var p = new RecastBuildParams
        {
            CellSize = _recastConfig.CellSize,
            CellHeight = _recastConfig.CellHeight,
            WalkableSlopeAngle = _recastConfig.WalkableSlopeAngle,
            WalkableHeight = _recastConfig.WalkableHeight,
            WalkableRadius = _recastConfig.WalkableRadius,
            WalkableClimb = _recastConfig.WalkableClimb,
            // Tuned for sub-tile portal connection: 21 m² minimum region so
            // narrow cave entrances / corridor floors aren't erased before
            // Recast can form the polygons that reach the sub-tile border.
            MinRegionArea = 100,
            MergeRegionArea = 400,
            MaxSimplificationError = 1.3f,
            TileX = adtX,
            TileY = adtY,
            MaxVertsPerPoly = 6,
            // BorderSize = 3.0 yards / cs ≈ 7 voxels (MaNGOS BORDER_RADIUS).
            // WalkableRadius + 3 was only ~4 voxels (1.84m) — too thin to
            // cover a WMO that crosses a sub-tile boundary, leaving the
            // cave floor short of the border so no Detour portal forms.
            BorderSize = Math.Max(7, _recastConfig.WalkableRadius + 3),
            // World-unit Detour params (Mangos C++ hardcodes these — they
            // land in the dtMeshHeader of every tile and are read back by
            // Detour at pathfinding time). With these at 0, the dtMeshHeader
            // is malformed → cross-tile portal links can be rejected as
            // "no free space" and BuildBvTree=0 makes every query do a
            // linear scan over all polys.
            WalkableHeightWorld = 2.22155f,
            WalkableRadiusWorld = 0.4f,
            WalkableClimbWorld  = 1.0f,
            BuildBvTree         = 1
        };

        // Expand the bounding box by BorderSize * CellSize on each XZ side so that
        // neighboring geometry is rasterized — this ensures polygon edges reach the
        // tile boundary and Detour can form portal links between adjacent sub-tiles.
        float borderMeters = p.BorderSize * _recastConfig.CellSize;
        float expandedMinX = minX - borderMeters;
        float expandedMaxX = maxX + borderMeters;
        float expandedMinZ = minZ - borderMeters;
        float expandedMaxZ = maxZ + borderMeters;

        // Compute Y bounds from vertices inside the sub-tile's EXPANDED XZ bbox
        // (sub-tile + border). Two reasons:
        //
        //   1. Without the XZ filter, far-away vertices from the 3x3 ADT
        //      neighborhood (mountain peaks at Y=1300 vs local terrain at Y=-400)
        //      inflated the heightfield Y range by ~1700 yards, far more than
        //      walkableClimb cells — rcFilterLedgeSpans then stripped legitimate
        //      high spans as ledges.
        //
        //   2. Darnassus/Teldrassil sits on top of a giant tree. The branches
        //      that hang down on the sides of the tree are flagged as walkable in
        //      the ADT data. On a sub-tile at the edge of the tree, a full Y
        //      range that includes the city platform (Y=top) AND those hanging
        //      branches (Y=top-500 to top-700) gives a heightfield wide enough to
        //      rasterize the branches as isolated walkable spans — the bot then
        //      pathfinds onto the side branches instead of staying on the city.
        //      With a tight sub-tile Y range, only the local geometry defines the
        //      heightfield and rcFilterLedgeSpans correctly treats the branches
        //      as ledges relative to the local ground.
        //
        // Also fixes a long-standing off-by-one in the iteration: the loop used
        // to start at v=1 with stride 3, which means it was reading Y values of
        // every vertex while the comment claimed it was reading X. Any XZ filter
        // based on this comparison was therefore silently broken — v=0 with
        // stride 3 reads X,Y,Z correctly and the XZ bbox check actually filters.
        float minY = float.MaxValue, maxYh = float.MinValue;
        for (int v = 0; v < geo.Vertices.Length; v += 3)
        {
            float vx = geo.Vertices[v];
            float vy = geo.Vertices[v + 1];
            float vz = geo.Vertices[v + 2];
            if (vx < expandedMinX || vx > expandedMaxX ||
                vz < expandedMinZ || vz > expandedMaxZ)
                continue;
            if (vy < minY) minY = vy;
            if (vy > maxYh) maxYh = vy;
        }
        if (minY == float.MaxValue) { minY = 0; maxYh = 100f; }

        fixed (float* v = geo.Vertices)
        fixed (int* i = tris)
        fixed (byte* a = areas)
        fixed (float* pOmVerts = omVerts.Length > 0 ? omVerts : new float[6])
        fixed (float* pOmRads  = omRads.Length  > 0 ? omRads  : new float[1])
        fixed (byte*  pOmDirs  = omDirs.Length  > 0 ? omDirs  : new byte[1])
        fixed (byte*  pOmAreas = omAreas.Length > 0 ? omAreas : new byte[1])
        fixed (ushort* pOmFlags = omFlags.Length > 0 ? omFlags : new ushort[1])
        {
            p.BoundingBoxMinX = expandedMinX;
            p.BoundingBoxMinY = minY - 2f;
            p.BoundingBoxMinZ = expandedMinZ;
            p.BoundingBoxMaxX = expandedMaxX;
            p.BoundingBoxMaxY = maxYh + 2f;
            p.BoundingBoxMaxZ = expandedMaxZ;

            _logger.LogDebug("[Mmap] RecastBuildParams: adt=({AX},{AY}) " +
                "bbox=[{MinX:F2},{MinY:F2},{MinZ:F2}]→[{MaxX:F2},{MaxY:F2},{MaxZ:F2}] " +
                "cellSize={CS:F6} cellHeight={CH:F3} input: {Verts} verts {Tris} tris {OmCount} offmesh",
                adtX, adtY,
                p.BoundingBoxMinX, p.BoundingBoxMinY, p.BoundingBoxMinZ,
                p.BoundingBoxMaxX, p.BoundingBoxMaxY, p.BoundingBoxMaxZ,
                p.CellSize, p.CellHeight, vertCount, triCount, omVerts.Length / 6);

            byte* outData;
            int outSize;
            int omCount = omVerts.Length / 6;

            _logger.LogDebug("[Mmap] BuildTile DLL call: adt=({AX},{AY}) {Verts}v {Tris}t", adtX, adtY, vertCount, triCount);
            var status = RecastNative.BuildTileDetailed(&p, v, vertCount, i, triCount, a,
                omCount > 0 ? pOmVerts : null,
                omCount > 0 ? pOmRads  : null,
                omCount > 0 ? pOmDirs  : null,
                omCount > 0 ? pOmAreas : null,
                omCount > 0 ? pOmFlags : null,
                omCount, &outData, &outSize);

            if (status == RecastBuildResult.Empty)
            {
                _logger.LogDebug("[Mmap] BuildTile empty/no polygons for adt=({AX},{AY})", adtX, adtY);
                return new BuildSubTileResult(status, null);
            }

            if (status != RecastBuildResult.Success)
            {
                _logger.LogWarning("[Mmap] BuildTile FAILED for adt=({AX},{AY}): {Status}", adtX, adtY, status);
                return new BuildSubTileResult(status, null);
            }

            _logger.LogDebug("[Mmap] BuildTile DLL returned: adt=({AX},{AY}) size={Size}", adtX, adtY, outSize);
            if (outData == null || outSize <= 0)
            {
                _logger.LogWarning("[Mmap] BuildTile returned empty for adt=({AX},{AY})", adtX, adtY);
                return new BuildSubTileResult(RecastBuildResult.Empty, null);
            }

            var result = new byte[outSize];
            fixed (byte* dest = result)
            {
                Buffer.MemoryCopy(outData, dest, outSize, outSize);
            }

            RecastNative.FreeBuffer(outData);
            return new BuildSubTileResult(RecastBuildResult.Success, result);
        }
    }

    private async Task WriteMmtileAsync(uint mapId, int fileTileX, int fileTileY, byte[]?[] navData, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(_outputDir);

            // {mapId:D3}{adtY:D2}{adtX:D2}.mmtile — same convention as map-extractor output
            string fileName = $"{mapId:D3}{fileTileX:D2}{fileTileY:D2}.mmtile";
            string filePath = Path.Combine(_outputDir, fileName);

            int payloadSize = navData.Sum(blob => 4 + (blob?.Length ?? 0));
            _logger.LogInformation("[Mmap] Writing {FileName}: {SubTiles} sub-tiles ({Size} bytes)",
                fileName, navData.Length, 20 + payloadSize);

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);
            writer.Write(MagicBytes.MmapMagic);
            writer.Write(MagicBytes.DtNavMeshVersion);
            writer.Write(MagicBytes.MmapMultiTileVersion);
            writer.Write((uint)navData.Length);
            writer.Write(1u);
            foreach (byte[]? blob in navData)
            {
                writer.Write((uint)(blob?.Length ?? 0));
                if (blob != null && blob.Length > 0)
                    writer.Write(blob);
            }
        }, ct);
    }

    internal void ClearCache() => _adtParser.ClearCache();

    // -----------------------------------------------------------------------
    // WMO geometry: loads collision mesh from all WMO placements in an ADT.
    // Transform math mirrors TerrainBuilder::loadVMap + transform() + copyVertices().
    // -----------------------------------------------------------------------

    private async Task AppendWmoGeometryAsync(
        AdtFile adt,
        List<float> outVerts,
        List<int> outTris,
        List<byte> outAreas,
        HashSet<uint> seenUniqueIds,
        CancellationToken ct)
    {
        foreach (var modf in adt.WmoPlacements)
        {
            if (!seenUniqueIds.Add(modf.UniqueId))
                continue; // already processed this WMO placement

            if (modf.NameId >= adt.WmoNames.Length)
                continue;

            string wmoName = adt.WmoNames[modf.NameId];
            if (string.IsNullOrEmpty(wmoName))
                continue;

            var wmoResult = await _wmoParser.ParseRootAsync(wmoName, ct);
            if (!wmoResult.Success || wmoResult.Root == null)
                continue;

            // Build rotation matrix matching C++ TerrainBuilder:
            //   G3D: rotation = fromEulerAnglesXYZ(iRot.z*pi/-180, iRot.x*pi/-180, iRot.y*pi/-180)
            //   G3D v * kXMat(θ) = Rx(-θ) because G3D column-vector matrix used in row-vector multiply.
            //   So v * kXMat(-α) = Rx(α). Net effect: Rx(RotZ*π/180), Ry(RotX*π/180), Rz(RotY*π/180).
            //   System.Numerics CreateRotationX(θ) = standard Rx(θ) → use POSITIVE angles.
            float ax = modf.RotationZ * MathF.PI / 180f;
            float ay = modf.RotationX * MathF.PI / 180f;
            float az = modf.RotationY * MathF.PI / 180f;
            var rotMatrix = Matrix4x4.CreateRotationX(ax)
                          * Matrix4x4.CreateRotationY(ay)
                          * Matrix4x4.CreateRotationZ(az);

            // Position: fixCoords(rawPos) = (pos.z, pos.x, pos.y)
            //   iPos.x = PositionZ,  iPos.y = PositionX,  iPos.z = PositionY (height)
            //   position = iPos - (32*TileSize, 32*TileSize, 0)
            float posX = modf.PositionZ - 32f * WowConstants.TileSize;
            float posY = modf.PositionX - 32f * WowConstants.TileSize;
            float posZ = modf.PositionY; // height, no adjustment

            for (uint groupIdx = 0; groupIdx < wmoResult.Root.Header.GroupCount; groupIdx++)
            {
                ct.ThrowIfCancellationRequested();

                string groupPath = BuildWmoGroupPath(wmoName, groupIdx);
                var grp = await _wmoParser.ParseGroupAsync(groupPath, (int)groupIdx, wmoName, ct);
                if (grp == null)
                {
                    _logger.LogInformation("[WMO] {Path} group {Idx}: parse returned null", wmoName, groupIdx);
                    continue;
                }
                if (grp.Vertices.Length == 0 || grp.Triangles.Length == 0)
                {
                    _logger.LogInformation("[WMO] {Path} group {Idx}: {V} verts, {T} tris → skipped (empty)", wmoName, groupIdx, grp.Vertices.Length, grp.Triangles.Length);
                    continue;
                }
                _logger.LogInformation("[WMO] {Path} group {Idx}: {V} verts, {T} tris → adding", wmoName, groupIdx, grp.Vertices.Length, grp.Triangles.Length);

                // Precise mode (preciseVectorData=true): include ALL triangles.
                // Matches MaNGOS C++ default — no MOPY filtering for mmap/navmesh.
                // Recast's slope filter handles walls (too steep = non-walkable);
                // solid geometry blocks agents from walking through buildings.
                var validTris = new List<int>(grp.Triangles.Length);
                for (int t = 0; t < grp.Triangles.Length; t++)
                    validTris.Add(t);

                if (validTris.Count == 0)
                    continue;

                // Collect the vertex indices actually referenced by valid triangles
                var usedVertSet = new HashSet<ushort>(validTris.Count * 3);
                foreach (int t in validTris)
                {
                    var tri = grp.Triangles[t];
                    usedVertSet.Add(tri.I0);
                    usedVertSet.Add(tri.I1);
                    usedVertSet.Add(tri.I2);
                }

                // Transform and add referenced vertices; build old→new index map
                var vertRemap = new Dictionary<ushort, int>(usedVertSet.Count);
                foreach (ushort vi in usedVertSet)
                {
                    if (vi >= grp.Vertices.Length)
                        continue;
                    var v = grp.Vertices[vi];
                    vertRemap[vi] = outVerts.Count / 3;
                    // VMapExtractor stores raw MOVT vertices (no fixCoords on individual vertices).
                    // TerrainBuilder reads them raw — coordinate mapping is done by transform() itself.
                    AppendTransformedVertex(v.X, v.Y, v.Z, rotMatrix, 1.0f, posX, posY, posZ, outVerts);
                }

                // Add triangles (no winding flip for WMO — isM2=false in original)
                foreach (int t in validTris)
                {
                    var tri = grp.Triangles[t];
                    if (!vertRemap.TryGetValue(tri.I0, out int r0)
                        || !vertRemap.TryGetValue(tri.I1, out int r1)
                        || !vertRemap.TryGetValue(tri.I2, out int r2))
                        continue;
                    outTris.Add(r0);
                    outTris.Add(r1);
                    outTris.Add(r2);
                    outAreas.Add(1); // NAV_GROUND
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // M2 geometry: reads bounding (collision) mesh from M2 files directly.
    // The vmap-extractor swaps I1↔I2 per triangle and then TerrainBuilder flips
    // again (isM2=true), so the net winding is identical to the raw M2 indices.
    // -----------------------------------------------------------------------

    private void AppendM2Geometry(
        AdtFile adt,
        List<float> outVerts,
        List<int> outTris,
        List<byte> outAreas,
        HashSet<uint> seenUniqueIds)
    {
        foreach (var mddf in adt.DoodadPlacements)
        {
            // mddf.FileDataId holds the placement uniqueId in WoW MDDF binary layout
            if (!seenUniqueIds.Add(mddf.FileDataId))
                continue; // already processed this M2 placement

            if (mddf.NameId >= adt.ModelNames.Length)
                continue;

            string modelName = adt.ModelNames[mddf.NameId];
            if (string.IsNullOrEmpty(modelName))
                continue;

            if (!_m2Parser.TryParseBoundingMesh(modelName, out float[] mverts, out ushort[] minds))
                continue;
            if (mverts.Length == 0 || minds.Length == 0)
                continue;

            // Build rotation matrix.
            // MDDF rotation binary order: RotationY (yaw, first), RotationX (pitch, second), RotationZ (roll, third)
            // In vmap: rot.x = ff[0] = mddf.RotationY (C# field), rot.y = ff[1] = mddf.RotationX, rot.z = ff[2] = mddf.RotationZ
            // Same sign fix as WMO: C++ G3D v*kXMat(-α)=Rx(α), so use POSITIVE angles.
            //   ax = RotationZ*π/180, ay = RotationY*π/180, az = RotationX*π/180
            float ax = mddf.RotationZ * MathF.PI / 180f;
            float ay = mddf.RotationY * MathF.PI / 180f; // C# field RotationY = yaw = vmap rot.x
            float az = mddf.RotationX * MathF.PI / 180f; // C# field RotationX = pitch = vmap rot.y
            var rotMatrix = Matrix4x4.CreateRotationX(ax)
                          * Matrix4x4.CreateRotationY(ay)
                          * Matrix4x4.CreateRotationZ(az);

            // Position: fixCoords(rawPos) = (pos.z, pos.x, pos.y)
            //   iPos.x = PositionZ,  iPos.y = PositionX,  iPos.z = PositionY (height)
            float posX = mddf.PositionZ - 32f * WowConstants.TileSize;
            float posY = mddf.PositionX - 32f * WowConstants.TileSize;
            float posZ = mddf.PositionY;
            float scale = mddf.Scale / 1024f;

            int vertBase = outVerts.Count / 3;
            int nVerts = mverts.Length / 3;
            for (int i = 0; i < nVerts; i++)
            {
                float vx = mverts[i * 3 + 0];
                float vy = mverts[i * 3 + 1];
                float vz = mverts[i * 3 + 2];
                // M2 is Z-up: pass raw vertices — no fixCoords needed (mirrors original VMap pipeline
                // where M2 vertices are written raw and TerrainBuilder rotates them directly)
                AppendTransformedVertex(vx, vy, vz, rotMatrix, scale, posX, posY, posZ, outVerts);
            }

            // Add triangles (no net winding flip — double-flip in original cancels out)
            int nIndices = minds.Length;
            for (int i = 0; i < nIndices; i += 3)
            {
                outTris.Add(vertBase + minds[i]);
                outTris.Add(vertBase + minds[i + 1]);
                outTris.Add(vertBase + minds[i + 2]);
                outAreas.Add(1); // NAV_GROUND
            }
        }
    }

    // -----------------------------------------------------------------------
    // Shared transform helper — mirrors TerrainBuilder::transform() + copyVertices()
    // -----------------------------------------------------------------------

    private static void AppendTransformedVertex(
        float vx, float vy, float vz,
        Matrix4x4 rotMatrix,
        float scale,
        float posX, float posY, float posZ,
        List<float> outVerts)
    {
        // v_world = v_model * rotMatrix * scale + position  (row-vector convention)
        var v = Vector3.Transform(new Vector3(vx, vy, vz), rotMatrix) * scale
                + new Vector3(posX, posY, posZ);

        // Flip X and Y axes (same as original C++ transform())
        v.X *= -1f;
        v.Y *= -1f;

        // copyVertices: Recast(X, Y, Z) = (v.Y, v.Z, v.X)
        outVerts.Add(v.Y); // Recast X
        outVerts.Add(v.Z); // Recast Y (height)
        outVerts.Add(v.X); // Recast Z
    }

    private GameObjectModelData? LoadGameObjectModel(GameObjectModelRef modelRef)
    {
        if (modelRef.IsWmo)
            return LoadGameObjectWmo(modelRef.Path);

        return LoadGameObjectM2(modelRef.Path);
    }

    private GameObjectModelData? LoadGameObjectM2(string modelPath)
    {
        if (!_m2Parser.TryParseBoundingMesh(modelPath, out float[] mverts, out ushort[] indices))
            return null; // file not found or fundamentally invalid

        if (mverts.Length == 0 || indices.Length == 0)
        {
            // Valid M2 but no bounding collision mesh — return empty sentinel so caller
            // can count it as skippedNoBounds (normal for decorative objects).
            return new GameObjectModelData(modelPath, IsM2: true,
                Array.Empty<float>(), Array.Empty<int>(),
                Vector3.Zero, Vector3.Zero);
        }

        // Apply fixCoords(v) = (v.Z, v.X, v.Y) to match the VMap pipeline:
        // VMapExtractor stores M2 vertices as fixCoords(raw), so TerrainBuilder reads them
        // pre-transformed. LoadGameObjectWmo does the same. We must be consistent.
        int nv = mverts.Length / 3;
        float[] vertices = new float[nv * 3];
        Vector3 boundsMin = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 boundsMax = new(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < nv; i++)
        {
            float rx = mverts[i * 3 + 2]; // fixCoords: vz → stored x
            float ry = mverts[i * 3 + 0]; // fixCoords: vx → stored y
            float rz = mverts[i * 3 + 1]; // fixCoords: vy → stored z
            vertices[i * 3 + 0] = rx;
            vertices[i * 3 + 1] = ry;
            vertices[i * 3 + 2] = rz;
            boundsMin.X = MathF.Min(boundsMin.X, rx);
            boundsMin.Y = MathF.Min(boundsMin.Y, ry);
            boundsMin.Z = MathF.Min(boundsMin.Z, rz);
            boundsMax.X = MathF.Max(boundsMax.X, rx);
            boundsMax.Y = MathF.Max(boundsMax.Y, ry);
            boundsMax.Z = MathF.Max(boundsMax.Z, rz);
        }

        return new GameObjectModelData(
            modelPath,
            IsM2: true,
            vertices,
            Array.ConvertAll(indices, static x => (int)x),
            boundsMin,
            boundsMax);
    }

    private GameObjectModelData? LoadGameObjectWmo(string modelPath)
    {
        var rootResult = _wmoParser.ParseRootAsync(modelPath).GetAwaiter().GetResult();
        if (!rootResult.Success || rootResult.Root == null)
            return null;

        var vertices = new List<float>();
        var indices = new List<int>();

        for (uint groupIdx = 0; groupIdx < rootResult.Root.Header.GroupCount; groupIdx++)
        {
            string groupPath = BuildWmoGroupPath(modelPath, groupIdx);
            var group = _wmoParser.ParseGroupAsync(groupPath, (int)groupIdx, modelPath).GetAwaiter().GetResult();
            if (group == null || group.Vertices.Length == 0 || group.Triangles.Length == 0)
                continue;

            // Precise mode: include ALL triangles (no MOPY filtering).
            var validTris = new List<int>(group.Triangles.Length);
            for (int t = 0; t < group.Triangles.Length; t++)
                validTris.Add(t);
            var usedVerts = new HashSet<ushort>(validTris.Count * 3);
            foreach (int triIndex in validTris)
            {
                var tri = group.Triangles[triIndex];
                usedVerts.Add(tri.I0);
                usedVerts.Add(tri.I1);
                usedVerts.Add(tri.I2);
            }

            var remap = new Dictionary<ushort, int>(usedVerts.Count);
            foreach (ushort vertIndex in usedVerts)
            {
                if (vertIndex >= group.Vertices.Length)
                    continue;

                var vertex = group.Vertices[vertIndex];
                remap[vertIndex] = vertices.Count / 3;
                // fixCoords(v) = (v.Z, v.X, v.Y) in the old VMAP pipeline before transform()
                vertices.Add(vertex.Z);
                vertices.Add(vertex.X);
                vertices.Add(vertex.Y);
            }

            foreach (int triIndex in validTris)
            {
                var tri = group.Triangles[triIndex];
                if (!remap.TryGetValue(tri.I0, out int r0)
                    || !remap.TryGetValue(tri.I1, out int r1)
                    || !remap.TryGetValue(tri.I2, out int r2))
                    continue;

                indices.Add(r0);
                indices.Add(r1);
                indices.Add(r2);
            }
        }

        if (vertices.Count == 0 || indices.Count == 0)
            return null;

        Vector3 boundsMin = new(
            rootResult.Root.Header.BoundingBoxMin.X,
            rootResult.Root.Header.BoundingBoxMin.Y,
            rootResult.Root.Header.BoundingBoxMin.Z);
        Vector3 boundsMax = new(
            rootResult.Root.Header.BoundingBoxMax.X,
            rootResult.Root.Header.BoundingBoxMax.Y,
            rootResult.Root.Header.BoundingBoxMax.Z);

        return new GameObjectModelData(modelPath, IsM2: false, vertices.ToArray(), indices.ToArray(), boundsMin, boundsMax);
    }

    private static void AppendGameObjectGeometry(
        GoSpawn spawn,
        GameObjectModelData modelData,
        List<float> outVerts,
        List<int> outIndices,
        List<byte> outAreas)
    {
        Quaternion quat = new(spawn.Rot0, spawn.Rot1, spawn.Rot2, spawn.Rot3);
        var rotation = Matrix4x4.Transpose(Matrix4x4.CreateFromQuaternion(quat));

        // position = (pos[0]-32G, pos[1]-32G, pos[2]) — mirrors TerrainBuilder::loadGameObjects
        float posX = spawn.PosX - 32f * WowConstants.TileSize;
        float posY = spawn.PosY - 32f * WowConstants.TileSize;
        float posZ = spawn.PosZ;
        int baseIndex = outVerts.Count / 3;

        for (int i = 0; i < modelData.Vertices.Length; i += 3)
        {
            float vx = modelData.Vertices[i + 0];
            float vy = modelData.Vertices[i + 1];
            float vz = modelData.Vertices[i + 2];
            AppendTransformedVertex(vx, vy, vz, rotation, spawn.Scale, posX, posY, posZ, outVerts);
        }

        // M2 models need a winding reorder — mirrors TerrainBuilder::copyIndices
        // (flip=true). The C++ writes (idx2, idx1, idx0) for an M2 with loaded
        // swapped .vmd indices; for the C# (which loads M2 indices directly from
        // the MPQ in raw (A, B, C) order) the equivalent reorder is (I1, I2, I0) =
        // (B, C, A) — a cyclic permutation by 2, which preserves the original
        // M2 winding. Using (I2, I1, I0) = (C, B, A) instead would be a
        // transposition that flips the winding — see the matching fix in
        // MangosVmapGeometryLoader.AppendSpawnCollision for the full rationale.
        // WMO GOs keep the original winding (isM2=false → no flip).
        bool flipWinding = modelData.IsM2;
        for (int i = 0; i < modelData.Indices.Length; i += 3)
        {
            if (flipWinding)
            {
                outIndices.Add(baseIndex + modelData.Indices[i + 1]);
                outIndices.Add(baseIndex + modelData.Indices[i + 2]);
                outIndices.Add(baseIndex + modelData.Indices[i + 0]);
            }
            else
            {
                outIndices.Add(baseIndex + modelData.Indices[i + 0]);
                outIndices.Add(baseIndex + modelData.Indices[i + 1]);
                outIndices.Add(baseIndex + modelData.Indices[i + 2]);
            }
            outAreas.Add(1);
        }
    }

    // -----------------------------------------------------------------------
    // OffMesh connections: ported from TerrainBuilder::loadOffMeshConnections
    // Format: mapId tx,ty (x y z) (x y z) size [areaType] [direction]
    // Verts stored as (p0[1], p0[2], p0[0]) per C++ convention (same as solidVerts space).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a WMO group file path from the root WMO path.
    /// Handles both uppercase (.WMO) and lowercase (.wmo) extensions.
    /// Mirrors C++ vmap-extractor: sprintf("%s_%03d.wmo", baseName, i)
    /// "STORMWIND.WMO" + 110 → "STORMWIND_110.wmo"
    /// </summary>
    private static string BuildWmoGroupPath(string wmoPath, uint groupIdx)
    {
        int extIndex = wmoPath.LastIndexOf('.');
        if (extIndex < 0)
            return wmoPath + "_" + groupIdx.ToString("D3", CultureInfo.InvariantCulture) + ".wmo";

        return string.Concat(
            wmoPath.AsSpan(0, extIndex),
            "_",
            groupIdx.ToString("D3", CultureInfo.InvariantCulture),
            ".wmo");
    }

    private static readonly Regex OffMeshLineRegex = new(
        @"^(\d+)\s+(-?\d+),(-?\d+)\s+\((-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\)\s+\((-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\)\s+([\d.]+)(?:\s+(\d+))?(?:\s+(\d+))?",
        RegexOptions.Compiled);

    private static OffMeshConnection[] LoadOffMeshConnections(string path)
    {
        var result = new List<OffMeshConnection>();
        foreach (string line in File.ReadLines(path))
        {
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == '/')
                continue;

            var m = OffMeshLineRegex.Match(trimmed);
            if (!m.Success)
                continue;

            int mid  = int.Parse(m.Groups[1].Value,  CultureInfo.InvariantCulture);
            int tx   = int.Parse(m.Groups[2].Value,  CultureInfo.InvariantCulture);
            int ty   = int.Parse(m.Groups[3].Value,  CultureInfo.InvariantCulture);
            float p0x = float.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            float p0y = float.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
            float p0z = float.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture);
            float p1x = float.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture);
            float p1y = float.Parse(m.Groups[8].Value, CultureInfo.InvariantCulture);
            float p1z = float.Parse(m.Groups[9].Value, CultureInfo.InvariantCulture);
            float size      = float.Parse(m.Groups[10].Value, CultureInfo.InvariantCulture);
            int areaType  = m.Groups[11].Success ? int.Parse(m.Groups[11].Value, CultureInfo.InvariantCulture) : 1;
            int direction = m.Groups[12].Success ? int.Parse(m.Groups[12].Value, CultureInfo.InvariantCulture) : 1;

            // Store in Recast/solidVerts space: C++ does append(p0[1], p0[2], p0[0])
            result.Add(new OffMeshConnection(
                mid, tx, ty,
                p0y, p0z, p0x,   // start: (p0[1], p0[2], p0[0])
                p1y, p1z, p1x,   // end:   (p1[1], p1[2], p1[0])
                size,
                (byte)direction,
                (byte)areaType,
                0x2F));           // flags: traversable by all non-transport queries
        }
        return result.ToArray();
    }
}

public readonly struct TileGeometry
{
    public float[] Vertices { get; }
    public int[] Indices { get; }
    public byte[] Areas { get; }
    public int VertexCount => Vertices.Length / 3;
    public int IndexCount => Indices.Length;

    public TileGeometry(float[] vertices, int[] indices, byte[] areas)
    {
        Vertices = vertices;
        Indices = indices;
        Areas = areas;
    }
}

// Parsed entry from the offmesh.txt connection file.
// Verts are pre-transformed to Recast/solidVerts space (p0[1], p0[2], p0[0]) per C++ convention.
internal readonly record struct OffMeshConnection(
    int MapId,
    int TileX,
    int TileY,
    float V0X, float V0Y, float V0Z,   // start point
    float V1X, float V1Y, float V1Z,   // end point
    float Radius,
    byte Direction,                     // 0=one-way, 1=bidirectional
    byte Area,
    ushort Flags);

internal readonly record struct GameObjectModelRef(
    string Path,
    bool IsWmo);

internal sealed record GameObjectModelData(
    string Path,
    bool IsM2,
    float[] Vertices,
    int[] Indices,
    Vector3 BoundsMin,
    Vector3 BoundsMax);
