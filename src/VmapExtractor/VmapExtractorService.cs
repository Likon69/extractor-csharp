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
        _logger.LogInformation("[Vmap] Output directory: {OutputDir}", outputDir);
    }

    public async Task<int> ExtractMapAsync(
        uint mapId,
        string mapName,
        IProgress<TileProgressEvent>? progress,
        CancellationToken ct = default,
        int? onlyTileX = null,
        int? onlyTileY = null)
    {
        _logger.LogInformation("[Vmap] Starting vmap extraction for {MapName} (id={MapId})", mapName, mapId);

        if (!await _wdtReader.LoadAsync(mapName, ct))
        {
            _logger.LogError("[Vmap] Failed to load WDT for map: {MapName}", mapName);
            return 0;
        }

        var tiles = _wdtReader.GetExistingTiles();
        if (onlyTileX.HasValue && onlyTileY.HasValue)
            tiles = tiles.Where(t => t.X == onlyTileX.Value && t.Y == onlyTileY.Value).ToList();
        _logger.LogInformation("[Vmap] Found {Count} ADT tiles for {MapName}", tiles.Count, mapName);

        int successCount = 0, failCount = 0;
        int totalWmos = 0, totalM2s = 0, totalWmoGroups = 0;

        foreach (var (tileX, tileY) in tiles)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY, TileStatus.Processing, ExtractionPhase.Vmap));

            var (success, wmos, m2s, groups) = await ProcessTileAsync(mapId, mapName, tileX, tileY, ct);

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY,
                success ? TileStatus.Done : TileStatus.Failed,
                ExtractionPhase.Vmap));

            if (success) successCount++; else failCount++;
            totalWmos += wmos; totalM2s += m2s; totalWmoGroups += groups;
        }

        _logger.LogInformation("[Vmap] Extraction complete for {MapName}: {Success} OK, {Failed} failed, {Total} tiles. " +
            "Total: {Wmos} WMOs ({Groups} groups), {M2s} M2 models",
            mapName, successCount, failCount, tiles.Count, totalWmos, totalWmoGroups, totalM2s);
        return successCount;
    }

    private async Task<(bool ok, int wmoCount, int m2Count, int groupCount)> ProcessTileAsync(uint mapId, string mapName, int tileX, int tileY, CancellationToken ct)
    {
        if (_writer.OutputFileExists(mapId, tileX, tileY))
        {
            _logger.LogDebug("[Vmap] Skipping ({TileX},{TileY}) — already exists", tileX, tileY);
            return (true, 0, 0, 0);
        }

        int wmoCount = 0, m2Count = 0, wmoGroupCount = 0;
        string adtPath = $"World\\Maps\\{mapName}\\{mapName}_{tileX}_{tileY}.adt";

        var result = await _adtParser.ParseAsync(adtPath, mapId, tileX, tileY, ct);
        if (!result.Success)
        {
            _logger.LogWarning("[Vmap] Failed to parse ADT ({TileX},{TileY})", tileX, tileY);
            return (false, 0, 0, 0);
        }

        var tile = new VmapTile(mapId, tileX, tileY);

        // Process WMO placements (MODF entries)
        foreach (var modf in result.Tile!.WmoPlacements)
        {
            string? wmoName = GetWmoName(result.Tile.WmoNames, (int)modf.NameId);
            if (string.IsNullOrEmpty(wmoName))
                continue;

            var wmoResult = await _wmoParser.ParseRootAsync(wmoName, ct);
            if (!wmoResult.Success)
            {
                _logger.LogWarning("[Vmap] ADT ({TileX},{TileY}): failed to parse WMO root: {WmoName}", tileX, tileY, wmoName);
                continue;
            }

            wmoCount++;
            for (uint groupIdx = 0; groupIdx < wmoResult.Root!.Header.GroupCount; groupIdx++)
            {
                string groupFilePath = wmoName[..^4] + $"_{groupIdx:D3}.wmo";
                var grpFile = await _wmoParser.ParseGroupAsync(groupFilePath, (int)groupIdx, wmoName, ct);

                var bbMin = grpFile != null ? grpFile.Header.BoundingBoxMin : wmoResult.Root.Header.BoundingBoxMin;
                var bbMax = grpFile != null ? grpFile.Header.BoundingBoxMax : wmoResult.Root.Header.BoundingBoxMax;

                var group = new VmapGroupData
                {
                    Name = groupFilePath,
                    Flags = grpFile?.Header.Flags ?? 0,
                    GroupWmoId = grpFile?.Header.GroupWmoId ?? groupIdx,
                    BoundingBoxMin = new Vector3Min(bbMin.X, bbMin.Y, bbMin.Z),
                    BoundingBoxMax = new Vector3Min(bbMax.X, bbMax.Y, bbMax.Z),
                    LiquidFlags = grpFile?.Header.LiquidType ?? 0,
                    Vertices = BuildVertexArray(grpFile?.Vertices),
                    Indices = BuildIndexArray(grpFile?.Triangles),
                    MobaData = BuildMobaData(grpFile?.RawMoba),
                    BatchCount = (grpFile?.RawMoba?.Length ?? 0) / 12
                };
                tile.AddGroup(group);
                wmoGroupCount++;
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
            m2Count++;
        }

        _logger.LogInformation("[Vmap] ADT ({TileX},{TileY}): {Wmos} WMOs ({Groups} groups), {M2s} M2 models",
            tileX, tileY, wmoCount, wmoGroupCount, m2Count);

        try
        {
            var tiles = new Dictionary<(int, int), VmapTile> { { (tileX, tileY), tile } };
            await _writer.WriteVmapFilesAsync(mapId, tiles, ct);
            _logger.LogInformation("[Vmap] ADT ({TileX},{TileY}) written OK", tileX, tileY);
            return (true, wmoCount, m2Count, wmoGroupCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Vmap] Failed to write vmap tile: ({TileX},{TileY})", tileX, tileY);
            return (false, wmoCount, m2Count, wmoGroupCount);
        }
    }

    private static float[] BuildVertexArray(WmoVertex[]? vertices)
    {
        if (vertices == null || vertices.Length == 0) return Array.Empty<float>();
        var result = new float[vertices.Length * 3];
        for (int i = 0; i < vertices.Length; i++)
        {
            result[i * 3] = vertices[i].X;
            result[i * 3 + 1] = vertices[i].Y;
            result[i * 3 + 2] = vertices[i].Z;
        }
        return result;
    }

    private static ushort[] BuildIndexArray(WmoTriangle[]? triangles)
    {
        if (triangles == null || triangles.Length == 0) return Array.Empty<ushort>();
        var result = new ushort[triangles.Length * 3];
        for (int i = 0; i < triangles.Length; i++)
        {
            result[i * 3] = triangles[i].I0;
            result[i * 3 + 1] = triangles[i].I1;
            result[i * 3 + 2] = triangles[i].I2;
        }
        return result;
    }

    private static int[] BuildMobaData(ushort[]? rawMoba)
    {
        // Matches C++ exactly: moba_batch = moba_size/12; MobaEx[k] = MOBA[8 + k*12];
        // Each batch = 12 uint16s = 24 bytes; value at uint16[8] = byte 16 of each batch.
        if (rawMoba == null || rawMoba.Length < 12) return Array.Empty<int>();
        int batchCount = rawMoba.Length / 12;
        var result = new int[batchCount];
        int k = 0;
        for (int i = 8; i < rawMoba.Length; i += 12)
            result[k++] = rawMoba[i];
        return result;
    }

    private static string? GetWmoName(string[] names, int index)
        => index >= 0 && index < names.Length ? names[index] : null;

    private static string? GetModelName(string[] names, int index)
        => index >= 0 && index < names.Length ? names[index] : null;

    internal void ClearCache() => _adtParser.ClearCache();
}
