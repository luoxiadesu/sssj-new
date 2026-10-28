using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>Verified challenge progression and first-clear rewards; scoring remains separate.</summary>
internal sealed class ChallengeRuns
{
    internal const string AttemptKey = "challengeRun";
    private readonly GameFeatureContext _ctx;
    private readonly ChallengeTable _weeks;
    private readonly PledgeTable _pledge;
    private readonly ChallengeScores _scores;
    private readonly ChallengeSeasons _seasons;
    private readonly Dictionary<int, int[]> _nodes = new();
    private readonly Dictionary<int, int> _zones = new();
    private readonly Dictionary<int, int[]> _passDamage = new();
    private readonly Dictionary<int, Level> _levels = new();
    private sealed record Level(int[] Previous, int Cost, int Extend, List<int[]> First);

    internal ChallengeRuns(GameFeatureContext ctx, ChallengeTable weeks, ChallengeSeasons? seasons = null)
    {
        _ctx = ctx;
        _weeks = weeks;
        _scores = new ChallengeScores(Path.GetFullPath(ctx.Options.SettingDirectory));
        _seasons = seasons ?? new(ctx.Store, weeks, Path.GetFullPath(ctx.Options.SettingDirectory));
        _pledge = PledgeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        var nodes = SettingTable.Load(Path.Combine(ctx.Options.SettingDirectory, "ChallengeTowerNode.txt"));
        if (nodes is not null)
            foreach (var row in nodes.Rows)
            {
                _nodes[nodes.Int(row, "ChallengeTowerId")] = Numbers(nodes.Str(row, "ContainLevel"));
                _zones[nodes.Int(row, "ChallengeTowerId")] = nodes.Int(row, "WarZone");
            }
        var zones = SettingTable.Load(Path.Combine(ctx.Options.SettingDirectory, "ChallengeTowerWarzone.txt"));
        var damageZone = zones?.Rows.FirstOrDefault(row => zones.Int(row, "Id") == 3);
        if (zones is not null && damageZone is not null)
        {
            var thresholds = zones.Str(damageZone, "PassDamage").Split(';');
            for (int i = 0; i < thresholds.Length; i++) _passDamage[i + 1] = Numbers(thresholds[i]);
        }
        var levels = SettingTable.Load(Path.Combine(ctx.Options.SettingDirectory, "ChallengeTowerLevel.txt"));
        if (levels is not null)
            foreach (var row in levels.Rows)
                _levels[levels.Int(row, "LevelId")] = new(Numbers(levels.Str(row, "PreLevelId")),
                    levels.Int(row, "PreCost"), levels.Int(row, "ExtendParam", 1), ParseRewards(levels.Str(row, "FirstAward")));
    }

