using System.Text.Json.Nodes;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>Rotating pools inherit ordinary pity/box counts; first-UP progress is per version.</summary>
internal static class GachaVersionState
{
    internal const string ExtraKey = "gachaVersionProgress";
    internal const int LegacyVersionTask = 10005;

    internal static bool Refresh(Player player, IEnumerable<GachaPool> pools,
        List<KeyValuePair<uint, int>> tasks)
    {
        bool changed = false;
        var root = player.Extra.GetValueOrDefault(ExtraKey) as JsonObject ?? new JsonObject();
        foreach (var pool in pools.Where(p => p.RotateVersion > 0))
        {
            string id = pool.Id.ToString();
            var state = root[id] as JsonObject;
            if (state is null) { state = new JsonObject(); root[id] = state; changed = true; }
            int upTask = GachaFeature.TaskProtectUpBegin + pool.Id;
            bool known = int.TryParse(state["version"]?.ToString(), out int previous) && previous > 0;
            if (!known)
            {
                // Original task 10005 describes the one original rotating family, ID43.
                // Unknown legacy saves adopt the current version without resetting progress.
                int legacy = pool.Id == 43 ? player.GetTask(15, LegacyVersionTask) : 0;
                previous = legacy > 0 ? legacy : pool.RotateVersion;
                state["version"] = previous;
                changed = true;
            }
            if (previous != pool.RotateVersion)
            {
                var history = state["progress"] as JsonObject;
                if (history is null) { history = new JsonObject(); state["progress"] = history; }
                history[previous.ToString()] = player.GetTask(15, upTask);
                int next = int.TryParse(history[pool.RotateVersion.ToString()]?.ToString(), out int remembered)
                    && remembered >= -1 ? remembered : 0;
                if (player.GetTask(15, upTask) != next)
                {
                    player.SetTask(15, upTask, next);
                    tasks.Add(new(Player.ComposeTaskId(15, upTask), next));
                }
                state["version"] = pool.RotateVersion;
                changed = true;
            }
            if (pool.Id == 43 && player.GetTask(15, LegacyVersionTask) != pool.RotateVersion)
            {
                player.SetTask(15, LegacyVersionTask, pool.RotateVersion);
                tasks.Add(new(Player.ComposeTaskId(15, LegacyVersionTask), pool.RotateVersion));
                changed = true;
            }
        }
        if (changed) player.Extra[ExtraKey] = root;
        return changed;
    }
}
