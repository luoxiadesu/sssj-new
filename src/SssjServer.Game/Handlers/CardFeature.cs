using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 角色卡养成域：<b>分解 / 升级 / 突破</b>（<c>LuaCall</c> 1 / 5 / 6）。
///
/// 取证：<c>work/card-domain-forensics.md</c>（989 行）+ <c>work/gap-analysis.md</c> §2。
/// 数值公式全部落在 <see cref="CardTable"/>（含 39 条对着真实配置断言的单测）。
///
/// <para><b>★ 0. 为什么做这个域：269 张卡发下去了，但一张也练不了</b></para>
/// GM 全解锁已把 <c>CharacterCard.txt</c> 的 269 张卡全发进背包，但服务端此前
/// 一条养成命令都没实现 ⇒ 角色卡界面的「强化 / 觉醒 / 突破 / 分解」全都点了没反应。
/// 这是玩家最核心的养成循环。
///
/// <para><b>★ 1. <c>CardLogic</c> 不在包内 —— 契约靠三处还原</b></para>
/// <c>LuaCall.lua:589..660</c> 的 <c>MODULE_SERVER</c> 分支调
/// <c>CardLogic:SellCard</c>/<c>EnhanceWithCommon</c>/<c>BreakCard</c> 等，
/// 但 <c>extracted/lua/CardLogic.lua</c> **不存在**（纯服务端模块，与 <c>ClubLogic</c> 同类）。
/// ⇒ 契约来自：① 上行构造点；② 客户端 <c>else</c> 分支；③ 配置表公式。
/// （<c>work/unhandled-roadmap.md</c> 与旧 HANDOFF 说「<c>CardLogic</c> 在包内可直接抄」，
/// **是错的**，已在 <c>work/gap-analysis.md</c> §0.1 更正。）
///
/// <para><b>★★ 2. 本域最重要的一条：回包只是信号，数值靠 pf 1031</b></para>
/// 客户端消费点 <c>UI_CardMain.lua:883</c> 的签名是 <c>function view:ReceiveUpResult()</c>
/// —— <b>不接收任何参数</b>，函数体第一句就是：
/// <code>
/// self.data = Game.EquipManager.GetCharacterCard(self.data.Guid)   -- :884
/// </code>
/// 而 <c>GetCharacterCard</c>（RVA <c>0x1D92668</c>）是
/// <c>Dictionary&lt;uint, CharacterCard&gt;.TryGetValue</c>（<c>0x1D926F8</c>）⇒ **纯读本地缓存**。
/// ⇒ <b>硬时序</b>（与 <c>AGENTS.md</c> §5「改背包必须用 pf 1031」同源）：
/// <code>
/// ① 先推 pf 1031 ItemUpdateNtf（带新的 enhancelevel/enhanceexp/breaklevel）
/// ② 再回 LuaCall 回包（只作为「成功」信号，触发客户端去读缓存）
/// </code>
/// 顺序反了 = **数值对了但界面不动**（要重开界面才刷新）。
/// 这也解释了为什么回包**不需要**带新等级：客户端压根不读回包里的数值。
///
/// <para><b>★★★ 3. 三条命令属于<u>三种不同</u>的错误约定（逐条确认，别一刀切）</b></para>
/// <list type="table">
///   <item><term><b>5 升级</b></term><description>
///     <c>if tbParam.errorKey then ShowMsgByKey("ui."..errorKey)</c>（<c>LuaCall.lua:628</c>）
///     ⇒ 成功**绝不能带** <c>errorKey</c>；失败带**字符串** errorKey。</description></item>
///   <item><term><b>6 突破</b></term><description>
///     <c>if tbParam and tbParam.ok then</c>（<c>LuaCall.lua:642</c>）
///     ⇒ 成功**必须带** <c>ok = true</c>；不带 = 客户端什么都不做（表现为「点了没反应」）。
///     ★ 与升级**方向相反**，这是本域最容易写错的一处。</description></item>
///   <item><term><b>1 分解</b></term><description>
///     <c>OnDecomposeSuccess(tbParam)</c>（<c>UI_CardList.lua:642..647</c>）**无条件**
///     <c>local getitems = tbParam.itemlist; table.insert(getitems, {15,1,1,1,tbParam.goldnum})</c>
///     ⇒ <c>itemlist</c> 为 nil 会**当场抛**（在 nil 上 <c>table.insert</c>）
///     ⇒ 即使没有返还材料也**必须回空数组** <c>[]</c>，且 <c>goldnum</c> 必填。
///     本族**没有失败通道**（不回包 = 界面不动）。</description></item>
/// </list>
/// ⇒ 至此本项目见到的错误约定已超过 <c>AGENTS.md</c> §5 那六种，
/// **必须逐条对照上表**，不要按「域」推断。
///
/// <para><b>★ 4. 3/4 两条命令不实现（无上行构造点）</b></para>
/// <c>Card_LevelUpWithExp</c>(3) 与 <c>Card_LevelUpWithCard</c>(4) 在
/// <c>UI_CardMain.lua</c>/<c>UI_CardList.lua</c> 里**找不到任何 <c>CallServer</c> 构造点</c>
/// （<c>grep -n "CallServer(LuaCall.Card_"</c> 只有 5/6/8/1 四条）
/// ⇒ 已被 <c>LevelUpCommon</c>(5) 取代。按「没有证据的东西不要编」一律不实现；
/// 万一真机出现 unhandled，日志会报出来再补。
///
/// <para><b>★ 5. 改造(8 Refit) 本批不做</b></para>
/// <c>CardRefit.txt</c> 只覆盖 **16 张特定卡**，且每段改造都带 <c>MissionId</c>
/// **前置任务门槛** + <c>RefitSkill</c> **技能替换** + <c>L2dCardId</c> 立绘替换
/// —— 牵动两个未实现的子系统。见 <c>work/gap-analysis.md</c> §2.5。
/// </summary>
public sealed class CardFeature : IGameFeature
{
    /// <summary>LuaCall.Card_Decompose = 1（角色卡分解）。</summary>
    public const int DecomposeCmd = 1;

