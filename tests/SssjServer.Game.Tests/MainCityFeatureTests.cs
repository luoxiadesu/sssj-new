using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Handlers;
using SssjServer.Protocol;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 「主城面板第一批」5 个 LuaCall 的回包契约单测（<see cref="MainCityFeature"/>）。
///
/// 断言全部对着客户端 else 分支的取证来（文件:行号写在各断言注释里），核心是三类坑：
///  * 字段名/大小写错 → 客户端读到 nil；
///  * 该给的默认值没给 → 客户端拿 nil 去比较/索引直接抛错（cmd 94 的 VisitingCardID、cmd 202 的 Result）；
///  * 用 0 冒充 false → Lua 里 0 是 truthy，语义整个反过来（cmd 230 的 PromiseIsOpen、cmd 94 的 nError）。
///
/// ★ cmd 80 <c>Friend_ReqList</c> 的用例已随实现迁去 <c>FriendFeatureTests</c>
/// （好友域整体在 <see cref="FriendFeature"/>，80..90 + 101）。本文件只留下一条守卫：
/// <see cref="MainCityFeatureTests.Feature_IsDiscoveredByReflection_WithPublicParameterlessCtor"/>
/// 断言 <c>HandledCmds</c> 里**没有** 80 —— 两个 feature 同时 <c>MapLuaCall(80, …)</c> 会让
/// 后装配的静默覆盖前者（<c>LuaCallRouter.cs:78..81</c> 的 <c>_map[cmd] = handler</c>）。
/// </summary>
public class MainCityFeatureTests
{
    // ---------------------------------------------------------------- 94 Player_GetVisitingCardData

    /// <summary>
    /// 三个字段固定齐全、大小写与 LuaCall.lua:1189 注释一致；
    /// ★ 绝不能带 nError —— UI_User.lua:1296 <c>if tbParam.nError then 弹错误框</c>，
    /// 而 Lua 里 0 是 truthy，回 nError=0 会在打开个人信息界面时弹 ui.Error.0。
    /// </summary>
    [Fact]
    public void VisitingCardPayload_HasExactlyThreeKeys_AndNoNError()
    {
        var payload = MainCityFeature.BuildVisitingCardPayload(new MainCityFeature.CardState(0, 0, 0));

        Assert.Equal("""{"VisitingCardID":0,"PlayerListSkinID":0,"ChatBubbleID":0}""", payload.ToJsonString());
        Assert.Null(payload["nError"]);          // UI_User.lua:1296
        Assert.Equal(3, payload.Count);
    }

    /// <summary>
    /// VisitingCardID 必须存在：UI_User.lua:1310 <c>self.skinId = tbParam.VisitingCardID or tbParam[1]</c>
    /// 之后 UI_User.lua:947 <c>if self.skinId > 0</c> —— 缺字段时 nil &gt; 0 直接抛
    /// 「attempt to compare nil with number」。
    /// </summary>
    [Fact]
    public void VisitingCardPayload_KeepsIdsEvenWhenZero()
    {
        var payload = MainCityFeature.BuildVisitingCardPayload(new MainCityFeature.CardState(0, 0, 0));
        Assert.NotNull(payload["VisitingCardID"]);
        Assert.NotNull(payload["PlayerListSkinID"]);   // UI_User.lua:1306 与 CardCommon.lua:1057
        Assert.NotNull(payload["ChatBubbleID"]);       // UI_User.lua:1306 与 CardCommon.lua:1063
    }

    /// <summary>没有存档时一律 0（= 没穿装扮），客户端三条分支都在 &gt; 0 时才去查配置表。</summary>
    [Fact]
    public void ReadCardState_NoExtra_IsAllZero()
    {
        var player = new Player("acc", 1);
        Assert.Equal(new MainCityFeature.CardState(0, 0, 0), MainCityFeature.ReadCardState(player));
        Assert.Equal(new MainCityFeature.CardState(0, 0, 0), MainCityFeature.ReadCardState(null));
    }

    /// <summary>Extra["maincity"] 被写成别的形状（如数组）时不抛异常，退回全 0。</summary>
    [Fact]
    public void ReadCardState_BrokenExtra_FallsBackToZero()
    {
        var player = new Player("acc", 1);
        player.Extra[MainCityFeature.ExtraKey] = new JsonArray();
        Assert.Equal(new MainCityFeature.CardState(0, 0, 0), MainCityFeature.ReadCardState(player));

        player.Extra[MainCityFeature.ExtraKey] = new JsonObject { ["card"] = "oops" };
        Assert.Equal(new MainCityFeature.CardState(0, 0, 0), MainCityFeature.ReadCardState(player));
    }

