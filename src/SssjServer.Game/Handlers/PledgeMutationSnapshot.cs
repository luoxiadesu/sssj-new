namespace SssjServer.Game.Handlers;

/// <summary>Extend the shared money/task/reward snapshot with pledge card growth and formation references.</summary>
internal sealed class PledgeMutationSnapshot
{
    private readonly ChallengeMutationSnapshot _common;
    private readonly (Item Item, uint Enhance, uint Exp, uint Break, List<ItemSkill> Skills)[] _cards;
    private readonly (FormationCardSlot Slot, uint Main, uint Used, uint[] Secondary)[] _slots;

    internal PledgeMutationSnapshot(Player player)
    {
        _common = new(player);
        _cards = player.Items.Where(i => i.Genre == 1).Select(i =>
            (i, i.EnhanceLevel, i.EnhanceExp, i.BreakLevel, i.Skills.ToList())).ToArray();
        _slots = player.Formations.SelectMany(f => f.Slots).Select(s =>
            (s, s.MainCardGuid, s.UsedCardGuid, s.SecondaryCardGuids.ToArray())).ToArray();
    }

    internal void Restore(Player player)
    {
        _common.Restore(player);
        foreach (var saved in _cards)
        {
            saved.Item.EnhanceLevel = saved.Enhance;
            saved.Item.EnhanceExp = saved.Exp;
            saved.Item.BreakLevel = saved.Break;
            saved.Item.Skills = saved.Skills;
        }
        foreach (var saved in _slots)
        {
            saved.Slot.MainCardGuid = saved.Main;
            saved.Slot.UsedCardGuid = saved.Used;
            saved.Slot.SecondaryCardGuids.Clear();
            saved.Slot.SecondaryCardGuids.AddRange(saved.Secondary);
        }
    }
}
