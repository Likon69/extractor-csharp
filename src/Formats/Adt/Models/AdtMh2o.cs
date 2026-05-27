namespace MaNGOS.Extractor.Formats.Adt.Models;

public struct AdtMh2o
{
    public const int Version = 1;
    public const int CellCount = 256;
    public AdtMh2oCell[] Cells;
    /// <summary>File offset of the first byte after the MH2O magic+size header (= base for all inner offsets).</summary>
    public uint ChunkDataStart;
}

public struct AdtMh2oCell
{
    /// <summary>ofsInformation: offset from ChunkDataStart to the SLiquidInstance, 0 if cell has no liquid.</summary>
    public uint HeaderOffset;
    /// <summary>nLayers: number of liquid layers in this cell.</summary>
    public uint LayerCount;
    /// <summary>ofsRenderMask: offset to render mask data (was erroneously named HeightCount).</summary>
    public uint RenderMaskOffset;

    public bool HasData => HeaderOffset > 0;
}

/// <summary>
/// SLiquidInstance — WotLK MH2O per-cell liquid descriptor.
/// Field order MUST match the binary layout in the ADT file.
/// </summary>
public struct AdtMh2oHeader
{
    // Offsets used to skip unknown sub-chunks
    public ushort LiquidType;     // LiquidType.dbc row ID
    public ushort VertexFormat;   // 0=height_depth, 2=height_depth_uv
    public float  MinHeight;
    public float  MaxHeight;
    public byte   OffsetX;        // x_offset within cell (0–7)
    public byte   OffsetY;        // y_offset within cell (0–7)
    public byte   Width;          // number of columns (1–8)
    public byte   Height;         // number of rows (1–8)
    /// <summary>Offset from ChunkDataStart to the height float array, 0 if flat.</summary>
    public uint   OfsHeightMap;
    /// <summary>Offset from ChunkDataStart to the 8-byte info mask, 0 if all visible.</summary>
    public uint   OfsInfoMask;
}

/// <summary>
/// Liquid type flags written to the .map file.
/// Values MUST match C++ MAP_LIQUID_TYPE_* constants.
/// </summary>
public enum LiquidType : ushort
{
    None  = 0,
    Magma = 0x01,   // MAP_LIQUID_TYPE_MAGMA
    Ocean = 0x02,   // MAP_LIQUID_TYPE_OCEAN
    Slime = 0x04,   // MAP_LIQUID_TYPE_SLIME
    Water = 0x08,   // MAP_LIQUID_TYPE_WATER
    Mask  = 0x0F
}

public struct LiquidData
{
    public float LiquidLevel;
    public LiquidType PrimaryType;
    /// <summary>Raw LiquidType.dbc row ID (unmodified from MH2O header).</summary>
    public ushort RawTypeId;
    public byte OffsetX;
    public byte OffsetY;
    public byte Width;
    public byte Height;
    public float[]? DepthMap;
    /// <summary>64-bit show-mask: bit at [y*Width+x] = 1 means sub-cell is visible.</summary>
    public ulong ShowMask;
    public (byte R, byte G, byte B, byte A)[]? VertexColors;

    public static LiquidData Empty => default;
    public bool HasLiquid => PrimaryType != LiquidType.None;
    public bool HasVertexHeight => DepthMap != null;
    public bool HasVertexColor => VertexColors != null;
}