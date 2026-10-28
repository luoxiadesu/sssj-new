using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;

public class ChallengeDailyDamageTests
{
    private static string Args(string damage, int level = 4021) => $"[6,349,187,{level},1,{{\"isPass\":false,\"nDamage\":{damage}}}]";
    private static JsonNode Reply(FeatureHarness h) => JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;

    [Theory]
    [InlineData("499999", false)]
    [InlineData("500000", true)]
    [InlineData("500001", true)]
    public async Task DamageThresholdUnlocksWithoutChangingFailureResult(string damage, bool unlock)
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-damage-" + damage);
        await h.DispatchLuaCallAsync(54, Args(damage));
        await ChallengeTestHarness.SettleAsync(h,55, Args(damage));
        Assert.Null(Reply(h)["nError"]);
        Assert.False(Reply(h)["isPass"]!.GetValue<bool>());
        Assert.Equal(unlock ? 1 : 0, h.Player.GetTask(9, 45301));
        await h.DispatchLuaCallAsync(207, new JsonObject { ["keep"] = false, ["battleToken"] = Reply(h)["battleToken"]!.GetValue<string>() }.ToJsonString());
        await h.DispatchLuaCallAsync(54, Args(damage, 4022));
        if (unlock) Assert.Null(Reply(h)["nError"]);
        else Assert.NotNull(Reply(h)["nError"]);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("null")]
    [InlineData("\"500000\"")]
    [InlineData("1e999")]
    public async Task InvalidDamageCannotSettleOrGrantProgress(string damage)
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-invalid-damage");
        await h.DispatchLuaCallAsync(54, Args(damage));
        await ChallengeTestHarness.SettleAsync(h,55, Args(damage));
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(0, h.Player.GetTask(9, 45301));
        Assert.True(((JsonObject)h.Player.Extra[ChallengeRuns.AttemptKey]!)["active"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RepeatedEntryAndFailedRunsDoNotSpendSaveQuota()
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-daily-spend");
        const string entry = "[6,349,907,2153,1]";
        const string failure = "[6,349,907,2153,1,{\"isPass\":false}]";
        for (int i = 0; i < 3; i++)
        {
            await h.DispatchLuaCallAsync(54, entry);
            Assert.Null(Reply(h)["nError"]);
            await h.DispatchLuaCallAsync(54, entry);
            Assert.Null(Reply(h)["nError"]);
            Assert.Equal(3, ((Player)h.Session.Tag!).GetTask(9, 45900));
            await ChallengeTestHarness.SettleAsync(h,55, failure);
            h.Session.Tag = h.Module.Store.Load(h.Player.Account)!;
        }
        await h.DispatchLuaCallAsync(54, entry);
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(3, ((Player)h.Session.Tag!).GetTask(9, 45900));
    }

    [Fact]
    public async Task RefreshAtFourAmPreservesLegacyCountsAndDoesNotResetScores()
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-daily-boundary");
        long boundary = new DateTimeOffset(2026, 10, 27, 4, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        var changes = new List<KeyValuePair<uint, int>>();
        h.Player.SetTask(9, 45900, 1);
        h.Player.SetTask(9, 45901, 12345);
        Assert.True(ChallengeDaily.Refresh(h.Player, 3, boundary - 1, changes));
        Assert.Equal(1, h.Player.GetTask(9, 45900));
        Assert.False(ChallengeDaily.Refresh(h.Player, 3, boundary - 1, changes));
        Assert.True(ChallengeDaily.Refresh(h.Player, 3, boundary, changes));
        Assert.Equal(3, h.Player.GetTask(9, 45900));
        Assert.False(ChallengeDaily.Refresh(h.Player, 3, boundary - 1, changes));
        Assert.Equal(12345, h.Player.GetTask(9, 45901));
    }
}
