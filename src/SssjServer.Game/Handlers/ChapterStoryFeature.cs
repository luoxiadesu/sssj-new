using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
namespace SssjServer.Game.Handlers;

/// <summary>Chapter 16 plays both story segments directly and completes through LuaCall 540.</summary>
public sealed class ChapterStoryFeature : IGameFeature
{
    internal const int Command = 540;
    private GameFeatureContext _ctx = null!;
    private CafeTable _cafe = null!;
    private GirlLoveTable _love = null!;
    public string Name => "第16章纯剧情通关（Chapter_StoryPass）";
    // The original callback has no error branch and unconditionally writes 15 locally.
    internal const string CallbackLua = "if ChapterLogic and not ChapterLogic._localStoryReply then local old=ChapterLogic.OnStoryChapterPass ChapterLogic.OnStoryChapterPass=function(self,r) if r and r.nError then Game.MessageBox.Simple(UIText('UI_LevelMap.Lock')) local ui=UI.IsOpen('UI_Cartoon') and UI.GetClass('UI_Cartoon') if ui then ui:CallBack(function() UI.Call('UI_Player','SetBackShowState',true) UI.Open('UI_Background') UI.OpenTop() end) end return end old(self,r) if r and r.nMapId and r.nStar then CS.Game.MapList.ReflashLevelList(r.nMapId,r.nStar) end end ChapterLogic._localStoryReply=true end";
    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _cafe = CafeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _love = GirlLoveTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.MapLuaCall(Command, Complete);
        ctx.OnPlayerLogin((session, player) => ctx.LuaCalls.CallClient(session,
            GmConsoleFeature.DoLuaStringCmd, new { luaStr = CallbackLua }));
    }
    private Task Complete(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            if (param is not JsonObject args || !Read(args, "Chapter", out int chapter)
                || !Read(args, "Index", out int index) || !Read(args, "Difficult", out int diff)
                || chapter != 16 || diff != 1 || index < 1 || index > 255
                || _ctx.Chapters.Get(chapter, index, diff) is not { } level)
                return Reject(session, cmd);
            uint map = level.MapId;
            int previous = player.GetLevelStar(map);
            bool first = MapList.PassCount(previous) == 0;
            if (first)
            {
                var predecessor = index > 1 ? _ctx.Chapters.Get(16, index - 1, 1)
                    : _ctx.Chapters.All.Where(l => l.Chapter == 15 && l.Diff == 1).MaxBy(l => l.Index);
                if (player.Level < level.LevelLimit || predecessor is null
                    || MapList.PassCount(player.GetLevelStar(predecessor.MapId)) == 0)
                    return Reject(session, cmd);
            }
            var rewards = new RewardGrantResult();
            if (first) _ctx.Rewards.Grant(player, level.FirstAward, RewardSource.FirstAward, rewards);
            // Story replay is not a new combat clear and never consumes stamina.
            int composed = MapList.Compose(7, Math.Max(1, MapList.PassCount(previous)));
            player.Levels[map] = composed;
            var coffee = CafeBizFeature.RefreshCoffeeConditions(_cafe, player);
            var stories = GirlLoveFeature.UnlockStories(_love, player);
            _ctx.Store.Save(player);
            GameNotify.PushChapter(session, map, composed);
            GameNotify.PushRewards(session, player, rewards);
            if (coffee.Count > 0) GameNotify.PushTaskValues(session, coffee);
            if (stories.Count > 0) GameNotify.PushTaskValues(session, stories);
            _ctx.LuaCalls.CallClient(session, cmd, new { tbNew = rewards.ToAwardTuples(), nMapId = map, nStar = composed });
        }
        return Task.CompletedTask;
    }
    private Task Reject(ClientSession session, int cmd)
    {
        Log.Warn($"[session {session.Id}] 第16章剧情通关请求无效或前置未达成，保留进度与奖励");
        _ctx.LuaCalls.CallClient(session, cmd, new { nError = 1, tbNew = Array.Empty<int[]>() });
        return Task.CompletedTask;
    }
    private static bool Read(JsonObject obj, string key, out int value)
    {
        value = 0;
        return obj[key] is JsonValue node && int.TryParse(node.ToString(), out value);
    }
}
