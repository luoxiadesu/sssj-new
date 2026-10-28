using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class GachaNewcomerTests
{
    [Fact]
    public async Task ConcurrentConfirmationsGrantOnlyOneBatch()
    {
        using var h = await FeatureHarness.CreateAsync("newcomer-concurrent");
        Unlock(h.Player);
        var ticket = h.Player.AddItem(9, 19, 1, 1, 2);
        await Preview(h);
        int before = h.Player.Items.Count(i => i.Genre == 1);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Last(h, true))));
        Assert.Equal(before + 10, h.Player.Items.Count(i => i.Genre == 1));
        Assert.Equal(1u, ticket.Count);
        Assert.Equal(10, h.Player.GetTask(15, GachaFeature.TaskTotal));
    }

    private static void Unlock(Player player) => player.RecordLevelPass(MapList.MapId(1, 6, 1), MapList.StarMask);
    private static Task Preview(FeatureHarness h) => h.LuaCalls.DispatchAsync(h.Session, "Lottery", "{\"nId\":666,\"bTen\":true}");
    private static Task Last(FeatureHarness h, bool confirm) => h.LuaCalls.DispatchAsync(h.Session, "GetLastGacha", confirm ? "true" : "false");
    private static JsonObject Reply(FeatureHarness h) => (JsonObject)JsonNode.Parse(h.LastOutbound.Json)!;
    private static long Material(Player player) => player.Items.Where(i => i.Genre == 7 && i.Detail == 1 && i.Particular == 4 && i.Level == 2).Sum(i => (long)i.Count);

    [Fact]
    public async Task PreviewsCanBeRepeatedWithoutChargingGrantingOrAdvancingCounters()
    {
        using var h = await FeatureHarness.CreateAsync("newcomer-preview");
        Unlock(h.Player);
        var ticket = h.Player.AddItem(9, 19, 1, 1, 1);
        var tasks = h.Player.Tasks.OrderBy(t => t.Key).ToArray();
        int count = h.Player.Items.Count;
        long material = Material(h.Player);
        for (int iteration = 0; iteration < 2; iteration++)
        {
            await Preview(h);
            var reply = Reply(h);
            Assert.Null(reply["err"]);
            Assert.False(reply["bGetCard"]!.GetValue<bool>());
            var awards = (JsonArray)reply["tbAwards"]!;
            Assert.Equal(10, awards.Count);
            var stars = awards.Select(a => Math.Min(4, a!["tbGDPL"]![3]!.GetValue<int>())).ToArray();
            Assert.Equal(1, stars.Count(s => s == 4));
            Assert.Equal(2, stars.Count(s => s == 3));
            Assert.Equal(7, stars.Count(s => s == 2));
            Assert.Equal(1u, ticket.Count);
            Assert.Equal(count, h.Player.Items.Count);
            Assert.Equal(material, Material(h.Player));
            Assert.Equal(tasks, h.Player.Tasks.OrderBy(t => t.Key).ToArray());
        }
        string saved = Reply(h)["tbAwards"]!.ToJsonString();
        await Last(h, false);
        Assert.Equal(saved, Reply(h)["tbAwards"]!.ToJsonString());
    }

    [Fact]
    public async Task ConfirmationAfterReloadConsumesOneTicketAndGrantsExactlyOnce()
    {
        using var h = await FeatureHarness.CreateAsync("newcomer-confirm");
        Unlock(h.Player);
        h.Player.AddItem(9, 19, 1, 1, 1);
        await Preview(h);
        var player = h.Module.Store.Load(h.Player.Account)!;
        h.Session.Tag = player;
        int before = player.Items.Count(i => i.Genre == 1 && i.Count > 0);
        long material = Material(player);
        await Last(h, true);
        var reply = Reply(h);
        Assert.Null(reply["err"]);
        Assert.True(reply["bGetCard"]!.GetValue<bool>());
        Assert.Equal(before + 10, player.Items.Count(i => i.Genre == 1 && i.Count > 0));
        Assert.Equal(material + 10, Material(player));
        Assert.Equal(0u, player.Items.Single(i => i.Genre == 9 && i.Detail == 19).Count);
        Assert.Equal(10, player.GetTask(15, GachaFeature.TaskTotal));
        Assert.Equal(10, player.GetTask(15, GachaFeature.TaskPrePoolBegin + 666));
        await Last(h, true);
        Assert.True(Reply(h)["bGetCard"]!.GetValue<bool>());
        Assert.Equal(before + 10, player.Items.Count(i => i.Genre == 1 && i.Count > 0));
        Assert.Equal(material + 10, Material(player));
        Assert.Equal(10, player.GetTask(15, GachaFeature.TaskTotal));
    }

    [Fact]
    public async Task LostTicketBlocksConfirmationButKeepsPendingPreview()
    {
        using var h = await FeatureHarness.CreateAsync("newcomer-no-ticket");
        Unlock(h.Player);
        var ticket = h.Player.AddItem(9, 19, 1, 1, 1);
        await Preview(h);
        ticket.Count = 0;
        int before = h.Player.Items.Count(i => i.Genre == 1);
        await Last(h, true);
        Assert.Equal(GachaFeature.ErrNoLast, Reply(h)["err"]!.GetValue<string>());
        Assert.Equal(before, h.Player.Items.Count(i => i.Genre == 1));
        await Last(h, false);
        Assert.False(Reply(h)["bGetCard"]!.GetValue<bool>());
        ticket.Count = 1;
        await Last(h, true);
        Assert.True(Reply(h)["bGetCard"]!.GetValue<bool>());
    }

    [Fact]
    public async Task OrdinaryDrawDoesNotOverwriteThePendingNewcomerResult()
    {
        using var h = await FeatureHarness.CreateAsync("newcomer-independent");
        Unlock(h.Player);
        h.Player.AddItem(9, 19, 1, 1, 1);
        await Preview(h);
        string saved = Reply(h)["tbAwards"]!.ToJsonString();
        h.Player.AddItem(9, 12, 1, 1, 1);
        await h.LuaCalls.DispatchAsync(h.Session, "Lottery", "{\"nId\":1,\"bTen\":false}");
        Assert.Single((JsonArray)Reply(h)["tbAwards"]!);
        await Last(h, false);
        Assert.Equal(saved, Reply(h)["tbAwards"]!.ToJsonString());
        await Last(h, true);
        Assert.True(Reply(h)["bGetCard"]!.GetValue<bool>());
        Assert.Equal(11, h.Player.GetTask(15, GachaFeature.TaskTotal));
    }
}
