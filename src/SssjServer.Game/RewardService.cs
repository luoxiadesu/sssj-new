using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;

namespace SssjServer.Game;

/// <summary>
/// 奖励来源。决定两件事：配置元组第 6 位怎么解释，以及回给客户端的展示标签。
/// </summary>
public enum RewardSource
{
    /// <summary>
    /// 首通奖励（Chapter.txt 的 <c>FirstAward</c>，表头写作 <c>G-D-P-L-N-Level</c>）。
    /// 第 6 位是「等级」，只对角色卡（G==1）有意义 —— 见 <see cref="RewardService"/> 的取证段。
    /// </summary>
    FirstAward,

    /// <summary>
    /// 固定奖励（<c>FixedAward</c>，表头 <c>G-D-P-L-N-Rate</c>）。第 6 位是万分比概率。
    /// </summary>
    FixedAward,

    /// <summary>
    /// 随机奖励（<c>RandomAward</c>，8 位：<c>G-D-P-L-N-Weight-0-0</c>）。
    /// 抽取由 <see cref="ChapterRandomAwardTable"/> 完成，进到本服务时已是「中签结果」，
    /// 第 6 位不再参与判定。
    /// </summary>
    RandomAward,
    ThiefDrop,

    /// <summary>其它来源（章节累计奖励等）：第 6 位忽略，按 100% 发放、无标签。</summary>
    Extra,
}

/// <summary>
/// 一条**实际发放**记录（不是配置，是发生过的事）。
/// <see cref="ToTuple"/> 就是客户端 <c>tbAwards</c> 里的一个元素。
/// </summary>
/// <param name="Genre">大类 G。</param>
/// <param name="Detail">细分 D。</param>
/// <param name="Particular">特化 P。</param>
/// <param name="Level">阶 L。</param>
/// <param name="Count">实际数量 N。</param>
/// <param name="Flag">客户端展示标签（tbAwards 第 6 位），取值见 <see cref="RewardService"/>。</param>
/// <param name="MoneyId">折算成的货币 id；0 表示不是货币。</param>
/// <param name="ItemGuid">落到背包的物品实例 guid；0 表示没有实例（货币）。</param>
/// <param name="Name">日志用名（中文名/多语言 key/四元组）。</param>
public sealed record GrantedReward(
    uint Genre,
    uint Detail,
    uint Particular,
    uint Level,
    uint Count,
    int Flag,
    int MoneyId,
    uint ItemGuid,
    string Name)
{
    /// <summary>是否货币类（已加进 <c>Player.Money</c>）。</summary>
    public bool IsMoney => MoneyId > 0;

    /// <summary>
    /// 转成客户端 <c>tbAwards</c> 的一个元素：<c>{G, D, P, L, N, Flag}</c>。
    ///
    /// 形状取证（全部在 <c>extracted/lua/UI_MissionSuccess.lua</c>，元素是**纯数组**、不是带字段的表）：
    /// <code>
    /// :1253  local rewards = self:RewardsSort(tbAwards);
    /// :1256  local tbAward = rewards[i];
    /// :1257  local itemInfo = GetItemBaseInfo(tbAward[1], tbAward[2], tbAward[3], tbAward[4]);  -- [1..4] = G,D,P,L
    /// :1300  txtNum.text = NumberToString( tbAward[5] );                                        -- [5]    = 数量
    /// :1311  self:SetItemFlag(item, tbAward[6]);                                                -- [6]    = 标签
    /// :1541  local one = self.tbAwards[i];
    /// :1542  local g, d, p, l = table.unpack(one);                                              -- 数组，可 unpack
    /// </code>
    /// 上游链路：<c>Chapter.lua:376 GameEnd(tbStar, tbAwards, ...)</c> →
    /// <c>Chapter.lua:387 UI.Open("UI_MissionSuccess", tbAwards, nMasterExp, nCardExp, nil, self.tbStarValue)</c>
    /// → <c>UI_MissionSuccess.lua:135 OnOpen(tbAwards, ...)</c>；
    /// 服务端下行字段名由 <c>extracted/lua/S2CCall.lua:104</c> 的注释写明：
    /// <c>tbParam = {nError, nState=1, nStar, tbExp, tbAwards = tbAwardItems}</c>。
    /// </summary>
    public int[] ToTuple() =>
        new[] { (int)Genre, (int)Detail, (int)Particular, (int)Level, (int)Count, Flag };
}

/// <summary>
/// 一次结算里所有实际发放的汇总：给调用方「发了什么」＋「要推哪些 Ntf」。
/// 可以被多次 <see cref="RewardService.Grant"/> 累积（首通 + 固定 + 随机合并成一份 tbAwards）。
/// </summary>
public sealed class RewardGrantResult
{
    private readonly List<GrantedReward> _rewards = new();
    private readonly List<int> _money = new();
    private readonly List<Item> _items = new();
    private readonly List<KeyValuePair<uint, int>> _tasks = new();

    /// <summary>按发放顺序的实际发放清单。</summary>
    public IReadOnlyList<GrantedReward> Rewards => _rewards;

    /// <summary>本次变动的货币 id（去重、保序）—— 每个都要推一条 pf 1032 MoneyUpdateNtf。</summary>
    public IReadOnlyList<int> ChangedMoney => _money;

