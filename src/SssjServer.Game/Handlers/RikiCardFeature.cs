using SssjServer.Game.Config;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game.Handlers;

public sealed class RikiCardFeature : IGameFeature
{
    public string Name => "角色卡图鉴同步（RikiList/RikiData）";
    public void Install(GameFeatureContext ctx)
    {
        var table = ctx.Rewards.RikiCards!;
        ctx.OnPlayerPrepare((session, player) =>
        {
            if (table.SyncOwned(player, ctx.CharacterCards)) ctx.Store.Save(player);
        });
        ctx.OnPlayerLogin((session, player) =>
        {
            var list = new RikiList();
            foreach (uint id in table.OwnedIds(player)) list.Rikidata.Add(new RikiData { Rikiid = id });
            session.Push(SssjServer.Protocol.Pf.RikiList, list);
        });
    }
}
