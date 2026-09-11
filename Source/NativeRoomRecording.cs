using System.Text.Json;

namespace Celeste.Mod.MicroblocksQolUtils;

internal sealed class NativeRoomRecording {
    private readonly NativeCaptureSession capture;
    private int stopped;
    private double lastMediaTime;
    private readonly int targetFrameRate;
    private long nextCheckpoint;
    private Task checkpoint = Task.CompletedTask;
    private readonly long initialSourcePoolDrops = CaptureSource.DroppedFrames;
    private readonly long initialSourceAudioDrops = CaptureSource.DroppedAudioChunks;

    public string Path { get; }
    public string BgmPath => Path + ".bgmchunks";
    public string MusicEventsPath => Path + ".music.jsonl";
    public string SfxEventsPath => Path + ".sfxevents";
    public string CaptureReportPath => Path + ".capture.json";
    public bool HasAudioTap => capture.HasAudioTap;

    public CaptureStatistics Statistics {
        get {
            try {
                return capture.Statistics;
            } catch (Exception exception) {
                Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Recorder",
                    $"Cannot read native capture statistics: {exception.Message}");
                return default;
            }
        }
    }

    private NativeRoomRecording(NativeCaptureSession capture, string path) {
        this.capture = capture;
        Path = path;
        targetFrameRate = MicroblocksQolUtilsModule.Settings.RecordingFrameRate;
        RecordingRecovery.Register(path);
    }

    public double MediaTimeSeconds {
        get {
            try {
                double value = capture.Statistics.MediaTimeSeconds;
                if (value > lastMediaTime) lastMediaTime = value;
            } catch (Exception exception) {
                Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Recorder", $"Cannot read native media clock: {exception.Message}");
            }
            return lastMediaTime;
        }
    }

    internal void CheckpointRecovery() {
        if (Environment.TickCount64 < nextCheckpoint || !checkpoint.IsCompleted) return;
        nextCheckpoint = Environment.TickCount64 + 1_000;
        double seconds = MediaTimeSeconds;
        checkpoint = Task.Run(() => RecordingRecovery.Checkpoint(Path, seconds));
    }

    internal double TimelineTimeSeconds => capture.TimelineTimeSeconds ?? MediaTimeSeconds;
    internal double TimeAt(ulong timestamp) => capture.TimeAt(timestamp) ?? MediaTimeSeconds;
    internal void RequestResumeFrame(ulong timestamp) => capture.RequestKeyframe(timestamp);
    internal ulong ResumeFrameTimestamp => capture.KeyframeAcceptedAt;
    internal double EncodedFrameTimeAt(ulong timestamp) => targetFrameRate <= 0 ? TimeAt(timestamp)
        : capture.FrameTimeAt(timestamp, (uint)targetFrameRate)
            ?? Math.Floor(TimeAt(timestamp)*targetFrameRate + .5d)/targetFrameRate;
    // Cut boundaries must use the same global grid as selection and encoder PTS.
    internal double FrameTimeAt(ulong timestamp, bool roundUp) {
        if (targetFrameRate <= 0) return TimeAt(timestamp);
        if (capture.FrameTimeAt(timestamp, (uint)targetFrameRate, roundUp) is { } time) return time;
        // Preserve safe boundary rounding when the first source frame has not
        // established an origin (also used by stopped/unavailable capture).
        double ticks = TimeAt(timestamp)*targetFrameRate;
        return (roundUp ? Math.Ceiling(ticks) : Math.Floor(ticks))/targetFrameRate;
    }

    public static NativeRoomRecording? Start(string output) {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        try {
            NativeCaptureSession capture = NativeCaptureBridge.StartRecording(
                settings.RecordingFrameRate,
                output,
                settings.RecordingEncoder,
                settings.RecordingBitrateKbps
            );
            return new NativeRoomRecording(capture, output);
        } catch (Exception exception) {
            Logger.Log(LogLevel.Error, "MicroblocksQolUtils/Recorder", $"Cannot start native recording: {exception.Message}");
            return null;
        }
    }

    public Task StopAsync() {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return Task.CompletedTask;
        // Disposing this sink unregisters only its callbacks; the shared DSP stays for other consumers.
        CaptureStatistics statistics = Statistics;
        Logger.Log(LogLevel.Info, "MicroblocksQolUtils/Recorder",
            "Recording audio sources: SFX and music event journals; no PCM is written during capture.");
        Task drain = capture.CompleteInput();
        return FinishAsync();

        async Task FinishAsync() {
            await checkpoint.ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            await Task.Run(() => {
            try {
                // Read counters after draining, before destroying the native handle.
                capture.Stop();
                RecordingRecovery.Checkpoint(Path, MediaTimeSeconds);
                var report = new RecordingCaptureReport(targetFrameRate, capture.Statistics,
                    capture.DeliveryStatistics, CaptureSource.DroppedFrames - initialSourcePoolDrops,
                    CaptureSource.DroppedAudioChunks - initialSourceAudioDrops);
                Logger.Log(report.UnderTarget ? LogLevel.Warn : LogLevel.Info,
                    "MicroblocksQolUtils/Recorder",
                    $"Video capture {report.Statistics.Width}x{report.Statistics.Height}: target={targetFrameRate}, "
                    + $"submitted={report.SubmittedFps:F2}fps, encoder-input={report.EncoderInputFps:F2}fps; "
                    + $"nativeDrops={report.Statistics.FramesDropped}, callbackDrops={report.Delivery.DroppedFrames}, "
                    + $"sourcePoolDrops={report.SourcePoolDrops}. File: {Path}");
                try {
                    File.WriteAllText(CaptureReportPath, JsonSerializer.Serialize(report,
                        new JsonSerializerOptions { WriteIndented = true }));
                } catch (Exception exception) {
                    // Diagnostics must not discard an otherwise usable recording.
                    Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/CaptureReport");
                }
            } finally { capture.Dispose(); }
            }).ConfigureAwait(false);
            RecordingRecovery.Release(Path);
        }
    }
}

internal sealed record RecordingCaptureReport(int TargetFps, CaptureStatistics Statistics,
    CaptureDeliveryStatistics Delivery, long SourcePoolDrops, long SourceAudioDrops) {
    public double SubmittedFps => Rate(Statistics.FramesCaptured);
    public double EncoderInputFps => Rate(Statistics.FramesConsumed);
    public bool UnderTarget => Statistics.MediaTimeSeconds >= 1 && EncoderInputFps < TargetFps * 0.98;
    private double Rate(ulong frames) => frames > 1 && Statistics.MediaTimeSeconds > 0
        ? (frames - 1) / Statistics.MediaTimeSeconds : 0;
}

public readonly record struct MusicPosition(string Event, int TimelineMilliseconds) {
    public static MusicPosition Read() {
        CaptureMusic? state = MusicCapture.Snapshots.FirstOrDefault(s => s.Track == "main");
        return state is null ? new("", 0) : new(state.Event, state.TimelineMilliseconds);
    }
}
