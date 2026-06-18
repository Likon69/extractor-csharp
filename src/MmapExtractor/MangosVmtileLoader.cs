using System.IO;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Formats.Vmap.Mangos;

namespace MaNGOS.Extractor.MmapExtractor;

/// <summary>
/// Helper for the mmap-extractor: read Mangos vmtile files to get WMO/M2 placements
/// (matching the Mangos pipeline where the vmap-extractor produces the placements
/// and the mmap-extractor consumes them).
/// </summary>
internal static class MangosVmtileLoader
{
    /// <summary>
    /// Look for a .vmtile file for a given tile. The C++ TileAssembler writes
    /// files directly under iDestDir (output/vmaps/), with NO per-map subdirectory.
    /// The filename is {mapId:D3}_{x:D2}_{y:D2}.vmtile.
    /// vmapDir is the output root (containing vmaps/ as a sibling of Buildings/).
    /// </summary>
    public static string? FindVmtilePath(string? vmapDir, uint mapId, string mapName, int tileX, int tileY)
    {
        if (string.IsNullOrEmpty(vmapDir)) return null;
        string vmtilePath = Path.Combine(vmapDir, "vmaps", MangosVmapTile.GetFileName(mapId, tileX, tileY));
        return File.Exists(vmtilePath) ? vmtilePath : null;
    }

    /// <summary>
    /// Read the .vmtile file for a tile, returning the list of (spawn, referencedVal)
    /// entries. Returns an empty list if the file doesn't exist or can't be read.
    /// </summary>
    public static List<(MangosModelSpawn Spawn, uint ReferencedVal)> LoadVmtileSpawns(string vmtilePath)
    {
        try
        {
            return MangosVmapTile.Read(vmtilePath);
        }
        catch
        {
            return new List<(MangosModelSpawn, uint)>();
        }
    }
}
