using Monocle;
using Microsoft.Xna.Framework;

namespace Celeste.Mod.MicroblocksQolUtils;

// Render/presentation driven, not a timer: the save cannot start until the
// indicator was presented, and gameplay cannot resume onto an indicator frame.
internal static class RecordingSavePause {
    private enum Phase { None, Boundary, Indicator, Save, Cloning, Loading, Clean, ResumeFrame }
    private static Phase phase;
    private static Level? level;
    private static Action<RecoveryResult>? completed;
    private static int cleanFrames;
    private static long beganAt;
    private static TimeSpan lastStep = TimeSpan.FromSeconds(1d / 60);
    private static bool resumeStep;
    private static bool resumeUpdateAwaitingPresentation;
    private static bool resumeDisplayPinned;
    internal static bool Active => phase != Phase.None;
    internal static bool ShowIndicator => phase is Phase.Indicator or Phase.Save or Phase.Cloning;

    internal static bool BeforeEngineUpdate(ref GameTime time) {
        if (Active) return false;
        if (resumeUpdateAwaitingPresentation) {
            // Outer smoothing hooks may reset their "updated" flag even for a
            // skipped catch-up tick. Keep the one accepted physics step visible.
            if (!resumeDisplayPinned) {
                RecordingMotionSmoothing.FreezePresentation();
                resumeDisplayPinned = true;
            }
            return false;
        }
        // The clean presentation gate drains fixed-step catch-up updates while
        // frozen. Also prevent a variable-step backend from applying the save's
        // elapsed wall time as a single giant physics step on resume.
        if (resumeStep) {
            if (time.ElapsedGameTime > lastStep) time = new GameTime(time.TotalGameTime, lastStep, time.IsRunningSlowly);
            resumeStep = false;
            resumeUpdateAwaitingPresentation = true;
            resumeDisplayPinned = false;
        }
        if (time.ElapsedGameTime > TimeSpan.Zero)
            lastStep = time.ElapsedGameTime < TimeSpan.FromSeconds(1d / 60)
                ? time.ElapsedGameTime : TimeSpan.FromSeconds(1d / 60);
        return true;
    }

    internal static void Begin(Level owner, Action<RecoveryResult> onComplete) {
        if (Active) return;
        RecordingMotionSmoothing.Prepare();
        level = owner;
        completed = onComplete;
        beganAt = Environment.TickCount64;
        // Present the exact frozen state once before drawing any save UI. This
        // is the end-exclusive timeline boundary AND the state cloned by SRT.
        phase = Phase.Boundary;
        RecordingMotionSmoothing.FreezePresentation();
    }

    internal static bool BeginRecovery(Level owner) {
        if (Active) return false;
        RecordingMotionSmoothing.Prepare();
        level = owner;
        beganAt = Environment.TickCount64;
        phase = Phase.Loading;
        RecordingPauseAudio.Pause();
        return true;
    }

    internal static void CompleteRecovery() {
        // SRT has restored entities, camera and scene clocks. Do not run even
        // ONE gameplay update before that exact state reaches both encoders.
        RecordingMotionSmoothing.PrimeRestoredState();
        RecordingDeathAudio.StopRemainder();
        AutoRecorder.StageInternalRecoveryResume();
        phase = Phase.Clean;
        cleanFrames = 0;
    }

    internal static void Update() {
        if (!Active) return;
        if (!ReferenceEquals(Engine.Scene, level) || !RecordingTransitionAutoSave.Enabled
            || Environment.TickCount64 - beganAt > 10_000 && phase != Phase.Cloning) {
            Cancel();
            return;
        }
        if (phase == Phase.Save) {
            RecoveryResult result = SpeedrunToolAutoSave.TrySave(deferPreClone: true);
            if (result == RecoveryResult.Success) phase = Phase.Cloning;
            else Finish(result);
        }
        if (phase == Phase.Cloning) {
            RecoveryResult result = SpeedrunToolRecoverySlot.CompletePending();
            if (result != RecoveryResult.Busy) Finish(result);
        }
    }

    private static void Finish(RecoveryResult result) {
        Action<RecoveryResult>? callback = completed;
        completed = null;
        phase = Phase.Clean;
        cleanFrames = 0;
        callback?.Invoke(result);
    }

    internal static void Presented(ulong timestamp) {
        // A fixed-step loop may call Engine.Update repeatedly after a stall.
        // Allow only one recovery step until its result was actually presented.
        resumeUpdateAwaitingPresentation = false;
        if (phase == Phase.Boundary) {
            // Exclude this saved pose here; include it exactly once at resume.
            // Earlier GPU deliveries cannot move the cut behind the state clone.
            AutoRecorder.SuspendForInternalSave(timestamp);
            RecordingPauseAudio.Pause();
            phase = Phase.Indicator;
        } else if (phase == Phase.Indicator) {
            phase = Phase.Save;
        } else if (phase == Phase.Clean && ++cleanFrames >= 2) {
            // Request on the source clock, before this clean presentation enters
            // the GPU queue. Keep simulation frozen until BOTH sinks accept their
            // actual first resumed frame; callback lag cannot select an old P frame.
            AutoRecorder.PrepareInternalSaveResume(timestamp);
            // Do not consume one-shot tails during the excluded clean-frame
            // guard. Resume sound at the first requested retained presentation.
            RecordingPauseAudio.Resume();
            phase = Phase.ResumeFrame;
        } else if (phase == Phase.ResumeFrame && AutoRecorder.TryResumeAfterInternalSave()) {
            resumeStep = true;
            phase = Phase.None;
            level = null;
        }
    }

    // Also release after an ordinary draw when capture has stopped or the source
    // backend is unavailable. A video-only callback must never deadlock gameplay.
    internal static void Drawn() => resumeUpdateAwaitingPresentation = false;

    internal static void Cancel() {
        if (!Active) return;
        SpeedrunToolRecoverySlot.CompletePending(wait: true);
        completed = null;
        RecordingPauseAudio.Resume();
        AutoRecorder.CancelInternalSave();
        resumeStep = true;
        phase = Phase.None;
        level = null;
    }
}
