using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MaNGOS.Extractor.Formats.Vmap.Mangos;

/// <summary>
/// Per-tile Mangos vmap file format (vmaps/{MapName}_XX_YY.vmtile).
///
/// Faithful port of the MaNGOS layout produced by <c>TileAssembler::convertWorld2</c>
/// (TileAssembler.cpp:218-245):
/// <code>
///   8 bytes   magic = "VMAP_4.0"   (VMAP_MAGIC)
///   uint32    numSpawns            (nSpawns = tileEntries.count(tileId))
///   for each spawn:
///     ModelSpawn payload           (ModelSpawn::WriteToFile — line 237)
///     uint32 referencedVal         (modelNodeIdx[spawn.ID] — line 240)
/// </code>
///
/// IMPORTANT order (corrected from a misleading earlier comment): the spawn
/// payload comes FIRST, the referencedVal (BIH tree index for this spawn.ID)
/// comes SECOND. This matches lines 237 (WriteToFile) → 240 (fwrite nIdx) in
/// the C++ source exactly.
///
/// Used by the Mangos mmap-extractor (TerrainBuilder::loadVMap) to enumerate
/// all WMO/M2 placements in a tile and load their collision meshes.
/// </summary>
public static class MangosVmapTile
{
    /// <summary>
    /// Compute the on-disk filename for a per-tile vmtile file (Mangos convention).
    /// </summary>
    public static string GetFileName(uint mapId, int tileX, int tileY)
    {
        return $"{mapId:D3}_{tileX:D2}_{tileY:D2}.vmtile";
    }

    /// <summary>
    /// Read a .vmtile file. Returns the list of (ModelSpawn, referencedVal) entries.
    /// </summary>
    public static List<(MangosModelSpawn Spawn, uint ReferencedVal)> Read(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        using var br = new BinaryReader(fs);
        return Read(br);
    }

    public static List<(MangosModelSpawn Spawn, uint ReferencedVal)> Read(BinaryReader br)
    {
        var magic = br.ReadBytes(8);
        var expected = Encoding.ASCII.GetBytes(MangosVmapMagic.Tree);
        if (!magic.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException($"Invalid vmtile magic: {Encoding.ASCII.GetString(magic)} (expected {MangosVmapMagic.Tree})");

        uint numSpawns = br.ReadUInt32();
        var entries = new List<(MangosModelSpawn, uint)>((int)numSpawns);
        for (int i = 0; i < numSpawns; i++)
        {
            // MaNGOS TileAssembler.cpp:237 then :240 — spawn payload first,
            // then referencedVal (the BIH tree index for this spawn.ID).
            var spawn = MangosModelSpawn.Read(br);
            uint referencedVal = br.ReadUInt32();
            entries.Add((spawn, referencedVal));
        }
        return entries;
    }

    /// <summary>
    /// Write a .vmtile file with the given (spawn, referencedVal) entries.
    /// The referencedVal MUST be supplied by the caller — it is the index of
    /// this spawn in the BIH tree (modelNodeIdx[spawn.ID], see
    /// TileAssembler.cpp:148-152, 240), NOT a sequential ADT index.
    /// </summary>
    public static void Write(string filePath, IEnumerable<(MangosModelSpawn Spawn, uint ReferencedVal)> entries)
    {
        using var fs = File.Create(filePath);
        using var bw = new BinaryWriter(fs);
        Write(bw, entries);
    }

    public static void Write(BinaryWriter bw, IEnumerable<(MangosModelSpawn Spawn, uint ReferencedVal)> entries)
    {
        var list = entries as IList<(MangosModelSpawn Spawn, uint ReferencedVal)> ?? entries.ToList();

        // Magic — TileAssembler.cpp:220
        bw.Write(Encoding.ASCII.GetBytes(MangosVmapMagic.Tree));
        // numSpawns — TileAssembler.cpp:225
        bw.Write((uint)list.Count);
        // Each (spawn, referencedVal) — spawn payload first, then referencedVal.
        // TileAssembler.cpp:237 (WriteToFile(spawn)) then :240 (fwrite nIdx).
        for (int i = 0; i < list.Count; i++)
        {
            list[i].Spawn.Write(bw);
            bw.Write(list[i].ReferencedVal);
        }
    }
}
