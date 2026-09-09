using System.Collections.Concurrent;

namespace Celeste.Mod.MicroblocksQolUtils;

// A single source worker rents; any subscriber worker may return. Bounds include
// checked-out buffers, so a slow borrowed callback cannot cause unlimited growth.
internal sealed class CaptureFramePool {
    private const int MaxBuffers = 32;
    private const int MaxBytes = 128 * 1024 * 1024;
    private readonly ConcurrentQueue<byte[]> free = new();
    private int buffers, bytes;

    internal FrameLease? Rent(int length) {
        if (free.TryDequeue(out var buffer)) {
            if (buffer.Length < length) {
                if (bytes - buffer.Length + length > MaxBytes) { free.Enqueue(buffer); return null; }
                bytes += length - buffer.Length;
                buffer = GC.AllocateUninitializedArray<byte>(length);
            }
            return new(this, buffer);
        }
        if (buffers == MaxBuffers || bytes + length > MaxBytes) return null;
        buffers++; bytes += length;
        return new(this, GC.AllocateUninitializedArray<byte>(length));
    }
    internal void Return(byte[] buffer) => free.Enqueue(buffer);
}

internal sealed class FrameLease(CaptureFramePool pool, byte[] buffer) {
    internal byte[] Buffer { get; } = buffer;
    private int references = 1;
    internal void Retain() => Interlocked.Increment(ref references);
    internal void Release() { if (Interlocked.Decrement(ref references) == 0) pool.Return(Buffer); }
}
