using SssjServer.Protocol.PvpProto;

namespace SssjServer.Game.Handlers;

/// <summary>Build lobby display data from owned server items, ignoring client-supplied names, stats and equipment.</summary>
internal static class CoopLobbyRoster
{
    internal static PlayerBaseInfo? Build(GameFeatureContext ctx, Player player, uint formationId)
    {
        if (formationId > int.MaxValue) return null;
        var formation = player.Formations.FirstOrDefault(f => f.Id == formationId);
        if (formation is null || formation.Slots.Count is < 1 or > 3) return null;
        var used = new HashSet<uint>();
        var girls = new HashSet<uint>();
        Item? Owned(uint guid, uint genre) => guid == 0 ? null : player.Items.FirstOrDefault(i => i.Guid == guid && i.Genre == genre && i.Count > 0);
        foreach (var slot in formation.Slots)
        {
            if (slot.MainCardGuid == 0)
            {
                if (slot.UsedCardGuid != 0 || slot.WeaponGuid != 0 || slot.SecondaryCardGuids.Any(g => g != 0) || slot.RuneItems.Any(g => g != 0)) return null;
                continue;
            }
            var main = Owned(slot.MainCardGuid, 1);
            if (main is null || ctx.CharacterCards.Get(main.Genre, main.Detail, main.Particular, main.Level) is null
                || !used.Add(main.Guid) || !girls.Add(main.Detail)) return null;
            if (slot.UsedCardGuid != 0 && slot.UsedCardGuid != main.Guid) return null;
            if (slot.WeaponGuid != 0 && (Owned(slot.WeaponGuid, 2) is null || !used.Add(slot.WeaponGuid))) return null;
            if (slot.SecondaryCardGuids.Count > 3 || slot.RuneItems.Count > 4) return null;
            foreach (uint guid in slot.SecondaryCardGuids.Where(g => g != 0))
                if (Owned(guid, 1) is null || !used.Add(guid)) return null;
            foreach (uint guid in slot.RuneItems.Where(g => g != 0))
                if (Owned(guid, 3) is null || !used.Add(guid)) return null;
        }
        var first = formation.Slots.Select(s => Owned(s.MainCardGuid, 1)).FirstOrDefault(i => i is not null);
        if (first is null) return null;
        var solution = new EquipSolution { SolutionId = checked((int)formationId), Girl = (int)first.Detail,
            Headframe = player.HeadFrame, State = 0, Teampower = (uint)Math.Max(0, player.Fightpower), Zan = 0, PvpZan = 0 };
        foreach (var slot in formation.Slots)
        {
            var main = Owned(slot.MainCardGuid, 1);
            var deputies = slot.SecondaryCardGuids.Select(g => Owned(g, 1)).ToArray();
            var runes = slot.RuneItems.Select(g => Owned(g, 3)).ToArray();
            solution.Groups.Add(new CardGroup { Maincard = Card(ctx, main), Weapon = Weapon(Owned(slot.WeaponGuid, 2)),
                Deputy1 = Card(ctx, deputies.ElementAtOrDefault(0)), Deputy2 = Card(ctx, deputies.ElementAtOrDefault(1)),
                Deputy3 = Card(ctx, deputies.ElementAtOrDefault(2)), Rune1 = Rune(runes.ElementAtOrDefault(0)),
                Rune2 = Rune(runes.ElementAtOrDefault(1)), Rune3 = Rune(runes.ElementAtOrDefault(2)), Rune4 = Rune(runes.ElementAtOrDefault(3)) });
        }
        // Native EquipSolution has three groups; empty slots carry empty messages, never null maincard.
        while (solution.Groups.Count < 3) solution.Groups.Add(EmptyGroup());
        return new PlayerBaseInfo { RoleId = player.Roleid, Name = player.Name, Level = (uint)Math.Max(1, player.Level),
            Solution = solution, Ready = false, IsInFormation = false };
    }

    internal static EquipSolution Unavailable(Player player)
    {
        var value = new EquipSolution { State = 1, Headframe = player.HeadFrame,
            Girl = (int)(player.Girls.FirstOrDefault()?.Girlid ?? 0) };
        for (int i = 0; i < 3; i++) value.Groups.Add(EmptyGroup());
        return value;
    }
    private static CardGroup EmptyGroup() => new() { Maincard = new CardInfo(), Weapon = new WeaponInfo(),
        Deputy1 = new CardInfo(), Deputy2 = new CardInfo(), Deputy3 = new CardInfo(),
        Rune1 = new RuneInfo(), Rune2 = new RuneInfo(), Rune3 = new RuneInfo(), Rune4 = new RuneInfo() };

    private static CardInfo Card(GameFeatureContext ctx, Item? item)
    {
        if (item is null) return new();
        var t = ctx.CharacterCards.Get(item.Genre, item.Detail, item.Particular, item.Level);
        uint Skill(int id) => id <= 0 ? 0 : CardSkills.Level(item, id);
        return new CardInfo { G = item.Genre, D = item.Detail, P = item.Particular, L = item.Level,
            Level = item.EnhanceLevel, Breaklevel = item.BreakLevel, Syncrate = item.SyncRate, Refitnum = item.RefitNum,
            Lv1 = Skill(t?.TacticalSkill ?? 0), Lv2 = Skill(t?.PassiveSkill1 ?? 0),
            Lv3 = Skill(t?.PassiveSkill2 ?? 0), Lv4 = Skill(t?.ResistanceSkill ?? 0) };
    }
    private static WeaponInfo Weapon(Item? item) => item is null ? new() : new WeaponInfo
    { G = item.Genre, D = item.Detail, P = item.Particular, L = item.Level, Level = item.EnhanceLevel,
        Breaklevel = item.BreakLevel, Lv1 = item.Skills.FirstOrDefault(s => s.SkillType == 1)?.SkillLevel ?? 1,
        Lv2 = item.Skills.FirstOrDefault(s => s.SkillType == 2)?.SkillLevel ?? 1 };
    private static RuneInfo Rune(Item? item)
    {
        if (item is null) return new();
        var rune = new RuneInfo { G = item.Genre, D = item.Detail, P = item.Particular, L = item.Level,
            Level = item.EnhanceLevel, Breaklevel = item.BreakLevel };
        foreach (var s in item.Skills) { rune.Skills.Add(s.SkillId); rune.Skilllevels.Add(s.SkillLevel); rune.SkillTypes.Add((uint)s.SkillType); }
        return rune;
    }
}
