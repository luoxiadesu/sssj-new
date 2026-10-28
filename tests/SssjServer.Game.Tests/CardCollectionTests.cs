using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class CardCollectionTests
{
    [Fact]
    public async Task RepeatedRewardsKeepSeparateCardsAndPreserveSeenCollectionMarker()
    {
        using var h = await FeatureHarness.CreateAsync("card-collection-reward");
        var card = h.Module.CharacterCards.All.First(c => c.D == 1);
        Assert.True(CardCollection.TryKey(card.D, card.P, card.L, out int group, out int task));
        int Before() => h.Player.Items.Count(i => i.Genre == 1 && i.Detail == card.D && i.Particular == card.P && i.Level == card.L);
        int before = Before();
        var result = h.Module.Rewards.Grant(h.Player, [new[] { 1, (int)card.D, (int)card.P, (int)card.L, 1 }], RewardSource.Extra);
        Assert.Equal(1, h.Player.GetTask(group, task));
        Assert.Contains(result.ChangedTasks, t => t.Key == Player.ComposeTaskId(group, task) && t.Value == 1);
        h.Player.SetTask(group, task, 2);
        h.Module.Rewards.Grant(h.Player, [new[] { 1, (int)card.D, (int)card.P, (int)card.L, 1 }], RewardSource.Extra);
        Assert.Equal(before + 2, Before());
        Assert.Equal(2, h.Player.GetTask(group, task));
        var guids = h.Player.Items.Where(i => i.Genre == 1 && i.Detail == card.D && i.Particular == card.P && i.Level == card.L).Select(i => i.Guid).ToArray();
        Assert.Equal(guids.Length, guids.Distinct().Count());
        h.Player.Items.RemoveAll(i => i.Genre == 1 && i.Detail == card.D && i.Particular == card.P && i.Level == card.L);
        Assert.True(CardCollection.HasOwned(h.Player, 1, card.D, card.P, card.L));
    }

    [Fact]
    public void LinkCollectionUsesGroup92AndDoesNotTouchNormalGirls()
    {
        var table = CharacterCardTable.Load(TestPaths.SettingDir);
        var card = table.All.First(c => GirlLoveTable.IsLinkGirl(c.D));
        var player = new Player("collection-link", 1);
        Assert.NotNull(CardCollection.MarkOwned(player, table, 1, card.D, card.P, card.L));
        Assert.True(CardCollection.TryKey(card.D, card.P, card.L, out int group, out int task));
        Assert.Equal(92, group);
        Assert.Equal(1, player.GetTask(92, task));
        Assert.Equal(0, player.GetTask(7, task));
    }

    [Fact]
    public void LoginSyncRequiresPositiveConfiguredCardButAllowsLockedOwnership()
    {
        var table = CharacterCardTable.Load(TestPaths.SettingDir);
        var card = table.All.First(c => c.D == 1);
        var player = new Player("collection-sync", 1);
        var item = player.AddItem(1, card.D, card.P, card.L, count: 0);
        Assert.False(CardCollection.SyncOwned(player, table));
        item.Count = 1;
        item.LockOn = 1;
        Assert.True(CardCollection.SyncOwned(player, table));
        Assert.False(CardCollection.SyncOwned(player, table));
        Assert.Null(CardCollection.MarkOwned(player, table, 1, card.D, 999, card.L));
        Assert.False(CardCollection.TryKey(card.D, 999, card.L, out _, out _));
    }

    [Fact]
    public void PreviouslyCollectedButSoldCardIsNotFirstGet()
    {
        var table = CharacterCardTable.Load(TestPaths.SettingDir);
        var card = table.All.First(c => c.D == 1 && c.L == 4);
        var player = new Player("collection-first", 1);
        CardCollection.MarkOwned(player, table, 1, card.D, card.P, card.L);
        var pool = new GachaPool { Id = 1, Cards = { [1] = new GachaCard
            { Id = 1, G = 1, D = (int)card.D, P = (int)card.P, L = 4, RawRate = 1 } }, StarTotal = { [4] = 1 } };
        Assert.False(new GachaFeature(new Random(1)).DrawOne(player, pool, null).FirstGet);
        Assert.Empty(player.Items);
    }
}
