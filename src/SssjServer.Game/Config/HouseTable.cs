using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 一件「原厂新号默认摆设」（<c>NCafeDefautItems.txt</c> 的一行）。
///
/// <para><b>★ 这张表是服务端专用表</b></para>
/// 806 个 lua 文件里对 <c>NCafeDefautItems</c> 零引用（<c>grep -rn NCafeDefautItems extracted/lua/</c> 无输出），
/// 也没有任何 C# <c>CsvFile.Load</c> 指向它（<c>NCafeSettingsController.OnInit</c> 的
/// <c>CsvFile.Load&lt;...&gt;</c> 清单里没有对应类型）——
/// 也就是说它<b>只被原服务端读</b>，正是「新号建房时摆什么」的权威清单。
///
/// <para><b>逐字同源验证（这条把「列语义」钉死了）</b></para>
/// 本表 13 行与 <c>NCafeThemes.txt</c> 的 Id=1（<c>UI_NCafeEditor.CafeDefault</c>「初始咖啡馆」，
/// <c>Area=1</c>）+ Id=2（<c>UI_NCafeEditor.RoomDefault</c>「初始宿舍」，<c>Area=2</c>）
/// 两行的 <c>Item0..Item8</c> / <c>Item0..Item3</c> 列<b>逐字一致、顺序一致</b>：
/// <code>
/// NCafeThemes  Id=1 Item0 = "401,6,0,5,0"      NCafeDefautItems Id=1  ItemId=401 Room=1 Pos="6,0,5,0"
/// NCafeThemes  Id=1 Item3 = "101,2,5,23,180"   NCafeDefautItems Id=4  ItemId=101 Room=1 Pos="2,5,23,180"
/// NCafeThemes  Id=2 Item0 = "703,10,10,18,180" NCafeDefautItems Id=10 ItemId=703 Room=2 Pos="10,10,18,180"
/// </code>
/// ⇒ <c>Pos</c> = 主题 <c>ItemN</c> 去掉首个 <c>ItemId</c> 后剩下的四段，
/// 而主题那边的格式是 <c>&lt;templateId&gt;,&lt;parentId&gt;,&lt;posX&gt;,&lt;posY&gt;,&lt;rotate&gt;</c>
/// （见 <see cref="HouseItemRow.DicArea"/> 的白名单校验），
/// 所以本表 <c>Pos</c> 的四段是 <b><c>parentId, posX, posY, rotate</c></b>，
/// 表头中文「物件位置Parent,X,Y,Rotate」也是这么写的。
/// </summary>
/// <param name="Id">行号（<c>Id</c> 列，1..13）。只用于日志与稳定排序，不下发。</param>
/// <param name="TemplateId">
/// 家具模板 id（<c>ItemId</c> 列）→ <c>gsproto.furniture.templateId</c>（字段 #1）
/// 与 <c>gsproto.objPose.templateId</c>（字段 #1）。必须在 <c>NCafeItems.txt</c> 里存在，
/// 否则 <c>NCafeItem.UpdateInfo</c>（RVA <c>0x1D2CA5C</c>）把
/// <c>NCafeSettingsController.ItemTemplate(templateId)</c> 的 null 直接存进
/// <c>NCafeItem.Template</c>（+0x60，<b>无 nil 检查</b>）。
/// </param>
/// <param name="Room">
/// 所属区域（<c>Room</c> 列）→ <c>gsproto.furniture.roomId</c>（字段 #4）。
/// 值域就是 <c>Game.NCafeAreaType</c>（<c>dump.cs:534517..534527</c>）：
/// <c>Cafe=1</c> 经营区 / <c>Rest=2</c> 休息区 / <c>Dorm=6</c> 新宿舍。本表只出现 1 和 2。
/// ★ 这个字段是<b>唯一</b>决定家具属于哪个区的东西：
/// <c>NCafeDataController.GetAreaItems</c>（RVA <c>0x1D6266C</c>）遍历 <c>Items.Values</c>
/// 只比 <c>item.Room == areaType</c>（<c>0x1D627B0 ldr w8,[x21,#0x18]; cmp w8,w20</c>），
/// <b>压根不看 <c>roomList</c></b>。
/// </param>
/// <param name="ParentId">
/// <b>父物件的 <c>objId</c></b>（<c>Pos</c> 第 1 段）→ <c>furniture.parentId</c>（#3）
/// 同时也是 <c>objPose.parentTemplateId</c>（#2）。
///
/// ★ 语义是 <b>objId（guid）而不是 templateId</b> —— 尽管字段名叫
/// <c>parentTemplateId</c>：客户端上行时两处都取自 <c>NCafeItem.Parent</c>(+0x14)，
/// 而 <c>NCafeItem.Parent</c> 与 <c>Items</c> 字典的 key（= objId）同域，
/// 家具树 <c>NCafeTree.&lt;InitNodes&gt;b__0</c>（RVA <c>0x1801830</c>）就是拿
/// <c>item.Parent == node.Item.Id</c> 去挂树的。
/// 本表的取值（2 / 6 / 10）指向的都是<b>容器节点</b>，而容器节点由客户端自建时
/// <c>Guid = templateId</c>（<c>InitFixedData</c> <c>0x1D61670</c>）——
/// 所以「objId」与「templateId」两种解读在这里结果相同，但**只对系统家具成立**：
/// 将来支持玩家把家具挂到别的<b>玩家家具</b>上时，这里必须填那件家具的 <c>objId</c>（≥1000）。
/// 挂不上树的后果是 <b>不加载、不显示、不报错</b>。
///
/// 取证：<c>NCafeDataController.ReqSaveItems</c>（RVA <c>0x1D6B7A8</c>）上行时
/// <c>0x1D6B95C ldr w23,[x22,#0x14]</c> → <c>furniture.set_parentId</c>，
/// <c>0x1D6BA04</c> 同一个 <c>+0x14</c> → <c>objPose.set_parentTemplateId</c> —— 两者同源。
/// </param>
/// <param name="PosX">网格 X（<c>Pos</c> 第 2 段）→ <c>objPose.posX</c>（#4）。</param>
/// <param name="PosY">网格 Y（<c>Pos</c> 第 3 段）→ <c>objPose.posY</c>（#5）。</param>
/// <param name="Rotate">朝向角度（<c>Pos</c> 第 4 段，0/90/180/270）→ <c>objPose.rotate</c>（#6）。</param>
/// <param name="BizCount">
/// 经营位个数（<c>BizCount</c> 列）→ <c>gsproto.furniture.bizcount</c>（#8）。
/// 本表 4 张 <c>templateId=101</c> 餐位都是 1，其余 0，与 <c>NCafeItems.txt</c> 的
/// <c>Points</c> 列里 <c>biz_</c> 前缀挂点个数一致（101 的 Points 含 <c>biz_1</c>）。
/// ★ <c>bizcount</c> 在 C# 侧<b>没有任何读取方</b>（<c>il2cpp_callers.py of
/// 'gsproto.furniture$$get_bizcount'</c> 只有 xLua wrapper），座位是客户端自己从
/// <c>biz_</c> 挂点算的（<c>NCafe.lua:170..180</c>），所以它只是「服务端记账用」——
/// 但照表填是零成本的正确做法。
/// </param>
public sealed record DefaultFurnitureRow(
    int Id,
    int TemplateId,
    int Room,
    uint ParentId,
    uint PosX,
    uint PosY,
    uint Rotate,
    int BizCount);