    /// <summary>
    /// 本次变动的背包实例（去重、保序，<see cref="Item.Count"/> 已是**变动后的绝对值**）
    /// —— 打进一条 pf 1031 ItemUpdateNtf。
    /// 绝对值语义取证：客户端 <c>Game.EquipManager.AddItem</c>（RVA <c>0x1D9A82C</c>）对已存在的 guid
    /// 走 <c>BaseItem.set_Count(itemInfo.count)</c>（<c>0x1D9AA50</c>）—— 是覆盖而不是累加；
    /// <c>count == 0</c> 则从字典里 <c>Remove(guid)</c>（<c>0x1D9A980</c>），即「删除该实例」。
    /// </summary>
    public IReadOnlyList<Item> ChangedItems => _items;

    /// <summary>什么都没发（配置为空 / 概率全没中）。</summary>
    public bool IsEmpty => _rewards.Count == 0;

    /// <summary>
    /// 本次发放**顺带改到的任务值**（组合键 → 值），要推一条 pf 1026。
    ///
    /// ★ 来源包括角色卡/皮肤道具解锁与服装计数同步；皮肤道具入包时把 group 4 的
    /// <c>SuitValue</c> 置 1（已获得未穿过）—— 见
    /// <see cref="RewardService.NoteSuitUnlock"/>。
    /// 不推它的话玩家买到皮肤后**要重登才能在换装界面看到那套衣服**
    /// （列表是按 <c>SuitValue &gt; 0</c> 过滤的，`UI_GirlClothes.lua:847`）。
    /// </summary>
    public IReadOnlyList<KeyValuePair<uint, int>> ChangedTasks => _tasks;

    private readonly HashSet<uint> _riki = new();
    public IReadOnlyCollection<uint> ChangedRiki => _riki;
    internal void NoteRiki(uint id) => _riki.Add(id);

    /// <summary>客户端要的 <c>tbAwards</c>：数组的数组。</summary>
    public int[][] ToAwardTuples()
    {
        var arr = new int[_rewards.Count][];
        for (int i = 0; i < _rewards.Count; i++)
            arr[i] = _rewards[i].ToTuple();
        return arr;
    }

