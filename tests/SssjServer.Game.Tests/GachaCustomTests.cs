using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class GachaCustomTests
{
    [Fact]
    public async Task ConfirmSelectionReturnsClientCallbackAfterPersistingChoice()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-selection-callback");
        var pool = GachaTable.Load(TestPaths.SettingDir).Get(401, DateTimeOffset.UtcNow.ToUnixTimeSeconds())!;
        Assert.NotNull(pool);
        var selected = pool.Cards.Values.Where(c => c.Star == 4 && c.UpFlag == 1).Take(2).ToArray();
        await h.DispatchFunctionAsync(GachaFeature.FnSetCardCustomUp,
            $"{{\"nId\":401,\"tbItems\":[{selected[0].Id},{selected[1].Id}]}}");
        Assert.Equal(selected[0].Id | selected[1].Id << 16, h.Player.GetTask(15, 6402));
        Assert.Equal(1, h.Player.GetTask(15, 7402));
        Assert.Equal("SetCardCustomUp", h.LastOutbound.Function);
        Assert.Equal(401, System.Text.Json.Nodes.JsonNode.Parse(h.LastOutbound.Json)!["nId"]!.GetValue<int>());
    }

    [Fact]
    public async Task InvalidSelectionUnblocksWithoutChangingChoiceOrTimes()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-selection-rejected");
        await h.DispatchFunctionAsync(GachaFeature.FnSetCardCustomUp, "{\"nId\":401,\"tbItems\":[1,1]}");
        Assert.Equal(0, h.Player.GetTask(15, 6402));
        Assert.Equal(0, h.Player.GetTask(15, 7402));
        Assert.Contains("Game.Blocking.Hide()", h.LastOutbound.Json);
    }

    private static GachaPool Pool(int id) => GachaTable.Load(TestPaths.SettingDir).Segments(id).First();

    [Fact]
    public void CustomUpUsesSelectedQuarterWeightsWithoutMutatingSharedConfiguration()
    {
        var pool = Pool(401);
        Assert.Equal(4, pool.SetUpNum);
        var selected = pool.Cards.Values.Where(c => c.Star == 4 && c.UpFlag == 1).Take(2).ToArray();
        int choice = selected[0].Id | selected[1].Id << 16;
        Assert.True(GachaFeature.ValidChoice(pool, choice));
        var before = pool.Cards.Values.Select(c => (c.Id, c.RawRate, c.UpFlag, c.RateInStar)).ToArray();
        var weights = GachaFeature.CardWeights(pool, 4, choice);
        Assert.Equal(.25, weights.Single(c => c.Card.Id == selected[0].Id).Weight);
        Assert.Equal(.25, weights.Single(c => c.Card.Id == selected[1].Id).Weight);
        Assert.Equal(.5, weights.Where(c => !selected.Contains(c.Card)).Sum(c => c.Weight), 10);
        Assert.Equal(1, weights.Sum(c => c.Weight), 10);
        Assert.Equal(before, pool.Cards.Values.Select(c => (c.Id, c.RawRate, c.UpFlag, c.RateInStar)).ToArray());
        Assert.False(GachaFeature.ValidChoice(pool, selected[0].Id | selected[0].Id << 16));
        Assert.False(GachaFeature.ValidChoice(pool, 0));
    }

    [Fact]
    public void GuaranteedChoiceReturnsExactlyTheSelectedCardRegardlessOfStarRoll()
    {
        var pool = Pool(800);
        int selected = pool.Cards.Values.First(c => c.Star == 4).Id;
        Assert.True(GachaFeature.ValidChoice(pool, selected));
        foreach (int star in new[] { 1, 2, 3, 4 })
        {
            var result = Assert.Single(GachaFeature.CardWeights(pool, star, selected));
            Assert.Equal(selected, result.Card.Id);
            Assert.Equal(1, result.Weight);
        }
        Assert.False(GachaFeature.ValidChoice(pool, selected | selected << 16));
    }
}
