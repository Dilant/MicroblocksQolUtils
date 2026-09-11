using System.Runtime.InteropServices;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>Replays the SFX journal through an independent FMOD Studio system.
/// The live Celeste system is never switched to an NRT output and is never used for replay.</summary>
internal static class AudioEventReplayRenderer {
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int MaxRenderTailSeconds = 2;
    private static readonly object RenderGate = new();

    internal static bool RenderToSidecar(string journalPath, string sidecarPath, double durationSeconds) {
        AudioEventSource source = AudioEventSource.Read(journalPath);
        return RenderToSidecar(source, sidecarPath, durationSeconds, null);
    }

    internal static bool RenderToSidecar(string journalPath, string sidecarPath,
        IReadOnlyList<RecordingClip> clips) {
        AudioEventSource source = AudioEventSource.Read(journalPath);
        double duration = clips.Count == 0 ? 0 : clips.Max(c => c.StartSeconds + c.DurationSeconds);
        return RenderToSidecar(source, sidecarPath, duration, clips);
    }

    internal static bool RenderCommandsToSidecar(IReadOnlyList<AudioCommand> commands,
        string sidecarPath, double durationSeconds) =>
        RenderToSidecar(new AudioEventSource(0, commands), sidecarPath, durationSeconds, null, 3);

    private static bool RenderToSidecar(AudioEventSource source, string sidecarPath,
        double durationSeconds, IReadOnlyList<RecordingClip>? clips, ushort busId = 1) {
        if (durationSeconds <= 0) return false;
        lock (RenderGate) {
            FMOD.Studio.System? studio = null;
            bool previousSuppress = AudioEventCapture.Suppress;
            AudioEventCapture.Suppress = true;
            try {
                Check(FMOD.Studio.System.create(out studio), "create offline Studio system");
                Check(studio.getLowLevelSystem(out FMOD.System lowLevel), "get offline low-level system");
                Check(lowLevel.setSoftwareFormat(SampleRate, FMOD.SPEAKERMODE.STEREO, 0), "set offline format");
                Check(lowLevel.setOutput(FMOD.OUTPUTTYPE.NOSOUND_NRT), "set offline NRT output");
                Check(studio.initialize(1024, FMOD.Studio.INITFLAGS.SYNCHRONOUS_UPDATE,
                    FMOD.INITFLAGS.NORMAL, IntPtr.Zero), "initialize offline Studio system");
                LoadBanks(studio);

                using MixerCapture capture = new(sidecarPath, clips, busId);
                Check(lowLevel.getMasterChannelGroup(out FMOD.ChannelGroup master), "get offline master group");
                capture.Attach(lowLevel, master);
                Dictionary<ulong, FMOD.Studio.EventInstance> instances = [];
                IReadOnlyList<AudioCommand> commands = clips is null
                    ? source.Commands
                    : FilterCommands(source, clips);
                double elapsed = clips is null ? 0 : Math.Max(0, clips.Min(clip => clip.StartSeconds) - MaxRenderTailSeconds);
                lowLevel.getDSPBufferSize(out uint blockSamples, out _);
                double blockSeconds = blockSamples > 0 ? blockSamples / (double)SampleRate : 512d / SampleRate;
                double end = durationSeconds + (clips is null ? MaxRenderTailSeconds : 0);
                int index = 0;
                double[] clipEnds = clips is null
                    ? []
                    : clips.Select(clip => clip.StartSeconds + clip.DurationSeconds)
                        .OrderBy(value => value).ToArray();
                int clipEndIndex = 0;
                while (elapsed < end) {
                    while (clipEndIndex < clipEnds.Length && elapsed >= clipEnds[clipEndIndex] - 1e-9) {
                        foreach (FMOD.Studio.EventInstance instance in instances.Values.ToArray()) {
                            if (instance.isValid()) _ = instance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
                            if (instance.isValid()) _ = instance.release();
                        }
                        instances.Clear();
                        clipEndIndex++;
                    }
                    if (clips is not null && clipEndIndex < clips.Count) {
                        // Decay the previous cut's reverb for at most two seconds, then
                        // jump over discarded source time without storing silence.
                        elapsed = Math.Max(elapsed, clips[clipEndIndex].StartSeconds - MaxRenderTailSeconds);
                    }
                    while (index < commands.Count
                        && RelativeSeconds(source, commands[index].TimestampNanos) <= elapsed + 1e-9) {
                        Apply(commands[index++], studio, instances);
                    }
                    capture.SetTime(elapsed);
                    Check(studio.update(), "offline Studio update");
                    capture.ThrowIfFailed();
                    elapsed = Math.Max(elapsed + blockSeconds, capture.TimeSeconds);
                }
                foreach (FMOD.Studio.EventInstance instance in instances.Values) {
                    if (instance.isValid()) _ = instance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
                    if (instance.isValid()) _ = instance.release();
                }
                Check(studio.flushCommands(), "flush offline Studio commands");
                capture.Detach(master);
                return capture.WrittenFrames > 0;
            } catch (Exception exception) {
                Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/OfflineSfx");
                return false;
            } finally {
                AudioEventCapture.Suppress = previousSuppress;
                try { if (studio is not null && studio.isValid()) _ = studio.release(); } catch { }
            }
        }
    }

