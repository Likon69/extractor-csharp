using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MaNGOS.Extractor.Formats.Vmap.Mangos;

/// <summary>
/// ModelSpawn — placement of a WMO or M2 in a map.
/// Binary format used by the Mangos vmap-extractor / mmap-extractor in
/// `.vmtree` and `.vmtile` files. See MaNGOS/src/game/vmap/ModelInstance.{h,cpp}.
/// </summary>
public struct MangosModelSpawn
{
    public uint Flags;     // MOD_M2=1, MOD_WORLDSPAWN=2, MOD_HAS_BOUND=4
    public ushort AdtId;
    public uint Id;
    public float[] Pos;                  // iPos (3 floats)
    public float[] Rot;                  // iRot (3 floats)
    public float Scale;
    public float[]? BoundLow;            // only present when MOD_HAS_BOUND
    public float[]? BoundHigh;
    public string Name;                  // raw in vmtile

    public const uint MOD_M2 = 0x1;
    public const uint MOD_WORLDSPAWN = 0x2;
    public const uint MOD_HAS_BOUND = 0x4;

    public bool IsM2 => (Flags & MOD_M2) != 0;
    public bool IsWorldSpawn => (Flags & MOD_WORLDSPAWN) != 0;
    public bool HasBound => (Flags & MOD_HAS_BOUND) != 0;

    /// <summary>
    /// Read a ModelSpawn from a binary stream in Mangos format.
    /// </summary>
    public static MangosModelSpawn Read(BinaryReader r)
    {
        var s = new MangosModelSpawn
        {
            Pos = new float[3],
            Rot = new float[3],
            Name = ""
        };
        s.Flags = r.ReadUInt32();
        s.AdtId = r.ReadUInt16();
        s.Id = r.ReadUInt32();
        for (int i = 0; i < 3; i++) s.Pos[i] = r.ReadSingle();
        for (int i = 0; i < 3; i++) s.Rot[i] = r.ReadSingle();
        s.Scale = r.ReadSingle();

        if (s.HasBound)
        {
            s.BoundLow = new float[3];
            s.BoundHigh = new float[3];
            for (int i = 0; i < 3; i++) s.BoundLow[i] = r.ReadSingle();
            for (int i = 0; i < 3; i++) s.BoundHigh[i] = r.ReadSingle();
        }

        uint nameLen = r.ReadUInt32();
        if (nameLen > 1024) throw new InvalidDataException($"ModelSpawn name too long: {nameLen}");
        var nameBytes = r.ReadBytes((int)nameLen);
        s.Name = Encoding.ASCII.GetString(nameBytes);
        return s;
    }

    /// <summary>
    /// Write a ModelSpawn to a binary stream in Mangos format.
    /// </summary>
    public void Write(BinaryWriter w)
    {
        if (Pos == null || Pos.Length != 3) throw new InvalidOperationException("Pos must be 3 floats");
        if (Rot == null || Rot.Length != 3) throw new InvalidOperationException("Rot must be 3 floats");
        w.Write(Flags);
        w.Write(AdtId);
        w.Write(Id);
        for (int i = 0; i < 3; i++) w.Write(Pos[i]);
        for (int i = 0; i < 3; i++) w.Write(Rot[i]);
        w.Write(Scale);

        if (HasBound)
        {
            for (int i = 0; i < 3; i++) w.Write(BoundLow![i]);
            for (int i = 0; i < 3; i++) w.Write(BoundHigh![i]);
        }

        var nameBytes = Encoding.ASCII.GetBytes(Name);
        w.Write((uint)nameBytes.Length);
        w.Write(nameBytes);
    }
}

/// <summary>
/// Header of a .vmtree or .vmtile file (8 bytes magic "VMAP_4.0").
/// </summary>
public static class MangosVmapMagic
{
    public const string Tree = "VMAP_4.0";
    public const string NodeChunk = "NODE";
    public const string GobjChunk = "GOBJ";

    /// <summary>
    /// Magic for RAW collision files written to Buildings/ by the vmap-extractor
    /// (MaNGOS C++ vmapexport.cpp). For WotLK 3.3.5a this is "VMAPt07\0" — the
    /// per-client variant is set by ExtractorCommon::setVMapMagicVersion.
    /// These raw files are consumed by TileAssembler (phase 2) which rebuilds
    /// the BIH and writes the final .vmtree/.vmtile with magic "VMAP_4.0".
    /// </summary>
    public const string RawWotlk = "VMAPt07";
}
