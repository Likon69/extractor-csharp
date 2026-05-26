using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;
using MaNGOS.Extractor.Core.Models;
using MaNGOS.Extractor.Formats.Adt.Models;
using MaNGOS.Extractor.Formats.Adt.Parsing;
using MaNGOS.Extractor.Formats.Vmap.Models;
using MaNGOS.Extractor.Formats.Vmap.Writing;
using MaNGOS.Extractor.Formats.Wdt;
using MaNGOS.Extractor.Formats.Wmo.Models;
using MaNGOS.Extractor.Formats.Wmo.Parsing;

namespace MaNGOS.Extractor.VmapExtractor;

/// <summary>
/// Extracts visibility map data (vmaps) from WMO and M2 models.
/// Produces VMAP tile files with model references and bounding data.
/// </summary>
public sealed class VmapExtractorService
{
    private readonly IArchiveReader _archive;
    private readonly WdtReader _wdtReader;
    private readonly AdtParser _adtParser;
    private readonly WmoParser _wmoParser;
    private readonly VmapFileWriter _writer;
    private readonly ILogger _logger;

    public VmapExtractorService(
        IArchiveReader archive,
        ILoggerFactory loggerFactory,
        string outputDir)
    {
        _archive = archive;
        _logger = loggerFactory.CreateLogger<VmapExtractorService>();
        _wdtReader = new WdtReader(archive);
        _adtParser = new AdtParser(archive, loggerFactory.CreateLogger<AdtParser>());
        _wmoParser = new WmoParser(archive, loggerFactory.CreateLogger<WmoParser>());
        _writer = new VmapFileWriter(outputDir, loggerFactory.CreateLogger<VmapFileWriter>());
    }

    /// <summary>
    /// Extracts VMAP data for a specific map.
    /// </summary>
    public async Task<int> ExtractMapAsync(
        uint mapId,
        string mapName,
        IProgress<TileProgressEvent>? progress,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting vmap extraction for {MapName}", mapName);

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
                (int)mapId, tileX, tileY, TileStatus.Processing, ExtractionPhase.Vmap));

            bool success = await ProcessTileAsync(mapId, mapName, tileX, tileY, ct);

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY,
                success ? TileStatus.Done : TileStatus.Failed,
                ExtractionPhase.Vmap));

            if (success)
                successCount++;
        }

        _logger.LogInformation("Vmap extraction complete: {Success}/{Total} tiles", successCount, tiles.Count);
        return successCount;
    }

    private async Task<bool> ProcessTileAsync(uint mapId, string mapName, int tileX, int tileY, CancellationToken ct)
    {
        string adtPath = $"World\\Maps\\{mapName}\\{mapName}_{tileX:D2}_{tileY:D2}.adt";

        var result = await _adtParser.ParseAsync(adtPath, mapId, tileX, tileY, ct);
        if (!result.Success)
            return false;

        var tile = new VmapTile(mapId, tileX, tileY);

        // Process WMO placements (MODF entries)
        foreach (var modf in result.Tile!.WmoPlacements)
        {
            string? wmoName = GetWmoName(result.Tile.WmoNames, (int)modf.NameId);
            if (string.IsNullOrEmpty(wmoName))
                continue;

            var wmoResult = await _wmoParser.ParseRootAsync(wmoName, ct);
            if (!wmoResult.Success)
                continue;

            for (uint groupIdx = 0; groupIdx < wmoResult.Root!.Header.GroupCount; groupIdx++)
            {
                string groupName = $"{wmoName}{groupIdx:D3}";
                var bbMin = wmoResult.Root.Header.BoundingBoxMin;
                var bbMax = wmoResult.Root.Header.BoundingBoxMax;
                var group = new VmapGroupData
                {
                    Name = groupName,
                    Flags = GetGroupFlags(wmoResult.Groups, (int)groupIdx),
                    BoundingBoxMin = new Vector3Min(bbMin.X, bbMin.Y, bbMin.Z),
                    BoundingBoxMax = new Vector3Min(bbMax.X, bbMax.Y, bbMax.Z),
                    LiquidType = wmoResult.Root.Header.LiquidType
                };
                tile.AddGroup(group);
            }
        }

        // Process M2 doodad placements (MDDF entries)
        foreach (var mddf in result.Tile.DoodadPlacements)
        {
            string? modelName = GetModelName(result.Tile.ModelNames, (int)mddf.NameId);
            if (string.IsNullOrEmpty(modelName))
                continue;

            var placement = new VmapModelPlacement
            {
                Name = modelName,
                PositionX = mddf.PositionX,
                PositionY = mddf.PositionY,
                PositionZ = mddf.PositionZ,
                RotationY = mddf.RotationY,
                RotationX = mddf.RotationX,
                RotationZ = mddf.RotationZ,
                Scale = mddf.Scale,
                Flags = 1
            };
            tile.AddModel(placement);
        }

        try
        {
            var tiles = new Dictionary<(int, int), VmapTile> { { (tileX, tileY), tile } };
            await _writer.WriteVmapFilesAsync(mapId, tiles, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write vmap tile: {TileX},{TileY}", tileX, tileY);
            return false;
        }
    }

    private static uint GetGroupFlags(WmoGroupFile[] groups, int idx)
    {
        if (idx >= groups.Length)
            return 0;

        uint flags = 0;
        if (groups[idx].Header.IsIndoor)
            flags |= 0x00000001;
        if (groups[idx].Header.HasLiquids)
            flags |= 0x00000004;
        return flags;
    }

    private static string? GetWmoName(string[] names, int index)
        => index >= 0 && index < names.Length ? names[index] : null;

    private static string? GetModelName(string[] names, int index)
        => index >= 0 && index < names.Length ? names[index] : null;

    internal void ClearCache() => _adtParser.ClearCache();
}