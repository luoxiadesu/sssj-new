using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Protocol.GsProto;
namespace SssjServer.Game.Handlers;

internal sealed record GachaPayment(uint Type,uint Amount);
internal sealed class GachaPaymentSnapshot
{
    private readonly Dictionary<int,int> _money;
    private readonly Dictionary<uint,long> _items;
    internal GachaPaymentSnapshot(Player p)
    {
        _money=p.Money.ToDictionary(e=>e.Key,e=>e.Value);
        _items=p.Items.GroupBy(i=>i.Guid).ToDictionary(g=>g.Key,g=>g.Sum(i=>(long)i.Count));
    }
    internal GachaPayment After(Player p)
    {
        long money=_money.Sum(e=>Math.Max(0L,(long)e.Value-p.GetMoney(e.Key)));
        if(money>0)return new(1,(uint)Math.Min(uint.MaxValue,money));
        var remaining=p.Items.GroupBy(i=>i.Guid).ToDictionary(g=>g.Key,g=>g.Sum(i=>(long)i.Count));
        long tickets=_items.Sum(e=>Math.Max(0L,e.Value-remaining.GetValueOrDefault(e.Key)));
        return new(0,(uint)Math.Min(uint.MaxValue,tickets));
    }
}
internal static class GachaHistory
{
    internal const string ExtraKey="gachaHistory";
    internal const int Limit=20;
    private static string Bucket(int kind)=>kind==1?"cards":"weapons";
    internal static GachaInfo Append(Player player,int kind,int pool,long time,IEnumerable<int[]> items,GachaPayment payment,int hedge=0)
    {
        var draws=items.Select(i=>i.ToArray()).ToArray();
        if(kind is not (0 or 1) || pool<=0 || time<=0 || draws.Length is not (1 or 10)
            || draws.Any(d=>d.Length!=4 || d.Any(n=>n<=0) || d[0]!=(kind==1?1:2)))throw new InvalidDataException("Invalid confirmed gacha record");
        var root=player.Extra.GetValueOrDefault(ExtraKey) as JsonObject;
        if(root is null){root=new();player.Extra[ExtraKey]=root;}
        var rows=root[Bucket(kind)] as JsonArray;
        if(rows is null){rows=new();root[Bucket(kind)]=rows;}
        var gdpl=new JsonArray();foreach(var row in draws)gdpl.Add(new JsonArray(row.Select(n=>(JsonNode?)JsonValue.Create(n)).ToArray()));
        var entry=new JsonObject{["pool"]=pool,["time"]=time,["moneyType"]=payment.Type,["moneyNum"]=payment.Amount,
            ["hedge"]=Math.Clamp(hedge,0,draws.Length),["items"]=gdpl};
        rows.Add(entry);while(rows.Count>Limit)rows.RemoveAt(0);
        return Read(entry,kind)!;
    }
    private static GachaInfo? Read(JsonObject row,int kind)
    {
        bool Number(string key,out ulong n) { n=0; return row[key] is JsonValue v && ulong.TryParse(v.ToString(),out n); }
        if(!Number("pool",out ulong pool)||pool==0||pool>uint.MaxValue || !Number("time",out ulong time)||time==0
            || !Number("moneyType",out ulong moneyType)||moneyType>1 || !Number("moneyNum",out ulong amount)||amount>uint.MaxValue
            || row["items"] is not JsonArray items || items.Count is not (1 or 10))return null;
        var result=new GachaInfo{Gachaid=(uint)pool,Gachatime=time,Moneytype=(uint)moneyType,Moneynum=(uint)amount};
        foreach(var node in items)
        {
            if(node is not JsonArray a || a.Count!=4)return null;
            var d=new uint[4];
            for(int i=0;i<4;i++)if(a[i] is not JsonValue v || !uint.TryParse(v.ToString(),out d[i]) || d[i]==0)return null;
            if(d[0]!=(kind==1?1:2))return null;
            result.Gachaitem.Add(new GDPLItem{Genre=d[0],Detail=d[1],Particular=d[2],Level=d[3]});
        }
        if(Number("hedge",out ulong hedge) && hedge<=(ulong)items.Count)result.Hedgepos=(uint)hedge;
        return result;
    }
    internal static IReadOnlyList<GachaInfo> List(Player player,int kind)
    {
        lock(player)
        {
            if(player.Extra.GetValueOrDefault(ExtraKey) is not JsonObject root || root[Bucket(kind)] is not JsonArray rows)return Array.Empty<GachaInfo>();
            return rows.OfType<JsonObject>().Select(row=>Read(row,kind)).Where(r=>r is not null).Select(r=>r!).TakeLast(Limit).Reverse().ToArray();
        }
    }
    internal static void Fill(PhoneMsgNtf message,Player player)
    {
        message.Cardgacha.AddRange(List(player,1));message.Weapongacha.AddRange(List(player,0));
    }
    internal static void Push(ClientSession session,int kind,GachaInfo entry)
        =>session.Push(SssjServer.Protocol.Pf.GachaUpdateNtf,new GachaUpdateNtf{Gachatype=(uint)kind,Gachainfo=entry});
}
