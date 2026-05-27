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
        _logger.LogInformation("[Map] Output directory: {OutputDir}", outputDir);
    }

    public async Task<int> ExtractMapAsync(
        uint mapId,
        string mapName,
        IProgress<TileProgressEvent>? progress,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[Map] Starting map extraction for {MapName} (id={MapId})", mapName, mapId);

        if (!await _wdtReader.LoadAsync(mapName, ct))
        {
            _logger.LogError("[Map] Failed to load WDT for map: {MapName}", mapName);
            return 0;
        }

        var tiles = _wdtReader.GetExistingTiles();
        _logger.LogInformation("[Map] Found {Count} ADT tiles for {MapName}", tiles.Count, mapName);

        int successCount = 0, failCount = 0;

        foreach (var (tileX, tileY) in tiles)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY, TileStatus.Processing, ExtractionPhase.Map));

            bool success = await ProcessTileAsync(mapId, mapName, tileX, tileY, ct);

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY,
                success ? TileStatus.Done : TileStatus.Failed,
                ExtractionPhase.Map));

            if (success) successCount++; else failCount++;
        }

        _logger.LogInformation("[Map] Extraction complete for {MapName}: {Success} OK, {Failed} failed, {Total} total",
            mapName, successCount, failCount, tiles.Count);
        return successCount;
    }

    private async Task<bool> ProcessTileAsync(uint mapId, string mapName, int tileX, int tileY, CancellationToken ct)
    {
        string adtPath = $"World\\Maps\\{mapName}\\{mapName}_{tileX:D2}_{tileY:D2}.adt";

        var result = await _adtParser.ParseAsync(adtPath, mapId, tileX, tileY, ct);

        if (!result.Success)
        {
            _logger.LogWarning("[Map] Failed to parse ADT ({TileX},{TileY}): {Path}", tileX, tileY, adtPath);
            return false;
        }

        var adt = result.Tile!;
        int liquidChunks = 0;
        for (int i = 0; i < 256; i++)
            if (adt.GetLiquidData(i).HasLiquid) liquidChunks++;

        _logger.LogInformation("[Map] ADT ({TileX},{TileY}): {Textures} textures, {Wmos} WMOs, {Models} M2s, {LiquidChunks}/256 liquid chunks",
            tileX, tileY, adt.TextureNames.Length, adt.WmoNames.Length, adt.ModelNames.Length, liquidChunks);

        try
        {
            var mapTile = MapFileWriter.FromAdtTile(adt);
            await _writer.WriteTileAsync(mapTile, ct);
            _logger.LogInformation("[Map] ADT ({TileX},{TileY}) written OK", tileX, tileY);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Map] Failed to write map tile: ({TileX},{TileY})", tileX, tileY);
            return false;
        }
    }

    internal void ClearCache() => _adtParser.ClearCache();
}
