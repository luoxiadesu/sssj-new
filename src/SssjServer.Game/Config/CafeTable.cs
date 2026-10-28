using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 一种宠物罐头（<c>NPet/PetFoodInfo.txt</c> 的一行）。
///
/// 取证 <c>NCafePetLogic:LoadPetFoodCfg</c>（<c>NCafePetLogicCfg.lua:31..54</c>）。
/// 现网只有 <b>2 行</b>（Id=1 低星罐头 / Id=2 高星罐头）。
/// </summary>
public sealed class PetFoodRow
{
    /// <summary>罐头配置 id（<c>Id</c> 列）。也是 <c>ItemList.txt</c> 里 genre 21 道具的 <c>ExtParam1</c>。</summary>
    public int Id { get; init; }

    /// <summary>罐头类别（<c>Type</c> 列，1 低星 / 2 高星 / 3 指定 …，服务端不用，留作日志）。</summary>
    public int Type { get; init; }

    /// <summary>
    /// 对应的道具四元组（<c>GDPL</c> 列，形如 <c>[21-1-1-2]</c>）。
    /// 投食时要从背包里扣掉它 —— 客户端 <c>NCafePetLogic:GetFoodList</c>
    /// （<c>NCafePetLogic.lua:354..372</c>）就是用 genre 21 的模板 + <c>ExtParam1</c> 反查本表。
    /// </summary>
    public int[] Gdpl { get; init; } = [];

    /// <summary>购买价：货币 id（<c>PriceGet</c> 列第 1 位，形如 <c>[2,5000]</c>）。</summary>
    public int CostType { get; init; }

    /// <summary>购买价：数量（<c>PriceGet</c> 第 2 位）。</summary>
    public int CostNum { get; init; }

    /// <summary>加速价：货币 id（<c>PricePer</c> 第 1 位）。</summary>
    public int SpeedType { get; init; }

    /// <summary>
    /// 加速价：每 10 分钟单价（<c>PricePer</c> 第 2 位）。
    /// 客户端展示式子 <c>speedNumPer * (hour*6 + floor(min/10) + 1)</c>
    /// （<c>UI_PetFinishFast.lua:61</c>）—— 服务端必须用同一条式子算实扣，否则「显示 X 实扣 Y」。
    /// </summary>
    public int SpeedNumPer { get; init; }

    /// <summary>
    /// 吸引宠物的稀有度权重（<c>GetPetRare</c> 列，形如 <c>{{1,90},{2,10}}</c>）。
    /// 每项 = <c>(Rare, 权重)</c>，与 <c>PetInfo.txt</c> 的 <c>Rare</c> 列对齐；
    /// 客户端把它按合计归一化后当概率展示（<c>UI_PetFood.lua:217..224</c>），
    /// 所以服务端用同一份权重抽，界面上的公示概率才是真的。
    /// </summary>
    public IReadOnlyList<(int Rare, int Weight)> RareWeights { get; init; } = [];

    public override string ToString()
        => $"food#{Id} ({string.Join('-', Gdpl)}) cost=({CostType},{CostNum}) speed=({SpeedType},{SpeedNumPer})";
}

/// <summary>
/// 食盆解锁价（<c>NPet/FoodBoxs.txt</c> 的一行）。
///
/// 取证 <c>NCafePetLogic:LoadPetBoxPrice</c>（<c>NCafePetLogicCfg.lua:80..93</c>）+
/// 客户端点未解锁食盆时的取价方式 <c>tbPetFoodPrice[UnClockNum + 1]</c>
/// （<c>NCafePetLogic.lua:294</c>）——<b>Index 是「已解锁数量 + 1」，不是食盆位置</b>。
/// 现网三行：<c>1 → [3,0]</c>（第一个免费）、<c>2 → [3,100]</c>、<c>3 → [3,200]</c>，
/// 货币 3 = 免费钻（<see cref="MoneyType.FreeDiamond"/>）。
/// </summary>
public sealed record FoodBoxPriceRow(int Index, int CostType, int CostNum);

/// <summary>
/// 一种「咖啡馆食物」（宠物点数 buff，<c>NPet/BuffFood.txt</c> 的一行）。
///
/// 取证 <c>NCafePetLogic:LoadCafeFoodCfg</c>（<c>NCafePetLogicCfg.lua:122..151</c>）。
/// 与 <see cref="PetFoodRow"/> 完全是两回事：这个不进食盆，而是往「咖啡馆食物点数」里加时长。
/// </summary>
public sealed class CafeBuffFoodRow
{
    /// <summary>食物 id（<c>Id</c> 列，现网 1..4）。</summary>
    public int Id { get; init; }

    /// <summary>价格货币 id（<c>PriceGet</c> 第 1 位）。</summary>
    public int PriceType { get; init; }

    /// <summary>价格数量（<c>PriceGet</c> 第 2 位）。</summary>
    public int PriceNum { get; init; }

    /// <summary>
    /// 加多少「食物点数」（<c>AddPoint</c> 列）。
    /// ★ 1 点 = 12 秒：客户端 <c>foodPoint = math.ceil(leaveTime / 12)</c>
    /// （<c>UI_PetAddBuff.lua:97</c>）而 <c>:34</c> 又直接把 <c>AddPoint</c> 与 <c>FoodPointNum</c> 相加比上限，
    /// 两者必须同单位 ⇒ 服务端存的是秒，加的是 <c>AddPoint * 12</c> 秒。
    /// </summary>
    public int AddPoint { get; init; }

    /// <summary>每小时额外精力（<c>Energy</c> 列，现网全 0）。</summary>
    public int EnergyPerHour { get; init; }

    /// <summary>每小时额外好感（<c>Love</c> 列，现网全 0）。</summary>
    public int LovePerHour { get; init; }

    /// <summary>
    /// 稀有度（<c>Rarity</c> 列）。客户端 <c>GetCurBuffState</c>
    /// （<c>NCafePetLogic.lua:615..640</c>）在 20 个槽位里取 <b>Rarity 最大</b>的那一格当「当前生效 buff」，
    /// 所以服务端消耗时长也按稀有度从高到低扣，两边对齐。
    /// </summary>
    public int Rarity { get; init; }

    public override string ToString()
        => $"buffFood#{Id} price=({PriceType},{PriceNum}) +{AddPoint}pt rarity={Rarity}";
}

/// <summary>
/// 一只宠物的模板（<c>NPet/PetInfo.txt</c> 的一行）。
///
/// 取证 <c>NCafePetLogic:LoadPetPosOnUI</c>（<c>NCafePetLogicCfg.lua:56..78</c>）。
/// 现网 9 只：<c>101/102/103</c>（犬）、<c>201/202/203</c>（猫）、<c>301/302/303</c>（熊），
/// 每族 <c>Rare</c> 依次 1/2/3。
///
/// ★ <b>服务端只能发这 9 个 templateid</b>：客户端多处
/// <c>GetPetModelByType(tmpId)</c> 之后立刻解引用 <c>modelInfo.Rare</c> / <c>.Mpath</c> / <c>.Name</c>
/// （<c>UI_PetList.lua:724/729</c>、<c>UI_PetInfo.lua:274</c>），表里没有的 id 会拿到 nil 直接抛。
/// </summary>
public sealed record PetTemplateRow(int Type, int Id, int Rare, int Paw);

/// <summary>放生返还（<c>NPet/GiveBack.txt</c> 的一行）：好感等级门槛 → 返还物品五元组。</summary>
public sealed record GiveBackRow(int Index, int[] Item);

