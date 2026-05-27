using System.Text;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Binary;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;
using MaNGOS.Extractor.Formats.Vmap.Models;
using MaNGOS.Extractor.Formats.Wmo.Models;

namespace MaNGOS.Extractor.Formats.Wmo.Parsing;

/// <summary>
/// Parses WMO (World Map Object) files from WoW MPQ archives.
/// Handles root files (MOHD, MOGI, MODN, MODD, MODS, MOTX) and group files (MOGP, MOVT, MOVI, MOPY, MOBA, MH2O).
/// </summary>
public sealed class WmoParser
{
    private readonly IArchiveReader _archive;
    private readonly ILogger<WmoParser> _logger;

    public WmoParser(IArchiveReader archive, ILogger<WmoParser> logger)
    {
        _archive = archive;
        _logger = logger;
    }

    /// <summary>
    /// Parses a WMO root file (not a group file).
    /// </summary>
    /// <param name="path">Path to the .wmo file in the archive.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parse result with root data and group info.</returns>
    public async Task<WmoParseResult> ParseRootAsync(string path, CancellationToken ct = default)
    {
        if (!_archive.TryReadFile(path, out ReadOnlyMemory<byte> data))
        {
            _logger.LogWarning("WMO file not found: {Path}", path);
            return WmoParseResult.Failed(new List<string> { $"WMO not found: {path}" });
        }

        return await Task.Run(() => ParseRootInternal(path, data), ct);
    }

    private WmoParseResult ParseRootInternal(string path, ReadOnlyMemory<byte> data)
    {
        var warnings = new List<string>();
        var reader = new SpanReader(data);

        // WMO files start with MVER (version header)
        uint magic = reader.ReadUInt32();
        if (magic != MagicBytes.Mver)
        {
            // Not a standard WMO — try as root without MVER
            reader.Seek(0);
        }
        else
        {
            uint mverSize = reader.ReadUInt32();
            reader.Skip((int)mverSize); // skip version data
        }

        // Now read MOHD
        uint moMagic = reader.ReadUInt32();
        if (moMagic != MagicBytes.Mohd)
        {
            warnings.Add($"Invalid WMO magic: expected MOHD (0x{MagicBytes.Mohd:X}), got 0x{moMagic:X}");
            return WmoParseResult.Failed(warnings);
        }

        uint chunkSize = reader.ReadUInt32();

        var header = new WmoRootHeader
        {
            TextureCount = reader.ReadUInt32(),
            GroupCount = reader.ReadUInt32(),
            PortalCount = reader.ReadUInt32(),
            LightCount = reader.ReadUInt32(),
            DoodadCount = reader.ReadUInt32(),
            DoodadSetCount = reader.ReadUInt32(),
            WmoId = reader.ReadUInt32(),
            LiquidType = reader.ReadUInt32(),
            BoundingBoxColor = reader.ReadUInt32(),
            BoundingBoxMin = new Vector3Min(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()),
            BoundingBoxMax = new Vector3Min(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat())
        };

        // Parse remaining chunks
        var groupNames = new List<string>();
        var doodadNames = new List<string>();
        var doodadPlacements = new List<WmoDoodadPlacement>();
        var doodadSets = new List<WmoDoodadSet>();
        var textureNames = new List<string>();

        while (reader.Remaining > 8)
        {
            uint chunkMagic = reader.ReadUInt32();
            uint chunkSize2 = reader.ReadUInt32();

            switch (chunkMagic)
            {
                case MagicBytes.Mogn: // Group names
                    ParseMogn(reader, chunkSize2, groupNames);
                    break;

                case MagicBytes.Motx: // Texture names
                    ParseMotx(reader, chunkSize2, textureNames);
                    break;

                case MagicBytes.Modn: // Doodad names
                    ParseModn(reader, chunkSize2, doodadNames);
                    break;

                case MagicBytes.Modd: // Doodad placements
                    ParseModd(reader, chunkSize2, doodadPlacements);
                    break;

                case MagicBytes.Mods: // Doodad sets
                    ParseMods(reader, chunkSize2, doodadSets);
                    break;

                default:
                    warnings.Add($"Unknown WMO chunk: {MagicBytes.FourCCToString(chunkMagic)} at offset {(int)chunkSize - reader.Remaining}");
                    break;
            }

            if (chunkSize2 == 0) break; // Safety guard
        }

        var root = new WmoRootFile(
            path,
            header,
            groupNames.ToArray(),
            doodadSets.ToArray(),
            doodadPlacements.ToArray(),
            textureNames.ToArray(),
            doodadNames.ToArray());

        return WmoParseResult.Ok(root, Array.Empty<WmoGroupFile>());
    }

