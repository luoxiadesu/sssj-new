using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;
public class TowerCombatProgressTests
{
    private static readonly MissionTable Missions=MissionTable.Load(TestPaths.SettingDir);
    private static readonly PledgeTable Pledge=PledgeTable.Load(TestPaths.SettingDir);
    private static void Seed(Player p,int step=1,int count=0)
    {
        var s=new PledgeState();var g=new PledgeGirlState{GirlId=7,Step=step};
        g.Tasks.Add(new PledgeTaskState{Id=740,Count=count});g.Tasks.Add(new PledgeTaskState{Id=731});
        s.Girls[7]=g;p.Extra[PledgeFeature.ExtraKey]=s.ToJson();
    }
    private static int Count(Player p,int task=740)=>PledgeFeature.LoadStateStatic(p).Girls[7].Tasks.Single(t=>t.Id==task).Count;
    private static int Achieve(Player p)=>MissionFeature.ProgressOf(p.GetTask(18,1098));
    private static void Team(Player p)
    {
        var c=p.AddItem(1,7,1,3,1);var f=p.Formations.First(f=>f.Id==1);f.Slots.Clear();f.Slots.Add(new FormationCardSlot{MainCardGuid=c.Guid});
    }
    private static string Args(bool challenge,bool won)=>challenge
        ? $"[6,349,907,2153,1,{{\"isPass\":{(won?"true":"false")}}}]" : "[4,21,0,2,1,{}]";
    [Theory]
    [InlineData(false,"win")]
    [InlineData(false,"fail")]
    [InlineData(false,"cancel")]
    [InlineData(true,"win")]
    [InlineData(true,"fail")]
    [InlineData(true,"cancel")]
    public async Task EntryIdentitySurvivesReloadAndOnlyFirstVictoryCounts(bool challenge,string outcome)
    {
        using var h=await ChallengeTestHarness.CreateAsync("tower-combat-"+challenge+outcome);
        Team(h.Player);Seed(h.Player);h.Player.SetTask(96,1802,1);h.Player.SetMoney(MoneyType.Vigour,100);
        await h.DispatchLuaCallAsync(54,Args(challenge,true));
        var p=h.Module.Store.Load(h.Player.Account)!;h.Session.Tag=p;
        var f=p.Formations.First(f=>f.Id==1);p.Items.First(i=>i.Guid==f.Slots[0].MainCardGuid).Particular=17;
        f.Slots.Clear();f.Slots.Add(new FormationCardSlot{MainCardGuid=p.Items.First(i=>i.Genre==1 && i.Detail!=7).Guid});
        if(outcome=="cancel")
        {
            var run=(JsonObject)p.Extra[challenge?ChallengeRuns.AttemptKey:TwentyFirstTowerHandler.AttemptKey]!;
            string token=run[challenge?"battleToken":"Token"]!.GetValue<string>();
            await h.DispatchFunctionAsync(challenge?"ChallengeCancel":"Tower21Cancel",new JsonObject{["battleToken"]=token}.ToJsonString());
        }
        else
        {
            int cmd=challenge || outcome=="win"?55:56;
            await ChallengeTestHarness.SettleAsync(h,cmd,Args(challenge,outcome=="win"));
            await ChallengeTestHarness.SettleAsync(h,cmd,Args(challenge,outcome=="win"));
        }
        int expected=outcome=="win"?1:0;
        Assert.Equal(expected,Count(p));Assert.Equal(0,Count(p,731));Assert.Equal(expected,Achieve(p));
        Assert.Equal(0,p.GetTask(6,31)); // Main-chapter difficulty counters are not tower counters.
        var saved=h.Module.Store.Load(p.Account)!;Assert.Equal(expected,Count(saved));Assert.Equal(expected,Achieve(saved));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewPledgeStageCannotReuseBattleStartedInOldStage(bool challenge)
    {
        using var h=await ChallengeTestHarness.CreateAsync("tower-pledge-stage-"+challenge);
        Team(h.Player);Seed(h.Player);h.Player.SetTask(96,1802,1);h.Player.SetMoney(MoneyType.Vigour,100);
        await h.DispatchLuaCallAsync(54,Args(challenge,true));Seed(h.Player,2);
        await ChallengeTestHarness.SettleAsync(h,55,Args(challenge,true));
        Assert.Equal(0,Count(h.Player));Assert.Equal(1,Achieve(h.Player));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyRunWithoutIdentityIsNotBackfilledFromCurrentRoster(bool challenge)
    {
        using var h=await ChallengeTestHarness.CreateAsync("tower-legacy-identity-"+challenge);
        Team(h.Player);Seed(h.Player);h.Player.SetTask(96,1802,1);h.Player.SetMoney(MoneyType.Vigour,100);
        await h.DispatchLuaCallAsync(54,Args(challenge,true));
        var run=(JsonObject)h.Player.Extra[challenge?ChallengeRuns.AttemptKey:TwentyFirstTowerHandler.AttemptKey]!;
        run.Remove(challenge?"cardIdentities":"Cards");
        await ChallengeTestHarness.SettleAsync(h,55,Args(challenge,true));
        Assert.Equal(0,Count(h.Player));Assert.Equal(0,Achieve(h.Player));
    }
    [Fact]
    public async Task StoryDoesNotCountAsCombatVictory()
    {
        using var h=await FeatureHarness.CreateAsync("tower-story-not-combat");Team(h.Player);Seed(h.Player);
        await h.DispatchLuaCallAsync(54,"[4,21,0,1,1]");
        await ChallengeTestHarness.SettleAsync(h,55,"[4,21,0,1,1,true]");
        Assert.Equal(0,Count(h.Player));Assert.Equal(0,Achieve(h.Player));
    }
    [Fact]
    public void PledgeOutfitCapsAndClaimedAchievementBitsRemainCorrect()
    {
        var p=new Player("tower-helper",1);Seed(p,count:79);
        p.SetTask(18,1098,MissionFeature.ComposeTaskValue(3,1));
        var cards=new[]{new BattleMainCard(1,1,7,17,3,0),new BattleMainCard(2,1,7,17,3,0)};
        for(int i=0;i<3;i++)ChapterCombatProgress.RecordTowerVictory(Missions,Pledge,p,cards,new Dictionary<int,int>{{7,1}});
        Assert.Equal(80,Count(p));Assert.Equal(3,Count(p,731));
        Assert.Equal(6,Achieve(p));Assert.Equal(1,MissionFeature.ClaimedOf(p.GetTask(18,1098)));
        Assert.All(PledgeFeature.LoadStateStatic(p).Girls[7].Tasks,t=>Assert.Equal(PledgeFeature.TaskStatusDoing,t.Status));
        Assert.Equal(0,p.GetTask(18,2292)); // Refit/infernal-bounty criteria are excluded.
    }
    [Fact]
    public async Task DamageThresholdUnlockWithoutVictoryDoesNotCountGenericCombat()
    {
        using var h=await ChallengeTestHarness.CreateAsync("tower-damage-not-victory");Team(h.Player);Seed(h.Player);
        const string body="[6,349,187,4021,1,{\"isPass\":false,\"nDamage\":500000}]";
        await h.DispatchLuaCallAsync(54,body);await ChallengeTestHarness.SettleAsync(h,55,body);
        Assert.Equal(1,h.Player.GetTask(9,45301));Assert.Equal(0,Count(h.Player));Assert.Equal(0,Achieve(h.Player));
    }
    [Fact]
    public void AlreadyClaimedPledgeAndModeSpecificTasksDoNotAdvance()
    {
        var p=new Player("tower-specific-tasks",1);Seed(p,count:79);
        var state=PledgeFeature.LoadStateStatic(p);state.Girls[7].Tasks[0].Status=PledgeFeature.TaskStatusDone;
        var other=new PledgeGirlState{GirlId=2,Step=1};other.Tasks.Add(new PledgeTaskState{Id=223});other.Tasks.Add(new PledgeTaskState{Id=231});state.Girls[2]=other;
        p.Extra[PledgeFeature.ExtraKey]=state.ToJson();
        ChapterCombatProgress.RecordTowerVictory(Missions,Pledge,p,new[]{new BattleMainCard(1,1,7,17,3,0),new BattleMainCard(2,1,2,17,3,0)},new Dictionary<int,int>{{7,1},{2,1}});
        Assert.Equal(79,Count(p));Assert.All(PledgeFeature.LoadStateStatic(p).Girls[2].Tasks,t=>Assert.Equal(0,t.Count));
    }
}
