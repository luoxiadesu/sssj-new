using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class ChallengeSeasonTests
{
    private static long Time(int day, int hour = 4) => new DateTimeOffset(2026, 10, day, hour, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
    private static ChallengeTable Table() => ChallengeTable.Load(TestPaths.SettingDir);
    private static bool Refresh(ChallengeSeasons seasons, Player player)
        => seasons.Refresh(player, new List<KeyValuePair<uint, int>>(), new List<MailEntry>());

    [Fact]
    public void ReplayPreservesContentButUsesRegionalCalendarAndMondayRestDay()
    {
        var table = Table();
        var sunday = table.CycleAt(Time(26) - 1);
        var monday = table.CycleAt(Time(26));
        var tuesday = table.CycleAt(Time(27));
        Assert.True(sunday.IsOpen(Time(26) - 1));
        Assert.False(monday.IsOpen(Time(26)));
        Assert.False(monday.IsOpen(Time(27) - 1));
        Assert.True(tuesday.IsOpen(Time(27)));
        Assert.Equal(monday.Cycle + 1, tuesday.Cycle);
        Assert.Equal(349, tuesday.Content);
        Assert.True(tuesday.Replay);
        Assert.Equal(Time(27), tuesday.Begin);
        Assert.Equal(Time(27) + 6 * 86400, tuesday.End);
        Assert.Equal(Time(27) + 7 * 86400, tuesday.Clear);
    }

    [Fact]
    public async Task LegacyAdoptionPreservesProgressAndLabelsExistingRun()
    {
        using var h = await FeatureHarness.CreateAsync("season-adopt");
        var seasons = new ChallengeSeasons(h.Module.Store, Table(), TestPaths.SettingDir, () => Time(27));
        h.Player.SetTask(9, 45901, 7000);
        h.Player.SetTask(9, 45906, 6500);
        h.Player.SetTask(9, 45900, 1);
        h.Player.SetTask(9, 45101, 1);
        h.Player.Extra[ChallengeRuns.AttemptKey] = new JsonObject { ["active"] = true, ["battleToken"] = "unchanged" };
        Assert.True(Refresh(seasons, h.Player));
        Assert.Equal(7000, h.Player.GetTask(9, 45901));
        Assert.Equal(6500, h.Player.GetTask(9, 45906));
        Assert.Equal(1, h.Player.GetTask(9, 45900));
        Assert.Equal(1, h.Player.GetTask(9, 45101));
        Assert.Equal(seasons.Current.Cycle, h.Player.Extra[ChallengeRuns.AttemptKey]!["cycle"]!.GetValue<int>());
        Assert.Empty(MailFeature.Load(h.Player));
        Assert.False(Refresh(seasons, h.Player));
    }

    [Fact]
    public async Task MondaySettlementIsDurableAndRepeatedRefreshDoesNotDuplicateMail()
    {
        using var h = await FeatureHarness.CreateAsync("season-close");
        long now = Time(20);
        var seasons = new ChallengeSeasons(h.Module.Store, Table(), TestPaths.SettingDir, () => now);
        Refresh(seasons, h.Player);
        h.Player.SetTask(9, 45901, 12000);
        h.Module.Store.SaveRequired(h.Player);
        int cycle = seasons.Current.Cycle;
        now = Time(26);
        Assert.True(Refresh(seasons, h.Player));
        var mails = MailFeature.Load(h.Player);
        var mail = Assert.Single(mails);
        Assert.Equal(3, mail.Items.Count); // One real participant => 100% tier, not a fabricated 1% rank.
        Assert.Contains(mail.Items, x => x.SequenceEqual(new[] { 15, 2, 1, 1, 50 }));
        Assert.Contains("共1位", mail.Content);
        Assert.True(h.Player.Extra[ChallengeSeasons.Key]!["settled"]!.GetValue<bool>());
        Assert.Equal(12000, h.Player.GetTask(9, 45901));
        Assert.Equal(12000, h.Player.Extra[ChallengeSeasons.HistoryKey]![cycle.ToString()]!["score"]!.GetValue<int>());
        Assert.False(Refresh(seasons, h.Player));
        var reloaded = h.Module.Store.Load(h.Player.Account)!;
        Assert.False(Refresh(seasons, reloaded));
        Assert.Single(MailFeature.Load(reloaded));
    }

    [Fact]
    public async Task NewCycleArchivesThenClearsOnlyChallengeDomainsAndPendingRun()
    {
        using var h = await FeatureHarness.CreateAsync("season-reset");
        long now = Time(20);
        var seasons = new ChallengeSeasons(h.Module.Store, Table(), TestPaths.SettingDir, () => now);
        Refresh(seasons, h.Player);
        foreach (int key in new[] { 45101, 45304, 45901, 45906, 46101, 46304, 47101 }) h.Player.SetTask(9, key, 123);
        h.Player.SetTask(9, 1000, 88);
        h.Player.SetTask(8, 45101, 77);
        h.Player.Extra[ChallengeRuns.AttemptKey] = new JsonObject { ["active"] = false, ["pendingScore"] = new JsonObject { ["score"] = 200 } };
        int oldCycle = seasons.Current.Cycle;
        h.Module.Store.SaveRequired(h.Player);
        now = Time(27);
        var changed = new List<KeyValuePair<uint, int>>();
        var added = new List<MailEntry>();
        Assert.True(seasons.Refresh(h.Player, changed, added));
        foreach (int key in new[] { 45101, 45304, 45901, 45906, 46101, 46304, 47101 }) Assert.Equal(0, h.Player.GetTask(9, key));
        Assert.Equal(3, h.Player.GetTask(9, 45900));
        Assert.Equal(88, h.Player.GetTask(9, 1000));
        Assert.Equal(77, h.Player.GetTask(8, 45101));
        Assert.False(h.Player.Extra.ContainsKey(ChallengeRuns.AttemptKey));
        Assert.Equal(123, h.Player.Extra[ChallengeSeasons.HistoryKey]![oldCycle.ToString()]!["score"]!.GetValue<int>());
        Assert.Equal(seasons.Current.Cycle, h.Player.Extra[ChallengeSeasons.Key]!["cycle"]!.GetValue<int>());
        Assert.Single(added);
        Assert.Contains(changed, p => p.Key == Player.ComposeTaskId(9, 45901) && p.Value == 0);
    }

    [Theory]
    [InlineData(26)]
    [InlineData(27)]
    public async Task FailedSettlementOrRolloverRestoresPlayerDiskAndNotificationLists(int day)
    {
        using var h = await FeatureHarness.CreateAsync("season-write-failure");
        long now = Time(20);
        var seasons = new ChallengeSeasons(h.Module.Store, Table(), TestPaths.SettingDir, () => now);
        Refresh(seasons, h.Player);
        h.Player.SetTask(9, 45901, 12000);
        h.Module.Store.SaveRequired(h.Player);
        string before = JsonSerializer.Serialize(h.Player);
        string diskPath = Path.Combine(h.Module.Store.DirectoryPath, h.Player.Account + ".player.json");
        string disk = File.ReadAllText(diskPath);
        now = Time(day);
        Directory.CreateDirectory(diskPath + ".tmp");
        var changed = new List<KeyValuePair<uint, int>>();
        var added = new List<MailEntry>();
        try { Assert.ThrowsAny<Exception>(() => seasons.Refresh(h.Player, changed, added)); }
        finally { Directory.Delete(diskPath + ".tmp"); }
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
        Assert.Equal(disk, File.ReadAllText(diskPath));
        Assert.Empty(changed);
        Assert.Empty(added);
        Assert.True(seasons.Refresh(h.Player, changed, added));
        Assert.Single(added);
        Assert.Single(MailFeature.Load(h.Module.Store.Load(h.Player.Account)!));
        Assert.False(Refresh(seasons, h.Player));
    }

    [Fact]
    public async Task RankingUsesRealSavedParticipantsAndSurvivesOtherPlayersReset()
    {
        using var h = await FeatureHarness.CreateAsync("season-rank-a");
        long now = Time(20);
        var seasons = new ChallengeSeasons(h.Module.Store, Table(), TestPaths.SettingDir, () => now);
        Refresh(seasons, h.Player);
        h.Player.SetTask(9, 45901, 5000);
        h.Module.Store.SaveRequired(h.Player);
        var b = new Player("season-rank-b", 42) { Name = "B" };
        Refresh(seasons, b);
        b.SetTask(9, 45901, 8000);
        h.Module.Store.SaveRequired(b);
        var zero = new Player("season-rank-zero", 43);
        Refresh(seasons, zero);
        int oldCycle = seasons.Current.Cycle;
        var rows = seasons.Ranking(oldCycle);
        Assert.Equal(2, rows.Count);
        Assert.Equal(b.Account, rows[0].Player.Account);
        Assert.Equal(1, rows[0].Rank);
        Assert.Equal(2, rows[1].Rank);
        now = Time(27);
        Refresh(seasons, b);
        rows = seasons.Ranking(oldCycle);
        Assert.Equal(2, rows.Count);
        Assert.Equal(8000, rows[0].Score);
        Refresh(seasons, h.Player);
        Assert.Equal(50, b.Extra[ChallengeSeasons.HistoryKey]![oldCycle.ToString()]!["rewardTier"]!.GetValue<int>());
        Assert.Equal(100, h.Player.Extra[ChallengeSeasons.HistoryKey]![oldCycle.ToString()]!["rewardTier"]!.GetValue<int>());
        Assert.Empty(seasons.Ranking(seasons.Current.Cycle));
    }

    [Fact]
    public async Task NoScoreGetsNoRankAwardAndClockRollbackCannotResetProgress()
    {
        using var h = await FeatureHarness.CreateAsync("season-empty");
        long now = Time(20);
        var seasons = new ChallengeSeasons(h.Module.Store, Table(), TestPaths.SettingDir, () => now);
        Refresh(seasons, h.Player);
        now = Time(27);
        Refresh(seasons, h.Player);
        Assert.Empty(MailFeature.Load(h.Player));
        h.Player.SetTask(9, 45901, 3333);
        string before = JsonSerializer.Serialize(h.Player);
        now = Time(20);
        Assert.False(Refresh(seasons, h.Player));
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
    }

    [Fact]
    public async Task NativeRankProtocolReturnsEightFieldRowsAndSupportsPaging()
    {
        using var h = await FeatureHarness.CreateAsync("season-rank-wire");
        await h.DispatchLuaCallAsync(53, "[349,6]");
        var tower = JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
        Assert.Equal(349, tower["cycle"]!["content"]!.GetValue<int>());
        Assert.True(tower["cycle"]!["replay"]!.GetValue<bool>());
        Assert.Equal(ChallengeDaily.Day(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), tower["cycle"]!["day"]!.GetValue<long>());
        h.Player.SetTask(9, 45901, 5000);
        h.Module.Store.SaveRequired(h.Player);
        await h.DispatchFunctionAsync("GameRankMsg", "[1,0,1,true,[0,50]]");
        Assert.Equal("ServerRankMsg", h.LastOutbound.Function);
        var data = JsonNode.Parse(h.LastOutbound.Json)!["tbData"]!;
        Assert.Equal(1, data["max"]!.GetValue<int>());
        Assert.Equal(1, data["me"]!.GetValue<int>());
        var row = Assert.Single((JsonArray)data["other"]!);
        Assert.Equal(8, ((JsonArray)row!).Count);
        Assert.Equal(5000, row[5]!.GetValue<int>());
        await h.DispatchFunctionAsync("GameRankMsg", "[1,0,1,true,[1,50]]");
        data = JsonNode.Parse(h.LastOutbound.Json)!["tbData"]!;
        Assert.Empty((JsonArray)data["other"]!);
        Assert.Equal(1, data["max"]!.GetValue<int>());
    }

    [Fact]
    public async Task OldCycleSettlementAndDecisionCannotAffectNewCycle()
    {
        using var h = await ChallengeTestHarness.CreateAsync("season-stale-token");
        await h.DispatchLuaCallAsync(54, "[6,349,907,2153,1]");
        var run = (JsonObject)h.Player.Extra[ChallengeRuns.AttemptKey]!;
        string token = run["battleToken"]!.GetValue<string>();
        var season = (JsonObject)h.Player.Extra[ChallengeSeasons.Key]!;
        int previous = season["cycle"]!.GetValue<int>() - 1;
        season["cycle"] = previous;
        run["cycle"] = previous;
        h.Player.SetTask(9, 45901, 4000);
        h.Module.Store.SaveRequired(h.Player);
        await h.DispatchLuaCallAsync(55, new JsonArray(6, 349, 907, 2153, 1,
            new JsonObject { ["isPass"] = true, ["battleToken"] = token }).ToJsonString());
        Assert.NotNull(JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!["nError"]);
        Assert.Equal(0, h.Player.GetTask(9, 45901));
        Assert.Equal(0, h.Player.GetTask(9, 45101));
        Assert.Equal(3, h.Player.GetTask(9, 45900));
        Assert.False(h.Player.Extra.ContainsKey(ChallengeRuns.AttemptKey));
        Assert.Single(MailFeature.Load(h.Player));
        await h.DispatchLuaCallAsync(207, new JsonObject { ["battleToken"] = token, ["keep"] = true }.ToJsonString());
        Assert.NotNull(JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!["nError"]);
        Assert.Single(MailFeature.Load(h.Player));
        await h.DispatchLuaCallAsync(54, "[6,349,907,2153,1]");
        Assert.Null(JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!["nError"]);
        Assert.NotEqual(token, h.Player.Extra[ChallengeRuns.AttemptKey]!["battleToken"]!.GetValue<string>());
    }
}
