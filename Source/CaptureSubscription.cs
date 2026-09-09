namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>Top-down BGRA8 pixels. Subscribe delivers owned data; SubscribeBorrowed
/// lends pixels only until callback return. Snapshot makes an owned retained copy.</summary>
public sealed record CaptureFrame(ReadOnlyMemory<byte> Pixels, int Width, int Height, ulong TimestampNanos, ulong Sequence) {
    public int Stride => checked(Width * 4);
    internal FrameLease? Lease { get; init; }
    public CaptureFrame Snapshot() => new(Pixels.ToArray(), Width, Height, TimestampNanos, Sequence);
}
/// <summary>Owned interleaved float PCM and FMOD bus/DSP metadata; timestamp uses the video source clock.</summary>
public sealed record CaptureAudio(ReadOnlyMemory<float> Samples, int SampleRate, int Channels,
    int BusId, string BusPath, ulong DspClock, ulong TimestampNanos);

/// <summary>Music control/state on the same clock as PCM/video, independent of video edits.
/// Track is main/alt; InstanceId distinguishes restarts of the same event. Parameters are owned snapshots.
/// Command timestamps describe when the game issued a change, not FMOD's eventual audible sample.</summary>
public sealed record CaptureMusic(ulong TimestampNanos, ulong Sequence, string Kind, string Track,
    string Event, ulong InstanceId, int TimelineMilliseconds, bool Paused, string PlaybackState,
    IReadOnlyDictionary<string, float> Parameters);

/// <summary>
/// Bounded isolated subscriber. Callbacks run serially on a worker, never the game/render/FMOD thread.
/// Dispose cancels queued work without waiting; await Completion before releasing resources used by callbacks.
/// Slow consumers lose old frames/chunks, not renderer or mixer time. Exceptions affect only this subscriber.
/// </summary>
public sealed class CaptureSubscription : IDisposable {
    private readonly object gate = new();
    private readonly Queue<CaptureFrame> frames = new(3);
    internal const int AudioCapacity = 256;
    private readonly Queue<CaptureAudio> audio = new(AudioCapacity);
    private readonly Queue<CaptureMusic> musicEvents = new(256);
    private readonly SemaphoreSlim ready = new(0, 1);
    private readonly Action<CaptureFrame>? pixels;
    private readonly Action<CaptureAudio>? fmod;
    private readonly Action<CaptureMusic>? music;
    private readonly Action<CaptureSubscription> unregister;
    private readonly ulong subscribedAt;
    private readonly uint maxFrameRate;
    private readonly int frameCapacity;
    private ulong? frameOrigin;
    private UInt128 lastFrameTick;
    private int disposed;
    private long droppedFrames, droppedAudio, droppedMusic, callbackErrors;
    internal bool WantsPixels => pixels is not null;
    internal bool WantsAudio => fmod is not null;
    internal bool WantsMusic => music is not null;
    internal bool BorrowsPixels { get; }
    internal uint MaxFrameRate => maxFrameRate;
    public long DroppedFrames => Interlocked.Read(ref droppedFrames);
    public long DroppedAudioChunks => Interlocked.Read(ref droppedAudio);
    public long CallbackErrors => Interlocked.Read(ref callbackErrors);
    /// <summary>Nonzero means the event journal is incomplete; do not reconstruct BGM from it.</summary>
    public long DroppedMusicEvents => Interlocked.Read(ref droppedMusic);
    public Task Completion { get; }

