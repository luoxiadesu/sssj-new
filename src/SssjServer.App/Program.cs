using System.Runtime.InteropServices;
using SssjServer.Core;
using SssjServer.Game;
using SssjServer.Game.Handlers;

namespace SssjServer.App;

/// <summary>
/// 服务端入口：解析命令行 → 装配 Game 层 handler → 并发启动 TCP 网关 + HTTP 引导
/// → Ctrl+C / SIGTERM 优雅退出。依赖方向 App → Game → Core → Protocol。
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var options = ParseArgs(args);

        using var cts = new CancellationTokenSource();

        void Shutdown()
        {
            Log.Info("收到中断信号，正在优雅退出…");
            cts.Cancel();
        }

        // 交互终端 Ctrl+C
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Shutdown();
        };

        // 非终端 / 后台（kill -INT / -TERM）也会触发，不依赖进程在前台进程组
        using var sigint = PosixSignalRegistration.Create(
            PosixSignal.SIGINT, ctx => { ctx.Cancel = true; Shutdown(); });
        using var sigterm = PosixSignalRegistration.Create(
            PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Shutdown(); });

        var router = new PfRouter();
        var luaCalls = new LuaCallRouter();

        // 业务层装配：注册全部 pf handler 与 LuaCall handler，并接上 NtfS2CCall 序列化
        var game = new GameModule(options);
        game.Install(router, luaCalls);

        router.MapDefault((session, packet) =>
        {
            var hex = Log.Hex(packet.Payload, 64);
            Log.Warn($"[session {session.Id}] unhandled pf={packet.Pf} len={packet.Payload.Length} hex={hex}");
            return Task.CompletedTask;
        });

        var gateway = new GameGateway(options, router);
        var http = new BootstrapHttpServer(options);

        Log.Info($"SssjServer 启动：gate={options.GateHost}:{options.GatePort} " +
                 $"bind={options.BindAddress} http={options.HttpPort} data={options.DataDirectory}");

        try
        {
            await Task.WhenAll(
                Task.Run(() => gateway.RunAsync(cts.Token)),
                Task.Run(() => http.RunAsync(cts.Token)),
                Task.Run(() => WatchGmFileAsync(options.GmFile, cts.Token)),
                Task.Run(() => WatchGachaRotationAsync(options.SettingDirectory, cts.Token)));
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception e)
        {
            Log.Error($"服务启动失败: {e}");
            throw;
        }

        Log.Info("服务已停止。");
    }

    /// <summary>
    /// GM 控制台的文件投递循环：轮询 <paramref name="path"/>，内容变化就推给所有在线客户端执行。
    ///
    /// 之所以用「文件 + 轮询」而不是 HTTP/stdin：服务端常以后台任务方式跑（无 tty），
    /// 而验收时最方便的驱动方式就是 <c>echo '...' &gt; work/gm.lua</c>。
    /// 轮询 500ms、只比对内容（不看 mtime），避免 WSL/挂载点的时间戳精度问题。
    /// 首次启动时**不执行**已存在的内容（只记录基线），防止重启即重放上次命令。
    /// </summary>
    private static async Task WatchGachaRotationAsync(string directory, CancellationToken ct)
    {
        var rotation = SssjServer.Game.Config.GachaRotation.For(directory);
        string last = rotation.Revision(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            string next = rotation.Revision(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (next == last) continue;
            last = next;
            GmConsole.Broadcast(rotation.ClientLua());
            Log.Info("[gacha-rotation] 排期已同步；重新进入补给页刷新列表");
        }
    }

    private static async Task WatchGmFileAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        string full = Path.GetFullPath(path);
        string? last = null;
        try
        {
            if (File.Exists(full))
                last = await File.ReadAllTextAsync(full, ct);
        }
        catch (Exception e)
        {
            Log.Warn($"[gm] 读取 {full} 失败：{e.Message}");
        }
        Log.Info($"[gm] 控制台就绪：把 Lua 写进 {full} 即下发给在线客户端（输出看 adb logcat Unity）");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, ct);
                if (!File.Exists(full))
                    continue;
                string cur = await File.ReadAllTextAsync(full, ct);
                if (cur == last)
                    continue;
                last = cur;
                if (string.IsNullOrWhiteSpace(cur))
                    continue;
                Log.Info($"[gm] 检测到新命令：{cur.Trim().Replace("\n", " ⏎ ")}");
                GmConsole.Broadcast(cur);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Warn($"[gm] 处理 {full} 失败：{e.Message}");
            }
        }
    }

    /// <summary>解析命令行（默认值取自 ServerOptions）。</summary>
    private static ServerOptions ParseArgs(string[] args)
    {
        var o = new ServerOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--gate-host" when i + 1 < args.Length:
                    o.GateHost = args[++i];
                    break;
                case "--gate-port" when i + 1 < args.Length:
                    o.GatePort = ParseInt(args[++i], o.GatePort, "--gate-port");
                    break;
                case "--http-port" when i + 1 < args.Length:
                    o.HttpPort = ParseInt(args[++i], o.HttpPort, "--http-port");
                    break;
                case "--bind" when i + 1 < args.Length:
                    o.BindAddress = args[++i];
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    o.DataDirectory = args[++i];
                    break;
                case "--skip-tutorial" when i + 1 < args.Length:
                    o.SkipTutorial = ParseBool(args[++i], o.SkipTutorial, "--skip-tutorial");
                    break;
                case "--grant-starter" when i + 1 < args.Length:
                    o.GrantStarter = ParseBool(args[++i], o.GrantStarter, "--grant-starter");
                    break;
                case "--setting-dir" when i + 1 < args.Length:
                    o.SettingDirectory = args[++i];
                    break;
                // ★ 生产环境关掉机器人好友与假社团：--bots false
                case "--bots" when i + 1 < args.Length:
                    o.EnableBots = ParseBool(args[++i], o.EnableBots, "--bots");
                    break;
                // ★ 资源下发（方案 A）：两个都给才生效，见 ServerOptions.AssetDirectory
                case "--asset-dir" when i + 1 < args.Length:
                    o.AssetDirectory = args[++i];
                    break;
                // ★★★ 热更新推荐用 `--asset-build-version auto`：每次现读
                //   {--asset-dir}/version.data 的 buildVersion ⇒ 与清单结构上不可能不一致，
                //   且 tools/hotfix.py 发布新补丁后**无需重启服务端**。
                //   写死数字的话，hotfix 把清单 bump 到 N+1 而这里还是 N ⇒ 客户端认为
                //   「没有更新」⇒ 什么都不下，而且**两端日志全都干净**（最难查的一类）。
                case "--asset-build-version" when i + 1 < args.Length:
                {
                    string raw = args[++i];
                    if (raw.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    {
                        o.AssetBuildVersionAuto = true;
                    }
                    else
                    {
                        o.AssetBuildVersionAuto = false;
                        o.AssetBuildVersion = ParseInt(raw, o.AssetBuildVersion, "--asset-build-version");
                    }
                    break;
                }
                case "--spend-vigour" when i + 1 < args.Length:
                    o.SpendVigour = ParseBool(args[++i], o.SpendVigour, "--spend-vigour");
                    break;
                case "--refill-vigour" when i + 1 < args.Length:
                    o.RefillVigourOnLogin = ParseBool(args[++i], o.RefillVigourOnLogin, "--refill-vigour");
                    break;
                case "--start-gold" when i + 1 < args.Length:
                    o.StartGold = ParseInt(args[++i], o.StartGold, "--start-gold");
                    break;
                case "--start-diamond" when i + 1 < args.Length:
                    o.StartDiamond = ParseInt(args[++i], o.StartDiamond, "--start-diamond");
                    break;
                case "--gm-file" when i + 1 < args.Length:
                    o.GmFile = args[++i];
                    break;
                // ★★ GM 全解锁的两个开关（此前只有代码默认值、无法从命令行调）。
                //
                // --gm-accounts：逗号分隔的账号白名单；特殊值 `*` = 所有账号都当 GM。
                //   ⇒ 「新用户一进来就全入口解锁」用 `--gm-accounts '*'`。
                //   ⇒ 公网收紧时改成 `--gm-accounts 'myname'`（只有自己是 GM），
                //      或配合 `--gm-full-unlock false` 彻底关掉。
                //   ★ 缺省是 `{""}`（空账号），因为 platform=selftest 下客户端上报的
                //     account 就是空串（见 ServerOptions.GmAccounts 注释）。
                case "--gm-accounts" when i + 1 < args.Length:
                    o.GmAccounts = args[++i]
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    break;
                case "--gm-full-unlock" when i + 1 < args.Length:
                    o.GmFullUnlock = ParseBool(args[++i], o.GmFullUnlock, "--gm-full-unlock");
                    break;
            }
        }
        return o;
    }

    private static bool ParseBool(string raw, bool fallback, string name)
    {
        return bool.TryParse(raw, out var v) ? v : fallback;
    }

    private static int ParseInt(string raw, int fallback, string name)
    {
        return int.TryParse(raw, out var v) ? v : fallback;
    }
}
