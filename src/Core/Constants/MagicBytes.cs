using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MaNGOS.Extractor.Core.Constants;

public static class MagicBytes
{
    public const uint MapMagicWotlk = 0x352E3176;
    public const uint MapMagicTbc = 0x73312E35;
    public const uint MapMagicClassic = 0x7A312E35;

    public static ReadOnlySpan<byte> VMapMagicWotlk => "VMAPt07"u8;
    public static ReadOnlySpan<byte> MMapMagicWotlk => "t06"u8;

    public const uint HeightMapMagic = 0x5447484D; // "MHGT" LE — MaNGOS MAP_HEIGHT_MAGIC
    public const uint LiquidMapMagic = 0x51494C4D; // "MLIQ" LE — MaNGOS MAP_LIQUID_MAGIC
    public const uint MmapTileMagic = 0x4D4D4150;
    public const uint MmapMagic = MmapTileMagic;
    public const uint MmapVersion = 4;
    public const uint DtNavMeshVersion = 7;
    public const uint Main = 0x4D41494E;
    public const uint Mamp = 0x4D414D50;
    public const uint OffMeshMagic = 0x4D4D4F46;

    public const uint Mver = 0x4D564552;
    public const uint Mphd = 0x4D504844;  // WDT header flags chunk
    public const uint Mhdr = 0x4D484452;
    public const uint Mcin = 0x4D43494E;
    public const uint Mcnk = 0x4D434E4B;
    public const uint Mtex = 0x4D544558;
    public const uint Mdxg = 0x4D445847;
    public const uint Mddf = 0x4D444446;
    public const uint Modf = 0x4D4F4446;
    public const uint Mfbo = 0x4D46424F;
    public const uint Mh2o = 0x4D48324F;
    public const uint Mmdx = 0x4D4D4458;
    public const uint Mmid = 0x4D4D4944;
    public const uint Mwmo = 0x4D574D4F;
    public const uint Mwid = 0x4D574944;
    public const uint Movv = 0x4D4F5656;

    public const uint Mcvt = 0x4D435654;
    public const uint Mcnr = 0x4D434E52;
    public const uint Mcly = 0x4D434C59;
    public const uint Mcrf = 0x4D435246;
    public const uint Mcal = 0x4D43414C;
    public const uint Mclq = 0x4D434C51;

    public const uint Mohd = 0x4D4F4844;
    public const uint Mogp = 0x4D4F4750;
    public const uint Mogn = 0x4D4F474E;
    public const uint Motx = 0x4D4F5458;
    public const uint Modn = 0x4D4F444E;
    public const uint Modd = 0x4D4F4444;
    public const uint Mods = 0x4D4F4453;
    public const uint Movt = 0x4D4F5654;
    public const uint Movi = 0x4D4F5649;
    public const uint Mopy = 0x4D4F5059;
    public const uint Moba = 0x4D4F4241;
    public const uint Mobr = 0x4D4F4252;
    public const uint Molog = 0x4D4F4C47;
    public const uint Molr = 0x4D4F4C52;
    public const uint Mogv = 0x4D4F4756;
    public const uint Motv = 0x4D4F5456;
    public const uint Molv = 0x4D4F4C56;
    public const uint Moll = 0x4D4F4C4C;
    public const uint Mols = 0x4D4F4C53;
    public const uint Mopv = 0x4D4F5056;
    public const uint Mops = 0x4D4F5053;
    public const uint Mopt = 0x4D4F5054;
    public const uint Mopb = 0x4D4F5042;
    public const uint Movb = 0x4D4F5642;
    public const uint Moxv = 0x4D4F5856;
    public const uint Moby = 0x4D4F4259;
    public const uint Liqu = 0x4C495155;
    public const uint Lmvw = 0x4C4D5657;
    public const uint Lmvw_v = 0x4C4D5657;
    public const uint Lvlu = 0x4C564C55;

    public const uint DbcMagic = 0x43424457;

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
