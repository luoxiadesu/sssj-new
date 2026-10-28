using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;

public class ChapterStoryFeatureTests
{
    private static Task Complete(FeatureHarness h, int index) => h.DispatchLuaCallAsync(540,
        $"{{\"Chapter\":16,\"Index\":{index},\"Difficult\":1}}");
    private static JsonNode Reply(FeatureHarness h) => JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
    [Fact]
    public async Task WholeChapter_SequentialStoriesAwardOnceWithoutStaminaAndPersist()
    {
        using var h = await FeatureHarness.CreateAsync("chapter16-all");
        h.Player.Levels[MapList.MapId(15, 12, 1)] = 15;
        int vigour = h.Player.Vigour, diamonds = h.Player.GetMoney(MoneyType.FreeDiamond);
        var levels = h.Module.Chapters.All.Where(l => l.Chapter == 16 && l.Diff == 1).OrderBy(l => l.Index).ToArray();
        Assert.Equal(12, levels.Length);
        foreach (var level in levels)
        {
            await Complete(h, level.Index);
            Assert.Null(Reply(h)["nError"]);
            Assert.Single((JsonArray)Reply(h)["tbNew"]!);
            Assert.Equal(15, h.Player.GetLevelStar(level.MapId));
        }
        Assert.Equal(vigour, h.Player.Vigour);
        Assert.Equal(diamonds + 240, h.Player.GetMoney(MoneyType.FreeDiamond));
        var saved = h.Module.Store.Load(h.Player.Account)!;
        h.Session.Tag = saved;
        await Complete(h, 12);
        Assert.Empty((JsonArray)Reply(h)["tbNew"]!);
        Assert.Equal(diamonds + 240, saved.GetMoney(MoneyType.FreeDiamond));
        Assert.Equal(15, saved.GetLevelStar(MapList.MapId(16, 12, 1)));
        Assert.True(await h.WaitForPfAsync((int)GamePf.ChapterUpdateNtf));
    }
    [Fact]
    public async Task LockedStory_DoesNotAdvanceOrAward()
    {
        using var h = await FeatureHarness.CreateAsync("chapter16-lock");
        int diamonds = h.Player.GetMoney(MoneyType.FreeDiamond);
        await Complete(h, 1);
        Assert.NotNull(Reply(h)["nError"]);
        h.Player.Levels[MapList.MapId(15, 12, 1)] = 15;
        await Complete(h, 2);
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(0, h.Player.GetLevelStar(MapList.MapId(16, 2, 1)));
        Assert.Equal(diamonds, h.Player.GetMoney(MoneyType.FreeDiamond));
    }
    [Fact]
    public async Task HistoricalPass_ReplayPreservesCountAndDoesNotBackfillReward()
    {
        using var h = await FeatureHarness.CreateAsync("chapter16-old");
        uint map = MapList.MapId(16, 5, 1);
        h.Player.Levels[map] = MapList.Compose(3, 7);
        int diamonds = h.Player.GetMoney(MoneyType.FreeDiamond);
        await Complete(h, 5);
        Assert.Empty((JsonArray)Reply(h)["tbNew"]!);
        Assert.Equal(7, MapList.PassCount(h.Player.GetLevelStar(map)));
        Assert.Equal(7, MapList.Stars(h.Player.GetLevelStar(map)));
        Assert.Equal(diamonds, h.Player.GetMoney(MoneyType.FreeDiamond));
    }
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Chapter\":15,\"Index\":1,\"Difficult\":1}")]
    [InlineData("{\"Chapter\":16,\"Index\":256,\"Difficult\":1}")]
    [InlineData("{\"Chapter\":16,\"Index\":13,\"Difficult\":1}")]
    [InlineData("{\"Chapter\":16,\"Index\":1,\"Difficult\":2}")]
    public async Task InvalidRequest_PreservesProgress(string request)
    {
        using var h = await FeatureHarness.CreateAsync("chapter16-invalid");
        var before = h.Player.Levels.ToArray();
        await h.DispatchLuaCallAsync(540, request);
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(before, h.Player.Levels);
    }
}
