using System.Text.Json.Nodes;
using SssjServer.Core;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 三条常驻小命令：推送开关（353）/ 咖啡馆少女体力同步（240）/ 随机事件完成（171）。
///
/// <para><b>为什么合在一个 feature 里</b></para>
/// 三条都是「单命令、无子系统、无配置表」的边角功能，各自不足以独立成域；
/// 但它们**都有活跃上行发送点、都属常驻系统**（设置页 / 咖啡馆 / 主城随机事件），
/// 玩家每天都会触发。放一起便于维护。
///
/// <para><b>★ 同批候选里被判死的三条（别再花时间）</b></para>
/// <list type="bullet">
///   <item><b>180 <c>ReqBuyJiJin</c> 成长基金</b>：上行点活着（<c>UI_NormalActivity.lua:453</c>
///     等 3 处、且走 <c>MoneyLogic:UseMoneyDialog</c> 是**游戏内货币不是真钱**），
///     但它归属 <c>ActivityList</c> 的**活动 25**，客户端自报
///     <c>nEndTime = 1514761200</c>（2018-01-01）⇒ **已过期 8 年**，福利页不会显示它。</item>
///   <item><b>220 <c>LvFirstPassReq</c></b>：唯一上行点 <c>UI_RecentInfo.lua:54</c>
///     **被 <c>--</c> 注释掉**了 ⇒ 客户端永不发送。</item>
///   <item><b>201 <c>UnLockGirlLevel</c></b>：<c>GirlCommon.lua:1289</c> 是
///     <c>LuaCall:<b>CallClient</b></c>（纯下行推送通道），**不是上行命令**。</item>
/// </list>
/// </summary>
public sealed class MiscCommandsFeature : IGameFeature
{
    /// <summary>171 <c>RequesEventComplete</c>：客户端完成随机事件。</summary>
    public const int CmdEventComplete = 171;

    /// <summary>240 <c>CafeGirlVigor</c>：咖啡馆少女体力自动增减的同步。</summary>
    public const int CmdCafeGirlVigor = 240;

    /// <summary>353 <c>SetPushEnable</c>：远程推送的四个静默选项。</summary>
    public const int CmdSetPushEnable = 353;

    /// <summary>存档键（<c>Player.Extra</c>）：推送开关。</summary>
    public const string PushExtraKey = "pushenable";

    // Legacy constants retained for old-save diagnostics only. Recovery now uses
    // the client's group 41 girl-id clocks; offset 17 is no longer written.
    public const int GirlVarGroup = 3;
    public const int GirlVigorOffset = 17;

    private GameFeatureContext _ctx = null!;
    private GirlMoodRecovery _mood = null!;

    public string Name => "杂项常驻（LuaCall 171 随机事件完成 / 240 少女体力 / 353 推送开关）";

    /// <summary>本模块注册的全部 LuaCall id（供测试与文档校对）。</summary>
    internal static IReadOnlyList<int> HandledCmds { get; } = new[]
    {
        CmdEventComplete, CmdCafeGirlVigor, CmdSetPushEnable,
    };

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _mood = new GirlMoodRecovery(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.OnPlayerPrepare((session, player) =>
        {
            if (_mood.Refresh(null, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds())) ctx.Store.Save(player);
        });
        ctx.MapLuaCall(CmdEventComplete, OnEventComplete);
        ctx.MapLuaCall(CmdCafeGirlVigor, OnCafeGirlVigor);
        ctx.MapLuaCall(CmdSetPushEnable, OnSetPushEnable);
    }

    // ================================================================ 171

