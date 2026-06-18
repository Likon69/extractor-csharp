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

    // BUG-008: C++ System.cpp::ConvertADT declares liquid_height[129][129] as a
    // GLOBAL — it is zero-initialized at program start (.bss) and NEVER reset
    // between tiles. The C++ only resets the 128x128 in-bounds region to
    // CONF_use_minHeight for non-show cells. Cells at index >= 128 keep stale
    // values from previous tiles' MH2O/MCLQ writes (out-of-bounds writes that
    // land at indices 128..255 in the C++ 2D array layout).
    // To match byte-for-byte, the C# must:
    //   1. Keep liquidHeight persistent across tiles (static)
    //   2. Only reset the 128x128 in-bounds region (matching C++ reset loop)
    //   3. Process tiles in the same order as the C++ reference binary
    private static readonly float[,] s_liquidHeight = new float[256, 256];
    private static bool s_liquidHeightInitialized = false;

    // BUG-009: C++ System.cpp::ConvertADT declares V8[128][128] and V9[129][129]
    // as GLOBALS (System.cpp:516-517) — zero-initialized at program start (.bss)
    // and NEVER reset between tiles (no memset in ConvertADT). Each present chunk
    // overwrites its 9x9 (V9) / 8x8 (V8) footprint; absent chunks (C++ !cell at
    // System.cpp:622-625) leave the previous tile's values in place. The
    // minHeight/maxHeight of the height header (offset 0x23C/0x240) is the min/max
    // over ALL of V8/V9 (System.cpp:693-723), so any border tile with an absent
    // chunk diverges unless this stale-data behavior is reproduced exactly.
    // This is the same class of bug as BUG-008 (liquid_height), now applied to
    // the terrain height grids. Tile iteration order is already identical to C++
    // (WdtReader.GetExistingTiles = y-outer/x-inner, MapExtractorService sequential).
    private static readonly float[,] s_V8 = new float[128, 128];
    private static readonly float[,] s_V9 = new float[129, 129];
    private static bool s_heightGridInitialized = false;

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
        // P0 #8 FIX: MaNGOS C++ System.cpp uses 128x128 liquid_show and 129x129
        // liquid_height arrays, then writes to indices >= 128 (out of bounds!)
        // when an MH2O chunk has yOffset/xOffset that pushes the liquid past the
        // tile edge. The C++ relies on undefined behavior here — the write
        // lands in adjacent memory, producing a "spurious" liquid section of
        // up to ~32 KB that the C# must replicate for byte-for-byte parity.
        // Solution: use 256x256 arrays and skip the bounds check entirely.
        // P0 #9 FIX: C++ liquid_height is a GLOBAL array — NOT memset'd — so it's
        // zero-initialized (0.0f). The C# was initializing to -500.0f everywhere,
        // which produced wrong values at indices >= 128 (the last row of the
        // bounding box write, which is NOT covered by the reset loop y < 128).
        var liquidShow   = new bool[256, 256];
        const float NoLiq = -500f;
        // BUG-008: C++ liquid_height is a GLOBAL array, zero-initialized at program
        // start, NEVER reset between tiles. The C# must use a static array to match
        // this behavior — otherwise the 40 tiles with OOB tail diffs lose their
        // stale data from previous tiles' MH2O writes.
        if (!s_liquidHeightInitialized)
        {
            Array.Clear(s_liquidHeight, 0, s_liquidHeight.Length);
            s_liquidHeightInitialized = true;
        }
        var liquidHeight = s_liquidHeight;

        // MCLQ first (C++ System.cpp lignes 827-893). The C++ processes MCLQ before MH2O;
        // the final liquid_show is the OR of both, and the final liquid_height is MH2O when present.
        if (tile.ChunkMclqs != null)
        {
            for (int ci = 0; ci < Math.Min(256, tile.ChunkMclqs.Length); ci++)
            {
                ref readonly var liq = ref tile.ChunkMclqs[ci];
                if (!liq.HasLiquid) continue;
                int cr = ci / 16, cc = ci % 16;
                // C++ System.cpp:862-877 — set liquidEntry and liquidFlags from MCNK flags
                // (parsed in ParseMclq: RawTypeId = 1/2/3, TypeFlags = 0x08/0x02/0x01 + optional 0x10 DARK_WATER).
                // MH2O will overwrite below if present, matching the C++ MH2O-overwrites-MCLQ behavior.
                liquidEntry[cr, cc] = liq.RawTypeId;
                liquidFlags[cr, cc] = liq.TypeFlags;
                // MCLQ always covers the full chunk (OffsetX=OffsetY=0, Width=Height=8)
                for (int ly = 0; ly < liq.Height; ly++)
                    for (int lx = 0; lx < liq.Width; lx++)
                    {
                        int bit = ly * liq.Width + lx;
                        if (liq.ShowMask != 0UL && ((liq.ShowMask >> bit) & 1UL) == 0UL) continue;
                        int gy = cr * 8 + ly, gx = cc * 8 + lx;
                        // P0 #8: no bounds check — C++ writes out of bounds
                        liquidShow[gy, gx] = true;
                    }
                for (int ly = 0; ly <= liq.Height; ly++)
                    for (int lx = 0; lx <= liq.Width; lx++)
                    {
                        int gy = cr * 8 + ly, gx = cc * 8 + lx;
                        // P0 #8: no bounds check — C++ writes out of bounds
                        int idx = ly * (liq.Width + 1) + lx;
                        liquidHeight[gy, gx] = liq.Heights != null && idx < liq.Heights.Length
                            ? liq.Heights[idx] : liq.MinHeight;
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
                        // P0 #8: no bounds check — C++ writes out of bounds
                        liquidShow[gy, gx] = true;
                    }

                for (int ly = 0; ly <= liq.Height; ly++)
                    for (int lx = 0; lx <= liq.Width; lx++)
                    {
                        int gy = cr * 8 + liq.OffsetY + ly, gx = cc * 8 + liq.OffsetX + lx;
                        // P0 #8: no bounds check — C++ writes out of bounds
                        int idx = ly * (liq.Width + 1) + lx;
                        float hv = liq.Heights != null && idx < liq.Heights.Length
                            ? liq.Heights[idx] : liq.MinHeight;
                        liquidHeight[gy, gx] = hv;
                    }
            }
        }

        // ── Step 2: Analyse liquid data ───────────────────────────────────
        bool anyLiquid = false;
        for (int y2 = 0; y2 < 16 && !anyLiquid; y2++)
            for (int x2 = 0; x2 < 16; x2++)
                if (liquidFlags[y2, x2] != 0) { anyLiquid = true; break; }

        // P0 #8: C++ starts minX/minY at 255, maxX/maxY at 0 — and the scan loop
        // runs to ADT_GRID_SIZE (128), so out-of-bounds show cells at index >= 128
        // CAN contribute to the bounding box. We replicate this by scanning the full
        // 256x256 array.
        int liqMinX = 255, liqMinY = 255, liqMaxX = 0, liqMaxY = 0;
        byte liqBaseType = 0;
        bool liqFullType = false;
        float liqMinH = 0f, liqMaxH = 0f;
        ushort liqHdrFlags = 0;
        int liqW = 1, liqH = 1;

        if (anyLiquid)
        {
            for (int y2 = 0; y2 < 256; y2++)
                for (int x2 = 0; x2 < 256; x2++)
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

            // P0 #8: C++ casts to uint8 — if maxX-minX+2 > 255 the byte wraps. The
            // resulting W/H is still correct mod 256 for the file size, but the C#
            // must use int to avoid signed overflow. Width/Height are stored as bytes
            // in the .map header (offset 12-13 of the MLIQ sub-header), so the C++
            // truncation is part of the file format.
            int liqW_i = liqMaxX - liqMinX + 2;
            int liqH_i = liqMaxY - liqMinY + 2;
            liqW = (byte)liqW_i;
            liqH = (byte)liqH_i;

            // P0 #7 FIX: MaNGOS C++ System.cpp::ConvertADT (lines 1020-1043) sets
            // liquid_height[y][x] = CONF_use_minHeight (-500) for EVERY cell where
            // liquid_show[y][x] is false, across the whole 128x128 grid. Then the
            // write loop (line 1165) just dumps the bounding box as-is. The previous
            // C# code only filtered non-show cells for the min/max computation but
            // still wrote the actual MH2O/MCLQ heights for them — producing wrong
            // terrain "spikes" wherever a chunk's MH2O sub-region extended past the
            // show mask. 2259-byte diff on tile 32,48 (Azeroth) was this bug.
            // P0 #8: the C++ scan loop is `y < ADT_GRID_SIZE` = 128, so it ONLY resets
            // the 128x128 in-bounds region. Out-of-bounds cells (y >= 128) keep the
            // MH2O-filled heights and are written as-is into the bounding box. This
            // is the source of the "spurious" 32 KB tail in the C++ output that the
            // C# must replicate.
            for (int y2 = 0; y2 < 128; y2++)
                for (int x2 = 0; x2 < 128; x2++)
                    if (!liquidShow[y2, x2])
                        liquidHeight[y2, x2] = NoLiq;

            liqMinH = float.MaxValue; liqMaxH = float.MinValue;
            // P0 #8: bounding box can exceed 128 (e.g. minY=129 for tile 32,48),
            // so the scan must run up to liqMaxY/liqMaxX without the 128 cap.
            for (int y2 = liqMinY; y2 <= liqMaxY; y2++)
                for (int x2 = liqMinX; x2 <= liqMaxX; x2++)
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

        // ── Step 2b: Reconstruct global V8/V9 exactly like C++ System.cpp ──
        // BUG-009: V8[128][128] and V9[129][129] are C++ globals (System.cpp:516-517),
        // never reset between tiles. Each present chunk overwrites its footprint
        // (init = ypos, then += MCVT when OfsHeight > 0); absent chunks leave the
        // previous tile's values in place. The height header minHeight/maxHeight
        // (offset 0x23C/0x240) is the min/max over ALL of V8/V9 (System.cpp:693-749),
        // scanned BEFORE the -500 clamp is applied to the grids. This replaces the
        // per-chunk min/max that FromAdtTile computed (which ignored stale data and
        // thus diverged on border tiles with absent chunks — the source of the
        // "1-ULP" diffs on the header and the 623-byte diff on 0004836.map).
        RebuildHeightGrids(tile);
        (tile.MinHeight, tile.MaxHeight) = ComputeHeightMinMax();

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
        // BUG-009: V9 then V8 are written from the persistent global grids
        // (s_V9/s_V8), which already have the -500 clamp applied during
        // RebuildHeightGrids — matching C++ System.cpp:729-748 (clamp mutates the
        // globals in place before fwrite at System.cpp:1147-1148).
        writer.Write(MagicBytes.HeightMapMagic);
        writer.Write(heightHdrFlags);
        writer.Write(tile.MinHeight);
        writer.Write(tile.MaxHeight);
        if ((heightHdrFlags & HeightMapHeader.NoHeight) == 0)
        {
            // V9: 129x129 (System.cpp:1147 fwrite(V9, sizeof(V9), 1, output))
            for (int y = 0; y <= 128; y++)
                for (int x = 0; x <= 128; x++)
                    writer.Write(s_V9[y, x]);
            // V8: 128x128 (System.cpp:1148 fwrite(V8, sizeof(V8), 1, output))
            for (int y = 0; y < 128; y++)
                for (int x = 0; x < 128; x++)
                    writer.Write(s_V8[y, x]);
        }

        // Liquid section (only when liquid is present)
        if (anyLiquid)
        {
            writer.Write(MagicBytes.LiquidMapMagic);
            writer.Write(liqHdrFlags);
            writer.Write(liqFullType ? (ushort)0 : (ushort)liqBaseType);
            writer.Write((byte)liqMinX); writer.Write((byte)liqMinY);
            writer.Write((byte)liqW); writer.Write((byte)liqH);
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

    /// <summary>
    /// BUG-009: Reconstructs the global V8/V9 grids exactly like C++ System.cpp
    /// ConvertADT (System.cpp:618-687). V8[128][128] / V9[129][129] are C++ globals
    /// never reset between tiles — present chunks overwrite their 9x9 (V9) / 8x8 (V8)
    /// footprint, absent chunks leave the previous tile's values in place. Each chunk
    /// first sets V9/V8 = MCNK.Ypos, then adds the MCVT deltas when OfsHeight > 0.
    /// The pre-computed <paramref name="tile"/>.HeightMap already holds Ypos+MCVT per
    /// vertex (AdtParser.cs:664 = the same ADDSS the C++ emits), so we write it
    /// directly; for chunks present without MCVT we write Ypos alone.
    /// </summary>
    private static void RebuildHeightGrids(MapTile tile)
    {
        // One-time zero-init mirroring the C++ .bss zero-initialization at program start.
        if (!s_heightGridInitialized)
        {
            Array.Clear(s_V8, 0, s_V8.Length);
            Array.Clear(s_V9, 0, s_V9.Length);
            s_heightGridInitialized = true;
        }

        // ADT_CELL_SIZE = 8, ADT_CELLS_PER_GRID = 16, ADT_GRID_SIZE = 128.
        // Faithful port of System.cpp:618-687 (loops over i=cells-y, j=cells-x).
        for (int i = 0; i < 16; i++)          // C++ outer: ADT_CELLS_PER_GRID
        {
            for (int j = 0; j < 16; j++)      // C++ inner: ADT_CELLS_PER_GRID
            {
                int chunkIndex = i * 16 + j;

                // C++ System.cpp:622-625 — `adt_MCNK* cell = cells->getMCNK(i, j);
                // if (!cell) continue;` Absent chunk → leave the global grids untouched
                // (the stale data from the previous tile is what the C++ writes).
                if (tile.ChunkPresent == null || !tile.ChunkPresent[chunkIndex])
                    continue;

                float ypos = tile.ChunkYpos != null ? tile.ChunkYpos[chunkIndex] : 0f;
                bool hasMcvt = tile.ChunkHasMcvt != null && tile.ChunkHasMcvt[chunkIndex];
                ReadOnlySpan<float> chunkData = tile.HeightMap != null
                    ? tile.HeightMap.AsSpan(chunkIndex * AdtMcvt.TotalVertices, AdtMcvt.TotalVertices)
                    : ReadOnlySpan<float>.Empty;

                // C++ System.cpp:644-661 — "Set map height as grid height":
                //   for y in 0..ADT_CELL_SIZE for x in 0..ADT_CELL_SIZE
                //       V9[cy][cx] = cell->ypos;
                //   for y in 0..ADT_CELL_SIZE-1 for x in 0..ADT_CELL_SIZE-1
                //       V8[cy][cx] = cell->ypos;
                // (ADT_CELL_SIZE = 8, so V9 covers 9x9 inclusive, V8 covers 8x8.)
                for (int y = 0; y <= 8; y++)
                    for (int x = 0; x <= 8; x++)
                        s_V9[i * 8 + y, j * 8 + x] = ypos;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        s_V8[i * 8 + y, j * 8 + x] = ypos;

                // C++ System.cpp:663-687 — `adt_MCVT* v = cell->getMCVT(); if (!v) continue;`
                // Then V9 += height_map[...] and V8 += height_map[...]. When OfsHeight==0
                // there is no MCVT and the grids stay at ypos (above). chunkData already
                // contains Ypos+MCVT (AdtParser.cs:664), which is exactly ypos+delta — the
                // same ADDSS result the C++ produces — so we write it directly.
                if (!hasMcvt || chunkData.IsEmpty)
                    continue;

                // V9: 9x9 outer vertices (System.cpp:668-677)
                for (int y = 0; y <= 8; y++)
                    for (int x = 0; x <= 8; x++)
                        s_V9[i * 8 + y, j * 8 + x] = AdtMcvt.GetV9(chunkData, y, x);
                // V8: 8x8 inner vertices (System.cpp:678-687)
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        s_V8[i * 8 + y, j * 8 + x] = AdtMcvt.GetV8(chunkData, y, x);
            }
        }
    }

    /// <summary>
    /// BUG-009: min/max scan over the global V8/V9 grids exactly like C++ System.cpp
    /// 693-723 (pre-clamp), followed by the -500 clamp block (System.cpp:727-748) which
    /// mutates the grids in place AND clamps the returned min/max. Returns (minHeight,
    /// maxHeight) = the values stored in map_heightHeader.gridHeight / gridMaxHeight.
    /// CONF_allow_height_limit defaults to true, CONF_use_minHeight = -500.0f.
    /// </summary>
    private static (float Min, float Max) ComputeHeightMinMax()
    {
        // C++ System.cpp:693-694: maxHeight = -20000; minHeight = 20000;
        float maxHeight = -20000f;
        float minHeight = 20000f;

        // V8 first (System.cpp:695-709), 128x128.
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                float h = s_V8[y, x];
                if (maxHeight < h) maxHeight = h;
                if (minHeight > h) minHeight = h;
            }
        // V9 next (System.cpp:710-724), 129x129 inclusive.
        for (int y = 0; y <= 128; y++)
            for (int x = 0; x <= 128; x++)
            {
                float h = s_V9[y, x];
                if (maxHeight < h) maxHeight = h;
                if (minHeight > h) minHeight = h;
            }

        // C++ System.cpp:727-748 — apply the -500 clamp when the pre-clamp minHeight is
        // below -500. This mutates the global grids IN PLACE so the subsequent fwrite
        // (System.cpp:1147-1148) dumps the clamped values, and also clamps the header
        // minHeight/maxHeight themselves.
        const float MinHeightClamp = -500.0f;
        if (minHeight < MinHeightClamp)
        {
            for (int y = 0; y < 128; y++)
                for (int x = 0; x < 128; x++)
                    if (s_V8[y, x] < MinHeightClamp)
                        s_V8[y, x] = MinHeightClamp;
            for (int y = 0; y <= 128; y++)
                for (int x = 0; x <= 128; x++)
                    if (s_V9[y, x] < MinHeightClamp)
                        s_V9[y, x] = MinHeightClamp;
            if (minHeight < MinHeightClamp) minHeight = MinHeightClamp;
            if (maxHeight < MinHeightClamp) maxHeight = MinHeightClamp;
        }

        return (minHeight, maxHeight);
    }

    public static MapTile FromAdtTile(AdtFile adt)
    {
        var tile = new MapTile(adt.MapId, adt.TileX, adt.TileY)
        {
            // PORT-001: store area flags (not raw IDs) so Recast gets the correct area type
            AreaMap = adt.GetAllAreaIds().ToArray().Select(id => unchecked((ushort)AdtFile.AreaIdToAreaFlags(id))).ToArray(),
            // BUG-009: MinHeight/MaxHeight are now recomputed in WriteTileSync from the
            // reconstructed global V8/V9 grids (ComputeHeightMinMax), which correctly
            // accounts for stale data on absent chunks and applies the -500 clamp in the
            // exact C++ order. The placeholders here are overwritten before the file is
            // written, so the values set here are irrelevant.
            MinHeight = float.MaxValue,
            MaxHeight = float.MinValue
        };

        // BUG-009: store the per-chunk MCNK metadata that RebuildHeightGrids needs to
        // reconstruct the global V8/V9 grids exactly like C++ System.cpp. The min/max
        // clamp is NO LONGER applied here — RebuildHeightGrids writes unclamped Ypos+MCVT
        // into the globals (matching C++ System.cpp:644-687), then ComputeHeightMinMax
        // applies the -500 clamp in place after the scan (System.cpp:727-748). Applying
        // the clamp here would corrupt the global grids: deep-ocean tiles would write
        // -500 into V8/V9 unconditionally, and the next tile's stale data would inherit
        // -500 instead of the real previous-tile heights.

        // Every MCVT vertex produces exactly one V9 or V8 entry — no filtering, no
        // skipping. The previous `if (h > -9000f)` guard was a C# invention that broke
        // byte-for-byte parity whenever a single height was below -9000. We store the
        // RAW Ypos+MCVT values here (no clamp); the clamp happens later in
        // ComputeHeightMinMax to match C++ ordering.
        var heights = new float[256 * AdtMcvt.TotalVertices];
        var chunkYpos = new float[256];
        var chunkHasMcvt = new bool[256];
        var chunkPresent = new bool[256];
        for (int i = 0; i < 256; i++)
        {
            chunkPresent[i] = adt.GetChunkPresent(i);
            chunkYpos[i] = adt.GetChunkYpos(i);
            chunkHasMcvt[i] = adt.GetChunkHasMcvt(i);

            var chunkHeights = adt.GetChunkHeights(i);
            chunkHeights.CopyTo(heights.AsSpan(i * AdtMcvt.TotalVertices, AdtMcvt.TotalVertices));
        }

        tile.HeightMap = heights;
        tile.ChunkYpos = chunkYpos;
        tile.ChunkHasMcvt = chunkHasMcvt;
        tile.ChunkPresent = chunkPresent;

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
                OfsInfoMask   = l.OfsInfoMask,
                OfsHeightMap  = l.OfsHeightMap
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
                OfsInfoMask   = l.OfsInfoMask,
                OfsHeightMap  = l.OfsHeightMap
            };
        }
        tile.ChunkMclqs = anyMclq ? chunkMclqs : null;

        return tile;
    }
}
