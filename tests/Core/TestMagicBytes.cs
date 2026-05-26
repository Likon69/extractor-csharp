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
        Assert.Equal(0x76312E35u, MagicBytes.MapMagicWotlk);
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
