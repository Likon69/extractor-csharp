namespace MaNGOS.Extractor.Core.Constants;

/// <summary>
/// World of Warcraft 3.3.5a (build 12340) client constants.
/// All spatial values are in world units.
/// </summary>
public static class WowConstants
{
    /// <summary>Size of one ADT tile in world units.</summary>
    public const float TileSize = 533.333333f;

    /// <summary>Number of MCNK chunks per ADT row/column (16×16 = 256 per tile).</summary>
    public const int ChunksPerTile = 16;

    /// <summary>Size of one MCNK chunk in world units.</summary>
    public const float ChunkSize = TileSize / ChunksPerTile;

    /// <summary>Half-width of the entire world in world units (32 tiles).</summary>
    public const float MapHalfSize = 17066.666f;

    /// <summary>Number of ADT tiles per map side (64×64 grid).</summary>
    public const int GridSize = 64;

    /// <summary>Target client build for WotLK 3.3.5a.</summary>
    public const uint TargetBuild = 12340u;

    /// <summary>All supported client builds (Classic/TBC/WotLK/Cata/MoP).</summary>
    public static readonly uint[] SupportedBuilds = { 5875, 6005, 6141, 8606, 12340, 15595, 18414 };

    /// <summary>Total MCNK chunks per ADT tile.</summary>
    public const int TotalChunks = ChunksPerTile * ChunksPerTile;

    /// <summary>Vertices per MCNK side (9×9 grid).</summary>
    public const int MCNKVerticesSide = 9;

    /// <summary>Total heightmap vertices per MCNK (9×9 + 8×8 = 145).</summary>
    public const int MCNKVertexCount = 145;

    /// <summary>Minimum world coordinate (origin of the map).</summary>
    public const float MinWorldCoord = -MapHalfSize;

    /// <summary>Maximum world coordinate.</summary>
    public const float MaxWorldCoord = MapHalfSize;

    /// <summary>Scale factor for converting raw height values (int16→float).</summary>
    public const float HeightScale = 0.02037f;

    /// <summary>Default walkable height in world units (11 cells × cellHeight).</summary>
    public const float DefaultWalkableHeight = 2.22155f;

    /// <summary>Default walkable radius in world units.</summary>
    public const float DefaultWalkableRadius = 0.400f;

    /// <summary>Default walkable climb in world units (5 cells × cellHeight).</summary>
    public const float DefaultWalkableClimb = 1.0f;

    /// <summary>Maximum vertices allowed per navmesh polygon.</summary>
    public const int MaxVerticesPerPolygon = 6;

    /// <summary>Default detail sample distance multiplier.</summary>
    public const float DefaultDetailSampleDist = 4.84848f;

    /// <summary>Default detail sample maximum error.</summary>
    public const float DefaultDetailSampleMaxError = 0.2f;

    // MMAP 4×4 ARCHITECTURE

    /// <summary>Sub-tiles per ADT side.</summary>
    public const int SubTilesPerAdtSide = 4;

    /// <summary>Size of a sub-tile in world units.</summary>
    public const float SubTileSize = TileSize / SubTilesPerAdtSide;

    /// <summary>Recast cells per sub-tile (subTileSize / cellSize).</summary>
    public const int CellsPerSubTile = 440;

    /// <summary>Internal Recast vertices per sub-tile side.</summary>
    public const int VertexPerSubTile = 40;

    // MAP DIRECTORY NAMES

    /// <summary>Converts a map ID to its directory name.</summary>
    public static string GetMapDirectory(uint mapId) => mapId switch
    {
        0 => "Azeroth",
        1 => "Kalimdor",
        530 => "Outland",
        571 => "Northrend",
        _ => $"Map{mapId:D4}"
    };
}