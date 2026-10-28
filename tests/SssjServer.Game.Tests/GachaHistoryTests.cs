using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Game.Tests;

public class GachaHistoryTests
{
    [Fact]
    public async Task CharacterDrawRecordsActualTicketAndResults()
    {
        using var h = await FeatureHarness.CreateAsync("history-character");
        h.Player.RecordLevelPass(MapList.MapId(1, 6, 1), MapList.StarMask);
        h.Player.AddItem(9, 14, 1, 1, 1);
        await h.LuaCalls.DispatchAsync(h.Session, "Lottery", "{\"nId\":66,\"bTen\":false}");
        var row = Assert.Single(GachaHistory.List(h.Player, 1));
        Assert.Equal(66u, row.Gachaid);
        Assert.Equal(1u, Assert.Single(row.Gachaitem).Genre);
        Assert.Equal(0u, row.Moneytype);
        Assert.Equal(1u, row.Moneynum);
        await h.WaitForPfAsync(SssjServer.Protocol.Pf.GachaUpdateNtf);
    }

    [Fact]
    public async Task PaymentTracksFreeDrawAndCombinedDiamondCost()
    {
        using var h = await FeatureHarness.CreateAsync("history-payment");
        var free = new GachaPaymentSnapshot(h.Player);
        Assert.Equal(new GachaPayment(0, 0), free.After(h.Player));
        h.Player.SetMoney(MoneyType.FreeDiamond, 100);
        h.Player.SetMoney(MoneyType.PayDiamond, 200);
        var before = new GachaPaymentSnapshot(h.Player);
        h.Player.SetMoney(MoneyType.FreeDiamond, 0);
        h.Player.SetMoney(MoneyType.PayDiamond, 150);
        Assert.Equal(new GachaPayment(1, 150), before.After(h.Player));
    }

    [Fact]
    public async Task BothKindsRetainTwentyBatchesAcrossReloadAndFullSnapshots()
    {
        using var h = await FeatureHarness.CreateAsync("history-retention");
        for (int i = 1; i <= 25; i++)
            GachaHistory.Append(h.Player, 1, 66, 1700000000 + i, new[] { new[] { 1, 1, 1, 4 } }, new(0, 1));
        GachaHistory.Append(h.Player, 0, 1, 1700000030,
            Enumerable.Range(0, 10).Select(_ => new[] { 2, 1, 3, 1 }), new(1, 900), 10);
        h.Module.Store.Save(h.Player);
        var loaded = h.Module.Store.Load(h.Player.Account)!;
        var message = new PhoneMsgNtf();
        GachaHistory.Fill(message, loaded);
        Assert.Equal(20, message.Cardgacha.Count);
        Assert.Equal(1700000025ul, message.Cardgacha[0].Gachatime);
        Assert.Equal(1700000006ul, message.Cardgacha[^1].Gachatime);
        var weapon = Assert.Single(message.Weapongacha);
        Assert.Equal(10, weapon.Gachaitem.Count);
        Assert.Equal(900u, weapon.Moneynum);
        Assert.Equal(1u, weapon.Moneytype);
        Assert.Equal(10u, weapon.Hedgepos);
        Assert.Equal(2u, weapon.Gachaitem[0].Genre);
        GachaHistory.Fill(new PhoneMsgNtf(), loaded);
        Assert.Equal(20, GachaHistory.List(loaded, 1).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WeaponDrawSavesActualBatchAndPushesHistory(bool ten)
    {
        using var h = await FeatureHarness.CreateAsync("history-weapon-" + ten);
        h.Player.AddItem(7, ten ? 10u : 7u, 1, 4, 1);
        await h.LuaCalls.DispatchAsync(h.Session, WeaponForgeFeature.Function,
            $"{{\"nCmd\":0,\"Id\":1,\"isTen\":{(ten ? "true" : "false")}}}");
        var row = Assert.Single(GachaHistory.List(h.Player, 0));
        Assert.Equal(ten ? 10 : 1, row.Gachaitem.Count);
        Assert.Equal(1u, row.Moneynum);
        Assert.Equal(0u, row.Moneytype);
        Assert.Equal(1u, row.Gachaid);
        Assert.InRange(row.Gachatime, (ulong)DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await h.WaitForPfAsync(SssjServer.Protocol.Pf.GachaUpdateNtf);
        Assert.Single(GachaHistory.List(h.Module.Store.Load(h.Player.Account)!, 0));
    }

    [Fact]
    public async Task NewcomerPreviewOnlyRecordsConfirmedBatchOnce()
    {
        using var h = await FeatureHarness.CreateAsync("history-newcomer");
        h.Player.RecordLevelPass(MapList.MapId(1, 6, 1), MapList.StarMask);
        h.Player.AddItem(9, 19, 1, 1, 1);
        await h.LuaCalls.DispatchAsync(h.Session, "Lottery", "{\"nId\":666,\"bTen\":true}");
        Assert.Empty(GachaHistory.List(h.Player, 1));
        await h.LuaCalls.DispatchAsync(h.Session, "GetLastGacha", "true");
        var row = Assert.Single(GachaHistory.List(h.Player, 1));
        Assert.Equal(10, row.Gachaitem.Count);
        Assert.Equal(666u, row.Gachaid);
        Assert.Equal(1u, row.Moneynum);
        await h.LuaCalls.DispatchAsync(h.Session, "GetLastGacha", "true");
        Assert.Single(GachaHistory.List(h.Player, 1));
    }

    [Fact]
    public async Task FailedDrawCreatesNoHistoryAndMalformedSavedRowsAreIgnored()
    {
        using var h = await FeatureHarness.CreateAsync("history-invalid");
        await h.LuaCalls.DispatchAsync(h.Session, WeaponForgeFeature.Function, "{\"nCmd\":0,\"Id\":999999,\"isTen\":false}");
        Assert.Empty(GachaHistory.List(h.Player, 0));
        h.Player.Extra[GachaHistory.ExtraKey] = JsonNode.Parse("{\"cards\":[null,{}, {\"pool\":-1}],\"weapons\":[]}");
        Assert.Empty(GachaHistory.List(h.Player, 1));
    }
}
