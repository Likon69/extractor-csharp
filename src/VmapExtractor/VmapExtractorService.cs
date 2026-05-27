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
                // PORT-005: load group file to get actual geometry
                string groupFilePath = wmoName.Replace(".wmo", $"{groupIdx:D3}.wmo");
                var grpFile = await _wmoParser.ParseGroupAsync(groupFilePath, (int)groupIdx, wmoName, ct);

                var bbMin = grpFile != null ? grpFile.Header.BoundingBoxMin : wmoResult.Root.Header.BoundingBoxMin;
                var bbMax = grpFile != null ? grpFile.Header.BoundingBoxMax : wmoResult.Root.Header.BoundingBoxMax;

                var group = new VmapGroupData
                {
                    Name           = groupFilePath,
                    Flags          = grpFile?.Header.Flags ?? 0,
                    GroupWmoId     = grpFile?.Header.GroupWmoId ?? groupIdx,
                    BoundingBoxMin = new Vector3Min(bbMin.X, bbMin.Y, bbMin.Z),
                    BoundingBoxMax = new Vector3Min(bbMax.X, bbMax.Y, bbMax.Z),
                    LiquidFlags    = grpFile?.Header.LiquidType ?? 0,
                    Vertices       = BuildVertexArray(grpFile?.Vertices),
                    Indices        = BuildIndexArray(grpFile?.Triangles),
                    MobaData       = BuildMobaData(grpFile?.Batches),
                    BatchCount     = grpFile?.Batches.Length ?? 0
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

    private static float[] BuildVertexArray(WmoVertex[]? vertices)
    {
        if (vertices == null || vertices.Length == 0) return Array.Empty<float>();
        var result = new float[vertices.Length * 3];
        for (int i = 0; i < vertices.Length; i++)
        {
            result[i * 3]     = vertices[i].X;
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
            result[i * 3]     = triangles[i].I0;
            result[i * 3 + 1] = triangles[i].I1;
            result[i * 3 + 2] = triangles[i].I2;
        }
        return result;
    }

    /// <summary>
    /// Builds MobaEx data (batch index counts) matching C++ MobaEx formula.
    /// C++: MOBA is uint16[], each batch is 12 uint16 entries (24 bytes).
    /// MOBA[8] within each batch = Count (uint16) = number of triangle indices.
    /// In C# WmoBatch (6×uint32), Count occupies the low 16 bits of VertexCount.
    /// </summary>
    private static int[] BuildMobaData(WmoBatch[]? batches)
    {
        if (batches == null || batches.Length == 0) return Array.Empty<int>();
        var result = new int[batches.Length];
        for (int i = 0; i < batches.Length; i++)
            result[i] = (int)(batches[i].VertexCount & 0xFFFF);
        return result;
    }

    private static string? GetWmoName(string[] names, int index)
        => index >= 0 && index < names.Length ? names[index] : null;

    private static string? GetModelName(string[] names, int index)
        => index >= 0 && index < names.Length ? names[index] : null;

    internal void ClearCache() => _adtParser.ClearCache();
}