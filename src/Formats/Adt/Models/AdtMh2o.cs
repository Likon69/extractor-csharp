namespace MaNGOS.Extractor.Formats.Adt.Models;

public struct AdtMh2o
{
    public const int Version = 1;
    public const int CellCount = 256;
    public AdtMh2oCell[] Cells;
}

public struct AdtMh2oCell
{
    public uint HeaderOffset;
    public uint LayerCount;
    public uint HeightCount;
    public uint VertexCount;

    public bool HasData => HeaderOffset > 0;
}

public struct AdtMh2oHeader
{
    public const ushort NoTypeFlag = 0x0001;
    public const ushort NoHeightFlag = 0x0002;
    public const ushort HasVertexHeightFlag = 0x0004;
    public const ushort HasVertexColorFlag = 0x0008;

    public byte OffsetX;
    public byte OffsetY;
    public byte Width;
    public byte Height;
    public ushort LiquidType;
    public ushort Flags;
    public float MinHeight;
    public float MaxHeight;

    public bool HasLiquidType => (Flags & NoTypeFlag) == 0;
    public bool HasHeightValues => (Flags & NoHeightFlag) == 0;
    public bool HasVertexHeight => (Flags & HasVertexHeightFlag) != 0;
    public bool HasVertexColor => (Flags & HasVertexColorFlag) != 0;
}

public enum LiquidType : ushort
{
    None = 0,
    Water = 0x01,
    Ocean = 0x02,
    Magma = 0x04,
    Slime = 0x08,
    Mask = 0x0F
}

public struct LiquidData
{
    public float LiquidLevel;
    public LiquidType PrimaryType;
    public byte OffsetX;
    public byte OffsetY;
    public byte Width;
    public byte Height;
    public float[]? DepthMap;
    public (byte R, byte G, byte B, byte A)[]? VertexColors;

    public static LiquidData Empty => default;
    public bool HasLiquid => PrimaryType != LiquidType.None;
    public bool HasVertexHeight => DepthMap != null;
    public bool HasVertexColor => VertexColors != null;
}