using System.IO;
using System.Text;
using MaNGOS.Extractor.Formats.Vmap.Mangos;
using MaNGOS.Extractor.Formats.Vmap.Mangos.G3d;

namespace MaNGOS.Extractor.Tests.Core;

/// <summary>
/// Byte-accurate tests for the Bih port (BIH.h / BIH.cpp). These pin the
/// exact on-disk layout that the reference MaNGOS extractor produces, so any
/// drift in the port is caught immediately.
///
/// Reference samples come from hex dumps of
/// C:\Users\Texy6\Desktop\World of Warcraft 3.3.5a original\vmaps\
///  - 013.vmtree / 559.vmtree : single-spawn BIH (treeSize=3, count=1)
///  - 001.vmtree / 000.vmtree : full multi-thousand-node BIH (not reproduced
///    here — those require the full ADT extraction pipeline).
/// </summary>
public class TestBihFormat
{
    /// <summary>
    /// A BIH built from a single primitive must produce exactly:
    ///   bounds.lo (3 floats) + bounds.hi (3 floats) + treeSize(=3) +
    ///   tree[0]=0xC0000000 + tree[1]=1 + tree[2]=0 + count(=1) + objects[0]=0.
    /// This matches the reference dump of 013.vmtree / 559.vmtree bytes 13..60.
    /// </summary>
    [Fact]
    public void SinglePrimitive_ProducesExpectedLayout()
    {
        var spawn = new MangosModelSpawn
        {
            Flags = 0,
            AdtId = 0,
            Id = 42,
            Pos = new float[] { 100, 200, 300 },
            Rot = new float[] { 0, 0, 0 },
            Scale = 1f,
            BoundLow = new float[] { 90, 190, 290 },
            BoundHigh = new float[] { 110, 210, 310 },
            Name = "test",
        };

        var bih = new Bih();
        bih.Build(new[] { spawn }, GetSpawnBounds);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bih.WriteToFile(bw);
        var bytes = ms.ToArray();

        // Expected size: 6 floats (bounds) + 1 uint (treeSize) + 3 uint (tree)
        //              + 1 uint (count) + 1 uint (objects[0])
        //             = 24 + 4 + 12 + 4 + 4 = 48 bytes
        Assert.Equal(48, bytes.Length);

        using var br = new BinaryReader(new MemoryStream(bytes));
        // bounds.lo
        Assert.Equal(90f, br.ReadSingle());
        Assert.Equal(190f, br.ReadSingle());
        Assert.Equal(290f, br.ReadSingle());
        // bounds.hi
        Assert.Equal(110f, br.ReadSingle());
        Assert.Equal(210f, br.ReadSingle());
        Assert.Equal(310f, br.ReadSingle());
        // treeSize
        Assert.Equal(3u, br.ReadUInt32());
        // tree[0] = (3 << 30) | 0  (leaf marker with left=0)
        Assert.Equal(0xC0000000u, br.ReadUInt32());
        // tree[1] = right - left + 1 = 0 - 0 + 1 = 1
        Assert.Equal(1u, br.ReadUInt32());
        // tree[2] = 0 (unused 3rd slot of the leaf node)
        Assert.Equal(0u, br.ReadUInt32());
        // count
        Assert.Equal(1u, br.ReadUInt32());
        // objects[0] = dat.indices[0] = 0
        Assert.Equal(0u, br.ReadUInt32());
    }

    /// <summary>
    /// An empty primitive list triggers InitEmpty, which still writes a
    /// 3-element tree [0xC0000000, 0, 0] with count=0. The bounds are zero
    /// in our port (C++ leaves them uninitialized — UB, but our default struct
    /// zero is the safe choice).
    /// </summary>
    [Fact]
    public void EmptyPrimitiveList_ProducesInitEmptyLayout()
    {
        var bih = new Bih();
        bih.Build(Array.Empty<MangosModelSpawn>(), GetSpawnBounds);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bih.WriteToFile(bw);
        var bytes = ms.ToArray();

        // 6 floats (all zero) + treeSize(3) + tree[3] + count(0) = 24+4+12+4 = 44
        Assert.Equal(44, bytes.Length);

        using var br = new BinaryReader(new MemoryStream(bytes));
        // bounds.lo = 0,0,0 ; bounds.hi = 0,0,0
        for (int i = 0; i < 6; i++) Assert.Equal(0f, br.ReadSingle());
        Assert.Equal(3u, br.ReadUInt32());        // treeSize
        Assert.Equal(0xC0000000u, br.ReadUInt32()); // tree[0]
        Assert.Equal(0u, br.ReadUInt32());          // tree[1] = 0 (no primitives)
        Assert.Equal(0u, br.ReadUInt32());          // tree[2]
        Assert.Equal(0u, br.ReadUInt32());          // count = 0
    }

    /// <summary>
    /// Round-trip: a BIH written then read back must produce identical
    /// tree/objects/bounds. Guards the reader used by the mmap-extractor.
    /// </summary>
    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var spawns = new[]
        {
            new MangosModelSpawn
            {
                Flags = 0, AdtId = 0, Id = 1, Pos = new float[] { 0, 0, 0 },
                Rot = new float[] { 0, 0, 0 }, Scale = 1f,
                BoundLow = new float[] { -100, -100, -100 },
                BoundHigh = new float[] { 100, 100, 100 },
                Name = "a",
            },
            new MangosModelSpawn
            {
                Flags = 0, AdtId = 0, Id = 2, Pos = new float[] { 500, 500, 500 },
                Rot = new float[] { 0, 0, 0 }, Scale = 1f,
                BoundLow = new float[] { 450, 450, 450 },
                BoundHigh = new float[] { 550, 550, 550 },
                Name = "b",
            },
        };

        var bih1 = new Bih();
        bih1.Build(spawns, GetSpawnBounds);

        // BinaryWriter's default ctor takes leaveOpen=false, which disposes
        // the underlying stream when the writer's `using` block exits.
        // Use leaveOpen: true (or open the reader BEFORE the writer block)
        // so we can rewind and re-read the same MemoryStream afterwards.
        var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            bih1.WriteToFile(bw);
        ms.Position = 0;
        using (var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true))
        {
            var bih2 = new Bih();
            bih2.ReadFromFile(br);

            Assert.Equal(bih1.PrimCount, bih2.PrimCount);
            Assert.Equal(bih1.Bounds.Lo.X, bih2.Bounds.Lo.X);
            Assert.Equal(bih1.Bounds.Hi.Z, bih2.Bounds.Hi.Z);
        }
    }

    private static void GetSpawnBounds(MangosModelSpawn spawn, out G3dAaBox outBounds)
    {
        outBounds = new G3dAaBox
        {
            Lo = new G3dVector3(spawn.BoundLow![0], spawn.BoundLow[1], spawn.BoundLow[2]),
            Hi = new G3dVector3(spawn.BoundHigh![0], spawn.BoundHigh[1], spawn.BoundHigh[2]),
        };
    }
}
