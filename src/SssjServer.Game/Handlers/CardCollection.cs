using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

internal static class CardCollection
{
    internal static bool TryKey(uint girl, uint particular, uint level, out int group, out int task)
    {
        group = GirlLoveTable.IsLinkGirl(girl) ? 92 : 7;
        uint local = GirlLoveTable.IsLinkGirl(girl) ? girl - GirlLoveTable.LinkGirlId : girl;
        long offset = (long)particular * 20 + level;
        task = 0;
        if (!GmUnlock.IsValidGirlId(girl) || offset is <= 0 or >= 2000) return false;
        task = (int)((local - 1) * 2000 + offset);
        return task <= ushort.MaxValue;
    }

    internal static bool HasOwned(Player player, uint g, uint d, uint p, uint l)
        => g == 1 && (TryKey(d, p, l, out int group, out int task) && player.GetTask(group, task) > 0
            || player.Items.Any(i => i.Count > 0 && i.Genre == g && i.Detail == d && i.Particular == p && i.Level == l));

    internal static KeyValuePair<uint, int>? MarkOwned(Player player, CharacterCardTable table, uint g, uint d, uint p, uint l)
    {
        if (g != 1 || table.Get(g, d, p, l) is null || !TryKey(d, p, l, out int group, out int task)
            || player.GetTask(group, task) > 0) return null;
        player.SetTask(group, task, 1);
        return new(Player.ComposeTaskId(group, task), 1);
    }

    internal static bool SyncOwned(Player player, CharacterCardTable table)
    {
        bool changed = false;
        foreach (var item in player.Items.Where(i => i.Genre == 1 && i.Count > 0))
            changed |= MarkOwned(player, table, item.Genre, item.Detail, item.Particular, item.Level).HasValue;
        return changed;
    }
}