/// <summary>
/// 一件家具模板（<c>NCafeItems.txt</c> 的一行，<b>863 行有效数据</b>，最大 <c>Id = 5097</c>）。
/// 只保留服务端要用的列。
///
/// ★ 行数口径：文件共 946 行，去掉 3 行表头后有 942 行「非零长度」的行，
/// 但其中 <b>79 行是纯制表符空行</b>（40 个 <c>\t</c>、零个非空单元格），
/// 所以按「<c>Id</c> 列可解析且 &gt; 0」筛出来的有效模板是 <b>863</b> 条。
/// 别把 941/942 当成模板数 —— 本类的加载逻辑跳过那 79 行是正确的
/// （复现：<c>awk -F'\t' 'NR&gt;3 &amp;&amp; $1+0&gt;0' extracted/setting/NCafeItems.txt | wc -l</c>）。
///
/// 客户端侧对应 <c>Game.NCafeItemTemplate</c>（<c>dump.cs:534417</c>），由
/// <c>CsvFile.Load&lt;uint, NCafeItemTemplate&gt;("NCafe/NCafeItems.txt")</c> 载入
/// （<c>NCafeSettingsController.OnInit</c> RVA <c>0x1A4F74C</c>，字符串槽 <c>0x52BE178</c>），
/// 查询入口 <c>NCafeSettingsController.ItemTemplate(uint)</c> RVA <c>0x1A3C23C</c>
/// （查不到打 <c>NCafe: cannot find item template {0}.</c> 并返回 null，<c>0x1A3C3B4</c>）。
/// </summary>
public sealed class HouseItemRow
{
    /// <summary>模板 id（<c>Id</c> 列）。</summary>
    public int Id { get; init; }

    /// <summary>
    /// 家具类别（<c>Type</c> 列）。与 <c>NCafeItemTypes.txt</c> 的 <c>Types</c> 列对齐：
    /// 100 餐位 / 101 地毯 / 103 墙饰 / 104 小件 / 201 大件 / 300 宠物，
    /// 以及 50/51/55/56/58/59/60..65/70..73/80..85 这批「系统家具」。
    /// 客户端 <c>NCafeItem.get_Type</c>（RVA <c>0x1D2C5C8</c>）= <c>Template.Type</c>（+0x38），
    /// <c>Template == null</c> 时返回 0（有 nil 保护）。
    /// </summary>
    public int Type { get; init; }
    public string ThemeType { get; init; } = "";
    public int OwnMax { get; init; }
    /// <summary>Parent container grid envelope from InnerSize; absent dimensions do not imply a zero-sized grid.</summary>
    public IReadOnlyList<uint> InnerSize { get; init; } = [];
    public IReadOnlyList<uint> Size { get; init; } = [];

    /// <summary>
    /// 父容器节点的模板 id（<c>Parent</c> 列，表头中文「父节点(固定物件使用)」）。
    /// 只有系统家具填了它：50 地板→2(Floor)、51 墙→3(Wall1)、55 窗→5(Wall3)、
    /// 56 大门→5、58 吧台→8(Batai)、59 吧台墙→9(Bataiqiang)、72 外景→22、73 灯→23。
    /// </summary>
    public uint Parent { get; init; }

    /// <summary>
    /// 摆放面（<c>DicPlace</c> 列，表头中文「摆放方向 0-全部 1-咖啡馆 2-宿舍 6-新宿舍」）。
    /// 系统家具用它区分自己属于哪个区（50..59 是 1、60..65 是 2、80..85 是 6、72/73 是 0 通用）。
    /// </summary>
    public int DicPlace { get; init; }

    /// <summary>
    /// <b>允许的 <c>parentId</c> 白名单</b>（<c>DicArea</c> 列，形如 <c>[2,10]</c>、
    /// <c>[4,6,11,12,13,14,31,32,33,34]</c>）。
    ///
    /// 判据：<c>NCafeDefautItems.txt</c> 里每一行的 <c>Pos</c> 第 1 段都落在对应模板的 <c>DicArea</c> 内 ——
    /// 101 餐位 <c>DicArea=[2,10]</c> 而 4 行的 parentId 都是 2（Floor 咖啡馆地面）；
    /// 401 墙饰 <c>DicArea=[4,6,11,12,13,14,31,32,33,34]</c> 而两行的 parentId 都是 6（Wall4）；
    /// 703..706 <c>DicArea=[2,10,30]</c> 而 parentId 都是 10（Floor 宿舍1）。13/13 命中。
    /// 空列表 = 不限制（系统家具与容器节点都是空）。
    /// </summary>
    public IReadOnlyList<uint> DicArea { get; init; } = [];

    /// <summary>
    /// 附加挂点名（<c>Points</c> 列，<c>|</c> 分隔）。表头中文写明前缀语义：
    /// <c>girl_</c> 少女位、<c>guest_</c> 顾客位、<b><c>biz_</c> 经营位</b>（座位就是从它算的）。
    /// </summary>
    public IReadOnlyList<string> Points { get; init; } = [];

    /// <summary>星级（<c>Level</c> 列）。</summary>
    public int Level { get; init; }

    /// <summary>提供的舒适度（<c>AddScore</c> 列）。服务端算 <c>comfort</c> 时会用到。</summary>
    public int AddScore { get; init; }

