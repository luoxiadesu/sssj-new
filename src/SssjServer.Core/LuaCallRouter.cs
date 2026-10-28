using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SssjServer.Core;

/// <summary>LuaCall 命令处理委托。param 为 sJsonParam 里的 tbParam 节点（可空）。</summary>
public delegate Task LuaCallHandler(ClientSession session, int cmd, JsonNode? param);

/// <summary>
/// 字符串脚本命令处理委托（<c>sFunction</c> != "LuaCall" 的那一族，见 extracted/lua/S2CCall.lua）。
/// param 是 sJsonParam 整体反序列化出来的节点 —— 这一族**没有** sCmd/tbParam 外壳，
/// 客户端 <c>SendCmd(sCmd, tbParam)</c>（Lib.lua:378）直接把 tbParam 的 JSON 放进 sJsonParam。
/// </summary>
public delegate Task ScriptCallHandler(ClientSession session, string function, JsonNode? param);

/// <summary>
/// 「不解析 JSON」的字符串命令处理函数签名：<paramref name="rawParam"/> 是 <c>sJsonParam</c> 原文。
/// 用于原服 GM 那种把 Lua 源码塞进 sJsonParam 的命令，见 <see cref="LuaCallRouter.MapRawFunction"/>。
/// </summary>
public delegate Task RawScriptCallHandler(ClientSession session, string function, string rawParam);

/// <summary>
/// 「同一个 sFunction 的协处理器」签名（见 <see cref="LuaCallRouter.AddFunctionCoHandler"/>）。
/// 返回值表示**这条命令是否由我处理**：<c>false</c> = 不是我的，路由继续问下一个。
/// </summary>
public delegate Task<bool> ScriptCoHandler(ClientSession session, string function, JsonNode? param);

/// <summary>
/// LuaCall 通用 RPC 路由（AGENTS.md §2.4 / docs 02 §3）。
///
/// pf=1022 C2SCallReq { sFunction="LuaCall", sJsonParam={"sCmd":<int>,"tbParam":{...}} }。
/// 回客户端：pf=1024 NtfS2CCall{ sFunction="LuaCall", sJsonParam={"sCmd":cmd,"tbParam":param} }，
/// 超过 LuaCall.ProSize(64000) 分片。
///
/// ★ 生成的 NtfS2CCall 类型由 task A 提供，本类通过 <see cref="NtfS2CCallSender"/> 接缝发送，
/// 不直接依赖任何生成类型 —— Game 层在启动时注入真实序列化实现。
/// </summary>
public sealed class LuaCallRouter
{
    // Request-local context: settlement acknowledgements must clear the client's
    // cached mission before dispatching to the ordinary LuaCall consumer.
    private readonly AsyncLocal<(ClientSession Session, int Command)?> _settlementReply = new();

    public async Task DispatchSettlementAsync(ClientSession session, int command, JsonNode? parameter)
    {
        var previous = _settlementReply.Value;
        _settlementReply.Value = (session, command);
        try
        {
            await DispatchAsync(session, LuaCallFunction,
                new JsonObject { ["sCmd"] = command, ["tbParam"] = parameter?.DeepClone() }.ToJsonString());
        }
        finally { _settlementReply.Value = previous; }
    }

    /// <summary>分片阈值（客户端 LuaCall.ProSize）。</summary>
    public const int ProSize = 64000;

    /// <summary>NtfS2CCall 所在 pf。</summary>
    public const int NtfS2CCallPf = 1024;

    /// <summary>客户端 <c>Game.Player.CallServer("LuaCall", ...)</c> 用的固定函数名。</summary>
    public const string LuaCallFunction = "LuaCall";

    /// <summary>
    /// ★ 下行 JSON 的序列化选项：必须用 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>。
    ///
    /// 原因（真机实测踩出来的）：System.Text.Json 默认编码器把 <c>"</c> 转义成 <c>\u0022</c>、
    /// 把中文等非 ASCII 转成 <c>\uXXXX</c>，而客户端的 Lua JSON 解码器
    /// （<c>extracted/lua/Json.lua:252</c>）只认这几种转义：
    /// <code>charstounescape = "\"'\\bfnrt/"</code>
    /// —— **没有 \u**。于是 <c>Json.Decode</c> 抛错，被
    /// <c>S2C.ProcCmd</c> 的 pcall 吞掉，客户端只打印一行
    /// <c>S2C.ProcCmd Error:: LuaCall</c> 然后 return，整包静默丢弃。
    /// 症状：服务端日志一切正常（=&gt; pf=1024 发出去了），客户端却毫无反应。
    /// UnsafeRelaxedJsonEscaping 只转义 <c>"</c>→<c>\"</c> 与 <c>\</c>、控制字符，
    /// 非 ASCII 直接输出 UTF-8 原文，正好落在客户端解码器支持的集合内。
    /// </summary>
    internal static readonly JsonSerializerOptions ClientJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<int, LuaCallHandler> _map = new();
    private readonly Dictionary<string, ScriptCallHandler> _funcMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RawScriptCallHandler> _rawFuncMap = new(StringComparer.Ordinal);

