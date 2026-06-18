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
/// Values MUST match C++ MAP_LIQUID_TYPE_* constants for WotLK (build 12340):
///   MAP_LIQUID_TYPE_NO_WATER = 0x00;
///   MAP_LIQUID_TYPE_WATER    = 0x01;
///   MAP_LIQUID_TYPE_OCEAN    = 0x02;
///   MAP_LIQUID_TYPE_MAGMA    = 0x04;
///   MAP_LIQUID_TYPE_SLIME    = 0x08;
///   MAP_LIQUID_TYPE_DARK_WATER = 0x10;
///   MAP_LIQUID_TYPE_WMO_WATER  = 0x20;
/// Faithful port of MaNGOS System.cpp::MAP_LIQUID_TYPE_* flag values (the
/// WOTLK branch). These bit flags are what get written directly into the
/// .map file's liquid section, so the enum values must match the C++
/// exactly for byte-for-byte parity. Note: the previous C# enum used
/// Classic/TBC values (Water=0x01, Magma=0x04, Slime=0x08), which are
/// the inverse of WOTLK. The actual C++ (WOTLK) values are:
///   Water     = 0x08   (MAP_LIQUID_TYPE_WATER)
///   Ocean     = 0x02   (MAP_LIQUID_TYPE_OCEAN)
///   Magma     = 0x01   (MAP_LIQUID_TYPE_MAGMA)
///   Slime     = 0x04   (MAP_LIQUID_TYPE_SLIME)
///   DarkWater = 0x10
///   WmoWater  = 0x20
/// </summary>
public enum LiquidType : ushort
{
    None  = 0x00,
    // WotLK (CLIENT_WOTLK in System.cpp::setMapMagicVersion lines 1581-1588):
    //   MAP_LIQUID_TYPE_WATER = 0x01
    //   MAP_LIQUID_TYPE_OCEAN = 0x02
    //   MAP_LIQUID_TYPE_MAGMA = 0x04
    //   MAP_LIQUID_TYPE_SLIME = 0x08
    Water = 0x01,
    Ocean = 0x02,
    Magma = 0x04,
    Slime = 0x08,
    DarkWater = 0x10,
    WmoWater  = 0x20,
    Mask  = 0x0F
}

public struct LiquidData
{
    public float LiquidLevel;
    public LiquidType PrimaryType;
    /// <summary>Raw LiquidType.dbc row ID (unmodified from MH2O header).</summary>
    public ushort RawTypeId;
    /// <summary>WotLK SLiquidInstance.vertexFormat (C++ adt_liquid_header.formatFlags).
    /// Bit 0x01 = ADT_LIQUID_HEADER_FULL_LIGHT.</summary>
    public ushort VertexFormat;
    public byte OffsetX;
    public byte OffsetY;
    public byte Width;
    public byte Height;
    public float[]? DepthMap;
    /// <summary>64-bit show-mask: bit at [y*Width+x] = 1 means sub-cell is visible.</summary>
    public ulong ShowMask;
    public (byte R, byte G, byte B, byte A)[]? VertexColors;
    /// <summary>WotLK SLiquidInstance.ofsInfoMask (C++ adt_liquid_header.offsData2a).
    /// Non-zero when the MH2O cell carries a show-mask (64-bit visibility bitfield).</summary>
    public uint OfsInfoMask;
    /// <summary>WotLK SLiquidInstance.ofsHeightMap (C++ adt_liquid_header.offsData2b).
    /// Non-zero when the MH2O cell carries a height map AND/OR a lightmap block (per C++ getLiquidLightMap, the lightmap shares this offset).</summary>
    public uint OfsHeightMap;

    public static LiquidData Empty => default;
    public bool HasLiquid => PrimaryType != LiquidType.None;
    public bool HasVertexHeight => DepthMap != null;
    public bool HasVertexColor => VertexColors != null;

    /// <summary>
    /// Faithful port of MaNGOS C++ vmap/adt.h::getLiquidLightMap():
    ///   if (h->formatFlags & ADT_LIQUID_HEADER_FULL_LIGHT) return 0;
    ///   if (h->offsData2b) return pointer;
    ///   return 0;
    /// Returns true when the cell has a usable lightmap (NOT dark water).
    /// </summary>
    public bool HasLightmap =>
        (VertexFormat & 0x01) == 0     // not FULL_LIGHT
        && OfsHeightMap != 0;           // lightmap at offsData2b (same offset as height map per C++ getLiquidLightMap)
}