using MaNGOS.Extractor.Core.Constants;

namespace MaNGOS.Extractor.Formats.Adt.Models;

public struct AdtMhdr
{
    public const int ExpectedVersion = 11;

    public uint FileDataId;
    public uint Flags;
    public uint McinOffset;
    public uint MtexOffset;
    public uint MmdxOffset;
    public uint MmidOffset;
    public uint MwmoOffset;
    public uint MwidOffset;
    public uint MddfOffset;
    public uint ModfOffset;
    public uint MfboOffset;
    public uint Mh2oOffset;
    public uint MtxpOffset;
    public uint MtxpSize;
    public uint Unused1, Unused2, Unused3;
}

public struct AdtMcin
{
    public uint Offset;
    public uint Size;
    public uint Flags;
    public uint AsyncId;
}

public struct AdtMcnk
{
    public const int HeaderSize = 128; // 0x80 bytes per WotLK spec
    public const uint FlagHighResHoles = 0x10000;

    public uint Flags;           // 0x00
    public uint IndexX;          // 0x04
    public uint IndexY;          // 0x08
    public uint NLayers;         // 0x0C  number of texture layers
    public uint NDoodadRefs;     // 0x10  number of doodad references
    public uint OfsHeight;       // 0x14  offset to MCVT (from MCNK magic start)
    public uint OfsNormal;       // 0x18  offset to MCNR
    public uint OfsLayer;        // 0x1C  offset to MCLY
    public uint OfsRefs;         // 0x20  offset to MCRF
    public uint OfsAlpha;        // 0x24  offset to MCAL
    public uint SizeAlpha;       // 0x28
    public uint OfsShadow;       // 0x2C  offset to MCSH
    public uint SizeShadow;      // 0x30
    public uint AreaId;          // 0x34  zone/area ID
    public uint NMapObjRefs;     // 0x38
    public uint Holes;           // 0x3C  low-res holes bitmask
    public ushort LowQualityTex0, LowQualityTex1, LowQualityTex2, LowQualityTex3; // 0x40–0x46
    public ushort LowQualityTex4, LowQualityTex5, LowQualityTex6, LowQualityTex7; // 0x48–0x4E
    public uint PredTex;         // 0x50  (WotLK predTex, unused)
    public uint NEffectDoodad;   // 0x54
    public uint OfsSndEmitters;  // 0x58
    public uint NSndEmitters;    // 0x5C
    public uint OfsLiquid;       // 0x60  offset to MCLQ
    public uint SizeLiquid;      // 0x64
    public float Zpos;           // 0x68  world north-south (WoW X)
    public float Xpos;           // 0x6C  world east-west   (WoW Y)
    public float Ypos;           // 0x70  world altitude = height base for MCVT values
    public uint OfsMCCV;         // 0x74
    public uint Unused1;         // 0x78
    public uint Unused2;         // 0x7C  (struct total = 128 bytes = 0x80)

    public bool HasHighResHoles => (Flags & FlagHighResHoles) != 0;
    public uint HolesMask => Holes & 0xFFFF;
}

public struct AdtMcvt
{
    public const int V9PerRow = 9;
    public const int V8PerRow = 8;
    public const int TotalVertices = V9PerRow * V9PerRow + V8PerRow * V8PerRow;
    public const int Stride = V9PerRow + V8PerRow;

    public static float GetV9(ReadOnlySpan<float> chunkData, int z, int x) =>
        chunkData[z * Stride + x];

    public static float GetV8(ReadOnlySpan<float> chunkData, int z, int x) =>
        chunkData[z * Stride + V9PerRow + x];
}

public struct AdtMcnr
{
    public const int TotalNormals = 145;
}

public struct AdtMcly
{
    public const uint FlagAnimated = 0x00000001;
    public const uint FlagSrgb = 0x00000002;
    public const uint FlagCompressed = 0x00000004;

    public uint TextureId;
    public uint Level;
    public uint Flags;
    public uint Offset1;
    public uint AlphaOffset;
    public uint EffectId;
}

public struct AdtMcrf
{
    public uint Count;
    public uint[] FileDataIds;
}

public struct AdtMddf
{
    public uint NameId;
    public uint FileDataId;
    public float PositionX;
    public float PositionY;
    public float PositionZ;
    public float RotationY;
    public float RotationX;
    public float RotationZ;
    public ushort Scale;
    public ushort Flags;
}

public struct AdtModf
{
    public uint NameId;
    public uint UniqueId;
    public float PositionX;
    public float PositionY;
    public float PositionZ;
    public float RotationX;
    public float RotationY;
    public float RotationZ;
    public float LowerBoundsX;
    public float LowerBoundsY;
    public float LowerBoundsZ;
    public float UpperBoundsX;
    public float UpperBoundsY;
    public float UpperBoundsZ;
    public float ScaleX;
    public float ScaleY;
    public float ScaleZ;
    public ushort Flags;
    public ushort DoodadSet;
    public ushort NameSet;
    public ushort Scale;
}

public struct AdtMfbo
{
    public (float X, float Y, float Z) BoxMin;
    public (float X, float Y, float Z) BoxMax;
    public uint LowerVertexCount;
    public uint UpperVertexCount;
    public (short X, short Y, short Z)[] LowerVertices;
    public (short X, short Y, short Z)[] UpperVertices;
}
