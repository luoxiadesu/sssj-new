using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using SssjServer.Core;

namespace SssjServer.Game.Tests;

/// <summary>
/// 功能域的通用端到端夹具：真实 <see cref="GameModule"/>（反射装配所有
/// <see cref="IGameFeature"/>）+ 真实 <see cref="LuaCallRouter"/> + loopback
/// <see cref="ClientSession"/>，并把 <c>NtfS2CCallSender</c> 换成内存捕获器，
/// 于是可以对 pf 1024 的 <c>(sFunction, sJsonParam)</c> 做确定性断言。
///
/// 两个必须这么做的理由：
/// <list type="number">
/// <item><c>SettingDirectory</c> 必须显式指到**仓库里的真实 setting 目录**
///   （用 <see cref="CallerFilePathAttribute"/> 反推仓库根）：<c>dotnet test</c> 的
///   工作目录随 <c>ArtifactsPath</c> 落在 /tmp，否则功能域会读到空配置表，
///   端到端断言全部失去意义（分解价、签到奖励都来自配置）。</item>
/// <item>必须用真的 <see cref="ClientSession"/>：<see cref="LuaCallRouter"/> 的签名要它，
///   而 <c>FakeSession</c> 只实现 <see cref="IGameSession"/>。loopback socket
///   足够便宜（一条测试一对 TcpClient），且能顺带验证「不会因为写 socket 抛异常」。</item>
/// </list>
///
/// 与 <c>MissionHarness</c> 的关系：那个是任务域先写的同构夹具，本类是它的泛化版
/// （多暴露了 <see cref="Module"/> 与 <see cref="Options"/>，便于断言配置载入结果）。
/// </summary>
internal sealed class FeatureHarness : IDisposable
{
    private readonly TcpListener _listener;
    private readonly TcpClient _client;
    private readonly TcpClient _accepted;
    private readonly string _dataDir;

    public ClientSession Session { get; }
    public LuaCallRouter LuaCalls { get; }
    public PfRouter Pf { get; }
    public GameModule Module { get; }
    public ServerOptions Options { get; }
    public Player Player { get; }

    /// <summary>捕获到的下行：(sFunction, sJsonParam 的 UTF-8 文本)，按发送顺序。</summary>
    public List<(string Function, string Json)> Outbound { get; } = new();

    /// <summary>捕获到的 pf 推送（不含 pf 1024，那条走 <see cref="Outbound"/>）。</summary>
    private readonly List<int> _pushedPf = new();
    private readonly List<byte> _pushBuf = new();

    /// <summary>
    /// 服务端本次会话里**通过 <c>session.Push</c> 推出去的 pf 序列**（按顺序，可重复）。
    ///
    /// 实现方式：<c>ClientSession.Push</c> 是直接写 socket 的（没有像
    /// <c>LuaCallRouter.NtfS2CCallSender</c> 那样的注入接缝），所以这里从
    /// **loopback 的客户端那一端**把字节排出来，按 16 字节包头解析 pf
    /// （头格式见 AGENTS.md §2.3：<c>[pf][总长][serial][压缩][0x88][code]</c>）。
    /// harness 不走 pf 1102 Verify，所以包体是明文、不需要解密。
    ///
    /// 每次读取属性都会**增量排空**当前可读的字节，因此断言前直接读它即可；
    /// 被调用多次也不会丢数据（已解析的留在列表里，半个包留在缓冲里等下次）。
    ///
    /// ★ 这个字段先前是**死的**（只声明、从未写入），导致所有功能域的单测都无法断言
    /// 「推了哪些 pf」，只能退化成断言玩家状态 —— 本轮补上。
    /// </summary>
    public List<int> PushedPf
    {
        get
        {
            DrainPushedPf();
            return _pushedPf;
        }
    }

