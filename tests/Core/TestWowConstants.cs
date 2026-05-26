using MaNGOS.Extractor.Core.Constants;

namespace MaNGOS.Extractor.Tests.Core;

public class WowConstantsTests
{
    [Fact]
    public void TileSize_IsExpected()
    {
        Assert.Equal(533.333333f, WowConstants.TileSize);
    }

    [Fact]
    public void ChunkSize_IsCorrect()
    {
        float expected = WowConstants.TileSize / WowConstants.ChunksPerTile;
        Assert.Equal(33.333333f, expected, 2);
    }

    [Fact]
    public void MapHalfSize_IsCorrect()
    {
        float expected = WowConstants.TileSize * 32;
        Assert.Equal(17066.666f, expected, 2);
    }

    [Fact]
    public void GridSize_Is64()
    {
        Assert.Equal(64, WowConstants.GridSize);
    }

    [Fact]
    public void ChunksPerTile_Is16()
    {
        Assert.Equal(16, WowConstants.ChunksPerTile);
    }

    [Fact]
    public void TargetBuild_Is12340()
    {
        Assert.Equal(12340u, WowConstants.TargetBuild);
    }

    [Fact]
    public void GetMapDirectory_ReturnsCorrectNames()
    {
        Assert.Equal("Azeroth", WowConstants.GetMapDirectory(0));
        Assert.Equal("Kalimdor", WowConstants.GetMapDirectory(1));
        Assert.Equal("Outland", WowConstants.GetMapDirectory(530));
        Assert.Equal("Northrend", WowConstants.GetMapDirectory(571));
    }

    [Fact]
    public void GetMapDirectory_Fallback()
    {
        Assert.Equal("Map0422", WowConstants.GetMapDirectory(422));
        Assert.Equal("Map1234", WowConstants.GetMapDirectory(1234));
    }
}
