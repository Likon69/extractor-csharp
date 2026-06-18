using System.Collections.Concurrent;
using MaNGOS.Extractor.Core.Binary;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;
using MaNGOS.Extractor.Formats.Dbc;

namespace MaNGOS.Extractor.Formats.Adt.Models;

/// <summary>
/// Represents a fully parsed ADT terrain tile (16x16 units of the game world).
/// Contains heightmaps, textures, liquids, and object placements for one .adt file.
/// </summary>
public sealed class AdtFile
{
    private static readonly ConcurrentDictionary<ushort, (uint Flags, uint Team)> AreaTableLookup = new();
    private static readonly ConcurrentDictionary<ushort, byte> LiquidTypeLookup = new();

    public static void LoadAreaTable(IArchiveReader archive)
    {
        if (!archive.TryReadFile("DBFilesClient\\AreaTable.dbc", out var data)) return;
        var reader = DbcReader<AreaTableRow>.Parse(data.Span);
        foreach (var row in reader.Rows)
        {
            ushort areaId = (ushort)row.Id;
            // P0 FIX: Mangos C++ System.cpp uses getUInt(3) for the area bit/flag
            // (line "areas[dbc.getRecord(x).getUInt(0)] = dbc.getRecord(x).getUInt(3);").
            // The C# DbcReader is 0-indexed like the C++ getUInt(), so field 3 (0-indexed)
            // is the same byte offset the C++ reads.
            uint flags = reader.GetUInt32(row, 3);
            uint team = reader.GetUInt32(row, 28);
            AreaTableLookup[areaId] = (flags, team);
        }
    }

    public static void LoadLiquidTypeTable(IArchiveReader archive)
    {
        if (!archive.TryReadFile("DBFilesClient\\LiquidType.dbc", out var data)) return;
        // Read raw DBC data: the LiquidTypeRow struct only contains Id (4 bytes)
        // but field 3 (SoundBank) lives at offset 12 of the record. The DbcReader<>
        // indexer would read padding bytes instead. We mirror the C++ DBCFile::getUInt(3)
        // by reading the raw record bytes directly.
        var reader = new SpanReader(data.Span);
        uint magic = reader.ReadUInt32();
        if (magic != MagicBytes.DbcMagic) return;
        uint recordCount = reader.ReadUInt32();
        uint fieldCount = reader.ReadUInt32();
        uint rowSize = reader.ReadUInt32();
        reader.Skip(4); // stringBlockSize
        for (uint i = 0; i < recordCount; i++)
        {
            int recordPos = reader.Position;
            // Each DBC field is 4 bytes. field 0 = Id, field 3 = SoundBank.
            // MaNGOS C++ System.cpp::ReadLiquidTypeTableDBC uses getUInt(3).
            uint id = reader.ReadUInt32();
            reader.Skip(8); // skip fields 1, 2
            uint soundBank = reader.ReadUInt32(); // field 3
            // Skip remaining fields in this record
            reader.Seek(recordPos + (int)rowSize);

            // WOTLK MAP_LIQUID_TYPE_* values (System.cpp CLIENT_WOTLK branch):
            //   SoundBank 0 (WATER) -> 0x01, 1 (OCEAN) -> 0x02, 2 (MAGMA) -> 0x04, 3 (SLIME) -> 0x08
            byte flag = soundBank switch {
                0 => 0x01, // WATER
                1 => 0x02, // OCEAN
                2 => 0x04, // MAGMA
                3 => 0x08, // SLIME
                _ => 0x01  // default = WATER
            };
            LiquidTypeLookup[(ushort)id] = flag;
        }
    }

    public static byte AreaIdToAreaType(ushort areaId)
    {
        // All walkable terrain is NAV_GROUND (1), matching the original MaNGOS extractor.
        // NAV_HORDE (15) and NAV_ALLIANCE (16) are not standard HonorBuddy area types;
        // using them breaks cross-tile pathfinding because the viewer's dtQueryFilter
        // has no defined traversal cost for those area IDs.
        return 1; // NAV_GROUND
    }

    public static uint AreaIdToAreaFlags(ushort areaId)
        => AreaTableLookup.TryGetValue(areaId, out var area) ? area.Flags : 0xFFFFFFFFu;