    private static IReadOnlyList<AudioCommand> FilterCommands(AudioEventSource source,
        IReadOnlyList<RecordingClip> clips) {
        double first = clips.Min(c => c.StartSeconds);
        double last = clips.Max(c => c.StartSeconds + c.DurationSeconds);
        double prefixStart = Math.Max(0, first - MaxRenderTailSeconds);
        bool Retained(double value) => clips.Any(c => value >= c.StartSeconds - 1e-9
            && value <= c.StartSeconds + c.DurationSeconds + 1e-9);
        HashSet<ulong> included = [];
        List<AudioCommand> result = [];
        foreach (AudioCommand command in source.Commands) {
            double time = RelativeSeconds(source, command.TimestampNanos);
            bool inPrefix = time >= prefixStart && time < first;
            bool keep = command.Operation == "start"
                ? time <= last + 1e-9 && (Retained(time) || inPrefix)
                : Retained(time) || inPrefix;
            if (command.Operation == "start" && keep) included.Add(command.InstanceId);
            if (command.Operation != "listener" && command.Operation != "start"
                && !included.Contains(command.InstanceId)) keep = false;
            if (keep) result.Add(command);
        }
        // Keep listener state across edits even when its last update is in discarded time.
        foreach (RecordingClip clip in clips) {
            AudioCommand? listener = source.Commands.LastOrDefault(command => command.Operation == "listener"
                && RelativeSeconds(source, command.TimestampNanos) <= clip.StartSeconds);
            if (listener is not null && !result.Contains(listener))
                result.Add(listener with { TimestampNanos = source.OriginNanos
                    + (ulong)(Math.Max(0, clip.StartSeconds - MaxRenderTailSeconds) * 1_000_000_000d) });
        }
        return result.OrderBy(command => command.TimestampNanos).ToArray();
    }

    private static double RelativeSeconds(AudioEventSource source, ulong timestamp) =>
        timestamp <= source.OriginNanos ? 0d : (timestamp - source.OriginNanos) / 1_000_000_000d;

    private static void LoadBanks(FMOD.Studio.System studio) {
        string root = FindCelesteRoot();
        string bankRoot = Path.Combine(root, "Content", "FMOD", "Desktop");
        string[] names = ["Master Bank.bank", "Master Bank.strings.bank", "sfx.bank", "ui.bank", "dlc_sfx.bank",
            "music.bank", "dlc_music.bank"];
        foreach (string name in names) {
            string path = Path.Combine(bankRoot, name);
            if (!File.Exists(path)) continue;
            Check(studio.loadBankFile(path, FMOD.Studio.LOAD_BANK_FLAGS.NORMAL, out var bank), $"load bank {name}");
            if (bank.isValid()) Check(bank.loadSampleData(), $"load samples {name}");
        }
        Check(studio.flushSampleLoading(), "flush FMOD samples");
    }