    /// <summary>
    /// 171 随机事件完成。上行 <c>{}</c>（<c>RandomEvent.lua:314</c> <c>EventComplete()</c>）。
    ///
    /// <para><b>回包契约</b></para>
    /// <code>
    /// RandomEvent.lua:318  function RandomEvent:OnEventComplete(tbParam)
    /// :319     if tbParam.ret == false then ... return end   -- ★ 只判 == false
    /// :323     self:Reward(); self:RefreshEntrustScore()
    /// </code>
    /// ⇒ 回 <c>{ret = true}</c> 才会发奖；<c>ret = false</c> 是「服务端拒绝」。
    /// ★ 注意判据是 <c>== false</c>（**不是** falsy）⇒ 不回包 / 回 <c>{}</c> 时
    /// <c>tbParam.ret</c> 是 nil、`nil == false` 为假 ⇒ **也会走发奖分支**。
    /// 但仍然必须回包：不回包客户端连 <c>OnEventComplete</c> 都不会进。
    ///
    /// ★ 奖励由**客户端自己**发（<c>self:Reward()</c> 读本地随机事件配置），
    /// 服务端只做「允许/拒绝」的闸门 —— 这与本仓多数命令相反，别去查发奖表。
    /// </summary>
    internal Task OnEventComplete(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        // 本服不限制随机事件（没有服务端侧的事件状态机）⇒ 一律放行
        Log.Info($"[session {session.Id}] RequesEventComplete(171) 随机事件完成 → ret=true" +
                 "（★ 奖励由客户端 RandomEvent:Reward() 自己发，服务端只做闸门）");

        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["ret"] = true });
        return Task.CompletedTask;
    }

    // ================================================================ 240

    /// <summary>
    /// 240 咖啡馆少女体力同步。上行 <c>{girlid = [id, id, …]}</c>
    /// （<c>GirlVigourManager.lua:286</c>，带 Blocking=true）。
    ///
    /// <para><b>★★★ 这条命令<u>不能回包</u></b></para>
    /// <c>LuaCall.lua</c> 的客户端分支**整段是空的**（只有一段注释、零语句）：
    /// <code>
    /// RegisterCMD(LuaCall.CafeGirlVigor, function(sCmd, tbParam)
    ///     if MODULE_SERVER then
    ///         local tbRet = CafeLogic:OnCMDAddorReduceGirlVigor(tbParam)
    ///     else
    ///         --  数据格式示例 ... （注释，无代码）
    ///     end
    /// end)
    /// </code>
    /// ⇒ 服务端**只落库、不回包**。回了也没人处理（客户端 else 分支什么都不做）。
    /// ★ 注意 <c>MODULE_SERVER</c> 分支也**没有 <c>CallClient</c>** —— 原服同样不回。
    ///
    /// 客户端只检测是否到时并上报id，服务端计算恢复并推少女/任务值。
    /// </summary>
    internal Task OnCafeGirlVigor(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var ids = ReadGirlIds(param);
        if (ids.Count == 0)
        {
            Log.Warn($"[session {session.Id}] CafeGirlVigor(240) girlid 为空或格式非法：" +
                     $"{param?.ToJsonString() ?? "null"}（★ 本命令不回包）");
            return Task.CompletedTask;
        }

        // The client reports IDs only; authoritative recovery changes Moodvalue
        // and pushes normal girl/task updates. There is no LuaCall 240 reply.
        if (ids.Any(id => player.GetGirl((uint)id) is not null)
            && _mood.Refresh(session, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            _ctx.Store.Save(player);
        return Task.CompletedTask;
    }

    // ================================================================ 353

    /// <summary>
    /// 353 远程推送开关。上行 <c>{Switch, Silence, GetEnergy, EnergyCover}</c>（各 0/1）
    /// （<c>Notification.lua:142..149</c> <c>NotificationSetting:SendMessage</c>）。
    ///
    /// <para><b>★ 回包必须是「无参」</b></para>
    /// <code>
    /// LuaCall.lua:  RemotePush:SetPushEnable(tbParam); LuaCall:CallClient(sCmd)   -- ★ 无第二参
    /// 客户端 else:  NotificationSetting.ClearDelaySendState()                     -- 不读任何字段
    /// </code>
    /// ⇒ 回**空对象**即可（客户端只用「收到回包」这件事本身来清延迟发送态）。
    /// ★ 不回包的后果：<c>ClearDelaySendState</c> 不执行 ⇒ 客户端会**反复重发**这条命令。
    /// </summary>
    internal Task OnSetPushEnable(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var o = param as JsonObject;
        var state = new JsonObject
        {
            ["Switch"] = ReadFlag(o, "Switch"),
            ["Silence"] = ReadFlag(o, "Silence"),
            ["GetEnergy"] = ReadFlag(o, "GetEnergy"),
            ["EnergyCover"] = ReadFlag(o, "EnergyCover"),
        };
        player.Extra[PushExtraKey] = state;
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] SetPushEnable(353) 推送开关：" +
                 $"总开关={state["Switch"]} 夜间静默={state["Silence"]} " +
                 $"体力领取={state["GetEnergy"]} 电力恢复={state["EnergyCover"]}" +
                 "；★ 回**无参**空对象（客户端只靠「收到回包」清延迟态，不回会反复重发）");

        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    // ================================================================ 内部

    /// <summary>
    /// 读 <c>{girlid: [...]}</c>。
    /// ★ <c>JsonNode["k"]</c> 对 <c>JsonArray</c> 会抛（AGENTS.md §5）⇒ 先判类型；
    /// 且客户端可能只发单个数字（而非数组），两种都要吃下。
    /// </summary>
    internal static List<int> ReadGirlIds(JsonNode? param)
    {
        var result = new List<int>();
        if (param is not JsonObject o || o["girlid"] is not JsonNode node)
            return result;

        if (node is JsonArray arr)
        {
            foreach (var el in arr)
            {
                int v = ReadInt(el);
                if (v > 0)
                    result.Add(v);
            }
        }
        else
        {
            int v = ReadInt(node);
            if (v > 0)
                result.Add(v);
        }
        return result;
    }

    /// <summary>读 0/1 开关；缺省按**开启**（客户端默认值就是 true）。</summary>
    private static int ReadFlag(JsonObject? o, string key)
    {
        if (o?[key] is not JsonValue v)
            return 1;
        if (v.TryGetValue<int>(out int n))
            return n != 0 ? 1 : 0;
        if (v.TryGetValue<bool>(out bool b))
            return b ? 1 : 0;
        return 1;
    }

    /// <summary>
    /// 读整数。★ 必须把 <c>int</c>/<c>uint</c>/<c>long</c> 都试一遍 ——
    /// <c>JsonValue.TryGetValue&lt;T&gt;</c> 按装箱类型精确匹配（第十六轮的坑，AGENTS.md §5）。
    /// </summary>
    private static int ReadInt(JsonNode? n)
    {
        if (n is not JsonValue v)
            return 0;
        if (v.TryGetValue<int>(out int x))
            return x;
        if (v.TryGetValue<uint>(out uint u))
            return (int)Math.Min(u, int.MaxValue);
        if (v.TryGetValue<long>(out long l))
            return (int)Math.Clamp(l, 0, int.MaxValue);
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out int m) ? m : 0;
    }
}
