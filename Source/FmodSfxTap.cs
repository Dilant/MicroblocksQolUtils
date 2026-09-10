using System.Reflection;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Installs pass-through DSPs at the tail of Celeste's gameplay audio buses. FMOD calls the
/// read callbacks on its mixer thread, so the callback only copies the bus audio
/// to the output and offers samples once to the shared source bounded queue.
/// </summary>
internal sealed class FmodSfxTap : IDisposable {
    private const int GameplayBusId = 1;
    private const int UiBusId = 2;
    private const int MusicBusId = 3;

    private readonly List<BusTap> taps = [];
    private int disposed;

    private FmodSfxTap() { }

    public static FmodSfxTap? Attach() {
        try {
            FMOD.Studio.System studio = GetStudioSystem();
            Check(studio.getLowLevelSystem(out FMOD.System lowLevel), "get low-level system");
            Check(lowLevel.getSoftwareFormat(out int sampleRate, out _, out _), "get software format");

            FmodSfxTap owner = new();
            owner.TryAttachBus(studio, lowLevel, "bus:/gameplay_sfx", GameplayBusId, sampleRate);
            owner.TryAttachBus(studio, lowLevel, "bus:/ui_sfx", UiBusId, sampleRate);
            owner.TryAttachBus(studio, lowLevel, "bus:/music", MusicBusId, sampleRate);

            if (owner.taps.Count == 0) {
                owner.Dispose();
                return null;
            }
            Logger.Log(
                LogLevel.Info,
                "MicroblocksQolUtils/Recorder",
                $"Attached FMOD audio tap to {owner.taps.Count} bus(es) at {sampleRate} Hz."
            );
            return owner;
        } catch (Exception exception) {
            Logger.Log(
                LogLevel.Warn,
                "MicroblocksQolUtils/Recorder",
                $"Cannot attach FMOD audio tap; video recording will continue without audio: {exception.Message}"
            );
            return null;
        }
    }

    private void TryAttachBus(
        FMOD.Studio.System studio,
        FMOD.System lowLevel,
        string path,
        int busId,
        int sampleRate
    ) {
        try {
            taps.Add(BusTap.Attach(studio, lowLevel, path, busId, sampleRate));
        } catch (Exception exception) {
            Logger.Log(
                LogLevel.Warn,
                "MicroblocksQolUtils/Recorder",
                $"Cannot tap {path}: {exception.Message}"
            );
        }
    }

    private static FMOD.Studio.System GetStudioSystem() {
        FieldInfo? field = typeof(Audio).GetField("system", BindingFlags.Static | BindingFlags.NonPublic);
        if (field?.GetValue(null) is FMOD.Studio.System studio && studio.isValid()) return studio;
        throw new InvalidOperationException("Celeste FMOD Studio system is unavailable");
    }

    private static void Check(FMOD.RESULT result, string operation) {
        if (result != FMOD.RESULT.OK) throw new InvalidOperationException($"FMOD {operation} failed: {result}");
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        for (int index = taps.Count - 1; index >= 0; index--) taps[index].Dispose();
        taps.Clear();
    }

    private sealed class BusTap : IDisposable {
        private readonly FMOD.Studio.Bus bus;
        private readonly FMOD.ChannelGroup group;
        private readonly FMOD.ChannelGroup clockGroup;
        private readonly FMOD.DSP dsp;
        private readonly FMOD.DSP_READCALLBACK callback;
        private readonly int busId;
        private readonly int sampleRate;
        private readonly FmodCaptureClock audioClock = new();
        private int disposed;

        private BusTap(
            FMOD.Studio.Bus bus,
            FMOD.ChannelGroup group,
            FMOD.ChannelGroup clockGroup,
            FMOD.DSP dsp,
            FMOD.DSP_READCALLBACK callback,
            int busId,
            int sampleRate
        ) {
            this.bus = bus;
            this.group = group;
            this.clockGroup = clockGroup;
            this.dsp = dsp;
            this.callback = callback;
            this.busId = busId;
            this.sampleRate = sampleRate;
        }

