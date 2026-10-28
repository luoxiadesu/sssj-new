using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SssjServer.Game.Config;
namespace SssjServer.Game.Handlers;
internal sealed record BountyThiefPlan(int Monster,IReadOnlyList<int[]> Drops,IReadOnlyList<int[]> Awards);
internal sealed class BountyThieves
{
    private readonly Dictionary<int,(int Count,List<int[]> Rolls)> _pools=new();
    private readonly Dictionary<int,int[]> _items=new();
    internal static List<int[]> Triples(string text)=>Regex.Matches(text,@"\[(\d+),(\d+),(\d+)\]")
        .Select(m=>m.Groups.Cast<Group>().Skip(1).Select(g=>int.Parse(g.Value)).ToArray()).ToList();
    internal static BountyThieves Load(string directory)
    {
        var result=new BountyThieves();
        var pools=SettingTable.Load(Path.Combine(directory,"ThiefRandomDrop.txt"));
        if(pools is not null)foreach(var row in pools.Rows)
        {
            int count=pools.Int(row,"Count");if(count<1 || count>20)continue;
            result._pools[pools.Int(row,"Id")]=(count,Triples(pools.Str(row,"Random")));
        }
        var items=SettingTable.Load(Path.Combine(directory,"DropItem.txt"));
        if(items is not null)foreach(var row in items.Rows)
        {
            if(items.Int(row,"type")!=0)continue;
            var gdpl=ChapterTable.ParseTuples(items.Str(row,"attachment")).FirstOrDefault();
            if(gdpl?.Length==4)result._items[items.Int(row,"id")]=gdpl;
        }
        return result;
    }
    private static int[]? Roll(IReadOnlyList<int[]> rows,int bound,Random rng)
    {
        int roll=rng.Next(bound),cursor=0;
        foreach(var row in rows){cursor+=row[2];if(roll<cursor)return row;}
        return null;
    }
    internal BountyThiefPlan? Prepare(BountyLevel level,Random? rng=null)
    {
        rng??=Random.Shared;
        var monster=Roll(Triples(level.ThiefMonsters),1000,rng);
        if(monster is null || !_pools.TryGetValue(monster[1],out var pool))return null;
        var drops=new List<int[]>();var awards=new List<int[]>();
        for(int n=0;n<pool.Count;n++)
        {
            var item=Roll(pool.Rolls,10000,rng);
            if(item is null || item[1]<=0 || !_items.TryGetValue(item[0],out var gdpl))continue;
            drops.Add(new[]{item[0],item[1]});awards.Add(gdpl.Concat(new[]{item[1]}).ToArray());
        }
        return new(monster[0],drops,awards);
    }
    internal static IReadOnlyList<int[]> Claimed(BountyThiefPlan? plan,JsonNode? report)
    {
        if(plan is null || report is not JsonObject body || body["tbThiefDeath"] is not JsonArray dead || dead.Count>32)return Array.Empty<int[]>();
        bool killed=dead.OfType<JsonObject>().Any(d=>d["monId"] is JsonValue id && id.TryGetValue<int>(out int n) && n==plan.Monster
            && d["bDeath"] is JsonValue flag && (flag.TryGetValue<bool>(out bool b) && b || flag.TryGetValue<int>(out int v) && v==1));
        if(!killed || body["tbThiefDropItem"] is not JsonArray claimed || claimed.Count>128)return Array.Empty<int[]>();
        string Key(int[] a)=>string.Join('-',a.Take(4));
        var offered=plan.Awards.GroupBy(Key).ToDictionary(g=>g.Key,g=>g.Sum(a=>(long)a[4]));
        var taken=new Dictionary<string,int[]>();
        foreach(var node in claimed)
        {
            if(node is not JsonArray a || a.Count<5)return Array.Empty<int[]>();
            var row=new int[5];
            for(int i=0;i<5;i++)if(a[i] is not JsonValue val || !val.TryGetValue<int>(out row[i]) || row[i]<=0)return Array.Empty<int[]>();
            string key=Key(row);long count=row[4]+(taken.TryGetValue(key,out var old)?(long)old[4]:0);
            if(!offered.TryGetValue(key,out long max) || count>max || count>int.MaxValue)return Array.Empty<int[]>();
            row[4]=(int)count;taken[key]=row;
        }
        return taken.Values.ToArray();
    }
}
