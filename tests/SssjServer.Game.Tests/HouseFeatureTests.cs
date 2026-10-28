using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using SssjServer.Protocol;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 家园 / 家具摆设域（pf 1048 <c>GetHouseInfoReq</c> → pf 1049 <c>GetHouseInfoRsp</c>）。
///
/// 每条断言都对应一处客户端取证（RVA / lua 文件:行号 / 配置表列名 / proto 字段号），
/// 完整推导见 <see cref="HouseFeature"/> 的类注释与 <c>work/house-domain-forensics-a.md</c>。
/// </summary>
public class HouseFeatureTests
{
    private static HouseTable Table => HouseTable.Load(TestPaths.SettingDir);

    private static Player NewPlayer(ulong roleid = 1234567UL) => new("house-test", roleid);

    private static Packet ReqPacket(ulong roleid, uint serial = 42)
        => new()
        {
            Pf = GamePf.GetHouseInfo,
            Serial = serial,
            Payload = new GetHouseInfoReq { Roleid = roleid }.ToByteArray(),
        };

    /// <summary>
    /// 直接驱动 pf 1048 的 handler 并取回 pf 1049 应答。
    ///
    /// handler 内部全程同步（末尾 <c>return Task.CompletedTask</c>），所以在这里
    /// 同步等待没有死锁风险；放在非测试方法里也顺带避开 xUnit1031 分析器。
    /// </summary>
    private static GetHouseInfoRsp RespondWith(FakeSession fake, Packet packet, HouseTable? table = null)
    {
        new HouseFeature(table ?? Table).HandleGetHouseInfo(fake, packet).GetAwaiter().GetResult();
        return (GetHouseInfoRsp)fake.LastOf(GamePf.GetHouseInfoRsp).Message;
    }

    private static GetHouseInfoRsp Respond(Player player, HouseTable? table = null, uint serial = 42)
        => RespondWith(new FakeSession { Tag = player }, ReqPacket(player.Roleid, serial), table);

    // =================================================================
    //  一、配置表（NCafeDefautItems.txt / NCafeItems.txt / Configure.txt）
    // =================================================================

    /// <summary>
    /// <c>NCafeDefautItems.txt</c> 现网 13 行有效数据，含 <b>4 张 <c>templateId=101</c> 餐位</b>
    /// （<c>BizCount</c> 列 = 1），其余 9 件 <c>BizCount = 0</c>。
    /// </summary>
    [Fact]
    public void 默认摆设表_13行_含4张101餐位()
    {
        var t = Table;
        Assert.Equal(13, t.DefaultItems.Count);

        var seats = t.DefaultItems.Where(r => r.TemplateId == 101).ToList();
        Assert.Equal(4, seats.Count);
        Assert.All(seats, s => Assert.Equal(1, s.BizCount));
        Assert.All(t.DefaultItems.Where(r => r.TemplateId != 101), r => Assert.Equal(0, r.BizCount));
    }

    /// <summary>
    /// <c>Pos</c> 列的四段语义 = <c>parentId, posX, posY, rotate</c>。
    ///
    /// 判据是与 <c>NCafeThemes.txt</c> Id=1「初始咖啡馆」/ Id=2「初始宿舍」的
    /// <c>ItemN</c> 列逐字同源（那边的格式是 <c>templateId,parentId,posX,posY,rotate</c>）：
    /// 这里抽查表头那三行 —— <c>401,6,0,5,0</c> / <c>501,2,29,9,0</c> / <c>101,2,5,23,180</c>。
    /// </summary>
    [Fact]
    public void 默认摆设表_Pos列语义_parentId_posX_posY_rotate()
    {
        var rows = Table.DefaultItems;

        var r1 = rows.Single(r => r.Id == 1);            // NCafeThemes Id=1 Item0 = "401,6,0,5,0"
        Assert.Equal(401, r1.TemplateId);
        Assert.Equal(1, r1.Room);
        Assert.Equal(6u, r1.ParentId);                  // Wall4（NCafeItems Id=6）
        Assert.Equal(0u, r1.PosX);
        Assert.Equal(5u, r1.PosY);
        Assert.Equal(0u, r1.Rotate);

        var r2 = rows.Single(r => r.Id == 2);            // Item1 = "501,2,29,9,0"
        Assert.Equal(501, r2.TemplateId);
        Assert.Equal(2u, r2.ParentId);                  // Floor 咖啡馆（NCafeItems Id=2）
        Assert.Equal(29u, r2.PosX);
        Assert.Equal(9u, r2.PosY);

        var r4 = rows.Single(r => r.Id == 4);            // Item3 = "101,2,5,23,180"
        Assert.Equal(101, r4.TemplateId);
        Assert.Equal(2u, r4.ParentId);
        Assert.Equal(5u, r4.PosX);
        Assert.Equal(23u, r4.PosY);
        Assert.Equal(180u, r4.Rotate);

        var r10 = rows.Single(r => r.Id == 10);          // NCafeThemes Id=2 Item0 = "703,10,10,18,180"
        Assert.Equal(703, r10.TemplateId);
        Assert.Equal(2, r10.Room);                       // Room=2 休息室
        Assert.Equal(10u, r10.ParentId);                 // Floor 休息室（NCafeItems Id=10）
    }

    /// <summary>
    /// 13 件默认摆设的 <c>parentId</c> 全部落在该模板的 <c>DicArea</c> 白名单里（13/13 命中）——
    /// 这是「<c>Pos</c> 第 1 段就是 <c>parentId</c>」最强的一条交叉验证。
    /// </summary>
    [Fact]
    public void 默认摆设表_parentId全部命中DicArea白名单()
    {
        var t = Table;
        foreach (var row in t.DefaultItems)
        {
            Assert.True(t.IsAllowedParent(row.TemplateId, row.ParentId),
                $"Id={row.Id} templateId={row.TemplateId} 的 parentId={row.ParentId} 不在 DicArea 内");
        }

        // 抽查白名单本身：101 餐位只能摆在地面（[2,10]），401 墙饰只能挂墙（含 6）
        Assert.Equal(new uint[] { 2, 10 }, t.Items[101].DicArea);
        Assert.Contains(6u, t.Items[401].DicArea);
        Assert.DoesNotContain(2u, t.Items[401].DicArea);
    }

    /// <summary>
    /// 系统家具的判据是 <c>Id == Type</c>，且全部落在 <c>[1, 85]</c>；
    /// 其中 <b>72 = 外景</b>（<c>Configure.OutDoorScene</c>）—— 这条同时印证
    /// <c>GetOutDoorItem</c>（RVA <c>0x1D6353C</c>）兜底 <c>GetItem(0x48)</c> 里的 72
    /// 既是 objId 也是 templateId/Type。
    /// </summary>
    [Fact]
    public void 家具模板表_系统家具Id等于Type_且含外景72()
    {
        var t = Table;
        Assert.NotEmpty(t.SystemItems);
        Assert.All(t.SystemItems, r =>
        {
            Assert.Equal(r.Id, r.Type);
            Assert.InRange((uint)r.Id, 1u, HouseTable.MaxSystemObjId);
        });

        var outdoor = t.Items[(int)HouseTable.OutdoorObjId];
        Assert.Equal(72, outdoor.Type);
        Assert.Equal(72, t.OutDoorScene);
    }

    /// <summary>
    /// <c>Configure.txt</c>（= 资源包内的 <c>NCafe/Configure.json</c>）的三个关键值：
    /// <c>SystemItems</c> 27 项容器节点、<c>DefaultItems</c> 27 项系统 prefab、
    /// <c>MinNormalItemId = 100</c>。
    /// </summary>
    [Fact]
    public void Configure表_SystemItems与DefaultItems与MinNormalItemId()
    {
        var t = Table;

        Assert.Equal(27, t.ConfigureSystemTypes.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 22, 23, 30, 31, 32, 33, 34, 35 },
                     t.ConfigureSystemTypes);

        Assert.Equal(27, t.ConfigureDefaultTypes.Count);
        Assert.Contains(72, t.ConfigureDefaultTypes);        // 外景
        Assert.DoesNotContain(53, t.ConfigureDefaultTypes);  // Wall3 整墙被门窗 55/56 替代
        Assert.Contains(55, t.ConfigureDefaultTypes);
        Assert.Contains(56, t.ConfigureDefaultTypes);