    /// <summary>
    /// 等到 <paramref name="pf"/> 出现在 <see cref="PushedPf"/> 里，返回是否等到。
    ///
    /// ★ 为什么需要它：<c>session.Push</c> 是**异步写 socket**，
    /// handler 返回之后字节未必已经到达 loopback 的另一端。
    /// 直接读 <see cref="PushedPf"/> 会 flaky（实测同一个 Theory 的两个用例一个过一个不过）。
    /// 断言 pf 推送一律用这个方法，不要裸读属性。
    /// </summary>
    public async Task<bool> WaitForPfAsync(int pf, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (PushedPf.Contains(pf))
                return true;
            await Task.Delay(20);
        }
        return PushedPf.Contains(pf);
    }

    // A real client reads while the server sends. Large, repeated login snapshots can fill
    // the loopback receive window if a test waits for the synchronous sender before reading.
    public async Task DrainWhileAsync(Func<Task> action)
    {
        var work = Task.Run(action);
        while (!work.IsCompleted)
        {
            DrainPushedPf();
            await Task.WhenAny(work, Task.Delay(10));
        }
        await work;
        DrainPushedPf();
    }

    /// <summary>把 loopback 上已到达的字节解析成 pf 列表（非阻塞）。</summary>
    private void DrainPushedPf()
    {
        try
        {
            var stream = _client.GetStream();
            while (_client.Available > 0)
            {
                var chunk = new byte[_client.Available];
                int n = stream.Read(chunk, 0, chunk.Length);
                if (n <= 0)
                    break;
                for (int i = 0; i < n; i++)
                    _pushBuf.Add(chunk[i]);
            }
        }
        catch (Exception)
        {
            // socket 已关闭等情况：保持已解析的内容，不影响断言
        }

        // 按 16 字节头切包：[0:4]pf [4:8]总长(16+payload) [8:12]serial [12]压缩 [13]0x88 [14:16]code
        while (_pushBuf.Count >= 16)
        {
            var head = _pushBuf.GetRange(0, 16).ToArray();
            int pf = BitConverter.ToInt32(head, 0);
            int total = BitConverter.ToInt32(head, 4);
            if (head[13] != 0x88 || total < 16 || total > 1 << 24)
            {
                // 不是合法包头（理论上不该发生）：丢一个字节重新对齐，避免死循环
                _pushBuf.RemoveAt(0);
                continue;
            }
            if (_pushBuf.Count < total)
                break;                      // 半个包，等下次
            _pushBuf.RemoveRange(0, total);
            _pushedPf.Add(pf);
        }
    }

    private FeatureHarness(TcpListener listener, TcpClient client, TcpClient accepted,
                           ClientSession session, LuaCallRouter luaCalls, PfRouter pf,
                           GameModule module, ServerOptions options, Player player, string dataDir)
    {
        _listener = listener;
        _client = client;
        _accepted = accepted;
        _dataDir = dataDir;
        Session = session;
        LuaCalls = luaCalls;
        Pf = pf;
        Module = module;
        Options = options;
        Player = player;
    }

    /// <param name="enableBots">
    /// 是否生成机器人好友与假社团（对应 <c>ServerOptions.EnableBots</c> / <c>--bots</c>）。
    /// 默认 <c>true</c> 与生产默认值一致；传 <c>false</c> 可测「生产环境关掉机器人」的形态。
    /// </param>
    public static async Task<FeatureHarness> CreateAsync(string account, bool attachPlayer = true,
                                                         bool enableBots = true)
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "sssj-feature-tests", Guid.NewGuid().ToString("N"));

        var options = new ServerOptions
        {
            DataDirectory = dataDir,
            SettingDirectory = TestPaths.SettingDir,
            // 测试里不要起后台文件轮询（ServerConsoleFeature 会开一个无限循环 Task）
            ServerGmFile = "",
            GmFile = "",
            EnableBots = enableBots,
        };

        var pf = new PfRouter();
        var luaCalls = new LuaCallRouter();
        var module = new GameModule(options);
        module.Install(pf, luaCalls);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var client = new TcpClient();
        var connect = client.ConnectAsync(IPAddress.Loopback, port);
        var accepted = await listener.AcceptTcpClientAsync();
        await connect;

        var session = new ClientSession(1, accepted);
        var player = module.World.GetOrCreatePlayer(account);
        if (attachPlayer)
            session.Tag = player;

        var harness = new FeatureHarness(listener, client, accepted, session, luaCalls, pf,
                                         module, options, player, dataDir);

        luaCalls.NtfS2CCallSender = (_, function, sJsonParam) =>
        {
            harness.Outbound.Add((function, Encoding.UTF8.GetString(sJsonParam)));
            return Task.CompletedTask;
        };

        return harness;
    }

    /// <summary>
    /// 触发功能域的登录钩子（<c>ctx.OnPlayerLogin</c>）。
    ///
    /// ★ harness 本身**不走登录流程**，所以「靠登录钩子主动推数据」的功能域
    /// （例如邮件 pf 1014 —— 客户端从不索要列表）默认测不到，必须显式调这个。
    /// </summary>
    public void RunLoginHooks() => Module.RunLoginHooksForTest(Session, Player);

    /// <summary>
    /// 触发功能域的**登录前**钩子（<c>ctx.OnPlayerPrepare</c>，真实流程里跑在 pf 1005 之前）。
    ///
    /// ★ 与 <see cref="RunLoginHooks"/> 是**两批不同的钩子**：挂在 <c>OnPlayerPrepare</c> 上的
    /// （少女剧情 <c>ELevel</c> 初值、换装的服饰获得标记）不会被 <see cref="RunLoginHooks"/> 跑到
    /// —— 测这类「登录时补任务值」的功能域必须调这一个。
    /// </summary>
    public void RunPrepareHooks() => Module.RunPrepareHooksForTest(Session, Player);

    /// <summary>
    /// 模拟一个 **pf 上行**（不走 pf 1022 的 LuaCall 通道，而是直接把 proto 消息
    /// 交给 <see cref="Pf"/> 路由 —— 邮件 1016/1018/1020 这类就是这么来的）。
    /// </summary>
    public Task DispatchPfAsync(int pf, SssjServer.Protocol.ProtoMessage req)
        => Pf.DispatchAsync(Session, new Packet
        {
            Pf = pf,
            Serial = 1,
            Compressed = false,
            Payload = req.ToByteArray(),
        });

    /// <summary>模拟 pf 1022 里 sFunction=&lt;字符串命令&gt; 的上行（S2CCall.lua 那一族）。</summary>
    public Task DispatchFunctionAsync(string function, string sJsonParam)
        => LuaCalls.DispatchAsync(Session, function, sJsonParam);

    /// <summary>模拟 pf 1022 里 sFunction="LuaCall" 的上行（数字命令族）。</summary>
    public Task DispatchLuaCallAsync(int cmd, string tbParamJson)
        => LuaCalls.DispatchAsync(Session, LuaCallRouter.LuaCallFunction,
                                  $"{{\"sCmd\":{cmd},\"tbParam\":{tbParamJson}}}");

    // Explicit test client for the patched chapter protocol, including the entry token.
    internal string ChapterBattleToken => Outbound.Select(o => System.Text.Json.Nodes.JsonNode.Parse(o.Json))
        .Where(o => o?["nState"]?.ToString() == "0" && o?["nError"]?.ToString() == "0")
        .Select(o => o!["battleToken"]!.GetValue<string>()).LastOrDefault() ?? "no-entry";

    internal Task CompleteChapterAsync(int chapter, int index, int diff, int stars = 7, bool success = true)
        => DispatchFunctionAsync("ChapterMsg", new System.Text.Json.Nodes.JsonObject
        {
            ["nState"] = 1, ["Chapter"] = chapter, ["Index"] = index, ["Difficult"] = diff,
            ["nStar"] = stars, ["bSuccess"] = success, ["battleToken"] = ChapterBattleToken,
        }.ToJsonString());

    /// <summary>取最后一条下行（断言用）；没有下行时抛。</summary>
    public (string Function, string Json) LastOutbound
        => Outbound.Count > 0 ? Outbound[^1] : throw new InvalidOperationException("没有任何下行");

    private static string ResolveSettingDir([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "extracted", "setting");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException($"从 {thisFile} 向上找不到 extracted/setting");
    }

    public void Dispose()
    {
        try { Session.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
        try { _accepted.Dispose(); } catch { }
        try { _listener.Stop(); } catch { }
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
