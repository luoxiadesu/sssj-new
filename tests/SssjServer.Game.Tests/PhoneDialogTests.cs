using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class PhoneDialogTests
{
    [Theory]
    [InlineData(42, 2)]
    [InlineData(41, 1)]
    [InlineData(43, 3)]
    public void AwardUsesLastRealReplyDespiteLegacyTrailingNotifications(int reply, int expected)
    {
        var topic = new TopicState(); topic.MsgIds.AddRange(new[] { 41, reply, 0, 0 });
        Assert.Equal(expected, PhoneDialog.AwardSelection(topic));
        Assert.Equal(new[] { 41, reply, 0, 0 }, topic.MsgIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroNotificationDoesNotMutateProgressTimeOrRewards(bool finished)
    {
        using var h = await FeatureHarness.CreateAsync("phone-zero-notification");
        h.Player.SetTask(18, 662, 200);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":11,\"nMsgId\":135}");
        if (finished) await Finish(h);
        var state = PhoneMsgFeature.LoadStateStatic(h.Player);
        state.Letters[1].CreateTime = 123;
        h.Player.Extra[PhoneMsgFeature.ExtraKey] = state.ToJson();
        h.Module.Store.Save(h.Player);
        string before = state.ToJson().ToJsonString();
        for (int i = 0; i < 3; i++)
            await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":3,\"nMsgId\":135,\"nSelectId\":0}");
        Assert.Equal(0, JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>());
        Assert.Equal(before, PhoneMsgFeature.LoadStateStatic(h.Player).ToJson().ToJsonString());
        Assert.Equal(before, PhoneMsgFeature.LoadStateStatic(h.Module.Store.Load(h.Player.Account)!).ToJson().ToJsonString());
        var cfg = PhoneMsgTable.Load(TestPaths.SettingDir).Letter(135)!;
        Assert.Equal(finished, PhoneDialog.Complete(cfg, PhoneMsgFeature.LoadStateStatic(h.Player).Letters[1].Topics[135]));
    }

    [Fact]
    public void StageEndDoesNotCompleteBeforePendingPlayerAndNullEndsOnlyMatchingBranch()
    {
        var config = new LetterTopic { Lines = new[] {
            new LetterLine { InfoType = 0, Info = new[] { "start" } },
            new LetterLine { InfoType = 2, Info = new[] { "a", "b" }, InfoNum = new[] { 1, 2 }, InfoFlag = 1 },
            new LetterLine { InfoType = 0, Info = new[] { "NULL" }, InfoNum = new[] { 1 } },
            new LetterLine { InfoType = 1, Info = new[] { "reply" }, InfoNum = new[] { 2 } },
            new LetterLine { InfoType = 0, Info = new[] { "end" }, InfoNum = new[] { 2 }, InfoFlag = 2 } } };
        var a = new TopicState(); a.MsgIds.Add(21);
        var b = new TopicState(); b.MsgIds.Add(22);
        Assert.True(PhoneDialog.Complete(config, a));
        Assert.False(PhoneDialog.Complete(config, b));
        b.MsgIds.Add(41);
        Assert.True(PhoneDialog.Complete(config, b));
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("\"1\"")]
    [InlineData("null")]
    [InlineData("2147483648")]
    public async Task MalformedReplyDoesNotChangeConversation(string selection)
    {
        using var h = await FeatureHarness.CreateAsync("phone-reply-params");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":11,\"nMsgId\":135}");
        string before = PhoneMsgFeature.LoadStateStatic(h.Player).ToJson().ToJsonString();
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", $"{{\"nCmd\":3,\"nMsgId\":135,\"nSelectId\":{selection}}}");
        Assert.True(JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>() > 0);
        Assert.Equal(before, PhoneMsgFeature.LoadStateStatic(h.Player).ToJson().ToJsonString());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(22)]
    public void LegacyReplyRepairsToPlayerLineAndIsIdempotent(int value)
    {
        var config = PhoneMsgTable.Load(TestPaths.SettingDir).Letter(135)!;
        var topic = new TopicState();
        topic.MsgIds.AddRange(new[] { 0, value });
        Assert.True(PhoneDialog.TryRepairLegacy(config, topic, out var repaired));
        Assert.Equal(new[] { 0, 42 }, repaired);
        topic.MsgIds.Clear(); topic.MsgIds.AddRange(repaired);
        Assert.False(PhoneDialog.TryRepairLegacy(config, topic, out _));
    }

    [Theory]
    [InlineData(29)]
    [InlineData(9991)]
    [InlineData(-2)]
    public void AmbiguousLegacyReplyIsPreserved(int value)
    {
        var config = PhoneMsgTable.Load(TestPaths.SettingDir).Letter(135)!;
        var topic = new TopicState(); topic.MsgIds.AddRange(new[] { 22, value });
        Assert.False(PhoneDialog.TryRepairLegacy(config, topic, out _));
        Assert.Equal(new[] { 22, value }, topic.MsgIds);
    }

    [Fact]
    public async Task ReplyCannotCreateUnreceivedTopic()
    {
        using var h = await FeatureHarness.CreateAsync("phone-reply-unreceived");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":3,\"nMsgId\":135,\"nSelectId\":1}");
        Assert.True(JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>() > 0);
        Assert.Empty(PhoneMsgFeature.LoadStateStatic(h.Player).Letters);
    }
    internal static async Task Finish(FeatureHarness h, int id = 135)
    {
        var config = PhoneMsgTable.Load(TestPaths.SettingDir).Letter(id)!;
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", $"{{\"nCmd\":11,\"nMsgId\":{id}}}");
        for (int i = 0; i < config.Lines.Count; i++)
        {
            var topic = PhoneMsgFeature.LoadStateStatic(h.Player).Letters[config.NpcId].Topics[id];
            if (PhoneDialog.Complete(config, topic)) return;
            await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", $"{{\"nCmd\":3,\"nMsgId\":{id},\"nSelectId\":1}}");
        }
        Assert.Fail("Conversation did not reach its configured end");
    }

    [Fact]
    public async Task UnfinishedAwardFailsAndDeletedClaimSurvivesReloadAndReacquisition()
    {
        using var h = await FeatureHarness.CreateAsync("phone-award-history");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        h.Player.Girls.Clear();
        var girl = new Girl { Girlid = 1 };
        h.Player.Girls.Add(girl);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":11,\"nMsgId\":135}");
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":7,\"nMsgId\":135}");
        Assert.Equal(0u, girl.Exp);
        Assert.False(PhoneMsgFeature.LoadStateStatic(h.Player).ClaimedLetters.Contains(135));
        await Finish(h);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":7,\"nMsgId\":135}");
        uint earned = girl.Exp;
        Assert.True(earned > 0);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":10,\"nMsgId\":135}");
        Assert.Contains(135, PhoneMsgFeature.LoadStateStatic(h.Module.Store.Load(h.Player.Account)!).ClaimedLetters);
        await Finish(h);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":7,\"nMsgId\":135}");
        Assert.Equal(earned, girl.Exp);
        Assert.True(JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>() > 0);
    }
    [Fact]
    public void RealTopicReplyTargetsPlayerChoiceInsteadOfNpcLine()
    {
        var config = PhoneMsgTable.Load(TestPaths.SettingDir).Letter(135)!;
        var topic = new TopicState { TopicId = 135 };
        Assert.False(PhoneDialog.Complete(config, topic));
        int reply = PhoneMsgFeature.BuildReplyId(config, topic, 2);
        Assert.Equal(4, PhoneMsgTable.ReplyPosOf(reply));
        topic.MsgIds.Add(reply);
        Assert.False(PhoneDialog.Complete(config, topic));
        int next = PhoneMsgFeature.BuildReplyId(config, topic, 1);
        int pos = PhoneMsgTable.ReplyPosOf(next);
        Assert.Equal(1, config.Lines[pos - 1].InfoType);
        Assert.Equal(2, config.Lines[pos - 1].InfoNum[0]);
    }

    [Fact]
    public void NpcTailCanFinishButPendingPlayerLineCannot()
    {
        var config = new LetterTopic { Lines = new[] {
            new LetterLine { InfoType = 0 }, new LetterLine { InfoType = 1 },
            new LetterLine { InfoType = 0, InfoFlag = 2 } } };
        var topic = new TopicState();
        Assert.False(PhoneDialog.Complete(config, topic));
        topic.MsgIds.Add(21);
        Assert.True(PhoneDialog.Complete(config, topic));
    }

    [Fact]
    public async Task OutOfRangeChoiceLeavesExistingConversationUnchanged()
    {
        using var h = await FeatureHarness.CreateAsync("phone-dialog-invalid");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":11,\"nMsgId\":135}");
        string before = PhoneMsgFeature.LoadStateStatic(h.Player).ToJson().ToJsonString();
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":3,\"nMsgId\":135,\"nSelectId\":99}");
        Assert.True(JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>() > 0);
        Assert.Equal(before, PhoneMsgFeature.LoadStateStatic(h.Player).ToJson().ToJsonString());
    }
}
