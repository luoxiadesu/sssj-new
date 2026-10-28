using System.Text.Json.Nodes;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// PldgeTask timing counts are minutes (GirlPromise.GetTaskStep multiplies by 60).
/// Count only observed assignment intervals in the active stage, retaining the
/// sub-minute remainder across assignments and restarts. Never backfill a legacy
/// assignment from an unrelated registration, mood, or login timestamp.
/// </summary>
internal sealed class PledgeTimeProgress
{
    internal const string ExtraKey = "pledgeTimers";
    private readonly PledgeTable _table;
    private readonly CafeTable _cafe;
    internal PledgeTimeProgress(PledgeTable table, CafeTable cafe) { _table = table; _cafe = cafe; }
    private static long Number(JsonNode? value) => long.TryParse(value?.ToString(), out long n) ? n : 0;
    private static bool Flag(JsonNode? value) => value is JsonValue v && v.TryGetValue<bool>(out bool b) && b;

    internal bool SaveRefresh(PlayerStore store, Player player, long now)
    {
        lock (player)
        {
            var before = new ChallengeMutationSnapshot(player);
            try
            {
                bool changed = Refresh(player, now);
                if (changed) store.SaveRequired(player);
                return changed;
            }
            catch { before.Restore(player); throw; }
        }
    }

    // Call before changing an assignment, and once more at the SAME now after it.
    // The caller persists both the assignment and timer changes in its transaction.
    internal bool Refresh(Player player, long now)
    {
        if (now <= 0) return false;
        lock (player)
        {
            var pledge = PledgeFeature.LoadStateStatic(player);
            var cafe = CafeFeature.ReadState(player, _cafe);
            var content = CafeFeature.ReadContent(player, cafe, _cafe,
                CafeFeature.CafeDayIndex(DateTimeOffset.FromUnixTimeSeconds(now)));
            int main = player.GetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId);
            var old = player.Extra.GetValueOrDefault(ExtraKey) as JsonObject;
            var oldRecords = old?["records"] as JsonObject;
            var records = new JsonObject();
            bool countsChanged = false;
            foreach (var girl in pledge.Girls.Values.OrderBy(g => g.GirlId))
            {
                if (_table.IsSimple(girl.GirlId)) continue;
                var stage = _table.Step(girl.GirlId, girl.Step);
                if (stage is null) continue;
                bool owned = player.GetGirl((uint)girl.GirlId) is not null;
                bool training = GirlTrainFeature.GetVar(player, (uint)girl.GirlId, GirlTrainFeature.VarTrainPos) > 0;
                bool In(int area) => content.AreaGirls.Count > area && content.AreaGirls[area].Any(g => g.GirlId == girl.GirlId);
                foreach (var task in girl.Tasks.OrderBy(t => t.Id))
                {
                    var cfg = _table.Task(task.Id);
                    if (task.Status == PledgeFeature.TaskStatusDone || cfg is null || cfg.GirlId != girl.GirlId
                        || !stage.TaskList.Contains(task.Id) || cfg.Count <= 0 || task.Count >= cfg.Count
                        || cfg.StrType is not ("GreeterTime" or "CafeTime" or "RestTime")) continue;
                    bool eligible = owned && (cfg.StrType switch
                    {
                        "GreeterTime" => main == girl.GirlId,
                        "CafeTime" => !training && In(CafeFeature.AreaIndexCafe),
                        "RestTime" => !training && (In(CafeFeature.AreaIndexRest) || In(CafeFeature.AreaIndexDorm)),
                        _ => false,
                    });
                    string key = $"{girl.GirlId}:{girl.Step}:{task.Id}";
                    var previous = oldRecords?[key] as JsonObject;
                    long clock = Number(previous?["clock"]);
                    long remainder = Math.Clamp(Number(previous?["seconds"]), 0, 59);
                    bool wasEligible = Flag(previous?["eligible"]);
                    long nextClock = Math.Max(now, clock);
                    if (previous is not null && clock > 0 && now >= clock && eligible && wasEligible)
                    {
                        long seconds = checked(now - clock + remainder);
                        long increment = Math.Min((long)cfg.Count - Math.Max(0, task.Count), seconds / 60);
                        if (increment > 0)
                        {
                            task.Count = checked(Math.Max(0, task.Count) + (int)increment);
                            countsChanged = true;
                        }
                        remainder = task.Count >= cfg.Count ? 0 : seconds % 60;
                    }
                    else if (previous is not null && !eligible && !wasEligible) nextClock = clock > 0 ? clock : now;
                    if (task.Count < cfg.Count)
                        records[key] = new JsonObject { ["clock"] = nextClock, ["seconds"] = remainder, ["eligible"] = eligible };
                }
            }
            if (countsChanged) player.Extra[PledgeFeature.ExtraKey] = pledge.ToJson();
            if (records.Count == 0) return player.Extra.Remove(ExtraKey) || countsChanged;
            var next = new JsonObject { ["records"] = records };
            bool metadataChanged = next.ToJsonString() != old?.ToJsonString();
            if (metadataChanged) player.Extra[ExtraKey] = next;
            return countsChanged || metadataChanged;
        }
    }
}