        Assert.Equal(100, t.MinNormalItemId);
        Assert.True(HouseTable.MinPlayerObjId >= (uint)t.MinNormalItemId,
            "服务端分配的 objId 必须 ≥ MinNormalItemId，否则会被 IsCustomItemId(0x1802368) 当成默认家具");
    }

    /// <summary>
    /// 三个「场景挂点宿主」（<c>NCafePoints.txt</c> 的 <c>ItemType</c> 列）
    /// 都在 <c>Configure.DefaultItems</c> 里 ⇒ 新号必然具备，服务端不必也不该补发：
    /// <c>56</c> 大门（<c>EnterDoor</c> + <b><c>GuestBrith</c> 顾客出生点</b>）、
    /// <c>58</c> 吧台（<c>Batai</c> 点餐 ×3）、<c>59</c> 吧台墙（<c>MakeCoffee</c> ×3）。
    /// </summary>
    [Fact]
    public void Configure表_场景挂点宿主56_58_59全部自带()
    {
        var t = Table;
        Assert.Equal(new[] { 56, 58, 59 }, HouseRooms.ScenePointHostTypes);
        foreach (int type in HouseRooms.ScenePointHostTypes)
            Assert.Contains(type, t.ConfigureDefaultTypes);
    }

    /// <summary>
    /// <c>IsClientOwnedObjId</c> 必须挡住客户端自建的 54 件默认家具的 objId，
    /// 并放过 <c>≥ 1000</c> 的玩家家具 —— 撞车会静默覆盖（<c>Items</c> 是
    /// <c>Dictionary&lt;uint, NCafeItem&gt;</c>，<c>set_Item 0x1D69580</c>）。
    /// </summary>
    [Fact]
    public void IsClientOwnedObjId_挡住系统家具_放过玩家家具()
    {
        var t = Table;
        Assert.True(t.IsClientOwnedObjId(1));    // SceneRoot
        Assert.True(t.IsClientOwnedObjId(50));   // 地板
        Assert.True(t.IsClientOwnedObjId(72));   // 外景
        Assert.True(t.IsClientOwnedObjId(85));   // 宿舍2 屋顶
        Assert.False(t.IsClientOwnedObjId(HouseTable.MinPlayerObjId));
        Assert.False(t.IsClientOwnedObjId(1012));
    }

    /// <summary>餐位类别（<c>NCafeItemTypes.txt</c> Id=1「咖啡馆-餐位」）的 <c>Types[0]</c> = 100。</summary>
    [Fact]
    public void 家具类别表_餐位Type是100()
    {
        Assert.Equal(100, Table.SeatType);
        Assert.Equal(100, Table.Items[101].Type);
    }

    /// <summary>
    /// <c>NCafeItems.txt</c> 的有效模板数是 <b>908</b>（最大 <c>Id = 5118</c>）。
    ///
    /// ★ 这条钉的是**行数口径**：表里有一批**纯制表符空行**（40 个 <c>\t</c>、零个非空单元格），
    /// 按「文件行数 - 表头」核对会误以为加载少读了
    /// —— 本测试防止有人「修」掉那个正确的跳过逻辑。
    /// ★★ 第十轮更新：配置表快照换成新版 <c>setting.u</c> 后 863 → **908**（最大 id 5097 → 5118）。
    /// </summary>
    [Fact]
    public void 家具模板表_有效行数908_最大id5118()
    {
        var t = Table;
        Assert.Equal(908, t.Items.Count);
        Assert.Equal(5118, t.Items.Keys.Max());
        Assert.All(t.Items.Keys, id => Assert.True(id > 0));
    }

    /// <summary>
    /// 13 件默认摆设的 <c>Type</c> 全部 ≥ <c>MinNormalItemId</c>(100) ⇒
    /// <c>RecoverDefaultItem</c>（RVA <c>0x1D6AD3C</c>，<c>0x1D6AE14 IsCustomItemId</c>）
    /// 对它们直接 return，不会把客户端自建的默认家具撤下。
    /// 这正是「这 13 件能安全下发、而系统家具不能」的分界线。
    /// </summary>
    [Fact]
    public void 默认摆设的Type全部大于等于100_不会触发RecoverDefaultItem()
    {
        var t = Table;
        foreach (var row in t.DefaultItems)
        {
            int type = t.Items[row.TemplateId].Type;
            Assert.True(type >= t.MinNormalItemId,
                $"templateId={row.TemplateId} 的 Type={type} < MinNormalItemId，下发会撤下默认家具");
        }
    }

    // =================================================================
    //  二、pf 1049 应答的必填字段
    // =================================================================

    /// <summary>
    /// <c>GetHouseInfoRsp.IsInitialized</c>（RVA <c>0x31EFAF8</c>，掩码 <c>0x3</c>）要求
    /// <c>roleid</c>(#1) + <c>houseInfo</c>(#2)；<c>visitinfo</c>(#3) 协议上 optional，
    /// 但 <c>NCafeNet.lua:173</c> 是<b>无条件</b> <c>HouseInfo.VisitedList:Clear()</c>，
    /// 而 <c>VisitedList</c>(+0x50) 只在 <c>has_visitinfo</c> 为真时才被懒创建
    /// （<c>AddVisitedInfos</c> RVA <c>0x1A379D0</c>，判定点 <c>0x1D68FB8</c>）⇒ 三个都必须有。
    /// </summary>
    [Fact]
    public void 应答_三个字段的has位全为真()
    {
        var rsp = Respond(NewPlayer());

        Assert.True(rsp.HasRoleid);
        Assert.True(rsp.HasHouseInfo);
        Assert.True(rsp.HasVisitinfo);
        Assert.NotNull(rsp.HouseInfo);
        Assert.NotNull(rsp.Visitinfo);
        Assert.Empty(rsp.Visitinfo!.Visitedlist);
    }

    /// <summary>
    /// <c>roleid</c> 必须等于本会话玩家的 roleid —— <c>RsqHouseInfos</c>
    /// <c>0x1D31DA8 cmp x20,x0; b.ne → ret</c> 不等就整包静默丢弃（连 <c>Items.Clear()</c> 都不执行）。
    /// 请求里带了别人的 roleid 时也要按本会话玩家回。
    /// </summary>
    [Fact]
    public void 应答_roleid恒为本会话玩家_忽略请求里的值()
    {
        var player = NewPlayer(998877UL);
        var fake = new FakeSession { Tag = player };

        // 请求里塞一个别人的 roleid
        var packet = new Packet
        {
            Pf = GamePf.GetHouseInfo,
            Serial = 7,
            Payload = new GetHouseInfoReq { Roleid = 111UL }.ToByteArray(),
        };

        var rsp = RespondWith(fake, packet);
        Assert.Equal(998877UL, rsp.Roleid);
        Assert.Equal(998877UL, rsp.HouseInfo!.Roleid);
    }

    /// <summary>回包必须带上请求的 serial（客户端按 serial 匹配回调）。</summary>
    [Fact]
    public void 应答_serial回显()
    {
        var player = NewPlayer();
        var fake = new FakeSession { Tag = player };
        RespondWith(fake, ReqPacket(player.Roleid, 4321));

        var sent = fake.LastOf(GamePf.GetHouseInfoRsp);
        Assert.Equal(4321u, sent.Serial);
    }

    /// <summary>
    /// ★ <c>furniture.pose</c> 必填：<c>NCafeItem.UpdateInfo</c>（RVA <c>0x1D2C68C</c>）
    /// 在 <c>0x1D2C92C get_pose</c> 后直接 <c>0x1D2C944 get_posX</c>
    /// （posY <c>0x1D2C97C</c> / rotate <c>0x1D2C9B4</c>），
    /// 中间只有 <c>0x1D2C934 cbnz x21,+8; bl #0x13F30AC</c>（抛 NRE），
    /// 且 <c>has_pose</c> 全链路一次都没被调用。
    /// </summary>
    [Fact]
    public void 应答_每件家具都带pose且三个坐标字段齐全()
    {
        var rsp = Respond(NewPlayer());
        var list = rsp.HouseInfo!.FurnitureList;

        Assert.NotEmpty(list);
        Assert.All(list, f =>
        {
            Assert.True(f.HasPose, "pose 缺失 = UpdatePlayerHouseInfo 的循环里逐件 NRE");
            Assert.NotNull(f.Pose);
            Assert.True(f.Pose!.HasPosX);
            Assert.True(f.Pose.HasPosY);
            Assert.True(f.Pose.HasRotate);
        });
    }

    /// <summary>
    /// <c>furniture</c> 的八个字段里，客户端会解引用的六个必须有 has 位：
    /// <c>templateId</c>(#1) / <c>objId</c>(#2) / <c>parentId</c>(#3) / <c>roomId</c>(#4) /
    /// <c>pose</c>(#5) / <c>status</c>(#6)；<c>isNew</c>(#7) 与 <c>bizcount</c>(#8) 也一并写出。
    /// 映射口径逐字照抄客户端自己的上行 <c>ReqSaveItems</c>（RVA <c>0x1D6B7A8</c>）。
    /// </summary>
    [Fact]
    public void 应答_furniture八个字段的has位全为真()
    {
        var rsp = Respond(NewPlayer());
        Assert.All(rsp.HouseInfo!.FurnitureList, f =>
        {
            Assert.True(f.HasTemplateId);
            Assert.True(f.HasObjId);
            Assert.True(f.HasParentId);
            Assert.True(f.HasRoomId);
            Assert.True(f.HasPose);
            Assert.True(f.HasStatus);
            Assert.True(f.HasIsNew);
            Assert.True(f.HasBizcount);
        });
    }

    /// <summary>
    /// <c>objPose.templateId</c>(#1) 与外层 <c>furniture.templateId</c> 同源、
    /// <c>objPose.parentTemplateId</c>(#2) 与 <c>furniture.parentId</c> 同源 ——
    /// 取证 <c>ReqSaveItems</c>：<c>0x1D6B9E0 get_Id → 0x1D6BA00 set_templateId</c>、
    /// <c>0x1D6BA04 ldr w24,[x22,#0x14] → 0x1D6BA20 set_parentTemplateId</c>
    /// （<c>+0x14</c> 正是 <c>furniture.parentId</c> 的来源 <c>NCafeItem.Parent</c>）。
    /// </summary>
    [Fact]
    public void 应答_objPose与外层字段同源()
    {
        var rsp = Respond(NewPlayer());
        Assert.All(rsp.HouseInfo!.FurnitureList, f =>
        {
            Assert.Equal(f.TemplateId, f.Pose!.TemplateId);
            Assert.Equal(f.ParentId, f.Pose.ParentTemplateId);
        });
    }

    /// <summary>
    /// 客户端咖啡馆这一路<b>从不读</b> <c>objPose.place</c>(#3)
    /// （<c>get_place</c> 的调用者只有 <c>RoomThemeData.ObjPose..ctor</c>、
    /// <c>RoomDiyManager.IsThemeDirty</c> 与 xLua wrapper），
    /// 客户端自己上行时也不写它（<c>ReqSaveItems</c> 无 <c>set_place</c>）——
    /// 所以本实现同样不写，避免编造字段。
    /// </summary>
    [Fact]
    public void 应答_不写objPose的place字段()
    {
        var rsp = Respond(NewPlayer());
        Assert.All(rsp.HouseInfo!.FurnitureList, f => Assert.False(f.Pose!.HasPlace));
    }

    /// <summary>新号第一次进店不该看到「NEW」角标（<c>IsHaveNewItem</c> RVA <c>0x1D65170</c> 用它点红点）。</summary>
    [Fact]
    public void 应答_isNew恒为false()
    {
        var rsp = Respond(NewPlayer());
        Assert.All(rsp.HouseInfo!.FurnitureList, f => Assert.False(f.IsNew));
    }

    /// <summary>
    /// ★★ 每件家具的 <c>pose</c> 必须是**独立实例**，不能共享一个 <c>objPose</c>。
    ///
    /// <c>NCafeItem.UpdateInfo</c> 的 <c>0x1D2C854 set_pose</c> 是<b>引用赋值</b>
    /// （深拷贝只做到 <c>netdata</c> 一层），而 <c>CheckDataChanged</c>（RVA <c>0x1D2CFE4</c>）
    /// 会往同一个 <c>objPose</c> 上<b>回写</b>坐标（<c>0x1D2D1D4</c>/<c>0x1D2D1F4 set_posX</c>）
    /// ⇒ 共享实例会让玩家挪动一件家具时静默污染另一件的位置。
    /// </summary>
    [Fact]
    public void 应答_每件家具的pose是独立实例()
    {
        var list = Respond(NewPlayer()).HouseInfo!.FurnitureList;
        var poses = list.Select(f => f.Pose!).ToList();

        Assert.Equal(list.Count, poses.Count);
        // 引用去重后个数不变 ⇒ 没有两件家具共享同一个 objPose 对象
        Assert.Equal(poses.Count, poses.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    /// <summary>
    /// <c>parentId</c> / <c>objPose.parentTemplateId</c> 的语义是<b>父物件的 objId</b>
    /// （尽管字段名叫 <c>parentTemplateId</c>）：家具树 <c>NCafeTree.&lt;InitNodes&gt;b__0</c>
    /// （RVA <c>0x1801830</c>）拿 <c>item.Parent == node.Item.Id</c> 挂树，
    /// 而容器节点由客户端自建时 <c>Guid = templateId</c>（<c>0x1D61670</c>）——
    /// 所以本表的 2 / 6 / 10 必须同时满足「是容器节点的 templateId」与「是它的 objId」。
    /// 挂不上树的后果是**不加载、不显示、不报错**。
    /// </summary>
    [Fact]
    public void 应答_parentId全部指向客户端自建的容器节点()
    {
        var t = Table;
        var list = Respond(NewPlayer(), t).HouseInfo!.FurnitureList;

        Assert.All(list, f =>
        {
            // ① 该 parentId 是一个真实存在的系统家具（Id == Type，因而 objId == templateId）
            Assert.True(t.Items.TryGetValue((int)f.ParentId, out var parent),
                $"parentId={f.ParentId} 不在 NCafeItems.txt 里，家具会挂不上树");
            Assert.True(parent!.IsSystem, $"parentId={f.ParentId} 不是系统家具（Id != Type）");
            // ② 它由客户端自建 ⇒ Items 字典里一定有这个 key
            Assert.True(t.IsClientOwnedObjId(f.ParentId),
                $"parentId={f.ParentId} 不在 Configure 的 SystemItems/DefaultItems 里");
        });

        // 现网只用到三个容器节点：2 Floor(咖啡馆) / 6 Wall4 / 10 Floor(休息室)
        Assert.Equal(new uint[] { 2, 6, 10 }, list.Select(f => f.ParentId).Distinct().OrderBy(x => x));
    }

    // =================================================================
    //  三、objId 分配规则
    // =================================================================

    /// <summary>
    /// 玩家家具的 <c>objId</c> 从 <see cref="HouseTable.MinPlayerObjId"/>(1000) 起<b>连续</b>分配，
    /// 顺序跟着 <c>NCafeDefautItems.Id</c> 列升序（⇒ 分配结果可复现）。
    /// </summary>
    [Fact]
    public void objId_从1000起连续分配()
    {
        var state = HouseFeature.CreateDefault(Table);
        var ids = state.Furniture.Select(f => f.ObjId).ToList();

        Assert.Equal(13, ids.Count);
        Assert.Equal(Enumerable.Range(1000, 13).Select(i => (uint)i), ids);
        Assert.Equal(1013u, state.NextObjId);
    }

    /// <summary>
    /// 所有下发的 <c>objId</c> 必须 <b>≥ 1000</b>，一件系统 objId（<c>≤ 85</c>）都不能有 ——
    /// 系统家具由客户端 <c>InitFixedData</c>(<c>0x1D61500</c>) / <c>InitDefaultData</c>(<c>0x1D61834</c>)
    /// 自建（<c>Guid = templateId</c>，<c>0x1D619A8</c>），
    /// 服务端回传会踩 <c>RecoverDefaultItem</c>(<c>0x1D6AD3C</c>) 把它们的 <c>Room</c> 清 0。
    /// </summary>
    [Fact]
    public void objId_全部大于等于1000_不下发系统家具()
    {
        var rsp = Respond(NewPlayer());
        var list = rsp.HouseInfo!.FurnitureList;

        Assert.All(list, f => Assert.True(f.ObjId >= HouseTable.MinPlayerObjId,
            $"objId={f.ObjId} 落在客户端自建的默认家具区间，会静默覆盖"));
        Assert.Equal(0, HouseFeature.CountSystem(HouseFeature.CreateDefault(Table)));
        Assert.Equal(13, HouseFeature.CountPlayer(HouseFeature.CreateDefault(Table)));
    }

    /// <summary>
    /// <c>objId</c> 不能重复：<c>Items</c> 是 <c>Dictionary&lt;uint, NCafeItem&gt;</c> 且
    /// <c>objId</c> 就是 key（<c>0x1D693E0 TryGetValue</c> / <c>0x1D69580 set_Item</c>），
    /// 重复时后者<b>静默覆盖</b>前者（不抛、也不再补建 <c>AddObjs</c> 附属件）。
    /// </summary>
    [Fact]
    public void objId_无重复()
    {
        var rsp = Respond(NewPlayer());
        var ids = rsp.HouseInfo!.FurnitureList.Select(f => f.ObjId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    /// <summary>
    /// <c>objId</c> 也要避开客户端 <c>AllocGUID()</c>（RVA <c>0x1D62104</c>）自建附属件用的
    /// <c>0xFFFFFFFF</c> 起递减那一段。1000 起显然安全，这条断言是回归护栏。
    /// </summary>
    [Fact]
    public void objId_远离AllocGUID的递减区间()
    {
        var state = HouseFeature.CreateDefault(Table);
        Assert.All(state.Furniture, f => Assert.True(f.ObjId < 0xFFFF0000u));
    }

    // =================================================================
    //  四、roomId / roomList
    // =================================================================

    /// <summary>
    /// ★ <c>roomId</c> 是<b>唯一</b>决定家具区域归属的字段：
    /// <c>GetAreaItems</c>（RVA <c>0x1D6266C</c>）遍历 <c>Items.Values</c> 只比
    /// <c>item.Room == areaType</c>（<c>0x1D627B0 ldr w8,[x21,#0x18]; cmp w8,w20</c>）。
    /// 取值必须落在 <c>Game.NCafeAreaType</c> 的真实房间 <c>{1 Cafe, 2 Rest, 6 Dorm}</c>。
    /// 现网 13 件里 9 件在经营区、4 件在休息室。
    /// </summary>
    [Fact]
    public void roomId_只用1_2_6_且分布符合默认摆设表()
    {
        var rsp = Respond(NewPlayer());
        var list = rsp.HouseInfo!.FurnitureList;

        Assert.All(list, f => Assert.Contains((int)f.RoomId, HouseRooms.Ids));
        Assert.Equal(9, list.Count(f => f.RoomId == 1));   // 401×2 + 501×2 + 505 + 101×4
        Assert.Equal(4, list.Count(f => f.RoomId == 2));   // 703/704/705/706
        Assert.Equal(0, list.Count(f => f.RoomId == 6));   // 女生宿舍未解锁，表里没有
    }

    /// <summary>
    /// 4 张餐位（<c>bizcount &gt; 0</c>）必须全在<b>经营区</b>（<c>roomId = 1</c>）——
    /// 摆错区的后果是 <c>NCafe.lua:163..180</c> <c>FixNoSeatProblem</c> 用
    /// <c>GetAreaItems(NCafeAreaType.Cafe)</c> 一张也筛不到 ⇒ 座位 0 ⇒ 顾客永远进不了店（静默失败）。
    /// </summary>
    [Fact]
    public void 餐位_4张且全在经营区()
    {
        var rsp = Respond(NewPlayer());
        var seats = rsp.HouseInfo!.FurnitureList.Where(f => f.Bizcount > 0).ToList();

        Assert.Equal(4, seats.Count);
        Assert.All(seats, s =>
        {
            Assert.Equal(101u, s.TemplateId);
            Assert.Equal(1u, s.RoomId);
            Assert.Equal(1u, s.Bizcount);
            Assert.Equal(2u, s.ParentId);      // Floor 咖啡馆
        });
    }

    /// <summary>
    /// ★ <c>roomList</c> 不能为空，且必须覆盖玩家能进编辑器的三个区 <c>{1, 2, 6}</c> ——
    /// <c>ReqSaveArea</c>（RVA <c>0x1D6B11C</c>）拿 <c>GetArea(type)</c> 之后
    /// <c>0x1D6B3AC cbnz x21,+12; mov x0,xzr; bl #0x13F30AC</c> <b>无 nil 保护</b>，
    /// 缺哪个区，玩家在那个区一保存装修就 NRE。
    /// </summary>
    [Fact]
    public void roomList_恒发1_2_6三个区()
    {
        var rsp = Respond(NewPlayer());
        var rooms = rsp.HouseInfo!.RoomList;

        Assert.Equal(3, rooms.Count);
        Assert.Equal(HouseRooms.Ids.Select(i => (uint)i), rooms.Select(r => r.RoomId));
        Assert.All(rooms, r =>
        {
            Assert.True(r.HasRoomId);
            Assert.True(r.HasThemeId);
            Assert.Equal(0u, r.ThemeId);        // 「没用主题」是合法正常态（GetTheme 0x1D62DD8 TryGetValue）
            Assert.True(r.HasFriendGirlId);
            Assert.Empty(r.GirlIds);            // OnUpdateRoom(0x1D696F0) 根本不读它
        });
    }

    /// <summary>
    /// <c>roomData.furnitureIds</c> 必须与各家具的 <c>roomId</c> 一致（同一份数据的两种索引）。
    /// 客户端 <c>OnUpdateRoom</c> 不会拿它反查 <c>Items</c>，但不一致会让 DIY 编辑器的
    /// 「本区家具数」与场景实际摆出来的对不上。
    /// </summary>
    [Fact]
    public void roomList_furnitureIds与家具roomId一致()
    {
        var rsp = Respond(NewPlayer());
        var cache = rsp.HouseInfo!;

        foreach (var room in cache.RoomList)
        {
            var expected = cache.FurnitureList.Where(f => f.RoomId == room.RoomId)
                                              .Select(f => f.ObjId).OrderBy(x => x).ToList();
            Assert.Equal(expected, room.FurnitureIds.OrderBy(x => x).ToList());
        }
        // 全部家具都被某个 room 收录（不能有孤儿）
        Assert.Equal(cache.FurnitureList.Count, cache.RoomList.Sum(r => r.FurnitureIds.Count));
    }

    /// <summary>
    /// <c>themeList</c> 与 <c>petList</c> 留空：
    /// 新号没买过主题（<c>decorationTheme.templateThemeId</c> 若不在 <c>NCafeThemes.txt</c>
    /// 的 49 条里，<c>NCafeTheme..ctor</c> <c>0x180061C</c> 会 NRE）；
    /// 宠物走 <see cref="CafePetFeature"/> + LuaCall 112 的 <c>petstatelist</c>。
    /// </summary>
    [Fact]
    public void 应答_themeList与petList为空()
    {
        var rsp = Respond(NewPlayer());
        Assert.Empty(rsp.HouseInfo!.ThemeList);
        Assert.Empty(rsp.HouseInfo.PetList);
    }

    /// <summary>
    /// 全部 <c>templateId</c> 必须在 <c>NCafeItems.txt</c> 里 ——
    /// <c>OnUpdateItem</c>（RVA <c>0x1D692A0</c>）在 <c>0x1D69364 ItemTemplate(...) == null</c> 时
    /// 打 <c>NCafe: cannot find item template, id {0} from net</c> 并<b>直接丢弃</b>（<c>0x1D69514</c>）。
    /// </summary>
    [Fact]
    public void 应答_全部templateId都在NCafeItems表里()
    {
        var t = Table;
        var rsp = Respond(NewPlayer(), t);
        Assert.All(rsp.HouseInfo!.FurnitureList, f => Assert.True(t.Items.ContainsKey((int)f.TemplateId)));
    }

    /// <summary>整包能 round-trip：序列化后按 <c>GetHouseInfoRsp</c> 解析回来字段不丢。</summary>
    [Fact]
    public void 应答_序列化后可完整解析回来()
    {
        var rsp = Respond(NewPlayer(555UL));
        var bytes = rsp.ToByteArray();
        var back = ProtoMessage.Parse<GetHouseInfoRsp>(bytes);

        Assert.Equal(555UL, back.Roleid);
        Assert.Equal(13, back.HouseInfo!.FurnitureList.Count);
        Assert.Equal(3, back.HouseInfo.RoomList.Count);
        Assert.True(back.HasVisitinfo);

        var first = back.HouseInfo.FurnitureList[0];
        Assert.Equal(1000u, first.ObjId);
        Assert.NotNull(first.Pose);
        Assert.Equal(first.TemplateId, first.Pose!.TemplateId);
    }

    // =================================================================
    //  五、持久化：Player.Extra["house"]
    // =================================================================

    /// <summary>
    /// 用<b>新键</b> <c>Player.Extra["house"]</c>，绝不碰 <c>["cafe"]</c>
    /// （<c>CafeFeature.WriteState</c> 每次进店整体覆写它）与 <c>["cafepet"]</c>（宠物域）。
    /// </summary>
    [Fact]
    public void 持久化_用house新键_不碰cafe与cafepet()
    {
        var player = NewPlayer();
        player.Extra["cafe"] = new JsonObject { ["level"] = 7 };
        player.Extra["cafepet"] = new JsonObject { ["nextuid"] = 3 };

        Respond(player);

        Assert.True(player.Extra.ContainsKey("house"));
        Assert.Equal(7, player.Extra["cafe"]!["level"]!.GetValue<int>());
        Assert.Equal(3, player.Extra["cafepet"]!["nextuid"]!.GetValue<int>());
    }

    /// <summary>存档里落的是完整的 9 个字段 + <c>nextobjid</c>（重启后 objId 分配不回退）。</summary>
    [Fact]
    public void 持久化_存档结构含九字段与nextobjid()
    {
        var player = NewPlayer();
        Respond(player);

        var node = Assert.IsType<JsonObject>(player.Extra["house"]);
        Assert.Equal(1013u, node["nextobjid"]!.GetValue<uint>());

        var arr = Assert.IsType<JsonArray>(node["furniturelist"]);
        Assert.Equal(13, arr.Count);
        var first = Assert.IsType<JsonObject>(arr[0]);
        foreach (var key in new[] { "objid", "templateid", "roomid", "parentid", "posx", "posy", "rotate", "bizcount", "status" })
            Assert.True(first.ContainsKey(key), $"存档缺字段 {key}");
    }

    /// <summary>第二次请求（已有存档）必须拿到与第一次完全一致的 objId 序列。</summary>
    [Fact]
    public void 持久化_重复请求objId稳定()
    {
        var player = NewPlayer();
        var first = Respond(player).HouseInfo!.FurnitureList.Select(f => f.ObjId).ToList();
        var second = Respond(player).HouseInfo!.FurnitureList.Select(f => f.ObjId).ToList();

        Assert.Equal(first, second);
        Assert.Equal(13, second.Count);
    }

    /// <summary>
    /// 存档里 <c>templateId</c> 不在 <c>NCafeItems.txt</c> 的家具要被丢弃 ——
    /// 下发它等于让客户端打 <c>cannot find item template</c> 并丢包，
    /// 服务端自己先过滤掉更干净（也避免「服务端算的家具数」与客户端不一致）。
    /// </summary>
    [Fact]
    public void 持久化_存档里表外templateId被丢弃()
    {
        var player = NewPlayer();
        player.Extra["house"] = new JsonObject
        {
            ["nextobjid"] = 1002,
            ["furniturelist"] = new JsonArray
            {
                new JsonObject { ["objid"] = 1000, ["templateid"] = 101, ["roomid"] = 1, ["parentid"] = 2 },
                new JsonObject { ["objid"] = 1001, ["templateid"] = 999999, ["roomid"] = 1, ["parentid"] = 2 },
            },
        };

        var rsp = Respond(player);
        var list = rsp.HouseInfo!.FurnitureList;
        Assert.Single(list);
        Assert.Equal(101u, list[0].TemplateId);
    }

    /// <summary>存档里 <c>objId</c> 重复的家具要被丢弃（否则客户端字典里后者静默覆盖前者）。</summary>
    [Fact]
    public void 持久化_存档里重复objId被丢弃()
    {
        var player = NewPlayer();
        player.Extra["house"] = new JsonObject
        {
            ["nextobjid"] = 1001,
            ["furniturelist"] = new JsonArray
            {
                new JsonObject { ["objid"] = 1000, ["templateid"] = 101, ["roomid"] = 1, ["parentid"] = 2 },
                new JsonObject { ["objid"] = 1000, ["templateid"] = 401, ["roomid"] = 1, ["parentid"] = 6 },
            },
        };

        var list = Respond(player).HouseInfo!.FurnitureList;
        Assert.Single(list);
        Assert.Equal(101u, list[0].TemplateId);
    }

    /// <summary>
    /// 存档里 <c>nextobjid</c> 小于已用最大 objId 时必须被修正 ——
    /// 否则下一件家具会分到一个已占用的 objId（静默覆盖）。
    /// </summary>
    [Fact]
    public void 持久化_nextobjid过小时被修正()
    {
        var player = NewPlayer();
        player.Extra["house"] = new JsonObject
        {
            ["nextobjid"] = 1000,        // 明显偏小
            ["furniturelist"] = new JsonArray
            {
                new JsonObject { ["objid"] = 1007, ["templateid"] = 101, ["roomid"] = 1, ["parentid"] = 2 },
            },
        };

        var state = HouseFeature.ReadOrCreate(player, Table);
        Assert.Equal(1008u, state.NextObjId);
    }

    /// <summary>显式保存的空库存不重新发放默认摆设，实例序号不回退。</summary>
    [Fact]
    public void 持久化_显式空列表保持为空()
    {
        var player = NewPlayer();
        player.Extra["house"] = new JsonObject { ["nextobjid"] = 2000, ["furniturelist"] = new JsonArray() };

        var state = HouseFeature.ReadOrCreate(player, Table);
        Assert.Empty(state.Furniture);
        Assert.Equal(2000u, state.NextObjId);
    }

    /// <summary>没有变化时 <c>WriteState</c> 返回 false（避免每次进店都写一遍存档）。</summary>
    [Fact]
    public void 持久化_无变化时不落盘()
    {
        var player = NewPlayer();
        var state = HouseFeature.CreateDefault(Table);

        Assert.True(HouseFeature.WriteState(player, state));
        Assert.False(HouseFeature.WriteState(player, state));
    }

    // =================================================================
    //  五之二、templateId 20/21 —— 唯一「服务端能造成加载条永远 0.4」的坑
    // =================================================================

    /// <summary>
    /// ★★ <c>templateId = 20 / 21</c> 是黑名单：这两行的 <c>Res</c> 列是<b>场景节点路径</b>
    /// （<c>SceneRoot/Cafe/Light</c> / <c>SceneRoot/Rest/Light</c>）而不是 <c>.prefab</c>，
    /// <c>NCafePoolController.LoadItem</c>（RVA <c>0x1A3BD10</c>）遇到它只打一条 Debug
    /// 就 <b>return 而不调回调</b>（<c>0x1A3BD54</c>）⇒ <c>InitObjects</c> 的加载链在那一格
    /// <b>静默停住</b>，<c>SetProgress(0.6)</c> 永不执行、logcat 里连一条 Error 都没有。
    /// </summary>
    [Fact]
    public void 黑名单_templateId20与21()
    {
        var t = Table;
        Assert.Equal(new[] { 20, 21 }, HouseTable.PoolBlockedTemplateIds);
        Assert.True(t.IsPoolBlockedTemplate(20));
        Assert.True(t.IsPoolBlockedTemplate(21));
        Assert.False(t.IsPoolBlockedTemplate(101));

        // 判据的数据侧核对：这两行的 Res 确实不是 prefab，且 Type 不在 Configure.SystemItems 里
        // （所以 IsSystemItem(0x1802168) 跳不过去）
        Assert.DoesNotContain(20, t.ConfigureSystemTypes);
        Assert.DoesNotContain(21, t.ConfigureSystemTypes);
        Assert.DoesNotContain(20, t.ConfigureDefaultTypes);
        Assert.DoesNotContain(21, t.ConfigureDefaultTypes);
    }

    /// <summary>正常下发的 13 件里一件 20/21 都没有（templateId 全 ≥ 100）。</summary>
    [Fact]
    public void 黑名单_默认摆设不含20与21()
    {
        var rsp = Respond(NewPlayer());
        Assert.All(rsp.HouseInfo!.FurnitureList, f =>
        {
            Assert.DoesNotContain((int)f.TemplateId, HouseTable.PoolBlockedTemplateIds);
            Assert.True(f.TemplateId >= 100, $"templateId={f.TemplateId} < 100 会触发 RecoverDefaultItem");
        });
    }

    /// <summary>存档里混进 20/21 时必须被丢弃（否则真机上就是「加载条永远 0.4、无任何报错」）。</summary>
    [Fact]
    public void 黑名单_存档里的20与21被丢弃()
    {
        var player = NewPlayer();
        player.Extra["house"] = new JsonObject
        {
            ["nextobjid"] = 1003,
            ["furniturelist"] = new JsonArray
            {
                new JsonObject { ["objid"] = 1000, ["templateid"] = 101, ["roomid"] = 1, ["parentid"] = 2 },
                new JsonObject { ["objid"] = 1001, ["templateid"] = 20, ["roomid"] = 1, ["parentid"] = 1 },
                new JsonObject { ["objid"] = 1002, ["templateid"] = 21, ["roomid"] = 2, ["parentid"] = 1 },
            },
        };

        var list = Respond(player).HouseInfo!.FurnitureList;
        Assert.Single(list);
        Assert.Equal(101u, list[0].TemplateId);
    }

    /// <summary>
    /// 存档里混进「客户端自建的系统 objId」时必须丢弃 ——
    /// 覆盖地板/墙/外景/灯光会让家具树 <c>NCafeTree.InitNodes</c>（<c>0x180130C</c>）取不到
    /// <c>Items[1]</c>/<c>Items[sysId]</c> 而 NRE，直接打死 state 0。
    /// </summary>
    [Fact]
    public void 黑名单_存档里的系统objId被丢弃()
    {
        var player = NewPlayer();
        player.Extra["house"] = new JsonObject
        {
            ["nextobjid"] = 1001,
            ["furniturelist"] = new JsonArray
            {
                new JsonObject { ["objid"] = 1000, ["templateid"] = 101, ["roomid"] = 1, ["parentid"] = 2 },
                new JsonObject { ["objid"] = 72, ["templateid"] = 72, ["roomid"] = 1, ["parentid"] = 22 },
                new JsonObject { ["objid"] = 1, ["templateid"] = 1, ["roomid"] = 1, ["parentid"] = 0 },
            },
        };

        var list = Respond(player).HouseInfo!.FurnitureList;
        Assert.Single(list);
        Assert.Equal(1000u, list[0].ObjId);
    }

    // =================================================================
    //  六、注册与降级
    // =================================================================

    /// <summary>
    /// ★★ <b><c>ctx.MapPf</c> 真的覆盖了 <c>GameModule</c> 的 pf 1048</b>。
    ///
    /// 这条是「不改 <c>GameModule.cs</c> 也能接管 pf」的前提：<c>GameModule.Install</c> 先在
    /// <c>GameModule.cs:114</c> 注册「回空 <c>HouseCache</c>」，再在 <c>:171</c> 反射装配 feature，
    /// 而 <c>PfRouter.Map</c>（<c>PfRouter.cs:16</c>）是 <c>_map[pf] = handler</c> 覆盖赋值
    /// ⇒ 后注册的 <see cref="HouseFeature"/> 胜出。
    ///
    /// 判据用<b>副作用</b>而不是回包内容（<c>FeatureHarness</c> 只能看到 pf 号）：
    /// <see cref="HouseFeature"/> 会往 <c>Player.Extra["house"]</c> 写摆设，
    /// 而 <c>GameModule.HandleGetHouseInfo</c> 一个字节都不写。
    /// </summary>
    [Fact]
    public async Task MapPf_覆盖GameModule的空HouseCache()
    {
        using var h = await FeatureHarness.CreateAsync("house-mappf");

        Assert.False(h.Player.Extra.ContainsKey("house"));

        await h.Pf.DispatchAsync(h.Session, ReqPacket(h.Player.Roleid));

        Assert.True(await h.WaitForPfAsync(GamePf.GetHouseInfoRsp),
            "pf 1049 没有被推送 —— pf 1048 的注册被谁吃掉了？");
        Assert.True(h.Player.Extra.ContainsKey("house"),
            "Extra[\"house\"] 没被写 ⇒ 仍然是 GameModule 的空 HouseCache handler 在处理 pf 1048");

        var arr = Assert.IsType<JsonArray>(h.Player.Extra["house"]!["furniturelist"]);
        Assert.Equal(13, arr.Count);
    }

    /// <summary>装配后功能名与配置表都就位（反射装配的回归护栏）。</summary>
    [Fact]
    public async Task 装配_功能名与配置表就位()
    {
        using var h = await FeatureHarness.CreateAsync("house-install");
        var feature = new HouseFeature();
        Assert.Equal("家园/家具摆设(pf 1048)", feature.Name);

        var table = HouseTable.Load(Path.GetFullPath(h.Options.SettingDirectory));
        Assert.Equal(13, table.DefaultItems.Count);
        Assert.NotEmpty(table.Items);
    }

    /// <summary>
    /// 未登录会话（<c>Tag</c> 非 <see cref="Player"/>）也必须回包 ——
    /// pf 1048 是带 serial 的请求，不回会让客户端回调永久悬挂。
    /// 此时只能回空 <c>HouseCache</c>，但三个 has 位仍要为真。
    /// </summary>
    [Fact]
    public void 未登录会话_仍回包且三字段齐全()
    {
        var fake = new FakeSession();          // 不挂 Player
        var rsp = RespondWith(fake, ReqPacket(4242UL));

        Assert.Equal(4242UL, rsp.Roleid);
        Assert.True(rsp.HasHouseInfo);
        Assert.True(rsp.HasVisitinfo);
        Assert.Empty(rsp.HouseInfo!.FurnitureList);
        // roomList 仍然要有三个区（ReqSaveArea 的 nil 保护缺失与登录状态无关）
        Assert.Equal(3, rsp.HouseInfo.RoomList.Count);
    }

    /// <summary>配置表全缺（空表）时不抛，只是发不出家具 —— 降级而不是崩。</summary>
    [Fact]
    public void 配置缺失_降级为空摆设且不抛()
    {
        var player = NewPlayer();
        var rsp = Respond(player, HouseTable.Empty);

        Assert.True(rsp.HasHouseInfo);
        Assert.Empty(rsp.HouseInfo!.FurnitureList);
        Assert.Equal(3, rsp.HouseInfo.RoomList.Count);
    }

    /// <summary><see cref="HouseTable.Load"/> 按目录做进程级缓存（同一目录拿到同一实例）。</summary>
    [Fact]
    public void 配置表_按目录进程级缓存()
    {
        Assert.Same(HouseTable.Load(TestPaths.SettingDir), HouseTable.Load(TestPaths.SettingDir));
    }

    /// <summary><c>Configure.txt</c> 的轻量提取器：数组与标量都要能取到，缺键返回空/0。</summary>
    [Fact]
    public void Configure提取器_数组与标量()
    {
        const string json = "{ \"A\" : [1,2, 3], \"B\": 42, \"C\" : { 2 : [[1,2]] } }";
        Assert.Equal(new[] { 1, 2, 3 }, HouseTable.ExtractIntArray(json, "A"));
        Assert.Equal(42, HouseTable.ExtractInt(json, "B"));
        Assert.Empty(HouseTable.ExtractIntArray(json, "Nope"));
        Assert.Equal(0, HouseTable.ExtractInt(json, "Nope"));
    }
}

/// <summary>
/// 装修域 LuaCall（126 买主题 / 127 查主题 / 128 点击 / 137 卖家具 / 138 音乐）。
///
/// <para><b>★★★ 本组最大的坑：三条命令的回包「不是对象」</b></para>
/// <list type="bullet">
///   <item><b>126</b> 成功回**裸字符串** <c>'buy_theme_succeed'</c>
///     （`LuaCall.lua:1859` 与字符串比较）；</item>
///   <item><b>127</b> 回**字符串字面量** <c>'true'</c>/<c>'false'</c>
///     —— 回布尔会因 Lua 里 <c>true == 'true'</c> 为 false 而**静默失效**
///     （`UI_CafeItemTips.lua:633/635`）；</item>
///   <item><b>128 / 138</b> 原厂两侧回包**都被注释掉** ⇒ **不回包**。</item>
/// </list>
/// </summary>
public class CafeHouseDecorTests
{
    private static JsonNode? Param(FeatureHarness h)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal("LuaCall", function);
        return Assert.IsType<JsonObject>(JsonNode.Parse(json))["tbParam"];
    }

    private static async Task<FeatureHarness> RichAsync(string account, int gold = 100_000_000)
    {
        var h = await FeatureHarness.CreateAsync(account);
        h.Player.Level = 300;
        h.Player.SetMoney(MoneyType.CoffeeCoin, gold);
        h.Player.AddMoney(HouseFeature.GoldMoneyId,
                          gold - h.Player.GetMoney(HouseFeature.GoldMoneyId));
        return h;
    }

    /// <summary>找一个「家具都有售价」的主题，便于验证扣费。</summary>
    private static int PickTheme(CafeTable t, bool needPrice = true)
    {
        foreach (var th in t.Themes.Values.OrderBy(x => x.Id))
        {
            if (th.Items.Count == 0 || th.ThemeType.Length == 0
                || th.Items.Any(i => !ShopTable.Load(TestPaths.SettingDir).GoodsOf(40001).Any(g => g.GoodsId == i.TemplateId)))
                continue;
            int price = th.Items.Select(f => t.Furniture.TryGetValue(f.TemplateId, out var r) ? r.SellPrice : 0).Sum();
            if (!needPrice || price > 0)
                return th.Id;
        }
        return 0;
    }

    // ================= 126 买主题 =================

    /// <summary>★★ 成功回包必须是**裸字符串** <c>'buy_theme_succeed'</c>。</summary>
    [Fact]
    public async Task BuyTheme_成功回裸字符串()
    {
        using var h = await RichAsync("house-buy-ok");
        var table = CafeTable.Load(TestPaths.SettingDir);
        int themeId = PickTheme(table);
        Assert.True(themeId > 0, "测试前提：要有一个含家具的主题");

        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, $"{{\"themeid\":{themeId}}}");

        // ★ tbParam 是字符串，不是对象
        var p = Param(h);
        Assert.Equal(JsonValueKind.String, p!.GetValueKind());
        Assert.Equal(HouseFeature.BuyThemeOk, p.GetValue<string>());
    }

    /// <summary>买主题只补缺件，按商品表扣咖啡币并推 pf 1032。</summary>
    [Fact]
    public async Task BuyTheme_家具入账并扣钱()
    {
        using var h = await RichAsync("house-buy-items");
        var table = CafeTable.Load(TestPaths.SettingDir);
        int themeId = PickTheme(table);
        var theme = table.Themes[themeId];
        var initial = HouseFeature.ReadOrCreate(h.Player, HouseTable.Load(TestPaths.SettingDir)).Furniture;
        int before = initial.Count;
        int missing = theme.Items.GroupBy(i => i.TemplateId)
            .Sum(g => Math.Max(0, g.Count() - initial.Count(f => f.TemplateId == g.Key)));
        int goldBefore = h.Player.GetMoney(MoneyType.CoffeeCoin);

        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, $"{{\"themeid\":{themeId}}}");

        int after = HouseFeature.ReadOrCreate(h.Player, HouseTable.Load(TestPaths.SettingDir)).Furniture.Count;
        Assert.Equal(before + missing, after);
        Assert.True(h.Player.GetMoney(MoneyType.CoffeeCoin) < goldBefore, "要按商品表扣咖啡币");
        Assert.True(await h.WaitForPfAsync(GamePf.MoneyUpdateNtf), "要推 pf 1032");
    }

    /// <summary>新购缺件只进仓库；现有房间摆位不得被购买主题覆盖。</summary>
    [Fact]
    public async Task BuyTheme_缺件进仓库且不改现有摆位()
    {
        using var h = await RichAsync("house-buy-pose");
        var table = CafeTable.Load(TestPaths.SettingDir);
        var houseTable = HouseTable.Load(TestPaths.SettingDir);
        int themeId = PickTheme(table);
        var theme = table.Themes[themeId];

        var beforeIds = HouseFeature.ReadOrCreate(h.Player, houseTable)
                                    .Furniture.Select(f => f.ObjId).ToHashSet();

        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, $"{{\"themeid\":{themeId}}}");

        var added = HouseFeature.ReadOrCreate(h.Player, houseTable)
                                .Furniture.Where(f => !beforeIds.Contains(f.ObjId)).ToList();
        Assert.NotEmpty(added);

        // Purchases belong in storage; parent=0 is valid only with room=0.
        Assert.All(added, f =>
        {
            Assert.Equal(0, f.RoomId);
            Assert.Equal(0u, f.ParentId);
        });
        Assert.All(HouseFeature.ReadOrCreate(h.Player, houseTable).Furniture.Where(f => beforeIds.Contains(f.ObjId)),
            f => Assert.NotEqual(0, f.RoomId));
    }
    /// <summary>★ 重复购买 ⇒ **不回包**（本族无失败通道，客户端只认那一个字符串）。</summary>
    [Fact]
    public async Task BuyTheme_重复购买不回包()
    {
        using var h = await RichAsync("house-buy-dup");
        int themeId = PickTheme(CafeTable.Load(TestPaths.SettingDir));
        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, $"{{\"themeid\":{themeId}}}");
        int n = h.Outbound.Count;

        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, $"{{\"themeid\":{themeId}}}");

        Assert.Equal(n, h.Outbound.Count);
    }

    /// <summary>商品所需咖啡币不足 ⇒ 不回包、不入账。</summary>
    [Fact]
    public async Task BuyTheme_钱不够不回包()
    {
        using var h = await RichAsync("house-buy-poor", gold: 0);
        var table = CafeTable.Load(TestPaths.SettingDir);
        int themeId = PickTheme(table);
        int n = h.Outbound.Count;

        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, $"{{\"themeid\":{themeId}}}");

        Assert.Equal(n, h.Outbound.Count);
        Assert.Empty(HouseFeature.ReadOwnedThemes(h.Player));
    }

    /// <summary>非法主题 id / 非法上行 ⇒ 不崩、不回包。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"themeid\":0}")]
    [InlineData("{\"themeid\":99999}")]
    public async Task BuyTheme_非法上行不崩(string payload)
    {
        using var h = await RichAsync($"house-buy-bad{payload.GetHashCode() & 0xffff}");
        int n = h.Outbound.Count;
        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, payload);
        Assert.Equal(n, h.Outbound.Count);
    }

    // ================= 127 查主题 =================

    /// <summary>
    /// ★★ 回包必须是**字符串** <c>'true'</c>/<c>'false'</c>，不能是布尔。
    /// 消费点 `UI_CafeItemTips.lua:633` 是 <c>if self.HaveTheme == 'true'</c>。
    /// </summary>
    [Fact]
    public async Task CheckTheme_回字符串不是布尔()
    {
        using var h = await RichAsync("house-check");
        int themeId = PickTheme(CafeTable.Load(TestPaths.SettingDir));

        // 未购 ⇒ 'false'
        await h.DispatchLuaCallAsync(HouseFeature.CheckThemeCmd, $"{{\"themeid\":{themeId}}}");
        var p1 = Param(h);
        Assert.Equal(JsonValueKind.String, p1!.GetValueKind());   // ★ 不是 True/False
        Assert.Equal(HouseFeature.HaveThemeFalse, p1.GetValue<string>());

        // 买了之后 ⇒ 'true'
        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, $"{{\"themeid\":{themeId}}}");
        await h.DispatchLuaCallAsync(HouseFeature.CheckThemeCmd, $"{{\"themeid\":{themeId}}}");
        var p2 = Param(h);
        Assert.Equal(JsonValueKind.String, p2!.GetValueKind());
        Assert.Equal(HouseFeature.HaveThemeTrue, p2.GetValue<string>());
    }

    /// <summary>上行键名有多种可能（未取证）⇒ 四种都要认。</summary>
    [Theory]
    [InlineData("themeid")]
    [InlineData("themeId")]
    [InlineData("id")]
    [InlineData("Id")]
    public async Task CheckTheme_多种键名都认(string key)
    {
        using var h = await RichAsync($"house-check-{key}");
        int themeId = PickTheme(CafeTable.Load(TestPaths.SettingDir));
        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, $"{{\"themeid\":{themeId}}}");

        await h.DispatchLuaCallAsync(HouseFeature.CheckThemeCmd, $"{{\"{key}\":{themeId}}}");

        Assert.Equal(HouseFeature.HaveThemeTrue, Param(h)!.GetValue<string>());
    }

    // ================= 137 卖家具 =================

    /// <summary>★ 回包形态 <c>{objIds:[...]}</c>（`LuaCall.lua:1878` 原厂注释）。</summary>
    [Fact]
    public async Task SellFur_回objIds并进账()
    {
        using var h = await RichAsync("house-sell");
        var cafe = CafeTable.Load(TestPaths.SettingDir);
        int themeId = PickTheme(cafe);
        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, $"{{\"themeid\":{themeId}}}");

        var state = HouseFeature.ReadOrCreate(h.Player, HouseTable.Load(TestPaths.SettingDir));
        var mine = state.Furniture.Where(f => f.ObjId >= HouseTable.MinPlayerObjId
            && (f.RoomId == 0 || f.ParentId == 0)
            && cafe.Furniture.TryGetValue(f.TemplateId, out var row) && row.SellPrice > 0).Take(2).ToList();
        Assert.NotEmpty(mine);
        int goldBefore = h.Player.GetMoney(HouseFeature.GoldMoneyId);

        string ids = string.Join(",", mine.Select(f => f.ObjId));
        await h.DispatchLuaCallAsync(HouseFeature.SellFurCmd, $"{{\"objIds\":[{ids}]}}");

        var p = Assert.IsType<JsonObject>(Param(h));
        var arr = Assert.IsType<JsonArray>(p["objIds"]);
        Assert.Equal(mine.Count, arr.Count);
        Assert.Equal(goldBefore + mine.Sum(f => cafe.Furniture[f.TemplateId].SellPrice),
            h.Player.GetMoney(HouseFeature.GoldMoneyId));

        // 家具真的没了
        var after = HouseFeature.ReadOrCreate(h.Player, HouseTable.Load(TestPaths.SettingDir));
        foreach (var f in mine)
            Assert.DoesNotContain(after.Furniture, x => x.ObjId == f.ObjId);
    }

    [Theory]
    [InlineData(1, 2, false)] // 已摆放
    [InlineData(0, 0, true)] // 金币已满
    public async Task SellFur_不可出售时库存和金币不变(int room, uint parent, bool fullMoney)
    {
        using var h = await RichAsync($"house-sell-guard-{room}-{fullMoney}");
        var table = HouseTable.Load(TestPaths.SettingDir);
        var cafe = CafeTable.Load(TestPaths.SettingDir);
        int template = cafe.Furniture.Values.First(f => f.SellPrice > 0 && table.Items.ContainsKey(f.Id)).Id;
        var furniture = new HouseFurniture(1000, template, room, parent, 0, 0, 0, 1, 0);
        HouseFeature.WriteState(h.Player, new HouseState
        {
            Furniture = new List<HouseFurniture> { furniture }, NextObjId = 1001
        });
        if (fullMoney) h.Player.SetMoney(HouseFeature.GoldMoneyId, int.MaxValue);
        int before = h.Player.GetMoney(HouseFeature.GoldMoneyId);

        await h.DispatchLuaCallAsync(HouseFeature.SellFurCmd, "{\"objIds\":[1000,1000]}");

        Assert.Empty(Assert.IsType<JsonArray>(Param(h)!["objIds"]));
        Assert.Equal(before, h.Player.GetMoney(HouseFeature.GoldMoneyId));
        Assert.Equal(furniture, Assert.Single(HouseFeature.ReadOrCreate(h.Player, table).Furniture));
    }

    /// <summary>★ 系统家具（objId &lt; 1000）不能被卖 —— 回空清单。</summary>
    [Fact]
    public async Task SellFur_系统家具不能卖()
    {
        using var h = await RichAsync("house-sell-sys");
        await h.DispatchLuaCallAsync(HouseFeature.SellFurCmd, "{\"objIds\":[1,2,3]}");

        var p = Assert.IsType<JsonObject>(Param(h));
        Assert.Empty(Assert.IsType<JsonArray>(p["objIds"]));
    }

    /// <summary>非法上行 ⇒ 回空清单、不崩（客户端 OnRecover 要拿到对象）。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"objIds\":[]}")]
    public async Task SellFur_非法上行回空清单(string payload)
    {
        using var h = await RichAsync($"house-sell-bad{payload.Length}");
        await h.DispatchLuaCallAsync(HouseFeature.SellFurCmd, payload);
        var p = Assert.IsType<JsonObject>(Param(h));
        Assert.Empty(Assert.IsType<JsonArray>(p["objIds"]));
    }

    // ================= 128 / 138 不回包 =================

    /// <summary>★ 128 点击物品：原厂两侧回包都被注释 ⇒ **不回包**。</summary>
    [Fact]
    public async Task ClickItem_不回包()
    {
        using var h = await RichAsync("house-click");
        int n = h.Outbound.Count;
        await h.DispatchLuaCallAsync(HouseFeature.ClickItemCmd, "{\"templateid\":401}");
        Assert.Equal(n, h.Outbound.Count);
    }

    /// <summary>★ 138 设置音乐：同样不回包，但选择要落盘。</summary>
    [Fact]
    public async Task SetMusic_不回包但落盘()
    {
        using var h = await RichAsync("house-music");
        int n = h.Outbound.Count;

        await h.DispatchLuaCallAsync(HouseFeature.SetMusicCmd, "{\"musicid\":7}");

        Assert.Equal(n, h.Outbound.Count);
        var node = Assert.IsType<JsonObject>(h.Player.Extra[HouseFeature.ExtraKey]);
        Assert.Equal(7, node[HouseFeature.MusicKey]!.GetValue<int>());
    }

    /// <summary>★ 写音乐/主题时不能抹掉 <c>house</c> 里的家具清单。</summary>
    [Fact]
    public async Task SetMusic_不抹掉家具清单()
    {
        using var h = await RichAsync("house-music-keep");
        int themeId = PickTheme(CafeTable.Load(TestPaths.SettingDir));
        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, $"{{\"themeid\":{themeId}}}");
        int before = HouseFeature.ReadOrCreate(h.Player, HouseTable.Load(TestPaths.SettingDir)).Furniture.Count;

        await h.DispatchLuaCallAsync(HouseFeature.SetMusicCmd, "{\"musicid\":3}");

        int after = HouseFeature.ReadOrCreate(h.Player, HouseTable.Load(TestPaths.SettingDir)).Furniture.Count;
        Assert.Equal(before, after);
        Assert.NotEmpty(HouseFeature.ReadOwnedThemes(h.Player));   // 主题清单也还在
    }
}

