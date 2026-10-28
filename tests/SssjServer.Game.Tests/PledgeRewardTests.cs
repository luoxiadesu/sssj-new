using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public sealed class PledgeRewardTests
{
    private static PledgeTable Table => PledgeTable.Load(TestPaths.SettingDir);
    private static JsonNode Reply(FeatureHarness h) => JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
    private static Task Claim(FeatureHarness h, int girl = 6)
        => h.DispatchLuaCallAsync(239, $"{{\"girlid\":{girl},\"step\":7}}");

    // Only isolated test players receive synthetic progress. No production saves are used.
    private static void Seed(FeatureHarness h, int girlId = 6, int completed = 0)
    {
        var cfg = Table.Step(girlId, 7)!;
        var girl = new PledgeGirlState { GirlId = girlId, Step = 7 };
        foreach (int id in cfg.TaskList)
            girl.Tasks.Add(new PledgeTaskState { Id = id,
                Count = Table.Task(id)!.Count,
                Status = girl.Tasks.Count < completed ? PledgeFeature.TaskStatusDone : PledgeFeature.TaskStatusDoing });
        var state = new PledgeState();
        state.Girls.Add(girlId, girl);
        h.Player.Extra[PledgeFeature.ExtraKey] = state.ToJson();
        if (h.Player.GetGirl((uint)girlId) is null)
            h.Player.Girls.Add(new Girl { Girlid = (uint)girlId });
        h.Module.Store.Save(h.Player);
    }

    private static void Complete(FeatureHarness h, int girlId, int index)
    {
        var state = PledgeFeature.LoadStateStatic(h.Player);
        state.Girls[girlId].Tasks[index].Status = PledgeFeature.TaskStatusDone;
        h.Player.Extra[PledgeFeature.ExtraKey] = state.ToJson();
    }

    [Fact]
    public void InstalledRewardsAreSevenNineMilestoneListsWithValidTemplates()
    {
        var rows = Table.AllGirlIds.SelectMany(g => Enumerable.Range(0, Table.MaxStep(g) + 1)
            .Select(s => Table.Step(g, s))).Where(r => r is { Rewards.Count: > 0 }).ToArray();
        Assert.Equal(new[] { 1, 6, 7, 9, 10, 12, 15 }, rows.Select(r => r!.GirlId).Order().ToArray());
        var templates = ItemBaseTable.Load(TestPaths.SettingDir);
        foreach (var row in rows)
        {
            Assert.Equal(7, row!.Step);
            Assert.Equal(9, row.TaskList.Count);
            Assert.Equal(Enumerable.Range(1, 9), row.Rewards.Select(r => r[5]));
            foreach (var r in row.Rewards)
                Assert.NotNull(templates.Get((uint)r[0], (uint)r[1], (uint)r[2], (uint)r[3]));
        }
    }

    [Theory]
    [InlineData(1)] [InlineData(6)] [InlineData(7)] [InlineData(9)]
    [InlineData(10)] [InlineData(12)] [InlineData(15)]
    public async Task EachGirlCanClaimAllNineMilestonesIncrementallyAndSurviveReload(int girlId)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-rewards-all-" + girlId);
        Seed(h, girlId);
        var cfg = Table.Step(girlId, 7)!;
        for (int n = 1; n <= 9; n++)
        {
            Complete(h, girlId, n - 1);
            await Claim(h, girlId);
            var reply = Reply(h);
            Assert.Equal(0, reply["errid"]!.GetValue<int>());
            Assert.Equal(n, reply["ncompletedtasknum"]!.GetValue<int>());
            Assert.NotEmpty((JsonArray)reply["tbaward"]!);
            Assert.Equal(n, PledgeFeature.LoadStateStatic(h.Module.Store.Load(h.Player.Account)!).Girls[girlId].RewardCount(7));
            if (n == 1) Assert.Equal(20000, reply["tbaward"]![0]![4]!.GetValue<int>());
            if (n == 8)
            {
                var skin = cfg.Rewards[7];
                var suit = GirlDressTable.Load(TestPaths.SettingDir).SuitForItem((uint)skin[0], (uint)skin[1], (uint)skin[2], (uint)skin[3])!;
                Assert.True(h.Player.GetTask(GmUnlock.SuitGroup, GmUnlock.GirlTaskId(suit.GirlId, GmUnlock.FixModelId(suit.ModelId))) > 0);
            }
            // Simulate the in-memory state a reload produces, then retry a lost response.
            var saved = h.Module.Store.Load(h.Player.Account)!;
            h.Player.Extra[PledgeFeature.ExtraKey] = saved.Extra[PledgeFeature.ExtraKey]!.DeepClone();
            string beforeRetry = JsonSerializer.Serialize(h.Player);
            await Claim(h, girlId);
            Assert.Equal(0, Reply(h)["errid"]!.GetValue<int>());
            Assert.Empty((JsonArray)Reply(h)["tbaward"]!);
            Assert.Equal(beforeRetry, JsonSerializer.Serialize(h.Player));
        }
    }

    [Fact]
    public async Task BatchClaimGrantsAllReachedMilestonesWithMoneyAndItemNotifications()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-rewards-batch");
        Seed(h, completed: 9);
        int goldBefore = h.Player.GetMoney(2);
        await Claim(h);
        Assert.Equal(0, Reply(h)["errid"]!.GetValue<int>());
        Assert.Equal(9, Reply(h)["ncompletedtasknum"]!.GetValue<int>());
        Assert.Equal(goldBefore + 20000, h.Player.GetMoney(2));
        Assert.True(await h.WaitForPfAsync(GamePf.ItemUpdateNtf));
        Assert.True(await h.WaitForPfAsync(GamePf.MoneyUpdateNtf));
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));
        await h.DispatchLuaCallAsync(231, "[]");
        Assert.Equal(9, Reply(h)[0]!["rewardlist"]![0]!.GetValue<int>());
    }

    [Fact]
    public async Task OnlyManuallyCompletedReferencedTasksUnlockRewardsRegardlessOfOrder()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-rewards-status");
        Seed(h);
        // Counts are already at target, but none have been manually completed.
        await h.DispatchLuaCallAsync(239, "{\"girlid\":6,\"step\":7,\"ncompletedtasknum\":9,\"tbaward\":[[15,2,1,1,999999]]}");
        Assert.Equal(0, Reply(h)["ncompletedtasknum"]!.GetValue<int>());
        Assert.Empty((JsonArray)Reply(h)["tbaward"]!);
        Complete(h, 6, 8);
        var state = PledgeFeature.LoadStateStatic(h.Player);
        state.Girls[6].Tasks.Add(new PledgeTaskState { Id = 999999, Status = 2 });
        h.Player.Extra[PledgeFeature.ExtraKey] = state.ToJson();
        await Claim(h);
        Assert.Equal(1, Reply(h)["ncompletedtasknum"]!.GetValue<int>());
        Assert.Equal(20000, Reply(h)["tbaward"]![0]![4]!.GetValue<int>());
    }

    [Theory]
    [InlineData("[]")] [InlineData("null")] [InlineData("123")]
    [InlineData("{\"girlid\":6}")]
    [InlineData("{\"girlid\":999,\"step\":7}")]
    [InlineData("{\"girlid\":6,\"step\":6}")]
    [InlineData("{\"girlid\":6,\"step\":8}")]
    [InlineData("{\"girlid\":13,\"step\":7}")]
    [InlineData("{\"girlid\":2,\"step\":7}")]
    [InlineData("{\"girlid\":{},\"step\":[]}")]
    public async Task InvalidOrUnreachedClaimsUseErridAndDoNotMutatePlayer(string payload)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-rewards-invalid");
        Seed(h, completed: 9);
        string before = JsonSerializer.Serialize(h.Player);
        await h.DispatchLuaCallAsync(239, payload);
        Assert.NotEqual(0, Reply(h)["errid"]!.GetValue<int>());
        Assert.Null(Reply(h)["Err"]);
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
    }

    [Fact]
    public async Task AnotherGirlsProgressCannotBeUsedForAnUnactivatedGirl()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-rewards-girl-isolation");
        Seed(h, completed: 9);
        await Claim(h, 1);
        Assert.NotEqual(0, Reply(h)["errid"]!.GetValue<int>());
        Assert.Equal(0, PledgeFeature.LoadStateStatic(h.Player).Girls[6].RewardCount(7));
    }

    [Theory]
    [InlineData(6)] [InlineData(8)]
    public async Task RewardsCannotBeClaimedBeforeOrAfterTheirActiveStage(int activeStep)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-rewards-wrong-stage");
        Seed(h, completed: 9);
        var state = PledgeFeature.LoadStateStatic(h.Player);
        state.Girls[6].Step = activeStep;
        h.Player.Extra[PledgeFeature.ExtraKey] = state.ToJson();
        string before = JsonSerializer.Serialize(h.Player);
        await Claim(h);
        Assert.NotEqual(0, Reply(h)["errid"]!.GetValue<int>());
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
    }

    [Fact]
    public async Task ExistingWatermarkSurvivesTaskRefreshAndInconsistentProgressIsRejected()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-rewards-watermark");
        Seed(h, completed: 3);
        await Claim(h);
        await h.DispatchLuaCallAsync(231, "[]");
        Assert.Equal(3, Reply(h)[0]!["rewardlist"]![0]!.GetValue<int>());
        // Preserve paid rewards even if inconsistent legacy progress falls backwards.
        var state = PledgeFeature.LoadStateStatic(h.Player);
        state.Girls[6].Tasks[0].Status = PledgeFeature.TaskStatusDoing;
        h.Player.Extra[PledgeFeature.ExtraKey] = state.ToJson();
        string before = JsonSerializer.Serialize(h.Player);
        await Claim(h);
        Assert.NotEqual(0, Reply(h)["errid"]!.GetValue<int>());
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
    }

    [Theory]
    [InlineData(0)] [InlineData(10)]
    public async Task BadInstalledThresholdRejectsWholeClaimWithoutAdvancingWatermark(int threshold)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-rewards-bad-threshold");
        Seed(h, completed: 9);
        var handler = h.Module.Features.OfType<PledgeFeature>().Single();
        var table = (PledgeTable)typeof(PledgeFeature).GetField("_table",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(handler)!;
        table.Step(6, 7)!.Rewards[0][5] = threshold;
        string before = JsonSerializer.Serialize(h.Player);
        await Claim(h);
        Assert.NotEqual(0, Reply(h)["errid"]!.GetValue<int>());
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
    }

    [Theory]
    [InlineData(1)] [InlineData(8)] [InlineData(9)]
    public async Task FailedSaveRollsBackEntireRewardAndRetryGrantsExactlyOnce(int completed)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-rewards-write-failure-" + completed);
        Seed(h, completed: completed);
        string before = JsonSerializer.Serialize(h.Player);
        string file = Path.Combine(h.Module.Store.DirectoryPath, h.Player.Account + ".player.json");
        string savedBefore = File.ReadAllText(file);
        string blocker = file + ".tmp";
        Directory.CreateDirectory(blocker);
        try { await Claim(h); }
        finally { Directory.Delete(blocker); }
        Assert.NotEqual(0, Reply(h)["errid"]!.GetValue<int>());
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
        Assert.Equal(savedBefore, File.ReadAllText(file));
        Assert.Empty(h.PushedPf);
        await Claim(h);
        Assert.Equal(0, Reply(h)["errid"]!.GetValue<int>());
        Assert.Equal(completed, PledgeFeature.LoadStateStatic(h.Player).Girls[6].RewardCount(7));
        string after = JsonSerializer.Serialize(h.Player);
        await Claim(h);
        Assert.Equal(after, JsonSerializer.Serialize(h.Player));
    }

    [Fact]
    public async Task DuplicateConcurrentClaimsGrantOnlyOnce()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-rewards-concurrent");
        Seed(h, completed: 9);
        var handler = h.Module.Features.OfType<PledgeFeature>().Single();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => handler.OnGetReward(h.Session, 239,
            JsonNode.Parse("{\"girlid\":6,\"step\":7}")))));
        var replies = h.Outbound.Select(o => JsonNode.Parse(o.Json)!["tbParam"]!).ToArray();
        Assert.Equal(8, replies.Length);
        Assert.All(replies, r => Assert.Equal(0, r["errid"]!.GetValue<int>()));
        Assert.Single(replies, r => ((JsonArray)r["tbaward"]!).Count > 0);
    }

    [Fact]
    public async Task LegacyGirlDataExposesZeroClaimWatermarkWithoutGrantingOrSaving()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-rewards-legacy");
        Seed(h, completed: 3);
        var legacyGirl = (JsonObject)h.Player.Extra[PledgeFeature.ExtraKey]!["girls"]![0]!;
        legacyGirl.Remove("rewardlist");
        h.Module.Store.Save(h.Player);
        await h.DispatchLuaCallAsync(231, "[]");
        Assert.Equal(0, Reply(h)[0]!["rewardlist"]![0]!.GetValue<int>());
        Assert.Null(h.Player.Extra[PledgeFeature.ExtraKey]!["girls"]![0]!["rewardlist"]);
        await Claim(h);
        Assert.Equal(3, Reply(h)["ncompletedtasknum"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("15-1-1-1-20000-1", 6)]
    [InlineData("15-1-bad-1-20000-1", 0)]
    [InlineData("15-1-1-1-20000-1,garbage", 6)]
    public void RewardParserKeepsMalformedEntriesSoTheyCannotBeSilentlyClaimed(string raw, int firstLength)
    {
        var parsed = PledgeTable.ParseRewards(raw);
        Assert.Equal(firstLength, parsed[0].Length);
        if (raw.Contains(',')) Assert.Empty(parsed[1]);
    }
}
