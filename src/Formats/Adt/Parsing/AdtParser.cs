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
    private readonly Dictionary<string, AdtFile> _cache;

    public AdtParser(IArchiveReader archive, ILogger<AdtParser> logger)
    {
        _archive = archive;
        _logger = logger;
        _cache = new Dictionary<string, AdtFile>(StringComparer.OrdinalIgnoreCase);
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

        var result = await Task.Run(() => ParseInternal(path, mapId, tileX, tileY, data, ct), ct);

        if (result.Success && result.Tile != null)
            _cache[path] = result.Tile;

        return result;
    }

    private AdtParseResult ParseInternal(string path, uint mapId, int tileX, int tileY, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var warnings = new List<string>();
        var reader = new SpanReader(data);

        uint magic = reader.ReadUInt32();
        if (magic != MagicBytes.Mver)
        {
            warnings.Add($"Invalid ADT magic: expected MVER (0x{MagicBytes.Mver:X}), got 0x{magic:X}");
            return AdtParseResult.Failed(warnings);
        }
        reader.Skip(4);

        var mhdrOffset = 0u;
        var mhdrSize = 0u;

        while (reader.Remaining > 8)
        {
            uint chunkMagic = reader.ReadUInt32();
            uint chunkSize = reader.ReadUInt32();

            switch (chunkMagic)
            {
                case MagicBytes.Mhdr:
                    mhdrOffset = (uint)reader.Position;
                    mhdrSize = chunkSize;
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
                    reader.Skip((int)chunkSize);
                    break;

                default:
                    if (chunkMagic == MagicBytes.Mcnk)
                    {
                        reader.Skip((int)chunkSize);
                    }
                    else
                    {
                        warnings.Add($"Unknown chunk at root level: {MagicBytes.FourCCToString(chunkMagic)}");
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
        reader.Skip(8);

        var header = new AdtMhdr
        {
            FileDataId = reader.ReadUInt32(),
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

        string[] textures = header.MtexOffset > 0 ? ParseTextures(reader, header.MtexOffset) : Array.Empty<string>();

        string[] wmos = ParseWmoNames(reader, header.MwmoOffset, header.MwidOffset);
        string[] models = ParseModelNames(reader, header.MmdxOffset, header.MmidOffset);

        AdtMfbo? mfbo = header.MfboOffset > 0 ? ParseMfbo(reader, header.MfboOffset) : null;

        AdtMcin[] mcinEntries = header.McinOffset > 0 ? ParseMcin(reader, header.McinOffset) : Array.Empty<AdtMcin>();

        AdtMh2o? waterData = header.Mh2oOffset > 0 ? ParseMh2o(reader, header.Mh2oOffset) : null;

        float[] allHeights = new float[256 * AdtMcvt.TotalVertices];
        ushort[] areaIds = new ushort[256];
        LiquidData[] liquids = new LiquidData[256];

        // Parse liquid data for all 256 cells from MH2O chunk
        if (waterData.HasValue && mcinEntries.Length == 256)
        {
            for (int i = 0; i < 256; i++)
            {
                var cell = waterData.Value.Cells[i];
                if (!cell.HasData)
                    continue;

                var mcnkEntry = mcinEntries[i];
                if (mcnkEntry.Offset > 0)
                {
                    int mcnkStart = (int)mcnkEntry.Offset;
                    liquids[i] = ParseMh2oCellData(reader, (uint)mcnkStart, cell);
                }
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
        }

        var mddf = header.MddfOffset > 0 ? ParseMddf(reader, header.MddfOffset) : Array.Empty<AdtMddf>();
        var modf = header.ModfOffset > 0 ? ParseModf(reader, header.ModfOffset) : Array.Empty<AdtModf>();

        // Parse texture IDs for each MCNK from MCLY chunks
        var chunkTextureIds = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            if (i < mcinEntries.Length && mcinEntries[i].Offset > 0)
            {
                chunkTextureIds[i] = ParseMcnkPrimaryTexture(reader, (int)mcinEntries[i].Offset);
            }
        }

        var tile = new AdtFile(
            mapId, tileX, tileY, path, header, mcinEntries, mfbo,
            textures, wmos, models, mddf, modf,
            allHeights, areaIds, liquids, chunkTextureIds);

        return new AdtParseResult(tile, warnings);
    }

    private LiquidData ParseMh2oCellData(SpanReader reader, uint offset, in AdtMh2oCell cell)
    {
        if (!cell.HasData || offset == 0)
            return LiquidData.Empty;

        reader.Seek((int)offset);

        var header = new AdtMh2oHeader
        {
            OffsetX = reader.ReadByte(),
            OffsetY = reader.ReadByte(),
            Width = reader.ReadByte(),
            Height = reader.ReadByte(),
            LiquidType = reader.ReadUInt16(),
            Flags = reader.ReadUInt16(),
            MinHeight = reader.ReadFloat(),
            MaxHeight = reader.ReadFloat()
        };

        var liquid = new LiquidData
        {
            LiquidLevel = header.MinHeight,
            PrimaryType = (LiquidType)(header.LiquidType & 0x0F),
            OffsetX = header.OffsetX,
            OffsetY = header.OffsetY,
            Width = header.Width,
            Height = header.Height
        };

        if (header.HasHeightValues && cell.HeightCount > 0)
        {
            int depthCount = (int)cell.HeightCount;
            var depths = new float[depthCount];
            for (int i = 0; i < depthCount; i++)
                depths[i] = reader.ReadFloat();
            liquid.DepthMap = depths;
        }

        if (header.HasVertexColor && cell.VertexCount > 0)
        {
            int vertexCount = (int)cell.VertexCount;
            var colors = new (byte R, byte G, byte B, byte A)[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                colors[i].R = reader.ReadByte();
                colors[i].G = reader.ReadByte();
                colors[i].B = reader.ReadByte();
                colors[i].A = reader.ReadByte();
            }
            liquid.VertexColors = colors;
        }

        return liquid;
    }

    private uint ParseMcnkPrimaryTexture(SpanReader reader, int mcnkOffset)
    {
        int savedPos = reader.Position;
        reader.Seek(mcnkOffset);

        uint magic = reader.ReadUInt32();
        if (magic != MagicBytes.Mcnk)
        {
            reader.Seek(savedPos);
            return 0;
        }
        reader.Skip(4);

        uint mclyOffset = reader.ReadUInt32();
        reader.Seek(savedPos);

        if (mclyOffset == 0)
            return 0;

        reader.Seek(mcnkOffset + (int)mclyOffset + 8);
        uint count = reader.ReadUInt32();

        uint primaryTextureId = 0;
        if (count > 0)
        {
            reader.Skip(16);
            primaryTextureId = reader.ReadUInt32();
        }

        reader.Seek(savedPos);
        return primaryTextureId;
    }

    private AdtMcin[] ParseMcin(SpanReader reader, uint offset)
    {
        reader.Seek((int)offset);

        uint magic = reader.ReadUInt32();
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
        reader.Skip(8);
        uint widSize = reader.ReadUInt32();
        uint count = widSize / 4;

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
        reader.Skip(8);
        uint midSize = reader.ReadUInt32();
        uint count = midSize / 4;

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

        float boxMinX = reader.ReadFloat();
        float boxMinY = reader.ReadFloat();
        float boxMinZ = reader.ReadFloat();
        float boxMaxX = reader.ReadFloat();
        float boxMaxY = reader.ReadFloat();
        float boxMaxZ = reader.ReadFloat();

        uint lowerCount = reader.ReadUInt32();
        uint upperCount = reader.ReadUInt32();

        var lowerVerts = new (short X, short Y, short Z)[Math.Min(lowerCount, 12)];
        for (int i = 0; i < lowerVerts.Length; i++)
            lowerVerts[i] = (reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16());

        var upperVerts = new (short X, short Y, short Z)[Math.Min(upperCount, 12)];
        for (int i = 0; i < upperVerts.Length; i++)
            upperVerts[i] = (reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16());

        return new AdtMfbo
        {
            BoxMin = (boxMinX, boxMinY, boxMinZ),
            BoxMax = (boxMaxX, boxMaxY, boxMaxZ),
            LowerVertexCount = lowerCount,
            UpperVertexCount = upperCount,
            LowerVertices = lowerVerts,
            UpperVertices = upperVerts
        };
    }

    private AdtMh2o? ParseMh2o(SpanReader reader, uint offset)
    {
        reader.Seek((int)offset);

        uint magic = reader.ReadUInt32();
        if (magic != MagicBytes.Mh2o)
            return null;

        uint size = reader.ReadUInt32();

        var cells = new AdtMh2oCell[256];
        for (int i = 0; i < 256; i++)
        {
            cells[i] = new AdtMh2oCell
            {
                HeaderOffset = reader.ReadUInt32(),
                LayerCount = reader.ReadUInt32(),
                HeightCount = reader.ReadUInt32(),
                VertexCount = reader.ReadUInt32()
            };
        }

        // Store the cells array in a temp field - simplified for now
        var h2o = new AdtMh2o { Cells = cells };
        return h2o;
    }

    private (float[] Heights, ushort AreaId, LiquidData Liquid) ParseMcnk(
        SpanReader reader, int startOffset, int chunkSize, LiquidData? waterData, List<string> warnings)
    {
        reader.Seek(startOffset);

        uint magic = reader.ReadUInt32();
        if (magic != MagicBytes.Mcnk)
        {
            warnings.Add($"MCNK at {startOffset} has invalid magic: {MagicBytes.FourCCToString(magic)}");
            return default;
        }

        reader.Skip(4);

        var header = new AdtMcnk
        {
            GridX = reader.ReadUInt32(),
            GridY = reader.ReadUInt32(),
            McvtOffset = reader.ReadUInt32(),
            McnrOffset = reader.ReadUInt32(),
            MclyOffset = reader.ReadUInt32(),
            McrfOffset = reader.ReadUInt32(),
            Unused1 = reader.ReadUInt32(),
            MclvOffset = reader.ReadUInt32(),
            AreaId = reader.ReadUInt32(),
            DoodleDbId = reader.ReadUInt32(),
            Holes = reader.ReadUInt32(),
            LowQualityGridHeight = reader.ReadUInt16(),
            NumLayers = reader.ReadUInt16(),
            NumDoodadRefs = reader.ReadUInt32(),
            DoodadReferencesOffset = reader.ReadUInt32(),
            MtxpOffset = reader.ReadUInt32(),
            MtxpSize = reader.ReadUInt32(),
            McnrOffset2 = reader.ReadUInt32(),
            McnrSize = reader.ReadUInt32(),
            PositionX = reader.ReadFloat(),
            PositionY = reader.ReadFloat(),
            PositionZ = reader.ReadFloat(),
            DoodadFlags = reader.ReadUInt32(),
            TextureId0 = reader.ReadUInt32(),
            TextureId1 = reader.ReadUInt32(),
            Unknown1 = reader.ReadUInt32(),
            Unknown2 = reader.ReadUInt32()
        };

        float[] heights = new float[AdtMcvt.TotalVertices];
        if (header.McvtOffset > 0)
        {
            reader.Seek(startOffset + (int)header.McvtOffset);
            reader.Skip(8);

            for (int i = 0; i < AdtMcvt.TotalVertices; i++)
                heights[i] = header.PositionY + reader.ReadFloat();
        }

        return (heights, (ushort)header!.AreaId, waterData ?? LiquidData.Empty);
    }

    private AdtMddf[] ParseMddf(SpanReader reader, uint offset)
    {
        if (offset == 0)
            return Array.Empty<AdtMddf>();

        reader.Seek((int)offset);

        uint magic = reader.ReadUInt32();
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
                Scale = reader.ReadFloat(),
                Flags = reader.ReadUInt16()
            };
            reader.Skip(2);
        }

        return entries;
    }

    private AdtModf[] ParseModf(SpanReader reader, uint offset)
    {
        if (offset == 0)
            return Array.Empty<AdtModf>();

        reader.Seek((int)offset);

        uint magic = reader.ReadUInt32();
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
                RotationY = reader.ReadFloat(),
                RotationX = reader.ReadFloat(),
                RotationZ = reader.ReadFloat(),
                ScaleX = reader.ReadFloat(),
                ScaleY = reader.ReadFloat(),
                ScaleZ = reader.ReadFloat(),
                Flags = reader.ReadUInt16(),
                DoodadSet = reader.ReadUInt16(),
                GroupIds = reader.ReadUInt32()
            };
        }

        return entries;
    }

    public void ClearCache() => _cache.Clear();
}
