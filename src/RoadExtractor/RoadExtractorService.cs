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
    }

    public async Task<int> ExtractMapAsync(
        uint mapId,
        string mapName,
        IProgress<TileProgressEvent>? progress,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting road extraction for {MapName}", mapName);

        if (!await _wdtReader.LoadAsync(mapName, ct))
        {
            _logger.LogError("Failed to load WDT for map: {MapName}", mapName);
            return 0;
        }

        var tiles = _wdtReader.GetExistingTiles();
        int successCount = 0;

        foreach (var (tileX, tileY) in tiles)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY, TileStatus.Processing, ExtractionPhase.Road));

            bool success = await ProcessTileAsync(mapId, tileX, tileY, ct);

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY,
                success ? TileStatus.Done : TileStatus.Failed,
                ExtractionPhase.Road));

            if (success)
                successCount++;
        }

        _logger.LogInformation("Road extraction complete: {Success}/{Total} tiles", successCount, tiles.Count);
        return successCount;
    }

    private async Task<bool> ProcessTileAsync(uint mapId, int tileX, int tileY, CancellationToken ct)
    {
        string mapDir = WowConstants.GetMapDirectory(mapId);
        string adtPath = $"World\\Maps\\{mapDir}\\{mapDir}_{tileX:D2}_{tileY:D2}.adt";

        var result = await _adtParser.ParseAsync(adtPath, mapId, tileX, tileY, ct);
        if (!result.Success || result.Tile == null)
            return false;

        var roadFlags = DetectRoadChunksPerChunk(result.Tile);

        string fileName = $"{mapId:D3}{tileX:D2}{tileY:D2}.road";
        string filePath = Path.Combine(_outputDir, fileName);

        try
        {
            await File.WriteAllBytesAsync(filePath, roadFlags, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write road file: {Path}", filePath);
            return false;
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
            else
            {
                roadFlags[chunkIdx] = 0;
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

    public void ClearCache() => _adtParser.ClearCache();
}