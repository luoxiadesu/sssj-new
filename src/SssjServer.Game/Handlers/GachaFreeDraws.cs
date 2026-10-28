using System.Text.Json.Nodes;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

internal static class GachaFreeDraws
{
    internal const string ExtraKey = "gachaFreeDays";
    // Chinese client UI_Gacha.UpdateFree counts down to local 04:00.
    internal static long Day(long now) => (long)Math.Floor((now + 8 * 3600d - 4 * 3600d) / 86400d);

    internal static bool Refresh(Player player, IEnumerable<GachaPool> pools, long now,
        List<KeyValuePair<uint, int>> tasks)
        => RefreshCounters(player, pools.Select(p => (p.Id, p.FreeType, p.FreeCount)), now,
            tasks, ExtraKey, GachaFeature.TaskFreeTime);

    internal static bool RefreshWeapons(Player player, IEnumerable<WeaponGachaPool> pools, long now,
        List<KeyValuePair<uint, int>> tasks)
        => RefreshCounters(player, pools.Select(p => (p.Id, p.FreeType, p.FreeCount)), now,
            tasks, "weaponGachaFreeDays", WeaponForgeFeature.TaskFreeTimeWeapon);

    private static bool RefreshCounters(Player player, IEnumerable<(int Id, int FreeType, int FreeCount)> pools,
        long now, List<KeyValuePair<uint, int>> tasks, string extraKey, int taskBegin)
    {
        var daily = pools.Where(p => p.FreeType == 1 && p.FreeCount > 0).ToArray();
        if (daily.Length == 0) return false;
        var days = player.Extra.GetValueOrDefault(extraKey) as JsonObject;
        bool changed = days is null;
        days ??= new JsonObject();
        long today = Day(now);
        foreach (var pool in daily)
        {
            string key = pool.Id.ToString();
            bool known = long.TryParse(days[key]?.ToString(), out long previous);
            // An old save with no day marker retains its consumed count on first
            // observation. Clock rollback cannot reset an already consumed day.
            if (known && previous < today)
            {
                int task = taskBegin + pool.Id;
                if (player.GetTask(15, task) != 0)
                {
                    player.SetTask(15, task, 0);
                    tasks.Add(new(Player.ComposeTaskId(15, task), 0));
                }
            }
            if (!known || previous < today) { days[key] = today; changed = true; }
        }
        player.Extra[extraKey] = days;
        return changed;
    }
}
