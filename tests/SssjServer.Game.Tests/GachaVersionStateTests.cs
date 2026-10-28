using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class GachaVersionStateTests
{
    private static GachaPool Pool(int version) => new()
    { Id = 43, RotateVersion = version, ProtectNum = 50, ProtectUpNum = 100, UpBoxNum = 300, UpBox = [8, 10, 43, 4] };
    private const int Up = GachaFeature.TaskProtectUpBegin + 43;

    [Fact]
    public void NewVersionResetsOnlyUpBenefitAndReplayingOldVersionRestoresItsProgress()
    {
        var player = new Player("rotation-versions", 1);
        player.SetTask(15, Up, -1);
        player.SetTask(15, GachaFeature.TaskProtectBegin + 43, 49);
        player.SetTask(15, GachaFeature.TaskPrePoolBegin + 43, 299);
        player.SetTask(15, GachaFeature.TaskTotal, 1000);
        GachaVersionState.Refresh(player, [Pool(1)], []);
        var tasks = new List<KeyValuePair<uint, int>>();
        Assert.True(GachaVersionState.Refresh(player, [Pool(2)], tasks));
        Assert.Equal(0, player.GetTask(15, Up));
        Assert.Contains(tasks, t => t.Key == Player.ComposeTaskId(15, Up) && t.Value == 0);
        Assert.Equal(49, player.GetTask(15, GachaFeature.TaskProtectBegin + 43));
        Assert.Equal(299, player.GetTask(15, GachaFeature.TaskPrePoolBegin + 43));
        Assert.Equal(1000, player.GetTask(15, GachaFeature.TaskTotal));
        Assert.Single(GachaFeature.PlanBonusRewards(Pool(2), 299, 1));
        player.SetTask(15, Up, 72);
        GachaVersionState.Refresh(player, [Pool(1)], []);
        Assert.Equal(-1, player.GetTask(15, Up));
        GachaVersionState.Refresh(player, [Pool(2)], []);
        Assert.Equal(72, player.GetTask(15, Up));
        Assert.False(GachaVersionState.Refresh(player, [Pool(2)], []));
    }

    [Fact]
    public void LegacyVersionMarkerAllowsKnownTransitionButUnknownSavesKeepCurrentProgress()
    {
        var player = new Player("rotation-legacy", 1);
        player.SetTask(15, Up, -1);
        GachaVersionState.Refresh(player, [Pool(31)], []);
        Assert.Equal(-1, player.GetTask(15, Up));
        Assert.Equal(31, player.GetTask(15, GachaVersionState.LegacyVersionTask));
        var known = new Player("rotation-known", 2);
        known.SetTask(15, Up, 82);
        known.SetTask(15, GachaVersionState.LegacyVersionTask, 30);
        GachaVersionState.Refresh(known, [Pool(31)], []);
        Assert.Equal(0, known.GetTask(15, Up));
        GachaVersionState.Refresh(known, [Pool(30)], []);
        Assert.Equal(82, known.GetTask(15, Up));
    }

    [Fact]
    public async Task PerVersionProgressSurvivesReloadAndOrdinaryPoolsDoNotResetIt()
    {
        using var h = await FeatureHarness.CreateAsync("rotation-persist");
        GachaVersionState.Refresh(h.Player, [Pool(1)], []);
        h.Player.SetTask(15, Up, 55);
        GachaVersionState.Refresh(h.Player, [Pool(2)], []);
        h.Player.SetTask(15, Up, -1);
        h.Module.Store.Save(h.Player);
        var player = h.Module.Store.Load(h.Player.Account)!;
        Assert.False(GachaVersionState.Refresh(player, [new GachaPool { Id = 1 }], []));
        GachaVersionState.Refresh(player, [Pool(1)], []);
        Assert.Equal(55, player.GetTask(15, Up));
        GachaVersionState.Refresh(player, [Pool(2)], []);
        Assert.Equal(-1, player.GetTask(15, Up));
    }

    [Fact]
    public void OriginalConfigurationHasOneRotatingFamilyWithUniqueVersions()
    {
        var rotating = GachaTable.Load(TestPaths.SettingDir).All.Where(p => p.RotateVersion > 0).ToArray();
        Assert.Equal(43, Assert.Single(rotating.Select(p => p.Id).Distinct()));
        Assert.Equal(rotating.Length, rotating.Select(p => p.RotateVersion).Distinct().Count());
    }
}
