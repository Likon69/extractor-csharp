namespace MaNGOS.Extractor.Formats.Map.Models;

public readonly struct MapFileHeader
{
    public const int Size = 44;

    public readonly uint MapMagic;
    public readonly uint VersionMagic;
    public readonly uint BuildMagic;
    public readonly uint AreaMapOffset;
    public readonly uint AreaMapSize;
    public readonly uint HeightMapOffset;
    public readonly uint HeightMapSize;
    public readonly uint LiquidMapOffset;
    public readonly uint LiquidMapSize;
    public readonly uint HolesOffset;
    public readonly uint HolesSize;
}

public readonly struct AreaMapHeader
{
    public const int Size = 8;
    /// <summary>Set when all 256 chunks share the same area flag — the
    /// 256-element array is then omitted and <c>gridArea</c> carries the
    /// common value. Matches MaNGOS C++ <c>MAP_AREA_NO_AREA = 0x0001</c>.</summary>
    public const ushort NoArea = 0x0001;

    public readonly uint FourCC;
    public readonly ushort Flags;
    public readonly ushort GridArea;
}

public readonly struct HeightMapHeader
{
    public const int Size = 16;
    public const uint NoHeight = 0x01;
    public const uint AsInt16 = 0x02;
    public const uint AsInt8 = 0x04;

    public readonly uint FourCC;
    public readonly uint Flags;
    public readonly float GridHeight;
    public readonly float GridMaxHeight;

    public readonly bool HasHeight => (Flags & NoHeight) == 0;
    public readonly bool UseInt16 => (Flags & AsInt16) != 0;
    public readonly bool UseInt8 => (Flags & AsInt8) != 0;
}

public readonly struct LiquidMapHeader
{
    public const int Size = 16;
    public const ushort NoType = 0x0001;
    public const ushort NoHeightValues = 0x0002;

    public readonly uint FourCC;
    public readonly ushort Flags;
    public readonly ushort LiquidType;
    public readonly byte OffsetX;
    public readonly byte OffsetY;
    public readonly byte Width;
    public readonly byte Height;
    public readonly float LiquidLevel;
}

public enum MapLiquidType : byte
{
    None = 0,
    Water = 1,
    Ocean = 2,
    Magma = 3,
    Slime = 4
}

public sealed class MapTile
{
    public uint MapId { get; }
    public int TileX { get; }
    public int TileY { get; }
    public float[]? HeightMap { get; set; }
    public ushort[]? AreaMap { get; set; }
    public ushort[]? HolesMap { get; set; }
    public MapLiquidEntry[]? LiquidMap { get; set; }
    /// <summary>256 per-MCNK liquid cells (one per chunk), populated from MH2O data.</summary>
    public MapLiquidCell[]? ChunkLiquids { get; set; }
    /// <summary>256 per-MCNK liquid cells from legacy MCLQ chunk (old TBC format, still present in some WotLK tiles).
    /// The C++ System.cpp processes MCLQ BEFORE MH2O; the final liquid_show is the OR of both.
    /// Fields match <see cref="MapLiquidCell"/> with OffsetX=OffsetY=0, Width=Height=8, and a 9×9 Heights array.</summary>
    public MapLiquidCell[]? ChunkMclqs { get; set; }
    public float MinHeight { get; set; }
    public float MaxHeight { get; set; }

    public MapTile(uint mapId, int tileX, int tileY) => (MapId, TileX, TileY) = (mapId, tileX, tileY);
}

/// <summary>
/// Per-MCNK liquid data used when building the .map liquid section.
/// Uses MAP_LIQUID_TYPE_* flag values in TypeFlags (WotLK: Water=0x01, Ocean=0x02, Magma=0x04, Slime=0x08, DarkWater=0x10).
/// </summary>
public struct MapLiquidCell
{
    /// <summary>Raw LiquidType.dbc row ID (written as liquid_entry in .map).</summary>
    public ushort RawTypeId;
    /// <summary>MAP_LIQUID_TYPE_* flag (WotLK: Water=0x01, Ocean=0x02, Magma=0x04, Slime=0x08).</summary>
    public byte TypeFlags;
    public float MinHeight;
    public byte OffsetX;
    public byte OffsetY;
    public byte Width;
    public byte Height;
    /// <summary>(Width+1)*(Height+1) height values, or null if flat.</summary>
    public float[]? Heights;
    /// <summary>64-bit visibility mask; bit at [y*Width+x] = 1 means sub-cell is visible.</summary>
    public ulong ShowMask;
    /// <summary>WotLK SLiquidInstance.vertexFormat — bit 0x01 = FULL_LIGHT (no lightmap).</summary>
    public ushort VertexFormat;
    /// <summary>WotLK SLiquidInstance.ofsInfoMask — non-zero when lightmap data is present.</summary>
    public uint OfsInfoMask;
    public bool HasLiquid => TypeFlags != 0;
    /// <summary>
    /// Faithful port of MaNGOS C++ getLiquidLightMap(): returns true when the
    /// cell has a usable lightmap (NOT dark water). Mirrors the C++ helper
    /// 1:1: <c>(formatFlags &amp; FULL_LIGHT) == 0 &amp;&amp; offsData2b != 0</c>.
    /// </summary>
    public bool HasLightmap =>
        (VertexFormat & 0x01) == 0
        && OfsInfoMask != 0;
}

public readonly struct MapLiquidEntry
{
    public readonly byte Type;
    public readonly float Level;
    public readonly byte OffsetX;
    public readonly byte OffsetY;
    public readonly byte Width;
    public readonly byte Height;

    public readonly bool HasLiquid => Type != 0;

    public static MapLiquidEntry Empty => default;
}

public readonly record struct MapGenConfig(
    string OutputDirectory,
    uint MapId,
    string MapName,
    bool UseInt16Heights = false
);
