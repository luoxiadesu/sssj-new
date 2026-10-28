using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 家园 / 家具摆设域：<b>pf 1048 <c>GetHouseInfoReq</c> → pf 1049 <c>GetHouseInfoRsp</c></b>。
///
/// <para><b>★ 0. 为什么需要这个域：咖啡馆（map 6）加载条卡 0.4 的第二层根因</b></para>
/// 完整取证在 <c>work/cafe-scene-forensics.md</c> 与 <c>docs/05-feature-domains.md §15</c>。
/// 简版：LuaCall 112 的六组清单补齐后，logcat 里
/// <c>LUA: loaded map:\t6\tfalse</c>（<c>Map.lua:49</c>）已出现、
/// 既无 <c>failed to enter map: 6</c> 也无 <c>cannot find guest template</c>
/// ⇒ 卡点在 <c>SetProgress(0.4)</c>（<c>NCafeManager.&lt;onEnter&gt;d__34</c> state0，
/// RVA <c>0x1D3147C</c>）之后的三句：<c>NCafeSceneController.ChangeArea</c>(<c>0x1A460C4</c>) /
/// <c>InitObjects</c>(<c>0x1A436E4</c>) / <c>RandomSun</c>(<c>0x1A4CC60</c>)。
/// 它们吃的是家具数据，而服务端此前对 pf 1048 回的是<b>空 <c>HouseCache</c></b>
/// （<c>GameModule.cs:402</c> <c>rsp.HouseInfo = new HouseCache()</c>），
/// 于是 <c>NCafeDataController.Items</c>/<c>Areas</c>/<c>Themes</c> 三个字典被
/// <c>UpdatePlayerHouseInfo</c>（RVA <c>0x1D68B04</c>）清空后什么都没填上。
///
/// <para><b>★ 1. 客户端怎么消费 pf 1049（两个 handler 都会跑）</b></para>
/// <list type="number">
///   <item><c>Game.NCafeNetController.RsqHouseInfos</c>（RVA <c>0x1D31C1C</c>）—— 咖啡馆那一路：
///     <code>
///     if (code != 0) { MessageBox.Simple(Localization.Get("ui.Error.{code}")); return; }   // 0x1D31CA4
///     if (rsp.roleid != Player.me.id) return;                                              // 0x1D31DA8 ★
///     NCafeController.Data.UpdatePlayerHouseInfo(rsp);                                     // 0x1D31DD0
///     GlobalEvent.DispatchLuaEvent(17, [1]);                                               // 0x1D31E88
///     if (NCafeManager.InScene) Lua.Call("CafeLogic.BackToHome");                          // 0x1D31F00
///     </code>
///     ★ <c>roleid</c> 必须等于玩家自己的 id，否则<b>整包被静默丢弃</b>（连日志都没有）。
///     所以本实现回的是 <c>player.Roleid</c>（服务端权威），不是 <c>req.Roleid</c>。</item>
///   <item><c>Game.HouseManager.OnGetHouseInfoRsp</c>（RVA <c>0x15A7790</c>）—— 宿舍 DIY 那一路：
///     缓存 <c>houseInfo</c>、若是自己则遍历 <c>furnitureList</c> 调
///     <c>RoomDiyTask.UpdateObjInfo</c>（<c>0x1B9F548</c>）+ <c>UpdateObjList</c>。</item>
/// </list>
///
/// <para><b>★ 2. required 位掩码（少一个字段整包 Parse 失败，AGENTS.md §5 / docs/05 §0.0b）</b></para>
/// <list type="bullet">
///   <item><c>GetHouseInfoRsp.IsInitialized</c> RVA <c>0x31EFAF8</c>：
///     <c>ldr w8,[x0,#0x14]; mov w9,#3; bics wzr,w9,w8</c> ⇒ 掩码 <b><c>0x3</c></b>
///     ⇒ required = <b><c>roleid</c>(#1) + <c>houseInfo</c>(#2)</b>，<c>visitinfo</c>(#3) 是 optional。</item>
///   <item><c>HouseCache</c>(<c>0x31F4104</c>) / <c>furniture</c>(<c>0x359AAF4</c>) /
///     <c>objPose</c>(<c>0x359B5DC</c>) / <c>roomData</c>(<c>0x359C440</c>) /
///     <c>decorationTheme</c>(<c>0x3599D04</c>) / <c>VisitedData</c>(<c>0x359740C</c>) /
///     <c>VisitedInfo</c>(<c>0x3597F54</c>) 全是 <c>mov w0,#1</c> ⇒ <b>恒真、零 required</b>。</item>
/// </list>
/// ⇒ 协议层只强制两个字段；真正的「必填」全在<b>逻辑层</b>（下面 §3）。
///
/// <para><b>★ 3. 五个「无 nil 保护 / 静默失败」点（本域的硬指标）</b></para>
/// <list type="number">
///   <item><b><c>furniture.pose</c> 必填</b> —— <c>NCafeItem.UpdateInfo</c>（RVA <c>0x1D2C68C</c>）
///     在 <c>0x1D2C92C get_pose</c> 之后立刻 <c>0x1D2C944 objPose.get_posX</c>
///     （posY <c>0x1D2C97C</c> / rotate <c>0x1D2C9B4</c> 同样），中间只有抛 NRE 的 null 检查
///     （<c>0x1D2C934 cbnz x21,+8; bl #0x13F30AC</c>）。<c>has_pose</c> 在整条链路一次都没被调用。
///     <b>不带 pose = 每件家具都在 <c>UpdatePlayerHouseInfo</c> 的循环里抛。</b></item>
///   <item><b><c>templateId</c> 必须在 <c>NCafeItems.txt</c> 里</b> ——
///     <c>OnUpdateItem</c>（RVA <c>0x1D692A0</c>）开头
///     <c>0x1D69364 ItemTemplate(templateId) == null</c> 时打
///     <c>NCafe: cannot find item template, id {0} from net</c> 并 <b>直接丢弃这条家具</b>
///     （<c>0x1D69514</c>）。这条日志是排查时的硬判据（logcat 里 grep 它）。</item>
///   <item><b><c>roomId</c> 是唯一的区域归属字段，<c>roomList</c> 不是</b> ——
///     <c>NCafeDataController.GetAreaItems</c>（RVA <c>0x1D6266C</c>）遍历 <c>Items.Values</c>
///     只比 <c>item.Room == areaType</c>（<c>0x1D627B0</c>），返回 <c>new List&lt;NCafeItem&gt;()</c>
///     （<b>永不为 null</b>，所以 <c>NCafe.lua:127/168</c> 的 <c>pairs(its)</c> 本身是安全的）。
///     <c>roomId</c> 填错 = 餐位摆到宿舍去，座位数变 0，顾客永远进不了店 —— 静默失败。</item>
///   <item><b><c>roomList</c> 不能为空</b>（至少要有 <c>roomId=1</c>）——
///     <c>NCafeDataController.ReqSaveArea</c>（RVA <c>0x1D6B11C</c>）在有改动时调
///     <c>GetArea(type)</c>，紧接着 <c>0x1D6B3AC cbnz x21,+12; mov x0,xzr; bl #0x13F30AC</c>
///     <b>无 nil 保护</b> ⇒ 玩家一保存装修就 NRE。<c>Areas</c> 的唯一填充来源就是
///     <c>roomList</c>（<c>OnUpdateRoom</c> RVA <c>0x1D696F0</c>）。见 <see cref="HouseRooms.Ids"/>。</item>
///   <item><b><c>objId</c> 重复 = 静默覆盖</b> —— <c>Items</c> 是
///     <c>Dictionary&lt;uint, NCafeItem&gt;</c> 且 <c>objId</c> 就是 key
///     （<c>0x1D693E0 TryGetValue</c> / <c>0x1D69580 set_Item</c>），
///     重复时走「已存在」分支 <c>UpdateInfo(data2)</c>，后者覆盖前者、不抛、也不再补建附属件。</item>
/// </list>
///
/// <para><b>★ 4. 下发内容：只发 <c>NCafeDefautItems.txt</c> 那 13 件（objId 1000..1012）</b></para>
/// <b>系统家具由客户端自建，服务端一件都不发</b> —— 取证见
/// <see cref="HouseTable.SystemItems"/>：<c>UpdatePlayerHouseInfo</c> 在
/// <c>!NCafeManager.InScene</c> 时（登录时的 pf 1048 必然满足）先
/// <c>Items.Clear()</c> 再 <c>OnInit()</c> = <c>InitFixedData</c>(<c>0x1D61500</c>) +
/// <c>InitDefaultData</c>(<c>0x1D61834</c>)，照 <c>Configure.txt</c> 的
/// <c>SystemItems</c>(27) + <c>DefaultItems</c>(27) 建 <b>54 件</b>，
/// 每件 <c>Guid = templateId</c>、<c>Parent = tpl.Parent</c>、<c>Room = tpl.DicPlace[0]</c>，
/// <b>外景 72、大门 56、吧台 58、吧台墙 59 都在其中</b>。
/// 回传它们反而会踩 <c>RecoverDefaultItem</c>（<c>0x1D6AD3C</c>）把默认件 <c>Room</c> 清 0。
///
/// 服务端要发的是那 13 件 <b>玩家家具</b>（<c>templateId</c> 全 ≥ 100、<c>Type</c> 全 ≥ 100
/// ⇒ <c>IsCustomItemId</c> 为真 ⇒ <c>RecoverDefaultItem</c> 直接 return，可安全下发）：
/// <c>401</c>×2 墙饰、<c>501</c>×2 + <c>505</c> 小件、<b><c>101</c>×4 餐位</b>（<c>BizCount=1</c>）
/// 在经营区，<c>703/704/705/706</c> 在休息室。见 <see cref="DefaultFurnitureRow"/>。
///
/// <c>roomList</c> 固定发 <see cref="HouseRooms.Ids"/> = <c>{1, 2, 6}</c>；
/// <c>themeList</c> 下发独立保存的自定方案（模板编号0走客户端自定分支）；
/// <c>petList</c> 留空 —— 后者要注意：<c>UpdatePlayerHouseInfo</c> 末尾会
/// <c>Pet.dic.Clear()</c> 再按 <c>petList</c> 重建，但 pf 1048 发生在登录期、
/// 而宠物数据是进店时 LuaCall 112 的 <c>petstatelist</c> + <c>UpdatePet</c> 才下发的
/// （见 <see cref="CafePetFeature"/>），顺序上 1048 在前，所以清空无害。
///
/// <para><b>★ 5. 持久化：<c>Player.Extra["house"]</c>（新键）</b></para>
/// <b>不能</b>并进 <c>["cafe"]</c> —— <c>CafeFeature.WriteState</c> 每次进店都整体覆写那个键；
/// 也不是 <c>["cafepet"]</c>（宠物域）。三个键互不相干，有单测把关。
///
/// <para><b>★ 6. 注册方式：<c>ctx.MapPf</c> 覆盖 <c>GameModule</c></b></para>
/// pf 1048 原本由 <c>GameModule.Install</c>（<c>GameModule.cs:114</c>）注册成「回空 <c>HouseCache</c>」。
/// 本域用 <c>ctx.MapPf</c>（<c>GameFeature.cs:120</c> → <c>PfRouter.Map</c>，
/// <c>PfRouter.cs:16</c> 是 <c>_map[pf] = handler</c> <b>覆盖赋值</b>）重新注册；
/// 而 feature 的反射装配（<c>GameModule.cs:171</c>）发生在自己那批 <c>router.Map</c>
/// （<c>GameModule.cs:110..118</c>）<b>之后</b> ⇒ 后注册的胜出。
/// 这条有专门的单测（<c>HouseFeatureTests.MapPf_覆盖GameModule的空HouseCache</c>）盯着，
/// 因为它是「不改 GameModule 也能接管 pf」的前提。
///
/// <para><b>★ 7. 本域做完之后，map 6 还可能卡 0.4 —— 但剩下的都不是服务端能修的</b></para>
/// <c>NCafeManager.&lt;onEnter&gt;d__34</c> state0 那一段（0.4 → 0.6）逐指令核对下来
/// （完整 997 行明细见 <c>work/house-domain-forensics-b.md</c>），能让它停住的点共 7 个，
/// 其中<b>只有 2 个是服务端能踩的</b>，且本实现都已避开：
/// <list type="number">
///   <item><b><c>furniture.pose</c> 缺失</b> ⇒ pf 1048 当场 NRE（§3 第 1 条）—— 已保证必带；</item>
///   <item><b><c>templateId = 20 / 21</c></b> ⇒ <c>LoadItem</c>(<c>0x1A3BD10</c>) 静默断链、
///     <b>加载条永远 0.4 且 logcat 连 Error 都没有</b>（唯一线索是一条 Debug
///     <c>[NcafePool.LoadItem] 物件路径不合法, path=SceneRoot/Cafe/Light</c>）——
///     见 <see cref="HouseTable.PoolBlockedTemplateIds"/>，已显式黑名单。</item>
/// </list>
/// 剩下 5 个全是<b>本地资源 / 场景</b>问题（CDN 停服后资源快照不全，AGENTS.md §3.1）：
/// <list type="bullet">
///   <item><b>头号嫌疑</b>：<c>RandomSun</c>(<c>0x1A4CC60</c>) 的 <c>light.Pool</c> NRE
///     （<c>0x1A4CD2C</c>）—— 灯光 prefab <c>Prefabs/DIY/Light/light_001.prefab</c>（templateId 73）
///     加载失败。判据：logcat 出现 <c>NCafeItem id[73] load failed, please check res path</c>
///     紧跟一条 <c>NullReferenceException</c>。
///     <para>★★ <b>这条的触发条件是反直觉的</b>：<c>Utils.GetAreaLightItem</c>(<c>0x1807C68</c>)
///     先 <c>GetItemsByType(73)</c>，<c>Count == 0</c> 时返回 null（<c>0x1807D54</c>）⇒
///     <c>RandomSun</c> 在 <c>0x1A4CD08</c> 早退、<b>安全</b>；但列表非空而每一件
///     <c>IsInSceneAndActive</c> 都为 false 时，它 <b>仍然</b> <c>idx = 0</c>
///     （<c>0x1807DCC</c>）把那件 <c>Pool == null</c> 的家具原样交出去 ⇒ 必抛。
///     即「<b>完全没有灯光家具反而安全，有灯光但 prefab 没加载出来才炸</b>」。
///     灯光 73 属于客户端自建的 <c>DefaultItems</c>，服务端既发不了也删不掉这一件。</para></item>
///   <item><c>NCafeSceneController.OnEnter</c>(<c>0x1A3E5FC</c>) 里
///     <c>GameObject.Find("SceneRoot")</c> 为 null ⇒ <c>0x1A3E6FC</c> NRE，
///     而 <c>NCafeManager.OnEnter</c> 的 controller 循环 <b>catch 掉异常只打 Logger.Exception</b>
///     并 continue（<c>0x1D3067C</c>）⇒ <c>DicEditMask</c>/<c>areaRoot</c>/<c>sceneRoot</c> 全为 null，
///     state0 第一句 <c>ChangeMask</c> 在 <c>0x1A46494</c> 再 NRE。
///     同理 <c>new NCafeTree()</c> 抛异常 ⇒ <c>SceneTree</c> 为 null ⇒
///     <c>LoadSceneTreeItems</c> 在 <c>0x1A438D4</c> NRE。</item>
///   <item><c>UpdateScenePoint</c>(<c>0x1A3F5F0</c>)：<c>trainRoot</c> 为 null（<c>0x1A3F6C4</c>）
///     或 <c>trainRoot[1..4]</c> 的 value 为 null（<c>OnEnter 0x1A3EF00</c> 的
///     <c>Add(k, Find(...))</c> 不判空）⇒ <c>UpdatePointPos</c> 在 <c>0x1A3F4F4</c> NRE。</item>
///   <item><c>UpdateWallMaterial</c>：<c>Assets.Load&lt;Material&gt;(".../Cafe/Never.mat")</c>
///     返回 null ⇒ <c>Instantiate(null)</c> ⇒ ArgumentException（<c>0x1A406E8</c>）。</item>
///   <item><c>FindWallMaterial</c>(<c>0x1804CF4</c>) 的 <c>GetComponent&lt;MeshRenderer&gt;()</c>
///     无判空 ⇒ <c>0x1804DA8</c> NRE：首墙 prefab（<c>Configure.FirstWallType = [51,62,81]</c>）
///     的根节点必须挂 MeshRenderer。</item>
/// </list>
///
/// <para><b>★ 7b. 一条「看到了也不要误判」的 logcat 噪声</b></para>
/// <c>ChangeArea</c>(<c>0x1A460C4</c>) 里对 <c>Configure.WallTypeIds</c> 那 12 面墙调
/// <c>DoWallVisualAnimation</c>(<c>0x1A46888</c>)，其中 <c>Data.GetItem(wallTypeId)</c> 与紧随的
/// <c>item.Pool</c>(+0x50) <b>都没有 nil 保护</b>（<c>0x1A46B3C</c>/<c>0x1A46B44</c>）。
/// 进店第一次 <c>ChangeArea</c> 时那些墙的 <c>Pool</c> 必然还是 null ⇒
/// <b>必然抛一次 NRE，但整个循环套在 try/catch 里</b>（<c>0x1A46294..0x1A462F8</c>
/// <c>Logger.Exception</c>）⇒ 流程照常继续。
/// ⇒ <b>logcat 里看到 <c>ChangeArea</c> 附近的 NRE 是正常噪声，不是卡 0.4 的元凶</b>；
/// 别据此改服务端数据。
///
/// 顺带两条与判读有关的事实：① state0 传给 <c>ChangeArea</c> 的 area 是<b>硬编码常量</b>
/// <c>Cafe(1)</c>（<c>0x1D314F0 mov w1,#1</c>），不是 <c>Data.Area</c>；
/// ② <c>InitObjects</c> 传给 <c>LoadSceneTreeItems</c> 的 area 是 <c>Player(0)</c>，
/// 这会<b>关闭</b>区域过滤（<c>b__1</c> 的 <c>0x1A4E46C cmp w8,#1; b.lt</c>）——
/// 所以 0.4 这一段加载的是整棵树的全部非系统家具，<c>roomId</c> 填错<b>不影响这一趟</b>
/// （它影响的是 §3 第 3 条那条 Lua 侧的座位统计）。
///
/// <para><b>★ 7c. 排查用的 logcat 字面量清单（都带 RVA，出处唯一）</b></para>
/// <code>
/// NCafe: cannot find item template, id {0} from net   OnUpdateItem        0x1D69510  ★服务端 templateId 不在表里
/// NCafeItem id[..] load failed, please check res path LoadItem            0x1A3BCD0  prefab 加载失败（资源侧）
/// [NcafePool.LoadItem] 物件路径不合法, path=            LoadItem            0x1A3BD10  ★★静默断链（templateId 20/21）
/// The Object you want to instantiate is null          UpdateWallMaterial  0x1A406E8  缺 Materials/Cafe/Never.mat
/// NCafe: cannot find NCafeSetting {0}.                GetNCafePointPos    0x1A405AC  （有 nil 保护，不致命）
/// NCafe: cannot find action settings {0}.             RenderSetting       0x1A4BFC4  调用侧 0x1A4432C 无保护
/// NCafe: cannot find AddObject {0}                    HandleAddObjectOnLoad 0x1A445B8
/// NCafe: Item cannot find Item {0}                    HandleAddObjectOnLoad 0x1A445F0
/// NCafe: cannot create itemclick {0} {1}              HandleItemClick     0x1A44EB4  （不抛）
/// 场景树中找不到父节点                                  OnAddDiyObj         0x1A42AF0
/// 找不到家具数据，guid=                                 OnAddDiyObj         0x1A42E4C
/// Failed to process message. PF : 1048                —— ★pose 缺失（发生在 0.4 之前）
/// </code>
/// 一条命令验收（注意 <c>Map.lua</c> 那几条判据是<b>制表符</b>分隔，见
/// <c>docs/05</c> §15.1）：
/// <code>
/// adb -s 127.0.0.1:16416 logcat -c
/// tools/verify_cafe.sh
/// adb -s 127.0.0.1:16416 logcat -d | grep -nE "cannot find item template|load failed, please check res path|物件路径不合法|cannot find AddObject|cannot find NCafeSetting|cannot find action settings|场景树中找不到父节点|找不到家具数据|instantiate is null|NullReferenceException|start load map"
/// </code>
/// ⇒ 若本域上线后加载条仍停 0.4，先 grep 上面这几条 logcat 判据；
/// 服务端侧只需确认「pf 1048 回包里 13 件家具、每件带 pose、templateId 无 20/21」。
/// </summary>
public sealed partial class HouseFeature : IGameFeature
{
    /// <summary>
    /// 存档键。★ <b>新键</b>，与 <c>CafeFeature.ExtraKey</c>(<c>"cafe"</c>) /
    /// <c>CafePetFeature.ExtraKey</c>(<c>"cafepet"</c>) 都不同 —— 理由见类注释 §5。
    /// </summary>
    internal const string ExtraKey = "house";

