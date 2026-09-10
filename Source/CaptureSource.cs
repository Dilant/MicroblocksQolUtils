using System.Runtime.InteropServices;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>The only acquisition owner: one native frame pipeline and one DSP per FMOD bus.</summary>
public static class CaptureSource {
    private static readonly object gate = new();
    private static CaptureSubscription[] subscriptions = [];
    private static CaptureMusic[] musicSnapshots = [];
    private static FmodSfxTap? tap;
    private static CancellationTokenSource? cancellation;
    private static Task? worker;
    private static long retryAudioAt;
    private static bool loaded;
    private static string? workerError;
    private static FmodPcmQueue[] audioRings = [];
    private static CaptureFramePool framePool = new();
    private static long sourceFrameDrops;
    private static long sourceAudioDrops;
    public static int SubscriberCount => Volatile.Read(ref subscriptions).Length;
    public static bool AudioAvailable => Volatile.Read(ref tap) is not null;
    public static string? VideoError => Volatile.Read(ref workerError) ?? SdlFrameSource.Failure;
    public static string? MusicError => MusicCapture.Failure;
    public static string VideoBackend => SdlFrameSource.Backend;
    public static long DroppedAudioChunks => Interlocked.Read(ref sourceAudioDrops);
    public static long DroppedFrames => Interlocked.Read(ref sourceFrameDrops);
    internal static bool WantsPixels => Volatile.Read(ref subscriptions).Any(s => s.WantsPixels);
    internal static CaptureSubscription[] FrameRoutes => Volatile.Read(ref subscriptions);

