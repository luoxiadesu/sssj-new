using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;
public class Tower21AttemptTests
{
    private const string Entry="[4,21,0,2,1]";
    private static JsonNode Reply(FeatureHarness h)=>JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
    private static JsonObject Run(FeatureHarness h)=>(JsonObject)((Player)h.Session.Tag!).Extra[TwentyFirstTowerHandler.AttemptKey]!;
    private static string Token(FeatureHarness h)=>Run(h)["Token"]!.GetValue<string>();
    private static string Args(string? token)
    {
        var a=(JsonArray)JsonNode.Parse(Entry)!;a.Add(new JsonObject());while(a.Count<10)a.Add(null);a[9]=token;return a.ToJsonString();
    }
    private static Task Cancel(FeatureHarness h,string token)=>h.DispatchFunctionAsync("Tower21Cancel",new JsonObject{["battleToken"]=token}.ToJsonString());
    [Fact]
    public async Task ExitRefundsActualCostOnceAcrossReloadAndOldCancelDoesNotTouchNewRun()
    {
        using var h=await FeatureHarness.CreateAsync("tower21-cancel-refund");
        h.Player.SetTask(96,1802,1);h.Player.SetMoney(MoneyType.Vigour,100);
        await h.DispatchLuaCallAsync(54,Entry);string old=Token(h);Assert.Equal(90,h.Player.Vigour);
        h.Session.Tag=h.Module.Store.Load(h.Player.Account)!;
        await Cancel(h,old);await Cancel(h,old);
        var p=(Player)h.Session.Tag;Assert.Equal(100,p.Vigour);Assert.False(Run(h)["Active"]!.GetValue<bool>());
        await h.DispatchLuaCallAsync(55,Args(old));Assert.NotNull(Reply(h)["nError"]);Assert.Equal(0,p.GetTask(96,1803));
        await h.DispatchLuaCallAsync(54,Entry);string current=Token(h);Assert.NotEqual(old,current);Assert.Equal(90,p.Vigour);
        await Cancel(h,old);Assert.True(Run(h)["Active"]!.GetValue<bool>());Assert.Equal(90,p.Vigour);
        await h.DispatchLuaCallAsync(55,Args(current));Assert.Null(Reply(h)["nError"]);
        await Cancel(h,current);Assert.Equal(90,p.Vigour);
        Assert.Equal(90,h.Module.Store.Load(p.Account)!.Vigour);
    }
    [Fact]
    public async Task FailureConsumesCostButLateOldOutcomeCannotCloseReenteredRun()
    {
        using var h=await FeatureHarness.CreateAsync("tower21-token-boundary");
        h.Player.SetTask(96,1802,1);h.Player.SetMoney(MoneyType.Vigour,100);
        await h.DispatchLuaCallAsync(54,Entry);string old=Token(h);
        await h.DispatchLuaCallAsync(56,Args(old));string failure=h.LastOutbound.Json;
        await h.DispatchLuaCallAsync(56,Args(old));Assert.Equal(failure,h.LastOutbound.Json);
        await Cancel(h,old);Assert.Equal(90,h.Player.Vigour);
        await h.DispatchLuaCallAsync(54,Entry);string current=Token(h);
        await h.DispatchLuaCallAsync(55,Args(old));Assert.NotNull(Reply(h)["nError"]);
        await h.DispatchLuaCallAsync(56,Args(old));Assert.NotNull(Reply(h)["nError"]);
        Assert.True(Run(h)["Active"]!.GetValue<bool>());
        await h.DispatchLuaCallAsync(55,Args(current));Assert.Null(Reply(h)["nError"]);Assert.Equal(80,h.Player.Vigour);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("bad")]
    [InlineData("00000000000000000000000000000000")]
    public async Task InvalidTokenCannotFinishOrMutateAttempt(string? token)
    {
        using var h=await FeatureHarness.CreateAsync("tower21-bad-token");
        h.Player.SetTask(96,1802,1);h.Player.SetMoney(MoneyType.Vigour,100);
        await h.DispatchLuaCallAsync(54,Entry);string before=Run(h).ToJsonString();
        await h.DispatchLuaCallAsync(55,Args(token));Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(before,Run(h).ToJsonString());Assert.Equal(90,h.Player.Vigour);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelNeverRefundsMoreThanTheActualCharge(bool spend)
    {
        using var h=await FeatureHarness.CreateAsync("tower21-actual-charge-"+spend);
        h.Options.SpendVigour=spend;h.Player.SetTask(96,1802,1);h.Player.SetMoney(MoneyType.Vigour,100);
        await h.DispatchLuaCallAsync(54,Entry);Assert.Equal(spend?90:100,h.Player.Vigour);
        await Cancel(h,Token(h));Assert.Equal(100,h.Player.Vigour);
    }
}
