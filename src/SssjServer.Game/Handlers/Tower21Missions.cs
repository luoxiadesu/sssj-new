using System.Text.Json.Nodes;
using SssjServer.Game.Config;
namespace SssjServer.Game.Handlers;

internal static class Tower21Missions
{
    internal const string DayKey = "tower21MissionDay";
    internal static bool RefreshDaily(Player player, long now, List<KeyValuePair<uint, int>> changes)
    {
        long day = GachaFreeDraws.Day(now);
        bool known = long.TryParse(player.Extra.GetValueOrDefault(DayKey)?.ToString(), out long previous);
        if (known && previous >= day) return false;
        // An old save without a day marker keeps its existing earned/claimed state.
        if (known && player.GetTask(18, 5600) != 0)
        {
            player.SetTask(18, 5600, 0);
            changes.Add(new(Player.ComposeTaskId(18, 5600), 0));
        }
        player.Extra[DayKey] = JsonValue.Create(day);
        return true;
    }
    internal static void ActPassed(Player player, MissionTable missions, long now, List<KeyValuePair<uint, int>> changes)
    {
        RefreshDaily(player, now, changes);
        SetProgress(player, missions.GetAchieveOther(5600), 1, changes);
    }
    internal static void SyncSp(Player player, MissionTable missions, List<KeyValuePair<uint, int>> changes)
    {
        long total = Enumerable.Range(18, 5).Sum(id => (long)Math.Max(0, player.GetTask(96, 60000 + id)));
        for (int id = 5601; id <= 5607; id++) SetProgress(player, missions.GetAchieveOther(id), total, changes);
    }
    internal static void TokensGained(Player player, MissionTable missions, int count, RewardGrantResult result)
    {
        if (count <= 0) return;
        var changes = new List<KeyValuePair<uint, int>>();
        for (int id = 5608; id <= 5614; id++)
        {
            var entry = missions.GetAchieveOther(id);
            if (entry is null) continue;
            long total = (long)MissionFeature.ProgressOf(player.GetTask(18, id)) + count;
            SetProgress(player, entry, total, changes);
        }
        foreach (var change in changes) result.NoteTask(change.Key, change.Value);
    }
    private static void SetProgress(Player player, MissionEntry? entry, long progress, List<KeyValuePair<uint, int>> changes)
    {
        if (entry is null) return;
        int old = player.GetTask(18, entry.Id);
        int value = MissionFeature.ComposeTaskValue((int)Math.Max(MissionFeature.ProgressOf(old),
            Math.Min(progress, entry.RequiredCount(true))), MissionFeature.ClaimedOf(old));
        if (old == value) return;
        player.SetTask(18, entry.Id, value);
        changes.Add(new(Player.ComposeTaskId(18, entry.Id), value));
    }
}

public sealed class Tower21MissionFeature : IGameFeature, IGamePulseFeature
{
    private GameFeatureContext _ctx = null!;
    public string Name => "第21期活动任务日切与SP进度";
    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        ctx.OnPlayerPrepare((session, player) => Refresh(session, player, false));
    }
    public void OnPlayerPulse(SssjServer.Core.ClientSession session, Player player) => Refresh(session, player, true);
    private void Refresh(SssjServer.Core.ClientSession session, Player player, bool push)
    {
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            bool dayChanged = Tower21Missions.RefreshDaily(player, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), changes);
            Tower21Missions.SyncSp(player, _ctx.Missions, changes);
            if (dayChanged || changes.Count > 0) _ctx.Store.Save(player);
            if (push && changes.Count > 0) GameNotify.PushTaskValues(session, changes);
        }
    }
}
