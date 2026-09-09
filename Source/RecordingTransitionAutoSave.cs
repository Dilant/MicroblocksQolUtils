using Monocle;
using Microsoft.Xna.Framework;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class RecordingTransitionAutoSave {
    private static Level? pendingLevel;
    private static Session? pendingSession;
    private static string pendingRoom = "";
    private static RecoveryAnchor? anchor;

    internal static bool Enabled => MicroblocksQolUtilsModule.Settings.Enabled
        && MicroblocksQolUtilsModule.Settings.RecordingAutoSaveOnTransition && AutoRecorder.IsRecording;

    internal static bool CanRecover(Level level) => Enabled && SpeedrunToolAutoSave.CanUse
        && SpeedrunToolAutoSave.HasState && anchor is { } saved
        && ReferenceEquals(saved.Level, level) && saved.Area == level.Session.Area
        && saved.Room == level.Session.Level && saved.Respawn == level.Session.RespawnPoint
        && saved.RecordingPath == AutoRecorder.CurrentPath && !level.Completed && !level.Transitioning;

    internal static void Queue(Level level, string room) {
        Cancel();
        anchor = null;
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (!settings.Enabled || !settings.RecordingAutoSaveOnTransition || !AutoRecorder.IsRecording) return;
        pendingLevel = level;
        pendingSession = level.Session;
        pendingRoom = room;
    }

    internal static void Cancel() {
        pendingLevel = null;
        pendingSession = null;
        pendingRoom = "";
    }

    internal static void Reset() {
        Cancel();
        anchor = null;
    }

    internal static void AfterEngineUpdate() {
        if (!Enabled || Engine.Scene is not Level) {
            Reset();
            SpeedrunToolRecoverySlot.Release();
            return;
        }
        if (pendingLevel is not { } level) return;
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        Player? player = level.Tracker.GetEntity<Player>();
        if (!settings.Enabled || !settings.RecordingAutoSaveOnTransition || !AutoRecorder.IsRecording
            || !ReferenceEquals(Engine.Scene, level) || !ReferenceEquals(level.Session, pendingSession)
            || level.Completed || player?.Dead == true) {
            Cancel();
            return;
        }
        if (level.Transitioning) return;
        if (level.Session.Level != pendingRoom) {
            Cancel();
            return;
        }
        if (player is null || player.StateMachine.State == Player.StIntroRespawn
            || level.Paused || level.InCutscene || level.SkippingCutscene || Engine.FreezeTimer > 0f
            || !AutoRecorder.CanSaveTransitionTimeline) return;

        // Deep-cloning may mutate EntityList. Never call SL from QolHud.Update or
        // inside the transition coroutine; wait until the entire scene update ends.
        RecoveryResult result = SpeedrunToolAutoSave.TrySave();
        if (result == RecoveryResult.Success)
            anchor = new(level, level.Session.Area, level.Session.Level, level.Session.RespawnPoint, AutoRecorder.CurrentPath);
        if (result != RecoveryResult.Busy) Cancel();
    }

    private sealed record RecoveryAnchor(Level Level, AreaKey Area, string Room, Vector2? Respawn, string RecordingPath);
}
