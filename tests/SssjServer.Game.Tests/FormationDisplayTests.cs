using SssjServer.Game;
using SssjServer.Game.Handlers;
using System.Threading.Tasks;
using Xunit;

namespace SssjServer.Game.Tests;

public class FormationDisplayTests
{
    [Fact]
    public async Task RenameAndEquip_SurviveReloadFromDisk()
    {
        using var h = await FeatureHarness.CreateAsync("formation-reload");
        var weapon = h.Player.AddItem(2, 1, 2, 2, 1);
        await h.DispatchLuaCallAsync(FormationHandler.UpdateCmd,
            "{\"Id\":1,\"Info\":[{\"MainCard\":1,\"UsedCard\":1,\"WeaponId\":" + weapon.Guid + ",\"Rune\":[0,0,0,0]}]}");
        await h.DispatchLuaCallAsync(FormationHandler.ChangeNameCmd,
            "{\"Id\":1,\"Name\":\"验证队伍\"}");
        var reloaded = new PlayerStore(h.Options).Load(h.Player.Account);
        Assert.NotNull(reloaded);
        var team = Assert.Single(reloaded!.Formations);
        Assert.Equal("验证队伍", team.Title);
        Assert.Equal(weapon.Guid, Assert.Single(team.Slots).WeaponGuid);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Id\":1}")]
    [InlineData("{\"Id\":-1,\"Info\":[]}")]
    [InlineData("{\"Id\":1,\"Info\":[null]}")]
    [InlineData("{\"Id\":1,\"Info\":[7]}")]
    [InlineData("{\"Id\":1,\"Info\":[{}, {\"MainCard\":-1}]}")]
    [InlineData("{\"Id\":1,\"Info\":[{\"Rune\":[-1]}]}")]
    [InlineData("{\"Id\":1,\"Info\":[{\"Secondarycard\":{}}]}")]
    public async Task InvalidUpdate_PreservesExistingTeamOnDisk(string request)
    {
        using var h = await FeatureHarness.CreateAsync("formation-invalid");
        await h.DispatchLuaCallAsync(21,
            """{"Id":1,"Info":[{"MainCard":11,"UsedCard":12,"WeaponId":13}]}""");
        await h.DispatchLuaCallAsync(23, request);
        var saved = h.Module.Store.Load(h.Player.Account)!;
        var slot = Assert.Single(Assert.Single(saved.Formations).Slots);
        Assert.Equal(11u, slot.MainCardGuid);
        Assert.Equal(12u, slot.UsedCardGuid);
        Assert.Equal(13u, slot.WeaponGuid);
    }

    [Fact]
    public async Task ReplaceThenExplicitClear_PersistWithoutMovingEmptySlots()
    {
        using var h = await FeatureHarness.CreateAsync("formation-clear");
        await h.DispatchLuaCallAsync(23,
            """{"Id":1,"Info":[{}, {"MainCard":12,"UsedCard":13,"Secondarycard":[0,14,0],"Rune":[0,15,0,0]}, {}]}""");
        var saved = h.Module.Store.Load(h.Player.Account)!;
        var slots = Assert.Single(saved.Formations).Slots;
        Assert.Equal(new uint[] { 0, 12, 0 }, slots.Select(s => s.MainCardGuid));
        Assert.Equal(new uint[] { 0, 14, 0 }, slots[1].SecondaryCardGuids);
        Assert.Equal(13u, slots[1].UsedCardGuid);
        await h.DispatchLuaCallAsync(22, """{"Id":1,"Info":[]}""");
        Assert.Empty(Assert.Single(h.Module.Store.Load(h.Player.Account)!.Formations).Slots);
    }

    [Theory]
    [InlineData("", "队伍 1")]
    [InlineData("  ", "队伍 1")]
    [InlineData("我的队伍", "我的队伍")]
    public void WireTitle_UsesDefaultOnlyForBlankNames(string title, string expected)
    {
        var formation = new Formation { Id = 1, Title = title };
        Assert.Equal(expected, FormationHandler.BuildFormationInfo(formation).Title);
        Assert.Equal(title, formation.Title);
    }
}
