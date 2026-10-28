using System.Text.Json;
using SssjServer.Game.Config;
using Xunit;

namespace SssjServer.Game.Tests;

public class GachaRotationTests
{
    [Fact]
    public void ScheduleHotReloadsForCachedCardAndWeaponTablesAndRetainsValidPlanOnErrors()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sssj-rotation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "gacha.txt"),
            "INT\tINT\tSTR\tSTR\nID\tType\tPool\tTime\nID\tType\tPool\tTime\n" +
            "1\t1\tfirst\t{20100101,20110101}\n1\t1\tsecond\t{20120101,20130101}\n1\t0\tweapon\t{20100101,20110101}\n");
        var cards = GachaTable.Load(directory);
        var weapons = WeaponGachaTable.Load(directory);
        var rotation = GachaRotation.For(directory);
        string path = Path.Combine(directory, GachaRotation.FileName);
        void Write(GachaRotationPlan plan) => File.WriteAllText(path, JsonSerializer.Serialize(plan));
        try
        {
            Assert.Null(cards.Get(1, 150));
            Write(new(true, [new(1, 1, 2, 100, 200), new(1, 1, 1, 200, 300), new(0, 1, 1, 100, 200)]));
            Assert.Equal("second", cards.Get(1, 100)!.PoolFile);
            Assert.Equal("first", cards.Get(1, 200)!.PoolFile);
            Assert.Equal("weapon", weapons.Get(1, 100)!.PoolFile);
            if (Environment.GetEnvironmentVariable("SSSJ_ROTATION_LUA_RECEIPT") is string receipt)
                File.WriteAllText(receipt, rotation.ClientLua());
            Assert.Null(weapons.Get(1, 200));
            Assert.Null(cards.Get(1, 300));
            Assert.Null(cards.Get(1, 99));
            Assert.Contains("_rotationOriginal", rotation.ClientLua());
            Assert.Contains("GachaCommon", rotation.ClientLua());
            string activeRevision = rotation.Revision(150);
            Assert.NotEqual(activeRevision, rotation.Revision(250));
            File.WriteAllText(path, "broken JSON");
            Assert.Equal("second", cards.Get(1, 150)!.PoolFile);
            Write(new(true, [new(1, 1, 9, 100, 200)]));
            Assert.Equal("second", cards.Get(1, 150)!.PoolFile);
            Write(new(false, []));
            Assert.Null(cards.Get(1, 150));
            Assert.Contains("v.nStart=v._rotationOriginal[1]", rotation.ClientLua());
        }
        finally
        {
            File.Delete(path);
            File.Delete(Path.Combine(directory, "gacha.txt"));
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void OverlapAndUnknownPoolsAreRejectedButAdjacentWindowsAreAllowed()
    {
        var catalog = new Dictionary<(int, int), int> { [(1, 1)] = 2, [(0, 1)] = 1 };
        Assert.Throws<InvalidDataException>(() => GachaRotation.Validate(new(true,
            [new(1, 1, 1, 100, 201), new(1, 1, 2, 200, 300)]), catalog));
        Assert.Throws<InvalidDataException>(() => GachaRotation.Validate(new(true,
            [new(1, 2, 1, 100, 200)]), catalog));
        GachaRotation.Validate(new(true, [new(1, 1, 1, 100, 200), new(1, 1, 2, 200, 300),
            new(0, 1, 1, 100, 200)]), catalog);
    }
}