    public static LiquidType LiquidTypeToFlags(ushort rawTypeId)
        => LiquidTypeLookup.TryGetValue(rawTypeId, out var f) ? (LiquidType)f : LiquidType.Water;

    private readonly float[] _heights;
    private readonly ushort[] _areaIds;
    private readonly LiquidData[] _liquids;
    private readonly LiquidData[] _mclqs;
    private readonly uint[] _chunkTextureIds; // MCLY: which texture each MCNK uses
    private readonly ushort[] _chunkHoles;    // P0 FIX: MCNK.holes per chunk (mirrors C++ map_fileheader.holes[16][16])
    // BUG-009: per-chunk MCNK metadata needed to reconstruct global V8/V9 exactly
    // like C++ System.cpp (init = ypos, += MCVT only when HasMcvt; absent chunks
    // leave the global V8/V9 untouched — stale data between tiles).
    private readonly float[] _chunkYpos;       // MCNK.Ypos (world altitude, height base) per chunk
    private readonly bool[] _chunkHasMcvt;     // MCNK.OfsHeight > 0 (MCVT present) per chunk
    private readonly bool[] _chunkPresent;     // chunk present in ADT (MCIN.Offset != 0) per chunk

    /// <summary>Map ID this tile belongs to.</summary>
    public uint MapId { get; }

    /// <summary>X index of this tile in the 64x64 map grid.</summary>
    public int TileX { get; }

    /// <summary>Y index of this tile in the 64x64 map grid.</summary>
    public int TileY { get; }

    /// <summary>Original file path from the archive.</summary>
    public string FilePath { get; }

    /// <summary>MHDR chunk data (offsets to sub-chunks).</summary>
    public AdtMhdr Header { get; }

    /// <summary>MCIN entries (256 MCNK offsets and sizes).</summary>
    public AdtMcin[] McinEntries { get; }

    /// <summary>MFBO data (flight bounds, may be empty).</summary>
    public AdtMfbo? FlightBounds { get; }

    /// <summary>Texture filenames from MTEX chunk.</summary>
    public string[] TextureNames { get; }

    /// <summary>WMO filenames from MWMO chunk.</summary>
    public string[] WmoNames { get; }

    /// <summary>M2/Model filenames from MMDX chunk.</summary>
    public string[] ModelNames { get; }

    /// <summary>MDDF entries (doodad placements).</summary>
    public AdtMddf[] DoodadPlacements { get; }

    /// <summary>MODF entries (WMO placements).</summary>
    public AdtModf[] WmoPlacements { get; }

    /// <summary>
    /// Gets the height value for a specific chunk and vertex.
    /// </summary>
    /// <param name="chunkIndex">MCNK chunk index (0-255).</param>
    /// <param name="vertexIndex">Vertex index within the chunk (0-144).</param>
    /// <returns>Height in world units, or 0 if out of range.</returns>
    public float GetHeight(int chunkIndex, int vertexIndex)
    {
        int offset = chunkIndex * AdtMcvt.TotalVertices + vertexIndex;
        return offset < _heights.Length ? _heights[offset] : 0f;
    }

    /// <summary>
    /// Gets V9 (outer) heights for a specific MCNK chunk for grid (z,x).
    /// </summary>
    public static float GetV8(ReadOnlySpan<float> chunkData, int z, int x) =>
        chunkData[z * AdtMcvt.Stride + AdtMcvt.V9PerRow + x];

    /// <summary>
    /// Gets all heights for a specific MCNK chunk.
    /// </summary>
    /// <param name="chunkIndex">MCNK chunk index (0-255).</param>
    /// <returns>Span of height values for this chunk.</returns>
    /// <remarks>Returned span is valid only while the AdtFile is alive. Do not store.</remarks>
    public Span<float> GetChunkHeights(int chunkIndex)
    {
        int start = chunkIndex * AdtMcvt.TotalVertices;
        return _heights.AsSpan(start, AdtMcvt.TotalVertices);
    }

    /// <summary>
    /// Gets the area ID for a specific chunk.
    /// </summary>
    /// <param name="chunkIndex">MCNK chunk index (0-255).</param>
    /// <returns>Area ID from AreaTable.dbc, or 0 if unknown.</returns>
    public ushort GetAreaId(int chunkIndex)
    {
        return chunkIndex < _areaIds.Length ? _areaIds[chunkIndex] : (ushort)0;
    }

