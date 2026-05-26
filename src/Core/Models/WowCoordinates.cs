using System.Numerics;
using MaNGOS.Extractor.Core.Constants;

namespace MaNGOS.Extractor.Core.Models;

/// <summary>
/// WoW world coordinate system conversion utilities.
/// WoW uses X=East/West, Y=Altitude, Z=North/South (right-hand, Y-up).
/// Tile origin is at the southwest corner (min X, min Z).
/// </summary>
public static class CoordConverter
{
    private const float HalfTileSize = WowConstants.TileSize / 2f;

    /// <summary>
    /// Converts tile indices to world origin (southwest corner of the tile).
    /// </summary>
    public static (float X, float Z) TileToWorld(int tileX, int tileY)
    {
        float x = tileX * WowConstants.TileSize - WowConstants.MapHalfSize;
        float z = tileY * WowConstants.TileSize - WowConstants.MapHalfSize;
        return (x, z);
    }

    /// <summary>
    /// Converts tile indices to world center point.
    /// </summary>
    public static (float X, float Z) TileToWorldCenter(int tileX, int tileY)
    {
        float x = tileX * WowConstants.TileSize - WowConstants.MapHalfSize + HalfTileSize;
        float z = tileY * WowConstants.TileSize - WowConstants.MapHalfSize + HalfTileSize;
        return (x, z);
    }

    /// <summary>
    /// Converts world XZ coordinates to tile indices.
    /// </summary>
    public static (int TileX, int TileY) WorldToTile(float x, float z)
    {
        int tileX = (int)MathF.Floor((x + WowConstants.MapHalfSize) / WowConstants.TileSize);
        int tileY = (int)MathF.Floor((z + WowConstants.MapHalfSize) / WowConstants.TileSize);

        tileX = Math.Clamp(tileX, 0, WowConstants.GridSize - 1);
        tileY = Math.Clamp(tileY, 0, WowConstants.GridSize - 1);

        return (tileX, tileY);
    }

    /// <summary>
    /// Checks if world coordinates are within map bounds.
    /// </summary>
    public static bool IsInBounds(float x, float z)
    {
        return x >= WowConstants.MinWorldCoord && x <= WowConstants.MaxWorldCoord
            && z >= WowConstants.MinWorldCoord && z <= WowConstants.MaxWorldCoord;
    }

    /// <summary>
    /// Calculates distance in world units between two XZ points.
    /// </summary>
    public static float XZDistance(Vector2 a, Vector2 b)
    {
        Vector2 delta = a - b;
        return delta.Length();
    }

    /// <summary>
    /// Converts sub-tile coords to world origin.
    /// </summary>
    public static (float X, float Z) SubTileToWorld(int adtX, int adtY, int subX, int subY)
    {
        float tileOriginX = adtX * WowConstants.TileSize - WowConstants.MapHalfSize;
        float tileOriginZ = adtY * WowConstants.TileSize - WowConstants.MapHalfSize;

        float subOriginX = tileOriginX + subX * WowConstants.SubTileSize;
        float subOriginZ = tileOriginZ + subY * WowConstants.SubTileSize;

        return (subOriginX, subOriginZ);
    }
}

/// <summary>
/// Represents a 2D position in world space.
/// </summary>
public readonly struct WorldPosition(float x, float z)
{
    public float X { get; } = x;
    public float Z { get; } = z;

    public readonly int TileX => (int)MathF.Floor((X + WowConstants.MapHalfSize) / WowConstants.TileSize);
    public readonly int TileY => (int)MathF.Floor((Z + WowConstants.MapHalfSize) / WowConstants.TileSize);

    public override readonly string ToString() => $"({X:F2}, {Z:F2})";
}