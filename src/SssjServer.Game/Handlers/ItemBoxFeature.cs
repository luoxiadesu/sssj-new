using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 手动开箱（LuaCall 75 <c>ItemBox_Open</c> / 76 <c>ItemBox_Open_MO</c>）。
///
/// <para><b>0. 命令面与「谁发上行」</b></para>
/// 两条都走客户端 <c>ItemBox:OpenBox</c>（<c>LuaCall.lua:1302</c>/<c>:1311</c>），
/// 但**只有 76 有上行发送点**：
/// <code>
/// UI_ItemBox.lua:331/359    CallServer(ItemBox_Open_MO, {gdpln = {G,D,P,L,num}, select = idx})
/// UI_ItemBox2.lua:189/210   同上
/// （75 在全仓 grep 无 CallServer 调用点）
/// </code>
/// ⇒ <b>75 是服务端主动下发通道</b>（「你获得了这些东西」的通用奖励弹窗，
/// 消费点 <c>ItemBox:OnOpenBox</c> → <c>UI.OpenAsChild('UI_RewardNormal', tbParam)</c>），
/// 本域仍然注册它，但只做「收到就当 76 处理」的兜底 —— 因为原厂客户端不会发，
/// 注册的意义是：万一某个界面版本发了，不至于变成 unhandled 卡住。
///
/// <para><b>1. ★★★ 回包是「GDPL 五元组数组」，不是对象</b></para>
/// <c>ItemBox.lua:87</c> <c>local tbGDPL = tbParam[nIdx]</c> →
/// <c>GetItemBaseInfo(tbGDPL[1], tbGDPL[2], tbGDPL[3], tbGDPL[4])</c>
/// ⇒ <c>tbParam</c> 本身是数组，每项是 <c>[G, D, P, L, N]</c>。
/// 回对象会让 <c>#tbParam == 0</c>（<c>:102</c> <c>if #tbParam == 0 then return end</c>）
/// ⇒ **奖励弹窗静默不出现**，玩家以为箱子白开了。
///
/// <para><b>2. ★★★ 抽取是服务端职责 —— 这里推翻了 <see cref="ItemBoxTable"/> 的旧注释</b></para>
/// 旧注释担心 <c>Random</c>/<c>Prob</c> 需要「与客户端一致的随机源」。**不需要**：
/// 客户端 <c>ItemBox.lua</c> 全文 <b>0 处</b> <c>random</c> 调用，
/// <c>OnOpenBox</c>/<c>OnOpenBoxMO</c> 都是纯渲染下发结果。
/// 其余读 <c>GetBoxCfg</c> 的地方（<c>UI_ItemBox.lua:94</c>、<c>UI_GachaDetail.lua:77</c>…）
/// 都只用来**预览"可能开出什么"**。⇒ 实现见 <see cref="ItemBoxTable.Roll"/>。
///
/// <para><b>3. ★ <c>select</c> 是 1-based 索引，0 表示「该箱无 Select 组」</b></para>
/// <c>UI_ItemBox.lua:104..113</c>：有 Select 组时 <c>selectidx = 缓存值 或 1</c>，
/// 没有时置 <c>0</c>；<c>:177</c> 用 <c>list[0].idx</c> 赋值 ⇒ 索引来自配置行顺序。
///
/// <para><b>4. ★★ 一次最多开 10 个（客户端自己夹的，服务端要复核）</b></para>
/// <c>UI_ItemBox.lua:344</c> <c>if num &gt; 10 then num = 10 end</c>；
/// 且有 Select 组时强制 <c>num = 1</c>（<c>:347..353</c>）。
/// 服务端**独立复核**：不能信客户端传的 num（否则一条包就能刷爆背包）。
///
/// <para><b>5. 错误约定：LuaCall 数字族，且本族「失败就不回包」</b></para>
/// 客户端 <c>OnOpenBox</c>/<c>OnOpenBoxMO</c> 里**没有任何 <c>nError</c> 分支**
/// （<c>ItemBox.lua:53..120</c> 通篇只判 <c>type(tbParam) ~= "table"</c> 与 <c>#tbParam == 0</c>）
/// ⇒ 回 <c>{nError=N}</c> 客户端会当成「一个长度为 0 的数组」直接 return，
/// 与不回包等效。所以失败时**回空数组**即可（让可能存在的遮罩解开），
/// 拒绝原因写日志。
/// ★ 这也意味着**不能**靠回包给玩家报错 —— 本域的校验必须宽容，
/// 宁可放行也不要把合法开箱拒掉。
/// </summary>
public sealed class ItemBoxFeature : IGameFeature
{
    /// <summary>LuaCall.ItemBox_Open = 75（<c>LuaCall.lua:74</c>「请求打开道具箱」，服务端下发通道）。</summary>
    public const int OpenBoxCmd = 75;

