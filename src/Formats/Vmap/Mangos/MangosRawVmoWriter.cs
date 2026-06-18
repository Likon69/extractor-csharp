using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;

namespace MaNGOS.Extractor.Formats.Vmap.Mangos;

/// <summary>
/// Writer for the RAW vmap collision files stored under Buildings/.
///
/// Mirrors the C++ vmap-extractor output format from
///   src/tools/Extractor_projects/vmap-extractor/model.cpp::Model::ConvertToVMAPModel
///   src/tools/Extractor_projects/vmap-extractor/wmo.cpp::WMORoot::ConvertToVMAPRootWmo
///   src/tools/Extractor_projects/vmap-extractor/wmo.cpp::WMOGroup::ConvertToVMAPGroupWmo
///
/// File magic: "VMAPt07\0" for WotLK (MangosVmapMagic.RawWotlk).
/// These raw files are consumed by the TileAssembler (phase 2) which
/// rebuilds the BIH and writes the final .vmtree/.vmtile with magic "VMAP_4.0".
/// </summary>
public static class MangosRawVmoWriter
{
    /// <summary>
    /// Holds the M2 (model) geometry needed to write a raw .vmd file.
    /// Faithful port of MaNGOS Model header fields used by ConvertToVMAPModel.
    /// </summary>
    public sealed class M2RawData
    {
        public required uint NVertices { get; init; }
        public required ushort[] Indices { get; init; }  // size = nIndices
        public required Vector3[] Vertices { get; init; } // size = nVertices
    }

    /// <summary>
    /// Holds one WMO group's geometry for the raw .vmo file.
    /// Mirrors the data WMOGroup::ConvertToVMAPGroupWmo reads from MOBA/MOVI/MOVT.
    /// </summary>
    public sealed class WmoGroupRawData
    {
        public required uint MogpFlags { get; init; }
        public required uint GroupWMOID { get; init; }
        public required Vector3 BoundMin { get; init; }
        public required Vector3 BoundMax { get; init; }
        public required uint LiquFlags { get; init; }
        /// <summary>MOBA BSP nodes (one int per node, root at index 0, then left/right pairs).</summary>
        public required int[] MobaNodes { get; init; }
        /// <summary>Triangle indices (3 per triangle, uint16). Already filtered (collision-only).</summary>
        public required ushort[] Indices { get; init; }
        /// <summary>Vertex positions (3 floats per vertex). Already remapped to compact index space.</summary>
        public required Vector3[] Vertices { get; init; }
    }

    /// <summary>
    /// Writes a raw M2 (.vmd) file.
    ///
    /// Mirrors MaNGOS model.cpp:121-184 (Model::ConvertToVMAPModel).
    /// </summary>
    public static byte[] WriteM2(M2RawData m2)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // 8 bytes: magic (7 chars + null terminator, matching MaNGOS char[8] layout)
        var rawMagic = new byte[8];
        Encoding.ASCII.GetBytes(MangosVmapMagic.RawWotlk, 0, MangosVmapMagic.RawWotlk.Length, rawMagic, 0);
        bw.Write(rawMagic);

        // 4 bytes: nVertices
        bw.Write(m2.NVertices);

        // 4 bytes: nofgroups = 1
        bw.Write((uint)1);

