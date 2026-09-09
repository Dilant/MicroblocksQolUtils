using System.Text.Json;

namespace Celeste.Mod.MicroblocksQolUtils;

internal sealed class NativeRoomRecording {
    private readonly NativeCaptureSession capture;
    private int stopped;
    private double lastMediaTime;
    private readonly int targetFrameRate;
    private readonly long initialSourcePoolDrops = CaptureSource.DroppedFrames;
    private readonly long initialSourceAudioDrops = CaptureSource.DroppedAudioChunks;

    public string Path { get; }
    public string AudioPath => Path + ".sfxchunks";
    public string BgmPath => Path + ".bgmchunks";
    public string MusicEventsPath => Path + ".music.jsonl";
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

    internal double TimelineTimeSeconds => capture.TimelineTimeSeconds ?? MediaTimeSeconds;
    internal double TimeAt(ulong timestamp) => capture.TimeAt(timestamp) ?? MediaTimeSeconds;
    internal void RequestResumeFrame(ulong timestamp) => capture.RequestKeyframe(timestamp);
    internal ulong ResumeFrameTimestamp => capture.KeyframeAcceptedAt;
    internal double EncodedFrameTimeAt(ulong timestamp) => targetFrameRate <= 0 ? TimeAt(timestamp)
        : Math.Floor(TimeAt(timestamp) * targetFrameRate + 0.5d) / targetFrameRate;
    internal double FrameTimeAt(ulong timestamp, bool roundUp) {
        double time = TimeAt(timestamp);
        if (targetFrameRate <= 0) return time;
        // Encoder PTS are quantized to frame ticks. An indicator presented just
        // after a tick may round backwards; never retain that UI as the last frame.
        double ticks = time * targetFrameRate;
        return (roundUp ? Math.Ceiling(ticks) : Math.Floor(ticks)) / targetFrameRate;
    }

    public static NativeRoomRecording? Start(string output) {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        try {
            NativeCaptureSession capture = NativeCaptureBridge.StartRecording(
                settings.RecordingFrameRate,
                output,
                settings.RecordingEncoder,
                settings.RecordingBitrateKbps,
                includeUiSfx: settings.RecordingIncludeUiSfx
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
        if (!capture.HasAudioTap || statistics.AudioFramesCaptured == 0) {
            Logger.Log(
                LogLevel.Warn,
                "MicroblocksQolUtils/Recorder",
                $"Recording stopped without captured FMOD audio (tap={capture.HasAudioTap}, "
                + $"videoFrames={statistics.FramesCaptured}, audioFrames={statistics.AudioFramesCaptured})."
            );
        } else {
            Logger.Log(
                LogLevel.Info,
                "MicroblocksQolUtils/Recorder",
                $"Captured {statistics.AudioFramesCaptured} FMOD audio frame(s); "
                + $"dropped {statistics.AudioChunksDropped} chunk(s)."
            );
        }
        Task drain = capture.CompleteInput();
        return FinishAsync();

        async Task FinishAsync() {
            await drain.ConfigureAwait(false);
            await Task.Run(() => {
            try {
                // Read counters after draining, before destroying the native handle.
                capture.Stop();
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