    /// <summary>
    /// 名片装扮存 <c>Player.Extra["maincity"]["card"]</c>，随存档 round-trip
    /// （PlayerStore.PlayerDto.Extra，见 PlayerStore.cs:150/173/210）。
    /// 这一条把「持久化位置」钉住，后续 LuaCall 91/92/93 落地时不必再挑 key。
    /// </summary>
    [Fact]
    public void CardState_RoundTripsThroughPlayerStore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sssj-maincity-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PlayerStore(new ServerOptions { DataDirectory = dir, Sid = 3 });
            var player = new Player("card_acc", 42);
            MainCityFeature.WriteCardState(player, new MainCityFeature.CardState(5, 3, 7));
            store.Save(player);

            var loaded = store.Load("card_acc");
            Assert.NotNull(loaded);
            var state = MainCityFeature.ReadCardState(loaded);
            Assert.Equal(new MainCityFeature.CardState(5, 3, 7), state);

            // 回包直接反映存档值（LuaCall.lua:1189 的示例形状 {ChatBubbleID:5, VisitingCardID:1, PlayerListSkinID:3}）
            var payload = MainCityFeature.BuildVisitingCardPayload(state);
            Assert.Equal(5, payload["VisitingCardID"]!.GetValue<int>());
            Assert.Equal(3, payload["PlayerListSkinID"]!.GetValue<int>());
            Assert.Equal(7, payload["ChatBubbleID"]!.GetValue<int>());

            // 覆盖写不重复建节点
            MainCityFeature.WriteCardState(loaded!, new MainCityFeature.CardState(1, 2, 3));
            Assert.Equal(new MainCityFeature.CardState(1, 2, 3), MainCityFeature.ReadCardState(loaded));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    // ---------------------------------------------------------------- 170 RequesRandomEvent

    /// <summary>
    /// 空对象 = RandomEvent.lua:72-74 的「该次未戳中随机事件」：
    /// <c>self.RandomEvent_Info = tbParam.random_event_ID</c> 得到 nil 后什么都不做。
    /// </summary>
    [Fact]
    public void RandomEventPayload_IsEmptyObject_MeaningNoEvent()
    {
        var payload = MainCityFeature.BuildRandomEventPayload();
        Assert.Equal("{}", payload.ToJsonString());
        Assert.Null(payload["random_event_ID"]);   // RandomEvent.lua:72
    }

    // ---------------------------------------------------------------- 202 GirlTest_OpenQi

    /// <summary>
    /// 字段名就是 Result（GirlTestLogic.lua:55）；0 = 未开放（GirlTestLogic.lua:39）。
    /// 不能省略：GirlTestLogic.lua:125 <c>tb_GirlTest_Team[self._OpenID][level]</c> 会对 nil 再索引。
    /// </summary>
    [Fact]
    public void GirlTestOpenQiPayload_IsResultZero()
    {
        var payload = MainCityFeature.BuildGirlTestOpenQiPayload(MainCityFeature.GirlTestClosedQi);
        Assert.Equal("""{"Result":0}""", payload.ToJsonString());
        Assert.Equal(0, payload["Result"]!.GetValue<int>());
    }

    // ---------------------------------------------------------------- 230 Pldge_ReqIsOpen

