using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 武器养成（`sFunction = WeaponLogicMsg`，6 个 nCmd）。
/// 取证见 `docs/05` §5.4 + `work/weapon-genre-forensics.md`，台账 `docs/05` §28。
///
/// <para><b>0. ★★★ 本族的错误约定：`S2CCall` 字符串族 —— 成功必须带 <c>nError = 0</c></b></para>
/// 客户端 `S2CCall.lua:153` 是 <c>if tbParam and tbParam.nError &gt; 0 then</c>
/// ⇒ **数值比较**，缺字段会 `nil &gt; 0` 抛错。这与少女养成族（真值判断、成功绝不带）**相反**。
/// 另外 <c>nCmd</c> 必须回显（`:163..187` 按它分流到 5 个界面回调）。
///
/// <para><b>1. ★★★ <c>nCmd == 2</c>（分解）的下行形状与那个致命陷阱</b></para>
/// <c>tbParam.tbParam</c> 是**位置数组** <c>[gold, [[G,D,P,L,N],…]]</c>。
/// 客户端 `S2CCall.lua:168..178`：
/// <code>
/// if tbParam.tbParam[1] &gt; 0 then                       -- 金币 &gt; 0
///     if not tbParam.tbParam[2] then tbParam.tbParam[2] = {} end   -- 只在这个分支里补 {}
///     table.insert(tbParam.tbParam[2], 1, {15,1,1,1, tbParam.tbParam[1]})
/// end
/// if #tbParam.tbParam[2] &gt; 0 then                      -- ★ 无条件取长度
/// </code>
/// ⇒ **金币为 0 且没给 <c>[2]</c> 时，`#nil` 直接抛** —— 补 `{}` 只发生在
/// 「金币 &gt; 0」那条分支里。所以服务端**必须无条件给 <c>[2]</c>（可空数组）**。
///
/// <para><b>2. ★★ 回包 handler 不收数据 ⇒ 必须「先推 pf 1031、再回包」</b></para>
/// `UI_WeaponStrong.lua:1650` 的 <c>OnServerSuccess(bBreak)</c> 只收一个 bool，
/// 数值全部从客户端本地缓存（<c>self.tbSuccessData</c> / <c>Game.EquipManager</c>）重读
/// ⇒ 与角色卡域同一条经验（AGENTS.md §5）：反了就是「数值对了但界面不动」。
///
/// <para><b>3. 武器的经验列是 <c>NeedExp3</c></b></para>
/// 见 <see cref="WeaponTable"/> 的类注释（取证文档写 <c>NeedExp2</c> 是错的）。
///
/// <para><b>4. 已实现 / 未实现</b></para>
/// <list type="table">
///   <item><term>nCmd 0 打造</term><description>★ **不实现**（论证见 <see cref="OnForge"/>）：
///     客户端 5 个发送点里 4 个被注释，仅剩的 `UI_WeaponSystemNEW.lua:805` 属抽卡式「武器池」，
///     依赖未取证的 `WeaponPool` 档期表。收到只记日志 + 回错误码，不静默。</description></item>
///   <item><term>nCmd 1 强化</term><description>✅ 吃素材武器的经验 → 升级</description></item>
///   <item><term>nCmd 2 分解</term><description>✅ 折金币 + 按万分比返材料</description></item>
///   <item><term>nCmd 3 突破</term><description>✅ 扣货币 + 提升等级上限</description></item>
///   <item><term>nCmd 4/5 校准</term><description>✅ 回执（重铸技能的随机池未取证，
///     只保证不卡遮罩、界面能刷新；见 <see cref="OnRecast"/>）</description></item>
/// </list>
/// </summary>
public sealed class WeaponFeature : IGameFeature
{
    /// <summary>下行入口（`S2CCall.lua:152` <c>S2C.WeaponLogicMsg</c>）。</summary>
    public const string Function = "WeaponLogicMsg";

    /// <summary>打造。</summary>
    public const int CmdForge = 0;

    /// <summary>强化（吃素材武器涨经验）。</summary>
    public const int CmdStrong = 1;

    /// <summary>分解。</summary>
    public const int CmdDecompose = 2;

    /// <summary>突破（提升等级上限）。</summary>
    public const int CmdBreak = 3;

    /// <summary>校准第一步。</summary>
    public const int CmdRecast = 4;

