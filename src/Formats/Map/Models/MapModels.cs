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
    public float MinHeight { get; set; }
    public float MaxHeight { get; set; }

    public MapTile(uint mapId, int tileX, int tileY) => (MapId, TileX, TileY) = (mapId, tileX, tileY);
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
