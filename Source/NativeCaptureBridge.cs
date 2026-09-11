using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class NativeCaptureBridge {
    private const string LibraryName = "microblocks_qol_native";
    private const uint ExpectedAbiVersion = 11;
    private static bool initialized;
    private static bool available;
    private static string? loadError;

    public static bool Available => available;

    public static void InitializeFromMod(EverestModuleMetadata metadata) {
        ArgumentNullException.ThrowIfNull(metadata);
        AndroidNativeLibrary.Load(metadata);
        Initialize(null);
    }

    public static void Initialize(string? nativeDirectory) {
        if (initialized) return;
        initialized = true;
        try {
            uint abi = CaptureAbiVersion();
            if (abi != ExpectedAbiVersion)
                throw new InvalidDataException($"native ABI {abi} != expected {ExpectedAbiVersion}");
            available = true;
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils/Recorder",
                "Loaded native capture backend through Everest's platform library resolver");
        } catch (Exception exception) {
            available = false;
            loadError = $"cannot load native capture backend through Everest: {exception}";
            Logger.Log(LogLevel.Error, "MicroblocksQolUtils/Recorder", loadError);
        }
    }

    public static NativeCaptureSession Start(int fps, int queueCapacity = 3) {
        return StartCore(fps, queueCapacity, null, "auto", 12_000);
    }

    public static NativeCaptureSession StartRecording(
        int fps,
        string outputPath,
        string encoder,
        int bitrateKbps,
        int queueCapacity = 3
    ) {
        return StartCore(fps, queueCapacity, Path.GetFullPath(outputPath), encoder, bitrateKbps);
    }

    private static NativeCaptureSession StartCore(
        int fps,
        int queueCapacity,
        string? outputPath,
        string encoder,
        int bitrateKbps
    ) {
        EnsureAvailable();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new {
            fps,
            queue_capacity = queueCapacity,
            output_path = outputPath,
            encoder,
            bitrate_kbps = bitrateKbps
        });
        int status = CaptureCreate(json, (nuint)json.Length, out ulong handle);
        ThrowIfFailed(status, "create");
        try {
            ThrowIfFailed(CaptureStart(handle), "start");
            return new NativeCaptureSession(handle, outputPath, (uint)fps);
        } catch {
            CaptureDestroy(handle);
            throw;
        }
    }

    public static Task FinalizeRecordingAsync(
        IReadOnlyList<RecordingClip> clips,
        string outputPath,
        string encoder,
        int bitrateKbps,
        int fps,
        bool reconstructBgm,
        bool removeFreezeFrames,
        string bgmEventMapFile,
        Action<double>? progress = null
    ) => FinalizeRecordingAsync(clips, outputPath, encoder, bitrateKbps, fps,
        reconstructBgm, removeFreezeFrames, bgmEventMapFile, false, progress);

    public static Task FinalizeRecordingAsync(
        IReadOnlyList<RecordingClip> clips,
        string outputPath,
        string encoder,
        int bitrateKbps,
        int fps,
        bool reconstructBgm,
        bool removeFreezeFrames,
        string bgmEventMapFile,
        bool preferVideoCopy,
        Action<double>? progress = null
    ) {
        EnsureAvailable();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new {
            clips = clips.Select(clip => new {
                source = Path.GetFullPath(clip.Source),
                start_seconds = clip.StartSeconds,
                duration_seconds = clip.DurationSeconds,
                music_event = clip.MusicEvent,
                music_timeline_milliseconds = clip.MusicTimelineMilliseconds,
                seamless_from_previous = clip.SeamlessFromPrevious,
                bgm_follows_video = clip.BgmFollowsVideo
            }),
            output_path = Path.GetFullPath(outputPath),
            encoder,
            bitrate_kbps = bitrateKbps,
            fps,
            reconstruct_bgm = reconstructBgm,
            remove_freeze_frames = removeFreezeFrames,
            prefer_video_copy = preferVideoCopy,
            bgm_event_map_file = string.IsNullOrWhiteSpace(bgmEventMapFile)
                ? ""
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(bgmEventMapFile))
        });
        return Task.Run(() => {
            GCHandle progressHandle = default;
            FinalizeProgressCallback? callback = null;
            GCHandle sfxHandle = default;
            FinalizeSfxCallback? sfxCallback = null;
            SfxRenderContext? sfxContext = null;
            try {
                if (progress is not null) {
                    progressHandle = GCHandle.Alloc(progress);
                    callback = ReportFinalizeProgress;
                }
                if (clips.Any(clip => File.Exists(Path.GetFullPath(clip.Source) + ".sfxevents")
                    || File.Exists(Path.GetFullPath(clip.Source) + ".music.jsonl"))) {
                    sfxContext = new SfxRenderContext(reconstructBgm);
                    sfxHandle = GCHandle.Alloc(sfxContext);
                    sfxCallback = RenderSfx;
                }
                ThrowIfFailed(RecordingFinalizeWithProgress(
                    json,
                    (nuint)json.Length,
                    callback,
                    progressHandle.IsAllocated ? GCHandle.ToIntPtr(progressHandle) : IntPtr.Zero,
                    sfxCallback,
                    sfxHandle.IsAllocated ? GCHandle.ToIntPtr(sfxHandle) : IntPtr.Zero
                ), sfxContext?.Error is Exception error ? $"finalize SFX ({error.Message})" : "finalize");
                progress?.Invoke(1d);
                GC.KeepAlive(callback);
                GC.KeepAlive(sfxCallback);
            } finally {
                if (progressHandle.IsAllocated) progressHandle.Free();
                if (sfxHandle.IsAllocated) sfxHandle.Free();
            }
        });
    }

    private sealed class SfxRenderContext {
        internal SfxRenderContext(bool reconstructBgm) => ReconstructBgm = reconstructBgm;
        public bool ReconstructBgm { get; }
        public Exception? Error;
    }

    private sealed record FinalizeClipDto(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("start_seconds")] double StartSeconds,
        [property: JsonPropertyName("duration_seconds")] double DurationSeconds,
        [property: JsonPropertyName("music_event")] string? MusicEvent = null,
        [property: JsonPropertyName("music_timeline_milliseconds")] int MusicTimelineMilliseconds = 0,
        [property: JsonPropertyName("seamless_from_previous")] bool SeamlessFromPrevious = false,
        [property: JsonPropertyName("bgm_follows_video")] bool BgmFollowsVideo = false
    );

    private static int RenderSfx(
        IntPtr clipsJson,
        nuint clipsLength,
        IntPtr sidecarPath,
        nuint sidecarLength,
        IntPtr context
    ) {
        try {
            if (context == IntPtr.Zero) return 1;
            SfxRenderContext renderContext = (SfxRenderContext?)GCHandle.FromIntPtr(context).Target
                ?? throw new InvalidOperationException("SFX renderer context was lost");
            if (clipsLength > int.MaxValue || sidecarLength > int.MaxValue)
                throw new InvalidDataException("native SFX render request is too large");
            byte[] json = new byte[(int)clipsLength];
            Marshal.Copy(clipsJson, json, 0, json.Length);
            string sidecar = Encoding.UTF8.GetString(ReadNativeBytes(sidecarPath, (int)sidecarLength));
            FinalizeClipDto[] effective = JsonSerializer.Deserialize<FinalizeClipDto[]>(json)
                ?? throw new InvalidDataException("native SFX render clip list is empty");
            RecordingClip[] clips = effective.Select(clip => new RecordingClip(
                Path.GetFullPath(clip.Source), clip.StartSeconds, clip.DurationSeconds,
                clip.MusicEvent ?? string.Empty, clip.MusicTimelineMilliseconds,
                clip.SeamlessFromPrevious, clip.BgmFollowsVideo)).ToArray();
            if (clips.Length == 0) throw new InvalidDataException("native SFX render clip list is empty");
            string journal = clips[0].Source + ".sfxevents";
            if (File.Exists(journal) && !AudioEventReplayRenderer.RenderToSidecar(journal, sidecar, clips))
                throw new InvalidDataException("offline FMOD SFX rendering produced no samples");
            string musicJournal = clips[0].Source + ".music.jsonl";
            string musicSidecar = Path.ChangeExtension(sidecar, "bgmchunks");
            if (File.Exists(musicJournal) && !MusicEventReplayRenderer.RenderToSidecar(
                    musicJournal, musicSidecar, clips, renderContext.ReconstructBgm))
                throw new InvalidDataException("offline FMOD music rendering produced no samples");
            return 0;
        } catch (Exception exception) {
            if (context != IntPtr.Zero && GCHandle.FromIntPtr(context).Target is SfxRenderContext renderContext)
                renderContext.Error = exception;
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/EventReplay");
            return 1;
        }
    }

    private static byte[] ReadNativeBytes(IntPtr pointer, int length) {
        byte[] bytes = new byte[length];
        if (length != 0) Marshal.Copy(pointer, bytes, 0, length);
        return bytes;
    }

    private static void ReportFinalizeProgress(float value, IntPtr context) {
        if (context == IntPtr.Zero) return;
        try {
            if (GCHandle.FromIntPtr(context).Target is Action<double> progress) {
                progress(Math.Clamp(value, 0f, 1f));
            }
        } catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Recorder",
                $"Cannot report finalization progress: {exception.Message}");
        }
    }

    private static void EnsureAvailable() {
        Initialize(null);
        if (!available) throw new DllNotFoundException(loadError ?? "native capture backend is unavailable");
    }

    private static void ThrowIfFailed(int status, string operation) {
        if (status == 0) return;
        throw new InvalidOperationException($"native capture {operation} failed ({status}): {LastError()}");
    }

    internal static string LastError() {
        nuint required = CaptureLastError(IntPtr.Zero, 0);
        if (required <= 1 || required > 64 * 1024) return "unknown native error";
        byte[] bytes = new byte[(int)required];
        unsafe {
            fixed (byte* pointer = bytes) CaptureLastError((IntPtr)pointer, (nuint)bytes.Length);
        }
        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    internal static CaptureStatistics GetStats(ulong handle) {
        ThrowIfFailed(CaptureGetStats(handle, out NativeCaptureStats stats), "stats");
        return new CaptureStatistics(
            stats.Running != 0,
            stats.Width,
            stats.Height,
            stats.QueueDepth,
            stats.FramesCaptured,
            stats.FramesConsumed,
            stats.FramesDropped,
            stats.BytesCaptured,
            stats.LastFrameUnixNanos,
            stats.MediaTimeNanos,
            stats.AudioFramesCaptured,
            stats.AudioChunksDropped
        );
    }

    internal static void Stop(ulong handle) {
        int status = CaptureStop(handle);
        if (status != 0 && status != -4) ThrowIfFailed(status, "stop");
    }

    internal static void RequestKeyframe(ulong handle, ulong timestamp) =>
        ThrowIfFailed(CaptureRequestKeyframe(handle, timestamp), "request keyframe");

    [DllImport(LibraryName, EntryPoint = "mqol_capture_request_keyframe", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureRequestKeyframe(ulong handle, ulong timestamp);

    internal static void Destroy(ulong handle) {
        int status = CaptureDestroy(handle);
        if (status != 0 && status != -2) ThrowIfFailed(status, "destroy");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCaptureStats {
        public uint AbiVersion;
        public uint Running;
        public uint Width;
        public uint Height;
        public uint QueueDepth;
        public ulong FramesCaptured;
        public ulong FramesConsumed;
        public ulong FramesDropped;
        public ulong BytesCaptured;
        public ulong LastFrameUnixNanos;
        public ulong MediaTimeNanos;
        public ulong AudioFramesCaptured;
        public ulong AudioChunksDropped;
    }

    [DllImport(LibraryName, EntryPoint = "mqol_capture_abi_version", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint CaptureAbiVersion();

    [DllImport(LibraryName, EntryPoint = "mqol_capture_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureCreate(byte[] config, nuint configLength, out ulong handle);

    [DllImport(LibraryName, EntryPoint = "mqol_capture_start", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureStart(ulong handle);

    [DllImport(LibraryName, EntryPoint = "mqol_capture_stop", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureStop(ulong handle);

    [DllImport(LibraryName, EntryPoint = "mqol_capture_get_stats", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureGetStats(ulong handle, out NativeCaptureStats stats);

    [DllImport(LibraryName, EntryPoint = "mqol_capture_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureDestroy(ulong handle);

    [DllImport(LibraryName, EntryPoint = "mqol_capture_push_audio", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int CapturePushAudio(
        ulong handle,
        float* samples,
        nuint sampleCount,
        uint sampleRate,
        ushort channels,
        ushort busId,
        ulong timestampNanos
    );

    [DllImport(LibraryName, EntryPoint = "mqol_capture_push_frame", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int CapturePushFrame(ulong handle, byte* pixels, nuint length, uint width, uint height, ulong timestamp);

    [DllImport(LibraryName, EntryPoint = "mqol_capture_last_error", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint CaptureLastError(IntPtr buffer, nuint capacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FinalizeProgressCallback(float progress, IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FinalizeSfxCallback(
        IntPtr clipsJson,
        nuint clipsLength,
        IntPtr sidecarPath,
        nuint sidecarLength,
        IntPtr context
    );

    [DllImport(LibraryName, EntryPoint = "mqol_recording_finalize_with_progress",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int RecordingFinalizeWithProgress(
        byte[] plan,
        nuint planLength,
        FinalizeProgressCallback? progress,
        IntPtr context,
        FinalizeSfxCallback? renderSfx,
        IntPtr renderSfxContext
    );
}

/// <summary>A recording sink, never an acquisition owner. Every instance subscribes to CaptureSource.</summary>
public sealed class NativeCaptureSession : IDisposable {
    private ulong handle;
    private readonly CaptureSubscription subscription;
    private readonly object gate = new();
    private readonly object stopGate = new();
    private bool videoStarted;
    private ulong origin;
    private ulong keyframeRequestedAt, keyframeAcceptedAt;
    internal ulong KeyframeAcceptedAt => Volatile.Read(ref keyframeAcceptedAt);
    internal void RequestKeyframe(ulong timestamp) {
        lock (gate) {
            NativeCaptureBridge.RequestKeyframe(handle, timestamp);
            Volatile.Write(ref keyframeAcceptedAt, 0);
            Volatile.Write(ref keyframeRequestedAt, timestamp);
        }
    }
    // Publish only after the first frame is accepted. Delivery stats lag the
    // game thread by GPU readback + subscriber queues and are not an edit clock.
    internal double? TimeAt(ulong timestamp) {
        ulong start = Volatile.Read(ref origin);
        return start == 0 ? null : timestamp <= start ? 0 : (timestamp - start) / 1_000_000_000d;
    }
    internal double? TimelineTimeSeconds => Volatile.Read(ref origin) == 0 ? null : TimeAt(SdlFrameSource.ClockNanos());
    internal double? FrameTimeAt(ulong timestamp, uint fps, bool? roundUp = null) {
        ulong start = Volatile.Read(ref origin);
        return start == 0 ? null : CaptureFrameClock.SecondsAt(timestamp, start, fps, roundUp);
    }
    private readonly MusicJournal? musicJournal;
    private readonly AudioEventJournal? audioEventJournal;
    private int stopped;
    internal NativeCaptureSession(ulong handle, string? outputPath, uint fps) {
        this.handle = handle;
        musicJournal = outputPath is null ? null : new MusicJournal(outputPath + ".music.jsonl");
        audioEventJournal = outputPath is null ? null : new AudioEventJournal(outputPath + ".sfxevents");
        if (audioEventJournal is not null) AudioEventCapture.SeedListener(audioEventJournal);
        try {
            // New recordings are fully event-only. No FMOD PCM callback is attached;
            // both SFX and music are rendered after the retained video branch is known.
            subscription = CaptureSource.SubscribeRecording(fps, PushFrame, null,
                musicJournal is null ? null : musicJournal.Accept);
        } catch { musicJournal?.Dispose(); audioEventJournal?.Dispose(); throw; }
    }
    public CaptureStatistics Statistics { get { lock (gate) return handle == 0 ? default : NativeCaptureBridge.GetStats(handle); } }
    public CaptureDeliveryStatistics DeliveryStatistics => new(subscription.DroppedFrames,
        subscription.DroppedAudioChunks, subscription.DroppedMusicEvents, subscription.CallbackErrors);
    public bool HasAudioTap => CaptureSource.AudioAvailable;
    internal Task CompleteInput() {
        // Detach immediately at the stop boundary, not when a busy thread pool
        // eventually schedules encoder teardown. Drain callbacks asynchronously.
        subscription.Complete();
        return subscription.Completion;
    }
    public void Stop() {
        // Concurrent Stop/Dispose callers must all wait for the drain, not destroy a
        // handle while the first caller is still waiting for its callback worker.
        lock (stopGate) {
            if (stopped != 0) return;
            stopped = 1;
            subscription.Complete();
            subscription.Completion.GetAwaiter().GetResult();
            lock (gate) {
                try {
                    if (handle != 0) NativeCaptureBridge.Stop(handle);
                    bool complete = subscription.DroppedMusicEvents == 0 && subscription.CallbackErrors == 0
                        && CaptureSource.MusicError is null && videoStarted;
                    // Finish both journals even if one reports an incomplete stream. This
                    // preserves an explicit footer for every sidecar and avoids leaving the
                    // event journal open when the legacy music journal rejects its input.
                    Exception? journalError = null;
                    try { musicJournal?.Finish(complete); } catch (Exception e) { journalError = e; }
                    try { audioEventJournal?.Finish(complete && AudioEventCapture.Failure is null); }
                    catch (Exception e) { journalError ??= e; }
                    if (journalError is not null) throw journalError;
                } finally { musicJournal?.Dispose(); audioEventJournal?.Dispose(); }
            }
        }
    }
    private unsafe void PushFrame(CaptureFrame frame) {
        fixed (byte* pixels = frame.Pixels.Span) {
            int status = NativeCaptureBridge.CapturePushFrame(handle, pixels, (nuint)frame.Pixels.Length,
                (uint)frame.Width, (uint)frame.Height, frame.TimestampNanos);
            if (status != 0) throw new InvalidOperationException(NativeCaptureBridge.LastError());
        }
        if (!videoStarted) {
            videoStarted = true;
            Volatile.Write(ref origin, frame.TimestampNanos);
            musicJournal?.Start(origin);
            audioEventJournal?.Start(origin);
        }
        ulong requested = Volatile.Read(ref keyframeRequestedAt);
        if (requested != 0 && frame.TimestampNanos >= requested)
            Interlocked.CompareExchange(ref keyframeAcceptedAt, frame.TimestampNanos, 0);
    }
    public void Dispose() {
        try { Stop(); }
        finally {
            lock (gate) {
                ulong owned = handle; handle = 0;
                if (owned != 0) NativeCaptureBridge.Destroy(owned);
            }
        }
    }
}

public readonly record struct CaptureDeliveryStatistics(long DroppedFrames, long DroppedAudioChunks,
    long DroppedMusicEvents, long CallbackErrors);

public readonly record struct CaptureStatistics(
    bool Running,
    uint Width,
    uint Height,
    uint QueueDepth,
    ulong FramesCaptured,
    ulong FramesConsumed,
    ulong FramesDropped,
    ulong BytesCaptured,
    ulong LastFrameUnixNanos,
    ulong MediaTimeNanos,
    ulong AudioFramesCaptured,
    ulong AudioChunksDropped
) {
    public double MediaTimeSeconds => MediaTimeNanos / 1_000_000_000.0;
}