/// <summary>
/// 委托任务（129 开启 / 131 完成 / 132 领奖）。
///
/// <para><b>★★ 本组最反直觉的一点：131/132 的回包字段是「大写驼峰」</b></para>
/// <c>TaskId</c> / <c>AwardItem</c> —— 取证 `CafeLogic.lua:906` 的签名注释
/// <c>---@param tbParam table { TaskId, AwardItem }</c> +
/// 消费点 `:916` <c>Data:CompleteEntrustTask(tbParam.TaskId)</c>。
/// 本项目多数命令用小写字段，照惯例写会让客户端拿到 nil。
///
/// <para><b>★ 130 / 133 论证为不可做</b></para>
/// <list type="bullet">
///   <item><b>130 AcceptEntrust</b>：`CafeLogic.AcceptEntrustTask`（`:826`）**整个函数体
///     被注释**，且注释写明「**此功能入口还未开放**」；上行发起点 `:858` 也被注释。</item>
///   <item><b>133 UpdateEntrust</b>：`LuaCall.lua:1989..1993` **两侧分支全空** ⇒ 死命令。</item>
/// </list>
/// </summary>
public class CafeEntrustTests
{
    private static JsonNode? Param(FeatureHarness h)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal("LuaCall", function);
        return Assert.IsType<JsonObject>(JsonNode.Parse(json))["tbParam"];
    }

    // ================= 129 开启委托 =================

    /// <summary>★ 回包只要一个 <c>starttime</c>（`CafeLogic.lua:816` 唯一被读的字段）。</summary>
    [Fact]
    public async Task OpenEntrust_回starttime()
    {
        using var h = await FeatureHarness.CreateAsync("entrust-open");

        await h.DispatchLuaCallAsync(HouseFeature.OpenEntrustCmd, "{}");

        var p = Assert.IsType<JsonObject>(Param(h));
        Assert.True(p["starttime"]!.GetValue<long>() > 1_700_000_000, "starttime 必须是 unix 秒");
        Assert.Null(p["nError"]);      // A 族：成功不带
    }

    /// <summary>★ 幂等：重复开启回同一个 starttime。</summary>
    [Fact]
    public async Task OpenEntrust_幂等()
    {
        using var h = await FeatureHarness.CreateAsync("entrust-open-twice");

        await h.DispatchLuaCallAsync(HouseFeature.OpenEntrustCmd, "{}");
        long t1 = Assert.IsType<JsonObject>(Param(h))["starttime"]!.GetValue<long>();
        await h.DispatchLuaCallAsync(HouseFeature.OpenEntrustCmd, "{}");
        long t2 = Assert.IsType<JsonObject>(Param(h))["starttime"]!.GetValue<long>();

        Assert.Equal(t1, t2);
    }

    // ================= 131 完成委托 =================

    /// <summary>★★ 回包字段必须是**大写** <c>TaskId</c> / <c>AwardItem</c>。</summary>
    [Fact]
    public async Task CompleteEntrust_大写字段与加分()
    {
        using var h = await FeatureHarness.CreateAsync("entrust-done");

        await h.DispatchLuaCallAsync(HouseFeature.CompleteEntrustCmd, "{\"VisitedId\":12345}");

        var p = Assert.IsType<JsonObject>(Param(h));
        Assert.NotNull(p["TaskId"]);                 // ★ 大写
        Assert.NotNull(p["AwardItem"]);
        Assert.Null(p["taskid"]);                    // 小写不该出现
        Assert.Null(p["nError"]);

        // 积分 +2（任务值 49/4）
        Assert.Equal(HouseFeature.EntrustPointPerTask,
                     h.Player.GetTask(HouseFeature.EntrustTaskGroup, HouseFeature.EntrustAwardTaskId));
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp), "任务值变更要推 pf 1026");
    }

    /// <summary>TaskId 必须来自 <c>VisitMission.txt</c>（否则客户端找不到任务配置会 error）。</summary>
    [Fact]
    public async Task CompleteEntrust_TaskId来自配置表()
    {
        using var h = await FeatureHarness.CreateAsync("entrust-taskid");
        var valid = CafeTable.Load(TestPaths.SettingDir).VisitMissions.Select(m => m.MissionId).ToHashSet();

        await h.DispatchLuaCallAsync(HouseFeature.CompleteEntrustCmd, "{\"VisitedId\":777}");

        int taskId = Assert.IsType<JsonObject>(Param(h))["TaskId"]!.GetValue<int>();
        Assert.Contains(taskId, valid);
    }

    /// <summary>多次完成要累加积分。</summary>
    [Fact]
    public async Task CompleteEntrust_积分累加()
    {
        using var h = await FeatureHarness.CreateAsync("entrust-points");

        for (int i = 0; i < 3; i++)
            await h.DispatchLuaCallAsync(HouseFeature.CompleteEntrustCmd, $"{{\"VisitedId\":{100 + i}}}");

        Assert.Equal(HouseFeature.EntrustPointPerTask * 3,
                     h.Player.GetTask(HouseFeature.EntrustTaskGroup, HouseFeature.EntrustAwardTaskId));
    }

    /// <summary>非法上行不崩。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task CompleteEntrust_非法上行不崩(string payload)
    {
        using var h = await FeatureHarness.CreateAsync($"entrust-bad{payload.Length}");
        await h.DispatchLuaCallAsync(HouseFeature.CompleteEntrustCmd, payload);
        // 没抛就算过（VisitedId=0 也能确定性挑一个任务）
    }

    // ================= 132 领奖 =================

    /// <summary>积分不够 ⇒ 不回包（A 族没有合适错误码）。</summary>
    [Fact]
    public async Task AwardEntrust_积分不够不回包()
    {
        using var h = await FeatureHarness.CreateAsync("entrust-award-poor");
        int n = h.Outbound.Count;

        await h.DispatchLuaCallAsync(HouseFeature.AwardEntrustCmd, "{}");

        Assert.Equal(n, h.Outbound.Count);
    }

    /// <summary>
    /// ★ 达到第 1 档门槛（Point=2，即完成 1 次委托）⇒ 能领，且
    /// <c>AwardItem</c> 会被整体塞进 `UI_RewardNormal`（`LuaCall.lua:1982`）。
    /// </summary>
    [Fact]
    public async Task AwardEntrust_达标可领并发奖()
    {
        using var h = await FeatureHarness.CreateAsync("entrust-award-ok");
        await h.DispatchLuaCallAsync(HouseFeature.CompleteEntrustCmd, "{\"VisitedId\":1}");
        int goldBefore = h.Player.GetMoney(2);

        await h.DispatchLuaCallAsync(HouseFeature.AwardEntrustCmd, "{}");

        var p = Assert.IsType<JsonObject>(Param(h));
        var award = Assert.IsType<JsonArray>(p["AwardItem"]);
        Assert.NotEmpty(award);
        Assert.Null(p["nError"]);
        // 第 1 档是 [15-1-1-1-10000] = 金币 10000
        Assert.True(h.Player.GetMoney(2) > goldBefore, "奖励要真进账");
    }

    /// <summary>★ 同一档不能重复领（已领掩码记在任务值 49/5）。</summary>
    [Fact]
    public async Task AwardEntrust_同档不能重复领()
    {
        using var h = await FeatureHarness.CreateAsync("entrust-award-dup");
        await h.DispatchLuaCallAsync(HouseFeature.CompleteEntrustCmd, "{\"VisitedId\":1}");
        await h.DispatchLuaCallAsync(HouseFeature.AwardEntrustCmd, "{}");
        int gold = h.Player.GetMoney(2);
        int n = h.Outbound.Count;

        await h.DispatchLuaCallAsync(HouseFeature.AwardEntrustCmd, "{}");

        Assert.Equal(n, h.Outbound.Count);          // 不回包
        Assert.Equal(gold, h.Player.GetMoney(2));   // 不重复发奖
    }

    /// <summary>★ 积分涨上去后能领到新档位（一次领所有已达门槛且未领的）。</summary>
    [Fact]
    public async Task AwardEntrust_涨分后可领新档()
    {
        using var h = await FeatureHarness.CreateAsync("entrust-award-more");
        // 完成 1 次 ⇒ 2 分 ⇒ 领第 1 档
        await h.DispatchLuaCallAsync(HouseFeature.CompleteEntrustCmd, "{\"VisitedId\":1}");
        await h.DispatchLuaCallAsync(HouseFeature.AwardEntrustCmd, "{}");

        // 再完成 1 次 ⇒ 4 分 ⇒ 应能领第 2 档
        await h.DispatchLuaCallAsync(HouseFeature.CompleteEntrustCmd, "{\"VisitedId\":2}");
        await h.DispatchLuaCallAsync(HouseFeature.AwardEntrustCmd, "{}");

        var award = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(Param(h))["AwardItem"]);
        Assert.Equal(3, award.Count);      // 第 2 档有 3 项奖励
    }

    /// <summary>一次性给足 10 分 ⇒ 五档全领（验证「领所有已达门槛」）。</summary>
    [Fact]
    public async Task AwardEntrust_满分一次领五档()
    {
        using var h = await FeatureHarness.CreateAsync("entrust-award-all");
        h.Player.SetTask(HouseFeature.EntrustTaskGroup, HouseFeature.EntrustAwardTaskId, 10);

        await h.DispatchLuaCallAsync(HouseFeature.AwardEntrustCmd, "{}");

        var award = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(Param(h))["AwardItem"]);
        var table = CafeTable.Load(TestPaths.SettingDir);
        int total = table.VisitAwards.Sum(r => r.Items.Count);
        Assert.Equal(total, award.Count);
    }
}

