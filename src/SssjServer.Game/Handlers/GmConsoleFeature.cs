using System.Text;
using System.Text.Json.Nodes;
using SssjServer.Core;

namespace SssjServer.Game.Handlers;

/// <summary>
/// GM 控制台：把一段 Lua 源码推给客户端执行，并把客户端的 print 输出捞回服务端日志。
///
/// 取证链：
///  * <c>LuaCall.DoLuaString = 999999</c>「服务器指定客户端执行Lua脚本」（LuaCall.lua:489）；
///    客户端分支（LuaCall.lua:3520-3526）：
///    <code>else GM.DoCommand("CL", tbParam.luaStr) end</code>
///  * <c>GM.DoCommand("CL", szCmd)</c>（GM.lua:81-106）：临时替换 <c>_G.print</c> 收集输出 →
///    <c>xpcall(function() assert(load(szCmd))() end, ...)</c> 执行 → 还原 print →
///    <c>DebugEditor.SendResult(table.concat(tbOutput, '\n'))</c>。
///    注意它同时调用了原始 print，所以**输出一定会进 logcat**（`adb logcat | grep Unity`）。
///  * <c>Game.DebugEditor</c>（dump.cs:581373）是客户端**自带的 TCP 调试服务器**
///    （<c>const int Port = 12346</c>），<c>SendResult</c> 写回最近连上的调试客户端 socket，
///    不经过游戏服务器；没有调试客户端连时就是空操作。
///    因此本控制台的「回显」通道是 logcat，不是网络。
///  * 另一条方向：服务端可以主动推 <c>S2C.DebugEditor{szRet=...}</c>（S2CCall.lua:74），
///    客户端会把 szRet 转给 <c>DebugEditor.SendResult</c>。这里也注册了同名 sFunction 的上行处理，
///    以防客户端（`GM.DoCommand` 的 "AU"/"GS" 分支，GM.lua:107）把 GM 命令发到服务端来。
///
/// 用法：往 <c>ServerOptions.GmFile</c>（默认 <c>work/gm.lua</c>）写 Lua 源码，服务端检测到文件
/// 变化就推给所有在线会话执行。例如：
/// <code>
/// echo 'print("vigour=", me.vigour)' > work/gm.lua
/// </code>
/// 这是本项目最强的「客户端内省」手段：能直接读客户端状态、驱动 UI、复现结算流程，
/// 不必靠 adb 点屏幕。
/// </summary>
public sealed class GmConsoleFeature : IGameFeature
{
    /// <summary>LuaCall.DoLuaString = 999999（LuaCall.lua:489）。</summary>
    public const int DoLuaStringCmd = 999999;

    public string Name => "GM 控制台（DoLuaString 999999 下发客户端 Lua）";

    public void Install(GameFeatureContext ctx)
    {
        GmConsole.Attach(ctx.World, ctx.LuaCalls);
        var rotation = Config.GachaRotation.For(ctx.Options.SettingDirectory);
        ctx.OnPlayerLogin((session, player) =>
        {
            // A persisted disabled plan must also restore dates on reconnect
            // when the client Lua VM survived the server restart.
            if (rotation.HasConfiguration || rotation.Read().Enabled)
                ctx.LuaCalls.CallClient(session, DoLuaStringCmd, new { luaStr = rotation.ClientLua() });
        });

        // 上行的 sFunction="DebugEditor"（原服 GM 通道，参数是 Lua 源码）由
        // GmDebugEditorFeature 用 MapRawFunction 处理 —— 那条通道的 sJsonParam 不是 JSON，
        // 用 MapFunction 注册的话会在 JsonNode.Parse 处就失败。

        // 上行（正常不会出现）：客户端把 999999 发回来
        ctx.MapLuaCall(DoLuaStringCmd, (session, cmd, param) =>
        {
            Log.Info($"[session {session.Id}] [gm] 收到客户端 DoLuaString 回声：{param?.ToJsonString()}");
            return Task.CompletedTask;
        });
    }
}

/// <summary>
/// GM 控制台的推送端。由 <see cref="GmConsoleFeature"/> 在装配时注入 World / LuaCallRouter，
/// 供 App 层的文件监视循环调用。
/// </summary>
public static class GmConsole
{
    private static World? _world;
    private static LuaCallRouter? _luaCalls;

    internal static void Attach(World world, LuaCallRouter luaCalls)
    {
        _world = world;
        _luaCalls = luaCalls;
    }

    /// <summary>是否已装配（未装配时 <see cref="Broadcast"/> 直接返回 0）。</summary>
    public static bool Ready => _world is not null && _luaCalls is not null;

    /// <summary>
    /// 把一段 Lua 源码推给所有在线会话执行（pf 1024，sCmd=999999，tbParam={luaStr=...}）。
    /// 返回推送到的会话数。客户端的 print 输出去 logcat。
    /// </summary>
    public static int Broadcast(string luaStr)
    {
        var world = _world;
        var luaCalls = _luaCalls;
        if (world is null || luaCalls is null)
        {
            Log.Warn("[gm] 控制台未装配（GmConsoleFeature 未安装），忽略本次命令");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(luaStr))
            return 0;

        int sent = 0;
        foreach (var session in world.OnlineSessions())
        {
            if (session is not SessionAdapter adapter)
                continue;
            luaCalls.CallClient(adapter.Inner, GmConsoleFeature.DoLuaStringCmd, new { luaStr });
            sent++;
        }
        Log.Info($"[gm] 已下发 Lua（{Encoding.UTF8.GetByteCount(luaStr)} 字节）到 {sent} 个会话");
        return sent;
    }
}
