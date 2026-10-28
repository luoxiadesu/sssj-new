using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class ChallengeExScoreTests
{
    private static ChallengeScores Scores() => new(TestPaths.SettingDir);
    private static JsonNode Reply(FeatureHarness h) => JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
    private static Player Current(FeatureHarness h) => (Player)h.Session.Tag!;
    private static async Task<FeatureHarness> Harness(string name)
    {
        var h = await ChallengeTestHarness.CreateAsync(name);
        for (int task = 45201; task <= 45203; task++) h.Player.SetTask(9, task, 1);
        return h;
    }
    private static string Stats(bool passed, string points) => $"[6,349,507,3018,1,{{\"isPass\":{(passed ? "true" : "false")},\"monPoint\":{points}}}]";
    private static Task Settle(FeatureHarness h, bool passed, string points) => ChallengeTestHarness.SettleAsync(h,55,Stats(passed,points));
    private static string Choice(string token, bool keep) => new JsonObject { ["battleToken"]=token,["keep"]=keep }.ToJsonString();

    [Theory]
    [InlineData(true,"0",0)]
    [InlineData(true,"1",1)]
    [InlineData(true,"9",9)]
    [InlineData(true,"2999",2999)]
    [InlineData(true,"3000",3000)]
    [InlineData(false,"0",0)]
    [InlineData(false,"0.5",1)]
    [InlineData(false,"4.5",5)]
    [InlineData(false,"40",40)]
    [InlineData(false,"1499.5",1500)]
    [InlineData(false,"1500",1500)]
    public void NativePointsAreFinalUnitsAndFailureIsNotHalvedAgain(bool passed,string points,int expected)
    {
        var statistics=JsonNode.Parse($"{{\"monPoint\":{points},\"leftTime\":90,\"leftHp\":0,\"leftShd\":0,\"maxHp\":1,\"mxShd\":0,\"nDamage\":999999999}}")!.AsObject();
        foreach (int teamSize in new[] {1,2,3})
        {
            Assert.Equal(ChallengeScores.Status.Available,Scores().Evaluate(507,3018,statistics,passed,out var result,teamSize));
            Assert.Equal(expected,result!.Score);
            Assert.Equal(0,result.Detail);
        }
    }

    [Theory]
    [InlineData(true,"3001")]
    [InlineData(false,"1500.5")]
    [InlineData(false,"3000")]
    [InlineData(true,"0.5")]
    [InlineData(false,"0.25")]
    [InlineData(false,"-1")]
    [InlineData(false,"null")]
    [InlineData(false,"true")]
    [InlineData(false,"\"40\"")]
    [InlineData(false,"{}")]
    [InlineData(false,"1e999")]
    public void ImpossibleOrMalformedNativeStatisticsAreRejected(bool passed,string points)
        => Assert.Equal(ChallengeScores.Status.Invalid,Scores().Evaluate(507,3018,JsonNode.Parse($"{{\"monPoint\":{points}}}")!.AsObject(),passed,out _));

    [Fact]
    public void MissingPointsRemainUnavailableAndOtherModesDoNotUseTheExFormula()
    {
        Assert.Equal(ChallengeScores.Status.Unavailable,Scores().Evaluate(507,3018,new JsonObject(),true,out _));
        Assert.Equal(ChallengeScores.Status.Unavailable,Scores().Evaluate(507,3007,new JsonObject { ["monPoint"]=3000 },true,out _));
        Assert.Equal(ChallengeScores.Status.Unavailable,Scores().Evaluate(907,2153,new JsonObject { ["monPoint"]=3000 },true,out _));
    }

    [Theory]
    [InlineData(true,"3000",3000)]
    [InlineData(false,"40",40)]
    [InlineData(false,"4.5",5)]
    public async Task ExResultSurvivesReloadAndKeepSpendsOnceWithoutFailureFirstClear(bool passed,string points,int expected)
    {
        using var h=await Harness("ex-score-reload");
        await h.DispatchLuaCallAsync(54,"[6,349,507,3018,1]");
        Assert.Null(Reply(h)["nError"]);
        await Settle(h,passed,points);
        Assert.Null(Reply(h)["nError"]);
        Assert.True(Reply(h)["scoreAvailable"]!.GetValue<bool>());
        Assert.Equal(expected,Reply(h)["SingleScore"]!.GetValue<int>());
        Assert.Equal(passed,Reply(h)["isPass"]!.GetValue<bool>());
        Assert.Equal(passed ? 1 : 0,Current(h).GetTask(9,45204));
        Assert.Equal(0,Current(h).GetTask(9,46204));
        Assert.Equal(3,Current(h).GetTask(9,45900));
        string token=Reply(h)["battleToken"]!.GetValue<string>();
        h.Session.Tag=h.Module.Store.Load(h.Player.Account)!;
        await h.DispatchLuaCallAsync(53,"[349,6]");
        Assert.NotNull(Reply(h)["pendingScore"]);
        await h.DispatchLuaCallAsync(207,Choice(token,true));
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(expected,Current(h).GetTask(9,46204));
        Assert.Equal(expected,Current(h).GetTask(9,45901));
        Assert.Equal(2,Current(h).GetTask(9,45900));
        string saved=JsonSerializer.Serialize(Current(h));
        await h.DispatchLuaCallAsync(207,Choice(token,true));
        Assert.Equal(saved,JsonSerializer.Serialize(Current(h)));
        Assert.Equal(expected,h.Module.Store.Load(h.Player.Account)!.GetTask(9,46204));
    }

    [Fact]
    public async Task InvalidExResultLeavesRunOpenForCorrectedRetry()
    {
        using var h=await Harness("ex-score-invalid-retry");
        await h.DispatchLuaCallAsync(54,"[6,349,507,3018,1]");
        string before=JsonSerializer.Serialize(Current(h));
        await Settle(h,false,"3000");
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(before,JsonSerializer.Serialize(Current(h)));
        await Settle(h,false,"1500");
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(1500,Reply(h)["SingleScore"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(505,3016)]
    [InlineData(503,3017)]
    [InlineData(507,3018)]
    [InlineData(504,3019)]
    [InlineData(506,3020)]
    public void AllFiveInstalledExAttributeVariantsUseTheirNativeCap(int node,int level)
    {
        Assert.Equal(ChallengeScores.Status.Available,Scores().Evaluate(node,level,new JsonObject { ["monPoint"]=3000 },true,out var result));
        Assert.Equal(3000,result!.Score);
        Assert.Equal(ChallengeScores.Status.Available,Scores().Evaluate(node,level,new JsonObject { ["monPoint"]=1500 },false,out result));
        Assert.Equal(1500,result!.Score);
    }

    [Fact]
    public async Task FailedExSettlementSaveRestoresRunAndRetryCreatesOnePendingResult()
    {
        using var h=await Harness("ex-score-io-retry");
        await h.DispatchLuaCallAsync(54,"[6,349,507,3018,1]");
        string before=JsonSerializer.Serialize(Current(h));
        string path=Path.Combine(h.Module.Store.DirectoryPath,h.Player.Account+".player.json");
        string disk=File.ReadAllText(path);
        Directory.CreateDirectory(path+".tmp");
        try { await Settle(h,true,"3000"); }
        finally { Directory.Delete(path+".tmp"); }
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(before,JsonSerializer.Serialize(Current(h)));
        Assert.Equal(disk,File.ReadAllText(path));
        await Settle(h,true,"3000");
        Assert.Equal(3000,Reply(h)["SingleScore"]!.GetValue<int>());
        string token=Reply(h)["battleToken"]!.GetValue<string>();
        string settled=JsonSerializer.Serialize(Current(h));
        await Settle(h,true,"3000");
        Assert.Equal(settled,JsonSerializer.Serialize(Current(h)));
        await h.DispatchLuaCallAsync(207,Choice(token,false));
        Assert.Equal(0,Current(h).GetTask(9,46204));
        Assert.Equal(3,Current(h).GetTask(9,45900));
        Assert.Equal(1,Current(h).GetTask(9,45204));
    }
}
