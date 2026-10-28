using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>Apply the completed stage's card configuration, never the next stage's preview.</summary>
internal static class PledgeCardRewards
{
    internal sealed record Result(RewardGrantResult Rewards, Item? Removed, IReadOnlyList<Formation> Formations);

    internal static Result Apply(GameFeatureContext ctx, Player player, PledgeGirlState girl, PledgeStepRow cfg)
    {
        if (cfg.Step is < 1 or > 6 || cfg.GirlId != girl.GirlId || cfg.CardGdpl.Count != 4
            || cfg.CardGdpl[0] != 1 || cfg.CardGdpl[1] != girl.GirlId
            || cfg.CardGdpl[2] != CardFeature.PldgeCardParticular || cfg.CardLv < 1
            || cfg.CardBreakLv < 0 || cfg.CardSkillLv.Count != 4
            || cfg.CardSkillLv.Any(l => l is < 1 or > 4))
            throw new InvalidOperationException("Invalid ordinary pledge card configuration");
        var g = cfg.CardGdpl.Select(v => checked((uint)v)).ToArray();
        var template = ctx.CharacterCards.Get(g[0], g[1], g[2], g[3])
            ?? throw new InvalidOperationException("Pledge card template is missing");
        // The heart display uses P=17,L=5 in UI_CardMain; there is no separate heart item field.
        if (cfg.Heart == 1 && g[3] != 5) throw new InvalidOperationException("Invalid pledge heart template");
        var ids = new[] { template.TacticalSkill, template.PassiveSkill1, template.PassiveSkill2, template.ResistanceSkill };
        if (ids.Any(id => id <= 0) || ids.Distinct().Count() != 4)
            throw new InvalidOperationException("Pledge card skills are missing or ambiguous");
        Item? old = girl.CardGuid == 0 ? null : player.GetItem(girl.CardGuid);
        if (girl.CardGuid != 0 && (old is null || old.Count != 1 || old.Genre != 1
            || old.Detail != girl.GirlId || old.Particular != CardFeature.PldgeCardParticular))
            throw new InvalidOperationException("Tracked pledge card is missing or belongs to another girl");
        if (old is not null && old.Level > g[3])
            throw new InvalidOperationException("Refuse to downgrade an inconsistent tracked pledge card");

        var rewards = new RewardGrantResult();
        Item card;
        Item? removed = null;
        var formations = new List<Formation>();
        bool sameTemplate = old is not null && old.Level == g[3];
        if (old is not null && !sameTemplate && cfg.ChangeCard != 1)
            throw new InvalidOperationException("Pledge template change requires changecard");
        if (sameTemplate)
        {
            card = old!;
            rewards.NoteItem(card);
        }
        else
        {
            if (player.NextGuid == 0 || player.NextGuid == uint.MaxValue || player.GetItem(player.NextGuid) is not null)
                throw new InvalidOperationException("No safe pledge card GUID available");
            // AddCharacterCard updates an existing GUID without rebuilding its
            // template. A different GDPL therefore needs a fresh client instance.
            ctx.Rewards.Grant(player, [new[] { (int)g[0], (int)g[1], (int)g[2], (int)g[3], 1 }], RewardSource.Extra, rewards);
            card = rewards.ChangedItems.Single(i => i.Genre == 1);
            if (old is not null)
            {
                card.LockOn = old.LockOn;
                card.SyncRate = old.SyncRate;
                card.RefitNum = old.RefitNum;
                old.Count = 0;
                player.Items.Remove(old);
                removed = old;
                foreach (var formation in player.Formations)
                {
                    bool changed = false;
                    foreach (var slot in formation.Slots)
                    {
                        if (slot.MainCardGuid == old.Guid) { slot.MainCardGuid = card.Guid; changed = true; }
                        if (slot.UsedCardGuid == old.Guid) { slot.UsedCardGuid = card.Guid; changed = true; }
                        for (int i = 0; i < slot.SecondaryCardGuids.Count; i++)
                            if (slot.SecondaryCardGuids[i] == old.Guid) { slot.SecondaryCardGuids[i] = card.Guid; changed = true; }
                    }
                    if (changed) formations.Add(formation);
                }
            }
        }
        card.EnhanceLevel = checked((uint)cfg.CardLv);
        card.EnhanceExp = 0;
        card.BreakLevel = checked((uint)cfg.CardBreakLv);
        card.Skills = ids.Select((id, i) => new ItemSkill { SkillId = (uint)id, SkillLevel = (uint)cfg.CardSkillLv[i] }).ToList();
        girl.CardGuid = card.Guid;
        return new(rewards, removed, formations);
    }
}
