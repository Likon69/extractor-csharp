using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MaNGOS.Extractor.Core.Constants;

public static class MagicBytes
{
    // Faithful port of MaNGOS: each chunk's magic is the uint32 that
    // MemoryMarshal.Read<uint>() returns from the FOURCC bytes as they
    // appear in the file on little-endian x86. The first file byte
    // becomes the LSB of the uint32. The previous constants had the
    // FOURCC string spelling stored in big-endian order (first char in
    // the MSB), which made every case statement fail silently and the
    // parsers skip all chunks.
    public const uint MapMagicWotlk = 0x352E3176;     // "v1.5" LE
    public const uint MapMagicTbc = 0x352E3173;       // "s1.5" LE
    public const uint MapMagicClassic = 0x352E317A;   // "z1.5" LE

    public static ReadOnlySpan<byte> VMapMagicWotlk => "VMAPt07"u8;
    public static ReadOnlySpan<byte> MMapMagicWotlk => "t06"u8;

    public const uint HeightMapMagic = 0x5447484D; // "MHGT" LE — MaNGOS MAP_HEIGHT_MAGIC
    public const uint LiquidMapMagic = 0x51494C4D; // "MLIQ" LE
    public const uint MmapTileMagic = 0x4D4D4150;  // "MMAP" big-endian (= "PAMM" on LE disk, matches MaNGOS C++ MMAP_MAGIC)
    public const uint MmapWatermark = 0xA3F8C1D7u;  // MaNGOS C++ MMAP_WATERMARK
    public const uint MmapMagic = MmapTileMagic;
    public const uint MmapVersion = 4;
    public const uint MmapMultiTileVersion = 5;
    public const uint DtNavMeshVersion = 7;
    public const uint Main = 0x4E49414D;          // "MAIN" LE
    public const uint Mamp = 0x504D414D;          // "MAMP" LE
    public const uint OffMeshMagic = 0x464F4D4D;  // "MMOF" LE

    public const uint Mver = 0x5245564D; // "MVER" LE
    public const uint Mphd = 0x4448504D; // "MPHD" LE — WDT header flags chunk
    public const uint Mhdr = 0x5244484D; // "MHDR" LE
    public const uint Mcin = 0x4E49434D; // "MCIN" LE
    public const uint Mcnk = 0x4B4E434D; // "MCNK" LE
    public const uint Mtex = 0x5845544D; // "MTEX" LE
    public const uint Mdxg = 0x4758444D; // "MDXG" LE
    public const uint Mddf = 0x4644444D; // "MDDF" LE
    public const uint Modf = 0x46444F4D; // "MODF" LE
    public const uint Mfbo = 0x4F42464D; // "MFBO" LE
    public const uint Mh2o = 0x4F32484D; // "MH2O" LE
    public const uint Mmdx = 0x58444D4D; // "MMDX" LE
    public const uint Mmid = 0x44494D4D; // "MMID" LE
    public const uint Mwmo = 0x4F4D574D; // "MWMO" LE
    public const uint Mwid = 0x4449574D; // "MWID" LE
    public const uint Movv = 0x56564F4D; // "MOVV" LE

    public const uint Mcvt = 0x5456434D; // "MCVT" LE
    public const uint Mcnr = 0x524E434D; // "MCNR" LE
    public const uint Mcly = 0x594C434D; // "MCLY" LE
    public const uint Mcrf = 0x4652434D; // "MCRF" LE
    public const uint Mcal = 0x4C41434D; // "MCAL" LE
    public const uint Mclq = 0x514C434D; // "MCLQ" LE

    public const uint Mohd = 0x44484F4D; // "MOHD" LE
    public const uint Mogp = 0x50474F4D; // "MOGP" LE
    public const uint Mogn = 0x4E474F4D; // "MOGN" LE
    public const uint Motx = 0x584F544D; // "MOTX" LE
    public const uint Modn = 0x4E444F4D; // "MODN" LE
    public const uint Modd = 0x44444F4D; // "MODD" LE
    public const uint Mods = 0x53444F4D; // "MODS" LE
    public const uint Modr = 0x52444F4D; // "MODR" LE — WMO doodad references per group
    public const uint Movt = 0x54564F4D; // "MOVT" LE
    public const uint Movi = 0x49564F4D; // "MOVI" LE
    public const uint Mopy = 0x59504F4D; // "MOPY" LE
    public const uint Moba = 0x41424F4D; // "MOBA" LE
    public const uint Mobr = 0x52424F4D; // "MOBR" LE
    public const uint Molog = 0x474C4F4D; // "MOLOG" LE
    public const uint Molr = 0x524C4F4D; // "MOLR" LE
    public const uint Mogv = 0x56474F4D; // "MOGV" LE
    public const uint Motv = 0x56544F4D; // "MOTV" LE
    public const uint Molv = 0x564C4F4D; // "MOLV" LE
    public const uint Moll = 0x4C4C4F4D; // "MOLL" LE
    public const uint Mols = 0x534C4F4D; // "MOLS" LE
    public const uint Mopv = 0x56504F4D; // "MOPV" LE
    public const uint Mops = 0x53504F4D; // "MOPS" LE
    public const uint Mopt = 0x54504F4D; // "MOPT" LE
    public const uint Mopb = 0x42504F4D; // "MOPB" LE
    public const uint Movb = 0x42564F4D; // "MOVB" LE
    public const uint Moxv = 0x56584F4D; // "MOXV" LE
    public const uint Moby = 0x59424F4D; // "MOBY" LE
    public const uint Liqu = 0x5551494C; // "LIQU" LE
    public const uint Lmvw = 0x57564D4C; // "LMVW" LE
    public const uint Lmvw_v = Lmvw;
    public const uint Lvlu = 0x554C564C; // "LVLU" LE

    public const uint DbcMagic = 0x43424457; // "WDBC" LE (was 0x57444243 — byte-reversed)

    public static uint ReadFourCC(ReadOnlySpan<byte> data)
    {
        return MemoryMarshal.Read<uint>(data);
    }

    public static uint FourCCToString(string str)
    {
        if (string.IsNullOrEmpty(str) || str.Length < 4)
            return 0;
        uint result = 0;
        result |= (uint)(byte)str[0];
        result |= (uint)(byte)str[1] << 8;
        result |= (uint)(byte)str[2] << 16;
        result |= (uint)(byte)str[3] << 24;
        return result;
    }

    public static string FourCCToString(uint fourCC)
    {
        byte b0 = (byte)((fourCC >> 0) & 0xFF);
        byte b1 = (byte)((fourCC >> 8) & 0xFF);
        byte b2 = (byte)((fourCC >> 16) & 0xFF);
        byte b3 = (byte)((fourCC >> 24) & 0xFF);
        return Encoding.ASCII.GetString(new[] { b0, b1, b2, b3 });
    }
}