    /// <summary>LuaCall.Card_LevelUpCommon = 5（角色卡升级，多类型素材；强化与觉醒共用）。</summary>
    public const int LevelUpCommonCmd = 5;

    /// <summary>LuaCall.Card_Break = 6（角色卡突破）。</summary>
    public const int BreakCmd = 6;

    /// <summary>
    /// LuaCall.Card_Refit = 8（角色卡**改造**，第十三轮补）。
    /// 上行点唯一：<c>UI_CardMain.lua:2648</c>
    /// <c>{guid = self.data.Guid, refitNum = self.refitIdx}</c>。
    /// </summary>
    public const int RefitCmd = 8;

    /// <summary>角色卡的 genre（<c>emGenreType.Card</c>，<c>dump.cs:372496</c>）。</summary>
    public const uint CardGenre = 1;

    /// <summary>金币货币 id（分解返还与升级/突破消耗都用它）。</summary>
    public const int GoldMoneyId = 2;

    /// <summary>
    /// 上行 <c>items</c> 元素的 <c>itype</c>：<b>2 = 用角色卡当素材</b>，其余 = 道具
    /// （<c>UI_CardMain.lua:1364/1367</c> 的 <c>if itype == 2</c> 分支）。
    /// </summary>
    public const int ItemTypeCard = 2;

    /// <summary>
    /// 升级一次最多能塞多少条素材（客户端 <c>UI_CardMain.lua:21</c> <c>self.MaxCount = 15</c>）。
    /// 界面自己拦住了，但 GM/改包能发任意长度 ⇒ 服务端必须复刻这条上限。
    /// </summary>
    public const int MaxMaterialEntries = 15;

    /// <summary>
    /// 誓约卡的 <c>Particular</c> 值（现网 <b>39 张</b>）——★ **既不能升级也不能当素材**。
    ///
    /// 取证 <c>UI_CardMain.lua:1683..1691</c>：客户端把「素材列表 / 加素材按钮 / 信息页签」
    /// 三个节点都 <c>SetActive(self.data.P ~= 17)</c>，然后 <c>if self.data.P == 17 then return; end</c>
    /// —— 也就是**界面层根本不给入口**。
    /// ⇒ 服务端必须自己拒绝：GM/改包能绕过 UI 直接发上行命令，
    /// 放过去就是「本该练不了的卡被练了」（数据层脏，且客户端界面永远看不到结果）。
    /// 复现张数：<c>tail -n +4 CharacterCard.txt | awk -F'\t' '$3==17' | wc -l</c> → 39。
    /// </summary>
    public const uint PldgeCardParticular = 17;

    // ---- 失败用的 errorKey（仅 4/5 升级族）----
    // ★★ **不要带 "ui." 前缀**：客户端是 `ShowMsgByKey("ui." .. tbParam.errorKey)`
    //   （LuaCall.lua:616/629 → Lib.lua:635 `Text(sKey)`），自己会拼。
    //   带上就变成 "ui.ui.xxx"，查不到本地化 ⇒ 弹一句原文乱码。
    // ★ 具体 key 的**取值未取证**：包内 `CardLogic` 缺失（正是它负责产生 errorKey），
    //   全仓 `grep "errorKey\s*=\s*['\"]"` 零命中 ⇒ 没有可照抄的实例。
    //   查不到本地化时客户端显示原文、不崩，所以用两个语义清晰的保守键。
    /// <summary>素材不足 / 参数非法。</summary>
    internal const string ErrKeyNotEnough = "error.item.notenough";

    /// <summary>目标卡不存在或不可养成（含誓约卡）。</summary>
    internal const string ErrKeyNotExists = "error.item.notexists";

    private GameFeatureContext? _ctx;
    private CardTable _cards = CardTable.Empty;
    private CharacterCardTable _templates = CharacterCardTable.Empty;

    public string Name => "角色卡养成（分解 1 / 升级 5 / 突破 6）";

    public CardFeature() { }

    /// <summary>单测用：注入配置表，跳过 <see cref="Install"/>。</summary>
    internal CardFeature(CardTable cards, CharacterCardTable templates)
    {
        _cards = cards;
        _templates = templates;
    }

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _cards = CardTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _templates = ctx.CharacterCards;
        ctx.OnPlayerPrepare((session, player) =>
        {
            bool changed = false;
            foreach (var item in player.Items)
                changed |= CardRefitCollection.Mark(player, item, _cards, _templates).HasValue;
            if (changed) ctx.Store.Save(player);
        });