    /// <summary>LuaCall.ItemBox_Open_MO = 76（<c>:75</c>「请求打开道具箱,手动打开箱子」）。</summary>
    public const int OpenBoxManualCmd = 76;

    /// <summary>
    /// 一次开箱上限（<c>UI_ItemBox.lua:344</c> <c>if num &gt; 10 then num = 10 end</c>）。
    /// 服务端独立复核，见类注释 §4。
    /// </summary>
    public const int MaxOpenPerRequest = 10;

    /// <summary>
    /// 礼包箱的 <c>ClassName</c>（<c>ItemList.txt</c>），与
    /// <see cref="RewardService"/> 自动展开用的是同一个值。
    /// </summary>
    public const string GiftBoxClassName = "IBGiftAuto";

    private GameFeatureContext _ctx = null!;
    private ItemBoxTable _boxes = ItemBoxTable.Empty;

    /// <summary>
    /// 随机源。★ 用实例字段而非 <c>Random.Shared</c>，便于测试注入确定性种子
    /// （<see cref="SetRngForTests"/>）。
    /// </summary>
    private Random _rng = new();

    public string Name => "手动开箱（LuaCall 75 / 76 ItemBox_Open）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _boxes = ItemBoxTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));

        ctx.MapLuaCall(OpenBoxCmd, OnOpenBox);
        ctx.MapLuaCall(OpenBoxManualCmd, OnOpenBox);
    }

    /// <summary>测试用：注入确定性随机源。</summary>
    internal void SetRngForTests(Random rng) => _rng = rng;

    /// <summary>
    /// 75 / 76 手动开箱。
    ///
    /// 流程：解析 <c>gdpln</c> + <c>select</c> → 找背包里的箱子道具 → 复核数量与模板
    /// → <see cref="ItemBoxTable.Roll"/> 抽取 → 扣箱子、发内容 → 推 pf 1031/1032
    /// → 回 GDPL 数组（§1）。
    /// </summary>
    internal Task OnOpenBox(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var (gdpl, want, select) = ParseRequest(param);
        if (gdpl is null)
        {
            Log.Warn($"[session {session.Id}] ItemBox_Open({cmd}) 参数非法：{param?.ToJsonString()}");
            ReplyEmpty(session, cmd);
            return Task.CompletedTask;
        }

        var (g, d, p, l) = gdpl.Value;
        string name = _ctx.ItemTemplates.NameOf(g, d, p, l);

        // ---- 找背包里的箱子 ----
        var stack = player.Items.FirstOrDefault(
            it => it.Genre == g && it.Detail == d && it.Particular == p && it.Level == l
                && it.LockOn == 0 && it.Count > 0);
        if (stack is null || stack.Count == 0)
        {
            Log.Warn($"[session {session.Id}] ItemBox_Open({cmd}) 背包里没有 {name}({g}-{d}-{p}-{l}) ⇒ 拒绝");
            ReplyEmpty(session, cmd);
            return Task.CompletedTask;
        }

        // ---- 复核次数（§4：不信客户端）----
        int times = Math.Clamp(want, 1, MaxOpenPerRequest);
        times = Math.Min(times, (int)stack.Count);

        // ---- 找箱子 id（ItemList.ExtParam1）----
        var tpl = _ctx.ItemTemplates.Get(g, d, p, l);
        if (tpl is null
            || !int.TryParse(tpl.ExtParam1, out int boxId)
            || boxId <= 0
            || !_boxes.Has(boxId))
        {
            Log.Warn($"[session {session.Id}] ItemBox_Open({cmd}) {name} 的 ExtParam1="
                   + $"\"{tpl?.ExtParam1}\" 查不到箱子 ⇒ 拒绝（道具不扣）");
            ReplyEmpty(session, cmd);
            return Task.CompletedTask;
        }

        // ★ 有 Select 组时客户端强制 num=1（UI_ItemBox.lua:347..353）⇒ 服务端同样收敛，
        //   否则「选一个 × 10」会发出 10 份玩家没预期的东西。
        var selects = _boxes.SelectOf(boxId);
        if (selects.Count > 0 && select > 0)
            times = 1;

        // ---- 抽取 ----
        var rolled = _boxes.Roll(boxId, times, select, _rng);
        if (rolled.Count == 0)
        {
            Log.Warn($"[session {session.Id}] ItemBox_Open({cmd}) {name} box {boxId} "
                   + $"抽取结果为空（times={times} select={select}）⇒ 不扣道具");
            ReplyEmpty(session, cmd);
            return Task.CompletedTask;
        }

        // ---- 扣箱子 ----
        var removed = new List<Item>();
        stack.Count -= (uint)times;
        if (stack.Count == 0)
        {
            player.Items.Remove(stack);
            removed.Add(stack);
        }

        // ---- 发内容（复用 RewardService：会自动处理礼包套娃 / 皮肤解锁 / 货币折算）----
        // ★ 用 RewardSource.Extra：第 6 位忽略、100% 发放。
        //   不能用 FixedAward —— 那会把第 6 位当万分比概率**再抽一次**，
        //   而 Roll() 出来的已经是「中签结果」⇒ 双重随机会让玩家少拿东西。
        var granted = _ctx.Rewards.Grant(player, rolled, RewardSource.Extra);
        _ctx.Store.Save(player);

        // ---- 推送：先推数据再回包 ----
        GameNotify.PushRewards(session, player, granted);
        if (removed.Count > 0)
            GameNotify.PushItemsRemoved(session, removed);
        else
            GameNotify.PushItem(session, stack);

        Log.Info($"[session {session.Id}] ItemBox_Open({cmd}) {name} ×{times} → box {boxId}"
               + (select > 0 ? $" select={select}" : "")
               + "：" + string.Join('、', rolled.Select(r => string.Join('-', r))));

        // §1：回 GDPL 五元组数组
        var arr = new JsonArray();
        foreach (var r in rolled)
        {
            var one = new JsonArray();
            foreach (int v in r)
                one.Add(v);
            arr.Add(one);
        }
        _ctx.LuaCalls.CallClient(session, cmd, arr);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 解析上行 <c>{gdpln = {G,D,P,L,num}, select = idx}</c>。
    /// ★ 也容忍裸 GDPL 数组（防御性：75 若被服务端以外的地方触发时形状未取证）。
    /// </summary>
    internal static ((uint G, uint D, uint P, uint L)? Gdpl, int Times, int Select) ParseRequest(JsonNode? param)
    {
        JsonArray? gdpln = null;
        int select = 0;

        switch (param)
        {
            case JsonObject o:
                gdpln = o["gdpln"] as JsonArray;
                select = ReadInt(o["select"]);
                break;

            // 裸数组：可能是 [G,D,P,L,N]
            case JsonArray a when a.Count >= 4 && a[0] is JsonValue:
                gdpln = a;
                break;
        }

        if (gdpln is null || gdpln.Count < 4)
            return (null, 1, 0);

        int g = ReadInt(gdpln[0], -1);
        int d = ReadInt(gdpln[1], -1);
        int p = ReadInt(gdpln[2], -1);
        int l = ReadInt(gdpln[3], -1);
        if (g < 0 || d < 0 || p < 0 || l < 0)
            return (null, 1, 0);

        int times = gdpln.Count >= 5 ? ReadInt(gdpln[4], 1) : 1;
        return (((uint)g, (uint)d, (uint)p, (uint)l), times <= 0 ? 1 : times, select);
    }

    private static int ReadInt(JsonNode? node, int fallback = 0)
    {
        if (node is not JsonValue v) return fallback;
        if (v.TryGetValue<int>(out int i)) return i;
        if (v.TryGetValue<double>(out double dd)) return (int)dd;
        if (v.TryGetValue<string>(out string? s) && int.TryParse(s, out int pp)) return pp;
        return fallback;
    }

    /// <summary>
    /// 失败回包：**空数组**。见类注释 §5 —— 本族没有 <c>nError</c> 通道，
    /// 客户端对空数组直接 return，等效于「什么都没发生」。
    /// </summary>
    private void ReplyEmpty(ClientSession session, int cmd)
        => _ctx.LuaCalls.CallClient(session, cmd, new JsonArray());
}
