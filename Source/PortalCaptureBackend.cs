namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// The desktop-portal / FFmpeg capture backend: on-screen capture via scap
/// (xdg-desktop-portal on Linux, WGC on Windows, ScreenCaptureKit on macOS) plus the
/// OS screen-capture authorization flow. This is one concrete <see cref="ICaptureBackend"/>
/// implementation; swapping to an engine-level frame source means adding another backend.
/// </summary>
internal sealed class PortalCaptureBackend : ICaptureBackend {
    public bool Available => NativeCaptureBridge.Available;

    public bool AuthorizationSupported => NativeCaptureBridge.AuthorizationSupported;

    public bool HasRecordingAuthorization() => NativeCaptureBridge.HasRecordingAuthorization();

    public ulong AuthorizationEventCount => NativeCaptureBridge.AuthorizationEventCount;

    public Task<bool> AuthorizeRecordingAsync(bool force) =>
        NativeCaptureBridge.AuthorizeRecordingAsync(force);

    public NativeCaptureSession Start(int fps, int queueCapacity = 3) =>
        NativeCaptureBridge.Start(fps, queueCapacity);

    public NativeCaptureSession StartRecording(
        int fps,
        string outputPath,
        string encoder,
        int bitrateKbps,
        int queueCapacity = 3
    ) => NativeCaptureBridge.StartRecording(fps, outputPath, encoder, bitrateKbps, queueCapacity);

    public Task FinalizeRecordingAsync(
        IReadOnlyList<RecordingClip> clips,
        string outputPath,
        string encoder,
        int bitrateKbps,
        int fps,
        bool reconstructBgm,
        bool removeFreezeFrames,
        string bgmEventMapFile,
        Action<double>? progress = null
    ) => NativeCaptureBridge.FinalizeRecordingAsync(
        clips,
        outputPath,
        encoder,
        bitrateKbps,
        fps,
        reconstructBgm,
        removeFreezeFrames,
        bgmEventMapFile,
        progress
    );
}
