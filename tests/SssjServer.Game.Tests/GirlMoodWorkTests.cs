using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class GirlMoodWorkTests
{
    private const long Now = 1800000000;
    private static void SetGuests(Player player, bool present)
    {
        var cafe = CafeTable.Load(TestPaths.SettingDir);
        var state = CafeFeature.ReadState(player, cafe);
        int guest = cafe.Guests.Keys.First();
        CafeFeature.WriteState(player, state, new CafeFeature.CafeContent
        {
            AreaGirls = new IReadOnlyList<CafeFeature.CafeAreaGirl>[]
                { new[] { new CafeFeature.CafeAreaGirl(1, 0) }, Array.Empty<CafeFeature.CafeAreaGirl>(), Array.Empty<CafeFeature.CafeAreaGirl>() },
            Queue = present ? new[] { new CafeFeature.CafeQueueGuest(guest, 1, Now) } : Array.Empty<CafeFeature.CafeQueueGuest>(),
        });
    }

    [Fact]
    public async Task CafeDownloadAssignmentAndLeaveDriveAuthoritativeClocks()
    {
        using var h = await FeatureHarness.CreateAsync("mood-work-commands");
        SetGuests(h.Player, true);
        var cafe = CafeTable.Load(TestPaths.SettingDir);
        CafeFeature.WriteState(h.Player, CafeFeature.ReadState(h.Player, cafe) with { Level = cafe.MaxCafeLevel });
        h.Player.GetGirl(1)!.Moodvalue = 50;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await h.DispatchLuaCallAsync(112, "{}");
        Assert.True(h.Player.GetTask(41, 10001) >= now);
        h.Player.SetTask(41, 10001, (int)now - 301);
        await h.DispatchLuaCallAsync(113, "[[],[1],[]]");
        Assert.Equal(49u, h.Player.GetGirl(1)!.Moodvalue);
        Assert.Equal(0, h.Player.GetTask(41, 10001));
        Assert.True(h.Player.GetTask(41, 1) >= now);
        await h.DispatchLuaCallAsync(113, "[[1],[],[]]");
        Assert.True(h.Player.GetTask(41, 10001) >= now);
        await h.DispatchLuaCallAsync(125, "{\"usetime\":1}");
        Assert.Equal(0, h.Player.GetTask(41, 10001));
        Assert.Equal(49u, new PlayerStore(h.Options).Load(h.Player.Account)!.GetGirl(1)!.Moodvalue);
    }

    [Fact]
    public async Task WorkConsumesOnlyWithGuestsAndActiveCafePreservingRemainder()
    {
        using var h = await FeatureHarness.CreateAsync("mood-work-clock");
        SetGuests(h.Player, true);
        h.Player.GetGirl(1)!.Moodvalue = 50;
        var mood = new GirlMoodRecovery(TestPaths.SettingDir);
        mood.Refresh(h.Session, h.Player, Now);
        Assert.Equal(0, h.Player.GetTask(41, 10001));
        mood.Enter(h.Session, h.Player, Now);
        Assert.Equal(Now, h.Player.GetTask(41, 10001));
        mood.Refresh(h.Session, h.Player, Now + 299);
        Assert.Equal(50u, h.Player.GetGirl(1)!.Moodvalue);
        mood.Refresh(h.Session, h.Player, Now + 301);
        Assert.Equal(49u, h.Player.GetGirl(1)!.Moodvalue);
        Assert.Equal(Now + 300, h.Player.GetTask(41, 10001));
        mood.Enter(h.Session, h.Player, Now + 301); // repeated download does not reset
        mood.Refresh(h.Session, h.Player, Now + 600);
        Assert.Equal(48u, h.Player.GetGirl(1)!.Moodvalue);
        mood.Refresh(h.Session, h.Player, Now + 600);
        Assert.Equal(48u, h.Player.GetGirl(1)!.Moodvalue);
        mood.Leave(h.Session, h.Player, Now + 600);
        Assert.Equal(0, h.Player.GetTask(41, 10001));
        mood.Refresh(h.Session, h.Player, Now + 3600);
        Assert.Equal(48u, h.Player.GetGirl(1)!.Moodvalue);
    }

    [Fact]
    public async Task GuestArrivalStartsNewClockAndLastDepartureStopsIt()
    {
        using var h = await FeatureHarness.CreateAsync("mood-work-guests");
        SetGuests(h.Player, false);
        h.Player.GetGirl(1)!.Moodvalue = 50;
        var mood = new GirlMoodRecovery(TestPaths.SettingDir);
        mood.Enter(h.Session, h.Player, Now);
        Assert.Equal(0, h.Player.GetTask(41, 10001));
        mood.Refresh(h.Session, h.Player, Now + 900);
        SetGuests(h.Player, true);
        mood.Refresh(h.Session, h.Player, Now + 900);
        Assert.Equal(50u, h.Player.GetGirl(1)!.Moodvalue);
        Assert.Equal(Now + 900, h.Player.GetTask(41, 10001));
        mood.Refresh(h.Session, h.Player, Now + 1200);
        SetGuests(h.Player, false);
        mood.Refresh(h.Session, h.Player, Now + 1200);
        Assert.Equal(49u, h.Player.GetGirl(1)!.Moodvalue);
        Assert.Equal(0, h.Player.GetTask(41, 10001));
    }

    [Fact]
    public async Task OfflineReloadAndNewSessionDoNotChargeStaleGuests()
    {
        using var h = await FeatureHarness.CreateAsync("mood-work-offline");
        SetGuests(h.Player, true);
        h.Player.GetGirl(1)!.Moodvalue = 50;
        var mood = new GirlMoodRecovery(TestPaths.SettingDir);
        mood.Enter(h.Session, h.Player, Now);
        new PlayerStore(h.Options).Save(h.Player);
        var loaded = new PlayerStore(h.Options).Load(h.Player.Account)!;
        mood.Refresh(null, loaded, Now + 86400); // login prepare
        Assert.Equal(50u, loaded.GetGirl(1)!.Moodvalue);
        Assert.Equal(0, loaded.GetTask(41, 10001));
        using var other = await FeatureHarness.CreateAsync("mood-other-session");
        mood.Refresh(other.Session, loaded, Now + 86400);
        Assert.Equal(0, loaded.GetTask(41, 10001));
        mood.Enter(other.Session, loaded, Now + 86400);
        Assert.Equal(Now + 86400, loaded.GetTask(41, 10001));
    }

    [Fact]
    public async Task WorkCapsAtZeroAndTrainingPausesConsumption()
    {
        using var h = await FeatureHarness.CreateAsync("mood-work-zero");
        SetGuests(h.Player, true);
        h.Player.GetGirl(1)!.Moodvalue = 1;
        var mood = new GirlMoodRecovery(TestPaths.SettingDir);
        mood.Enter(h.Session, h.Player, Now);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainPos, 11);
        mood.Refresh(h.Session, h.Player, Now);
        mood.Refresh(h.Session, h.Player, Now + 3600);
        Assert.Equal(1u, h.Player.GetGirl(1)!.Moodvalue);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainPos, 0);
        mood.Refresh(h.Session, h.Player, Now + 3600);
        mood.Refresh(h.Session, h.Player, Now + 7200);
        Assert.Equal(0u, h.Player.GetGirl(1)!.Moodvalue);
        Assert.Equal(0, h.Player.GetTask(41, 10001));
    }
}
