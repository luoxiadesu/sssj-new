using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class ChapterAttemptTests
{
    private static string Request(int state, int index = 1, int stars = 7) => new JsonObject
    {
        ["nState"] = state, ["Chapter"] = 1, ["Index"] = index, ["Difficult"] = 1, ["nStar"] = stars,
    }.ToJsonString();
    private static JsonNode Body(FeatureHarness h)
    {
        var root = JsonNode.Parse(h.LastOutbound.Json)!;
        return root["tbParam"]?["tbParam"] ?? root;
    }
    private static async Task Enter(FeatureHarness h, int index = 1)
    {
        h.Player.SetMoney(MoneyType.Vigour, 100);
        await h.DispatchFunctionAsync("ChapterMsg", Request(0, index));
    }
    private static Task Settle(FeatureHarness h, int index = 1, int stars = 7)
    {
        var body = JsonNode.Parse(Request(1, index, stars))!;
        body["bSuccess"] = true;
        body["battleToken"] = h.ChapterBattleToken;
        return h.DispatchLuaCallAsync(252,
            new JsonObject { ["sCmd"] = "ChapterMsg", ["tbParam"] = body }.ToJsonString());
    }

    [Fact]
    public async Task SettlementRequiresMatchingEntry()
    {
        using var h = await FeatureHarness.CreateAsync("chapter-no-entry");
        await Settle(h);
        Assert.Equal(1, Body(h)["nError"]!.GetValue<int>());
        Assert.Equal(0, MapList.PassCount(h.Player.GetLevelStar(MapList.MapId(1, 1, 1))));
        Assert.DoesNotContain(h.Player.Items, item => item.Genre == 7);
        await Enter(h);
        await Settle(h, 2);
        Assert.Equal(1, Body(h)["nError"]!.GetValue<int>());
        await Settle(h);
        Assert.Equal(0, Body(h)["nError"]!.GetValue<int>());
    }

    [Fact]
    public async Task DuplicateSettlementReplaysReplyWithoutGrantingAgainAndNewRunWorks()
    {
        using var h = await FeatureHarness.CreateAsync("chapter-repeat");
        await Enter(h);
        await Settle(h);
        string reply = h.LastOutbound.Json;
        int gold = h.Player.GetMoney(MoneyType.Gold), exp = h.Player.Exp;
        await Settle(h);
        Assert.Equal(reply, h.LastOutbound.Json);
        Assert.Equal(gold, h.Player.GetMoney(MoneyType.Gold));
        Assert.Equal(exp, h.Player.Exp);
        Assert.Equal(1, MapList.PassCount(h.Player.GetLevelStar(MapList.MapId(1, 1, 1))));
        await Enter(h);
        await Settle(h);
        Assert.Equal(gold + 1000, h.Player.GetMoney(MoneyType.Gold));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(2, MapList.PassCount(saved.GetLevelStar(MapList.MapId(1, 1, 1))));
    }

    [Fact]
    public async Task DuplicateEntryDoesNotChargeTwice()
    {
        using var h = await FeatureHarness.CreateAsync("chapter-enter-repeat");
        await Enter(h);
        int remaining = h.Player.Vigour;
        await h.DispatchFunctionAsync("ChapterMsg", Request(0));
        Assert.Equal(remaining, h.Player.Vigour);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(2147483647)]
    public async Task InvalidStarsCannotConsumeAttempt(int stars)
    {
        using var h = await FeatureHarness.CreateAsync($"chapter-stars-{stars}");
        await Enter(h);
        await Settle(h, stars: stars);
        Assert.Equal(1, Body(h)["nError"]!.GetValue<int>());
        Assert.DoesNotContain(h.Player.Items, item => item.Genre == 7);
        await Settle(h);
        Assert.Equal(0, Body(h)["nError"]!.GetValue<int>());
    }

    [Fact]
    public async Task InvalidEntryAndMalformedArrayDoNotMutatePlayer()
    {
        using var h = await FeatureHarness.CreateAsync("chapter-invalid-entry");
        h.Player.SetMoney(MoneyType.Vigour, 100);
        await h.DispatchFunctionAsync("ChapterMsg", Request(0, 257));
        Assert.Equal(100, h.Player.Vigour);
        await Settle(h);
        Assert.Equal(1, Body(h)["nError"]!.GetValue<int>());
        await h.DispatchFunctionAsync("ChapterMsg", "[]");
        Assert.DoesNotContain(h.Player.Items, item => item.Genre == 7);
    }

    [Fact]
    public async Task FailedEnterCannotReplaceExistingAttempt()
    {
        using var h = await FeatureHarness.CreateAsync("chapter-insufficient");
        h.Options.SpendVigour = true;
        await Enter(h);
        h.Player.SetMoney(MoneyType.Vigour, 0);
        await h.DispatchFunctionAsync("ChapterMsg", Request(0, 2));
        Assert.Equal(0, h.Player.Vigour);
        await Settle(h, 2);
        Assert.Equal(1, Body(h)["nError"]!.GetValue<int>());
        await Settle(h);
        Assert.Equal(0, Body(h)["nError"]!.GetValue<int>());
    }

    [Fact]
    public async Task SwitchingPlayerOnSessionCannotReuseAttempt()
    {
        using var h = await FeatureHarness.CreateAsync("chapter-switch-player");
        await Enter(h);
        var other = new Player("other-chapter-player", 999);
        h.Session.Tag = other;
        await Settle(h);
        Assert.Equal(1, Body(h)["nError"]!.GetValue<int>());
        Assert.Empty(other.Items);
        Assert.Equal(0, other.Exp);
    }

    [Theory]
    [InlineData(true, 0, 1)]
    [InlineData(false, 0, 0)]
    [InlineData(false, 7, 0)]
    public async Task ExplicitOutcomeDistinguishesZeroStarVictoryFromDefeat(bool success, int stars, int passes)
    {
        using var h = await FeatureHarness.CreateAsync($"chapter-outcome-{success}-{stars}");
        await Enter(h);
        int exp = h.Player.Exp, gold = h.Player.GetMoney(MoneyType.Gold), stamina = h.Player.Vigour;
        await h.CompleteChapterAsync(1, 1, 1, stars, success);
        Assert.Equal(passes, MapList.PassCount(h.Player.GetLevelStar(MapList.MapId(1, 1, 1))));
        if (success) return;
        string reply = h.LastOutbound.Json;
        Assert.Equal(exp, h.Player.Exp);
        Assert.Equal(gold, h.Player.GetMoney(MoneyType.Gold));
        Assert.Equal(stamina, h.Player.Vigour);
        var payload = JsonNode.Parse(reply)!;
        Assert.False(payload["bSuccess"]!.GetValue<bool>());
        Assert.Empty(Assert.IsType<JsonArray>(payload["tbAwards"]));
        await h.CompleteChapterAsync(1, 1, 1, stars, success);
        Assert.Equal(reply, h.LastOutbound.Json);
        await Enter(h);
        await Settle(h);
        Assert.Equal(1, MapList.PassCount(h.Player.GetLevelStar(MapList.MapId(1, 1, 1))));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("\"false\"")]
    public async Task MissingOrNonBooleanOutcomeCannotGrant(string outcome)
    {
        using var h = await FeatureHarness.CreateAsync("chapter-malformed-outcome");
        await Enter(h);
        var request = JsonNode.Parse(Request(1))!;
        request["battleToken"] = h.ChapterBattleToken;
        request["bSuccess"] = JsonNode.Parse(outcome);
        await h.DispatchFunctionAsync("ChapterMsg", request.ToJsonString());
        Assert.Equal(1, Body(h)["nError"]!.GetValue<int>());
        Assert.Equal(0, h.Player.Exp);
        await Settle(h);
        Assert.Equal(0, Body(h)["nError"]!.GetValue<int>());
    }

    [Fact]
    public async Task CancelIsIdempotentAndOldTokenCannotSettleNewRun()
    {
        using var h = await FeatureHarness.CreateAsync("chapter-cancel-token");
        await Enter(h);
        string old = h.ChapterBattleToken;
        int stamina = h.Player.Vigour;
        var cancel = JsonNode.Parse(Request(3))!;
        cancel["battleToken"] = old;
        await h.DispatchFunctionAsync("ChapterMsg", cancel.ToJsonString());
        await h.DispatchFunctionAsync("ChapterMsg", cancel.ToJsonString());
        Assert.Equal(0, JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>());
        Assert.Equal(100, h.Player.Vigour);
        await Settle(h);
        Assert.Equal(1, Body(h)["nError"]!.GetValue<int>());
        await Enter(h);
        Assert.NotEqual(old, h.ChapterBattleToken);
        var stale = JsonNode.Parse(Request(1))!;
        stale["battleToken"] = old;
        stale["bSuccess"] = true;
        await h.DispatchFunctionAsync("ChapterMsg", stale.ToJsonString());
        Assert.Equal(1, JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>());
        await h.DispatchFunctionAsync("ChapterMsg", cancel.ToJsonString());
        Assert.Equal(1, JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>());
        await Settle(h);
        Assert.Equal(1, MapList.PassCount(h.Player.GetLevelStar(MapList.MapId(1, 1, 1))));
    }

    [Fact]
    public async Task MissingTokenCannotSettleAndDuplicateEntryReturnsSameToken()
    {
        using var h = await FeatureHarness.CreateAsync("chapter-token-required");
        await Enter(h);
        string token = h.ChapterBattleToken;
        await h.DispatchFunctionAsync("ChapterMsg", Request(0));
        Assert.Equal(token, h.ChapterBattleToken);
        var payload = JsonNode.Parse(Request(1))!;
        payload["bSuccess"] = true;
        await h.DispatchFunctionAsync("ChapterMsg", payload.ToJsonString());
        Assert.Equal(1, JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>());
        await Settle(h);
        Assert.Equal(0, Body(h)["nError"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExitRefundsOnlyActualDebitOnceAndPersists(bool spend)
    {
        using var h = await FeatureHarness.CreateAsync($"chapter-refund-{spend}");
        var router = new SssjServer.Core.LuaCallRouter();
        string reply = "";
        router.NtfS2CCallSender = (_, _, bytes) => { reply = System.Text.Encoding.UTF8.GetString(bytes); return Task.CompletedTask; };
        var handler = new ChapterHandler(new PlayerStore(h.Options), router,
            ChapterTable.Load(TestPaths.SettingDir), spend);
        h.Player.SetMoney(MoneyType.Vigour, 100);
        await handler.OnScriptCall(h.Session, "ChapterMsg", JsonNode.Parse(Request(0)));
        string token = JsonNode.Parse(reply)!["battleToken"]!.GetValue<string>();
        int charged = 100 - h.Player.Vigour;
        Assert.Equal(spend ? ChapterTable.Load(TestPaths.SettingDir).Get(1, 1, 1)!.TotalVigour : 0, charged);
        h.Player.AddMoney(MoneyType.Vigour, 3); // Preserve unrelated recovery or purchases.
        var cancel = JsonNode.Parse(Request(3))!;
        cancel["battleToken"] = token;
        await handler.OnScriptCall(h.Session, "ChapterMsg", cancel);
        Assert.Equal(103, h.Player.Vigour);
        await handler.OnScriptCall(h.Session, "ChapterMsg", cancel);
        Assert.Equal(103, h.Player.Vigour);
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(103, saved.Vigour);
        Assert.True(await h.WaitForPfAsync(GamePf.MoneyUpdateNtf));
    }

    private static long CardExperience(CardTable table, Item item)
        => table.TotalExpBefore((int)item.EnhanceLevel) + item.EnhanceExp;

    [Fact]
    public async Task CardExperienceUsesEntrySnapshotRatherThanEditedFormationAndOnlyOnce()
    {
        using var h = await FeatureHarness.CreateAsync("chapter-card-snapshot");
        var cards = h.Player.Items.Where(i => i.Genre == 1).Take(2).ToArray();
        var table = CardTable.Load(TestPaths.SettingDir);
        var formation = h.Player.Formations.First(f => f.Id == 1);
        formation.Slots.Clear();
        formation.Slots.Add(new FormationCardSlot { MainCardGuid = cards[0].Guid, UsedCardGuid = cards[1].Guid });
        formation.Slots[0].SecondaryCardGuids.Add(cards[1].Guid);
        long first = CardExperience(table, cards[0]), second = CardExperience(table, cards[1]);
        await Enter(h);
        formation.Slots[0].MainCardGuid = cards[1].Guid;
        await Settle(h);
        Assert.Equal(first + 300, CardExperience(table, cards[0]));
        Assert.Equal(second, CardExperience(table, cards[1]));
        await Settle(h);
        Assert.Equal(first + 300, CardExperience(table, cards[0]));
        var loaded = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(first + 300, CardExperience(table, loaded.Items.Single(i => i.Guid == cards[0].Guid)));
        Assert.True(await h.WaitForPfAsync(GamePf.ItemUpdateNtf));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefeatOrCancellationDoesNotGrantCardExperience(bool cancel)
    {
        using var h = await FeatureHarness.CreateAsync($"chapter-no-card-exp-{cancel}");
        var table = CardTable.Load(TestPaths.SettingDir);
        var before = h.Player.Items.Where(i => i.Genre == 1).ToDictionary(i => i.Guid, i => CardExperience(table, i));
        await Enter(h);
        if (cancel)
        {
            var request = JsonNode.Parse(Request(3))!;
            request["battleToken"] = h.ChapterBattleToken;
            await h.DispatchFunctionAsync("ChapterMsg", request.ToJsonString());
        }
        else await h.CompleteChapterAsync(1, 1, 1, 0, false);
        foreach (var item in h.Player.Items.Where(i => i.Genre == 1)) Assert.Equal(before[item.Guid], CardExperience(table, item));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public async Task InvalidExplicitFormationDoesNotCharge(int id)
    {
        using var h = await FeatureHarness.CreateAsync($"chapter-invalid-team-{id}");
        h.Player.SetMoney(MoneyType.Vigour, 100);
        var request = JsonNode.Parse(Request(0))!;
        request["nFormationId"] = id;
        await h.DispatchFunctionAsync("ChapterMsg", request.ToJsonString());
        Assert.Equal(1, JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>());
        Assert.Equal(100, h.Player.Vigour);
    }

    [Fact]
    public void SnapshotRejectsDuplicateAndUnownedMainCards()
    {
        var player = new Player("bad-team", 1);
        player.Items.Add(new Item { Guid = 1, Genre = 1, Count = 1 });
        var team = new Formation { Id = 1 };
        player.Formations.Add(team);
        team.Slots.Add(new FormationCardSlot { MainCardGuid = 1 });
        team.Slots.Add(new FormationCardSlot { MainCardGuid = 1 });
        Assert.Empty(ChapterHandler.SnapshotMainCards(player, null, out bool valid));
        Assert.False(valid);
        team.Slots[1].MainCardGuid = 99;
        Assert.Empty(ChapterHandler.SnapshotMainCards(player, null, out valid));
        Assert.False(valid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BattleCardExperienceRespectsBreakCapWithoutDowngradingLegacyCards(bool aboveCap)
    {
        using var h = await FeatureHarness.CreateAsync($"chapter-card-cap-{aboveCap}");
        var card = h.Player.Items.First(i => i.Genre == 1);
        var template = CharacterCardTable.Load(TestPaths.SettingDir).Get(card.Genre, card.Detail, card.Particular, card.Level)!;
        var table = CardTable.Load(TestPaths.SettingDir);
        int cap = table.MaxLevelFor(CardTable.BreakTypeOf(template.WeaponType, template.Rarity), 0);
        card.EnhanceLevel = (uint)(aboveCap ? cap + 1 : cap);
        card.EnhanceExp = 123;
        var formation = h.Player.Formations.First(f => f.Id == 1);
        formation.Slots.Clear();
        formation.Slots.Add(new FormationCardSlot { MainCardGuid = card.Guid });
        await Enter(h);
        await Settle(h);
        Assert.Equal((uint)(aboveCap ? cap + 1 : cap), card.EnhanceLevel);
        Assert.Equal((uint)(aboveCap ? 123 : 423), card.EnhanceExp);
    }

    [Fact]
    public async Task MasterExperienceUsesLevelCurveAndPersists()
    {
        using var h = await FeatureHarness.CreateAsync("chapter-level-up");
        var table = PlayerExpTable.Load(TestPaths.SettingDir);
        h.Player.Level = 1;
        h.Player.Exp = table.ExpToNext(1) - 1;
        await Enter(h);
        await Settle(h);
        Assert.Equal(2, h.Player.Level);
        Assert.Equal(5, h.Player.Exp);
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(2, saved.Level);
        Assert.Equal(5, saved.Exp);
        Assert.True(await h.WaitForPfAsync(GamePf.PlayerDataNtf));
    }
}
