using MaNGOS.Extractor.Core.Constants;

namespace MaNGOS.Extractor.Formats.Wmo.Models;

// Simple XYZ vector used by WMO bounding-box headers. Lives in the Wmo.Models
// namespace (not Vmap.Models — that namespace is reserved for the compiled
// vmap file format and was removed when we deleted the dead VMAPt07 writer).
public struct Vector3Min
{
    public float X;
    public float Y;
    public float Z;

    public Vector3Min(float x, float y, float z) { X = x; Y = y; Z = z; }
}

public struct WmoRootHeader
{
    public const int WotlkVersion = 2;

    public uint TextureCount;
    public uint GroupCount;
    public uint PortalCount;
    public uint LightCount;
    public uint DoodadCount;       // nModels  (number of doodad paths in MODN)
    public uint DoodadPlacementCount; // nDoodads (number of placements in MODD) — was missing!
    public uint DoodadSetCount;    // nDoodadSets (number of sets in MODS)
    public uint BoundingBoxColor;  // col (ambient color) — comes BEFORE WmoId in MOHD
    public uint WmoId;
    public Vector3Min BoundingBoxMin;
    public Vector3Min BoundingBoxMax;
    public uint LiquidType;
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
    // Faithful port of MaNGOS wmo.h::MODD: 40 bytes per entry.
    // - NameIndexAndFlags: bits 0..23 = name offset into MODN block,
    //                      bits 24..31 = doodad-specific flags (unused for collision).
    // - Position[3]: local position inside the WMO's coordinate system (NOT fixCoords).
    // - Rotation[4]: quaternion (X, Y, Z, W). The doodad's quaternion is composed
    //                on top of the parent WMO's rotation in Doodad::ExtractSet.
    // - Scale: uniform scale factor applied to the doodad model.
    // - Color: BGRA tint packed in a uint32 (unused for collision).
    public uint NameIndexAndFlags;
    public float PositionX;
    public float PositionY;
    public float PositionZ;
    public float RotationX;
    public float RotationY;
    public float RotationZ;
    public float RotationW;
    public float Scale;
    public uint Color;

    /// <summary>Bits 0..23 of <see cref="NameIndexAndFlags"/> = name offset in MODN.</summary>
    public uint NameIndex => NameIndexAndFlags & 0x00FFFFFFu;
}

public struct WmoDoodadSet
{
    // Faithful port of MaNGOS wmo.h::MODS: 32 bytes per entry.
    // The trailing 4 bytes after Count are Padding in the C++ (unused).
    public string Name;
    public uint FirstDoodadIndex;
    public uint DoodadCount;
    public uint Padding;
}