    /// <summary>
    /// 是否「系统家具/场景固定节点」—— 判据是 <c><see cref="Id"/> == <see cref="Type"/></c>。
    ///
    /// <c>NCafeItems.txt</c> 里恰好有 42 行满足它，且全部落在 <c>Id ∈ [1, 85]</c>：
    /// <list type="bullet">
    ///   <item><b>1..35 容器节点</b>（<c>Res</c> 列是场景节点名而不是 prefab 路径：
    ///     1 <c>SceneRoot</c> / 2 <c>Floor</c> / 3..6 <c>Wall1..4</c> / 7 <c>Top</c> /
    ///     8 <c>Batai</c> / 9 <c>Bataiqiang</c> / 10..15 宿舍1 的同一套 / 16..19 外景训练位 /
    ///     20,21,23 <c>Light</c> / 22 外景容器 / 30..35 宿舍2 的同一套）</item>
    ///   <item><b>50..85 系统 prefab</b>（50 地板 / 51..54 墙 / 55 窗 / 56 大门 / 57 屋顶 /
    ///     58 吧台 / 59 吧台墙 / 60..65 宿舍1 / 66..69 外景训练器械 / 70,71,73 灯 /
    ///     <b>72 外景</b> / 80..85 宿舍2）</item>
    /// </list>
    /// 这批家具的 <c>objId</c> 与 <c>templateId</c> 相等 —— 铁证是
    /// <c>NCafeDataController.GetOutDoorItem</c>（RVA <c>0x1D6353C</c>）在
    /// 「按 Type 筛不到外景」时的兜底：<c>0x1D63744 mov w1,#0x48</c> +
    /// <c>0x1D6374C bl GetItem</c> —— 拿 <b><c>objId = 72</c></b> 去查，
    /// 而 72 正是外景的 <c>templateId</c>（也是它的 <c>Type</c>）。
    /// </summary>
    public bool IsSystem => Id == Type;

    /// <summary>本模板的经营位（座位）个数 = <c>Points</c> 里 <c>biz_</c> 前缀的挂点数。</summary>
    public int BizPointCount
    {
        get
        {
            int n = 0;
            foreach (var p in Points)
            {
                if (p.StartsWith("biz_", StringComparison.Ordinal))
                    n++;
            }
            return n;
        }
    }

    public override string ToString()
        => $"item#{Id} type={Type} parent={Parent} place={DicPlace} biz={BizPointCount}";
}

/// <summary>
/// 一个家具类别（<c>NCafeItemTypes.txt</c> 的一行，33 行有效数据）。
/// 客户端 <c>NCafeSettingsController.ItemTypeInfo(int)</c> 查它，
/// <c>NCafe.lua:112/163</c> 用 <c>ItemTypeInfo(1).Types[0]</c>（=100）筛餐位。
/// </summary>
/// <param name="Id">类别 id（<c>Id</c> 列）。1 = 咖啡馆-餐位、22/23 = 外景、9 = 咖啡馆-吧台 …</param>
/// <param name="Area">所属区域（<c>Area</c> 列），值域同 <c>NCafeAreaType</c>：1 / 2 / 6。</param>
/// <param name="Types">该类别包含的 <c>NCafeItems.Type</c> 值（<c>Types</c> 列，INTARR）。</param>
/// <param name="Limit">逐等级数量上限（<c>Limit</c> 列，INTARR；餐位类恰好 30 项 = 咖啡馆最高等级）。</param>
public sealed record HouseItemTypeRow(int Id, int Area, IReadOnlyList<int> Types, IReadOnlyList<int> Limit);

/// <summary>
/// 家园 / 家具摆设域（pf 1048 <c>GetHouseInfoReq</c> → 1049 <c>GetHouseInfoRsp</c>）的配置表。
///
/// 三张表：
/// <list type="bullet">
///   <item><c>NCafeDefautItems.txt</c> —— 新号默认摆设（13 行，<b>服务端专用</b>），见 <see cref="DefaultFurnitureRow"/></item>
///   <item><c>NCafeItems.txt</c> —— 家具模板全集（863 行），见 <see cref="HouseItemRow"/></item>
///   <item><c>NCafeItemTypes.txt</c> —— 家具类别（33 行），见 <see cref="HouseItemTypeRow"/></item>
/// </list>
///
/// 与 <see cref="CafeTable"/> 一样按目录做<b>进程级缓存</b>（载入后完全不可变）：
/// 不缓存的话每建一个 <see cref="GameModule"/> 就重读 863 行的 <c>NCafeItems.txt</c>，
/// 而单测里每个用例都要建一个 harness。
/// </summary>
public sealed class HouseTable
{
    /// <summary>新号默认摆设表（服务端专用）。</summary>
    public const string DefaultItemsFileName = "NCafeDefautItems.txt";

    /// <summary>家具模板表。</summary>
    public const string ItemsFileName = "NCafeItems.txt";

    /// <summary>家具类别表。</summary>
    public const string ItemTypesFileName = "NCafeItemTypes.txt";

    /// <summary>
    /// 家园域的 JSON 总配置（<b>不是</b> tab 分隔表，不能用 <see cref="SettingTable"/> 读）。
    ///
    /// 取证：<c>NCafeSettingsController.OnInit</c>（RVA <c>0x1A4F74C</c>）里有字面量
    /// <c>'NCafe/Configure.json'</c>（slot <c>0x52BE170</c>）+
    /// <c>JsonConvert.DeserializeObject&lt;NCafeSettingsController.NCafeJsonConfigure&gt;()</c>
    /// （类定义 <c>dump.cs:533995..534030</c>），加载器 <c>Game.Assets.LoadText</c>（RVA <c>0x175D764</c>）
    /// 会给逻辑路径拼上前缀 <c>Assets/_Game/SettingGen/</c>。
    /// 本仓库里它就是 <c>extracted/setting/Configure.txt</c>（导表时 TextAsset 一律落成 <c>.txt</c>，
    /// 已与 <c>setting.u</c> 内的原文 md5 逐字节核对一致，见 <c>work/ncafe-configure-forensics.md</c>）。
    ///
    /// ★ <b>不要用 <c>System.Text.Json</c> 解析它</b>：<c>DicBlocks</c> / <c>DicPathBlocks</c> /
    /// <c>WallTypes</c> 三个字典的整数键<b>没有引号</b>（<c>2 : [[33,0,45,38]]</c>），
    /// 标准 JSON 解析器直接报错（Newtonsoft 宽松所以客户端能读）。
    /// 服务端只需要其中 5 个平坦字段，所以用「按键名取方括号/数字」的轻量提取。
    ///
    /// 另一个坑：JSON 里还多一个 <c>"TopTypeId" : 4</c>，而 C# 类 <c>NCafeJsonConfigure</c>
    /// 的 31 个字段里<b>没有它</b>（<c>dump.cs:533995..534030</c>）——
    /// Newtonsoft 默认 <c>MissingMemberHandling=Ignore</c> 所以被<b>静默丢弃</b>，
    /// 客户端根本没读到。反向也核过：那 31 个字段全部在 JSON 里有显式值，无一落默认值。
    /// </summary>
    public const string ConfigureFileName = "Configure.txt";