/// <summary>
/// 一类顾客（<c>NCafe/NCafeGuests.txt</c> 的一行）。
///
/// 取证 <c>CafeLogic:LoadCustomerListConfig</c>（<c>CafeLogic.lua:257..274</c>）。
/// 现网 <b>12 行</b>，<c>Id</c> 是 <b>201..212</b>（不是从 1 开始）。
///
/// ★ <b>服务端只能下发这 12 个 Id</b>：客户端多处拿到配置就直接解引用，没有 nil 保护 ——
/// <c>UI_CafeHelp.lua:195..196</c> <c>local d = CafeLogic:GetCustomerCfg(v[1]); d.weight = …</c>、
/// <c>CoffeeCommon.lua:498..499</c> <c>Customer_Info.Prefer</c>、
/// <c>UI_CafeGuestRect.lua:64</c> <c>CafeLogic:GetCustomerCfg(guest_id).PreferDec</c>。
/// </summary>
/// <param name="Id">顾客编号（<c>Id</c> 列，201..212），即 <c>weightlist[i].Id</c> 与
///   <c>seatlist[i].customertype</c> / <c>customerqueue[i].customertype</c> 的取值。</param>
/// <param name="Star">星级（<c>Type</c> 列，1/2/3），决定它属于「今日顾客」的哪一档。</param>
/// <param name="NeedLv">解锁所需咖啡馆等级（<c>NeedLv</c> 列，现网 12 行全是 1）。</param>
/// <param name="WeightMin">出现权重下限（<c>WeightMin</c> 列）。</param>
/// <param name="WeightMax">出现权重上限（<c>WeightMax</c> 列）。</param>
/// <param name="NeedCoffee">喜好咖啡 id（<c>NeedCoffee</c> 列，0 = 无要求；1 星顾客全是 0）。</param>
public sealed record CafeGuestRow(
    int Id, int Star, int NeedLv, int WeightMin, int WeightMax, int NeedCoffee,
    // ★ 116 喝完结算用（NCafeGuests.txt）。取证 CoffeeCommon.lua:499：
    //   `if cafe[Customer_Info.Prefer] >= Customer_Info.Preferfactor then` ⇒ 算「匹配」。
    //   Prefer 是**咖啡属性的列名**（Sweet/Appearance/Concentration/Flavor），
    //   映射见 CoffeeCommon.lua:629..632（酸/醇/苦/风味）。
    string Prefer, int PreferFactor,
    // 额外奖励概率（百分比，"晶币/咖啡币/人气奖励概率100"）：
    //   1★ 顾客全 0；2★ 起 Rate1=15 ⇒ 15% 概率给晶币附加。
    int Rate1, int Rate2, int Rate3);

/// <summary>
/// 一种咖啡（<c>NCafe/CoffeeList.txt</c> 的一行）。
///
/// 取证 <c>CoffeeCommon:LoadCoffeeListConfig</c>（<c>CoffeeCommon.lua:131..</c>）+
/// <c>CafeLogic:LoadCafeListConfig</c>（<c>CafeLogic.lua:295..314</c>）。现网 <b>12 行，Id = 1..12</b>。
///
/// ★ <b><c>coffeelist</c> 里的 <c>coffeetype</c> 必须命中本表</b>：
/// <c>NCafeNet.lua:135..136</c> 是
/// <c>local template = CoffeeCommon:GetCoffeeListConfig(v.coffeetype); … template.Time</c>
/// —— <b>没有 nil 保护</b>，表里没有的 id 会直接在 <c>template.Time</c> 上抛，
/// 而这一句就在 <c>RspDownloadCafeData</c> 里（进咖啡馆前的那条回包），抛了就再也走不到
/// <c>Map.LoadById(6)</c>。同一段代码在 <c>LuaCall.lua:1709</c>（清橱窗）与 <c>:1738</c>（制作咖啡）复用。
/// </summary>
public sealed record CafeCoffeeRow(
    int Id, int Star, int Time,
    // ★ 四个口味属性（CoffeeList.txt）——「匹配」判定用，与顾客 Prefer 同名。
    int Sweet, int Appearance, int Concentration, int Flavor,
    // ★ 三档收益：「匹配收益 Base + 附加」。
    //   AwardA/RateA = 晶币（金币）、AwardB/RateB = 咖啡币、AwardC/RateC = 人气。
    //   现网 1★：AwardA=10 / AwardB=1 / AwardC=1，Rate* 全 0。
    int AwardA, int RateA, int AwardB, int RateB, int AwardC, int RateC,
    int Price, int Cost,
    // Cond1 原文（`CoffeeList.txt`），是关卡条件，不是前置咖啡。
    //   现网咖啡6/8/10/12配置了章节、关卡、难度条件。
    //   学习时按真实通关记录校验。
    string Cond1, int RequiredPlayerLevel = 0)
{
    /// <summary>按顾客 <c>Prefer</c> 的列名取本咖啡对应的属性值（取证 CoffeeCommon.lua:499）。</summary>
    public int AttrByName(string prefer) => prefer switch
    {
        "Sweet" => Sweet,
        "Appearance" => Appearance,
        "Concentration" => Concentration,
        "Flavor" => Flavor,
        _ => 0,
    };
}

/// <summary>
/// 咖啡馆一个等级的配置（<c>NCafe/NCafeLevels.txt</c> 的一行）。
///
/// 取证 <c>CafeLogic:LoadCafeConfig</c>（<c>CafeLogic.lua:194..225</c>）。
/// 现网 <b>31 行</b>：普通等级 <b>1..30</b> ＋ 一行 <c>Level = 10001</c>
/// （<c>CafeLogic.MaxRewardLvId</c>，<c>CafeLogic.lua:44</c>，只用来处理满级人气兑换，
/// <b>不是可下发的等级</b>，见 <see cref="CafeTable.MaxCafeLevel"/>）。
/// </summary>
/// <param name="Level">等级（<c>Level</c> 列）。</param>
/// <param name="SlotEat">用餐槽位数（<c>SlotEat</c> 列，L1=6 → L30=16）。
///   与 <c>NCafeItemTypes.txt</c> 里餐位类（Id=1）的 <c>Limit</c> 逐级相等，
///   是 <c>seatlist</c> 的长度上限。</param>
/// <param name="SlotCoffee">咖啡橱窗槽位数（<c>SlotCoffee</c> 列，L1=1 → L15+=4），
///   是 <c>coffeelist</c> 的长度上限；界面只有 4 个格子
///   （<c>UI_CoffeeMake.lua:17</c> <c>for idx = 1, 4</c>）。</param>
/// <param name="MaxQSize">排队队列长度（<c>MaxQSize</c> 列，★ 现网 31 行<b>全是 1</b>），
///   是 <c>customerqueue</c> 的长度上限。</param>
/// <param name="MaxGuestTypes">「今日顾客」种类数（<c>MaxGuestTypes</c> 列，4/5/6）。</param>
/// <param name="Type1Num">其中 1 星顾客的个数（<c>Type1Num</c> 列，恒 2）。</param>
/// <param name="Type2Num">其中 2 星顾客的个数（<c>Type2Num</c> 列，1 或 2）。</param>
/// <param name="OtherTypeNum">其中 3 星及以上顾客的个数（<c>OtherTypeNum</c> 列，1 或 2）。</param>
/// <param name="CafeUnlockNum">经营区少女上限（<c>CafeUnlockNum</c> 列，1..3），
///   是 <c>roomgirlslist[1]</c> 的长度上限。</param>
/// <param name="RestUnlockNum">休息区少女上限（<c>RestUnlockNum</c> 列），<c>roomgirlslist[2]</c> 的上限。</param>
/// <param name="DormUnlockNum">宿舍少女上限（<c>DormUnlockNum</c> 列），<c>roomgirlslist[3]</c> 的上限。</param>
/// <summary>
/// 一件家具（<c>NCafeItems.txt</c>，942 件）。
/// 表头 3 行；只取本域用得到的列。
/// </summary>
public sealed record CafeFurnitureRow(
    int Id, int Type, string ThemeType, int Level, int AddScore, int SellPrice);

