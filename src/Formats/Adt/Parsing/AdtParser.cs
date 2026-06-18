using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Binary;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;
using MaNGOS.Extractor.Formats.Adt.Models;

namespace MaNGOS.Extractor.Formats.Adt.Parsing;

public sealed class AdtParser
{
    private readonly IArchiveReader _archive;
    private readonly ILogger<AdtParser> _logger;
    private readonly ConcurrentDictionary<string, AdtFile> _cache;

    public AdtParser(IArchiveReader archive, ILogger<AdtParser> logger)
    {
        _archive = archive;
        _logger = logger;
        _cache = new ConcurrentDictionary<string, AdtFile>(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<AdtParseResult> ParseAsync(string path, uint mapId, int tileX, int tileY, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(path, out var cached))
            return new AdtParseResult(cached, new List<string>());

        if (!_archive.TryReadFile(path, out ReadOnlyMemory<byte> data))
        {
            _logger.LogWarning("ADT not found: {Path}", path);
            return AdtParseResult.Failed(new List<string> { $"ADT not found: {path}" });
        }

        AdtParseResult result;
        try
        {
            result = await Task.Run(() => ParseInternal(path, mapId, tileX, tileY, data, ct), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse ADT {Path}", path);
            return AdtParseResult.Failed(new List<string> { $"Parse error on {path}: {ex.Message}" });
        }

        if (result.Success && result.Tile != null)
            _cache[path] = result.Tile;

        return result;
    }

    private AdtParseResult ParseInternal(string path, uint mapId, int tileX, int tileY, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var warnings = new List<string>();
        var reader = new SpanReader(data);

        if (data.Length < 12)
        {
            warnings.Add($"ADT file too small: {path} ({data.Length} bytes)");
            return AdtParseResult.Failed(warnings);
        }

        // First 4 bytes are the MVER chunk magic, stored REVERSED on disk
        // (MaNGOS adtfile.cpp::flipcc). Flip before comparing.
        uint magic = ReverseChunkMagic(reader.ReadUInt32());
        if (magic != MagicBytes.Mver)
        {
            warnings.Add($"Invalid ADT magic: expected MVER (0x{MagicBytes.Mver:X}), got 0x{magic:X}");
            return AdtParseResult.Failed(warnings);
        }
        reader.Skip(8);   // skip MVER size (4) + MVER version data (4)

        var mhdrOffset = 0u;
        var mhdrSize = 0u;

        bool stopRootScan = false;

        while (reader.Remaining >= 8 && !stopRootScan)
        {
            // ADT files store chunk magics REVERSED on disk (MaNGOS
            // adtfile.cpp::flipcc reverses each 4-byte magic after reading).
            // Read 4 bytes, then byte-swap to get canonical "ABCD" order
            // before comparing against MagicBytes constants.
            uint chunkMagic = ReverseChunkMagic(reader.ReadUInt32());
            uint chunkSize = reader.ReadUInt32();

            switch (chunkMagic)
            {
                case MagicBytes.Mhdr:
                    mhdrOffset = (uint)reader.Position;
                    mhdrSize = chunkSize;
                    if (chunkSize > int.MaxValue || chunkSize > (uint)reader.Remaining)
                    {
                        warnings.Add($"Invalid chunk size for {MagicBytes.FourCCToString(chunkMagic)}: {chunkSize} (remaining={reader.Remaining})");
                        stopRootScan = true;
                        break;
                    }
                    reader.Skip((int)chunkSize);
                    break;

                case MagicBytes.Mtex:
                case MagicBytes.Mmdx:
                case MagicBytes.Mmid:
                case MagicBytes.Mwmo:
                case MagicBytes.Mwid:
                case MagicBytes.Mddf:
                case MagicBytes.Modf:
                case MagicBytes.Mfbo:
                case MagicBytes.Mh2o:
                    if (chunkSize > int.MaxValue || chunkSize > (uint)reader.Remaining)
                    {
                        warnings.Add($"Invalid chunk size for {MagicBytes.FourCCToString(chunkMagic)}: {chunkSize} (remaining={reader.Remaining})");
                        stopRootScan = true;
                        break;
                    }
                    reader.Skip((int)chunkSize);
                    break;

                default:
                    if (chunkMagic == MagicBytes.Mcnk)
                    {
                        if (chunkSize > int.MaxValue || chunkSize > (uint)reader.Remaining)
                        {
                            warnings.Add($"Invalid chunk size for {MagicBytes.FourCCToString(chunkMagic)}: {chunkSize} (remaining={reader.Remaining})");
                            stopRootScan = true;
                            break;
                        }
                        reader.Skip((int)chunkSize);
                    }
                    else
                    {
                        warnings.Add($"Unknown chunk at root level: {MagicBytes.FourCCToString(chunkMagic)}");
                        if (chunkSize > int.MaxValue || chunkSize > (uint)reader.Remaining)
                        {
                            warnings.Add($"Invalid chunk size for {MagicBytes.FourCCToString(chunkMagic)}: {chunkSize} (remaining={reader.Remaining})");
                            stopRootScan = true;
                            break;
                        }
                        reader.Skip((int)chunkSize);
                    }
                    break;
            }

            reader.Align4();
        }

        if (mhdrOffset == 0)
        {
            warnings.Add("MHDR chunk not found");
            return AdtParseResult.Failed(warnings);
        }

        reader.Seek((int)mhdrOffset);
        var header = new AdtMhdr
        {
            Flags = reader.ReadUInt32(),
            McinOffset = reader.ReadUInt32(),
            MtexOffset = reader.ReadUInt32(),
            MmdxOffset = reader.ReadUInt32(),
            MmidOffset = reader.ReadUInt32(),
            MwmoOffset = reader.ReadUInt32(),
            MwidOffset = reader.ReadUInt32(),
            MddfOffset = reader.ReadUInt32(),
            ModfOffset = reader.ReadUInt32(),
            MfboOffset = reader.ReadUInt32(),
            Mh2oOffset = reader.ReadUInt32(),
            MtxpOffset = reader.ReadUInt32(),
            MtxpSize = reader.ReadUInt32(),
            Unused1 = reader.ReadUInt32(),
            Unused2 = reader.ReadUInt32(),
            Unused3 = reader.ReadUInt32()
        };

        if (header.McinOffset == 0 || header.MtexOffset == 0)
        {
            warnings.Add("Invalid MHDR: missing required chunk offsets");
            return AdtParseResult.Failed(warnings);
        }

        // MHDR offsets are relative to &flags (= mhdrOffset), not file start.
        // C++ confirms: getMCIN() returns (uint8*)&flags + offsMCIN.
        string[] textures = header.MtexOffset > 0 ? ParseTextures(reader, mhdrOffset + header.MtexOffset) : Array.Empty<string>();

        string[] wmos = ParseWmoNames(reader, mhdrOffset + header.MwmoOffset, mhdrOffset + header.MwidOffset);
        string[] models = ParseModelNames(reader, mhdrOffset + header.MmdxOffset, mhdrOffset + header.MmidOffset);

        AdtMfbo? mfbo = header.MfboOffset > 0 ? ParseMfbo(reader, mhdrOffset + header.MfboOffset) : null;

        AdtMcin[] mcinEntries = header.McinOffset > 0 ? ParseMcin(reader, mhdrOffset + header.McinOffset) : Array.Empty<AdtMcin>();

        AdtMh2o? waterData = header.Mh2oOffset > 0 ? ParseMh2o(reader, mhdrOffset + header.Mh2oOffset) : null;

        float[] allHeights = new float[256 * AdtMcvt.TotalVertices];
        ushort[] areaIds = new ushort[256];
        LiquidData[] liquids = new LiquidData[256];
        LiquidData[] mclqs = new LiquidData[256]; // MCLQ fallback (C++ System.cpp lignes 827-893)
        ushort[] chunkHoles = new ushort[256]; // P0 FIX: MCNK.holes per chunk (mirrors C++ map_fileheader.holes[16][16])

        // Parse liquid data for all 256 cells from MH2O chunk
        if (waterData.HasValue && mcinEntries.Length == 256)
        {
            uint mh2oBase = waterData.Value.ChunkDataStart;
            for (int i = 0; i < 256; i++)
            {
                var cell = waterData.Value.Cells[i];
                if (!cell.HasData)
                    continue;

                uint instanceOffset = mh2oBase + cell.HeaderOffset;
                liquids[i] = ParseMh2oCellData(reader, instanceOffset, mh2oBase, cell);
            }
        }

        for (int i = 0; i < 256; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (i >= mcinEntries.Length)
                continue;

            var mcnkEntry = mcinEntries[i];
            if (mcnkEntry.Offset == 0 || mcnkEntry.Size == 0)
            {
                continue;
            }

            LiquidData? cellWater = i < liquids.Length && liquids[i].HasLiquid ? liquids[i] : null;

            var chunkResult = ParseMcnk(reader, (int)mcnkEntry.Offset, (int)mcnkEntry.Size, cellWater, warnings);

            var heightSpan = allHeights.AsSpan(i * AdtMcvt.TotalVertices, AdtMcvt.TotalVertices);
            chunkResult.Heights.CopyTo(heightSpan);
            areaIds[i] = chunkResult.AreaId;
            liquids[i] = chunkResult.Liquid;
            // MCLQ : C++ System.cpp traite MCLQ pour TOUS les chunks (puis MH2O écrase).
            // Le C# doit aussi stocker MCLQ même si MH2O a des données, car MCLQ peut
            // ajouter des cellules visibles que MH2O n'a pas (ex: tile 32,48 chunk (15,5)).
            if (chunkResult.Mclq.HasLiquid)
                mclqs[i] = chunkResult.Mclq;
            chunkHoles[i] = chunkResult.Holes;
        }

        var mddf = header.MddfOffset > 0 ? ParseMddf(reader, mhdrOffset + header.MddfOffset) : Array.Empty<AdtMddf>();
        var modf = header.ModfOffset > 0 ? ParseModf(reader, mhdrOffset + header.ModfOffset) : Array.Empty<AdtModf>();

        // Parse texture IDs for each MCNK from MCLY chunks
        var chunkTextureIds = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            if (i < mcinEntries.Length && mcinEntries[i].Offset > 0)
            {
                chunkTextureIds[i] = ParseMcnkRoadOrPrimaryTexture(reader, (int)mcinEntries[i].Offset, textures);
            }
        }

        var tile = new AdtFile(
            mapId, tileX, tileY, path, header, mcinEntries, mfbo,
            textures, wmos, models, mddf, modf,
            allHeights, areaIds, liquids, mclqs, chunkTextureIds, chunkHoles);

        return new AdtParseResult(tile, warnings);
    }

    private LiquidData ParseMh2oCellData(SpanReader reader, uint instanceOffset, uint mh2oDataBase, in AdtMh2oCell cell)
    {
        if (!cell.HasData || instanceOffset == 0)
            return LiquidData.Empty;

        reader.Seek((int)instanceOffset);

        // Read SLiquidInstance in exact WotLK binary order (24 bytes total)
        // C++ adt_liquid_header has: ... width, height, offsData2a (show mask), offsData2b (height map).
        // CRITICAL: offsData2a (show mask) comes FIRST, offsData2b (height map) comes SECOND.
        ushort liquidType  = reader.ReadUInt16();
        ushort vertexFormat = reader.ReadUInt16();
        float  minHeight   = reader.ReadFloat();
        float  maxHeight   = reader.ReadFloat();
        byte   offsetX     = reader.ReadByte();
        byte   offsetY     = reader.ReadByte();
        byte   width       = reader.ReadByte();
        byte   height      = reader.ReadByte();
        uint   ofsInfoMask  = reader.ReadUInt32();  // offsData2a — 64-bit show mask
        uint   ofsHeightMap = reader.ReadUInt32();  // offsData2b — height float array

        if (width == 0 || height == 0)
            return LiquidData.Empty;

        var liquid = new LiquidData
        {
            LiquidLevel  = minHeight,
            RawTypeId    = liquidType,
            PrimaryType  = AdtFile.LiquidTypeToFlags(liquidType),
            VertexFormat = vertexFormat,
            OfsInfoMask  = ofsInfoMask,
            OffsetX      = offsetX,
            OffsetY      = offsetY,
            Width        = width,
            Height       = height
        };

        // Read 64-bit show mask (8 bytes = 64 bits, bit[y*Width+x] = sub-cell visible)
        if (ofsInfoMask != 0)
        {
            reader.Seek((int)(mh2oDataBase + ofsInfoMask));
            ulong mask = 0;
            for (int b = 0; b < 8; b++)
                mask |= (ulong)reader.ReadByte() << (b * 8);
            liquid.ShowMask = mask;
            if ((instanceOffset & 0xFFFF) < 0x10) Console.Error.WriteLine($"[DBG-MH2O] cell@{instanceOffset:X} ofsInfoMask=0x{ofsInfoMask:X8} base=0x{mh2oDataBase:X8} target=0x{(mh2oDataBase+ofsInfoMask):X8} mask=0x{mask:X16}");
            if (mask == 0x430FFD2A430FFD2AUL) Console.Error.WriteLine($"[DBG-DUP] ofsInfoMask=0x{ofsInfoMask:X8} same mask!");
        }
        else
        {
            liquid.ShowMask = ulong.MaxValue; // all sub-cells visible
        }

        // Read height floats: (Width+1)*(Height+1) values
        if (ofsHeightMap != 0)
        {
            reader.Seek((int)(mh2oDataBase + ofsHeightMap));
            int count = (width + 1) * (height + 1);
            var depths = new float[count];
            for (int i = 0; i < count; i++)
                depths[i] = reader.ReadFloat();
            liquid.DepthMap = depths;
        }

        return liquid;
    }

    private uint ParseMcnkRoadOrPrimaryTexture(SpanReader reader, int mcnkOffset, string[] textures)
    {
        int savedPos = reader.Position;
        reader.Seek(mcnkOffset);

        uint magic = ReverseChunkMagic(reader.ReadUInt32());
        if (magic != MagicBytes.Mcnk)
        {
            reader.Seek(savedPos);
            return 0;
        }
        reader.Skip(4);  // skip MCNK size
        reader.Skip(12); // flags + ix + iy
        uint nLayers = reader.ReadUInt32();
        reader.Skip(12); // nDoodadRefs + ofsHeight + ofsNormal
        uint mclyOffset = reader.ReadUInt32();
        reader.Seek(savedPos);

        if (mclyOffset == 0)
            return 0;

        // MCLY entries are 16 bytes: textureId, flags, alphaOffset, effectId.
        reader.Seek(mcnkOffset + (int)mclyOffset + 8); // skip MCLY magic + size
        uint primaryTextureId = 0;
        uint roadTextureId = 0;
        bool hasRoadTexture = false;

        for (uint layer = 0; layer < nLayers; layer++)
        {
            uint textureId = reader.ReadUInt32();
            if (layer == 0)
                primaryTextureId = textureId;

            if (textureId < textures.Length && IsRoadTexture(textures[(int)textureId]))
            {
                roadTextureId = textureId;
                hasRoadTexture = true;
            }

            reader.Skip(12); // flags + alphaOffset + effectId
        }

        reader.Seek(savedPos);
        return hasRoadTexture ? roadTextureId : primaryTextureId;
    }

    private static bool IsRoadTexture(string textureName)
    {
        if (string.IsNullOrEmpty(textureName))
            return false;

        string lower = textureName.ToLowerInvariant();
        return lower.Contains("road") || lower.Contains("cobblestone")
            || lower.Contains("path_stone") || lower.Contains("bridgefloor");
    }

    private AdtMcin[] ParseMcin(SpanReader reader, uint offset)
    {
        reader.Seek((int)offset);

        uint magic = ReverseChunkMagic(reader.ReadUInt32());
        if (magic != MagicBytes.Mcin)
            throw new InvalidDataException($"Expected MCIN, got {MagicBytes.FourCCToString(magic)}");

        reader.Skip(4);

        var entries = new AdtMcin[256];
        for (int i = 0; i < 256; i++)
        {
            entries[i] = new AdtMcin
            {
                Offset = reader.ReadUInt32(),
                Size = reader.ReadUInt32(),
                Flags = reader.ReadUInt32(),
                AsyncId = reader.ReadUInt32()
            };
        }

        return entries;
    }

    private string[] ParseTextures(SpanReader reader, uint offset)
    {
        if (offset == 0)
            return Array.Empty<string>();

        reader.Seek((int)offset);
        reader.Skip(8);

        var textures = new List<string>(64);
        while (reader.Remaining > 0)
        {
            string name = reader.ReadCString(260);
            if (string.IsNullOrEmpty(name))
                break;
            textures.Add(name);
        }

        return textures.ToArray();
    }

    private string[] ParseWmoNames(SpanReader reader, uint wmoOffset, uint widOffset)
    {
        if (wmoOffset == 0 || widOffset == 0)
            return Array.Empty<string>();

        reader.Seek((int)widOffset);
        reader.Skip(4);             // skip MWID magic
        uint widSize = reader.ReadUInt32(); // read chunk data size
        uint count = widSize / 4;
        uint maxCount = (uint)Math.Max(0, reader.Remaining / 4);
        if (count > maxCount) count = maxCount;

        var offsets = new uint[count];
        for (uint i = 0; i < count; i++)
            offsets[i] = reader.ReadUInt32();

        reader.Seek((int)wmoOffset);
        reader.Skip(8);

        var names = new List<string>((int)count);
        foreach (var off in offsets)
        {
            reader.Seek((int)(wmoOffset + 8 + off));
            string name = reader.ReadCString(260);
            names.Add(name);
        }

        return names.ToArray();
    }

    private string[] ParseModelNames(SpanReader reader, uint mmdxOffset, uint midOffset)
    {
        if (mmdxOffset == 0 || midOffset == 0)
            return Array.Empty<string>();

        reader.Seek((int)midOffset);
        reader.Skip(4);             // skip MMID magic
        uint midSize = reader.ReadUInt32(); // read chunk data size
        uint count = midSize / 4;
        uint maxCount = (uint)Math.Max(0, reader.Remaining / 4);
        if (count > maxCount) count = maxCount;

        var offsets = new uint[count];
        for (uint i = 0; i < count; i++)
            offsets[i] = reader.ReadUInt32();

        reader.Seek((int)mmdxOffset);
        reader.Skip(8);

        var names = new List<string>((int)count);
        foreach (var off in offsets)
        {
            reader.Seek((int)(mmdxOffset + 8 + off));
            string name = reader.ReadCString(260);
            names.Add(name);
        }

        return names.ToArray();
    }

    private AdtMfbo? ParseMfbo(SpanReader reader, uint offset)
    {
        reader.Seek((int)offset);

        uint magic = reader.ReadUInt32();
        if (magic != MagicBytes.Mfbo)
            return null;

        uint size = reader.ReadUInt32();
        // MFBO real format: two short[3][3] planes (max then min) = 36 bytes total.
        // Used for flight bounds only — not needed for map extraction.
        if (size < 36)
            return null;

        var maximum = new short[9];
        for (int i = 0; i < 9; i++) maximum[i] = reader.ReadInt16();
        var minimum = new short[9];
        for (int i = 0; i < 9; i++) minimum[i] = reader.ReadInt16();

        return new AdtMfbo { Maximum = maximum, Minimum = minimum };
    }

    private AdtMh2o? ParseMh2o(SpanReader reader, uint offset)
    {
        reader.Seek((int)offset);

        uint magic = ReverseChunkMagic(reader.ReadUInt32());
        if (magic != MagicBytes.Mh2o)
            return null;

        uint size = reader.ReadUInt32();

        // Position is now at offset+8 = start of chunk data (base for all relative offsets)
        uint chunkDataStart = (uint)reader.Position;

        var cells = new AdtMh2oCell[256];
        for (int i = 0; i < 256; i++)
        {
            cells[i] = new AdtMh2oCell
            {
                HeaderOffset    = reader.ReadUInt32(),
                LayerCount      = reader.ReadUInt32(),
                RenderMaskOffset = reader.ReadUInt32()
            };
        }

        var h2o = new AdtMh2o { Cells = cells, ChunkDataStart = chunkDataStart };
        return h2o;
    }

    private (float[] Heights, ushort AreaId, LiquidData Liquid, ushort Holes, LiquidData Mclq) ParseMcnk(
        SpanReader reader, int startOffset, int chunkSize, LiquidData? waterData, List<string> warnings)
    {
        reader.Seek(startOffset);

        uint magic = ReverseChunkMagic(reader.ReadUInt32());
        if (magic != MagicBytes.Mcnk)
        {
            warnings.Add($"MCNK at {startOffset} has invalid magic: {MagicBytes.FourCCToString(magic)}");
            return (new float[AdtMcvt.TotalVertices], 0, waterData ?? LiquidData.Empty, 0, LiquidData.Empty);
        }

        reader.Skip(4); // size (already known)

        // WotLK MCNK header — 128 bytes (0x80), all offsets from MCNK magic start
        var header = new AdtMcnk
        {
            Flags         = reader.ReadUInt32(), // 0x00
            IndexX        = reader.ReadUInt32(), // 0x04
            IndexY        = reader.ReadUInt32(), // 0x08
            NLayers       = reader.ReadUInt32(), // 0x0C  ← was missing, caused all below to be wrong
            NDoodadRefs   = reader.ReadUInt32(), // 0x10  ← was missing
            OfsHeight     = reader.ReadUInt32(), // 0x14  offset to MCVT (from MCNK magic)
            OfsNormal     = reader.ReadUInt32(), // 0x18  offset to MCNR
            OfsLayer      = reader.ReadUInt32(), // 0x1C  offset to MCLY
            OfsRefs       = reader.ReadUInt32(), // 0x20  offset to MCRF
            OfsAlpha      = reader.ReadUInt32(), // 0x24
            SizeAlpha     = reader.ReadUInt32(), // 0x28
            OfsShadow     = reader.ReadUInt32(), // 0x2C
            SizeShadow    = reader.ReadUInt32(), // 0x30
            AreaId        = reader.ReadUInt32(), // 0x34  ← zone/area ID
            NMapObjRefs   = reader.ReadUInt32(), // 0x38
            Holes         = reader.ReadUInt32(), // 0x3C  ← low-res holes bitmask
            LowQualityTex0 = reader.ReadUInt16(), // 0x40
            LowQualityTex1 = reader.ReadUInt16(), // 0x42
            LowQualityTex2 = reader.ReadUInt16(), // 0x44
            LowQualityTex3 = reader.ReadUInt16(), // 0x46
            LowQualityTex4 = reader.ReadUInt16(), // 0x48
            LowQualityTex5 = reader.ReadUInt16(), // 0x4A
            LowQualityTex6 = reader.ReadUInt16(), // 0x4C
            LowQualityTex7 = reader.ReadUInt16(), // 0x4E
            NEffectDoodad  = reader.ReadUInt32(), // 0x50
            OfsSndEmitters = reader.ReadUInt32(), // 0x54
            NSndEmitters   = reader.ReadUInt32(), // 0x58
            OfsLiquid      = reader.ReadUInt32(), // 0x5C
            SizeLiquid     = reader.ReadUInt32(), // 0x60
            PredTex        = reader.ReadUInt32(), // 0x64
            Zpos           = reader.ReadFloat(),  // 0x68  position.x in file layout
            Xpos           = reader.ReadFloat(),  // 0x6C  position.y in file layout
            Ypos           = reader.ReadFloat(),  // 0x70  position.z = height base
            OfsMCCV        = reader.ReadUInt32(), // 0x74
            Unused1        = reader.ReadUInt32(), // 0x78
            Unused2        = reader.ReadUInt32(), // 0x7C
        };

        // Faithful port of MaNGOS C++ System.cpp::ConvertADT: V9 and V8 are
        // first initialized to cell->ypos (= chunk base altitude), then the
        // += MCVT loop is applied when the MCVT chunk is present. The C#
        // previously left the array at all-zeros when OfsHeight was 0, which
        // collapsed WMO-only / dummy MCNK tiles to sea level and produced
        // byte-for-byte divergence from the C++ reference.
        float[] heights = new float[AdtMcvt.TotalVertices];
        if (header.OfsHeight > 0)
        {
            reader.Seek(startOffset + (int)header.OfsHeight);
            reader.Skip(8); // skip MCVT magic + size

            for (int i = 0; i < AdtMcvt.TotalVertices; i++)
                heights[i] = header.Ypos + reader.ReadFloat(); // Ypos = world Y = height base
        }
        else
        {
            Array.Fill(heights, header.Ypos);
        }

        return (heights, (ushort)header.AreaId, waterData ?? ParseMclq(reader, startOffset, in header) ?? LiquidData.Empty, (ushort)(header.Holes & 0xFFFFu), ParseMclq(reader, startOffset, in header) ?? LiquidData.Empty);
    }

    /// <summary>
    /// Port fidèle de MaNGOS System.cpp lignes 827-893 (chemin MCLQ "old").
    /// MCLQ est encore présent dans certains chunks WotLK. Le C++ le traite
    /// AVANT MH2O — MH2O écrase si présent. Ici on l'utilise quand MH2O n'a rien.
    /// </summary>
    private LiquidData? ParseMclq(SpanReader reader, int mcnkStart, in AdtMcnk header)
    {
        if (header.OfsLiquid == 0 || header.SizeLiquid <= 8)
            return null;

        // MCLQ layout (adt.h adt_MCLQ):
        //   fcc(4) + size(4) + height1(4) + height2(4) = 16 bytes header
        //   liquid[9][9] : { uint32 light; float height; } = 8 bytes each → 9*9*8 = 648 bytes
        //   flags[8][8] : uint8 = 64 bytes
        //   data[84] : uint8
        int mclqStart = mcnkStart + (int)header.OfsLiquid;
        const int DataStart = 16; // skip fcc+size+height1+height2
        const int LiquidEntrySize = 8;
        const int GridFull = 9;
        const int FlagGrid = 8;

        // Read 9x9 height grid (only the 'height' float, skip 'light' uint32)
        var heights = new float[GridFull * GridFull];
        int heightGridStart = mclqStart + DataStart;
        for (int y = 0; y < GridFull; y++)
        {
            for (int x = 0; x < GridFull; x++)
            {
                int pos = heightGridStart + (y * GridFull + x) * LiquidEntrySize;
                reader.Seek(pos + 4); // skip 'light' uint32
                heights[y * GridFull + x] = reader.ReadFloat();
            }
        }

        // Read 8x8 flag grid
        int flagOffset = mclqStart + DataStart + GridFull * GridFull * LiquidEntrySize;
        reader.Seek(flagOffset);
        Span<byte> flags = stackalloc byte[FlagGrid * FlagGrid];
        for (int i = 0; i < flags.Length; i++)
            flags[i] = reader.ReadByte();

        // Build show mask: flags[y][x] != 0x0F means visible
        int count = 0;
        ulong showMask = 0UL;
        bool hasDarkWater = false;
        for (int y = 0; y < FlagGrid; y++)
        {
            for (int x = 0; x < FlagGrid; x++)
            {
                byte f = flags[y * FlagGrid + x];
                if (f != 0x0F)
                {
                    showMask |= 1UL << (y * FlagGrid + x);
                    count++;
                }
                if ((f & 0x80) != 0)
                    hasDarkWater = true;
            }
        }

        if (count == 0)
            return null;

        // Type from MCNK.flags bits 2,3,4 (System.cpp lignes 862-877)
        //   bit 2 → water (entry=1)
        //   bit 3 → ocean (entry=2)
        //   bit 4 → magma (entry=3)
        uint cFlag = header.Flags;
        ushort rawTypeId;
        LiquidType liquidType;
        if ((cFlag & (1u << 4)) != 0) { rawTypeId = 3; liquidType = LiquidType.Magma; }
        else if ((cFlag & (1u << 3)) != 0) { rawTypeId = 2; liquidType = LiquidType.Ocean; }
        else if ((cFlag & (1u << 2)) != 0) { rawTypeId = 1; liquidType = LiquidType.Water; }
        else return null; // flags sans bit 2/3/4 → pas de liquide valide

        byte typeFlags = (byte)liquidType;
        if (hasDarkWater && liquidType == LiquidType.Ocean)
            typeFlags |= 0x10; // MAP_LIQUID_TYPE_DARK_WATER

        // MinHeight = min des heights du MCLQ (utilisé par liquidLevel du header)
        float minH = float.MaxValue;
        for (int i = 0; i < heights.Length; i++)
            if (heights[i] < minH) minH = heights[i];
        if (minH == float.MaxValue) minH = 0f;

        return new LiquidData
        {
            RawTypeId = rawTypeId,
            PrimaryType = (LiquidType)typeFlags,
            LiquidLevel = minH,
            OffsetX = 0,
            OffsetY = 0,
            Width = FlagGrid,
            Height = FlagGrid,
            DepthMap = heights,
            ShowMask = showMask,
            VertexFormat = 0,
            OfsInfoMask = 0
        };
    }

    private AdtMddf[] ParseMddf(SpanReader reader, uint offset)
    {
        if (offset == 0)
            return Array.Empty<AdtMddf>();

        reader.Seek((int)offset);

        uint magic = ReverseChunkMagic(reader.ReadUInt32());
        if (magic != MagicBytes.Mddf)
            return Array.Empty<AdtMddf>();

        uint size = reader.ReadUInt32();
        uint count = size / 36;

        var entries = new AdtMddf[count];
        for (uint i = 0; i < count; i++)
        {
            entries[i] = new AdtMddf
            {
                NameId = reader.ReadUInt32(),
                FileDataId = reader.ReadUInt32(),
                PositionX = reader.ReadFloat(),
                PositionY = reader.ReadFloat(),
                PositionZ = reader.ReadFloat(),
                RotationY = reader.ReadFloat(),
                RotationX = reader.ReadFloat(),
                RotationZ = reader.ReadFloat(),
                Scale = reader.ReadUInt16(),
                Flags = reader.ReadUInt16()
            };
        }

        return entries;
    }

    private AdtModf[] ParseModf(SpanReader reader, uint offset)
    {
        if (offset == 0)
            return Array.Empty<AdtModf>();

        reader.Seek((int)offset);

        uint magic = ReverseChunkMagic(reader.ReadUInt32());
        if (magic != MagicBytes.Modf)
            return Array.Empty<AdtModf>();

        uint size = reader.ReadUInt32();
        uint count = size / 64;

        var entries = new AdtModf[count];
        for (uint i = 0; i < count; i++)
        {
            entries[i] = new AdtModf
            {
                NameId = reader.ReadUInt32(),
                UniqueId = reader.ReadUInt32(),
                PositionX = reader.ReadFloat(),
                PositionY = reader.ReadFloat(),
                PositionZ = reader.ReadFloat(),
                RotationX = reader.ReadFloat(),
                RotationY = reader.ReadFloat(),
                RotationZ = reader.ReadFloat(),
                LowerBoundsX = reader.ReadFloat(),
                LowerBoundsY = reader.ReadFloat(),
                LowerBoundsZ = reader.ReadFloat(),
                UpperBoundsX = reader.ReadFloat(),
                UpperBoundsY = reader.ReadFloat(),
                UpperBoundsZ = reader.ReadFloat(),
                Flags = reader.ReadUInt16(),
                DoodadSet = reader.ReadUInt16(),
                NameSet = reader.ReadUInt16(),
                Scale = reader.ReadUInt16()
            };
        }

        return entries;
    }

    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// ADT files store chunk magics REVERSED on disk (MaNGOS adtfile.cpp::flipcc
    /// reverses the 4 bytes after each Read). This swaps the 4 bytes of
    /// <paramref name="reversed"/> back to canonical "ABCD" order so the value
    /// can be compared against MagicBytes constants (MCNK, MHDR, MTEX, …).
    /// </summary>
    private static uint ReverseChunkMagic(uint reversed)
    {
        return ((reversed & 0x000000FFu) << 24)
             | ((reversed & 0x0000FF00u) << 8)
             | ((reversed & 0x00FF0000u) >> 8)
             | ((reversed & 0xFF000000u) >> 24);
    }
}
