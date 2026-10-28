using System.Net;
using System.Net.Sockets;
using SssjServer.Core;
using SssjServer.Game;
using SssjServer.Protocol;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 端到端（loopback socket）：真实 ClientSession + 已 wire 的 PfRouter/LuaCallRouter，
/// 走完 1102(明文 Verify) → client_key → 3DES → 1001 Login → 1002 LoginRsp / 1005 PlayerNtf。
/// 用真实网络 + 真实 3DES，验证 GameModule.Install 的 pf 映射与加密时序。
/// </summary>
public class LoginFlowIntegrationTests : IAsyncLifetime
{
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _clientStream;
    private ClientSession? _serverSession;
    private string _dataDir = "";
    private CancellationTokenSource _cts = new();

    public async Task InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sssj-int-tests", Guid.NewGuid().ToString("N"));

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        var router = new PfRouter();
        var luaCalls = new LuaCallRouter();
        var module = new GameModule(new ServerOptions { DataDirectory = _dataDir, Aid = 1, Sid = 7 });
        module.Install(router, luaCalls);

        _client = new TcpClient();
        var connect = _client.ConnectAsync(IPAddress.Loopback, port);
        var accepted = await _listener.AcceptTcpClientAsync();
        await connect;
        _clientStream = _client.GetStream();

