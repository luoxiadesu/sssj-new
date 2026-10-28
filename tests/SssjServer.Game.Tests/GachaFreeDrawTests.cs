using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class GachaFreeDrawTests
{
    [Fact]
    public async Task DayMarkerAndConsumedCountSurviveReload()
    {
        using var h = await FeatureHarness.CreateAsync("free-persist");
        GachaFreeDraws.Refresh(h.Player, [Pool()], Time(24, 12), []);
        h.Player.SetTask(15, 8002, 1);
        h.Module.Store.Save(h.Player);
        var player = h.Module.Store.Load(h.Player.Account)!;
        Assert.False(GachaFreeDraws.Refresh(player, [Pool()], Time(24, 13), []));
        Assert.Equal(1, player.GetTask(15, 8002));
        Assert.True(GachaFreeDraws.Refresh(player, [Pool()], Time(25, 4), []));
        Assert.Equal(0, player.GetTask(15, 8002));
    }

    private static GachaPool Pool(int type = 1) => new()
    { Id = 1, FreeType = type, FreeCount = 1, CashType = 3, CostOneMoney = 150, CostTenMoney = 1500 };
    private static long Time(int day, int hour, int minute = 0)
        => new DateTimeOffset(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();

    [Fact]
    public void FreeSinglePreservesCurrencyAndTicketsButNextSingleCostsMoney()
    {
        var player = new Player("free-single", 1);
        player.SetMoney(3, 3000);
        var ticket = player.AddItem(9, 12, 1, 1, count: 5);
        var feature = new GachaFeature(new Random(1));
        var free = feature.TrySpend(player, Pool(), false, Time(24, 12));
        Assert.True(free.Ok);
        Assert.Empty(free.ChangedMoney);
        Assert.Empty(free.ChangedItems);
        Assert.Single(free.ChangedTasks);
        Assert.Equal(3000, player.GetMoney(3));
        Assert.Equal(5u, ticket.Count);
        Assert.Equal(1, player.GetTask(15, 8002));
        Assert.True(feature.TrySpend(player, Pool(), false, Time(24, 12)).Ok);
        Assert.Equal(2850, player.GetMoney(3));
        Assert.Equal(1, player.GetTask(15, 8002));
    }

    [Fact]
    public void TenDrawDoesNotConsumeFreeSingleEntitlement()
    {
        var player = new Player("free-ten", 1);
        player.SetMoney(3, 2000);
        var spent = new GachaFeature(new Random(1)).TrySpend(player, Pool(), true, Time(24, 12));
        Assert.True(spent.Ok);
        Assert.Equal(500, player.GetMoney(3));
        Assert.Equal(0, player.GetTask(15, 8002));
    }

    [Fact]
    public void DailyResetUsesBeijingFourAmAndDoesNotResetForClockRollback()
    {
        var player = new Player("free-reset", 1);
        var pool = Pool();
        long before = Time(24, 3, 59), after = Time(24, 4);
        Assert.Equal(GachaFreeDraws.Day(before) + 1, GachaFreeDraws.Day(after));
        GachaFreeDraws.Refresh(player, [pool], before, []);
        player.SetTask(15, 8002, 1);
        var tasks = new List<KeyValuePair<uint, int>>();
        Assert.True(GachaFreeDraws.Refresh(player, [pool], after, tasks));
        Assert.Single(tasks);
        Assert.Equal(0, player.GetTask(15, 8002));
        player.SetTask(15, 8002, 1);
        Assert.False(GachaFreeDraws.Refresh(player, [pool], before, []));
        Assert.Equal(1, player.GetTask(15, 8002));
        Assert.False(GachaFreeDraws.Refresh(player, [pool], after, []));
    }

    [Fact]
    public void LegacyCountIsPreservedOnFirstObservationAndNonResettingPoolNeverResets()
    {
        var player = new Player("free-legacy", 1);
        player.SetTask(15, 8002, 1);
        GachaFreeDraws.Refresh(player, [Pool()], Time(24, 12), []);
        Assert.Equal(1, player.GetTask(15, 8002));
        GachaFreeDraws.Refresh(player, [Pool(2)], Time(25, 12), []);
        Assert.Equal(1, player.GetTask(15, 8002));
        GachaFreeDraws.Refresh(player, [Pool()], Time(25, 12), []);
        Assert.Equal(0, player.GetTask(15, 8002));
    }

    [Fact]
    public void FreeWindowMatchesOriginalInclusiveEndAndRejectsUnknownType()
    {
        var pool = new GachaPool { FreeType = 1, FreeCount = 1, FreeStart = 100, FreeEnd = 200 };
        Assert.False(pool.FreeAvailableAt(99, 0));
        Assert.True(pool.FreeAvailableAt(100, 0));
        Assert.True(pool.FreeAvailableAt(200, 0));
        Assert.False(pool.FreeAvailableAt(201, 0));
        Assert.False(pool.FreeAvailableAt(150, 1));
        Assert.False(new GachaPool { FreeType = 3, FreeCount = 1 }.FreeAvailableAt(150, 0));
    }

    [Fact]
    public void ActualDailyFreeConfigurationIsLoaded()
    {
        var pool = GachaTable.Load(TestPaths.SettingDir).Segments(38).Single(p => p.FreeType == 1);
        Assert.Equal(1, pool.FreeCount);
        Assert.True(pool.FreeStart > 0 && pool.FreeEnd > pool.FreeStart);
    }
}