    // ================================================================
    //  装修域的 LuaCall（126/127/128/137/138）—— 取证 work/cafe-biz-forensics.md §N
    // ================================================================

    /// <summary>LuaCall.CafeHouse_BuyTheme = 126（`LuaCall.lua:1848`「购买家具/主题」）。</summary>
    public const int BuyThemeCmd = 126;

    /// <summary>LuaCall.CafeHouse_CheckTheme = 127（`LuaCall.lua:1905`「检查是否已拥有该主题」）。</summary>
    public const int CheckThemeCmd = 127;

    /// <summary>LuaCall.CafeHouse_ClickItem = 128（`LuaCall.lua:1916`「点击某物品」）。</summary>
    public const int ClickItemCmd = 128;

    /// <summary>LuaCall.CafeHouse_SellFur = 137（`LuaCall.lua:1871`「卖家具」）。</summary>
    public const int SellFurCmd = 137;

    /// <summary>LuaCall.CafeHouse_SetMusic = 138（`LuaCall.lua:1889`「设置主题音乐」）。</summary>
    public const int SetMusicCmd = 138;

    /// <summary>
    /// ★ 126 成功时的**裸字符串**回包（不是对象！）。
    /// 取证 `LuaCall.lua:1859` <c>if tbParam == 'buy_theme_succeed' then</c>
    /// → 客户端关掉 `UI_CafeItemTips` 并让 `UI_NCafeShop` 刷新列表（`:1865`）。
    /// </summary>
    public const string BuyThemeOk = "buy_theme_succeed";

    /// <summary>
    /// ★★ 127 的回包也是**裸字符串**，且是 <c>'true'</c>/<c>'false'</c> 的**字符串字面量**。
    /// 取证 `UI_CafeItemTips.lua:633/635`：
    /// <code>
    /// if self.HaveTheme == 'true' then      Shop_Canbuy_MaxNum.text = '1 / 1'
    /// elseif self.HaveTheme == 'false' then Shop_Canbuy_MaxNum.text = '0 / 1'
    /// </code>
    /// ⇒ 回布尔 <c>true</c> 会因为 Lua 里 <c>true == 'true'</c> 是 **false**
    /// 而让两个分支都不成立 ⇒ 界面数量**静默显示不出来**。
    /// </summary>
    public const string HaveThemeTrue = "true";

