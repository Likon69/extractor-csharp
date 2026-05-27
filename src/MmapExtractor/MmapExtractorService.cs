using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Threading;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;
using MaNGOS.Extractor.Core.Models;
using MaNGOS.Extractor.Formats.Adt.Models;
using MaNGOS.Extractor.Formats.Adt.Parsing;
using MaNGOS.Extractor.Formats.M2;
using MaNGOS.Extractor.Formats.Wdt;
using MaNGOS.Extractor.Formats.Wmo.Models;
using MaNGOS.Extractor.Formats.Wmo.Parsing;
using MaNGOS.Extractor.MmapExtractor.Recast;
using MaNGOS.Extractor.UI;

namespace MaNGOS.Extractor.MmapExtractor;

public sealed class MmapExtractorService
{
    private static readonly object NativeBuildLock = new();
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
    private readonly string? _offMeshPath;
    private readonly string? _roadMapsDir;

    public MmapExtractorService(
        IArchiveReader archive,
        ILoggerFactory loggerFactory,
        string outputDir,
        RecastConfig recastConfig,
        int maxDegreeOfParallelism = 1,
        string? goSpawnsPath = null,
        string? offMeshPath = null,
        string? roadMapsDir = null)
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
        if (!string.IsNullOrEmpty(_roadMapsDir))
            _logger.LogInformation("[Mmap] Road mask directory: {Path}", _roadMapsDir);
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);
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
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting mmap extraction for {MapName} (id={MapId}) with {Threads} threads",
            mapName, mapId, _maxDegreeOfParallelism);

        if (!await _wdtReader.LoadAsync(mapName, ct))
        {
            _logger.LogError("Failed to load WDT for map: {MapName}", mapName);
            return 0;
        }

        var tiles = _wdtReader.GetExistingTiles();
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

            var navData = BuildNavMeshSubTilesSync(geometry, tileX, tileY, maxAdtX, maxAdtY, bminX, bminZ);
            if (navData.All(blob => blob == null || blob.Length == 0))
            {
                _logger.LogWarning("[Mmap] BuildTile returned empty for all sub-tiles in ADT ({TileX},{TileY})", tileX, tileY);
                return false;
            }
            await WriteMmtileAsync(mapId, tileY, tileX, navData, ct);
            return true;
        }
        catch (Exception ex)
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

                string adtPath = $"World\\Maps\\{mapName}\\{mapName}_{adjX:D2}_{adjY:D2}.adt";

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

        if (tiles.Count == 0)
            return default;

        var geo = ExtrudeTileGeometry(mapId, centerX, centerY, tiles, LoadRoadMask(mapId, centerX, centerY));

        // Add WMO and M2 building/object geometry to the navmesh.
        // Process all tiles in the 3x3 neighborhood (same as original C++) so that
        // large WMOs spanning tile borders are fully included. Recast clips to tile bounds.
        {
            var modelVerts = new List<float>();
            var modelTris  = new List<int>();
            var modelAreas = new List<byte>();
            var seenWmoIds = new HashSet<uint>();
            var seenM2Ids  = new HashSet<uint>();

            foreach (var (_, _, adts) in tiles)
            {
                foreach (var adt in adts)
                {
                    await AppendWmoGeometryAsync(adt, modelVerts, modelTris, modelAreas, seenWmoIds, ct);
                    AppendM2Geometry(adt, modelVerts, modelTris, modelAreas, seenM2Ids);
                }
            }

            if (modelVerts.Count > 0)
            {
                _logger.LogInformation(
                    "[Mmap] ADT ({CX},{CY}): WMO/M2 geometry: {TotalTris} tris, {TotalVerts} verts ({WMOs} unique WMOs, {M2s} unique M2 placements)",
                    centerX, centerY,
                    modelTris.Count / 3,
                    modelVerts.Count / 3,
                    seenWmoIds.Count,
                    seenM2Ids.Count);

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

        // Add GameObject spawns for this map/tile
        if (_goSpawns.Length > 0)
        {
            float tileMinX = centerX * WowConstants.TileSize - WowConstants.MapHalfSize;
            float tileMaxX = tileMinX + WowConstants.TileSize;
            float tileMinZ = centerY * WowConstants.TileSize - WowConstants.MapHalfSize;
            float tileMaxZ = tileMinZ + WowConstants.TileSize;

            var goVerts = new List<float>();
            var goIndices = new List<int>();
            var goAreas = new List<byte>();
            int goMatchCount = 0;

            foreach (var go in _goSpawns)
            {
                if (go.MapId != mapId)
                    continue;
                if (go.PosX < tileMinX || go.PosX > tileMaxX
                    || go.PosZ < tileMinZ || go.PosZ > tileMaxZ)
                    continue;

                goMatchCount++;

                float hs = 0.5f * go.Scale;
                float ht = 2.0f * go.Scale;
                float x0 = -(go.PosX + hs), x1 = -(go.PosX - hs);
                float y0 = go.PosY, y1 = go.PosY + ht;
                float z0 = -(go.PosZ + hs), z1 = -(go.PosZ - hs);

                int baseIdx = goVerts.Count / 3;

                goVerts.Add(x0); goVerts.Add(y0); goVerts.Add(z0);
                goVerts.Add(x1); goVerts.Add(y0); goVerts.Add(z0);
                goVerts.Add(x1); goVerts.Add(y0); goVerts.Add(z1);
                goVerts.Add(x0); goVerts.Add(y0); goVerts.Add(z1);
                goVerts.Add(x0); goVerts.Add(y1); goVerts.Add(z0);
                goVerts.Add(x1); goVerts.Add(y1); goVerts.Add(z0);
                goVerts.Add(x1); goVerts.Add(y1); goVerts.Add(z1);
                goVerts.Add(x0); goVerts.Add(y1); goVerts.Add(z1);

                goIndices.Add(baseIdx + 4); goIndices.Add(baseIdx + 5); goIndices.Add(baseIdx + 6);
                goIndices.Add(baseIdx + 4); goIndices.Add(baseIdx + 6); goIndices.Add(baseIdx + 7);
                goIndices.Add(baseIdx + 1); goIndices.Add(baseIdx + 0); goIndices.Add(baseIdx + 3);
                goIndices.Add(baseIdx + 1); goIndices.Add(baseIdx + 3); goIndices.Add(baseIdx + 2);
                goIndices.Add(baseIdx + 1); goIndices.Add(baseIdx + 5); goIndices.Add(baseIdx + 6);
                goIndices.Add(baseIdx + 1); goIndices.Add(baseIdx + 6); goIndices.Add(baseIdx + 2);
                goIndices.Add(baseIdx + 0); goIndices.Add(baseIdx + 4); goIndices.Add(baseIdx + 7);
                goIndices.Add(baseIdx + 0); goIndices.Add(baseIdx + 7); goIndices.Add(baseIdx + 3);
                goIndices.Add(baseIdx + 3); goIndices.Add(baseIdx + 7); goIndices.Add(baseIdx + 6);
                goIndices.Add(baseIdx + 3); goIndices.Add(baseIdx + 6); goIndices.Add(baseIdx + 2);
                goIndices.Add(baseIdx + 0); goIndices.Add(baseIdx + 1); goIndices.Add(baseIdx + 5);
                goIndices.Add(baseIdx + 0); goIndices.Add(baseIdx + 5); goIndices.Add(baseIdx + 4);

                for (int i = 0; i < 12; i++) goAreas.Add(0);
            }

            if (goMatchCount > 0)
            {
                _logger.LogInformation("[Mmap] ADT ({CX},{CY}): {GOCount} GameObjects in tile " +
                    "(tileBounds X=[{MinX:F1},{MaxX:F1}] Z=[{MinZ:F1},{MaxZ:F1}])",
                    centerX, centerY, goMatchCount, tileMinX, tileMaxX, tileMinZ, tileMaxZ);
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

                byte areaType = adt.GetChunkAreaType(chunkIdx);
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
                vertexOffset = vertices.Count / 3;
            }
        }

        return new TileGeometry(vertices.ToArray(), indices.ToArray(), areas.ToArray());
    }

    private byte[]?[] BuildNavMeshSubTilesSync(
        TileGeometry geo, int adtX, int adtY, int maxAdtX, int maxAdtY,
        float adtMinX, float adtMinZ)
    {
        var navData = new byte[]?[WowConstants.SubTilesPerAdtSide * WowConstants.SubTilesPerAdtSide];

        for (int subY = 0; subY < WowConstants.SubTilesPerAdtSide; subY++)
        {
            for (int subX = 0; subX < WowConstants.SubTilesPerAdtSide; subX++)
            {
                int slot = subY * WowConstants.SubTilesPerAdtSide + subX;
                float minX = adtMinX + subX * WowConstants.SubTileSize;
                float maxX = minX + WowConstants.SubTileSize;
                float minZ = adtMinZ + subY * WowConstants.SubTileSize;
                float maxZ = minZ + WowConstants.SubTileSize;

                int detourTileX = (maxAdtX - adtX) * WowConstants.SubTilesPerAdtSide + subX;
                int detourTileY = (maxAdtY - adtY) * WowConstants.SubTilesPerAdtSide + subY;

                navData[slot] = BuildNavMeshTileSync(
                    geo, detourTileX, detourTileY,
                    minX, minZ, maxX, maxZ);
            }
        }

        return navData;
    }

    private unsafe byte[]? BuildNavMeshTileSync(
        TileGeometry geo, int adtX, int adtY,
        float minX, float minZ, float maxX, float maxZ)
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
            TileX = adtX,
            TileY = adtY,
            MaxVertsPerPoly = 6
        };

        float minY = float.MaxValue, maxYh = float.MinValue;
        for (int v = 1; v < geo.Vertices.Length; v += 3)
        {
            if (geo.Vertices[v] < minY) minY = geo.Vertices[v];
            if (geo.Vertices[v] > maxYh) maxYh = geo.Vertices[v];
        }
        if (minY == float.MaxValue) { minY = 0; maxYh = 100f; }

        fixed (float* v = geo.Vertices)
        fixed (int* i = geo.Indices)
        fixed (byte* a = geo.Areas)
        {
            p.BoundingBoxMinX = minX;
            p.BoundingBoxMinY = minY - 2f;
            p.BoundingBoxMinZ = minZ;
            p.BoundingBoxMaxX = maxX;
            p.BoundingBoxMaxY = maxYh + 2f;
            p.BoundingBoxMaxZ = maxZ;

            _logger.LogDebug("[Mmap] RecastBuildParams: adt=({AX},{AY}) " +
                "bbox=[{MinX:F2},{MinY:F2},{MinZ:F2}]→[{MaxX:F2},{MaxY:F2},{MaxZ:F2}] " +
                "cellSize={CS:F6} cellHeight={CH:F3} input: {Verts} verts {Tris} tris",
                adtX, adtY,
                p.BoundingBoxMinX, p.BoundingBoxMinY, p.BoundingBoxMinZ,
                p.BoundingBoxMaxX, p.BoundingBoxMaxY, p.BoundingBoxMaxZ,
                p.CellSize, p.CellHeight, vertCount, triCount);

            byte* outData;
            int outSize;

            lock (NativeBuildLock)
            {
                if (!RecastNative.BuildTile(&p, v, vertCount, i, triCount, a, &outData, &outSize))
                {
                    _logger.LogWarning("[Mmap] BuildTile FAILED for adt=({AX},{AY})", adtX, adtY);
                    return null;
                }

                if (outData == null || outSize <= 0)
                {
                    _logger.LogWarning("[Mmap] BuildTile returned empty for adt=({AX},{AY})", adtX, adtY);
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

            // Build rotation matrix using G3D fromEulerAnglesXYZ(ax, ay, az) convention:
            //   v_world = v_model * Rx(ax) * Ry(ay) * Rz(az)
            // which in System.Numerics (also row-vector) is:
            //   Matrix4x4.CreateRotationX(ax) * CreateRotationY(ay) * CreateRotationZ(az)
            //
            // TerrainBuilder:
            //   ax = iRot.z  * pi/-180   iRot = raw MODF rotation (no fixCoords)
            //   ay = iRot.x  * pi/-180   In our AdtModf: RotationX/Y/Z are stored in binary order X,Y,Z
            //   az = iRot.y  * pi/-180   so iRot.x=RotationX, iRot.y=RotationY, iRot.z=RotationZ
            float ax = modf.RotationZ * MathF.PI / -180f;
            float ay = modf.RotationX * MathF.PI / -180f;
            float az = modf.RotationY * MathF.PI / -180f;
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

                string groupPath = wmoName.Replace(".wmo", $"{groupIdx:D3}.wmo");
                var grp = await _wmoParser.ParseGroupAsync(groupPath, (int)groupIdx, wmoName, ct);
                if (grp == null || grp.Vertices.Length == 0 || grp.Triangles.Length == 0)
                    continue;

                // Filter triangles using vmap-extractor logic:
                //   keep if (NoCollision NOT set) AND (Hint OR CollideHit IS set)
                var validTris = new List<int>(grp.Triangles.Length);
                for (int t = 0; t < grp.Triangles.Length; t++)
                {
                    WmoMaterialFlag flags = t < grp.Materials.Length
                        ? grp.Materials[t].Flags
                        : WmoMaterialFlag.CollideHit; // assume collidable if no MOPY entry

                    bool noCollision = (flags & WmoMaterialFlag.NoCollision) != 0;
                    bool hasCollideFlag = (flags & (WmoMaterialFlag.Hint | WmoMaterialFlag.CollideHit)) != 0;
                    if (noCollision || !hasCollideFlag)
                        continue;

                    validTris.Add(t);
                }

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
            // TerrainBuilder: ax = iRot.z*pi/-180 = RotationZ*pi/-180
            //                 ay = iRot.x*pi/-180 = RotationY*pi/-180  (C# field RotationY = first binary float)
            //                 az = iRot.y*pi/-180 = RotationX*pi/-180  (C# field RotationX = second binary float)
            float ax = mddf.RotationZ * MathF.PI / -180f;
            float ay = mddf.RotationY * MathF.PI / -180f; // C# field RotationY = yaw = vmap rot.x
            float az = mddf.RotationX * MathF.PI / -180f; // C# field RotationX = pitch = vmap rot.y
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