        ctx.MapLuaCall(DecomposeCmd, OnDecompose);
        ctx.MapLuaCall(LevelUpCommonCmd, OnLevelUpCommon);
        ctx.MapLuaCall(BreakCmd, OnBreak);
        ctx.MapLuaCall(RefitCmd, OnRefit);
    }

    // =================================================================
    //  LuaCall 1：分解
    // =================================================================

    /// <summary>
    /// 分解一批角色卡。上行是**裸 guid 数组** <c>[guid1, guid2, ...]</c>
    /// （<c>UI_CardList.lua:652..659</c> 逐个 <c>table.insert(tbGuid, item.Guid)</c>）。
    ///
    /// 回包必须带 <c>itemlist</c>(数组，可空) + <c>goldnum</c>，理由见类注释 §3。
    /// </summary>
    private Task OnDecompose(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Card_Decompose(1) 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        // ★ 上行是裸数组；JsonNode["key"] 对 JsonArray 会抛（AGENTS.md 记过这条坑）
        var guids = ReadGuidArray(param);
        if (guids.Count == 0)
        {
            Log.Warn($"[session {session.Id}] Card_Decompose(1) 上行没有可解析的 guid：{param?.ToJsonString()}");
            return Task.CompletedTask;
        }

        var changed = new List<Item>();
        var removed = new List<Item>();
        long gold = 0;
        var rewardTuples = new List<int[]>();
        var rejected = new List<string>();

        foreach (uint guid in guids)
        {
            var item = player.Items.FirstOrDefault(i => i.Guid == guid);
            if (item is null)
            {
                rejected.Add($"{guid}(不存在)");
                continue;
            }
            if (item.Genre != CardGenre)
            {
                rejected.Add($"{guid}(genre={item.Genre} 不是角色卡)");
                continue;
            }

            // ★ 与背包分解共用同一份校验（锁定 / 在编队中），避免规则漂移
            int err = BagFeature.CheckSellable(player, item);
            if (err != 0)
            {
                rejected.Add($"{guid}(校验失败 {err})");
                continue;
            }

            int rarity = RarityOf(item);
            gold += _cards.DecomposeGold((int)item.EnhanceLevel, rarity);

            // 返还材料：逐项按万分比概率
            foreach (var d in _cards.DecomposeItems(rarity))
            {
                if (d.Probability <= 0)
                    continue;
                if (_ctx is null || _ctx.Rewards.Roll(d.Probability))
                    rewardTuples.Add(new[] { d.G, d.D, d.P, d.L, d.N });
            }

            player.Items.Remove(item);
            removed.Add(item);
        }

        if (removed.Count == 0)
        {
            Log.Warn($"[session {session.Id}] Card_Decompose(1) 没有一张卡可分解" +
                     $"（{string.Join(',', rejected)}）—— 不回包（本族无失败通道）");
            return Task.CompletedTask;
        }

        // 发返还材料（走统一发奖层，自动叠加/建实例）
        // ★ 用 RewardSource.Extra：概率已由上面的 Rewards.Roll 判过，
        //   这里需要「第 6 位忽略、100% 发放」的语义，不能让发奖层二次抽签。
        var granted = _ctx?.Rewards.Grant(player, rewardTuples, RewardSource.Extra);
        if (gold > 0)
            player.AddMoney(GoldMoneyId, (int)Math.Min(gold, int.MaxValue));

        _ctx?.Store.Save(player);

        Log.Info($"[session {session.Id}] 角色卡分解：{removed.Count} 张" +
                 $"（guid {string.Join(',', removed.Select(i => i.Guid))}）→ 金币 +{gold}、" +
                 $"返还 {rewardTuples.Count} 项" +
                 $"{(rejected.Count > 0 ? $"；拒绝 {rejected.Count} 张（{string.Join(',', rejected)}）" : "")}");

        // ★ 先推 1031（移除）+ 货币，再回包 —— 理由见类注释 §2
        GameNotify.PushItemsRemoved(session, removed);
        if (granted is not null)
            GameNotify.PushRewards(session, player, granted);
        if (gold > 0)
            GameNotify.PushMoney(session, player, GoldMoneyId);

        // ★★ itemlist 必须存在（客户端在它上面 table.insert，nil 会抛）
        var itemlist = new JsonArray();
        foreach (var t in rewardTuples)
            itemlist.Add(new JsonArray { t[0], t[1], t[2], t[3], t[4] });

        _ctx?.LuaCalls.CallClient(session, DecomposeCmd, new JsonObject
        {
            ["itemlist"] = itemlist,
            ["goldnum"] = gold,
        });
        return Task.CompletedTask;
    }

    // =================================================================
    //  LuaCall 5：升级（强化 / 觉醒共用）
    // =================================================================

    /// <summary>
    /// 升级。上行 <c>{guid, items = [[itype, id, num], ...], clientlv, clientSkillLv}</c>。
    ///
    /// <para><b>★★ <c>items</c> 里道具的 <c>id</c> 不是 GDPL，是素材索引</b></para>
    /// <c>itype != 2</c> 时 <c>id</c> 是 <c>CardCommon.tbExpCard</c> 的 **1-based 下标**
    /// （<c>UI_CardMain.lua:1367</c> 传的是 <c>GetIdxByGDPL(...)</c> 的返回值），
    /// 必须用 <see cref="CardTable.DecodeExpItem"/> 反查回 GDPL 才能扣对道具。
    /// 这是本域最容易写错的一处。
    ///
    /// <para>★ 同名卡素材只增加技能提升次数，不产生角色等级经验。</para>
    /// </summary>
    private Task OnLevelUpCommon(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is Player player)
        {
            lock (player) return LevelUpCommonLocked(session, cmd, param);
        }
        return Task.CompletedTask;
    }

    private Task LevelUpCommonLocked(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Card_LevelUpCommon(5) 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        var obj = param as JsonObject;
        uint guid = obj?["guid"] is JsonValue value && value.TryGetValue<uint>(out var parsedGuid) ? parsedGuid : 0;
        var target = guid == 0 ? null : player.Items.FirstOrDefault(i => i.Guid == guid);
        if (target is null || target.Count == 0 || target.Genre != CardGenre)
        {
            Log.Warn($"[session {session.Id}] Card_LevelUpCommon(5) 目标卡 guid={guid} 不存在或不是角色卡");
            return Fail(session, LevelUpCommonCmd, ErrKeyNotExists);
        }

        // ★ 誓约卡（P == 17，现网 39 张）客户端界面根本没有升级入口
        //   （UI_CardMain.lua:1683..1691 隐藏三个节点并 return）
        //   ⇒ 服务端必须拒绝，否则 GM/改包能绕过 UI 把它练起来。
        if (target.Particular == PldgeCardParticular)
        {
            Log.Warn($"[session {session.Id}] Card_LevelUpCommon(5) 目标是誓约卡（P={PldgeCardParticular}）" +
                     $" guid={guid}，客户端界面本就没有升级入口，拒绝");
            return Fail(session, LevelUpCommonCmd, ErrKeyNotExists);
        }

        int rarity = RarityOf(target);
        int attribute = AttributeOf(target);
        int breakType = BreakTypeOf(target);
        int maxLevel = _cards.MaxLevelFor(breakType, (int)target.BreakLevel);

        long gain = 0;
        int sameNameCards = 0, skillCookies = 0;
        long skillPoints = 0;
        var changed = new List<Item>();
        var removed = new List<Item>();

        // ★ 素材条目数上限（客户端 UI_CardMain.lua:21 `self.MaxCount = 15`）。
        //   客户端界面自己拦住了，但 GM/改包能发任意长度 ⇒ 服务端复刻这条上限，
        //   否则一次上行就能塞几千条素材（既是刷数值也是 DoS 面）。
        var materials = ReadMaterials(obj?["items"]);
        if (materials.Count > MaxMaterialEntries)
        {
            Log.Warn($"[session {session.Id}] Card_LevelUpCommon(5) 素材条目 {materials.Count} 条" +
                     $"超过客户端上限 {MaxMaterialEntries}（UI_CardMain.lua:21），拒绝");
            return Fail(session, LevelUpCommonCmd, ErrKeyNotEnough);
        }

        long goldCost = UpgradeGoldCost(player, target, materials);
        if (goldCost < 0 || goldCost > int.MaxValue || player.GetMoney(GoldMoneyId) < goldCost)
            return Fail(session, LevelUpCommonCmd, ErrKeyNotEnough);

        bool hasSkillMaterial = materials.Any(m => m.Num > 0 &&
            (m.Itype != ItemTypeCard ? CardTable.IsSkillCookie(m.Id) : player.Items.Any(i =>
                i.Guid == (uint)m.Id && i.Guid != target.Guid && i.Genre == target.Genre &&
                i.Detail == target.Detail && i.Particular == target.Particular && i.Level == target.Level)));
        var targetTemplate = _templates.Get(target.Genre, target.Detail, target.Particular, target.Level);
        if (hasSkillMaterial && CardSkills.Remaining(target, targetTemplate) == 0)
            return Fail(session, LevelUpCommonCmd, ErrKeyNotEnough);

        foreach (var (itype, id, num) in materials)
        {
            if (num <= 0)
                continue;

            // ★ 道具索引 0 是 `GetIdxByGDPL` 匹配失败的**哨兵值**（CardCommon.lua:695 `return 0`）
            //   ⇒ 收到 0 说明客户端压根没找到这个道具，必须拒绝而不是当合法索引去解码。
            if (itype != ItemTypeCard && id == 0)
            {
                Log.Warn($"[session {session.Id}] 升级素材索引为 0（GetIdxByGDPL 的失败哨兵），跳过");
                continue;
            }

            if (itype == ItemTypeCard)
            {
                // 素材是角色卡：id 是它的 guid
                var mat = player.Items.FirstOrDefault(i => i.Guid == (uint)id);
                if (mat is null || mat.Genre != CardGenre || mat.Guid == target.Guid)
                {
                    Log.Warn($"[session {session.Id}] 升级素材卡 guid={id} 非法，跳过");
                    continue;
                }
                // ★ 誓约卡也不能当素材（同一处取证：客户端连素材列表都不显示它）
                if (mat.Particular == PldgeCardParticular)
                {
                    Log.Warn($"[session {session.Id}] 升级素材卡 guid={id} 是誓约卡" +
                             $"（P={PldgeCardParticular}），不可作为素材，跳过");
                    continue;
                }
                if (BagFeature.CheckSellable(player, mat) != 0)
                {
                    Log.Warn($"[session {session.Id}] 升级素材卡 guid={id} 已锁定/在编队中，跳过");
                    continue;
                }

                // ★ 同名卡（GDPL 四元全等）只加技能等级，不产生经验
                //   取证 UI_CardMain.lua:1999..2021；额外继承素材已有的技能提升。
                if (mat.Genre == target.Genre && mat.Detail == target.Detail &&
                    mat.Particular == target.Particular && mat.Level == target.Level)
                {
                    sameNameCards++;
                    skillPoints += CardSkills.FeedPoints(mat, targetTemplate);
                }
                else
                {
                    gain += _cards.FeedCardExp((int)mat.EnhanceLevel, RarityOf(mat));
                }

                player.Items.Remove(mat);
                removed.Add(mat);
                continue;
            }

            // 素材是道具：id 是 tbExpCard 的 1-based 下标
            var gdpl = CardTable.DecodeExpItem(id);
            if (gdpl is null)
            {
                Log.Warn($"[session {session.Id}] 升级素材索引 {id} 超出 tbExpCard 范围（1..{CardTable.ExpCards.Count + CardTable.SkillCookies.Count}），跳过");
                continue;
            }
            var (g, d, p, l) = gdpl.Value;

            int took = ConsumeByTemplate(player, (uint)g, (uint)d, (uint)p, (uint)l, num, changed, removed);
            if (took <= 0)
            {
                Log.Warn($"[session {session.Id}] 升级素材 ({g},{d},{p},{l}) 库存不足（要 {num}），跳过");
                continue;
            }

            if (CardTable.IsSkillCookie(id))
            {
                // 技能饼干加的是技能经验（GetEnhanceCardExp 的 else 分支 return count）
                skillCookies += took;
                skillPoints += took;
                continue;
            }

            long unit = ExpItemUnitExp(g, d, p, l);
            double rate = CardTable.ExpRate(attribute, p);
            gain += (long)Math.Floor(rate * unit * took);
        }

        if (gain <= 0 && sameNameCards == 0 && skillCookies == 0)
        {
            Log.Warn($"[session {session.Id}] Card_LevelUpCommon(5) 没有任何有效素材，回 errorKey");
            return Fail(session, LevelUpCommonCmd, ErrKeyNotEnough);
        }

        int oldLv = (int)target.EnhanceLevel;
        var (newLv, newExp) = _cards.ApplyExp(oldLv, target.EnhanceExp, gain, maxLevel);
        target.EnhanceLevel = (uint)newLv;
        target.EnhanceExp = (uint)Math.Min(newExp, uint.MaxValue);
        CardSkills.Apply(target, targetTemplate, skillPoints);
        player.AddMoney(GoldMoneyId, -(int)goldCost);
        if (!changed.Contains(target))
            changed.Add(target);

        _ctx?.Store.Save(player);

        Log.Info($"[session {session.Id}] 角色卡升级 guid={guid}：" +
                 $"LV.{oldLv} → LV.{newLv}（+{gain} 经验，残余 {target.EnhanceExp}，上限 {maxLevel}" +
                 $"／突破 {target.BreakLevel} 次、type={breakType}）；" +
                 $"消耗素材：卡 {removed.Count(i => i.Genre == CardGenre)} 张" +
                 $"（其中同名卡 {sameNameCards} 张只加技能，技能提升点数 {skillPoints}）、" +
                 $"道具 {changed.Count(i => i.Genre != CardGenre)} 种、技能饼干 {skillCookies} 个");

        // ★ 先推 1031，再回包（类注释 §2）
        if (removed.Count > 0)
            GameNotify.PushItemsRemoved(session, removed);
        GameNotify.PushItems(session, changed);

        if (goldCost > 0) GameNotify.PushMoney(session, player, GoldMoneyId);

        // ★ 成功回包**绝不能带 errorKey**（Lua 真值判断）
        _ctx?.LuaCalls.CallClient(session, LevelUpCommonCmd, new JsonObject());
        return Task.CompletedTask;
    }

    // =================================================================
    //  LuaCall 6：突破
    // =================================================================

    /// <summary>
    /// 突破。上行 <c>{guid}</c>（<c>UI_CardMain.lua:2395</c>）。
    /// ★★ 成功回包**必须带 <c>ok = true</c>**（<c>LuaCall.lua:642</c> 的
    /// <c>if tbParam and tbParam.ok then</c>）—— 与升级族方向相反。
    /// </summary>
    private Task OnBreak(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is Player player)
        {
            lock (player) return BreakLocked(session, cmd, param);
        }
        return Task.CompletedTask;
    }

    private Task BreakLocked(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Card_Break(6) 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        uint guid = (param as JsonObject)?["guid"] is JsonValue value && value.TryGetValue<uint>(out var parsedGuid) ? parsedGuid : 0;
        var target = guid == 0 ? null : player.Items.FirstOrDefault(i => i.Guid == guid);
        if (target is null || target.Count == 0 || target.Genre != CardGenre)
        {
            Log.Warn($"[session {session.Id}] Card_Break(6) 目标卡 guid={guid} 不存在或不是角色卡，不回包");
            return Task.CompletedTask;
        }

        // P=17 growth is awarded by completed pledge stages, not ordinary materials.
        if (target.Particular == PldgeCardParticular) return Task.CompletedTask;

        int rarity = RarityOf(target);
        int breakType = BreakTypeOf(target);
        var row = _cards.BreakOf(breakType);
        int maxBreak = _cards.MaxBreakCount(breakType, rarity);
        int next = (int)target.BreakLevel + 1;

        if (row is null || next > maxBreak || next > row.Steps.Count)
        {
            Log.Warn($"[session {session.Id}] Card_Break(6) guid={guid} 不能再突破" +
                     $"（当前 {target.BreakLevel} 次，上限 {maxBreak}，type={breakType} " +
                     $"{(row is null ? "无突破配置" : $"配了 {row.Steps.Count} 档")}），不回包");
            return Task.CompletedTask;
        }

        var step = row.Steps[next - 1];

        // ---- 校验消耗 ----
        if (step.CurrencyCount > 0 && player.GetMoney(step.CurrencyId) < step.CurrencyCount)
        {
            Log.Warn($"[session {session.Id}] Card_Break(6) 货币不足：" +
                     $"需要 {step.CurrencyId}×{step.CurrencyCount}，持有 {player.GetMoney(step.CurrencyId)}，不回包");
            return Task.CompletedTask;
        }
        foreach (var need in step.Items)
        {
            long have = CountByTemplate(player, (uint)need.G, (uint)need.D, (uint)need.P, (uint)need.L);
            if (have < need.N)
            {
                Log.Warn($"[session {session.Id}] Card_Break(6) 材料不足：" +
                         $"({need.G},{need.D},{need.P},{need.L}) 需要 {need.N}、持有 {have}，不回包");
                return Task.CompletedTask;
            }
        }

        // ---- 扣消耗 + 提升突破等级 ----
        var changed = new List<Item>();
        var removed = new List<Item>();
        foreach (var need in step.Items)
            ConsumeByTemplate(player, (uint)need.G, (uint)need.D, (uint)need.P, (uint)need.L,
                              need.N, changed, removed);

        if (step.CurrencyCount > 0)
            player.AddMoney(step.CurrencyId, -step.CurrencyCount);

        target.BreakLevel = (uint)next;
        if (!changed.Contains(target))
            changed.Add(target);

        _ctx?.Store.Save(player);

        int newMax = _cards.MaxLevelFor(breakType, next);
        Log.Info($"[session {session.Id}] 角色卡突破 guid={guid}：" +
                 $"第 {next} 次（上限 {maxBreak}，type={breakType}）→ 等级上限 {newMax}；" +
                 $"消耗 货币{step.CurrencyId}×{step.CurrencyCount} + 材料 " +
                 $"{string.Join(',', step.Items.Select(i => $"({i.G},{i.D},{i.P},{i.L})×{i.N}"))}");

        // ★ 先推 1031 + 货币，再回包
        if (removed.Count > 0)
            GameNotify.PushItemsRemoved(session, removed);
        GameNotify.PushItems(session, changed);
        if (step.CurrencyCount > 0)
            GameNotify.PushMoney(session, player, step.CurrencyId);

        // ★★ 突破族：成功必须带 ok（客户端 `if tbParam and tbParam.ok then`，LuaCall.lua:642）
        // ★ 用 JSON bool 而不是 1：Lua 侧是**纯真值判断**，两者等效；
        //   且本项目已有 6 处 bool 回包先例（好友域的 canrecv/cansend 真机验证过）。
        _ctx?.LuaCalls.CallClient(session, BreakCmd, new JsonObject { ["ok"] = true });
        return Task.CompletedTask;
    }

    // =================================================================
    //  LuaCall 8：改造（第十三轮）
    // =================================================================

    /// <summary>
    /// 角色卡改造。上行 <c>{guid, refitNum}</c>（<c>UI_CardMain.lua:2648</c>）。
    ///
    /// <para><b>1. ★ 错误约定：与突破(6) 同族，成功必须带 <c>ok = true</c></b></para>
    /// <code>
    /// LuaCall.lua（Card_Refit 客户端分支）：
    ///     if tbParam and tbParam.ok then UI.Call("UI_CardMain", "ReceiveUpResult") end
    /// </code>
    /// ⇒ 不带 <c>ok</c> = 点了没反应（与升级(5) 的 <c>errorKey</c> 真值判断**方向相反** ——
    /// 同一个域里三种约定，见 §22.3）。
    /// ★ 本族**没有失败通道**：失败时不回包（与突破一致），拒绝原因写日志。
    ///
    /// <para><b>2. ★★ <c>ReceiveUpResult</c> 不收参数 ⇒ 必须先推 pf 1031</b></para>
    /// <c>UI_CardMain.lua:883</c> 的 <c>ReceiveUpResult()</c> 无参，第一句就从
    /// <c>Game.EquipManager.GetCharacterCard(guid)</c> **重读本地缓存**
    /// ⇒ 「先推 1031、再回包」，反了就是「数值对了但界面不动」（§22.2）。
    ///
    /// <para><b>3. 四个前置条件（逐条对齐客户端 <c>UI_CardMain.lua:2625</c>）</b></para>
    /// <code>
    /// if idx == self.data.RefitNum + 1 and enoughMoney and enoughItem and missionComp and syncEnough then
    /// </code>
    /// <list type="number">
    ///   <item><b>顺序</b>：<c>refitNum</c> 必须正好等于「当前改造次数 + 1」（不能跳档）；</item>
    ///   <item><b>货币</b>：<c>:2480</c> <c>me:GetMoney(CurrencyId) &gt;= CurrencyCount</c>；</item>
    ///   <item><b>材料</b>：<c>:2459..2473</c> 逐项 <c>num &lt; n</c> 即不足；</item>
    ///   <item><b>同步率</b>：<c>:2451</c> <c>SyncRate &gt;= RefitSync&lt;n&gt;</c>（单位千分之一）。</item>
    /// </list>
    /// ★ <b>任务前置（<c>MissionId</c>）</b>：客户端判据是
    /// <c>MissionManager:GetOtherAchieve(id)</c> + <c>CheckInfoState(conf, 3)</c>
    /// （<c>:2490..2494</c>）。按原表assistparam选择阈值，group18任务值右移1位为进度；
    /// 达标或已领取均可改造，不在改造时领取奖励或消耗任务进度。
    /// </summary>
    private Task OnRefit(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is Player player)
        {
            lock (player) return RefitLocked(session, cmd, param);
        }
        return Task.CompletedTask;
    }

    private Task RefitLocked(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Card_Refit(8) 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        var body = param as JsonObject;
        if (body?["guid"] is not JsonValue guidValue || !guidValue.TryGetValue<uint>(out uint guid) || guid == 0 ||
            !GachaRequestLimits.TryInteger(body?["refitNum"], out int want) || want <= 0)
        {
            Log.Warn($"[session {session.Id}] Card_Refit(8) 参数不是有效整数，不回包");
            return Task.CompletedTask;
        }

        var target = guid == 0 ? null : player.Items.FirstOrDefault(i => i.Guid == guid);
        if (target is null || target.Count == 0 || target.Genre != CardGenre)
        {
            Log.Warn($"[session {session.Id}] Card_Refit(8) 目标卡 guid={guid} 不存在或不是角色卡，不回包");
            return Task.CompletedTask;
        }

        var row = _cards.RefitOf((int)target.Genre, (int)target.Detail,
                                 (int)target.Particular, (int)target.Level);
        if (row is null)
        {
            Log.Warn($"[session {session.Id}] Card_Refit(8) guid={guid} "
                   + $"({target.Genre},{target.Detail},{target.Particular},{target.Level}) "
                   + $"不在 CardRefit.txt 里（现网 {_cards.RefitCardCount} 张可改造），不回包");
            return Task.CompletedTask;
        }

        // ① 顺序：必须正好是下一档
        int next = (int)target.RefitNum + 1;
        if (want != next)
        {
            Log.Warn($"[session {session.Id}] Card_Refit(8) guid={guid} 档位不对："
                   + $"请求第 {want} 档、当前已改造 {target.RefitNum} 次（只能改第 {next} 档），不回包");
            return Task.CompletedTask;
        }
        if (next > row.MaxRefit)
        {
            Log.Warn($"[session {session.Id}] Card_Refit(8) guid={guid} 已达改造上限 "
                   + $"{row.MaxRefit} 次，不回包");
            return Task.CompletedTask;
        }

        var step = row.Steps[next - 1];
        if (step.MissionId > 0)
        {
            var mission = _ctx?.Missions.GetAchieveOther(step.MissionId);
            if (mission is null || Math.Max(MissionFeature.ProgressOf(player.GetTask(mission.Group, mission.Id)),
                MissionFeature.DerivedProgress(player, mission)) <
                mission.RequiredCount(achieveKey: true))
            {
                Log.Warn($"[session {session.Id}] Card_Refit(8) guid={guid} 前置成就 {step.MissionId} 未达标或缺少配置，不回包");
                return Task.CompletedTask;
            }
        }
        var cost = row.Cost?.Steps.Count >= next ? row.Cost.Steps[next - 1] : null;
        if (cost is null)
        {
            Log.Warn($"[session {session.Id}] Card_Refit(8) guid={guid} 第 {next} 档没有消耗配置"
                   + $"（CostType={row.CostType}），不回包");
            return Task.CompletedTask;
        }

        // ② 同步率（★ 千分之一单位）
        if (target.SyncRate < step.NeedSync)
        {
            Log.Warn($"[session {session.Id}] Card_Refit(8) guid={guid} 同步率不足："
                   + $"需要 {step.NeedSync}（{step.NeedSync / 1000.0:F1}%）、当前 {target.SyncRate}，不回包");
            return Task.CompletedTask;
        }

        // ③ 货币
        if (cost.CurrencyCount > 0 && player.GetMoney(cost.CurrencyId) < cost.CurrencyCount)
        {
            Log.Warn($"[session {session.Id}] Card_Refit(8) 货币不足：需要 "
                   + $"{cost.CurrencyId}×{cost.CurrencyCount}、持有 {player.GetMoney(cost.CurrencyId)}，不回包");
            return Task.CompletedTask;
        }

        // ④ 材料
        foreach (var need in cost.Items)
        {
            long have = CountByTemplate(player, (uint)need.G, (uint)need.D, (uint)need.P, (uint)need.L);
            if (have < need.N)
            {
                Log.Warn($"[session {session.Id}] Card_Refit(8) 材料不足："
                       + $"({need.G},{need.D},{need.P},{need.L}) 需要 {need.N}、持有 {have}，不回包");
                return Task.CompletedTask;
            }
        }

        // ---- 扣消耗 + 提升改造次数 ----
        var changed = new List<Item>();
        var removed = new List<Item>();
        foreach (var need in cost.Items)
            ConsumeByTemplate(player, (uint)need.G, (uint)need.D, (uint)need.P, (uint)need.L,
                              need.N, changed, removed);

        if (cost.CurrencyCount > 0)
            player.AddMoney(cost.CurrencyId, -cost.CurrencyCount);

        target.RefitNum = (uint)next;
        var refitCollection = CardRefitCollection.Mark(player, target, _cards, _templates);
        if (!changed.Contains(target))
            changed.Add(target);

        _ctx?.Store.Save(player);

        Log.Info($"[session {session.Id}] 角色卡改造 guid={guid}：第 {next} 档"
               + $"（上限 {row.MaxRefit}）；同步率 {target.SyncRate}/{step.NeedSync}；"
               + $"消耗 货币{cost.CurrencyId}×{cost.CurrencyCount} + 材料 "
               + string.Join(',', cost.Items.Select(i => $"({i.G},{i.D},{i.P},{i.L})×{i.N}")));

        // ★★ 先推 1031 + 货币，再回包（§2：ReceiveUpResult 重读本地缓存）
        if (removed.Count > 0)
            GameNotify.PushItemsRemoved(session, removed);
        GameNotify.PushItems(session, changed);
        if (cost.CurrencyCount > 0)
            GameNotify.PushMoney(session, player, cost.CurrencyId);

        if (refitCollection is { } task) GameNotify.PushTaskValues(session, new[] { task });

        // ★ 与突破同族：成功必须带 ok
        _ctx?.LuaCalls.CallClient(session, RefitCmd, new JsonObject { ["ok"] = true });
        return Task.CompletedTask;
    }

    // =================================================================
    //  小工具
    // =================================================================

    /// <summary>回一个带 <c>errorKey</c> 的失败包（**仅升级族**用；突破/分解族不能这么回）。</summary>
    private Task Fail(ClientSession session, int cmd, string errorKey)
    {
        _ctx?.LuaCalls.CallClient(session, cmd, new JsonObject { ["errorKey"] = errorKey });
        return Task.CompletedTask;
    }

    /// <summary>卡的稀有度：优先查模板表的 <c>Rarity</c>，查不到退化用 <c>Level</c>（实测两者一致）。</summary>
    private int RarityOf(Item item)
    {
        var tpl = _templates.Get(item.Genre, item.Detail, item.Particular, item.Level);
        return tpl is not null && tpl.Rarity > 0 ? tpl.Rarity : (int)item.Level;
    }

    /// <summary>卡的属性（决定经验素材倍率）；查不到按 0。</summary>
    private int AttributeOf(Item item)
        => _templates.Get(item.Genre, item.Detail, item.Particular, item.Level)?.AttributeType ?? 0;

    /// <summary>
    /// 卡的突破类型 <c>= WeaponType * 10 + Rarity</c>（269/269 程序化验证，
    /// 见 <see cref="CardTable.BreakTypeOf"/>）。模板查不到时返回 0（⇒ 不可突破）。
    /// </summary>
    private int BreakTypeOf(Item item)
    {
        var tpl = _templates.Get(item.Genre, item.Detail, item.Particular, item.Level);
        return tpl is null ? 0 : CardTable.BreakTypeOf(tpl.WeaponType, tpl.Rarity);
    }

    /// <summary>经验道具单个提供的经验 = <c>ItemList.ExtParam1</c>（<c>GetEnhanceCardExp</c>）。</summary>
    private long ExpItemUnitExp(int g, int d, int p, int l)
    {
        var tpl = _ctx?.ItemTemplates.Get((uint)g, (uint)d, (uint)p, (uint)l);
        if (tpl is null)
            return 0;
        return long.TryParse(tpl.ExtParam1, out long v) ? v : 0;
    }

    // UI_CardMain.GetSelectCost: cards use CoinCostRate(1) * rarity base;
    // material items use ExtParam2. Simulate consumption so repeated entries
    // and locked stacks are charged only for the quantities actually consumed.
    private long UpgradeGoldCost(Player player, Item target, List<(int Itype, int Id, int Num)> materials)
    {
        long cost = 0;
        var cards = new HashSet<uint>();
        var remaining = player.Items.ToDictionary(i => i.Guid, i => (long)i.Count);
        foreach (var (type, id, num) in materials)
        {
            if (num <= 0) continue;
            if (type == ItemTypeCard)
            {
                var item = player.Items.FirstOrDefault(i => i.Guid == (uint)id);
                if (item is null || item.Genre != CardGenre || item.Guid == target.Guid ||
                    item.Particular == PldgeCardParticular || BagFeature.CheckSellable(player, item) != 0 ||
                    !cards.Add(item.Guid)) continue;
                cost += _cards.RarityFeedBaseExp(RarityOf(item));
                continue;
            }
            var gdpl = CardTable.DecodeExpItem(id);
            if (id == 0 || gdpl is null) continue;
            var (g, d, p, l) = gdpl.Value;
            var template = _ctx?.ItemTemplates.Get((uint)g, (uint)d, (uint)p, (uint)l);
            long unit = 0;
            if (template is null || (template.ExtParam2.Length > 0 &&
                (!long.TryParse(template.ExtParam2, out unit) || unit < 0))) return -1;
            long want = num;
            foreach (var item in player.Items.Where(i => i.Genre == g && i.Detail == d &&
                i.Particular == p && i.Level == l && i.LockOn == 0).OrderBy(i => i.CreateTime).ThenBy(i => i.Guid))
            {
                long use = Math.Min(want, remaining[item.Guid]);
                remaining[item.Guid] -= use;
                want -= use;
                if (unit > int.MaxValue || cost > int.MaxValue - use * unit) return -1;
                cost += use * unit;
                if (want == 0) break;
            }
        }
        return cost;
    }

    /// <summary>按模板统计持有数量（跨实例累加 count）。</summary>
    private static long CountByTemplate(Player player, uint g, uint d, uint p, uint l)
        => player.Items
            .Where(i => i.Genre == g && i.Detail == d && i.Particular == p && i.Level == l)
            .Where(i => i.LockOn == 0)
            .Sum(i => (long)i.Count);

    /// <summary>
    /// 按模板扣 <paramref name="want"/> 个，返回实际扣到的数量。
    /// 与 <see cref="BagFeature"/> 的做法一致：按 <c>CreateTime</c>/<c>Guid</c> 稳定排序，
    /// 扣完的实例进 <paramref name="removed"/>、部分扣的进 <paramref name="changed"/>。
    /// </summary>
    private static int ConsumeByTemplate(Player player, uint g, uint d, uint p, uint l, int want,
                                         List<Item> changed, List<Item> removed)
    {
        int taken = 0;
        var candidates = player.Items
            .Where(i => i.Genre == g && i.Detail == d && i.Particular == p && i.Level == l)
            .Where(i => i.LockOn == 0)
            .OrderBy(i => i.CreateTime)
            .ThenBy(i => i.Guid)
            .ToList();

        foreach (var item in candidates)
        {
            if (taken >= want)
                break;
            uint use = (uint)Math.Min((long)want - taken, item.Count);
            if (use == 0)
                continue;
            if (use >= item.Count)
            {
                player.Items.Remove(item);
                removed.Add(item);
            }
            else
            {
                item.Count -= use;
                if (!changed.Contains(item))
                    changed.Add(item);
            }
            taken += (int)use;
        }
        return taken;
    }

    /// <summary>
    /// 读「裸 guid 数组」上行（<c>Card_Decompose</c>）。
    /// ★ 必须容忍 <see cref="JsonArray"/>：对它取 <c>["key"]</c> 会抛
    /// （<c>AGENTS.md</c> 记过这条坑，咖啡馆 115 真机上崩过）。
    /// 也兼容 <c>{guids:[...]}</c> 这种对象形态（防客户端版本差异）。
    /// </summary>
    internal static List<uint> ReadGuidArray(JsonNode? param)
    {
        var result = new List<uint>();
        JsonArray? arr = param as JsonArray;
        if (arr is null && param is JsonObject o)
            arr = (o["guids"] ?? o["guid"] ?? o["items"]) as JsonArray;
        if (arr is null)
            return result;

        foreach (var el in arr)
        {
            uint v = ReadUInt(el);
            if (v != 0)
                result.Add(v);
        }
        return result;
    }

    /// <summary>
    /// 读升级素材列表：元素是**裸数组** <c>[itype, id, num]</c>
    /// （<c>UI_CardMain.lua:1364/1367</c>）。也容忍对象形态。
    /// </summary>
    internal static List<(int Itype, int Id, int Num)> ReadMaterials(JsonNode? node)
    {
        var result = new List<(int, int, int)>();
        if (node is not JsonArray arr)
            return result;

        foreach (var el in arr)
        {
            if (el is JsonArray triple && triple.Count >= 3)
            {
                result.Add(((int)ReadUInt(triple[0]), (int)ReadUInt(triple[1]), (int)ReadUInt(triple[2])));
            }
            else if (el is JsonObject o)
            {
                result.Add(((int)ReadUInt(o["itype"] ?? o["type"]),
                            (int)ReadUInt(o["id"] ?? o["guid"] ?? o["idx"]),
                            (int)ReadUInt(o["num"] ?? o["count"])));
            }
        }
        return result;
    }

    /// <summary>
    /// 读 uint。容忍 JSON 里的各种数字 CLR 类型（存档回读会变 long/double），
    /// 与 <c>CafeFeature.ReadInt</c>/<c>HouseFeature.ReadUInt</c> 同一套写法。
    /// </summary>
    private static uint ReadUInt(JsonNode? node)
    {
        if (node is null)
            return 0;
        try
        {
            return node.GetValue<uint>();
        }
        catch (Exception)
        {
            return double.TryParse(node.ToString(), out double d) && d >= 0 && d <= uint.MaxValue
                ? (uint)d
                : 0;
        }
    }
}
