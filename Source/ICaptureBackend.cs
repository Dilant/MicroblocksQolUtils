namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// The screen-capture and encoding backend that feeds the recorder. This is the swappable
/// unit between the current desktop-portal/FFmpeg path and a future engine-level frame
/// source: everything the timeline, finalizer and UI know about capturing goes through here.
/// </summary>
public interface ICaptureBackend {
    bool Available { get; }

    /// <summary>Whether this backend needs (and can show) an OS screen-capture authorization prompt.</summary>
    bool AuthorizationSupported { get; }

    bool HasRecordingAuthorization();

    ulong AuthorizationEventCount { get; }

    Task<bool> AuthorizeRecordingAsync(bool force);

    NativeCaptureSession Start(int fps, int queueCapacity = 3);

    NativeCaptureSession StartRecording(
        int fps,
        string outputPath,
        string encoder,
        int bitrateKbps,
        int queueCapacity = 3
    );

    Task FinalizeRecordingAsync(
        IReadOnlyList<RecordingClip> clips,
        string outputPath,
        string encoder,
        int bitrateKbps,
        int fps,
        bool reconstructBgm,
        bool removeFreezeFrames,
        string bgmEventMapFile,
        Action<double>? progress = null
    );
}
