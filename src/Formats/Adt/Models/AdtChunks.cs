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
    public const int HeaderSize = 144;
    public const uint HolesHighResolutionMask = 0x20000000;

    public uint GridX;
    public uint GridY;
    public uint McvtOffset;
    public uint McnrOffset;
    public uint MclyOffset;
    public uint McrfOffset;
    public uint Unused1;
    public uint MclvOffset;
    public uint AreaId;
    public uint DoodleDbId;
    public uint Holes;
    public ushort LowQualityGridHeight;
    public ushort NumLayers;
    public uint NumDoodadRefs;
    public uint DoodadReferencesOffset;
    public uint MtxpOffset;
    public uint MtxpSize;
    public uint McnrOffset2;
    public uint McnrSize;
    public float PositionX, PositionY, PositionZ;
    public uint DoodadFlags;
    public uint TextureId0;
    public uint TextureId1;
    public uint Unknown1;
    public uint Unknown2;

    public bool HasHighResHoles => (DoodadFlags & HolesHighResolutionMask) != 0;
    public uint HolesMask => Holes & 0xFFFF;
}

public struct AdtMcvt
{
    public const int VerticesSide = 9;
    public const int TotalVertices = 145;
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
    public float Scale;
    public ushort Flags;
}

public struct AdtModf
{
    public uint NameId;
    public uint UniqueId;
    public float PositionX;
    public float PositionY;
    public float PositionZ;
    public float RotationY;
    public float RotationX;
    public float RotationZ;
    public float ScaleX;
    public float ScaleY;
    public float ScaleZ;
    public ushort Flags;
    public ushort DoodadSet;
    public uint GroupIds;
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
