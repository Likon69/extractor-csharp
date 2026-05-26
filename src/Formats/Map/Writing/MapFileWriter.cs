using System.IO;
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
        string fileName = $"{tile.MapId:D3}{tile.TileX:D2}{tile.TileY:D2}.map";
        string filePath = Path.Combine(_outputDir, fileName);
        await Task.Run(() => WriteTileSync(tile, filePath), ct);
        _logger.LogDebug("Wrote tile: {Path}", filePath);
    }

    private void WriteTileSync(MapTile tile, string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        // Header: 11 uint32 fields = 44 bytes (matching MaNGOS GridMap.cpp)
        uint headerOffset = 44;
        uint areaMapOffset = headerOffset;
        uint heightMapOffset = areaMapOffset + 8 + 256 * 2;
        uint liquidMapOffset = heightMapOffset + 16 + (129 * 129 + 128 * 128) * 4;
        uint holesOffset = liquidMapOffset + 16;
        uint areaMapSize = 8 + 256 * 2;
        uint heightMapSize = 16 + (129 * 129 + 128 * 128) * 4;
        uint liquidMapSize = 16; // single header, no liquid data
        uint holesSize = 256 * 2;

        writer.Write(MagicBytes.MapMagicWotlk);
        writer.Write(1u);
        writer.Write(WowConstants.TargetBuild);
        writer.Write(areaMapOffset);
        writer.Write(areaMapSize);
        writer.Write(heightMapOffset);
        writer.Write(heightMapSize);
        writer.Write(liquidMapOffset);
        writer.Write(liquidMapSize);
        writer.Write(holesOffset);
        writer.Write(holesSize);

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
        var v8 = BuildV8Heights(v9);

        foreach (var h in v9) writer.Write(h);
        foreach (var h in v8) writer.Write(h);

        // Liquid section: GridMapLiquidHeader(16 bytes)
        writer.Write(MagicBytes.LiquidMapMagic);
        writer.Write((ushort)0); // flags
        writer.Write((ushort)0); // liquidType
        writer.Write((byte)0); // offsetX
        writer.Write((byte)0); // offsetY
        writer.Write((byte)0); // width
        writer.Write((byte)0); // height
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
                // 145 floats per chunk: V9[9×9] interleaved with V8[8×8]
                var chunkData = tile.HeightMap.AsSpan(
                    chunkZ * 16 * AdtMcvt.TotalVertices + chunkX * AdtMcvt.TotalVertices,
                    AdtMcvt.TotalVertices);

                int vBaseZ = chunkZ * 8;
                int vBaseX = chunkX * 8;

                // V9: outer grid vertices (9×9 per chunk)
                for (int z = 0; z < 9; z++)
                    for (int x = 0; x < 9; x++)
                        heights[(vBaseZ + z) * 129 + (vBaseX + x)] = AdtMcvt.GetV9(chunkData, z, x);
            }
        }

        return heights;
    }

    // V8 heights: interpolate from the 4 surrounding V9 corners (MaNGOS original method)
    private static float[] BuildV8Heights(float[] v9)
    {
        var heights = new float[128 * 128];
        for (int z = 0; z < 128; z++)
            for (int x = 0; x < 128; x++)
            {
                int v9Base = z * 129 + x;
                heights[z * 128 + x] = (v9[v9Base] + v9[v9Base + 1]
                    + v9[v9Base + 129] + v9[v9Base + 130]) * 0.25f;
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