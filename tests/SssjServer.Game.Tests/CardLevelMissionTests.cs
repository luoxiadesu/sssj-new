using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;
public class CardLevelMissionTests
{
    [Theory]
    [InlineData(69, 4)]
    [InlineData(172, 3)]
    public void CountUsesEnhanceLevelPositiveQuantityAndRequestedGirl(int type, int expected)
    {
        var player = new Player("card-level-count", 1);
        player.Items.AddRange(new[] {
            new Item { Genre = 1, Detail = 6, Level = 1, EnhanceLevel = 70, Count = 2, LockOn = 1 },
            new Item { Genre = 1, Detail = 6, Level = 4, EnhanceLevel = 71, Count = 1 },
            new Item { Genre = 1, Detail = 2, Level = 4, EnhanceLevel = 70, Count = 1 },
            new Item { Genre = 1, Detail = 6, Level = 100, EnhanceLevel = 69, Count = 10 },
            new Item { Genre = 1, Detail = 6, EnhanceLevel = 70, Count = 0 },
            new Item { Genre = 2, Detail = 6, EnhanceLevel = 70, Count = 10 } });
        var entry = new MissionEntry { Group = MissionTable.OtherGroup, AchieveType = type,
            Counts = type == 69 ? new() { 3, 70 } : new() { 6, 70, 3 } };
        Assert.Equal(expected, MissionFeature.DerivedProgress(player, entry));
    }

    [Fact]
    public async Task RealMissionUpdatesOnPulsePersistsAndPreservesClaimedBit()
    {
        using var h = await FeatureHarness.CreateAsync("card-level-mission");
        h.Player.Items.Clear();
        h.Player.SetTask(18, 19, 1);
        for (int i = 0; i < 5; i++) h.Player.Items.Add(new Item {
            Guid = (uint)(i + 1), Genre = 1, Detail = 1, Particular = 1, Level = 1, EnhanceLevel = 30, Count = 1 });
        var feature = (IGamePulseFeature)h.Module.Features.OfType<MissionFeature>().Single();
        feature.OnPlayerPulse(h.Session, h.Player);
        Assert.Equal(11, h.Player.GetTask(18, 19));
        Assert.Equal(11, h.Module.Store.Load(h.Player.Account)!.GetTask(18, 19));
        h.Player.Items.Clear();
        feature.OnPlayerPulse(h.Session, h.Player);
        Assert.Equal(11, h.Player.GetTask(18, 19));
    }

    [Fact]
    public void LargeStackCannotWrapProgress()
    {
        var player = new Player("card-level-overflow", 1);
        player.Items.Add(new Item { Genre = 1, Detail = 6, EnhanceLevel = 70, Count = uint.MaxValue });
        var entry = new MissionEntry { Group = MissionTable.OtherGroup, AchieveType = 172, Counts = new() { 6, 70, 3 } };
        Assert.Equal(int.MaxValue, MissionFeature.DerivedProgress(player, entry));
    }
}
