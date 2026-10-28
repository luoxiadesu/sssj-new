using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;

public class ChallengeRewardConcurrencyTests
{
    private static long Material(Player p) => p.Items.Where(i => i.Genre == 7 && i.Detail == 1
        && i.Particular == 4 && i.Level == 2).Sum(i => (long)i.Count);
    private static Task Claim(FeatureHarness h) => h.DispatchLuaCallAsync(206, "{}");
    private static bool Result(FeatureHarness h) => JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!["res"]!.GetValue<bool>();

    [Fact]
    public async Task ConcurrentClaimsGrantEveryEligibleTierExactlyOnceAndPersist()
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-concurrent-rewards");
        h.Player.SetTask(9, 45901, 3600);
        long material = Material(h.Player);
        int gold = h.Player.GetMoney(MoneyType.Gold);
        int waiting = 8;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            if (Interlocked.Decrement(ref waiting) == 0) ready.SetResult();
            await start.Task;
            await Claim(h);
        })).ToArray();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        start.SetResult();
        await Task.WhenAll(requests);
        var replies = h.Outbound.Select(o => JsonNode.Parse(o.Json)!)
            .Where(o => o["sCmd"]?.GetValue<int>() == 206).Select(o => o["tbParam"]!).ToArray();
        Assert.Equal(8, replies.Length);
        Assert.Single(replies, r => r["res"]!.GetValue<bool>());
        Assert.Equal(material + 1, Material(h.Player));
        Assert.Equal(gold + 10000, h.Player.GetMoney(MoneyType.Gold));
        Assert.Equal(3600, h.Player.GetTask(9, 45906));
        var loaded = h.Module.Store.Load(h.Player.Account)!;
        Assert.Equal(3600, loaded.GetTask(9, 45906));
        Assert.Equal(gold + 10000, loaded.GetMoney(MoneyType.Gold));
        h.Session.Tag = loaded;
        await Claim(h);
        Assert.False(Result(h));
        Assert.Equal(material + 1, Material(loaded));
        Assert.Equal(gold + 10000, loaded.GetMoney(MoneyType.Gold));
    }

    [Fact]
    public async Task HigherScoreOnlyGrantsNewTierAndLowerScoreDoesNotResetClaimedGoal()
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-incremental-rewards");
        long material = Material(h.Player);
        int gold = h.Player.GetMoney(MoneyType.Gold);
        h.Player.SetTask(9, 45901, 2000);
        await Claim(h);
        Assert.True(Result(h));
        h.Player.SetTask(9, 45901, 3600);
        await Claim(h);
        Assert.True(Result(h));
        Assert.Equal(material + 1, Material(h.Player));
        Assert.Equal(gold + 10000, h.Player.GetMoney(MoneyType.Gold));
        h.Player.SetTask(9, 45901, 0);
        await Claim(h);
        Assert.False(Result(h));
        Assert.Equal(3600, h.Player.GetTask(9, 45906));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(3600, -1)]
    public async Task InvalidStoredProgressDoesNotGrantOrRewriteState(int total, int claimed)
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-corrupt-rewards");
        h.Player.SetTask(9, 45901, total);
        h.Player.SetTask(9, 45906, claimed);
        var money = h.Player.Money.OrderBy(e => e.Key).ToArray();
        long material = Material(h.Player);
        await Claim(h);
        Assert.False(Result(h));
        Assert.Equal(claimed, h.Player.GetTask(9, 45906));
        Assert.Equal(total, h.Player.GetTask(9, 45901));
        Assert.Equal(material, Material(h.Player));
        Assert.Equal(money, h.Player.Money.OrderBy(e => e.Key).ToArray());
    }
}
