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
using MaNGOS.Extractor.Formats.Map.Reading;
using MaNGOS.Extractor.Formats.Vmap.Mangos;
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
    // NOTE: AdtParser is intentionally NOT used here. The mmap reads terrain from
    // the extracted .map files only (MapFileReader), never the raw ADT. The WMO/M2
    // parsers below are still needed for GameObject model meshes.
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
    private readonly bool _usesLiquids;
    private readonly string? _vmapDir;  // directory containing vmaps/MapName/
    private readonly string? _mapsDir;  // directory containing maps/{id:D3}{y:D2}{x:D2}.map

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
        _wmoParser = new WmoParser(archive, loggerFactory.CreateLogger<WmoParser>());
        _m2Parser = new M2Parser(archive);
        _outputDir = outputDir;
        _recastConfig = recastConfig;
        _maxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism);
        _offMeshPath = offMeshPath;
        _usesLiquids = usesLiquids;
        _roadMapsDir = roadMapsDir;
        _vmapDir = vmapDir;
        _mapsDir = mapsDir;

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
        if (string.IsNullOrEmpty(_mapsDir))
            throw new ArgumentException("[Mmap] mapsDir is required — the mmap reads only the extracted .map files, never the raw ADT. Run the Map phase first.", nameof(mapsDir));
        _logger.LogInformation("[Mmap] Map file directory (extracted .map): {Path}", _mapsDir);
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

    private void WriteMmapHeader(uint mapId, uint tileCount, int maxAdtX, int maxAdtY)
    {
        // Mirrors MaNGOS C++ MapBuilder::buildNavMesh + getTileBounds (verts=NULL).
        // C++ computes bmin from the most-positive-index tile:
        //   bmax[0] = (32 - tileXMax) * GRID_SIZE
        //   bmin[0] = bmax[0] - GRID_SIZE   = (31 - tileXMax) * GRID_SIZE
        //   bmin[1] = FLT_MIN
        //   bmax[1] = FLT_MAX
        //   bmin[2] = bmax[2] - GRID_SIZE   = (31 - tileYMax) * GRID_SIZE
        // then rcVcopy(navMeshParams->orig, bmin) copies bmin to orig.
        float orig0 = (31 - maxAdtX) * WowConstants.TileSize;
        float orig1 = 1.175494E-38f;  // FLT_MIN, matches C++ getTileBounds bmin[1] = FLT_MIN
        float orig2 = (31 - maxAdtY) * WowConstants.TileSize;

        string mmapPath = Path.Combine(_outputDir, $"{mapId:D3}.mmap");
        // C# 4x4 sub-tile format: tileSize = SubTileSize, maxTiles = ADT count * 16
        // (one Detour tile per sub-tile). Matches the multi-tile .mmtile layout
        // (16 sub-tiles per ADT) consumed by the C# bot runtime.
        float tileSize = WowConstants.SubTileSize;
        int maxTiles = (int)(tileCount * WowConstants.SubTilesPerAdtSide * WowConstants.SubTilesPerAdtSide);

        _logger.LogInformation("[Mmap] Writing header: {Path} (adts={TileCount}, maxTiles={MaxTiles}, tileSize={TileSize}, orig=({Orig0},{Orig2}))",
            mmapPath, tileCount, maxTiles, tileSize, orig0, orig2);
        using var stream = new FileStream(mmapPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write(orig0);                      // orig[0] = (31 - maxAdtX) * TileSize
        writer.Write(orig1);                      // orig[1] = FLT_MIN
        writer.Write(orig2);                      // orig[2] = (31 - maxAdtY) * TileSize
        writer.Write(tileSize);                   // tileWidth  = GRID_SIZE (533.333)
        writer.Write(tileSize);                   // tileHeight = GRID_SIZE (533.333)
        writer.Write(maxTiles);                   // maxTiles   = number of ADT tiles
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
            // ADT bmin in world space — matches C++ getTileBounds(tileX) byte-for-byte
            // (MapBuilder.cpp:996-1000):
            //   bmax[0] = (32 - tileX) * GRID_SIZE
            //   bmin[0] = bmax[0] - GRID_SIZE
            // For tileX=32, bmaxX = 0, bminX = -GRID_SIZE = -533.33. For tileY=48,
            // bmaxZ = -16*GRID_SIZE, bminZ = -17*GRID_SIZE = -9066.66.
            //
            // The previous code used (32 - tileX) * TileSize which gave 0 for
            // tileX=32 — that was actually bmaxX, not bminX. The terrain is emitted
            // at X = [-TileSize, 0] for tileX=32, so the bbox was one whole TileSize
            // to the east of the actual geometry. Result: the Recast heightfield
            // only saw the border strip (4× too small), the visible sub-tile was
            // rotated 90° (border rasterized diagonally), and the position was
            // off by one TileSize.
            float bminX = (32 - tileX - 1) * WowConstants.TileSize;
            float bminZ = (32 - tileY - 1) * WowConstants.TileSize;

            var navData = BuildNavMeshSubTilesSync(mapId, geometry, tileX, tileY, maxAdtX, maxAdtY, bminX, bminZ, ct);
            if (navData.All(blob => blob == null || blob.Length == 0))
            {
                _logger.LogWarning("[Mmap] BuildTile returned empty for all sub-tiles in ADT ({TileX},{TileY})", tileX, tileY);
                return false;
            }
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
        // Mangos-faithful terrain loading: read the .map files produced by the
        // map-extractor (mangostwo-server/.../map-extractor/System.cpp). The
        // original C++ MapBuilder::buildTile calls m_terrainBuilder->loadMap
        // which opens the .map file directly — it NEVER re-parses the raw ADT.
        //
        // The whole point of the other extractors (map/vmap/road) is to feed the
        // mmap from their output. There is therefore NO raw-ADT fallback: if the
        // .map directory is missing or the center tile's .map is absent, this is
        // a configuration error and we throw instead of silently degrading.
        // (The previous raw fallback was a non-Mangos path that produced meshes
        // inconsistent with the rest of the pipeline.)
        if (string.IsNullOrEmpty(_mapsDir) || !Directory.Exists(_mapsDir))
        {
            throw new InvalidOperationException(
                "[Mmap] mapsDir is required (run the Map phase first to produce the .map files). " +
                "The mmap-extractor reads only the extracted .map files, never the raw ADT.");
        }

        var tiles = new List<(int X, int Y, MapFileReader.TileData Tile)>();
        int loadedCount = 0, skippedCount = 0;

        // The C++ TerrainBuilder::loadMap loads the center ADT plus its 8 neighbors
        // (3x3 window) so the navmesh can be linked across tile boundaries. We mirror
        // that exactly, reading the .map file for each present neighbor.
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

                string mapPath = Path.Combine(_mapsDir!, $"{mapId:D3}{adjY:D2}{adjX:D2}.map");
                if (!File.Exists(mapPath))
                {
                    // Neighbor tiles may legitimately not exist (map edges). Only the
                    // CENTER tile is mandatory — a missing center means the ADT itself
                    // doesn't exist, which is a real error handled by ExtractMapAsync
                    // via the WDT existence check before we ever get here.
                    skippedCount++;
                    continue;
                }
                var tileData = MapFileReader.Read(mapPath);
                if (tileData == null)
                {
                    _logger.LogWarning("[Mmap] Failed to parse .map ({TileX},{TileY}): {Path}", adjX, adjY, mapPath);
                    skippedCount++;
                    continue;
                }
                tiles.Add((adjX, adjY, tileData));
                loadedCount++;
            }
        }

        _logger.LogInformation("[Mmap] LoadTileGeometry adt=({CX},{CY}): {Loaded} .map tiles loaded, {Skipped} skipped",
            centerX, centerY, loadedCount, skippedCount);

        await Task.CompletedTask;  // keep async signature for callers; .map IO is synchronous

        if (tiles.Count == 0)
            return default;

        var geo = ExtrudeTileGeometry(mapId, centerX, centerY, tiles, LoadRoadMask(mapId, centerX, centerY));

        // Add WMO and M2 building/object geometry to the navmesh.
        // Mangos-faithful path: the vmap-extractor has already produced
        // .vmtile (placements) + .vmo/.vmd (compiled collision meshes). The
        // mmap-extractor reads THOSE files only — never the raw ADT/WMO/M2.
        // This matches the C++ TerrainBuilder::loadVMap exactly.
        if (!string.IsNullOrEmpty(_vmapDir))
        {
            var modelVerts = new List<float>();
            var modelTris  = new List<int>();
            var modelAreas = new List<byte>();

            var loader = new MangosVmapGeometryLoader(_vmapDir, mapName, mapId, _logger);
            loader.AppendNeighborhoodCollision(centerX, centerY, modelVerts, modelTris, modelAreas);

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
            // World-space bbox of the ADT plus one-tile margin on each side
            // (matches C++ TerrainBuilder::loadGameObjects which iterates the
            // 3x3 neighborhood of the current ADT). For tileX=t, worldX is
            // [(32 - t) * GRID_SIZE, (33 - t) * GRID_SIZE]; we expand by one
            // GRID_SIZE on each side to catch spawns just outside the cell.
            float worldXMin = (32f - centerX)     * WowConstants.TileSize;
            float worldXMax = (32f - centerX + 1f) * WowConstants.TileSize;
            float worldYMin = (32f - centerY)     * WowConstants.TileSize;
            float worldYMax = (32f - centerY + 1f) * WowConstants.TileSize;

            worldXMin -= WowConstants.TileSize;
            worldXMax += WowConstants.TileSize;
            worldYMin -= WowConstants.TileSize;
            worldYMax += WowConstants.TileSize;

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
                if (go.PosX < worldXMin || go.PosX > worldXMax
                    || go.PosY < worldYMin || go.PosY > worldYMax)
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
        if (mask.Length != 256)
        {
            _logger.LogWarning("[Mmap] Road mask is incomplete: {Path} ({Length}/256 bytes)", roadPath, mask.Length);
            return null;
        }

        int roadChunks = mask.Count(b => b != 0);
        if (roadChunks > 0)
            _logger.LogInformation("[Mmap] ADT ({TileX},{TileY}): loaded road mask {RoadChunks}/256 chunks",
                tileX, tileY, roadChunks);

        return roadChunks > 0 ? mask : null;
    }

    // Mangos-faithful terrain extrusion from the extracted .map file
    // (vmap-extractor output). The .map file stores a flat 129×129 V9 grid
    // and a 128×128 V8 grid for the entire ADT, plus 256 area flags (one
    // per MCNK chunk). We slice the appropriate 9×9 / 8×8 window for each
    // chunk and emit the same 4-triangle-per-cell fan that the C++
    // TerrainBuilder::loadMap produces.
    private static TileGeometry ExtrudeTileGeometry(uint mapId, int centerX, int centerY, List<(int X, int Y, MapFileReader.TileData Tile)> tiles, byte[]? centerRoadMask)
    {
        var vertices = new List<float>();
        var indices = new List<int>();
        var areas = new List<byte>();

        const float v9Step = WowConstants.ChunkSize / (WowConstants.MCNKVerticesSide - 1);

        foreach (var (adjX, adjY, tile) in tiles)
        {
            for (int chunkIdx = 0; chunkIdx < 256; chunkIdx++)
            {
                int chunkX = chunkIdx % 16;
                int chunkY = chunkIdx / 16;

                // Tile bmin in world space — same convention as the C++ map-extractor
                // (System.cpp). For tileX=32 (the map center) bmin=0; tileX=0
                // (west of center) bmin=32*GRID_SIZE; tileX=63 (east) bmin=-GRID_SIZE.
                // Chunks within the ADT are placed going EAST→WEST. The V9 array
                // (read from the .map file) is laid out with low cx at the EAST
                // side of the ADT (cx=0 → worldX=0 for tileX=32) and high cx at
                // the WEST side (cx=128 → worldX=G). The C# writer maps chunkX
                // directly to the V9 array index (vBaseX = chunkX * 8), so chunk
                // C++ TerrainBuilder::loadMap convention: tileOriginX/Z is the
                // NE corner of the ADT bbox. Each chunk occupies
                // [tileOrigin - (chunkX+1)*ChunkSize, tileOrigin - chunkX*ChunkSize]
                // along X. So chunkOriginX (the WEST edge of each chunk) is:
                //   chunkOriginX = tileOriginX - (chunkX + 1) * ChunkSize
                // chunk 0 (east side): chunkOriginX = tileOriginX - ChunkSize
                // chunk 15 (west side): chunkOriginX = tileOriginX - TileSize
                float tileOriginX = (32f - adjX) * WowConstants.TileSize;
                float tileOriginZ = (32f - adjY) * WowConstants.TileSize;

                float chunkOriginX = tileOriginX - (chunkX + 1) * WowConstants.ChunkSize;
                float chunkOriginZ = tileOriginZ - (chunkY + 1) * WowConstants.ChunkSize;

                int vertexOffset = vertices.Count / 3;
                int v9BaseZ = chunkY * 8;
                int v9BaseX = chunkX * 8;

                for (int z = 0; z < 9; z++)
                {
                    for (int x = 0; x < 9; x++)
                    {
                        float h = tile.V9Heights[(v9BaseZ + z) * MapFileReader.V9Side + (v9BaseX + x)];
                        vertices.Add(chunkOriginX + x * v9Step);
                        vertices.Add(h);
                        vertices.Add(chunkOriginZ + z * v9Step);
                    }
                }

                int v8BaseOffset = vertices.Count / 3;
                for (int z = 0; z < 8; z++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        float h = tile.V8Heights[(v9BaseZ + z) * MapFileReader.V8Side + (v9BaseX + x)];
                        vertices.Add(chunkOriginX + (x + 0.5f) * v9Step);
                        vertices.Add(h);
                        vertices.Add(chunkOriginZ + (z + 0.5f) * v9Step);
                    }
                }

                // Area flag: any non-empty (non 0xFFFF) area → NAV_GROUND (1).
                // The .map file stores the AreaTable.dbc area id; the C++ side
                // also uses 1 as the Recast area type for any valid walkable area.
                byte areaType = tile.AreaFlags[chunkIdx] != 0 && tile.AreaFlags[chunkIdx] != 0xFFFF
                    ? (byte)1
                    : (byte)0;
                if (adjX == centerX && adjY == centerY && centerRoadMask != null && centerRoadMask[chunkIdx] != 0)
                    areaType = 4; // NAV_ROAD

                for (int z = 0; z < 8; z++)
                {
                    for (int x = 0; x < 8; x++)
                    {
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
            }
        }

        return new TileGeometry(vertices.ToArray(), indices.ToArray(), areas.ToArray());
    }

    private byte[]?[] BuildNavMeshSubTilesSync(
        uint mapId, TileGeometry geo, int adtX, int adtY, int maxAdtX, int maxAdtY,
        float adtMinX, float adtMinZ, CancellationToken ct = default)
    {
        // Filter offmesh connections for this ADT tile once — same as C++ loadOffMeshConnections(mapID, tileX, tileY)
        var tileConns = _offMeshConnections
            .Where(c => c.MapId == (int)mapId && c.TileX == adtX && c.TileY == adtY)
            .ToArray();

        // ----------------------------------------------------------------------
        // Compute a single Y range for the WHOLE ADT (not per sub-tile).
        //
        // This mirrors the original Mangos MapBuilder.cpp behavior: the full ADT
        // Y range is passed unchanged to every mini-tile's heightfield:
        //
        //     rcVcopy(config.bmin, bmin);
        //     rcVcopy(config.bmax, bmax);
        //     // per-tile config only touches X/Z; bmin[1]/bmax[1] stay global
        //
        // Why this matters for the 4x4 sub-tile architecture: at cave entrances
        // or other vertical drops, the mountain-top and the cave floor can be
        // ~hundreds of world units apart. If each sub-tile recomputes its Y
        // range from vertices in its own (sub-tile + border) XZ bbox, a sub-tile
        // sitting on the mountain top gets a Y range that does NOT include the
        // cave floor. The cave-floor span rasterized in the border is then
        // dropped by rcCreateHeightfield (its Y is outside [bmin[1], bmax[1]]).
        // Detour then links the mountain-top span in the neighbor sub-tile to
        // nothing at the cave-floor level — the portal goes "up" instead of
        // "down", which is the bug the user is seeing.
        //
        // rcFilterLedgeSpans is NOT a concern with the full-ADT Y range: it only
        // marks the HIGHER span of a ledge pair as unwalkable, never the lower
        // one. And the spans it inspects are still the spans in the sub-tile's
        // XZ cells — a larger Y range just gives the sparse heightfield more
        // headroom to store them.
        // ----------------------------------------------------------------------
        float adtMaxX = adtMinX + WowConstants.TileSize;
        float adtMaxZ = adtMinZ + WowConstants.TileSize;
        float adtMinY = float.MaxValue, adtMaxY = float.MinValue;
        for (int v = 0; v < geo.Vertices.Length; v += 3)
        {
            float vx = geo.Vertices[v];
            float vy = geo.Vertices[v + 1];
            float vz = geo.Vertices[v + 2];
            if (vx < adtMinX || vx > adtMaxX || vz < adtMinZ || vz > adtMaxZ)
                continue;
            if (vy < adtMinY) adtMinY = vy;
            if (vy > adtMaxY) adtMaxY = vy;
        }
        if (adtMinY == float.MaxValue) { adtMinY = 0; adtMaxY = 100f; }

        _logger.LogInformation("[Mmap] ADT ({AdtX},{AdtY}) global Y range: [{MinY:F2}, {MaxY:F2}] (from {Verts} verts in ADT bbox)",
            adtX, adtY, adtMinY, adtMaxY, geo.Vertices.Length / 3);

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

        var navData = new byte[]?[WowConstants.SubTilesPerAdtSide * WowConstants.SubTilesPerAdtSide];


        // 4x4 sub-tiling implementation
        // Replaced single-ADT tile logic with 16 Detour sub-tiles (4x4) per ADT.
        // Detour tile coordinates are relative to the global origin.
        float subTileSize = WowConstants.TileSize / 4f;
        int baseDetourX = (maxAdtX - adtX) * 4;
        int baseDetourY = (maxAdtY - adtY) * 4;

        _logger.LogInformation("[Mmap] adt=({AdtX},{AdtY}) ADT bbox=[{MinX:F2},{MinY:F2},{MinZ:F2}]→[{MaxX:F2},{MaxY:F2},{MaxZ:F2}] baseDetour=({BDX},{BDY})",
            adtX, adtY, adtMinX, adtMinY, adtMinZ, adtMaxX, adtMaxY, adtMaxZ, baseDetourX, baseDetourY);

        for (int sy = 0; sy < 4; sy++)
        {
            for (int sx = 0; sx < 4; sx++)
            {
                int subTileIdx = sy * 4 + sx;

                float subMinX = adtMinX + sx * subTileSize;
                float subMinZ = adtMinZ + sy * subTileSize;
                float subMaxX = subMinX + subTileSize;
                float subMaxZ = subMinZ + subTileSize;

                int detourTileX = baseDetourX + sx;
                int detourTileY = baseDetourY + sy;

                navData[subTileIdx] = BuildNavMeshTileSync(
                    geo, detourTileX, detourTileY,
                    subMinX, subMinZ, subMaxX, subMaxZ,
                    adtMinY, adtMaxY,
                    omVerts, omRads, omDirs, omAreas, omFlags);
            }
        }

        return navData;
    }

    private unsafe byte[]? BuildNavMeshTileSync(
        TileGeometry geo, int detourTileX, int detourTileY,
        float minX, float minZ, float maxX, float maxZ,
        float adtMinY, float adtMaxY,
        float[] omVerts, float[] omRads, byte[] omDirs, byte[] omAreas, ushort[] omFlags)
    {
        int vertCount = geo.VertexCount;
        int triCount = geo.IndexCount / 3;

        var p = new RecastBuildParams
        {
            CellSize = _recastConfig.CellSize,
            CellHeight = _recastConfig.CellHeight,
            WalkableSlopeAngle = _recastConfig.WalkableSlopeAngle,
            WalkableHeight = _recastConfig.WalkableHeight,
            WalkableRadius = _recastConfig.WalkableRadius,
            WalkableClimb = _recastConfig.WalkableClimb,
            MinRegionArea = 400,
            MergeRegionArea = 1600,
            MaxSimplificationError = 1.3f,
            TileX = detourTileX,
            TileY = detourTileY,
            MaxVertsPerPoly = 6,
            BorderSize = _recastConfig.WalkableRadius + 3
        };

        // Expand the bounding box by BorderSize * CellSize on each XZ side so that
        // neighboring geometry is rasterized — this ensures polygon edges reach the
        // tile boundary and Detour can form portal links between adjacent sub-tiles.
        float borderMeters = p.BorderSize * _recastConfig.CellSize;
        float expandedMinX = minX - borderMeters;
        float expandedMaxX = maxX + borderMeters;
        float expandedMinZ = minZ - borderMeters;
        float expandedMaxZ = maxZ + borderMeters;

        // Use the ADT-wide Y range passed by BuildNavMeshSubTilesSync — this is
        // the FIX for cave entrances / vertical drops at sub-tile boundaries.
        //
        // Original (buggy): each sub-tile recomputed its Y range from vertices in
        // its own (sub-tile + border) XZ bbox. A sub-tile sitting on a mountain
        // top above a cave entrance would get a Y range that does NOT include the
        // cave floor (which lives in a different sub-tile). The cave-floor span
        // rasterized inside the border was then silently dropped by
        // rcCreateHeightfield (its Y is outside the local Y range), and Detour
        // linked the mountain-top span of the neighbor sub-tile to nothing at
        // the cave-floor level — producing the "connects on top instead of
        // bottom" artefact.
        //
        // Fixed: use the full ADT Y range for every sub-tile, matching the
        // original Mangos MapBuilder.cpp behavior. This is safe because
        // rcFilterLedgeSpans only ever marks the HIGHER span of a ledge pair as
        // unwalkable, and a wider Y range only gives the sparse heightfield
        // more headroom — it does not change which spans exist in each cell.
        float minY = adtMinY;
        float maxYh = adtMaxY;

        fixed (float* v = geo.Vertices)
        fixed (int* i = geo.Indices)
        fixed (byte* a = geo.Areas)
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
                detourTileX, detourTileY,
                p.BoundingBoxMinX, p.BoundingBoxMinY, p.BoundingBoxMinZ,
                p.BoundingBoxMaxX, p.BoundingBoxMaxY, p.BoundingBoxMaxZ,
                p.CellSize, p.CellHeight, vertCount, triCount, omVerts.Length / 6);

            byte* outData;
            int outSize;
            int omCount = omVerts.Length / 6;

            _logger.LogInformation("[Mmap] BuildTile DLL call: adt=({AX},{AY}) {Verts}v {Tris}t", detourTileX, detourTileY, vertCount, triCount);
            if (!RecastNative.BuildTile(&p, v, vertCount, i, triCount, a,
                omCount > 0 ? pOmVerts : null,
                omCount > 0 ? pOmRads  : null,
                omCount > 0 ? pOmDirs  : null,
                omCount > 0 ? pOmAreas : null,
                omCount > 0 ? pOmFlags : null,
                omCount, &outData, &outSize))
            {
                _logger.LogWarning("[Mmap] BuildTile FAILED for adt=({AX},{AY})", detourTileX, detourTileY);
                return null;
            }

            _logger.LogInformation("[Mmap] BuildTile DLL returned: adt=({AX},{AY}) size={Size}", detourTileX, detourTileY, outSize);
            if (outData == null || outSize <= 0)
            {
                _logger.LogWarning("[Mmap] BuildTile returned empty for adt=({AX},{AY})", detourTileX, detourTileY);
                return null;
            }

            var result = new byte[outSize];
            fixed (byte* dest = result)
            {
                Buffer.MemoryCopy(outData, dest, outSize, outSize);
            }

            RecastNative.FreeBuffer(outData);
            return result;
        }
    }

    private async Task WriteMmtileAsync(uint mapId, int fileTileX, int fileTileY, byte[]?[] navData, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(_outputDir);

            // C# multi-tile format: ONE mmtile per ADT containing all 16
            // sub-tiles (4x4) combined. The 4x4 sub-tiling is done internally
            // for finer navmesh granularity, but the on-disk format is one
            // blob per ADT (not 16 separate files).
            //
            // File layout (20-byte header + per-sub-tile blocks):
            //   uint32 mmapMagic            = 0x50414D4D "MMAP" LE
            //   uint32 dtVersion            = 7
            //   uint32 mmapVersion          = 5 (multi-tile version)
            //   uint32 subTileCount         = 16
            //   uint32 1u                   (reserved/unknown)
            //   For each sub-tile:
            //     uint32 size               (navmesh data size, 0 if empty)
            //     byte[size] data           (Detour navmesh blob)
            //
            // Filename: {mapId:D3}{adtY:D2}{adtX:D2}.mmtile (ADT coordinates)
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

    /// <summary>
    /// Clears cached GameObject model data. Called between extractions to free memory.
    /// The AdtParser is intentionally NOT used by the mmap — terrain comes from .map files.
    /// </summary>
    internal void ClearCache() => _gameObjectModelCache.Clear();

    // NOTE: WMO/M2 geometry is now loaded from the compiled .vmo/.vmd files
    // by MangosVmapGeometryLoader (see LoadTileGeometryAsync). The previous
    // raw-parse path is intentionally removed to follow the official Mangos
    // pipeline: vmap-extractor writes .vmtile/.vmo/.vmd, mmap-extractor reads
    // them. The raw WmoParser/M2Parser instances are still used for
    // GameObject spawns (see LoadGameObjectM2 / LoadGameObjectWmo below)
    // because GameObjects reference their source .m2/.wmo directly via the
    // gameobject_spawns.bin + GameObjectDisplayInfo.dbc lookup.

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
        => LoadGameObjectVmoOrVmd(modelPath, isM2: true);

    private GameObjectModelData? LoadGameObjectWmo(string modelPath)
        => LoadGameObjectVmoOrVmd(modelPath, isM2: false);

    // -----------------------------------------------------------------------
    // Load a GameObject's compiled collision mesh from the vmap-extractor's
    // output (vmaps/<path>.vmo or vmaps/<path>.vmd). This replaces the
    // previous raw-MPQ parse path — the same way the C++ mmap-extractor
    // reads gameobject collision meshes through VMapManager2 (which itself
    // reads the .vmo/.vmd files produced by the vmap-extractor).
    //
    // The GameObjectSpawns index (gameobject_spawns.bin, user-provided) maps
    // a GameObject GUID to a displayId, position, rotation and scale. The
    // mmap service then resolves displayId → model path (via the DBC, which
    // is the static model catalog and not gameplay data) and loads the
    // matching compiled file.
    //
    // If the compiled file is missing (the vmap-extractor never built it —
    // e.g. a model referenced from gameobject_spawns.bin but not from any
    // ADT MDDF), the loader returns null and the spawn is skipped.
    // -----------------------------------------------------------------------
    private GameObjectModelData? LoadGameObjectVmoOrVmd(string modelPath, bool isM2)
    {
        if (string.IsNullOrEmpty(_vmapDir))
        {
            _logger.LogDebug("[Mmap-GO] Cannot load {Model}: vmapDir not configured", modelPath);
            return null;
        }

        string ext = isM2 ? MangosVmoReader.VmdExtension : MangosVmoReader.VmoExtension;
        string compiledPath = Path.Combine(_vmapDir, "vmaps", modelPath.Replace('\\', '/') + ext);
        if (!File.Exists(compiledPath))
        {
            _logger.LogDebug("[Mmap-GO] Compiled collision file missing: {Path}", compiledPath);
            return null;
        }

        var worldModel = MangosVmoReader.Read(compiledPath);
        if (worldModel == null || !worldModel.Valid)
        {
            _logger.LogDebug("[Mmap-GO] Could not parse compiled file: {Path}", compiledPath);
            return null;
        }
        if (worldModel.Groups.Count == 0)
            return null; // valid file but no geometry

        var vertices = new List<float>();
        var indices = new List<int>();
        Vector3 boundsMin = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 boundsMax = new(float.MinValue, float.MinValue, float.MinValue);

        foreach (var grp in worldModel.Groups)
        {
            if (grp.Vertices.Length == 0 || grp.Triangles.Length == 0) continue;
            int vertBase = vertices.Count / 3;
            for (int v = 0; v < grp.Vertices.Length; v++)
            {
                var vec = grp.Vertices[v];
                vertices.Add(vec.X);
                vertices.Add(vec.Y);
                vertices.Add(vec.Z);
                boundsMin = Vector3.Min(boundsMin, vec);
                boundsMax = Vector3.Max(boundsMax, vec);
            }
            for (int t = 0; t < grp.Triangles.Length; t++)
            {
                var tri = grp.Triangles[t];
                indices.Add(vertBase + (int)tri.I0);
                indices.Add(vertBase + (int)tri.I1);
                indices.Add(vertBase + (int)tri.I2);
            }
        }

        if (vertices.Count == 0)
        {
            // Compiled file parsed but produced no geometry — return empty
            // sentinel so the caller can count it as skippedNoBounds.
            return new GameObjectModelData(modelPath, IsM2: isM2,
                Array.Empty<float>(), Array.Empty<int>(), Vector3.Zero, Vector3.Zero);
        }

        return new GameObjectModelData(
            modelPath,
            IsM2: isM2,
            vertices.ToArray(),
            indices.ToArray(),
            boundsMin,
            boundsMax);
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

        // M2 models need winding reversed — mirrors TerrainBuilder::copyIndices(tris, offset, isM2=true).
        // WMO GOs keep original winding (isM2=false → no flip).
        bool flipWinding = modelData.IsM2;
        for (int i = 0; i < modelData.Indices.Length; i += 3)
        {
            if (flipWinding)
            {
                outIndices.Add(baseIndex + modelData.Indices[i + 2]);
                outIndices.Add(baseIndex + modelData.Indices[i + 1]);
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
