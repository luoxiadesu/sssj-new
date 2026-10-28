using System.Text.Json.Nodes;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Roll back the mutation domains used by challenge rewards and combat progress.
/// The caller holds the player lock and sends notifications only after persistence.
/// Retain existing item/girl instances: reward aggregation may refer to those objects.
/// </summary>
internal sealed class ChallengeMutationSnapshot
{
    private readonly Dictionary<int, int> _money;
    private readonly Dictionary<uint, int> _tasks;
    private readonly Dictionary<string, JsonNode?> _extra;
    private readonly (Item Item, uint Count)[] _items;
    private readonly GirlBefore[] _girls;
    private readonly uint _nextGuid;

    private sealed record GirlBefore(Girl Girl, uint Level, uint Exp, uint Model,
        uint Mood, uint Vigor, uint Flag);

    internal ChallengeMutationSnapshot(Player player)
    {
        _money = new(player.Money);
        _tasks = new(player.Tasks);
        _extra = player.Extra.ToDictionary(e => e.Key, e => e.Value?.DeepClone(), StringComparer.Ordinal);
        _items = player.Items.Select(i => (i, i.Count)).ToArray();
        _girls = player.Girls.Select(g => new GirlBefore(g, g.Level, g.Exp, g.Modelid,
            g.Moodvalue, g.Vigor, g.Flag)).ToArray();
        _nextGuid = player.NextGuid;
    }

    internal void Restore(Player player)
    {
        player.Money.Clear();
        foreach (var e in _money) player.Money.Add(e.Key, e.Value);
        player.Tasks.Clear();
        foreach (var e in _tasks) player.Tasks.Add(e.Key, e.Value);
        player.Extra.Clear();
        foreach (var e in _extra) player.Extra.Add(e.Key, e.Value);
        player.Items.Clear();
        foreach (var (item, count) in _items)
        {
            item.Count = count;
            player.Items.Add(item);
        }
        player.Girls.Clear();
        foreach (var saved in _girls)
        {
            var g = saved.Girl;
            g.Level = saved.Level;
            g.Exp = saved.Exp;
            g.Modelid = saved.Model;
            g.Moodvalue = saved.Mood;
            g.Vigor = saved.Vigor;
            g.Flag = saved.Flag;
            player.Girls.Add(g);
        }
        player.NextGuid = _nextGuid;
    }
}
