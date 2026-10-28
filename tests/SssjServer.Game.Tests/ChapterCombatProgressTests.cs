using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;

public class ChapterCombatProgressTests
{
    private static readonly MissionTable Missions = MissionTable.Load(TestPaths.SettingDir);
    private static readonly PledgeTable Pledge = PledgeTable.Load(TestPaths.SettingDir);
    private static BattleMainCard Card(int girl, int particular = 1) => new(1,1,(uint)girl,(uint)particular,3,0);
    private static void Seed(Player player, int girlId, int taskId, int count = 0)
    {
        var state = new PledgeState();
        var girl = new PledgeGirlState { GirlId=girlId, Step=1 };
        girl.Tasks.Add(new PledgeTaskState { Id=taskId, Count=count });
        state.Girls[girlId]=girl;
        player.Extra[PledgeFeature.ExtraKey]=state.ToJson();
    }

    [Theory]
    [InlineData(223,2,1,100,1,10,1)]
    [InlineData(223,2,1,100,2,10,0)]
    [InlineData(223,2,1,100,1,1,0)]
    [InlineData(224,2,1,100,2,10,0)]
    [InlineData(224,2,17,100,2,10,1)]
    [InlineData(224,3,17,100,2,10,0)]
    [InlineData(131,1,1,1,1,1,0)]
    [InlineData(131,1,17,1,1,1,1)]
    [InlineData(140,1,1,1,1,1,1)]
    public void PledgeMatchesExactChapterAndEntryCard(int task, int cardGirl, int outfit, int c, int i, int d, int expected)
    {
        var player = new Player("pledge-combat",1);
        int girl = Pledge.Task(task)!.GirlId;
        Seed(player,girl,task);
        ChapterCombatProgress.RecordVictory(Missions,Pledge,player,c,i,d,new[]{Card(cardGirl,outfit)},new Dictionary<int,int>{{girl,1}});
        Assert.Equal(expected,PledgeFeature.LoadStateStatic(player).Girls[girl].Tasks[0].Count);
    }

    [Fact]
    public void PledgeRejectsNewStageAndCapsProgressWithoutAutoClaim()
    {
        var player = new Player("pledge-stage",1);
        Seed(player,1,140,79);
        ChapterCombatProgress.RecordVictory(Missions,Pledge,player,1,1,1,new[]{Card(1)},new Dictionary<int,int>{{1,2}});
        Assert.Equal(79,PledgeFeature.LoadStateStatic(player).Girls[1].Tasks[0].Count);
        for(int n=0;n<3;n++) ChapterCombatProgress.RecordVictory(Missions,Pledge,player,1,1,1,new[]{Card(1)},new Dictionary<int,int>{{1,1}});
        var task=PledgeFeature.LoadStateStatic(player).Girls[1].Tasks[0];
        Assert.Equal(80,task.Count);
        Assert.Equal(PledgeFeature.TaskStatusDoing,task.Status);
    }

    [Fact]
    public void AchievementsCountOncePerClearAndPreserveClaimedBitWithoutUnlockingBounties()
    {
        var player = new Player("combat-achieve",1);
        player.RecordLevelPass(MapList.MapId(1,6,1),7);
        player.SetTask(18,1098,MissionFeature.ComposeTaskValue(3,1));
        ChapterCombatProgress.RecordVictory(Missions,Pledge,player,1,6,1,new[]{Card(7),Card(7),Card(6),Card(16)},new Dictionary<int,int>());
        Assert.Equal(1,MissionFeature.ProgressOf(player.GetTask(6,31)));
        Assert.Equal(1,MissionFeature.ProgressOf(player.GetTask(18,31)));
        Assert.Equal(4,MissionFeature.ProgressOf(player.GetTask(18,1098)));
        Assert.Equal(1,MissionFeature.ClaimedOf(player.GetTask(18,1098)));
        Assert.Equal(1,MissionFeature.ProgressOf(player.GetTask(18,4205)));
        Assert.Equal(0,player.GetTask(18,2292));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SettlementCountsOriginalRosterOnlyOnVictoryAndPersists(bool success)
    {
        using var h=await FeatureHarness.CreateAsync($"combat-integrated-{success}");
        var first=h.Player.Items.First(i=>i.Genre==1 && i.Detail==1);
        var other=h.Player.Items.First(i=>i.Genre==1 && i.Detail!=1);
        var formation=h.Player.Formations.First(f=>f.Id==1);
        formation.Slots.Clear();formation.Slots.Add(new FormationCardSlot{MainCardGuid=first.Guid});
        Seed(h.Player,1,140);
        h.Player.SetMoney(MoneyType.Vigour,100);
        await h.DispatchFunctionAsync("ChapterMsg", "{\"nState\":0,\"Chapter\":1,\"Index\":6,\"Difficult\":1,\"nFormationId\":1}");
        formation.Slots[0].MainCardGuid=other.Guid;
        await h.CompleteChapterAsync(1,6,1,success?7:0,success);
        await h.CompleteChapterAsync(1,6,1,success?7:0,success);
        Assert.Equal(success?1:0,PledgeFeature.LoadStateStatic(h.Player).Girls[1].Tasks[0].Count);
        Assert.Equal(success?1:0,MissionFeature.ProgressOf(h.Player.GetTask(6,31)));
        if(!success)return;
        var saved=new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(1,PledgeFeature.LoadStateStatic(saved).Girls[1].Tasks[0].Count);
        Assert.Equal(1,MissionFeature.ProgressOf(saved.GetTask(6,31)));
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));
    }
}