    /// <summary>见 <see cref="HaveThemeTrue"/>。</summary>
    public const string HaveThemeFalse = "false";

    /// <summary>主题音乐存在 <c>Player.Extra["house"]</c> 的这个键里。</summary>
    internal const string MusicKey = "music";

    /// <summary>已购主题清单存在 <c>Player.Extra["house"]</c> 的这个键里。</summary>
    internal const string OwnedThemesKey = "themes";

    /// <summary>咖啡馆装修用的货币 = 金币（与 111 升级同源，`CoffeeCommon.GetCurGold` 读 `me.gold`）。</summary>
    public const int GoldMoneyId = 2;

    // ================================================================
    //  委托任务（129/131/132）—— 130/133 论证为不可做，见各 handler 注释
    // ================================================================

    /// <summary>LuaCall.CafeHouse_OpenEntrust = 129（`LuaCall.lua:1931`「开启委托任务状态」）。</summary>
    public const int OpenEntrustCmd = 129;

    /// <summary>LuaCall.CafeHouse_CompleteEntrust = 131（`LuaCall.lua:1961`「完成委托任务」）。</summary>
    public const int CompleteEntrustCmd = 131;

    /// <summary>LuaCall.CafeHouse_AwardEntrust = 132（`LuaCall.lua:1971`「委托积分领奖」）。</summary>
    public const int AwardEntrustCmd = 132;

    /// <summary>
    /// LuaCall.CafeHouse_SetVisiteGirl = 134（「设置访问少女」，第十三轮补）。
    /// 上行点两处：<c>UI_CafeVisitSetting.lua:102</c> 与 <c>UI_CafeVisitMain.lua:553</c>，
    /// 都是 <c>{ GirlId = &lt;少女id&gt; }</c>。
    /// </summary>
    public const int SetVisiteGirlCmd = 134;

    /// <summary>
    /// 访问少女存在委托 group 的这个任务值 id = **1**
    /// （`CafeLogic.lua:29` <c>CafeLogic.VisitGirl = 1</c>）。
    /// 客户端读取点：`UI_CafeVisitMain.lua:511..513`
    /// <c>Player:GetTaskValue(CafeLogic.EntrustTask, CafeLogic.VisitGirl)</c>。
    /// </summary>
    public const int VisitGirlTaskId = 1;

    /// <summary>
    /// ★★★ 音乐节外景的家具 templateId = <b>1400</b>（第十三轮补，**音游玩法的唯一入口开关**）。
    ///
    /// 取证：客户端咖啡馆里的「音游」按钮显隐judge是
    /// <code>
    /// UI_NCafe.lua:556  if MusicActivityLogic:IsMusicGameOpen() or MusicActivityLogic:HasByMusicScene() then
    ///                       self.objMusicGame:SetActive(self.IsShowOptions)
    ///                   else  self.objMusicGame:SetActive(false)  end
    /// MusicActivityLogic.lua:703..712  IsMusicGameOpen() —— 走 tbMusicTowerTotalConfig 时间窗（★ 已过期）
    /// MusicActivityLogic.lua:714..716  HasByMusicScene() —— 只看
    ///     Game.NCafeManager.Data:GetItemByTemplate(1400).Count &gt; 0
    /// </code>
    /// ⇒ **档期那条恒 false，但只要玩家持有这件家具，音游按钮就常驻可见**。
    /// 这是「过期活动的玩法本体仍然可玩」的一个实例 —— 判死活时值得多找一条旁路。
    ///
    /// 表里的定义（<c>NCafeItems.txt</c>）：
    /// <c>Id=1400  ItemInfo=11-19-5-1  Explain=音乐节外景  Type=72(外景)  Parent=22  DicPlace=0
    /// ThemeType=NCI_MUSIC  AddObjs=[1401..1407]  OwnMax=99</c>。
    /// ★ 它**不属于**主题 32（<c>NCI_MUSIC</c> 那 34 件里没有 1400）⇒ 必须单独下发。
    /// </summary>
    public const int MusicSceneTemplateId = 1400;

    /// <summary>
    /// 外景区域 id = <b>5</b>（<c>Game.NCafeAreaType.Outdoor</c>，
    /// 见 <c>HouseTable</c> 里 <c>Room</c> 列的取证：「<c>5</c>（<c>Outdoor</c>）是外景」）。
    /// </summary>
    public const int OutdoorRoomId = 5;

    /// <summary>
    /// 委托任务的任务值 group = **49**（`CafeLogic.lua:28` <c>CafeLogic.EntrustTask = 49</c>）。
    /// </summary>
    public const int EntrustTaskGroup = 49;

    /// <summary>
    /// 委托**积分**的任务值 id = **4**（`CafeLogic.lua:32` <c>CafeLogic.EntrustAward = 4</c>）。
    /// 消费点 `UI_CafeVisitReward.lua:115`
    /// <c>Player:GetTaskValue(CafeLogic.EntrustTask, CafeLogic.EntrustAward)</c>。
    /// </summary>
    public const int EntrustAwardTaskId = 4;

    /// <summary>已领档位的位掩码存在这个任务值 id（★ 本服自定，原厂位置未取证）。</summary>
    public const int EntrustClaimedTaskId = 5;

    /// <summary>委托开启状态（<c>starttime</c>）存在 <c>Extra["house"]</c> 的这个键里。</summary>
    internal const string EntrustStartKey = "entruststart";

    /// <summary>完成一次委托获得的积分（★ 本服取舍，原厂数值未取证 —— 见 131 注释）。</summary>
    public const int EntrustPointPerTask = 2;

    private GameFeatureContext? _ctx;
    private HouseTable _table = HouseTable.Empty;
    private CafeTable _cafe = CafeTable.Empty;
    private ShopTable _themeShop = ShopTable.Empty;
    private CafeComfortSync _comfort = null!;
    private HousePlacementLimits? _placementLimits;

    public string Name => "家园/家具摆设(pf 1048)";

    /// <summary>家园配置（家具模板 / 默认摆设 / Configure.json），单测可注入。</summary>
    internal HouseTable Table => _table;

    public HouseFeature() { }

    /// <summary>单测用：直接注入配置表，跳过 <see cref="Install"/>。</summary>
    internal HouseFeature(HouseTable table, string? settingDirectory = null)
    {
        _table = table;
        if (settingDirectory is not null)
        {
            _placementLimits = new HousePlacementLimits(settingDirectory);
            _cafe = CafeTable.Load(settingDirectory);
        }
    }

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _comfort = new CafeComfortSync(ctx);
        _table = HouseTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _placementLimits = new HousePlacementLimits(Path.GetFullPath(ctx.Options.SettingDirectory));

        // ★ 覆盖注册（理由与生效前提见类注释 §6）。不要改 GameModule.cs。
        ctx.MapPf(GamePf.GetHouseInfo, (s, p) => HandleGetHouseInfo(new SessionAdapter(s), p));
        ctx.MapPf(SssjServer.Protocol.Pf.ClickUpdateHouseReq,
            (s, p) => HandleUpdateHouse(new SessionAdapter(s), p));
        ctx.MapPf(SssjServer.Protocol.Pf.ClickDelThemeReq,
            (s, p) => HandleDeletePlan(new SessionAdapter(s), p));

        // 装修域的 5 条 LuaCall
        ctx.MapLuaCall(BuyThemeCmd, OnBuyTheme);
        ctx.MapLuaCall(CheckThemeCmd, OnCheckTheme);
        ctx.MapLuaCall(ClickItemCmd, OnClickItem);
        ctx.MapLuaCall(SellFurCmd, OnSellFur);
        ctx.MapLuaCall(SetMusicCmd, OnSetMusic);
        ctx.MapLuaCall(OpenEntrustCmd, OnOpenEntrust);
        ctx.MapLuaCall(CompleteEntrustCmd, OnCompleteEntrust);
        ctx.MapLuaCall(AwardEntrustCmd, OnAwardEntrust);
        ctx.MapLuaCall(SetVisiteGirlCmd, OnSetVisiteGirl);

