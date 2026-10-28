using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class ChallengeScoreSaveTests
{
    private const string Entry = "[6,349,187,4021,1]";
    private static Player Current(FeatureHarness h) => (Player)h.Session.Tag!;
    private static JsonNode Reply(FeatureHarness h) => JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
    private static string Token(FeatureHarness h) => Current(h).Extra[ChallengeRuns.AttemptKey]!["battleToken"]!.GetValue<string>();
    private static string Choice(string token, bool keep) => new JsonObject { ["battleToken"] = token, ["keep"] = keep }.ToJsonString();
    private static async Task<string> Finish(FeatureHarness h, int damage = 500000, bool wrapped = false)
    {
        await h.DispatchLuaCallAsync(54, Entry);
        Assert.Null(Reply(h)["nError"]);
        string token = Token(h);
        string result = ChallengeTestHarness.WithToken(h, $"[6,349,187,4021,1,{{\"isPass\":false,\"nDamage\":{damage}}}]");
        if (wrapped)
        {
            await h.DispatchLuaCallAsync(252, new JsonObject { ["sCmd"] = 55, ["tbParam"] = JsonNode.Parse(result) }.ToJsonString());
            Assert.Null(Reply(h)["tbParam"]!["nError"]);
        }
        else
        {
            await h.DispatchLuaCallAsync(55, result);
            Assert.Null(Reply(h)["nError"]);
        }
        return token;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(41, 0)]
    [InlineData(42, 1)]
    [InlineData(250000, 3000)]
    [InlineData(500000, 6000)]
    [InlineData(800000, 7200)]
    [InlineData(900000, 8400)]
    [InlineData(1000000, 9600)]
    [InlineData(1200000, 10800)]
    [InlineData(1500000, 11400)]
    [InlineData(1800000, 12000)]
    [InlineData(999999999, 12000)]
    public async Task DamageCurveUsesInstalledVariantAndDifficulty(int damage, int expected)
    {
        using var h = await ChallengeTestHarness.CreateAsync("score-curve");
        var scores = new ChallengeScores(h.Options.SettingDirectory);
        Assert.Equal(ChallengeScores.Status.Available,
            scores.Evaluate(187, 4021, JsonNode.Parse($"{{\"nDamage\":{damage}}}")!.AsObject(), false, out var result));
        Assert.Equal(expected, result!.Score);
        Assert.Equal(damage, result.Detail);
    }

    [Theory]
    [InlineData(122, 4108, 900000, 9000)]
    [InlineData(122, 4108, 2500000, 17100)]
    [InlineData(122, 4109, 24000000, 21560)] // Installed cap is 30m, unlike the older web calculator's 24m.
    [InlineData(122, 4109, 30000000, 22000)]
    [InlineData(161, 4012, 25000000, 20900)]
    [InlineData(161, 4012, 50000000, 22000)]
    [InlineData(187, 4023, 25000000, 20900)]
    [InlineData(187, 4023, 50000000, 22000)]
    public async Task BossAndGroupVariantsUseLocalThresholds(int node, int level, int damage, int expected)
    {
        using var h = await ChallengeTestHarness.CreateAsync("score-variants");
        var scores = new ChallengeScores(h.Options.SettingDirectory);
        Assert.Equal(ChallengeScores.Status.Available,
            scores.Evaluate(node, level, JsonNode.Parse($"{{\"nDamage\":{damage}}}")!.AsObject(), false, out var result));
        Assert.Equal(expected, result!.Score);
    }

    [Theory]
    [InlineData(300, 12000, 0)]
    [InlineData(299, 11980, 1)]
    [InlineData(123.45, 8469, 177)]
    [InlineData(0, 6000, 300)]
    public async Task OrdinarySpeedWinUsesTimeAndLocalMaximum(double left, int expected, int seconds)
    {
        using var h = await ChallengeTestHarness.CreateAsync("score-speed");
        var scores = new ChallengeScores(h.Options.SettingDirectory);
        var stats = JsonNode.Parse(JsonSerializer.Serialize(new { leftTime = left }))!.AsObject();
        Assert.Equal(ChallengeScores.Status.Available, scores.Evaluate(907, 2153, stats, true, out var result));
        Assert.Equal(expected, result!.Score);
        Assert.Equal(seconds, result.Detail);
        Assert.Equal(ChallengeScores.Status.Unavailable, scores.Evaluate(907, 2153, stats, false, out _));
        Assert.Equal(ChallengeScores.Status.Unavailable, scores.Evaluate(507, 3007, stats, true, out _));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("null")]
    [InlineData("\"500000\"")]
    [InlineData("1e999")]
    public async Task InvalidStatsCannotBecomeScores(string damage)
    {
        using var h = await ChallengeTestHarness.CreateAsync("score-invalid");
        var scores = new ChallengeScores(h.Options.SettingDirectory);
        Assert.Equal(ChallengeScores.Status.Invalid,
            scores.Evaluate(187, 4021, JsonNode.Parse($"{{\"nDamage\":{damage}}}")!.AsObject(), false, out _));
    }

    [Fact]
    public async Task KeepPersistsScoreAndDetailsSpendsOnceThenImprovementAddsOnlyDelta()
    {
        using var h = await ChallengeTestHarness.CreateAsync("score-save");
        string token = await Finish(h);
        Assert.True(Reply(h)["isBest"]!.GetValue<bool>());
        Assert.Equal(6000, Reply(h)["SingleScore"]!.GetValue<int>());
        Assert.Equal(0, Reply(h)["nGetScore"]!.GetValue<int>());
        Assert.Equal(0, h.Player.GetTask(9, 45901));
        Assert.Equal(3, h.Player.GetTask(9, 45900));
        Assert.Equal(1, h.Player.GetTask(9, 45301));
        h.Session.Tag = h.Module.Store.Load(h.Player.Account)!;
        await h.DispatchLuaCallAsync(207, Choice(token, true));
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(6000, Current(h).GetTask(9, 46301));
        Assert.Equal(500000, Current(h).GetTask(9, 47301));
        Assert.Equal(6000, Current(h).GetTask(9, 45901));
        Assert.Equal(2, Current(h).GetTask(9, 45900));
        string saved = JsonSerializer.Serialize(Current(h));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.DispatchLuaCallAsync(207, Choice(token, true))));
        Assert.Equal(saved, JsonSerializer.Serialize(Current(h)));
        await h.DispatchLuaCallAsync(207, Choice(token, false));
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(saved, JsonSerializer.Serialize(Current(h)));
        token = await Finish(h, 900000);
        Assert.Equal(6000, Reply(h)["nGetScore"]!.GetValue<int>());
        await h.DispatchLuaCallAsync(207, Choice(token, true));
        Assert.Equal(8400, Current(h).GetTask(9, 45901));
        Assert.Equal(1, Current(h).GetTask(9, 45900));
        Assert.Equal(8400, h.Module.Store.Load(h.Player.Account)!.GetTask(9, 46301));
    }

    [Fact]
    public async Task DiscardRetainsFirstClearAndCannotBeChangedToKeepOrAppliedToNewRun()
    {
        using var h = await ChallengeTestHarness.CreateAsync("score-discard");
        string old = await Finish(h);
        await h.DispatchLuaCallAsync(54, Entry);
        Assert.NotNull(Reply(h)["nError"]); // An unresolved decision cannot be overwritten.
        await h.DispatchLuaCallAsync(207, Choice(old, false));
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(3, h.Player.GetTask(9, 45900));
        Assert.Equal(0, h.Player.GetTask(9, 45901));
        Assert.Equal(1, h.Player.GetTask(9, 45301));
        await h.DispatchLuaCallAsync(207, Choice(old, true));
        Assert.NotNull(Reply(h)["nError"]);
        string current = await Finish(h, 900000);
        Assert.NotEqual(old, current);
        string before = JsonSerializer.Serialize(h.Player);
        await h.DispatchLuaCallAsync(207, Choice(old, true));
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
        await h.DispatchLuaCallAsync(207, Choice(current, true));
        Assert.Equal(8400, h.Player.GetTask(9, 45901));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DecisionSaveFailureRestoresWholePlayerAndRetryIsDurable(bool keep)
    {
        using var h = await ChallengeTestHarness.CreateAsync("score-save-failure");
        string token = await Finish(h);
        string before = JsonSerializer.Serialize(h.Player);
        string disk = File.ReadAllText(Path.Combine(h.Module.Store.DirectoryPath, h.Player.Account + ".player.json"));
        string blocker = Path.Combine(h.Module.Store.DirectoryPath, h.Player.Account + ".player.json.tmp");
        Directory.CreateDirectory(blocker);
        try { await h.DispatchLuaCallAsync(207, Choice(token, keep)); }
        finally { Directory.Delete(blocker); }
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
        Assert.Equal(disk, File.ReadAllText(Path.Combine(h.Module.Store.DirectoryPath, h.Player.Account + ".player.json")));
        await h.DispatchLuaCallAsync(207, Choice(token, keep));
        Assert.Null(Reply(h)["nError"]);
        var loaded = h.Module.Store.Load(h.Player.Account)!;
        Assert.Equal(keep ? 2 : 3, loaded.GetTask(9, 45900));
        Assert.Equal(keep ? 6000 : 0, loaded.GetTask(9, 45901));
    }

    [Fact]
    public async Task ExhaustedSaveQuotaKeepsPendingScoreAndNextDayRefreshAllowsRetry()
    {
        using var h = await ChallengeTestHarness.CreateAsync("score-no-quota");
        string token = await Finish(h);
        h.Player.SetTask(9, 45900, 0);
        h.Module.Store.SaveRequired(h.Player);
        string before = JsonSerializer.Serialize(h.Player);
        await h.DispatchLuaCallAsync(207, Choice(token, true));
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
        h.Player.Extra[ChallengeDaily.DayKey] = ChallengeDaily.Day(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - 1;
        await h.DispatchLuaCallAsync(207, Choice(token, true));
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(2, h.Player.GetTask(9, 45900));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("{\"keep\":true}")]
    [InlineData("{\"keep\":1,\"battleToken\":\"00000000000000000000000000000000\"}")]
    public async Task UnboundOrMalformedDecisionCannotModifyPendingRun(string request)
    {
        using var h = await ChallengeTestHarness.CreateAsync("score-bad-choice");
        await Finish(h);
        string before = JsonSerializer.Serialize(h.Player);
        await h.DispatchLuaCallAsync(207, request);
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
    }

    [Fact]
    public async Task ReconnectRetrievesPendingDecisionAndRewardsOnlyUseCommittedTotal()
    {
        using var h = await ChallengeTestHarness.CreateAsync("score-recover");
        string token = await Finish(h, wrapped: true);
        await h.DispatchLuaCallAsync(206, "null");
        Assert.False(Reply(h)["res"]!.GetValue<bool>());
        h.Session.Tag = h.Module.Store.Load(h.Player.Account)!;
        await h.DispatchLuaCallAsync(53, "[349,6]");
        Assert.Equal(token, Reply(h)["pendingScore"]!["battleToken"]!.GetValue<string>());
        Assert.Equal(6000, Reply(h)["pendingScore"]!["SingleScore"]!.GetValue<int>());
        await h.DispatchLuaCallAsync(207, Choice(token, true));
        Assert.Null(Reply(h)["nError"]);
        await h.DispatchLuaCallAsync(53, "[349,6]");
        Assert.Null(Reply(h)["pendingScore"]);
        await h.DispatchLuaCallAsync(206, "null");
        Assert.True(Reply(h)["res"]!.GetValue<bool>());
        Assert.Equal(4900, Current(h).GetTask(9, 45906));
        await h.DispatchLuaCallAsync(206, "null");
        Assert.False(Reply(h)["res"]!.GetValue<bool>());
    }

    [Fact]
    public async Task EqualResultDoesNotPromptOrSpendAndOldEntryDebitIsNotChargedAgain()
    {
        using var h = await ChallengeTestHarness.CreateAsync("score-equal");
        h.Player.SetTask(9, 46301, 6000);
        h.Player.SetTask(9, 45901, 6000);
        string token = await Finish(h);
        Assert.False(Reply(h)["isBest"]!.GetValue<bool>());
        await h.DispatchLuaCallAsync(207, Choice(token, true));
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(3, h.Player.GetTask(9, 45900));
        await h.DispatchLuaCallAsync(54, Entry);
        ((JsonObject)h.Player.Extra[ChallengeRuns.AttemptKey]!).Remove("quotaAtSave");
        await ChallengeTestHarness.SettleAsync(h, 55, "[6,349,187,4021,1,{\"isPass\":false,\"nDamage\":1800000}]");
        Assert.False(Reply(h)["scoreAvailable"]!.GetValue<bool>());
        Assert.False(Reply(h)["isBest"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ThreeImprovementSavesExhaustDailyQuotaWithoutChargingEntryOrDiscard()
    {
        using var h = await ChallengeTestHarness.CreateAsync("score-three-saves");
        string token = await Finish(h);
        await h.DispatchLuaCallAsync(207, Choice(token, false));
        Assert.Equal(3, h.Player.GetTask(9, 45900));
        int remaining = 3;
        foreach (int damage in new[] { 500000, 900000, 1800000 })
        {
            token = await Finish(h, damage);
            Assert.Equal(remaining, h.Player.GetTask(9, 45900));
            await h.DispatchLuaCallAsync(207, Choice(token, true));
            Assert.Null(Reply(h)["nError"]);
            Assert.Equal(--remaining, h.Player.GetTask(9, 45900));
        }
        Assert.Equal(12000, h.Player.GetTask(9, 45901));
        await h.DispatchLuaCallAsync(54, Entry);
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(0, h.Module.Store.Load(h.Player.Account)!.GetTask(9, 45900));
    }
}
