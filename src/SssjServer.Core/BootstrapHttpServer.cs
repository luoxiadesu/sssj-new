using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SssjServer.Core;

/// <summary>
/// HTTP 引导服务（替代 tools/bootstrap_server.py，无 ASP.NET Core，用原生 TcpListener 手写 HTTP/1.1）。
/// 端点：
///   GET  /serverlist?platform=&version=&device=  → 服务器列表（state=1）
///   GET  /getstate?id=N                           → {"state":1}
///   POST /checkupdate（GET 同样返回）              → msg="0"、buildversion=0（让客户端跳过资源更新）
///   POST /uploadclientip                          → {"ret":0}
///   其它路径 → 404 {}
/// 每个请求都打日志（方法/路径/query/POST body），是开发期的核心反馈循环。
/// </summary>
public sealed class BootstrapHttpServer
{
    private readonly ServerOptions _options;
    private readonly AssetManifest _manifest;

    public BootstrapHttpServer(ServerOptions options)
    {
        _options = options;
        _manifest = new AssetManifest(options.AssetDirectory);
    }

    /// <summary>
    /// 本次要下发的资源清单版本。
    ///
    /// <para><c>--asset-build-version auto</c> 时**每次现读** <c>version.data</c>
    /// ⇒ 与清单在结构上不可能不一致，发布热更补丁**无需重启服务端**
    /// （见 <see cref="AssetManifest"/> 的类注释：手写死版本号是热更新的第一号陷阱）。</para>
    /// </summary>
    private int EffectiveBuildVersion =>
        _options.AssetBuildVersionAuto ? _manifest.ReadBuildVersion() : _options.AssetBuildVersion;

