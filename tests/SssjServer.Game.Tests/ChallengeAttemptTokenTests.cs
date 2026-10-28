using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;

public class ChallengeAttemptTokenTests
{
    private const string Entry="[6,349,907,2153,1]";
    private static JsonNode Reply(FeatureHarness h)=>JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
    private static JsonObject Run(FeatureHarness h)=>(JsonObject)((Player)h.Session.Tag!).Extra[ChallengeRuns.AttemptKey]!;
    private static string Token(FeatureHarness h)=>Run(h)["battleToken"]!.GetValue<string>();
    private static string Result(string? token,bool win=true)
    {
        var a=(JsonArray)JsonNode.Parse(Entry)!;var extra=new JsonObject{["isPass"]=win};
        if(token is not null)extra["battleToken"]=token;a.Add(extra);return a.ToJsonString();
    }
    [Fact]
    public async Task OldSettlementCannotFinishNewRunOfSameLevel()
    {
        using var h=await ChallengeTestHarness.CreateAsync("challenge-old-attempt");
        await h.DispatchLuaCallAsync(54,Entry);string old=Token(h);
        await h.DispatchLuaCallAsync(55,Result(old,false));
        await h.DispatchLuaCallAsync(54,Entry);string current=Token(h);Assert.NotEqual(old,current);
        await h.DispatchLuaCallAsync(55,Result(old));Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(old,Reply(h)["battleToken"]!.GetValue<string>());
        Assert.True(Run(h)["active"]!.GetValue<bool>());Assert.Equal(0,h.Player.GetTask(9,45101));
        await h.DispatchLuaCallAsync(55,Result(current));Assert.True(Reply(h)["isPass"]!.GetValue<bool>());
    }
    [Fact]
    public async Task CancelIsPersistentAndOldCancelDoesNotCloseNewAttempt()
    {
        using var h=await ChallengeTestHarness.CreateAsync("challenge-cancel-token");
        await h.DispatchLuaCallAsync(54,Entry);string old=Token(h);
        await h.DispatchFunctionAsync("ChallengeCancel",new JsonObject{["battleToken"]=old}.ToJsonString());
        await h.DispatchFunctionAsync("ChallengeCancel",new JsonObject{["battleToken"]=old}.ToJsonString());
        h.Session.Tag=h.Module.Store.Load(h.Player.Account)!;Assert.False(Run(h)["active"]!.GetValue<bool>());
        Assert.Equal(3,((Player)h.Session.Tag).GetTask(9,45900));
        await h.DispatchLuaCallAsync(55,Result(old));Assert.NotNull(Reply(h)["nError"]);
        await h.DispatchLuaCallAsync(54,Entry);string current=Token(h);Assert.NotEqual(old,current);
        await h.DispatchFunctionAsync("ChallengeCancel",new JsonObject{["battleToken"]=old}.ToJsonString());
        Assert.True(Run(h)["active"]!.GetValue<bool>());
        await h.DispatchLuaCallAsync(55,Result(current));Assert.Null(Reply(h)["nError"]);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("00000000000000000000000000000000")]
    public async Task MissingMalformedOrWrongTokenDoesNotConsumeRun(string? token)
    {
        using var h=await ChallengeTestHarness.CreateAsync("challenge-invalid-token");
        await h.DispatchLuaCallAsync(54,Entry);await h.DispatchLuaCallAsync(55,Result(token));
        Assert.NotNull(Reply(h)["nError"]);Assert.True(Run(h)["active"]!.GetValue<bool>());
        Assert.Equal(0,h.Player.GetTask(9,45101));Assert.Equal(3,h.Player.GetTask(9,45900));
    }
    [Fact]
    public async Task EntryRetryPreservesTokenAndLegacyAttemptGetsTokenWithoutAnotherCharge()
    {
        using var h=await ChallengeTestHarness.CreateAsync("challenge-legacy-token");
        await h.DispatchLuaCallAsync(54,Entry);string first=Token(h);
        await h.DispatchLuaCallAsync(54,Entry);Assert.Equal(first,Token(h));
        Run(h).Remove("battleToken");
        await h.DispatchLuaCallAsync(54,Entry);Assert.True(Guid.TryParseExact(Token(h),"N",out _));
        Assert.Equal(3,h.Player.GetTask(9,45900));
        h.Session.Tag=h.Module.Store.Load(h.Player.Account)!;
        string request=Result(Token(h));
        await h.DispatchLuaCallAsync(252,new JsonObject{["sCmd"]=55,["tbParam"]=JsonNode.Parse(request)}.ToJsonString());
        var reply=Reply(h)["tbParam"]!;Assert.Equal(Token(h),reply["battleToken"]!.GetValue<string>());
        Assert.True(reply["isPass"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("[6,349,507,3007,1]")]
    [InlineData("[6,349,907,2153,2]")]
    public async Task DifferentEntryCannotReplaceOutstandingRun(string other)
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-outstanding-entry");
        // A second valid formation makes this exercise the active-run guard.
        var second = new Formation { Id = 2 };
        second.Slots.AddRange(h.Player.Formations.First(f => f.Id == 1).Slots);
        h.Player.Formations.Add(second);
        await h.DispatchLuaCallAsync(54, Entry);
        string token = Token(h);
        await h.DispatchLuaCallAsync(54, other);
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(token, Token(h));
        Assert.Equal(3, h.Player.GetTask(9, 45900));
        Assert.Equal(token, h.Module.Store.Load(h.Player.Account)!.Extra[ChallengeRuns.AttemptKey]!["battleToken"]!.GetValue<string>());
        await h.DispatchLuaCallAsync(55, Result(token));
        Assert.True(Reply(h)["isPass"]!.GetValue<bool>());
    }

    [Fact]
    public async Task FailedFirstEntryRestoresAbsentDailyStateAndRetryDoesNotSpendQuota()
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-entry-write-failure");
        string blocker = BlockSave(h);
        int count = h.Outbound.Count;
        try { await h.DispatchLuaCallAsync(54, Entry); }
        finally { Directory.Delete(blocker); }
        Assert.Equal(count + 1, h.Outbound.Count);
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Null(Reply(h)["battleToken"]);
        Assert.False(h.Player.Extra.ContainsKey(ChallengeRuns.AttemptKey));
        Assert.False(h.Player.Extra.ContainsKey(ChallengeDaily.DayKey));
        Assert.False(h.Player.Tasks.ContainsKey(Player.ComposeTaskId(9, 45900)));
        await h.DispatchLuaCallAsync(54, Entry);
        Assert.Equal(3, h.Player.GetTask(9, 45900));
        Assert.Equal(Token(h), h.Module.Store.Load(h.Player.Account)!.Extra[ChallengeRuns.AttemptKey]!["battleToken"]!.GetValue<string>());
    }