    private static int[] Numbers(string value) => Regex.Matches(value, @"\d+")
        .Select(m => int.Parse(m.Value)).ToArray();
    // This table groups multiple GDPLN tuples inside one bracket: [a-b-c-d-n,a-b-c-d-n].
    internal static List<int[]> ParseRewards(string value) => Regex.Matches(value, @"\[([^\[\]]+)\]")
        .SelectMany(m => m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        .Where(tuple => Regex.IsMatch(tuple.Trim(), @"^[1-9]\d*(?:-[1-9]\d*){4}$"))
        .SelectMany(tuple => ChapterTable.ParseTuples("[" + tuple + "]"))
        .Where(tuple => tuple.Length == 5 && tuple.All(n => n > 0)).ToList();
    private static int Number(JsonArray args, int index) => index < args.Count
        && args[index] is JsonValue v && v.TryGetValue<int>(out int n) ? n : -1;
    private static int SavedNumber(JsonObject saved, string key) => saved[key] is JsonValue v
        && v.TryGetValue<int>(out int n) ? n : -1;
    private static string? SavedToken(JsonObject saved) => saved["battleToken"] is JsonValue value
        && value.TryGetValue<string>(out string? token) && Guid.TryParseExact(token, "N", out _) ? token : null;
    internal static string? TokenFromSettlement(JsonNode? value) => value is JsonArray { Count: >= 6 } args
        && args[5] is JsonObject extra ? SavedToken(extra) : null;

    private bool Resolve(JsonNode? value, out int week, out int node, out int level, out int index)
    {
        week = node = level = index = 0;
        if (value is not JsonArray { Count: >= 5 } args || Number(args, 0) != 6) return false;
        week = Number(args, 1); node = Number(args, 2); level = Number(args, 3);
        if (week != _seasons.Current.Content
            || _weeks.WeekOf(week) is not { } config || !_nodes.TryGetValue(node, out var contained)
            || !_levels.ContainsKey(level) || Number(args, 4) <= 0) return false;
        int offset = config.NodeIds.ToList().IndexOf(node);
        int position = Array.IndexOf(contained, level);
        if (offset < 0 || position < 0) return false;
        index = (offset + 1) * 100 + position + 1;
        return true;
    }

    internal Task Enter(ClientSession session, int cmd, JsonNode? value)
    {
        try { return EnterCore(session, cmd, value); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // PersistRunChange has restored the old state; do not leave the client waiting.
            return Error(session, cmd);
        }
    }

    private Task EnterCore(ClientSession session, int cmd, JsonNode? value)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            if (!Resolve(value, out int week, out int node, out int level, out int index)
                || _levels[level].Cost != 0) return Error(session, cmd);
            // SystemUnlock.UI_UnlockChallengeTower: normal 5-12. Use native map encoding.
            if (!player.Levels.TryGetValue(MapList.MapId(5, 12, 1), out int cleared)
                || cleared <= 0) return Error(session, cmd);
            int formationId = Number((JsonArray)value!, 4);
            var formation = player.Formations.FirstOrDefault(f => f.Id == formationId);
            var mainCards = ChapterHandler.SnapshotMainCards(player,
                new JsonObject { ["nFormationId"] = formationId }, out bool validFormation);
            if (!validFormation || formation is null || formation.Slots.Count > StarterLoadout.MaxFormationSlots)
                return Error(session, cmd);
            _seasons.Refresh(session, player);
            if (!_seasons.Current.IsOpen(_seasons.Now) || !_seasons.BelongsToCurrent(player)) return Error(session, cmd);
            foreach (int previous in _levels[level].Previous.Where(p => p > 0))
            {
                int slot = Array.IndexOf(_nodes[node], previous);
                if (slot < 0 || player.GetTask(ChallengeTable.Group,
                        ChallengeTable.TaskLevelProcessBase + index / 100 * 100 + slot + 1) <= 0)
                    return Error(session, cmd);
            }
            if (player.Extra.GetValueOrDefault(AttemptKey) is JsonObject existing
                && existing["active"] is JsonValue activeValue && activeValue.TryGetValue<bool>(out bool active) && active)
            {
                if (SavedNumber(existing, "week") != week || SavedNumber(existing, "node") != node
                    || SavedNumber(existing, "level") != level || SavedNumber(existing, "formation") != formationId)
                    return Error(session, cmd);
                string token = SavedToken(existing) ?? Guid.NewGuid().ToString("N");
                if (SavedToken(existing) is null)
                {
                    PersistRunChange(player, () => existing["battleToken"] = token);
                }
                _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["tbData"] = new JsonArray(6, week, node, level), ["battleToken"] = token });
                return Task.CompletedTask;
            }
            if (player.Extra.GetValueOrDefault(AttemptKey) is JsonObject pending
                && SavedNumber(pending, "week") == week
                && pending["pendingScore"] is JsonObject && pending["scoreChoice"] is null)
                return Error(session, cmd);
            var changed = new List<KeyValuePair<uint, int>>();
            string? battleToken = null;
            PersistRunChange(player, () =>
            {
                ChallengeDaily.Refresh(player, _weeks.AttackTimes, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), changed);
                int remaining = player.GetTask(9, ChallengeDaily.RemainingTask);
                if (remaining <= 0)
                {
                    return;
                }
                battleToken = Guid.NewGuid().ToString("N");
                player.Extra[AttemptKey] = new JsonObject
                {
                    ["battleToken"] = battleToken,
                    ["cycle"] = _seasons.Current.Cycle,
                    ["week"] = week, ["node"] = node, ["level"] = level,
                    ["formation"] = Number((JsonArray)value!, 4), ["active"] = true,
                    ["quotaAtSave"] = true,
                    ["mainCards"] = new JsonArray(mainCards.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()),
                    ["cardIdentities"] = JsonSerializer.SerializeToNode(mainCards.Select(g=>BattleMainCard.From(player.Items.First(i=>i.Guid==g))).ToArray()),
                    ["pledgeSteps"] = JsonSerializer.SerializeToNode(PledgeFeature.LoadStateStatic(player).Girls.ToDictionary(g=>g.Key,g=>g.Value.Step)),
                };
            });
            GameNotify.PushTaskValues(session, changed);
            if (battleToken is null) return Error(session, cmd);
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["tbData"] = new JsonArray(6, week, node, level), ["battleToken"] = battleToken });
        }
        return Task.CompletedTask;
    }

    internal Task Settle(ClientSession session, int cmd, JsonNode? value, bool passed)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        string? token = TokenFromSettlement(value);
        Task Fail() => Error(session, cmd, token);
        lock (player)
        {
            try { _seasons.Refresh(session, player); }
            catch (Exception) { return Fail(); }
            if (token is null || !Resolve(value, out int week, out int node, out int level, out int index)
                || !_seasons.Current.IsOpen(_seasons.Now) || !_seasons.BelongsToCurrent(player)
                || player.Extra.GetValueOrDefault(AttemptKey) is not JsonObject attempt
                || token != SavedToken(attempt)
                || SavedNumber(attempt, "cycle") != _seasons.Current.Cycle
                || SavedNumber(attempt, "week") != week || SavedNumber(attempt, "node") != node
                || SavedNumber(attempt, "level") != level
                || SavedNumber(attempt, "formation") != Number((JsonArray)value!, 4)
                || attempt["active"] is not JsonValue state || !state.TryGetValue<bool>(out bool active)) return Fail();
            if (!active)
            {
                if (attempt["reply"] is not JsonObject saved || saved["isPass"] is not JsonValue status
                    || !status.TryGetValue<bool>(out bool savedPass) || savedPass != passed)
                    return Fail();
                _ctx.LuaCalls.CallClient(session, cmd, saved.DeepClone());
                return Task.CompletedTask;
            }
            var rewards = new RewardGrantResult();
            var changed = new List<KeyValuePair<uint, int>>();
            int key = ChallengeTable.TaskLevelProcessBase + index;
            bool cleared = passed;
            if (_zones.GetValueOrDefault(node) == 3)
            {
                int position = Array.IndexOf(_nodes[node], level);
                if (!_passDamage.TryGetValue(_levels[level].Extend, out var thresholds)
                    || position < 0 || position >= thresholds.Length || thresholds[position] <= 0
                    || ((JsonArray)value!)[5] is not JsonObject stats
                    || stats["nDamage"] is not JsonValue damageValue
                    || !damageValue.TryGetValue<double>(out double damage) || !double.IsFinite(damage) || damage < 0)
                    return Fail();
                cleared = damage >= thresholds[position];
            }
            int teamSize = (attempt["mainCards"] as JsonArray)?.Count ?? 0;
            var scoreStatus = _scores.Evaluate(node, level, (JsonObject)((JsonArray)value!)[5]!, passed, out var score, teamSize);
            if (scoreStatus == ChallengeScores.Status.Invalid) return Fail();
            var before = new ChallengeMutationSnapshot(player);
            JsonObject reply;
            try
            {
                if (cleared && player.GetTask(ChallengeTable.Group, key) <= 0)
                {
                    _ctx.Rewards.Grant(player, _levels[level].First, RewardSource.FirstAward, rewards);
                    player.SetTask(ChallengeTable.Group, key, 1);
                    changed.Add(new(Player.ComposeTaskId(ChallengeTable.Group, key), 1));
                }
                // Damage-only unlocks with isPass=false are not victories for generic missions.
                if(passed)
                {
                    BattleMainCard[] cards=Array.Empty<BattleMainCard>();
                    Dictionary<int,int> steps=new();
                    try
                    {
                        cards=attempt["cardIdentities"]?.Deserialize<BattleMainCard[]>() ?? cards;
                        steps=attempt["pledgeSteps"]?.Deserialize<Dictionary<int,int>>() ?? steps;
                    }
                    catch(JsonException) { cards=Array.Empty<BattleMainCard>();steps=new(); }
                    changed.AddRange(ChapterCombatProgress.RecordTowerVictory(_ctx.Missions,_pledge,player,cards,steps));
                }
                var data = new JsonArray();
                foreach (var reward in rewards.ToAwardTuples())
                    data.Add(new JsonArray(reward.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()));
                reply = new JsonObject { ["tbData"] = data, ["isPass"] = passed, ["battleToken"] = token };
                // Older active runs already spent a quota at entry. Do not migrate those
                // into the new save-quota flow or charge/refund historical data implicitly.
                bool available = scoreStatus == ChallengeScores.Status.Available
                    && attempt["quotaAtSave"]?.GetValue<bool>() == true;
                int previousScore = player.GetTask(9, ChallengeTable.TaskLevelScoreBase + index);
                int totalScore = player.GetTask(9, ChallengeTable.TaskTotalScore);
                reply["scoreAvailable"] = available;
                reply["isBest"] = available && score!.Score > previousScore;
                reply["SingleScore"] = available ? score!.Score : 0;
                reply["nGetScore"] = previousScore; // Client labels this historical best.
                reply["TotalScore"] = totalScore;
                reply["NextGoal"] = _weeks.Rewards.FirstOrDefault(r => r.Goal > totalScore)?.Goal ?? 9999999999L;
                if (available && score!.Score > previousScore)
                    attempt["pendingScore"] = new JsonObject { ["index"] = index, ["score"] = score.Score, ["detail"] = score.Detail };
                attempt["active"] = false;
                attempt["reply"] = reply.DeepClone();
                _ctx.Store.SaveRequired(player);
            }
            catch (Exception e)
            {
                before.Restore(player);
                Log.Warn($"[session {session.Id}] Activities_PassTower(55) 未保存，场次、奖励与任务进度已回滚：{e.Message}");
                return Fail();
            }
            GameNotify.PushRewards(session, player, rewards);
            GameNotify.PushTaskValues(session, changed);
            _ctx.LuaCalls.CallClient(session, cmd, reply);
        }
        return Task.CompletedTask;
    }

    internal Task Cancel(ClientSession session, string function, JsonNode? value)
    {
        if (session.Tag is not Player player || value is not JsonObject request) return Task.CompletedTask;
        lock (player)
        {
            if (SavedToken(request) is not { } token || player.Extra.GetValueOrDefault(AttemptKey) is not JsonObject attempt
                || token != SavedToken(attempt) || attempt["active"] is not JsonValue active
                || !active.TryGetValue<bool>(out bool running) || !running) return Task.CompletedTask;
            PersistRunChange(player, () =>
            {
                attempt["active"] = false;
                attempt["cancelled"] = true;
            });
        }
        return Task.CompletedTask;
    }

    internal Task ExitSave(ClientSession session, int cmd, JsonNode? value)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        string? token = value is JsonObject request ? SavedToken(request) : null;
        Task Fail() => Error(session, cmd, token);
        if (value is not JsonObject args || token is null || args["keep"] is not JsonValue choice
            || !choice.TryGetValue<bool>(out bool keep)) return Fail();
        lock (player)
        lock (_seasons.Gate)
        {
            try { _seasons.Refresh(session, player); }
            catch (Exception) { return Fail(); }
            if (player.Extra.GetValueOrDefault(AttemptKey) is not JsonObject run || SavedToken(run) != token
                || run["active"] is not JsonValue active || !active.TryGetValue<bool>(out bool running) || running
                || run["reply"] is not JsonObject
                || SavedNumber(run, "week") != _seasons.Current.Content
                || SavedNumber(run, "cycle") != _seasons.Current.Cycle
                || !_seasons.BelongsToCurrent(player) || (keep && !_seasons.Current.IsOpen(_seasons.Now))) return Fail();
            if (run["scoreChoice"] is JsonValue savedChoice && savedChoice.TryGetValue<bool>(out bool saved))
            {
                if (keep != saved || run["scoreReply"] is not JsonObject receipt) return Fail();
                _ctx.LuaCalls.CallClient(session, cmd, receipt.DeepClone());
                return Task.CompletedTask;
            }
            if (keep && run["pendingScore"] is not JsonObject) return Fail();
            var before = new ChallengeMutationSnapshot(player);
            var changed = new List<KeyValuePair<uint, int>>();
            var reply = new JsonObject { ["battleToken"] = token, ["keep"] = keep };
            try
            {
                if (keep)
                {
                    var candidate = (JsonObject)run["pendingScore"]!;
                    int index = SavedNumber(candidate, "index"), score = SavedNumber(candidate, "score");
                    int previous = player.GetTask(9, ChallengeTable.TaskLevelScoreBase + index);
                    if (index <= 0 || score <= previous) throw new InvalidOperationException("No improved score to retain");
                    ChallengeDaily.Refresh(player, _weeks.AttackTimes, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), changed);
                    int remaining = player.GetTask(9, ChallengeDaily.RemainingTask);
                    if (remaining <= 0) throw new InvalidOperationException("No score-save quota remains");
                    int total = checked(player.GetTask(9, ChallengeTable.TaskTotalScore) + score - previous);
                    void Set(int task, int n)
                    {
                        player.SetTask(9, task, n);
                        changed.Add(new(Player.ComposeTaskId(9, task), n));
                    }
                    Set(ChallengeDaily.RemainingTask, remaining - 1);
                    Set(ChallengeTable.TaskLevelScoreBase + index, score);
                    Set(47000 + index, SavedNumber(candidate, "detail"));
                    Set(ChallengeTable.TaskTotalScore, total);
                }
                run["scoreChoice"] = keep;
                run["scoreReply"] = reply.DeepClone();
                _ctx.Store.SaveRequired(player);
            }
            catch (Exception error)
            {
                before.Restore(player);
                Log.Warn($"[session {session.Id}] Challenge_Exit_Save(207) 未保存，成绩和次数已回滚：{error.Message}");
                return Fail();
            }
            GameNotify.PushTaskValues(session, changed);
            if (keep) _ctx.LuaCalls.CallClientFunction(session, "ServerRankMsg", _seasons.RankReply(player));
            _ctx.LuaCalls.CallClient(session, cmd, reply);
        }
        return Task.CompletedTask;
    }

    internal JsonObject? PendingScore(Player player)
    {
        lock (player)
        {
            if (player.Extra.GetValueOrDefault(AttemptKey) is not JsonObject run
                || SavedNumber(run, "week") != _seasons.Current.Content
                || SavedNumber(run, "cycle") != _seasons.Current.Cycle
                || !_seasons.Current.IsOpen(_seasons.Now)
                || run["active"]?.GetValue<bool>() != false || run["scoreChoice"] is not null
                || run["pendingScore"] is not JsonObject || run["reply"] is not JsonObject reply) return null;
            return (JsonObject)reply.DeepClone();
        }
    }

    // Entry and cancellation only change these three fields. Restore absent keys as absent,
    // so a failed first entry cannot initialize a day or consume an attempt in memory.
    private void PersistRunChange(Player player, Action mutate)
    {
        uint remainingKey = Player.ComposeTaskId(9, ChallengeDaily.RemainingTask);
        bool hadRemaining = player.Tasks.TryGetValue(remainingKey, out int remaining);
        bool hadDay = player.Extra.TryGetValue(ChallengeDaily.DayKey, out var day);
        bool hadAttempt = player.Extra.TryGetValue(AttemptKey, out var attempt);
        day = day?.DeepClone();
        attempt = attempt?.DeepClone();
        try
        {
            mutate();
            _ctx.Store.SaveRequired(player);
        }
        catch
        {
            if (hadRemaining) player.Tasks[remainingKey] = remaining;
            else player.Tasks.Remove(remainingKey);
            if (hadDay) player.Extra[ChallengeDaily.DayKey] = day;
            else player.Extra.Remove(ChallengeDaily.DayKey);
            if (hadAttempt) player.Extra[AttemptKey] = attempt;
            else player.Extra.Remove(AttemptKey);
            throw;
        }
    }

    private Task Error(ClientSession session, int cmd, string? token = null)
    {
        var reply = new JsonObject { ["nError"] = ChallengeTowerFeature.ErrGeneric };
        if (token is not null) reply["battleToken"] = token;
        _ctx.LuaCalls.CallClient(session, cmd, reply);
        return Task.CompletedTask;
    }
}
