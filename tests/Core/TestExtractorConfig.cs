using MaNGOS.Extractor.Core.Models;

namespace MaNGOS.Extractor.Tests.Core;

public class ExtractorConfigTests
{
    [Fact]
    public void Constructor_AllProperties()
    {
        var recastConfig = new RecastConfig(0.3f, 0.2f, 45f, 10, 3, 4);
        Assert.Equal(0.3f, recastConfig.CellSize);
        Assert.Equal(0.2f, recastConfig.CellHeight);
        Assert.Equal(45f, recastConfig.WalkableSlopeAngle);
        Assert.Equal(10, recastConfig.WalkableHeight);
        Assert.Equal(3, recastConfig.WalkableRadius);
        Assert.Equal(4, recastConfig.WalkableClimb);
    }

    [Fact]
    public void TileProgressEvent_Defaults()
    {
        var evt = new TileProgressEvent(0, 32, 32, TileStatus.Done, ExtractionPhase.Map);
        Assert.Equal(0, evt.MapId);
        Assert.Equal(32, evt.TileX);
        Assert.Equal(32, evt.TileY);
        Assert.Equal(TileStatus.Done, evt.Status);
        Assert.Equal(ExtractionPhase.Map, evt.Phase);
        Assert.Null(evt.Message);
    }

    [Fact]
    public void TileProgressEvent_WithMessage()
    {
        var evt = new TileProgressEvent(571, 0, 0, TileStatus.Failed, ExtractionPhase.Mmap, "No geometry");
        Assert.Equal(571, evt.MapId);
        Assert.Equal(0, evt.TileX);
        Assert.Equal(0, evt.TileY);
        Assert.Equal(TileStatus.Failed, evt.Status);
        Assert.Equal(ExtractionPhase.Mmap, evt.Phase);
        Assert.Equal("No geometry", evt.Message);
    }

    [Fact]
    public void DefaultValues()
    {
        var config = new ExtractorConfig();
        Assert.Null(config.WowClientPath);
        Assert.Null(config.OutputPath);
        Assert.Empty(config.EnabledPhases);
        Assert.Empty(config.SelectedMapIds);
    }
}
