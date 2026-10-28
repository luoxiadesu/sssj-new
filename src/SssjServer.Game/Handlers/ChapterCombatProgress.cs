using SssjServer.Game.Config;
namespace SssjServer.Game.Handlers;

// Stable entry-time identity; future battle trackers must not read a modified roster.
internal sealed record BattleMainCard(uint Guid, uint Genre, uint Girl, uint Particular, uint Rarity, uint Refit)
{
    internal static BattleMainCard From(Item i) => new(i.Guid, i.Genre, i.Detail, i.Particular, i.Level, i.RefitNum);
}

internal static class ChapterCombatProgress
{
    internal static List<KeyValuePair<uint, int>> RecordVictory(MissionTable missions, PledgeTable pledge,
        Player player, int chapter, int index, int difficulty, IReadOnlyList<BattleMainCard> cards,
        IReadOnlyDictionary<int, int> entryPledgeSteps)
    {
        var changes = new List<KeyValuePair<uint, int>>();
        var girls = cards.Select(c => (int)c.Girl).ToHashSet();
        foreach (var mission in missions.Achieve.Values.Concat(missions.AchieveOther.Values))
        {
            var p = mission.Counts;
            bool matches = mission.AchieveType switch
            {
                2 => p.Count == 4 && p[0] == chapter && p[1] == index && p[2] == difficulty,
                22 => p.Count == 2 && p[1] == difficulty,
                147 => p.Count == 2 && p[1] > 0 && girls.Contains(p[1]),
                235 => p.Count > 1 && p.Skip(1).Any(g => g > 0 && girls.Contains(g)),
                // Type 173 is configured for infernal bounties, not chapter fights.
                _ => false,
            };
            int target = mission.RequiredCount(true);
            if (!matches || target <= 0 || target == int.MaxValue) continue;
            int old = player.GetTask(mission.Group, mission.Id);
            int progress = mission.AchieveType == 2
                ? Math.Max(MissionFeature.ProgressOf(old), MapList.PassCount(player.GetLevelStar(MapList.MapId(chapter,index,difficulty))))
                : (int)Math.Min(0x3FFFFFFFL, (long)MissionFeature.ProgressOf(old) + 1);
            int value = MissionFeature.ComposeTaskValue(progress, MissionFeature.ClaimedOf(old));
            if (value == old) continue;
            player.SetTask(mission.Group, mission.Id, value);
            changes.Add(new(Player.ComposeTaskId(mission.Group, mission.Id), value));
        }
        RecordPledge(pledge, player, chapter, index, difficulty, cards, entryPledgeSteps);
        return changes;
    }

    internal static List<KeyValuePair<uint,int>> RecordBounty(MissionTable missions,PledgeTable pledge,
        Player player,int eventType,int difficulty,IReadOnlyList<BattleMainCard> cards,IReadOnlyDictionary<int,int> steps)
    {
        var changes=new List<KeyValuePair<uint,int>>();
        // Freeze predecessor eligibility before the same clear updates any progress.
        var eligible=missions.Achieve.Values.Concat(missions.AchieveOther.Values).Where(m=>
        {
            var p=m.Counts;
            if(m.AchieveType==147)return p.Count==2 && cards.Any(c=>c.Girl==p[1]);
            if(m.AchieveType==235)return p.Count>1 && p.Skip(1).Any(g=>cards.Any(c=>c.Girl==g));
            if(m.AchieveType!=173 || p.Count!=8 || p[1]!=2 || p[2]!=difficulty || p[3]<0)return false;
            if(!cards.Any(c=>c.Refit>=p[3] && c.Genre==p[4] && c.Girl==p[5] && c.Particular==p[6] && c.Rarity==p[7]))return false;
            if(m.PreId<=0)return true;
            var pre=missions.GetAchieveOther(m.PreId);
            return pre is not null && Math.Max(MissionFeature.ProgressOf(player.GetTask(pre.Group,pre.Id)),
                MissionFeature.DerivedProgress(player,pre))>=pre.RequiredCount(true);
        }).ToArray();
        foreach(var m in eligible)
        {
            int old=player.GetTask(m.Group,m.Id);
            int value=MissionFeature.ComposeTaskValue((int)Math.Min(0x3FFFFFFFL,(long)MissionFeature.ProgressOf(old)+1),MissionFeature.ClaimedOf(old));
            if(value==old)continue;
            player.SetTask(m.Group,m.Id,value);changes.Add(new(Player.ComposeTaskId(m.Group,m.Id),value));
        }
        RecordPledge(pledge,player,0,0,difficulty,cards,steps,true,eventType);
        return changes;
    }