// ================================================================ 134 设置访问少女（第十三轮）

/// <summary>
/// 134 <c>CafeHouse_SetVisiteGirl</c>「设置访问少女」（去别人咖啡馆时派谁去）。
///
/// 断言重点：
/// <list type="number">
///   <item>★ 存任务值 <c>(49, 1)</c>（<c>CafeLogic.lua:28/29</c>）并**先推 pf 1026 再回包** ——
///         消费点 <c>UI_CafeVisitMain:OnChangeGirl</c>（<c>:131</c>）不收参数、只弹提示，
///         界面重开时读的是任务值；</item>
///   <item>★ 成功**绝不能带 <c>nError</c>**（LuaCall 数字族，真值判断）；</item>
///   <item>★ 未拥有 / 非法少女要拒绝（否则访客界面拿一个查不到的 id 去建角色）。</item>
/// </list>
/// </summary>
public class CafeVisitGirlTests
{
    private static JsonNode? Param(FeatureHarness h)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal("LuaCall", function);
        return Assert.IsType<JsonObject>(JsonNode.Parse(json))["tbParam"];
    }

    private static Task SetAsync(FeatureHarness h, int girlId)
        => h.DispatchLuaCallAsync(HouseFeature.SetVisiteGirlCmd, $"{{\"GirlId\":{girlId}}}");

    /// <summary>造一个玩家拥有的少女（复用已存在的同 id，避免 Girls 里出现两个）。</summary>
    private static uint SeedGirl(FeatureHarness h, uint girlId = 1)
    {
        if (h.Player.GetGirl(girlId) is null)
            h.Player.Girls.Add(new Girl { Girlid = girlId });
        return girlId;
    }

    [Fact]
    public async Task 设置访问少女写任务值并推1026且不带nError()
    {
        using var h = await FeatureHarness.CreateAsync("visitgirl-ok");
        uint girl = SeedGirl(h, 1);

        await SetAsync(h, (int)girl);

        Assert.Equal((int)girl,
            h.Player.GetTask(HouseFeature.EntrustTaskGroup, HouseFeature.VisitGirlTaskId));
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));

        var p = Assert.IsType<JsonObject>(Param(h));
        Assert.False(p.ContainsKey("nError"), "成功回包带了 nError，客户端会弹错误提示");
    }

    [Fact]
    public async Task 未拥有的少女被拒绝且不改任务值()
    {
        using var h = await FeatureHarness.CreateAsync("visitgirl-notown");

        await SetAsync(h, 9999);

        Assert.Equal(0,
            h.Player.GetTask(HouseFeature.EntrustTaskGroup, HouseFeature.VisitGirlTaskId));
        var p = Assert.IsType<JsonObject>(Param(h));
        Assert.True(p.ContainsKey("nError"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"GirlId\":0}")]
    public async Task 非法参数不崩且有回包(string json)
    {
        using var h = await FeatureHarness.CreateAsync("visitgirl-bad" + json.GetHashCode());

        int before = h.Outbound.Count;
        await h.DispatchLuaCallAsync(HouseFeature.SetVisiteGirlCmd, json);

        Assert.Equal(before + 1, h.Outbound.Count);
    }

    /// <summary>★ 改一次之后再改一次要覆盖（不是累加/忽略）。</summary>
    [Fact]
    public async Task 重复设置会覆盖()
    {
        using var h = await FeatureHarness.CreateAsync("visitgirl-twice");
        SeedGirl(h, 1);
        SeedGirl(h, 2);

        await SetAsync(h, 1);
        await SetAsync(h, 2);

        Assert.Equal(2,
            h.Player.GetTask(HouseFeature.EntrustTaskGroup, HouseFeature.VisitGirlTaskId));
    }
}