    /// <summary>日志用的一行摘要。</summary>
    public string Describe()
        => _rewards.Count == 0
            ? "（无）"
            : string.Join(", ", _rewards.Select(r =>
                $"{r.Name}x{r.Count}{(r.IsMoney ? $"→货币{r.MoneyId}" : $"#{r.ItemGuid}")}[flag{r.Flag}]"));

    internal void AddReward(GrantedReward reward) => _rewards.Add(reward);

    private readonly List<Girl> _girls = new();
    public IReadOnlyList<Girl> ChangedGirls => _girls;
    internal void NoteGirl(Girl girl)
    {
        int index = _girls.FindIndex(g => g.Girlid == girl.Girlid);
        if (index >= 0) _girls[index] = girl;
        else _girls.Add(girl);
    }

    internal void NoteTask(uint composedKey, int value)
    {
        int idx = _tasks.FindIndex(kv => kv.Key == composedKey);
        if (idx >= 0)
            _tasks[idx] = new KeyValuePair<uint, int>(composedKey, value);
        else
            _tasks.Add(new KeyValuePair<uint, int>(composedKey, value));
    }

    internal void NoteMoney(int moneyId)
    {
        if (!_money.Contains(moneyId))
            _money.Add(moneyId);
    }

    internal void NoteItem(Item item)
    {
        if (!_items.Contains(item))
            _items.Add(item);
    }

    private readonly List<HouseFurniture> _furniture = [];
    internal IReadOnlyList<HouseFurniture> ChangedFurniture => _furniture;
    internal void NoteFurniture(HouseFurniture item) => _furniture.Add(item);

    internal void Absorb(RewardGrantResult other)
    {
        _rewards.AddRange(other.Rewards);
        foreach (int id in other.ChangedMoney) NoteMoney(id);
        foreach (var item in other.ChangedItems) NoteItem(item);
        foreach (var girl in other.ChangedGirls) NoteGirl(girl);
        foreach (var task in other.ChangedTasks) NoteTask(task.Key, task.Value);
        _furniture.AddRange(other.ChangedFurniture);
    }
}

/// <summary>
/// 奖励发放服务：把配置里的 <c>(G,D,P,L,N[,第6位])</c> 元组真正发到 <see cref="Player"/> 身上，
/// 并产出「实际发放清单」（供 <c>tbAwards</c> 与增量 Ntf 使用）。
///
/// ---- 元组第 6 位的结论（取证）----
/// <c>extracted/setting/Chapter.txt</c> 第 2 行（中文列名）写着：
/// <code>
/// 第21列 关卡首次通关奖励(G-D-P-L-N-Level)
/// 第22列 固定奖励(G-D-P-L-N-Rate)
/// </code>
/// 全表 495 行统计（`Chapter.txt` 实测）：
/// <list type="bullet">
///   <item><b>FixedAward</b>：324 个元组**全是 6 位**，第 6 位**恒为 10000**。
///         配合 <c>Rate</c> 列名 → 万分比概率，10000 = 100%。因此现网数据里固定奖励必掉，
///         但判定逻辑照实现（<see cref="RateBase"/>），以便其它表/后续配置复用。</item>
///   <item><b>FirstAward</b>：1471 个元组里 1460 个只有 5 位、11 个有 6 位；
///         而**这 11 个恰好就是全部 G==1（角色卡）的元组**，第 6 位取值
///         5/5/10/10/13/14/14/15/19/23/25，随章节推进单调变大
///         （1-5→5, 1-6→5, 2-4→10, 2-6→13, 3-1→14, 3-2→15, 3-6→19, 4-5→23, 4-6→25）。
///         结论：<b>第 6 位是「赠送的角色卡等级」</b>，与列名 <c>Level</c> 吻合
///         （四元组里的 L 是卡的「阶」，另一回事）。所以只在 G==1 时用它设
///         <see cref="Girl.Level"/>；其它 genre 出现第 6 位时忽略并打 DEBUG。</item>
/// </list>
///
/// ---- 展示标签（tbAwards 第 6 位）----
/// 客户端把它当「掉落类型角标」用，<c>UI_MissionSuccess.lua:1303</c> 留着完整注释：
/// <c>6= nil不显示 0首通 1额外 2活动 3加成 4评级 5星级 6日常 7随机</c>，
/// 另见 <c>UI_LevelReward.lua:26</c> <c>tbReward_type = {First=0, Random=1, Fixed=2, Add=3}</c>
/// （那是战前预览面板的分组值，与结算面板不同源）。结算面板的实际行为：
/// <code>
/// UI_MissionSuccess.lua:1267  if (not tbAward[6] or tbAward[6] == 100) then IsNormal = true;
/// UI_MissionSuccess.lua:1269  elseif(tbAward[6] == 0) then IsNormal = true;   -- 首通：普通格 + 角标
/// UI_MissionSuccess.lua:1271  else IsNormal = false;                          -- 其它：进「追加奖励」格
/// UI_MissionSuccess.lua:1364  RewardsSort: flag ~= 100 and flag ~= 7 的排前面
/// Lib.lua:1426                SetItemIconFlagType: not nType or nType == 100 → 隐藏角标
/// LevelManager.lua:611        服务端下发的其它掉落 i 缺省就是 100
/// </code>
/// 所以本实现取：首通 = <see cref="FlagFirstClear"/>(0)、固定 = <see cref="FlagNone"/>(100)、
/// 随机 = <see cref="FlagRandom"/>(7)。角标文案走 <c>UIText("item.levelRewardType"..n)</c>，
/// 而 <c>Game.Localization.Get</c>（RVA <c>0x1C84E38</c>）查不到 key 时返回 <b>""</b>（不是 nil），
/// 所以任何取值都不会让客户端抛异常。
///
/// ---- 背包叠加 vs 新建实例 ----
/// <c>ItemInfo.count</c> 与 <c>guid</c> 的关系取自 <c>Game.EquipManager</c>：
/// <list type="bullet">
///   <item><c>OnUpdateItem</c>（RVA <c>0x1D9D72C</c>）按 <c>genre</c> 分派：
///         1→<c>AddCharacterCard</c>、2→<c>AddWeapon</c>、4→<c>AddOriginal</c>、其余→<c>AddItem</c>；
///         对应五个**以 guid 为键**的字典 <c>_characterCards/_weapons/_runes/_originals/_items</c>
///         （dump.cs:519570..519576）。</item>
///   <item><c>AddItem</c>（RVA <c>0x1D9A82C</c>）：<c>TryGetValue(guid)</c> 命中就
///         <c>set_Guid/set_Count/set_CreateTime</c> 覆盖，未命中才新建；
///         <c>count==0</c> → <c>Remove(guid)</c>。即 <b>guid 是主键、count 是绝对值</b>。</item>
///   <item><c>GetItemCount(g,d,p,l)</c>（RVA <c>0x1D9B840</c>）遍历字典、按 G/D/P/L 匹配并**累加** count，
///         所以同一四元组拆成多个实例也能正确显示总数。</item>
/// </list>
/// 据此：<see cref="InstanceGenreMax"/> 及以下的 genre（Card/Weapon/Rune/Original，
/// <c>emGenreType</c> dump.cs:372492）每份一个 guid（它们要独立强化/上阵，绝不能叠）；
/// genre ≥ 5 的消耗品/材料/代币按四元组叠加到同一 guid 上，并推**叠加后的绝对值**。
/// （现网 Chapter.txt 里 genre ≤ 4 的奖励元组 N 恒为 1，实测统计。）
/// </summary>
public sealed class RewardService
{
    /// <summary>概率基数：万分比。<c>10000</c> = 100%（Chapter.txt 的 <c>Rate</c> 列口径）。</summary>
    public const int RateBase = 10000;

    /// <summary>tbAwards 第 6 位 —— 首通（客户端画「首通」角标，仍在普通格）。</summary>
    public const int FlagFirstClear = 0;

    /// <summary>tbAwards 第 6 位 —— 随机掉落（进「追加奖励」格）。</summary>
    public const int FlagRandom = 7;

    /// <summary>tbAwards 第 6 位 —— 无角标（普通格），也是原服其它掉落的缺省值。</summary>
    public const int FlagNone = 100;

    /// <summary>
    /// 「一份一个 guid」的 genre 上界：<c>emGenreType {Card=1, Weapon=2, Rune=3, Original=4, Item=5}</c>
    /// （dump.cs:372492）。&lt;= 4 的按实例发，&gt;= 5 的按四元组叠加。
    /// </summary>
    public const uint InstanceGenreMax = 4;

    /// <summary>角色卡的 genre（<c>emGenreType.Card</c>）。</summary>
    public const uint GenreCard = 1;

    /// <summary>单个元组最多创建多少个实例（防御配置异常导致的 guid 爆炸）。</summary>
    public const int InstanceCap = 100;

    /// <summary>
    /// 「礼包箱」道具的 ClassName（<c>ItemList.txt</c>），其 <c>ExtParam1</c> 是
    /// <c>ItemBoxInfo.txt</c> 的 <c>boxId</c> —— 取证 <c>IBLogic.lua:1196</c>
    /// <c>if not pItem or pItem.ClassName ~= "IBGiftAuto" then return end</c>。
    /// </summary>
    public const string GiftBoxClassName = "IBGiftAuto";

    /// <summary>礼包展开的最大递归深度（防御「箱子里装自己」这类配置事故）。</summary>
    public const int BoxExpandMaxDepth = 4;

    private readonly ItemBaseTable _templates;
    private readonly Random _rng;

    /// <summary>
    /// 礼包箱表。**可选**：为 <c>null</c>（或 <see cref="ItemBoxTable.Empty"/>）时
    /// 礼包道具按普通道具原样进背包（保持第九轮以前的行为）。
    ///
    /// ★★★ 设成非空之后，<see cref="Grant"/> 会把 <c>ClassName == IBGiftAuto</c> 的道具
    /// **就地展开成箱内的 <c>Fix</c> 物品**，而不是把礼包本体塞进背包 ——
    /// 这是「十面妆造买了皮肤却什么都没得到」的修法（见 <see cref="ItemBoxTable"/> 类注释 §0）。
    /// </summary>
    public ItemBoxTable? Boxes { get; set; }

    /// <summary>
    /// Spine 皮肤表。**可选**：非空时，发放 genre 19 皮肤道具会顺带把
    /// 任务值 group 4 的 <c>SuitValue</c> 置 1（已获得未穿过），
    /// 变更累积到 <see cref="RewardGrantResult.ChangedTasks"/> 里等调用方推 pf 1026。
    ///
    /// ★★★ 不挂它的后果：玩家买到皮肤后**换装列表里看不到那套衣服**
    /// （列表按 <c>SuitValue &gt; 0</c> 过滤，`UI_GirlClothes.lua:847`）——
    /// 与 <see cref="Boxes"/> 是同一条链上的两环，缺任何一环都表现为「钱花了、什么都没得到」。
    /// </summary>
    public GirlDressTable? Suits { get; set; }
    public CharacterCardTable? CharacterCards { get; set; }
    public RikiCardTable? RikiCards { get; set; }
    public MissionTable? Missions { get; set; }
    public HouseTable? Furniture { get; set; }

    internal bool IsCafeFurniture(uint g, uint d, uint p, uint l)
        => _templates.Get(g, d, p, l)?.ClassName == "CafeItemAuto";

    // Purchase limits are checked before charging. Scripted rewards use the same
    // conversion but do not invent a purchase-limit rule for non-shop sources.
    internal bool CanReceiveFurniture(Player player, uint g, uint d, uint p, uint l, int count,
        bool purchaseLimits = true)
    {
        if (!IsCafeFurniture(g, d, p, l)) return true;
        var template = _templates.Get(g, d, p, l)!;
        if (Furniture is null || !int.TryParse(template.ExtParam1, out int id)
            || !Furniture.Items.TryGetValue(id, out var row) || row.IsSystem
            || Furniture.IsPoolBlockedTemplate(id) || count <= 0) return false;
        var state = HouseFeature.ReadOrCreate(player, Furniture);
        if ((ulong)state.NextObjId + (uint)count > uint.MaxValue) return false;
        return !purchaseLimits || (long)state.Furniture.Count + count <= 2000
            && (long)state.Furniture.Count(f => f.TemplateId == id) + count <= row.OwnMax;
    }

    /// <param name="templates">物品模板表（判定「是不是货币」）。缺表时传 <see cref="ItemBaseTable.Empty"/> 也能跑。</param>
    /// <param name="rng">随机源。单测传固定种子可完全确定化。</param>
    public RewardService(ItemBaseTable templates, Random? rng = null)
    {
        _templates = templates ?? ItemBaseTable.Empty;
        _rng = rng ?? Random.Shared;
    }

    /// <summary>物品模板表（调用方偶尔要查名字/图标）。</summary>
    public ItemBaseTable Templates => _templates;

    /// <summary>
    /// 把一批配置元组发给玩家。
    /// </summary>
    /// <param name="player">收奖玩家。函数会直接改 <c>Money</c>/<c>Items</c>/<c>Girls</c>（不负责存盘）。</param>
    /// <param name="awards">配置元组，每项至少 5 位 <c>(G,D,P,L,N)</c>，第 6 位含义见 <paramref name="source"/>。</param>
    /// <param name="source">奖励来源，决定第 6 位语义与展示标签。</param>
    /// <param name="into">要累积到的结果；为 null 时新建一份。</param>
    /// <returns>实际发放清单（与 <paramref name="into"/> 同一个对象）。</returns>
    public RewardGrantResult Grant(
        Player player,
        IEnumerable<int[]>? awards,
        RewardSource source,
        RewardGrantResult? into = null)
    {
        var result = into ?? new RewardGrantResult();
        if (awards is null)
            return result;

        int flag = FlagFor(source);

        foreach (var tuple in awards)
        {
            if (tuple is null || tuple.Length < 5)
            {
                Log.Warn($"[reward] 奖励元组长度不足 5（{(tuple is null ? "null" : string.Join('-', tuple))}），跳过");
                continue;
            }

            if (tuple[0] < 0 || tuple[1] < 0 || tuple[2] < 0 || tuple[3] < 0)
            {
                Log.Warn($"[reward] 奖励元组四元组含负值（{string.Join('-', tuple)}），跳过");
                continue;
            }

            uint g = (uint)tuple[0];
            uint d = (uint)tuple[1];
            uint p = (uint)tuple[2];
            uint l = (uint)tuple[3];
            int count = tuple[4];
            int? sixth = tuple.Length >= 6 ? tuple[5] : null;

            if (count <= 0)
            {
                // 配置里 N<=0 的元组只用于「占位/展示」，不发
                Log.Debug($"[reward] {_templates.NameOf(g, d, p, l)} 数量为 {count}，不发放");
                continue;
            }

            // FixedAward 的第 6 位是万分比概率；其它来源不做概率判定
            if (source == RewardSource.FixedAward)
            {
                int rate = sixth ?? RateBase;
                if (!Roll(rate))
                {
                    Log.Debug($"[reward] {_templates.NameOf(g, d, p, l)} 概率未中（rate={rate}/{RateBase}）");
                    continue;
                }
            }

            // FirstAward 的第 6 位是角色卡等级，只对 G==1 有意义
            int? cardLevel = null;
            if (source == RewardSource.FirstAward && sixth.HasValue)
            {
                if (g == GenreCard && sixth.Value > 0)
                    cardLevel = sixth.Value;
                else
                    Log.Debug($"[reward] FirstAward {_templates.NameOf(g, d, p, l)} 带第6位={sixth.Value} " +
                              "但不是角色卡（G!=1），按「Level」语义忽略");
            }

            GrantOne(player, result, g, d, p, l, count, flag, cardLevel);
        }

        return result;
    }

    /// <summary>
    /// 礼包箱展开：若 <paramref name="g"/>..<paramref name="l"/> 是
    /// <see cref="GiftBoxClassName"/> 类道具且 <see cref="Boxes"/> 里能查到它的箱子，
    /// 就把箱内 <c>Fix</c> 项按 <paramref name="count"/> 份发出去并返回 <c>true</c>
    /// （礼包本体**不进背包**）；否则返回 <c>false</c> 走普通发放路径。
    ///
    /// <para><b>为什么这一步不可少</b></para>
    /// 见 <see cref="Config.ItemBoxTable"/> 类注释 §0：十面妆造卖的 genre 13/14 是
    /// **礼包**，皮肤本体（genre 19）在箱子里。不展开就是「花了钱、什么都没得到」。
    ///
    /// <para><b>三条保守设计</b></para>
    /// <list type="number">
    ///   <item><b>只发 <c>Fix</c></b>：<c>Random</c>/<c>Prob</c> 需要与客户端一致的随机源，
    ///     否则「弹窗显示的和实际拿到的不一样」。现网妆造商店的箱子全是 <c>Fix</c>
    ///     ⇒ 遇到非 Fix 项记一条 warning（比静默发错东西安全）。</item>
    ///   <item><b>箱子查不到就不展开</b>：宁可让玩家背包里多一个礼包道具（可见、可排查），
    ///     也不要静默吞掉。</item>
    ///   <item><b>限深 <see cref="BoxExpandMaxDepth"/></b>：防「箱子里装同一个箱子」死循环。</item>
    /// </list>
    /// </summary>
    private bool TryExpandGiftBox(Player player, RewardGrantResult result,
                                  uint g, uint d, uint p, uint l, int count, int flag, int depth)
    {
        var boxes = Boxes;
        if (boxes is null || boxes.BoxCount == 0)
            return false;

        var tpl = _templates.Get(g, d, p, l);
        if (tpl is null || !string.Equals(tpl.ClassName, GiftBoxClassName, StringComparison.Ordinal))
            return false;
        if (!int.TryParse(tpl.ExtParam1, out int boxId) || boxId <= 0 || !boxes.Has(boxId))
        {
            Log.Warn($"[reward] {_templates.NameOf(g, d, p, l)} 是 {GiftBoxClassName} 但 "
                   + $"ExtParam1=\"{tpl.ExtParam1}\" 查不到箱子 ⇒ 按普通道具发放"
                   + "（玩家会看到一个用不掉的礼包，便于排查）");
            return false;
        }

        if (depth >= BoxExpandMaxDepth)
        {
            Log.Warn($"[reward] 礼包展开深度超过 {BoxExpandMaxDepth}（box {boxId}），停止展开");
            return false;
        }

        var entries = boxes.Get(boxId);
        var fixedItems = entries.Where(e => e.ObtainType == Config.ItemBoxEntry.TypeFix).ToList();
        var others = entries.Where(e => e.ObtainType != Config.ItemBoxEntry.TypeFix).ToList();

        if (fixedItems.Count == 0)
        {
            Log.Warn($"[reward] 礼包 box {boxId} 里没有 Fix 项"
                   + $"（{string.Join('/', others.Select(o => o.ObtainType).Distinct())} 尚未实现）"
                   + " ⇒ 按普通道具发放");
            return false;
        }

        foreach (var e in fixedItems)
        {
            // ★ 礼包买了 N 份 ⇒ 箱内每项也乘 N
            int n = e.Gdpl[4] * Math.Max(1, count);
            GrantOne(player, result, (uint)e.Gdpl[0], (uint)e.Gdpl[1], (uint)e.Gdpl[2], (uint)e.Gdpl[3],
                     n, flag, cardLevel: null, depth: depth + 1);
        }

        if (others.Count > 0)
        {
            Log.Warn($"[reward] 礼包 box {boxId} 还有 {others.Count} 个非 Fix 项未发放"
                   + $"（{string.Join('、', others.Select(o => $"{o.ObtainType}:{string.Join('-', o.Gdpl)}"))}）"
                   + " —— 需要与客户端一致的随机源，见 RewardService.TryExpandGiftBox 注释");
        }

        Log.Info($"[reward] 礼包 {_templates.NameOf(g, d, p, l)} ×{count} 展开 box {boxId}："
               + string.Join('、', fixedItems.Select(e => $"{string.Join('-', e.Gdpl)}")));
        return true;
    }

    /// <summary>发一个四元组（已完成概率/语义判定）。</summary>
    private void GrantOne(Player player, RewardGrantResult result,
                          uint g, uint d, uint p, uint l, int count, int flag, int? cardLevel,
                          int depth = 0)
    {
        string name = _templates.NameOf(g, d, p, l);

        if (IsCafeFurniture(g, d, p, l))
        {
            if (!CanReceiveFurniture(player, g, d, p, l, count, purchaseLimits: false))
            {
                Log.Warn($"[reward] Invalid CafeItemAuto {g}-{d}-{p}-{l}; not granted");
                return;
            }
            int id = int.Parse(_templates.Get(g, d, p, l)!.ExtParam1);
            var state = HouseFeature.ReadOrCreate(player, Furniture!);
            var items = state.Furniture.ToList();
            uint next = state.NextObjId;
            for (int n = 0; n < count; n++)
            {
                // Room=0 is storage. A purchase must never activate a layout or
                // add objects with parent=0 to an active scene room.
                var item = new HouseFurniture(next++, id, 0, 0, 0, 0, 0,
                    Furniture!.Items[id].BizPointCount, 0);
                items.Add(item);
                result.NoteFurniture(item);
            }
            HouseFeature.WriteState(player, new HouseState { Furniture = items, NextObjId = next });
            result.AddReward(new GrantedReward(g, d, p, l, (uint)count, flag, 0, 0, name));
            return;
        }

        // ⓪ ★★★ 礼包箱（ClassName = IBGiftAuto）：**就地展开**成箱内的 Fix 物品，
        //    礼包本体不进背包。不展开的后果见 ItemBoxTable 类注释 §0
        //    （十面妆造买皮肤只拿到一个用不掉的礼包道具、换装界面里皮肤仍是锁的）。
        if (TryExpandGiftBox(player, result, g, d, p, l, count, flag, depth))
            return;

        // ① 货币类道具：入包即折算成货币（客户端不会在背包里看到它）
        if (_templates.TryGetMoneyId(g, d, p, l, out int moneyId))
        {
            player.AddMoney(moneyId, count);
            if (g == 15 && d == 127 && p == 1 && l == 1 && Missions is not null)
                Tower21Missions.TokensGained(player, Missions, count, result);
            result.NoteMoney(moneyId);
            result.AddReward(new GrantedReward(g, d, p, l, (uint)count, flag, moneyId, 0, name));
            return;
        }

        // ② Card/Weapon/Rune/Original：每份一个实例 guid，绝不叠加
        if (g <= InstanceGenreMax)
        {
            int instances = count;
            if (instances > InstanceCap)
            {
                Log.Warn($"[reward] {name} 要求 {count} 个独立实例，超过上限 {InstanceCap}，只发 {InstanceCap} 个");
                instances = InstanceCap;
            }

            uint lastGuid = 0;
            for (int i = 0; i < instances; i++)
            {
                var created = player.AddItem(g, d, p, l, count: 1);
                lastGuid = created.Guid;
                result.NoteItem(created);
            }

            if (g == GenreCard)
            {
                if (CharacterCards is not null && Handlers.CardCollection.MarkOwned(player, CharacterCards, g, d, p, l) is { } owned)
                    result.NoteTask(owned.Key, owned.Value);
                if (CharacterCards?.Get(g, d, p, l) is not null && RikiCards is not null)
                    foreach (uint id in RikiCards.MarkOwned(player, g, d, p, l))
                    {
                        result.NoteRiki(id);
                        result.NoteTask(Player.ComposeTaskId(53, (int)id), 1);
                    }
                var before = player.GetGirl(d);
                uint oldLevel = before?.Level ?? 0;
                EnsureGirl(player, d, cardLevel);
                var girl = player.GetGirl(d)!;
                var card = CharacterCards?.Get(g, d, p, l);
                if (before is null && card is not null && card.ModelId > 0)
                    girl.Modelid = (uint)card.ModelId;
                if (before is null || oldLevel != girl.Level) result.NoteGirl(girl);
                if (Suits is not null && CharacterCards is not null)
                    foreach (var change in Handlers.GirlDressFeature.SyncRewardSuits(Suits, CharacterCards, player, d))
                        result.NoteTask(change.Key, change.Value);
            }

            // 展示上仍是「一种奖励 x N」，与客户端 txtNum.text = tbAward[5] 的用法一致
            result.AddReward(new GrantedReward(g, d, p, l, (uint)instances, flag, 0, lastGuid, name));
            return;
        }

        // ③ 消耗品/材料/代币：按四元组叠加到同一实例，推「叠加后」的绝对 count
        var stack = player.Items.FirstOrDefault(it => it.Genre == g && it.Detail == d
                                                   && it.Particular == p && it.Level == l);
        if (stack is null)
            stack = player.AddItem(g, d, p, l, (uint)count);
        else
            stack.Count += (uint)count;

        result.NoteItem(stack);
        result.AddReward(new GrantedReward(g, d, p, l, (uint)count, flag, 0, stack.Guid, name));

        // ④ ★★★ 皮肤道具（genre 19 SuitItem）：顺带把 group 4 的 SuitValue 置 1。
        //    不做这一步，玩家买到皮肤后**换装列表里根本看不到那套衣服**
        //    （列表按 SuitValue > 0 过滤，UI_GirlClothes.lua:847）⇒ 表现为「钱花了、没东西」。
        NoteSuitUnlock(player, result, g, d, p, l);
    }

    /// <summary>
    /// 皮肤道具入包 ⇒ 解锁对应服饰（任务值 group 4 的 <c>SuitValue</c> 置 1 = 已获得未穿过）。
    ///
    /// ★ 需要 <see cref="Suits"/> 已挂上；没挂则什么都不做（保持旧行为）。
    /// ★ 已获得（1）或已穿过（2）的不动 ⇒ 幂等。
    /// ★ 变更累积进 <see cref="RewardGrantResult.ChangedTasks"/>，由调用方推 pf 1026。
    /// </summary>
    private void NoteSuitUnlock(Player player, RewardGrantResult result,
                                uint g, uint d, uint p, uint l)
    {
        var suits = Suits;
        if (suits is null || suits.Count == 0 || g != Config.GirlDressTable.SuitItemGenre)
            return;

        var suit = suits.SuitForItem(g, d, p, l);
        if (suit is null)
        {
            Log.Warn($"[reward] 皮肤道具 {g}-{d}-{p}-{l} 不在 SpineSuit.txt 里"
                   + $"（本地 {suits.Count} 条）⇒ 无法解锁对应服饰，玩家在换装界面看不到它");
            return;
        }
        if (player.GetGirl(suit.GirlId) is null)
        {
            Log.Warn($"[reward] 皮肤 {suit} 对应的少女 {suit.GirlId} 未持有 ⇒ 暂不解锁服饰"
                   + "（拿到少女后登录时会由 GirlDressFeature.SyncOwnedSuits 补上）");
            return;
        }

        if (CharacterCards is not null)
        {
            foreach (var change in Handlers.GirlDressFeature.SyncRewardSuits(suits, CharacterCards, player, suit.GirlId))
                result.NoteTask(change.Key, change.Value);
            return;
        }

        int group = Handlers.GirlDressFeature.SuitGroupOf(suit.GirlId);
        int taskId = Handlers.GirlDressFeature.SuitTaskId(suit.GirlId, suit.ModelId);
        if (player.GetTask(group, taskId) != Handlers.GirlDressFeature.SuitNone)
            return;     // 已获得/已穿过：幂等

        player.SetTask(group, taskId, Handlers.GirlDressFeature.SuitNew);
        result.NoteTask(Player.ComposeTaskId(group, taskId), Handlers.GirlDressFeature.SuitNew);
        Log.Info($"[reward] 皮肤道具 {g}-{d}-{p}-{l} 解锁服饰：{suit}"
               + $"（group {group} taskId {taskId} → 1 已获得未穿过）");
    }

    /// <summary>
    /// 赠卡时补一条 <see cref="Girl"/>（少女数据），与 <see cref="StarterLoadout.Grant"/> 的落法一致：
    /// <c>Girlid = Detail</c>、<c>Modelid = Detail</c>、心情/体力取正常非零值、<c>Flag = 1</c>。
    /// 已有则只在配置给了更高等级时抬一次等级（同一张卡重复赠送不重复建条目）。
    /// </summary>
    private static void EnsureGirl(Player player, uint girlId, int? cardLevel)
    {
        uint level = cardLevel.HasValue && cardLevel.Value > 0 ? (uint)cardLevel.Value : 1u;

        var girl = player.GetGirl(girlId);
        if (girl is null)
        {
            player.Girls.Add(new Girl
            {
                Girlid = girlId,
                Level = level,
                Modelid = girlId,
                Moodvalue = 100,
                Vigor = 100,
                Flag = 1,
            });
            return;
        }

        if (girl.Level < level)
            girl.Level = level;
    }

    /// <summary>万分比判定：<paramref name="rate"/> &gt;= <see cref="RateBase"/> 恒中，&lt;= 0 恒不中。</summary>
    public bool Roll(int rate)
    {
        if (rate >= RateBase)
            return true;
        if (rate <= 0)
            return false;
        return _rng.Next(RateBase) < rate;
    }

    /// <summary>
    /// 按权重抽随机奖励。
    ///
    /// 取证：<c>Chapter.txt</c> 的 <c>RandomAward</c> 每项 8 位 <c>G-D-P-L-N-W-0-0</c>（第 7/8 位全表恒为 0），
    /// 而**同一行内 W 之和恒 ≤ 10000**（实测直方图：10000 / 9998 / 9996 / 9300），
    /// 例如 1-1：<c>120 + 1976*5 = 10000</c>。配合 <c>RandDropNum</c>（随机奖励掉落次数，
    /// 全表只有 0 或 1）→ 语义是「掷 <c>RandDropNum</c> 次，每次在 [0,10000) 里取一个数，
    /// 落进哪段权重就掉那件；落进 10000 与权重和之间的空档就什么都不掉」。
    /// 空档正好解释了为什么有些行的权重和不足 10000。
    /// 每次抽取相互独立（可重复）；现网 <c>RandDropNum</c> 最大为 1，取不到差异。
    /// </summary>
    /// <param name="pool">随机奖励池（每项至少 6 位，第 6 位是万分权重）。</param>
    /// <param name="drawCount">抽取次数（<c>RandDropNum</c>）。</param>
    public List<int[]> DrawRandom(IReadOnlyList<int[]>? pool, int drawCount)
    {
        var picked = new List<int[]>();
        if (pool is null || pool.Count == 0 || drawCount <= 0)
            return picked;

        for (int n = 0; n < drawCount; n++)
        {
            int roll = _rng.Next(RateBase);
            int cursor = 0;
            foreach (var entry in pool)
            {
                if (entry is null || entry.Length < 6)
                    continue;
                int weight = entry[5];
                if (weight <= 0)
                    continue;
                cursor += weight;
                if (roll < cursor)
                {
                    picked.Add(entry);
                    break;
                }
            }
            // roll >= 全部权重之和 → 本次空掉落（配置留的空档），什么都不加
        }
        return picked;
    }

    /// <summary>来源 → 客户端展示标签。</summary>
    public static int FlagFor(RewardSource source) => source switch
    {
        RewardSource.FirstAward => FlagFirstClear,
        RewardSource.RandomAward => FlagRandom,
        RewardSource.ThiefDrop => 10,
        _ => FlagNone,
    };
}

/// <summary>
/// <c>Chapter.txt</c> 的随机奖励两列：<c>RandDropNum</c>（随机奖励掉落次数）与
/// <c>RandomAward</c>（随机奖励池，8 位元组 <c>G-D-P-L-N-Weight-0-0</c>）。
///
/// 为什么单独再读一遍 Chapter.txt：<see cref="ChapterTable"/> 只解析了
/// <c>FirstAward</c>/<c>FixedAward</c> 两列，而按本次任务的分工约定
/// <c>Config/ChapterTable.cs</c> 由别人维护、我不能改它。所以这里用公开的
/// <see cref="SettingTable.Load"/> 把同一张表再读一遍，只取这两列，
/// 键仍用 <see cref="MapList.MapId"/> 口径，保证和 <see cref="ChapterTable"/> 对得上。
/// 等这两列并进 ChapterTable 之后，本类可以直接删掉。
/// </summary>
public sealed class ChapterRandomAwardTable
{
    /// <summary>一关的随机奖励配置。</summary>
    /// <param name="DrawCount">抽取次数（<c>RandDropNum</c>）。</param>
    /// <param name="Pool">奖励池（每项 8 位，第 6 位是万分权重）。</param>
    public sealed record Entry(int DrawCount, List<int[]> Pool);

    private readonly Dictionary<uint, Entry> _byMapId = new();

    private ChapterRandomAwardTable() { }

    public int Count => _byMapId.Count;

    /// <summary>空表（缺表降级：不发随机奖励）。</summary>
    public static ChapterRandomAwardTable Empty { get; } = new();

    private static readonly Lazy<ChapterRandomAwardTable> _shared =
        new(() => Load(ItemBaseTable.ResolveSettingDirectory()), isThreadSafe: true);

    /// <summary>进程级共享表（懒加载；目录解析复用 <see cref="ItemBaseTable.ResolveSettingDirectory"/>）。</summary>
    public static ChapterRandomAwardTable Shared => _shared.Value;

    /// <summary>按 (chapter,index,diff) 查；没有随机奖励返回 null。</summary>
    public Entry? Get(int chapter, int index, int diff)
        => GetByMapId(MapList.MapId(chapter, index, diff));

    /// <summary>按 <see cref="MapList"/> 口径的 mapId 查；没有随机奖励返回 null。</summary>
    public Entry? GetByMapId(uint mapId)
        => _byMapId.TryGetValue(mapId, out var e) ? e : null;

    /// <summary>从 setting 目录加载；缺表返回空表并打 WARN。</summary>
    public static ChapterRandomAwardTable Load(string settingDir)
    {
        var table = new ChapterRandomAwardTable();
        var raw = SettingTable.Load(Path.Combine(settingDir, "Chapter.txt"));
        if (raw is null)
        {
            Log.Warn("[setting] Chapter.txt 缺失：关卡随机奖励（RandomAward）降级为不发放");
            return table;
        }

        foreach (var row in raw.Rows)
        {
            int chapter = raw.Int(row, "Chapter", -1);
            int index = raw.Int(row, "Index", -1);
            int diff = raw.Int(row, "Difficult", -1);
            if (chapter < 0 || index < 0 || diff < 0)
                continue;

            int draw = raw.Int(row, "RandDropNum");
            var pool = ChapterTable.ParseTuples(raw.Str(row, "RandomAward"));
            if (draw <= 0 || pool.Count == 0)
                continue;

            table._byMapId[MapList.MapId(chapter, index, diff)] = new Entry(draw, pool);
        }

        Log.Info($"[setting] Chapter.txt 随机奖励列载入 {table._byMapId.Count} 关");
        return table;
    }
}
