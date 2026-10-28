using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class GachaUpGuaranteeTests
{
    private sealed class FirstRandom : Random
    {
        public override int Next(int maxValue) => 0;
        public override double NextDouble() => 0;
    }

    private static GachaPool Pool(bool custom = false)
    {
        var pool = new GachaPool { Id = 10, ProtectNum = 50, ProtectUpNum = 100, JudgeType = custom ? 3 : 0 };
        void Add(int id, int star, int up)
        {
            var card = new GachaCard { Id = id, G = 1, D = 1, P = id, L = star, RawRate = 1, UpFlag = up };
            pool.Cards[id] = card;
            pool.StarTotal[star] = pool.StarTotal.GetValueOrDefault(star) + 1;
        }
        Add(1, 3, 0);
        Add(2, 4, 1);
        Add(3, 4, 1);
        Add(4, 4, 1);
        return pool;
    }

    private static CardStarSeedRow Seed(int star)
    {
        var seed = new CardStarSeedRow();
        seed.Steps[star - 1].Add(new StarSeedStep(1000, 10000));
        return seed;
    }

    [Fact]
    public void ThresholdGuaranteesUpAndClosesTheBenefitOnce()
    {
        var player = new Player("up-threshold", 1);
        var pool = Pool();
        var feature = new GachaFeature(new FirstRandom());
        int key = GachaFeature.TaskProtectUpBegin + pool.Id;
        player.SetTask(15, key, 98);
        player.SetTask(15, GachaFeature.TaskTotal, 800);
        player.SetTask(15, GachaFeature.TaskPrePoolBegin + pool.Id, 30);
        player.SetTask(15, GachaFeature.TaskProtectBegin + pool.Id, 7);
        var before = feature.DrawOne(player, pool, Seed(3));
        Assert.Equal(8, before.Times);
        Assert.Equal(31, before.TotalTimes);
        Assert.False(before.IsUp);
        Assert.Equal(99, player.GetTask(15, key));
        var guaranteed = feature.DrawOne(player, pool, Seed(3));
        Assert.True(guaranteed.IsUp);
        Assert.Equal(4, guaranteed.Card.Star);
        Assert.Equal(100, guaranteed.UpTimes);
        Assert.Equal(-1, player.GetTask(15, key));
        Assert.Equal(0, player.GetTask(15, GachaFeature.TaskProtectBegin + pool.Id));
        var after = feature.DrawOne(player, pool, Seed(3));
        Assert.False(after.IsUp);
        Assert.Equal(0, after.UpTimes);
        Assert.Equal(-1, player.GetTask(15, key));
    }

    [Fact]
    public void NaturalUpDisablesBenefitWithoutShowingGuaranteeBadge()
    {
        var player = new Player("up-natural", 1);
        var pool = Pool();
        var feature = new GachaFeature(new FirstRandom());
        var result = feature.DrawOne(player, pool, Seed(4));
        Assert.True(result.IsUp);
        Assert.Equal(0, result.UpTimes);
        Assert.Equal(-1, player.GetTask(15, GachaFeature.TaskProtectUpBegin + pool.Id));
    }

    [Fact]
    public void CustomGuaranteeOnlySelectsThePlayersChosenCards()
    {
        var player = new Player("up-custom", 1);
        var pool = Pool(custom: true);
        player.SetTask(15, GachaFeature.TaskCardCustomChoice + pool.Id, 3 | 4 << 16);
        player.SetTask(15, GachaFeature.TaskProtectUpBegin + pool.Id, 99);
        var result = new GachaFeature(new FirstRandom()).DrawOne(player, pool, Seed(3));
        Assert.Equal(3, result.Card.Id);
        Assert.True(result.IsUp);
        Assert.Equal(100, result.UpTimes);
    }

    [Fact]
    public void PoolsWithoutUpGuaranteeDoNotWriteAnUpProgressTask()
    {
        var player = new Player("no-up-benefit", 1);
        var result = new GachaFeature(new FirstRandom()).DrawOne(player,
            new GachaPool { Id = 1, ProtectUpNum = 0, Cards = { [1] = new GachaCard { Id = 1, L = 4, RawRate = 1 } }, StarTotal = { [4] = 1 } }, Seed(4));
        Assert.Equal(0, result.UpTimes);
        Assert.Equal(0, player.GetTask(15, GachaFeature.TaskProtectUpBegin + 1));
    }
}
