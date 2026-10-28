using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game;

/// <summary>
/// 一次全解锁的变更清单。调用方按需推送（登录时不用推 —— 数据会随全量包下发；
/// 运行期用 gmsrv 命令时则要按 <see cref="GameNotify"/> 的通道推增量）。
/// </summary>
public sealed class GmUnlockReport
{
    private readonly List<int> _money = new();
    private readonly List<Item> _items = new();
    private readonly List<Item> _removed = new();
    private readonly List<KeyValuePair<uint, int>> _levels = new();
    private readonly List<KeyValuePair<uint, int>> _tasks = new();
    private readonly List<Girl> _girls = new();

    /// <summary>变更过的货币 id（去重、保持首次出现顺序）。</summary>
    public IReadOnlyList<int> ChangedMoney => _money;

    /// <summary>新增或改过数量的背包实例（推 pf 1031 用；count 已是变更后的绝对值）。</summary>
    public IReadOnlyList<Item> ChangedItems => _items;

    /// <summary>
    /// 已从背包**摘掉**的实例（推 pf 1031 时要把 count 置 0，见 <see cref="GameNotify.PushItemsRemoved"/>）。
    /// 目前只有一个来源：<see cref="GmUnlock.ApplyItems"/> 清理「图标缺失」的历史道具。
    /// </summary>
    public IReadOnlyList<Item> RemovedItems => _removed;

    /// <summary>变更过的关卡（mapId → 完整 star 位域，推 pf 1034 用）。</summary>
    public IReadOnlyList<KeyValuePair<uint, int>> ChangedLevels => _levels;

    /// <summary>变更过的任务值（组合键 → 值，推 pf 1026 用）。</summary>
    public IReadOnlyList<KeyValuePair<uint, int>> ChangedTasks => _tasks;

    /// <summary>新增的少女（推 pf 1030 用）。</summary>
    public IReadOnlyList<Girl> NewGirls => _girls;

    /// <summary>等级/经验是否变过（变过就要推 pf 1029，见 <see cref="GameNotify.PushLevelExp"/>）。</summary>
    public bool LevelChanged { get; internal set; }

    /// <summary>新建的角色卡张数（统计用）。</summary>
    public int NewCards { get; internal set; }

    /// <summary>本次是否什么都没改。</summary>
    public bool IsEmpty => _money.Count == 0 && _items.Count == 0 && _levels.Count == 0
                        && _tasks.Count == 0 && _girls.Count == 0 && _removed.Count == 0
                        && !LevelChanged;

    internal void NoteMoney(int id)
    {
        if (!_money.Contains(id))
            _money.Add(id);
    }

    internal void NoteItem(Item item)
    {
        if (!_items.Contains(item))
            _items.Add(item);
    }

    /// <summary>记一件「已从背包摘掉」的实例。</summary>
    internal void NoteRemoved(Item item)
    {
        _items.Remove(item);
        if (!_removed.Contains(item))
            _removed.Add(item);
    }

    internal void NoteLevel(uint mapId, int star) => _levels.Add(new(mapId, star));

    internal void NoteTask(uint key, int value) => _tasks.Add(new(key, value));

    internal void NoteGirl(Girl girl) => _girls.Add(girl);

    /// <summary>合并另一份报告（<c>ApplyAll</c> 用）。</summary>
    internal void Merge(GmUnlockReport other)
    {
        foreach (int id in other._money)
            NoteMoney(id);
        foreach (var it in other._items)
            NoteItem(it);
        foreach (var it in other._removed)
            NoteRemoved(it);
        _levels.AddRange(other._levels);
        _tasks.AddRange(other._tasks);
        _girls.AddRange(other._girls);
        NewCards += other.NewCards;
        LevelChanged |= other.LevelChanged;
    }

    public string Describe()
        => $"货币 {_money.Count} 种 / 背包 {_items.Count} 项（新卡 {NewCards} 张）/ " +
           $"移除 {_removed.Count} 项 / 关卡 {_levels.Count} 关 / 任务值 {_tasks.Count} 条 / " +
           $"新少女 {_girls.Count} 位{(LevelChanged ? " / 等级已变更" : "")}";
}

