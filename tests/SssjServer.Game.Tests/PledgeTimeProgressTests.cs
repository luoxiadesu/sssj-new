using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class PledgeTimeProgressTests
{
    private const long Start = 1800000000;
    private static PledgeTimeProgress Clock() => new(PledgeTable.Load(TestPaths.SettingDir), CafeTable.Load(TestPaths.SettingDir));
    private static (int Girl, int Task, int Step) Ids(string type) => type switch
    {
        "GreeterTime" => (2, 230, 3), "CafeTime" => (8, 835, 6), "RestTime" => (2, 235, 6), _ => throw new ArgumentException(type),
    };
    private static void Seed(Player player, string type, int count = 0)
    {
        var id = Ids(type);
        if (player.GetGirl((uint)id.Girl) is null) player.Girls.Add(new Girl { Girlid = (uint)id.Girl });
        var state = new PledgeState();
        var girl = new PledgeGirlState { GirlId = id.Girl, Step = id.Step };
        girl.Tasks.Add(new PledgeTaskState { Id = id.Task, Count = count });
        state.Girls[id.Girl] = girl;
        player.Extra[PledgeFeature.ExtraKey] = state.ToJson();
        Assign(player, type, true);
    }
    private static void Assign(Player player, string type, bool assigned, int restArea = 1)
    {
        var id = Ids(type);
        if (type == "GreeterTime") player.SetTask(2, 2, assigned ? id.Girl : 1);
        else
        {
            var areas = new JsonArray(new JsonArray(), new JsonArray(), new JsonArray());
            if (assigned) ((JsonArray)areas[type == "CafeTime" ? 0 : restArea]!).Add(new JsonArray(id.Girl, 0));
            player.Extra[CafeFeature.ExtraKey] = new JsonObject { ["level"] = 8, ["roomgirlslist"] = areas };
        }
    }
    private static PledgeTaskState Task(Player player, string type)
    {
        var id = Ids(type);
        return PledgeFeature.LoadStateStatic(player).Girls[id.Girl].Tasks.Single(t => t.Id == id.Task);
    }
    private static void SaveTick(FeatureHarness h, PledgeTimeProgress clock, long now)
        => clock.SaveRefresh(h.Module.Store, h.Player, now);

    [Theory]
    [InlineData("GreeterTime")]
    [InlineData("CafeTime")]
    [InlineData("RestTime")]
    public async Task CountsMinutesRetainsRemainderAndSurvivesRestartWithoutHistoryBackfill(string type)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-clock-minutes");
        Seed(h.Player, type, 5);
        var clock = Clock();
        SaveTick(h, clock, Start);
        Assert.Equal(5, Task(h.Player, type).Count);
        SaveTick(h, clock, Start + 59);
        Assert.Equal(5, Task(h.Player, type).Count);
        SaveTick(h, clock, Start + 60);
        Assert.Equal(6, Task(h.Player, type).Count);
        Assert.False(clock.SaveRefresh(h.Module.Store, h.Player, Start + 60));
        var loaded = h.Module.Store.Load(h.Player.Account)!;
        clock.SaveRefresh(h.Module.Store, loaded, Start + 3660);
        Assert.Equal(66, Task(loaded, type).Count);
    }

    [Theory]
    [InlineData("GreeterTime")]
    [InlineData("CafeTime")]
    [InlineData("RestTime")]
    public async Task AssignmentIntervalsExcludePausedTimeAndKeepFractionalMinutes(string type)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-clock-interval");
        Seed(h.Player, type);
        var clock = Clock();
        SaveTick(h, clock, Start);
        clock.Refresh(h.Player, Start + 30);
        Assign(h.Player, type, false);
        clock.Refresh(h.Player, Start + 30);
        clock.Refresh(h.Player, Start + 90);
        Assert.Equal(0, Task(h.Player, type).Count);
        Assign(h.Player, type, true);
        clock.Refresh(h.Player, Start + 90);
        SaveTick(h, clock, Start + 120);
        Assert.Equal(1, Task(h.Player, type).Count);
    }

    [Theory]
    [InlineData("GreeterTime")]
    [InlineData("CafeTime")]
    [InlineData("RestTime")]
    public async Task NoAssignmentDoesNotAccrueAndLaterAssignmentStartsAtObservedTime(string type)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-clock-paused");
        Seed(h.Player, type);
        Assign(h.Player, type, false);
        var clock = Clock();
        SaveTick(h, clock, Start);
        SaveTick(h, clock, Start + 86400);
        Assert.Equal(0, Task(h.Player, type).Count);
        Assign(h.Player, type, true);
        SaveTick(h, clock, Start + 86400);
        Assert.Equal(0, Task(h.Player, type).Count);
        SaveTick(h, clock, Start + 86460);
        Assert.Equal(1, Task(h.Player, type).Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RestAndDormAreBothRestAssignmentsWhileTrainingPausesThem(int area)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-clock-rest");
        Seed(h.Player, "RestTime");
        Assign(h.Player, "RestTime", true, area);
        var clock = Clock();
        SaveTick(h, clock, Start);
        SaveTick(h, clock, Start + 60);
        Assert.Equal(1, Task(h.Player, "RestTime").Count);
        clock.Refresh(h.Player, Start + 60);
        h.Player.SetTask(GirlLoveFeature.VarGroup(2), GirlLoveFeature.VarTaskId(2, GirlTrainFeature.VarTrainPos), 1);
        clock.Refresh(h.Player, Start + 60);
        SaveTick(h, clock, Start + 3600);
        Assert.Equal(1, Task(h.Player, "RestTime").Count);
    }

    [Fact]
    public async Task CapDoesNotAutoClaimAndNewStageCannotUsePreviousStageClock()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-clock-stage");
        Seed(h.Player, "GreeterTime", 7199);
        var clock = Clock();
        SaveTick(h, clock, Start);
        SaveTick(h, clock, Start + 120);
        Assert.Equal(7200, Task(h.Player, "GreeterTime").Count);
        Assert.Equal(PledgeFeature.TaskStatusDoing, Task(h.Player, "GreeterTime").Status);
        var state = PledgeFeature.LoadStateStatic(h.Player);
        var girl = state.Girls[2];
        girl.Step = 6;
        girl.Tasks.Clear();
        girl.Tasks.Add(new PledgeTaskState { Id = 235 });
        h.Player.Extra[PledgeFeature.ExtraKey] = state.ToJson();
        Assign(h.Player, "RestTime", true);
        SaveTick(h, clock, Start + 3600);
        Assert.Equal(0, PledgeFeature.LoadStateStatic(h.Player).Girls[2].Tasks[0].Count);
        SaveTick(h, clock, Start + 3660);
        Assert.Equal(1, PledgeFeature.LoadStateStatic(h.Player).Girls[2].Tasks[0].Count);
    }

    [Fact]
    public async Task ClockRollbackCannotCreditTheSameMinuteTwice()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-clock-rollback");
        Seed(h.Player, "GreeterTime");
        var clock = Clock();
        SaveTick(h, clock, Start);
        SaveTick(h, clock, Start + 60);
        SaveTick(h, clock, Start - 60);
        SaveTick(h, clock, Start + 90);
        Assert.Equal(1, Task(h.Player, "GreeterTime").Count);
        SaveTick(h, clock, Start + 120);
        Assert.Equal(2, Task(h.Player, "GreeterTime").Count);
    }

    [Fact]
    public async Task FailedClockSaveRollsBackCountsRemainderAndDiskThenRetryCreditsOnce()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-clock-write-failure");
        Seed(h.Player, "GreeterTime");
        var clock = Clock();
        SaveTick(h, clock, Start);
        string before = JsonSerializer.Serialize(h.Player);
        string path = Path.Combine(h.Module.Store.DirectoryPath, h.Player.Account + ".player.json");
        string disk = File.ReadAllText(path);
        Directory.CreateDirectory(path + ".tmp");
        try { Assert.ThrowsAny<Exception>(() => SaveTick(h, clock, Start + 60)); }
        finally { Directory.Delete(path + ".tmp"); }
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
        Assert.Equal(disk, File.ReadAllText(path));
        SaveTick(h, clock, Start + 60);
        Assert.Equal(1, Task(h.Player, "GreeterTime").Count);
        Assert.False(clock.SaveRefresh(h.Module.Store, h.Player, Start + 60));
    }

    [Fact]
    public async Task MainGirlSwitchPersistsOldTimeAndDoesNotCreditTheNewGirlRetroactively()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-clock-switch");
        Seed(h.Player, "GreeterTime");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SaveTick(h, Clock(), now - 125);
        await h.DispatchFunctionAsync("GirlLogic", "{\"sCmd\":\"SetMainGirl\",\"nId\":1}");
        Assert.Equal(1, h.Player.GetTask(2, 2));
        Assert.InRange(Task(h.Player, "GreeterTime").Count, 2, 3);
        int count = Task(h.Player, "GreeterTime").Count;
        SaveTick(h, Clock(), now + 3600);
        Assert.Equal(count, Task(h.Player, "GreeterTime").Count);
    }

    [Fact]
    public async Task CafeAssignmentSwitchSettlesPreviousJobBeforeReplacingIt()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-clock-cafe-switch");
        Seed(h.Player, "CafeTime");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SaveTick(h, Clock(), now - 65);
        await h.DispatchLuaCallAsync(113, "[[[1,0]],[[8,0]],[]]");
        Assert.InRange(Task(h.Player, "CafeTime").Count, 1, 2);
        int count = Task(h.Player, "CafeTime").Count;
        SaveTick(h, Clock(), now + 3600);
        Assert.Equal(count, Task(h.Player, "CafeTime").Count);
    }

    [Fact]
    public async Task FailedAssignmentSaveReturnsOldRosterAndRestoresPlayer()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-clock-cafe-write-failure");
        Seed(h.Player, "CafeTime");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SaveTick(h, Clock(), now);
        string before = JsonSerializer.Serialize(h.Player);
        string blocker = Path.Combine(h.Module.Store.DirectoryPath, h.Player.Account + ".player.json.tmp");
        Directory.CreateDirectory(blocker);
        try { await h.DispatchLuaCallAsync(113, "[[[1,0]],[[8,0]],[]]"); }
        finally { Directory.Delete(blocker); }
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
        var roster = JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
        Assert.Equal(8, roster[0]![0]![0]!.GetValue<int>());
        Assert.Empty((JsonArray)roster[1]!);
    }

    [Fact]
    public async Task ClaimRefreshesElapsedTimeButStillRequiresExplicitTaskCompletion()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-clock-claim");
        Seed(h.Player, "GreeterTime", 7199);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SaveTick(h, Clock(), now - 65);
        await h.DispatchLuaCallAsync(234, "{\"girlid\":2,\"taskid\":230}");
        var reply = JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
        Assert.Null(reply["Err"]);
        Assert.Equal(PledgeFeature.TaskStatusDone, Task(h.Player, "GreeterTime").Status);
        Assert.Equal(7200, Task(h.Module.Store.Load(h.Player.Account)!, "GreeterTime").Count);
    }

    [Theory]
    [InlineData("InterruptTrain")]
    [InlineData("EndTrain")]
    public async Task NativeTrainingCommandsPauseRestTimeAndResumeWithoutChargingTraining(string command)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-clock-native-training-" + command);
        Seed(h.Player, "RestTime");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SaveTick(h, Clock(), now - 125);
        await h.DispatchFunctionAsync("GirlLogic", "{\"sCmd\":\"StartTrain\",\"nId\":2,\"nPos\":11}");
        Assert.Null(JsonNode.Parse(h.LastOutbound.Json)!["nError"]);
        Assert.True(GirlTrainFeature.GetVar(h.Player, 2, GirlTrainFeature.VarTrainPos) > 0);
        int settled = Task(h.Player, "RestTime").Count;
        Assert.InRange(settled, 2, 3);
        var record = h.Player.Extra[PledgeTimeProgress.ExtraKey]!["records"]!["2:6:235"]!;
        Assert.False(record["eligible"]!.GetValue<bool>());
        record["clock"] = now - 3600;
        h.Player.SetTask(GirlLoveFeature.VarGroup(2), GirlLoveFeature.VarTaskId(2, GirlTrainFeature.VarTrainTime), 1);
        await h.DispatchFunctionAsync("GirlLogic", $"{{\"sCmd\":\"{command}\",\"nId\":2}}");
        Assert.Null(JsonNode.Parse(h.LastOutbound.Json)!["nError"]);
        Assert.Equal(0, GirlTrainFeature.GetVar(h.Player, 2, GirlTrainFeature.VarTrainPos));
        Assert.Equal(settled, Task(h.Player, "RestTime").Count);
        var saved = h.Module.Store.Load(h.Player.Account)!;
        Assert.True(saved.Extra[PledgeTimeProgress.ExtraKey]!["records"]!["2:6:235"]!["eligible"]!.GetValue<bool>());
        SaveTick(h, Clock(), DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60);
        Assert.Equal(settled + 1, Task(h.Player, "RestTime").Count);
    }
}