    /// <summary>校准第二步（选择保留旧的还是换新的）。</summary>
    public const int CmdRecastChoose = 5;

    /// <summary>成功（`S2CCall.lua:153` 是 <c>nError &gt; 0</c> 的数值比较 ⇒ 必须带 0）。</summary>
    public const int ErrOk = 0;

    /// <summary>
    /// 通用失败码。★ 客户端会拿它查本地化 `ui.Error.&lt;code&gt;` 并弹框
    /// （`S2CCall.lua:159`），查不到就显示原始 key —— 属可接受的降级。
    /// </summary>
    public const int ErrGeneric = 1;

    /// <summary>分解返还金币走的货币 id（客户端硬编码 <c>{15,1,1,1,gold}</c>，即道具形态的金币）。</summary>
    public const int GoldMoneyId = 2;

    /// <summary>武器分解用 <c>ItemRarityItem.txt</c> 的**第 2 组**（`WeaponCommon.lua:597` 传 type=2）。</summary>
    public const int DecomposeParamGroup = 2;

    /// <summary>校准选择：保留旧技能。</summary>
    public const int RecastKeepOld = 1;

    /// <summary>校准选择：换成新技能。</summary>
    public const int RecastTakeNew = 2;

    private GameFeatureContext _ctx = null!;
    private WeaponTable _weapons = WeaponTable.Empty;
    private CardTable _cards = CardTable.Empty;

