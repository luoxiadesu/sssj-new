using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

internal static class CardRefitCollection
{
    // CardCommon.HasRefitMax: 30 bits per task, using girl*10000 + P*10 + L.
    internal static KeyValuePair<uint, int>? Mark(Player player, Item item, CardTable cards, CharacterCardTable templates)
    {
        if (item.Genre != 1 || item.Count == 0 || !GmUnlock.IsValidGirlId(item.Detail) ||
            templates.Get(item.Genre, item.Detail, item.Particular, item.Level) is null) return null;
        var row = cards.RefitOf((int)item.Genre, (int)item.Detail, (int)item.Particular, (int)item.Level);
        if (row is null || row.MaxRefit <= 0 || item.RefitNum != row.MaxRefit) return null;
        bool link = GirlLoveTable.IsLinkGirl(item.Detail);
        uint girl = link ? item.Detail - GirlLoveTable.LinkGirlId : item.Detail;
        long id = (long)girl * 10000 + item.Particular * 10L + item.Level;
        long key = id / 30;
        if (key > ushort.MaxValue) return null;
        int group = link ? 94 : 71, task = (int)key, mask = 1 << (int)(id % 30);
        int value = player.GetTask(group, task) | mask;
        if (value == player.GetTask(group, task)) return null;
        player.SetTask(group, task, value);
        return new(Player.ComposeTaskId(group, task), value);
    }
}
