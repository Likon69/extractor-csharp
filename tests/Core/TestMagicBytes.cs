using MaNGOS.Extractor.Core.Constants;

namespace MaNGOS.Extractor.Tests.Core;

public class MagicBytesTests
{
    [Fact]
    public void ChunkMagics_AreValid()
    {
        Assert.NotEqual(0u, MagicBytes.Mver);
        Assert.NotEqual(0u, MagicBytes.Mhdr);
        Assert.NotEqual(0u, MagicBytes.Mcin);
        Assert.NotEqual(0u, MagicBytes.Mcnk);
        Assert.NotEqual(0u, MagicBytes.Mtex);
    }

    [Fact]
    public void WmoMagics_AreValid()
    {
        Assert.NotEqual(0u, MagicBytes.Mohd);
        Assert.NotEqual(0u, MagicBytes.Mogp);
        Assert.NotEqual(0u, MagicBytes.Movt);
        Assert.NotEqual(0u, MagicBytes.Movi);
    }

    [Fact]
    public void FourCC_Conversion_BackAndForth()
    {
        uint magic = MagicBytes.FourCCToString("MVER");
        string result = MagicBytes.FourCCToString(magic);
        Assert.Contains("MVER", result);
    }

    [Fact]
    public void MapMagic_Wotlk()
    {
        // Faithful port of MaNGOS C++ ExtractorCommon.cpp::setMapMagicVersion(CLIENT_WOTLK).
        // The C++ writes the literal "v1.5" (bytes 0x76, 0x31, 0x2E, 0x35), which on
        // little-endian x86 reads back as the uint32 0x352E3176. The previous test
        // value 0x76312E35u was byte-reversed (it would be correct only on a
        // big-endian machine) and never matched the production code.
        Assert.Equal(0x352E3176u, MagicBytes.MapMagicWotlk);
    }

    [Fact]
    public void LiquidMapMagic()
    {
        Assert.NotEqual(0u, MagicBytes.LiquidMapMagic);
    }

    [Fact]
    public void MmapMagics()
    {
        Assert.NotEqual(0u, MagicBytes.MmapTileMagic);
        Assert.Equal(4u, MagicBytes.MmapVersion);
    }
}
