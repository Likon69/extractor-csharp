using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;
using MaNGOS.Extractor.Core.Models;
using MaNGOS.Extractor.Formats.Adt.Models;
using MaNGOS.Extractor.Formats.Adt.Parsing;
using MaNGOS.Extractor.Formats.Wdt;

namespace MaNGOS.Extractor.RoadExtractor;

public sealed class RoadExtractorService
{
    private readonly IArchiveReader _archive;
    private readonly WdtReader _wdtReader;
    private readonly AdtParser _adtParser;
    private readonly ILogger _logger;
    private readonly string _outputDir;

    private static readonly string[] RoadPatterns =
    {
        "road", "cobblestone", "path_stone", "bridgefloor"
    };

    public RoadExtractorService(
        IArchiveReader archive,
        ILoggerFactory loggerFactory,
        string outputDir)
    {
        _archive = archive;
        _logger = loggerFactory.CreateLogger<RoadExtractorService>();
        _wdtReader = new WdtReader(archive);
        _adtParser = new AdtParser(archive, loggerFactory.CreateLogger<AdtParser>());
        _outputDir = outputDir;
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);
        _logger.LogInformation("[Road] Output directory: {OutputDir}", outputDir);
    }

    public async Task<int> ExtractMapAsync(
        uint mapId,
        string mapName,
        IProgress<TileProgressEvent>? progress,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[Road] Starting road extraction for {MapName} (id={MapId})", mapName, mapId);

        if (!await _wdtReader.LoadAsync(mapName, ct))
        {
            _logger.LogError("[Road] Failed to load WDT for map: {MapName}", mapName);
            return 0;
        }

        var tiles = _wdtReader.GetExistingTiles();
        _logger.LogInformation("[Road] Found {Count} ADT tiles for {MapName}", tiles.Count, mapName);

        int successCount = 0, failCount = 0;
        int totalRoadChunks = 0, totalChunks = 0;

        foreach (var (tileX, tileY) in tiles)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY, TileStatus.Processing, ExtractionPhase.Road));

            var (success, roadChunks, chunks) = await ProcessTileAsync(mapId, mapName, tileX, tileY, ct);

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY,
                success ? TileStatus.Done : TileStatus.Failed,
                ExtractionPhase.Road));

            if (success) successCount++; else failCount++;
            totalRoadChunks += roadChunks; totalChunks += chunks;
        }

        _logger.LogInformation("[Road] Extraction complete for {MapName}: {Success} OK, {Failed} failed. " +
            "Road chunks: {RoadChunks}/{TotalChunks}",
            mapName, successCount, failCount, totalRoadChunks, totalChunks);
        return successCount;
    }

    private async Task<(bool ok, int roadChunkCount, int totalChunkCount)> ProcessTileAsync(uint mapId, string mapName, int tileX, int tileY, CancellationToken ct)
    {
        int roadChunkCount = 0, totalChunkCount = 256;
        string adtPath = $"World\\Maps\\{mapName}\\{mapName}_{tileX:D2}_{tileY:D2}.adt";

        var result = await _adtParser.ParseAsync(adtPath, mapId, tileX, tileY, ct);
        if (!result.Success || result.Tile == null)
        {
            _logger.LogWarning("[Road] Failed to parse ADT ({TileX},{TileY})", tileX, tileY);
            return (false, 0, 256);
        }

        var roadFlags = DetectRoadChunksPerChunk(result.Tile);
        roadChunkCount = roadFlags.Count(b => b == 1);

        string fileName = $"{mapId:D3}{tileY:D2}{tileX:D2}.road";
        string filePath = Path.Combine(_outputDir, fileName);

        if (roadChunkCount > 0)
            _logger.LogInformation("[Road] ADT ({TileX},{TileY}): {RoadChunks}/256 road chunks detected",
                tileX, tileY, roadChunkCount);

        try
        {
            await File.WriteAllBytesAsync(filePath, roadFlags, ct);
            return (true, roadChunkCount, 256);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Road] Failed to write road file: ({TileX},{TileY})", tileX, tileY);
            return (false, roadChunkCount, 256);
        }
    }

    private byte[] DetectRoadChunksPerChunk(AdtFile adt)
    {
        var roadFlags = new byte[256];

        for (int chunkIdx = 0; chunkIdx < 256; chunkIdx++)
        {
            uint textureId = adt.GetChunkTextureId(chunkIdx);

            if (textureId < adt.TextureNames.Length)
            {
                string texName = adt.TextureNames[(int)textureId];
                roadFlags[chunkIdx] = IsRoadTexture(texName) ? (byte)1 : (byte)0;
            }
        }

        return roadFlags;
    }

    private static bool IsRoadTexture(string textureName)
    {
        if (string.IsNullOrEmpty(textureName))
            return false;

        string lower = textureName.ToLowerInvariant();
        foreach (var pattern in RoadPatterns)
        {
            if (lower.Contains(pattern))
                return true;
        }
        return false;
    }

    internal void ClearCache() => _adtParser.ClearCache();
}