    internal CaptureSubscription(Action<CaptureFrame>? pixels, Action<CaptureAudio>? fmod, Action<CaptureSubscription> unregister, ulong subscribedAt = 0, Action<CaptureMusic>? music = null, bool borrowsPixels = false, uint maxFrameRate = 0) {
        this.maxFrameRate = maxFrameRate;
        // Recorder delivery tolerates ~83ms at 60Hz for codec startup / IO bursts.
        // The shared 128-MiB lease pool still bounds memory, including held frames.
        frameCapacity = maxFrameRate == 0 ? 3 : 5;
        BorrowsPixels = borrowsPixels;
        this.subscribedAt = subscribedAt;
        this.pixels = pixels; this.fmod = fmod; this.unregister = unregister;
        this.music = music;
        Completion = Task.Run(DispatchAsync);
    }
    internal void Offer(CaptureMusic value) {
        // Initial snapshots may predate registration by one update; keep their actual timestamp.
        if (music is null || Volatile.Read(ref disposed) != 0) return;
        lock (gate) {
            if (disposed != 0) return;
            if (musicEvents.Count == 256) { Interlocked.Increment(ref droppedMusic); return; }
            musicEvents.Enqueue(value); Signal();
        }
    }
    internal void Offer(CaptureFrame value) {
        if (value.TimestampNanos < subscribedAt || pixels is null || Volatile.Read(ref disposed) != 0) return;
        lock (gate) {
            if (disposed != 0) return;
            // Select recording frames BEFORE the bounded callback queue. A 60-fps
            // recorder must not compete with redundant 120/144-Hz presentations.
            // Ordinary subscribers (rate=0) still receive every acquired frame.
            if (maxFrameRate != 0) {
                if (frameOrigin is ulong origin) {
                    if (value.TimestampNanos < origin) return;
                    UInt128 tick = ((UInt128)(value.TimestampNanos - origin) * maxFrameRate + 500_000_000) / 1_000_000_000;
                    if (tick <= lastFrameTick) return;
                    lastFrameTick = tick;
                } else { frameOrigin = value.TimestampNanos; lastFrameTick = 0; }
            }
            value.Lease?.Retain();
            if (frames.Count == frameCapacity) { frames.Dequeue().Lease?.Release(); Interlocked.Increment(ref droppedFrames); }
            frames.Enqueue(value); Signal();
        }
    }
    internal void Offer(CaptureAudio value) {
        if (value.TimestampNanos < subscribedAt || fmod is null || Volatile.Read(ref disposed) != 0) return;
        lock (gate) {
            if (disposed != 0) return;
            if (audio.Count == AudioCapacity) { audio.Dequeue(); Interlocked.Increment(ref droppedAudio); }
            audio.Enqueue(value); Signal();
        }
    }
    private void Signal() { if (ready.CurrentCount == 0) { try { ready.Release(); } catch (SemaphoreFullException) { } } }
    private async Task DispatchAsync() {
        int turn = 0;
        while (true) {
            await ready.WaitAsync().ConfigureAwait(false);
            while (true) {
                CaptureFrame? frame = null; CaptureAudio? chunk = null; CaptureMusic? change = null;
                lock (gate) {
                    if (disposed == 1) return;
                    // Round-robin: first video establishes origin, but continuous
                    // video must not starve music/PCM (nor vice versa).
                    for (int i = 0; i < 3; i++) {
                        int lane = turn; turn = (turn + 1) % 3;
                        if (lane == 0 && frames.Count != 0) { frame = frames.Dequeue(); break; }
                        if (lane == 1 && musicEvents.Count != 0) { change = musicEvents.Dequeue(); break; }
                        if (lane == 2 && audio.Count != 0) { chunk = audio.Dequeue(); break; }
                    }
                    if (frame is null && change is null && chunk is null) {
                        if (disposed == 2) return;
                        break;
                    }
                }
                try { if (frame is not null) pixels!(frame); else if (change is not null) music!(change); else fmod!(chunk!); }
                catch (Exception e) {
                    if (Interlocked.Increment(ref callbackErrors) == 1)
                        Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Capture", $"Subscriber callback failed: {e.Message}");
                }
                finally { frame?.Lease?.Release(); }
            }
        }
    }
    /// <summary>Unregister, then drain already accepted work. Unlike Dispose this preserves the final music events.</summary>
    public void Complete() {
        if (Interlocked.CompareExchange(ref disposed, 2, 0) != 0) return;
        unregister(this);
        lock (gate) Signal();
    }
    public void Dispose() {
        int previous = Interlocked.Exchange(ref disposed, 1);
        if (previous == 1) return;
        if (previous == 0) unregister(this);
        lock (gate) {
            Interlocked.Add(ref droppedMusic, musicEvents.Count);
            while (frames.TryDequeue(out var frame)) frame.Lease?.Release();
            audio.Clear(); musicEvents.Clear(); Signal();
        }
    }
}
