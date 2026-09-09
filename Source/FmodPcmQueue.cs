namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>One SPSC ring per FMOD bus. Slots are allocated before DSP attachment.
/// No allocation, worker mutex, or wait in TryWrite; the worker copies an owned
/// payload before releasing the slot. A defensive writer guard rejects reentrancy.</summary>
internal sealed class FmodPcmQueue {
    internal const int Capacity = 64;
    internal const int MaxSamples = 16_384;
    private sealed class Slot {
        internal readonly float[] Samples = new float[MaxSamples];
        internal int Count, Rate, Channels;
        internal ulong Clock, Timestamp;
    }
    private readonly Slot[] slots = Enumerable.Range(0, Capacity + 1).Select(_ => new Slot()).ToArray();
    private int read, write, writerActive;

    internal bool TryWrite(ReadOnlySpan<float> samples, int rate, int channels, ulong clock, ulong timestamp) {
        if (samples.Length == 0 || samples.Length > MaxSamples || Interlocked.CompareExchange(ref writerActive, 1, 0) != 0)
            return false;
        try {
            int next = (write + 1) % slots.Length;
            if (next == Volatile.Read(ref read)) return false;
            Slot slot = slots[write];
            samples.CopyTo(slot.Samples);
            slot.Count = samples.Length; slot.Rate = rate; slot.Channels = channels;
            slot.Clock = clock; slot.Timestamp = timestamp;
            Volatile.Write(ref write, next);
            return true;
        } finally { Volatile.Write(ref writerActive, 0); }
    }

    // Only the source worker reads. The mixer cannot reuse this slot until the
    // owned copy is complete and read has been published with release semantics.
    internal bool TryRead(int bus, out CaptureAudio? chunk) {
        chunk = null;
        if (read == Volatile.Read(ref write)) return false;
        Slot slot = slots[read];
        chunk = new(slot.Samples.AsMemory(0, slot.Count).ToArray(), slot.Rate, slot.Channels,
            bus, bus switch { 1 => "bus:/gameplay_sfx", 2 => "bus:/ui_sfx", _ => "bus:/music" }, slot.Clock, slot.Timestamp);
        Volatile.Write(ref read, (read + 1) % slots.Length);
        return true;
    }
}