    /// <summary>
    /// Parses a WMO group file.
    /// </summary>
    /// <param name="path">Path to the group .wmo file (e.g., "foo.wmo" for group 0).</param>
    /// <param name="groupIndex">Group index within the parent WMO.</param>
    /// <param name="rootFileName">Name of the parent WMO root file.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<WmoGroupFile?> ParseGroupAsync(string path, int groupIndex, string rootFileName, CancellationToken ct = default)
    {
        if (!_archive.TryReadFile(path, out ReadOnlyMemory<byte> data))
        {
            _logger.LogWarning("WMO group file not found: {Path}", path);
            return null;
        }

        return await Task.Run(() => ParseGroupInternal(path, groupIndex, rootFileName, data), ct);
    }

    private WmoGroupFile? ParseGroupInternal(string path, int groupIndex, string rootFileName, ReadOnlyMemory<byte> data)
    {
        var reader = new SpanReader(data);

        // WMO group files start with MVER, then MOGP
        uint magic = reader.ReadUInt32();
        if (magic == MagicBytes.Mver)
        {
            uint mverSize = reader.ReadUInt32();
            reader.Skip((int)mverSize);
            magic = reader.ReadUInt32(); // read MOGP magic
        }
        if (magic != MagicBytes.Mogp)
            return null;

        uint chunkSize = reader.ReadUInt32();

        // Parse MOGP header
        var header = new WmoGroupHeader
        {
            GroupNameIndex = reader.ReadInt32(),
            DescGroupNameIndex = reader.ReadInt32(),
            Flags = reader.ReadUInt32(),
            BoundingBoxMin = new Vector3Min(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()),
            BoundingBoxMax = new Vector3Min(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()),
            PortalStart = reader.ReadUInt16(),
            PortalCount = reader.ReadUInt16(),
            BatchA = reader.ReadUInt16(),
            BatchB = reader.ReadUInt16(),
            BatchC = reader.ReadUInt32(),
            FogIndex = reader.ReadUInt32(),
            LiquidType = reader.ReadUInt32(),
            GroupWmoId = reader.ReadUInt32()
        };

        // Parse sub-chunks
        var vertices = new List<WmoVertex>();
        var triangles = new List<WmoTriangle>();
        var materials = new List<WmoMaterial>();
        var batches = new List<WmoBatch>();
        WmoLiquidData? liquid = null;

        while (reader.Remaining > 8)
        {
            uint chunkMagic = reader.ReadUInt32();
            uint chunkSize2 = reader.ReadUInt32();

            switch (chunkMagic)
            {
                case MagicBytes.Movt: // Group WMO files use MOVT for vertices (MOVV is root-only portal vertices)
                    ParseMovv(reader, chunkSize2, vertices);
                    break;

                case MagicBytes.Movi: // Indices
                    ParseMovi(reader, chunkSize2, triangles);
                    break;

                case MagicBytes.Mopy:
                    ParseMopy(reader, (int)chunkSize2, materials);
                    break;

                case MagicBytes.Moba:
                    ParseMoba(reader, (int)chunkSize2, batches);
                    break;

                case MagicBytes.Liqu:
                    liquid = ParseLiquid(reader, (int)chunkSize2);
                    break;

                default:
                    // Skip unknown chunks
                    break;
            }

            if (chunkSize2 == 0) break; // Safety guard
        }

        return new WmoGroupFile(
            rootFileName,
            groupIndex,
            header,
            vertices.ToArray(),
            triangles.ToArray(),
            materials.ToArray(),
            batches.ToArray(),
            liquid);
    }

    private void ParseMogn(SpanReader reader, uint chunkSize, List<string> names)
    {
        int endPos = reader.Position + (int)chunkSize;
        while (reader.Position < endPos && reader.Remaining > 8)
        {
            string name = reader.ReadCString(260);
            if (!string.IsNullOrEmpty(name))
                names.Add(name);
        }
        int padding = (4 - ((int)chunkSize % 4)) % 4;
        if (reader.Position < endPos + padding)
            reader.Skip(endPos + padding - reader.Position);
    }

    private void ParseMotx(SpanReader reader, uint size, List<string> textures)
    {
        var data = reader.ReadSpan((int)size);
        int pos = 0;
        while (pos < data.Length)
        {
            int end = data.Slice(pos).IndexOf((byte)0);
            if (end < 0) break;
            string tex = Encoding.ASCII.GetString(data.Slice(pos, end));
            if (!string.IsNullOrEmpty(tex))
                textures.Add(tex);
            pos += end + 1;
        }
        // ReadSpan already advanced reader by size bytes — caller handles padding
    }

    private void ParseModn(SpanReader reader, uint size, List<string> names)
    {
        var data = reader.ReadSpan((int)size);
        int pos = 0;
        while (pos < data.Length)
        {
            int end = data.Slice(pos).IndexOf((byte)0);
            if (end < 0) break;
            string name = Encoding.ASCII.GetString(data.Slice(pos, end));
            if (!string.IsNullOrEmpty(name))
                names.Add(name);
            pos += end + 1;
        }
        // ReadSpan already advanced reader by size bytes — caller handles padding
    }

