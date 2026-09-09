namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>Owned, immutable top-down BGRA8 pixels. Safe to retain after the callback.</summary>
public sealed record CaptureFrame(ReadOnlyMemory<byte> Pixels, int Width, int Height, ulong TimestampNanos, ulong Sequence) {
    public int Stride => checked(Width * 4);
}
/// <summary>Owned interleaved float PCM and FMOD bus/DSP metadata; timestamp uses the video source clock.</summary>
public sealed record CaptureAudio(ReadOnlyMemory<float> Samples, int SampleRate, int Channels,
    int BusId, string BusPath, ulong DspClock, ulong TimestampNanos);

/// <summary>
/// Bounded isolated subscriber. Callbacks run serially on a worker, never the game/render/FMOD thread.
/// Dispose cancels queued work without waiting; await Completion before releasing resources used by callbacks.
/// Slow consumers lose old frames/chunks, not renderer or mixer time. Exceptions affect only this subscriber.
/// </summary>
public sealed class CaptureSubscription : IDisposable {
    private readonly object gate = new();
    private readonly Queue<CaptureFrame> frames = new(3);
    private readonly Queue<CaptureAudio> audio = new(32);
    private readonly SemaphoreSlim ready = new(0, 1);
    private readonly Action<CaptureFrame>? pixels;
    private readonly Action<CaptureAudio>? fmod;
    private readonly Action<CaptureSubscription> unregister;
    private readonly ulong subscribedAt;
    private int disposed;
    private long droppedFrames, droppedAudio, callbackErrors;
    internal bool WantsPixels => pixels is not null;
    internal bool WantsAudio => fmod is not null;
    public long DroppedFrames => Interlocked.Read(ref droppedFrames);
    public long DroppedAudioChunks => Interlocked.Read(ref droppedAudio);
    public long CallbackErrors => Interlocked.Read(ref callbackErrors);
    public Task Completion { get; }

    internal CaptureSubscription(Action<CaptureFrame>? pixels, Action<CaptureAudio>? fmod, Action<CaptureSubscription> unregister, ulong subscribedAt = 0) {
        this.subscribedAt = subscribedAt;
        this.pixels = pixels; this.fmod = fmod; this.unregister = unregister;
        Completion = Task.Run(DispatchAsync);
    }
    internal void Offer(CaptureFrame value) {
        if (value.TimestampNanos < subscribedAt || pixels is null || Volatile.Read(ref disposed) != 0) return;
        if (!Monitor.TryEnter(gate)) { Interlocked.Increment(ref droppedFrames); return; }
        try {
            if (disposed != 0) return;
            if (frames.Count == 3) { frames.Dequeue(); Interlocked.Increment(ref droppedFrames); }
            frames.Enqueue(value); Signal();
        } finally { Monitor.Exit(gate); }
    }
    internal void Offer(CaptureAudio value) {
        if (value.TimestampNanos < subscribedAt || fmod is null || Volatile.Read(ref disposed) != 0) return;
        if (!Monitor.TryEnter(gate)) { Interlocked.Increment(ref droppedAudio); return; }
        try {
            if (disposed != 0) return;
            if (audio.Count == 32) { audio.Dequeue(); Interlocked.Increment(ref droppedAudio); }
            audio.Enqueue(value); Signal();
        } finally { Monitor.Exit(gate); }
    }
    private void Signal() { if (ready.CurrentCount == 0) { try { ready.Release(); } catch (SemaphoreFullException) { } } }
    private async Task DispatchAsync() {
        while (true) {
            await ready.WaitAsync().ConfigureAwait(false);
            while (true) {
                CaptureFrame? frame = null; CaptureAudio? chunk = null;
                lock (gate) {
                    if (disposed != 0) return;
                    // Deliver video first to establish recording origin before pending audio.
                    if (frames.Count != 0) frame = frames.Dequeue();
                    else if (audio.Count != 0) chunk = audio.Dequeue();
                    else break;
                }
                try { if (frame is not null) pixels!(frame); else fmod!(chunk!); }
                catch (Exception e) {
                    if (Interlocked.Increment(ref callbackErrors) == 1)
                        Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Capture", $"Subscriber callback failed: {e.Message}");
                }
            }
        }
    }
    public void Dispose() {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        unregister(this);
        lock (gate) { frames.Clear(); audio.Clear(); Signal(); }
    }
}
