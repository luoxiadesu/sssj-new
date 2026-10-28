using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Evidence-bounded score calculation. Damage breakpoints come from the installed
/// Warzone table; interpolation/nearest-integer rounding and ordinary speed wins
/// follow the original author's BWIKI calculator (see the restoration report).
/// Ordinary survival uses elapsed time and health/shield state, with the entry
/// team-size correction. EX receives the native capped kill points, already halved
/// by the original Lua on failure. Integer conversion follows this local server's
/// shared nearest-integer policy; original server rounding is not in the client.
/// </summary>
internal sealed class ChallengeScores
{
    internal enum Status { Unavailable, Invalid, Available }
    internal sealed record Result(int Score, int Detail);
    private sealed record Level(int MaxScore, int Time, int Offset, int Extend);
    private readonly Dictionary<int, Level> _levels = new();
    private readonly Dictionary<int, int> _zones = new();
    private readonly Dictionary<int, int[]> _nodes = new();
    private readonly Dictionary<string, int[][]> _damage = new();
    private static readonly string[] Columns = ["PassDamage", "Damage60", "Damage80", "Damage90", "Damage95", "MaxDamage"];
    private static readonly decimal[] Rates = [0m, .5m, .6m, .8m, .9m, .95m, 1m];

    internal ChallengeScores(string directory)
    {
        var levels = SettingTable.Load(Path.Combine(directory, "ChallengeTowerLevel.txt"));
        if (levels is not null)
            foreach (var r in levels.Rows)
                _levels[levels.Int(r, "LevelId")] = new(levels.Int(r, "MaxScore"), levels.Int(r, "LevelTime"),
                    levels.Int(r, "CalcScoreTime"), levels.Int(r, "ExtendParam", 1));
        var nodes = SettingTable.Load(Path.Combine(directory, "ChallengeTowerNode.txt"));
        if (nodes is not null)
            foreach (var r in nodes.Rows)
            {
                int id = nodes.Int(r, "ChallengeTowerId");
                _nodes[id] = Numbers(nodes.Str(r, "ContainLevel"));
                _zones[id] = nodes.Int(r, "WarZone");
            }
        var zones = SettingTable.Load(Path.Combine(directory, "ChallengeTowerWarzone.txt"));
        var damage = zones?.Rows.FirstOrDefault(r => zones.Int(r, "Id") == 3);
        if (zones is not null && damage is not null)
            foreach (string name in Columns) _damage[name] = zones.Str(damage, name).Split(';').Select(Numbers).ToArray();
    }

    private static int[] Numbers(string value) => Regex.Matches(value, @"\d+").Select(m => int.Parse(m.Value)).ToArray();
    private static bool Read(JsonObject stats, string key, out decimal value)
    {
        value = 0;
        return stats[key] is JsonValue v && decimal.TryParse(v.ToJsonString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value) && value >= 0 && value <= decimal.MaxValue / 4;
    }
    private static int Round(decimal value) => checked((int)decimal.Floor(value + .5m));

    internal Status Evaluate(int node, int level, JsonObject stats, bool passed, out Result? result, int teamSize = 3)
    {
        result = null;
        if (!_levels.TryGetValue(level, out var config) || config.MaxScore <= 0 || config.Time <= 0
            || !_nodes.TryGetValue(node, out var levels)) return Status.Unavailable;
        int position = Array.IndexOf(levels, level);
        if (position < 0) return Status.Invalid;
        if (_zones.GetValueOrDefault(node) == 3)
        {
            if (!Read(stats, "nDamage", out decimal damage)) return Status.Invalid;
            var points = new List<decimal> { 0 };
            foreach (string column in Columns)
            {
                int type = config.Extend - 1;
                if (!_damage.TryGetValue(column, out var groups) || type < 0 || type >= groups.Length
                    || position >= groups[type].Length || groups[type][position] <= points[^1]) return Status.Unavailable;
                points.Add(groups[type][position]);
            }
            decimal rate = 1;
            for (int i = 1; i < points.Count; i++)
                if (damage < points[i])
                {
                    rate = Rates[i - 1] + (damage - points[i - 1]) / (points[i] - points[i - 1]) * (Rates[i] - Rates[i - 1]);
                    break;
                }
            result = new(Round(config.MaxScore * rate), (int)decimal.Min(damage, int.MaxValue));
            return Status.Available;
        }
        if (_zones.GetValueOrDefault(node) == 1 && passed && config.Offset == 0)
        {
            if (!stats.ContainsKey("leftTime")) return Status.Unavailable;
            if (!Read(stats, "leftTime", out decimal left) || left > config.Time) return Status.Invalid;
            result = new(Round(config.MaxScore * (.5m + left / config.Time / 2)),
                checked((int)decimal.Ceiling(config.Time - left)));
            return Status.Available;
        }
        if (_zones.GetValueOrDefault(node) == 1 && !passed && config.Offset == 0)
        {
            // The original client overwrites maxHp with TEAM HP. A dedicated
            // enemyMaxHp field is supplied by the companion patch; never divide
            // damage by the team's HP or award a speed bonus for an unfinished run.
            if (!stats.ContainsKey("enemyMaxHp") || !stats.ContainsKey("nDamage")) return Status.Unavailable;
            if (!Read(stats, "enemyMaxHp", out decimal maximum) || maximum <= 0
                || !Read(stats, "nDamage", out decimal damage)) return Status.Invalid;
            result = new(Round(config.MaxScore * (decimal.Min(damage, maximum) / maximum) / 2), 0);
            return Status.Available;
        }
        if (_zones.GetValueOrDefault(node) == 2 && config.Extend == 4 && config.Offset == 0)
        {
            // Activity.lua assigns LevelChallenge.MaxScore from this level. The
            // native SetChallengeMonPoint adds GetMonPoint(id), then Math.Mins
            // the accumulator with MaxScore. GetChallengeMonPoint returns that
            // integer unchanged. CalculationChallengeScore divides it by two
            // on failure BEFORE sending: never halve it a second time here.
            if (!stats.ContainsKey("monPoint")) return Status.Unavailable;
            if (!Read(stats, "monPoint", out decimal points)
                || points > (passed ? config.MaxScore : config.MaxScore / 2m)
                || (passed ? points : points * 2) % 1 != 0) return Status.Invalid;
            // EX has no ordinary survival-state detail (UI_LevelInfoNew hides it).
            result = new(Round(points), 0);
            return Status.Available;
        }
        if (_zones.GetValueOrDefault(node) == 2 && config.Extend != 4 && config.Offset == 0)
        {
            string[] required = ["leftTime", "leftHp", "leftShd", "maxHp", "mxShd"];
            if (required.Any(key => !stats.ContainsKey(key)) || teamSize is < 1 or > 3) return Status.Unavailable;
            if (!Read(stats, "leftTime", out decimal left) || left > config.Time
                || !Read(stats, "leftHp", out decimal hp) || !Read(stats, "leftShd", out decimal shield)
                || !Read(stats, "maxHp", out decimal maxHp) || !Read(stats, "mxShd", out decimal maxShield)
                || maxHp + maxShield <= 0) return Status.Invalid;
            decimal state = decimal.Min(hp + shield, maxHp + maxShield) / (maxHp + maxShield);
            decimal elapsed = (config.Time - left) / config.Time;
            result = new(Round(config.MaxScore * (elapsed + state) / 2 * teamSize / 3), Round(state * 1000));
            return Status.Available;
        }
        return Status.Unavailable;
    }
}
