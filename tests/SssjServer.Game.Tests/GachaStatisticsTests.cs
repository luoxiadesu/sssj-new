using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class GachaStatisticsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SuccessfulDrawCountsCardsAndOneOperation(bool weapon, bool ten)
    {
        using var h = await FeatureHarness.CreateAsync($"gacha-stat-{weapon}-{ten}");
        var ticket = h.Player.AddItem(weapon ? 7u : 9u,
            weapon ? (ten ? 10u : 7u) : (ten ? 13u : 12u), 1, weapon ? 4u : 1u, 1);
        int totalKey = weapon ? 1001 : GachaFeature.TaskTotal;
        int operationKey = weapon ? (ten ? 10004 : 10003) : (ten ? 10002 : 10001);
        h.Player.SetTask(15, totalKey, 20);
        h.Player.SetTask(15, operationKey, 3);
        await Draw(h, weapon, ten);
        Assert.Equal(0u, ticket.Count);
        Assert.Equal(20 + (ten ? 10 : 1), h.Player.GetTask(15, totalKey));
        Assert.Equal(4, h.Player.GetTask(15, operationKey));
        Assert.Equal(4, h.Module.Store.Load(h.Player.Account)!.GetTask(15, operationKey));
        Assert.Equal(0, h.Player.GetTask(15, weapon ? 1 : 1001));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CounterOverflowRejectsBeforeConsumingTicket(bool weapon)
    {
        using var h = await FeatureHarness.CreateAsync($"gacha-stat-limit-{weapon}");
        var ticket = h.Player.AddItem(weapon ? 7u : 9u, weapon ? 7u : 12u, 1, weapon ? 4u : 1u, 1);
        h.Player.SetTask(15, weapon ? 1001 : 1, int.MaxValue);
        await Draw(h, weapon, false);
        Assert.Equal(1u, ticket.Count);
        Assert.Equal(0, h.Player.GetTask(15, weapon ? 10003 : 10001));
    }

    private static Task Draw(FeatureHarness h, bool weapon, bool ten) =>
        h.LuaCalls.DispatchAsync(h.Session, weapon ? "WeaponLogicMsg" : "Lottery",
            weapon ? $"{{\"nCmd\":0,\"Id\":1,\"isTen\":{ten.ToString().ToLowerInvariant()}}}"
                : $"{{\"nId\":1,\"bTen\":{ten.ToString().ToLowerInvariant()}}}");
}
