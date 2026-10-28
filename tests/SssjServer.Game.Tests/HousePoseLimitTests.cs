using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class HousePoseLimitTests
{
    [Fact]
    public void OriginalDefaultLayoutsRemainWithinTheirParentEnvelope()
    {
        var table = HouseTable.Load(TestPaths.SettingDir);
        Assert.Equal(new uint[] { 45, 38 }, table.Items[2].InnerSize);
        Assert.Equal(new uint[] { 28, 28 }, table.Items[10].InnerSize);
        Assert.Equal(new uint[] { 30, 34 }, table.Items[30].InnerSize);
        foreach (var f in HouseFeature.CreateDefault(table).Furniture)
            Assert.True(HousePoseLimits.Valid(table.Items[(int)f.ParentId], f.PosX, f.PosY, f.Rotate, table.Items[f.TemplateId]));
    }

    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(45, 38, 90, true)]
    [InlineData(45, 38, 360, true)]
    [InlineData(46, 38, 0, false)]
    [InlineData(45, 39, 0, false)]
    [InlineData(0, 0, 45, false)]
    public void GridEnvelopeIsConservativeAndDoesNotAssumeFootprints(int x, int y, int angle, bool valid)
        => Assert.Equal(valid, HousePoseLimits.Valid(HouseTable.Load(TestPaths.SettingDir).Items[2],
            (uint)x, (uint)y, (uint)angle));

    [Fact]
    public void MissingGridDoesNotInventDimensions()
        => Assert.True(HousePoseLimits.Valid(new HouseItemRow(), 100, 100, 90));

    [Theory]
    [InlineData(25, 23, 0, true)]
    [InlineData(26, 23, 0, false)]
    [InlineData(23, 25, 90, true)]
    [InlineData(24, 25, 90, false)]
    [InlineData(25, 23, 180, true)]
    [InlineData(23, 25, 270, true)]
    [InlineData(25, 23, 360, true)]
    [InlineData(uint.MaxValue, 0, 0, false)]
    public void SofaFootprintRotatesAndMayTouchButNotCrossTheGridEdge(uint x, uint y, uint angle, bool valid)
    {
        var table = HouseTable.Load(TestPaths.SettingDir);
        Assert.Equal(new uint[] { 3, 5 }, table.Items[703].Size);
        Assert.Equal(valid, HousePoseLimits.Valid(table.Items[10], x, y, angle, table.Items[703]));
    }
}
