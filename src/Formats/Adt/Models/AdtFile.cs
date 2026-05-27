using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<ushort, ushort> AreaTableLookup = new();
    private static readonly ConcurrentDictionary<ushort, byte> LiquidTypeLookup = new();

    public static void LoadAreaTable(IArchiveReader archive)
    {
        if (!archive.TryReadFile("DBFilesClient\\AreaTable.dbc", out var data)) return;
        var reader = DbcReader<AreaTableRow>.Parse(data.Span);
        foreach (var row in reader.Rows)
        {
            ushort areaId = (ushort)row.Id;
            ushort flags = (ushort)reader.GetUInt32(row, 3); // field 3 = area flags
            AreaTableLookup[areaId] = flags;
        }
    }

    public static void LoadLiquidTypeTable(IArchiveReader archive)
    {
        if (!archive.TryReadFile("DBFilesClient\\LiquidType.dbc", out var data)) return;
        var reader = DbcReader<AreaTableRow>.Parse(data.Span);
        foreach (var row in reader.Rows)
        {
            ushort id = (ushort)row.Id;
            uint soundBank = reader.GetUInt32(row, 3); // field 3 = SoundBank
            byte flag = soundBank switch {
                0 => 0x08, // WATER
                1 => 0x02, // OCEAN
                2 => 0x01, // MAGMA
                3 => 0x04, // SLIME
                _ => 0x08
            };
            LiquidTypeLookup[id] = flag;
        }
    }

    public static byte AreaIdToAreaType(ushort areaId)
    {
        if (!AreaTableLookup.TryGetValue(areaId, out var flags)) return 1;
        if ((flags & 0x40) != 0) return 2;
        if ((flags & 0x80) != 0) return 3;
        if ((flags & 1) != 0) return 1;
        return 0;
    }

    public static ushort AreaIdToAreaFlags(ushort areaId)
        => AreaTableLookup.TryGetValue(areaId, out var f) ? f : (ushort)0xFFFF;

    public static LiquidType LiquidTypeToFlags(ushort rawTypeId)
        => LiquidTypeLookup.TryGetValue(rawTypeId, out var f) ? (LiquidType)f : LiquidType.Water;

    private readonly float[] _heights;
    private readonly ushort[] _areaIds;
    private readonly LiquidData[] _liquids;
    private readonly uint[] _chunkTextureIds; // MCLY: which texture each MCNK uses

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
        uint[] chunkTextureIds)
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
        _chunkTextureIds = chunkTextureIds ?? new uint[256];
    }

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
            return liquid.PrimaryType == LiquidType.Magma ? (byte)3 : (byte)2; // NAV_LAVA : NAV_WATER
        uint texId = _chunkTextureIds[chunkIndex];
        if (texId < TextureNames.Length && IsRoadTexture(TextureNames[(int)texId]))
            return 4; // NAV_ROAD
        return 1; // NAV_GROUND
    }

    /// <summary>Gets the primary texture ID of a MCNK chunk (MCLY index 0).</summary>
    public uint GetChunkTextureId(int chunkIndex) =>
        (uint)(chunkIndex >= 0 && chunkIndex < 256 ? _chunkTextureIds[chunkIndex] : 0);
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