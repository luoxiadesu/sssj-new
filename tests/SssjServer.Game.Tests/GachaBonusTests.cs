using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class GachaBonusTests
{
    [Fact]
    public void ZeroCountIsNotOwnedAndNextDuplicateIsNoLongerFirstGet()
    {
        var player = new Player("gacha-first-get", 1);
        player.AddItem(1, 1, 1, 4, count: 0);
        var pool = new GachaPool { Id = 1, Cards =
            { [1] = new GachaCard { Id = 1, G = 1, D = 1, P = 1, L = 4, RawRate = 1 } },
            StarTotal = { [4] = 1 } };
        var feature = new GachaFeature(new Random(1));
        Assert.True(feature.DrawOne(player, pool, null).FirstGet);
        player.AddItem(1, 1, 1, 4, count: 1);
        Assert.False(feature.DrawOne(player, pool, null).FirstGet);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 10)]
    public async Task CompanionMaterialIsGrantedAndReplayedWithoutGrantingAgain(bool ten, int count)
    {
        using var h = await FeatureHarness.CreateAsync($"gacha-bonus-{ten}");
        var pool = GachaTable.Load(TestPaths.SettingDir).Get(1, DateTimeOffset.UtcNow.ToUnixTimeSeconds())!;
        var material = Assert.IsType<int[]>(pool.GetItem);
        Assert.Equal(new[] { 7, 1, 4, 2, 1 }, material);
        long Count() => h.Player.Items.Where(i => i.Genre == 7 && i.Detail == 1 && i.Particular == 4 && i.Level == 2).Sum(i => (long)i.Count);
        long before = Count();
        h.Player.AddItem(9, ten ? 13u : 12u, 1, 1, count: 1);
        await h.LuaCalls.DispatchAsync(h.Session, "Lottery", $"{{\"nId\":1,\"bTen\":{ten.ToString().ToLowerInvariant()}}}");
        var reply = JsonNode.Parse(h.LastOutbound.Json)!;
        Assert.Null(reply["err"]);
        Assert.Equal(count, reply["getItem"]![4]!.GetValue<int>());
        Assert.Equal(before + count, Count());
        await h.LuaCalls.DispatchAsync(h.Session, "GetLastGacha", "true");
        Assert.Equal(count, JsonNode.Parse(h.LastOutbound.Json)!["getItem"]![4]!.GetValue<int>());
        Assert.Equal(before + count, Count());
        var reloaded = h.Module.Store.Load(h.Player.Account)!;
        Assert.Equal(count, GachaFeature.ReadLast(reloaded)!["getItem"]![4]!.GetValue<int>());
    }

    [Theory]
    [InlineData(0, 299, 1, 1)]
    [InlineData(0, 300, 10, 0)]
    [InlineData(1, 599, 10, 1)]
    [InlineData(1, 600, 10, 0)]
    [InlineData(1, 299, 1, 1)]
    public void BoxIsGrantedOnlyWhenCrossingItsConfiguredThreshold(int rotation, int before, int draws, int boxes)
    {
        var pool = new GachaPool { UpBoxNum = 300, RotateVersion = rotation, UpBox = [8, 10, 1, 4] };
        var rewards = GachaFeature.PlanBonusRewards(pool, before, draws);
        if (boxes == 0) Assert.Empty(rewards);
        else Assert.Equal(new[] { 8, 10, 1, 4, boxes }, Assert.Single(rewards));
    }

    [Fact]
    public void OverflowRewardQuantityIsRejectedBeforeCharging()
    {
        var pool = new GachaPool { GetItem = [7, 1, 4, 2, int.MaxValue] };
        Assert.Throws<InvalidDataException>(() => GachaFeature.PlanBonusRewards(pool, 0, 10));
    }
}
