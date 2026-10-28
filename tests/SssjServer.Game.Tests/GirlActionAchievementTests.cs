using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;
public class GirlActionAchievementTests
{
    [Theory]
    [InlineData("EndTrain", "1.5")]
    [InlineData("InterruptTrain", "\"1\"")]
    [InlineData("HeadTouched", "1.5")]
    public async Task MalformedGirlCannotSettleInterruptOrCount(string cmd, string id)
    {
        using var h = await FeatureHarness.CreateAsync("girl-action-invalid-id");
        h.Player.SetTask(3, GirlTrainFeature.VarTrainPos, 11);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainTime, 1);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainOutId, 72);
        var exp = h.Player.GetGirl(1)!.Exp;
        await h.LuaCalls.DispatchAsync(h.Session, "GirlLogic", $"{{\"sCmd\":\"{cmd}\",\"nId\":{id}}}");
        Assert.Equal(11, GirlTrainFeature.GetVar(h.Player, 1, GirlTrainFeature.VarTrainPos));
        Assert.Equal(exp, h.Player.GetGirl(1)!.Exp);
        Assert.Equal(0, h.Player.GetTask(18, 58));
        Assert.Equal(0, h.Player.GetTask(18, 1577));
    }

    [Fact]
    public async Task ConcurrentTrainingClaimsCountOnceAndPrematureOrInterruptedTrainingNeverCounts()
    {
        using var h = await FeatureHarness.CreateAsync("girl-action-concurrency");
        h.Player.SetTask(3, GirlTrainFeature.VarTrainPos, 11);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainTime, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainOutId, 72);
        await Send("EndTrain");
        Assert.Equal(0, h.Player.GetTask(18, 58));
        await Send("InterruptTrain");
        Assert.Equal(0, h.Player.GetTask(18, 58));
        h.Player.SetTask(3, GirlTrainFeature.VarTrainPos, 11);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainTime, 1);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainOutId, 72);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Send("EndTrain"))));
        Assert.Equal(2, h.Player.GetTask(18, 58));
        Assert.Equal(2, h.Module.Store.Load(h.Player.Account)!.GetTask(18, 58));
        Task Send(string cmd) => h.LuaCalls.DispatchAsync(h.Session, "GirlLogic", $"{{\"sCmd\":\"{cmd}\",\"nId\":1}}");
    }

    [Fact]
    public async Task ConcurrentHeadTouchesCannotSpendOrCountBeyondAvailableTouch()
    {
        using var h = await FeatureHarness.CreateAsync("girl-touch-concurrency");
        h.Player.SetTask(2, 101, 1);
        h.Player.SetTask(2, 102, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            h.LuaCalls.DispatchAsync(h.Session, "GirlLogic", "{\"sCmd\":\"HeadTouched\",\"nId\":1,\"nType\":0}"))));
        Assert.Equal(0, h.Player.GetTask(2, 101));
        Assert.Equal(2, h.Player.GetTask(18, 1577));
        Assert.Equal(2, h.Module.Store.Load(h.Player.Account)!.GetTask(18, 1577));
    }

    [Theory]
    [InlineData(174, 2295, 2, 1)]
    [InlineData(175, 2299, 9, 0)]
    public void GatedActionRequiresCompletedPredecessorAndCorrectGirl(int type, int id, int girl, int training)
    {
        var missions = MissionTable.Load(TestPaths.SettingDir);
        var player = new Player("girl-action-gate", 1);
        var entry = missions.GetAchieveOther(id)!;
        Assert.Equal(type, entry.AchieveType);
        var pre = missions.GetAchieveOther(entry.PreId)!;
        int? trainingId = training == 0 ? null : training;
        player.SetTask(18, pre.Id, 1); // Claim bit alone is not progress.
        GirlActionAchievements.Record(missions, player, (uint)girl, trainingId);
        Assert.Equal(0, player.GetTask(18, id));
        player.SetTask(18, pre.Id, MissionFeature.ComposeTaskValue(pre.RequiredCount(true), 0));
        GirlActionAchievements.Record(missions, player, (uint)(girl + 100), trainingId);
        Assert.Equal(0, player.GetTask(18, id));
        GirlActionAchievements.Record(missions, player, (uint)girl, trainingId);
        Assert.Equal(2, player.GetTask(18, id));
        player.SetTask(18, id, 3);
        GirlActionAchievements.Record(missions, player, (uint)girl, trainingId);
        Assert.Equal(5, player.GetTask(18, id));
    }

    [Fact]
    public void TrainingIdAndGirlMustMatchEightHourAchievement()
    {
        var missions = MissionTable.Load(TestPaths.SettingDir);
        var player = new Player("girl-training-kind", 1);
        GirlActionAchievements.Record(missions, player, 7, 1);
        Assert.Equal(0, player.GetTask(18, 1092));
        GirlActionAchievements.Record(missions, player, 6, 4);
        Assert.Equal(0, player.GetTask(18, 1092));
        GirlActionAchievements.Record(missions, player, 7, 4);
        Assert.Equal(2, player.GetTask(18, 1092));
    }

    [Fact]
    public async Task TrainingSettlementCountsOnceAndHeadTouchUsesActualGirl()
    {
        using var h = await FeatureHarness.CreateAsync("girl-action-endpoints");
        if (h.Player.GetGirl(6) is null) h.Player.Girls.Add(new Girl { Girlid = 6 });
        int group = GirlLoveFeature.VarGroup(6);
        h.Player.SetTask(group, GirlLoveFeature.VarTaskId(6, GirlTrainFeature.VarTrainPos), 11);
        h.Player.SetTask(group, GirlLoveFeature.VarTaskId(6, GirlTrainFeature.VarTrainTime), 1);
        h.Player.SetTask(group, GirlLoveFeature.VarTaskId(6, GirlTrainFeature.VarTrainOutId), 72);
        await Send("EndTrain", 6);
        Assert.Equal(2, h.Player.GetTask(18, 92));
        Assert.Equal(2, h.Module.Store.Load(h.Player.Account)!.GetTask(18, 92));
        await Send("EndTrain", 6);
        Assert.Equal(2, h.Player.GetTask(18, 92));
        await Send("HeadTouched", 6);
        Assert.Equal(2, h.Player.GetTask(18, 91));
        await Send("HeadTouched", 1);
        Assert.Equal(2, h.Player.GetTask(18, 91));
        Task Send(string cmd, int girl) => h.LuaCalls.DispatchAsync(h.Session, "GirlLogic",
            $"{{\"sCmd\":\"{cmd}\",\"nId\":{girl},\"nType\":0}}");
    }
}
