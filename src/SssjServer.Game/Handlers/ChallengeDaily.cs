using System.Text.Json.Nodes;
namespace SssjServer.Game.Handlers;

internal static class ChallengeDaily
{
    internal const string DayKey = "challengeDailyDay";
    internal const int RemainingTask = 45900;
    // Server region UTC+8; the configured day starts at 04:00.
    internal static long Day(long now) => (now + 4 * 3600) / 86400;
    internal static bool Refresh(Player player, int limit, long now, List<KeyValuePair<uint, int>> changes)
    {
        long day = Day(now);
        bool known = long.TryParse(player.Extra.GetValueOrDefault(DayKey)?.ToString(), out long previous);
        if (known && previous >= day) return false;
        uint key = Player.ComposeTaskId(9, RemainingTask);
        bool exists = player.Tasks.ContainsKey(key);
        int next = known || !exists ? limit : Math.Clamp(player.GetTask(9, RemainingTask), 0, limit);
        if (!exists || next != player.GetTask(9, RemainingTask))
        {
            player.SetTask(9, RemainingTask, next);
            changes.Add(new(key, next));
        }
        player.Extra[DayKey] = JsonValue.Create(day);
        return true;
    }
}
