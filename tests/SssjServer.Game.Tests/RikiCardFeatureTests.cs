using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class RikiCardFeatureTests
{
    [Theory]
    [InlineData(4, 4, 1263)]
    [InlineData(8, 3, 1264)]
    [InlineData(11, 4, 1265)]
    [InlineData(16, 3, 1266)]
    public async Task NormalReward_UnlocksRikiOnceAndSurvivesSaleAndReload(int girl, int level, int riki)
    {
        using var h = await FeatureHarness.CreateAsync("riki-reward");
        var reward = new[] { 1, girl, 34, level, 1 };
        var granted = h.Module.Rewards.Grant(h.Player, [reward], RewardSource.Extra);
        Assert.Contains((uint)riki, granted.ChangedRiki);
        Assert.Contains(granted.ChangedTasks, t => t.Key == Player.ComposeTaskId(53, riki) && t.Value == 1);
        GameNotify.PushRewards(h.Session, h.Player, granted);
        Assert.True(await h.WaitForPfAsync(SssjServer.Protocol.Pf.RikiData));
        Assert.Empty(h.Module.Rewards.Grant(h.Player, [reward], RewardSource.Extra).ChangedRiki);
        h.Player.Items.RemoveAll(i => i.Genre == 1 && i.Detail == girl && i.Particular == 34);
        h.Module.Store.Save(h.Player);
        var saved = h.Module.Store.Load(h.Player.Account)!;
        Assert.Equal(1, saved.GetTask(53, riki));
        Assert.Contains((uint)riki, h.Module.Rewards.RikiCards!.OwnedIds(saved));
    }

    [Fact]
    public void LoginSync_UsesHistoricalCollectionButDoesNotGrantUnknownCards()
    {
        var table = RikiCardTable.Load(TestPaths.SettingDir);
        var cards = CharacterCardTable.Load(TestPaths.SettingDir);
        var player = new Player("riki-history", 1);
        Assert.False(table.SyncOwned(player, cards));
        Assert.Empty(table.OwnedIds(player));
        Assert.NotNull(CardCollection.MarkOwned(player, cards, 1, 16, 34, 3));
        Assert.True(table.SyncOwned(player, cards));
        Assert.Equal(new uint[] { 1266 }, table.OwnedIds(player));
        Assert.Empty(player.Items);
        Assert.False(table.SyncOwned(player, cards));
        player.SetTask(53, 1266, 2);
        Assert.False(table.SyncOwned(player, cards));
        Assert.Equal(2, player.GetTask(53, 1266));
    }

    [Fact]
    public async Task Login_SendsDedicatedListAndPersistsOwnedCardRepair()
    {
        using var h = await FeatureHarness.CreateAsync("riki-login");
        h.Player.AddItem(1, 4, 34, 4, 1);
        h.Module.RunPrepareHooksForTest(h.Session, h.Player);
        h.Module.RunLoginHooksForTest(h.Session, h.Player);
        Assert.True(await h.WaitForPfAsync(SssjServer.Protocol.Pf.RikiList));
        Assert.Equal(1, h.Module.Store.Load(h.Player.Account)!.GetTask(53, 1263));
    }

    [Fact]
    public void EmptyAndInvalidCards_DoNotUnlockRiki()
    {
        var table = RikiCardTable.Load(TestPaths.SettingDir);
        var cards = CharacterCardTable.Load(TestPaths.SettingDir);
        var player = new Player("riki-invalid", 1);
        player.AddItem(1, 4, 34, 4, 0);
        player.AddItem(1, 16, 999, 3, 1);
        Assert.False(table.SyncOwned(player, cards));
        Assert.Empty(table.OwnedIds(player));
    }

    [Fact]
    public void CurrentPermanentPool_ContainsThreeGachaCardsButNotEventRewardYui()
    {
        var table = GachaTable.Load(TestPaths.SettingDir);
        var pool = table.Get(1, new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds());
        Assert.NotNull(pool);
        foreach (int girl in new[] { 4, 8, 11 })
            Assert.Contains(pool!.Cards.Values, c => c.D == girl && c.P == 34 && c.RawRate > 0);
        Assert.DoesNotContain(pool!.Cards.Values, c => c.D == 16 && c.P == 34);
    }
}
