using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class GachaRequestLimitTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CapacityRejectionLeavesPaymentInventoryAndTasksUnchanged(int genre)
    {
        using var h = await FeatureHarness.CreateAsync($"gacha-capacity-{genre}");
        var table = SettingTable.Load(Path.Combine(TestPaths.SettingDir, "LimitInfo.txt"))!;
        int limit = table.Int(table.Rows.Single(r => table.Int(r, "ID") == genre), "MaxValue");
        h.Player.Items.Clear();
        for (int i = 0; i < limit - 1; i++) h.Player.Items.Add(new Item
        { Guid = (uint)(100000 + i), Genre = (uint)genre, Detail = 1, Particular = 1, Level = 1, Count = 1 });
        var ticket = h.Player.AddItem(genre == 1 ? 9u : 7u, genre == 1 ? 12u : 7u, 1, genre == 1 ? 1u : 4u, 1);
        h.Player.SetMoney(3, 500);
        h.Player.SetMoney(12, 500);
        var tasks = h.Player.Tasks.OrderBy(t => t.Key).ToArray();
        int count = h.Player.Items.Count;
        await h.LuaCalls.DispatchAsync(h.Session, genre == 1 ? "Lottery" : "WeaponLogicMsg",
            genre == 1 ? "{\"nId\":1,\"bTen\":false}" : "{\"nCmd\":0,\"Id\":1,\"isTen\":false}");
        var reply = JsonNode.Parse(h.LastOutbound.Json)!;
        if (genre == 1) Assert.Equal("error.gacha.maxcard", reply["err"]!.GetValue<string>());
        else Assert.Equal(20082, reply["nError"]!.GetValue<int>());
        Assert.Equal(500, h.Player.GetMoney(3));
        Assert.Equal(500, h.Player.GetMoney(12));
        Assert.Equal(1u, ticket.Count);
        Assert.Equal(count, h.Player.Items.Count);
        Assert.Equal(tasks, h.Player.Tasks.OrderBy(t => t.Key).ToArray());
    }

    [Theory]
    [InlineData("Lottery", "{\"nId\":1.5}")]
    [InlineData("Lottery", "{\"nId\":1,\"bTen\":1}")]
    [InlineData("Lottery", "{\"nId\":1,\"bTen\":\"true\"}")]
    [InlineData("Lottery", "{\"nId\":2147483648}")]
    [InlineData("WeaponLogicMsg", "{\"nCmd\":0,\"Id\":1.5}")]
    [InlineData("WeaponLogicMsg", "{\"nCmd\":0,\"Id\":1,\"isTen\":1}")]
    [InlineData("WeaponLogicMsg", "{\"nCmd\":0.5,\"Id\":1}")]
    public async Task MalformedRequestsNeverChargeOrAdvanceCounters(string function, string json)
    {
        using var h = await FeatureHarness.CreateAsync($"gacha-malformed-{function}-{json.Length}");
        h.Player.SetMoney(3, 5000);
        var tasks = h.Player.Tasks.OrderBy(t => t.Key).ToArray();
        int count = h.Player.Items.Count;
        await h.LuaCalls.DispatchAsync(h.Session, function, json);
        var reply = JsonNode.Parse(h.LastOutbound.Json)!;
        if (function == "Lottery") Assert.NotNull(reply["err"]);
        else Assert.True(reply["nError"]!.GetValue<int>() > 0);
        Assert.Equal(5000, h.Player.GetMoney(3));
        Assert.Equal(count, h.Player.Items.Count);
        Assert.Equal(tasks, h.Player.Tasks.OrderBy(t => t.Key).ToArray());
    }

    [Fact]
    public void CapacityUsesStrictBoundaryAndOnlyPositiveInstancesOfTheRelevantGenre()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sssj-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "LimitInfo.txt");
        File.WriteAllText(path, "INT\tINT\nID\tMaxValue\nID\tMaxValue\n1\t5\n2\t3\n");
        try
        {
            var limits = new GachaRequestLimits(directory);
            var player = new Player("capacity-boundary", 1);
            for (int i = 0; i < 3; i++) player.AddItem(1, 1, 1, 1, 1).LockOn = 1;
            player.AddItem(1, 1, 1, 1, 0);
            player.AddItem(2, 1, 1, 1, 1);
            Assert.True(limits.HasRoom(player, 1, 1));
            Assert.False(limits.HasRoom(player, 1, 2));
            Assert.True(limits.HasRoom(player, 2, 1));
            Assert.False(limits.HasRoom(player, 2, 2));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }
}
