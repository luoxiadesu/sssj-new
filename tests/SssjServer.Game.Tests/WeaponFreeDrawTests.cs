using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class WeaponFreeDrawTests
{
    private static WeaponGachaPool Pool(int type = 1) => new()
    { Id = 1, FreeType = type, FreeCount = 1, CashType = 3, CostOneMoney = 90, CostTenMoney = 900 };
    private static long Time(int day, int hour) => new DateTimeOffset(2026, 10, day, hour, 0, 0,
        TimeSpan.FromHours(8)).ToUnixTimeSeconds();

    [Fact]
    public void WeaponAndCardDailyCountsAreIndependentEvenWithSamePoolId()
    {
        var player = new Player("free-independent", 1);
        var card = new GachaPool { Id = 1, FreeType = 1, FreeCount = 1 };
        GachaFreeDraws.Refresh(player, [card], Time(24, 12), []);
        GachaFreeDraws.RefreshWeapons(player, [Pool()], Time(24, 12), []);
        player.SetTask(15, 8002, 1);
        player.SetTask(15, 9002, 1);
        var tasks = new List<KeyValuePair<uint, int>>();
        Assert.True(GachaFreeDraws.Refresh(player, [card], Time(25, 4), tasks));
        Assert.Equal(1, player.GetTask(15, 9002));
        Assert.True(GachaFreeDraws.RefreshWeapons(player, [Pool()], Time(25, 4), tasks));
        Assert.Equal(0, player.GetTask(15, 9002));
        Assert.Equal(2, tasks.Count);
        player.SetTask(15, 9002, 1);
        Assert.False(GachaFreeDraws.RefreshWeapons(player, [Pool()], Time(24, 12), []));
        Assert.Equal(1, player.GetTask(15, 9002));
    }

    [Fact]
    public void WeaponSingleIsFreeAndTenStillCostsMoneyWithoutUsingFreeQuota()
    {
        var player = new Player("weapon-free-payment", 1);
        player.SetMoney(3, 1000);
        var ticket = player.AddItem(7, 7, 1, 4, count: 5);
        var feature = new WeaponForgeFeature();
        Assert.True(feature.TrySpend(player, Pool(), true, Time(24, 12)).Ok);
        Assert.Equal(100, player.GetMoney(3));
        Assert.Equal(0, player.GetTask(15, 9002));
        var free = feature.TrySpend(player, Pool(), false, Time(24, 12));
        Assert.True(free.Ok);
        Assert.Empty(free.ChangedMoney);
        Assert.Empty(free.ChangedItems);
        Assert.Equal(100, player.GetMoney(3));
        Assert.Equal(5u, ticket.Count);
        Assert.Equal(1, player.GetTask(15, 9002));
    }

    [Fact]
    public async Task WeaponDayMarkerSurvivesSaveReloadAndTypeTwoNeverResets()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-free-persist");
        GachaFreeDraws.RefreshWeapons(h.Player, [Pool()], Time(24, 12), []);
        h.Player.SetTask(15, 9002, 1);
        h.Module.Store.Save(h.Player);
        var player = h.Module.Store.Load(h.Player.Account)!;
        Assert.False(GachaFreeDraws.RefreshWeapons(player, [Pool()], Time(24, 13), []));
        Assert.False(GachaFreeDraws.RefreshWeapons(player, [Pool(2)], Time(25, 4), []));
        Assert.Equal(1, player.GetTask(15, 9002));
        Assert.True(GachaFreeDraws.RefreshWeapons(player, [Pool()], Time(25, 4), []));
        Assert.Equal(0, player.GetTask(15, 9002));
    }

    [Fact]
    public async Task NormalHeartbeatStillRepliesWithBothFreeMaintenanceFeaturesInstalled()
    {
        using var h = await FeatureHarness.CreateAsync("free-pulse");
        var features = h.Module.Features.OfType<IGamePulseFeature>().ToArray();
        Assert.Contains(features, f => f is GachaFeature);
        Assert.Contains(features, f => f is WeaponForgeFeature);
        await h.Module.HandleKeepAlive(new SessionAdapter(h.Session), new Packet { Pf = GamePf.KeepAlive, Serial = 42 });
        Assert.Contains(GamePf.KeepAlive, h.PushedPf);
    }

    [Fact]
    public void WeaponFreeWindowAndConfigurationUseOriginalRules()
    {
        var table = WeaponGachaTable.Load(TestPaths.SettingDir);
        Assert.Contains(table.Segments(509), p => p.FreeType == 1 && p.FreeCount == 1);
        Assert.Contains(table.Segments(519), p => p.FreeType == 1 && p.FreeCount == 1);
        var pool = new WeaponGachaPool { FreeType = 1, FreeCount = 1, FreeStart = 100, FreeEnd = 200 };
        Assert.False(pool.FreeAvailableAt(99, 0));
        Assert.True(pool.FreeAvailableAt(200, 0));
        Assert.False(pool.FreeAvailableAt(201, 0));
        Assert.False(pool.FreeAvailableAt(150, -1));
    }
}
