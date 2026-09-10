using Monocle;
using Microsoft.Xna.Framework;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class RecordingTransitionAutoSave {
    private static Level? pendingLevel;
    private static Session? pendingSession;
    private static string pendingRoom = "";
    private static RecoveryAnchor? anchor;
    private static readonly Dictionary<string, RecoveryAnchor> versions = [];
    private static readonly Dictionary<Checkpoint, string> heads = [];
    private static bool referencesDirty = true;
    internal static void InvalidateReferences() => referencesDirty = true;
    internal static string? CurrentVersionId => anchor?.Id;
    internal static int RetainedVersionCount => versions.Count;

    internal static void RestoreContext(Level level, RecordingTimelineSnapshot snapshot) {
        InvalidateReferences();
        anchor = snapshot.RecoveryVersionId is { } id && versions.TryGetValue(id, out var saved)
            && Matches(saved, level) && SpeedrunToolRecoverySlot.Contains(id) ? saved : null;
        if (anchor is { } current) SpeedrunToolRecoverySlot.SelectVersion(current.Id);
    }

    private static bool Matches(RecoveryAnchor saved, Level level) => ReferenceEquals(saved.Level, level)
        && saved.Area == level.Session.Area && saved.Room == level.Session.Level
        && saved.Respawn == level.Session.RespawnPoint && saved.RecordingPath == AutoRecorder.CurrentPath;

    private static void Collect() {
        if (!referencesDirty || SpeedrunToolRecoverySlot.Completing || SpeedrunToolAutoSave.ManualOperationActive) return;
        // Graph traversal/allocations belong to save/load/clear events, not to
        // every gameplay tick (large maps can retain many checkpoint versions).
        bool retry = false;
        var roots = new HashSet<string>(SpeedrunToolBridge.ReferencedRecoveryVersions(AutoRecorder.CurrentPath));
        foreach (var pair in heads.ToArray()) {
            if (pair.Key.RecordingPath == AutoRecorder.CurrentPath && ReferenceEquals(pair.Key.Level, Engine.Scene)) roots.Add(pair.Value);
            else heads.Remove(pair.Key);
        }
        if (anchor is { } current) roots.Add(current.Id);
        foreach (string id in SpeedrunToolRecoverySlot.VersionIds.ToArray()) {
            if (!roots.Contains(id)) {
                if (SpeedrunToolRecoverySlot.ReleaseVersion(id)) versions.Remove(id);
                else retry = true;
            }
        }
        foreach (string id in versions.Keys.ToArray()) if (!SpeedrunToolRecoverySlot.Contains(id)) versions.Remove(id);
        if (anchor is { } selected && SpeedrunToolRecoverySlot.Contains(selected.Id)) SpeedrunToolRecoverySlot.SelectVersion(selected.Id);
        referencesDirty = retry;
    }

    internal static bool Enabled => MicroblocksQolUtilsModule.Settings.Enabled
        && MicroblocksQolUtilsModule.Settings.RecordingAutoSaveOnTransition && AutoRecorder.IsRecording;

    internal static bool CanRecover(Level level) => Enabled && SpeedrunToolAutoSave.CanUse
        && !SpeedrunToolAutoSave.HasManualState && anchor is { } saved && Matches(saved, level)
        && SpeedrunToolRecoverySlot.Contains(saved.Id) && !level.Completed && !level.Transitioning;

    internal static void Queue(Level level, string room) {
        Cancel();
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (!settings.Enabled || !settings.RecordingAutoSaveOnTransition || !AutoRecorder.IsRecording) return;
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
        InvalidateReferences();
        Cancel();
        anchor = null;
        heads.Clear();
    }

    internal static void AfterEngineUpdate() {
        // TransitionTo queues the destination while Session.Level may still name
        // the departure room. Its manual save must not erase the pending request.
        if (pendingLevel is { Transitioning: true } transitioning
            && ReferenceEquals(Engine.Scene, transitioning)) return;
        if (RecordingSavePause.Active) {
            RecordingSavePause.Update();
            return;
        }
        if (!Enabled || Engine.Scene is not Level) {
            Reset();
            SpeedrunToolRecoverySlot.Release();
            versions.Clear();
            return;
        }
        Collect();
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
            InvalidateReferences();
            if (result == RecoveryResult.Success) {
                // Publish only after both the scene snapshot and its pre-clone succeed.
                string id = SpeedrunToolRecoverySlot.Name;
                anchor = new(id, level, level.Session.Area, level.Session.Level, level.Session.RespawnPoint, AutoRecorder.CurrentPath);
                versions[id] = anchor;
                heads[new(level, anchor.Area, anchor.Room, anchor.Respawn, anchor.RecordingPath)] = id;
            } else if (anchor is { } previous && SpeedrunToolRecoverySlot.Contains(previous.Id)) {
                SpeedrunToolRecoverySlot.SelectVersion(previous.Id);
            }
            // Don't cancel the pause: its clean-frame presentation gate still owns
            // the resume. A busy user SL can be retried once gameplay runs again.
            if (result != RecoveryResult.Busy) {
                pendingLevel = null;
                pendingSession = null;
                pendingRoom = "";
            }
        });
    }

    private sealed record RecoveryAnchor(string Id, Level Level, AreaKey Area, string Room, Vector2? Respawn, string RecordingPath);
    private sealed record Checkpoint(Level Level, AreaKey Area, string Room, Vector2? Respawn, string RecordingPath);
}
