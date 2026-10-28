using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;
public class PhoneWindowRewardTests
{
    [Theory]
    [InlineData("2026102508-2026102510", 7, false)]
    [InlineData("2026102508-2026102510", 8, true)]
    [InlineData("2026102508-2026102510", 10, true)]
    [InlineData("2026102508-2026102510", 11, false)]
    [InlineData("2026023008-2026122510", 9, false)]
    [InlineData("2026102511-2026102508", 9, false)]
    [InlineData("2026102508-2026102510-extra", 9, false)]
    [InlineData("2026102525-2026102625", 9, false)]
    public void WindowUsesValidCalendarHours(string window, int hour, bool expected)
        => Assert.Equal(expected, PhoneEventWindow.Contains(window, new DateTime(2026, 10, 25, hour, 59, 59)));

    [Theory]
    [InlineData("135.5")]
    [InlineData("\"135\"")]
    public async Task MalformedAwardIdCannotClaim(string id)
    {
        using var h = await FeatureHarness.CreateAsync("phone-award-id");
        h.Player.SetTask(18, 662, 200);
        await PhoneDialogTests.Finish(h);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", $"{{\"nCmd\":7,\"nMsgId\":{id}}}");
        Assert.True(JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>() > 0);
        Assert.DoesNotContain(135, PhoneMsgFeature.LoadStateStatic(h.Player).ClaimedLetters);
    }

    [Fact]
    public async Task MaxLevelAwardPreservesAccumulatedExperienceAndCannotBeClaimedTwice()
    {
        using var h = await FeatureHarness.CreateAsync("phone-max-reward");
        h.Player.SetTask(18, 662, 200);
        h.Player.Girls.Clear();
        var girl = new Girl { Girlid = 1, Level = 100, Exp = 3456 };
        h.Player.Girls.Add(girl);
        await PhoneDialogTests.Finish(h);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":7,\"nMsgId\":135}");
        Assert.Contains(135, PhoneMsgFeature.LoadStateStatic(h.Player).ClaimedLetters);
        Assert.Equal(3456u, girl.Exp);
        Assert.Equal(3456u, h.Module.Store.Load(h.Player.Account)!.GetGirl(1)!.Exp);
        await h.LuaCalls.DispatchAsync(h.Session, "PhoneMsg", "{\"nCmd\":7,\"nMsgId\":135}");
        Assert.True(JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>() > 0);
        Assert.Equal(3456u, girl.Exp);
    }
}
