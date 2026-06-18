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

    public bool OutputFileExists(uint mapId, int tileX, int tileY)
        => File.Exists(Path.Combine(_outputDir, $"{mapId:D3}{tileY:D2}{tileX:D2}.map"));

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

        // MCLQ first (C++ System.cpp lignes 827-893). The C++ processes MCLQ before MH2O;
        // the final liquid_show is the OR of both, and the final liquid_height is MH2O when present.
        if (tile.ChunkMclqs != null)
        {
            for (int ci = 0; ci < Math.Min(256, tile.ChunkMclqs.Length); ci++)
            {
                ref readonly var liq = ref tile.ChunkMclqs[ci];
                if (!liq.HasLiquid) continue;
                int cr = ci / 16, cc = ci % 16;
                // MCLQ always covers the full chunk (OffsetX=OffsetY=0, Width=Height=8)
                // and uses cell->flags for liquid type (not DBC lookup). Don't overwrite
                // liquidEntry/liquidFlags here — MH2O will overwrite below if present.
                for (int ly = 0; ly < liq.Height; ly++)
                    for (int lx = 0; lx < liq.Width; lx++)
                    {
                        int bit = ly * liq.Width + lx;
                        if (liq.ShowMask != 0UL && ((liq.ShowMask >> bit) & 1UL) == 0UL) continue;
                        int gy = cr * 8 + ly, gx = cc * 8 + lx;
                        if ((uint)gy < 128 && (uint)gx < 128) liquidShow[gy, gx] = true;
                    }
                for (int ly = 0; ly <= liq.Height; ly++)
                    for (int lx = 0; lx <= liq.Width; lx++)
                    {
                        int gy = cr * 8 + ly, gx = cc * 8 + lx;
                        if ((uint)gy < 129 && (uint)gx < 129)
                        {
                            int idx = ly * (liq.Width + 1) + lx;
                            liquidHeight[gy, gx] = liq.Heights != null && idx < liq.Heights.Length
                                ? liq.Heights[idx] : liq.MinHeight;
                        }
                    }
            }
        }

        if (tile.ChunkLiquids != null)
        {
            for (int ci = 0; ci < Math.Min(256, tile.ChunkLiquids.Length); ci++)
            {
                ref readonly var liq = ref tile.ChunkLiquids[ci];
                if (!liq.HasLiquid) continue;
                int cr = ci / 16, cc = ci % 16;
                liquidEntry[cr, cc] = liq.RawTypeId;
                liquidFlags[cr, cc] = liq.TypeFlags;
                // P0 #6 FIX: dark water — faithful port of MaNGOS C++ System.cpp:
                //   if (LiqType[h->liquidType] == LIQUID_TYPE_OCEAN) {
                //       uint8* lm = h2o->getLiquidLightMap(h);
                //       if (!lm) liquid_flags[i][j] |= MAP_LIQUID_TYPE_DARK_WATER;
                //   }
                // LIQUID_TYPE_OCEAN == 0x02 in WotLK MAP_LIQUID_TYPE_* flags.
                if (liq.TypeFlags == 0x02 && !liq.HasLightmap)
                    liquidFlags[cr, cc] |= 0x10; // MAP_LIQUID_TYPE_DARK_WATER

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

            // P0 #4 FIX: base type = liquid_flags[0][0], compare ALL 256 (zeros included).
            // C++ System.cpp uses liquid_flags[0][0] as the base and breaks the moment
            // any other cell differs — including the zero cells of dry chunks. The
            // previous C# logic ignored zero values, which wrongly packed mixed
            // tiles (water + land) as MAP_LIQUID_NO_TYPE.
            liqBaseType = liquidFlags[0, 0];
            for (int y2 = 0; y2 < 16 && !liqFullType; y2++)
            {
                for (int x2 = 0; x2 < 16; x2++)
                {
                    if (liquidFlags[y2, x2] != liqBaseType)
                    {
                        liqFullType = true;
                        break;
                    }
                }
            }

            liqW = (byte)(liqMaxX - liqMinX + 2);
            liqH = (byte)(liqMaxY - liqMinY + 2);

            liqMinH = float.MaxValue; liqMaxH = float.MinValue;
            // C++ System.cpp: minHeight/maxHeight is computed ONLY over cells where
            // liquid_show[y][x] is true. Non-visible cells are set to CONF_use_minHeight
            // and excluded. The previous C# code filtered by `hv > NoLiq` which incorrectly
            // included non-visible cells that had been written by MCLQ or MH2O height fill
            // (those fills always write the 9x9 region regardless of the show mask).
            for (int y2 = liqMinY; y2 <= liqMaxY && y2 < 128; y2++)
                for (int x2 = liqMinX; x2 <= liqMaxX && x2 < 128; x2++)
                {
                    if (!liquidShow[y2, x2]) continue;
                    float hv = liquidHeight[y2, x2];
                    if (hv > NoLiq) { if (hv < liqMinH) liqMinH = hv; if (hv > liqMaxH) liqMaxH = hv; }
                }
            if (liqMinH > liqMaxH) liqMinH = liqMaxH = 0f;

            if (!liqFullType) liqHdrFlags |= LiquidMapHeader.NoType;
            // P1.2 FIX: match MaNGOS C++ exactly. With CONF_allow_float_to_int=false
            // (the default), MAP_LIQUID_NO_HEIGHT is only set when maxHeight == minHeight
            // (truly flat). The previous `< 0.001f` was a C# invention that broke
            // byte-for-byte compatibility with the C++ output.
            if (liqMaxH == liqMinH) liqHdrFlags |= LiquidMapHeader.NoHeightValues;
        }

        // ── Step 3: Compute section offsets ───────────────────────────────
        // P1.1 FIX: AREA packing — mirror MaNGOS C++ System.cpp:
        //   if all 256 area flags are equal, write only the 8-byte header
        //   with MAP_AREA_NO_AREA set and gridArea = the common value
        //   (saves 512 bytes per tile for zones that don't split).
        bool allSameArea = true;
        ushort firstArea = 0;
        if (tile.AreaMap != null && tile.AreaMap.Length >= 256)
        {
            firstArea = tile.AreaMap[0];
            for (int i = 1; i < 256; i++)
            {
                if (tile.AreaMap[i] != firstArea) { allSameArea = false; break; }
            }
        }
        ushort areaHdrFlags = allSameArea ? AreaMapHeader.NoArea : (ushort)0;
        ushort areaGridArea = allSameArea ? firstArea : (ushort)0;

        // P0 #5 FIX: MAP_HEIGHT_NO_HEIGHT when the tile is exactly flat
        // (maxHeight == minHeight). With CONF_allow_float_to_int=false (the C++
        // default), the C++ extractor skips the 132100-byte V9+V8 array and writes
        // only the 16-byte header — saving 128 KB per flat tile. The previous C#
        // code always wrote the full V9+V8, which broke byte-for-byte output.
        bool heightIsFlat = tile.MinHeight == tile.MaxHeight;
        uint heightHdrFlags = heightIsFlat ? HeightMapHeader.NoHeight : 0u;

        const uint areaMapOffset  = 44u;
        uint areaMapSize          = allSameArea ? AreaMapHeader.Size : (AreaMapHeader.Size + 256u * 2u);
        uint heightMapOffset      = areaMapOffset + areaMapSize;
        uint heightMapSize        = heightIsFlat ? HeightMapHeader.Size : (HeightMapHeader.Size + (129u * 129u + 128u * 128u) * 4u);

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

        // Area section (P1.1: packed when all 256 chunks share the same flag)
        writer.Write(0x41455241u); // "AREA"
        writer.Write(areaHdrFlags);
        writer.Write(areaGridArea);
        if ((areaHdrFlags & AreaMapHeader.NoArea) == 0)
        {
            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                    writer.Write(tile.AreaMap != null ? tile.AreaMap[z * 16 + x] : (ushort)0);
        }

        // Height section (P0 #5: header only when MAP_HEIGHT_NO_HEIGHT is set)
        writer.Write(MagicBytes.HeightMapMagic);
        writer.Write(heightHdrFlags);
        writer.Write(tile.MinHeight);
        writer.Write(tile.MaxHeight);
        if ((heightHdrFlags & HeightMapHeader.NoHeight) == 0)
        {
            foreach (var h in BuildV9Heights(tile)) writer.Write(h);
            foreach (var h in BuildV8Heights(tile)) writer.Write(h);
        }

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

        // P0 #5 FIX: apply the MaNGOS C++ CONF_use_minHeight clamp (-500) to every
        // V8/V9 value BEFORE computing min/max. C++ System.cpp:
        //   if (CONF_allow_height_limit && minHeight < CONF_use_minHeight) {
        //       for (...) if (V8[y][x] < CONF_use_minHeight) V8[y][x] = CONF_use_minHeight;
        //       for (...) if (V9[y][x] < CONF_use_minHeight) V9[y][x] = CONF_use_minHeight;
        //       if (minHeight < CONF_use_minHeight) minHeight = CONF_use_minHeight;
        //       if (maxHeight < CONF_use_minHeight) maxHeight = CONF_use_minHeight;
        //   }
        // This is what makes deep-ocean tiles (raw heights ~-515) collapse to
        // exactly -500, allowing MAP_HEIGHT_NO_HEIGHT to kick in. Without it,
        // every ocean tile loses the 128 KB height-pack optimization and the
        // C# output diverges byte-for-byte from the C++ reference.
        const float MinHeightClamp = -500.0f;

        // Faithful port of MaNGOS C++ System.cpp::ConvertADT: every MCVT vertex
        // produces exactly one V9 or V8 entry — no filtering, no skipping. The
        // previous `if (h > -9000f)` guard was a C# invention that broke
        // byte-for-byte parity whenever a single height was below -9000: the
        // resulting List<float> was shorter than 256 * AdtMcvt.TotalVertices
        // (37200), which then triggered the safety net in BuildV9Heights /
        // BuildV8Heights that fills the entire V9/V8 grid with 0f, silently
        // corrupting up to 128 KB of the output .map file.
        var heights = new float[256 * AdtMcvt.TotalVertices];
        for (int i = 0; i < 256; i++)
        {
            var chunkHeights = adt.GetChunkHeights(i);
            chunkHeights.CopyTo(heights.AsSpan(i * AdtMcvt.TotalVertices, AdtMcvt.TotalVertices));
            for (int j = 0; j < chunkHeights.Length; j++)
            {
                // Clamp first, then track min/max (matches C++ exactly).
                float ch = chunkHeights[j] < MinHeightClamp ? MinHeightClamp : chunkHeights[j];
                heights[i * AdtMcvt.TotalVertices + j] = ch;
                if (ch < tile.MinHeight) tile.MinHeight = ch;
                if (ch > tile.MaxHeight) tile.MaxHeight = ch;
            }
        }

        tile.HeightMap = heights;

        // P0 FIX: populate the per-chunk holes bitmask from the ADT's MCNK.holes
        // (was previously always 0). The C++ map-extractor writes
        // <c>map_fileheader.holes[i][j] = cell->holes</c> directly.
        var holes = new ushort[256];
        for (int i = 0; i < 256; i++)
            holes[i] = adt.GetChunkHoles(i);
        tile.HolesMap = holes;

        // BUG-005: populate per-chunk liquid data from MH2O
        var chunkLiquids = new MapLiquidCell[256];
        for (int i = 0; i < 256; i++)
        {
            ref readonly var l = ref adt.GetLiquidData(i);
            if (!l.HasLiquid) continue;
            chunkLiquids[i] = new MapLiquidCell
            {
                RawTypeId    = l.RawTypeId,
                TypeFlags     = (byte)l.PrimaryType,
                MinHeight     = l.LiquidLevel,
                OffsetX       = l.OffsetX,
                OffsetY       = l.OffsetY,
                Width         = l.Width,
                Height        = l.Height,
                Heights       = l.DepthMap,
                ShowMask      = l.ShowMask,
                VertexFormat  = l.VertexFormat,
                OfsInfoMask   = l.OfsInfoMask
            };
        }
        tile.ChunkLiquids = chunkLiquids;

        // MCLQ legacy path (MaNGOS C++ System.cpp lignes 827-893). MCLQ is still embedded
        // in some WotLK MCNK chunks. The C++ processes it BEFORE MH2O and the final
        // liquid_show is the OR of both — without this, the bounding box misses cells
        // covered only by MCLQ (e.g. tile 32,48 chunk (15,5) where minX drops from 47 to 48).
        var chunkMclqs = new MapLiquidCell[256];
        bool anyMclq = false;
        for (int i = 0; i < 256; i++)
        {
            ref readonly var l = ref adt.GetMclqData(i);
            if (!l.HasLiquid) continue;
            anyMclq = true;
            chunkMclqs[i] = new MapLiquidCell
            {
                RawTypeId    = l.RawTypeId,
                TypeFlags     = (byte)l.PrimaryType,
                MinHeight     = l.LiquidLevel,
                OffsetX       = l.OffsetX,
                OffsetY       = l.OffsetY,
                Width         = l.Width,
                Height        = l.Height,
                Heights       = l.DepthMap,
                ShowMask      = l.ShowMask,
                VertexFormat  = l.VertexFormat,
                OfsInfoMask   = l.OfsInfoMask
            };
        }
        tile.ChunkMclqs = anyMclq ? chunkMclqs : null;

        return tile;
    }
}