/// <summary>
/// 「GM 测试账号全解锁」的实现：全道具、全角色卡、全关卡三星、全任务完成。
///
/// <para><b>0. 为什么要它</b></para>
/// 验收一个功能域的标准做法是「GM 下发一条上行命令」（AGENTS.md §3.0），
/// 但绝大多数上行命令都有前置数据要求：仓库要有物品才能分解、角色界面要有卡才能看衣服、
/// 商店/抽卡/养成都要有货币和材料、任务面板要有进度才能领奖。
/// 逐条 <c>gmsrv item …</c> 造数据既慢又容易漏，所以把「一个什么都有的账号」做成一条命令。
///
/// <para><b>1. 幂等 + revision 重放</b></para>
/// 全部四个域的写法都是「设成目标值」而不是「累加」：
/// <list type="bullet">
///   <item>货币 → <see cref="Player.SetMoney"/> 绝对值（不会翻倍、不会溢出 int32）；</item>
///   <item>道具 → 已有实例把 <see cref="Item.Count"/> 抬到目标值，没有才新建；</item>
///   <item>角色卡 → 按 <c>(G,D,P,L)</c> 查重，已有就不再建实例（卡是 genre&lt;=4，一份一个 guid）；</item>
///   <item>关卡 → 只补三星位，已有通关记录不再累加次数（与 <c>gmsrv unlock</c> 同一策略）；</item>
///   <item>任务值 → 直接覆盖成 <c>(阈值 &lt;&lt; 1) | 已领bit</c>。</item>
/// </list>
/// 于是可以在**每次登录**时无条件调用而不会把存档撑爆。为避免每次登录都白跑一遍
/// 3000+ 条循环，存档里记一个 <see cref="Revision"/>（<c>Player.Extra["gmunlock"]</c>），
/// 只有 revision 落后才真正执行 —— 将来往全解锁里加内容时把 <see cref="Revision"/> 加一，
/// 老存档下次登录会自动补齐。
///
/// <para><b>2. 时机：必须在 pf 1005 之前</b></para>
/// 由 <see cref="Handlers.GmProvisionFeature"/> 注册到 <c>ctx.OnPlayerPrepare</c>，
/// 该钩子在 <c>World.Login</c> 之后、<c>BuildPlayerNtf</c> **之前**触发，
/// 所以全部数据随登录本来就要发的四条全量包（1005 PlayerNtf / 1104 ItemNtf / 1026 TaskValueSyncRsp）
/// 下发，**一个额外封包都不需要**。运行期用 gmsrv 触发时才需要 <see cref="GmUnlockReport"/> 推增量。
///
/// <para><b>3. 四个域各自的取证</b></para>
/// 见 <see cref="ApplyItems"/> / <see cref="ApplyCards"/> / <see cref="ApplyLevels"/> /
/// <see cref="ApplyMissions"/> 的方法注释。
/// </summary>
public sealed class GmUnlock
{
    /// <summary>
    /// 全解锁内容的版本号。**往全解锁里加新内容时必须加一**，
    /// 否则已经标记过的老存档不会重放（见类注释 §1）。
    ///
    /// 变更历史：
    /// <list type="number">
    ///   <item>1 —— 首版：全道具（ItemList.txt 2120 条）、全角色卡（CharacterCard.txt 268 张，
    ///     排除 Detail=102）、全关卡三星（Chapter.txt 495 关）、全任务完成
    ///     （DailyMission / Achievement / AchievementOther / GuideMission / ClubMission）
    ///     + 衣服(group 4)/卡面(group 7) 获得标记 + 少女展示标记(group 3)。</item>
    ///   <item>2 —— 补「全新手指引完成」（<c>UIGuide.txt</c> 114 条 → 任务值 group 10 = 100）。
    ///     没有它的话 GM 账号登录后仍会触发引导剧情，而事后用客户端的
    ///     <c>GuideManager:DebugSetGuideComplete()</c> 去关会把 UI 栈打空
    ///     （表现极像「账号数据被清空、退回新手教程」）——
    ///     取证与坑见 <see cref="Config.GuideTable"/>。
    ///     同时补「指挥官满级」（<c>PlayerExp.txt</c> 的 <c>MaxLevel</c>，现网 300 级）
    ///     与该等级的体力上限。</item>
    ///   <item>3 —— 「全道具」跳过并**清理**图标在本地资源快照里缺失的 136 条道具
    ///     （清单 <c>MissingIcon.txt</c>，见 <see cref="Config.MissingIconTable"/>）。
    ///     它们在仓库里会画成灰底问号（客户端兜底图 <c>Num/what.png</c>，<c>Lib.lua:475</c>），
    ///     而对应功能（塔活动/宠物/外景 DIY/少女皮肤）本来也没实装。
    ///     rev 2 的老存档已经发过这些道具，所以本次升级要把它们从背包摘掉并推 count=0。</item>
    ///   <item>4 —— 设「看板娘」任务值 <c>(2,2)</c>。不设它主城左上角的玩家头像是**纯白块**
    ///     （<c>UI_Player.lua:507</c> 读 <c>GirlCommon:MainGirl()</c>，缺省 0 ⇒
    ///     <c>GetGirl(0)</c> 为 nil ⇒ <c>imgPortrait.sprite</c> 从未赋值 ⇒ Unity 默认白图）。
    ///     这是纯服务端问题，与资源缺失无关。</item>
    ///   <item>5 —— ★ **补发之前被「图标缺失」误伤的道具**。
    ///     根因搞清了：那些图标**一直都在资源包里**（例：`icon_weapon_part_add` 在
    ///     `item/dg2_more.u`），取不到是因为**设备上的 `version.data` 是 APK 自带的旧清单**、
    ///     里面没登记 `dg2_more` 这类包（`push_assets.sh` 默认不推 `version.data`）。
    ///     把备份的 `version.data` 推上去后，缺失图标从 **125 张降到 23 张**
    ///     （受影响道具 136 → 25）⇒ 之前被 <see cref="MissingIconTable"/> 跳过的
    ///     100 多条道具现在可以正常发了，需要提 rev 触发补发。
    ///     详见 `docs/05` §10.1.1。</item>
    ///   <item>6 —— ★ **全剧情/CG 已读**（第十二轮，<see cref="ApplyCartoons"/>）。
    ///     填满任务值 group 39 的位域 ⇒ 往昔之忆/剧情回顾/CG 图鉴全开，
    ///     且终端私信/朋友圈的「剧情类」触发（177 条）从此可被补发。
    ///     存量存档需要提 rev 才会补上这批位域。详见 `docs/05` §39。</item>
    /// </list>
    /// </summary>
    public const int Revision = 6;


    /// <summary><see cref="Player.Extra"/> 里记 revision 的键。</summary>
    public const string ExtraKey = "gmunlock";

    /// <summary>
    /// 少女 id 的合法区间（客户端 <c>GirlConfig.CheckGirlId</c>，GirlConfig.lua:436..445）：
    /// <c>1 &lt;= id &lt;= NormalEndGirlId(32)</c> 是普通少女，
    /// <c>200 &lt; id &lt;= LinkEndGirlId(232)</c> 是联动少女，其余一律 <c>GirlIdNull</c>。
    ///
    /// ★ 这条判定就是**必须排除 <c>CharacterCard.txt</c> 里 Detail=102 那张卡**的原因：
    /// 102 既 &gt; 32 又不在 (200,232]，客户端 <c>GirlCommon:GetGirlTaskEx/SetGirlTaskEx</c>
    /// （GirlCommon.lua:1514..1521 / :1538..1545）会走 <c>GirlIdNull</c> 分支，
    /// 打印 <c>GetGirlTaskEx Error Group…</c> 后 <b>return nil</b> —— 于是衣服/卡面标记
    /// 全部读到 nil，之后任何 <c>&gt; 0</c> 比较都会抛「attempt to compare nil with number」。
    /// </summary>
    public const int NormalEndGirlId = 32;

    /// <summary>联动少女起始 id（<c>GirlConfig.LinkGirlId = 200</c>，GirlConfig.lua:82）。</summary>
    public const int LinkGirlId = 200;

    /// <summary>联动少女结束 id（<c>GirlConfig.LinkEndGirlId = 232</c>，GirlConfig.lua:83）。</summary>
    public const int LinkEndGirlId = 232;

    /// <summary>
    /// 少女相关任务值的 taskId 偏移基数：<c>taskId = (girlId - 1) * 2000 + off</c>
    /// （<c>GirlCommon:GetGirlTaskEx</c>，GirlCommon.lua:1525）。
    /// </summary>
    public const int GirlTaskStride = 2000;

    /// <summary>少女扩展变量 group（<c>GirlCommon:Get/Set(nId, sKey)</c> 走 group 3，GirlCommon.lua:98/:149）。</summary>
    public const int GirlVarGroup = 3;

    /// <summary>
    /// 衣服（服装/模型）获得标记 group（<c>GirlCommon:SuitValue</c>，GirlCommon.lua:637..641）。
    /// <c>taskId = (girlId-1)*2000 + FixModelId(ModelId)</c>，值语义：
    /// 0 未获得 / 1 已获得但「新」（红点，<c>UI_GirlClothes.lua:846 IsNew = (SuitValue == 1)</c>）/
    /// 2 已获得且已穿戴过。全解锁填 <b>2</b>：既解锁又不留红点。
    /// </summary>
    public const int SuitGroup = 4;

    /// <summary>
    /// 卡面获得标记 group（<c>GirlCommon:CardValue</c>，GirlCommon.lua:658..662）。
    /// <c>taskId = (girlId-1)*2000 + (P*20 + L)</c>，值语义：
    /// <c>&gt; 0</c> 已解锁图鉴（<c>UI_GirlLoveAlbum.lua:131/:257/:265</c>）、
    /// <c>&gt; 1</c> 才算 <c>bHasCard</c>（<c>UI_GachaGet.lua:100</c> 判重复卡）。
    /// 全解锁填 <b>2</b>。
    /// </summary>
    public const int CardGroup = 7;

