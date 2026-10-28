using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Regional calendar cycles and durable local ranking settlement. The last
/// archived content row is replayed after its original calendar has ended;
/// calendar cycle IDs stay distinct so its results never leak into another week.
/// Rankings include only actually saved local participants, never synthetic rows.
/// </summary>
internal sealed class ChallengeSeasons
{
    internal const string Key = "challengeSeason";
    internal const string HistoryKey = "challengeHistory";
    private readonly PlayerStore _store;
    private readonly ChallengeTable _table;
    private readonly Func<long> _clock;
    private readonly List<(int Percent, List<int[]> Items)> _awards = new();
    internal sealed record RankRow(Player Player, int Score, int Rank);

    internal ChallengeSeasons(PlayerStore store, ChallengeTable table, string settings, Func<long>? clock = null)
    {
        _store = store;
        _table = table;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var awards = SettingTable.Load(Path.Combine(settings, "RankTower.txt"));
        if (awards is not null)
            foreach (var row in awards.Rows)
            {
                int percent = awards.Int(row, "RankIndex");
                if (percent is >= 1 and <= 100)
                    _awards.Add((percent, ChallengeRuns.ParseRewards(awards.Str(row, "AwardItem"))));
            }
        _awards.Sort((a, b) => a.Percent.CompareTo(b.Percent));
    }

    internal long Now => _clock();
    // All score commits and season finalization share this gate. Lock order is
    // player -> gate; ranking reads persisted snapshots and never locks another player.
    internal object Gate { get; } = new();
    internal ChallengeCycle Current => _table.CycleAt(Now);
    internal static int Number(JsonNode? node) => int.TryParse(node?.ToString(), out int value) ? value : 0;
    internal static bool Flag(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out bool flag) && flag;
    internal bool BelongsToCurrent(Player player) => player.Extra.GetValueOrDefault(Key) is JsonObject state
        && Number(state["cycle"]) == Current.Cycle;

    internal JsonObject ScheduleJson()
    {
        var s = Current;
        return new JsonObject { ["cycle"] = s.Cycle, ["content"] = s.Content, ["beginTime"] = s.Begin,
            ["endTime"] = s.End, ["clearTime"] = s.Clear, ["replay"] = s.Replay, ["day"] = ChallengeDaily.Day(Now) };
    }

    internal void Refresh(ClientSession session, Player player, bool notify = true)
    {
        var changed = new List<KeyValuePair<uint, int>>();
        var mails = new List<MailEntry>();
        Refresh(player, changed, mails);
        if (!notify) return;
        GameNotify.PushTaskValues(session, changed);
        foreach (var mail in mails) session.Push(GamePf.AddMailNtf, new AddMailNtf { Mail = MailFeature.BuildMailInfo(mail) });
    }

    // Caller sends these notifications only after this method has saved successfully.
    internal bool Refresh(Player player, List<KeyValuePair<uint, int>> changed, List<MailEntry> added)
    {
        lock (player)
        lock (Gate)
        {
            long now = Now;
            var cycle = _table.CycleAt(now);
            if (cycle.Cycle <= 0) return false;
            var before = new ChallengeMutationSnapshot(player);
            int oldChanged = changed.Count, oldAdded = added.Count;
            bool dirty = false;
            try
            {
                if (player.Extra.GetValueOrDefault(Key) is not JsonObject state)
                {
                    // Adopt unlabelled legacy progress once; do not erase it or
                    // manufacture rewards for unobserved historical seasons.
                    state = NewState(cycle);
                    player.Extra[Key] = state;
                    if (player.Extra.GetValueOrDefault(ChallengeRuns.AttemptKey) is JsonObject legacy)
                        legacy["cycle"] = cycle.Cycle;
                    dirty = true;
                }
                int previous = Number(state["cycle"]);
                if (previous > cycle.Cycle) return false; // Clock rollback cannot reset progress.
                if (previous < 1) throw new InvalidDataException("Invalid challenge calendar cycle");
                if ((previous < cycle.Cycle || now >= _table.CycleAt(now).End) && !Flag(state["settled"]))
                {
                    Settle(player, state, added, now);
                    dirty = true;
                }
                if (previous < cycle.Cycle)
                {
                    Archive(player, state);
                    foreach (uint task in player.Tasks.Keys.Where(IsChallengeTask).ToArray())
                    {
                        player.Tasks.Remove(task);
                        changed.Add(new(task, 0));
                    }
                    player.SetTask(9, ChallengeDaily.RemainingTask, _table.AttackTimes);
                    changed.Add(new(Player.ComposeTaskId(9, ChallengeDaily.RemainingTask), _table.AttackTimes));
                    player.Extra[ChallengeDaily.DayKey] = ChallengeDaily.Day(now);
                    player.Extra.Remove(ChallengeRuns.AttemptKey);
                    player.Extra[Key] = NewState(cycle);
                    dirty = true;
                }
                if (dirty) _store.SaveRequired(player);
                return dirty;
            }
            catch
            {
                before.Restore(player);
                changed.RemoveRange(oldChanged, changed.Count - oldChanged);
                added.RemoveRange(oldAdded, added.Count - oldAdded);
                throw;
            }
        }
    }