    /// <summary>
    /// 外景家具的 <c>objId</c>（同时也是它的 <c>templateId</c> 与 <c>Type</c>）。
    ///
    /// <c>NCafe.lua:58..59</c> 里
    /// <code>
    /// local outDoorItem = Game.NCafeManager.Data:GetOutDoorItem()
    /// U3D.RenderSettings.fog = (outDoorItem.Id ~= 1800)     -- ★ 无 nil 保护
    /// </code>
    /// 而 <c>GetOutDoorItem</c>（RVA <c>0x1D6353C</c>）先按
    /// <c>Type == Settings.OutDoorScene</c>(=72) 筛 <c>Items</c>（<c>0x1D63628 GetItemsByType</c>），
    /// <c>Count &lt; 1</c> 时兜底 <c>0x1D63744 mov w1,#0x48</c>(=72) + <c>GetItem(objId)</c>。
    ///
    /// ★ 这一件<b>由客户端自建</b>（72 在 <c>Configure.txt:3</c> 的 <c>DefaultItems</c> 里，
    /// 取证见 <see cref="SystemItems"/>），服务端<b>不需要也不应该</b>下发；
    /// 这个常量留作两个用途：① 排查「进店抛 NRE」时先确认外景在不在；
    /// ② 它是「系统家具的 <c>objId</c> 等于 <c>templateId</c>」这条结论的唯一硬证据
    /// （<c>GetItem</c> 收的是 objId，而 72 是 templateId/Type）。
    /// </summary>
    public const uint OutdoorObjId = 72;

    /// <summary>
    /// 系统家具 <c>objId</c> 的上界（含）。<c>NCafeItems.txt</c> 里 <c>Id == Type</c> 的 42 行
    /// 全部落在 <c>[1, 85]</c>，最大的是 85（宿舍2 屋顶）。
    /// </summary>
    public const uint MaxSystemObjId = 85;

    /// <summary>
    /// 玩家家具 <c>objId</c> 的起始值。
    ///
    /// 客户端侧的下界是 <c>NCafeJsonConfigure.MinNormalItemId</c>
    /// （<c>dump.cs:534003</c>），<c>Configure.txt:10</c> 现网值 = <b>100</b>。
    /// 本实现取 <b>1000</b>（更保守）：既大于 <see cref="MaxSystemObjId"/>=85，
    /// 也大于 100，且给「将来把系统家具编号扩到三位数」留了余量；
    /// 同时 1000 起的 objId 一眼就能和系统家具区分开，看日志省事。
    /// </summary>
    public const uint MinPlayerObjId = 1000;

    /// <summary>咖啡馆-餐位的类别 id（<c>NCafeItemTypes.txt</c> 的 <c>Id=1</c>）。</summary>
    public const int SeatItemTypeId = 1;

    /// <summary>
    /// ★★ <b><c>templateId</c> 绝对不能下发的两个值：<c>20</c>（<c>SceneRoot/Cafe/Light</c>）与
    /// <c>21</c>（<c>SceneRoot/Rest/Light</c>）。</b>
    ///
    /// 这两行的 <c>Res</c> 列是<b>场景节点路径</b>而不是 <c>.prefab</c>，而
    /// <c>NCafePoolController.LoadItem</c>（RVA <c>0x1A3BD10</c>）遇到
    /// 「<c>Res</c> 不以 <c>.prefab</c> 结尾」时只打一条 Debug
    /// （<c>[NcafePool.LoadItem] 物件路径不合法, path=…</c>）然后 <b>直接 return、不调回调</b>
    /// （<c>0x1A3BD54</c> 函数尾）；而 <c>IsSystemItem</c>（<c>0x1802168</c>）判的是 <c>Type</c>，
    /// 20/21 的 <c>Type</c> 不在 <c>Configure.SystemItems</c> 里 ⇒ 跳不过去。
    ///
    /// 后果：<c>NCafeSceneController.InitObjects</c> 的逐件加载链<b>在那一格静默停住</b>，
    /// <c>SetProgress(0.6)</c> 永不执行 ⇒ <b>加载条永远停在 0.4，且 logcat 里连一条 Error 都没有</b>。
    /// 全表只有这 2 个 id 有此风险。
    ///
    /// 本实现只下发 <c>NCafeDefautItems.txt</c> 的 13 件（templateId 全 ≥ 100），
    /// 天然避开；这个名单是**存档脏数据**的防线，见
    /// <c>HouseFeature.ReadFurniture</c> 与 <c>HouseFeature.CreateDefault</c>。
    /// </summary>
    public static IReadOnlyList<int> PoolBlockedTemplateIds { get; } = new[] { 20, 21 };

    private readonly List<DefaultFurnitureRow> _defaults = new();
    private readonly Dictionary<int, HouseItemRow> _items = new();
    private readonly Dictionary<int, HouseItemTypeRow> _itemTypes = new();
    private readonly List<HouseItemRow> _systemItems = new();
    private readonly List<int> _configureDefaultTypes = new();
    private readonly List<int> _configureSystemTypes = new();
    private int _minNormalItemId;
    private int _outDoorScene;
    public int CarpetTypeId { get; private set; } = 101;
    public Dictionary<uint, List<int[]>> Blocks { get; } = new();

    private HouseTable() { }

    /// <summary>空表（配置缺失时的降级形态；<see cref="HouseFeatureFallback"/> 用它兜底）。</summary>
    public static HouseTable Empty { get; } = new();

    /// <summary>新号默认摆设，按 <c>Id</c> 列升序（下发顺序稳定 ⇒ objId 分配可复现）。</summary>
    public IReadOnlyList<DefaultFurnitureRow> DefaultItems => _defaults;

    /// <summary>家具模板：<c>templateId</c> → 行。</summary>
    public IReadOnlyDictionary<int, HouseItemRow> Items => _items;

    /// <summary>家具类别：<c>Id</c> → 行。</summary>
    public IReadOnlyDictionary<int, HouseItemTypeRow> ItemTypes => _itemTypes;

    /// <summary>
    /// <c>Configure.txt</c> 的 <c>DefaultItems</c>（现网
    /// <c>[50,51,52,54,55,56,57,58,59,60..69,72,73,80..85]</c>，27 项）。
    ///
    /// <b>★ 这就是「新号场景里默认存在的系统家具」清单</b>，语义靠三条互相独立的证据钉住：
    /// <list type="number">
    ///   <item>值全部落在 <c>NCafeItems.txt</c> 里 <c>Id == Type</c> 的那 42 行的
    ///     「系统 prefab」半区（50..85），一个容器节点（1..35）都没有 ——
    ///     容器节点在同一份 JSON 的<b>另一个</b>键 <c>SystemItems</c> 里（<see cref="ConfigureSystemTypes"/>）；</item>
    ///   <item>缺项恰好自洽：缺 <b>53</b>（<c>Parent=5</c> 的整面墙 <c>wall001</c>）
    ///     而有 <b>55/56</b>（同样 <c>Parent=5</c> 的窗与门）—— 那面墙上开了门窗，
    ///     所以不放整墙；缺 <b>70/71</b>（<c>Parent=20/21</c> 的灯）而有 <b>73</b>，
    ///     与 <c>Configure.txt:6</c> 的 <c>OutDoorLight = 73</c> 一致；</item>
    ///   <item>含 <b>72</b>，而 72 正是 <c>OutDoorScene</c>（<c>Configure.txt:5</c>）
    ///     与 <c>GetOutDoorItem</c> 兜底要拿的那个 objId（<see cref="OutdoorObjId"/>）。</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<int> ConfigureDefaultTypes => _configureDefaultTypes;

