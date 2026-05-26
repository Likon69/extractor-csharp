using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Formats.Adt.Models;
using MaNGOS.Extractor.Formats.Map.Models;

namespace MaNGOS.Extractor.Formats.Map.Writing;

public sealed class MapFileWriter
{
    private readonly ILogger<MapFileWriter> _logger;
    private readonly string _outputDir;

    public MapFileWriter(string outputDir, ILogger<MapFileWriter> logger)
    {
        _outputDir = outputDir;
        _logger = logger;
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);
    }

    public async Task WriteTileAsync(MapTile tile, CancellationToken ct = default)
    {
        string fileName = $"{tile.MapId:D3}{tile.TileY:D2}{tile.TileX:D2}.map";
        string filePath = Path.Combine(_outputDir, fileName);
        await Task.Run(() => WriteTileSync(tile, filePath), ct);
        _logger.LogDebug("Wrote tile: {Path}", filePath);
    }

    private void WriteTileSync(MapTile tile, string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        // 11 uint32 header = 44 bytes (MaNGOS GridMap.cpp)
        uint offsets = 44; // areaMapOffset = 44 (fixed, right after header)
        uint heightMapOffset = offsets + 8 + 256 * 2;
        uint liquidMapOffset = heightMapOffset + 16 + (129 * 129 + 128 * 128) * 4;
        uint holesOffset = liquidMapOffset + 16;
        uint areaMapSize = 8 + 256 * 2;
        uint heightMapSize = 16 + (129 * 129 + 128 * 128) * 4;
        uint liquidMapSize = 16;
        uint holesSize = 256 * 2;

        writer.Write(0x5350414Du);             // "MAPS" — mapMagic
        writer.Write(MagicBytes.MapMagicWotlk); // "v1.5" — 0x76312E35
        writer.Write(WowConstants.TargetBuild); // 12340
        writer.Write(offsets);                  // areaMapOffset = 44
        writer.Write(areaMapSize);              // size of area section
        writer.Write(heightMapOffset);
        writer.Write(heightMapSize);
        writer.Write(liquidMapOffset);
        writer.Write(liquidMapSize);            // 16 (header only)
        writer.Write(holesOffset);
        writer.Write(holesSize);                // 16 bytes holes

        // Area section: GridMapAreaHeader(8 bytes) + uint16[16][16]
        writer.Write(0x47444944u); // 'GRID' fourcc
        writer.Write(0u); // flags
        for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
                writer.Write(tile.AreaMap != null ? tile.AreaMap[z * 16 + x] : (ushort)0);

        // Height section: GridMapHeightHeader(16 bytes) + V9[129][129] + V8[128][128]
        writer.Write(MagicBytes.HeightMapMagic);
        writer.Write(0u); // flags
        writer.Write(tile.MinHeight);
        writer.Write(tile.MaxHeight);

        var v9 = BuildV9Heights(tile);
        var v8 = BuildV8Heights(tile);

        foreach (var h in v9) writer.Write(h);
        foreach (var h in v8) writer.Write(h);

        // Liquid section: GridMapLiquidHeader(16 bytes)
        writer.Write(MagicBytes.LiquidMapMagic);
        writer.Write((ushort)0); // flags
        writer.Write((ushort)0); // liquidType
        writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write(0f); // liquidLevel

        // Holes section: uint16[16][16]
        for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
                writer.Write(tile.HolesMap != null ? tile.HolesMap[z * 16 + x] : (ushort)0);
    }

    private static float[] BuildV9Heights(MapTile tile)
    {
        var heights = new float[129 * 129];
        if (tile.HeightMap == null || tile.HeightMap.Length < 256 * AdtMcvt.TotalVertices)
        {
            Array.Fill(heights, 0f);
            return heights;
        }

        for (int chunkZ = 0; chunkZ < 16; chunkZ++)
        {
            for (int chunkX = 0; chunkX < 16; chunkX++)
            {
                var chunkData = tile.HeightMap.AsSpan(
                    chunkZ * 16 * AdtMcvt.TotalVertices + chunkX * AdtMcvt.TotalVertices,
                    AdtMcvt.TotalVertices);

                int vBaseZ = chunkZ * 8;
                int vBaseX = chunkX * 8;

                for (int z = 0; z < 9; z++)
                    for (int x = 0; x < 9; x++)
                        heights[(vBaseZ + z) * 129 + (vBaseX + x)] = AdtMcvt.GetV9(chunkData, z, x);
            }
        }

        return heights;
    }

    private static float[] BuildV8Heights(MapTile tile)
    {
        var heights = new float[128 * 128];
        if (tile.HeightMap == null || tile.HeightMap.Length < 256 * AdtMcvt.TotalVertices)
        {
            Array.Fill(heights, 0f);
            return heights;
        }

        for (int chunkZ = 0; chunkZ < 16; chunkZ++)
        {
            for (int chunkX = 0; chunkX < 16; chunkX++)
            {
                var chunkData = tile.HeightMap.AsSpan(
                    chunkZ * 16 * AdtMcvt.TotalVertices + chunkX * AdtMcvt.TotalVertices,
                    AdtMcvt.TotalVertices);

                int vBaseZ = chunkZ * 8;
                int vBaseX = chunkX * 8;

                // V8: inner grid vertices, read directly from interleaved data
                for (int z = 0; z < 8; z++)
                    for (int x = 0; x < 8; x++)
                        heights[(vBaseZ + z) * 128 + (vBaseX + x)] = AdtMcvt.GetV8(chunkData, z, x);
            }
        }

        return heights;
    }

    public static MapTile FromAdtTile(AdtFile adt)
    {
        var tile = new MapTile(adt.MapId, adt.TileX, adt.TileY)
        {
            AreaMap = adt.GetAllAreaIds().ToArray(),
            MinHeight = float.MaxValue,
            MaxHeight = float.MinValue
        };

        var heights = new List<float>(256 * AdtMcvt.TotalVertices);
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