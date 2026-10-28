using SssjServer.Core;
using SssjServer.Game;
using SssjServer.Protocol;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 任务值系统测试：id 组合/分解、1027→1028 往返、1026 登录推送、SkipTutorial 种子、
/// PlayerStore 的任务字典 JSON 往返。
/// </summary>
public class TaskValueTests : IDisposable
{
    private readonly string _dataDir;
    private readonly GameModule _module;
    private readonly FakeSession _session = new() { Id = 7 };

    public TaskValueTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sssj-task-tests", Guid.NewGuid().ToString("N"));
        var options = new ServerOptions
        {
            DataDirectory = _dataDir,
            Aid = 1,
            Sid = 7,
            SkipTutorial = true,   // 显式开启，覆盖默认
        };
        _module = new GameModule(options);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    // ---- 1. id 组合/分解 ----
    [Fact]
    public void ComposeTaskId_ProducesVerifiedCaptureValue()
    {
        // 真实抓包：pf=1027 payload 0A06088F80041001 → Task{id=65551, value=1}
        // 65551 == (1<<16)|15 —— (group=1, taskId=15) 必须组合成 65551。
        uint composed = Player.ComposeTaskId(1, 15);
        Assert.Equal(65551u, composed);

        // 分解还原
        Assert.Equal(1, Player.TaskGroupOf(composed));
        Assert.Equal(15, Player.TaskIdOf(composed));
    }

    [Fact]
    public void SetTask_GetTask_RoundTrip_ByGroupAndId()
    {
        var p = new Player("acc_x", 1);
        Assert.Equal(0, p.GetTask(1, 15));

        p.SetTask(1, 15, 6);
        Assert.Equal(6, p.GetTask(1, 15));
        Assert.Equal(6, p.Tasks[Player.ComposeTaskId(1, 15)]);
        Assert.Equal(0, p.GetTask(1, 14));   // 不同任务互不影响
    }

    // ---- 2. 1027 → 1028 往返 + 持久化 ----
    [Fact]
    public async Task TaskValueChange_RepliesOnPf1028_EchoesStoredValues_AndPersists()
    {
        // 先登录，让 session.Tag 挂上 Player（与真实链路一致）
        await _module.HandleLogin(_session, new Packet
        {
            Pf = GamePf.Login,
            Serial = 1,
            Payload = new LoginReq { Account = "tester", Channel = "selftest" }.ToByteArray(),
        });

        var req = new TaskValueChangeReq();
        req.Task.Add(new TaskInt { Id = 65551, Value = 1 });   // (1,15)=1
        req.Task.Add(new TaskInt { Id = 65550, Value = 2 });   // (1,14)=2

        await _module.HandleTaskValueChange(_session, new Packet
        {
            Pf = GamePf.TaskValueChangeReq,
            Serial = 42,
            Payload = req.ToByteArray(),
        });

        // 应答 pf=1028 + 请求 serial，echo 已存值
        var outbound = _session.Sent.Last(o => o.Pf == GamePf.TaskValueChangeRsp);
        Assert.Equal(42u, outbound.Serial);
        var rsp = Assert.IsType<TaskValueChangeRsp>(outbound.Message);
        Assert.Equal(2, rsp.Task.Count);
        var t1 = rsp.Task.Single(t => t.Id == 65551);
        Assert.Equal(1, t1.Value);
        var t2 = rsp.Task.Single(t => t.Id == 65550);
        Assert.Equal(2, t2.Value);

        // Player 内存态已更新
        var player = _module.World.FindOnlineByAccount("tester");
        Assert.NotNull(player);
        Assert.Equal(1, player!.GetTask(1, 15));
        Assert.Equal(2, player.GetTask(1, 14));
    }