/// <summary>
/// 一个装修主题（<c>NCafeThemes.txt</c>，53 个，表头 3 行 —— 取证
/// <c>CoffeeCommon.lua:236</c> <c>LoadTabFile(self.THEME_PATH, 3)</c>）。
/// </summary>
/// <param name="Items">
/// <c>Item0..N</c> 列的摆位清单，每项原文形如 <c>"401,6,0,5,0"</c>
/// = <c>家具id,parentId,posX,posY,rotate</c>（★ 后四段与 <c>NCafeDefautItems.txt</c> 的
/// <c>Pos</c> 列**同一格式**，只是前面多一段家具 id —— 取证见 <c>HouseTable</c>
/// 类注释开头那三行对照表：<c>Item0 = "401,6,0,5,0"</c> ↔ <c>Pos = "6,0,5,0"</c>）。
///
/// <para><b>★★★ 必须保留后四段，不能只取家具 id</b>（第十二轮修掉的一个真 bug）：</para>
/// 早先本字段是 <c>IReadOnlyList&lt;int&gt;</c>、解析时注释写着「只取家具 id」，于是
/// <see cref="SssjServer.Game.Handlers.HouseFeature"/> 的 <c>OnBuyTheme</c> 入账时把
/// <c>parentId/posX/posY/rotate</c> 一律写 <b>0</b>。后果是
/// <b>买过主题的存档进不了咖啡馆</b>（map 6 卡在 40% 加载条、四分钟无新日志）：
/// <c>parentId=0</c> 的家具在客户端场景树里挂不上父节点 ⇒ 该件 <c>Obj</c>(+0x50) 恒 null ⇒
/// <c>NCafeUtilsController.UpdateNavMeshObstacle</c>（RVA <c>0x1808C18</c>）遍历
/// <c>GetAreaItems</c> 时**无 nil 保护**、抛 NRE ⇒ <c>LoadSceneTreeItems</c> 的
/// 回调链断在中途 ⇒ <c>NCafeMgr.Ins:OnEnter</c> 的 callback 永不触发 ⇒
/// <c>NCafe.lua:33</c> 的 <c>UI.Open('UI_NCafe')</c> 永不执行。
/// </param>
public sealed record CafeThemeRow(
    int Id, string ThemeType, int Area, bool CanSell,
    int AddComfort, int CoinGainRate, IReadOnlyList<CafeThemeItem> Items);

/// <summary>
/// 主题里的一件家具及其**摆位**（<c>NCafeThemes.txt</c> 的一个 <c>Item&lt;i&gt;</c> 单元格）。
///
/// 原文 <c>"502,2,0,24,90"</c> ⇒ <c>TemplateId=502, ParentId=2, PosX=0, PosY=24, Rotate=90</c>。
///
/// <para><b>★ 为什么 <c>ParentId</c> 必须照表下发</b></para>
/// <c>parentId = 0</c> <b>不是</b>「无父节点」的合法值 —— 真实值域是该模板
/// <see cref="HouseItemRow.DicArea"/> 白名单里的挂点（现网见到 2/4/6/10）。
/// 填 0 会让客户端挂不上点、进而在 <c>UpdateNavMeshObstacle</c> 抛 NRE 卡死加载，
/// 详见 <see cref="CafeThemeRow"/> 的 <c>Items</c> 参数注释。
/// </summary>
public sealed record CafeThemeItem(
    int TemplateId, uint ParentId, uint PosX, uint PosY, uint Rotate);

/// <summary>
/// 一档访问委托奖励（<c>VisitAward.txt</c>，5 档）。
/// <c>Point</c> 是积分门槛（现网 2/4/6/8/10），<c>Items</c> 是 <c>Item1..Item5</c> 的
/// <c>[G-D-P-L-N]</c> 奖励清单。
/// </summary>
public sealed record CafeVisitAwardRow(int Id, int Point, IReadOnlyList<int[]> Items);

/// <summary>
/// 一个访问委托任务（<c>VisitMission.txt</c>，4 个）。
/// <c>Action</c> 是少女要播的动作名（客户端 <c>girl:PlayAnimator(config.Action, ...)</c>，
/// `CafeLogic.lua:899`）。
/// </summary>
public sealed record CafeVisitMissionRow(int MissionId, string Action);

/// <summary>
/// 一个少女生日（<c>GirlBirthday.txt</c>，15 个）。
/// 判定见 `GirlCommon.GetBirthdayGirl`（`GirlCommon.lua:1394..1408`）：
/// 按 <c>Month</c>/<c>Day</c> 匹配当天 **且**玩家拥有该少女。
/// </summary>
public sealed record GirlBirthdayRow(int GirlId, int Month, int Day, IReadOnlyList<int[]> Awards);

public sealed record CafeLevelRow(
    int Level, int SlotEat, int SlotCoffee, int MaxQSize,
    int MaxGuestTypes, int Type1Num, int Type2Num, int OtherTypeNum,
    int CafeUnlockNum, int RestUnlockNum, int DormUnlockNum,
    // ★ 升级条件三件套（LuaCall 111 Cafe_LevelUp 用）。
    //   取证 CafeLogic:CheckCafeCanLevelUp（CafeLogic.lua:453..475）：
    //     NeedLevel —— **玩家**等级门槛（不是咖啡馆等级；现网 L1..L5 全是 1）
    //     NeedHot   —— 人气门槛，`> 0` 才检查（CafeLogic.lua:466）
    //     NeedGold  —— 升级消耗的金币（CafeLogic.lua:471 用 CoffeeCommon.GetCurGold 比）
    int NeedLevel, int NeedHot, int NeedGold,
    // 满级人气兑换用（LuaCall 135 Cafe_GetMaxHotReward）
    int TotalHot, int MaxCoffee = 0, int DailyLmtGold = 0, int DailyLmtCoffeeCoin = 0, int CoffeeCoinMax = 0, int OpenTrain = 0);

/// <summary>
/// 咖啡馆配置表集合（12 张表，全部平铺在 <c>extracted/setting/</c>）：
/// <list type="bullet">
///   <item><b>宠物子域 7 张</b>（<see cref="Handlers.CafePetFeature"/> 用）：
///     <c>PetFoodInfo / FoodBoxs / BuffFood / PetInfo / LoveLevel / AreaUnlock / GiveBack</c></item>
///   <item><b>经营本体 5 张</b>（<see cref="Handlers.CafeFeature"/> 用，本轮新增）：
///     <c>NCafeGuests</c>（顾客）/ <c>CoffeeList</c>（咖啡）/ <c>NCafeLevels</c>（等级）/
///     <c>ComfortEff</c>（舒适度区间）/ <c>NCafeItemTypes</c>（家具类型上限）</item>
/// </list>
///
/// 客户端里这些表的路径写的是 <c>NPet/xxx.txt</c> / <c>NCafe/xxx.txt</c>
/// （<c>NCafePetLogicCfg.lua</c> / <c>CafeLogic.lua:9..13</c>），
/// 但 <c>setting.u</c> 解出来是<b>平铺</b>的，所以这里直接按文件名找 —— 与
/// <see cref="ItemBaseTable"/>/<see cref="GachaTable"/> 的做法一致。
///
/// 缺表时**降级而不崩**：对应集合为空，<see cref="Handlers.CafePetFeature"/> 的相关命令
/// 会回一条「配置缺失」日志并保持回包形状合法（客户端不会抛）；
/// <see cref="Handlers.CafeFeature"/> 则退化成「只发合法的空清单」（见
/// <see cref="MaxCafeLevel"/> / <see cref="MaxComfort"/> 的降级值）。
/// </summary>
public sealed class CafeTable
{
    /// <summary>宠物罐头表（<c>NCafePetLogicCfg.lua:32</c> <c>LoadTabFile("NPet/PetFoodInfo.txt",3)</c>）。</summary>
    public const string PetFoodFileName = "PetFoodInfo.txt";

    /// <summary>食盆解锁价表（<c>NCafePetLogicCfg.lua:81</c>）。</summary>
    public const string FoodBoxFileName = "FoodBoxs.txt";

    /// <summary>咖啡馆食物（点数 buff）表（<c>NCafePetLogicCfg.lua:123</c>）。</summary>
    public const string BuffFoodFileName = "BuffFood.txt";

    /// <summary>宠物模板表（<c>NCafePetLogicCfg.lua:57</c>）。</summary>
    public const string PetInfoFileName = "PetInfo.txt";

    /// <summary>好感等级经验表（<c>NCafePetLogicCfg.lua:110</c>）。</summary>
    public const string LoveLevelFileName = "LoveLevel.txt";

    /// <summary>宠物指派位解锁表（<c>NCafePetLogicCfg.lua:154</c>）。</summary>
    public const string AreaUnlockFileName = "AreaUnlock.txt";

    /// <summary>放生返还表（<c>NCafePetLogicCfg.lua:163</c>）。</summary>
    public const string GiveBackFileName = "GiveBack.txt";

    /// <summary>顾客表（<c>CafeLogic.lua:10</c> <c>NCafeGuests_PATH = 'NCafe/NCafeGuests.txt'</c>）。</summary>
    public const string GuestFileName = "NCafeGuests.txt";

