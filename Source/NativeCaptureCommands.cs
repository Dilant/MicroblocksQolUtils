using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class NativeCaptureCommands {
    private static NativeCaptureSession? probe;

    [Command("qol_capture_probe_start", "Start the native platform capture probe")]
    public static void Start() {
        probe?.Dispose();
        probe = CaptureBackend.Current.Start(
            MicroblocksQolUtilsModule.Settings.RecordingFrameRate
        );
        Engine.Commands.Log("Native scap capture probe started.");
    }

    [Command("qol_capture_probe_stats", "Show native platform capture statistics")]
    public static void Stats() {
        if (probe is null) {
            Engine.Commands.Log("Native capture probe is not running.");
            return;
        }
        CaptureStatistics stats = probe.Statistics;
        Engine.Commands.Log(
            $"running={stats.Running} size={stats.Width}x{stats.Height} "
            + $"captured={stats.FramesCaptured} consumed={stats.FramesConsumed} "
            + $"dropped={stats.FramesDropped} queue={stats.QueueDepth} "
            + $"bytes={stats.BytesCaptured} media={stats.MediaTimeSeconds:0.000}s"
        );
    }

    [Command("qol_capture_probe_stop", "Stop the native platform capture probe")]
    public static void Stop() {
        probe?.Dispose();
        probe = null;
        Engine.Commands.Log("Native scap capture probe stopped.");
    }

    [Command("qol_record_start", "Start manual run recording")]
    public static void StartManualRecording() {
        AutoRecorder.StartManual();
        Engine.Commands.Log("Manual recording armed; capture starts when gameplay resumes.");
    }

    [Command("qol_record_save", "Stop and save the current manual recording")]
    public static void SaveManualRecording() {
        AutoRecorder.StopManual(Engine.Scene as Level, save: true);
        Engine.Commands.Log("Manual recording stopped and queued for finalization.");
    }

    [Command("qol_record_discard", "Stop and discard the current manual recording")]
    public static void DiscardManualRecording() {
        AutoRecorder.StopManual(Engine.Scene as Level, save: false);
        Engine.Commands.Log("Manual recording discarded.");
    }

    [Command("qol_record_status", "Show manual/automatic recording status")]
    public static void RecordingStatus() {
        Engine.Commands.Log(
            $"manual={AutoRecorder.ManualMode} recording={AutoRecorder.IsRecording} "
            + $"deathReplay={AutoRecorder.IsDeathReplayRecording} "
            + $"finalizing={AutoRecorder.IsFinalizing} media={AutoRecorder.CurrentSeconds:0.000}s "
            + $"deathMedia={AutoRecorder.DeathReplaySeconds:0.000}s "
            + $"audioTap={AutoRecorder.HasAudioTap} audioFrames={AutoRecorder.AudioFramesCaptured} "
            + $"audioDropped={AutoRecorder.AudioChunksDropped} "
            + $"path={AutoRecorder.CurrentPath} last={AutoRecorder.LastOutput}"
        );
    }

    public static void Unload() {
        probe?.Dispose();
        probe = null;
    }
}
