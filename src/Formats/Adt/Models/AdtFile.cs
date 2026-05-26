using MaNGOS.Extractor.Core.Constants;

namespace MaNGOS.Extractor.Formats.Adt.Models;

/// <summary>
/// Represents a fully parsed ADT terrain tile (16x16 units of the game world).
/// Contains heightmaps, textures, liquids, and object placements for one .adt file.
/// </summary>
public sealed class AdtFile
{
    private readonly float[] _heights;
    private readonly ushort[] _areaIds;
    private readonly LiquidData[] _liquids;

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
    /// Gets all 145 heights for a specific chunk.
    /// </summary>
    /// <param name="chunkIndex">MCNK chunk index (0-255).</param>
    /// <returns>Span of 145 height values.</returns>
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
        float h00 = heights[vy0 * 9 + vx0];
        float h10 = heights[vy0 * 9 + vx1];
        float h01 = heights[vy1 * 9 + vx0];
        float h11 = heights[vy1 * 9 + vx1];

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
        float[] heights, ushort[] areaIds, LiquidData[] liquids)
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
    }
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