    /// <summary>
    /// Gets all 256 area IDs as a span.
    /// </summary>
    public ReadOnlySpan<ushort> GetAllAreaIds() => _areaIds;

    /// <summary>
    /// Gets liquid data for a specific chunk.
    /// </summary>
    /// <param name="chunkIndex">MCNK chunk index (0-255).</param>
    /// <returns>LiquidData (may be empty/default).</returns>
    public ref readonly LiquidData GetLiquidData(int chunkIndex)
    {
        return ref _liquids[chunkIndex < 256 ? chunkIndex : 0];
    }

    /// <summary>
    /// Gets legacy MCLQ liquid data for a specific chunk (MaNGOS C++ System.cpp lignes 827-893).
    /// MCLQ is the "old" TBC liquid format still embedded in some WotLK MCNK chunks. The C++ processes
    /// it BEFORE MH2O and the final liquid_show is the OR of both. Empty if no MCLQ data for this chunk.
    /// </summary>
    public ref readonly LiquidData GetMclqData(int chunkIndex)
    {
        return ref _mclqs[chunkIndex < 256 ? chunkIndex : 0];
    }

    /// <summary>
    /// Gets the world-space height at a specific position.
    /// </summary>
    /// <param name="worldX">World X coordinate.</param>
    /// <param name="worldZ">World Z coordinate.</param>
    /// <returns>Interpolated height, or float.MinValue if outside tile.</returns>
    public float SampleHeight(float worldX, float worldZ)
    {
        // Local position within tile
        float localX = worldX - (TileX * WowConstants.TileSize - WowConstants.MapHalfSize);
        float localZ = worldZ - (TileY * WowConstants.TileSize - WowConstants.MapHalfSize);

        if (localX < 0 || localX > WowConstants.TileSize || localZ < 0 || localZ > WowConstants.TileSize)
            return float.MinValue;

        // Chunk coordinates
        float chunkF = localX / WowConstants.ChunkSize;
        float chunkF2 = localZ / WowConstants.ChunkSize;
        int chunkX = (int)chunkF;
        int chunkY = (int)chunkF2;

        // Vertex coordinates within chunk
        float vertexX = (chunkF - chunkX) * 8f; // 8 units per vertex
        float vertexY = (chunkF2 - chunkY) * 8f;

        int chunkIndex = chunkY * WowConstants.ChunksPerTile + chunkX;
        Span<float> heights = GetChunkHeights(chunkIndex);

        // Bilinear interpolation on the 9x9 grid
        int vx0 = (int)MathF.Floor(vertexX);
        int vy0 = (int)MathF.Floor(vertexY);
        int vx1 = Math.Min(vx0 + 1, 8);
        int vy1 = Math.Min(vy0 + 1, 8);

        float fx = vertexX - vx0;
        float fy = vertexY - vy0;

        // H0 = top-left, H1 = top-right, H2 = bottom-left, H3 = bottom-right
        float h00 = AdtMcvt.GetV9(heights, vy0, vx0);
        float h10 = AdtMcvt.GetV9(heights, vy0, vx1);
        float h01 = AdtMcvt.GetV9(heights, vy1, vx0);
        float h11 = AdtMcvt.GetV9(heights, vy1, vx1);

        return h00 * (1 - fx) * (1 - fy) +
               h10 * fx * (1 - fy) +
               h01 * (1 - fx) * fy +
               h11 * fx * fy;
    }

    internal AdtFile(
        uint mapId, int tileX, int tileY, string filePath, AdtMhdr header,
        AdtMcin[] mcinEntries, AdtMfbo? flightBounds,
        string[] textures, string[] wmos, string[] models,
        AdtMddf[] doodads, AdtModf[] wmosPlacements,
        float[] heights, ushort[] areaIds, LiquidData[] liquids,
        LiquidData[] mclqs,
        uint[] chunkTextureIds, ushort[] chunkHoles,
        float[] chunkYpos, bool[] chunkHasMcvt, bool[] chunkPresent)
    {
        MapId = mapId;
        TileX = tileX;
        TileY = tileY;
        FilePath = filePath;
        Header = header;
        McinEntries = mcinEntries;
        FlightBounds = flightBounds;
        TextureNames = textures;
        WmoNames = wmos;
        ModelNames = models;
        DoodadPlacements = doodads;
        WmoPlacements = wmosPlacements;
        _heights = heights;
        _areaIds = areaIds;
        _liquids = liquids;
        _mclqs = mclqs ?? new LiquidData[256];
        _chunkTextureIds = chunkTextureIds ?? new uint[256];
        _chunkHoles = chunkHoles ?? new ushort[256];
        _chunkYpos = chunkYpos ?? new float[256];
        _chunkHasMcvt = chunkHasMcvt ?? new bool[256];
        _chunkPresent = chunkPresent ?? new bool[256];
    }

