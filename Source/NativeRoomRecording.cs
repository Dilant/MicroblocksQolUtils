namespace Celeste.Mod.MicroblocksQolUtils;

internal sealed class NativeRoomRecording {
    private readonly NativeCaptureSession capture;
    private int stopped;
    private double lastMediaTime;

    public string Path { get; }
    public string AudioPath => Path + ".sfxchunks";
    public string BgmPath => Path + ".bgmchunks";
    public string MusicEventsPath => Path + ".music.jsonl";
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
        return Task.Run(capture.Dispose);
    }
}

public readonly record struct MusicPosition(string Event, int TimelineMilliseconds) {
    public static MusicPosition Read() {
        CaptureMusic? state = MusicCapture.Snapshots.FirstOrDefault(s => s.Track == "main");
        return state is null ? new("", 0) : new(state.Event, state.TimelineMilliseconds);
    }
}