    /// <summary>
    /// ★★★ <c>PromiseIsOpen</c> 必须是 <b>JSON 布尔字面量</b>（不能是数字）——
    /// Lua 里 <c>0</c> 是 truthy，用数字会让语义反转
    /// （<c>GirlPromise.lua:532</c> → <c>:26</c> <c>if not GirlPromise.PromiseOpen then</c>）。
    ///
    /// ★ 第十五轮起该开关是 <b>true</b>（誓约已实现，见 <see cref="PledgeFeature"/>）。
    /// 旧断言写死 <c>false</c>，理由是「本服没实现誓约地图」——**该理由已被实测证伪**
    /// （真机 <c>Map.Find(11001)</c> 有效、<c>mappromise_prefab.u</c> 在设备与资源站且已登记清单）。
    /// 本用例现在只锁「**类型必须是布尔**」这条不随开关值变化的契约。
    /// </summary>
    [Fact]
    public void PldgeIsOpenPayload_IsJsonBool_NotNumber()
    {
        var payload = MainCityFeature.BuildPldgeIsOpenPayload(MainCityFeature.PromiseOpen);

        using var doc = JsonDocument.Parse(payload.ToJsonString());
        var kind = doc.RootElement.GetProperty("PromiseIsOpen").ValueKind;
        Assert.True(kind is JsonValueKind.True or JsonValueKind.False,
            $"PromiseIsOpen 必须是 JSON 布尔，实际是 {kind}（Lua 里 0 是 truthy，数字会让语义反转）");
        Assert.Equal(MainCityFeature.PromiseOpen, payload["PromiseIsOpen"]!.GetValue<bool>());

        // 两种取值都要能正确序列化成布尔字面量
        Assert.Equal("""{"PromiseIsOpen":true}""",
            MainCityFeature.BuildPldgeIsOpenPayload(true).ToJsonString());
        Assert.Equal("""{"PromiseIsOpen":false}""",
            MainCityFeature.BuildPldgeIsOpenPayload(false).ToJsonString());
    }

    // ---------------------------------------------------------------- 231 Pldge_ReqGirls


    // ---------------------------------------------------------------- 参数解析 / 装配

    /// <summary>
    /// Lua 空表被 <c>Json.Encode</c> 编成 <c>[]</c>（实测 work/server.log:27 的 <c>"tbParam":[]</c>），
    /// 所以 tbParam 可能是 JsonArray；读字段必须容忍，不能抛。
    /// </summary>
    [Fact]
    public void ReadInt_And_ReadString_TolerateLuaEmptyTableAsArray()
    {
        var arrayParam = JsonNode.Parse("[]");
        Assert.Equal(0, MainCityFeature.ReadInt(arrayParam, "reqfriendtype", 0));
        Assert.Equal("", MainCityFeature.ReadString(arrayParam, "type", ""));
        Assert.Equal(0, MainCityFeature.ReadInt(null, "reqfriendtype", 0));

        // 真实上行：{"reqfriendtype":3} / {"type":"ShangXian"}（work/server.log:36/42）
        Assert.Equal(3, MainCityFeature.ReadInt(JsonNode.Parse("""{"reqfriendtype":3}"""), "reqfriendtype", 0));
        Assert.Equal("ShangXian", MainCityFeature.ReadString(JsonNode.Parse("""{"type":"ShangXian"}"""), "type", ""));

        // Lua 侧偶尔给字符串/浮点，兜一层
        Assert.Equal(6, MainCityFeature.ReadInt(JsonNode.Parse("""{"reqfriendtype":"6"}"""), "reqfriendtype", 0));
        Assert.Equal(1, MainCityFeature.ReadInt(JsonNode.Parse("""{"reqfriendtype":1.0}"""), "reqfriendtype", 0));
    }

    /// <summary>
    /// GameModule 靠 <see cref="GameFeatureLoader.Discover"/> 反射装配（GameFeature.cs:87），
    /// 要求公开无参构造函数；这条守住「新增文件即自动生效」的契约。
    ///
    /// ★ 同时守住「cmd 80 已迁往 <see cref="FriendFeature"/>」：<c>HandledCmds</c> 里绝不能再有 80，
    /// 否则与 <see cref="FriendFeature"/> 撞车、后装配的静默覆盖前者（<c>LuaCallRouter.cs:78..81</c>）。
    /// </summary>
    [Fact]
    public void Feature_IsDiscoveredByReflection_WithPublicParameterlessCtor()
    {
        var features = GameFeatureLoader.Discover();
        var mine = features.OfType<MainCityFeature>().SingleOrDefault();
        Assert.NotNull(mine);
        Assert.False(string.IsNullOrWhiteSpace(mine!.Name));
        // ★ 231 第十五轮迁去 PledgeFeature（MapLuaCall 覆盖式，两处注册会互相吃掉）
        Assert.Equal(new[] { 94, 170, 202, 230 }, MainCityFeature.HandledCmds);
        Assert.DoesNotContain(PledgeFeature.CmdReqGirls, MainCityFeature.HandledCmds);
        Assert.DoesNotContain(FriendFeature.CmdReqList, MainCityFeature.HandledCmds);
        // 类名/描述也要跟着更新（它不再负责好友列表）
        Assert.DoesNotContain("好友", mine.Name);
    }
}

