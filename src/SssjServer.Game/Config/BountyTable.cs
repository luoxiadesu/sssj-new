namespace SssjServer.Game.Config;
public sealed record BountyLevel(int Id, int Diff, int EventType, int Cost, int MasterExp, IReadOnlyList<int[]> Fixed)
{
    public IReadOnlyList<int[]> Random { get; init; } = Array.Empty<int[]>();
    public IReadOnlyList<int[]> Daily { get; init; } = Array.Empty<int[]>();
    public int DrawCount { get; init; }
    public int DailyLimit { get; init; }
    public int Belong { get; init; }
    public string ThiefMonsters { get; init; }="";
    public IReadOnlyList<int> OpenDays { get; init; }=Array.Empty<int>();
    public bool IsOpen(long now)
    {
        var day=DateTimeOffset.FromUnixTimeSeconds(now).ToOffset(TimeSpan.FromHours(8)).AddHours(-4).DayOfWeek;
        return OpenDays.Contains(day==DayOfWeek.Sunday?7:(int)day);
    }
    public (uint P,uint L)? Key(int mode)=>mode is 1 or 2 ? Belong switch
    {
        2=>((uint)mode,mode==1?3u:4u),
        3=>((uint)(mode+2),mode==1?3u:4u),
        _=>null,
    }:null;
}
public sealed class BountyTable
{
    private readonly Dictionary<(int,int),BountyLevel> _rows=new();
    public IEnumerable<BountyLevel> All=>_rows.Values;
    public BountyLevel? Get(int id,int diff)=>_rows.GetValueOrDefault((id,diff));
    internal static List<int[]> ParseAwards(string text)
    {
        if(string.IsNullOrWhiteSpace(text))return new();
        var rows=new List<int[]>();
        foreach(string part in text.Trim('[',']').Split(','))
        {
            var values=part.Trim('[',']',' ').Split('-');
            var row=new int[values.Length];
            if(values.Length<4 || values.Length>8)return new();
            for(int i=0;i<values.Length;i++)if(!int.TryParse(values[i],out row[i]) || row[i]<0)return new();
            rows.Add(row);
        }
        return rows;
    }
    public static BountyTable Load(string directory)
    {
        var result=new BountyTable();
        var raw=SettingTable.Load(Path.Combine(directory,"BountyEvents.txt"));
        if(raw is null)return result;
        var activities=SettingTable.Load(Path.Combine(directory,"Activities.txt"));
        var limits=new Dictionary<int,int>();
        var belongs=new Dictionary<int,int>();var days=new Dictionary<int,int[]>();
        if(activities is not null)
            foreach(var activity in activities.Rows)
            {
                int activityId=activities.Int(activity,"ActiveId");
                limits[activityId]=Math.Max(0,activities.Int(activity,"Times"));
                belongs[activityId]=activities.Int(activity,"Belong");
                days[activityId]=activities.Str(activity,"OpenDay").Trim('[',']').Split(',').Select(v=>int.TryParse(v,out int d)?d:0).ToArray();
            }
        foreach(var row in raw.Rows)
        {
            int id=raw.Int(row,"EventId"),diff=raw.Int(row,"Diff"),pre=raw.Int(row,"PreCost"),post=raw.Int(row,"PostCost");
            if(id<1 || id>12 || diff<1 || pre<0 || post<0 || (long)pre+post>int.MaxValue)continue;
            result._rows[(id,diff)]=new(id,diff,raw.Int(row,"EventTypeId"),pre+post,raw.Int(row,"MasterExp"),ParseAwards(raw.Str(row,"FixAward")))
            {
                Random=ParseAwards(raw.Str(row,"RandomAward"))
                    .Select(a=>a.Length==5?a.Concat(new[]{10000}).ToArray():a).ToArray(),
                Daily=ParseAwards(raw.Str(row,"DailyFixAward")),
                DrawCount=Math.Max(0,raw.Int(row,"RandDropNum")),
                DailyLimit=limits.GetValueOrDefault(id),
                Belong=belongs.GetValueOrDefault(id),
                ThiefMonsters=raw.Str(row,"ThiefMonsterDrop"),
                OpenDays=days.GetValueOrDefault(id)??Array.Empty<int>(),
            };
        }
        return result;
    }
}