    // ---- 3. 1026 登录推送包含种子值 ----
    [Fact]
    public async Task Login_PushesTaskValueSyncRsp_ContainingSeededTutorialValue()
    {
        await _module.HandleLogin(_session, new Packet
        {
            Pf = GamePf.Login,
            Serial = 1,
            Payload = new LoginReq { Account = "newbie", Channel = "selftest" }.ToByteArray(),
        });

        // 顺序：1005 → 1026(教程闸门小包) → 1002 → 1026(全量) → 1104
        // ★ 登录会推**两条** 1026：先是只含 (1,15)/(1,14) 的闸门小包（紧跟 1005，
        //   保证客户端在 UpdateEndNode.OnEnter 判定之前就拿到它），再是全量任务值。
        //   取证见 GameModule.BuildTutorialGateSyncRsp 与 work/tutorial-replay-fix.md。
        var syncs = _session.Sent.Where(o => o.Pf == GamePf.TaskValueSyncRsp).ToList();
        Assert.Equal(2, syncs.Count);
        Assert.All(syncs, s => Assert.Equal(0u, s.Serial));   // 纯推送

        Assert.True(_session.Events.IndexOf("Push:1005") < _session.Events.IndexOf("Push:1026"),
            "教程闸门 1026 必须紧跟 PlayerNtf(1005)");
        Assert.True(_session.Events.IndexOf("Push:1005") < _session.Events.IndexOf("Send:1002:1"),
            "PlayerNtf(1005) 必须先于 LoginRsp(1002)");
        Assert.True(_session.Events.IndexOf("Push:1026") < _session.Events.IndexOf("Push:1104"),
            "任务值 1026 必须早于 65KB 的全量背包 1104，否则会被推迟 ~230ms 压过判定线");

        // 闸门小包只有 1 条（新号只播种了 (1,15)，没有 (1,14)）
        var gate = Assert.IsType<TaskValueSyncRsp>(syncs[0].Message);
        Assert.All(gate.Arr, t => Assert.Contains(t.Id, GameModule.TutorialGateTaskIds));
        Assert.Equal(6, gate.Arr.Single(t => t.Id == 65551).Value);

        // 全量包仍然包含种子值
        var rsp = Assert.IsType<TaskValueSyncRsp>(syncs[1].Message);
        var seed = rsp.Arr.Single(t => t.Id == 65551);      // (1,15)
        Assert.Equal(6, seed.Value);                         // SkipTutorial 种子
    }

    /// <summary>
    /// 教程闸门小包的构造：只挑 (1,15)/(1,14)，一条都没有时返回 null（不发空包）。
    /// 这是「重启客户端就重播新手教程」那个 bug 的修复点，回归保护见
    /// work/tutorial-replay-fix.md。
    /// </summary>
    [Fact]
    public void BuildTutorialGateSyncRsp_OnlyPicksGateTasks_AndReturnsNullWhenAbsent()
    {
        // 一条都没有 → null（不推空包）
        var empty = new Player("gate_none", 1);
        empty.SetTask(2, 2, 5);
        Assert.Null(_module.BuildTutorialGateSyncRsp(empty));

        // 两条都有 → 只带这两条，不带别的（哪怕玩家有几千条任务值）
        var full = new Player("gate_full", 2);
        full.SetTask(1, 15, 6);
        full.SetTask(1, 14, 1);
        for (int i = 0; i < 50; i++)
            full.SetTask(3, i, i + 1);

        var gate = _module.BuildTutorialGateSyncRsp(full);
        Assert.NotNull(gate);
        Assert.Equal(2, gate!.Arr.Count);
        Assert.Equal(6, gate.Arr.Single(t => t.Id == ((1u << 16) | 15u)).Value);
        Assert.Equal(1, gate.Arr.Single(t => t.Id == ((1u << 16) | 14u)).Value);

        // 闸门包必须小到能与 1005 挤在同一个 Socket.Receive 里（客户端每帧只收一次、
        // 上限 0x10000 字节，RVA 0x1B4D3E8）——几十字节量级即可。
        Assert.True(gate.ToByteArray().Length < 64,
            $"闸门包应该只有几十字节，实际 {gate.ToByteArray().Length}");

        // 只有 (1,14) 时也要发（IsFirstLevelComplete 的第二条件）
        var only14 = new Player("gate_14", 3);
        only14.SetTask(1, 14, 1);
        var g14 = _module.BuildTutorialGateSyncRsp(only14);
        Assert.NotNull(g14);
        Assert.Equal((1u << 16) | 14u, g14!.Arr.Single().Id);
    }

