using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class PledgeCardTests
{
    private static PledgeTable Table => PledgeTable.Load(TestPaths.SettingDir);
    private static JsonNode Reply(FeatureHarness h) => JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
    private static PledgeGirlState Girl(FeatureHarness h, int girl = 2) => PledgeFeature.LoadStateStatic(h.Player).Girls[girl];
    private static Task Settle(FeatureHarness h, int girl = 2) => h.DispatchLuaCallAsync(235, $"{{\"girlid\":{girl}}}");

    private static void Seed(FeatureHarness h, int girlId = 2, int step = 1, bool done = true, uint cardGuid = 0, int rewardStep = 0)
    {
        var cfg = Table.Step(girlId, step)!;
        var girl = new PledgeGirlState { GirlId = girlId, Step = step, CardGuid = cardGuid, RewardStep = rewardStep };
        foreach (int id in cfg.TaskList)
            girl.Tasks.Add(new PledgeTaskState { Id = id, Count = Table.Task(id)!.Count,
                Status = done ? PledgeFeature.TaskStatusDone : PledgeFeature.TaskStatusDoing });
        var state = new PledgeState();
        state.Girls.Add(girlId, girl);
        h.Player.Extra[PledgeFeature.ExtraKey] = state.ToJson();
    }

    private static void CompleteCurrent(FeatureHarness h, int girlId = 2)
    {
        var state = PledgeFeature.LoadStateStatic(h.Player);
        foreach (var task in state.Girls[girlId].Tasks)
        {
            task.Count = Table.Task(task.Id)!.Count;
            task.Status = PledgeFeature.TaskStatusDone;
        }
        h.Player.Extra[PledgeFeature.ExtraKey] = state.ToJson();
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)]
    [InlineData(14)] [InlineData(15)]
    public async Task EveryOrdinaryGirlReceivesCompletedStagesCardAndGrowthExactlyOnce(int girlId)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-cards-chain-" + girlId);
        Seed(h, girlId);
        int beforeCards = h.Player.Items.Count(i => i.Genre == 1);
        uint previous = 0;
        for (int stage = 1; stage <= 6; stage++)
        {
            if (stage > 1) CompleteCurrent(h, girlId);
            var cfg = Table.Step(girlId, stage)!;
            await Settle(h, girlId);
            var girl = Girl(h, girlId);
            Assert.Equal(stage, girl.RewardStep);
            Assert.Equal(Math.Min(stage + 1, Table.MaxStep(girlId)), girl.Step);
            var card = Assert.Single(h.Player.Items, i => i.Guid == girl.CardGuid);
            Assert.Equal(1u, card.Count);
            Assert.Equal((uint)cfg.CardGdpl[3], card.Level);
            Assert.Equal((uint)cfg.CardLv, card.EnhanceLevel);
            Assert.Equal((uint)cfg.CardBreakLv, card.BreakLevel);
            Assert.Equal(0u, card.EnhanceExp);
            var template = h.Module.CharacterCards.Get(1, (uint)girlId, 17, card.Level)!;
            var ids = new[] { template.TacticalSkill, template.PassiveSkill1, template.PassiveSkill2, template.ResistanceSkill };
            Assert.Equal(ids.Select(id => (uint)id), card.Skills.Select(s => s.SkillId));
            Assert.Equal(cfg.CardSkillLv.Select(l => (uint)l), card.Skills.Select(s => s.SkillLevel));
            Assert.Equal(4, GameNotify.BuildItemInfo(card).Skillinfo.Count);
            Assert.Equal(beforeCards + 1, h.Player.Items.Count(i => i.Genre == 1));
            if (stage is 2 or 3 or 5) Assert.Equal(previous, card.Guid);
            else Assert.NotEqual(previous, card.Guid);
            previous = card.Guid;
            var restored = h.Module.Store.Load(h.Player.Account)!;
            Assert.Equal(girl.RewardStep, PledgeFeature.LoadStateStatic(restored).Girls[girlId].RewardStep);
            Assert.Equal(card.Guid, PledgeFeature.LoadStateStatic(restored).Girls[girlId].CardGuid);
            Assert.Equal(card.Skills.Select(s => s.SkillLevel), restored.GetItem(card.Guid)!.Skills.Select(s => s.SkillLevel));
            string beforeRetry = JsonSerializer.Serialize(h.Player);
            await Settle(h, girlId);
            Assert.Equal(beforeRetry, JsonSerializer.Serialize(h.Player));
        }
        Assert.Equal(5u, h.Player.GetItem(Girl(h, girlId).CardGuid)!.Level);
        await h.DispatchLuaCallAsync(231, "[]");
        Assert.Equal(6, Reply(h)[0]!["rewardstep"]!.GetValue<int>());
    }

    [Fact]
    public async Task ActivationDoesNotAwardCardOrStoryBeforeItsFirstStageCompletes()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-cards-activation");
        var cost = Table.ActivateTask(2)!;
        h.Player.AddItem(9, 16, 1, 1, (uint)cost.Count);
        int before = h.Player.Items.Count(i => i.Genre == 1);
        await h.DispatchLuaCallAsync(233, "{\"girlid\":2}");
        Assert.Equal(1, Girl(h).Step);
        Assert.Equal(0u, Girl(h).CardGuid);
        Assert.Equal(0, Reply(h)["rewardstep"]!.GetValue<int>());
        Assert.Equal(before, h.Player.Items.Count(i => i.Genre == 1));
        await Settle(h);
        Assert.Equal(0u, Girl(h).CardGuid);
    }

    [Fact]
    public async Task ReplacementMigratesAllCardSlotsPreservesEquipmentAndLeavesUnrelatedCopiesAlone()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-cards-formation");
        var old = h.Player.AddItem(1, 2, 17, 3, 1);
        var unrelated = h.Player.AddItem(1, 2, 17, 3, 1);
        old.LockOn = 1;
        old.SyncRate = 25;
        Seed(h, step: 4, cardGuid: old.Guid, rewardStep: 3);
        var formation = new Formation { Id = 91, Title = "retain" };
        var slot = new FormationCardSlot { MainCardGuid = old.Guid, UsedCardGuid = old.Guid, WeaponGuid = 999 };
        slot.SecondaryCardGuids.AddRange([old.Guid, unrelated.Guid]);
        slot.RuneItems.Add(777);
        formation.Slots.Add(slot);
        h.Player.Formations.Add(formation);
        await Settle(h);
        uint current = Girl(h).CardGuid;
        Assert.NotEqual(old.Guid, current);
        Assert.DoesNotContain(old, h.Player.Items);
        Assert.Contains(unrelated, h.Player.Items);
        Assert.Equal(current, slot.MainCardGuid);
        Assert.Equal(current, slot.UsedCardGuid);
        Assert.Equal(new[] { current, unrelated.Guid }, slot.SecondaryCardGuids);
        Assert.Equal(999u, slot.WeaponGuid);
        Assert.Equal(new uint[] { 777 }, slot.RuneItems);
        Assert.Equal(1u, h.Player.GetItem(current)!.LockOn);
        Assert.Equal(25u, h.Player.GetItem(current)!.SyncRate);
        Assert.True(await h.WaitForPfAsync(GamePf.ItemUpdateNtf));
        Assert.True(await h.WaitForPfAsync(GamePf.FormationInfoUpdateNtf));
        Assert.Equal(current, h.Module.Store.Load(h.Player.Account)!.Formations.Single(f => f.Id == 91).Slots[0].MainCardGuid);
    }

    [Fact]
    public async Task GrowthOfExistingGuidStillPushesTheUpdatedItem()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-cards-growth-notify");
        var old = h.Player.AddItem(1, 2, 17, 3, 1);
        old.EnhanceLevel = 40;
        Seed(h, step: 2, cardGuid: old.Guid, rewardStep: 1);
        await Settle(h);
        Assert.Equal(old.Guid, Girl(h).CardGuid);
        Assert.Equal(50u, old.EnhanceLevel);
        Assert.True(await h.WaitForPfAsync(GamePf.ItemUpdateNtf));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(6)]
    public async Task FailedSettlementSaveRestoresCardStatsInventoryFormationsAndPledgeTogether(int stage)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-cards-save-failure-" + stage);
        Item? old = stage == 1 ? null : h.Player.AddItem(1, 2, 17, stage == 6 ? 4u : 3u, 1);
        if (old is not null)
        {
            old.EnhanceLevel = 12;
            old.EnhanceExp = 17;
            old.BreakLevel = 0;
            old.Skills.Add(new ItemSkill { SkillId = 30392, SkillLevel = 3 });
            var formation = new Formation { Id = 90 };
            formation.Slots.Add(new FormationCardSlot { MainCardGuid = old.Guid, UsedCardGuid = old.Guid });
            h.Player.Formations.Add(formation);
        }
        Seed(h, step: stage, cardGuid: old?.Guid ?? 0, rewardStep: stage - 1);
        await h.DispatchLuaCallAsync(231, "[]");
        h.Module.Store.Save(h.Player);
        string before = JsonSerializer.Serialize(h.Player);
        string file = Path.Combine(h.Module.Store.DirectoryPath, h.Player.Account + ".player.json");
        string disk = File.ReadAllText(file);
        string blocker = file + ".tmp";
        Directory.CreateDirectory(blocker);
        int replies = h.Outbound.Count;
        try { await Settle(h); }
        finally { Directory.Delete(blocker); }
        Assert.Equal(replies, h.Outbound.Count);
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
        Assert.Equal(disk, File.ReadAllText(file));
        Assert.Empty(h.PushedPf);
        if (old is not null) Assert.Same(old, h.Player.GetItem(old.Guid));
        await Settle(h);
        Assert.Equal(stage, Girl(h).RewardStep);
        Assert.Equal((uint)Table.Step(2, stage)!.CardLv, h.Player.GetItem(Girl(h).CardGuid)!.EnhanceLevel);
    }

    [Theory]
    [InlineData("empty")] [InlineData("wrong-task")] [InlineData("doing")]
    public async Task MissingOrUnfinishedFormalTasksCannotAwardStageCard(string corruption)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-cards-task-guard");
        Seed(h);
        var state = PledgeFeature.LoadStateStatic(h.Player);
        var tasks = state.Girls[2].Tasks;
        if (corruption == "empty") tasks.Clear();
        else if (corruption == "wrong-task") tasks[0].Id = 236;
        else tasks[0].Status = PledgeFeature.TaskStatusDoing;
        h.Player.Extra[PledgeFeature.ExtraKey] = state.ToJson();
        await Settle(h);
        Assert.Equal(0u, Girl(h).CardGuid);
        Assert.Equal(0, Girl(h).RewardStep);
    }

    [Theory]
    [InlineData("absent")] [InlineData("wrong-girl")] [InlineData("ordinary")] [InlineData("higher-stage")]
    public async Task BadTrackedCardCannotConsumeAnotherCardOrProduceAReplacement(string corruption)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-cards-guid-guard");
        uint guid = 99999;
        if (corruption == "wrong-girl") guid = h.Player.AddItem(1, 3, 17, 3, 1).Guid;
        if (corruption == "ordinary") guid = h.Player.AddItem(1, 2, 1, 3, 1).Guid;
        if (corruption == "higher-stage") guid = h.Player.AddItem(1, 2, 17, 5, 1).Guid;
        Seed(h, step: 4, cardGuid: guid, rewardStep: 3);
        await Settle(h);
        Assert.Equal(4, Girl(h).Step);
        Assert.Equal(guid, Girl(h).CardGuid);
        Assert.Equal(3, Girl(h).RewardStep);
    }

    [Fact]
    public async Task LegacyMissingCardDoesNotTriggerLoginGrantButCanEarnCurrentCompletedStage()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-cards-legacy");
        Seed(h, step: 3);
        int before = h.Player.Items.Count;
        await h.DispatchLuaCallAsync(231, "[]");
        Assert.Equal(before, h.Player.Items.Count);
        Assert.Equal(0u, Girl(h).CardGuid);
        await Settle(h);
        Assert.Equal(before + 1, h.Player.Items.Count);
        var card = h.Player.GetItem(Girl(h).CardGuid)!;
        Assert.Equal(60u, card.EnhanceLevel);
        Assert.Equal(2u, card.BreakLevel);
        Assert.Equal(3, Girl(h).RewardStep);
    }

    [Fact]
    public async Task TrackedCardCannotBeSoldFedOrManuallyBroken()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-cards-protect");
        Seed(h);
        await Settle(h);
        var card = h.Player.GetItem(Girl(h).CardGuid)!;
        Assert.NotEqual(0, BagFeature.CheckSellable(h.Player, card));
        await h.DispatchLuaCallAsync(1, $"[{card.Guid}]");
        Assert.Contains(card, h.Player.Items);
        int replies = h.Outbound.Count;
        await h.DispatchLuaCallAsync(6, $"{{\"guid\":{card.Guid}}}");
        Assert.Equal(replies, h.Outbound.Count);
        Assert.Equal(0u, card.BreakLevel);
        await h.DispatchLuaCallAsync(5, $"{{\"guid\":{card.Guid},\"items\":[]}}");
        Assert.NotNull(Reply(h)["errorKey"]);
        Assert.Equal(40u, card.EnhanceLevel);
    }

    [Theory]
    [InlineData(13)] [InlineData(16)]
    public async Task SimplePledgesCompleteStoryFlagsWithoutReceivingOrdinaryCards(int girlId)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-cards-simple-" + girlId);
        int before = h.Player.Items.Count;
        await h.DispatchLuaCallAsync(233, $"{{\"girlid\":{girlId}}}");
        for (int step = 2; step <= 7; step++)
        {
            await h.DispatchLuaCallAsync(228, $"{{\"girlId\":{girlId},\"step\":{step}}}");
            Assert.Equal(step - 1, Girl(h, girlId).RewardStep);
        }
        Assert.Equal(0u, Girl(h, girlId).CardGuid);
        Assert.Equal(before, h.Player.Items.Count);
    }

    [Fact]
    public async Task FinalStageSevenClaimSetsCompletionFlagWithoutAwardingAnotherCard()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-cards-stage-seven");
        Seed(h, girlId: 6, step: 7, rewardStep: 6);
        int cards = h.Player.Items.Count(i => i.Genre == 1);
        await h.DispatchLuaCallAsync(239, "{\"girlid\":6,\"step\":7}");
        Assert.Equal(0, Reply(h)["errid"]!.GetValue<int>());
        Assert.Equal(7, Girl(h, 6).RewardStep);
        Assert.Equal(cards, h.Player.Items.Count(i => i.Genre == 1));
        string before = JsonSerializer.Serialize(h.Player);
        await h.DispatchLuaCallAsync(239, "{\"girlid\":6,\"step\":7}");
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
    }

    [Theory]
    [InlineData(233)] [InlineData(234)] [InlineData(228)]
    public async Task FailedActivationClaimOrSimpleStorySaveDoesNotConsumeOrAdvance(int command)
    {
        using var h = await FeatureHarness.CreateAsync("pledge-cards-operation-failure-" + command);
        string request;
        if (command == 233)
        {
            h.Player.AddItem(9, 16, 1, 1, 100);
            request = "{\"girlid\":2}";
        }
        else if (command == 234)
        {
            Seed(h, done: false);
            var task = Table.Task(201)!;
            var g = task.ReduceGdpl;
            h.Player.AddItem((uint)g[0], (uint)g[1], (uint)g[2], (uint)g[3], (uint)task.Count);
            request = "{\"girlid\":2,\"taskid\":201}";
        }
        else
        {
            await h.DispatchLuaCallAsync(233, "{\"girlid\":13}");
            request = "{\"girlId\":13,\"step\":2}";
        }
        await h.DispatchLuaCallAsync(231, "[]");
        h.Module.Store.Save(h.Player);
        string before = JsonSerializer.Serialize(h.Player);
        string file = Path.Combine(h.Module.Store.DirectoryPath, h.Player.Account + ".player.json");
        string disk = File.ReadAllText(file);
        Directory.CreateDirectory(file + ".tmp");
        try { await h.DispatchLuaCallAsync(command, request); }
        finally { Directory.Delete(file + ".tmp"); }
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
        Assert.Equal(disk, File.ReadAllText(file));
        Assert.Empty(h.PushedPf);
        await h.DispatchLuaCallAsync(command, request);
        Assert.Null(Reply(h)["Err"]);
    }

    [Fact]
    public void CardGuidUsesFullUnsignedProtocolRange()
    {
        var girl = PledgeGirlState.FromJson(JsonNode.Parse("{\"girlid\":2,\"step\":2,\"cardguid\":3000000000,\"rewardstep\":1}")!.AsObject());
        Assert.Equal(3000000000u, girl.CardGuid);
        Assert.Equal(3000000000u, PledgeGirlState.FromJson(girl.ToJson()).CardGuid);
    }

    [Fact]
    public async Task ConcurrentFinalStageRetriesReplaceCardOnlyOnce()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-cards-concurrent");
        var old = h.Player.AddItem(1, 2, 17, 4, 1);
        Seed(h, step: 6, cardGuid: old.Guid, rewardStep: 5);
        uint next = h.Player.NextGuid;
        var handler = h.Module.Features.OfType<PledgeFeature>().Single();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => handler.OnStepUpGirl(h.Session, 235,
            JsonNode.Parse("{\"girlid\":2}")))));
        Assert.Equal(next, Girl(h).CardGuid);
        Assert.Equal(next + 1, h.Player.NextGuid);
        Assert.Equal(6, Girl(h).RewardStep);
        Assert.DoesNotContain(old, h.Player.Items);
        Assert.Equal(8, h.Outbound.Count);
    }

    [Fact]
    public async Task FailedTimerRefreshDuringActivationDoesNotSendMalformedGirlResponse()
    {
        using var h = await FeatureHarness.CreateAsync("pledge-cards-activation-timer-failure");
        Seed(h, step: 3, done: false);
        var state = PledgeFeature.LoadStateStatic(h.Player);
        state.Girls[2].Tasks.Single(t => t.Id == 230).Count = 0;
        h.Player.Extra[PledgeFeature.ExtraKey] = state.ToJson();
        if (h.Player.GetGirl(2) is null) h.Player.Girls.Add(new Girl { Girlid = 2 });
        h.Player.SetTask(2, 2, 2);
        h.Player.AddItem(9, 16, 1, 1, 100);
        await h.DispatchLuaCallAsync(231, "[]");
        h.Player.Extra[PledgeTimeProgress.ExtraKey]!["records"]!["2:3:230"]!["clock"]
            = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120;
        h.Module.Store.Save(h.Player);
        string before = JsonSerializer.Serialize(h.Player);
        string file = Path.Combine(h.Module.Store.DirectoryPath, h.Player.Account + ".player.json");
        Directory.CreateDirectory(file + ".tmp");
        int count = h.Outbound.Count;
        try { await h.DispatchLuaCallAsync(233, "{\"girlid\":1}"); }
        finally { Directory.Delete(file + ".tmp"); }
        Assert.Equal(count, h.Outbound.Count);
        Assert.Equal(before, JsonSerializer.Serialize(h.Player));
        Assert.False(PledgeFeature.LoadStateStatic(h.Player).Girls.ContainsKey(1));
    }
}
