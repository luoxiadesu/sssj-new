using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
namespace SssjServer.Game.Tests;

internal static class ChallengeTestHarness
{
    internal static async Task<FeatureHarness> CreateAsync(string account)
    {
        var harness = await FeatureHarness.CreateAsync(account);
        harness.Player.RecordLevelPass(MapList.MapId(5, 12, 1), MapList.StarMask);
        return harness;
    }
    internal static string WithToken(FeatureHarness harness, string json)
    {
        var node=JsonNode.Parse(json);
        if (node is JsonArray { Count: >= 6 } a && a[0]?.GetValue<int>()==6 && a[5] is JsonObject extra
            && harness.Session.Tag is Player player && player.Extra.GetValueOrDefault(ChallengeRuns.AttemptKey) is JsonObject run
            && run["battleToken"] is JsonValue value && value.TryGetValue<string>(out string? token)) extra["battleToken"]=token;
        if (node is JsonArray { Count: >= 2 } tower && tower[0]?.GetValue<int>()==4 && tower[1]?.GetValue<int>()==21
            && harness.Session.Tag is Player p && p.Extra.GetValueOrDefault(TwentyFirstTowerHandler.AttemptKey) is JsonObject towerRun
            && towerRun["Token"] is JsonValue tv && tv.TryGetValue<string>(out string? tt))
        {
            while(tower.Count<10)tower.Add(null);
            tower[9]=tt;
        }
        return node!.ToJsonString();
    }
    internal static Task SettleAsync(FeatureHarness harness,int command,string json)
        =>harness.DispatchLuaCallAsync(command,WithToken(harness,json));
}
