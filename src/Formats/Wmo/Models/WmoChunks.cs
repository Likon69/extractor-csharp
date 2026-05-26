using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Formats.Vmap.Models;

namespace MaNGOS.Extractor.Formats.Wmo.Models;

public struct WmoRootHeader
{
    public const int WotlkVersion = 2;

    public uint TextureCount;
    public uint GroupCount;
    public uint PortalCount;
    public uint LightCount;
    public uint DoodadCount;
    public uint DoodadSetCount;
    public uint WmoId;
    public uint LiquidType;
    public uint BoundingBoxColor;
    public Vector3Min BoundingBoxMin;
    public Vector3Min BoundingBoxMax;
}

public struct WmoGroupHeader
{
    public int GroupNameIndex;
    public int DescGroupNameIndex;
    public uint Flags;
    public Vector3Min BoundingBoxMin;
    public Vector3Min BoundingBoxMax;
    public ushort PortalStart;
    public ushort PortalCount;
    public ushort BatchA;
    public ushort BatchB;
    public uint BatchC;
    public uint FogIndex;
    public uint LiquidType;
    public uint GroupWmoId;

    public const uint FlagIndoor = 0x00000001;
    public const uint FlagOutdoor = 0x00000002;
    public const uint FlagHasLiquids = 0x00000004;
    public const uint FlagRivers = 0x00000008;
    public const uint FlagOcean = 0x00000010;
    public const uint FlagMagma = 0x00000020;
    public const uint FlagSlime = 0x00000040;

    public bool IsIndoor => (Flags & FlagIndoor) != 0;
    public bool HasLiquids => (Flags & FlagHasLiquids) != 0;
}

public struct WmoBatch
{
    public uint Flags;
    public uint FirstIndex;
    public uint IndexCount;
    public uint FirstVertex;
    public uint VertexCount;
    public uint MaterialId;
}

public enum WmoMaterialFlag : byte
{
    None = 0,
    NoCameraCollision = 0x01,
    Detail = 0x02,
    NoCollision = 0x04,
    Hint = 0x08,
    Render = 0x10,
    CollideHit = 0x20,
    WallSurface = 0x40
}

public struct WmoMaterial
{
    public WmoMaterialFlag Flags;
    public int LightMapId;
    public uint ShaderType;

    public bool HasCollision => Flags != WmoMaterialFlag.None
        && (Flags & (WmoMaterialFlag.NoCollision | WmoMaterialFlag.NoCameraCollision)) == 0;

    public bool IsRenderable => (Flags & WmoMaterialFlag.Render) != 0
        || Flags == WmoMaterialFlag.None;
}

public struct WmoLiquidHeader
{
    public int VertexX;
    public int VertexY;
    public int TileX;
    public int TileY;
    public float PositionX;
    public float PositionY;
    public float PositionZ;
    public short LiquidType;
}

public struct WmoLiquidVertex
{
    public ushort Unk1;
    public ushort Unk2;
    public float Height;
}

public struct WmoVertex
{
    public float X, Y, Z;
}

public struct WmoTriangle
{
    public ushort I0, I1, I2;
}

public struct WmoDoodadPlacement
{
    public uint NameIndex;
    public float PositionX;
    public float PositionY;
    public float PositionZ;
    public float RotationY;
    public float RotationX;
    public float RotationZ;
    public float Scale;
    public uint Color;
}

public struct WmoDoodadSet
{
    public string Name;
    public uint FirstDoodadIndex;
    public uint DoodadCount;
    public uint SetId;
}