        _serverSession = new ClientSession(1, accepted);
        _cts = new CancellationTokenSource();
        // 后台驱动读循环 → dispatch
        _ = Task.Run(() => _serverSession.RunAsync(router.DispatchAsync, _cts.Token));
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        try { _serverSession?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _listener?.Stop();
        await Task.Delay(50);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task FullLoginChain_Verify_Then_EncryptedLogin()
    {
        var stream = _clientStream!;

        // ---- 1. Verify（明文） ----
        const uint verifySerial = 100;
        var verifyReq = new VerifyReq { Platform = "selftest", Account = "int_acc", Unityplat = 11 };
        await stream.WriteAsync(PacketCodec.Encode(GamePf.Verify, verifySerial, verifyReq.ToByteArray()));

        var verifyPkt = await ReadPacketAsync(stream);
        Assert.Equal(GamePf.Verify, verifyPkt.Pf);
        Assert.Equal(verifySerial, verifyPkt.Serial);          // 回包按请求 serial
        var verifyRsp = ProtoMessage.Parse<VerifyRsp>(verifyPkt.Payload);  // 明文阶段
        Assert.Equal(0u, verifyRsp.Retid);
        Assert.True(verifyRsp.ClientKey.Length >= 32, "client_key 必须 ≥ 32 字节");
        Assert.Equal("int_acc", verifyRsp.Account);
        Assert.Equal(1, verifyRsp.Aid);
        Assert.Equal(7, verifyRsp.Sid);

        // 之后所有包均为 3DES（客户端用 rsp.client_key 初始化）
        var crypto = new SessionCrypto(verifyRsp.ClientKey);

        // ---- 2. Login（3DES）----
        const uint loginSerial = 101;
        var loginReq = new LoginReq { Account = "int_acc", Channel = "selftest", Devname = "test-dev" };
        var loginBody = crypto.Encrypt(loginReq.ToByteArray());
        await stream.WriteAsync(PacketCodec.Encode(GamePf.Login, loginSerial, loginBody));

        // 2a. PlayerNtf 先到：pf=1005, serial=0
        //     顺序是硬要求 —— pf=1002 的 PlayerLogineRsp → OnEnterGame() 读 Game.Player.me，
        //     而 me 由 pf=1005 的 PlayerLogined 建立；反序真机会抛 NullReferenceException。
        //     注意 3DES 的 CBC 链跨包连续，所以必须按到达顺序依次解密，不能跳过任何一个包。
        var ntfPkt = await ReadPacketAsync(stream);
        Assert.Equal(GamePf.PlayerNtf, ntfPkt.Pf);
        Assert.Equal(0u, ntfPkt.Serial);
        var ntf = ProtoMessage.Parse<PlayerNtf>(crypto.Decrypt(ntfPkt.Payload));
        Assert.Equal(World.DeriveRoleId("int_acc"), ntf.Roleid);
        Assert.Equal("int_acc", ntf.Name);
        Assert.True(ntf.HasServertime && ntf.HasStarttime && ntf.HasRegisterTime && ntf.HasServerzone);
        Assert.Equal(7, ntf.Serverzone);
        // 新建号赠送起始卡：girllist 与 formationlist 应非空，且编队主卡可落在卡片上
        Assert.True(ntf.HasGirllist && ntf.Girllist!.Girls.Count > 0, "新号应带起始角色卡 girllist");
        Assert.True(ntf.Formationlist.Count > 0, "新号应带默认编队 formationlist");
        var firstFormation = ntf.Formationlist[0];
        Assert.Equal(1u, firstFormation.Id);
        Assert.True(firstFormation.Fightcard.Count > 0);
        Assert.True(firstFormation.Fightcard[0].Maincardguid > 0);

        // 2a-2. ★ 教程闸门小包：pf=1026，serial=0，紧跟 1005 且在 1104 之前。
        //       客户端在 1005 之后 ~300ms（UpdateEndNode.OnEnter → PlayerLogic.OnUpdateEnd，
        //       PlayerLogic.lua:436/516）读 GetTaskValue(1,15)/(1,14) 决定「进主城还是重播新手剧情」，
        //       而全量背包 pf 1104 有 65KB，会把排在它后面的 1026 推迟 ~230ms 压过判定线
        //       （真机取证见 work/tutorial-replay-fix.md）。所以先单独推这一条几十字节的小包。
        var gatePkt = await ReadPacketAsync(stream);
        Assert.Equal(GamePf.TaskValueSyncRsp, gatePkt.Pf);
        Assert.Equal(0u, gatePkt.Serial);
        var gate = ProtoMessage.Parse<TaskValueSyncRsp>(crypto.Decrypt(gatePkt.Payload));
        Assert.All(gate.Arr, t => Assert.Contains(t.Id, GameModule.TutorialGateTaskIds));
        Assert.Equal(6, gate.Arr.Single(t => t.Id == 65551).Value);   // (1<<16)|15，SkipTutorial 种子

        // 2b. LoginRsp 后到：pf=1002 + 请求 serial
        var loginPkt = await ReadPacketAsync(stream);
        Assert.Equal(GamePf.LoginRsp, loginPkt.Pf);
        Assert.Equal(loginSerial, loginPkt.Serial);
        var loginRsp = ProtoMessage.Parse<LoginRsp>(crypto.Decrypt(loginPkt.Payload));
        Assert.Equal(0u, loginRsp.Retid);
        Assert.True(loginRsp.Optime > 0);

        // 2c. 全量任务值：pf=1026 TaskValueSyncRsp（serial=0）。
        //     SkipTutorial 建号时含种子值 (1,15)→6，使客户端判定新手关完成。
        //     ★ 排在 pf=1104 之前（原先在它之后，见 2d 的说明）。
        var taskSyncPkt = await ReadPacketAsync(stream);
        Assert.Equal(GamePf.TaskValueSyncRsp, taskSyncPkt.Pf);
        Assert.Equal(0u, taskSyncPkt.Serial);
        var taskSync = ProtoMessage.Parse<TaskValueSyncRsp>(crypto.Decrypt(taskSyncPkt.Payload));
        var seed = taskSync.Arr.Single(t => t.Id == 65551);   // (group=1 <<16 | 15)
        Assert.Equal(6, seed.Value);

        // 2d. 全量背包推送：pf=1104 ItemNtf（serial=0），**最后**一条 —— 它是全链路最大的包，
        //     客户端 UNet.Connection.Update()（RVA 0x1B4D340）每帧只做一次
        //     Socket.Receive(≤0x10000) 再调一次 Net.Dispatch()，排它后面的包会被整体推迟。
        //     itemnum == items 数量；每张卡 guid 单调。
        var itemPkt = await ReadPacketAsync(stream);
        Assert.Equal(GamePf.ItemNtf, itemPkt.Pf);
        Assert.Equal(0u, itemPkt.Serial);
        var itemNtf = ProtoMessage.Parse<ItemNtf>(crypto.Decrypt(itemPkt.Payload));
        Assert.Equal(itemNtf.Items.Count, itemNtf.Itemnum);
        Assert.True(itemNtf.Items.Count > 0, "新建号应被赠送起始角色卡");
        var guids = itemNtf.Items.Select(i => i.Guid).ToList();
        Assert.Equal(guids.Count, guids.Distinct().Count());
        for (int i = 1; i < guids.Count; i++)
            Assert.True(guids[i] > guids[i - 1]);

        // ---- 3. C2SCall cmd=10002 Club_ReqClubInfo → 空对象应答（Clublogic §7）----
        const uint callSerial = 102;
        var callReq = new C2SCallReq
        {
            SFunction = "LuaCall",
            SJsonParam = "{\"sCmd\":10002,\"tbParam\":{}}",
        };
        var callBody = crypto.Encrypt(callReq.ToByteArray());
        await stream.WriteAsync(PacketCodec.Encode(GamePf.C2SCall, callSerial, callBody));

        // 3a. NtfS2CCall：pf=1024，sJsonParam 含 sCmd=10002 + 空 tbParam
        //     ★ 用 ReadPacketOfPfAsync 而不是 ReadPacketAsync：功能域的登录钩子
        //     （ctx.OnPlayerLogin，GameModule.HandleLogin 第 ⑥ 步）会在五条基础包之后继续推
        //     自己那部分（例如聊天域的 pf 1012 系统欢迎语），数量会随新功能域增加。
        //     断言「紧接着的下一个包」本身就是脆的，这里改成「跳到目标 pf」，
        //     跳过的包也照样解密以推进 CBC 链（AGENTS.md §2.3）。
        NtfS2CCall ntfCall;
        do
        {
            var ntfCallPkt = await ReadPacketOfPfAsync(stream, crypto, GamePf.NtfS2CCall);
            ntfCall = ProtoMessage.Parse<NtfS2CCall>(crypto.Decrypt(ntfCallPkt.Payload));
            using var candidate = System.Text.Json.JsonDocument.Parse(ntfCall.SJsonParam);
            var candidateCmd = candidate.RootElement.GetProperty("sCmd").GetInt32();
            if (candidateCmd == 10002) break;
            Assert.Equal(999999, candidateCmd);
        } while (true);
        Assert.Equal("LuaCall", ntfCall.SFunction);
        using (var doc = System.Text.Json.JsonDocument.Parse(ntfCall.SJsonParam))
        {
            Assert.Equal(10002, doc.RootElement.GetProperty("sCmd").GetInt32());
            var tb = doc.RootElement.GetProperty("tbParam");
            Assert.Equal(System.Text.Json.JsonValueKind.Object, tb.ValueKind);
            Assert.Empty(tb.EnumerateObject()); // 空对象 = “没有社团”
        }

        // 3b. ack：pf=1023，SErrMsg 空
        var callAckPkt = await ReadPacketAsync(stream);
        Assert.Equal(GamePf.C2SCallRsp, callAckPkt.Pf);
        Assert.Equal(callSerial, callAckPkt.Serial);
        var callRsp = ProtoMessage.Parse<C2SCallRsp>(crypto.Decrypt(callAckPkt.Payload));
        Assert.Equal("", callRsp.SErrMsg);
    }

    /// <summary>
    /// 一直读到指定 pf 为止；沿途跳过的包**照样解密**（3DES 的 CBC 链跨包连续，漏一个后面全乱，
    /// AGENTS.md §2.3）。用于容纳「功能域登录钩子会推多少条包」这种会随时间增长的量。
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
