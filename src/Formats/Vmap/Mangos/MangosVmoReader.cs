using System.IO;
using System.Numerics;
using System.Text;

namespace MaNGOS.Extractor.Formats.Vmap.Mangos;

/// <summary>
/// Reader for the Mangos ".vmo" (WMO) and ".vmd" (M2) compiled collision
/// files produced by the vmap-extractor (MaNGOS vmapexport.cpp +
/// WorldModel::WriteFile).
///
/// On-disk layout (see MaNGOS/src/game/vmap/WorldModel.cpp):
///
///   8 bytes    magic = "VMAP_4.0"
///   4 bytes    "WMOD"  chunk name
///   4 bytes    chunkSize (sizeof(uint32) + sizeof(uint32) = 8)
///   4 bytes    RootWMOID
///
///   if groupCount > 0:
///     4 bytes    "GMOD" chunk name
///     4 bytes    count (number of GroupModels)
///     For each GroupModel:
///       G3D::AABox bound   (24 bytes: 3 floats low + 3 floats high)
///       uint32   mogpFlags
///       uint32   groupWMOID
///       4 bytes  "VERT"    chunk name
///       4 bytes  chunkSize (sizeof(uint32) + sizeof(Vector3) * count)
///       4 bytes  count
///       Vector3  vertices[count]    (12 bytes each)
///       4 bytes  "TRIM"    chunk name
///       4 bytes  chunkSize (sizeof(uint32) + sizeof(MeshTriangle) * count)
///       4 bytes  count
///       MeshTriangle triangles[count] (12 bytes each: 3 uint32)
///       4 bytes  "MBIH"    chunk name
///       BIH      (meshTree)
///       4 bytes  "LIQU"    chunk name
///       4 bytes  chunkSize
///       (if chunkSize > 0: WmoLiquid data)
///
///     4 bytes  "GBIH"  chunk name
///     BIH      (groupTree, of group BIHs)
///
/// BIH format (MaNGOS/src/game/vmap/BIH.cpp):
///   Vector3 lo (12 bytes)
///   Vector3 hi (12 bytes)
///   uint32  treeSize
///   uint32  tree[treeSize]
///   uint32  count
///   uint32  objects[count]
///
/// This file format is the OFFICIAL Mangos vmap format. The mmap-extractor
/// reads it via VMapManager2::loadMap → ModelInstance → WorldModel::ReadFile.
/// Replicating the reader here lets the mmap-extractor consume the same
/// output as Mangos and let us match its collision mesh exactly.
/// </summary>
public static class MangosVmoReader
{
    public const string VmoExtension = ".vmo";
    public const string VmdExtension = ".vmd";

    /// <summary>
    /// A single triangle from the compiled collision mesh.
    /// Mirrors VMAP::MeshTriangle (3 uint32 indices into the vertex array).
    /// </summary>
    public readonly struct MeshTriangle
    {
        public readonly uint I0;
        public readonly uint I1;
        public readonly uint I2;
        public MeshTriangle(uint i0, uint i1, uint i2) { I0 = i0; I1 = i1; I2 = i2; }
    }

    /// <summary>
    /// Compiled collision mesh for one WMO group (or M2 model — same format).
    /// </summary>
    public sealed class GroupModel
    {
        public uint MogpFlags;
        public uint GroupWMOID;
        public Vector3 BoundLo;
        public Vector3 BoundHi;
        public Vector3[] Vertices = Array.Empty<Vector3>();
        public MeshTriangle[] Triangles = Array.Empty<MeshTriangle>();
    }

    /// <summary>
    /// Complete compiled model — one or more GroupModels.
    /// </summary>
    public sealed class WorldModelData
    {
        public uint RootWMOID;
        public List<GroupModel> Groups = new();
        public bool Valid;
    }