    /// <summary>Register any combination of pixel and FMOD callbacks. Dispose the returned registration to unsubscribe.</summary>
    public static CaptureSubscription Subscribe(Action<CaptureFrame>? pixels = null, Action<CaptureAudio>? fmod = null, Action<CaptureMusic>? music = null) {
        return SubscribeCore(pixels, fmod, music, false);
    }
    /// <summary>Allocation-light pixels valid ONLY during the synchronous callback.
    /// Use frame.Snapshot() to retain or pass pixels to asynchronous work. PCM/music remain owned.
    /// Callbacks still run on isolated workers, not the renderer/mixer.</summary>
    public static CaptureSubscription SubscribeBorrowed(Action<CaptureFrame>? pixels = null, Action<CaptureAudio>? fmod = null, Action<CaptureMusic>? music = null) {
        return SubscribeCore(pixels, fmod, music, true);
    }
    internal static CaptureSubscription SubscribeRecording(uint fps, Action<CaptureFrame> pixels, Action<CaptureAudio>? fmod, Action<CaptureMusic>? music) =>
        SubscribeCore(pixels, fmod, music, true, fps);
    private static CaptureSubscription SubscribeCore(Action<CaptureFrame>? pixels, Action<CaptureAudio>? fmod, Action<CaptureMusic>? music, bool borrowed, uint maxFrameRate = 0) {
        if (pixels is null && fmod is null && music is null) throw new ArgumentException("At least one callback is required");
        lock (gate) {
            if (!loaded) throw new InvalidOperationException("Capture source has not been loaded");
            if (pixels is not null && VideoError is { } error) throw new NotSupportedException(error);
            if (subscriptions.Length >= 16) throw new InvalidOperationException("At most 16 capture subscribers are supported");
            uint used = 0;
            foreach (var existing in subscriptions) used |= existing.SourceMask;
            uint mask = 1;
            while ((used & mask) != 0) mask <<= 1;
            CaptureSubscription subscription = new(pixels, fmod, Remove, SdlFrameSource.ClockNanos(), music, borrowed, maxFrameRate) { SourceMask = mask };
            foreach (var snapshot in musicSnapshots) subscription.Offer(snapshot with { Kind = "snapshot" });
            Volatile.Write(ref subscriptions, [.. subscriptions, subscription]);
            return subscription;
        }
    }
    private static void Remove(CaptureSubscription value) {
        lock (gate) Volatile.Write(ref subscriptions, subscriptions.Where(s => !ReferenceEquals(s, value)).ToArray());
    }
    internal static void Load() {
        lock (gate) {
            if (loaded) return;
            if (!NativeCaptureBridge.Available) return;
            workerError = null;
            audioRings = [new(), new(), new()];
            framePool = new();
            musicSnapshots = [];
            SdlFrameSource.Load();
            MusicCapture.Load();
            cancellation = new();
            loaded = true;
            CancellationToken token = cancellation.Token;
            worker = Task.Run(() => PumpAsync(token));
        }
    }
    /// <summary>Called on the game thread: FMOD graph attach/detach never runs inside DSP or user callbacks.</summary>
    internal static void Update() {
        SdlFrameSource.Update();
        MusicCapture.Update();
        bool wantsAudio = Volatile.Read(ref subscriptions).Any(s => s.WantsAudio);
        if (!wantsAudio) { Interlocked.Exchange(ref tap, null)?.Dispose(); retryAudioAt = 0; }
        else if (tap is null && Environment.TickCount64 >= retryAudioAt) {
            retryAudioAt = Environment.TickCount64 + 2_000;
            tap = FmodSfxTap.Attach();
        }
    }
    internal static void PublishMusic(CaptureMusic[] latest, IEnumerable<CaptureMusic> changes) {
        lock (gate) {
            musicSnapshots = latest;
            foreach (var change in changes)
                foreach (var subscription in subscriptions) subscription.Offer(change);
        }
    }
    // FMOD real-time producer: preallocated bus-local ring, no worker locks or allocations.
    internal static unsafe void PublishAudio(float* input, int count, int rate, int channels, int bus, ulong dspClock, ulong timestamp) {
        var rings = audioRings;
        if (input == null || count <= 0 || count > FmodPcmQueue.MaxSamples || bus < 1 || bus > rings.Length
            || !rings[bus - 1].TryWrite(new ReadOnlySpan<float>(input, count), rate, channels, dspClock, timestamp))
            Interlocked.Increment(ref sourceAudioDrops);
    }
    private static async Task PumpAsync(CancellationToken token) {
        try {
            while (!token.IsCancellationRequested) {
                bool work = false;
                // Drain a bounded batch so audio is never starved by a fast video source.
                for (int i = 0; i < 3; i++) {
                    int result = SdlFrameSource.Poll(out var native);
                    if (result < 0) throw new InvalidOperationException(NativeCaptureBridge.LastError());
                    if (result == 0) break;
                    work = true;
                    try {
                        int length = checked((int)native.Length);
                        FrameLease? lease = framePool.Rent(length);
                        if (lease is null) { Interlocked.Increment(ref sourceFrameDrops); continue; }
                        try {
                            Marshal.Copy(native.Pixels, lease.Buffer, 0, length);
                            CaptureFrame borrowed = new(lease.Buffer.AsMemory(0, length), (int)native.Width,
                                (int)native.Height, native.Timestamp, native.Sequence) { Lease = lease };
                            CaptureFrame? owned = null;
                            foreach (var subscription in Volatile.Read(ref subscriptions)) {
                                if (!subscription.WantsPixels || !subscription.ReceivesSourceFrame(native.RouteMask, native.RouteVersion)) continue;
                                subscription.Offer(subscription.BorrowsPixels ? borrowed : owned ??= borrowed.Snapshot());
                            }
                        } finally { lease.Release(); }
                    } finally { SdlFrameSource.Free(native.Pixels, native.Length); }
                }
                for (int bus = 1; bus <= audioRings.Length; bus++) {
                    for (int i = 0; i < FmodPcmQueue.Capacity && audioRings[bus - 1].TryRead(bus, out var chunk); i++) {
                        work = true;
                        foreach (var subscription in Volatile.Read(ref subscriptions)) subscription.Offer(chunk!);
                    }
                }
                await Task.Delay(work ? 1 : 4, token).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) {
            workerError = e.Message;
            Logger.LogDetailed(e, "MicroblocksQolUtils/Capture/Source");
        }
    }
    internal static void Unload() {
        CaptureSubscription[] old;
        lock (gate) { loaded = false; old = subscriptions; musicSnapshots = []; Volatile.Write(ref subscriptions, []); }
        foreach (var subscription in old) subscription.Dispose();
        Interlocked.Exchange(ref tap, null)?.Dispose();
        SdlFrameSource.Unload();
        MusicCapture.Unload();
        cancellation?.Cancel();
        worker?.GetAwaiter().GetResult();
        cancellation?.Dispose(); cancellation = null; worker = null;
        audioRings = [];
    }
}
