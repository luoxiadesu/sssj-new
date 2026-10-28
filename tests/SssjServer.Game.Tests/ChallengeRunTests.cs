using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;

public class ChallengeRunTests
{
    [Theory]
    [InlineData(907, 2153, 45101)]
    [InlineData(507, 3007, 45201)]
    [InlineData(187, 4021, 45301)]
    public async Task CurrentWeekZonesUseClientTaskOffsetsWithoutOverwritingScores(int node, int level, int key)
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-zone-" + node);
        h.Player.SetTask(9, 45901, 4242);
        h.Player.SetTask(9, 46000 + key - 45000, 1200);
        await h.DispatchLuaCallAsync(54, Args(level, node: node));
        Assert.Null(Reply(h)["nError"]);
        await ChallengeTestHarness.SettleAsync(h,55, Args(level, node: node));
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(1, h.Player.GetTask(9, key));
        Assert.Equal(4242, h.Player.GetTask(9, 45901));
        Assert.Equal(1200, h.Player.GetTask(9, 46000 + key - 45000));
    }

    [Fact]
    public void MultipleRewardsInsideSingleBracketRemainSeparateTuples()
    {
        var parsed = ChallengeRuns.ParseRewards("[7-7-1-4-1,15-1-1-1-3000]");
        Assert.Equal(2, parsed.Count);
        Assert.Equal(new[] { 7, 7, 1, 4, 1 }, parsed[0]);
        Assert.Equal(new[] { 15, 1, 1, 1, 3000 }, parsed[1]);
        Assert.Empty(ChallengeRuns.ParseRewards("[7-7-1-4--1]"));
    }

    private static string Args(int level = 2153, bool win = true, int node = 907, int week = 349)
        => $"[6,{week},{node},{level},1,{{\"isPass\":{(win ? "true" : "false")},\"nDamage\":3000000}}]";
    private static JsonNode Reply(FeatureHarness h) => JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
    private static long Tickets(Player p) => p.Items.Where(i => i.Genre == 7 && i.Detail == 7 && i.Particular == 1 && i.Level == 4).Sum(i => (long)i.Count);

    [Theory]
    [InlineData(1, 907, 349)]
    [InlineData(2153, 507, 349)]
    [InlineData(2153, 907, 348)]
    [InlineData(2154, 907, 349)]
    public async Task InvalidMembershipWeekOrLockedLevelCannotEnter(int level, int node, int week)
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-invalid-enter");
        await h.DispatchLuaCallAsync(54, Args(level, node: node, week: week));
        Assert.NotNull(Reply(h)["nError"]);
        Assert.False(h.Player.Extra.ContainsKey(ChallengeRuns.AttemptKey));
    }

    [Fact]
    public async Task FirstClearUnlocksNextLevelAndRewardsSurviveReloadWithoutDuplication()
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-first-clear");
        long before = Tickets(h.Player);
        await h.DispatchLuaCallAsync(54, Args());
        await ChallengeTestHarness.SettleAsync(h,55, Args());
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(before + 1, Tickets(h.Player));
        Assert.Equal(1, h.Player.GetTask(9, 45101));
        var balances = h.Player.Money.OrderBy(e => e.Key).ToArray();
        string firstReply = h.LastOutbound.Json;
        var loaded = h.Module.Store.Load(h.Player.Account)!;
        h.Session.Tag = loaded;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => ChallengeTestHarness.SettleAsync(h,55, Args()))));
        Assert.Equal(firstReply, h.LastOutbound.Json);
        Assert.Equal(before + 1, Tickets(loaded));
        Assert.Equal(balances, loaded.Money.OrderBy(e => e.Key).ToArray());
        await h.DispatchLuaCallAsync(54, Args());
        await ChallengeTestHarness.SettleAsync(h,55, Args());
        Assert.Empty((JsonArray)Reply(h)["tbData"]!);
        Assert.Equal(before + 1, Tickets(loaded));
        await h.DispatchLuaCallAsync(54, Args(2154));
        Assert.Null(Reply(h)["nError"]);
    }

    [Fact]
    public async Task FailureCannotUnlockOrLaterBecomeVictoryAndNoEntryCannotSettle()
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-fail-boundary");
        await ChallengeTestHarness.SettleAsync(h,55, Args());
        Assert.NotNull(Reply(h)["nError"]);
        await h.DispatchLuaCallAsync(54, Args());
        long before = Tickets(h.Player);
        await ChallengeTestHarness.SettleAsync(h,55, Args(win: false));
        Assert.False(Reply(h)["isPass"]!.GetValue<bool>());
        await ChallengeTestHarness.SettleAsync(h,55, Args());
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(before, Tickets(h.Player));
        Assert.Equal(0, h.Player.GetTask(9, 45101));
        await h.DispatchLuaCallAsync(54, Args(2154));
        Assert.NotNull(Reply(h)["nError"]);
    }

    [Fact]
    public async Task DifferentLevelOrFormationCannotConsumeActiveRun()
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-run-match");
        await h.DispatchLuaCallAsync(54, Args());
        await ChallengeTestHarness.SettleAsync(h,55, Args(2154));
        Assert.NotNull(Reply(h)["nError"]);
        await ChallengeTestHarness.SettleAsync(h,55, Args().Replace(",1,{", ",2,{"));
        Assert.NotNull(Reply(h)["nError"]);
        await ChallengeTestHarness.SettleAsync(h,55, Args());
        Assert.True(Reply(h)["isPass"]!.GetValue<bool>());
    }
}
