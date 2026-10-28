using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class ChallengeRemainingScoreTests
{
    private static ChallengeScores Scores() => new(TestPaths.SettingDir);

    [Theory]
    [InlineData(0, 1000, 500, 3, 12000, 1000)]
    [InlineData(0, 500, 250, 3, 9000, 500)]
    [InlineData(45, 0, 0, 3, 3000, 0)]
    [InlineData(90, 1000, 500, 3, 6000, 1000)]
    [InlineData(0, 2000, 1000, 3, 12000, 1000)]
    [InlineData(0, 1000, 500, 2, 8000, 1000)]
    [InlineData(0, 1000, 500, 1, 4000, 1000)]
    public void SurvivalUsesElapsedTimeShieldAndEntryTeamCount(int left, int hp, int shield, int team, int score, int detail)
    {
        var stats = JsonNode.Parse($"{{\"leftTime\":{left},\"leftHp\":{hp},\"leftShd\":{shield},\"maxHp\":1000,\"mxShd\":500}}")!.AsObject();
        Assert.Equal(ChallengeScores.Status.Available, Scores().Evaluate(507, 3007, stats, true, out var result, team));
        Assert.Equal(score, result!.Score);
        Assert.Equal(detail, result.Detail);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 1500)]
    [InlineData(100, 3000)]
    [InlineData(200, 6000)]
    [InlineData(999999, 6000)]
    public void SpeedFailureGetsDamageComponentOnlyAndUsesEnemyHp(int damage, int expected)
    {
        var stats = JsonNode.Parse($"{{\"nDamage\":{damage},\"enemyMaxHp\":200,\"maxHp\":1,\"leftTime\":299}}")!.AsObject();
        Assert.Equal(ChallengeScores.Status.Available, Scores().Evaluate(907, 2153, stats, false, out var result));
        Assert.Equal(expected, result!.Score);
        Assert.Equal(0, result.Detail);
    }

    [Theory]
    [InlineData("{\"nDamage\":100,\"maxHp\":200}", false)]
    [InlineData("{\"nDamage\":100,\"enemyMaxHp\":0}", true)]
    [InlineData("{\"nDamage\":100,\"enemyMaxHp\":\"200\"}", true)]
    public void MissingOrMalformedEnemyHpNeverFallsBackToTeamHp(string json, bool invalid)
        => Assert.Equal(invalid ? ChallengeScores.Status.Invalid : ChallengeScores.Status.Unavailable,
            Scores().Evaluate(907, 2153, JsonNode.Parse(json)!.AsObject(), false, out _));

    [Theory]
    [InlineData(-1, 1000, 500)]
    [InlineData(91, 1000, 500)]
    [InlineData(0, -1, 500)]
    [InlineData(0, 1000, -1)]
    public void InvalidSurvivalStatisticsCannotProduceScore(int left, int hp, int shield)
    {
        var stats = JsonNode.Parse($"{{\"leftTime\":{left},\"leftHp\":{hp},\"leftShd\":{shield},\"maxHp\":1000,\"mxShd\":500}}")!.AsObject();
        Assert.Equal(ChallengeScores.Status.Invalid, Scores().Evaluate(507, 3007, stats, false, out _));
    }

    [Fact]
    public void NoNonzeroTimeOffsetsExistInInstalledContent()
    {
        var table = SssjServer.Game.Config.SettingTable.Load(Path.Combine(TestPaths.SettingDir, "ChallengeTowerLevel.txt"))!;
        Assert.NotEmpty(table.Rows);
        Assert.All(table.Rows, row => Assert.Equal(0, table.Int(row, "CalcScoreTime")));
    }

    [Fact]
    public async Task SurvivalResultCanBeRetainedWithoutReinterpretingItAsDamage()
    {
        using var h = await ChallengeTestHarness.CreateAsync("survival-retain");
        await h.DispatchLuaCallAsync(54, "[6,349,507,3007,1]");
        await ChallengeTestHarness.SettleAsync(h, 55,
            "[6,349,507,3007,1,{\"isPass\":true,\"leftTime\":0,\"leftHp\":500,\"leftShd\":250,\"maxHp\":1000,\"mxShd\":500}]");
        var reply = JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
        Assert.Null(reply["nError"]);
        Assert.Equal(9000, reply["SingleScore"]!.GetValue<int>());
        string token = reply["battleToken"]!.GetValue<string>();
        await h.DispatchLuaCallAsync(207, new JsonObject { ["battleToken"] = token, ["keep"] = true }.ToJsonString());
        reply = JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
        Assert.Null(reply["nError"]);
        Assert.Equal(9000, h.Player.GetTask(9, 46201));
        Assert.Equal(500, h.Player.GetTask(9, 47201));
        Assert.Equal(9000, h.Player.GetTask(9, 45901));
        Assert.Equal(2, h.Player.GetTask(9, 45900));
    }
}
