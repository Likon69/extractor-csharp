using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;
using MaNGOS.Extractor.Core.Models;
using MaNGOS.Extractor.Formats.Adt.Models;
using MaNGOS.Extractor.Formats.Adt.Parsing;
using MaNGOS.Extractor.Formats.Map.Models;
using MaNGOS.Extractor.Formats.Map.Writing;
using MaNGOS.Extractor.Formats.Wdt;

namespace MaNGOS.Extractor.MapExtractor;

/// <summary>
/// Extracts terrain data (.map files) from ADT tiles.
/// Produces one .map file per tile with heightmaps, area IDs, liquids, and holes.
/// </summary>
public sealed class MapExtractorService
{
    private readonly IArchiveReader _archive;
    private readonly WdtReader _wdtReader;
    private readonly AdtParser _adtParser;
    private readonly MapFileWriter _writer;
    private readonly ILogger<MapExtractorService> _logger;

    public MapExtractorService(
        IArchiveReader archive,
        ILoggerFactory loggerFactory,
        string outputDir)
    {
        _archive = archive;
        _logger = loggerFactory.CreateLogger<MapExtractorService>();
        _wdtReader = new WdtReader(archive);
        _adtParser = new AdtParser(archive, loggerFactory.CreateLogger<AdtParser>());
        _writer = new MapFileWriter(outputDir, loggerFactory.CreateLogger<MapFileWriter>());
    }

    /// <summary>
    /// Extracts terrain data for a specific map.
    /// </summary>
    /// <param name="mapId">Map ID.</param>
    /// <param name="mapName">Map directory name (e.g., "Azeroth").</param>
    /// <param name="progress">Progress reporter for UI updates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of tiles processed successfully.</returns>
    public async Task<int> ExtractMapAsync(
        uint mapId,
        string mapName,
        IProgress<TileProgressEvent>? progress,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting map extraction for {MapName} (ID: {MapId})", mapName, mapId);

        if (!await _wdtReader.LoadAsync(mapName, ct))
        {
            _logger.LogError("Failed to load WDT for map: {MapName}", mapName);
            return 0;
        }

        var tiles = _wdtReader.GetExistingTiles();
        _logger.LogInformation("Found {Count} tiles for map {MapName}", tiles.Count, mapName);

        int successCount = 0;

        foreach (var (tileX, tileY) in tiles)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY, TileStatus.Processing, ExtractionPhase.Map));

            bool success = await ProcessTileAsync(mapId, tileX, tileY, ct);

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY,
                success ? TileStatus.Done : TileStatus.Failed,
                ExtractionPhase.Map));

            if (success)
                successCount++;
        }

        _logger.LogInformation("Map extraction complete: {Success}/{Total} tiles for {MapName}",
            successCount, tiles.Count, mapName);

        return successCount;
    }

    private async Task<bool> ProcessTileAsync(
        uint mapId,
        int tileX,
        int tileY,
        CancellationToken ct)
    {
        string mapDir = WowConstants.GetMapDirectory(mapId);
        string adtPath = $"World\\Maps\\{mapDir}\\{mapDir}_{tileX:D2}_{tileY:D2}.adt";

        var result = await _adtParser.ParseAsync(adtPath, mapId, tileX, tileY, ct);

        if (!result.Success)
        {
            _logger.LogWarning("Failed to parse ADT: {Path}", adtPath);
            return false;
        }

        try
        {
            var mapTile = MapFileWriter.FromAdtTile(result.Tile!);
            await _writer.WriteTileAsync(mapTile, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write map tile: {TileX},{TileY}", tileX, tileY);
            return false;
        }
    }

    public void ClearCache()
    {
        _adtParser.ClearCache();
    }
}