    /// <summary>
    /// <c>Configure.txt</c> 的 <c>SystemItems</c>（现网 <c>[1..19,22,23,30..35]</c>，27 项）——
    /// 场景<b>容器节点</b>的 <c>Type</c>（<c>SceneRoot</c>/<c>Floor</c>/<c>Wall1..4</c>/<c>Top</c>/
    /// <c>Batai</c>/<c>Bataiqiang</c>/<c>Light</c> 那批）。
    ///
    /// 客户端把它当「不是商品」的判据用：<c>NCafeSettingsController.OnInit</c>
    /// <c>0x1A4F98C bl List&lt;uint&gt;.Contains</c> 判 <c>tmpl.Type</c>（<c>0x1A4F970 ldr w21,[x21,#0x38]</c>），
    /// <b>不</b>在表里才去 <c>ItemMgr.GetItemBaseInfoByNCafeItemId</c>（<c>0x1A4F9CC</c>）
    /// 补名字/图标。
    ///
    /// ⇒ 服务端<b>不下发</b>容器节点：它们是场景 prefab 自带的层级节点，
    /// 只作为 <c>furniture.parentId</c> 的取值出现（<c>FloorTypeId=[2,10,30]</c> /
    /// <c>WallTypeIds=[3,4,5,6,11,12,13,14,31,32,33,34]</c> 正是这批值）。
    /// </summary>
    public IReadOnlyList<int> ConfigureSystemTypes => _configureSystemTypes;

    /// <summary>
    /// <c>Configure.txt</c> 的 <c>MinNormalItemId</c>（现网 100）；读不到时为 0。
    /// 只用于日志与「我们取的 1000 是否确实 ≥ 客户端下界」的自检，
    /// 实际分配用 <see cref="MinPlayerObjId"/>。
    /// </summary>
    public int MinNormalItemId => _minNormalItemId;

    /// <summary>
    /// <c>Configure.txt</c> 的 <c>OutDoorScene</c>（现网 72）；读不到时回落
    /// <see cref="OutdoorObjId"/>。<c>GetOutDoorItem</c>（RVA <c>0x1D6353C</c>）
    /// 先按这个 <b>Type</b> 筛 <c>Items</c>，筛不到才按 <b>objId</b>=72 兜底。
    /// </summary>
    public int OutDoorScene => _outDoorScene > 0 ? _outDoorScene : (int)OutdoorObjId;

    /// <summary>
    /// <b>★ 服务端不下发系统家具</b> —— 客户端自己会造。
    ///
    /// 取证：<c>NCafeDataController.UpdatePlayerHouseInfo</c>（RVA <c>0x1D68B04</c>）在
    /// <c>0x1D68BF8 get_InScene</c> 之后有
    /// <code>
    /// if (!NCafeManager.InScene) { Items.Clear(); this.OnInit(); }   // 0x1D68BFC tbnz / 0x1D68C24 / 0x1D68C34
    /// </code>
    /// 而 <c>OnInit</c> = <c>InitFixedData()</c>（RVA <c>0x1D61500</c>，吃
    /// <see cref="ConfigureSystemTypes"/>）+ <c>InitDefaultData()</c>（RVA <c>0x1D61834</c>，吃
    /// <see cref="ConfigureDefaultTypes"/>），每件都
    /// <code>
    /// it.Guid     = id;                    // 0x1D619A8 str w21,[x22,#0x10]   ★ objId = templateId
    /// it.Parent   = tpl.Parent;            // 0x1D619B0/0x1D619B8（NCafeItemTemplate.Parent @0x48）
    /// it.Room     = tpl.DicPlace[0];       // 0x1D61A04/0x1D61A08（DicPlace @0x40）
    /// Items[id]   = it;                    // 0x1D61A40
    /// </code>
    /// ⇒ 登录时（此刻必然 <c>InScene == false</c>）客户端已经把 <b>27 + 27 = 54 件</b>
    /// 默认家具连 <c>Parent</c>/<c>Room</c> 一起建好了，<b>外景 72 也在其中</b>。
    ///
    /// 反过来，服务端<b>回传</b>这些 objId 是有害的：<c>OnUpdateItem</c> 末尾会调
    /// <c>RecoverDefaultItem</c>（RVA <c>0x1D6AD3C</c>）——
    /// <c>parentId != 0</c>（<c>0x1D6ADE0</c>）且 <c>Type &lt; MinNormalItemId</c>
    /// （<c>0x1D6AE14 IsCustomItemId</c> = <c>id &gt;= 100</c>）且 <c>Items.ContainsKey(Type)</c> 时
    /// 会把 <c>Items[Type].Parent = 0; Room = 0</c>（<c>0x1D6AE7C</c>/<c>0x1D6AE94</c>），
    /// 即「把出厂默认件撤下」；而系统家具满足 <c>objId == templateId == Type</c>，
    /// <b>等于自己把自己撤下</b>（<c>Room = 0</c> ⇒ 从 <c>GetAreaItems</c> 的任何区域里消失）。
    ///
    /// 而 <c>NCafeDefautItems.txt</c> 那 13 件的 <c>Type</c> 全部 ≥ 100
    /// （101→100 餐位、401→103 墙饰、501/505/703→104 小件、704/705/706→201 大件）
    /// ⇒ <c>RecoverDefaultItem</c> 对它们<b>直接 return</b>，可以安全下发。
    /// 这也正是「为什么原厂那张服务端专用表只有 13 行」的答案。
    /// </summary>
    public IReadOnlyList<HouseItemRow> SystemItems => _systemItems;

    /// <summary>
    /// 一个 <c>objId</c> 是否会与「客户端自建的默认家具」撞车 ——
    /// 即它是否落在 <see cref="ConfigureSystemTypes"/> ∪ <see cref="ConfigureDefaultTypes"/> 里，
    /// 或者（配置读不到时）落在 <c>[1, <see cref="MaxSystemObjId"/>]</c>。
    /// 服务端下发前用它自检，理由见 <see cref="SystemItems"/>。
    /// </summary>
    public bool IsClientOwnedObjId(uint objId)
    {
        if (_configureSystemTypes.Count > 0 || _configureDefaultTypes.Count > 0)
        {
            int id = (int)objId;
            return _configureSystemTypes.Contains(id) || _configureDefaultTypes.Contains(id);
        }
        return objId >= 1 && objId <= MaxSystemObjId;
    }

