namespace MaNGOS.Extractor.Formats.Vmap.Models;

public struct VmapTileHeader
{
    public static ReadOnlySpan<byte> Magic => "VMAPt07"u8;
    public readonly uint GroupCount;
    public readonly uint BuildNumber;
}

public struct VmapGroupData
{
    public string Name;
    public uint Flags;
    public Vector3Min BoundingBoxMin;
    public Vector3Min BoundingBoxMax;
    public uint LiquidType;
}

public struct Vector3Min
{
    public float X, Y, Z;

    public Vector3Min(float x, float y, float z) => (X, Y, Z) = (x, y, z);
}

public sealed class VmapTile
{
    private readonly List<VmapGroupData> _groups = new();
    private readonly List<VmapModelPlacement> _models = new();

    public uint MapId { get; }
    public int TileX { get; }
    public int TileY { get; }
    public VmapGroupData[] Groups => _groups.ToArray();
    public VmapModelPlacement[] Models => _models.ToArray();

    internal VmapTile(uint mapId, int tileX, int tileY)
    {
        MapId = mapId;
        TileX = tileX;
        TileY = tileY;
    }

    public void AddGroup(VmapGroupData group) => _groups.Add(group);
    public void AddModel(VmapModelPlacement model) => _models.Add(model);
}

public struct VmapModelPlacement
{
    public string Name;
    public float PositionX;
    public float PositionY;
    public float PositionZ;
    public float RotationY;
    public float RotationX;
    public float RotationZ;
    public float Scale;
    public uint Flags;
}
