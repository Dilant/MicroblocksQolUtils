namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>Per-bus mapping of FMOD's sample clock into the shared monotonic presentation clock.</summary>
internal sealed class FmodCaptureClock {
    private ulong anchorDsp, anchorTime, lastDsp;
    private int rate;
    internal ulong Timestamp(ulong dspClock, int sampleRate, ulong now) {
        if (rate != sampleRate || dspClock < lastDsp || anchorTime == 0) Reset(dspClock, sampleRate, now);
        ulong delta = dspClock - anchorDsp;
        ulong value = anchorTime + delta / (ulong)sampleRate * 1_000_000_000
            + delta % (ulong)sampleRate * 1_000_000_000 / (ulong)sampleRate;
        // Re-anchor after a device suspension/reset; normal mixer scheduling jitter doesn't move PCM timestamps.
        if (value > now && value - now > 250_000_000 || now > value && now - value > 250_000_000) {
            Reset(dspClock, sampleRate, now); value = now;
        }
        lastDsp = dspClock;
        return value;
    }
    private void Reset(ulong clock, int sampleRate, ulong now) { anchorDsp = lastDsp = clock; anchorTime = now; rate = sampleRate; }
}
