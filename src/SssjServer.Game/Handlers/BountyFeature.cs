using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
namespace SssjServer.Game.Handlers;

public sealed class BountyFeature : IGameFeature, IGamePulseFeature
{
    public string Name=>"普通电力悬赏（50/51/52，退出返电）";
    private GameFeatureContext _ctx=null!;
    private BountyTable _table=null!;
    private PledgeTable _pledge=null!;
    private BountyThieves _thieves=null!;
    private readonly ConditionalWeakTable<ClientSession,Attempt> _attempts=new();
    private sealed class Attempt
    {
        internal Player Player=null!;
        internal BountyLevel Level=null!;
        internal string Token=Guid.NewGuid().ToString("N");
        internal BattleMainCard[] Cards=Array.Empty<BattleMainCard>();
        internal Dictionary<int,int> Steps=new();
        internal int Spent;
        internal int KeyMode;
        internal BountyThiefPlan? Thief;
        internal Item? KeyItem;
        internal IReadOnlyList<int[]> RandomRewards=Array.Empty<int[]>();
        internal bool Cancelled;
        internal int ResultCmd;
        internal JsonObject? Reply;
    }
    public void Install(GameFeatureContext ctx)
    {
        _ctx=ctx;_table=BountyTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _pledge=PledgeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _thieves=BountyThieves.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.MapLuaCall(50,Join);ctx.MapLuaCall(51,Pass);ctx.MapLuaCall(52,Fail);
        ctx.MapFunction("BountyCancel",Cancel);
        ctx.OnPlayerPrepare((session,player)=>
        {
            lock(player)
            {
                if(BountyDaily.Refresh(player,_table,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),new()))ctx.Store.Save(player);
            }
        });
    }
    private Task Join(ClientSession s,int cmd,JsonNode? p)
    {
        if(s.Tag is not Player player)return Task.CompletedTask;
        lock(player)
        {
            RefreshDaily(s,player);
            var level=_table.Get(Int(p,"id"),Int(p,"diff"));
            int keyMode=Int(p,"key",0);
            if(level is null || keyMode is <0 or >2 || keyMode>0 && level.Key(keyMode) is null)return Error(s,50,2);
            if(level.Diff>1 && player.GetTask(9,100+level.Id)<level.Diff-1)return Error(s,50,1);
            if(_attempts.TryGetValue(s,out var old) && ReferenceEquals(old.Player,player) && old.Level==level && old.KeyMode==keyMode && !old.Cancelled && old.Reply is null)
                return Entry(s,old);
            if(_attempts.TryGetValue(s,out var pending) && ReferenceEquals(pending.Player,player) && !pending.Cancelled && pending.Reply is null)return Error(s,50,6);
            if(keyMode==0 && !level.IsOpen(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))return Error(s,50,3);
            Item? keyItem=null;
            if(keyMode>0)
            {
                var key=level.Key(keyMode)!.Value;
                keyItem=player.Items.FirstOrDefault(i=>i.Genre==9 && i.Detail==4 && i.Particular==key.P && i.Level==key.L && i.Count>0 && i.LockOn==0);
                if(keyItem is null)return Error(s,50,2);
            }
            var ids=ChapterHandler.SnapshotMainCards(player,p,out bool valid);
            if(!valid || ids.Length==0)return Error(s,50,10);
            if(_ctx.Options.SpendVigour && !player.TrySpendVigour(level.Cost))return Error(s,50,5);
            if(keyItem is not null)keyItem.Count--;
            var attempt=new Attempt{Player=player,Level=level,KeyMode=keyMode,KeyItem=keyItem,Spent=_ctx.Options.SpendVigour?level.Cost:0,
                Cards=ids.Select(id=>BattleMainCard.From(player.Items.First(i=>i.Guid==id))).ToArray(),
                Steps=PledgeFeature.LoadStateStatic(player).Girls.ToDictionary(g=>g.Key,g=>g.Value.Step),
                RandomRewards=_ctx.Rewards.DrawRandom(level.Random,level.DrawCount),Thief=_thieves.Prepare(level)};
            _attempts.Remove(s);_attempts.Add(s,attempt);
            _ctx.Store.Save(player);GameNotify.PushMoney(s,player,MoneyType.Vigour);
            if(keyItem is not null)GameNotify.PushItems(s,new[]{keyItem});
            return Entry(s,attempt);
        }
    }
    private static int[][] FixedAwards(Attempt a)=>a.Level.Fixed.Select(row=>
    {
        var copy=row.ToArray();if(a.KeyMode==2)copy[4]=checked(copy[4]*2);return copy;
    }).ToArray();
    private Task Entry(ClientSession s,Attempt a)
    {
        _ctx.LuaCalls.CallClient(s,50,new{tbData=new{id=a.Level.Id,diff=a.Level.Diff},battleToken=a.Token,
            tbDropItems=FixedAwards(a).Select(row=>row.Take(5).Concat(new[]{100}).ToArray())
                .Concat(a.RandomRewards.Select(row=>row.Take(5).Concat(new[]{7}).ToArray())).ToArray(),
            tbSpeThiefDrop=a.Thief is null?Array.Empty<object>():new object[]{new{monId=a.Thief.Monster,
                tbDrop=a.Thief.Drops.Select(d=>new{dropId=d[0],dropCount=d[1]}).ToArray()}}});
        return Task.CompletedTask;
    }
    private bool Matches(ClientSession s,Player player,JsonNode? p,out Attempt a)
    {
        a=null!;
        return _attempts.TryGetValue(s,out a!) && ReferenceEquals(a.Player,player) && a.Level.Id==Int(p,"nActivityId")
            && a.Level.Diff==Int(p,"nDiff") && (p as JsonObject)?["battleToken"]?.ToString()==a.Token;
    }
    private Task Pass(ClientSession s,int cmd,JsonNode? p)=>Finish(s,p,true);
    private Task Fail(ClientSession s,int cmd,JsonNode? p)=>Finish(s,p,false);
    private Task Finish(ClientSession s,JsonNode? p,bool won)
    {
        if(s.Tag is not Player player)return Task.CompletedTask;
        lock(player)
        {
            int cmd=won?51:52;
            if(!Matches(s,player,p,out var a)||a.Cancelled)return Error(s,cmd,6,p);
            if(a.Reply is not null)
            {
                if(a.ResultCmd!=cmd)return Error(s,cmd,6,p);
                Reply(s,cmd,a.Reply);return Task.CompletedTask;
            }
            var rewards=new RewardGrantResult();
            var changes=new List<KeyValuePair<uint,int>>();
            if(won)
            {
                BountyDaily.Refresh(player,_table,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),changes);
                var fixedAwards=FixedAwards(a);
                bool Gold(int[] row)=>row.Length>=5 && row[0]==15 && row[1]==1 && row[2]==1 && row[3]==1;
                _ctx.Rewards.Grant(player,fixedAwards.Where(row=>!Gold(row)),RewardSource.FixedAward,rewards);
                _ctx.Rewards.Grant(player,a.RandomRewards.Where(row=>!Gold(row)),RewardSource.RandomAward,rewards);
                int maxGold=(int)Math.Min(int.MaxValue,fixedAwards.Concat(a.RandomRewards).Where(Gold).Sum(row=>(long)row[4]));
                int pickedGold=Math.Clamp(Int(p,"nGold",0),0,maxGold);
                if(pickedGold>0)_ctx.Rewards.Grant(player,new[]{new[]{15,1,1,1,pickedGold}},RewardSource.FixedAward,rewards);
                _ctx.Rewards.Grant(player,BountyThieves.Claimed(a.Thief,p),RewardSource.ThiefDrop,rewards);
                if(BountyDaily.Consume(player,a.Level,changes))
                    _ctx.Rewards.Grant(player,a.Level.Daily,RewardSource.Extra,rewards);
                PlayerProgress.AddMasterExp(player,_ctx.PlayerExp,a.Level.MasterExp);
                player.SetTask(9,100+a.Level.Id,Math.Max(player.GetTask(9,100+a.Level.Id),a.Level.Diff));
                changes.Add(new(Player.ComposeTaskId(9,100+a.Level.Id),player.GetTask(9,100+a.Level.Id)));
                changes.AddRange(ChapterCombatProgress.RecordBounty(_ctx.Missions,_pledge,player,a.Level.EventType,a.Level.Diff,a.Cards,a.Steps));
            }
            var awards=new JsonArray();foreach(var row in rewards.ToAwardTuples())awards.Add(new JsonArray(row.Select(n=>(JsonNode?)JsonValue.Create(n)).ToArray()));
            a.ResultCmd=cmd;a.Reply=new JsonObject{["nError"]=0,["tbData"]=awards,["nMasterExp"]=won?a.Level.MasterExp:0,["nCardExp"]=0,["battleToken"]=a.Token};
            _ctx.Store.Save(player);
            GameNotify.PushRewards(s,player,rewards);GameNotify.PushTaskValues(s,changes);
            if(won && a.Level.MasterExp>0)GameNotify.PushLevelExp(s,player);
            Reply(s,cmd,a.Reply);return Task.CompletedTask;
        }
    }
    private Task Cancel(ClientSession s,string function,JsonNode? p)
    {
        if(s.Tag is not Player player)return Task.CompletedTask;
        lock(player)
        {
            if(!Matches(s,player,p,out var a)||a.Reply is not null)return Task.CompletedTask;
            if(!a.Cancelled)
            {
                int previous=player.Vigour;a.Cancelled=true;
                player.SetMoney(MoneyType.Vigour,(int)Math.Min(int.MaxValue,(long)previous+a.Spent));
                if(a.KeyItem is not null)
                {
                    if(!player.Items.Contains(a.KeyItem))player.Items.Add(a.KeyItem);
                    a.KeyItem.Count++;
                }
                try{_ctx.Store.Save(player);}catch
                {
                    a.Cancelled=false;player.SetMoney(MoneyType.Vigour,previous);
                    if(a.KeyItem is not null)a.KeyItem.Count--;
                    throw;
                }
            }
            GameNotify.PushMoney(s,player,MoneyType.Vigour);
            if(a.KeyItem is not null)GameNotify.PushItems(s,new[]{a.KeyItem});
            return Task.CompletedTask;
        }
    }
    public void OnPlayerPulse(ClientSession session,Player player)
    {
        lock(player)RefreshDaily(session,player);
    }
    private void RefreshDaily(ClientSession session,Player player)
    {
        var changes=new List<KeyValuePair<uint,int>>();
        if(!BountyDaily.Refresh(player,_table,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),changes))return;
        _ctx.Store.Save(player);GameNotify.PushTaskValues(session,changes);
    }
    private Task Error(ClientSession s,int cmd,int code,JsonNode? param=null)
    {
        var body=new JsonObject{["nError"]=code,["battleToken"]=(param as JsonObject)?["battleToken"]?.DeepClone()};
        if(cmd==50)_ctx.LuaCalls.CallClient(s,cmd,body);else Reply(s,cmd,body);
        return Task.CompletedTask;
    }
    private void Reply(ClientSession s,int cmd,JsonObject body)=>_ctx.LuaCalls.CallClient(s,252,new{sCmd=cmd,tbParam=body});
    private static int Int(JsonNode? p,string key,int fallback=-1)=> (p as JsonObject)?[key] is JsonValue v && int.TryParse(v.ToString(),out int n)?n:fallback;
}