    /// <summary>咖啡表（<c>CafeLogic.lua:11</c> <c>NCafeCoffeeList_PATH = 'NCafe/CoffeeList.txt'</c>）。</summary>
    public const string CoffeeFileName = "CoffeeList.txt";

    /// <summary>咖啡馆等级表（<c>CafeLogic.lua:9</c> <c>NCafeLevels_PATH = 'NCafe/NCafeLevels.txt'</c>）。</summary>
    public const string LevelFileName = "NCafeLevels.txt";

    /// <summary>舒适度区间表（<c>CafeLogic.lua:12</c> <c>NCafeComfortEff_PATH = 'NCafe/ComfortEff.txt'</c>）。</summary>
    public const string ComfortFileName = "ComfortEff.txt";

    /// <summary>
    /// 家具类型表。★ 只用它取「餐位类（<c>Id = 1</c> 咖啡馆-餐位）的 <c>Limit</c> 列长度」，
    /// 因为它是**咖啡馆等级的硬上限**，见 <see cref="MaxCafeLevel"/>。
    /// 客户端读它的入口是 C# <c>NCafeSettingsController.ItemTypeInfo(1)</c>（<c>NCafe.lua:112/161</c>）。
    /// </summary>
    public const string ItemTypeFileName = "NCafeItemTypes.txt";

    /// <summary>家具表文件名（942 件）。</summary>
    public const string FurnitureFileName = "NCafeItems.txt";

    /// <summary>装修主题表文件名（53 个）。</summary>
    public const string ThemeFileName = "NCafeThemes.txt";

    /// <summary>访问委托的积分奖励表（5 档）。</summary>
    public const string VisitAwardFileName = "VisitAward.txt";

    /// <summary>访问委托任务表（4 个）。</summary>
    public const string VisitMissionFileName = "VisitMission.txt";

    /// <summary>少女生日表（15 个）。</summary>
    public const string BirthdayFileName = "GirlBirthday.txt";

    /// <summary>餐位类家具在 <c>NCafeItemTypes.txt</c> 里的 <c>Id</c> —— <c>NCafe.lua:112</c> / <c>:161</c>
    /// 都写死 <c>NCafeMgr.Settings:ItemTypeInfo(1)</c>。</summary>
    public const int SeatItemTypeId = 1;

    /// <summary>
    /// 特殊等级 id 的起始值 = 10000（<c>CafeLogic.SpecialLvIdStart</c>，<c>CafeLogic.lua:42</c>）。
    /// <c>NCafeLevels.txt</c> 里 <c>Level = 10001</c> 那行是「满级人气兑换」用的伪等级
    /// （<c>CafeLogic.MaxRewardLvId</c>，<c>CafeLogic.lua:44</c>），
    /// 统计「最大可下发等级」时必须排除它。
    /// </summary>
    public const int SpecialLevelIdStart = 10000;

    /// <summary>
    /// 宠物能量（<c>hot</c>）上限 = 100。
    /// 取证 <c>UI_PetList:GetNowPetNeedHotToMaxLevel</c>（<c>UI_PetList.lua:703</c>）
    /// <c>return 100 - (info.hot or 0)</c> —— 客户端就是拿它算「一键送礼最多能送几个」。
    /// </summary>
    public const int MaxPetHot = 100;

    /// <summary>
    /// 宠物性格取值 1..4。取证本地化表 <c>ItemSort.txt:259..262</c> 只有
    /// <c>PetNature1..PetNature4</c> 四个 key（对应 <c>ui.UI_PetInfo.Nature1..4</c>），
    /// 且客户端拼图标路径 <c>"UI/UI_PetMain/pet_nature_0" .. info.nature</c>
    /// （<c>UI_PetInfo.lua:271</c>、<c>UI_PetList.lua:113/466</c>）—— 超出 1..4 会加载不到图。
    /// </summary>
    public const int NatureCount = 4;

    private readonly Dictionary<int, PetFoodRow> _petFoods = new();
    private readonly List<FoodBoxPriceRow> _foodBoxPrices = new();
    private readonly Dictionary<int, CafeBuffFoodRow> _buffFoods = new();
    private readonly Dictionary<int, PetTemplateRow> _petTemplates = new();
    private readonly Dictionary<int, int> _loveLevelExp = new();
    private readonly List<int> _areaUnlockCosts = new();
    private readonly List<GiveBackRow> _giveBacks = new();
    private readonly Dictionary<int, CafeGuestRow> _guests = new();
    private readonly Dictionary<int, CafeCoffeeRow> _coffees = new();
    private readonly Dictionary<int, CafeLevelRow> _cafeLevels = new();
    private readonly Dictionary<int, CafeFurnitureRow> _furniture = new();
    private readonly Dictionary<int, CafeThemeRow> _themes = new();
    private readonly List<CafeVisitAwardRow> _visitAwards = new();
    private readonly List<CafeVisitMissionRow> _visitMissions = new();
    private readonly List<GirlBirthdayRow> _birthdays = new();
    private int _maxComfort;
    private int _seatLimitLength;

    private CafeTable() { }

    /// <summary>空表（缺配置时的降级实例）。</summary>
    public static CafeTable Empty { get; } = new();

    /// <summary>罐头配置 id → 行。</summary>
    public IReadOnlyDictionary<int, PetFoodRow> PetFoods => _petFoods;

    /// <summary>食盆解锁价（按 <c>Index</c> 升序）。</summary>
    public IReadOnlyList<FoodBoxPriceRow> FoodBoxPrices => _foodBoxPrices;

    /// <summary>咖啡馆食物 id → 行。</summary>
    public IReadOnlyDictionary<int, CafeBuffFoodRow> BuffFoods => _buffFoods;

    /// <summary>宠物 templateid → 模板行。</summary>
    public IReadOnlyDictionary<int, PetTemplateRow> PetTemplates => _petTemplates;

    /// <summary>放生返还表（按 <c>Index</c> 升序）。</summary>
    public IReadOnlyList<GiveBackRow> GiveBacks => _giveBacks;

    /// <summary>顾客编号（201..212）→ 顾客配置行。</summary>
    public IReadOnlyDictionary<int, CafeGuestRow> Guests => _guests;

    /// <summary>咖啡 id（1..12）→ 咖啡配置行。</summary>
    public IReadOnlyDictionary<int, CafeCoffeeRow> Coffees => _coffees;

    /// <summary>咖啡馆等级 → 等级配置行（含 <c>Level = 10001</c> 那行伪等级）。</summary>
    public IReadOnlyDictionary<int, CafeLevelRow> CafeLevels => _cafeLevels;

    /// <summary>家具表（<c>NCafeItems.txt</c>）。</summary>
    public IReadOnlyDictionary<int, CafeFurnitureRow> Furniture => _furniture;

    /// <summary>装修主题表（<c>NCafeThemes.txt</c>）。</summary>
    public IReadOnlyDictionary<int, CafeThemeRow> Themes => _themes;

    /// <summary>访问委托的积分奖励档位（<c>VisitAward.txt</c>，5 档）。</summary>
    public IReadOnlyList<CafeVisitAwardRow> VisitAwards => _visitAwards;

    /// <summary>访问委托任务（<c>VisitMission.txt</c>，2 个）。</summary>
    public IReadOnlyList<CafeVisitMissionRow> VisitMissions => _visitMissions;

    /// <summary>少女生日表（<c>GirlBirthday.txt</c>，15 个）。</summary>
    public IReadOnlyList<GirlBirthdayRow> Birthdays => _birthdays;

    /// <summary>
    /// 满级人气兑换用的专用配置行 —— <c>NCafeLevels.txt</c> 的 **Level = 10001**
    /// （`CafeLogic.lua:44` <c>CafeLogic.MaxRewardLvId = 10001</c>）。
    /// 现网：<c>NeedHot = 30000</c>、<c>LevelAwards = [8-2-1-3-1]</c>。
    /// ★ 它**不是**可下发的咖啡馆等级（<see cref="MaxCafeLevel"/> 会把它排除）。
    /// </summary>
    public const int MaxRewardLevelId = 10001;

    private readonly List<int[]> _maxRewardAwards = new();

    /// <summary>
    /// 满级人气兑换的奖励（<c>NCafeLevels.txt</c> 的 <c>Level=10001</c> 行的
    /// <c>LevelAwards</c> 列）。现网是 <c>[8-2-1-3-1]</c>。
    /// </summary>
    public IReadOnlyList<int[]> MaxRewardAwards => _maxRewardAwards;

