using System.Text.Json.Nodes;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

public sealed partial class HouseFeature
{
    private bool TryBuyMissingTheme(Player player, CafeThemeRow theme, JsonNode? requested,
        out RewardGrantResult granted, out List<int> changedMoney)
    {
        granted = new RewardGrantResult();
        changedMoney = [];
        var state = ReadOrCreate(player, _table);
        var have = state.Furniture.GroupBy(f => f.TemplateId).ToDictionary(g => g.Key, g => g.Count());
        var missing = theme.Items.GroupBy(i => i.TemplateId)
            .Select(g => (Id: g.Key, Count: Math.Max(0, g.Count() - have.GetValueOrDefault(g.Key))))
            .Where(g => g.Count > 0).ToList();

        // The legacy UI sends its missing-item multiset. Never charge for more
        // than that reviewed list if it was computed from stale client holdings.
        if (requested is not null)
        {
            if (requested is not JsonArray arr) return false;
            var ids = arr.Select(n => ReadInt(n, -1)).ToList();
            if (ids.Any(id => id <= 0)) return false;
            var counts = ids.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            if (counts.Count != missing.Count
                || missing.Any(m => counts.GetValueOrDefault(m.Id) != m.Count)) return false;
        }
        long totalCount = missing.Sum(m => (long)m.Count);
        if (totalCount > 0 && ((long)state.Furniture.Count + totalCount > 2000
            || (ulong)state.NextObjId + (ulong)totalCount > uint.MaxValue)) return false;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var purchases = new List<(GoodsRow Goods, int Count)>();
        foreach (var item in missing)
        {
            var candidates = _themeShop.GoodsOf(40001).Where(g => g.GoodsId == item.Id
                && g.ShopType == 4 && g.IsInTimeAt(now)).ToList();
            if (candidates.Count != 1 || _themeShop.GetShop(40001) is not { IsCafeShop: true } shop
                || !shop.IsOpenAt(now)) return false;
            var goods = candidates[0];
            // The current furniture catalog has fixed prices, no random groups
            // or discounts and one furniture per sale. Do not guess other rules.
            if (goods.Group.HasValue || goods.DiscountList.Count > 0 || goods.Prices.Length != 1
                || goods.LimitKind != ShopLimitKind.None || goods.GoodsNum != 1
                || !_ctx!.Rewards.IsCafeFurniture((uint)goods.G, (uint)goods.D, (uint)goods.P, (uint)goods.L)
                || _ctx.Rewards.Templates.Get((uint)goods.G, (uint)goods.D, (uint)goods.P, (uint)goods.L)?.ExtParam1 != item.Id.ToString()
                || !_ctx.Rewards.CanReceiveFurniture(player, (uint)goods.G, (uint)goods.D,
                    (uint)goods.P, (uint)goods.L, item.Count)) return false;
            purchases.Add((goods, item.Count));
        }

        // Simulate all currency pools together before mutating the real player;
        // a later insufficient balance must not leave an earlier deduction.
        var payment = new Player("theme-payment-preview", player.Roleid);
        foreach (var (id, amount) in player.Money) payment.SetMoney(id, amount);
        foreach (var group in purchases.GroupBy(p => p.Goods.MoneyType))
        {
            long cost = group.Sum(p => (long)p.Goods.UnitPrice(1, 100) * p.Count);
            if (cost < 0 || cost > int.MaxValue
                || !ShopFeature.TrySpendMoney(payment, group.Key, (int)cost, changedMoney)) return false;
        }
        foreach (int id in changedMoney) player.SetMoney(id, payment.GetMoney(id));
        foreach (var (goods, count) in purchases)
        {
            _ctx!.Rewards.Grant(player, [[goods.G, goods.D, goods.P, goods.L, count]], RewardSource.Extra, granted);
            ShopFeature.AddBuyCount(player, goods.ShopId, ShopLimitKind.Total, goods.GoodsId, count);
        }
        return true;
    }
}
