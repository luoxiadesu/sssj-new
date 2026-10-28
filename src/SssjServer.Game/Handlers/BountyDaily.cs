using System.Text.Json.Nodes;
using SssjServer.Game.Config;
namespace SssjServer.Game.Handlers;
internal static class BountyDaily
{
    internal const string DayKey="bountyDailyDay";
    // The original Activities table specifies 04:00; client/server region is UTC+8.
    internal static long Day(long now)=>(now+4*3600)/86400;
    internal static bool Refresh(Player player,BountyTable table,long now,List<KeyValuePair<uint,int>> changes)
    {
        long day=Day(now);
        bool known=long.TryParse(player.Extra.GetValueOrDefault(DayKey)?.ToString(),out long previous);
        if(known && previous>=day)return false;
        foreach(var category in table.All.GroupBy(r=>r.EventType))
        {
            int id=4000+category.Key,limit=category.Select(r=>r.DailyLimit).Distinct().Single();
            uint key=Player.ComposeTaskId(9,id);
            bool exists=player.Tasks.ContainsKey(key);
            // Legacy counts without a day marker are evidence: never replenish them speculatively.
            int next=known || !exists?limit:Math.Clamp(player.GetTask(9,id),0,limit);
            if(!exists || next!=player.GetTask(9,id))
            {
                player.SetTask(9,id,next);changes.Add(new(key,next));
            }
        }
        player.Extra[DayKey]=JsonValue.Create(day);return true;
    }
    internal static bool Consume(Player player,BountyLevel level,List<KeyValuePair<uint,int>> changes)
    {
        int id=4000+level.EventType,remaining=player.GetTask(9,id);
        if(remaining<=0 || level.Daily.Count==0)return false;
        player.SetTask(9,id,remaining-1);changes.Add(new(Player.ComposeTaskId(9,id),remaining-1));return true;
    }
}