    public async Task RunAsync(CancellationToken ct)
    {
        var bind = _options.BindAddress == "0.0.0.0" || string.IsNullOrEmpty(_options.BindAddress)
            ? IPAddress.Any
            : IPAddress.Parse(_options.BindAddress);

        var listener = new TcpListener(bind, _options.HttpPort);
        listener.Start();
        Log.Info($"[http] 引导服务监听 {_options.BindAddress}:{_options.HttpPort}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                _ = Task.Run(() => HandleConnectionAsync(tcp), CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp)
    {
        using (tcp)
        using (var stream = tcp.GetStream())
        {
            try
            {
                var buffer = new List<byte>();
                var req = await ReadRequestAsync(stream, buffer, readBody: true);
                if (req == null)
                    return;

                LogRequest(req);

                // ★ 资源文件走**流式**发送（不把整个文件读进内存），见 TryResolveAssetPath / WriteFileResponseAsync。
                //   起因（第七轮实测）：148 MB 的 APK 用 File.ReadAllBytes 会把整份读进堆，
                //   撞上 systemd 的 MemoryHigh=768M ⇒ 内核持续回收并**节流进程**
                //   （cgroup memory.events 的 high 计数飙到 3968）⇒ 下载速度从
                //   11 MB/s 掉到 ~480 KB/s。流式发送后内存占用与文件大小无关。
                if (TryResolveAssetPath(req.Path, out var full, out var deny))
                {
                    if (deny is not null)
                    {
                        await WriteResponseAsync(stream, deny.Value.Status, deny.Value.StatusText,
                                                 deny.Value.Body, deny.Value.ContentType);
                        return;
                    }
                    await WriteFileResponseAsync(stream, full!);
                    return;
                }

                var (status, statusText, body, contentType) = Route(req);
                await WriteResponseAsync(stream, status, statusText, body, contentType);
            }
            catch (Exception e)
            {
                Log.Warn($"[http] 连接处理异常: {e.Message}");
            }
        }
    }

    /// <summary>默认响应类型：引导接口全是 JSON，只有 /notice.html 是 text/html。</summary>
    private const string JsonContentType = "application/json; charset=utf-8";

    private static readonly JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private (int Status, string StatusText, byte[] Body, string ContentType) Route(HttpRequest req)
    {
        // 用 UnsafeRelaxedJsonEscaping 输出 UTF-8 原文而不是 \uXXXX：
        // 客户端这几个接口都是 C# 侧解析（Newtonsoft / SimpleJson），两种写法都能解，
        // 但原文让 `curl http://127.0.0.1:8080/notice.txt` 直接可读，排障省事。
        // 注意这与 LuaCallRouter.ClientJson 的理由不同 —— 那里是**必须**，
        // 因为 Lua 的 Json.Decode（Json.lua:252）根本不认 \u 转义。
        byte[] Json(JsonObject o) => Encoding.UTF8.GetBytes(o.ToJsonString(RelaxedJson));

        switch (req.Path)
        {
            case "/serverlist":
            {
                var list = new JsonObject
                {
                    ["id"] = _options.ServerId,
                    ["ip"] = _options.GateHost,
                    ["port"] = new JsonArray(_options.GatePort),
                    ["aid"] = _options.Aid,
                    ["sid"] = _options.Sid,
                    ["name"] = _options.ServerName,
                    ["state"] = 1,
                    ["flag"] = "",
                };
                var root = new JsonObject
                {
                    ["level"] = 1,
                    ["serverlist"] = new JsonArray(list),
                };
                return (200, "OK", Json(root), JsonContentType);
            }

            case "/getstate":
            {
                int id = req.Query.TryGetValue("id", out var idStr)
                    && int.TryParse(idStr, out var parsed) ? parsed : 1;
                var root = new JsonObject { ["state"] = 1, ["id"] = id };
                return (200, "OK", Json(root), JsonContentType);
            }

            case "/checkupdate":
                return (200, "OK", Json(CheckUpdate()), JsonContentType);

            case "/uploadclientip":
                return (200, "OK", Json(new JsonObject { ["ret"] = 0 }), JsonContentType);

            // ---- 公告：两条**纯 HTTP**链路，与 TCP / pf 完全无关 ----
            //
            // ① GET {updateUrl}notice.txt —— banner / 活动 / 新闻 / 登录公告的数据源。
            //    取证：extracted/lua/NoticeCommon.lua:16 默认
            //    http://gcg2notice.ks3-cn-beijing.ksyun.com/notice.txt，:18..21 改用
            //    Game.Dir.GetBaseUpdateUrl() .. "notice.txt"，:32..36 追加 ?rnd=<random>。
            //    而 tools/patch_apk.py:36 把 envir.json 的 updateUrl 设成 http://<host>:<port>/，
            //    所以它落在本服务上。反序列化目标是 NoticeHelper.Notice（Newtonsoft，RVA 0x18133C4）。
            // ② GET {notice} —— 主界面公告由 UniWebView 直接 Load 的网页。
            //    取证：UI_Notice.lua:193 `self.uniwebview:Load(string.format("%s?%d",
            //    Game.ServerList.GetNoticeUrl(), os.time()))`；patch_apk.py:38 写的是 base + "/notice.html"。
            //
            // 不实现这两条不会崩，只是公告面板空白 —— 之前实测面板里显示一个字面的 "{}"，
            // 就是 404 分支回的空 JSON 被 UniWebView 当网页正文渲染了。
            case "/notice.txt":
                return (200, "OK", Json(Notice()), JsonContentType);

            case "/notice.html":
                return (200, "OK", Encoding.UTF8.GetBytes(NoticeHtml()), "text/html; charset=utf-8");

            default:
            {
                // ★ 资源下发（客户端拉 {updateUrl}Bundles/<bundle 名>，
                //   Game.Dir.update RVA 0x1E8EFE8 = baseUpdateUrl + "Bundles" + "/"；
                //   清单本身走 {updateUrl}Bundles/version.data?time=<rand>，0x1531578）
                //   **已在 HandleConnectionAsync 里被流式分支提前处理**，不会走到这里。
                //   ⇒ 落到这里的就是真正未实现的路径。
                Log.Warn($"[http] 未处理 {req.Method} {req.Path}");
                return (404, "Not Found", Json(new JsonObject()), JsonContentType);
            }
        }
    }

    /// <summary>
    /// <c>notice.txt</c> 的最小合法体。字段名与类型来自 <c>NoticeHelper</c> 的四个 DTO
    /// （<c>Notice</c> / <c>BannerData</c> / <c>NoticeData</c> / <c>SimpleNoticeData</c>），
    /// 客户端取用接口 <c>GetBanners 0x1812AF0</c> / <c>GetNotices 0x1812B60</c> /
    /// <c>GetActivity 0x1812BD0</c> / <c>GetNews 0x1812C40</c> /
    /// <c>GetLoginNotice 0x1812CB0</c> / <c>GetBannerLoopTime 0x1813000</c>；
    /// lua 侧一次性取全部六项：<c>NoticeCommon.lua:51..60</c> → <c>UI_Notice.lua:202</c>。
    ///
    /// 六个数组都允许为空（客户端只是没内容可显示）。<c>startTime</c>/<c>endTime</c> 是
    /// <c>DateTime</c>，用 Newtonsoft 默认能解的 ISO 8601；这里给一个宽区间让公告恒生效。
    /// <c>LoginNotice</c> 非空会在登录后弹窗，默认留空免打扰。
    /// </summary>
    private static JsonObject Notice()
    {
        JsonObject Item(int id, string title, string content) => new()
        {
            ["ID"] = id,
            ["forceShow"] = 0,
            ["priority"] = id,
            ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ["title"] = title,
            ["content"] = content,
            ["startTime"] = "2000-01-01T00:00:00",
            ["endTime"] = "2099-01-01T00:00:00",
        };

        return new JsonObject
        {
            ["bannerLoopTime"] = 5,
            ["banners"] = new JsonArray(),
            ["notices"] = new JsonArray(
                Item(1, "本地服务端",
                     "制作人：SteveS，勿擅自转发\n\n这是 sssj-new 本地服务端下发的公告。\n登录 / 关卡 / 背包 / 签到等功能已可用。")),
            ["activity"] = new JsonArray(),
            ["news"] = new JsonArray(),
            ["LoginNotice"] = new JsonArray(),
        };
    }

    /// <summary>主界面公告网页（UniWebView 只负责展示，内容随意）。</summary>
    private static string NoticeHtml() => """
        <!DOCTYPE html>
        <html lang="zh-CN"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>公告</title>
        <style>
          body{margin:0;padding:24px;background:#f4f6f9;color:#2b3038;
               font-family:-apple-system,"PingFang SC","Microsoft YaHei",sans-serif;line-height:1.8}
          h1{font-size:22px;margin:0 0 16px;color:#1a7fd4}
          h2{font-size:17px;margin:20px 0 8px}
          code{background:#e6ebf2;padding:1px 5px;border-radius:3px;font-size:13px}
          .tip{color:#7a8494;font-size:13px;margin-top:24px}
        </style></head><body>
        <h1>sssj-new 本地服务端</h1>
        <p><strong>制作人：SteveS，勿擅自转发</strong></p>
        <p>这是本地服务端提供的公告页（<code>GET /notice.html</code>）。</p>
        <h2>已可用</h2>
        <p>登录 · 主线关卡与结算 · 编队 · 背包与分解 · 每日签到 · 任务成就领奖 · 咖啡馆数据 · 手机短信</p>
        <h2>开发中</h2>
        <p>商店 · 抽卡 · 邮件 · 武器与零件 · 少女养成</p>
        <p class="tip">数据源见 <code>server/src/SssjServer.Core/BootstrapHttpServer.cs</code>。</p>
        </body></html>
        """;

    /// <summary>
    /// <c>POST /checkupdate</c> 的应答。
    ///
    /// <para><b>★★ 两个字段的语义（最容易搞反的一处）</b></para>
    /// <list type="bullet">
    ///   <item><c>msg</c> 必须是字符串 <c>"0"</c> —— 它表示「**本次请求成功**」，
    ///     <b>不是</b>「无更新」。取证：<c>0x1F546EC String.op_Equality(msg, "0")</c>
    ///     → <c>0x1F546F0 tbz</c>，不等就跳失败路径、0.2s 后换下一个 URL 无限重试。</item>
    ///   <item><c>urllist.buildversion</c> 才是「要不要更新」的开关：
    ///     <c>0x1F548C0 ldr w8,[x20,#0x30]</c> + <c>0x1F548C4 cbnz</c> ——
    ///     <b>0 ⇒ 打日志 <c>"buildversion is 0. dont update"</c> 并跳过整个资源更新</b>；
    ///     非 0 ⇒ 进下载流程。</item>
    /// </list>
    ///
    /// ★ <c>urllist.updateurl</c> 会被客户端 <c>ClearUrl</c>+<c>AddUrl</c>
    /// （<c>0x1F54734</c> / <c>0x1F547BC</c>）**覆盖** <c>envir.json</c> 里的 <c>updateUrl</c>，
    /// 成为后续所有资源下载的 base ⇒ 这里必须给出可达地址且**末尾带 /**
    /// （客户端拼接是纯 <c>Concat</c>，不补斜杠）。
    ///
    /// 只有 4 个字段被真正读取（<c>msg</c> / <c>urllist</c> / <c>updateurl</c> /
    /// <c>buildversion</c>），其余 6 个保留为空即可。
    /// </summary>
    private JsonObject CheckUpdate()
    {
        int buildVersion = EffectiveBuildVersion;
        bool serveAssets = !string.IsNullOrEmpty(_options.AssetDirectory) && buildVersion != 0;
        if (!serveAssets)
            return NoUpdate();

        string baseUrl = $"http://{_options.GateHost}:{_options.HttpPort}/";
        var rsp = NoUpdate();
        var url = (JsonObject)rsp["urllist"]!;
        url["buildversion"] = buildVersion;
        url["updatever"] = buildVersion.ToString();
        url["updateurl"] = new JsonArray { baseUrl };
        Log.Info($"[http] checkupdate → 有更新：buildversion={buildVersion}" +
                 (_options.AssetBuildVersionAuto ? "（auto，现读清单）" : "") +
                 $"、updateurl={baseUrl}（资源目录 {_options.AssetDirectory}）");
        return rsp;
    }

    /// <summary>
    /// 尝试把 <c>/Bundles/&lt;相对路径&gt;</c> 映射到 <c>{AssetDirectory}/&lt;相对路径&gt;</c>。
    /// 不是资源请求（或未配置资源目录）返回 null，由调用方走 404。
    ///
    /// <para>★ 三个必须处理的细节</para>
    /// <list type="number">
    ///   <item>客户端会在清单 URL 后加 <c>?time=&lt;rand&gt;</c>（<c>0x1531578</c>）
    ///     —— query 已由 <c>HttpRequest</c> 拆掉，这里拿到的 <c>path</c> 是干净的。</item>
    ///   <item>**必须防目录穿越**：路径里含 <c>..</c> 一律拒绝（回 403）。
    ///     ★ 实测：裸 <c>..</c> 被这里挡住（<c>curl --path-as-is</c> 验证，普通 curl 会
    ///     自己规范化路径所以看不到）；URL 编码形式 <c>%2e%2e%2f</c> 因为**本服务不做
    ///     URL 解码**而拿不到文件（落到 404）。
    ///     ⇒ 不解码是安全的，且**不会漏掉正常资源**：全部
    ///     5150 个 <c>abinfo</c> 名 + 5138 个 <c>bundles</c> 名经程序化校验
    ///     **零个**含非 <c>[A-Za-z0-9_./()-]</c> 字符（无空格、无中文、无需转义）。</item>
    ///   <item>客户端**串行下载、无 Range 续传**，所以这里不实现 206；
    ///     但 <c>Accept-Ranges: none</c> 也不必声明（客户端不看）。</item>
    /// </list>
    ///
    /// <para>★★ 第七轮改成「只解析路径、不读内容」：内容由
    /// <see cref="WriteFileResponseAsync"/> **流式**写出。理由见
    /// <c>HandleConnectionAsync</c> 里的注释（`File.ReadAllBytes` 撞 cgroup
    /// `MemoryHigh` 会让大文件下载被内核节流到 ~480 KB/s）。</para>
    /// </summary>
    /// <param name="full">解析出的磁盘绝对路径（仅当返回 true 且 deny 为 null 时有效）。</param>
    /// <param name="deny">403/404 等需要直接回给客户端的响应；为 null 表示可以发文件。</param>
    /// <returns>是否是资源请求（false = 交给 <see cref="Route"/> 处理）。</returns>
    private bool TryResolveAssetPath(
        string path,
        out string? full,
        out (int Status, string StatusText, byte[] Body, string ContentType)? deny)
    {
        full = null;
        deny = null;

        if (string.IsNullOrEmpty(_options.AssetDirectory))
            return false;
        const string prefix = "/Bundles/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        string rel = path.Substring(prefix.Length);
        if (rel.Length == 0 || rel.Contains("..", StringComparison.Ordinal))
        {
            Log.Warn($"[http] 拒绝可疑资源路径 {path}");
            deny = (403, "Forbidden", Array.Empty<byte>(), "application/octet-stream");
            return true;
        }

        string candidate = Path.Combine(_options.AssetDirectory, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(candidate))
        {
            // ★ 这条日志是验收时的关键判据：客户端拉了我们没有的文件
            Log.Warn($"[http] 资源不存在 {rel}（找 {candidate}）");
            deny = (404, "Not Found", Array.Empty<byte>(), "application/octet-stream");
            return true;
        }

        full = candidate;
        return true;
    }

    /// <summary>
    /// 流式发送一个资源文件：先写头（含准确的 <c>Content-Length</c>），再用固定大小的
    /// 缓冲区把文件内容拷到 socket。**内存占用与文件大小无关**（常数 80 KB）。
    ///
    /// <para>★ 为什么不用 <c>File.ReadAllBytes</c>（第七轮实测的坑）：
    /// 148 MB 的 APK 会一次性进堆，撞上 <c>systemd</c> 的 <c>MemoryHigh=768M</c>
    /// ⇒ 内核持续回收 + 节流（<c>memory.events</c> 的 <c>high</c> 计数到 3968）
    /// ⇒ 下载从 11 MB/s 掉到 ~480 KB/s。改流式后不再触发。</para>
    ///
    /// <para>客户端断开（关游戏 / 超时）会抛 <c>IOException: Broken pipe</c>，
    /// 这是**正常情况**，只记一条 debug 级日志，不当错误。</para>
    /// </summary>
    private static async Task WriteFileResponseAsync(NetworkStream stream, string full)
    {
        var fi = new FileInfo(full);
        string rel = fi.Name;
        try
        {
            await using var fs = new FileStream(
                full, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);

            long len = fs.Length;
            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/octet-stream\r\n" +
                $"Content-Length: {len}\r\n" +
                "Connection: close\r\n" +
                "\r\n");
            await stream.WriteAsync(head);
            await fs.CopyToAsync(stream, 81920);
            Log.Info($"[http] 发资源 {rel}（{len} 字节，流式）");
        }
        catch (IOException e)
        {
            // 客户端提前断开：常见且无害（原厂客户端下载超时就会重试）
            Log.Info($"[http] 资源传输中断 {rel}：{e.Message}");
        }
        catch (Exception e)
        {
            Log.Warn($"[http] 读资源失败 {rel}：{e.Message}");
        }
    }

    private static JsonObject NoUpdate() => new()
    {
        ["msg"] = "0",
        ["urllist"] = new JsonObject
        {
            ["updatever"] = "",
            ["updateurl"] = new JsonArray(),
            ["platform"] = "",
            ["envir"] = "",
            ["buildversion"] = 0,
            ["norestartversion"] = 0,
            ["apklist"] = new JsonObject(),
            // ★★★ `crc` 必须**保持空对象** —— 这是热更新能成立的前提之一。
            //
            // 客户端 Game.Assets.LoadBundleOne（RVA 0x176007C）对 BundleEncrypt 名单内的包
            // （只有 lua.u 与 setting.u，见 NoticeNode..cctor 0x21D42AC 的两个字面量）
            // 是这样走的：
            //     if (BundleEncrypt.names.Contains(name))        0x17602A8
            //         data = File.ReadAllBytes(path)             0x17602C8
            //         if (EntryMgr.CheckBundleCRC(name, data))   0x1760300
            //             BundleEncrypt.Decrypt(data)            0x176032C   ← 解密只在这里
            //         AssetBundle.LoadFromMemory(data)
            //
            // 而 EntryMgr.CheckBundleCRC（RVA 0x21D012C）的判据链是：
            //     if (!BundleEncrypt.names.Contains(name)) return true;      0x21D0200
            //     crc = Utils.CalcCRC32(data).ToString();                    0x21D0244
            //     dict = isNewCheckUpdate ? serverminimal.crc : minimal.CRC; 0x21D02F8 分支
            //     if (dict == null)             return true;                 0x21D0384 cbz
            //     if (!dict.ContainsKey(name))  return true;                 0x21D03F0 tbz
            //     return dict[name] == crc;                                  0x21D0594
            //
            // 本 APK 的 package.txt 里 isNewCheckUpdate=true ⇒ 取的正是**这里下发的 crc**。
            // ⇒ 空对象 ⇒ ContainsKey 为假 ⇒ 恒 return true ⇒ 解密照做。
            // ⇒ 反过来：**一旦在这里登记 lua.u/setting.u 的 CRC**，热更换包后 CRC 不匹配
            //    ⇒ CheckBundleCRC 返回 false ⇒ **跳过解密** ⇒ 把加密包当明文
            //    ⇒ LoadFromMemory 失败、游戏起不来。所以这张表**永远留空**。
            // 回归闸：HotfixDownlinkTests.CheckUpdate_CrcDictionary_MustStayEmpty。
            ["crc"] = new JsonObject(),
        },
    };

    private void LogRequest(HttpRequest req)
    {
        if (req.Body.Length > 0)
            Log.Info($"[http] {req.Method} {req.Path} query={req.RawQuery} body={Encoding.UTF8.GetString(req.Body)}");
        else
            Log.Info($"[http] {req.Method} {req.Path} query={req.RawQuery}");
    }

    // ---------- 极简 HTTP/1.1 解析 ----------

    private static readonly byte[] HeaderTerminator = Encoding.ASCII.GetBytes("\r\n\r\n");

    private sealed class HttpRequest
    {
        public string Method = "";
        public string Path = "";
        public string RawQuery = "";
        public Dictionary<string, string> Query = new();
        public byte[] Body = [];
    }

    private static async Task<HttpRequest?> ReadRequestAsync(
        NetworkStream stream, List<byte> buffer, bool readBody)
    {
        // 1) 读到头部结束 \r\n\r\n
        while (!Contains(buffer, HeaderTerminator))
        {
            var tmp = new byte[8192];
            int n = await stream.ReadAsync(tmp);
            if (n <= 0)
                return null;
            buffer.AddRange(tmp.AsSpan(0, n).ToArray());
        }

        int headerEnd = IndexOf(buffer, HeaderTerminator) + HeaderTerminator.Length;
        var headText = Encoding.ASCII.GetString(buffer.GetRange(0, headerEnd).ToArray());
        var lines = headText.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // 2) 请求行: METHOD SP path SP HTTP/x.y
        var req = new HttpRequest();
        var first = lines[0].Split(' ');
        req.Method = first.Length > 0 ? first[0] : "?";
        string target = first.Length > 1 ? first[1] : "/";
        int q = target.IndexOf('?');
        req.Path = q >= 0 ? target[..q] : target;
        req.RawQuery = q >= 0 ? target[(q + 1)..] : "";
        req.Query = ParseQuery(req.RawQuery);

        // 3) Content-Length
        int contentLength = 0;
        foreach (var line in lines)
        {
            int c = line.IndexOf(':');
            if (c <= 0)
                continue;
            var name = line[..c].Trim();
            var value = line[(c + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(value, out contentLength);
        }

        if (!readBody || contentLength <= 0)
        {
            req.Body = [];
            return req;
        }

        // 4) 收齐 body
        while (buffer.Count < headerEnd + contentLength)
        {
            var tmp = new byte[8192];
            int n = await stream.ReadAsync(tmp);
            if (n <= 0)
                return null;
            buffer.AddRange(tmp.AsSpan(0, n).ToArray());
        }

        req.Body = buffer.GetRange(headerEnd, contentLength).ToArray();
        return req;
    }

    private static Dictionary<string, string> ParseQuery(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw))
            return dict;
        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.Length == 0)
                continue;
            int eq = pair.IndexOf('=');
            if (eq < 0)
            {
                dict[Uri.UnescapeDataString(pair)] = "";
            }
            else
            {
                var k = Uri.UnescapeDataString(pair[..eq]);
                var v = Uri.UnescapeDataString(pair[(eq + 1)..]);
                dict[k] = v;
            }
        }
        return dict;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream, int status, string statusText, byte[] body, string contentType)
    {
        var head = $"HTTP/1.1 {status} {statusText}\r\n" +
                   $"Content-Type: {contentType}\r\n" +
                   $"Content-Length: {body.Length}\r\n" +
                   "Connection: close\r\n" +
                   "\r\n";
        var headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes);
        if (body.Length > 0)
            await stream.WriteAsync(body);
    }

    private static bool Contains(List<byte> buffer, byte[] pattern)
    {
        return IndexOf(buffer, pattern) >= 0;
    }

    private static int IndexOf(List<byte> buffer, byte[] pattern)
    {
        if (pattern.Length == 0)
            return 0;
        for (int i = 0; i + pattern.Length <= buffer.Count; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (buffer[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }
}