    /// <summary>餐位对应的 <c>NCafeItems.Type</c> 值（<c>ItemTypes[1].Types[0]</c>，现网 100）。</summary>
    public int SeatType
        => _itemTypes.TryGetValue(SeatItemTypeId, out var row) && row.Types.Count > 0 ? row.Types[0] : 0;

    /// <summary>
    /// 校验一个 <c>templateId</c> 是否在 <c>NCafeItems.txt</c> 里。
    /// 表整体缺失时一律放行（否则会把「配置读不到」升级成「一件家具都发不出去」，
    /// 那比发了可能不认识的 id 更糟 —— 后者只影响单件，前者直接卡加载）。
    /// </summary>
    public bool IsKnownTemplate(int templateId)
        => _items.Count == 0 || _items.ContainsKey(templateId);

    /// <summary>
    /// 取一个家具模板（<c>NCafeItems.txt</c> 的一行）；不存在返回 null。
    /// 下发家具时用它读 <see cref="HouseItemRow.Parent"/> 等列 ——
    /// ★ 不要写死 parentId（第十二轮那个「parentid=0 让客户端挂不上场景树、
    /// 咖啡馆卡 40%」的真 bug 就是写死 0 造成的）。
    /// </summary>
    public HouseItemRow? ItemOf(int templateId)
        => _items.TryGetValue(templateId, out var row) ? row : null;

    /// <summary>
    /// 这个 <c>templateId</c> 是否会让 <c>NCafePoolController.LoadItem</c> 静默断链
    /// （即 <see cref="PoolBlockedTemplateIds"/> 里的 20/21）。
    /// 判据除了硬名单，还顺带按 <c>Res</c> 列复核一次：不以 <c>.prefab</c> 结尾就是危险的。
    /// </summary>
    public bool IsPoolBlockedTemplate(int templateId)
    {
        if (PoolBlockedTemplateIds.Contains(templateId))
            return true;
        // 只对「表里有、但 Res 不是 prefab」的行报警；系统家具由客户端自建、不经这条路，
        // 所以这里只需要覆盖服务端真的会下发的那些 templateId。
        return false;
    }

    /// <summary>
    /// 校验 <paramref name="parentId"/> 是否在该模板的 <see cref="HouseItemRow.DicArea"/> 白名单里。
    /// 模板未知或白名单为空时放行（空 = 不限制，见 <see cref="HouseItemRow.DicArea"/>）。
    /// </summary>
    public bool IsAllowedParent(int templateId, uint parentId)
    {
        if (!_items.TryGetValue(templateId, out var row) || row.DicArea.Count == 0)
            return true;
        return row.DicArea.Contains(parentId);
    }

    private static readonly object _cacheLock = new();
    private static readonly Dictionary<string, HouseTable> _cache = new(StringComparer.Ordinal);