/// <summary>
/// 端到端（loopback socket + 真 ClientSession + 真 3DES）：登录后按实测顺序发这 5 条 pf=1022，
/// 校验每条都收到 pf=1024 NtfS2CCall 回包（sCmd 对得上、tbParam 就是纯函数算出的那份），
/// 而不是只在服务端日志里打 unhandled。
/// 写法照 <c>LoginFlowIntegrationTests</c>（CBC 链跨包连续，必须按到达顺序逐包解密）。
/// </summary>
public class MainCityFeatureIntegrationTests : IAsyncLifetime
{
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _clientStream;
    private ClientSession? _serverSession;
    private string _dataDir = "";
    private CancellationTokenSource _cts = new();

    public async Task InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sssj-maincity-int", Guid.NewGuid().ToString("N"));

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        var router = new PfRouter();
        var luaCalls = new LuaCallRouter();
        var module = new GameModule(new ServerOptions { DataDirectory = _dataDir, Aid = 1, Sid = 7 });
        module.Install(router, luaCalls);   // 内含 GameFeatureLoader.Discover() → MainCityFeature

        _client = new TcpClient();
        var connect = _client.ConnectAsync(IPAddress.Loopback, port);
        var accepted = await _listener.AcceptTcpClientAsync();
        await connect;
        _clientStream = _client.GetStream();

