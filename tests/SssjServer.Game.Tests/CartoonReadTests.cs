using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public sealed class CartoonReadTests
{
    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(30, 0, 30)]
    [InlineData(31, 201, 21)]
    [InlineData(32, 1, 0)]
    [InlineData(63, 202, 22)]
    public void OriginalBitLayout(int id, int task, int bit)
        => Assert.Equal((task, bit), CartoonTable.TaskBit(id));

    [Fact]
    public async Task OnlyTheRequiredReadStoryUnlocksItsLetterAndSurvivesReload()
    {
        using var h = await FeatureHarness.CreateAsync("story-exact-bit");
        var phone = PhoneMsgTable.Load(TestPaths.SettingDir);
        var ids = SettingTable.Load(Path.Combine(TestPaths.SettingDir, "CartoonFileId.txt"), 0)!;
        var mapping = ids.Rows.ToDictionary(r => ids.Str(r, "File"), r => ids.Int(r, "Id"));
        var ev = phone.AllLetterEvents.First(e => e.Conds.Count == 1 && e.Conds[0].Type == 1
            && phone.Letter(e.Id) is not null && mapping.ContainsKey(e.Conds[0].Info + ".txt"));
        string path = ev.Conds[0].Info;
        int npc = phone.Letter(ev.Id)!.NpcId;
        var (task, bit) = CartoonTable.TaskBit(mapping[path + ".txt"]);
        h.Player.Tasks.Clear();
        // A nearby read bit is insufficient, even in the same word.
        h.Player.SetTask(39, task, 1 << ((bit + 1) % 31));
        h.RunLoginHooks();
        Assert.DoesNotContain(PhoneMsgFeature.LoadStateStatic(h.Player).Letters.Values,
            l => l.Topics.ContainsKey(ev.Id));
        h.Player.SetTask(39, task, h.Player.GetTask(39, task) | (1 << bit));
        h.RunLoginHooks();
        Assert.True(PhoneMsgFeature.LoadStateStatic(h.Player).Letters[npc].Topics.ContainsKey(ev.Id));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        var cartoons = CartoonTable.Load(TestPaths.SettingDir);
        Assert.True(cartoons.IsRead(saved, path));
        Assert.True(cartoons.IsRead(saved, path + ".txt"));
        // Even a fully opened bitfield cannot make an unknown path valid.
        for (int i = 0; i <= 400; i++) saved.SetTask(39, i, int.MaxValue);
        Assert.False(cartoons.IsRead(saved, "missing/story"));
    }
}
