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
        && !SpeedrunToolAutoSave.HasManualState
        && SpeedrunToolAutoSave.HasState && anchor is { } saved
        && ReferenceEquals(saved.Level, level) && saved.Area == level.Session.Area
        && saved.Room == level.Session.Level && saved.Respawn == level.Session.RespawnPoint
        && saved.RecordingPath == AutoRecorder.CurrentPath && !level.Completed && !level.Transitioning;

    internal static void Queue(Level level, string room) {
        Cancel();
        anchor = null;
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (!settings.Enabled || !settings.RecordingAutoSaveOnTransition || !AutoRecorder.IsRecording
            || SpeedrunToolRecoverySlot.HasUserStateIn(level, room)) return;
        pendingLevel = level;
        pendingSession = level.Session;
        pendingRoom = room;
    }

    internal static void Cancel() {
        RecordingSavePause.Cancel();
        pendingLevel = null;
        pendingSession = null;
        pendingRoom = "";
    }

    internal static void Reset() {
        Cancel();
        anchor = null;
    }

    internal static void AfterEngineUpdate() {
        // TransitionTo queues the destination while Session.Level may still name
        // the departure room. Its manual save must not erase the pending request.
        if (pendingLevel is { Transitioning: true } transitioning
            && ReferenceEquals(Engine.Scene, transitioning)) return;
        if (SpeedrunToolAutoSave.HasManualState) {
            Reset();
            // Do not block/wait inside SRT's save/load callbacks. Once the user
            // operation is idle, retire the now-obsolete private snapshot.
            if (!SpeedrunToolAutoSave.ManualOperationActive) SpeedrunToolRecoverySlot.Release();
            return;
        }
        if (RecordingSavePause.Active) {
            RecordingSavePause.Update();
            return;
        }
        if (!Enabled || Engine.Scene is not Level) {
            Reset();
            SpeedrunToolRecoverySlot.Release();
            return;
        }
        // Clearing user slots is not a save trigger. Wait for the existing
        // recording-start, room-transition or respawn-point-change requests.
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
        if (!SpeedrunToolAutoSave.CanUse) { Cancel(); return; }
        if (!SpeedrunToolAutoSave.ReadyToSave) return;
        RecordingSavePause.Begin(level, result => {
            if (result == RecoveryResult.Success)
                anchor = new(level, level.Session.Area, level.Session.Level, level.Session.RespawnPoint, AutoRecorder.CurrentPath);
            // Don't cancel the pause: its clean-frame presentation gate still owns
            // the resume. A busy user SL can be retried once gameplay runs again.
            if (result != RecoveryResult.Busy) {
                pendingLevel = null;
                pendingSession = null;
                pendingRoom = "";
            }
        });
    }

    private sealed record RecoveryAnchor(Level Level, AreaKey Area, string Room, Vector2? Respawn, string RecordingPath);
}
