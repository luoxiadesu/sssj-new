using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class GirlHeadTouchTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    public async Task HeadTouchSynchronizesStoryUnlockAndPreservesReadState(int oldState, int expected)
    {
        using var h = await FeatureHarness.CreateAsync("head-story-unlock");
        var girl = h.Player.GetGirl(1)!;
        girl.Level = 10;
        girl.Exp = 0;
        int task = GirlLoveFeature.VarTaskId(1, GirlLoveFeature.ELevelOffset(1));
        h.Player.SetTask(3, task, oldState);
        h.Player.SetTask(2, 101, 5);
        await h.DispatchFunctionAsync("GirlLogic", "{\"sCmd\":\"HeadTouched\",\"nId\":1,\"nType\":1}");
        Assert.Equal(expected, h.Player.GetTask(3, task));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(expected, saved.GetTask(3, task));
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));
    }

    [Fact]
    public void LegacySave_MissingCountersReceivesInitialCapacityOnlyOnce()
    {
        var player = new Player("head-legacy", 1);
        Assert.True(GirlTrainFeature.RefreshHeadTouches(player, 10000));
        Assert.Equal(5, player.GetTask(2, 101));
        Assert.Equal(0, player.GetTask(2, 102));
        Assert.False(GirlTrainFeature.RefreshHeadTouches(player, 10001));
        player.SetTask(2, 101, 0);
        GirlTrainFeature.RefreshHeadTouches(player, 10002);
        Assert.Equal(0, player.GetTask(2, 101));
        Assert.Equal(10002, player.GetTask(2, 102));
    }

    [Theory]
    [InlineData(2, 1000, 4599, 2, 1000)]
    [InlineData(2, 1000, 4600, 3, 4600)]
    [InlineData(2, 1000, 8201, 4, 8200)]
    [InlineData(2, 1000, 50000, 5, 0)]
    public void RechargePreservesRemainderAndStopsAtCap(int count, int time, int now, int expectedCount, int expectedTime)
    {
        var player = new Player("head-recharge", 1);
        player.SetTask(2, 101, count);
        player.SetTask(2, 102, time);
        GirlTrainFeature.RefreshHeadTouches(player, now);
        Assert.Equal(expectedCount, player.GetTask(2, 101));
        Assert.Equal(expectedTime, player.GetTask(2, 102));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CompletionEchoesSourceConsumesOneAndPersists(int source)
    {
        using var h = await FeatureHarness.CreateAsync("head-source-" + source);
        h.Player.SetTask(2, 101, 5);
        h.Player.SetTask(2, 102, 0);
        var girl = h.Player.GetGirl(1)!;
        uint beforeExp = girl.Exp;
        await h.DispatchFunctionAsync("GirlLogic", $"{{\"sCmd\":\"HeadTouched\",\"nId\":1,\"nType\":{source}}}");
        var reply = JsonNode.Parse(h.LastOutbound.Json)!;
        Assert.Equal(source, reply["nType"]!.GetValue<int>());
        Assert.Null(reply["nError"]);
        Assert.Equal(4, h.Player.GetTask(2, 101));
        Assert.Equal(1, MissionFeature.ProgressOf(h.Player.GetTask(6, 61)));
        Assert.True(h.Player.GetTask(2, 102) > 0);
        Assert.Equal(beforeExp + 50u, girl.Exp);
        Assert.Equal(50, reply["nAddExp"]!.GetValue<int>());
        Assert.Equal(beforeExp + 50u, reply["nNewExp"]!.GetValue<uint>());
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(4, saved.GetTask(2, 101));
        Assert.Equal(girl.Exp, saved.GetGirl(1)!.Exp);
        Assert.Equal(1, MissionFeature.ProgressOf(saved.GetTask(6, 61)));
        Assert.Equal(h.Player.GetTask(2, 102), saved.GetTask(2, 102));
    }

    [Theory]
    [InlineData(1, 90, 2, 40, 50)]
    [InlineData(100, 0, 100, 0, 0)]
    public async Task AffectionRewardUsesLevelCurveAndCap(uint level, uint exp, uint expectedLevel, uint expectedExp, int awarded)
    {
        using var h = await FeatureHarness.CreateAsync("head-love-cap");
        var girl = h.Player.GetGirl(1)!;
        girl.Level = level; girl.Exp = exp;
        h.Player.SetTask(2, 101, 5);
        await h.DispatchFunctionAsync("GirlLogic", "{\"sCmd\":\"HeadTouched\",\"nId\":1,\"nType\":1}");
        var reply = JsonNode.Parse(h.LastOutbound.Json)!;
        Assert.Null(reply["nError"]);
        Assert.Equal(awarded, reply["nAddExp"]!.GetValue<int>());
        Assert.Equal(expectedLevel, girl.Level);
        Assert.Equal(expectedExp, girl.Exp);
        Assert.True(await h.WaitForPfAsync(GamePf.GirlDataNtf));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(expectedLevel, saved.GetGirl(1)!.Level);
        Assert.Equal(expectedExp, saved.GetGirl(1)!.Exp);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(21, 23)]
    [InlineData(2147483647, 2147483647)]
    public void HeadTouchAchievementPreservesClaimBitAndAvoidsOverflow(int initial, int expected)
    {
        var player = new Player("head-achievement", 1);
        player.SetTask(6, 61, initial);
        player.SetTask(6, 1, 42); // unrelated achievement
        var missions = SssjServer.Game.Config.MissionTable.Load(TestPaths.SettingDir);
        GirlTrainFeature.RecordHeadTouchAchievements(missions, player);
        Assert.Equal(expected, player.GetTask(6, 61));
        Assert.Equal(2, player.GetTask(6, 62));
        Assert.Equal(42, player.GetTask(6, 1));
    }

    [Fact]
    public async Task NoChargesRejectsWithoutInventingAnotherUse()
    {
        using var h = await FeatureHarness.CreateAsync("head-empty");
        int now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        h.Player.SetTask(2, 101, 0);
        h.Player.SetTask(2, 102, now);
        await h.DispatchFunctionAsync("GirlLogic", "{\"sCmd\":\"HeadTouched\",\"nId\":1,\"nType\":1}");
        var reply = JsonNode.Parse(h.LastOutbound.Json)!;
        Assert.Equal(2, reply["nError"]!.GetValue<int>());
        Assert.Equal(0, h.Player.GetTask(2, 101));
        Assert.Equal(now, h.Player.GetTask(2, 102));
        Assert.Equal(0, h.Player.GetTask(6, 61));
    }
}