        // ★ GM 全解锁账号补「音乐节外景」= 打开音游入口（见 MusicSceneTemplateId 的取证）。
        //   用 OnPlayerPrepare（pf 1005 之前）⇒ 变更随登录本来就要发的家具下发一起走，不额外发包。
        ctx.OnPlayerPrepare(GrantMusicSceneForGm);
        _cafe = CafeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _themeShop = ShopTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
    }

    // =================================================================
    //  装修域 LuaCall 126 / 127 / 128 / 137 / 138
    // =================================================================

    /// <summary>
    /// LuaCall 126「购买家具/主题」。上行 <c>{themeid, furidlist:[...]}</c>
    /// （`LuaCall.lua:1850..1853` 的原厂注释）。
    ///
    /// <para><b>★★ 成功回的是「裸字符串」<c>'buy_theme_succeed'</c>，不是对象</b></para>
    /// 取证 `LuaCall.lua:1856..1866`：
    /// <code>
    /// if UI.IsOpen('UI_CafeItemTips') then
    ///     if tbParam == 'buy_theme_succeed' then          -- ★ 与**字符串**比较
    ///         UI.Call('UI_CafeItemTips', 'Click_Close')
    /// if UI.IsOpen('UI_NCafeShop') then
    ///     UI.Call('UI_NCafeShop', 'SendToServerForShopList')   -- 刷新商店列表
    /// </code>
    /// ⇒ 回对象/回别的字符串都不会关窗（**静默无反应**）。
    /// ★ 本族**没有失败通道** —— 客户端只认这一个字符串，失败时我们不回包并记日志。
    ///
    /// <para><b>扣费与入账</b></para>
    /// 按商品配置购买实际缺件，预先检查全部费用与容量，家具进入仓库。
    /// </summary>
    private Task OnBuyTheme(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] CafeHouse_BuyTheme({cmd}) 未登录，忽略");
            return Task.CompletedTask;
        }

        var obj = param as JsonObject;
        int themeId = ReadInt(obj?["themeid"], 0);
        if (themeId <= 0 || !_cafe.Themes.TryGetValue(themeId, out var theme))
        {
            Log.Warn($"[session {session.Id}] CafeHouse_BuyTheme 主题 {themeId} 不在 NCafeThemes.txt 里 —— 不回包");
            return Task.CompletedTask;
        }

        var owned = ReadOwnedThemes(player);
        if (owned.Contains(themeId))
        {
            Log.Info($"[session {session.Id}] CafeHouse_BuyTheme 主题 {themeId} 已购过 —— 不回包");
            return Task.CompletedTask;
        }

        if (!TryBuyMissingTheme(player, theme, obj?["furidlist"], out var granted, out var changedMoney))
        {
            Log.Warn($"[session {session.Id}] Theme {themeId} missing-item purchase rejected; no charge or furniture grant");
            return Task.CompletedTask;
        }
        owned.Add(themeId);
        WriteOwnedThemes(player, owned);
        bool comfortChanged = granted.ChangedFurniture.Count > 0 && _comfort.Refresh(session, player);
        _ctx!.Store.Save(player);
        GameNotify.PushMoney(session, player, changedMoney);
        GameNotify.PushRewards(session, player, granted);
        _comfort.Push(session, player, comfortChanged);
        Log.Info($"[session {session.Id}] Theme {themeId}: missing furniture added to storage, existing layout preserved");
        // ★★ 裸字符串，不是对象
        _ctx.LuaCalls.CallClient(session, cmd, BuyThemeOk);
        return Task.CompletedTask;
    }

    /// <summary>
    /// LuaCall 127「查询是否拥有该主题」。上行含主题 id。
    ///
    /// ★★ 回包是**字符串** <c>'true'</c> / <c>'false'</c>（见 <see cref="HaveThemeTrue"/> 注释）。
    /// 消费点 `UI_CafeItemTips.lua:629..638`，它把回包**整体**存进 `self.HaveTheme`
    /// 再与字符串字面量比较。
    /// </summary>
    private Task OnCheckTheme(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] CafeHouse_CheckTheme({cmd}) 未登录，忽略");
            return Task.CompletedTask;
        }

        // 上行键名未取证（客户端发起点在 C# 侧）；这里把常见几种都试一遍
        var obj = param as JsonObject;
        int themeId = ReadInt(obj?["themeid"], 0);
        if (themeId <= 0)
            themeId = ReadInt(obj?["themeId"], 0);
        if (themeId <= 0)
            themeId = ReadInt(obj?["id"], 0);
        if (themeId <= 0)
            themeId = ReadInt(obj?["Id"], 0);

        bool have = themeId > 0 && ReadOwnedThemes(player).Contains(themeId);
        Log.Info($"[session {session.Id}] CafeHouse_CheckTheme 主题 {themeId} → {(have ? "已拥有" : "未拥有")}");

        // ★★ 字符串字面量，不是布尔
        _ctx!.LuaCalls.CallClient(session, cmd, have ? HaveThemeTrue : HaveThemeFalse);
        return Task.CompletedTask;
    }

    /// <summary>
    /// LuaCall 128「点击某物品」。上行 <c>{templateid}</c>（`LuaCall.lua:1918..1922`）。
    ///
    /// ★ **不回包**：服务端侧 <c>LuaCall:CallClient</c> 被原厂**注释掉**（`:1924`），
    /// 客户端 <c>else</c> 分支也是空的（`:1926`）⇒ 纯上报。
    /// 原服大概用它统计「每日点击家具次数」（`NCafeLevels.txt` 有 <c>DailyLmtItemClick</c> 列），
    /// 但**具体奖励规则未取证** ⇒ 本实现只记日志，不发奖、不计数。
    /// </summary>
    private Task OnClickItem(ClientSession session, int cmd, JsonNode? param)
    {
        int templateId = ReadInt((param as JsonObject)?["templateid"], 0);
        Log.Info($"[session {session.Id}] CafeHouse_ClickItem templateid={templateId}" +
                 "（原厂两侧回包均被注释 ⇒ 不回包；奖励规则未取证，只记账）");
        return Task.CompletedTask;
    }

    /// <summary>
    /// LuaCall 137「卖家具」。上行 <c>{objIds: [...]}</c>（`LuaCall.lua:1873..1875`）。
    ///
    /// <para>★ 回包形态 <c>{objIds: [...卖掉的...]}</c></para>
    /// 服务端侧的 <c>CallClient</c> 虽被注释（`:1877`），但**客户端 <c>else</c> 分支是活的**：
    /// <c>UI.Call("UI_CafeStore", "OnRecover", tbParam)</c>（`:1883`）
    /// ⇒ 必须回包，且把 <c>tbParam</c> **整体**交给 `OnRecover`。
    /// 原厂注释（`:1878..1880`）明确回包形状是 <c>{ objIds = [] }</c>。
    ///
    /// ★ 只卖**玩家家具**（<c>objId &gt;= <see cref="HouseTable.MinPlayerObjId"/></c>）——
    /// 系统家具（1..85）服务端本来就不发、也不该被卖掉。
    /// 售价按 <c>NCafeItems.txt</c> 的 <c>SellPrice</c>。
    /// </summary>
    private Task OnSellFur(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] CafeHouse_SellFur({cmd}) 未登录，忽略");
            return Task.CompletedTask;
        }

        // ★ 上行可能是 JsonArray（别的咖啡馆命令真机上就这样）⇒ 先判类型
        var ids = new HashSet<uint>();
        if (param is JsonObject req && req["objIds"] is JsonArray arr)
        {
            foreach (var n in arr)
            {
                uint v = ReadUInt(n, 0);
                if (v > 0)
                    ids.Add((uint)v);
            }
        }

        if (ids.Count == 0)
        {
            Log.Warn($"[session {session.Id}] CafeHouse_SellFur 没有合法 objIds（param={param?.ToJsonString()}）—— 回空清单");
            _ctx!.LuaCalls.CallClient(session, cmd, new JsonObject { ["objIds"] = new JsonArray() });
            return Task.CompletedTask;
        }

        var state = ReadOrCreate(player, _table, session.Id);
        var keep = new List<HouseFurniture>();
        var sold = new List<uint>();
        long income = 0;

        foreach (var f in state.Furniture)
        {
            // 只允许卖玩家家具
            // UI_CafeStore.Decompose 只提交未在场景启用且有回收价的实例。
            bool target = ids.Contains(f.ObjId) && f.ObjId >= HouseTable.MinPlayerObjId
                && (f.RoomId == 0 || f.ParentId == 0)
                && _cafe.Furniture.TryGetValue(f.TemplateId, out var price) && price.SellPrice > 0;
            if (!target)
            {
                keep.Add(f);
                continue;
            }
            sold.Add(f.ObjId);
            if (_cafe.Furniture.TryGetValue(f.TemplateId, out var row))
                income += row.SellPrice;
        }

        if (sold.Count == 0)
        {
            Log.Warn($"[session {session.Id}] CafeHouse_SellFur 请求的 {ids.Count} 个 objId 都不是玩家家具" +
                     $"（或不存在）—— 回空清单");
            _ctx!.LuaCalls.CallClient(session, cmd, new JsonObject { ["objIds"] = new JsonArray() });
            return Task.CompletedTask;
        }

        // 整批保持原样，避免金币溢出后家具已经消失。
        if (income > int.MaxValue - (long)player.GetMoney(GoldMoneyId))
        {
            _ctx!.LuaCalls.CallClient(session, cmd, new JsonObject { ["objIds"] = new JsonArray() });
            return Task.CompletedTask;
        }
        if (income > 0)
            player.AddMoney(GoldMoneyId, (int)income);
        WriteState(player, new HouseState { Furniture = keep, NextObjId = state.NextObjId });
        bool comfortChanged = _comfort.Refresh(session, player);
        _ctx!.Store.Save(player);

        Log.Info($"[session {session.Id}] 卖家具 {sold.Count} 件（objId {string.Join(',', sold)}）：" +
                 $"收入 {income} 金币 → 余 {player.GetMoney(GoldMoneyId)}；剩余家具 {keep.Count} 件");

        if (income > 0)
            GameNotify.PushMoney(session, player, GoldMoneyId);

        var back = new JsonArray();
        foreach (uint id in sold)
            back.Add(id);
        // UI_CafeStore.OnRecover reads singular objId, despite LuaCall.lua's
        // old comment spelling it objIds. Keep the legacy field for compatibility.
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["objIds"] = back, ["objId"] = back.DeepClone() });
        _comfort.Push(session, player, comfortChanged);
        return Task.CompletedTask;
    }

    /// <summary>
    /// LuaCall 138「设置主题音乐」。
    ///
    /// ★ **不回包**：服务端侧 <c>CallClient</c>（`LuaCall.lua:1892`）与客户端整个
    /// <c>else</c> 分支（`:1894..1900`）**都被原厂注释掉了** ⇒ 纯记录。
    /// 只把选择存进 <c>Extra["house"].music</c>，供将来 pf 1048 下发时用
    /// （★ 当前 pf 1049 的 <c>musicinfo</c> 字段仍留空 —— 见类注释）。
    /// </summary>
    private Task OnSetMusic(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var obj = param as JsonObject;
        int music = ReadInt(obj?["musicid"], 0);
        if (music <= 0)
            music = ReadInt(obj?["music"], 0);
        if (music <= 0)
            music = ReadInt(obj?["id"], 0);

        var node = player.Extra.TryGetValue(ExtraKey, out var v) && v is JsonObject o
            ? (JsonObject)o.DeepClone()
            : new JsonObject();
        node[MusicKey] = music;
        player.Extra[ExtraKey] = node;
        _ctx!.Store.Save(player);

        Log.Info($"[session {session.Id}] CafeHouse_SetMusic 主题音乐 → {music}" +
                 "（原厂两侧回包均被注释 ⇒ 不回包）");
        return Task.CompletedTask;
    }

    // =================================================================
    //  委托任务 129 / 131 / 132
    // =================================================================

    /// <summary>
    /// LuaCall 129「开启委托任务状态」。上行**空表**
    /// （`CafeLogic.lua:809` <c>LuaCall:CallServer(LuaCall.CafeHouse_OpenEntrust, {})</c>）。
    ///
    /// <para><b>★ 回包只要一个 <c>starttime</c></b></para>
    /// 消费点 `CafeLogic.OnOpenEntrustTask`（`:814..822`）：
    /// <code>
    /// Game.NCafeManager.Data.EntrustTime = tbParam.starttime;   -- ★ 唯一被读的字段
    /// if UI.IsOpen("UI_NCafeTask") then UI.Call("UI_NCafeTask", "Refresh") end
    /// </code>
    /// A 族约定（`LuaCall.lua:1936` <c>if (tbParam.nError) then</c>，**真值**）
    /// ⇒ 成功**绝不带** <c>nError</c>。
    ///
    /// ★ 幂等：重复开启就回同一个 <c>starttime</c>（客户端只是刷新界面）。
    /// </summary>
    private Task OnOpenEntrust(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] CafeHouse_OpenEntrust({cmd}) 未登录，忽略");
            return Task.CompletedTask;
        }

        long start = ReadEntrustStart(player);
        if (start <= 0)
        {
            start = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            WriteHouseKey(player, EntrustStartKey, start);
            _ctx!.Store.Save(player);
            Log.Info($"[session {session.Id}] 开启委托任务：starttime={start}");
        }
        else
        {
            Log.Info($"[session {session.Id}] 委托任务已开启（starttime={start}）⇒ 幂等回同一值");
        }

        _ctx!.LuaCalls.CallClient(session, cmd, new JsonObject { ["starttime"] = start });
        return Task.CompletedTask;
    }

    /// <summary>
    /// LuaCall 131「完成委托任务」。上行 <c>{VisitedId: 被访问者 roleid}</c>
    /// （★ **大写**，取证 `CafeLogic.lua:900..901`
    /// <c>local tbParam = { VisitedId = cafeRoleId }</c>）。
    ///
    /// <para><b>★★ 回包字段是「大写驼峰」<c>TaskId</c> / <c>AwardItem</c></b></para>
    /// 取证 `CafeLogic.lua:906` 的签名注释 <c>---@param tbParam table { TaskId, AwardItem }</c>
    /// + 消费点 `OnCompleteEntrustTask`（`:907..917`）：
    /// <code>
    /// UI.CloseAll(); UI.Open("UI_NCafeVisit")
    /// if (tbParam.nError) then ShowMsgByKey(...)
    /// else Game.NCafeManager.Data:CompleteEntrustTask(tbParam.TaskId)   -- ★ 大写 TaskId
    /// </code>
    /// ★ 与本项目多数「小写字段」的惯例相反 —— 照小写写会让客户端
    /// <c>CompleteEntrustTask(nil)</c>。
    ///
    /// <para><b>★ 积分数值是本服取舍</b></para>
    /// 完成一次给 <see cref="EntrustPointPerTask"/> = 2 分。
    /// **原厂每次给多少分未取证**（`VisitMission.txt` 只有 `Awards` 道具、没有积分列；
    /// `VisitAward.txt` 的门槛是 2/4/6/8/10 ⇒ 每次 2 分刚好 5 次集满，故取 2）。
    /// </summary>
    private Task OnCompleteEntrust(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] CafeHouse_CompleteEntrust({cmd}) 未登录，忽略");
            return Task.CompletedTask;
        }

        var obj = param as JsonObject;
        ulong visited = (ulong)Math.Max(0, ReadInt(obj?["VisitedId"], 0));
        if (visited == 0)
            visited = (ulong)Math.Max(0, ReadInt(obj?["visitedid"], 0));

        // 委托任务 id：从 VisitMission.txt 里按访问目标确定性挑一个
        var missions = _cafe.VisitMissions;
        if (missions.Count == 0)
        {
            Log.Warn($"[session {session.Id}] CafeHouse_CompleteEntrust：VisitMission.txt 为空 ⇒ 不回包");
            return Task.CompletedTask;
        }
        int taskId = missions[(int)(visited % (ulong)missions.Count)].MissionId;

        // 积分 +N
        int point = player.GetTask(EntrustTaskGroup, EntrustAwardTaskId) + EntrustPointPerTask;
        player.SetTask(EntrustTaskGroup, EntrustAwardTaskId, point);
        _ctx!.Store.Save(player);

        Log.Info($"[session {session.Id}] 完成委托任务 TaskId={taskId}（访问 roleid={visited}）：" +
                 $"委托积分 +{EntrustPointPerTask} → {point}");

        // 任务值变更要推 pf 1026（UI_CafeVisitReward 读的就是它）
        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(
                Player.ComposeTaskId(EntrustTaskGroup, EntrustAwardTaskId), point),
        });

        // ★★ 大写字段；AwardItem 给空数组（本次不直接发奖，奖励走 132 按积分档领）
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
        {
            ["TaskId"] = taskId,
            ["AwardItem"] = new JsonArray(),
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// LuaCall 132「委托积分领奖」。上行**空表**（`UI_CafeVisitReward.lua:294`）。
    ///
    /// <para><b>★ 回包 <c>AwardItem</c> 会被直接塞进奖励弹窗</b></para>
    /// 取证 `LuaCall.lua:1976..1984`：
    /// <code>
    /// if (tbParam.nError) then ShowMsgByKey("ui.Error." .. tbParam.nError); return; end
    /// UI.OpenAsChild("UI_RewardNormal", tbParam.AwardItem);   -- ★ 整体交给弹窗
    /// UI.Call("UI_CafeVisitReward", "UpdateBoxState");        -- 刷新档位亮灯
    /// </code>
    /// ⇒ 成功必须带 <c>AwardItem</c>（**A 族**：不带 <c>nError</c>）。
    ///
    /// <para><b>档位与门槛</b></para>
    /// `VisitAward.txt`（5 档）：<c>Point</c> 门槛 2/4/6/8/10，各带 1..3 项奖励
    /// （<c>Item1..Item5</c>，`[G-D-P-L-N]` 形态）。
    /// 积分来自任务值 <c>(49, 4)</c>，已领档位记在 <c>(49, 5)</c> 的位掩码里
    /// （★ 已领位置**原厂未取证**，本服自定 —— 见 <see cref="EntrustClaimedTaskId"/>）。
    /// 一次领**所有已达门槛且未领**的档位。
    /// </summary>
    private Task OnAwardEntrust(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] CafeHouse_AwardEntrust({cmd}) 未登录，忽略");
            return Task.CompletedTask;
        }

        int point = player.GetTask(EntrustTaskGroup, EntrustAwardTaskId);
        int claimed = player.GetTask(EntrustTaskGroup, EntrustClaimedTaskId);

        var tuples = new List<int[]>();
        var got = new List<int>();
        foreach (var row in _cafe.VisitAwards.OrderBy(r => r.Id))
        {
            if (row.Point > point)
                continue;                       // 门槛没到
            int bit = 1 << Math.Min(30, row.Id);
            if ((claimed & bit) != 0)
                continue;                       // 已领过
            tuples.AddRange(row.Items);
            claimed |= bit;
            got.Add(row.Id);
        }

        if (got.Count == 0)
        {
            Log.Info($"[session {session.Id}] CafeHouse_AwardEntrust：积分 {point} 无可领档位" +
                     $"（已领掩码 {claimed}）⇒ 不回包（A 族无合适错误码）");
            return Task.CompletedTask;
        }

        var granted = _ctx!.Rewards.Grant(player, tuples, RewardSource.Extra);
        player.SetTask(EntrustTaskGroup, EntrustClaimedTaskId, claimed);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] 委托积分领奖：积分 {point}、领取档位 [{string.Join(',', got)}]、" +
                 $"发出 {granted.Rewards.Count} 项（{granted.Describe()}）");

        // ★ 先把物品真正推进背包，再回业务包
        GameNotify.PushRewards(session, player, granted);
        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(
                Player.ComposeTaskId(EntrustTaskGroup, EntrustClaimedTaskId), claimed),
        });

        // ★ AwardItem 整体进奖励弹窗
        var award = new JsonArray();
        foreach (var t in tuples)
        {
            var one = new JsonArray();
            foreach (int v in t)
                one.Add(v);
            award.Add(one);
        }
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["AwardItem"] = award });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 134：设置「访问少女」（去别人咖啡馆时派哪个少女去）。
    ///
    /// 上行 <c>{ GirlId = &lt;少女id&gt; }</c>（<c>UI_CafeVisitSetting.lua:102</c> /
    /// <c>UI_CafeVisitMain.lua:553</c>；两处都先判 <c>if (not self.visitGirl) then return end</c>
    /// ⇒ 不会发 0/nil）。
    ///
    /// 存任务值 <c>(49, 1)</c> —— 客户端读取点 <c>UI_CafeVisitMain.lua:511..513</c>
    /// <c>Player:GetTaskValue(CafeLogic.EntrustTask, CafeLogic.VisitGirl)</c>。
    ///
    /// <para>★ 回包只需「有没有 <c>nError</c>」</para>
    /// 消费点是 <c>UI_CafeVisitMain:OnChangeGirl</c>（<c>:131..133</c>），函数体只有一句
    /// <c>Game.MessageBox.Simple(UIText("UI_CafeVisit.GirlSet"))</c> —— **不读任何字段、不收参数**。
    /// ⇒ 成功回空对象即可，但**必须先推 pf 1026**：玩家再打开设置界面时读的是任务值，
    /// 不推就会显示旧的少女（与 §22.2「回包 handler 不收参数 ⇒ 数值靠推送先到」同型）。
    ///
    /// ★ 错误约定：LuaCall 数字族（<c>LuaCall.lua</c> 客户端分支 <c>if (tbParam.nError) then</c>
    /// 是**真值判断**）⇒ 成功**绝不能带** <c>nError</c>（带 0 也会弹错误框）。
    /// </summary>
    private Task OnSetVisiteGirl(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        // ★★ 只在 JsonObject 上取字段（JsonNode["key"] 对 JsonArray 会抛）
        var body = param as JsonObject;
        int girlId = body?["GirlId"] is JsonValue gv && gv.TryGetValue<int>(out int g) ? g : 0;

        // 少女必须是玩家实际拥有的 —— 否则访客界面会拿一个查不到的 id 去建角色
        if (girlId <= 0 || player.GetGirl((uint)girlId) is null)
        {
            Log.Warn($"[session {session.Id}] CafeHouse_SetVisiteGirl GirlId={girlId} 非法或未拥有，拒绝");
            _ctx!.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = 1 });
            return Task.CompletedTask;
        }

        int before = player.GetTask(EntrustTaskGroup, VisitGirlTaskId);
        player.SetTask(EntrustTaskGroup, VisitGirlTaskId, girlId);
        _ctx!.Store.Save(player);

        // ★ 先推任务值，再回包
        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(
                Player.ComposeTaskId(EntrustTaskGroup, VisitGirlTaskId), girlId),
        });

        Log.Info($"[session {session.Id}] CafeHouse_SetVisiteGirl 访问少女 {before} → {girlId}");

        // 成功：不带 nError
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    // ---- 委托状态的读写 ----

    /// <summary>读委托开启时间（0 = 未开启）。</summary>
    internal static long ReadEntrustStart(Player player)
    {
        if (player.Extra.TryGetValue(ExtraKey, out var v) && v is JsonObject o
            && o[EntrustStartKey] is JsonNode n && long.TryParse(n.ToString(), out long t))
            return t;
        return 0;
    }

    /// <summary>往 <c>Extra["house"]</c> 写一个键（★ 克隆原节点，不抹掉别的键）。</summary>
    internal static void WriteHouseKey(Player player, string key, JsonNode? value)
    {
        var node = player.Extra.TryGetValue(ExtraKey, out var v) && v is JsonObject o
            ? (JsonObject)o.DeepClone()
            : new JsonObject();
        node[key] = value;
        player.Extra[ExtraKey] = node;
    }

    // ---- 已购主题清单的读写（Extra["house"].themes）----

    /// <summary>读已购主题 id 集合（缺失/损坏 ⇒ 空集合，不抛）。</summary>
    internal static HashSet<int> ReadOwnedThemes(Player player)
    {
        var set = new HashSet<int>();
        if (player.Extra.TryGetValue(ExtraKey, out var v) && v is JsonObject o
            && o[OwnedThemesKey] is JsonArray arr)
        {
            foreach (var n in arr)
            {
                int id = ReadInt(n, 0);
                if (id > 0)
                    set.Add(id);
            }
        }
        return set;
    }

    /// <summary>写已购主题 id 集合（★ 克隆原节点，不抹掉 <c>house</c> 里的其它键）。</summary>
    internal static void WriteOwnedThemes(Player player, HashSet<int> themes)
    {
        var node = player.Extra.TryGetValue(ExtraKey, out var v) && v is JsonObject o
            ? (JsonObject)o.DeepClone()
            : new JsonObject();
        var arr = new JsonArray();
        foreach (int id in themes.OrderBy(x => x))
            arr.Add(id);
        node[OwnedThemesKey] = arr;
        player.Extra[ExtraKey] = node;
    }

    // =================================================================
    //  pf 1048 GetHouseInfoReq → pf 1049 GetHouseInfoRsp
    // =================================================================

    /// <summary>
    /// pf 1048 的处理：读（必要时创建）本玩家的摆设 → 回 pf 1049。
    ///
    /// 未登录（<c>session.Tag</c> 非 <see cref="Player"/>）时仍然要回包 —— 客户端
    /// <c>NCafeNetController.ReqHouseInfo</c> 是带 serial 的请求，不回会让回调永久悬挂；
    /// 此时只能回 <c>req.Roleid</c> + 空 <c>HouseCache</c>（与旧行为一致）。
    /// </summary>
    internal Task HandleGetHouseInfo(IGameSession session, Packet packet)
    {
        var req = TryParseReq(session, packet);

        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] GetHouseInfoReq(1048) 到达但会话未登录（Tag 非 Player），" +
                     "只能回空 HouseCache —— 咖啡馆场景会卡在加载条 0.4");
            var bare = BuildResponse(req?.HasRoleid == true ? req.Roleid : 0UL, new HouseState());
            session.Send(GamePf.GetHouseInfoRsp, packet.Serial, bare);
            return Task.CompletedTask;
        }

        // ★ 客户端 RsqHouseInfos 0x1D31DA8 用 rsp.roleid 与 Player.me.id 比，不等就整包丢弃。
        // 所以一律回服务端权威的 player.Roleid；对不上只记一条日志（正常不会发生）。
        if (req?.HasRoleid == true && req.Roleid != player.Roleid)
        {
            Log.Warn($"[session {session.Id}] GetHouseInfoReq.roleid={req.Roleid} 与本会话玩家 " +
                     $"{player.Roleid} 不一致，按本会话玩家回包（pf 1112 才是「看别人的店」）");
        }

        var state = ReadOrCreate(player, _table, session.Id);
        if (WriteState(player, state))
            _ctx?.Store.Save(player);

        var rsp = BuildResponse(player.Roleid, state, player);

        Log.Info($"[session {session.Id}] 家园数据下发 roleid={player.Roleid}：" +
                 $"玩家家具 {state.Furniture.Count} 件" +
                 $"（objId {(state.Furniture.Count > 0 ? $"{state.Furniture[0].ObjId}..{state.Furniture[^1].ObjId}" : "-")}）、" +
                 $"其中餐位 {CountSeats(state)} 张（经营位合计 {state.Furniture.Sum(f => f.BizCount)}）、" +
                 $"分区 {string.Join(',', state.Furniture.GroupBy(f => f.RoomId).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}件"))}；" +
                 $"roomList {rsp.HouseInfo!.RoomList.Count} 个（{string.Join(',', rsp.HouseInfo.RoomList.Select(r => r.RoomId))}）、" +
                 $"自定方案 {ReadPlans(player).Count} 套、宠物 0 只、访客 0 —— " +
                 $"系统家具 54 件由客户端 InitFixedData/InitDefaultData 自建（含外景 {HouseTable.OutdoorObjId}）；" +
                 $"nextObjId={state.NextObjId}");

        session.Send(GamePf.GetHouseInfoRsp, packet.Serial, rsp);
        return Task.CompletedTask;
    }

    // ReqSaveArea/ReqSaveItems send changed instances, not a replacement inventory.
    // Room membership is derived from accepted furniture. Custom plan metadata
    // is stored separately; legacy DIY girl positions cannot overwrite NCafe girls.
    internal Task HandleUpdateHouse(IGameSession session, Packet packet)
    {
        var reply = new UpdateHouseRsp();
        if (session.Tag is not Player player)
        {
            session.Send(SssjServer.Protocol.Pf.UpdateHouseRsp, packet.Serial, reply);
            return Task.CompletedTask;
        }
        reply.Roleid = player.Roleid;
        UpdateHouseReq req;
        try { req = SssjServer.Protocol.ProtoMessage.Parse<UpdateHouseReq>(packet.Payload); }
        catch (Exception e)
        {
            Log.Warn($"[session {session.Id}] UpdateHouseReq malformed: {e.Message}");
            session.Send(SssjServer.Protocol.Pf.UpdateHouseRsp, packet.Serial, reply);
            return Task.CompletedTask;
        }
        if (req.HasRoleid && req.Roleid != player.Roleid)
        {
            Log.Warn($"[session {session.Id}] UpdateHouseReq roleid mismatch; ignored");
            session.Send(SssjServer.Protocol.Pf.UpdateHouseRsp, packet.Serial, reply);
            return Task.CompletedTask;
        }

        var state = ReadOrCreate(player, _table, session.Id);
        var byId = state.Furniture.ToDictionary(f => f.ObjId);
        int cafeLevel = CafeFeature.ReadState(player, _cafe).Level;
        bool acceptedLayout = true;
        var duplicates = req.Furnitures.GroupBy(f => f.ObjId)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var change in req.Furnitures.DistinctBy(f => f.ObjId))
        {
            if (!byId.TryGetValue(change.ObjId, out var owned))
            {
                acceptedLayout = false;
                Log.Warn($"[session {session.Id}] UpdateHouseReq unowned objId={change.ObjId}; ignored");
                continue;
            }
            var pose = change.Pose;
            bool valid = !duplicates.Contains(change.ObjId)
                && (_placementLimits is null || _placementLimits.RoomOpen(cafeLevel, (int)change.RoomId)
                    || change.RoomId == owned.RoomId && change.ParentId == owned.ParentId)
                && change.HasTemplateId && change.TemplateId == owned.TemplateId
                && change.HasRoomId && change.HasParentId && pose is not null
                && pose.HasPosX && pose.HasPosY && pose.HasRotate
                && (!pose.HasTemplateId || pose.TemplateId == owned.TemplateId)
                && (!pose.HasParentTemplateId || pose.ParentTemplateId == change.ParentId)
                && (change.RoomId == 0
                    ? change.ParentId == 0
                    : (HouseRooms.Ids.Contains((int)change.RoomId) || change.RoomId == OutdoorRoomId)
                      && change.ParentId != 0
                      && _table.IsAllowedParent(owned.TemplateId, change.ParentId)
                      && _table.Items.TryGetValue((int)change.ParentId, out var parent)
                      && HousePoseLimits.Valid(parent, pose.PosX, pose.PosY, pose.Rotate, _table.Items[owned.TemplateId])
                      && (parent.DicPlace == 0 || parent.DicPlace == change.RoomId
                          || (change.RoomId == OutdoorRoomId && change.ParentId == 22
                              && _table.Items[owned.TemplateId].Type == _table.OutDoorScene)));
            if (valid)
            {
                owned = owned with { RoomId = (int)change.RoomId, ParentId = change.ParentId,
                    PosX = pose!.PosX, PosY = pose.PosY, Rotate = pose.Rotate };
                byId[owned.ObjId] = owned;
            }
            else
            {
                acceptedLayout = false;
                Log.Warn($"[session {session.Id}] UpdateHouseReq invalid placement objId={change.ObjId}; kept authoritative state");
            }
            // Echo authoritative state even after rejection, so the client can
            // restore a known instance instead of retaining unsaved coordinates.
            reply.NewFurnitures.Add(BuildFurniture(owned));
        }
        var next = new HouseState { NextObjId = state.NextObjId,
            Furniture = state.Furniture.Select(f => byId[f.ObjId]).ToList() };
        if (!HousePlacementLimits.OutdoorLimit(_table, state, next)
            || !HouseCollisionLimits.NoWorse(_table, state, next)
            || _placementLimits is not null && !_placementLimits.WithinLimits(_table, state, next, cafeLevel))
        {
            Log.Warn($"[session {session.Id}] UpdateHouseReq layout limits exceeded; placement batch unchanged");
            next = state;
            acceptedLayout = false;
            reply.NewFurnitures.Clear();
            reply.NewFurnitures.AddRange(req.Furnitures.DistinctBy(f => f.ObjId)
                .Where(f => byId.ContainsKey(f.ObjId))
                .Select(f => BuildFurniture(state.Furniture.First(old => old.ObjId == f.ObjId))));
        }
        bool plansChanged = UpdatePlans(player, next, req, reply, session.Id, acceptedLayout);
        if (WriteState(player, next) || plansChanged) _ctx?.Store.Save(player);
        reply.NewRooms.AddRange(BuildResponse(player.Roleid, next, player).HouseInfo!.RoomList);
        if (req.Rooms.Any(r => r.GirlIds.Count > 0))
            Log.Warn($"[session {session.Id}] UpdateHouseReq legacy girls not applied by NCafe placement handler");
        session.Send(SssjServer.Protocol.Pf.UpdateHouseRsp, packet.Serial, reply);
        return Task.CompletedTask;
    }

    private static GetHouseInfoReq? TryParseReq(IGameSession session, Packet packet)
    {
        try
        {
            return SssjServer.Protocol.ProtoMessage.Parse<GetHouseInfoReq>(packet.Payload);
        }
        catch (Exception e)
        {
            Log.Warn($"[session {session.Id}] 解析 GetHouseInfoReq(1048) 失败：{e.Message}");
            return null;
        }
    }

    // =================================================================
    //  应答构造
    // =================================================================

    /// <summary>
    /// 把 <see cref="HouseState"/> 组装成 <c>GetHouseInfoRsp</c>。
    ///
    /// 三个「一定要写」的字段（理由见类注释 §2/§3）：
    /// <list type="bullet">
    ///   <item><c>roleid</c>(#1) —— required，且必须等于 <c>Player.me.id</c>；</item>
    ///   <item><c>houseInfo</c>(#2) —— required；</item>
    ///   <item><c>visitinfo</c>(#3) —— 协议上 optional，但 <c>NCafeNet.lua:173</c> 是<b>无条件</b>
    ///     <c>NCafe.Data.HouseInfo.VisitedList:Clear()</c>，而 <c>VisitedList</c>(+0x50) 在
    ///     <c>NCafePlayerHouseInfo..ctor</c>（RVA <c>0x1A385D4</c>）里<b>不初始化</b>，
    ///     唯一的懒创建点 <c>AddVisitedInfos</c>（RVA <c>0x1A379D0</c>）只在
    ///     <c>has_visitinfo</c> 为真时才走到（<c>0x1D68FB8</c>）⇒ <b>不带就 NRE</b>。</item>
    /// </list>
    /// </summary>
    internal static GetHouseInfoRsp BuildResponse(ulong roleid, HouseState state, Player? player = null)
    {
        var rsp = new GetHouseInfoRsp
        {
            Roleid = roleid,
            HouseInfo = new HouseCache { Roleid = roleid },
            // 空 VisitedData 即「没有访客记录」的合法答案；不能省（见上）
            Visitinfo = new VisitedData(),
        };

        var cache = rsp.HouseInfo;
        foreach (var f in state.Furniture)
            cache.FurnitureList.Add(BuildFurniture(f));

        // roomList：固定发 HouseRooms.Ids = {1 Cafe, 2 Rest, 6 Dorm}，即使某区还没家具也要发 ——
        // 理由见类注释 §3 第 4 条（ReqSaveArea 的 GetArea() 无 nil 保护）。
        // 客户端 OnUpdateRoom（RVA 0x1D696F0）只读 roomId / themeId / furnitureIds，
        // 压根不读 girlIds 与 friendGirlId（那两个字段属于 RoomDiyManager / HouseManager 那条链），
        // 所以这里不写 girlIds（空 repeated 不占字节），friendGirlId 写 0 表示「明确没有好友少女」。
        foreach (int roomId in HouseRooms.Ids)
        {
            var room = new roomData
            {
                RoomId = (uint)roomId,
                // themeId = 0 是「没用主题」的合法正常态：GetTheme(0)（RVA 0x1D62DD8）
                // 用 TryGetValue + csel 返回 null 而不抛，且 Themes.Clear() 发生在 room 循环之后
                // （UpdatePlayerHouseInfo 0x1D68E84）—— 客户端从设计上就不指望这里能查到主题。
                ThemeId = 0,
                FriendGirlId = 0,
            };
            foreach (var f in state.Furniture)
            {
                if (f.RoomId == roomId)
                    room.FurnitureIds.Add(f.ObjId);
            }
            cache.RoomList.Add(room);
        }

        // TemplateThemeId=0 is the explicit custom-plan branch of NCafeTheme's
        // constructor (0x18005C4 -> 0x180062C); it reads name/poses from protobuf.
        if (player is not null) AddPlansToCache(player, cache);
        return rsp;
    }

    /// <summary>
    /// 一件家具 → <c>gsproto.furniture</c>。字段顺序与取值口径<b>逐字照抄客户端自己的上行构造</b>
    /// <c>NCafeDataController.ReqSaveItems</c>（RVA <c>0x1D6B7A8</c>）：
    /// <code>
    /// f.templateId = item.Id            // 0x1D6B90C get_Id  → 0x1D6B92C set_templateId
    /// f.objId      = item+0x10 Guid     // 0x1D6B93C        → 0x1D6B958 set_objId
    /// f.parentId   = item+0x14 Parent   // 0x1D6B95C        → 0x1D6B978 set_parentId
    /// f.roomId     = item+0x18 Room     // 0x1D6B97C        → 0x1D6B998 set_roomId
    /// f.isNew      = item.IsNew        // 0x1D6B99C ldrb [item,#0x2C], 0x1D6B9AC cmp/cset
    /// p.templateId       = item.Id      // 0x1D6B9E0        → 0x1D6BA00
    /// p.parentTemplateId = item+0x14    // 0x1D6BA04        → 0x1D6BA20   ← 与 f.parentId 同源
    /// p.posX/posY/rotate = item+0x1C/0x20/0x24              // 0x1D6BA40 / 0x1D6BA60 / 0x1D6BA80
    /// f.pose = p                        // 0x1D6BA9C
    /// </code>
    /// ⇒ 客户端上行<b>不写</b> <c>objPose.place</c>（#3）、<c>furniture.status</c>(#6)、
    /// <c>bizcount</c>(#8)。其中 <c>place</c> 在咖啡馆这一路<b>确实没人读</b>
    /// （<c>il2cpp_callers.py of 'gsproto.objPose$$get_place'</c> 只有
    /// <c>RoomThemeData.ObjPose..ctor</c> / <c>RoomDiyManager.IsThemeDirty</c> 与 xLua wrapper），
    /// 所以本实现同样不写；<c>status</c> 与 <c>bizcount</c> 则显式写出来（服务端记账口径明确）。
    ///
    /// ★★ <b>每件家具必须给一个独立的 <c>objPose</c> 实例，绝不能共享</b>：
    /// <c>NCafeItem.UpdateInfo</c> 的 <c>0x1D2C854 set_pose</c> 是<b>引用赋值</b>
    /// （深拷贝只做到 <c>netdata</c> 这一层），而 <c>CheckDataChanged</c>（RVA <c>0x1D2CFE4</c>）
    /// 会往同一个 <c>objPose</c> 上<b>回写</b>坐标（<c>0x1D2D1D4</c>/<c>0x1D2D1F4 set_posX</c>）——
    /// 共享一个实例会让玩家挪动一件家具时静默污染另一件的位置。
    /// 下面每次调用都 <c>new objPose</c>，这条约束别在「优化分配」时被破坏。
    /// </summary>
    internal static furniture BuildFurniture(HouseFurniture f)
    {
        return new furniture
        {
            TemplateId = (uint)f.TemplateId,
            ObjId = f.ObjId,
            ParentId = f.ParentId,
            RoomId = (uint)f.RoomId,
            Pose = new objPose
            {
                TemplateId = (uint)f.TemplateId,
                ParentTemplateId = f.ParentId,
                PosX = f.PosX,
                PosY = f.PosY,
                Rotate = f.Rotate,
            },
            Status = (uint)f.Status,
            // ★ 一律 false：新号第一次进店不该看到「NEW」角标。
            // 客户端 NCafeItem.UpdateInfo 0x1D2C8E8 无条件读它写进 NCafeItem.IsNew(+0x2C)，
            // 而 IsHaveNewItem(0x1D65170) 用它点红点。
            IsNew = false,
            Bizcount = (uint)f.BizCount,
        };
    }

    // =================================================================
    //  服务端状态：Player.Extra["house"]
    // =================================================================

    /// <summary>
    /// 读出本玩家的摆设；存档里没有（新号）时，按
    /// <c>Configure.txt</c> + <c>NCafeDefautItems.txt</c> <b>生成一份默认摆设</b>。
    ///
    /// 存档里的家具一律**先按 <c>NCafeItems.txt</c> 校验再下发**（理由见类注释 §3 第 2 条）：
    /// 一件 <c>templateId</c> 表外的家具就会变成「Type=0 的幽灵家具」，
    /// 而它可能正好被算进餐位/上限统计，属于最难查的一类静默失败。
    /// </summary>
    internal static HouseState ReadOrCreate(Player player, HouseTable table, int sessionId = 0)
    {
        var node = player.Extra.TryGetValue(ExtraKey, out var v) ? v as JsonObject : null;
        var stored = ReadFurniture(node?["furniturelist"] as JsonArray, table, sessionId);

        // An explicitly saved empty inventory is valid (e.g. all items sold).
        // Do not respawn default furniture or reset the instance allocator.
        if (stored.Count > 0 || node?["furniturelist"] is JsonArray { Count: 0 }
            && ReadUInt(node?["nextobjid"], 0) >= HouseTable.MinPlayerObjId)
        {
            uint next = ReadUInt(node?["nextobjid"], 0);
            // 存档里的 nextObjId 必须严格大于已用的最大玩家 objId，否则会分配出重复 id
            // （Items 是 Dictionary<uint, NCafeItem>，重复 objId 会让后一件覆盖前一件 —— 静默丢家具）
            uint maxUsed = HouseTable.MinPlayerObjId - 1;
            foreach (var f in stored)
            {
                if (f.ObjId >= HouseTable.MinPlayerObjId && f.ObjId > maxUsed)
                    maxUsed = f.ObjId;
            }
            if (next <= maxUsed)
                next = maxUsed + 1;
            return new HouseState { Furniture = stored, NextObjId = next };
        }

        return CreateDefault(table, sessionId);
    }

    /// <summary>
    /// GM 全解锁账号登录时补一件「音乐节外景」（templateId <see cref="MusicSceneTemplateId"/>），
    /// 从而打开咖啡馆里的**音游入口**（取证见那个常量的注释）。
    ///
    /// <para><b>为什么放在这里而不是 <see cref="GmUnlock"/></b></para>
    /// 家具存在 <c>Player.Extra["house"]</c>，不属于 <c>GmUnlock</c> 管的
    /// 「任务值 / 道具 / 角色卡」三套体系；而本域已经有完整的家具读写与下发链
    /// （<see cref="ReadOrCreate"/> / <see cref="WriteState"/> / pf 1048）⇒ 就近实现最稳。
    ///
    /// <para><b>★ 幂等</b></para>
    /// 已经有这件家具就直接返回，不重复加（<c>OwnMax=99</c> 允许多件，但多了没意义）。
    /// </summary>
    internal void GrantMusicSceneForGm(ClientSession session, Player player)
    {
        if (_ctx is null || _table is null)
            return;
        if (!_ctx.GmUnlock.IsGmAccount(player.Account))
            return;

        // 模板必须存在（配置换版后可能没有这件）
        if (!_table.IsKnownTemplate(MusicSceneTemplateId))
        {
            Log.Warn($"[session {session.Id}] 音乐节外景 templateId={MusicSceneTemplateId} "
                   + $"不在 {HouseTable.ItemsFileName} 里，跳过（音游入口将不可见）");
            return;
        }

        var state = ReadOrCreate(player, _table, session.Id);
        if (state.Furniture.Any(f => f.TemplateId == MusicSceneTemplateId))
            return;                                     // ★ 幂等

        var tpl = _table.ItemOf(MusicSceneTemplateId);
        var list = state.Furniture.ToList();
        uint next = state.NextObjId;
        bool hasPlacedOutdoor = state.Furniture.Any(f => f.RoomId != 0 && f.ParentId != 0
            && _table.ItemOf(f.TemplateId)?.Type == _table.OutDoorScene);

        // Placed scenes need the configured container (22). When another outdoor
        // is already placed, grant ownership in storage (room/parent 0), like a
        // normal furniture reward; HasByMusicScene checks ownership, not placement.
        list.Add(new HouseFurniture(
            ObjId: next++,
            TemplateId: MusicSceneTemplateId,
            RoomId: hasPlacedOutdoor ? 0 : OutdoorRoomId,
            ParentId: hasPlacedOutdoor ? 0u : tpl?.Parent ?? 22u,
            PosX: 0, PosY: 0, Rotate: 0,
            BizCount: 0,
            Status: 0));

        WriteState(player, new HouseState { Furniture = list, NextObjId = next });

        Log.Info($"[session {session.Id}] GM 补发音乐节外景（templateId={MusicSceneTemplateId}"
               + $" objId={next - 1} stored={hasPlacedOutdoor}）⇒ 咖啡馆音游入口已打开");
    }

    /// <summary>
    /// 生成新号的默认摆设 = <c>NCafeDefautItems.txt</c> 那 13 件，<c>objId</c> 从
    /// <see cref="HouseTable.MinPlayerObjId"/>(1000) 起按表的 <c>Id</c> 列升序递增分配。
    ///
    /// <b>不含系统家具</b>（客户端自建，见类注释 §4 与 <see cref="HouseTable.SystemItems"/>）。
    /// </summary>
    internal static HouseState CreateDefault(HouseTable table, int sessionId = 0)
    {
        var list = new List<HouseFurniture>();
        uint next = HouseTable.MinPlayerObjId;

        foreach (var row in table.DefaultItems)
        {
            if (!table.IsKnownTemplate(row.TemplateId))
            {
                Log.Warn($"[house] {HouseTable.DefaultItemsFileName} 第 Id={row.Id} 行的 " +
                         $"ItemId={row.TemplateId} 不在 {HouseTable.ItemsFileName} 里，跳过 " +
                         "（客户端 OnUpdateItem 0x1D69364 也会丢弃它并打 " +
                         "\"NCafe: cannot find item template, id N from net\"）");
                continue;
            }
            if (table.IsPoolBlockedTemplate(row.TemplateId))
            {
                // ★★ 这条是「加载条永远 0.4 且 logcat 一条 Error 都没有」的唯一服务端成因，
                // 取证见 HouseTable.PoolBlockedTemplateIds。
                Log.Error($"[house] {HouseTable.DefaultItemsFileName} 第 Id={row.Id} 行的 " +
                          $"ItemId={row.TemplateId} 在 LoadItem(0x1A3BD10) 的「路径不合法」黑名单里，" +
                          "已跳过 —— 下发它会让 InitObjects 静默断链、加载条永远停在 0.4");
                continue;
            }
            if (!table.IsAllowedParent(row.TemplateId, row.ParentId))
            {
                Log.Warn($"[house] {HouseTable.DefaultItemsFileName} 第 Id={row.Id} 行的 " +
                         $"parentId={row.ParentId} 不在模板 {row.TemplateId} 的 DicArea 白名单里，" +
                         "仍按表下发（表是原厂数据，以表为准）");
            }
            if (!HouseRooms.Ids.Contains(row.Room))
            {
                Log.Warn($"[house] {HouseTable.DefaultItemsFileName} 第 Id={row.Id} 行的 " +
                         $"Room={row.Room} 不在 {{1,2,6}} 里（NCafeAreaType 的真实房间），" +
                         "该件家具将不属于任何可编辑区域");
            }

            uint objId = next++;
            if (table.IsClientOwnedObjId(objId))
            {
                // 正常永远不会发生（1000 起远离 1..85），但这条自检值得留着：
                // objId 撞上客户端自建的默认家具 = 静默覆盖 + 可能触发 RecoverDefaultItem
                Log.Warn($"[house] 分配到的 objId={objId} 与客户端自建的默认家具冲突，" +
                         "会静默覆盖它（见 HouseTable.SystemItems 的取证）");
            }

            list.Add(new HouseFurniture(
                ObjId: objId,
                TemplateId: row.TemplateId,
                RoomId: row.Room,
                ParentId: row.ParentId,
                PosX: row.PosX, PosY: row.PosY, Rotate: row.Rotate,
                BizCount: row.BizCount,
                Status: 0));
        }

        var state = new HouseState { Furniture = list, NextObjId = next };
        Log.Info($"[house]{(sessionId > 0 ? $"[session {sessionId}]" : "")} 生成新号默认摆设：" +
                 $"{list.Count} 件（objId {HouseTable.MinPlayerObjId}.." +
                 $"{(next > HouseTable.MinPlayerObjId ? next - 1 : HouseTable.MinPlayerObjId)}）、" +
                 $"餐位 {CountSeats(state)} 张");
        return state;
    }

    /// <summary>
    /// 把摆设写回 <c>Player.Extra["house"]</c>，返回是否发生变化
    /// （调用方据此决定要不要落盘，避免每次进店都写一遍存档）。
    ///
    /// 实现上「克隆原对象再覆写」而不是新建：将来别的地方往 <c>["house"]</c> 里加键不会被吃掉
    /// （与 <c>CafeFeature.WriteState</c> 同一套写法）。
    /// </summary>
    internal static bool WriteState(Player player, HouseState state)
    {
        string? cur = player.Extra.TryGetValue(ExtraKey, out var old) ? old?.ToJsonString() : null;
        var obj = old is JsonObject prev ? (JsonObject)prev.DeepClone() : new JsonObject();

        obj["nextobjid"] = state.NextObjId;
        var arr = new JsonArray();
        foreach (var f in state.Furniture)
        {
            arr.Add(new JsonObject
            {
                ["objid"] = f.ObjId,
                ["templateid"] = f.TemplateId,
                ["roomid"] = f.RoomId,
                ["parentid"] = f.ParentId,
                ["posx"] = f.PosX,
                ["posy"] = f.PosY,
                ["rotate"] = f.Rotate,
                ["bizcount"] = f.BizCount,
                ["status"] = f.Status,
            });
        }
        obj["furniturelist"] = arr;

        if (string.Equals(cur, obj.ToJsonString(), StringComparison.Ordinal))
            return false;

        player.Extra[ExtraKey] = obj;
        return true;
    }

    /// <summary>从存档读家具，逐件按 <c>NCafeItems.txt</c> 校验（表外 templateId 丢弃）。</summary>
    private static List<HouseFurniture> ReadFurniture(JsonArray? arr, HouseTable table, int sessionId)
    {
        var result = new List<HouseFurniture>();
        if (arr is null)
            return result;

        var seen = new HashSet<uint>();
        foreach (var node in arr)
        {
            if (node is not JsonObject o)
                continue;

            uint objId = ReadUInt(o["objid"], 0);
            int templateId = ReadInt(o["templateid"], 0);
            if (objId == 0 || templateId <= 0)
                continue;

            if (!table.IsKnownTemplate(templateId))
            {
                Log.Warn($"[house]{(sessionId > 0 ? $"[session {sessionId}]" : "")} 存档里 objId={objId} 的 " +
                         $"templateId={templateId} 不在 {HouseTable.ItemsFileName} 里，丢弃该件 " +
                         "（下发会让客户端得到 Template=null 的幽灵家具）");
                continue;
            }
            if (table.IsPoolBlockedTemplate(templateId))
            {
                Log.Error($"[house]{(sessionId > 0 ? $"[session {sessionId}]" : "")} 存档里 objId={objId} 的 " +
                          $"templateId={templateId} 在 LoadItem(0x1A3BD10) 的「路径不合法」黑名单里，丢弃该件 " +
                          "—— 下发它会让 InitObjects 静默断链、加载条永远停在 0.4");
                continue;
            }
            if (table.IsClientOwnedObjId(objId))
            {
                Log.Warn($"[house]{(sessionId > 0 ? $"[session {sessionId}]" : "")} 存档里 objId={objId} " +
                         "与客户端自建的默认家具（Configure.SystemItems/DefaultItems）冲突，丢弃该件 " +
                         "—— 会静默覆盖地板/墙/外景/灯光，可能让家具树 NCafeTree.InitNodes(0x180130C) NRE");
                continue;
            }
            if (!seen.Add(objId))
            {
                Log.Warn($"[house]{(sessionId > 0 ? $"[session {sessionId}]" : "")} 存档里 objId={objId} 重复，" +
                         "丢弃后一件（Items 是 Dictionary<uint,NCafeItem>，重复会静默覆盖）");
                continue;
            }

            result.Add(new HouseFurniture(
                objId, templateId,
                ReadInt(o["roomid"], 1),
                ReadUInt(o["parentid"], 0),
                ReadUInt(o["posx"], 0),
                ReadUInt(o["posy"], 0),
                ReadUInt(o["rotate"], 0),
                ReadInt(o["bizcount"], 0),
                ReadInt(o["status"], 0)));
        }
        return result;
    }

    // =================================================================
    //  小工具
    // =================================================================

    /// <summary>
    /// 系统家具件数（<c>objId ≤ 85</c>）。本域正常下发时恒为 <b>0</b> ——
    /// 那 54 件由客户端自建，见类注释 §4。留着它是为了「万一存档里混进了系统 objId」能被断言逮到。
    /// </summary>
    internal static int CountSystem(HouseState state)
        => state.Furniture.Count(f => f.ObjId <= HouseTable.MaxSystemObjId);

    /// <summary>玩家家具件数（<c>objId ≥ 1000</c>）。</summary>
    internal static int CountPlayer(HouseState state)
        => state.Furniture.Count(f => f.ObjId >= HouseTable.MinPlayerObjId);

    /// <summary>餐位张数（<c>bizcount &gt; 0</c> 的家具件数）。</summary>
    internal static int CountSeats(HouseState state)
        => state.Furniture.Count(f => f.BizCount > 0);

    /// <summary>
    /// ★★★ 修复**既有存档**里「摆位全 0」的家具（第十二轮新增，配套 <c>gmsrv housefix</c>）。
    ///
    /// <para><b>为什么需要它</b></para>
    /// 早先 <c>OnBuyTheme</c> 的 bug 已经把坏数据**落盘**了：买主题入账的家具
    /// <c>parentId/posX/posY/rotate</c> 全是 0。光修代码不够 —— 存量存档照样进不了咖啡馆
    /// （<c>parentId=0</c> ⇒ 客户端挂不上场景树 ⇒
    /// <c>UpdateNavMeshObstacle</c>(<c>0x1808C18</c>) NRE ⇒ 加载条卡 40%）。
    ///
    /// <para><b>修法</b></para>
    /// 拿玩家**已购主题**清单去 <c>NCafeThemes.txt</c> 回查摆位，按 <c>templateId</c>
    /// 把坏件逐个补回原厂坐标；同一 templateId 有多份时按出现顺序配对。
    /// 补不回来的（主题里查不到该 templateId）**一律删除** —— 留着就是继续卡加载条，
    /// 而玩家最多损失一件装饰，权衡后取「能进店」。
    /// </summary>
    /// <returns>(修好的件数, 删掉的件数)</returns>
    internal static (int Fixed, int Removed) RepairPlacements(
        Player player, HouseTable table, CafeTable cafe, int sessionId = 0)
    {
        var state = ReadOrCreate(player, table, sessionId);
        var broken = state.Furniture.Where(f => f.ParentId == 0).ToList();
        if (broken.Count == 0)
            return (0, 0);

        // 已购主题 → 该主题所有 (templateId → 摆位) 的候选队列
        var pool = new Dictionary<int, Queue<CafeThemeItem>>();
        foreach (int themeId in ReadOwnedThemes(player).OrderBy(x => x))
        {
            if (!cafe.Themes.TryGetValue(themeId, out var theme))
                continue;
            foreach (var it in theme.Items)
            {
                if (!pool.TryGetValue(it.TemplateId, out var q))
                    pool[it.TemplateId] = q = new Queue<CafeThemeItem>();
                q.Enqueue(it);
            }
        }

        var result = new List<HouseFurniture>();
        int fixedCount = 0, removed = 0;
        foreach (var f in state.Furniture)
        {
            if (f.ParentId != 0)
            {
                result.Add(f);
                continue;
            }

            if (pool.TryGetValue(f.TemplateId, out var q) && q.Count > 0)
            {
                var want = q.Dequeue();
                result.Add(f with
                {
                    ParentId = want.ParentId,
                    PosX = want.PosX,
                    PosY = want.PosY,
                    Rotate = want.Rotate,
                });
                fixedCount++;
            }
            else
            {
                Log.Warn($"[house]{(sessionId > 0 ? $"[session {sessionId}]" : "")} " +
                         $"家具 objId={f.ObjId} templateId={f.TemplateId} 摆位为 0 且" +
                         "在已购主题里查不到对应摆位 —— **删除**（留着会让进店卡在 40% 加载条）");
                removed++;
            }
        }

        WriteState(player, new HouseState { Furniture = result, NextObjId = state.NextObjId });
        Log.Info($"[house]{(sessionId > 0 ? $"[session {sessionId}]" : "")} " +
                 $"摆位修复：坏件 {broken.Count} 件 → 修好 {fixedCount}、删除 {removed}，" +
                 $"现存 {result.Count} 件、餐位 {result.Count(x => x.BizCount > 0)} 张");
        return (fixedCount, removed);
    }

    /// <summary>
    /// 读整数。★ 必须容忍「JSON 里存的是别的数字 CLR 类型」：
    /// <see cref="WriteState"/> 写进去的是 <c>uint</c>，而存档落盘再读回来会变成
    /// <c>JsonValue&lt;long&gt;</c>/<c>&lt;double&gt;</c>，直接 <c>GetValue&lt;int&gt;()</c> 会抛
    /// <c>InvalidOperationException</c> ⇒ 整条家具被当成脏数据丢掉（静默丢家具）。
    /// 与 <c>CafeFeature.ReadInt</c> 同一套写法。
    /// </summary>
    private static int ReadInt(JsonNode? node, int fallback)
    {
        if (node is null)
            return fallback;
        try
        {
            return node.GetValue<int>();
        }
        catch (Exception)
        {
            return double.TryParse(node.ToString(), out var d) ? (int)d : fallback;
        }
    }

    /// <summary>读 <c>uint</c>（<c>objId</c>/<c>parentId</c>/<c>posX</c> 等在 proto 里都是 uint32）。理由同 <see cref="ReadInt"/>。</summary>
    private static uint ReadUInt(JsonNode? node, uint fallback)
    {
        if (node is null)
            return fallback;
        try
        {
            return node.GetValue<uint>();
        }
        catch (Exception)
        {
            if (double.TryParse(node.ToString(), out var d) && d >= 0 && d <= uint.MaxValue)
                return (uint)d;
            return fallback;
        }
    }
}