    public string Name => "武器养成（WeaponLogicMsg：1 强化 / 2 分解 / 3 突破 / 4,5 校准）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        string dir = Path.GetFullPath(ctx.Options.SettingDirectory);
        _weapons = WeaponTable.Load(dir);
        // 分解返还复用角色卡域那套 ItemRarityItem 解析（武器取第 2 组，见 DecomposeParamGroup）
        _cards = CardTable.Load(dir);
        ctx.MapFunction(Function, OnCommand);
    }

    private Task OnCommand(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        // ★ 上行一定是对象（`WeaponCommon:SendToServer` 传的是 table），但真机上
        // 见过空数组形态的上行（咖啡馆 115 那条坑）⇒ 先判类型再取键，否则 JsonArray 会抛
        var obj = param as JsonObject;
        int cmd = ReadInt(obj?["nCmd"], -1);

        return cmd switch
        {
            CmdForge => OnForge(session, player, obj),
            CmdStrong => OnStrong(session, player, obj),
            CmdDecompose => OnDecompose(session, player, obj),
            CmdBreak => OnBreak(session, player, obj),
            CmdRecast => OnRecast(session, player, obj),
            CmdRecastChoose => OnRecastChoose(session, player, obj),
            _ => Unknown(session, cmd, param),
        };
    }

    // ================= nCmd 0：打造（★ 已由 WeaponForgeFeature 实现）=================

    /// <summary>
    /// 打造（游戏内叫「武器补给」）。上行 <c>{nCmd=0, Id=&lt;池id&gt;, isTen=&lt;bool&gt;}</c>。
    ///
    /// <para><b>★★★ 第九轮：本命令已实现，实现在 <see cref="WeaponForgeFeature"/></b></para>
    /// 那边用 <c>ctx.AddFunctionCoHandler</c> 挂在同一个 sFunction 上、只认 <c>nCmd == 0</c>，
    /// 且**协处理器先跑** ⇒ 正常情况下本方法**永远不会被调用**。
    /// 它留在这里只作为「协处理器没装上」的兜底（例如 <see cref="WeaponForgeFeature"/>
    /// 因缺配置表而降级），此时至少要回包解掉遮罩。
    ///
    /// <para><b>★★ 第八轮写在这里的三条「不实现」论证，其中两条已被推翻</b></para>
    /// 取证过程见 `work/round9-mainsession-forensics.md` §1.2：
    /// <list type="number">
    ///   <item>「5 个发送点 4 个被注释」—— ✅ **成立**，活着的是
    ///     `UI_WeaponSystemNEW.lua:803 SendServerMsg`。</item>
    ///   <item>「依赖未取证的**武器池档期表**」—— ★★★ **错**。那张表就是
    ///     <c>gacha.txt</c> 的 <c>Type == 0</c> 行（`WeaponForge.lua:19/21`），
    ///     池内容在 <c>Gacha/Pools/&lt;Pool&gt;.txt</c>（本地 25 个 <c>weapon_*.txt</c> 全都在）。
    ///     真机自报**池 1「武器补给」现在就开着**，一路开到 2030-03-02。</item>
    ///   <item>「<c>Id</c>/<c>Type</c> 不是主键 ⇒ 无法确定抽到哪把武器」—— ★★★ **错**。
    ///     <c>Id</c> 是池 id，池表每行的 <c>GDPLN</c> 列**就是**武器四元组主键；
    ///     而 <c>Type</c> 字段**上行压根不存在**（真机只发 <c>nCmd/Id/isTen</c>）。</item>
    /// </list>
    /// ⇒ 通用教训：「某张表未取证」这类判断，下结论前先 grep 配置目录
    /// （<c>ls extracted/setting/ | grep weapon_</c> 一条命令就能推翻它）。
    ///
    /// <para><b>★★★ 另一条更要命的更正：不回包会卡遮罩 5 分钟</b></para>
    /// 第八轮认为「唯一活着的发送点 <c>bNoWait=true</c> ⇒ 不回包也不会卡界面」——
    /// 但 <c>SendServerMsg</c>（`UI_WeaponSystemNEW.lua:803`）在 <c>SendToServer</c> **之前**
    /// 自己调了 <c>Game.Blocking.Show(300)</c> ⇒ 实际有 300 秒遮罩。
    /// ⇒ 判断「不回包会不会卡」要看**整条调用链上所有 <c>Blocking.Show</c>**，
    /// 不能只看发包函数的 <c>bNoWait</c> 参数。
    /// </summary>
    private Task OnForge(ClientSession session, Player player, JsonObject? param)
    {
        Log.Warn($"[session {session.Id}] WeaponLogicMsg nCmd=0(打造) 落到 WeaponFeature 兜底分支" +
                 "（正常应由 WeaponForgeFeature 协处理器处理，检查它是否装配成功）" +
                 $" Id={ReadInt(param?["Id"])} isTen={param?["isTen"]}");
        Fail(session, CmdForge, ErrGeneric);
        return Task.CompletedTask;
    }

    // ================= nCmd 1：强化 =================

    /// <summary>
    /// 强化：把 <c>tbGuid</c> 里的素材武器吃掉，换成目标武器的经验。
    ///
    /// 上行 <c>{nGuid, tbGuid: [[guid, count], …]}</c>（`UI_WeaponStrong.lua:1495..1504`）。
    /// ★ <c>tbGuid</c> 的每项是**二元位置数组**，不是对象。
    ///
    /// 经验公式（1:1 照 `WeaponCommon.lua:454..455`）：每把素材提供
    /// <c>RarityExp + floor(min(SumExpBelow * 0.3, 150000))</c>。
    /// 强化液（7-3）按 ItemList.ExtParam1/2 提供经验及金币消耗，支持堆叠数量。
    /// 所有素材与金币预先校验，失败时不改存档。
    /// 升级推演走 <see cref="WeaponTable.DestLevel"/>（保留余额、撞上限仍累加）。
    /// </summary>
    private Task OnStrong(ClientSession session, Player player, JsonObject? param)
    {
        uint guid = (uint)ReadInt(param?["nGuid"]);
        var target = FindWeapon(player, guid);
        if (target is null)
        {
            Fail(session, CmdStrong, ErrGeneric);
            Log.Warn($"[session {session.Id}] WeaponLogicMsg 强化：guid={guid} 不是玩家的武器");
            return Task.CompletedTask;
        }

        var tpl = _weapons.Find(target.Detail, target.Particular, target.Level);
        if (tpl is null)
        {
            Fail(session, CmdStrong, ErrGeneric);
            Log.Warn($"[session {session.Id}] WeaponLogicMsg 强化：武器 {Describe(target)} 不在 Weapon.txt 里");
            return Task.CompletedTask;
        }

        // The client sends [guid, count] pairs. Reject malformed entries as a
        // whole: the shared permissive reader otherwise silently drops them.
        if (param?["tbGuid"] is not JsonArray raw || raw.Count == 0 || raw.Any(el =>
                el is not JsonArray row || row.Count != 2 ||
                !uint.TryParse(row[0]?.ToString(), out uint id) || id == 0 ||
                !int.TryParse(row[1]?.ToString(), out int n) || n <= 0))
        {
            Fail(session, CmdStrong, ErrGeneric);
            return Task.CompletedTask;
        }
        var mats = ReadGuidCountList(raw);
        int maxLevel = _weapons.MaxLevel(tpl.Rarity, (int)target.BreakLevel);
        long addExp = 0;
        long gold = 0;
        var consumed = new List<(Item Item, uint Count)>();
        var seen = new HashSet<uint>();

        foreach (var (matGuid, count) in mats)
        {
            var mat = player.Items.FirstOrDefault(i => i.Guid == matGuid);
            if (matGuid == guid || !seen.Add(matGuid) || mat is null ||
                mat.LockOn != 0 || mat.Count < count)
            {
                Fail(session, CmdStrong, ErrGeneric);
                return Task.CompletedTask;
            }

            if (mat.Genre == 7 && mat.Detail == 3)
            {
                var material = _ctx.ItemTemplates.Get(mat.Genre, mat.Detail, mat.Particular, mat.Level);
                if (material is null || !int.TryParse(material.ExtParam1, out int exp) || exp <= 0 ||
                    !int.TryParse(material.ExtParam2, out int cost) || cost < 0)
                {
                    Fail(session, CmdStrong, ErrGeneric);
                    return Task.CompletedTask;
                }
                // UI_Updatebox: experience = ExtParam1, gold = ExtParam2, per unit.
                addExp += (long)exp * count;
                gold += (long)cost * count;
            }
            else if (mat.Genre == WeaponTable.WeaponGenre && count == 1)
            {
                var matTpl = _weapons.Find(mat.Detail, mat.Particular, mat.Level);
                if (matTpl is null || mat.OwnGuid != 0 ||
                    player.Formations.Any(f => f.Slots.Any(slot => slot.WeaponGuid == matGuid)))
                {
                    Fail(session, CmdStrong, ErrGeneric);
                    return Task.CompletedTask;
                }
                // WeaponCommon:GetWeaponGiveExp/GetWeaponGiveGold, used by manual selection.
                addExp += _weapons.RarityExp(matTpl.Rarity) +
                    (long)Math.Floor(Math.Min(_weapons.SumExpBelow((int)mat.EnhanceLevel) * 0.3, 150000));
                gold += _weapons.RarityExp(matTpl.Rarity);
            }
            else
            {
                Fail(session, CmdStrong, ErrGeneric);
                return Task.CompletedTask;
            }
            consumed.Add((mat, (uint)count));
        }

        if (addExp <= 0 || addExp > int.MaxValue - (long)target.EnhanceExp ||
            gold > int.MaxValue || player.GetMoney(GoldMoneyId) < gold)
        {
            Fail(session, CmdStrong, ErrGeneric);
            return Task.CompletedTask;
        }

        int oldLevel = (int)target.EnhanceLevel;
        int oldExp = (int)target.EnhanceExp;
        var (newLevel, newExp) = _weapons.DestLevel(oldLevel, oldExp, (int)addExp, maxLevel);

        target.EnhanceLevel = (uint)newLevel;
        target.EnhanceExp = (uint)newExp;
        var removed = new List<Item>();
        var changed = new List<Item> { target };
        foreach (var (mat, count) in consumed)
        {
            mat.Count -= count;
            if (mat.Count == 0)
            {
                player.Items.Remove(mat);
                removed.Add(mat);
            }
            else
                changed.Add(mat);
        }
        player.AddMoney(GoldMoneyId, -(int)gold);
        _ctx.Store.Save(player);

        GameNotify.PushItemsRemoved(session, removed);
        GameNotify.PushItems(session, changed);
        if (gold > 0)
            GameNotify.PushMoney(session, player, GoldMoneyId);
        Ok(session, CmdStrong);

        Log.Info($"[session {session.Id}] WeaponLogicMsg 强化 {Describe(target)}：" +
                 $"吃掉 {consumed.Count} 把素材（+{addExp} 经验、消耗 {gold} 晶币）→ " +
                 $"LV{oldLevel}({oldExp}) → LV{newLevel}({newExp})，上限 {maxLevel}");
        return Task.CompletedTask;
    }

    // ================= nCmd 2：分解 =================

    /// <summary>
    /// 分解。上行 <c>{tbGuid: [guid, …]}</c>（**没有 <c>nGuid</c>**，且是裸 guid 数组）。
    ///
    /// 金币公式 1:1 照 `WeaponCommon:GetWeaponSellGold`（`:596..601`）：
    /// <c>floor(SumExpBelow(等级) * Param2[1] + 稀有度 * Param2[2])</c>
    /// —— ★ 用 <c>ItemRarityItem.txt</c> 的**第 2 组**（角色卡用第 1 组）。
    /// 材料按 <c>DisItem2</c> 的万分比概率返还。
    ///
    /// ★★★ 回包**必须无条件带 <c>tbParam[2]</c>**（可空数组），否则金币为 0 时
    /// 客户端 `#nil` 抛异常 —— 见类注释 §1。
    /// </summary>
    private Task OnDecompose(ClientSession session, Player player, JsonObject? param)
    {
        var guids = ReadGuidList(param?["tbGuid"]);
        if (guids.Count == 0)
        {
            Fail(session, CmdDecompose, ErrGeneric);
            Log.Warn($"[session {session.Id}] WeaponLogicMsg 分解：guid 列表为空");
            return Task.CompletedTask;
        }

        long gold = 0;
        var removed = new List<Item>();
        var returned = new List<int[]>();

        foreach (uint guid in guids)
        {
            var w = FindWeapon(player, guid);
            if (w is null || w.LockOn != 0)
                continue;
            var tpl = _weapons.Find(w.Detail, w.Particular, w.Level);
            if (tpl is null)
                continue;

            gold += DecomposeGold((int)w.EnhanceLevel, tpl.Rarity);
            foreach (var item in _cards.DecomposeItems(tpl.Rarity, DecomposeParamGroup))
                returned.Add(new[] { (int)item.G, (int)item.D, (int)item.P, (int)item.L, item.N });

            player.Items.Remove(w);
            removed.Add(w);
        }

        if (removed.Count == 0)
        {
            Fail(session, CmdDecompose, ErrGeneric);
            Log.Warn($"[session {session.Id}] WeaponLogicMsg 分解：{guids.Count} 个 guid 一个都不可分解");
            return Task.CompletedTask;
        }

        var granted = new RewardGrantResult();
        if (gold > 0)
            _ctx.Rewards.Grant(player, new[] { new[] { 15, 1, 1, 1, (int)gold } }, RewardSource.Extra, granted);
        if (returned.Count > 0)
            _ctx.Rewards.Grant(player, returned, RewardSource.Extra, granted);

        _ctx.Store.Save(player);

        GameNotify.PushItemsRemoved(session, removed);
        GameNotify.PushRewards(session, player, granted);

        // ★ tbParam = [gold, [[G,D,P,L,N],…]]，**第 2 项无条件给**（可空数组）
        var items = new JsonArray();
        foreach (var r in returned)
            items.Add(new JsonArray(r[0], r[1], r[2], r[3], r[4]));

        Reply(session, CmdDecompose, new JsonArray((int)gold, items));

        Log.Info($"[session {session.Id}] WeaponLogicMsg 分解 {removed.Count} 把武器：" +
                 $"金币 +{gold}、返还材料 {returned.Count} 项（{granted.Describe()}）");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 分解金币：<c>floor(累计经验 * Param2[0] + 稀有度 * Param2[1])</c>。
    /// ★ 与客户端 `WeaponCommon:GetWeaponSellGold` 同源；那边在**弹确认框之前**
    /// 就用同一公式给玩家看过预览金额，算错会「显示的和到手的不一致」。
    /// </summary>
    internal long DecomposeGold(int level, int rarity)
    {
        var row = _cards.RarityOf(rarity);
        if (row is null)
            return 0;
        var (c0, c1, _) = row.For(DecomposeParamGroup);
        long preExp = _weapons.SumExpBelow(level);
        return (long)Math.Floor(preExp * c0 + rarity * c1);
    }

    // ================= nCmd 3：突破 =================

    /// <summary>
    /// 突破：扣货币 + <c>breaklevel + 1</c>（提升等级上限）。
    /// 上行 <c>{nGuid, tbGuid: [[guid,count],…]}</c> —— <c>tbGuid</c> 是突破材料。
    ///
    /// 消耗来自 <c>WeaponBreak.txt</c> 的 <c>CurrencyId&lt;n&gt;</c>/<c>CurrencyCount&lt;n&gt;</c>
    /// （n = 当前已突破次数 + 1）。次数用尽时 <see cref="WeaponTable.BreakCostFor"/> 返回 null ⇒ 拒绝。
    /// </summary>
    private Task OnBreak(ClientSession session, Player player, JsonObject? param)
    {
        uint guid = (uint)ReadInt(param?["nGuid"]);
        var target = FindWeapon(player, guid);
        if (target is null)
        {
            Fail(session, CmdBreak, ErrGeneric);
            Log.Warn($"[session {session.Id}] WeaponLogicMsg 突破：guid={guid} 不是玩家的武器");
            return Task.CompletedTask;
        }

        var tpl = _weapons.Find(target.Detail, target.Particular, target.Level);
        if (tpl is null)
        {
            Fail(session, CmdBreak, ErrGeneric);
            Log.Warn($"[session {session.Id}] WeaponLogicMsg 突破：{Describe(target)} 不在 Weapon.txt 里");
            return Task.CompletedTask;
        }

        int breakLevel = (int)target.BreakLevel;
        int breakMax = _weapons.BreakNumFor(tpl.Rarity);
        if (breakLevel >= breakMax)
        {
            Fail(session, CmdBreak, ErrGeneric);
            Log.Warn($"[session {session.Id}] WeaponLogicMsg 突破：{Describe(target)} 已突破满" +
                     $"（{breakLevel}/{breakMax}）");
            return Task.CompletedTask;
        }

        var cost = _weapons.BreakCostFor(tpl.Rarity, breakLevel);
        if (cost is null)
        {
            Fail(session, CmdBreak, ErrGeneric);
            Log.Warn($"[session {session.Id}] WeaponLogicMsg 突破：Rarity {tpl.Rarity} 第 {breakLevel + 1} 次无消耗配置");
            return Task.CompletedTask;
        }

        // ★ 突破前应达到当前等级上限（客户端本地也会拦，这里是服务端兜底）
        int curMax = _weapons.MaxLevel(tpl.Rarity, breakLevel);
        if (target.EnhanceLevel < curMax)
        {
            Fail(session, CmdBreak, ErrGeneric);
            Log.Warn($"[session {session.Id}] WeaponLogicMsg 突破：{Describe(target)} " +
                     $"等级 {target.EnhanceLevel} 未达上限 {curMax}");
            return Task.CompletedTask;
        }

        // WeaponBreak.BreakItem is a required COUNT, not an item template id.
        // UI_Updatebox allows identical weapons or WeaponCommon's universal weapon.
        if (param?["tbGuid"] is not JsonArray raw || raw.Count == 0 || raw.Any(el =>
                el is not JsonArray row || row.Count != 2 ||
                !uint.TryParse(row[0]?.ToString(), out uint id) || id == 0 ||
                !int.TryParse(row[1]?.ToString(), out int n) || n != 1))
        {
            Fail(session, CmdBreak, ErrGeneric);
            return Task.CompletedTask;
        }
        var materials = ReadGuidCountList(raw);
        var removed = new List<Item>();
        var seen = new HashSet<uint>();
        foreach (var (materialGuid, _) in materials)
        {
            var material = FindWeapon(player, materialGuid);
            bool same = material is not null && material.Detail == target.Detail &&
                material.Particular == target.Particular && material.Level == target.Level;
            bool universal = material is not null && material.Detail == 2 && material.Level == 1 &&
                material.Particular == (tpl.Rarity > 3 ? 10000u : 10001u);
            if (material is null || materialGuid == guid || !seen.Add(materialGuid) ||
                material.Count != 1 || material.LockOn != 0 || material.OwnGuid != 0 ||
                (!same && !universal) ||
                player.Formations.Any(f => f.Slots.Any(slot => slot.WeaponGuid == materialGuid)))
            {
                Fail(session, CmdBreak, ErrGeneric);
                return Task.CompletedTask;
            }
            removed.Add(material);
        }
        if (removed.Count != cost.BreakItem || cost.BreakItem <= 0)
        {
            Fail(session, CmdBreak, ErrGeneric);
            return Task.CompletedTask;
        }

        if (cost.CurrencyCount > 0)
        {
            long have = player.GetMoney(cost.CurrencyId);
            if (have < cost.CurrencyCount)
            {
                Fail(session, CmdBreak, ErrGeneric);
                Log.Warn($"[session {session.Id}] WeaponLogicMsg 突破：货币 {cost.CurrencyId} 不足" +
                         $"（有 {have}、要 {cost.CurrencyCount}）");
                return Task.CompletedTask;
            }
            player.AddMoney(cost.CurrencyId, -cost.CurrencyCount);
        }

        foreach (var material in removed)
            player.Items.Remove(material);
        target.BreakLevel = (uint)(breakLevel + 1);
        int newMax = _weapons.MaxLevel(tpl.Rarity, breakLevel + 1);
        _ctx.Store.Save(player);

        if (cost.CurrencyCount > 0)
            GameNotify.PushMoney(session, player, cost.CurrencyId);
        GameNotify.PushItemsRemoved(session, removed);
        GameNotify.PushItem(session, target);
        Ok(session, CmdBreak);

        Log.Info($"[session {session.Id}] WeaponLogicMsg 突破 {Describe(target)}：" +
                 $"{breakLevel} → {breakLevel + 1} 次（上限 {curMax} → {newMax}）、" +
                 $"扣货币 {cost.CurrencyId}×{cost.CurrencyCount}");
        return Task.CompletedTask;
    }

    // ================= nCmd 4/5：校准 =================

    /// <summary>
    /// 校准第一步（掷出一组新的重铸技能）。上行 <c>{nGuid}</c>。
    ///
    /// <para><b>★ 只回执，不改数据 —— 这是有意的</b></para>
    /// 客户端 `UI_WeaponStrong.lua:2808` 的 <c>OnServerRecast</c> 只调
    /// <c>self:ShowRightPanel()</c>（重绘右侧面板，数据从本地缓存读）。
    /// 而「掷出什么新技能」需要**重铸技能的随机池 + 权重**，这部分未取证
    /// （`WeaponRecast*.txt` 一类的表不在本地快照里）。
    /// ⇒ 现在保证「点了不卡遮罩、面板能刷新」；真正的技能变更留待取证后补。
    /// 不回包的后果是**卡死遮罩**（`WeaponCommon:SendToServer` 默认挂 <c>Blocking.Show()</c>）。
    /// </summary>
    private Task OnRecast(ClientSession session, Player player, JsonObject? param)
    {
        uint guid = (uint)ReadInt(param?["nGuid"]);
        if (FindWeapon(player, guid) is null)
        {
            Fail(session, CmdRecast, ErrGeneric);
            Log.Warn($"[session {session.Id}] WeaponLogicMsg 校准：guid={guid} 不是玩家的武器");
            return Task.CompletedTask;
        }

        Ok(session, CmdRecast);
        Log.Info($"[session {session.Id}] WeaponLogicMsg 校准第一步 guid={guid}" +
                 $"（★ 仅回执解遮罩：重铸技能随机池未取证）");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 校准第二步。上行 <c>{nGuid, nSelect}</c>（1 = 保留旧 / 2 = 换新）。
    /// 同 <see cref="OnRecast"/>：客户端回调只重绘面板，故仅回执。
    /// </summary>
    private Task OnRecastChoose(ClientSession session, Player player, JsonObject? param)
    {
        uint guid = (uint)ReadInt(param?["nGuid"]);
        int select = ReadInt(param?["nSelect"]);
        if (FindWeapon(player, guid) is null)
        {
            Fail(session, CmdRecastChoose, ErrGeneric);
            Log.Warn($"[session {session.Id}] WeaponLogicMsg 校准选择：guid={guid} 不是玩家的武器");
            return Task.CompletedTask;
        }

        Ok(session, CmdRecastChoose);
        Log.Info($"[session {session.Id}] WeaponLogicMsg 校准第二步 guid={guid} " +
                 $"nSelect={select}（{(select == RecastTakeNew ? "换新" : "保留旧")}，仅回执）");
        return Task.CompletedTask;
    }

    private Task Unknown(ClientSession session, int cmd, JsonNode? param)
    {
        Log.Warn($"[session {session.Id}] unhandled WeaponLogicMsg nCmd={cmd} param={param?.ToJsonString()}");
        // ★ 这一族挂遮罩 ⇒ 即便不认识也要回，否则界面卡死
        Fail(session, cmd, ErrGeneric);
        return Task.CompletedTask;
    }

    // ================= 工具 =================

    /// <summary>取玩家背包里的一把武器（genre 2）；不是武器或不存在返回 null。</summary>
    private static Item? FindWeapon(Player player, uint guid)
    {
        if (guid == 0)
            return null;
        var item = player.Items.FirstOrDefault(i => i.Guid == guid);
        return item is not null && item.Genre == WeaponTable.WeaponGenre ? item : null;
    }

    private static string Describe(Item w)
        => $"guid={w.Guid}({w.Genre}-{w.Detail}-{w.Particular}-{w.Level})";

    /// <summary>
    /// 成功回包：<c>{nError = 0, nCmd = &lt;cmd&gt;}</c>。
    /// ★ 本族是 `S2CCall` 字符串族 ⇒ <c>nError</c> **必须带 0**（数值比较，缺了 `nil &gt; 0` 抛）。
    /// </summary>
    private void Ok(ClientSession session, int cmd)
        => _ctx.LuaCalls.CallClientFunction(session, Function, new JsonObject
        {
            ["nError"] = ErrOk,
            ["nCmd"] = cmd,
        });

    /// <summary>带 <c>tbParam</c> 的成功回包（只有分解用）。</summary>
    private void Reply(ClientSession session, int cmd, JsonNode tbParam)
        => _ctx.LuaCalls.CallClientFunction(session, Function, new JsonObject
        {
            ["nError"] = ErrOk,
            ["nCmd"] = cmd,
            ["tbParam"] = tbParam,
        });

    /// <summary>
    /// 失败回包：<c>nError &gt; 0</c>。客户端会弹 `ui.Error.&lt;code&gt;` 并顺带调
    /// <c>UI_WeaponStrong:CleanS2Sstate</c> + <c>UI_Warehouse:OnServerSell(nError)</c> 复位状态。
    /// </summary>
    private void Fail(ClientSession session, int cmd, int code)
        => _ctx.LuaCalls.CallClientFunction(session, Function, new JsonObject
        {
            ["nError"] = code <= 0 ? ErrGeneric : code,
            ["nCmd"] = cmd,
        });

    private static int ReadInt(JsonNode? node, int fallback = 0)
        => node is null ? fallback : int.TryParse(node.ToString(), out var v) ? v : fallback;

    /// <summary>
    /// 读「裸 guid 数组」上行（分解用：<c>tbGuid = [guid, …]</c>）。
    /// 也容忍 <c>[[guid,count],…]</c> 形态（取第 1 个元素），防客户端版本差异。
    /// </summary>
    internal static List<uint> ReadGuidList(JsonNode? node)
    {
        var result = new List<uint>();
        if (node is not JsonArray arr)
            return result;
        foreach (var el in arr)
        {
            switch (el)
            {
                case JsonArray inner when inner.Count > 0:
                    if (uint.TryParse(inner[0]?.ToString(), out uint g2) && g2 > 0)
                        result.Add(g2);
                    break;
                case not null:
                    if (uint.TryParse(el.ToString(), out uint g) && g > 0)
                        result.Add(g);
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// 读「(guid, count) 二元组数组」上行（强化/突破用：<c>tbGuid = [[guid,count],…]</c>）。
    /// 也容忍裸 guid 数组（count 记 1）。
    /// </summary>
    internal static List<(uint Guid, int Count)> ReadGuidCountList(JsonNode? node)
    {
        var result = new List<(uint, int)>();
        if (node is not JsonArray arr)
            return result;
        foreach (var el in arr)
        {
            if (el is JsonArray inner && inner.Count > 0)
            {
                if (uint.TryParse(inner[0]?.ToString(), out uint g) && g > 0)
                {
                    int c = inner.Count > 1 && int.TryParse(inner[1]?.ToString(), out int cc) ? cc : 1;
                    result.Add((g, Math.Max(1, c)));
                }
            }
            else if (el is not null && uint.TryParse(el.ToString(), out uint g2) && g2 > 0)
            {
                result.Add((g2, 1));
            }
        }
        return result;
    }
}
