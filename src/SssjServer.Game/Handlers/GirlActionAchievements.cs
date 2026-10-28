using SssjServer.Game.Config;
namespace SssjServer.Game.Handlers;

internal static class GirlActionAchievements
{
    // null trainingId denotes a successful head touch, otherwise a completed training.
    internal static List<KeyValuePair<uint, int>> Record(MissionTable missions, Player player, uint girl, int? trainingId)
    {
        bool Matches(MissionEntry m)
        {
            var p = m.Counts;
            bool match = trainingId is null ? m.AchieveType switch
            {
                15 => true,
                51 or 175 => p.Count >= 2 && p[1] > 0 && girl == (uint)p[1],
                237 => p.Count > 1 && p.Skip(1).Any(g => g > 0 && girl == (uint)g),
                _ => false,
            } : m.AchieveType switch
            {
                29 => true,
                66 or 174 => p.Count >= 2 && p[1] > 0 && girl == (uint)p[1],
                148 => p.Count >= 3 && p[0] > 0 && girl == (uint)p[0] && trainingId == p[1],
                234 => p.Count > 1 && p.Skip(1).Any(g => g > 0 && girl == (uint)g),
                _ => false,
            };
            if (!match || m.RequiredCount(true) <= 0) return false;
            if (m.AchieveType is not (174 or 175)) return true;
            var pre = missions.GetAchieveOther(m.PreId);
            return pre is not null && pre.RequiredCount(true) > 0 &&
                Math.Max(MissionFeature.ProgressOf(player.GetTask(pre.Group, pre.Id)),
                    MissionFeature.DerivedProgress(player, pre)) >= pre.RequiredCount(true);
        }
        // Snapshot eligibility before mutation, so completing a predecessor with this
        // action never also counts the same action toward its gated successor.
        var eligible = missions.Achieve.Values.Concat(missions.AchieveOther.Values).Where(Matches).ToArray();
        var changes = new List<KeyValuePair<uint, int>>();
        foreach (var mission in eligible)
        {
            int old = player.GetTask(mission.Group, mission.Id);
            int next = MissionFeature.ComposeTaskValue(
                (int)Math.Min(0x3FFFFFFFL, (long)MissionFeature.ProgressOf(old) + 1), MissionFeature.ClaimedOf(old));
            if (next == old) continue;
            player.SetTask(mission.Group, mission.Id, next);
            changes.Add(new(Player.ComposeTaskId(mission.Group, mission.Id), next));
        }
        return changes;
    }
}