    private static string FindCelesteRoot() {
        string root = Environment.GetEnvironmentVariable("CELESTE_ROOT")
            ?? Path.GetDirectoryName(typeof(Audio).Assembly.Location)!;
        if (!File.Exists(Path.Combine(root, "Celeste.dll")))
            throw new DirectoryNotFoundException($"Celeste installation not found: {root}");
        return root;
    }

    private static void Apply(AudioCommand command, FMOD.Studio.System studio,
        Dictionary<ulong, FMOD.Studio.EventInstance> instances) {
        if (command.Operation == "listener") {
            if (Unpack(command.Attributes) is { } listener) _ = studio.setListenerAttributes((int)command.Value.GetValueOrDefault(), listener);
            return;
        }
        if (command.EventPath.StartsWith("event:/music/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(command.Bus, "music", StringComparison.OrdinalIgnoreCase)) return;
        if (command.Operation == "start") {
            if (!instances.TryGetValue(command.InstanceId, out var instance) || !instance.isValid()) {
                if (studio.getEvent(command.EventPath, out var description) != FMOD.RESULT.OK
                    || description.createInstance(out instance) != FMOD.RESULT.OK) return;
            }
            if (command.State is { } state) {
                foreach (var pair in state.Parameters) _ = instance.setParameterValue(pair.Key, pair.Value);
                _ = instance.setVolume(state.Volume); _ = instance.setPitch(state.Pitch); _ = instance.setPaused(state.Paused);
                if (Unpack(state.Attributes) is { } position) _ = instance.set3DAttributes(position);
            }
            if (instance.start() == FMOD.RESULT.OK) {
                instances[command.InstanceId] = instance;
                if (command.State?.TimelineMilliseconds is > 0) _ = instance.setTimelinePosition(command.State.TimelineMilliseconds);
            }
            else _ = instance.release();
            return;
        }
        if (!instances.TryGetValue(command.InstanceId, out var target) || !target.isValid()) return;
        switch (command.Operation) {
            case "stop": _ = target.stop((FMOD.Studio.STOP_MODE)(int)command.Value.GetValueOrDefault()); break;
            // Own the offline handle until the cut so released one-shot tails can
            // still be stopped when a discarded branch begins.
            case "release": break;
            case "setPaused": _ = target.setPaused(command.Value.GetValueOrDefault() != 0); break;
            case "setTimelinePosition": _ = target.setTimelinePosition((int)command.Value.GetValueOrDefault()); break;
            case "setParameterValue" when command.Parameter is not null:
                _ = target.setParameterValue(command.Parameter, command.Value.GetValueOrDefault()); break;
            case "setVolume": _ = target.setVolume(command.Value.GetValueOrDefault()); break;
            case "setPitch": _ = target.setPitch(command.Value.GetValueOrDefault()); break;
            case "attributes" when Unpack(command.Attributes) is { } attributes:
                _ = target.set3DAttributes(attributes); break;
        }
    }

    private static FMOD.Studio._3D_ATTRIBUTES? Unpack(float[]? values) {
        if (values is not { Length: 12 } || values.Any(v => !float.IsFinite(v))) return null;
        return new FMOD.Studio._3D_ATTRIBUTES {
            position = new FMOD.VECTOR { x = values[0], y = values[1], z = values[2] },
            velocity = new FMOD.VECTOR { x = values[3], y = values[4], z = values[5] },
            forward = new FMOD.VECTOR { x = values[6], y = values[7], z = values[8] },
            up = new FMOD.VECTOR { x = values[9], y = values[10], z = values[11] }
        };
    }

    private static void Check(FMOD.RESULT result, string operation) {
        if (result != FMOD.RESULT.OK) throw new InvalidOperationException($"FMOD {operation} failed: {result}");
    }

    private sealed class MixerCapture : IDisposable {
        private readonly BinaryWriter writer;
        private readonly IReadOnlyList<RecordingClip>? clips;
        private readonly ushort busId;
        private long timelineFrame;
        private float[] monoToStereo = [];
        private Exception? failure;
        internal long WrittenFrames { get; private set; }
        internal double TimeSeconds => timelineFrame / (double)SampleRate;
        private FMOD.DSP dsp = default!;
        private FMOD.DSP_READCALLBACK? callback;
        internal MixerCapture(string path, IReadOnlyList<RecordingClip>? clips, ushort busId) {
            this.clips = clips;
            this.busId = busId;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            writer = new BinaryWriter(File.Create(path));
            writer.Write("MQOLAUD1"u8.ToArray());
        }
        internal void SetTime(double seconds) => timelineFrame = (long)Math.Round(seconds * SampleRate);
        internal void ThrowIfFailed() {
            if (failure is not null) throw new IOException("Cannot write offline SFX samples", failure);
        }
        internal void Attach(FMOD.System system, FMOD.ChannelGroup group) {
            MixerCapture owner = this;
            callback = (ref FMOD.DSP_STATE state, IntPtr input, IntPtr output, uint length,
                int inputChannels, ref int outputChannels) => owner.Read(input, output, length, inputChannels, ref outputChannels);
            FMOD.DSP_DESCRIPTION description = new() {
                pluginsdkversion = FMOD.VERSION.number, name = "MQOL offline SFX".PadRight(32, '\0').ToCharArray(),
                version = 1, numinputbuffers = 1, numoutputbuffers = 1, read = callback
            };
            Check(system.createDSP(ref description, out dsp), "create offline capture DSP");
            Check(group.addDSP(FMOD.CHANNELCONTROL_DSP_INDEX.TAIL, dsp), "attach offline capture DSP");
        }
        private FMOD.RESULT Read(IntPtr input, IntPtr output, uint length, int inputChannels, ref int outputChannels) {
            if (input == IntPtr.Zero || output == IntPtr.Zero || inputChannels <= 0) return FMOD.RESULT.OK;
            outputChannels = inputChannels;
            try {
                if (inputChannels is not (1 or Channels)) throw new InvalidDataException($"Offline FMOD mixed {inputChannels} channels");
                unsafe {
                    ReadOnlySpan<float> samples = new((void*)input, checked((int)length * inputChannels));
                    samples.CopyTo(new Span<float>((void*)output, samples.Length));
                    if (inputChannels == 1) {
                        int count = checked((int)length * Channels);
                        if (monoToStereo.Length < count) monoToStereo = new float[count];
                        for (int frame = 0; frame < (int)length; frame++)
                            monoToStereo[frame * 2] = monoToStereo[frame * 2 + 1] = samples[frame];
                        samples = monoToStereo.AsSpan(0, count);
                    }
                    double start = TimeSeconds;
                    double end = (timelineFrame + length) / (double)SampleRate;
                    if (clips is null || clips.Any(clip => start < clip.StartSeconds + clip.DurationSeconds
                        && end > clip.StartSeconds)) {
                        for (int first = 0; first < (int)length; first += 8192) {
                            int frames = Math.Min(8192, (int)length - first);
                            writer.Write((ulong)((timelineFrame + first) * 1_000_000_000L / SampleRate));
                            writer.Write(SampleRate); writer.Write((ushort)Channels); writer.Write(busId);
                            writer.Write((uint)frames); writer.Write((uint)(frames * Channels));
                            writer.Write(MemoryMarshal.AsBytes(samples.Slice(first * Channels, frames * Channels)));
                            WrittenFrames += frames;
                        }
                    }
                }
            } catch (Exception exception) { failure ??= exception; }
            timelineFrame += length;
            return FMOD.RESULT.OK;
        }
        internal void Detach(FMOD.ChannelGroup group) {
            if (dsp.isValid()) _ = group.removeDSP(dsp);
        }
        public void Dispose() => writer.Dispose();
    }
}
