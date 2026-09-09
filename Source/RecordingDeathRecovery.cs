using System.Reflection;
using Monocle;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class RecordingDeathRecovery {
    private static readonly FieldInfo? Finished = typeof(PlayerDeadBody).GetField("finished",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static PlayerDeadBody? pendingBody;
    private static On.Celeste.PlayerDeadBody.orig_End? fallback;

    internal static void Load() {
        // Run before SRT's death auto-load even if SRT installs its hook later.
        using (new DetourConfigContext(new DetourConfig("MicroblocksQolUtils.RecordingRecovery", priority: 1000)).Use())
            On.Celeste.PlayerDeadBody.End += End;
    }

    internal static void Unload() {
        On.Celeste.PlayerDeadBody.End -= End;
        pendingBody = null;
        fallback = null;
    }

    private static void End(On.Celeste.PlayerDeadBody.orig_End orig, PlayerDeadBody body) {
        if (ReferenceEquals(body, pendingBody)) return;
        if (Finished?.GetValue(body) is not false || body.Scene is not Level level
            || !ReferenceEquals(Engine.Scene, level) || body.HasGolden
            || (body.DeathAction is not null && body.DeathAction != (Action)level.Reload)
            || level.Entities.FindFirst<PlayerSeeker>() is not null
            || !RecordingTransitionAutoSave.CanRecover(level)) {
            orig(body);
            return;
        }
        body.Get<Coroutine>()?.Cancel();
        pendingBody = body;
        fallback = orig;
    }

    internal static void AfterEngineUpdate() {
        if (pendingBody is not { } body) return;
        if (body.Scene is not Level level || !ReferenceEquals(Engine.Scene, level)
            || level.Tracker.GetEntity<Player>() is { Dead: false }) {
            pendingBody = null;
            fallback = null;
            return;
        }
        RecoveryResult result = RecoveryResult.Unavailable;
        if (RecordingTransitionAutoSave.CanRecover(level)) {
            // A normal death must not rewind time/death statistics, even if the
            // user's manual SL configuration has SaveTimeAndDeaths enabled.
            DeathStatistics statistics = DeathStatistics.Capture(level);
            result = SpeedrunToolAutoSave.TryLoad(level);
            if (result == RecoveryResult.Busy) return;
            if (result == RecoveryResult.Success) statistics.Restore(level);
        }
        On.Celeste.PlayerDeadBody.orig_End? original = fallback;
        pendingBody = null;
        fallback = null;
        if (result == RecoveryResult.Success) {
            InstantDeaths.Reset();
        } else {
            RecordingTransitionAutoSave.Reset();
            original?.Invoke(body);
        }
    }

    private sealed record DeathStatistics(long SessionTime, int Deaths, int RoomDeaths,
        long TotalTime, int TotalDeaths, long AreaTime, int AreaDeaths) {
        internal static DeathStatistics Capture(Level level) {
            SaveData data = SaveData.Instance;
            var mode = data.Areas_Safe[level.Session.Area.ID].Modes[(int)level.Session.Area.Mode];
            return new(level.Session.Time, level.Session.Deaths, level.Session.DeathsInCurrentLevel,
                data.Time, data.TotalDeaths, mode.TimePlayed, mode.Deaths);
        }

        internal void Restore(Level level) {
            level.Session.Time = SessionTime;
            level.Session.Deaths = Deaths;
            level.Session.DeathsInCurrentLevel = RoomDeaths;
            SaveData data = SaveData.Instance;
            data.Time = TotalTime;
            data.TotalDeaths = TotalDeaths;
            var mode = data.Areas_Safe[level.Session.Area.ID].Modes[(int)level.Session.Area.Mode];
            mode.TimePlayed = AreaTime;
            mode.Deaths = AreaDeaths;
        }
    }
}
