using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Binary;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;
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
        // Safety net: a corrupt WMO (truncated chunk, wrong mverSize, etc.)
        // will throw EndOfStreamException from SpanReader.ReadPrimitive. The
        // C++ Mangos wmo.cpp handles this by trusting SFileReadFile's bounded
        // buffer; we must catch the equivalent here so one bad WMO doesn't
        // abort the whole global scan. The WMO is marked as Failed and the
        // caller (TryBuildWmoAsync) skips writing its .vmo to Buildings/.
        try
        {
            return ParseRootInternalUnsafe(path, data, warnings);
        }
        catch (EndOfStreamException ex)
        {
            warnings.Add($"Truncated/corrupt WMO {path}: {ex.Message}");
            return WmoParseResult.Failed(warnings);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            warnings.Add($"Out-of-range read in WMO {path}: {ex.Message}");
            return WmoParseResult.Failed(warnings);
        }
    }

    private WmoParseResult ParseRootInternalUnsafe(string path, ReadOnlyMemory<byte> data, List<string> warnings)
    {
        var reader = new SpanReader(data);

        // WMO files start with MVER (version header). Both WMO root and group
        // files store their chunk magics REVERSED on disk (MaNGOS wmo.cpp::flipcc
        // reverses each 4-byte magic after reading). We byte-swap here so the
        // values can be compared against the normal MagicBytes constants.
        uint magic = ReverseChunkMagic(reader.ReadUInt32());
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
        uint moMagic = ReverseChunkMagic(reader.ReadUInt32());
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
            // MOHD doodad fields (MaNGOS wmo.cpp:78-80, 3 uint32s):
            //   nModels      = number of doodad paths in MODN
            //   nDoodads     = number of placements in MODD
            //   nDoodadSets  = number of sets in MODS
            DoodadCount = reader.ReadUInt32(),       // nModels
            DoodadPlacementCount = reader.ReadUInt32(), // nDoodads (was missing — caused WMOID misalignment)
            DoodadSetCount = reader.ReadUInt32(),    // nDoodadSets
            // MOHD tail (MaNGOS wmo.cpp:83-87): col, wmoID, bbox, liquidType
            BoundingBoxColor = reader.ReadUInt32(),  // col (ambient color)
            WmoId = reader.ReadUInt32(),             // RootWMOID
            BoundingBoxMin = new Vector3Min(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()),
            BoundingBoxMax = new Vector3Min(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()),
            LiquidType = reader.ReadUInt32(),
        };

        // Parse remaining chunks
        var groupNames = new List<string>();
        var doodadNames = new List<string>();
        var doodadPlacements = new List<WmoDoodadPlacement>();
        var doodadSets = new List<WmoDoodadSet>();
        var textureNames = new List<string>();
        byte[] doodadPaths = Array.Empty<byte>(); // Raw MODN bytes preserved for NameIndex offset lookup

        while (reader.Remaining > 8)
        {
            uint chunkMagic = ReverseChunkMagic(reader.ReadUInt32());
            uint chunkSize2 = reader.ReadUInt32();

            // Defensive: if the chunk claims to be larger than what's left in
            // the file, it's a corrupt WMO. Bail out of the loop instead of
            // trying to read gigabytes past the end. The MaNGOS C++ code does
            // the same — it just doesn't crash because it uses SFileReadFile
            // with a bounded buffer.
            if (chunkSize2 > reader.Remaining)
            {
                warnings.Add($"Corrupt WMO chunk {MagicBytes.FourCCToString(chunkMagic)}: size={chunkSize2} > remaining={reader.Remaining} at offset ~{data.Length - reader.Remaining}");
                break;
            }

            switch (chunkMagic)
            {
                case MagicBytes.Mogn: // Group names
                    ParseMogn(ref reader, chunkSize2, groupNames);
                    break;

                case MagicBytes.Motx: // Texture names
                    ParseMotx(ref reader, chunkSize2, textureNames);
                    break;

                case MagicBytes.Modn: // Doodad names — keep raw bytes for NameIndex offset resolution
                    doodadPaths = ParseModnRaw(ref reader, chunkSize2);
                    ParseModn(ref reader, chunkSize2, doodadNames);
                    break;

                case MagicBytes.Modd: // Doodad placements
                    ParseModd(ref reader, chunkSize2, doodadPlacements);
                    break;

                case MagicBytes.Mods: // Doodad sets
                    ParseMods(ref reader, chunkSize2, doodadSets);
                    break;

                default:
                    warnings.Add($"Unknown WMO chunk: {MagicBytes.FourCCToString(chunkMagic)} at offset {(int)chunkSize - reader.Remaining}");
                    reader.Skip((int)chunkSize2);
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
            doodadNames.ToArray(),
            doodadPaths);

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

        // WMO group files start with MVER, then MOGP. Both chunk magics are
        // REVERSED on disk (MaNGOS wmo.cpp::flipcc) — byte-swap before compare.
        uint magic = ReverseChunkMagic(reader.ReadUInt32());
        if (magic == MagicBytes.Mver)
        {
            uint mverSize = reader.ReadUInt32();
            reader.Skip((int)mverSize);
            magic = ReverseChunkMagic(reader.ReadUInt32()); // read MOGP magic
        }
        if (magic != MagicBytes.Mogp)
        {
            _logger.LogWarning("[WmoParser] {Path} group {Idx}: expected MOGP 0x{Expected:X8} but got 0x{Got:X8} (file size={Size})",
                path, groupIndex, MagicBytes.Mogp, magic, data.Length);
            return null;
        }

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

        // WotLK 3.3.5a MOGP header is 68 bytes total; C++ reads 60 then seeks to +68.
        // The remaining 8 bytes (flags2 + unk) must be skipped before sub-chunks.
        reader.Skip(8);

        // Parse sub-chunks
        var vertices = new List<WmoVertex>();
        var triangles = new List<WmoTriangle>();
        var materials = new List<WmoMaterial>();
        ushort[] rawMoba = Array.Empty<ushort>();
        ushort[] rawMobr = Array.Empty<ushort>();
        ushort[] doodadRefs = Array.Empty<ushort>();
        WmoLiquidData? liquid = null;

        while (reader.Remaining > 8)
        {
            // WMO group chunk magics are also REVERSED on disk (MaNGOS wmo.cpp::flipcc).
            uint chunkMagic = ReverseChunkMagic(reader.ReadUInt32());
            uint chunkSize2 = reader.ReadUInt32();

            switch (chunkMagic)
            {
                case MagicBytes.Movt: // Group WMO files use MOVT for vertices (MOVV is root-only portal vertices)
                    ParseMovv(ref reader, chunkSize2, vertices);
                    break;

                case MagicBytes.Movi: // Indices
                    ParseMovi(ref reader, chunkSize2, triangles);
                    break;

                case MagicBytes.Mopy:
                    ParseMopy(ref reader, (int)chunkSize2, materials);
                    break;

                case MagicBytes.Moba:
                    rawMoba = ParseMoba(ref reader, (int)chunkSize2);
                    break;

                case MagicBytes.LiquidMapMagic: // "MLIQ" — WMO group liquid (C++ wmo.cpp: strcmp(fourcc,"MLIQ"))
                    liquid = ParseLiquid(ref reader, (int)chunkSize2);
                    break;

                case MagicBytes.Modr: // Doodad references for this group (MaNGOS wmo.cpp:251-258)
                    doodadRefs = ParseDoodadReferences(ref reader, (int)chunkSize2);
                    break;

                case MagicBytes.Mobr: // Face indices for BSP nodes
                    ParseMobr(ref reader, (int)chunkSize2, out rawMobr);
                    break;

                default:
                    reader.Skip((int)chunkSize2);
                    break;
            }

            if (chunkSize2 == 0) break; // Safety guard
        }

        var groupFile = new WmoGroupFile(
            rootFileName,
            groupIndex,
            header,
            vertices.ToArray(),
            triangles.ToArray(),
            materials.ToArray(),
            rawMoba,
            liquid,
            doodadRefs);

        groupFile.BspTriangleIndices = rawMobr;
        return groupFile;
    }

    private void ParseMogn(ref SpanReader reader, uint chunkSize, List<string> names)
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

    private void ParseMotx(ref SpanReader reader, uint size, List<string> textures)
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

    private void ParseModn(ref SpanReader reader, uint size, List<string> names)
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

    /// <summary>
    /// Read the raw MODN block bytes so WmoDoodadPlacement.NameIndex (which
    /// is a byte offset into the block, not a string-array index) can be
    /// resolved later. MaNGOS keeps the raw block in
    /// <c>WMODoodadData::Paths</c> (wmo.cpp:96) and reads it with
    /// <c>&amp;doodadData.Paths[doodad.NameIndex()]</c> when extracting
    /// doodad models for the dir_bin record.
    /// </summary>
    private static byte[] ParseModnRaw(ref SpanReader reader, uint size)
    {
        return reader.ReadBytes((int)size);
    }

    /// <summary>
    /// Read MODR: uint16 indices into the root's DoodadData.Spawns that
    /// this group's geometry actually references. MaNGOS wmo.cpp:251-258.
    /// </summary>
    private static ushort[] ParseDoodadReferences(ref SpanReader reader, int size)
    {
        if (size <= 0) return Array.Empty<ushort>();
        int count = size / 2;
        var refs = new ushort[count];
        for (int i = 0; i < count; i++)
            refs[i] = reader.ReadUInt16();
        return refs;
    }

    private void ParseModd(ref SpanReader reader, uint size, List<WmoDoodadPlacement> placements)
    {
        // MODD entry = 40 bytes (MaNGOS wmo.h::MODD). The previous 36-byte
        // read dropped the W component of the rotation quaternion, which made
        // doodad rotations degenerate to zero and broke Doodad::ExtractSet's
        // quaternion composition. Now reads the full 40-byte record.
        uint count = size / 40;
        for (uint i = 0; i < count; i++)
        {
            placements.Add(new WmoDoodadPlacement
            {
                NameIndexAndFlags = reader.ReadUInt32(),
                PositionX = reader.ReadFloat(),
                PositionY = reader.ReadFloat(),
                PositionZ = reader.ReadFloat(),
                RotationX = reader.ReadFloat(),
                RotationY = reader.ReadFloat(),
                RotationZ = reader.ReadFloat(),
                RotationW = reader.ReadFloat(),
                Scale = reader.ReadFloat(),
                Color = reader.ReadUInt32()
            });
        }
    }

    private void ParseMods(ref SpanReader reader, uint size, List<WmoDoodadSet> sets)
    {
        // MODS entry = 32 bytes (MaNGOS wmo.h::MODS):
        //   Name[20] | StartIndex(4) | Count(4) | Padding(4)
        uint count = size / 32;
        for (uint i = 0; i < count; i++)
        {
            string name = reader.ReadFixedString(20);
            sets.Add(new WmoDoodadSet
            {
                Name = name.Trim(),
                FirstDoodadIndex = reader.ReadUInt32(),
                DoodadCount = reader.ReadUInt32(),
                Padding = reader.ReadUInt32()
            });
        }
    }

    private void ParseMovv(ref SpanReader reader, uint size, List<WmoVertex> vertices)
    {
        // Mirrors MaNGOS wmo.cpp:235-237 — read exactly `size` bytes, then
        // compute nVertices = size / 12. Reading the exact byte count (not
        // count*12) ensures the reader is at the correct position for the
        // next chunk, even if size is not a multiple of 12.
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
        // Skip any trailing bytes if size is not a multiple of 12
        uint trailing = size - count * 12;
        if (trailing > 0) reader.Skip((int)trailing);
    }

    private void ParseMovi(ref SpanReader reader, uint size, List<WmoTriangle> triangles)
    {
        // Mirrors MaNGOS wmo.cpp — read exactly `size` bytes, then
        // compute nTriangles = size / 6. Skip trailing bytes if needed.
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
        uint trailing = size - count * 6;
        if (trailing > 0) reader.Skip((int)trailing);
    }

    private void ParseMopy(ref SpanReader reader, int size, List<WmoMaterial> materials)
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

    private ushort[] ParseMoba(ref SpanReader reader, int size)
    {
        // Read raw as uint16 array — exactly like C++: MOBA = new uint16[size/2]; f.read(MOBA, size);
        // Each batch = 12 uint16s = 24 bytes. moba_batch = moba_size/12.
        int count = size / 2;
        var raw = new ushort[count];
        for (int i = 0; i < count; i++)
            raw[i] = reader.ReadUInt16();
        return raw;
    }

    private void ParseMobr(ref SpanReader reader, int size, out ushort[] indices)
    {
        int count = size / 2;
        indices = new ushort[count];
        for (int i = 0; i < count; i++)
            indices[i] = reader.ReadUInt16();
    }

    private WmoLiquidData? ParseLiquid(ref SpanReader reader, int size)
    {
        if (size < 30) // C++ WMOLiquidHeader = 0x1E = 30 bytes exactly
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
        // C++ struct WMOLiquidHeader = 30 bytes exactly, no struct padding in file

        // C++ WMOLiquidVert = { uint16 unk1, uint16 unk2, float height } = 8 bytes each
        int heightCount = header.VertexX * header.VertexY;
        int tileCount = header.TileX * header.TileY;

        var heights = new float[heightCount];
        for (int i = 0; i < heightCount; i++)
        {
            reader.Skip(4); // unk1 (uint16) + unk2 (uint16)
            heights[i] = reader.ReadFloat();
        }

        var tileFlags = new byte[tileCount];
        for (int i = 0; i < tileCount; i++)
            tileFlags[i] = reader.ReadByte();

        return new WmoLiquidData(header, heights, tileFlags);
    }

    /// <summary>
    /// WMO root and group files store chunk magics REVERSED on disk
    /// (MaNGOS wmo.cpp::flipcc reverses the 4 bytes after each Read). This
    /// swaps the 4 bytes of <paramref name="reversed"/> back to canonical
    /// "ABCD" order so the value can be compared against MagicBytes constants
    /// (MVER, MOHD, MOGP, MOVT, MOVI, MOPY, MOBA, MOBR, MLIQ, MODR, …).
    /// </summary>
    private static uint ReverseChunkMagic(uint reversed)
    {
        return ((reversed & 0x000000FFu) << 24)
             | ((reversed & 0x0000FF00u) << 8)
             | ((reversed & 0x00FF0000u) >> 8)
             | ((reversed & 0xFF000000u) >> 24);
    }
}