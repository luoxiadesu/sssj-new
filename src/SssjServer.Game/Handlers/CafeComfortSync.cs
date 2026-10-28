using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>Inventory changes affect owned-item rankings even when furniture is stored.
/// NCafeNet.RspDownloadCafeData accepts zero comfort; its other lists must be complete.</summary>
internal sealed class CafeComfortSync
{
    private readonly GameFeatureContext _ctx;
    private readonly HouseTable _house;
    private readonly CafeTable _cafe;
    private readonly GirlMoodRecovery _mood;

    internal CafeComfortSync(GameFeatureContext ctx)
    {
        _ctx = ctx;
        string settings = Path.GetFullPath(ctx.Options.SettingDirectory);
        _house = HouseTable.Load(settings);
        _cafe = CafeTable.Load(settings);
        _mood = new GirlMoodRecovery(settings);
    }

    // Called after inventory mutation, before the caller saves. Mood depends on the
    // previous saved comfort, so the old rate still settles correctly here.
    internal bool Refresh(ClientSession session, Player player)
    {
        var state = CafeFeature.ReadState(player, _cafe);
        int score = HouseComfort.Calculate(_house, HouseFeature.ReadOrCreate(player, _house), state.Level, _cafe.MaxComfort);
        if (score == state.Comfort) return false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _mood.Refresh(session, player, now);
        CafeFeature.WriteState(player, state with { Comfort = score });
        _mood.Refresh(session, player, now);
        return true;
    }

    // Do not mark an outside/stale session as entering the cafe just to send a score.
    internal void Push(ClientSession session, Player player, bool changed)
    {
        if (!changed || !GirlMoodRecovery.IsInCafe(session, player)) return;
        var now = DateTimeOffset.UtcNow;
        var state = CafeFeature.ReadState(player, _cafe);
        var content = CafeFeature.ReadContent(player, state, _cafe, CafeFeature.CafeDayIndex(now), session.Id);
        _ctx.LuaCalls.CallClient(session, CafeFeature.ReqCafeDataCmd,
            CafeFeature.BuildCafeDataPayload(state, now.ToUnixTimeSeconds(), CafePetState.Read(player), content));
    }
}
