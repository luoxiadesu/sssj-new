using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class PhoneMsgOnlineTests
{
    [Fact]
    public async Task OnlineAffectionThresholdDeliversAndPersistsTopicWithoutRelogin()
    {
        using var h = await FeatureHarness.CreateAsync("phone-online-love");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        var table = PhoneMsgTable.Load(TestPaths.SettingDir);
        var ev = table.AllLetterEvents.First(e => e.Conds.Count == 1 && e.Conds[0].Type == 3 &&
            table.Letter(e.Id) is { NpcId: > 0 });
        int npc = table.Letter(ev.Id)!.NpcId;
        int need = int.Parse(ev.Conds[0].Info.Trim().Trim('[', ']'));
        h.Player.Girls.Clear();
        var girl = new Girl { Girlid = (uint)npc, Level = 1 };
        h.Player.Girls.Add(girl);
        var feature = (IGamePulseFeature)h.Module.Features.OfType<PhoneMsgFeature>().Single();
        feature.OnPlayerPulse(h.Session, h.Player);
        Assert.False(PhoneMsgFeature.LoadStateStatic(h.Player).Letters.TryGetValue(npc, out var old) && old.Topics.ContainsKey(ev.Id));
        h.Outbound.Clear();
        girl.Level = (uint)need;
        feature.OnPlayerPulse(h.Session, h.Player);
        Assert.True(PhoneMsgFeature.LoadStateStatic(h.Player).Letters[npc].Topics.ContainsKey(ev.Id));
        Assert.True(PhoneMsgFeature.LoadStateStatic(h.Module.Store.Load(h.Player.Account)!).Letters[npc].Topics.ContainsKey(ev.Id));
        var notices = h.Outbound.Where(o => o.Function == PhoneMsgFeature.DownFunction)
            .Select(o => JsonNode.Parse(o.Json)!).Where(o => o["nCmd"]!.GetValue<int>() == 3 &&
                o["nMsgId"]!.GetValue<int>() == ev.Id).ToList();
        Assert.Single(notices);
        Assert.Equal(npc, notices[0]["nNpcId"]!.GetValue<int>());
        Assert.Equal(0, notices[0]["nSelectId"]!.GetValue<int>());
        Assert.Empty(PhoneMsgFeature.LoadStateStatic(h.Player).Letters[npc].Topics[ev.Id].MsgIds);
        Assert.True(await h.WaitForPfAsync(GamePf.PhoneMsgNtf));
        int sent = h.Outbound.Count;
        feature.OnPlayerPulse(h.Session, h.Player);
        Assert.Single(PhoneMsgFeature.LoadStateStatic(h.Player).Letters[npc].Topics.Values, t => t.TopicId == ev.Id);
        Assert.Equal(sent, h.Outbound.Count);
    }

    [Fact]
    public async Task AwardCannotCreateUnreceivedTopicAndConcurrentClaimsGrantOnce()
    {
        using var h = await FeatureHarness.CreateAsync("phone-online-award");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        h.Player.Girls.Clear();
        var girl = new Girl { Girlid = 1 };
        h.Player.Girls.Add(girl);
        await Send(h, 7);
        Assert.Empty(PhoneMsgFeature.LoadStateStatic(h.Player).Letters);
        Assert.Equal(0u, girl.Exp);
        await Send(h, 11);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":3,\"nMsgId\":135,\"nSelectId\":1}");
        await PhoneDialogTests.Finish(h);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Send(h, 7))));
        Assert.Equal(10u, girl.Exp);
        Assert.Equal(1, PhoneMsgFeature.LoadStateStatic(h.Player).Letters[1].Topics[135].AwardFlag);
    }

    private static Task Send(FeatureHarness h, int cmd) => h.LuaCalls.DispatchAsync(h.Session,
        "PhoneMsg", $"{{\"nCmd\":{cmd},\"nMsgId\":135}}");
}