    /// <summary>按 <paramref name="settingDir"/> 做进程级缓存的加载入口。</summary>
    public static HouseTable Load(string settingDir)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(settingDir, out var cached))
                return cached;
        }

        var loaded = LoadUncached(settingDir);

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(settingDir, out var cached))
                return cached;
            _cache[settingDir] = loaded;
            return loaded;
        }
    }

    private static HouseTable LoadUncached(string settingDir)
    {
        var table = new HouseTable();

        // ---- Configure.txt（JSON 总配置，先读：后面要用它筛「默认下发哪些系统家具」）----
        LoadConfigure(table, settingDir);

        // ---- NCafeItems.txt（家具模板全集）----
        // ★ 这张表的「中文列名行」（第 1 行）是 GBK 字节，用 UTF-8 读会得到替换字符，
        //    但 SettingTable 只用第 2 行（英文列名）建索引，所以不受影响。
        var items = SettingTable.Load(Path.Combine(settingDir, ItemsFileName));
        if (items is null)
        {
            Log.Warn($"[setting] {ItemsFileName} 缺失：无法校验 furniture.templateId，" +
                     "也拿不到系统家具清单（外景 72 / 大门 56 / 吧台 58 …）——" +
                     "pf 1048 只能回默认摆设，咖啡馆场景很可能仍进不去");
        }
        else
        {
            foreach (var row in items.Rows)
            {
                int id = items.Int(row, "Id", -1);
                if (id <= 0)
                    continue;
                var parsed = new HouseItemRow
                {
                    Id = id,
                    Type = items.Int(row, "Type", 0),
                    ThemeType = items.Str(row, "ThemeType"),
                    OwnMax = items.Int(row, "OwnMax", 0),
                    InnerSize = ParseUIntList(items.Str(row, "InnerSize")),
                    Size = ParseUIntList(items.Str(row, "Size")),
                    Parent = (uint)Math.Max(0, items.Int(row, "Parent", 0)),
                    DicPlace = items.Int(row, "DicPlace", 0),
                    DicArea = ParseUIntList(items.Str(row, "DicArea")),
                    Points = ParsePipeList(items.Str(row, "Points")),
                    Level = items.Int(row, "Level", 0),
                    AddScore = items.Int(row, "AddScore", 0),
                };
                table._items[id] = parsed;
            }

            foreach (var row in table._items.Values)
            {
                if (row.IsSystem && row.Id <= MaxSystemObjId)
                    table._systemItems.Add(row);
            }
            table._systemItems.Sort((a, b) => a.Id.CompareTo(b.Id));
        }

        // ---- NCafeItemTypes.txt（家具类别）----
        var types = SettingTable.Load(Path.Combine(settingDir, ItemTypesFileName));
        if (types is null)
        {
            Log.Warn($"[setting] {ItemTypesFileName} 缺失：无法确定餐位的 Type 值，" +
                     "座位数统计只能记 0（不影响下发，客户端自己从 biz_ 挂点算）");
        }
        else
        {
            foreach (var row in types.Rows)
            {
                int id = types.Int(row, "Id", -1);
                if (id <= 0)
                    continue;
                table._itemTypes[id] = new HouseItemTypeRow(
                    id,
                    types.Int(row, "Area", 0),
                    ParseIntList(types.Str(row, "Types")),
                    ParseIntList(types.Str(row, "Limit")));
            }
        }

        // ---- NCafeDefautItems.txt（新号默认摆设）----
        var defaults = SettingTable.Load(Path.Combine(settingDir, DefaultItemsFileName));
        if (defaults is null)
        {
            Log.Warn($"[setting] {DefaultItemsFileName} 缺失：新号将只有系统家具、没有餐位，" +
                     "咖啡馆里一个座位都摆不出来（顾客永远进不了店）");
        }
        else
        {
            foreach (var row in defaults.Rows)
            {
                int id = defaults.Int(row, "Id", -1);
                int templateId = defaults.Int(row, "ItemId", -1);
                if (id <= 0 || templateId <= 0)
                    continue;

                // Pos 列 = "parentId,posX,posY,rotate"（表头中文「物件位置Parent,X,Y,Rotate」）
                var pos = ParseUIntList(defaults.Str(row, "Pos"));
                if (pos.Count < 4)
                {
                    Log.Warn($"[setting] {DefaultItemsFileName} 第 Id={id} 行的 Pos 列不是 4 段" +
                             $"（实得 {pos.Count} 段），跳过该行");
                    continue;
                }

                table._defaults.Add(new DefaultFurnitureRow(
                    id, templateId,
                    defaults.Int(row, "Room", 0),
                    pos[0], pos[1], pos[2], pos[3],
                    defaults.Int(row, "BizCount", 0)));
            }
            table._defaults.Sort((a, b) => a.Id.CompareTo(b.Id));
        }

        int seatRows = 0;
        foreach (var d in table._defaults)
        {
            if (d.BizCount > 0)
                seatRows++;
        }
        Log.Info($"[setting] 家园配置：家具模板 {table._items.Count} 种、" +
                 $"系统家具 {table._systemItems.Count} 件（objId 1..{MaxSystemObjId}，★由客户端自建、服务端不发）、" +
                 $"类别 {table._itemTypes.Count} 组（餐位 Type={table.SeatType}）、" +
                 $"新号默认摆设 {table._defaults.Count} 件（含餐位 {seatRows} 张）、" +
                 $"Configure: SystemItems {table._configureSystemTypes.Count} + " +
                 $"DefaultItems {table._configureDefaultTypes.Count} 项、" +
                 $"MinNormalItemId={table._minNormalItemId}（本实现从 {MinPlayerObjId} 起分配）、" +
                 $"OutDoorScene={table.OutDoorScene}");
        return table;
    }

    /// <summary>
    /// 从 <c>Configure.txt</c> 里取 5 个平坦字段。
    /// 用正则而不是 JSON 解析器的理由见 <see cref="ConfigureFileName"/>（整数键无引号）。
    /// </summary>
    private static void LoadConfigure(HouseTable table, string settingDir)
    {
        string path = Path.Combine(settingDir, ConfigureFileName);
        if (!File.Exists(path))
        {
            Log.Warn($"[setting] {ConfigureFileName} 缺失：拿不到 DefaultItems（新号默认系统家具清单），" +
                     "退化为「NCafeItems 里 Id≥50 的系统家具全发」");
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e)
        {
            Log.Warn($"[setting] 读取 {ConfigureFileName} 失败：{e.Message}");
            return;
        }

        table._configureSystemTypes.AddRange(ExtractIntArray(text, "SystemItems"));
        int carpet = ExtractInt(text, "CarpetTypeId");
        if (carpet > 0) table.CarpetTypeId = carpet;
        var blockSection = System.Text.RegularExpressions.Regex.Match(text,
            "\"DicBlocks\"\\s*:\\s*\\{(.*?)\\}", System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (System.Text.RegularExpressions.Match entry in System.Text.RegularExpressions.Regex.Matches(
            blockSection.Groups[1].Value, @"(\d+)\s*:\s*\[((?:[^\[\]]|\[[^\[\]]*\])*)\]"))
        {
            if (!uint.TryParse(entry.Groups[1].Value, out uint parent)) continue;
            var blocks = new List<int[]>();
            foreach (System.Text.RegularExpressions.Match rect in System.Text.RegularExpressions.Regex.Matches(entry.Groups[2].Value, @"\[([^\[\]]*)\]"))
            {
                var values = ParseIntList(rect.Groups[1].Value);
                if (values.Count == 4 && values[0] <= values[2] && values[1] <= values[3]) blocks.Add(values.ToArray());
            }
            table.Blocks[parent] = blocks;
        }
        table._configureDefaultTypes.AddRange(ExtractIntArray(text, "DefaultItems"));
        table._minNormalItemId = ExtractInt(text, "MinNormalItemId");
        table._outDoorScene = ExtractInt(text, "OutDoorScene");

        if (table._minNormalItemId > 0 && MinPlayerObjId < (uint)table._minNormalItemId)
        {
            Log.Warn($"[setting] MinPlayerObjId({MinPlayerObjId}) 小于 {ConfigureFileName} 的 " +
                     $"MinNormalItemId({table._minNormalItemId})：玩家家具的 objId 会被客户端当成系统家具");
        }
    }

    /// <summary>取 <c>"Key" : [1,2,3]</c> 里的整数数组（找不到返回空表）。</summary>
    internal static List<int> ExtractIntArray(string json, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            json, "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*\\[([^\\]]*)\\]");
        return m.Success ? ParseIntList(m.Groups[1].Value) : new List<int>();
    }

    /// <summary>取 <c>"Key" : 123</c> 里的整数（找不到返回 0）。</summary>
    internal static int ExtractInt(string json, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            json, "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    /// <summary>解析 <c>[1,2,3]</c> / <c>1,2,3</c> 形态的整数列表（方括号可选，空串 → 空表）。</summary>
    internal static List<int> ParseIntList(string raw)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;
        foreach (var part in raw.Trim().Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out var v))
                result.Add(v);
        }
        return result;
    }

    /// <summary>解析同上，但产出 <c>uint</c>（负数丢弃 —— proto 里这些字段都是 uint32）。</summary>
    internal static List<uint> ParseUIntList(string raw)
    {
        var result = new List<uint>();
        foreach (var v in ParseIntList(raw))
        {
            if (v >= 0)
                result.Add((uint)v);
        }
        return result;
    }

    /// <summary>解析 <c>a|b|c</c> 形态的字符串列表（<c>Points</c> 列）。</summary>
    internal static List<string> ParsePipeList(string raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;
        foreach (var part in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = part.Trim();
            if (t.Length > 0)
                result.Add(t);
        }
        return result;
    }
}