    [Fact]
    public async Task Login_WithoutSkipTutorial_DoesNotSeedTutorialValue()
    {
        var options = new ServerOptions
        {
            DataDirectory = _dataDir,
            SkipTutorial = false,
        };
        var module = new GameModule(options);
        var sess = new FakeSession { Id = 99 };
        await module.HandleLogin(sess, new Packet
        {
            Pf = GamePf.Login,
            Serial = 1,
            Payload = new LoginReq { Account = "nostep", Channel = "selftest" }.ToByteArray(),
        });

        // 没有种子值 ⇒ 教程闸门小包一条都挑不出来 ⇒ 只推全量那一条 1026
        var sync = sess.Sent.Single(o => o.Pf == GamePf.TaskValueSyncRsp);
        var rsp = Assert.IsType<TaskValueSyncRsp>(sync.Message);
        Assert.DoesNotContain(rsp.Arr, t => t.Id == 65551); // 未播种
    }

    // ---- 4. PlayerStore 任务字典往返（uint 键） ----
    [Fact]
    public void PlayerStore_RoundTrips_TaskDictionary_WithUIntKeys()
    {
        var store = new PlayerStore(new ServerOptions { DataDirectory = _dataDir, Sid = 9 });
        var p = new Player("tasker", World.DeriveRoleId("tasker"));
        p.SetTask(1, 15, 6);
        p.SetTask(1, 14, 3);
        p.SetTask(2, 7, 100);

        store.Save(p);

        var loaded = store.Load("tasker");
        Assert.NotNull(loaded);
        Assert.Equal(6, loaded!.GetTask(1, 15));
        Assert.Equal(3, loaded.GetTask(1, 14));
        Assert.Equal(100, loaded.GetTask(2, 7));
        Assert.Equal(0, loaded.GetTask(9, 9));   // 未存的不出现
        Assert.Equal(3, loaded.Tasks.Count);
    }

    // ---- 5. GetHouseInfoRsp 两个嵌套消息都存在 ----
    [Fact]
    public void GetHouseInfoRsp_HasBothNestedMessagesPresent()
    {
        var module = new GameModule(new ServerOptions { DataDirectory = _dataDir, Sid = 7 });
        var sess = new FakeSession { Id = 8 };
        var req = new GetHouseInfoReq { Roleid = 123 };
        module.HandleGetHouseInfo(sess, new Packet
        {
            Pf = GamePf.GetHouseInfo,
            Serial = 5,
            Payload = req.ToByteArray(),
        });

        var outbound = sess.Sent.Single(o => o.Pf == GamePf.GetHouseInfoRsp);
        Assert.Equal(5u, outbound.Serial);
        var rsp = Assert.IsType<GetHouseInfoRsp>(outbound.Message);
        Assert.Equal(123UL, rsp.Roleid);
        Assert.True(rsp.HasHouseInfo, "HouseInfo 嵌套消息必须存在");
        Assert.True(rsp.HasVisitinfo, "Visitinfo 嵌套消息必须存在");
        Assert.NotNull(rsp.HouseInfo);
        Assert.NotNull(rsp.Visitinfo);
        // 内部集合为空但可安全遍历
        Assert.Empty(rsp.HouseInfo!.FurnitureList);
        Assert.Empty(rsp.Visitinfo!.Entrustlog);
        Assert.Empty(rsp.Visitinfo!.Visitedlist);
        Assert.Empty(rsp.Visitinfo!.Tasklist);
        // 能编解码
        var bytes = rsp.ToByteArray();
        var roundTrip = ProtoMessage.Parse<GetHouseInfoRsp>(bytes);
        Assert.True(roundTrip.HasHouseInfo && roundTrip.HasVisitinfo);
    }

    // ---- 6. RenameReq field 3 往返 ----
    [Fact]
    public void RenameReq_StrName_IsOnField3_AndRoundTrips()
    {
        // 真实抓包 1A066C756F786961 = field 3, wire 2, len 6, "luoxia"
        var req = new RenameReq { StrName = "luoxia" };
        byte[] bytes = req.ToByteArray();
        Assert.Equal(new byte[] { 0x1A, 0x06, 0x6C, 0x75, 0x6F, 0x78, 0x69, 0x61 }, bytes);

        var parsed = ProtoMessage.Parse<RenameReq>(bytes);
        Assert.True(parsed.HasStrName);
        Assert.Equal("luoxia", parsed.StrName);
    }

