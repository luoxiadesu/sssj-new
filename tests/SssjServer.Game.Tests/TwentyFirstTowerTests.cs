using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class TwentyFirstTowerTests
{
    private static string Args(int level, string extra = "{}") => $"[4,21,0,{level},1,{extra}]";
    private static JsonObject Reply(FeatureHarness h) => (JsonObject)JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
    private static async Task Enter(FeatureHarness h, int level) => await h.DispatchLuaCallAsync(54, Args(level));
    private static async Task Pass(FeatureHarness h, int level, string extra = "{}") => await ChallengeTestHarness.SettleAsync(h,55, Args(level, extra));

    [Fact]
    public async Task Mainline_UnlocksSequentiallyAndAwardsYuiExactlyOnceAcrossReload()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-mainline");
        h.Player.SetMoney(MoneyType.Vigour, 1000);
        var levels = TwentyFirstTowerTable.Load(TestPaths.SettingDir).Levels;
        int before = h.Player.Vigour;
        await Enter(h, 12);
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(before, h.Player.Vigour);
        for (int id = 1; id <= 17; id++)
        {
            await Enter(h, id);
            Assert.Null(Reply(h)["nError"]);
            Assert.Equal(id, Reply(h)["tbData"]![3]!.GetValue<int>());
            await Pass(h, id, levels[id].Type == 2 ? "true" : "{}");
            Assert.Null(Reply(h)["nError"]);
        }
        Assert.Equal(before - levels.Values.Where(l => l.Id <= 17).Sum(l => l.Cost), h.Player.Vigour);
        Assert.Single(h.Player.Items, i => i.Genre == 1 && i.Detail == 16 && i.Particular == 34 && i.Level == 3);
        Assert.Equal(1, h.Player.GetTask(53, 1266));
        Assert.True(await h.WaitForPfAsync(SssjServer.Protocol.Pf.RikiData));
        var saved = h.Module.Store.Load(h.Player.Account)!;
        Assert.Equal(1, saved.GetTask(96, 1813)); // 1801 + level12
        h.Session.Tag = saved;
        await Enter(h, 12);
        await Pass(h, 12);
        Assert.Single(saved.Items, i => i.Genre == 1 && i.Detail == 16 && i.Particular == 34 && i.Level == 3);
    }

    [Fact]
    public async Task EnterRetry_DoesNotDoubleChargeAndPassRetryDoesNotDoubleReward()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-retry");
        h.Player.SetTask(96, 1802, 1);
        h.Player.SetMoney(MoneyType.Vigour, 100);
        await Enter(h, 2);
        await Enter(h, 2);
        Assert.Equal(90, h.Player.Vigour);
        await Pass(h, 2);
        var snapshot = h.Player.Money.OrderBy(p => p.Key).ToArray();
        var items = h.Player.Items.Count;
        int exp = h.Player.Exp;
        await Pass(h, 2);
        Assert.Equal(snapshot, h.Player.Money.OrderBy(p => p.Key));
        Assert.Equal(items, h.Player.Items.Count);
        Assert.Equal(exp, h.Player.Exp);
        Assert.Null(Reply(h)["nError"]);
    }

    [Fact]
    public async Task FailThenReenter_CannotGrantUntilAStartedAttemptSucceeds()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-fail");
        h.Player.SetMoney(MoneyType.Vigour, 100);
        h.Player.SetTask(96, 1812, 1); // prerequisite level11
        await Pass(h, 12);
        Assert.NotNull(Reply(h)["nError"]);
        await Enter(h, 12);
        await ChallengeTestHarness.SettleAsync(h,56, Args(12));
        await Pass(h, 12);
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(0, h.Player.GetTask(96, 1813));
        await Enter(h, 12);
        await Pass(h, 12);
        Assert.Equal(64, h.Player.Vigour);
        Assert.Equal(1, h.Player.GetTask(96, 1813));
    }

    [Fact]
    public async Task EmptyStamina_RejectsEntryWithoutCreatingAnAttempt()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-energy");
        h.Player.SetTask(96, 1802, 1);
        h.Player.SetMoney(MoneyType.Vigour, 0);
        await Enter(h, 2);
        Assert.Equal(5, Reply(h)["nError"]!.GetValue<int>());
        await Pass(h, 2);
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(0, h.Player.GetTask(96, 1803));
    }

    [Fact]
    public async Task Skills_UseConfiguredCurrencyAndPersistLevelAndCap()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-skill");
        var skill = TwentyFirstTowerTable.Load(TestPaths.SettingDir).Skills[1];
        var point = h.Player.AddItem((uint)skill.Item[0], (uint)skill.Item[1], (uint)skill.Item[2], (uint)skill.Item[3], (uint)skill.Costs.Sum());
        for (int level = 1; level <= skill.Costs.Length; level++)
        {
            await h.DispatchLuaCallAsync(527, "[21,1]");
            Assert.Equal(1, Reply(h)["isOk"]!.GetValue<int>());
            Assert.Equal(level, h.Player.GetTask(96, 1712));
        }
        Assert.Equal(0u, point.Count);
        Assert.DoesNotContain(point, h.Player.Items);
        await h.DispatchLuaCallAsync(527, "[21,1]");
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(skill.Costs.Length, h.Module.Store.Load(h.Player.Account)!.GetTask(96, 1712));
    }

    [Fact]
    public async Task SpGoals_AccumulateUniqueGoalsAndOnlyGrantNewThresholds()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-goals");
        h.Player.SetTask(96, 1805, 1); // level4 prerequisite of SP1
        int diamond = h.Player.GetMoney(MoneyType.FreeDiamond);
        await Enter(h, 18);
        await Pass(h, 18, "{\"1\":true,\"2\":false,\"99\":true}");
        Assert.Equal(10, h.Player.GetTask(96, 60018));
        Assert.Equal(1, h.Player.GetTask(96, 61801));
        Assert.Equal(diamond + 5, h.Player.GetMoney(MoneyType.FreeDiamond));
        await Enter(h, 18);
        await Pass(h, 18, "[true,true]");
        Assert.Equal(20, h.Player.GetTask(96, 60018));
        Assert.Equal(diamond + 10, h.Player.GetMoney(MoneyType.FreeDiamond));
        await Enter(h, 18);
        await Pass(h, 18, "[true,true]");
        Assert.Equal(diamond + 10, h.Player.GetMoney(MoneyType.FreeDiamond));
        Assert.Equal(20, h.Module.Store.Load(h.Player.Account)!.GetTask(96, 60018));
    }

    [Theory]
    [InlineData("[4,21,0,999,1,{}]")]
    [InlineData("[4,21,9,1,1,true]")]
    [InlineData("[4,21,0,-1,1,{}]")]
    public async Task InvalidLevel_DoesNotConsumeOrAward(string args)
    {
        using var h = await FeatureHarness.CreateAsync("tower21-invalid");
        int vigour = h.Player.Vigour;
        await h.DispatchLuaCallAsync(54, args);
        Assert.NotNull(Reply(h)["nError"]);
        await ChallengeTestHarness.SettleAsync(h,55, args);
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(vigour, h.Player.Vigour);
    }

    [Fact]
    public async Task StoryRequiresStoryCallbackAndCannotSettleAnotherLevel()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-story");
        await Enter(h, 1);
        await Pass(h, 1);
        Assert.NotNull(Reply(h)["nError"]);
        await Pass(h, 2);
        Assert.NotNull(Reply(h)["nError"]);
        await Pass(h, 1, "true");
        Assert.Equal(1, h.Player.GetTask(96, 1802));
    }

    [Fact]
    public async Task Replay_PreservesHistoricalPositiveScoresWithoutBackfillingRewards()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-old-score");
        h.Player.SetTask(96, 1805, 1);
        h.Player.SetTask(96, 1819, 19);
        h.Player.SetTask(96, 60018, 60);
        int diamonds = h.Player.GetMoney(MoneyType.FreeDiamond);
        await Enter(h, 18);
        await Pass(h, 18, "[true]");
        Assert.Equal(19, h.Player.GetTask(96, 1819));
        Assert.Equal(60, h.Player.GetTask(96, 60018));
        Assert.Equal(diamonds, h.Player.GetMoney(MoneyType.FreeDiamond));
    }

    [Fact]
    public async Task ReconnectedSession_RequiresTokenAndCanResumePersistedAttempt()
    {
        using var first = await FeatureHarness.CreateAsync("tower21-disconnect");
        await Enter(first, 1);
        var saved = first.Module.Store.Load(first.Player.Account)!;
        using var next = await FeatureHarness.CreateAsync("tower21-reconnect");
        next.Session.Tag = saved;
        await next.DispatchLuaCallAsync(55, Args(1,"true"));
        Assert.NotNull(Reply(next)["nError"]);
        Assert.Equal(0, saved.GetTask(96, 1802));
        await Enter(next, 1);
        await Pass(next, 1, "true");
        Assert.Equal(1, saved.GetTask(96, 1802));
    }

    [Fact]
    public async Task UnaffordableOrUnknownSkill_DoesNotChangeItemsOrProgress()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-poor-skill");
        var point = h.Player.AddItem(15, 126, 1, 1, 99);
        foreach (string args in new[] { "[21,1]", "[21,99]", "[20,1]", "null", "{}" })
        {
            await h.DispatchLuaCallAsync(527, args);
            Assert.NotNull(Reply(h)["nError"]);
            Assert.Equal(99u, point.Count);
            Assert.Equal(0, h.Player.GetTask(96, 1712));
        }
    }

    [Fact]
    public async Task GetTower_UsesOriginalNodeTypeAndDoesNotHijackChallenge()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-get");
        await h.DispatchLuaCallAsync(53, "[21]");
        Assert.Equal(4, Reply(h)["ActivityType"]!.GetValue<int>());
        Assert.Equal(21, Reply(h)["TowerId"]!.GetValue<int>());
        await h.DispatchLuaCallAsync(53, "[1,6]");
        Assert.Equal(6, Reply(h)["ActivityType"]!.GetValue<int>());
    }
}
