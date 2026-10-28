using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;
public class PhoneTriggerPrecisionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InformationTriggerRequiresExactGirlsSecret(bool ownTarget, bool unlockTarget)
    {
        using var h = await FeatureHarness.CreateAsync("phone-secret-condition");
        h.Player.Girls.Clear();
        h.Player.Girls.Add(new Girl { Girlid = 12 });
        if (ownTarget) h.Player.Girls.Add(new Girl { Girlid = 16 });
        // Unlocking the same secret on girl12 must never satisfy girl16's condition.
        h.Player.SetTask(GirlLoveFeature.VarGroup(12), GirlLoveFeature.VarTaskId(12, 111), 1);
        if (unlockTarget)
            h.Player.SetTask(GirlLoveFeature.VarGroup(16), GirlLoveFeature.VarTaskId(16, 111), 1);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":12,\"nMsgId\":219}");
        var state = PhoneMsgFeature.LoadStateStatic(h.Player);
        Assert.Equal(ownTarget && unlockTarget,
            state.Letters.TryGetValue(16, out var letter) && letter.Topics.ContainsKey(219));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(198, false)]
    [InlineData(199, false)]
    [InlineData(200, true)]
    [InlineData(201, true)]
    public async Task AchievementRequiresFullDecodedProgressRegardlessOfClaimBit(int stored, bool expected)
    {
        using var h = await FeatureHarness.CreateAsync("phone-achievement-threshold");
        h.Player.SetTask(18, 662, stored);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":11,\"nMsgId\":135}");
        var state = PhoneMsgFeature.LoadStateStatic(h.Player);
        Assert.Equal(expected, state.Letters.TryGetValue(1, out var letter) && letter.Topics.ContainsKey(135));
    }

    [Fact]
    public async Task ZeroQuantityCardCannotTriggerButPositiveQuantityCan()
    {
        using var h = await FeatureHarness.CreateAsync("phone-card-quantity");
        var table = PhoneMsgTable.Load(TestPaths.SettingDir);
        var ev = table.AllLetterEvents.First(e => e.Conds.Count == 1 && e.Conds[0].Type == 6 && table.Letter(e.Id) != null);
        int npc = table.Letter(ev.Id)!.NpcId;
        var gdpl = ev.Conds[0].Info.Trim().Trim('[', ']').Split('-').Select(uint.Parse).ToArray();
        h.Player.Items.Clear();
        var card = new Item { Genre = gdpl[0], Detail = gdpl[1], Particular = gdpl[2], Level = gdpl[3], Count = 0 };
        h.Player.Items.Add(card);
        var pulse = (IGamePulseFeature)h.Module.Features.OfType<PhoneMsgFeature>().Single();
        pulse.OnPlayerPulse(h.Session, h.Player);
        var before = PhoneMsgFeature.LoadStateStatic(h.Player);
        Assert.False(before.Letters.TryGetValue(npc, out var letter) && letter.Topics.ContainsKey(ev.Id));
        card.Count = 1;
        pulse.OnPlayerPulse(h.Session, h.Player);
        Assert.Contains(ev.Id, PhoneMsgFeature.LoadStateStatic(h.Player).Letters[npc].Topics.Keys);
    }
}
