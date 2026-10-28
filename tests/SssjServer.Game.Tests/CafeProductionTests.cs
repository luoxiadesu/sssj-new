using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public sealed class CafeProductionTests
{
    [Fact]
    public async Task ProductionChargesConfiguredCostAndPersistsBothChanges()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-production-cost");
        h.Player.SetTask(23, 1, 1 << 8);
        int before = h.Player.GetMoney(2);
        int cost = CafeTable.Load(TestPaths.SettingDir).Coffees[1].Cost;
        await h.DispatchLuaCallAsync(119, "{\"coffeetype\":1,\"count\":3}");
        Assert.Equal(before - cost * 3, h.Player.GetMoney(2));
        Assert.True(await h.WaitForPfAsync(GamePf.MoneyUpdateNtf));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(before - cost * 3, saved.GetMoney(2));
        Assert.Equal(3, Content(saved).Coffees.Single().Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483647")]
    [InlineData("1.5")]
    [InlineData("null")]
    public async Task InvalidOrUnaffordableBatchChangesNothing(string count)
    {
        using var h = await FeatureHarness.CreateAsync("cafe-production-invalid");
        h.Player.SetTask(23, 1, 1 << 8);
        int before = h.Player.GetMoney(2);
        await h.DispatchLuaCallAsync(119, "{\"coffeetype\":1,\"count\":" + count + "}");
        Assert.Equal(before, h.Player.GetMoney(2));
        Assert.Empty(Content(h.Player).Coffees);
        var response = JsonNode.Parse(h.LastOutbound.Json)!;
        Assert.NotNull(response["tbParam"]?["nError"]);
    }

    [Fact]
    public async Task InsufficientBalanceDoesNotAddCoffee()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-production-poor");
        h.Player.SetTask(23, 1, 1 << 8);
        h.Player.AddMoney(2, -h.Player.GetMoney(2));
        await h.DispatchLuaCallAsync(119, "{\"coffeetype\":1,\"count\":1}");
        Assert.Equal(0, h.Player.GetMoney(2));
        Assert.Empty(Content(h.Player).Coffees);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task UnlearnedRecipeRejectsWithoutChargingOrSaving(int bits)
    {
        using var h = await FeatureHarness.CreateAsync("cafe-production-unlearned");
        h.Player.SetTask(23, 1, bits);
        var before = h.Player.GetMoney(2);
        await h.DispatchLuaCallAsync(119, "{\"coffeetype\":1,\"count\":3}");
        Assert.NotNull(JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]?["nError"]);
        Assert.Equal(before, h.Player.GetMoney(2));
        Assert.Equal(bits, h.Player.GetTask(23, 1));
        Assert.Empty(Content(h.Player).Coffees);
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(before, saved.GetMoney(2));
        Assert.Empty(Content(saved).Coffees);
    }

    [Theory]
    [InlineData(1, 240)]
    [InlineData(2, 260)]
    public async Task ProductionCapacityUsesCafeLevelAndRejectsOverflowAtomically(int level, int max)
    {
        using var h = await FeatureHarness.CreateAsync("cafe-cup-cap");
        h.Player.SetTask(23, 1, 256);
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(level, 0, 0));
        await h.DispatchLuaCallAsync(119, $"{{\"coffeetype\":1,\"count\":{max}}}");
        Assert.Null(JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]?["nError"]);
        Assert.Equal(max, Content(h.Player).Coffees.Single().Count);
        int before = h.Player.GetMoney(2);
        await h.DispatchLuaCallAsync(119, "{\"coffeetype\":1,\"count\":1}");
        Assert.NotNull(JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]?["nError"]);
        Assert.Equal(before, h.Player.GetMoney(2));
        Assert.Equal(max, Content(h.Player).Coffees.Single().Count);
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(before, saved.GetMoney(2));
        Assert.Equal(max, Content(saved).Coffees.Single().Count);
    }

    private static CafeFeature.CafeContent Content(Player p)
        => CafeFeature.ReadContent(p, CafeFeature.ReadState(p), CafeTable.Load(TestPaths.SettingDir),
            CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow));
}