    internal static List<KeyValuePair<uint,int>> RecordTowerVictory(MissionTable missions,PledgeTable pledge,
        Player player,IReadOnlyList<BattleMainCard> cards,IReadOnlyDictionary<int,int> steps)
    {
        var changes=new List<KeyValuePair<uint,int>>();
        var girls=cards.Select(c=>(int)c.Girl).ToHashSet();
        foreach(var m in missions.Achieve.Values.Concat(missions.AchieveOther.Values))
        {
            var p=m.Counts;
            bool match=m.AchieveType switch
            {
                147=>p.Count==2 && p[1]>0 && girls.Contains(p[1]),
                235=>p.Count>1 && p.Skip(1).Any(g=>g>0 && girls.Contains(g)),
                _=>false,
            };
            int target=m.RequiredCount(true);
            if(!match || target<=0 || target==int.MaxValue)continue;
            int old=player.GetTask(m.Group,m.Id);
            int next=MissionFeature.ComposeTaskValue((int)Math.Min(0x3FFFFFFFL,(long)MissionFeature.ProgressOf(old)+1),MissionFeature.ClaimedOf(old));
            if(old==next)continue;
            player.SetTask(m.Group,m.Id,next);changes.Add(new(Player.ComposeTaskId(m.Group,m.Id),next));
        }
        RecordPledge(pledge,player,0,0,0,cards,steps,tower:true);
        return changes;
    }

    private static void RecordPledge(PledgeTable table, Player player, int chapter, int index, int difficulty,
        IReadOnlyList<BattleMainCard> cards, IReadOnlyDictionary<int, int> entrySteps, bool bounty=false, int eventType=0,bool tower=false)
    {
        var state = PledgeFeature.LoadStateStatic(player);
        bool changed = false;
        foreach (var girl in state.Girls.Values)
        {
            if (!entrySteps.TryGetValue(girl.GirlId, out int step) || step != girl.Step) continue;
            foreach (var task in girl.Tasks.Where(t => t.Status != PledgeFeature.TaskStatusDone))
            {
                var cfg = table.Task(task.Id);
                if (cfg is null || cfg.GirlId != girl.GirlId || cfg.Recount != 1 || cfg.Count <= 0
                    || cfg.StrType is not ("ChapterFight" or "ArbitraryFight" or "EventFight")) continue;
                if(tower && cfg.StrType!="ArbitraryFight")continue;
                if (bounty && cfg.StrType=="ChapterFight" || !bounty && cfg.StrType=="EventFight") continue;
                if (cfg.StrType=="EventFight")
                {
                    string[] eventParts=cfg.Para2.Trim('[',']').Split('|');
                    if(eventParts.Length!=2 || !int.TryParse(eventParts[0],out int type) || !int.TryParse(eventParts[1],out int diff)
                        || type!=-1 && type!=eventType || diff!=-1 && diff!=difficulty)continue;
                }
                if (!int.TryParse(cfg.Para1, out int participation) || participation is not (1 or 2)) continue;
                if (!cards.Any(c => c.Girl == girl.GirlId && (participation == 1 || c.Particular == CardFeature.PldgeCardParticular))) continue;
                if (cfg.StrType == "ChapterFight")
                {
                    string[] parts = cfg.Para2.Trim('[', ']').Split('|');
                    if (parts.Length != 3 || !int.TryParse(parts[0], out int c) || !int.TryParse(parts[1], out int i)
                        || !int.TryParse(parts[2], out int d) || c != chapter || i != index || d != difficulty) continue;
                }
                task.Count = (int)Math.Min(cfg.Count, Math.Max(0L, task.Count) + 1);
                changed = true;
            }
        }
        if (changed) player.Extra[PledgeFeature.ExtraKey] = state.ToJson();
    }
}
