using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;
public class PhoneBbsBoundaryTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LegacyHiddenChoiceCannotUnlockNewPostButHistoryIsPreserved(int choice)
    {
        using var h = await FeatureHarness.CreateAsync("bbs-legacy-hidden");
        var state = new PhoneState(); state.Bbs[90] = new BbsState { TopicId = 90, ReplyInfo = choice };
        h.Player.Extra[PhoneMsgFeature.ExtraKey] = state.ToJson();
        var pulse = (IGamePulseFeature)h.Module.Features.OfType<PhoneMsgFeature>().Single();
        pulse.OnPlayerPulse(h.Session, h.Player);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":13,\"nMsgId\":94}");
        var after = PhoneMsgFeature.LoadStateStatic(h.Player);
        Assert.DoesNotContain(94, after.Bbs.Keys);
        Assert.Equal(choice, after.Bbs[90].ReplyInfo);
        // Already received content is historical state, not revoked by new validation.
        after.Bbs[94] = new BbsState { TopicId = 94 };
        h.Player.Extra[PhoneMsgFeature.ExtraKey] = after.ToJson();
        pulse.OnPlayerPulse(h.Session, h.Player);
        Assert.Contains(94, PhoneMsgFeature.LoadStateStatic(h.Player).Bbs.Keys);
    }

    [Fact]
    public async Task BbsPredecessorRequiresReplyAndOnlineDeliveryNotifiesOnlyOnce()
    {
        using var h = await FeatureHarness.CreateAsync("bbs-predecessor-refresh");
        h.Player.Girls.Clear(); h.Player.Girls.Add(new Girl { Girlid = 14, Level = 9 });
        var pulse = (IGamePulseFeature)h.Module.Features.OfType<PhoneMsgFeature>().Single();
        pulse.OnPlayerPulse(h.Session, h.Player);
        var before = PhoneMsgFeature.LoadStateStatic(h.Player);
        Assert.Contains(90, before.Bbs.Keys);
        Assert.DoesNotContain(94, before.Bbs.Keys);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":13,\"nMsgId\":94}");
        Assert.DoesNotContain(94, PhoneMsgFeature.LoadStateStatic(h.Player).Bbs.Keys);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":1,\"nMsgId\":90,\"nSelectId\":1}");
        h.Outbound.Clear();
        pulse.OnPlayerPulse(h.Session, h.Player);
        Assert.Contains(94, PhoneMsgFeature.LoadStateStatic(h.Player).Bbs.Keys);
        Assert.Contains(94, PhoneMsgFeature.LoadStateStatic(h.Module.Store.Load(h.Player.Account)!).Bbs.Keys);
        var notice = Assert.Single(h.Outbound.Where(o => o.Function == PhoneMsgFeature.DownFunction)
            .Select(o => JsonNode.Parse(o.Json)!).Where(o => o["nCmd"]!.GetValue<int>() == 1 && o["nMsgId"]!.GetValue<int>() == 94));
        Assert.Equal(15, notice["nNpcId"]!.GetValue<int>());
        Assert.Equal(0, notice["nSelectId"]!.GetValue<int>());
        Assert.Equal(0, PhoneMsgFeature.LoadStateStatic(h.Player).Bbs[94].ReplyInfo);
        int count = h.Outbound.Count;
        pulse.OnPlayerPulse(h.Session, h.Player);
        Assert.Equal(count, h.Outbound.Count);
    }

    [Fact]
    public async Task ReplyCannotCreateUnreceivedPostAndEventRequiresRealCondition()
    {
        using var h = await FeatureHarness.CreateAsync("bbs-receive-auth");
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":1,\"nMsgId\":1,\"nSelectId\":1}");
        Assert.Empty(PhoneMsgFeature.LoadStateStatic(h.Player).Bbs);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":13,\"nMsgId\":1}");
        Assert.Empty(PhoneMsgFeature.LoadStateStatic(h.Player).Bbs);
        h.Player.Levels[SssjServer.Game.Config.ChapterTable.Load(TestPaths.SettingDir).Get(2, 6, 1)!.MapId] = 7;
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":13,\"nMsgId\":1}");
        Assert.Contains(1, PhoneMsgFeature.LoadStateStatic(h.Player).Bbs.Keys);
        h.Player.Levels.Clear();
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":13,\"nMsgId\":1}");
        Assert.Equal(0, JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task HiddenPlaceholderCannotConsumeReplyOrAward(int choice)
    {
        using var h = await FeatureHarness.CreateAsync("bbs-placeholder");
        var girl = new Girl { Girlid = 7 }; h.Player.Girls.Add(girl);
        var received = new PhoneState(); received.Bbs[1] = new BbsState { TopicId = 1 };
        h.Player.Extra[PhoneMsgFeature.ExtraKey] = received.ToJson();
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", $"{{\"nCmd\":1,\"nMsgId\":1,\"nSelectId\":{choice}}}");
        Assert.True(JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>() > 0);
        Assert.Equal(0, PhoneMsgFeature.LoadStateStatic(h.Player).Bbs[1].ReplyInfo);
        Assert.Equal(0u, girl.Exp);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":1,\"nMsgId\":1,\"nSelectId\":2}");
        Assert.Equal(2, PhoneMsgFeature.LoadStateStatic(h.Player).Bbs[1].ReplyInfo);
        Assert.Equal(10u, girl.Exp);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("\"1\"")]
    [InlineData("-1")]
    [InlineData("99")]
    [InlineData("0")]
    [InlineData("null")]
    public async Task InvalidReplyCannotCreateOrReward(string selection)
    {
        using var h = await FeatureHarness.CreateAsync("bbs-invalid-reply");
        var girl = new Girl { Girlid = 7 }; h.Player.Girls.Add(girl);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", $"{{\"nCmd\":1,\"nMsgId\":1,\"nSelectId\":{selection}}}");
        Assert.True(JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>() > 0);
        Assert.Empty(PhoneMsgFeature.LoadStateStatic(h.Player).Bbs);
        Assert.Equal(0u, girl.Exp);
    }
    [Fact]
    public async Task ConcurrentRepliesGrantOnlyOnceAndSurviveReload()
    {
        using var h = await FeatureHarness.CreateAsync("bbs-repeat-reply");
        var girl = new Girl { Girlid = 7 }; h.Player.Girls.Add(girl);
        var received = new PhoneState(); received.Bbs[1] = new BbsState { TopicId = 1 };
        h.Player.Extra[PhoneMsgFeature.ExtraKey] = received.ToJson();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":1,\"nMsgId\":1,\"nSelectId\":2}"))));
        Assert.Equal(10u, girl.Exp);
        Assert.Equal(2, PhoneMsgFeature.LoadStateStatic(h.Module.Store.Load(h.Player.Account)!).Bbs[1].ReplyInfo);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":1,\"nMsgId\":1,\"nSelectId\":2}");
        Assert.Equal(10u, girl.Exp);
        Assert.Equal(2, PhoneMsgFeature.LoadStateStatic(h.Player).Bbs[1].ReplyInfo);
    }
}