// ================================================================ 音游外景（第十三轮）

/// <summary>
/// GM 账号补发「音乐节外景」（templateId 1400）—— **音游玩法的唯一入口开关**。
///
/// 取证（<c>HouseFeature.MusicSceneTemplateId</c> 的注释）：
/// <c>UI_NCafe.lua:556</c> 的音游按钮显隐 =
/// <c>IsMusicGameOpen()</c>（档期，已过期）**或** <c>HasByMusicScene()</c>
/// （<c>MusicActivityLogic.lua:714..716</c>，只看是否持有家具 1400）。
/// </summary>
public class MusicSceneGrantTests
{
    [Fact]
    public async Task ExistingOutdoorIsPreservedAndMusicGrantGoesToStorage()
    {
        using var h = await GmAsync("music-scene-preserve-outdoor");
        var outdoor = new HouseFurniture(1000, 1000, 5, 22, 0, 0, 0, 0, 0);
        HouseFeature.WriteState(h.Player, new HouseState { Furniture = [outdoor], NextObjId = 1001 });
        h.RunPrepareHooks();
        h.RunPrepareHooks();
        var state = HouseFeature.ReadOrCreate(h.Player, Table);
        Assert.Equal(outdoor, state.Furniture[0]);
        var music = Assert.Single(state.Furniture, f => f.TemplateId == HouseFeature.MusicSceneTemplateId);
        Assert.Equal(0, music.RoomId);
        Assert.Equal(0u, music.ParentId);
        Assert.Equal(1000, GirlTrainFeature.CurrentOutdoor(h.Player, Table));
    }