/// <summary>
/// 家园域的两个「必须下发」清单（都不是配置表能表达的，所以单列成常量并各自带取证）。
/// </summary>
public static class HouseRooms
{
    /// <summary>
    /// <b>必须下发的 <c>roomData</c> 区域 id</b>（<c>Game.NCafeAreaType</c> 的实值，
    /// <c>dump.cs:534518..534528</c>）：<c>1</c> 咖啡馆经营区 / <c>2</c> 休息室 / <c>6</c> 女生宿舍。
    ///
    /// 为什么<b>不能空</b>：<c>NCafeDataController.ReqSaveArea</c>（RVA <c>0x1D6B11C</c>）
    /// 在有改动时调 <c>GetArea(type)</c>，紧接着
    /// <c>0x1D6B3AC cbnz x21,+12; mov x0,xzr; bl #0x13F30AC</c> —— <b>无 nil 保护</b>，
    /// 玩家一保存装修就 NRE。而 <c>Areas</c> 的唯一填充来源就是 <c>roomList</c>
    /// （<c>OnUpdateRoom</c> RVA <c>0x1D696F0</c>）。
    ///
    /// 为什么<b>只有这三个</b>：<c>3/4/7</c>（<c>CafeEditor</c>/<c>RestEditor</c>/<c>DormEditor</c>）
    /// 是编辑器态镜像、<c>5</c>（<c>Outdoor</c>）是外景、<c>0</c>（<c>Player</c>）不是房间。
    /// 五方交叉印证：
    /// <list type="bullet">
    ///   <item><c>NCafeNet.lua:5..9</c> 的 <c>AreaType = {Cafe, Rest, Dorm}</c>（只对这三个区做保存/上传）</item>
    ///   <item><c>Configure.txt:20..22</c> 的 <c>CafeFloor=2 / RestFloor=10 / DormFloor=30</c>
    ///     —— 三个房间各有一块地板容器节点，且同房间的墙 id = 地板 id + (1..4)
    ///     （<c>{2:3,4,5,6}</c> / <c>{10:11..14}</c> / <c>{30:31..34}</c>），
    ///     合起来正好等于 <c>WallTypeIds</c> 那 12 个值</item>
    ///   <item><c>NCafeThemes.txt</c> 的 <c>Area</c> 列取值恰为 <c>{1,2,6}</c></item>
    ///   <item><c>NCafeItemTypes.txt</c> 的 <c>Area</c> 列 + <c>Name</c>（「咖啡馆-*」/「宿舍1-*」/「宿舍2-*」）</item>
    ///   <item><c>NCafeItems.txt</c> 的 <c>DicPlace</c> 列（表头「摆放范围 0-全部 1-咖啡馆 2-休息室 6-女生宿舍」）</item>
    /// </list>
    /// ★ <b>不要</b>把 <c>Configure.txt:54</c> 的 <c>WallTypes {1:[51..56], 2:[61..64], 6:[81..84]}</c>
    /// 当成这条的证据：那三个键是<b>墙纸分组</b>而不是房间号（只是分组恰好按房间划分）。
    /// 判据是同目录的 <c>Configure说明.txt</c>（打包时 GBK 被按 UTF-8 导入已损坏，
    /// 但字节级比对能反推出「<c>WallTypes</c> 咖啡馆所有墙的类型，需要跟墙纸…」
    /// 与「<c>FirstWallType</c> 咖啡馆第一面墙的类型」两行，原始字节存
    /// <c>work/ncafe/Configure说明.raw.txt</c>）；旁证是
    /// <c>FirstWallType = [51, 62, 81]</c> 第二组取的是 <b>62 而不是组内最小的 61</b> ——
    /// 因为 <c>61</c> 在 <c>DoorWindow = [55,56,61,64,81,84]</c> 里，门窗不能当默认墙纸。
    /// </summary>
    public static IReadOnlyList<int> Ids { get; } = new[] { 1, 2, 6 };

    /// <summary>
    /// 参考：<b>客户端自建那 54 件的实际 <c>Room</c> 分布</b>（逐条核对 <c>NCafeItems.DicPlace</c> 列，
    /// 赋值点 <c>InitFixedData</c> <c>0x1D616F8</c> / <c>InitDefaultData</c> <c>0x1D61A08</c>：
    /// <c>it.Room = (uint)tpl.DicPlace[0]</c>）：
    /// <list type="bullet">
    ///   <item><b><c>0</c> Player</b>：模板 <b>1</b>（<c>SceneRoot</c>，<c>DicPlace</c> 列<b>为空</b>）、
    ///     <b>72 外景</b>、<b>73 灯光</b></item>
    ///   <item><c>1</c> Cafe：2..9, 22, 23 / 50,51,52,54,55,56,57,58,59</item>
    ///   <item><c>2</c> Rest：10..19 / 60..65</item>
    ///   <item><c>5</c> Outdoor：66,67,68,69（四个训练位）</item>
    ///   <item><c>6</c> Dorm：30..35 / 80..85</item>
    /// </list>
    ///
    /// ★★ 两条容易写错的：
    /// <list type="number">
    ///   <item><b>外景 72 与灯光 73 的 <c>Room</c> 是 <c>0</c>（Player），不是 1</b> ——
    ///     别把 <c>DicPlace = 0</c>（表头「摆放范围 0-全部」）理解成「归到经营区」；
    ///     客户端<b>原样保留 0</b>。它们靠 <c>GetItemsByType</c>（<b>不筛 room</b>，
    ///     RVA <c>0x1D63118</c>）被找到，所以归 0 不影响功能。
    ///     ⇒ 服务端也<b>不要</b>做「<c>DicPlace 0 → roomId 1</c>」这种映射。</item>
    ///   <item>模板 <b>1</b> 是<b>唯一</b> <c>DicPlace</c> 为空的模板，而
    ///     <c>InitFixedData</c> 在 <c>0x1D616E0</c> 判 <c>dp.Length == 0</c> 就
    ///     <b>抛 IndexOutOfRange</b>（<c>0x1D616E8</c>）——
    ///     原厂正是用 <c>Configure.NoRoomItems = [1]</c>（<c>0x1D616C8 Contains</c>）
    ///     把它豁免掉的。<b>别给模板 1 补 <c>DicPlace</c>，也别把 1 从 <c>NoRoomItems</c> 删掉。</b></item>
    /// </list>
    /// 本域下发的 13 件玩家家具直接用 <c>NCafeDefautItems.Room</c> 列（只有 1 和 2），
    /// 不经这套推算 —— 这张表只是排查「某件系统家具怎么不在这个区」时的对照。
    /// </summary>
    public static IReadOnlyList<int> ClientOwnedRoomZero { get; } = new[] { 1, 72, 73 };

    /// <summary>
    /// 三个「场景挂点宿主」的系统家具 <c>Type</c>（<c>NCafePoints.txt</c>）——
    /// 客户端自建，服务端<b>不发</b>，这里只记录取证，供排查「顾客刷不出来 / 少女不做咖啡」时对照：
    /// <list type="bullet">
    ///   <item><c>56</c> 咖啡馆-大门 —— <c>EnterDoor</c>(Id=4) 进门点 +
    ///     <b><c>GuestBrith</c>(Id=5) 顾客出生点</b>（两条的 <c>ItemType</c> 都是 56）</item>
    ///   <item><c>58</c> 咖啡馆-吧台 —— <c>Batai</c>(Id=6) 点餐点，<c>Count=3</c></item>
    ///   <item><c>59</c> 吧台墙 —— <c>MakeCoffee</c>(Id=7) 妹子做咖啡点，<c>Count=3</c></item>
    /// </list>
    /// 三者都在 <c>Configure.txt:3</c> 的 <c>DefaultItems</c> 里，所以新号必然具备。
    /// </summary>
    public static IReadOnlyList<int> ScenePointHostTypes { get; } = new[] { 56, 58, 59 };
}