    private static JsonObject NewState(ChallengeCycle cycle) => new()
        { ["cycle"] = cycle.Cycle, ["content"] = cycle.Content, ["settled"] = false };

    private static bool IsChallengeTask(uint key) => key >> 16 == 9 && (key & 0xffff) is >= 45000 and < 48000;

    private void Settle(Player player, JsonObject state, List<MailEntry> added, long now)
    {
        int cycle = Number(state["cycle"]);
        int score = player.GetTask(9, ChallengeTable.TaskTotalScore);
        var ranking = Ranking(cycle, player);
        var own = ranking.FirstOrDefault(r => r.Player.Account == player.Account);
        state["score"] = score;
        state["settled"] = true;
        state["settledAt"] = now;
        if (own is not null)
        {
            int percent = (int)Math.Ceiling(100m * own.Rank / ranking.Count);
            var tier = _awards.FirstOrDefault(a => a.Percent >= percent);
            state["rank"] = own.Rank;
            state["participants"] = ranking.Count;
            state["percent"] = percent;
            if (tier.Items is { Count: > 0 })
            {
                var mails = MailFeature.Load(player);
                var mail = new MailEntry
                {
                    Id = MailFeature.NextId(mails), RecvTime = now, ExpiryTime = now + MailFeature.MailOutSeconds,
                    Title = "本地数据演习排名奖励", Sender = "战场处理者",
                    Content = $"本轮本地演习共{ranking.Count}位指挥官保留成绩；您的积分为{score}，排名第{own.Rank}，对应{tier.Percent}%档奖励。",
                    Items = tier.Items.Select(t => t.ToArray()).ToList(),
                };
                mails.Add(mail);
                MailFeature.Save(player, mails);
                state["mailId"] = mail.Id;
                state["rewardTier"] = tier.Percent;
                added.Add(mail);
            }
        }
        Archive(player, state);
    }

    private static void Archive(Player player, JsonObject state)
    {
        if (player.Extra.GetValueOrDefault(HistoryKey) is not JsonObject history)
            player.Extra[HistoryKey] = history = new JsonObject();
        var copy = (JsonObject)state.DeepClone();
        copy["score"] = player.GetTask(9, ChallengeTable.TaskTotalScore);
        var tasks = new JsonObject();
        foreach (var pair in player.Tasks.Where(p => IsChallengeTask(p.Key))) tasks[pair.Key.ToString()] = pair.Value;
        copy["tasks"] = tasks;
        history[Number(state["cycle"]).ToString()] = copy;
    }

    private static int ScoreOf(Player player, int cycle)
    {
        if (player.Extra.GetValueOrDefault(Key) is JsonObject current && Number(current["cycle"]) == cycle)
            return player.GetTask(9, ChallengeTable.TaskTotalScore);
        return player.Extra.GetValueOrDefault(HistoryKey) is JsonObject history
            && history[cycle.ToString()] is JsonObject archived ? Number(archived["score"]) : 0;
    }

    internal List<RankRow> Ranking(int cycle, Player? current = null)
    {
        var participants = new Dictionary<string, Player>(StringComparer.Ordinal);
        if (Directory.Exists(_store.DirectoryPath))
            foreach (string path in Directory.EnumerateFiles(_store.DirectoryPath, "*.player.json"))
            {
                string name = Path.GetFileName(path)[..^".player.json".Length];
                if (_store.Load(name) is { } saved) participants[saved.Account] = saved;
            }
        if (current is not null) participants[current.Account] = current;
        return participants.Values.Select(p => (Player: p, Score: ScoreOf(p, cycle)))
            .Where(p => p.Score > 0 && p.Player.Roleid > 0)
            .OrderByDescending(p => p.Score).ThenBy(p => p.Player.Roleid).ThenBy(p => p.Player.Account, StringComparer.Ordinal)
            .Select((p, index) => new RankRow(p.Player, p.Score, index + 1)).ToList();
    }

    internal JsonObject RankReply(Player player, int start = 0, int count = 50)
    {
        var rows = Ranking(Current.Cycle, player);
        var other = new JsonArray();
        foreach (var row in rows.Skip(Math.Max(0, start)).Take(Math.Clamp(count, 1, 100)))
            other.Add(new JsonArray(row.Rank, row.Player.Name, row.Player.Roleid.ToString(), 1,
                row.Player.Level.ToString(), row.Score, 0, row.Player.HeadFrame));
        return new JsonObject { ["isFirst"] = true, ["tbData"] = new JsonObject
        {
            ["tbId"] = new JsonArray(1, 0, 1), ["max"] = rows.Count,
            ["me"] = rows.FirstOrDefault(r => r.Player.Account == player.Account)?.Rank ?? 0, ["other"] = other,
        } };
    }
}
