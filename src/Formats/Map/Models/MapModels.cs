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
    public float MinHeight { get; set; }
    public float MaxHeight { get; set; }

    public MapTile(uint mapId, int tileX, int tileY) => (MapId, TileX, TileY) = (mapId, tileX, tileY);
}

/// <summary>
/// Per-MCNK liquid data used when building the .map liquid section.
/// Uses MAP_LIQUID_TYPE_* flag values in TypeFlags (Magma=0x01, Ocean=0x02, Slime=0x04, Water=0x08).
/// </summary>
public struct MapLiquidCell
{
    /// <summary>Raw LiquidType.dbc row ID (written as liquid_entry in .map).</summary>
    public ushort RawTypeId;
    /// <summary>MAP_LIQUID_TYPE_* flag: 0x01=Magma, 0x02=Ocean, 0x04=Slime, 0x08=Water.</summary>
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
    public bool HasLiquid => TypeFlags != 0;
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