    // ---- 7. 1006 rename handler ----
    [Fact]
    public async Task LoginRepairsNamedCompletedSaveAndPersistsGate()
    {
        var player = _module.World.GetOrCreatePlayer("named-returning");
        player.Name = "RestorationTester";
        player.SetTask(1, 15, 6);
        player.SetTask(1, 14, 0);
        _module.Store.Save(player);
        await _module.HandleLogin(_session, new Packet
        {
            Pf = GamePf.Login, Serial = 1,
            Payload = new LoginReq { Account = "named-returning", Channel = "selftest" }.ToByteArray()
        });
        Assert.Equal(1, _module.World.FindOnlineByAccount("named-returning")!.GetTask(1, 14));
        Assert.Equal(1, _module.Store.Load("named-returning")!.GetTask(1, 14));
    }

    [Fact]
    public async Task Rename_RepliesOnPf1006_SameSerial_EmptyRsp_SetsNameAndTask114()
    {
        // 先登录，让 session.Tag 挂上 Player
        await _module.HandleLogin(_session, new Packet
        {
            Pf = GamePf.Login,
            Serial = 1,
            Payload = new LoginReq { Account = "luo", Channel = "selftest" }.ToByteArray(),
        });
        var player = _module.World.FindOnlineByAccount("luo")!;
        string oldName = player.Name;
        Assert.Equal(0, player.GetTask(1, 14));

        var req = new RenameReq { StrName = "luoxia" };
        await _module.HandleRename(_session, new Packet
        {
            Pf = GamePf.Rename,
            Serial = 88,
            Payload = req.ToByteArray(),
        });

        // 回包 pf=1006 + 请求 serial，空 RenameRsp
        var outbound = _session.Sent.Last(o => o.Pf == GamePf.RenameRsp);
        Assert.Equal(88u, outbound.Serial);
        var rsp = Assert.IsType<RenameRsp>(outbound.Message);
        Assert.Empty(rsp.ToByteArray());   // 空 body

        // 名称已改 + task(1,14)=1
        Assert.Equal("luoxia", player.Name);
        Assert.NotEqual(oldName, player.Name);
        Assert.Equal(1, player.GetTask(1, 14));
    }

    [Fact]
    public async Task Rename_EmptyName_IsRejected_KeepsOldNameAndTask()
    {
        await _module.HandleLogin(_session, new Packet
        {
            Pf = GamePf.Login,
            Serial = 1,
            Payload = new LoginReq { Account = "luo2", Channel = "selftest" }.ToByteArray(),
        });
        var player = _module.World.FindOnlineByAccount("luo2")!;
        string oldName = player.Name;

        await _module.HandleRename(_session, new Packet
        {
            Pf = GamePf.Rename,
            Serial = 9,
            Payload = new RenameReq { StrName = "   " }.ToByteArray(),   // 纯空白 → 拒绝
        });

        Assert.Equal(oldName, player.Name);
        Assert.Equal(0, player.GetTask(1, 14));   // 拒绝时不得置“已命名”标志
        Assert.Equal(9u, (_session.Sent.Last(o => o.Pf == GamePf.RenameRsp)).Serial);
    }

    // ---- 8. 名称 + (1,14) 持久化 ----
    [Fact]
    public async Task Rename_PersistsName_AndTask114_ToStore()
    {
        await _module.HandleLogin(_session, new Packet
        {
            Pf = GamePf.Login,
            Serial = 1,
            Payload = new LoginReq { Account = "luo3", Channel = "selftest" }.ToByteArray(),
        });

        await _module.HandleRename(_session, new Packet
        {
            Pf = GamePf.Rename,
            Serial = 3,
            Payload = new RenameReq { StrName = "指挥官A" }.ToByteArray(),
        });

        // 从磁盘重新加载，验证名称与 (1,14) 都落盘
        var loaded = _module.Store.Load("luo3");
        Assert.NotNull(loaded);
        Assert.Equal("指挥官A", loaded!.Name);
        Assert.Equal(1, loaded.GetTask(1, 14));
    }
}
