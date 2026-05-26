using System.IO;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Formats.Adt.Models;
using MaNGOS.Extractor.Formats.Map.Models;

namespace MaNGOS.Extractor.Formats.Map.Writing;

/// <summary>
/// Writes MaNGOS .map terrain files from parsed ADT tiles.
/// One file per tile: {mapId:03d}{tileX:02d}{tileY:02d}.map
/// </summary>
public sealed class MapFileWriter
{
    private readonly ILogger<MapFileWriter> _logger;
    private readonly string _outputDir;

    /// <summary>
    /// Creates a new .map writer.
    /// </summary>
    public MapFileWriter(string outputDir, ILogger<MapFileWriter> logger)
    {
        _outputDir = outputDir;
        _logger = logger;

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);
    }

    /// <summary>
    /// Writes a single .map tile file.
    /// </summary>
    public async Task WriteTileAsync(MapTile tile, CancellationToken ct = default)
    {
        string fileName = $"{tile.MapId:D3}{tile.TileX:D2}{tile.TileY:D2}.map";
        string filePath = Path.Combine(_outputDir, fileName);

        await Task.Run(() => WriteTileSync(tile, filePath), ct);

        _logger.LogDebug("Wrote tile: {Path}", filePath);
    }

    private void WriteTileSync(MapTile tile, string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        writer.Write(MagicBytes.MapMagicWotlk); // mapMagic
        writer.Write(1u); // versionMagic
        writer.Write(WowConstants.TargetBuild); // buildMagic

        // Area map section
        uint areaMapOffset = 44;
        writer.Write(areaMapOffset);
        writer.Write(512u); // 256 × ushort

        // Height map section
        uint heightMapOffset = areaMapOffset + 512;
        writer.Write(heightMapOffset);
        writer.Write(16u + (uint)(tile.HeightMap?.Length ?? 145) * 4); // header + heights

        // Liquid map section
        uint liquidMapOffset = heightMapOffset + 16u + (uint)(tile.HeightMap?.Length ?? 145) * 4;
        writer.Write(liquidMapOffset);
        writer.Write(tile.LiquidMap != null ? 16u : 0u);

        // Holes section
        uint holesOffset = liquidMapOffset + (tile.LiquidMap != null ? 16u : 0u);
        writer.Write(holesOffset);
        writer.Write(512u); // 256 × ushort

        if (tile.AreaMap != null)
        {
            for (int i = 0; i < 256; i++)
                writer.Write(i < tile.AreaMap.Length ? tile.AreaMap[i] : (ushort)0);
        }
        else
        {
            for (int i = 0; i < 256; i++)
                writer.Write((ushort)0);
        }

        writer.Write(MagicBytes.HeightMapMagic);
        writer.Write(0u); // flags
        writer.Write(tile.MinHeight);
        writer.Write(tile.MaxHeight);

        if (tile.HeightMap != null)
        {
            foreach (var h in tile.HeightMap)
                writer.Write(h);
        }
        else
        {
            for (int i = 0; i < 145; i++)
                writer.Write(0f);
        }

        if (tile.LiquidMap != null)
        {
            writer.Write(MagicBytes.LiquidMapMagic);

            foreach (var entry in tile.LiquidMap)
            {
                writer.Write((ushort)(entry.HasLiquid ? 0 : 0x0001)); // flags
                writer.Write((ushort)entry.Type);
                writer.Write(entry.OffsetX);
                writer.Write(entry.OffsetY);
                writer.Write(entry.Width);
                writer.Write(entry.Height);
                writer.Write(entry.Level);
            }
        }

        if (tile.HolesMap != null)
        {
            for (int i = 0; i < 256; i++)
                writer.Write(i < tile.HolesMap.Length ? tile.HolesMap[i] : (ushort)0);
        }
        else
        {
            for (int i = 0; i < 256; i++)
                writer.Write((ushort)0);
        }
    }

    /// <summary>
    /// Converts an ADT tile to a MapTile for .map output.
    /// </summary>
    public static MapTile FromAdtTile(AdtFile adt)
    {
        var tile = new MapTile(adt.MapId, adt.TileX, adt.TileY)
        {
            AreaMap = adt.GetAllAreaIds().ToArray(),
            MinHeight = float.MaxValue,
            MaxHeight = float.MinValue
        };

        var heights = new List<float>(256 * 145);
        for (int i = 0; i < 256; i++)
        {
            var chunkHeights = adt.GetChunkHeights(i);
            foreach (var h in chunkHeights)
            {
                if (h > -9000f)
                {
                    heights.Add(h);
                    if (h < tile.MinHeight) tile.MinHeight = h;
                    if (h > tile.MaxHeight) tile.MaxHeight = h;
                }
            }
        }

        tile.HeightMap = heights.ToArray();
        return tile;
    }
}