    /// <summary>卡面标记里 P 的乘数（<c>nCardP * 20 + nCardL</c>，GirlCommon.lua:660）。</summary>
    public const int CardPStride = 20;

    /// <summary>
    /// group 3 偏移 7 <c>IsEndShow</c>「是否结束获取新角色的展示」（GirlCommon.lua:30）。
    /// 全解锁一次给 16 位少女，不置这个标记会在进游戏后排队播 N 段「获得新角色」展示。
    /// </summary>
    public const int GirlVarIsEndShow = 7;

    /// <summary>group 3 偏移 13 <c>SuitCount</c>「衣服数」（GirlCommon.lua:41）。</summary>
    public const int GirlVarSuitCount = 13;

    /// <summary>
    /// 看板娘所在的任务值 group（<c>GirlCommon:MainGirl</c> / <c>SetMainGirl</c> 走
    /// <c>me:GetTaskValue(2, 2)</c> / <c>SetTaskValue(2, 2, id)</c>，GirlCommon.lua:676..687）。
    /// </summary>
    public const int MainGirlGroup = 2;

    /// <summary>看板娘的 taskId（同上，固定 2）。</summary>
    public const int MainGirlTaskId = 2;

    /// <summary>
    /// 衣服/卡面「已获得且非新」的标记值（见 <see cref="SuitGroup"/> / <see cref="CardGroup"/>）。
    /// </summary>
    public const int OwnedNotNew = 2;

    private readonly ChapterTable _chapters;
    private readonly ItemBaseTable _items;
    private readonly CharacterCardTable _cards;
    private readonly MissionTable _missions;
    private readonly GuideTable _guides;
    private readonly PlayerExpTable _playerExp;
    private readonly MissingIconTable _missingIcons;
    private readonly ServerOptions _options;

    public GmUnlock(ServerOptions options, ChapterTable chapters, ItemBaseTable items,
                    CharacterCardTable cards, MissionTable missions,
                    GuideTable guides, PlayerExpTable playerExp,
                    MissingIconTable? missingIcons = null)
    {
        _options = options;
        _chapters = chapters;
        _items = items;
        _cards = cards;
        _missions = missions;
        _guides = guides;
        _playerExp = playerExp;
        _missingIcons = missingIcons ?? MissingIconTable.Empty;
    }