    /// <summary>
    /// Read a .vmo / .vmd file. Returns null if the file is not a valid
    /// Mangos vmap file.
    /// </summary>
    public static WorldModelData? Read(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            using var fs = File.OpenRead(filePath);
            using var br = new BinaryReader(fs);
            return Read(br);
        }
        catch
        {
            return null;
        }
    }

    public static WorldModelData? Read(BinaryReader br)
    {
        var data = new WorldModelData();

        // VMAP_MAGIC = "VMAP_4.0" (8 bytes)
        var magic = br.ReadBytes(8);
        var expected = Encoding.ASCII.GetBytes(MangosVmapMagic.Tree);
        if (!magic.AsSpan().SequenceEqual(expected))
            return null;

        // WMOD chunk: readChunk + chunkSize + RootWMOID (Mangos C++ reader, verbatim)
        if (!ReadChunkHeader(br, "WMOD", out _))
            return null;
        data.RootWMOID = br.ReadUInt32();

        // Optional GMOD chunk
        if (br.BaseStream.Position + 8 > br.BaseStream.Length)
        {
            data.Valid = true;
            return data;
        }

        if (!PeekChunkId(br, "GMOD"))
        {
            data.Valid = true;
            return data;
        }
        br.ReadBytes(4); // consume "GMOD"
        uint gmodCount = br.ReadUInt32();

        for (uint i = 0; i < gmodCount; i++)
        {
            var grp = ReadGroupModel(br);
            if (grp == null) return null;
            data.Groups.Add(grp);
        }

        // Optional GBIH chunk — NO size field, just id + BIH data
        if (br.BaseStream.Position + 4 <= br.BaseStream.Length && PeekChunkId(br, "GBIH"))
        {
            br.ReadBytes(4); // consume "GBIH"
            SkipBih(br);
        }

        data.Valid = true;
        return data;
    }

    private static GroupModel? ReadGroupModel(BinaryReader br)
    {
        var grp = new GroupModel();

        // AABox: 3 floats low + 3 floats high
        grp.BoundLo = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        grp.BoundHi = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

        grp.MogpFlags = br.ReadUInt32();
        grp.GroupWMOID = br.ReadUInt32();

        // VERT chunk — Mangos format: "VERT" (4) + chunkSize (4) + count (4) + vertices
        if (!ReadChunkHeader(br, "VERT", out _))
            return null;
        uint vertCount = br.ReadUInt32();
        if (vertCount > 0)
        {
            grp.Vertices = new Vector3[vertCount];
            for (int v = 0; v < vertCount; v++)
                grp.Vertices[v] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        }

        // TRIM chunk — Mangos format: "TRIM" (4) + chunkSize (4) + count (4) + triangles
        if (!ReadChunkHeader(br, "TRIM", out _))
            return null;
        uint triCount = br.ReadUInt32();
        if (triCount > 0)
        {
            grp.Triangles = new MeshTriangle[triCount];
            for (int t = 0; t < triCount; t++)
            {
                uint i0 = br.ReadUInt32();
                uint i1 = br.ReadUInt32();
                uint i2 = br.ReadUInt32();
                grp.Triangles[t] = new MeshTriangle(i0, i1, i2);
            }
        }

        // MBIH chunk — Mangos format: "MBIH" (4) + BIH data (NO size field!)
        // See GroupModel::WriteToFile in MaNGOS: fwrite("MBIH", ...) then meshTree.WriteToFile(wf).
        // ReadChunkHeader would read 4 extra bytes (the first 4 of the BIH) and break parsing.
        if (!ReadIdOnly(br, "MBIH"))
            return null;
        SkipBih(br);

        // LIQU chunk — Mangos format: "LIQU" (4) + chunkSize (4) + optional liquid data
        if (!ReadChunkHeader(br, "LIQU", out uint liquSize))
            return null;
        if (liquSize > 0)
            br.BaseStream.Seek(liquSize, SeekOrigin.Current);

        return grp;
    }

    /// <summary>
    /// Read a 4-byte chunk id WITHOUT a size field. Used for MBIH and GBIH chunks
    /// in the Mangos WorldModel format, which are written as just the id followed
    /// by the chunk data.
    /// </summary>
    private static bool ReadIdOnly(BinaryReader br, string expected)
    {
        if (br.BaseStream.Position + 4 > br.BaseStream.Length) return false;
        var id = br.ReadBytes(4);
        return Encoding.ASCII.GetString(id) == expected;
    }

    private static bool ReadChunkHeader(BinaryReader br, string expected, out uint chunkSize)
    {
        chunkSize = 0;
        if (br.BaseStream.Position + 8 > br.BaseStream.Length) return false;
        var id = br.ReadBytes(4);
        if (Encoding.ASCII.GetString(id) != expected) return false;
        chunkSize = br.ReadUInt32();
        return true;
    }

    private static bool PeekChunkId(BinaryReader br, string expected)
    {
        if (br.BaseStream.Position + 4 > br.BaseStream.Length) return false;
        var id = br.ReadBytes(4);
        br.BaseStream.Seek(-4, SeekOrigin.Current);
        return Encoding.ASCII.GetString(id) == expected;
    }

    private static void SkipBih(BinaryReader br)
    {
        // BIH = Vector3 lo (12) + Vector3 hi (12) + uint32 treeSize +
        //       uint32[treeSize] + uint32 count + uint32[count]
        var lo = br.ReadSingle(); br.ReadSingle(); br.ReadSingle();
        var hi = br.ReadSingle(); br.ReadSingle(); br.ReadSingle();
        uint treeSize = br.ReadUInt32();
        br.BaseStream.Seek(treeSize * 4, SeekOrigin.Current);
        uint objCount = br.ReadUInt32();
        br.BaseStream.Seek(objCount * 4, SeekOrigin.Current);
    }

    // -----------------------------------------------------------------------
    // Writer (for the vmap-extractor to produce .vmo / .vmd files)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Write a .vmo (WMO) or .vmd (M2) compiled collision file.
    /// This matches WorldModel::WriteFile from MaNGOS.
    /// </summary>
    public static void Write(string filePath, uint rootWmoId, IReadOnlyList<GroupModel> groups)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        using var fs = File.Create(filePath);
        using var bw = new BinaryWriter(fs);
        Write(bw, rootWmoId, groups);
    }

    /// <summary>
    /// Serialize a WorldModel to a byte array. Used when the same .vmo/.vmd
    /// content must be written to two locations (MaNGOS vmap-extractor writes
    /// once to <c>Buildings/{uniform_name}</c> and once to <c>vmaps/{path}.vmo</c>).
    /// </summary>
    public static byte[] ToBytes(uint rootWmoId, IReadOnlyList<GroupModel> groups)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        Write(bw, rootWmoId, groups);
        return ms.ToArray();
    }

    public static void Write(BinaryWriter bw, uint rootWmoId, IReadOnlyList<GroupModel> groups)
    {
        // Magic
        bw.Write(Encoding.ASCII.GetBytes(MangosVmapMagic.Tree));

        // WMOD chunk
        bw.Write(Encoding.ASCII.GetBytes("WMOD"));
        bw.Write((uint)8); // chunkSize = sizeof(uint32) + sizeof(uint32) = 8
        bw.Write(rootWmoId);
        // Note: original C++ writes 2 uint32 inside WMOD but only reads 1 (RootWMOID)
        // We match the on-disk size for round-trip compatibility.

        if (groups.Count == 0) return;

        // GMOD chunk
        bw.Write(Encoding.ASCII.GetBytes("GMOD"));
        bw.Write((uint)groups.Count);

        for (int g = 0; g < groups.Count; g++)
        {
            WriteGroupModel(bw, groups[g]);
        }

        // GBIH chunk — empty BIH (just bounds, 0 nodes)
        bw.Write(Encoding.ASCII.GetBytes("GBIH"));
        WriteEmptyBih(bw);
    }

    private static void WriteGroupModel(BinaryWriter bw, GroupModel grp)
    {
        // AABox (lo, hi) — 6 floats
        bw.Write(grp.BoundLo.X); bw.Write(grp.BoundLo.Y); bw.Write(grp.BoundLo.Z);
        bw.Write(grp.BoundHi.X); bw.Write(grp.BoundHi.Y); bw.Write(grp.BoundHi.Z);

        bw.Write(grp.MogpFlags);
        bw.Write(grp.GroupWMOID);

        // VERT chunk
        bw.Write(Encoding.ASCII.GetBytes("VERT"));
        bw.Write((uint)(4 + 12 * grp.Vertices.Length)); // sizeof(uint32) + sizeof(Vector3)*count
        bw.Write((uint)grp.Vertices.Length);
        for (int v = 0; v < grp.Vertices.Length; v++)
        {
            bw.Write(grp.Vertices[v].X);
            bw.Write(grp.Vertices[v].Y);
            bw.Write(grp.Vertices[v].Z);
        }

        // TRIM chunk
        bw.Write(Encoding.ASCII.GetBytes("TRIM"));
        bw.Write((uint)(4 + 12 * grp.Triangles.Length)); // sizeof(uint32) + sizeof(MeshTriangle)*count
        bw.Write((uint)grp.Triangles.Length);
        for (int t = 0; t < grp.Triangles.Length; t++)
        {
            bw.Write(grp.Triangles[t].I0);
            bw.Write(grp.Triangles[t].I1);
            bw.Write(grp.Triangles[t].I2);
        }

        // MBIH chunk — empty BIH
        bw.Write(Encoding.ASCII.GetBytes("MBIH"));
        WriteEmptyBih(bw);

        // LIQU chunk — no liquid (chunkSize = 0)
        bw.Write(Encoding.ASCII.GetBytes("LIQU"));
        bw.Write((uint)0);
    }

    private static void WriteEmptyBih(BinaryWriter bw)
    {
        // BIH = bounds lo + bounds hi + treeSize + tree + count + objects
        // Empty = dummy leaf node (3 << 30) + 2 zeroes
        bw.Write(0f); bw.Write(0f); bw.Write(0f); // lo
        bw.Write(0f); bw.Write(0f); bw.Write(0f); // hi
        bw.Write((uint)3);   // treeSize (3 uint32 for the dummy leaf)
        bw.Write((uint)((3u << 30))); // tree[0] = dummy leaf marker
        bw.Write((uint)0);   // tree[1]
        bw.Write((uint)0);   // tree[2]
        bw.Write((uint)0);   // objectCount
    }
}