        _serverSession = new ClientSession(1, accepted);
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => _serverSession.RunAsync(router.DispatchAsync, _cts.Token));
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        try { _serverSession?.Dispose(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        _listener?.Stop();
        await Task.Delay(50);
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task MainCityFirstBatch_EveryCommand_GetsLuaCallReply()
    {
        var stream = _clientStream!;

        // ---- 登录（明文 Verify → client_key → 3DES → LoginReq）----
        var verifyReq = new VerifyReq { Platform = "selftest", Account = "maincity_acc", Unityplat = 11 };
        await stream.WriteAsync(PacketCodec.Encode(GamePf.Verify, 100, verifyReq.ToByteArray()));
        var verifyPkt = await ReadPacketAsync(stream);
        var verifyRsp = ProtoMessage.Parse<VerifyRsp>(verifyPkt.Payload);
        var crypto = new SessionCrypto(verifyRsp.ClientKey);

        var loginReq = new LoginReq { Account = "maincity_acc", Channel = "selftest", Devname = "test-dev" };
        await stream.WriteAsync(PacketCodec.Encode(GamePf.Login, 101, crypto.Encrypt(loginReq.ToByteArray())));

        // 登录后服务端固定推 5 个包：
        //   1005 PlayerNtf → 1026 教程闸门小包 → 1002 LoginRsp → 1026 全量任务值 → 1104 ItemNtf。
        // （1026 提到 1104 之前 + 额外一条闸门小包，是「重启就重播新手教程」的修复，
        //   取证见 GameModule.BuildTutorialGateSyncRsp 与 work/tutorial-replay-fix.md）
        // CBC 链跨包连续，必须一个不漏地按序解密（AGENTS.md §2.3）。
        var ntfPkt = await ReadPacketAsync(stream);
        ProtoMessage.Parse<PlayerNtf>(crypto.Decrypt(ntfPkt.Payload));   // 只为推进解密链
        Assert.Equal(GamePf.PlayerNtf, ntfPkt.Pf);
        foreach (var pf in new[]
                 {
                     GamePf.TaskValueSyncRsp, GamePf.LoginRsp,
                     GamePf.TaskValueSyncRsp, GamePf.ItemNtf,
                 })
        {
            var pkt = await ReadPacketAsync(stream);
            Assert.Equal(pf, pkt.Pf);
            crypto.Decrypt(pkt.Payload);        // 只为推进解密链
        }

        // ---- 5 条主城请求：tbParam 全部照抄实测（work/server.log:27..57）----
        // ★ cmd 80 Friend_ReqList 已迁去 FriendFeature（回包不再是 5 个空数组，
        //   带确定性机器人好友），它的端到端用例在 FriendFeatureTests 里。
        var cases = new (int Cmd, string TbParam, string ExpectedTbParam)[]
        {
            (MainCityFeature.GetVisitingCardDataCmd, "[]",
                MainCityFeature.BuildVisitingCardPayload(new MainCityFeature.CardState(0, 0, 0)).ToJsonString()),
            (MainCityFeature.PldgeReqIsOpenCmd, "[]",
                MainCityFeature.BuildPldgeIsOpenPayload(MainCityFeature.PromiseOpen).ToJsonString()),
            (MainCityFeature.RequesRandomEventCmd, """{"type":"ShangXian"}""",
                MainCityFeature.BuildRandomEventPayload().ToJsonString()),
            (MainCityFeature.GirlTestOpenQiCmd, "[]",
                MainCityFeature.BuildGirlTestOpenQiPayload(MainCityFeature.GirlTestClosedQi).ToJsonString()),
            // ★ cmd 231 Pldge_ReqGirls 第十五轮已迁去 PledgeFeature（接真实存档，不再写死空表），
            //   端到端用例见 PledgeFeatureTests。
        };

        uint serial = 200;
        foreach (var (cmd, tbParam, expected) in cases)
        {
            serial++;
            var req = new C2SCallReq
            {
                SFunction = "LuaCall",
                SJsonParam = $"{{\"sCmd\":{cmd},\"tbParam\":{tbParam}}}",
            };
            await stream.WriteAsync(PacketCodec.Encode(GamePf.C2SCall, serial, crypto.Encrypt(req.ToByteArray())));

            // ① pf=1024 NtfS2CCall（sFunction="LuaCall"，sJsonParam={"sCmd":cmd,"tbParam":…}）
            //    ★ 用 ReadPacketOfPfAsync：功能域的登录钩子（GameModule.HandleLogin 第 ⑥ 步）
            //    会在基础包之后继续推自己那部分（例如聊天域的 pf 1012 系统欢迎语），
            //    数量随新功能域增长；沿途跳过的包照样解密以推进 CBC 链（AGENTS.md §2.3）。
            NtfS2CCall ntfCall;
            do
            {
                var ntfCallPkt = await ReadPacketOfPfAsync(stream, crypto, GamePf.NtfS2CCall);
                ntfCall = ProtoMessage.Parse<NtfS2CCall>(crypto.Decrypt(ntfCallPkt.Payload));
            } while (JsonNode.Parse(ntfCall.SJsonParam)!["sCmd"]!.GetValue<int>() == 999999);
            Assert.Equal("LuaCall", ntfCall.SFunction);

            var root = JsonNode.Parse(ntfCall.SJsonParam)!;
            Assert.Equal(cmd, root["sCmd"]!.GetValue<int>());
            Assert.Equal(expected, root["tbParam"]!.ToJsonString());

            // ② pf=1023 ack（SErrMsg 空 = 成功）
            var ackPkt = await ReadPacketAsync(stream);
            Assert.Equal(GamePf.C2SCallRsp, ackPkt.Pf);
            Assert.Equal(serial, ackPkt.Serial);
            Assert.Equal("", ProtoMessage.Parse<C2SCallRsp>(crypto.Decrypt(ackPkt.Payload)).SErrMsg);
        }
    }

    /// <summary>
    /// 一直读到指定 pf 为止；沿途跳过的包**照样解密**（3DES 的 CBC 链跨包连续，AGENTS.md §2.3）。
    /// </summary>
    private static async Task<Packet> ReadPacketOfPfAsync(NetworkStream s, SessionCrypto crypto, int pf)
    {
        var skipped = new List<int>();
        for (int i = 0; i < 32; i++)
        {
            var pkt = await ReadPacketAsync(s);
            if (pkt.Pf == pf)
                return pkt;
            if (pkt.Payload.Length > 0)
                crypto.Decrypt(pkt.Payload);   // 只为推进解密链
            skipped.Add(pkt.Pf);
        }
        Assert.Fail($"读了 32 个包也没等到 pf {pf}，沿途是：[{string.Join(",", skipped)}]");
        throw new InvalidOperationException("unreachable");
    }

    private static async Task<Packet> ReadPacketAsync(NetworkStream s)
    {
        var hdr = new byte[16];
        await ReadExactAsync(s, hdr, 0, hdr.Length);
        int total = BitConverter.ToInt32(hdr, 4);
        Assert.True(total >= 16, "非法封包长度");
        var full = new byte[total];
        Array.Copy(hdr, full, 16);
        await ReadExactAsync(s, full, 16, total - 16);

        var ok = PacketCodec.TryDecode(full, out var pkt, out _);
        Assert.True(ok, "无法解码回包");
        return pkt!;
    }

    private static async Task ReadExactAsync(NetworkStream s, byte[] buf, int offset, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buf, offset + read, count - read);
            Assert.True(n > 0, "连接意外关闭");
            read += n;
        }
    }
}
