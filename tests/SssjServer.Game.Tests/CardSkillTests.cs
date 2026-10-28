using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class CardSkillTests
{
    [Fact]
    public void SameCardInheritsExistingSkillIncreasesAndAbsentSkillsDoNotParticipate()
    {
        var template = new CharacterCardTemplate { TacticalSkill = 101, PassiveSkill1 = 102, ResistanceSkill = 103 };
        var item = new Item();
        item.Skills.Add(new ItemSkill { SkillId = 101, SkillLevel = 3 });
        Assert.Equal(5, CardSkills.FeedPoints(item, template));
        Assert.Equal(7, CardSkills.Remaining(item, template));
        CardSkills.Apply(item, template, 100);
        Assert.All(CardSkills.Ids(template), id => Assert.Equal(4u, CardSkills.Level(item, id)));
        Assert.Equal(3, item.Skills.Count);
    }

    [Fact]
    public async Task CookiesIncreaseSkillsAndPersistToClientItemPayload()
    {
        using var h = await FeatureHarness.CreateAsync("card-skill-cookie");
        var template = h.Module.CharacterCards.All.First(t => t.P != 17 && CardSkills.Ids(t).Length >= 3);
        var card = h.Player.AddItem(template.G, template.D, template.P, template.L, 1);
        var cookie = h.Player.AddItem(7, 4, 1, 3, 2);
        h.Player.SetMoney(2, 10000000);
        int index = CardTable.ExpCards.Count + 1;
        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{card.Guid},\"items\":[[1,{index},2]]}}");
        Assert.DoesNotContain(h.Player.Items, i => i.Guid == cookie.Guid);
        Assert.Equal(2, CardSkills.Ids(template).Sum(id => (long)CardSkills.Level(card, id) - 1));
        var restored = h.Module.Store.Load(h.Player.Account)!.Items.Single(i => i.Guid == card.Guid);
        Assert.Equal(card.Skills.Select(s => s.SkillLevel), restored.Skills.Select(s => s.SkillLevel));
        Assert.Equal(card.Skills.Count, GameNotify.BuildItemInfo(restored).Skillinfo.Count);
    }

    [Fact]
    public async Task FullyTrainedCardDoesNotConsumeCookiesOrGold()
    {
        using var h = await FeatureHarness.CreateAsync("card-skill-max");
        var template = h.Module.CharacterCards.All.First(t => t.P != 17 && CardSkills.Ids(t).Length >= 3);
        var card = h.Player.AddItem(template.G, template.D, template.P, template.L, 1);
        CardSkills.Apply(card, template, 100);
        var cookie = h.Player.AddItem(7, 4, 1, 3, 1);
        int before = h.Player.GetMoney(2);
        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{card.Guid},\"items\":[[1,{CardTable.ExpCards.Count + 1},1]]}}");
        Assert.Equal(1u, cookie.Count);
        Assert.Contains(cookie, h.Player.Items);
        Assert.Equal(before, h.Player.GetMoney(2));
    }
}
