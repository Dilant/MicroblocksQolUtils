namespace Celeste.Mod.MicroblocksQolUtils;

internal sealed class NativeRoomRecording {
    private readonly NativeCaptureSession capture;
    private readonly FmodSfxTap? sfxTap;
    private int stopped;
    private double lastMediaTime;

    public string Path { get; }
    public string AudioPath => Path + ".sfxchunks";
    public bool HasAudioTap => sfxTap is not null;

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

    private NativeRoomRecording(NativeCaptureSession capture, FmodSfxTap? sfxTap, string path) {
        this.capture = capture;
        this.sfxTap = sfxTap;
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
            NativeCaptureSession capture = CaptureBackend.Current.StartRecording(
                settings.RecordingFrameRate,
                output,
                settings.RecordingEncoder,
                settings.RecordingBitrateKbps
            );
            FmodSfxTap? sfxTap = FmodSfxTap.Attach(capture, settings.RecordingIncludeUiSfx);
            return new NativeRoomRecording(capture, sfxTap, output);
        } catch (Exception exception) {
            Logger.Log(LogLevel.Error, "MicroblocksQolUtils/Recorder", $"Cannot start native recording: {exception.Message}");
            return null;
        }
    }

    public Task StopAsync() {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return Task.CompletedTask;
        // Detach synchronously so FMOD cannot race another callback into a closing native queue.
        sfxTap?.Dispose();
        CaptureStatistics statistics = Statistics;
        if (sfxTap is null || statistics.AudioFramesCaptured == 0) {
            Logger.Log(
                LogLevel.Warn,
                "MicroblocksQolUtils/Recorder",
                $"Recording stopped without captured FMOD audio (tap={sfxTap is not null}, "
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
        try {
            FMOD.Studio.EventInstance instance = Audio.CurrentMusicEventInstance;
            string name = Audio.GetEventName(instance) ?? "";
            instance.getTimelinePosition(out int position);
            return new MusicPosition(name, Math.Max(0, position));
        } catch {
            return new MusicPosition("", 0);
        }
    }
}