/// <summary>
/// 一件家具的服务端状态（对应 <c>gsproto.furniture</c> + 内嵌 <c>objPose</c> 的可用子集）。
/// 字段与 <c>Game.NCafeItem</c>（<c>dump.cs:530422..530440</c>）一一对应：
/// <c>Guid</c>(+0x10)=<see cref="ObjId"/> / <c>Parent</c>(+0x14)=<see cref="ParentId"/> /
/// <c>Room</c>(+0x18)=<see cref="RoomId"/> / <c>X</c>(+0x1C) / <c>Y</c>(+0x20) /
/// <c>Rotate</c>(+0x24) / <c>State</c>(+0x28)=<see cref="Status"/>。
/// </summary>
internal sealed record HouseFurniture(
    uint ObjId,
    int TemplateId,
    int RoomId,
    uint ParentId,
    uint PosX,
    uint PosY,
    uint Rotate,
    int BizCount,
    int Status);

/// <summary>一个玩家的家园摆设状态（<c>Player.Extra["house"]</c> 的内存形态）。</summary>
internal sealed class HouseState
{
    /// <summary>全部家具（系统家具 + 玩家家具），下发顺序即此顺序。</summary>
    public List<HouseFurniture> Furniture { get; init; } = new();

    /// <summary>下一个可用的玩家家具 <c>objId</c>（始终 ≥ <see cref="HouseTable.MinPlayerObjId"/>）。</summary>
    public uint NextObjId { get; init; } = HouseTable.MinPlayerObjId;
}