    /// <summary>
    /// 舒适度的最大**有加成**值 = <c>ComfortEff.txt</c> 里最大的 <c>High</c>（现网 <b>40200</b>）。
    ///
    /// ★ 为什么要钳位（注意：这里是**静默退化**，不是抛异常）：
    /// <code>
    /// -- GirlVigourManager.lua:208..223  GetComfortConfig
    /// local ComfortConfig = CafeLogic:GetComfortCfg()      -- 字典，键 = Id 列 = 0..9
    /// local MatchLevel = 0
    /// for k = 1, #ComfortConfig do                          -- ← 从 1 开始，只扫 Id 1..9
    ///     if one_config.Low &lt;= v and one_config.High &gt;= v then MatchLevel = k; break end
    /// end
    /// return ComfortConfig[MatchLevel]                      -- 没匹配上就返回 [0]
    /// </code>
    /// <c>ComfortEff.txt</c> 第一行 <c>Id=0 / Low=0 / High=0 / 全部加成 0</c> **就是为了兜住这条路**
    /// （<c>comfort = 0</c> 时 Id 1..9 的 <c>Low</c> 最小是 1，一条都匹配不上 ⇒ 落到 <c>[0]</c>）。
    /// 所以舒适度**越界不会 nil 解引用**，而是悄悄退化成「零加成」——
    /// 界面上 <c>UI_CoffeeList.lua:490 comfortConf.AddEarn_Coin</c> 会显示 0% 而不是应有的 90%，
    /// 少女心情恢复加成（<c>GirlVigourManager.lua:231 comfortConf.AddVigor</c>）也变 0。
    /// 这是「数据看着没错、加成却全没了」型的静默失败，所以服务端主动钳到 40200。
    ///
    /// 缺表时返回 0：那就只允许 <c>comfort = 0</c>（也是新号的真实值），宁可保守。
    /// </summary>
    public int MaxComfort => _maxComfort;

    /// <summary>
    /// **可下发的最大咖啡馆等级** = min（<c>NCafeLevels.txt</c> 里的最大普通等级,
    /// <c>NCafeItemTypes.txt</c> 餐位类 <c>Limit</c> 列的长度）。现网两者都是 <b>30</b>。
    ///
    /// ★ 为什么上限是「<c>Limit</c> 列长度」而不是等级表行数：
    /// <code>
    /// -- CafeEditor.lua:103..116  CafeEditor.GetItemTypeInfoLimit
    /// local index = NCafe.Data.CafeLevel - 1;
    /// local length = tbTypeInfo.Limit.Length;
    /// if (index &gt; length - 1) then
    ///     index = length - 1;
    ///     error("[CafeEditor.GetItemTypeInfoLimit] 咖啡馆等级与NCafeItemTypes表的Limit列表长度不符")
    /// end
    /// return tbTypeInfo.Limit[index];
    /// </code>
    /// <c>error()</c> 写在 <c>return</c> **之前**，所以「等级 &gt; Limit 长度」是**直接抛**，不是兜底。
    /// 而这条路就在进咖啡馆的第一步：
    /// <c>NCafe.lua:19</c> <c>self:FixSeatOverLimit()</c> → <c>NCafe.lua:119</c>
    /// <c>CafeEditor.GetItemTypeInfoLimit(typeInfo)</c>，
    /// 位置在 <c>NCafeMgr.Ins:OnEnter(...)</c>（<c>NCafe.lua:23</c>）**之前** ——
    /// 抛了整个 <c>NCafe:OnEnter</c> 就断掉，场景一个对象都不会初始化。
    /// 同理等级 <c>&lt;= 0</c> 会让 <c>index = -1</c> 去索引 C# <c>int[]</c>，直接越界异常。
    ///
    /// 缺表时返回 <see cref="Handlers.CafeFeature.MinLevel"/> 的等价值 1：服务端当前也只会
    /// 产出等级 1（没有升级逻辑），保守钳到 1 不会有行为差异，且日志会说明。
    /// </summary>
    public int MaxCafeLevel
    {
        get
        {
            int byLevelTable = 0;
            foreach (int level in _cafeLevels.Keys)
            {
                if (level < SpecialLevelIdStart && level > byLevelTable)
                    byLevelTable = level;
            }
            if (byLevelTable <= 0 || _seatLimitLength <= 0)
                return 1;
            return Math.Min(byLevelTable, _seatLimitLength);
        }
    }

    /// <summary>
    /// 宠物指派位的最大数量 = <c>AreaUnlock.txt</c> 的行数。
    /// 取证 <c>GetMaxPetAreaNum() = CountTB(self.PetAreaAsk)</c>（<c>NCafePetLogic.lua:903</c>）。
    /// 现网 6 行、<c>Cost</c> 全 0 ⇒ <c>GetPetAreaNum()</c>（<c>:890</c>）恒返回 6，
    /// 也就是「6 个指派位从一开始就全开」。缺表时按 1 兜底（至少能指派一只）。
    /// </summary>
    public int MaxPetAreaCount => _areaUnlockCosts.Count > 0 ? _areaUnlockCosts.Count : 1;

    /// <summary>七张表是否都读到了（日志用）。</summary>
    public bool IsComplete => _petFoods.Count > 0 && _foodBoxPrices.Count > 0
                           && _buffFoods.Count > 0 && _petTemplates.Count > 0
                           && _loveLevelExp.Count > 0 && _areaUnlockCosts.Count > 0
                           && _giveBacks.Count > 0;

    /// <summary>
    /// 按背包道具四元组反查罐头配置。
    /// 客户端是「genre 21 的模板 → <c>ExtParam1</c> → <c>PetFoodInfo.Id</c>」
    /// （<c>NCafePetLogic.lua:362..363</c>），这里等价地用表里的 <c>GDPL</c> 列直接比。
    /// </summary>
    public PetFoodRow? PetFoodByItem(int g, int d, int p, int l)
    {
        foreach (var row in _petFoods.Values)
        {
            if (row.Gdpl.Length >= 4 && row.Gdpl[0] == g && row.Gdpl[1] == d
                && row.Gdpl[2] == p && row.Gdpl[3] == l)
                return row;
        }
        return null;
    }

    /// <summary>
    /// 解锁「第 <paramref name="unlockedCount"/> + 1 个」食盆要花多少。
    /// 与客户端 <c>tbPetFoodPrice[UnClockNum + 1]</c>（<c>NCafePetLogic.lua:294</c>）同口径；
    /// 越界（已全开）返回 null。
    /// </summary>
    public FoodBoxPriceRow? FoodBoxPriceFor(int unlockedCount)
        => _foodBoxPrices.FirstOrDefault(r => r.Index == unlockedCount + 1);

    /// <summary>
    /// 好感 <paramref name="level"/> 级升下一级需要的经验。
    ///
    /// 对应客户端 <c>GetLoveLevelNeedExp(level) = PetLoveLevel[level] or 1</c>
    /// （<c>NCafePetLogic.lua:688</c>）。★ 一处刻意的偏差：<c>LoveLevel.txt</c> 最后一行
    /// （Level=50）的 <c>Exp</c> 是 <b>0</b>，而 Lua 里 <c>0 or 1</c> 得到的是 <b>0</b>
    /// （0 为真），也就是客户端在 50 级会拿到 0 —— 它靠 <c>if level &lt; maxLevel</c>
    /// （<c>UI_PetInfo.lua:260</c>）在满级时改显示 "Max" 才没除零。
    /// 服务端这里把 &lt;= 0 一律当 1，避免任何除零/死循环，且因为满级不再升级，行为等价。
    /// </summary>
    public int LoveLevelNeedExp(int level)
        => _loveLevelExp.TryGetValue(level, out var v) && v > 0 ? v : 1;

    /// <summary>
    /// 某稀有度宠物的好感等级上限：Rare 1 → 30、2 → 40、3 → 50。
    /// 取证 <c>UI_PetList.lua:728..733</c>（<c>UI_PetInfo.lua:255..259</c> 同）——
    /// 这个数字是**客户端硬编码**的，不在任何配置表里。
    /// </summary>
    public static int MaxLoveLevelForRare(int rare) => rare switch
    {
        >= 3 => 50,
        2 => 40,
        _ => 30,
    };

