using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;

public class ChallengeEntryGateTests
{
    private const string Entry = "[6,349,907,2153,1]";
    private static JsonNode Reply(FeatureHarness h) => JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OnlyCanonicalNormalFiveTwelveUnlocksTower(int wrongRecord)
    {
        using var h = await FeatureHarness.CreateAsync("challenge-locked-" + wrongRecord);
        if (wrongRecord == 1) h.Player.RecordLevelPass(MapList.MapId(5, 12, 2), 7);
        if (wrongRecord == 2) h.Player.RecordLevelPass(512, 7);
        h.Player.SetTask(9, 45900, 2);
        await h.DispatchLuaCallAsync(54, Entry);
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(2, h.Player.GetTask(9, 45900));
        Assert.False(h.Player.Extra.ContainsKey(ChallengeRuns.AttemptKey));
        Assert.False(h.Player.Extra.ContainsKey(ChallengeDaily.DayKey));
        h.Player.RecordLevelPass(MapList.MapId(5, 12, 1), 7);
        await h.DispatchLuaCallAsync(54, Entry);
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(2, h.Player.GetTask(9, 45900));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("unknown-card")]
    [InlineData("depleted-card")]
    [InlineData("weapon")]
    [InlineData("overfull")]
    public async Task InvalidTeamDoesNotSpendOrReplaceActiveRun(string invalid)
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-team-" + invalid);
        h.Player.SetTask(9, 45900, 2);
        var team = h.Player.Formations.First(f => f.Id == 1);
        switch (invalid)
        {
            case "missing": h.Player.Formations.Clear(); break;
            case "empty": team.Slots.Clear(); break;
            case "duplicate": team.Slots[1].MainCardGuid = team.Slots[0].MainCardGuid; break;
            case "unknown-card": team.Slots[0].MainCardGuid = uint.MaxValue; break;
            case "depleted-card": h.Player.Items.First(i => i.Guid == team.Slots[0].MainCardGuid).Count = 0; break;
            case "weapon": team.Slots[0].MainCardGuid = h.Player.AddItem(2, 1, 3, 1, 1).Guid; break;
            case "overfull": team.Slots.Add(new FormationCardSlot()); break;
        }
        var old = new JsonObject { ["active"] = false, ["test"] = "existing-run" };
        h.Player.Extra[ChallengeRuns.AttemptKey] = old.DeepClone();
        await h.DispatchLuaCallAsync(54, Entry);
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(2, h.Player.GetTask(9, 45900));
        Assert.Equal(old.ToJsonString(), h.Player.Extra[ChallengeRuns.AttemptKey]!.ToJsonString());
        Assert.False(h.Player.Extra.ContainsKey(ChallengeDaily.DayKey));
    }

    [Fact]
    public async Task EntrySnapshotPersistsAndSettlementUsesOriginalRun()
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-team-snapshot");
        uint[] cards = h.Player.Formations.First(f => f.Id == 1).Slots.Select(s => s.MainCardGuid).Where(g => g != 0).ToArray();
        await h.DispatchLuaCallAsync(54, Entry);
        Assert.Null(Reply(h)["nError"]);
        var loaded = h.Module.Store.Load(h.Player.Account)!;
        var attempt = (JsonObject)loaded.Extra[ChallengeRuns.AttemptKey]!;
        Assert.Equal(cards, ((JsonArray)attempt["mainCards"]!).Select(n => n!.GetValue<uint>()).ToArray());
        h.Session.Tag = loaded;
        loaded.Formations.First(f => f.Id == 1).Slots.Clear();
        await ChallengeTestHarness.SettleAsync(h,55, "[6,349,907,2153,1,{\"isPass\":true}]");
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(1, loaded.GetTask(9, 45101));
    }
}
