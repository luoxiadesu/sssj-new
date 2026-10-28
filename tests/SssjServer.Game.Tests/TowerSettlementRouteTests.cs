using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using Xunit;
namespace SssjServer.Game.Tests;
public class TowerSettlementRouteTests
{
    [Theory]
    [InlineData("{\"isPass\":true}", true)]
    [InlineData("{\"isPass\":false}", false)]
    public async Task ChallengeResultSurvivesRealSettlementEnvelope(string extra, bool expected)
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-real-result-" + expected);
        await h.DispatchLuaCallAsync(54, "[6,349,907,2153,1]");
        var tasks = h.Player.Tasks.OrderBy(t => t.Key).ToArray();
        await Settle(h, 55, $"[6,349,907,2153,1,{extra},null,[],12345]");
        var reply = Wrapped(h, 55);
        Assert.Null(reply["nError"]);
        Assert.Equal(expected, reply["isPass"]!.GetValue<bool>());
        Assert.IsType<JsonArray>(reply["tbData"]);
        if (!expected) Assert.Equal(tasks, h.Player.Tasks.OrderBy(t => t.Key).ToArray());
        else Assert.Equal(1, h.Player.GetTask(9, 45101));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"isPass\":0}")]
    [InlineData("{\"isPass\":1}")]
    [InlineData("{\"isPass\":\"false\"}")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task MissingOrNonBooleanChallengeResultCannotBecomeVictory(string extra)
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-invalid-result");
        await Settle(h, 55, $"[6,349,907,1,1,{extra}]");
        var reply = Wrapped(h, 55);
        Assert.NotNull(reply["nError"]);
        Assert.Null(reply["isPass"]);
    }

    private static string Args(int level,string extra="{}")=>$"[4,21,0,{level},1,{extra}]";
    private static Task Settle(FeatureHarness h,int command,string body)=>h.DispatchLuaCallAsync(252,
        new JsonObject{["sCmd"]=command,["tbParam"]=JsonNode.Parse(ChallengeTestHarness.WithToken(h,body))}.ToJsonString());
    private static JsonNode Wrapped(FeatureHarness h,int expected)
    {
        var root=JsonNode.Parse(h.LastOutbound.Json)!;
        Assert.Equal(252,root["sCmd"]!.GetValue<int>());
        Assert.Equal(expected,root["tbParam"]!["sCmd"]!.GetValue<int>());
        return root["tbParam"]!["tbParam"]!;
    }
    [Fact]
    public async Task RealClientEnvelopeCompletesTowerAndRetryCannotDuplicateRewards()
    {
        using var h=await ChallengeTestHarness.CreateAsync("tower-real-settle");h.Player.SetMoney(MoneyType.Vigour,100);
        h.Player.SetTask(96,1802,1);
        await h.DispatchLuaCallAsync(54,Args(2));
        await Settle(h,55,Args(2));Assert.Null(Wrapped(h,55)["nError"]);
        var balances=h.Player.Money.OrderBy(v=>v.Key).ToArray();int exp=h.Player.Exp;
        var first=h.LastOutbound.Json;
        await Settle(h,55,Args(2));Assert.Equal(first,h.LastOutbound.Json);
        Assert.Equal(balances,h.Player.Money.OrderBy(v=>v.Key));Assert.Equal(exp,h.Player.Exp);
        Assert.Equal(1,h.Player.GetTask(96,1803));
        Assert.Equal(1,new PlayerStore(h.Options).Load(h.Player.Account)!.GetTask(96,1803));
    }
    [Theory]
    [InlineData(55)]
    [InlineData(56)]
    public async Task ErrorsUseSettlementEnvelopeToo(int command)
    {
        using var h=await ChallengeTestHarness.CreateAsync($"tower-error-route-{command}");
        await Settle(h,command,Args(12));Assert.NotNull(Wrapped(h,command)["nError"]);
        Assert.Equal(0,h.Player.GetTask(96,1813));
    }
    [Fact]
    public async Task FailureViaRealEnvelopePreventsLateVictory()
    {
        using var h=await ChallengeTestHarness.CreateAsync("tower-fail-route");h.Player.SetMoney(MoneyType.Vigour,100);h.Player.SetTask(96,1812,1);
        await h.DispatchLuaCallAsync(54,Args(12));
        await Settle(h,56,Args(12));Assert.Null(Wrapped(h,56)["nError"]);
        await Settle(h,55,Args(12));Assert.NotNull(Wrapped(h,55)["nError"]);
        Assert.DoesNotContain(h.Player.Items,i=>i.Genre==1 && i.Detail==16 && i.Particular==34);
    }
    [Fact]
    public async Task RoutingContextDoesNotLeakToDirectCallsOrOtherHarnesses()
    {
        using var a=await ChallengeTestHarness.CreateAsync("tower-route-context-a");
        using var b=await ChallengeTestHarness.CreateAsync("tower-route-context-b");
        await Task.WhenAll(Settle(a,55,Args(12)),Settle(b,56,Args(12)));
        Wrapped(a,55);Wrapped(b,56);
        await a.DispatchLuaCallAsync(55,Args(12));
        Assert.Equal(55,JsonNode.Parse(a.LastOutbound.Json)!["sCmd"]!.GetValue<int>());
        await a.DispatchLuaCallAsync(53,"[21,4]");
        Assert.Equal(53,JsonNode.Parse(a.LastOutbound.Json)!["sCmd"]!.GetValue<int>());
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"sCmd\":252,\"tbParam\":{}}")]
    public async Task MalformedOrRecursiveSettlementCannotMutateProgress(string request)
    {
        using var h=await ChallengeTestHarness.CreateAsync("tower-invalid-envelope");
        var before=h.Player.Tasks.ToArray();
        await h.DispatchLuaCallAsync(252,request);
        Assert.Equal(before,h.Player.Tasks.ToArray());
        Assert.Empty(h.Outbound);
    }
}