    private static HouseTable Table => HouseTable.Load(TestPaths.SettingDir);

    /// <summary>
    /// 造一个 **GM 账号** 的 harness。
    /// ★ 默认 <c>GmAccounts = { "" }</c>（只有空账号是 GM，见 <c>ServerOptions.GmAccounts</c>）
    /// ⇒ 具名测试账号默认**不是** GM，必须显式加进白名单，否则补发钩子直接 return
    /// （第一版没加、三条用例全红）。
    /// </summary>
    private static async Task<FeatureHarness> GmAsync(string account)
    {
        var h = await FeatureHarness.CreateAsync(account);
        h.Options.GmAccounts = new[] { account };
        return h;
    }

    /// <summary>★ 表里必须有 1400，否则整个音游入口方案不成立。</summary>
    [Fact]
    public void 音乐节外景模板存在于配置表()
    {
        var t = Table;
        Assert.True(t.IsKnownTemplate(HouseFeature.MusicSceneTemplateId));
        var row = t.ItemOf(HouseFeature.MusicSceneTemplateId);
        Assert.NotNull(row);
        // ★ Type 72 = 外景、Parent 22 = 外景容器（NCafeItems.txt 实测值）
        Assert.Equal(72, row!.Type);
        Assert.Equal(22u, row.Parent);
    }

