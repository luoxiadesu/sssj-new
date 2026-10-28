using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>GirlVigourManager.lua:41 and 225..264: rest recovery, active cafe consumption and group 41 clocks.</summary>
internal sealed class GirlMoodRecovery
{
    internal const string ExtraKey = "girlMoodRecovery";
    private static readonly ConditionalWeakTable<ClientSession, SessionMarker> Sessions = new();
    private sealed class SessionMarker { internal string Token { get; } = Guid.NewGuid().ToString("N"); }
    private static string Token(ClientSession session) => Sessions.GetValue(session, _ => new()).Token;
    internal static bool IsInCafe(ClientSession session, Player player)
        => (player.Extra.GetValueOrDefault(ExtraKey) as JsonObject)?["cafeSession"]?.ToString() == Token(session);
    private readonly CafeTable _cafe;
    private readonly SettingTable? _levels;
    private readonly SettingTable? _comfort;

    internal GirlMoodRecovery(string settings)
    {
        _cafe = CafeTable.Load(settings);
        _levels = SettingTable.Load(Path.Combine(settings, "NCafeLevels.txt"));
        _comfort = SettingTable.Load(Path.Combine(settings, "ComfortEff.txt"));
    }

    internal bool Enter(ClientSession session, Player player, long now)
    {
        bool changed = Refresh(session, player, now);
        var meta = (JsonObject)player.Extra[ExtraKey]!;
        if (meta["cafeSession"]?.ToString() == Token(session)) return changed;
        meta["cafeSession"] = Token(session);
        Refresh(session, player, now);
        return true;
    }

    internal bool Leave(ClientSession session, Player player, long now)
    {
        bool changed = Refresh(session, player, now);
        var meta = (JsonObject)player.Extra[ExtraKey]!;
        // A stale session must not close another session's cafe.
        if (meta["cafeSession"]?.ToString() != Token(session)) return changed;
        meta.Remove("cafeSession");
        Refresh(session, player, now);
        return true;
    }

    internal bool Refresh(ClientSession? session, Player player, long now, bool notify = true)
    {
        var state = CafeFeature.ReadState(player, _cafe);
        var content = CafeFeature.ReadContent(player, state, _cafe,
            CafeFeature.CafeDayIndex(DateTimeOffset.FromUnixTimeSeconds(now)));
        int speed = 0;
        var level = _levels?.Rows.FirstOrDefault(r => _levels.Int(r, "Level") == state.Level);
        if (level is not null) speed = _levels!.Int(level, "AddVigor");
        var comfort = _comfort?.Rows.FirstOrDefault(r => _comfort.Int(r, "Id") > 0
            && _comfort.Int(r, "Low") <= state.Comfort && _comfort.Int(r, "High") >= state.Comfort);
        if (comfort is not null) speed += _comfort!.Int(comfort, "AddVigor");
        var metadata = player.Extra.GetValueOrDefault(ExtraKey) as JsonObject;
        bool changed = metadata is null;
        metadata ??= new JsonObject();
        player.Extra[ExtraKey] = metadata;
        bool working = session is not null && metadata["cafeSession"]?.ToString() == Token(session)
            && (content.Queue.Count > 0 || content.Seats.Any(s => s.CustomerIdx is not null));
        int reduceSpeed = level is null ? 0 : Math.Max(0, _levels!.Int(level, "ReduceVigor"));
        var tasks = new List<KeyValuePair<uint, int>>();
        foreach (var girl in player.Girls)
        {
            int id = (int)girl.Girlid;
            int area = 0;
            for (int i = 0; i < content.AreaGirls.Count; i++)
                if (content.AreaGirls[i].Any(g => g.GirlId == id)) { area = i + 1; break; }
            bool resting = area is 2 or 3 && GirlTrainFeature.GetVar(player, girl.Girlid, GirlTrainFeature.VarTrainPos) <= 0;
            string signature = resting ? $"{area}:{speed}" : "paused";
            string key = id.ToString();
            long clock = player.GetTask(41, id);
            bool sameMode = metadata[key]?.ToString() == signature;
            uint oldMood = girl.Moodvalue;
            long next = clock;
            if (!sameMode || clock <= 0 || clock > now)
                next = resting && speed > 0 && girl.Moodvalue < 100 ? now : 0;
            else if (resting && speed > 0 && girl.Moodvalue < 100)
            {
                long points = Math.Min(100 - Math.Min(100u, girl.Moodvalue), (now - clock) * speed / 3600);
                if (points > 0)
                {
                    girl.Moodvalue += (uint)points;
                    next = girl.Moodvalue >= 100 ? 0 : clock + (points * 3600 + speed - 1) / speed;
                }
            }
            else next = 0;
            if (!sameMode) { metadata[key] = signature; changed = true; }
            int timestamp = (int)Math.Min(int.MaxValue, next);
            if (timestamp != clock)
            {
                player.SetTask(41, id, timestamp);
                tasks.Add(new(Player.ComposeTaskId(41, id), timestamp));
                changed = true;
            }
            // Client GirlVigourManager uses group 41, task 10000 + girlId for work.
            bool consuming = working && area == 1 && reduceSpeed > 0
                && GirlTrainFeature.GetVar(player, girl.Girlid, GirlTrainFeature.VarTrainPos) <= 0;
            string workKey = "work:" + key;
            string workMode = consuming ? $"{Token(session!)}:{reduceSpeed}" : "paused";
            long workClock = player.GetTask(41, 10000 + id);
            long workNext = workClock;
            if (metadata[workKey]?.ToString() != workMode || workClock <= 0 || workClock > now)
                workNext = consuming && girl.Moodvalue > 0 ? now : 0;
            else if (consuming && girl.Moodvalue > 0)
            {
                long points = Math.Min(girl.Moodvalue, (now - workClock) * reduceSpeed / 3600);
                if (points > 0)
                {
                    girl.Moodvalue -= (uint)points;
                    workNext = girl.Moodvalue == 0 ? 0 : workClock + (points * 3600 + reduceSpeed - 1) / reduceSpeed;
                }
            }
            else workNext = 0;
            if (metadata[workKey]?.ToString() != workMode) { metadata[workKey] = workMode; changed = true; }
            int workTime = (int)Math.Min(int.MaxValue, workNext);
            if (workTime != workClock)
            {
                player.SetTask(41, 10000 + id, workTime);
                tasks.Add(new(Player.ComposeTaskId(41, 10000 + id), workTime));
                changed = true;
            }
            if (oldMood != girl.Moodvalue)
            {
                if (session is not null && notify) GameNotify.PushGirl(session, girl);
                changed = true;
            }
        }
        if (session is not null && notify && tasks.Count > 0) GameNotify.PushTaskValues(session, tasks);
        return changed;
    }
}
