using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;
using MaNGOS.Extractor.Core.Models;
using MaNGOS.Extractor.Formats.Adt.Models;
using MaNGOS.Extractor.Formats.Adt.Parsing;
using MaNGOS.Extractor.Formats.Wdt;
using MaNGOS.Extractor.MmapExtractor.Recast;

namespace MaNGOS.Extractor.MmapExtractor;

public sealed class MmapExtractorService
{
    private readonly IArchiveReader _archive;
    private readonly WdtReader _wdtReader;
    private readonly AdtParser _adtParser;
    private readonly ILogger _logger;
    private readonly string _outputDir;
    private readonly RecastConfig _recastConfig;
    private readonly int _maxDegreeOfParallelism;
    private readonly GoSpawn[] _goSpawns;
    private readonly string? _offMeshPath;   // reserved for future off-mesh connections

    public MmapExtractorService(
        IArchiveReader archive,
        ILoggerFactory loggerFactory,
        string outputDir,
        RecastConfig recastConfig,
        int maxDegreeOfParallelism = 1,
        string? goSpawnsPath = null,
        string? offMeshPath = null)
    {
        _archive = archive;
        _logger = loggerFactory.CreateLogger<MmapExtractorService>();
        _wdtReader = new WdtReader(archive);
        _adtParser = new AdtParser(archive, loggerFactory.CreateLogger<AdtParser>());
        _outputDir = outputDir;
        _recastConfig = recastConfig;
        _maxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism);
        _goSpawns = !string.IsNullOrEmpty(goSpawnsPath)
            ? GoSpawnsReader.Read(goSpawnsPath)
            : Array.Empty<GoSpawn>();
        _offMeshPath = offMeshPath;
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);
    }

    public async Task<int> ExtractMapAsync(
        uint mapId,
        string mapName,
        IProgress<TileProgressEvent>? progress,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting mmap extraction for {MapName} with {Threads} threads", mapName, _maxDegreeOfParallelism);

        if (!await _wdtReader.LoadAsync(mapName, ct))
        {
            _logger.LogError("Failed to load WDT for map: {MapName}", mapName);
            return 0;
        }

        var tiles = _wdtReader.GetExistingTiles();
        _logger.LogInformation("Found {Count} tiles for map {MapName}", tiles.Count, mapName);
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

            bool success = await ProcessTileAsync(mapId, mapName, tileX, tileY, token);

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY,
                success ? TileStatus.Done : TileStatus.Failed,
                ExtractionPhase.Mmap));

            if (success)
                Interlocked.Increment(ref successCount);
        });

        _logger.LogInformation("Mmap extraction complete: {Success}/{Total} tiles", successCount, tiles.Count);
        WriteMmapHeader(mapId, (uint)tiles.Count);
        return successCount;
    }

    private void WriteMmapHeader(uint mapId, uint tileCount)
    {
        string mmapPath = Path.Combine(_outputDir, $"{mapId:D3}.mmap");
        using var stream = new FileStream(mmapPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write(-WowConstants.MapHalfSize);
        writer.Write(0f);
        writer.Write(-WowConstants.MapHalfSize);
        writer.Write(WowConstants.SubTileSize);
        writer.Write(WowConstants.SubTileSize);
        writer.Write((int)(tileCount * 16));
        writer.Write(16384);
    }

    private async Task<bool> ProcessTileAsync(uint mapId, string mapName, int tileX, int tileY, CancellationToken ct)
    {
        var geometry = await LoadTileGeometryAsync(mapId, mapName, tileX, tileY, ct);
        if (geometry.Vertices == null || geometry.Vertices.Length == 0 ||
            geometry.Indices == null || geometry.Areas == null)
            return false;
        try
        {
            await BuildSubTilesAsync(mapId, tileX, tileY, geometry, ct);
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

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int adjX = centerX + dx;
                int adjY = centerY + dy;
                if (adjX < 0 || adjX >= 64 || adjY < 0 || adjY >= 64)
                    continue;

                string adtPath = $"World\\Maps\\{mapName}\\{mapName}_{adjX:D2}_{adjY:D2}.adt";

                var result = await _adtParser.ParseAsync(adtPath, mapId, adjX, adjY, ct);
                if (!result.Success)
                    continue;
                var adt = result.Tile;
                if (adt == null) continue;
                tiles.Add((adjX, adjY, new[] { adt }));
            }
        }

        if (tiles.Count == 0)
            return default;

        var geo = ExtrudeTileGeometry(mapId, centerX, centerY, tiles);

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

            foreach (var go in _goSpawns)
            {
                if (go.MapId != mapId)
                    continue;
                if (go.PosX < tileMinX || go.PosX > tileMaxX
                    || go.PosZ < tileMinZ || go.PosZ > tileMaxZ)
                    continue;

                // Box obstacle (3D solid) — area=0 (RC_NULL_AREA) blocks Recast navigation
                float hs = 0.5f * go.Scale; // half-size XZ
                float ht = 2.0f * go.Scale; // total height Y
                float x0 = go.PosX - hs, x1 = go.PosX + hs;
                float y0 = go.PosY,       y1 = go.PosY + ht;
                float z0 = go.PosZ - hs, z1 = go.PosZ + hs;

                int baseIdx = goVerts.Count / 3;


                // 8 vertices
                goVerts.Add(x0); goVerts.Add(y0); goVerts.Add(z0);
                goVerts.Add(x1); goVerts.Add(y0); goVerts.Add(z0);
                goVerts.Add(x1); goVerts.Add(y0); goVerts.Add(z1);
                goVerts.Add(x0); goVerts.Add(y0); goVerts.Add(z1);
                goVerts.Add(x0); goVerts.Add(y1); goVerts.Add(z0);
                goVerts.Add(x1); goVerts.Add(y1); goVerts.Add(z0);
                goVerts.Add(x1); goVerts.Add(y1); goVerts.Add(z1);
                goVerts.Add(x0); goVerts.Add(y1); goVerts.Add(z1);

                // 12 triangles (6 faces × 2), area=0 for every triangle
                // +Y face
                goIndices.Add(baseIdx+4); goIndices.Add(baseIdx+5); goIndices.Add(baseIdx+6);
                goIndices.Add(baseIdx+4); goIndices.Add(baseIdx+6); goIndices.Add(baseIdx+7);
                // -Y face
                goIndices.Add(baseIdx+1); goIndices.Add(baseIdx+0); goIndices.Add(baseIdx+3);
                goIndices.Add(baseIdx+1); goIndices.Add(baseIdx+3); goIndices.Add(baseIdx+2);
                // +X face
                goIndices.Add(baseIdx+1); goIndices.Add(baseIdx+5); goIndices.Add(baseIdx+6);
                goIndices.Add(baseIdx+1); goIndices.Add(baseIdx+6); goIndices.Add(baseIdx+2);
                // -X face
                goIndices.Add(baseIdx+0); goIndices.Add(baseIdx+4); goIndices.Add(baseIdx+7);
                goIndices.Add(baseIdx+0); goIndices.Add(baseIdx+7); goIndices.Add(baseIdx+3);
                // +Z face
                goIndices.Add(baseIdx+3); goIndices.Add(baseIdx+7); goIndices.Add(baseIdx+6);
                goIndices.Add(baseIdx+3); goIndices.Add(baseIdx+6); goIndices.Add(baseIdx+2);
                // -Z face
                goIndices.Add(baseIdx+0); goIndices.Add(baseIdx+1); goIndices.Add(baseIdx+5);
                goIndices.Add(baseIdx+0); goIndices.Add(baseIdx+5); goIndices.Add(baseIdx+4);

                for (int i = 0; i < 12; i++) goAreas.Add(0); // RC_NULL_AREA — solid obstacle
            }

            if (goVerts.Count > 0)
            {
                var combinedVerts = new float[geo.Vertices.Length + goVerts.Count];
                var combinedIdx = new int[geo.Indices.Length + goIndices.Count];
                var combinedAreas = new byte[geo.Areas.Length + goAreas.Count];
                geo.Vertices.CopyTo(combinedVerts, 0);
                goVerts.ToArray().CopyTo(combinedVerts, geo.Vertices.Length);
                geo.Indices.CopyTo(combinedIdx, 0);
                // B22: offset GO indices to point into the combined vertex array
                int vertBase = geo.Vertices.Length / 3;
                for (int i = 0; i < goIndices.Count; i++) combinedIdx[geo.Indices.Length + i] = goIndices[i] + vertBase;
                geo.Areas.CopyTo(combinedAreas, 0);
                goAreas.ToArray().CopyTo(combinedAreas, geo.Areas.Length);
                geo = new TileGeometry(combinedVerts, combinedIdx, combinedAreas);
            }
        }

        return geo;
    }

    private static TileGeometry ExtrudeTileGeometry(uint mapId, int centerX, int centerY, List<(int X, int Y, AdtFile[] Tile)> tiles)
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

                float chunkOriginX = adjX * WowConstants.TileSize - WowConstants.MapHalfSize + chunkX * WowConstants.ChunkSize;
                float chunkOriginZ = adjY * WowConstants.TileSize - WowConstants.MapHalfSize + chunkY * WowConstants.ChunkSize;

                var chunkHeights = adt.GetChunkHeights(chunkIdx);

                for (int z = 0; z < 9; z++)
                {
                    for (int x = 0; x < 9; x++)
                    {
                        float h = AdtMcvt.GetV9(chunkHeights, z, x);
                        vertices.Add(chunkOriginX + x * WowConstants.ChunkSize / WowConstants.MCNKVerticesSide);
                        vertices.Add(h);
                        vertices.Add(chunkOriginZ + z * WowConstants.ChunkSize / WowConstants.MCNKVerticesSide);
                    }
                }

                for (int z = 0; z < 8; z++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        int v0 = vertexOffset + z * 9 + x;
                        indices.Add(v0); indices.Add(v0 + 9); indices.Add(v0 + 1);
                        indices.Add(v0 + 1); indices.Add(v0 + 9); indices.Add(v0 + 10);
                        byte areaType = adt.GetChunkAreaType(chunkIdx);
                        areas.Add(areaType); areas.Add(areaType);
                    }
                }
                vertexOffset = vertices.Count / 3;
            }
        }

        return new TileGeometry(vertices.ToArray(), indices.ToArray(), areas.ToArray());
    }

    private async Task BuildSubTilesAsync(uint mapId, int adtX, int adtY, TileGeometry geometry, CancellationToken ct)
    {
        float tileOriginX = adtX * WowConstants.TileSize - WowConstants.MapHalfSize;
        float tileOriginZ = adtY * WowConstants.TileSize - WowConstants.MapHalfSize;

        for (int subY = 0; subY < 4; subY++)
        {
            for (int subX = 0; subX < 4; subX++)
            {
                ct.ThrowIfCancellationRequested();
                float subOriginX = tileOriginX + subX * WowConstants.SubTileSize;
                float subOriginZ = tileOriginZ + subY * WowConstants.SubTileSize;

                int globalTileX = adtX * 4 + subX;
                int globalTileY = adtY * 4 + subY;

                var navData = BuildNavMeshTileSync(geometry, globalTileX, globalTileY, subOriginX, subOriginZ);
                if (navData != null)
                    await WriteMmtileAsync(mapId, adtX, adtY, subX, subY, navData, ct);
            }
        }
    }

    private unsafe byte[]? BuildNavMeshTileSync(
        TileGeometry geo, int globalTileX, int globalTileY, float originX, float originZ)
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
            TileX = globalTileX,
            TileY = globalTileY,
            MaxVertsPerPoly = 6
        };

        float tileX = originX + WowConstants.SubTileSize;
        float tileZ = originZ + WowConstants.SubTileSize;

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
            p.BoundingBoxMinX = originX;
            p.BoundingBoxMinY = minY - 2f;
            p.BoundingBoxMinZ = originZ;
            p.BoundingBoxMaxX = tileX;
            p.BoundingBoxMaxY = maxYh + 2f;
            p.BoundingBoxMaxZ = tileZ;

            byte* outData;
            int outSize;

            if (!RecastNative.BuildTile(&p, v, vertCount, i, triCount, a, &outData, &outSize))
                return null;

            var result = new byte[outSize];
            fixed (byte* dest = result)
            {
                Buffer.MemoryCopy(outData, dest, outSize, outSize);
            }
            RecastNative.FreeBuffer(outData);
            return result;
        }
    }

    private async Task WriteMmtileAsync(uint mapId, int adtX, int adtY, int subX, int subY, byte[] navData, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(_outputDir);

            int globalSubY = adtY * 4 + subY;
            int globalSubX = adtX * 4 + subX;
            string fileName = $"{mapId:D3}{globalSubY:D3}{globalSubX:D3}.mmtile";
            string filePath = Path.Combine(_outputDir, fileName);

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);
            writer.Write(MagicBytes.MmapMagic);
            writer.Write(MagicBytes.DtNavMeshVersion);
            writer.Write(MagicBytes.MmapVersion);
            writer.Write((uint)navData.Length);
            writer.Write((byte)1);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write(navData);
        }, ct);
    }

    internal void ClearCache() => _adtParser.ClearCache();
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