    /// <summary>★ GM 账号登录后应持有这件家具（音游入口打开）。</summary>
    [Fact]
    public async Task GM账号登录后补发音乐节外景()
    {
        using var h = await GmAsync("music-scene-gm");
        h.RunPrepareHooks();

        var furniture = ReadFurnitureTemplateIds(h.Player);
        Assert.Contains(HouseFeature.MusicSceneTemplateId, furniture);
    }

    /// <summary>
    /// ★★ 幂等：连续两次登录不能加出两件
    /// （<c>OwnMax=99</c> 允许多件，但重复补发说明判据写错了）。
    /// </summary>
    [Fact]
    public async Task 重复登录不会重复补发()
    {
        using var h = await GmAsync("music-scene-idem");
        h.RunPrepareHooks();
        h.RunPrepareHooks();

        int count = ReadFurnitureTemplateIds(h.Player)
            .Count(id => id == HouseFeature.MusicSceneTemplateId);
        Assert.Equal(1, count);
    }

    /// <summary>
    /// ★★★ <c>parentId</c> 必须是表里的 22，**绝不能是 0** ——
    /// 第十二轮的真 bug：<c>parentid=0</c> 的家具客户端挂不上场景树 ⇒
    /// <c>UpdateNavMeshObstacle</c> 抛 NRE ⇒ 回调链断 ⇒ 咖啡馆卡 40% 进不去。
    /// </summary>
    [Fact]
    public async Task 补发的外景parentId不为0()
    {
        using var h = await GmAsync("music-scene-parent");
        h.RunPrepareHooks();

        var node = h.Player.Extra["house"] as JsonObject;
        var arr = node?["furniturelist"] as JsonArray;
        Assert.NotNull(arr);

        var one = arr!.OfType<JsonObject>()
            .First(o => o["templateid"]!.GetValue<int>() == HouseFeature.MusicSceneTemplateId);
        Assert.Equal(22u, one["parentid"]!.GetValue<uint>());
        // objId 必须是玩家家具区间（≥1000），不能撞客户端自建的 1..85
        Assert.True(one["objid"]!.GetValue<uint>() >= HouseTable.MinPlayerObjId);
    }

    private static List<int> ReadFurnitureTemplateIds(Player player)
    {
        var result = new List<int>();
        if (player.Extra.TryGetValue("house", out var v) && v is JsonObject o
            && o["furniturelist"] is JsonArray arr)
        {
            foreach (var it in arr.OfType<JsonObject>())
            {
                if (it["templateid"] is JsonNode t)
                    result.Add(t.GetValue<int>());
            }
        }
        return result;
    }
}
