using System.Runtime.InteropServices;
using MaNGOS.Extractor.Core.Constants;

namespace MaNGOS.Extractor.Formats.Wmo.Models;

public sealed class WmoRootFile
{
    public string FileName { get; }
    public WmoRootHeader Header { get; }
    public string[] GroupNames { get; }
    public WmoDoodadSet[] DoodadSets { get; }
    public WmoDoodadPlacement[] DoodadPlacements { get; }
    public string[] TextureNames { get; }
    public string[] DoodadNames { get; }

    internal WmoRootFile(
        string fileName, WmoRootHeader header,
        string[] groupNames, WmoDoodadSet[] doodadSets, WmoDoodadPlacement[] doodadPlacements,
        string[] textures, string[] doodadNames)
    {
        FileName = fileName;
        Header = header;
        GroupNames = groupNames;
        DoodadSets = doodadSets;
        DoodadPlacements = doodadPlacements;
        TextureNames = textures;
        DoodadNames = doodadNames;
    }
}

public sealed class WmoGroupFile
{
    public string RootFileName { get; }
    public int GroupIndex { get; }
    public WmoGroupHeader Header { get; }
    public WmoVertex[] Vertices { get; }
    public WmoTriangle[] Triangles { get; }
    public WmoMaterial[] Materials { get; }
    /// <summary>Raw MOBA uint16 data: each batch = 12 uint16s (24 bytes). Matches C++ uint16* MOBA.</summary>
    public ushort[] RawMoba { get; }
    public WmoLiquidData? Liquid { get; }

    public string Name => RootFileName[..^4] + $"_{GroupIndex:D3}.wmo";

    internal WmoGroupFile(
        string rootFileName, int groupIndex, WmoGroupHeader header,
        WmoVertex[] vertices, WmoTriangle[] triangles, WmoMaterial[] materials,
        ushort[] rawMoba, WmoLiquidData? liquid)
    {
        RootFileName = rootFileName;
        GroupIndex = groupIndex;
        Header = header;
        Vertices = vertices;
        Triangles = triangles;
        Materials = materials;
        RawMoba = rawMoba;
        Liquid = liquid;
    }
}

public sealed class WmoLiquidData
{
    public WmoLiquidHeader Header { get; }
    public float[] Heights { get; }
    public byte[] TileFlags { get; }

    internal WmoLiquidData(WmoLiquidHeader header, float[] heights, byte[] tileFlags)
    {
        Header = header;
        Heights = heights;
        TileFlags = tileFlags;
    }
}

public readonly record struct WmoParseResult(
    WmoRootFile? Root,
    WmoGroupFile[] Groups,
    List<string> Warnings)
{
    public bool Success => Root != null;
    public static WmoParseResult Failed(List<string> warnings) => new(null, Array.Empty<WmoGroupFile>(), warnings);
    public static WmoParseResult Ok(WmoRootFile root, WmoGroupFile[] groups) => new(root, groups, new List<string>());
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct RawWmoChunk
{
    public readonly uint Magic;
    public readonly uint Size;
}
