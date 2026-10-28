using SssjServer.Game.Handlers;

namespace SssjServer.Game.Config;

/// <summary>Riki.lua uses dedicated 1112/1114 messages, independently of card ownership group 7.</summary>
public sealed class RikiCardTable
{
    private readonly Dictionary<(uint G, uint D, uint P, uint L), List<uint>> _cards = new();
    private readonly HashSet<uint> _ids = new();
    public static RikiCardTable Load(string directory)
    {
        var result = new RikiCardTable();
        var table = SettingTable.Load(Path.Combine(directory, "Riki.txt"));
        if (table is null) return result;
        foreach (var row in table.Rows)
        {
            int id = table.Int(row, "Id");
            if (id <= 0 || id > ushort.MaxValue) continue;
            result._ids.Add((uint)id);
            if (table.Int(row, "Type") != 1 || table.Int(row, "G") != 1) continue;
            int d = table.Int(row, "D"), p = table.Int(row, "P"), l = table.Int(row, "L");
            if (d <= 0 || p <= 0 || l <= 0) continue;
            var key = (1u, (uint)d, (uint)p, (uint)l);
            if (!result._cards.TryGetValue(key, out var ids)) result._cards[key] = ids = new();
            if (!ids.Contains((uint)id)) ids.Add((uint)id);
        }
        return result;
    }

    internal IReadOnlyList<uint> MarkOwned(Player player, uint g, uint d, uint p, uint l)
    {
        var changed = new List<uint>();
        if (!_cards.TryGetValue((g, d, p, l), out var ids)) return changed;
        foreach (uint id in ids)
        {
            if (player.GetTask(53, (int)id) > 0) continue;
            player.SetTask(53, (int)id, 1);
            changed.Add(id);
        }
        return changed;
    }

    internal bool SyncOwned(Player player, CharacterCardTable templates)
    {
        bool changed = false;
        foreach (var key in _cards.Keys)
            if (templates.Get(key.G, key.D, key.P, key.L) is not null
                && CardCollection.HasOwned(player, key.G, key.D, key.P, key.L))
                changed |= MarkOwned(player, key.G, key.D, key.P, key.L).Count > 0;
        return changed;
    }

    internal IEnumerable<uint> OwnedIds(Player player)
        => _ids.Where(id => player.GetTask(53, (int)id) > 0).Order();
}
