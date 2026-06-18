using System.IO;
using System.Text;

namespace MaNGOS.Extractor.Formats.Vmap.Mangos;

/// <summary>
/// Map-tree file format used by the Mangos vmap-extractor (vmaps/{MapName}.vmtree).
///
/// Faithful port of the MaNGOS layout, verified byte-for-byte against the
/// reference output in <c>C:\Users\Texy6\Desktop\World of Warcraft 3.3.5a
/// original\vmaps\</c> and the C++ writer <c>TileAssembler::convertWorld2</c>
/// (TileAssembler.cpp:165-195):
///
/// <code>
///   8 bytes   magic      = "VMAP_4.0"   (VMAP_MAGIC)
///   1 byte    isTiled     (0 = per-tile .vmtile; 1 = global WMO only)
///   4 bytes   "NODE"                     (NO chunk size field)
///   ...       BIH payload                (Bih.WriteToFile: bounds.lo, bounds.hi,
///                                        treeSize, tree[], count, objects[])
///   4 bytes   "GOBJ"                     (NO chunk size, NO count field)
///   ...       ModelSpawn[]               (concatenated until EOF)
/// </code>
///
/// Notes:
///   - The <c>isTiled</c> flag is INVERTED relative to intuition: the C++ sets
///     <c>isTiled = (no tile (65,65) entry)</c>, so 1 means "no terrain, WMO
///     instance", 0 means "has terrain tiles". See TileAssembler.cpp:172.
///   - There is NO chunk size between "NODE" and the BIH data, nor between
///     "GOBJ" and the first ModelSpawn. The previous C# placeholder invented
///     those size fields; they are NOT in the reference output.
///   - An empty BIH (no primitives) is NOT treeSize=0: Bih.InitEmpty() writes
///     treeSize=3 with a dummy leaf node [0xC0000000, 0, 0]. This was the
///     signature of the original "BIH vide" bug.
/// </summary>
public static class MangosVmapTree
{
    public const string FileExt = ".vmtree";

    /// <summary>
    /// Compute the on-disk filename for the per-map .vmtree file.
    /// </summary>
    /// <summary>
    /// Compute the on-disk filename for the per-map .vmtree file.
    /// MaNGOS TileAssembler.cpp uses the zero-padded map id (e.g. "000.vmtree"),
    /// NOT the human-readable map name (e.g. "Azeroth.vmtree"). Matching this
    /// convention is required for byte-for-byte parity with the C++ reference.
    /// </summary>
    public static string GetFileName(uint mapId) => $"{mapId:D3}{FileExt}";

    /// <summary>
    /// Read the .vmtree. Returns the magic-validity flag, the tiled flag, and
    /// the list of global model spawns (empty for tiled maps). The BIH payload
    /// is consumed via <see cref="Bih.ReadFromFile"/> but discarded — only the
    /// mmap-extractor uses this reader, and it only needs the global spawns.
    /// </summary>
    public static (bool ValidMagic, bool Tiled, List<MangosModelSpawn> GlobalSpawns) Read(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        using var br = new BinaryReader(fs);
        var magic = br.ReadBytes(8);
        var expected = Encoding.ASCII.GetBytes(MangosVmapMagic.Tree);
        bool valid = magic.AsSpan().SequenceEqual(expected);
        if (!valid) return (false, false, new List<MangosModelSpawn>());

        byte tiled = br.ReadByte();

        // "NODE" chunk header — 4 bytes tag, NO size field (TileAssembler.cpp:178).
        var nodeHdr = br.ReadBytes(4);
        if (Encoding.ASCII.GetString(nodeHdr) != MangosVmapMagic.NodeChunk)
            throw new InvalidDataException("Expected NODE chunk in vmtree");

        // BIH payload — consumed and discarded (Bih.ReadFromFile reads bounds +
        // treeSize + tree[] + count + objects[] in that exact order).
        var bih = new Bih();
        bih.ReadFromFile(br);

        var spawns = new List<MangosModelSpawn>();
        if (tiled == 0 && br.BaseStream.Position + 4 <= br.BaseStream.Length)
        {
            var gobjHdr = br.ReadBytes(4);
            if (Encoding.ASCII.GetString(gobjHdr) == MangosVmapMagic.GobjChunk)
            {
                // "GOBJ" has NO size and NO count field (TileAssembler.cpp:187):
                // the spawns are written back-to-back until EOF. Read until the
                // stream is exhausted (ModelSpawn.Read throws at EOF).
                while (br.BaseStream.Position < br.BaseStream.Length)
                {
                    spawns.Add(MangosModelSpawn.Read(br));
                }
            }
        }
        return (true, tiled != 0, spawns);
    }

    /// <summary>
    /// Write the .vmtree, faithful port of <c>TileAssembler::convertWorld2</c>
    /// lines 165-195. Writes: VMAP_MAGIC, isTiled flag, "NODE" + BIH payload,
    /// "GOBJ" + concatenated global spawns. No chunk-size or count fields.
    /// </summary>
    /// <param name="filePath">Output .vmtree path.</param>
    /// <param name="isTiled">true if the map has terrain tiles (per-tile
    ///   .vmtile files exist); false for WMO-instance maps whose spawns go in
    ///   the GOBJ chunk.</param>
    /// <param name="bih">Pre-built BIH tree (Build(mapSpawns, ...)).</param>
    /// <param name="globalSpawns">Spawns belonging to tile (65,65) — written
    ///   verbatim after the GOBJ tag (TileAssembler.cpp:192-195). Empty for
    ///   tiled maps.</param>
    public static void Write(string filePath, bool isTiled, Bih bih, IReadOnlyList<MangosModelSpawn> globalSpawns)
    {
        using var fs = File.Create(filePath);
        using var bw = new BinaryWriter(fs);
        Write(bw, isTiled, bih, globalSpawns);
    }

    public static void Write(BinaryWriter bw, bool isTiled, Bih bih, IReadOnlyList<MangosModelSpawn> globalSpawns)
    {
        // General info — TileAssembler.cpp:166
        bw.Write(Encoding.ASCII.GetBytes(MangosVmapMagic.Tree)); // VMAP_MAGIC, 8 bytes
        bw.Write(isTiled ? (byte)1 : (byte)0);                   // isTiled, 1 byte (line 173)

        // Nodes — TileAssembler.cpp:178-185
        bw.Write(Encoding.ASCII.GetBytes(MangosVmapMagic.NodeChunk)); // "NODE", 4 bytes, NO size
        bih.WriteToFile(bw);                                          // BIH payload (line 184)

        // Global map spawns (WDT) — TileAssembler.cpp:187-195
        bw.Write(Encoding.ASCII.GetBytes(MangosVmapMagic.GobjChunk)); // "GOBJ", 4 bytes, NO size, NO count
        for (int i = 0; i < globalSpawns.Count; i++)
        {
            globalSpawns[i].Write(bw);                                // ModelSpawn::WriteToFile (line 194)
        }
    }
}
