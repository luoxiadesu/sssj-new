using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class GachaAchievementTests
{
    [Fact]
    public async Task PulseProjectsRealRecruitCountAndPreservesClaimBit()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-achieve-pulse");
        var feature = h.Module.Features.OfType<MissionFeature>().Single();
        h.Player.SetTask(15, 1, 999);
        ((IGamePulseFeature)feature).OnPlayerPulse(h.Session, h.Player);
        Assert.Equal(999 << 1, h.Player.GetTask(6, 2217));
        h.Player.SetTask(6, 2217, (999 << 1) | 1);
        h.Player.SetTask(15, 1, 1000);
        ((IGamePulseFeature)feature).OnPlayerPulse(h.Session, h.Player);
        Assert.Equal((1000 << 1) | 1, h.Player.GetTask(6, 2217));
        Assert.Equal((1000 << 1) | 1, h.Module.Store.Load(h.Player.Account)!.GetTask(6, 2217));
    }

    [Fact]
    public async Task ThresholdAndConcurrentClaimsGrantOnlyOnce()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-achieve-claim");
        h.Player.SetTask(15, 1, 999);
        await Claim(h);
        Assert.NotEqual(0, JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>());
        int moneyBefore = h.Player.GetMoney(2);
        h.Player.SetTask(15, 1, 1000);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Claim(h))));
        Assert.Equal(moneyBefore + 2000, h.Player.GetMoney(2));
        Assert.Equal((1000 << 1) | 1, h.Player.GetTask(6, 2217));
        await Claim(h);
        Assert.Equal(moneyBefore + 2000, h.Player.GetMoney(2));
    }

    [Fact]
    public void WeaponCountsDoNotContributeToRoleRecruitAchievement()
    {
        var player = new Player("gacha-achieve-weapon", 1);
        player.SetTask(15, 1001, 1000);
        Assert.Equal(0, MissionFeature.DerivedProgress(player,
            new MissionEntry { Group = 6, AchieveType = 60 }));
    }

    private static Task Claim(FeatureHarness h) => h.LuaCalls.DispatchAsync(h.Session,
        "AchievementGetAward", "{\"nId\":2217}");
}