    /// <summary>sFunction → 协处理器列表（见 <see cref="AddFunctionCoHandler"/>）。</summary>
    private readonly Dictionary<string, List<ScriptCoHandler>> _funcCoHandlers = new(StringComparer.Ordinal);

    /// <summary>
    /// 发送一条 NtfS2CCall 的接缝：入参是 (sFunction, 本类算好的 sJsonParam 原始字节)，
    /// Game 层把它包成真实生成的 NtfS2CCall（pf=1024）后推给会话。
    /// sFunction 通常是 "LuaCall"（数字命令族），也可以是 S2CCall.lua 里的字符串命令名
    /// （如 "ChapterMsg"）—— 客户端 <c>S2C.ProcCmd(sFunction, sJsonParam)</c> 按它分发。
    /// 未注入时的默认实现：把 sJsonParam 字节当作 pf=1024 的原始 payload 直接推送
    /// （占位/调试用途，不是合法 NtfS2CCall proto）。
    /// </summary>
    public Func<ClientSession, string, byte[], Task>? NtfS2CCallSender { get; set; }

    public void Map(int cmd, LuaCallHandler handler)
    {
        _map[cmd] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>注册一个字符串脚本命令（sFunction）处理函数，例如 "ChapterMsg"。</summary>
    public void MapFunction(string function, ScriptCallHandler handler)
    {
        if (string.IsNullOrEmpty(function))
            throw new ArgumentException("function 不能为空", nameof(function));
        _funcMap[function] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>
    /// 给一个**已注册**的 sFunction **追加**一个协处理器（不覆盖原 handler）。
    ///
    /// <para><b>★★ 存在的理由：一个 sFunction 被多个功能域瓜分</b></para>
    /// <see cref="MapFunction"/> 是覆盖式的（<c>_funcMap[function] = handler</c>），
    /// 所以两个 <c>IGameFeature</c> 各自 <c>MapFunction("GirlLogic", …)</c> 时，
    /// **后装配的会把前一个整域吃掉** —— 症状是「另一个域的所有命令都不响应，
    /// 连 unhandled 日志都没有」（路由压根没到那个域）。
    /// 现网 <c>GirlLogic</c> 就是这种情况：特训（<c>GirlTrainFeature</c>）与
    /// 好感度/剧情（<c>GirlLoveFeature</c>）共用同一个 sFunction。
    ///
    /// <para><b>语义</b></para>
    /// 分发时**协处理器优先**：按注册顺序依次问，某个返回 <c>true</c> 即结束；
    /// 全都返回 <c>false</c> 时才交给主 handler（<see cref="MapFunction"/> 注册的那个），
    /// 由它处理自己的命令或打 unhandled 日志。
    ///
    /// 这个顺序而不是「主 handler 优先」的原因：主 handler 的签名是
    /// <see cref="ScriptCallHandler"/>（返回 <c>Task</c>），**无法表达「这条不是我的」**，
    /// 所以只能让能表达的一方先走。
    ///
    /// <para><b>★ 为什么不用静态表</b></para>
    /// 本轮先用「功能域里的 <c>static</c> 字典」实现过，**在并行单测下会串**：
    /// 每条用例新建一个 <c>GameModule</c>，后装配的实例把静态键覆盖成自己的闭包，
    /// 于是别的 harness 的命令被路由到**另一个会话的 feature 实例**（其 store/ctx 都不同），
    /// 表现为「随机几条用例没有下行」。协处理器挂在 <b>router 实例</b>上才与 module 同生命周期。
    /// </summary>
    public void AddFunctionCoHandler(string function, ScriptCoHandler handler)
    {
        if (string.IsNullOrEmpty(function))
            throw new ArgumentException("function 不能为空", nameof(function));
        ArgumentNullException.ThrowIfNull(handler);
        if (!_funcCoHandlers.TryGetValue(function, out var list))
            _funcCoHandlers[function] = list = new List<ScriptCoHandler>();
        list.Add(handler);
    }

    /// <summary>
    /// 注册一个**不解析 JSON**的字符串命令处理函数，拿到的是 <c>sJsonParam</c> 原文。
    ///
    /// 存在的理由：<c>S2CCall.lua</c> 那一族命令的 <c>sJsonParam</c> 绝大多数是 JSON，
    /// 但**原服的 GM 通道不是** —— 客户端
    /// <c>Game.Player.CallServer('DebugEditor', 'GM:UnlockAllChapter();')</c>
    /// （取证 <c>AutoTestCase.lua:190..539</c>、<c>UI_GM.lua:587/:596/:719</c>）
    /// 直接把一段 **Lua 源码**放进 sJsonParam，交给原服 eval。
    /// 这种参数过 <c>JsonNode.Parse</c> 必然抛，走
    /// <see cref="MapFunction"/> 的话 handler 根本收不到（只会打一条解析失败的 ERROR）。
    ///
    /// 优先级高于 <see cref="MapFunction"/>：同名时 raw 处理器胜出。
    /// </summary>
    public void MapRawFunction(string function, RawScriptCallHandler handler)
    {
        if (string.IsNullOrEmpty(function))
            throw new ArgumentException("function 不能为空", nameof(function));
        _rawFuncMap[function] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>该 sFunction 是否已注册处理函数（"LuaCall" 不在此表内）。</summary>
    /// <summary>
    /// 该 sFunction 是否已注册处理函数（"LuaCall" 不在此表内）。
    /// ★ 必须同时查 raw 表 —— <c>GameModule.HandleC2SCall</c> 用这个判定决定「分发还是打 unhandled」，
    /// 漏掉 raw 表会让 <see cref="MapRawFunction"/> 注册的命令（原服 GM 的 <c>DebugEditor</c>）
    /// 永远走不到 handler。
    /// </summary>
    public bool HasFunction(string function)
        => _funcMap.ContainsKey(function) || _rawFuncMap.ContainsKey(function);

    public async Task DispatchAsync(ClientSession session, string function, string jsonParam)
    {
        if (!string.Equals(function, LuaCallFunction, StringComparison.Ordinal))
        {
            // ① 原始字符串处理器优先（sJsonParam 不是 JSON 的命令，如原服 GM 的 "DebugEditor"）
            if (_rawFuncMap.TryGetValue(function, out var rawHandler))
            {
                await rawHandler(session, function, jsonParam);
                return;
            }

            // 字符串脚本命令族（S2CCall.lua）：sJsonParam 就是 tbParam 本体，没有 sCmd 外壳
            if (_funcMap.TryGetValue(function, out var funcHandler))
            {
                JsonNode? node = null;
                try
                {
                    node = JsonNode.Parse(jsonParam);
                }
                catch (Exception e)
                {
                    Log.Error($"[session {session.Id}] 解析 sFunction={function} 的 sJsonParam 失败: {e.Message} json={jsonParam}");
                    return;
                }

                // ★ 协处理器优先（同一个 sFunction 被多个功能域瓜分时用，见 AddFunctionCoHandler）
                if (_funcCoHandlers.TryGetValue(function, out var coHandlers))
                {
                    foreach (var co in coHandlers)
                    {
                        if (await co(session, function, node))
                            return;
                    }
                }

                await funcHandler(session, function, node);
                return;
            }

            Log.Warn($"[session {session.Id}] unhandled sFunction={function} param={jsonParam}");
            return;
        }

        int cmd = -1;
        JsonNode? param = null;
        try
        {
            var root = JsonNode.Parse(jsonParam);
            cmd = root?["sCmd"]?.GetValue<int>() ?? -1;
            param = root?["tbParam"];
        }
        catch (Exception e)
        {
            Log.Error($"[session {session.Id}] 解析 sJsonParam 失败: {e.Message} json={jsonParam}");
            return;
        }

        if (_map.TryGetValue(cmd, out var handler))
        {
            await handler(session, cmd, param);
            return;
        }

        Log.Warn($"[session {session.Id}] unhandled LuaCall cmd={cmd} param={jsonParam}");
    }

    /// <summary>
    /// 回客户端：pf=1024 NtfS2CCall{ sFunction="LuaCall", sJsonParam={"sCmd":cmd,"tbParam":param} }。
    /// 超过 64000 字节按 AGENTS.md §2.4 分片：每片 = Json{CallTime,CallEnd,CallCmd} .. <原文切片>，
    /// 无分隔符直接拼接；只有最后一片 CallEnd=1。
    /// </summary>
    public void CallClient(ClientSession session, int cmd, object? param)
    {
        if (_settlementReply.Value is { } context && ReferenceEquals(context.Session, session) && context.Command == cmd)
        {
            param = new { sCmd = cmd, tbParam = param };
            cmd = 252;
        }

        Task Send(byte[] sJsonParam)
        {
            var sender = NtfS2CCallSender;
            if (sender != null)
                return sender(session, LuaCallFunction, sJsonParam);
            return Task.Run(() => session.PushRaw(NtfS2CCallPf, sJsonParam));
        }

        var fullBytes = Encoding.UTF8.GetBytes(BuildFullJson(cmd, param));
        long unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (fullBytes.Length <= ProSize)
        {
            Send(fullBytes); // 单片：sJsonParam 就是完整 {"sCmd":..,"tbParam":..}，无 header
            return;
        }

        foreach (var (header, slice) in BuildChunks(cmd, fullBytes, unix))
        {
            var chunk = new byte[header.Length + slice.Length];
            header.CopyTo(chunk, 0);
            slice.CopyTo(chunk.AsSpan(header.Length));
            Send(chunk);
        }
    }

    /// <summary>
    /// 回客户端：pf=1024 NtfS2CCall{ sFunction=&lt;function&gt;, sJsonParam=&lt;param 的 JSON&gt; }。
    ///
    /// 这是 S2CCall.lua 那一族字符串命令的下行通道：客户端
    /// <c>Game.Player.OnCallClient</c> → <c>S2C.ProcCmd(sFunction, sJsonParam)</c> →
    /// <c>S2C[sFunction](Json.Decode(sJsonParam))</c>。
    ///
    /// **不分片**：S2C.ProcCmd 只对 sFunction=="LuaCall" 做 {CallTime,CallEnd,CallCmd} 分片重组
    /// （S2CCall.lua:29），其它命令一律整包 Json.Decode，分片会直接解析失败。
    /// </summary>
    public void CallClientFunction(ClientSession session, string function, object? param)
    {
        if (string.Equals(function, LuaCallFunction, StringComparison.Ordinal))
            throw new ArgumentException("LuaCall 请走 CallClient(cmd, param)", nameof(function));

        var json = param is null ? "{}" : JsonSerializer.Serialize(param, ClientJson);
        var bytes = Encoding.UTF8.GetBytes(json);

        var sender = NtfS2CCallSender;
        if (sender != null)
        {
            sender(session, function, bytes);
            return;
        }
        session.PushRaw(NtfS2CCallPf, bytes);
    }

    /// <summary>
    /// 把一个超过 ProSize 的 sJsonParam 原文切成若干 (header, slice) 分片。
    /// header = Json{CallTime,CallEnd,CallCmd}（末片 CallEnd=1），
    /// slice 为原文的连续 64000 字节切片；按序拼接所有 slice 即还原原文。
    /// 供 <see cref="CallClient"/> 使用；internal 暴露以便单测精确校验切片边界。
    /// </summary>
    internal static List<(byte[] Header, byte[] Slice)> BuildChunks(int cmd, byte[] fullBytes, long unix)
    {
        var result = new List<(byte[], byte[])>();
        int chunks = (fullBytes.Length + ProSize - 1) / ProSize;
        for (int i = 0; i < chunks; i++)
        {
            int start = i * ProSize;
            int len = Math.Min(ProSize, fullBytes.Length - start);
            var slice = fullBytes.AsSpan(start, len).ToArray();

            bool last = i == chunks - 1;
            var header = new JsonObject
            {
                ["CallTime"] = unix,
                ["CallEnd"] = last ? 1 : 0,
                ["CallCmd"] = cmd,
            };
            var headerBytes = Encoding.UTF8.GetBytes(header.ToJsonString());

            result.Add((headerBytes, slice));
        }
        return result;
    }

    /// <summary>拼出完整 sJsonParam：{"sCmd":cmd,"tbParam":param}。</summary>
    internal static string BuildFullJson(int cmd, object? param)
    {
        var tb = param is null ? null : JsonSerializer.SerializeToNode(param, ClientJson);
        var root = new JsonObject
        {
            ["sCmd"] = cmd,
            ["tbParam"] = tb,
        };
        // 注意：ToJsonString 也要带 ClientJson，否则 JsonNode 自己会用默认编码器再把 " 变成 \u0022
        return root.ToJsonString(ClientJson);
    }
}
