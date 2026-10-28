using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

public sealed partial class GachaFeature
{
    internal const string NewcomerKey = "gachaNewcomer";

    private Task PreviewNewcomer(ClientSession session, Player player, GachaPool pool, bool ten, List<int[]> bonus)
    {
        lock (player) return PreviewNewcomerCore(session, player, pool, ten, bonus);
    }

    private Task PreviewNewcomerCore(ClientSession session, Player player, GachaPool pool, bool ten, List<int[]> bonus)
    {
        int[] stars = [4, 3, 3, 2, 2, 2, 2, 2, 2, 2];
        if (!ten || !IsSelectable(player, pool)
            || stars.Distinct().Any(s => !pool.Cards.Values.Any(c => c.Star == s && c.RawRate > 0)))
        {
            Reply(session, FnLottery, new JsonObject { ["err"] = ErrNotExists });
            return Task.CompletedTask;
        }
        for (int i = stars.Length - 1; i > 0; i--)
        {
            int next = _rng.Next(i + 1);
            (stars[i], stars[next]) = (stars[next], stars[i]);
        }
        var seen = new HashSet<(int, int, int, int)>();
        var results = new List<DrawResult>();
        for (int i = 0; i < stars.Length; i++)
        {
            var card = PickCard(pool, stars[i], 0);
            bool first = !CardCollection.HasOwned(player, (uint)card.G, (uint)card.D, (uint)card.P, (uint)card.L)
                && seen.Add((card.G, card.D, card.P, card.L));
            results.Add(new(card, false, first, i + 1, i + 1, false, 0));
        }
        var payload = new JsonObject { ["bTen"] = true, ["bGetCard"] = false,
            ["tbAwards"] = BuildAwards(player, pool, results) };
        if (pool.GetItem is { Length: >= 4 } && bonus.Count > 0)
            payload["getItem"] = new JsonArray(bonus[0].Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        player.Extra[NewcomerKey] = new JsonObject { ["poolId"] = pool.Id, ["poolFile"] = pool.PoolFile,
            ["claimed"] = false, ["payload"] = payload.DeepClone() };
        _ctx.Store.Save(player);
        Reply(session, FnLottery, payload);
        return Task.CompletedTask;
    }

    private bool HandleNewcomerLast(ClientSession session, Player player, bool confirm)
    {
        lock (player) return HandleNewcomerLastCore(session, player, confirm);
    }

    private bool HandleNewcomerLastCore(ClientSession session, Player player, bool confirm)
    {
        if (player.Extra.GetValueOrDefault(NewcomerKey) is not JsonObject pending) return false;
        void Fail(string error) => Reply(session, FnGetLastGacha, new JsonObject { ["err"] = error });
        if (pending["payload"] is not JsonObject stored || stored["tbAwards"] is not JsonArray { Count: 10 } awards)
        { Fail(ErrNoLast); return true; }
        var payload = (JsonObject)stored.DeepClone();
        bool alreadyClaimed = pending["claimed"] is JsonValue status && status.TryGetValue<bool>(out bool claimed) && claimed;
        if (!confirm || alreadyClaimed)
        { Reply(session, FnGetLastGacha, payload); return true; }
        if (!GachaRequestLimits.TryInteger(pending["poolId"], out int poolId))
        { Fail(ErrNoLast); return true; }
        var pool = _table.Segments(poolId).FirstOrDefault(p => p.JudgeType == 2
            && p.PoolFile == pending["poolFile"]?.ToString());
        if (pool is null || !IsSelectable(player, pool)) { Fail(ErrNoLast); return true; }
        // ShowLastGacha only handles nolast before indexing tbAwards.
        if (!_requestLimits.HasRoom(player, 1, TenCount)) { Fail(ErrNoLast); return true; }
        var cards = new List<GachaCard>();
        foreach (var node in awards)
        {
            if (node is not JsonObject award || !GachaRequestLimits.TryInteger(award["nId"], out int id)
                || !pool.Cards.TryGetValue(id, out var card) || award["tbGDPL"] is not JsonArray { Count: 4 } gdpl)
            { Fail(ErrNoLast); return true; }
            int[] actual = [card.G, card.D, card.P, card.L];
            for (int i = 0; i < 4; i++)
                if (!GachaRequestLimits.TryInteger(gdpl[i], out int field) || field != actual[i])
                { Fail(ErrNoLast); return true; }
            cards.Add(card);
        }
        if (cards.Count(c => c.Star == 4) != 1 || cards.Count(c => c.Star == 3) != 2
            || cards.Count(c => c.Star == 2) != 7) { Fail(ErrNoLast); return true; }
        int[] counters = [TaskTotal, TaskPrePoolBegin + poolId];
        if (counters.Any(key => player.GetTask(TaskGroup, key) > int.MaxValue - TenCount)
            || player.GetTask(TaskGroup, TaskCardMul) == int.MaxValue) { Fail(ErrNoLast); return true; }
        List<int[]> bonus;
        try { bonus = PlanBonusRewards(pool, player.GetTask(TaskGroup, TaskPrePoolBegin + poolId), TenCount); }
        catch (InvalidDataException) { Fail(ErrNoLast); return true; }
        if (cards.Any(c => _ctx.CharacterCards.Get((uint)c.G, (uint)c.D, (uint)c.P, (uint)c.L) is null)
            || bonus.Any(b => _ctx.ItemTemplates.Get((uint)b[0], (uint)b[1], (uint)b[2], (uint)b[3]) is null))
        { Fail(ErrNoLast); return true; }
        long confirmedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var paymentBefore = new GachaPaymentSnapshot(player);
        var spend = TrySpend(player, pool, true, confirmedAt);
        if (!spend.Ok) { Fail(ErrNoLast); return true; }
        var payment = paymentBefore.After(player);
        var granted = new RewardGrantResult();
        var claimedAwards = (JsonArray)payload["tbAwards"]!;
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            bool first = !CardCollection.HasOwned(player, (uint)card.G, (uint)card.D, (uint)card.P, (uint)card.L);
            claimedAwards[i]!["bFirstGet"] = first;
            claimedAwards[i]!["bHasCard"] = !first;
            _ctx.Rewards.Grant(player, [new[] { card.G, card.D, card.P, card.L, 1 }], RewardSource.Extra, granted);
        }
        _ctx.Rewards.Grant(player, bonus, RewardSource.Extra, granted);
        foreach (int key in counters) player.SetTask(TaskGroup, key, player.GetTask(TaskGroup, key) + TenCount);
        player.SetTask(TaskGroup, TaskCardMul, player.GetTask(TaskGroup, TaskCardMul) + 1);
        payload["bGetCard"] = true;
        pending["claimed"] = true;
        pending["payload"] = payload.DeepClone();
        WriteLast(player, true, claimedAwards, payload["getItem"] as JsonArray);
        var history = GachaHistory.Append(player,1,pool.Id,confirmedAt,cards.Select(c=>new[]{c.G,c.D,c.P,c.L}),payment);
        _ctx.Store.Save(player);
        GachaHistory.Push(session,1,history);
        GameNotify.PushMoney(session, player, spend.ChangedMoney);
        if (spend.ChangedItems.Count > 0) GameNotify.PushItems(session, spend.ChangedItems);
        GameNotify.PushRewards(session, player, granted);
        GameNotify.PushTaskValues(session, TaskDeltaOf(player, pool));
        Reply(session, FnGetLastGacha, payload);
        return true;
    }
}