    private void ParseModd(SpanReader reader, uint size, List<WmoDoodadPlacement> placements)
    {
        uint count = size / 36; // MODD entry size
        for (uint i = 0; i < count; i++)
        {
            placements.Add(new WmoDoodadPlacement
            {
                NameIndex = reader.ReadUInt32(),
                PositionX = reader.ReadFloat(),
                PositionY = reader.ReadFloat(),
                PositionZ = reader.ReadFloat(),
                RotationY = reader.ReadFloat(),
                RotationX = reader.ReadFloat(),
                RotationZ = reader.ReadFloat(),
                Scale = reader.ReadFloat(),
                Color = reader.ReadUInt32()
            });
        }
    }

    private void ParseMods(SpanReader reader, uint size, List<WmoDoodadSet> sets)
    {
        uint count = size / 32; // MODS entry size
        for (uint i = 0; i < count; i++)
        {
            string name = reader.ReadFixedString(20);
            sets.Add(new WmoDoodadSet
            {
                Name = name.Trim(),
                FirstDoodadIndex = reader.ReadUInt32(),
                DoodadCount = reader.ReadUInt32(),
                SetId = reader.ReadUInt32()
            });
        }
    }

    private void ParseMovv(SpanReader reader, uint size, List<WmoVertex> vertices)
    {
        uint count = size / 12; // 3 floats per vertex
        for (uint i = 0; i < count; i++)
        {
            vertices.Add(new WmoVertex
            {
                X = reader.ReadFloat(),
                Y = reader.ReadFloat(),
                Z = reader.ReadFloat()
            });
        }
    }

    private void ParseMovi(SpanReader reader, uint size, List<WmoTriangle> triangles)
    {
        uint count = size / 6; // 3 uint16 per triangle
        for (uint i = 0; i < count; i++)
        {
            triangles.Add(new WmoTriangle
            {
                I0 = reader.ReadUInt16(),
                I1 = reader.ReadUInt16(),
                I2 = reader.ReadUInt16()
            });
        }
    }

    private void ParseMopy(SpanReader reader, int size, List<WmoMaterial> materials)
    {
        uint count = (uint)size / 2;
        for (uint i = 0; i < count; i++)
        {
            byte flags = reader.ReadByte();
            byte unk = reader.ReadByte(); // Usually 0x00 or 0x08

            materials.Add(new WmoMaterial
            {
                Flags = (WmoMaterialFlag)flags,
                LightMapId = -1,
                ShaderType = 0
            });
        }
    }

    private void ParseMobr(SpanReader reader, uint size, List<WmoBatch> batches)
    {
        // 20 bytes per batch
        uint count = size / 20;
        for (uint i = 0; i < count; i++)
        {
            batches.Add(new WmoBatch
            {
                Flags = reader.ReadUInt32(),
                FirstIndex = reader.ReadUInt32(),
                IndexCount = reader.ReadUInt32(),
                FirstVertex = reader.ReadUInt32(),
                VertexCount = reader.ReadUInt32(),
                MaterialId = reader.ReadUInt32()
            });
        }
    }

    private void ParseMoba(SpanReader reader, int size, List<WmoBatch> batches)
    {
        // MOBA: 20 bytes per batch, same as MOBR
        int count = size / 20;
        for (int i = 0; i < count; i++)
        {
            batches.Add(new WmoBatch
            {
                Flags = reader.ReadUInt32(),
                FirstIndex = reader.ReadUInt32(),
                IndexCount = reader.ReadUInt32(),
                FirstVertex = reader.ReadUInt32(),
                VertexCount = reader.ReadUInt32(),
                MaterialId = reader.ReadUInt32()
            });
        }
    }

    private WmoLiquidData? ParseLiquid(SpanReader reader, int size)
    {
        if (size < 32)
            return null;

        var header = new WmoLiquidHeader
        {
            VertexX = reader.ReadInt32(),
            VertexY = reader.ReadInt32(),
            TileX = reader.ReadInt32(),
            TileY = reader.ReadInt32(),
            PositionX = reader.ReadFloat(),
            PositionY = reader.ReadFloat(),
            PositionZ = reader.ReadFloat(),
            LiquidType = reader.ReadInt16()
        };
        reader.Skip(2); // Padding

        int heightCount = header.VertexX * header.VertexY;
        int tileCount = header.TileX * header.TileY;

        var heights = new float[heightCount];
        for (int i = 0; i < heightCount; i++)
            heights[i] = reader.ReadFloat();

        var tileFlags = new byte[tileCount];
        for (int i = 0; i < tileCount; i++)
            tileFlags[i] = reader.ReadByte();

        return new WmoLiquidData(header, heights, tileFlags);
    }
}