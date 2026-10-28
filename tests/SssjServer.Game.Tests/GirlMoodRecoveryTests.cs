using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class GirlMoodRecoveryTests
{
    private const long Now = 1800000000;
    private static void Place(Player player, int area, int comfort = 0)
    {
        var cafe = CafeTable.Load(TestPaths.SettingDir);
        var state = CafeFeature.ReadState(player, cafe) with { Level = cafe.MaxCafeLevel, Comfort = comfort };
        var areas = Enumerable.Range(1, 3).Select(i => (IReadOnlyList<CafeFeature.CafeAreaGirl>)
            (i == area ? new[] { new CafeFeature.CafeAreaGirl(1, 0) } : Array.Empty<CafeFeature.CafeAreaGirl>())).ToList();
        CafeFeature.WriteState(player, state, new CafeFeature.CafeContent { AreaGirls = areas });
    }

    [Fact]
    public async Task SyncCommandPushesActualMoodAndTrainingTransitionsResetClock()
    {
        using var h = await FeatureHarness.CreateAsync("mood-sync-transition");
        Place(h.Player, 2);
        h.Player.GetGirl(1)!.Moodvalue = 50;
        var mood = new GirlMoodRecovery(TestPaths.SettingDir);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        mood.Refresh(null, h.Player, now - 901);
        int before = h.Outbound.Count;
        await h.DispatchLuaCallAsync(240, "{\"girlid\":[1,99999]}");
        Assert.Equal(51u, h.Player.GetGirl(1)!.Moodvalue);
        Assert.Equal(before, h.Outbound.Count); // 240 has no Lua reply; data arrives as normal PF pushes.
        Assert.True(await h.WaitForPfAsync(GamePf.GirlDataNtf));
        Assert.Equal(51u, new PlayerStore(h.Options).Load(h.Player.Account)!.GetGirl(1)!.Moodvalue);
        await h.DispatchFunctionAsync("GirlLogic", "{\"sCmd\":\"StartTrain\",\"nId\":1,\"nPos\":11}");
        Assert.Equal(11, GirlTrainFeature.GetVar(h.Player, 1, GirlTrainFeature.VarTrainPos));
        Assert.Equal(0, h.Player.GetTask(41, 1));
        await h.DispatchFunctionAsync("GirlLogic", "{\"sCmd\":\"InterruptTrain\",\"nId\":1}");
        Assert.True(h.Player.GetTask(41, 1) >= now);
        Assert.Equal(51u, h.Player.GetGirl(1)!.Moodvalue);
    }

    [Fact]
    public async Task AssigningAreasSettlesPreviousRestAndStartsNewClock()
    {
        using var h = await FeatureHarness.CreateAsync("mood-assignment");
        Place(h.Player, 2);
        h.Player.GetGirl(1)!.Moodvalue = 50;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        new GirlMoodRecovery(TestPaths.SettingDir).Refresh(null, h.Player, now - 901);
        await h.DispatchLuaCallAsync(113, "[[1],[],[]]");
        Assert.Equal(51u, h.Player.GetGirl(1)!.Moodvalue);
        Assert.Equal(0, h.Player.GetTask(41, 1));
        await h.DispatchLuaCallAsync(113, "[[],[1],[]]");
        Assert.True(h.Player.GetTask(41, 1) >= now);
        Assert.Equal(51u, h.Player.GetGirl(1)!.Moodvalue);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RestRecoveryPreservesPartialIntervalAndSurvivesReload(int area)
    {
        using var h = await FeatureHarness.CreateAsync("mood-recover-" + area);
        Place(h.Player, area);
        h.Player.GetGirl(1)!.Moodvalue = 50;
        var mood = new GirlMoodRecovery(TestPaths.SettingDir);
        mood.Refresh(null, h.Player, Now);
        Assert.Equal(Now, h.Player.GetTask(41, 1));
        mood.Refresh(null, h.Player, Now + 899);
        Assert.Equal(50u, h.Player.GetGirl(1)!.Moodvalue);
        mood.Refresh(null, h.Player, Now + 901);
        Assert.Equal(51u, h.Player.GetGirl(1)!.Moodvalue);
        Assert.Equal(Now + 900, h.Player.GetTask(41, 1));
        new PlayerStore(h.Options).Save(h.Player);
        var reloaded = new PlayerStore(h.Options).Load(h.Player.Account)!;
        mood.Refresh(null, reloaded, Now + 1800);
        Assert.Equal(52u, reloaded.GetGirl(1)!.Moodvalue);
        mood.Refresh(null, reloaded, Now + 1800);
        Assert.Equal(52u, reloaded.GetGirl(1)!.Moodvalue);
    }

    [Fact]
    public async Task ComfortBonusCapAndClockRollbackAreHandled()
    {
        using var h = await FeatureHarness.CreateAsync("mood-comfort");
        Place(h.Player, 2, 2400);
        h.Player.GetGirl(1)!.Moodvalue = 98;
        var mood = new GirlMoodRecovery(TestPaths.SettingDir);
        mood.Refresh(null, h.Player, Now);
        mood.Refresh(null, h.Player, Now + 720);
        Assert.Equal(99u, h.Player.GetGirl(1)!.Moodvalue);
        mood.Refresh(null, h.Player, Now + 719);
        Assert.Equal(99u, h.Player.GetGirl(1)!.Moodvalue);
        mood.Refresh(null, h.Player, Now + 1440);
        Assert.Equal(100u, h.Player.GetGirl(1)!.Moodvalue);
        Assert.Equal(0, h.Player.GetTask(41, 1));
    }

    [Fact]
    public async Task TrainingPausesRecoveryAndAreaChangesDoNotCreditNonRestTime()
    {
        using var h = await FeatureHarness.CreateAsync("mood-pause");
        Place(h.Player, 2);
        h.Player.GetGirl(1)!.Moodvalue = 50;
        var mood = new GirlMoodRecovery(TestPaths.SettingDir);
        mood.Refresh(null, h.Player, Now);
        mood.Refresh(null, h.Player, Now + 900);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainPos, 11);
        mood.Refresh(null, h.Player, Now + 900);
        Assert.Equal(0, h.Player.GetTask(41, 1));
        mood.Refresh(null, h.Player, Now + 9000);
        Assert.Equal(51u, h.Player.GetGirl(1)!.Moodvalue);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainPos, 0);
        mood.Refresh(null, h.Player, Now + 9000);
        mood.Refresh(null, h.Player, Now + 9900);
        Assert.Equal(52u, h.Player.GetGirl(1)!.Moodvalue);
        Place(h.Player, 1);
        mood.Refresh(null, h.Player, Now + 9900);
        mood.Refresh(null, h.Player, Now + 20000);
        Assert.Equal(52u, h.Player.GetGirl(1)!.Moodvalue);
        Place(h.Player, 2);
        mood.Refresh(null, h.Player, Now + 20000);
        Assert.Equal(Now + 20000, h.Player.GetTask(41, 1));
    }
}
