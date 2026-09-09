using System.Buffers;
using System.Runtime.InteropServices;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>The only acquisition owner: one SDL/PBO pipeline and one DSP per FMOD bus.</summary>
public static class CaptureSource {
    private static readonly object gate = new();
    private static CaptureSubscription[] subscriptions = [];
    private static FmodSfxTap? tap;
    private static CancellationTokenSource? cancellation;
    private static Task? worker;
    private static long retryAudioAt;
    private static bool loaded;
    private static string? workerError;
    private static readonly object audioGate = new();
    private static readonly Queue<PendingAudio> pendingAudio = new(32);
    private static long sourceAudioDrops;
    private sealed record PendingAudio(float[] Buffer, int Count, int Rate, int Channels, int Bus, ulong Clock, ulong Timestamp);
    public static int SubscriberCount => Volatile.Read(ref subscriptions).Length;
    public static bool AudioAvailable => Volatile.Read(ref tap) is not null;
    public static string? VideoError => Volatile.Read(ref workerError) ?? SdlFrameSource.Failure;
    public static long DroppedAudioChunks => Interlocked.Read(ref sourceAudioDrops);
    internal static bool WantsPixels => Volatile.Read(ref subscriptions).Any(s => s.WantsPixels);

    /// <summary>Register any combination of pixel and FMOD callbacks. Dispose the returned registration to unsubscribe.</summary>
    public static CaptureSubscription Subscribe(Action<CaptureFrame>? pixels = null, Action<CaptureAudio>? fmod = null) {
        if (pixels is null && fmod is null) throw new ArgumentException("At least one callback is required");
        lock (gate) {
            if (!loaded) throw new InvalidOperationException("Capture source has not been loaded");
            if (pixels is not null && VideoError is { } error) throw new NotSupportedException(error);
            if (subscriptions.Length >= 16) throw new InvalidOperationException("At most 16 capture subscribers are supported");
            CaptureSubscription subscription = new(pixels, fmod, Remove, SdlFrameSource.ClockNanos());
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
            SdlFrameSource.Load();
            cancellation = new();
            loaded = true;
            CancellationToken token = cancellation.Token;
            worker = Task.Run(() => PumpAsync(token));
        }
    }
    /// <summary>Called on the game thread: FMOD graph attach/detach never runs inside DSP or user callbacks.</summary>
    internal static void Update() {
        SdlFrameSource.Update();
        bool wantsAudio = Volatile.Read(ref subscriptions).Any(s => s.WantsAudio);
        if (!wantsAudio) { Interlocked.Exchange(ref tap, null)?.Dispose(); retryAudioAt = 0; }
        else if (tap is null && Environment.TickCount64 >= retryAudioAt) {
            retryAudioAt = Environment.TickCount64 + 2_000;
            tap = FmodSfxTap.Attach();
        }
    }
    // FMOD real-time producer: nonblocking, pooled copy only, no user code or native encoder calls.
    internal static unsafe void PublishAudio(float* input, int count, int rate, int channels, int bus, ulong dspClock, ulong timestamp) {
        if (count <= 0 || count > 16_384 || !Monitor.TryEnter(audioGate)) { Interlocked.Increment(ref sourceAudioDrops); return; }
        try {
            if (pendingAudio.Count == 32) { Interlocked.Increment(ref sourceAudioDrops); return; }
            float[] buffer = ArrayPool<float>.Shared.Rent(count);
            new ReadOnlySpan<float>(input, count).CopyTo(buffer);
            pendingAudio.Enqueue(new(buffer, count, rate, channels, bus, dspClock, timestamp));
        } finally { Monitor.Exit(audioGate); }
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
                        byte[] pixels = new byte[checked((int)native.Length)];
                        Marshal.Copy(native.Pixels, pixels, 0, pixels.Length);
                        CaptureFrame frame = new(pixels, (int)native.Width, (int)native.Height, native.Timestamp, native.Sequence);
                        foreach (var subscription in Volatile.Read(ref subscriptions)) subscription.Offer(frame);
                    } finally { SdlFrameSource.Free(native.Pixels, native.Length); }
                }
                for (int i = 0; i < 32; i++) {
                    PendingAudio pending;
                    lock (audioGate) { if (!pendingAudio.TryDequeue(out pending!)) break; }
                    work = true;
                    try {
                        // One immutable payload shared by all subscribers. No copies on the mixer thread per subscriber.
                        CaptureAudio chunk = new(pending.Buffer.AsMemory(0, pending.Count).ToArray(), pending.Rate,
                            pending.Channels, pending.Bus, pending.Bus switch {1 => "bus:/gameplay_sfx", 2 => "bus:/ui_sfx", _ => "bus:/music"}, pending.Clock, pending.Timestamp);
                        foreach (var subscription in Volatile.Read(ref subscriptions)) subscription.Offer(chunk);
                    } finally { ArrayPool<float>.Shared.Return(pending.Buffer); }
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
        lock (gate) { loaded = false; old = subscriptions; Volatile.Write(ref subscriptions, []); }
        foreach (var subscription in old) subscription.Dispose();
        Interlocked.Exchange(ref tap, null)?.Dispose();
        SdlFrameSource.Unload();
        cancellation?.Cancel();
        worker?.GetAwaiter().GetResult();
        cancellation?.Dispose(); cancellation = null; worker = null;
        lock (audioGate) { while (pendingAudio.TryDequeue(out var item)) ArrayPool<float>.Shared.Return(item.Buffer); }
    }
}
