using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class GirlDressRewardTests
{
    private static void ClearOutfits(FeatureHarness h, uint id)
    {
        h.Player.Items.RemoveAll(i => i.Genre == 19 || i.Genre == 1 && i.Detail == id);
        var models = h.Module.CharacterCards.ForGirl(id).Select(c => c.ModelId)
            .Concat(GirlDressTable.Load(TestPaths.SettingDir).All.Where(s => s.GirlId == id).Select(s => s.ModelId));
        foreach (var model in models) GirlDressFeature.SetSuitValue(h.Player, id, model, 0, null);
        h.Player.SetTask(GirlLoveFeature.VarGroup(id), GirlLoveFeature.VarTaskId(id, GirlDressFeature.VarSuitCount), 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(201)]
    public async Task CardImmediatelyUnlocksAndCountsOutfitWithoutResettingWornState(int id)
    {
        using var h = await FeatureHarness.CreateAsync("dress-reward-card-" + id);
        uint girlId = (uint)id;
        if (h.Player.GetGirl(girlId) is null) h.Player.Girls.Add(new Girl { Girlid = girlId, Level = 1, Modelid = 1 });
        ClearOutfits(h, girlId);
        var card = h.Module.CharacterCards.ForGirl(girlId).First();
        var grant = h.Module.Rewards.Grant(h.Player, new[] { new[] { 1, id, (int)card.P, (int)card.L, 1 } }, RewardSource.Extra);
        int group = GirlLoveFeature.VarGroup(girlId);
        int countTask = GirlLoveFeature.VarTaskId(girlId, GirlDressFeature.VarSuitCount);
        Assert.Equal(1, h.Player.GetTask(group, countTask));
        Assert.Equal(GirlDressFeature.SuitNew, GirlDressFeature.GetSuitValue(h.Player, girlId, card.ModelId));
        Assert.Contains(grant.ChangedTasks, t => t.Key == Player.ComposeTaskId(group, countTask) && t.Value == 1);
        GirlDressFeature.SetSuitValue(h.Player, girlId, card.ModelId, GirlDressFeature.SuitWorn, null);
        h.Module.Rewards.Grant(h.Player, new[] { new[] { 1, id, (int)card.P, (int)card.L, 2 } }, RewardSource.Extra);
        Assert.Equal(1, h.Player.GetTask(group, countTask));
        Assert.Equal(GirlDressFeature.SuitWorn, GirlDressFeature.GetSuitValue(h.Player, girlId, card.ModelId));
        h.RunPrepareHooks();
        Assert.Equal(1, h.Player.GetTask(group, countTask));
    }

    [Fact]
    public async Task SkinBeforeFirstGirlUnlocksOnCardGrantAndPushesGirlBeforeOutfitTasks()
    {
        using var h = await FeatureHarness.CreateAsync("dress-reward-deferred-skin");
        var suit = GirlDressTable.Load(TestPaths.SettingDir).All.First(s => s.Gdpl is not null);
        uint id = suit.GirlId;
        ClearOutfits(h, id);
        h.Player.Girls.RemoveAll(g => g.Girlid == id);
        var q = suit.Gdpl!.Value;
        h.Module.Rewards.Grant(h.Player, new[] { new[] { (int)q.G, (int)q.D, (int)q.P, (int)q.L, 1 } }, RewardSource.Extra);
        Assert.Equal(0, GirlDressFeature.GetSuitValue(h.Player, id, suit.ModelId));
        var card = h.Module.CharacterCards.ForGirl(id).First();
        var granted = h.Module.Rewards.Grant(h.Player, new[] { new[] { 1, (int)id, (int)card.P, (int)card.L, 1 } }, RewardSource.Extra);
        Assert.Single(granted.ChangedGirls);
        Assert.Equal((uint)card.ModelId, h.Player.GetGirl(id)!.Modelid);
        Assert.Equal(1, GirlDressFeature.GetSuitValue(h.Player, id, suit.ModelId));
        int expected = new[] { suit.ModelId, card.ModelId }.Select(GmUnlock.FixModelId).Distinct().Count();
        int group = GirlLoveFeature.VarGroup(id);
        int task = GirlLoveFeature.VarTaskId(id, GirlDressFeature.VarSuitCount);
        Assert.Equal(expected, h.Player.GetTask(group, task));
        GameNotify.PushRewards(h.Session, h.Player, granted);
        Assert.True(await h.WaitForPfAsync(1026));
        Assert.True(h.PushedPf.IndexOf(1030) < h.PushedPf.IndexOf(1031));
        Assert.True(h.PushedPf.IndexOf(1031) < h.PushedPf.IndexOf(1026));
        new PlayerStore(h.Options).Save(h.Player);
        Assert.Equal(expected, new PlayerStore(h.Options).Load(h.Player.Account)!.GetTask(group, task));
        h.RunPrepareHooks();
        Assert.Equal(expected, h.Player.GetTask(group, task));
    }

    [Fact]
    public async Task SkinOnOwnedGirlRefreshesCountImmediatelyAndDuplicatesDoNotInflateIt()
    {
        using var h = await FeatureHarness.CreateAsync("dress-reward-skin-count");
        var suit = GirlDressTable.Load(TestPaths.SettingDir).All.First(s => s.Gdpl is not null);
        uint id = suit.GirlId;
        if (h.Player.GetGirl(id) is null) h.Player.Girls.Add(new Girl { Girlid = id, Level = 1 });
        ClearOutfits(h, id);
        var q = suit.Gdpl!.Value;
        var award = new[] { new[] { (int)q.G, (int)q.D, (int)q.P, (int)q.L, 1 } };
        h.Module.Rewards.Grant(h.Player, award, RewardSource.Extra);
        int group = GirlLoveFeature.VarGroup(id), task = GirlLoveFeature.VarTaskId(id, GirlDressFeature.VarSuitCount);
        Assert.Equal(1, h.Player.GetTask(group, task));
        h.Module.Rewards.Grant(h.Player, award, RewardSource.Extra);
        Assert.Equal(1, h.Player.GetTask(group, task));
    }
}
