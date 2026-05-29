using System.IO;
using System.Linq;
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
        // ── Step 1: Build liquid grid from per-chunk data ─────────────────
        var liquidEntry  = new ushort[16, 16]; // raw LiquidType.dbc ID per MCNK
        var liquidFlags  = new byte[16, 16];   // MAP_LIQUID_TYPE flags per MCNK
        var liquidShow   = new bool[128, 128]; // per sub-cell visibility
        const float NoLiq = -500f;
        var liquidHeight = new float[129, 129];
        for (int y2 = 0; y2 < 129; y2++)
            for (int x2 = 0; x2 < 129; x2++)
                liquidHeight[y2, x2] = NoLiq;

        if (tile.ChunkLiquids != null)
        {
            for (int ci = 0; ci < Math.Min(256, tile.ChunkLiquids.Length); ci++)
            {
                ref readonly var liq = ref tile.ChunkLiquids[ci];
                if (!liq.HasLiquid) continue;
                int cr = ci / 16, cc = ci % 16;
                liquidEntry[cr, cc] = liq.RawTypeId;
                liquidFlags[cr, cc] = liq.TypeFlags;

                for (int ly = 0; ly < liq.Height; ly++)
                    for (int lx = 0; lx < liq.Width; lx++)
                    {
                        int bit = ly * liq.Width + lx;
                        if (liq.ShowMask != 0UL && ((liq.ShowMask >> bit) & 1UL) == 0UL) continue;
                        int gy = cr * 8 + liq.OffsetY + ly, gx = cc * 8 + liq.OffsetX + lx;
                        if ((uint)gy < 128 && (uint)gx < 128) liquidShow[gy, gx] = true;
                    }

                for (int ly = 0; ly <= liq.Height; ly++)
                    for (int lx = 0; lx <= liq.Width; lx++)
                    {
                        int gy = cr * 8 + liq.OffsetY + ly, gx = cc * 8 + liq.OffsetX + lx;
                        if ((uint)gy < 129 && (uint)gx < 129)
                        {
                            int idx = ly * (liq.Width + 1) + lx;
                            liquidHeight[gy, gx] = liq.Heights != null && idx < liq.Heights.Length
                                ? liq.Heights[idx] : liq.MinHeight;
                        }
                    }
            }
        }

        // ── Step 2: Analyse liquid data ───────────────────────────────────
        bool anyLiquid = false;
        for (int y2 = 0; y2 < 16 && !anyLiquid; y2++)
            for (int x2 = 0; x2 < 16; x2++)
                if (liquidFlags[y2, x2] != 0) { anyLiquid = true; break; }

        int liqMinX = 127, liqMinY = 127, liqMaxX = 0, liqMaxY = 0;
        byte liqBaseType = 0;
        bool liqFullType = false;
        float liqMinH = 0f, liqMaxH = 0f;
        ushort liqHdrFlags = 0;
        byte liqW = 1, liqH = 1;

        if (anyLiquid)
        {
            for (int y2 = 0; y2 < 128; y2++)
                for (int x2 = 0; x2 < 128; x2++)
                    if (liquidShow[y2, x2])
                    {
                        if (x2 < liqMinX) liqMinX = x2; if (x2 > liqMaxX) liqMaxX = x2;
                        if (y2 < liqMinY) liqMinY = y2; if (y2 > liqMaxY) liqMaxY = y2;
                    }
            if (liqMinX > liqMaxX) { liqMinX = liqMinY = 0; liqMaxX = liqMaxY = 0; }

            bool baseFound = false;
            for (int y2 = 0; y2 < 16 && !baseFound; y2++)
                for (int x2 = 0; x2 < 16; x2++)
                    if (liquidFlags[y2, x2] != 0) { liqBaseType = liquidFlags[y2, x2]; baseFound = true; break; }

            for (int y2 = 0; y2 < 16 && !liqFullType; y2++)
                for (int x2 = 0; x2 < 16; x2++)
                    if (liquidFlags[y2, x2] != 0 && liquidFlags[y2, x2] != liqBaseType)
                    { liqFullType = true; break; }

            liqW = (byte)(liqMaxX - liqMinX + 2);
            liqH = (byte)(liqMaxY - liqMinY + 2);

            liqMinH = float.MaxValue; liqMaxH = float.MinValue;
            for (int y2 = liqMinY; y2 <= liqMaxY + 1 && y2 < 129; y2++)
                for (int x2 = liqMinX; x2 <= liqMaxX + 1 && x2 < 129; x2++)
                {
                    float hv = liquidHeight[y2, x2];
                    if (hv > NoLiq) { if (hv < liqMinH) liqMinH = hv; if (hv > liqMaxH) liqMaxH = hv; }
                }
            if (liqMinH > liqMaxH) liqMinH = liqMaxH = 0f;

            if (!liqFullType) liqHdrFlags |= LiquidMapHeader.NoType;
            if (liqMaxH - liqMinH < 0.001f) liqHdrFlags |= LiquidMapHeader.NoHeightValues;
        }

        // ── Step 3: Compute section offsets ───────────────────────────────
        const uint areaMapOffset  = 44u;
        const uint areaMapSize    = 8u + 256u * 2u;
        const uint heightMapOffset = areaMapOffset + areaMapSize;
        const uint heightMapSize   = 16u + (129u * 129u + 128u * 128u) * 4u;

        uint liquidMapOffset, liquidMapSize;
        if (!anyLiquid) { liquidMapOffset = 0u; liquidMapSize = 0u; }
        else
        {
            liquidMapOffset = heightMapOffset + heightMapSize;
            liquidMapSize   = 16u;
            if ((liqHdrFlags & LiquidMapHeader.NoType)         == 0) liquidMapSize += 512u + 256u;
            if ((liqHdrFlags & LiquidMapHeader.NoHeightValues) == 0) liquidMapSize += (uint)(liqW * liqH * 4);
        }
        uint holesOffset = anyLiquid ? liquidMapOffset + liquidMapSize : heightMapOffset + heightMapSize;
        const uint holesSize = 256u * 2u;

        // ── Step 4: Write file ────────────────────────────────────────────
        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        // Header (44 bytes)
        writer.Write(0x5350414Du);              // "MAPS"
        writer.Write(MagicBytes.MapMagicWotlk);
        writer.Write(WowConstants.TargetBuild);
        writer.Write(areaMapOffset);
        writer.Write(areaMapSize);
        writer.Write(heightMapOffset);
        writer.Write(heightMapSize);
        writer.Write(liquidMapOffset);
        writer.Write(liquidMapSize);
        writer.Write(holesOffset);
        writer.Write(holesSize);

        // Area section
        writer.Write(0x41455241u); // "AREA"
        writer.Write((ushort)0);   // flags
        writer.Write((ushort)0);   // gridArea
        for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
                writer.Write(tile.AreaMap != null ? tile.AreaMap[z * 16 + x] : (ushort)0);

        // Height section
        writer.Write(MagicBytes.HeightMapMagic);
        writer.Write(0u);
        writer.Write(tile.MinHeight);
        writer.Write(tile.MaxHeight);
        foreach (var h in BuildV9Heights(tile)) writer.Write(h);
        foreach (var h in BuildV8Heights(tile)) writer.Write(h);

        // Liquid section (only when liquid is present)
        if (anyLiquid)
        {
            writer.Write(MagicBytes.LiquidMapMagic);
            writer.Write(liqHdrFlags);
            writer.Write(liqFullType ? (ushort)0 : (ushort)liqBaseType);
            writer.Write((byte)liqMinX); writer.Write((byte)liqMinY);
            writer.Write(liqW); writer.Write(liqH);
            writer.Write(liqMinH);

            if ((liqHdrFlags & LiquidMapHeader.NoType) == 0)
            {
                for (int y2 = 0; y2 < 16; y2++)
                    for (int x2 = 0; x2 < 16; x2++) writer.Write(liquidEntry[y2, x2]);
                for (int y2 = 0; y2 < 16; y2++)
                    for (int x2 = 0; x2 < 16; x2++) writer.Write(liquidFlags[y2, x2]);
            }
            if ((liqHdrFlags & LiquidMapHeader.NoHeightValues) == 0)
            {
                for (int y2 = 0; y2 < liqH; y2++)
                    for (int x2 = 0; x2 < liqW; x2++)
                        writer.Write(liquidHeight[liqMinY + y2, liqMinX + x2]);
            }
        }

        // Holes section
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
            // PORT-001: store area flags (not raw IDs) so Recast gets the correct area type
            AreaMap = adt.GetAllAreaIds().ToArray().Select(id => unchecked((ushort)AdtFile.AreaIdToAreaFlags(id))).ToArray(),
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

        // BUG-005: populate per-chunk liquid data from MH2O
        var chunkLiquids = new MapLiquidCell[256];
        for (int i = 0; i < 256; i++)
        {
            ref readonly var l = ref adt.GetLiquidData(i);
            if (!l.HasLiquid) continue;
            chunkLiquids[i] = new MapLiquidCell
            {
                RawTypeId = l.RawTypeId,
                TypeFlags  = (byte)l.PrimaryType,
                MinHeight  = l.LiquidLevel,
                OffsetX    = l.OffsetX,
                OffsetY    = l.OffsetY,
                Width      = l.Width,
                Height     = l.Height,
                Heights    = l.DepthMap,
                ShowMask   = l.ShowMask
            };
        }
        tile.ChunkLiquids = chunkLiquids;

        return tile;
    }
}