    /// <summary>
    /// 放生一只 <paramref name="loveLevel"/> 级宠物返还的物品五元组。
    /// 移植客户端 <c>GetPetGiveUpGift</c>（<c>NCafePetLogic.lua:998..1009</c>）：
    /// 顺序遍历，只要 <c>level &gt; lastIndex</c> 就把本行的 item 记下来，
    /// 也就是「取最后一个门槛低于该等级的行」。表为空时返回 null（不发奖，不报错）。
    /// </summary>
    public int[]? GiveBackItemFor(int loveLevel)
    {
        int[]? item = null;
        int lastIndex = 0;
        foreach (var row in _giveBacks)
        {
            if (loveLevel > lastIndex)
                item = row.Item;
            lastIndex = row.Index;
        }
        return item;
    }

    /// <summary>某稀有度下可用的宠物模板（按 templateid 升序，保证可复现）。</summary>
    public IReadOnlyList<PetTemplateRow> TemplatesOfRare(int rare)
        => _petTemplates.Values.Where(t => t.Rare == rare).OrderBy(t => t.Id).ToList();

    /// <summary>
    /// 取某等级的配置行；表里没有这一级时回落到 <b>1 级</b>（<c>NCafeLevels.txt</c> 的第一行），
    /// 全表都缺时返回 null。
    ///
    /// 回落到 1 级而不是 null，是为了让「长度上限」类判断始终有值可用：客户端
    /// <c>CafeLogic:GetCafeLvCfg</c>（<c>CafeLogic.lua:449</c> 附近）拿到 nil 时的行为各处不一，
    /// 而服务端只要不越 1 级的上限就一定安全（1 级是全表最小的一组上限）。
    /// </summary>
    /// <summary>
    /// 当前 CoffeeList 未提供 PrevCoffee 列，因此没有前置咖啡限制。
    /// Cond1 是关卡条件，不能把其中的关卡章节号解释为咖啡编号。
    /// 与原客户端 CoffeeCommon.lua 对 PrevCoffee 缺失时回落到 0 的行为一致。
    /// </summary>
    public int CoffeePrereq(int coffeeId) => 0;

    public CafeLevelRow? LevelRowFor(int level)
    {
        if (_cafeLevels.TryGetValue(level, out var row))
            return row;
        return _cafeLevels.TryGetValue(1, out var first) ? first : null;
    }

    /// <summary>
    /// 「今日顾客」的候选池：<c>NeedLv &lt;= <paramref name="cafeLevel"/></c> 的顾客，按 <c>Id</c> 升序。
    /// 取证 <c>NCafeGuests.txt</c> 的 <c>NeedLv</c> 列（现网 12 行全是 1，即 1 级就全开）。
    /// </summary>
    public IReadOnlyList<CafeGuestRow> GuestsUnlockedAt(int cafeLevel)
        => _guests.Values.Where(g => g.NeedLv <= cafeLevel).OrderBy(g => g.Id).ToList();

    /// <summary>
    /// 读全部 12 张表。任何一张缺失都只记 WARN 并降级。
    ///
    /// ★ 按 <paramref name="settingDir"/> **进程级缓存**（表载入后完全不可变）：
    /// <see cref="Handlers.CafePetFeature"/> 与 <see cref="Handlers.CafeFeature"/> 都要这份表，
    /// 不缓存的话每建一个 <see cref="GameModule"/> 就读 24 次磁盘、并把
    /// 「咖啡馆宠物配置 / 咖啡馆经营配置」两行日志各打两遍，看日志的人会误以为装配了两次。
    /// </summary>
    public static CafeTable Load(string settingDir)
    {
        string key = settingDir;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }

        var loaded = LoadUncached(settingDir);

