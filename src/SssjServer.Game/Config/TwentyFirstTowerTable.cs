namespace SssjServer.Game.Config;

internal sealed record RestoredTowerLevel(int Id, int Type, int Cost, int MasterExp, int[] Previous,
    List<int[]> First, List<int[]> Fixed);
internal sealed record RestoredTowerSkill(int Id, int[] Item, int[] Costs);
internal sealed record RestoredTowerGoal(int Id, int Points);
internal sealed record RestoredTowerGoalReward(int Points, List<int[]> Rewards);

internal sealed class TwentyFirstTowerTable
{
    internal Dictionary<int, RestoredTowerLevel> Levels { get; } = new();
    internal Dictionary<int, RestoredTowerSkill> Skills { get; } = new();
    internal Dictionary<int, List<RestoredTowerGoal>> Goals { get; } = new();
    internal Dictionary<int, List<RestoredTowerGoalReward>> GoalRewards { get; } = new();
    internal static TwentyFirstTowerTable Load(string directory)
    {
        var result = new TwentyFirstTowerTable();
        var levels = SettingTable.Load(Path.Combine(directory, "TwentyFirstTowerLevel.txt"));
        if (levels is not null)
            foreach (var row in levels.Rows)
            {
                int id = levels.Int(row, "LevelId");
                if (levels.Int(row, "Id") != 21 || id <= 0) continue;
                result.Levels[id] = new(id, levels.Int(row, "LevelType"), levels.Int(row, "PreCost"),
                    levels.Int(row, "MasterExp"), Numbers(levels.Str(row, "PreLevelId")),
                    ChapterTable.ParseTuples(levels.Str(row, "FirstAward")),
                    ChapterTable.ParseTuples(levels.Str(row, "FixAward")));
            }
        var skills = SettingTable.Load(Path.Combine(directory, "TowerSkill.txt"));
        if (skills is not null)
            foreach (var row in skills.Rows)
            {
                int id = skills.Int(row, "Id");
                if (id > 0) result.Skills[id] = new(id, Numbers(skills.Str(row, "UpgradeItem")), Numbers(skills.Str(row, "UpgradeCost")));
            }
        var goals = SettingTable.Load(Path.Combine(directory, "TowerGoal.txt"));
        if (goals is not null)
            foreach (var row in goals.Rows)
            {
                if (goals.Int(row, "Id") != 21) continue;
                int level = goals.Int(row, "LevelId");
                if (!result.Goals.TryGetValue(level, out var list)) result.Goals[level] = list = new();
                list.Add(new(goals.Int(row, "GoalID"), goals.Int(row, "GoalPoint")));
            }
        var awards = SettingTable.Load(Path.Combine(directory, "TowerGoalReward.txt"));
        if (awards is not null)
            foreach (var row in awards.Rows)
            {
                if (awards.Int(row, "Id") != 21) continue;
                int level = awards.Int(row, "LevelId");
                if (!result.GoalRewards.TryGetValue(level, out var list)) result.GoalRewards[level] = list = new();
                list.Add(new(awards.Int(row, "GoalPoint"), ChapterTable.ParseTuples(awards.Str(row, "Reward"))));
            }
        return result;
    }
    internal static int[] Numbers(string text) => System.Text.RegularExpressions.Regex.Matches(text, @"\d+")
        .Select(m => int.Parse(m.Value)).ToArray();
}