    /// <summary>MCNK.Ypos (world altitude = MCVT height base) for the given chunk. BUG-009.</summary>
    public float GetChunkYpos(int chunkIndex)
        => chunkIndex >= 0 && chunkIndex < 256 ? _chunkYpos[chunkIndex] : 0f;

    /// <summary>True when the chunk has an MCVT sub-chunk (MCNK.OfsHeight > 0). BUG-009.</summary>
    public bool GetChunkHasMcvt(int chunkIndex)
        => chunkIndex >= 0 && chunkIndex < 256 && _chunkHasMcvt[chunkIndex];

    /// <summary>True when the chunk is present in the ADT (MCIN.Offset != 0).
    /// Absent chunks must leave the global V8/V9 grids untouched in MapFileWriter
    /// to reproduce the C++ stale-data behavior (System.cpp:516-517, 622-625). BUG-009.</summary>
    public bool GetChunkPresent(int chunkIndex)
        => chunkIndex >= 0 && chunkIndex < 256 && _chunkPresent[chunkIndex];

    /// <summary>Returns true if the given texture name contains road-like patterns.</summary>
    public static bool IsRoadTexture(string textureName)
    {
        if (string.IsNullOrEmpty(textureName)) return false;
        string lower = textureName.ToLowerInvariant();
        return lower.Contains("road") || lower.Contains("cobblestone")
            || lower.Contains("path_stone") || lower.Contains("bridgefloor");
    }

    /// <summary>Returns the NavTerrain area type for a chunk (NAV_GROUND, NAV_WATER, or NAV_ROAD).</summary>
    /// <param name="chunkIndex">MCNK chunk index (0-255).</param>
    public byte GetChunkAreaType(int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= 256)
            return 1;

        ref readonly var liquid = ref GetLiquidData(chunkIndex);
        if (liquid.HasLiquid)
            return liquid.PrimaryType is LiquidType.Magma or LiquidType.Slime ? (byte)3 : (byte)2; // NAV_LAVA : NAV_WATER
        uint texId = _chunkTextureIds[chunkIndex];
        if (texId < TextureNames.Length && IsRoadTexture(TextureNames[(int)texId]))
            return 4; // NAV_ROAD
        return 1; // NAV_GROUND
    }

    /// <summary>Gets the primary texture ID of a MCNK chunk (MCLY index 0).</summary>
    public uint GetChunkTextureId(int chunkIndex) =>
        (uint)(chunkIndex >= 0 && chunkIndex < 256 ? _chunkTextureIds[chunkIndex] : 0);

    /// <summary>
    /// Gets the MCNK holes bitmask for a specific chunk (P0 FIX: was previously
    /// never populated, causing MapFileWriter to write all zeros). The 16-bit
    /// bitmask indicates which of the 8×8 sub-cells inside the chunk are
    /// "holes" (no ground) — the same value the C++ map-extractor writes to
    /// <c>map_fileheader.holes</c>.
    /// </summary>
    public ushort GetChunkHoles(int chunkIndex)
        => (ushort)(chunkIndex >= 0 && chunkIndex < 256 ? _chunkHoles[chunkIndex] : 0);
}

/// <summary>
/// Result of parsing an ADT file, containing the parsed tile and any warnings.
/// </summary>
/// <param name="Tile">The parsed ADT tile, or null if parsing failed.</param>
/// <param name="Warnings">List of non-fatal issues encountered during parsing.</param>
public readonly record struct AdtParseResult(AdtFile? Tile, List<string> Warnings)
{
    /// <summary>Whether parsing succeeded.</summary>
    public bool Success => Tile != null;

    /// <summary>Creates a failed result.</summary>
    public static AdtParseResult Failed(List<string> warnings) => new(null, warnings);

    /// <summary>Creates a successful result.</summary>
    public static AdtParseResult Ok(AdtFile tile) => new(tile, new List<string>());
}