        lock (_cacheLock)
        {
            // 并发下可能已有别的线程放好了；以先到的那份为准，保证全服拿到同一实例
            if (_cache.TryGetValue(key, out var cached))
                return cached;
            _cache[key] = loaded;
            return loaded;
        }
    }

    private static readonly object _cacheLock = new();
    private static readonly Dictionary<string, CafeTable> _cache = new(StringComparer.Ordinal);

    private static CafeTable LoadUncached(string settingDir)
    {
        var table = new CafeTable();

        // ---- PetFoodInfo.txt ----
        var petFood = SettingTable.Load(Path.Combine(settingDir, PetFoodFileName));
        if (petFood is null)
        {
            Log.Warn($"[setting] {PetFoodFileName} 缺失：食盆投食（SetFood）无法扣道具，将拒绝该命令");
        }
        else
        {
            foreach (var row in petFood.Rows)
            {
                int id = petFood.Int(row, "Id", -1);
                if (id <= 0)
                    continue;
                var price = ParsePair(petFood.Str(row, "PriceGet"));
                var per = ParsePair(petFood.Str(row, "PricePer"));
                var gdpl = ChapterTable.ParseTuples(petFood.Str(row, "GDPL")).FirstOrDefault() ?? [];
                table._petFoods[id] = new PetFoodRow
                {
                    Id = id,
                    Type = petFood.Int(row, "Type"),
                    Gdpl = gdpl,
                    CostType = price.A,
                    CostNum = price.B,
                    SpeedType = per.A,
                    SpeedNumPer = per.B,
                    RareWeights = ParsePairList(petFood.Str(row, "GetPetRare")),
                };
            }
        }

        // ---- FoodBoxs.txt ----
        var foodBox = SettingTable.Load(Path.Combine(settingDir, FoodBoxFileName));
        if (foodBox is null)
        {
            Log.Warn($"[setting] {FoodBoxFileName} 缺失：食盆解锁（OnFoodBoxOpen）无法定价，将拒绝该命令");
        }
        else
        {
            foreach (var row in foodBox.Rows)
            {
                int index = foodBox.Int(row, "Index", -1);
                if (index <= 0)
                    continue;
                var cost = ParsePair(foodBox.Str(row, "Cost"));
                table._foodBoxPrices.Add(new FoodBoxPriceRow(index, cost.A, cost.B));
            }
            table._foodBoxPrices.Sort((a, b) => a.Index.CompareTo(b.Index));
        }

        // ---- BuffFood.txt ----
        var buffFood = SettingTable.Load(Path.Combine(settingDir, BuffFoodFileName));
        if (buffFood is null)
        {
            Log.Warn($"[setting] {BuffFoodFileName} 缺失：买咖啡馆食物（BuyCafeFood）将拒绝");
        }
        else
        {
            foreach (var row in buffFood.Rows)
            {
                int id = buffFood.Int(row, "Id", -1);
                if (id <= 0)
                    continue;
                var price = ParsePair(buffFood.Str(row, "PriceGet"));
                table._buffFoods[id] = new CafeBuffFoodRow
                {
                    Id = id,
                    PriceType = price.A,
                    PriceNum = price.B,
                    AddPoint = buffFood.Int(row, "AddPoint"),
                    EnergyPerHour = buffFood.Int(row, "Energy"),
                    LovePerHour = buffFood.Int(row, "Love"),
                    Rarity = buffFood.Int(row, "Rarity"),
                };
            }
        }

        // ---- PetInfo.txt ----
        var petInfo = SettingTable.Load(Path.Combine(settingDir, PetInfoFileName));
        if (petInfo is null)
        {
            Log.Warn($"[setting] {PetInfoFileName} 缺失：无法生成宠物，吸引/领养会被拒绝");
        }
        else
        {
            foreach (var row in petInfo.Rows)
            {
                int id = petInfo.Int(row, "Id", -1);
                if (id <= 0)
                    continue;   // PetInfo.txt 里每族之间有整行空白（分隔行），Id 解不出来正好跳过
                table._petTemplates[id] = new PetTemplateRow(
                    petInfo.Int(row, "Type"), id,
                    Math.Max(1, petInfo.Int(row, "Rare", 1)),
                    petInfo.Int(row, "Paw"));
            }
        }

        // ---- LoveLevel.txt ----
        var loveLevel = SettingTable.Load(Path.Combine(settingDir, LoveLevelFileName));
        if (loveLevel is null)
        {
            Log.Warn($"[setting] {LoveLevelFileName} 缺失：宠物送礼升级按每级 1 点经验兜底");
        }
        else
        {
            foreach (var row in loveLevel.Rows)
            {
                int level = loveLevel.Int(row, "Level", -1);
                if (level <= 0)
                    continue;
                table._loveLevelExp[level] = loveLevel.Int(row, "Exp", 1);
            }
        }

        // ---- AreaUnlock.txt ----
        var areaUnlock = SettingTable.Load(Path.Combine(settingDir, AreaUnlockFileName));
        if (areaUnlock is null)
        {
            Log.Warn($"[setting] {AreaUnlockFileName} 缺失：宠物指派位上限按 1 兜底");
        }
        else
        {
            foreach (var row in areaUnlock.Rows)
            {
                int index = areaUnlock.Int(row, "Index", -1);
                if (index <= 0)
                    continue;
                table._areaUnlockCosts.Add(areaUnlock.Int(row, "Cost"));
            }
        }

        // ---- GiveBack.txt ----
        var giveBack = SettingTable.Load(Path.Combine(settingDir, GiveBackFileName));
        if (giveBack is null)
        {
            Log.Warn($"[setting] {GiveBackFileName} 缺失：放生宠物不返还物品");
        }
        else
        {
            foreach (var row in giveBack.Rows)
            {
                int index = giveBack.Int(row, "Index", -1);
                var item = ChapterTable.ParseTuples(giveBack.Str(row, "GDPLN")).FirstOrDefault();
                if (index <= 0 || item is null || item.Length < 5)
                    continue;
                table._giveBacks.Add(new GiveBackRow(index, item));
            }
            table._giveBacks.Sort((a, b) => a.Index.CompareTo(b.Index));
        }

        Log.Info($"[setting] 咖啡馆宠物配置：罐头 {table._petFoods.Count} 种、食盆价 {table._foodBoxPrices.Count} 档、" +
                 $"咖啡馆食物 {table._buffFoods.Count} 种、宠物模板 {table._petTemplates.Count} 只、" +
                 $"好感等级 {table._loveLevelExp.Count} 级、指派位 {table.MaxPetAreaCount} 个、放生返还 {table._giveBacks.Count} 档");

        LoadBusinessTables(table, settingDir);
        return table;
    }

    /// <summary>
    /// 读「经营本体」那 5 张表（顾客 / 咖啡 / 等级 / 舒适度 / 家具类型上限）。
    /// 与宠物那 7 张一样：任何一张缺失只记 WARN 并降级，绝不抛。
    /// </summary>
    private static void LoadBusinessTables(CafeTable table, string settingDir)
    {
        // ---- NCafeGuests.txt（顾客）----
        var guests = SettingTable.Load(Path.Combine(settingDir, GuestFileName));
        if (guests is null)
        {
            Log.Warn($"[setting] {GuestFileName} 缺失：LuaCall 112 的 weightlist（今日顾客）只能下发空数组，" +
                     "客户端「顾客信息」面板会空、咖啡收益预估算 0");
        }
        else
        {
            foreach (var row in guests.Rows)
            {
                int id = guests.Int(row, "Id", -1);
                if (id <= 0)
                    continue;
                table._guests[id] = new CafeGuestRow(
                    id,
                    Math.Max(1, guests.Int(row, "Type", 1)),
                    Math.Max(1, guests.Int(row, "NeedLv", 1)),
                    guests.Int(row, "WeightMin"),
                    guests.Int(row, "WeightMax"),
                    guests.Int(row, "NeedCoffee"),
                    guests.Str(row, "Prefer") ?? "",
                    guests.Int(row, "Preferfactor"),
                    guests.Int(row, "Rate1"),
                    guests.Int(row, "Rate2"),
                    guests.Int(row, "Rate3"));
            }
        }

        // ---- CoffeeList.txt（咖啡）----
        var coffees = SettingTable.Load(Path.Combine(settingDir, CoffeeFileName));
        if (coffees is null)
        {
            Log.Warn($"[setting] {CoffeeFileName} 缺失：无法校验 coffeelist 的 coffeetype，" +
                     "为避免客户端 NCafeNet.lua:136 在 template.Time 上抛，将只下发空 coffeelist");
        }
        else
        {
            foreach (var row in coffees.Rows)
            {
                int id = coffees.Int(row, "Id", -1);
                if (id <= 0)
                    continue;
                table._coffees[id] = new CafeCoffeeRow(
                    id,
                    Math.Max(1, coffees.Int(row, "star", 1)),
                    coffees.Int(row, "Time"),
                    coffees.Int(row, "Sweet"),
                    coffees.Int(row, "Appearance"),
                    coffees.Int(row, "Concentration"),
                    coffees.Int(row, "Flavor"),
                    coffees.Int(row, "AwardA"),
                    coffees.Int(row, "RateA"),
                    coffees.Int(row, "AwardB"),
                    coffees.Int(row, "RateB"),
                    coffees.Int(row, "AwardC"),
                    coffees.Int(row, "RateC"),
                    coffees.Int(row, "Price"),
                    coffees.Int(row, "Cost"),
                    coffees.Str(row, "Cond1") ?? "",
                    coffees.Int(row, "Cond3"));
            }
        }

        // ---- NCafeLevels.txt（等级）----
        var levels = SettingTable.Load(Path.Combine(settingDir, LevelFileName));
        if (levels is null)
        {
            Log.Warn($"[setting] {LevelFileName} 缺失：咖啡馆等级只能钳在 1，各清单长度按 1 级上限处理");
        }
        else
        {
            foreach (var row in levels.Rows)
            {
                int level = levels.Int(row, "Level", -1);
                if (level <= 0)
                    continue;
                table._cafeLevels[level] = new CafeLevelRow(
                    level,
                    levels.Int(row, "SlotEat"),
                    levels.Int(row, "SlotCoffee"),
                    levels.Int(row, "MaxQSize"),
                    levels.Int(row, "MaxGuestTypes"),
                    levels.Int(row, "Type1Num"),
                    levels.Int(row, "Type2Num"),
                    levels.Int(row, "OtherTypeNum"),
                    levels.Int(row, "CafeUnlockNum"),
                    levels.Int(row, "RestUnlockNum"),
                    levels.Int(row, "DormUnlockNum"),
                    levels.Int(row, "NeedLevel"),
                    levels.Int(row, "NeedHot"),
                    levels.Int(row, "NeedGold"),
                    levels.Int(row, "TotalHot"),
                    levels.Int(row, "MaxCoffee"),
                    levels.Int(row, "DailyLmtGold"), levels.Int(row, "DailyLmtCoffeeCoin"),
                    levels.Int(row, "CoffeeCoinMax"), levels.Int(row, "OpenTrain"));

                // ★ Level=10001 是满级人气兑换的专用行，顺手把它的 LevelAwards 解出来
                if (level == MaxRewardLevelId)
                {
                    foreach (var g in CardTable.ParseGdplList(levels.Str(row, "LevelAwards")))
                        table._maxRewardAwards.Add(new[] { g.G, g.D, g.P, g.L, g.N });
                }
            }
        }

        // ---- ComfortEff.txt（舒适度区间）----
        var comfort = SettingTable.Load(Path.Combine(settingDir, ComfortFileName));
        if (comfort is null)
        {
            Log.Warn($"[setting] {ComfortFileName} 缺失：舒适度只能下发 0（唯一可确定落在区间内的值）");
        }
        else
        {
            foreach (var row in comfort.Rows)
            {
                int high = comfort.Int(row, "High", -1);
                if (high > table._maxComfort)
                    table._maxComfort = high;
            }
        }

        // ---- NCafeItemTypes.txt（只取餐位类的 Limit 列长度）----
        // ---- NCafeItems.txt（家具）----
        var furn = SettingTable.Load(Path.Combine(settingDir, FurnitureFileName));
        if (furn is null)
        {
            Log.Warn($"[setting] {FurnitureFileName} 缺失：装修域（LuaCall 126/137）会拒绝所有请求");
        }
        else
        {
            foreach (var row in furn.Rows)
            {
                int id = furn.Int(row, "Id", -1);
                if (id <= 0)
                    continue;
                table._furniture[id] = new CafeFurnitureRow(
                    id,
                    furn.Int(row, "Type"),
                    furn.Str(row, "ThemeType") ?? "",
                    furn.Int(row, "Level"),
                    furn.Int(row, "AddScore"),
                    furn.Int(row, "SellPrice"));
            }
        }

        // ---- NCafeThemes.txt（主题）----
        var themes = SettingTable.Load(Path.Combine(settingDir, ThemeFileName));
        if (themes is null)
        {
            Log.Warn($"[setting] {ThemeFileName} 缺失：装修域（LuaCall 126/127）会拒绝所有请求");
        }
        else
        {
            foreach (var row in themes.Rows)
            {
                int id = themes.Int(row, "Id", -1);
                if (id <= 0)
                    continue;

                // Item0..N：原文 "家具id,parentId,posX,posY,rotate"
                // ★★★ 五段**全部**要解出来。只取家具 id 会让 OnBuyTheme 把摆位写 0 ⇒
                //    买过主题的存档进不了咖啡馆（map 6 卡 40%），取证见 CafeThemeItem。
                //
                // ★★ 用 `themes.Columns.ContainsKey` 判「列到头了」，**不能**靠
                //    `Str(...) is null` —— SettingTable.Str 对**不存在的列也返回空串**
                //    （`SettingTable.cs:116..121`），那样写会**死循环**（本轮踩过：
                //    测试直接超时 60s 无输出）。
                var items = new List<CafeThemeItem>();
                for (int i = 0; themes.Columns.ContainsKey($"Item{i}"); i++)
                {
                    var raw = themes.Str(row, $"Item{i}");
                    if (raw.Length == 0)
                        continue;                   // 该格没配家具

                    var seg = raw.Split(',');
                    if (!int.TryParse(seg[0].Trim(), out int fid) || fid <= 0)
                        continue;

                    // ★ 缺段按 0 兜底而不是丢弃整件：现网 53 个主题全是 5 段，
                    //   但「少配一段」也不该让整个主题买不了。
                    static uint Seg(string[] a, int i)
                        => i < a.Length && uint.TryParse(a[i].Trim(), out uint v) ? v : 0u;

                    var item = new CafeThemeItem(fid, Seg(seg, 1), Seg(seg, 2), Seg(seg, 3), Seg(seg, 4));
                    if (seg.Length < 5)
                    {
                        Log.Warn($"[setting] {ThemeFileName} 主题 {id} 的 Item{i}=\"{raw}\" " +
                                 $"不是 5 段（实得 {seg.Length} 段），缺的段按 0 处理 —— " +
                                 "★ parentId 为 0 会让客户端挂不上场景树节点、进店时卡加载条");
                    }
                    items.Add(item);
                }

                table._themes[id] = new CafeThemeRow(
                    id,
                    themes.Str(row, "ThemeType") ?? "",
                    themes.Int(row, "Area"),
                    themes.Int(row, "CanSell") != 0,
                    themes.Int(row, "AddComfort"),
                    themes.Int(row, "CoinGainRate"),
                    items);
            }
        }

        // ---- VisitAward.txt（委托积分奖励，5 档）----
        var va = SettingTable.Load(Path.Combine(settingDir, VisitAwardFileName));
        if (va is not null)
        {
            foreach (var row in va.Rows)
            {
                int id = va.Int(row, "Id", -1);
                if (id <= 0)
                    continue;
                var items = new List<int[]>();
                for (int i = 1; va.Columns.ContainsKey($"Item{i}"); i++)
                {
                    // 形如 "[15-1-1-1-10000]" ⇒ 复用 CardTable 的 GDPL 解析
                    foreach (var g in CardTable.ParseGdplList(va.Str(row, $"Item{i}")))
                        items.Add(new[] { g.G, g.D, g.P, g.L, g.N });
                }
                table._visitAwards.Add(new CafeVisitAwardRow(id, va.Int(row, "Point"), items));
            }
        }

        // ---- VisitMission.txt（委托任务，4 个）----
        var vm = SettingTable.Load(Path.Combine(settingDir, VisitMissionFileName));
        if (vm is not null)
        {
            foreach (var row in vm.Rows)
            {
                int id = vm.Int(row, "MissionId", -1);
                if (id <= 0)
                    continue;
                table._visitMissions.Add(new CafeVisitMissionRow(id, vm.Str(row, "Action")));
            }
        }

        // ---- GirlBirthday.txt（少女生日，15 个）----
        var bd = SettingTable.Load(Path.Combine(settingDir, BirthdayFileName));
        if (bd is not null)
        {
            foreach (var row in bd.Rows)
            {
                int gid = bd.Int(row, "Id", -1);
                int month = bd.Int(row, "Month", -1);
                int day = bd.Int(row, "Day", -1);
                if (gid <= 0 || month is < 1 or > 12 || day is < 1 or > 31)
                    continue;
                var awards = CardTable.ParseGdplList(bd.Str(row, "Award"))
                    .Select(g => new[] { g.G, g.D, g.P, g.L, g.N })
                    .ToList();
                table._birthdays.Add(new GirlBirthdayRow(gid, month, day, awards));
            }
        }

        var itemTypes = SettingTable.Load(Path.Combine(settingDir, ItemTypeFileName));
        if (itemTypes is null)
        {
            Log.Warn($"[setting] {ItemTypeFileName} 缺失：无法确定咖啡馆等级上限，钳在 1（见 MaxCafeLevel 注释）");
        }
        else
        {
            foreach (var row in itemTypes.Rows)
            {
                if (itemTypes.Int(row, "Id", -1) != SeatItemTypeId)
                    continue;
                // Limit 是 INTARR 列（逗号分隔），第 i 项 = 第 i 级的餐位上限
                table._seatLimitLength = ParseIntList(itemTypes.Str(row, "Limit")).Count;
                break;
            }
        }

        var lv1 = table.LevelRowFor(1);
        Log.Info($"[setting] 咖啡馆经营配置：顾客 {table._guests.Count} 类" +
                 $"（Id {(table._guests.Count > 0 ? $"{table._guests.Keys.Min()}..{table._guests.Keys.Max()}" : "-")}）、" +
                 $"咖啡 {table._coffees.Count} 种、等级 {table._cafeLevels.Count} 行" +
                 $"（可下发上限 {table.MaxCafeLevel}）、舒适度上限 {table.MaxComfort}、" +
                 $"1 级槽位 餐位{lv1?.SlotEat ?? 0}/咖啡{lv1?.SlotCoffee ?? 0}/排队{lv1?.MaxQSize ?? 0}");
    }


    /// <summary>
    /// 解析 <c>[2,5000]</c> 这类「货币 id + 数量」二元组（客户端 <c>Str2IntArr</c>）。
    /// 位数不足时返回 (0,0)，调用方据此判「没配价格」。
    /// </summary>
    internal static (int A, int B) ParsePair(string value)
    {
        var nums = ParseIntList(value);
        return nums.Count >= 2 ? (nums[0], nums[1]) : (0, 0);
    }

    /// <summary>
    /// 解析 <c>{{1,90},{2,10}}</c> 这类嵌套二元组表（客户端 <c>Str2Val</c> 的常见形态）。
    /// 实现上只按 <c>{ }</c> 的最内层分组，每组取前两个整数。
    /// </summary>
    internal static List<(int Rare, int Weight)> ParsePairList(string value)
    {
        var result = new List<(int, int)>();
        if (string.IsNullOrWhiteSpace(value))
            return result;

        int i = 0;
        while (i < value.Length)
        {
            int open = value.IndexOf('{', i);
            if (open < 0)
                break;
            int close = value.IndexOf('}', open + 1);
            if (close < 0)
                break;
            var body = value.Substring(open + 1, close - open - 1);
            if (body.Contains('{'))
            {
                // 外层 {{..},{..}}：跳过这一层，让内层各自匹配
                i = open + 1;
                continue;
            }
            var nums = ParseIntList(body);
            if (nums.Count >= 2)
                result.Add((nums[0], nums[1]));
            i = close + 1;
        }
        return result;
    }

    /// <summary>把 <c>[a,b]</c> / <c>a,b</c> / <c>a b</c> 里的整数抽出来（去掉一切括号）。</summary>
    private static List<int> ParseIntList(string value)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(value))
            return result;
        foreach (var part in value.Split([',', ' ', '[', ']', '{', '}', '\t'],
                                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var n))
                result.Add(n);
        }
        return result;
    }
}