        public static BusTap Attach(
            FMOD.Studio.System studio,
            FMOD.System lowLevel,
            string path,
            int busId,
            int sampleRate
        ) {
            FmodSfxTap.Check(studio.getBus(path, out FMOD.Studio.Bus bus), $"get bus {path}");
            FmodSfxTap.Check(bus.lockChannelGroup(), $"lock {path}");
            FMOD.ChannelGroup? group = null;
            FMOD.DSP? dsp = null;
            try {
                // Studio creates a locked bus's low-level channel group asynchronously.
                // Flush before querying it or an otherwise valid inactive bus reports NOT_LOADED.
                FmodSfxTap.Check(studio.flushCommands(), $"materialize channel group {path}");
                FmodSfxTap.Check(bus.getChannelGroup(out group), $"get channel group {path}");
                FmodSfxTap.Check(lowLevel.getMasterChannelGroup(out var clockGroup), "get mixer clock group");

                BusTap? owner = null;
                FMOD.DSP_READCALLBACK callback = (ref FMOD.DSP_STATE state, IntPtr input, IntPtr output,
                    uint length, int inputChannels, ref int outputChannels) =>
                    owner?.Read(ref state, input, output, length, inputChannels, ref outputChannels)
                    ?? FMOD.RESULT.OK;
                FMOD.DSP_DESCRIPTION description = new() {
                    pluginsdkversion = FMOD.VERSION.number,
                    name = DspName(path),
                    version = 1,
                    numinputbuffers = 1,
                    numoutputbuffers = 1,
                    read = callback
                };
                FmodSfxTap.Check(lowLevel.createDSP(ref description, out dsp), $"create DSP for {path}");
                owner = new BusTap(bus, group, clockGroup, dsp, callback, busId, sampleRate);
                FmodSfxTap.Check(group.addDSP(FMOD.CHANNELCONTROL_DSP_INDEX.TAIL, dsp), $"add DSP to {path}");
                return owner;
            } catch {
                if (dsp is not null && dsp.isValid()) _ = dsp.release();
                _ = bus.unlockChannelGroup();
                throw;
            }
        }

        private static char[] DspName(string path) {
            char[] name = new char[32];
            string value = path.EndsWith("ui_sfx", StringComparison.Ordinal)
                ? "MQOL UI SFX tap"
                : path.EndsWith("music", StringComparison.Ordinal)
                    ? "MQOL music tap"
                    : "MQOL gameplay SFX tap";
            value.AsSpan(0, Math.Min(value.Length, name.Length - 1)).CopyTo(name);
            return name;
        }

        private unsafe FMOD.RESULT Read(
            ref FMOD.DSP_STATE state,
            IntPtr input,
            IntPtr output,
            uint length,
            int inputChannels,
            ref int outputChannels
        ) {
            _ = state;
            try {
                if (input == IntPtr.Zero || output == IntPtr.Zero || inputChannels <= 0) return FMOD.RESULT.OK;
                if (length > int.MaxValue / inputChannels) return FMOD.RESULT.ERR_INVALID_PARAM;
                int sampleCount = (int)length * inputChannels;
                long byteCount = (long)sampleCount * sizeof(float);
                Buffer.MemoryCopy(input.ToPointer(), output.ToPointer(), byteCount, byteCount);
                outputChannels = inputChannels;
                if (Volatile.Read(ref disposed) == 0) {
                    // A paused bus's own clock STOPS. Mapping it to video shifts
                    // resumed SFX backwards until a large resync, then loses a
                    // block at edits. The master mixer clock keeps real time.
                    ulong now = SdlFrameSource.ClockNanos();
                    FMOD.RESULT result = clockGroup.getDSPClock(out ulong clock, out _);
                    ulong timestamp = result == FMOD.RESULT.OK ? audioClock.Timestamp(clock, sampleRate, now) : now;
                    CaptureSource.PublishAudio((float*)input.ToPointer(), sampleCount, sampleRate, inputChannels, busId, clock, timestamp);
                }
                return FMOD.RESULT.OK;
            } catch {
                // Never unwind managed exceptions through FMOD's real-time mixer thread.
                return FMOD.RESULT.ERR_INTERNAL;
            }
        }

        public void Dispose() {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            // removeDSP synchronizes removal from the shared mix graph. Keep the callback delegate rooted until after DSP release.
            try { if (group.isValid() && dsp.isValid()) _ = group.removeDSP(dsp); } catch { }
            try { if (dsp.isValid()) _ = dsp.release(); } catch { }
            try { if (bus.isValid()) _ = bus.unlockChannelGroup(); } catch { }
            GC.KeepAlive(callback);
        }
    }
}