    [Fact]
    public async Task FailedCancellationKeepsRunActiveUntilDurableRetry()
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-cancel-write-failure");
        await h.DispatchLuaCallAsync(54, Entry);
        string token = Token(h);
        string request = new JsonObject { ["battleToken"] = token }.ToJsonString();
        string blocker = BlockSave(h);
        try { await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => h.DispatchFunctionAsync("ChallengeCancel", request)); }
        finally { Directory.Delete(blocker); }
        Assert.True(Run(h)["active"]!.GetValue<bool>());
        Assert.Null(Run(h)["cancelled"]);
        Assert.True(h.Module.Store.Load(h.Player.Account)!.Extra[ChallengeRuns.AttemptKey]!["active"]!.GetValue<bool>());
        await h.DispatchFunctionAsync("ChallengeCancel", request);
        Assert.False(Run(h)["active"]!.GetValue<bool>());
        Assert.Equal(3, h.Player.GetTask(9, 45900));
    }

    [Fact]
    public async Task FailedLegacyMigrationDoesNotExposeUnsavedToken()
    {
        using var h = await ChallengeTestHarness.CreateAsync("challenge-migration-write-failure");
        await h.DispatchLuaCallAsync(54, Entry);
        Run(h).Remove("battleToken");
        h.Module.Store.Save(h.Player);
        string blocker = BlockSave(h);
        int count = h.Outbound.Count;
        try { await h.DispatchLuaCallAsync(54, Entry); }
        finally { Directory.Delete(blocker); }
        Assert.Equal(count + 1, h.Outbound.Count);
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Null(Reply(h)["battleToken"]);
        Assert.Null(Run(h)["battleToken"]);
        await h.DispatchLuaCallAsync(54, Entry);
        Assert.True(Guid.TryParseExact(Token(h), "N", out _));
        Assert.Equal(3, h.Player.GetTask(9, 45900));
    }

    private static string BlockSave(FeatureHarness h)
    {
        string path = Path.Combine(h.Module.Store.DirectoryPath, h.Player.Account + ".player.json.tmp");
        Directory.CreateDirectory(path);
        return path;
    }
}
