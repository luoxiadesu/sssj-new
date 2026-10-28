using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;
public class PhoneLetterAccessTests
{
    [Fact]
    public async Task RepeatedGetPreservesExistingBranchAndConversationSortTime()
    {
        using var h = await FeatureHarness.CreateAsync("phone-get-idempotent");
        h.Player.SetTask(18, 662, 200);
        await Send(h, 11);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":3,\"nMsgId\":135,\"nSelectId\":2}");
        var state = PhoneMsgFeature.LoadStateStatic(h.Player); state.Letters[1].CreateTime = 123;
        h.Player.Extra[PhoneMsgFeature.ExtraKey] = state.ToJson();
        string before = state.ToJson().ToJsonString();
        await Send(h, 11); await Send(h, 11);
        Assert.Equal(before, PhoneMsgFeature.LoadStateStatic(h.Player).ToJson().ToJsonString());
        Assert.Equal(before, PhoneMsgFeature.LoadStateStatic(h.Module.Store.Load(h.Player.Account)!).ToJson().ToJsonString());
    }

    [Fact]
    public async Task DeleteArchivesExactBranchAndExplicitGetRestoresItAfterReload()
    {
        using var h = await FeatureHarness.CreateAsync("phone-archive-branch");
        h.Player.SetTask(18, 662, 200);
        await Send(h, 11);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":3,\"nMsgId\":135,\"nSelectId\":2}");
        string original = PhoneMsgFeature.LoadStateStatic(h.Player).Letters[1].Topics[135].ToJson().ToJsonString();
        await Send(h, 10);
        var disk = PhoneMsgFeature.LoadStateStatic(h.Module.Store.Load(h.Player.Account)!);
        Assert.Equal(original, disk.ArchivedTopics[135].ToJson().ToJsonString());
        h.Player.Extra[PhoneMsgFeature.ExtraKey] = disk.ToJson();
        await Send(h, 11);
        var restored = PhoneMsgFeature.LoadStateStatic(h.Player);
        Assert.Equal(original, restored.Letters[1].Topics[135].ToJson().ToJsonString());
        Assert.DoesNotContain(135, restored.ArchivedTopics.Keys);
        await Send(h, 10);
        await Send(h, 11);
        Assert.Equal(original, PhoneMsgFeature.LoadStateStatic(h.Player).Letters[1].Topics[135].ToJson().ToJsonString());
    }

    [Fact]
    public async Task DeletedTopicStaysDeletedAcrossPulseLoginAndReloadUntilExplicitGet()
    {
        using var h = await FeatureHarness.CreateAsync("phone-delete-tombstone");
        h.Player.SetTask(18, 662, 200);
        await Send(h, 11);
        await Send(h, 10);
        Assert.Contains(135, PhoneMsgFeature.LoadStateStatic(h.Module.Store.Load(h.Player.Account)!).DeletedLetters);
        var pulse = (IGamePulseFeature)h.Module.Features.OfType<PhoneMsgFeature>().Single();
        pulse.OnPlayerPulse(h.Session, h.Player);
        h.RunLoginHooks();
        await Send(h, 12);
        var deleted = PhoneMsgFeature.LoadStateStatic(h.Player);
        Assert.False(deleted.Letters.TryGetValue(1, out var letter) && letter.Topics.ContainsKey(135));
        await Send(h, 11);
        Assert.Contains(135, PhoneMsgFeature.LoadStateStatic(h.Player).Letters[1].Topics.Keys);
        Assert.DoesNotContain(135, PhoneMsgFeature.LoadStateStatic(h.Player).DeletedLetters);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public async Task FirstReceiptRequiresConditionsButDeletedHistorySurvivesConditionLoss(int cmd)
    {
        using var h = await FeatureHarness.CreateAsync("phone-access");
        await Send(h, cmd);
        Assert.Empty(PhoneMsgFeature.LoadStateStatic(h.Player).Letters);
        Assert.True(JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>() > 0);
        h.Player.SetTask(18, 662, 200);
        await Send(h, cmd);
        Assert.Contains(135, PhoneMsgFeature.LoadStateStatic(h.Player).Letters[1].Topics.Keys);
        await PhoneDialogTests.Finish(h);
        await Send(h, 10);
        h.Player.SetTask(18, 662, 0);
        var saved = PhoneMsgFeature.LoadStateStatic(h.Module.Store.Load(h.Player.Account)!);
        Assert.Contains(135, saved.ReceivedLetters);
        Assert.Contains(135, saved.CompletedLetters);
        // Event replay is automatic and must respect deletion; explicit history get restores it.
        await Send(h, 12);
        Assert.Empty(PhoneMsgFeature.LoadStateStatic(h.Player).Letters);
        await Send(h, 11);
        Assert.Contains(135, PhoneMsgFeature.LoadStateStatic(h.Player).Letters[1].Topics.Keys);
        Assert.DoesNotContain(135, PhoneMsgFeature.LoadStateStatic(h.Player).DeletedLetters);
    }

    [Theory]
    [InlineData(11, "135.5")]
    [InlineData(12, "135.5")]
    [InlineData(11, "\"135\"")]
    [InlineData(12, "\"135\"")]
    public async Task MalformedIdCannotReceive(int cmd, string id)
    {
        using var h = await FeatureHarness.CreateAsync("phone-access-invalid");
        h.Player.SetTask(18, 662, 200);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", $"{{\"nCmd\":{cmd},\"nMsgId\":{id}}}");
        Assert.Empty(PhoneMsgFeature.LoadStateStatic(h.Player).Letters);
    }
    private static Task Send(FeatureHarness h, int cmd) => h.LuaCalls.DispatchAsync(h.Session,
        "PhoneMsg", $"{{\"nCmd\":{cmd},\"nMsgId\":135}}");
}