        // 12 bytes: N[3] = {rootwmoid=0, flags=0, groupid=0}
        bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0);

        // 24 bytes: bbox = 6 zeros (only needed for WMO, M2 uses zero bbox here)
        for (int i = 0; i < 6; i++) bw.Write(0f);

        // 4 bytes: liquidflags = 0
        bw.Write((uint)0);

        // 4 bytes: "GRP " chunk name
        bw.Write(Encoding.ASCII.GetBytes("GRP "));

        // 4 bytes: wsize = sizeof(branches) + sizeof(uint32)*branches = 8
        bw.Write(8);

        // 4 bytes: branches = 1
        bw.Write((uint)1);

        // 4 bytes: nIndexes
        uint nIndexes = (uint)m2.Indices.Length;
        bw.Write(nIndexes);

        // 4 bytes: "INDX" chunk name
        bw.Write(Encoding.ASCII.GetBytes("INDX"));

        // 4 bytes: wsize = sizeof(uint32) + sizeof(ushort)*nIndexes
        bw.Write(4 + 2 * nIndexes);

        // 4 bytes: nIndexes (repeated)
        bw.Write(nIndexes);

        // nIndexes*2 bytes: indices (with swap of index[1] and index[2] for every group of 3)
        // The C++ swap: for i in 0..nIndices, if (i % 3) - 1 == 0, swap indices[i] and indices[i+1].
        // This is equivalent to swapping the Y and Z vertex indices within each triangle.
        if (nIndexes > 0)
        {
            var swapped = new ushort[nIndexes];
            Array.Copy(m2.Indices, swapped, nIndexes);
            for (uint i = 0; i < nIndexes; i++)
            {
                if ((i % 3) - 1 == 0) // i = 1, 4, 7, ...
                {
                    (swapped[i], swapped[i + 1]) = (swapped[i + 1], swapped[i]);
                }
            }
            // Write 2 bytes at a time (ushort is little-endian on x86, matches BW writer)
            for (int i = 0; i < swapped.Length; i++)
                bw.Write(swapped[i]);
        }

        // 4 bytes: "VERT" chunk name
        bw.Write(Encoding.ASCII.GetBytes("VERT"));

        // 4 bytes: wsize = sizeof(int) + sizeof(float)*3*nVertices
        bw.Write(4 + 12 * m2.NVertices);

        // 4 bytes: nVertices
        bw.Write(m2.NVertices);

        // nVertices*12 bytes: vertices
        foreach (var v in m2.Vertices)
        {
            bw.Write(v.X);
            bw.Write(v.Y);
            bw.Write(v.Z);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Writes a raw WMO (.vmo) file containing the root header + all groups.
    ///
    /// Mirrors MaNGOS wmo.cpp:152-165 (WMORoot::ConvertToVMAPRootWmo) +
    /// wmo.cpp:284-...   (WMOGroup::ConvertToVMAPGroupWmo).
    /// The total vertex count is patched back at offset 8 after all groups are written.
    /// </summary>
    public static byte[] WriteWmo(uint rootWmoId, IReadOnlyList<WmoGroupRawData> groups)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // === Root header (ConvertToVMAPRootWmo) ===
        // 8 bytes: magic (7 chars + null terminator, matching MaNGOS char[8] layout)
        var rawMagic = new byte[8];
        Encoding.ASCII.GetBytes(MangosVmapMagic.RawWotlk, 0, MangosVmapMagic.RawWotlk.Length, rawMagic, 0);
        bw.Write(rawMagic);
        // 4 bytes: nVectors (placeholder, will be patched at the end)
        long nVectorsOffset = ms.Position;
        bw.Write((uint)0);
        // 4 bytes: nGroups
        bw.Write((uint)groups.Count);
        // 4 bytes: RootWMOID
        bw.Write(rootWmoId);

        // === Per-group data (ConvertToVMAPGroupWmo) ===
        uint totalVertices = 0;
        foreach (var g in groups)
        {
            totalVertices += (uint)g.Vertices.Length;

            // 4 bytes: mogpFlags
            bw.Write(g.MogpFlags);
            // 4 bytes: groupWMOID
            bw.Write(g.GroupWMOID);
            // 12 bytes: bbcorn1 (bound min)
            bw.Write(g.BoundMin.X); bw.Write(g.BoundMin.Y); bw.Write(g.BoundMin.Z);
            // 12 bytes: bbcorn2 (bound max)
            bw.Write(g.BoundMax.X); bw.Write(g.BoundMax.Y); bw.Write(g.BoundMax.Z);
            // 4 bytes: liquflags
            bw.Write(g.LiquFlags);

            // 4 bytes: "GRP " chunk name
            bw.Write(Encoding.ASCII.GetBytes("GRP "));
            // 4 bytes: moba_size_grp = sizeof(moba_batch) + sizeof(int)*moba_batch
            int mobaSize = 4 + 4 * g.MobaNodes.Length;
            bw.Write(mobaSize);
            // 4 bytes: moba_batch
            bw.Write(g.MobaNodes.Length);
            // moba_batch*4 bytes: MobaEx (root + BSP nodes)
            foreach (var n in g.MobaNodes) bw.Write(n);

            // 4 bytes: "INDX" chunk name
            bw.Write(Encoding.ASCII.GetBytes("INDX"));
            uint nIdexes = (uint)g.Indices.Length;
            // 4 bytes: wsize = sizeof(uint32) + sizeof(ushort)*nIdexes
            bw.Write(4 + 2 * nIdexes);
            // 4 bytes: nIdexes
            bw.Write(nIdexes);
            // nIdexes*2 bytes: MOVI
            for (int i = 0; i < g.Indices.Length; i++) bw.Write(g.Indices[i]);

            // 4 bytes: "VERT" chunk name
            bw.Write(Encoding.ASCII.GetBytes("VERT"));
            // 4 bytes: wsize = sizeof(int) + sizeof(float)*3*nVertices
            bw.Write(4 + 12 * g.Vertices.Length);
            // 4 bytes: nVertices
            bw.Write((uint)g.Vertices.Length);
            // nVertices*12 bytes: MOVT
            foreach (var v in g.Vertices)
            {
                bw.Write(v.X);
                bw.Write(v.Y);
                bw.Write(v.Z);
            }
        }

        // === Patch nVectors at offset 8 (fseek(output, 8, SEEK_SET) in C++) ===
        long endPos = ms.Position;
        ms.Position = nVectorsOffset;
        bw.Write(totalVertices);
        ms.Position = endPos;

        return ms.ToArray();
    }
}