    /// <summary>该账号是否是 GM 测试账号（<see cref="ServerOptions.GmAccounts"/>，支持 <c>"*"</c> 通配）。</summary>
    public bool IsGmAccount(string account)
    {
        var list = _options.GmAccounts;
        if (list is null || list.Length == 0)
            return false;
        foreach (var entry in list)
        {
            if (entry == "*")
                return true;
            if (string.Equals(entry ?? "", account ?? "", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>存档里记的全解锁 revision（没有记过返回 0）。</summary>
    public static int RevisionOf(Player player)
    {
        if (!player.Extra.TryGetValue(ExtraKey, out var node) || node is null)
            return 0;
        try
        {
            return node["rev"]?.GetValue<int>() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>把当前 <see cref="Revision"/> 与时间戳写进存档。</summary>
    private static void MarkRevision(Player player)
    {
        player.Extra[ExtraKey] = new System.Text.Json.Nodes.JsonObject
        {
            ["rev"] = Revision,
            ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }

    /// <summary>
    /// 存档是否已经跑过当前版本的全解锁（<see cref="Revision"/> 不落后）。
    /// </summary>
    public static bool IsUpToDate(Player player) => RevisionOf(player) >= Revision;

    /// <summary>
    /// 四个域一起做，并写上 revision 标记。<paramref name="force"/> 为 false 时
    /// 已是最新版本就直接返回空报告（登录路径用，避免每次登录白跑 3000+ 次循环）。
    /// </summary>
    public GmUnlockReport ApplyAll(Player player, bool force = true)
    {
        var report = new GmUnlockReport();
        if (!force && IsUpToDate(player))
            return report;

        report.Merge(ApplyItems(player));
        report.Merge(ApplyCards(player));
        report.Merge(ApplyLevels(player));
        report.Merge(ApplyMissions(player));
        report.Merge(ApplyGuides(player));
        report.Merge(ApplyCartoons(player));
        report.Merge(ApplyMaxLevel(player));
        MarkRevision(player);
        return report;
    }

    // =================================================================
    //  ① 全道具
    // =================================================================

    /// <summary>
    /// 把 <c>ItemList.txt</c> 的每一条模板都给玩家。
    ///
    /// <para><b>两条分支</b></para>
    /// <list type="number">
    /// <item><b>货币类道具</b>（<see cref="ItemBaseTable.TryGetMoneyId"/> 命中：
    ///   <c>ClassName ∈ *CoinAuto</c> 且 <c>ExtParam2</c> 是货币 id，或
    ///   <see cref="ItemBaseTable.KnownMoneyItems"/> 硬映射）→ 折算成货币，
    ///   直接 <see cref="Player.SetMoney"/> 成 <see cref="ServerOptions.GmMoneyAmount"/>
    ///   （绝对值，幂等）。客户端本来也看不到这些道具的背包实例（<c>AutoUse</c>）。</item>
    /// <item><b>其余道具</b> → 按四元组叠加成一个实例，数量抬到
    ///   <see cref="ServerOptions.GmItemCount"/>。</item>
    /// </list>
    ///
    /// <para><b>为什么这么发是安全的</b></para>
    /// <c>ItemList.txt</c> 的 <c>Genre</c> 列值域是 <b>5..22</b>（实测 2120 行统计），
    /// 而客户端 pf 1031 的分发（<c>Game.EquipManager.OnUpdateItem</c> RVA 0x1D9D72C，
    /// genre 比较在 0x1D9D85C 起）只对 1/2/4 特判：
    /// <code>
    ///   genre == 1 → AddCharacterCard (0x1D95368)
    ///   genre == 2 → AddWeapon        (0x1D96FEC)
    ///   genre == 4 → AddOriginal      (0x1D99094)
    ///   其余(含 3 与 5..22) → AddItem  (default 分支 0x1D9D934)
    /// </code>
    /// 即本表全部走 <c>AddItem</c>，**不碰** <c>skillinfo</c>/<c>weaponinlay</c>/<c>attrlist</c>/<c>ownguid</c>
    /// 那四个「无条件先 Clear」的字段（docs/05-feature-domains.md §0.3 的红线），
    /// 所以留空是安全的。武器(2)/符文(3)/零件(4) 不在本表里，本方法也就不会发它们。
    ///
    /// <para><b>已知不完美</b></para>
    /// 1719 条模板是 <c>AutoUse</c>（入包即自动使用），其中 707 条 <c>CafeItemAuto</c>（咖啡馆家具）、
    /// 441 条 <c>IBGiftAuto</c>（礼包）、110 条 <c>ItemHeadFrameAuto</c>（头像框）等，
    /// 原服会在入包瞬间把效果落到玩家数据上（解锁家具 / 开出礼包 / 解锁头像框），道具本身不留在背包。
    /// 服务端还没有这些 <c>ClassName</c> 的逐类语义（docs/05 §「使用道具」），
    /// 所以这里一律只塞进背包 —— 表现是「背包里有 707 个家具道具，但咖啡馆家具没解锁」。
    /// 这不影响任何链路，等实现 <c>ItemFlushFlag nCmd=2</c> 的逐类效果后会自然对齐。
    /// </summary>
    public GmUnlockReport ApplyItems(Player player)
    {
        var report = new GmUnlockReport();
        if (_items.Count == 0)
        {
            Log.Warn("[gmunlock] ItemList.txt 为空表，跳过「全道具」");
            return report;
        }

        int money = 0, stacks = 0;
        uint target = (uint)Math.Max(1, _options.GmItemCount);
        int skippedIcon = 0;

        // 先建 (G,D,P,L) → 实例 的索引，避免对 2120 条各做一次 O(n) 线性查找
        var byKey = new Dictionary<(uint, uint, uint, uint), Item>();
        foreach (var it in player.Items)
            byKey.TryAdd((it.Genre, it.Detail, it.Particular, it.Level), it);

        foreach (var tpl in _items.All)
        {
            if (_items.TryGetMoneyId(tpl.G, tpl.D, tpl.P, tpl.L, out int moneyId))
            {
                if (player.GetMoney(moneyId) != _options.GmMoneyAmount)
                {
                    player.SetMoney(moneyId, _options.GmMoneyAmount);
                    report.NoteMoney(moneyId);
                }
                money++;
                continue;
            }

            // ★ 图标在本地资源快照里缺失的道具：默认跳过，否则仓库里会画成灰底问号
            // （客户端 Lib.lua:475 的兜底图 Num/what.png）。取证见 MissingIconTable。
            // 老存档里已经发过的也要**摘掉**（只是「以后不发」不够 —— 问号已经在仓库里了）。
            if (_options.GmSkipMissingIcon && _missingIcons.IsMissing(tpl.IconId))
            {
                skippedIcon++;
                if (byKey.TryGetValue((tpl.G, tpl.D, tpl.P, tpl.L), out var stale))
                {
                    player.Items.Remove(stale);
                    byKey.Remove((tpl.G, tpl.D, tpl.P, tpl.L));
                    report.NoteRemoved(stale);
                }
                continue;
            }

            if (byKey.TryGetValue((tpl.G, tpl.D, tpl.P, tpl.L), out var exist))
            {
                if (exist.Count < target)
                {
                    exist.Count = target;
                    report.NoteItem(exist);
                }
            }
            else
            {
                var created = player.AddItem(tpl.G, tpl.D, tpl.P, tpl.L, target);
                // Flag bit1 = 剧情已读（bit1=1 才不显示「剧情未读」红点，见 Item.Flag 注释）；
                // bit0 = 背包「新道具」红点，全解锁不点亮。
                created.Flag = Item.FlagStoryReadBit;
                byKey[(tpl.G, tpl.D, tpl.P, tpl.L)] = created;
                report.NoteItem(created);
            }
            stacks++;
        }

        Log.Info($"[gmunlock] 全道具：模板 {_items.Count} 条 → 货币 {money} 种（各 {_options.GmMoneyAmount}）" +
                 $"、背包 {stacks} 种（各 {target}），本次变更 {report.ChangedItems.Count} 项" +
                 (skippedIcon > 0
                     ? $"；跳过 {skippedIcon} 条「图标缺失」道具" +
                       (report.RemovedItems.Count > 0
                           ? $"（其中从背包摘掉 {report.RemovedItems.Count} 条历史遗留）"
                           : "") +
                       "（本地资源快照没有那张图，发了会在仓库里画成问号 —— 见 MissingIconTable）"
                     : ""));
        return report;
    }

    // =================================================================
    //  ② 全角色卡
    // =================================================================

    /// <summary>
    /// 把 <c>CharacterCard.txt</c> 的每张卡都给玩家，并补齐少女数据与「已获得」标记。
    ///
    /// <para><b>四件事</b></para>
    /// <list type="number">
    /// <item><b>卡片实例</b>：genre=1，每张一个 guid（卡是 <c>genre &lt;= 4</c>，绝不叠加 ——
    ///   与 <see cref="RewardService"/> 同一规则）。已有同四元组实例就跳过（幂等）。</item>
    /// <item><b>少女数据</b>（<c>PlayerNtf.girllist</c> → <c>GirlInfo</c>）：每个出现过的
    ///   <c>Detail</c> 建一条。<c>Flag = 0</c> —— ★ <c>GirlInfo.flag == 1</c> 是
    ///   「新」红点（<c>UI_Girl.lua:1085..1090 UpdateNewFlag</c>：<c>newFlag == 1</c> 时才发
    ///   <c>LuaCall.Girl_UpdateFlag</c> 请服务端清除；<c>UI_CafeAssign.lua:1064</c>
    ///   <c>cell.RedObj:SetActive(newFlag == 1)</c>），而该上行命令服务端还没实现 ⇒
    ///   给 1 会让红点**永久**亮着。<c>Modelid</c> 取该少女最小 <c>(P,L)</c> 那张卡的
    ///   <c>ModelId</c> 列 —— ★ 实测 269 条全部满足 <c>ModelId == Particular</c>，
    ///   即它是「当前穿的那套衣服」而不是少女 id（取证 <c>UI_GirlClothes.lua:479</c>
    ///   <c>if tbGirl.ModelId == tbBaseInfo.ModelId</c>、<c>GirlConfig.lua:340</c>
    ///   <c>tbSuitConfig[GirlId][tb.ModelId]</c>）。</item>
    /// <item><b>衣服获得标记</b>：group 4，<c>taskId=(girlId-1)*2000+FixModelId(ModelId)</c> = 2。</item>
    /// <item><b>卡面获得标记</b>：group 7，<c>taskId=(girlId-1)*2000+(P*20+L)</c> = 2。</item>
    /// </list>
    /// 另外置 group 3 的 <c>IsEndShow</c>=1（不然进游戏会排队播 16 段「获得新角色」展示）
    /// 与 <c>SuitCount</c>=该少女衣服数。
    ///
    /// <para><b>排除项</b></para>
    /// <c>Detail</c> 不在 <c>[1,32] ∪ (200,232]</c> 的卡一律跳过 —— 现网表里就是
    /// <c>Detail=102</c> 那 1 张（卡名与洛可可同 <c>cc_gongshifu_luokeke</c>，疑为测试残留）。
    /// 理由见 <see cref="NormalEndGirlId"/> 的注释：客户端会因 <c>GirlIdNull</c> 返回 nil 而抛错。
    /// </summary>
    public GmUnlockReport ApplyCards(Player player)
    {
        var report = new GmUnlockReport();
        if (_cards.Count == 0)
        {
            Log.Warn("[gmunlock] CharacterCard.txt 为空表，跳过「全角色卡」");
            return report;
        }

        var owned = new HashSet<(uint, uint, uint, uint)>();
        foreach (var it in player.Items)
        {
            if (it.Genre == CharacterCardTable.CardGenre)
                owned.Add((it.Genre, it.Detail, it.Particular, it.Level));
        }

        int skipped = 0, added = 0;
        // girlId → (该少女的全部 ModelId, 最小 (P,L) 那张卡)
        var suitsByGirl = new Dictionary<uint, HashSet<int>>();
        var firstCardByGirl = new Dictionary<uint, CharacterCardTemplate>();

        foreach (var card in _cards.All.OrderBy(c => c.D).ThenBy(c => c.P).ThenBy(c => c.L))
        {
            if (!IsValidGirlId(card.D))
            {
                skipped++;
                Log.Warn($"[gmunlock] 跳过角色卡 {card}：Detail={card.D} 不是合法少女 id " +
                         $"（GirlConfig.CheckGirlId 判为 GirlIdNull，客户端 GetGirlTaskEx 会返回 nil）");
                continue;
            }

            if (!suitsByGirl.TryGetValue(card.D, out var suits))
                suitsByGirl[card.D] = suits = new HashSet<int>();
            suits.Add(FixModelId(card.ModelId));
            firstCardByGirl.TryAdd(card.D, card);

            if (owned.Add((CharacterCardTable.CardGenre, card.D, card.P, card.L)))
            {
                var item = player.AddItem(CharacterCardTable.CardGenre, card.D, card.P, card.L, count: 1);
                item.Flag = Item.FlagStoryReadBit;   // 卡片剧情标记为已读，不留红点
                report.NoteItem(item);
                added++;
            }

            // 衣服（服装）获得标记：group 4
            SetTask(player, report, GirlTaskGroup(SuitGroup, card.D), GirlTaskId(card.D, FixModelId(card.ModelId)), OwnedNotNew);
            // 卡面获得标记：group 7
            SetTask(player, report, GirlTaskGroup(CardGroup, card.D),
                    GirlTaskId(card.D, (int)(card.P * CardPStride + card.L)), OwnedNotNew);
        }

        foreach (var (girlId, card) in firstCardByGirl.OrderBy(kv => kv.Key))
        {
            var girl = player.GetGirl(girlId);
            if (girl is null)
            {
                girl = new Girl
                {
                    Girlid = girlId,
                    Level = 1,
                    Modelid = (uint)FixModelId(card.ModelId),
                    Moodvalue = 100,
                    Vigor = 100,
                    Flag = 0,   // 0 = 不亮「新」红点（见方法注释）
                };
                player.Girls.Add(girl);
                report.NoteGirl(girl);
            }
            else if (girl.Flag == 1)
            {
                // 老存档（含 StarterLoadout 发的 4 张）留下的常亮红点，顺手清掉
                girl.Flag = 0;
                report.NoteGirl(girl);
            }

            SetTask(player, report, GirlTaskGroup(GirlVarGroup, girlId), GirlTaskId(girlId, GirlVarIsEndShow), 1);
            SetTask(player, report, GirlTaskGroup(GirlVarGroup, girlId), GirlTaskId(girlId, GirlVarSuitCount),
                    suitsByGirl[girlId].Count);
        }

        // ★ 看板娘：不设的话主城左上角的玩家头像是**纯白块**。
        // 取证 UI_Player.lua:507..511：
        //     local nMainGirlID = GirlCommon:MainGirl();        -- = me:GetTaskValue(2, 2)
        //     if nMainGirlID then
        //         local tbGirl = me:GetGirl(nMainGirlID);
        //         if tbGirl then self.imgPortrait.sprite = Sprite(tbGirl.Icon); end
        //     end
        // (2,2) 缺省是 0 ⇒ GetGirl(0) 返回 nil ⇒ imgPortrait.sprite **从未被赋值** ⇒
        // 保持 Unity 默认的白图。注意它连 Sprite() 的兜底都走不到，所以是「空白」而不是「问号」。
        if (player.GetTask(MainGirlGroup, MainGirlTaskId) == 0 && firstCardByGirl.Count > 0)
        {
            uint mainGirl = firstCardByGirl.Keys.Min();
            SetTask(player, report, MainGirlGroup, MainGirlTaskId, (int)mainGirl);
            Log.Info($"[gmunlock] 看板娘 (2,2) → 少女 {mainGirl}" +
                     "（不设会让主城左上角的玩家头像显示成白块，取证 UI_Player.lua:507）");
        }

        Log.Info($"[gmunlock] 全角色卡：表内 {_cards.Count} 张 → 新建 {added} 张实例，" +
                 $"少女 {firstCardByGirl.Count} 位（跳过非法 Detail {skipped} 张）");
        report.NewCards = added;
        return report;
    }

    /// <summary>少女 id 是否合法（<c>GirlConfig.CheckGirlId</c> 口径，见 <see cref="NormalEndGirlId"/>）。</summary>
    public static bool IsValidGirlId(uint girlId)
        => (girlId >= 1 && girlId <= NormalEndGirlId)
        || (girlId > LinkGirlId && girlId <= LinkEndGirlId);

    /// <summary>少女相关任务值的 taskId：<c>(girlId-1) * 2000 + off</c>（GirlCommon.lua:1525）。</summary>
    public static int GirlTaskId(uint girlId, int off)
        => ((int)(girlId > LinkGirlId && girlId <= LinkEndGirlId ? girlId - LinkGirlId : girlId) - 1) * GirlTaskStride + off;

    public static int GirlTaskGroup(int group, uint girlId)
        => girlId > LinkGirlId && girlId <= LinkEndGirlId
            ? group switch { GirlVarGroup => 90, SuitGroup => 91, CardGroup => 92, _ => group }
            : group;

    /// <summary>
    /// <c>GirlCommon:FixModelId</c> 的移植（GirlCommon.lua:609..629）：
    /// &lt; 2000 原样；8001/8002/8003 → 1801/1802/1803；&gt;= 3000 且个十位 &lt;= 29 →
    /// <c>modelId % 100 + (modelId / 1000) * 30 + 1500</c>；其余是配置错误（客户端 assert）。
    ///
    /// ★ 这个函数**在现网数据上真的会被用到**（早先以为不会）：
    /// <c>CharacterCard.txt</c> 里有 27 张卡的 ModelId &gt;= 2000 ——
    /// 13 张 <c>8001</c>（→1801）、3 张 <c>8002</c>（→1802）、11 张 <c>7001</c>（→1711）。
    /// 不过这一层，group 4 的衣服标记就会写到 <c>(girlId-1)*2000 + 8001</c>，
    /// 而 <see cref="Player.ComposeTaskId"/> 只保留低 16 位 ——
    /// girlId &gt;= 3 时 <c>(girlId-1)*2000 + 8001</c> 仍在 16 位内，但**与客户端读的键不同**，
    /// 于是「衣服明明发了却显示未获得」，且没有任何报错。
    /// </summary>
    public static int FixModelId(int modelId)
    {
        if (modelId < 2000)
            return modelId;
        switch (modelId)
        {
            case 8001: return 1801;
            case 8002: return 1802;
            case 8003: return 1803;
        }
        if (modelId >= 3000 && modelId % 100 <= 29)
            return modelId % 100 + modelId / 1000 * 30 + 1500;

        Log.Warn($"[gmunlock] ModelId={modelId} 不符合 GirlCommon:FixModelId 的任何分支" +
                 "（客户端会 assert），原样返回");
        return modelId;
    }

    // =================================================================
    //  ③ 全关卡
    // =================================================================

    /// <summary>
    /// 把 <c>Chapter.txt</c> 的每一关都记成三星通关。
    ///
    /// star 是位域（bit0..2 三星、bit3..31 通关次数，见 <see cref="MapList"/>）；
    /// 已有通关记录的只补三星位、**不再累加通关次数**（与 <c>gmsrv unlock/passchapter</c>
    /// 同一策略，避免反复执行把次数刷爆）。
    ///
    /// 这一条同时解决了「功能解锁」：主城几乎所有入口都由 <c>SystemUnlock.txt</c>
    /// 按「某一关是否通关」判定（docs/04-level-flow.md §4），所以全关卡通关 ⊇ <c>unlock all</c>。
    /// 现网 <c>Chapter.txt</c> 是 495 关（163 关 × 难度 1/2/3 + 6 关 difficult=10，
    /// 章节 1..15 与 100）。
    /// </summary>
    public GmUnlockReport ApplyLevels(Player player)
    {
        var report = new GmUnlockReport();
        if (_chapters.Count == 0)
        {
            Log.Warn("[gmunlock] Chapter.txt 为空表，跳过「全关卡」");
            return report;
        }

        foreach (var cfg in _chapters.All.OrderBy(c => c.Chapter).ThenBy(c => c.Index).ThenBy(c => c.Diff))
        {
            uint mapId = cfg.MapId;
            int cur = player.GetLevelStar(mapId);
            int composed = MapList.PassCount(cur) > 0
                ? MapList.Compose(MapList.Stars(cur) | MapList.StarMask, MapList.PassCount(cur))
                : MapList.Compose(MapList.StarMask, 1);
            if (composed == cur)
                continue;
            player.Levels[mapId] = composed;
            report.NoteLevel(mapId, composed);
        }

        Log.Info($"[gmunlock] 全关卡：Chapter.txt {_chapters.Count} 关 → 变更 {report.ChangedLevels.Count} 关三星通关");
        return report;
    }

    // =================================================================
    //  ④ 全任务 / 成就
    // =================================================================

    /// <summary>
    /// 把全部任务/成就标记成「完成」。
    ///
    /// <para><b>写什么</b></para>
    /// 任务值 = <c>(进度 &lt;&lt; 1) | 已领bit</c>（<c>MissionManager:GetTaskPro/GetTaskAward</c>，
    /// MissionManager.lua:595/:602，反过来拼即 <see cref="Handlers.MissionFeature.ComposeTaskValue"/>）。
    /// 进度取该条的完成阈值 <c>missionMax[nKey]</c>（成就 <c>nKey = assistparam[1]</c>，
    /// 其余固定第 1 项 —— <c>CheckInfoState</c>，MissionManager.lua:927..954），
    /// 已领 bit 取 <see cref="ServerOptions.GmMissionClaimed"/>。
    ///
    /// <para><b>覆盖哪些表</b></para>
    /// <list type="bullet">
    ///   <item><c>DailyMission.txt</c>（group 5）、<c>GuideMission.txt</c>（group 5）、
    ///     <c>ClubMission.txt</c>（group 5）、<c>Achievement.txt</c>（group 6）——
    ///     这四张就是 <see cref="Handlers.MissionFeature"/> 的 <c>AllEntryBuckets</c>，
    ///     所以本方法写完的值不会被它的 <c>SyncTaskValues</c> 改回去
    ///     （那边的进度是「只升不降」的 max 合并）。</item>
    ///   <item><c>AchievementOther.txt</c>（group 18，1857 条活动成就）—— MissionFeature
    ///     只装载不使用，这里一并置上，让活动成就面板也是满的。</item>
    /// </list>
    /// 再加三个「进度奖励档位」位标记（<c>PointAward 30001</c> / <c>GuideCompleteAward 41001</c> /
    /// <c>ClubCompleteAward 51001</c>，bit 从 **1** 起，bit0 空着不用 ——
    /// <c>UI_Achievement.lua:715 for i=1,5</c>）与活跃点数 <c>30000</c>（裸值，
    /// 设成 <c>MissionAward.txt</c> 的最大 Point）。
    ///
    /// <para><b>阈值缺失的处理</b></para>
    /// <c>RequiredCount</c> 在配置缺 <c>missionMax</c> 时返回 <see cref="int.MaxValue"/>
    /// （「宁可不发也不错发」）。这里把它当 1 处理 —— 全解锁的语义就是「都算完成」，
    /// 而 <c>ComposeTaskValue</c> 会把进度夹到 <c>0x3FFFFFFF</c>，
    /// 直接写 MaxValue 会得到一个巨大但无意义的进度数字显示在面板上。
    /// </summary>
    public GmUnlockReport ApplyMissions(Player player)
    {
        var report = new GmUnlockReport();
        int claimedBit = _options.GmMissionClaimed ? 1 : 0;
        int total = 0;

        foreach (var (entries, achieveKey) in AllMissionBuckets())
        {
            foreach (var entry in entries.Values.OrderBy(e => e.Id))
            {
                int need = entry.RequiredCount(achieveKey);
                if (need == int.MaxValue || need <= 0)
                    need = 1;
                SetTask(player, report, entry.Group, entry.Id,
                        Handlers.MissionFeature.ComposeTaskValue(need, claimedBit));
                total++;
            }
        }

        // 三个进度奖励档位的位标记（bit 从 1 起）
        SetFlagBits(player, report, Handlers.MissionFeature.PointAwardTaskId,
                    _missions.ActivePointAwards.Keys);
        SetFlagBits(player, report, Handlers.MissionFeature.GuideCompleteAwardTaskId,
                    _missions.GuideProgressAwards.Keys);
        SetFlagBits(player, report, Handlers.MissionFeature.ClubCompleteAwardTaskId,
                    _missions.ClubProgressAwards.Keys);

        // 活跃点数：裸值（MissionManager.lua:619..621 没过 LoadBits）
        if (_missions.ActivePointMax > 0)
        {
            SetTask(player, report, MissionTable.MissionGroup,
                    Handlers.MissionFeature.ActivePointTaskId, _missions.ActivePointMax);
        }

        Log.Info($"[gmunlock] 全任务：{total} 条任务/成就标记为" +
                 $"{(claimedBit != 0 ? "已完成已领奖" : "已完成可领取")}，" +
                 $"活跃点数 {_missions.ActivePointMax}，本次变更任务值 {report.ChangedTasks.Count} 条");
        return report;
    }

    /// <summary>五张任务表 + 「阈值是否走 assistparam」的标志（见 <see cref="MissionEntry.RequiredCount"/>）。</summary>
    private IEnumerable<(IReadOnlyDictionary<int, MissionEntry> Entries, bool AchieveKey)> AllMissionBuckets()
    {
        yield return (_missions.Daily, false);
        yield return (_missions.Guide, false);
        yield return (_missions.Club, false);
        yield return (_missions.Achieve, true);
        yield return (_missions.AchieveOther, true);
    }

    // =================================================================
    //  ⑤ 全新手指引完成
    // =================================================================

    /// <summary>
    /// 把 <c>UIGuide.txt</c> 的每一条新手指引标记为已完成
    /// （任务值 <c>group 10</c>，taskId = <c>Index</c> 列，值 = 100）。
    ///
    /// <para><b>★ 这一条是「必须由服务端做」而不是「事后用客户端 Debug 方法关」（本轮踩的坑）</b></para>
    /// 客户端自带 <c>GuideManager:DebugSetGuideComplete()</c>，可以用 <c>tools/gm.sh</c> 事后调用，
    /// 但它内部会 <c>UI.Close("UI_Guide")</c>，若此刻正在播引导剧情就会**把界面栈打空**
    /// （实测 <c>UI.GetTop()</c> 变 nil），画面卡在剧情立绘上、所有按钮点不动 ——
    /// 表现极像「账号数据被清空、退回新手教程」，而实际数据完好
    /// （同一时刻 <c>GetAllItems=2044 / GetAllCard=268 / GirlCommon:Has×16 / IsFirstLevelComplete=true</c>）。
    /// 恢复得手工跑：
    /// <code>
    /// UI.ClearStack(); UI.CloseAll(true)
    /// Map.LoadById(2, function() UI.Open("UI_Player"); UI.Open("UI_Background"); UI.Open("UI_Main") end)
    /// </code>
    /// 服务端在 pf 1005 之前就把这些任务值写好，客户端 <c>GuideManager</c> 初始化时看到的
    /// 就是「全部已完成」，引导**从一开始就不会触发**，也就没有可打断的东西。
    ///
    /// 取证与列语义见 <see cref="GuideTable"/>。
    /// </summary>
    public GmUnlockReport ApplyGuides(Player player)
    {
        var report = new GmUnlockReport();
        if (_guides.Count == 0)
        {
            Log.Warn("[gmunlock] UIGuide.txt 为空表，跳过「全新手指引完成」——" +
                     "GM 账号登录后仍会走引导流程");
            return report;
        }

        foreach (var guide in _guides.All)
        {
            // 与客户端 SetTask 同语义（GuideManager.lua:124）：只升不降
            if (player.GetTask(GuideTable.TaskGroup, guide.TaskId) >= GuideTable.CompleteValue)
                continue;
            SetTask(player, report, GuideTable.TaskGroup, guide.TaskId, GuideTable.CompleteValue);
        }

        Log.Info($"[gmunlock] 全指引：UIGuide.txt {_guides.Count} 条 → " +
                 $"变更 {report.ChangedTasks.Count} 条（group {GuideTable.TaskGroup} = {GuideTable.CompleteValue}）");
        return report;
    }

    // =================================================================
    //  ⑥ 指挥官满级
    // =================================================================

    /// <summary>
    /// 把指挥官等级拉到 <c>PlayerExp.txt</c> 的最高级（现网 300 级），经验清 0。
    ///
    /// <para><b>为什么是「设绝对值」而不是「加经验」</b></para>
    /// 原服的两条 GM 命令是分开的（取证 <c>AutoTestCase.lua:211</c> / <c>:222</c>，
    /// 都走 <c>Game.Player.CallServer('DebugEditor', ...)</c>）：
    /// <code>
    /// GM:SetPlayerLevel(n)    -- 直接设等级
    /// GM:SetPlayerExp(n)      -- 直接设经验
    /// </code>
    /// 而 <c>GMTable.txt</c> 里「Add Exp / 店长经验」那条是 <c>Player:AddExp({0},3)</c>（累加）。
    /// 全解锁要幂等，所以取「设等级」这一路 —— 与 <c>gmsrv level n</c>
    /// （<see cref="Handlers.ServerConsoleFeature"/>）走同一个写法。
    ///
    /// <para><b>连带效果</b></para>
    /// 等级决定体力上限（<c>PlayerExp.txt</c> 的 <c>Spirit</c> 列 → 客户端 <c>me.LevelMaxSpirit</c>）
    /// 与咖啡馆总等级上限（<c>Coffee</c> 列）。体力本身已被 <see cref="ApplyItems"/> 设成
    /// <see cref="ServerOptions.GmMoneyAmount"/>，且 <c>World.RecoverVigour</c> 对
    /// 「已高于上限」的情况只推进计时基准、不回落，所以两者不冲突。
    ///
    /// 关卡的 <c>LevelLimit</c>（指挥官等级门槛）也因此全部满足 —— 这是「全关卡可进」的另一半，
    /// <see cref="ApplyLevels"/> 只负责「已通关记录」。
    /// </summary>
    public GmUnlockReport ApplyMaxLevel(Player player)
    {
        var report = new GmUnlockReport();

        int max = _playerExp.MaxLevel;
        if (max <= 0)
        {
            Log.Warn("[gmunlock] PlayerExp.txt 为空表（MaxLevel=0），跳过「指挥官满级」");
            return report;
        }

        if (player.Level >= max && player.Exp == 0)
            return report;

        int before = player.Level;
        player.Level = max;
        player.Exp = 0;
        report.LevelChanged = true;

        Log.Info($"[gmunlock] 指挥官满级：LV.{before} → LV.{max}" +
                 $"（体力上限 {_playerExp.MaxVigour(max)}，咖啡馆等级上限 {_playerExp.Get(max)?.Coffee ?? 0}）");
        return report;
    }

    // =================================================================
    //  ⑦ 全剧情 / CG 已读（第十二轮新增）
    // =================================================================

    /// <summary>剧情已读位域所在的任务值 group（<c>CartoonData.TaskGroup</c>，`CartoonData.lua:4`）。</summary>
    public const int CartoonGroup = 39;

    /// <summary>剧情已读位域 taskId 的下界（<c>CartoonData.tbTaskId = {0, 400}</c>，`CartoonData.lua:5`）。</summary>
    public const int CartoonMinTaskId = 0;

    /// <summary>见 <see cref="CartoonMinTaskId"/>。</summary>
    public const int CartoonMaxTaskId = 400;

    /// <summary>
    /// 每个 taskId 填多少位。<c>CartoonData:__GetTaskId</c> 用 <c>bit = cartoonId % 32</c>，
    /// 但 <c>bit >= 31</c> 会走另一条分支，所以普通分支实际只用到 <b>bit 0..30</b>；
    /// 且 bit 31 是符号位，写它会让值变负、客户端算术位运算出错。详见 <see cref="ApplyCartoons"/>。
    /// </summary>
    public const int CartoonBitsPerTask = 31;

    /// <summary>
    /// 把**全部剧情/CG 标记为已读** —— 填满任务值 <see cref="CartoonGroup"/> 的
    /// <see cref="CartoonMinTaskId"/>..<see cref="CartoonMaxTaskId"/> 每个 taskId 的低 31 位。
    ///
    /// <para><b>取证：剧情已读就是一个位域，没有别的存储</b></para>
    /// <c>CartoonData</c>（`CartoonData.lua`）全文只有三个入口，都读写同一个位域：
    /// <code>
    /// TaskGroup = 39;  tbTaskId = {0, 400}
    /// __GetTaskId(id): taskId = floor(id/32), bit = id%32                  -- :48..58
    /// CheckById(id):   LoadBits(GetTaskValue(39, taskId), bit, bit) == 1   -- :24..32
    /// SetIsRead(path): value = SetBits(value, 1, bit, bit)                 -- :62..88
    /// </code>
    /// 消费方 <c>CheckByPath(path)</c> 先用 <c>CartoonFileId.txt</c> 把路径映射成 id
    /// 再查这个位域。**服务端不需要那张映射表**：要的是「全解锁」，把值域填满即可，
    /// 无论哪个 id 落在哪一位都会命中。
    /// ★ 那张表确实不在本地快照、也不在任何 <c>.u</c> 里（`tools/scan_bundles.py`
    /// 全量 4684 个包只在 `lua.u` 命中那行 <c>LoadTabFile</c> 的字面量本身），
    /// 所以「按 id 精确置位」这条路走不通，而「全填」不依赖它。
    ///
    /// <para><b>效果</b></para>
    /// <list type="bullet">
    ///   <item><b>往昔之忆 / 剧情回顾全开</b> —— <c>Riki.lua:407</c> 与 <c>:490</c> 的
    ///     <c>info.Unlock = { ... or CartoonData:CheckByPath(StoryID) ... }</c> 全部为真；</item>
    ///   <item><b>活动剧情/CG 图鉴全开</b> —— <c>UI_GuessStory</c> /
    ///     <c>UI_AnniversaryGuessStory</c> 等一律走 <c>CheckByPath</c>；</item>
    ///   <item><b>终端私信/朋友圈的「剧情类」触发（177 条）可被补发</b> ——
    ///     见 <c>PhoneMsgFeature</c> 的 <c>case 1</c>。</item>
    /// </list>
    ///
    /// <para><b>★ 代价（已与用户确认接受）</b></para>
    /// 所有剧情都算「已读」⇒ <b>NEW 红点全部消失、首次播放的自动触发不再发生</b>。
    /// 这正是「全解锁账号」的预期语义 —— 与 <see cref="ApplyGuides"/> 把 114 条新手指引
    /// 直接标成已完成是同一个取舍。
    ///
    /// <para><b>★★ 为什么只填 bit 0..30，不填 bit 31</b></para>
    /// <list type="number">
    ///   <item>客户端 <c>__GetTaskId</c> 在 <c>bit >= 31</c> 时**改写 taskId**
    ///     （<c>taskId = 201 + floor(id/32)</c>、<c>bit = taskId % 30</c>）
    ///     ⇒ <c>id % 32 == 31</c> 的剧情根本不落在「本该」的位上，而是落进
    ///     taskId 201..400 的低位里 —— 那些位**已经**被填满，所以照样命中；</item>
    ///   <item>bit 31 是 <c>int</c> 的符号位，而客户端 <c>LoadBits</c>/<c>SetBits</c>
    ///     （`Lib.lua:519..548`）用 <c>%</c> 与 <c>/</c> 做**算术**运算，
    ///     负数会让 <c>nInt % (1 &lt;&lt; (nEnd+1))</c> 得到负余数 ⇒ 判定出错。
    ///     ⇒ 每个 taskId 填 <c>0x7FFFFFFF</c> 而不是 <c>-1</c>。</item>
    /// </list>
    /// </summary>
    public GmUnlockReport ApplyCartoons(Player player)
    {
        var report = new GmUnlockReport();

        // 低 31 位全 1（不含符号位，理由见方法注释）。
        // ★ 不能写 `(1 << 31) - 1` —— 那在 checked 模式下编译期溢出（CS0220）。
        const int allRead = int.MaxValue;      // 0x7FFFFFFF = bit 0..30 全 1

        for (int taskId = CartoonMinTaskId; taskId <= CartoonMaxTaskId; taskId++)
        {
            int cur = player.GetTask(CartoonGroup, taskId);
            if ((cur & allRead) == allRead)
                continue;                      // 已全读，别记进报告（省无意义的 pf 1026）
            SetTask(player, report, CartoonGroup, taskId, cur | allRead);
        }

        Log.Info($"[gmunlock] 全剧情/CG 已读：group {CartoonGroup} 的 taskId " +
                 $"{CartoonMinTaskId}..{CartoonMaxTaskId} 各填低 {CartoonBitsPerTask} 位 → " +
                 $"变更 {report.ChangedTasks.Count} 条（可承载 " +
                 $"{(CartoonMaxTaskId - CartoonMinTaskId + 1) * CartoonBitsPerTask} 段剧情）—— " +
                 "往昔之忆/剧情回顾/CG 图鉴全开，终端剧情类触发可补发");
        return report;
    }

    /// <summary>把一组档位 id 当 bit 下标或进某个位标记任务值（bit 从 1 起，见 <see cref="ApplyMissions"/>）。</summary>
    private static void SetFlagBits(Player player, GmUnlockReport report, int taskId, IEnumerable<int> slots)
    {
        int value = player.GetTask(MissionTable.MissionGroup, taskId);
        foreach (int idx in slots)
        {
            if (idx is >= 0 and < 31)
                value |= 1 << idx;
        }
        SetTask(player, report, MissionTable.MissionGroup, taskId, value);
    }

    /// <summary>写一个任务值；值没变就不记进报告（避免推无意义的 pf 1026）。</summary>
    private static void SetTask(Player player, GmUnlockReport report, int group, int taskId, int value)
    {
        if (taskId < 0 || taskId > 0xFFFF)
        {
            // Player.ComposeTaskId 只保留低 16 位（Game.Player.GetTask 的 orr w19,w19,w20,lsl #16）
            Log.Warn($"[gmunlock] 任务值 taskId={taskId}（group {group}）超出 16 位，跳过");
            return;
        }
        if (player.GetTask(group, taskId) == value)
            return;
        player.SetTask(group, taskId, value);
        report.NoteTask(Player.ComposeTaskId(group, taskId), value);
    }
}
