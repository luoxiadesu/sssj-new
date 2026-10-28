using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class ChallengePersistenceTests
{
    private static string State(Player p) => JsonSerializer.Serialize(PlayerStore.PlayerDto.FromPlayer(p), PlayerStore.JsonOptions);
    private static JsonNode Reply(FeatureHarness h) => JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
    private static string BlockSave(FeatureHarness h)
    {
        string path = Path.Combine(h.Module.Store.DirectoryPath, h.Player.Account + ".player.json.tmp");
        Directory.CreateDirectory(path);
        return path;
    }

    [Theory]
    [InlineData(0, 120000)]
    [InlineData(2000, 3600)]
    [InlineData(3600, 120000)]
    public async Task FailedRewardSaveRestoresAllStateAndRetryGrantsOnlyUnclaimedTiers(int claimed, int total)
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-reward-durable-" + claimed);
        var p = h.Player;
        p.SetTask(9, 45901, total);
        if (claimed > 0) p.SetTask(9, 45906, claimed);
        else p.Tasks.Remove(Player.ComposeTaskId(9, 45906));
        p.Money.Remove(MoneyType.Gold);
        var stack = p.Items.FirstOrDefault(i => i.Genre == 7 && i.Detail == 1 && i.Particular == 4 && i.Level == 2)
            ?? p.AddItem(7, 1, 4, 2, 5);
        uint count = stack.Count;
        var items = p.Items.ToArray();
        p.Extra["unrelated-null"] = null;
        p.Extra["unrelated-json"] = new JsonObject { ["nested"] = new JsonArray(1, 2, 3) };
        h.Module.Store.Save(p);
        string before = State(p);
        string disk = File.ReadAllText(Path.Combine(h.Module.Store.DirectoryPath, p.Account + ".player.json"));
        string blocker = BlockSave(h);
        int replies = h.Outbound.Count;
        try { await h.DispatchLuaCallAsync(206, "{}"); }
        finally { Directory.Delete(blocker); }
        Assert.False(Reply(h)["res"]!.GetValue<bool>());
        Assert.Equal(replies + 1, h.Outbound.Count);
        Assert.Equal(before, State(p));
        Assert.Equal(disk, File.ReadAllText(Path.Combine(h.Module.Store.DirectoryPath, p.Account + ".player.json")));
        Assert.Equal(count, stack.Count);
        Assert.Equal(items.Length, p.Items.Count);
        for (int i = 0; i < items.Length; i++) Assert.Same(items[i], p.Items[i]);
        Assert.False(await h.WaitForPfAsync(GamePf.MoneyUpdateNtf, 100));
        Assert.DoesNotContain(GamePf.ItemUpdateNtf, h.PushedPf);
        Assert.DoesNotContain(GamePf.TaskValueSyncRsp, h.PushedPf);
        await h.DispatchLuaCallAsync(206, "{}");
        Assert.True(Reply(h)["res"]!.GetValue<bool>());
        Assert.Equal(total, p.GetTask(9, 45906));
        var table = ChallengeTable.Load(TestPaths.SettingDir);
        int gold = table.ClaimableRewards(total, claimed).SelectMany(r => r.Rewards)
            .Where(r => r[0] == 15 && r[1] == 1 && r[2] == 1 && r[3] == 1).Sum(r => r[4]);
        Assert.Equal(gold, p.GetMoney(MoneyType.Gold));
        Assert.Equal(State(p), State(h.Module.Store.Load(p.Account)!));
        string saved = State(p);
        await h.DispatchLuaCallAsync(206, "{}");
        Assert.False(Reply(h)["res"]!.GetValue<bool>());
        Assert.Equal(saved, State(p));
    }

    private static void SeedCombat(Player p)
    {
        var card = p.AddItem(1, 7, 1, 3, 1);
        var team = p.Formations.First(f => f.Id == 1);
        team.Slots.Clear();
        team.Slots.Add(new FormationCardSlot { MainCardGuid = card.Guid });
        var pledge = new PledgeState();
        var girl = new PledgeGirlState { GirlId = 7, Step = 1 };
        girl.Tasks.Add(new PledgeTaskState { Id = 740, Count = 3 });
        pledge.Girls[7] = girl;
        p.Extra[PledgeFeature.ExtraKey] = pledge.ToJson();
        p.SetTask(18, 1098, MissionFeature.ComposeTaskValue(3, 1));
    }

    private static int PledgeCount(Player p) => PledgeFeature.LoadStateStatic(p).Girls[7].Tasks.Single(t => t.Id == 740).Count;

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    public async Task FailedSettlementRetainsAttemptAndRollsBackRewardsAchievementsAndPledge(bool won, bool wrapped, bool damage)
    {
        using var h = await ChallengeTestHarness.CreateAsync($"challenge-settle-durable-{won}-{wrapped}-{damage}");
        SeedCombat(h.Player);
        string entry = damage ? "[6,349,187,4021,1]" : "[6,349,907,2153,1]";
        await h.DispatchLuaCallAsync(54, entry);
        Assert.Null(Reply(h)["nError"]);
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));
        var run = (JsonObject)h.Player.Extra[ChallengeRuns.AttemptKey]!;
        string token = run["battleToken"]!.GetValue<string>();
        var request = (JsonArray)JsonNode.Parse(entry)!;
        request.Add(new JsonObject { ["isPass"] = won, ["nDamage"] = damage ? 500000 : 0, ["battleToken"] = token });
        Task Settle() => wrapped
            ? h.DispatchLuaCallAsync(252, new JsonObject { ["sCmd"] = 55, ["tbParam"] = request.DeepClone() }.ToJsonString())
            : h.DispatchLuaCallAsync(55, request.ToJsonString());
        JsonNode Body() => wrapped ? Reply(h)["tbParam"]! : Reply(h);
        string before = State(h.Player);
        string disk = File.ReadAllText(Path.Combine(h.Module.Store.DirectoryPath, h.Player.Account + ".player.json"));
        int outbound = h.Outbound.Count;
        int pushed = h.PushedPf.Count;
        string blocker = BlockSave(h);
        try { await Settle(); }
        finally { Directory.Delete(blocker); }
        Assert.NotNull(Body()["nError"]);
        Assert.Equal(token, Body()["battleToken"]!.GetValue<string>());
        Assert.Equal(outbound + 1, h.Outbound.Count);
        Assert.Equal(before, State(h.Player));
        Assert.Equal(disk, File.ReadAllText(Path.Combine(h.Module.Store.DirectoryPath, h.Player.Account + ".player.json")));
        Assert.True(h.Player.Extra[ChallengeRuns.AttemptKey]!["active"]!.GetValue<bool>());
        Assert.Null(h.Player.Extra[ChallengeRuns.AttemptKey]!["reply"]);
        Assert.False(await h.WaitForPfAsync(GamePf.MoneyUpdateNtf, 100));
        Assert.Equal(pushed, h.PushedPf.Count);
        await Settle();
        Assert.Null(Body()["nError"]);
        Assert.Equal(won, Body()["isPass"]!.GetValue<bool>());
        Assert.Equal(won || damage ? 1 : 0, h.Player.GetTask(9, damage ? 45301 : 45101));
        Assert.Equal(won ? 4 : 3, PledgeCount(h.Player));
        Assert.Equal(won ? 4 : 3, MissionFeature.ProgressOf(h.Player.GetTask(18, 1098)));
        Assert.Equal(1, MissionFeature.ClaimedOf(h.Player.GetTask(18, 1098)));
        Assert.False(h.Player.Extra[ChallengeRuns.AttemptKey]!["active"]!.GetValue<bool>());
        string saved = State(h.Player);
        Assert.Equal(saved, State(h.Module.Store.Load(h.Player.Account)!));
        await Settle();
        Assert.Equal(saved, State(h.Player));
        h.Session.Tag = h.Module.Store.Load(h.Player.Account)!;
        await Settle();
        Assert.Equal(saved, State((Player)h.Session.Tag));
    }
}
