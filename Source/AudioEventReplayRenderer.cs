using System.Runtime.InteropServices;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>Replays the SFX journal through an independent FMOD Studio system.
/// The live Celeste system is never switched to an NRT output and is never used for replay.</summary>
internal static class AudioEventReplayRenderer {
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int MaxRenderTailSeconds = 2;
    private static readonly object RenderGate = new();
    // A stream that has not buffered within this wall-clock budget is rendered as-is
    // (e.g. a disk stall); it must never hang finalization.
    private const int MaxStreamPrimeMilliseconds = 3000;
    private sealed record ReplayVoice(FMOD.Studio.EventInstance Instance, bool FinishNaturally,
        bool Stream, bool Primed = false);

    internal static bool RenderToSidecar(string journalPath, string sidecarPath, double durationSeconds) {
        AudioEventSource source = AudioEventSource.Read(journalPath);
        return RenderToSidecar(source, sidecarPath, durationSeconds, null);
    }

    internal static bool RenderToSidecar(string journalPath, string sidecarPath,
        IReadOnlyList<RecordingClip> clips) {
        AudioEventSource source = AudioEventSource.Read(journalPath);
        return RenderToSidecar(new AudioEventSource(0, PlanSfxCommands(source, clips)),
            sidecarPath, clips.Sum(c => c.DurationSeconds), clips);
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
                // Studio delays stream starts by 8192 samples (171ms) to hide buffering.
                // PrimeStreams buffers in real time instead, so that gap would only be
                // recorded silence, e.g. at every clip that resumes mid-song.
                Check(studio.getAdvancedSettings(out FMOD.Studio.ADVANCEDSETTINGS advanced), "get offline Studio settings");
                advanced.streamingscheduledelay = 1;
                Check(studio.setAdvancedSettings(advanced), "set offline Studio settings");
                Check(studio.initialize(1024, FMOD.Studio.INITFLAGS.SYNCHRONOUS_UPDATE,
                    FMOD.INITFLAGS.STREAM_FROM_UPDATE, IntPtr.Zero), "initialize offline Studio system");
                Dictionary<string, Guid> eventIds = LoadBanks(studio, source.Commands);

                using MixerCapture capture = new(sidecarPath, clips, busId);
                Check(lowLevel.getMasterChannelGroup(out FMOD.ChannelGroup master), "get offline master group");
                capture.Attach(lowLevel, master);
                Dictionary<ulong, ReplayVoice> instances = [];
                IReadOnlyList<AudioCommand> commands = source.Commands;
                double elapsed = 0;
                lowLevel.getDSPBufferSize(out uint blockSamples, out _);
                double blockSeconds = blockSamples > 0 ? blockSamples / (double)SampleRate : 512d / SampleRate;
                double end = durationSeconds + (clips is null ? MaxRenderTailSeconds : 0);
                int index = 0;
                long primeWaitMs = 0;
                while (elapsed < end) {
                    while (index < commands.Count
                        && RelativeSeconds(source, commands[index].TimestampNanos) <= elapsed + 1e-9) {
                        Apply(commands[index++], studio, instances, eventIds);
                    }
                    if (primeWaitMs < MaxStreamPrimeMilliseconds)
                        primeWaitMs += PrimeStreams(studio, master, capture, instances,
                            MaxStreamPrimeMilliseconds - primeWaitMs);
                    capture.SetTime(elapsed);
                    Check(studio.update(), "offline Studio update");
                    capture.ThrowIfFailed();
                    foreach (var (id, voice) in instances.ToArray()) {
                        if (!voice.Instance.isValid()) { instances.Remove(id); continue; }
                        if (voice.Instance.getPlaybackState(out var state) == FMOD.RESULT.OK
                            && state == FMOD.Studio.PLAYBACK_STATE.STOPPED) {
                            _ = voice.Instance.release();
                            instances.Remove(id);
                        }
                    }
                    elapsed = Math.Max(elapsed + blockSeconds, capture.TimeSeconds);
                }
                foreach (var voice in instances.Values) {
                    var instance = voice.Instance;
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

    /// <summary>Streamed events decode on FMOD's wall-clock stream thread while this
    /// renderer runs ~200x real time, so a fresh stream would be mixed as digital
    /// silence until it catches up. Freeze the virtual timeline (paused master, no
    /// captured frames) and give the stream real time until it is audibly ready.</summary>
    private static long PrimeStreams(FMOD.Studio.System studio, FMOD.ChannelGroup master,
        MixerCapture capture, Dictionary<ulong, ReplayVoice> instances, long budgetMilliseconds) {
        if (!instances.Values.Any(v => v.Stream && !v.Primed)) return 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Check(master.setPaused(true), "pause offline mix for stream priming");
        capture.Hold = true;
        try {
            // The first update lets Studio create the instrument channels and sounds.
            do {
                Check(studio.update(), "offline Studio update");
                if (instances.Values.All(v => !v.Stream || v.Primed || StreamReady(v))) break;
                Thread.Sleep(1);
            } while (clock.ElapsedMilliseconds < budgetMilliseconds);
        } finally {
            capture.Hold = false;
            _ = master.setPaused(false);
        }
        MarkPrimed(instances);
        return clock.ElapsedMilliseconds;
    }

    private static void MarkPrimed(Dictionary<ulong, ReplayVoice> instances) {
        foreach (var (id, voice) in instances.ToArray())
            if (voice.Stream && !voice.Primed) instances[id] = voice with { Primed = true };
    }

    private static bool StreamReady(ReplayVoice voice) {
        if (!voice.Instance.isValid()) return true;
        if (voice.Instance.getPlaybackState(out var state) != FMOD.RESULT.OK) return true;
        if (state is FMOD.Studio.PLAYBACK_STATE.STOPPED or FMOD.Studio.PLAYBACK_STATE.STOPPING) return true;
        if (state == FMOD.Studio.PLAYBACK_STATE.STARTING) return false;
        if (voice.Instance.getChannelGroup(out var group) != FMOD.RESULT.OK || !group.isValid()) return false;
        int channels = 0;
        return GroupReady(group, ref channels, 0) && channels > 0;
    }

    private static bool GroupReady(FMOD.ChannelGroup group, ref int channels, int depth) {
        if (depth > 16) return true;
        if (group.getNumChannels(out int count) == FMOD.RESULT.OK)
            for (int i = 0; i < count; i++) {
                if (group.getChannel(i, out var channel) != FMOD.RESULT.OK || !channel.isValid()) continue;
                if (channel.getCurrentSound(out var sound) != FMOD.RESULT.OK || !sound.isValid()) continue;
                channels++;
                if (sound.getOpenState(out var open, out _, out bool starving, out _) != FMOD.RESULT.OK) continue;
                if (starving || open is FMOD.OPENSTATE.LOADING or FMOD.OPENSTATE.BUFFERING
                    or FMOD.OPENSTATE.CONNECTING or FMOD.OPENSTATE.SEEKING or FMOD.OPENSTATE.SETPOSITION) return false;
            }
        if (group.getNumGroups(out int groups) == FMOD.RESULT.OK)
            for (int i = 0; i < groups; i++)
                if (group.getGroup(i, out var child) == FMOD.RESULT.OK && child.isValid()
                    && !GroupReady(child, ref channels, depth + 1)) return false;
        return true;
    }

    internal static IReadOnlyList<AudioCommand> PlanSfxCommands(AudioEventSource source,
        IReadOnlyList<RecordingClip> clips) {
        // Replay on the OUTPUT clock. Discarded time must neither start sounds nor
        // consume the remaining duration of a retained one-shot.
        Dictionary<ulong, ulong> included = [];
        ulong nextId = 0;
        double output = 0;
        RecordingClip? previous = null;
        List<AudioCommand> result = [];
        foreach (RecordingClip clip in clips) {
            ulong At(double time) => (ulong)Math.Round(Math.Max(0, time) * 1_000_000_000d);
            bool cut = previous is not null && (previous.Source != clip.Source
                || Math.Abs(previous.StartSeconds + previous.DurationSeconds - clip.StartSeconds) > 1e-7);
            if (cut) {
                result.Add(new(At(output), 0, "", "cut"));
                // The old voices can finish, but commands in a new branch must
                // not modify them, even if a native handle is reused.
                included.Clear();
            }
            AudioCommand? listener = source.Commands.LastOrDefault(command => command.Operation == "listener"
                && RelativeSeconds(source, command.TimestampNanos) <= clip.StartSeconds);
            if (listener is not null) result.Add(listener with { TimestampNanos = At(output) });
            foreach (AudioCommand command in source.Commands) {
                double time = RelativeSeconds(source, command.TimestampNanos);
                if (time < clip.StartSeconds || time >= clip.StartSeconds + clip.DurationSeconds) continue;
                ulong timestamp = At(output + time - clip.StartSeconds);
                if (command.Operation == "listener") {
                    result.Add(command with { TimestampNanos = timestamp });
                    continue;
                }
                if (command.Operation == "start") {
                    if (included.TryGetValue(command.InstanceId, out ulong oldId))
                        result.Add(command with { TimestampNanos = timestamp, InstanceId = oldId, Operation = "stop" });
                    included[command.InstanceId] = ++nextId;
                }
                if (included.TryGetValue(command.InstanceId, out ulong id))
                    result.Add(command with { TimestampNanos = timestamp, InstanceId = id });
            }
            output += clip.DurationSeconds;
            previous = clip;
        }
        return result.OrderBy(command => command.TimestampNanos).ToArray();
    }

    private static double RelativeSeconds(AudioEventSource source, ulong timestamp) =>
        timestamp <= source.OriginNanos ? 0d : (timestamp - source.OriginNanos) / 1_000_000_000d;

    private static Dictionary<string, Guid> LoadBanks(FMOD.Studio.System studio, IReadOnlyList<AudioCommand> commands) {
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
        // Mod banks frequently have no strings bank: Everest resolves their .guids
        // aliases itself. Recreate both the bank and that lookup in the offline
        // system instead of treating an unknown path as a successfully silent event.
        HashSet<string> wanted = commands.Where(c => c.Operation == "start")
            .Select(c => c.EventPath).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, Guid> eventIds = new(StringComparer.Ordinal);
        foreach (var (asset, liveBank) in Audio.Banks.ModCache.ToArray()) {
            if (!liveBank.isValid()) continue;
            Check(liveBank.getEventList(out var events), $"enumerate bank {asset.PathVirtual}");
            bool needed = false;
            foreach (var description in events) {
                string path = Audio.GetEventName(description) ?? "";
                Check(description.getID(out Guid id), $"identify event {path}");
                if (!wanted.Contains(path) && !wanted.Contains("guid://" + id)) continue;
                eventIds[path] = id;
                needed = true;
            }
            if (!needed) continue;
            // Stream works for both unpacked and ZIP-backed Everest assets. FMOD's
            // loadBankMemory copies the bytes, so no live bank/stream is reused.
            using Stream input = asset.Stream;
            using MemoryStream bytes = new();
            input.CopyTo(bytes);
            Check(studio.loadBankMemory(bytes.ToArray(), FMOD.Studio.LOAD_BANK_FLAGS.NORMAL, out var bank),
                $"load mod bank {asset.PathVirtual}");
            Check(bank.loadSampleData(), $"load mod samples {asset.PathVirtual}");
        }
        Check(studio.flushSampleLoading(), "flush FMOD samples");
        return eventIds;
    }

    private static string FindCelesteRoot() {
        string root = Environment.GetEnvironmentVariable("CELESTE_ROOT")
            ?? Path.GetDirectoryName(typeof(Audio).Assembly.Location)!;
        if (!File.Exists(Path.Combine(root, "Celeste.dll")))
            throw new DirectoryNotFoundException($"Celeste installation not found: {root}");
        return root;
    }

    private static void Apply(AudioCommand command, FMOD.Studio.System studio,
        Dictionary<ulong, ReplayVoice> instances, IReadOnlyDictionary<string, Guid> eventIds) {
        if (command.Operation == "cut") {
            foreach (var (id, voice) in instances.ToArray()) {
                if (voice.FinishNaturally) continue;
                if (voice.Instance.isValid()) {
                    _ = voice.Instance.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);
                    _ = voice.Instance.release();
                }
                instances.Remove(id);
            }
            return;
        }
        if (command.Operation == "listener") {
            if (Unpack(command.Attributes) is { } listener) _ = studio.setListenerAttributes((int)command.Value.GetValueOrDefault(), listener);
            return;
        }
        if (command.EventPath.StartsWith("event:/music/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(command.Bus, "music", StringComparison.OrdinalIgnoreCase)) return;
        if (command.Operation == "start") {
            FMOD.Studio.EventInstance instance;
            bool finishNaturally, stream;
            if (!instances.TryGetValue(command.InstanceId, out var existing) || !existing.Instance.isValid()) {
                FMOD.Studio.EventDescription description;
                FMOD.RESULT resolved = eventIds.TryGetValue(command.EventPath, out Guid id)
                    || (command.EventPath.StartsWith("guid://", StringComparison.OrdinalIgnoreCase)
                        && Guid.TryParse(command.EventPath.AsSpan(7), out id))
                    ? studio.getEventByID(id, out description)
                    : studio.getEvent(command.EventPath, out description);
                Check(resolved, $"resolve replay event {command.EventPath}");
                Check(description.createInstance(out instance), $"create replay event {command.EventPath}");
                finishNaturally = !string.Equals(command.Bus, "music", StringComparison.OrdinalIgnoreCase)
                    && description.isOneshot(out bool oneShot) == FMOD.RESULT.OK && oneShot;
                stream = description.isStream(out bool isStream) == FMOD.RESULT.OK && isStream;
            } else {
                instance = existing.Instance;
                finishNaturally = existing.FinishNaturally;
                stream = existing.Stream;
            }
            if (command.State is { } state) {
                foreach (var pair in state.Parameters) _ = instance.setParameterValue(pair.Key, pair.Value);
                _ = instance.setVolume(state.Volume); _ = instance.setPitch(state.Pitch); _ = instance.setPaused(state.Paused);
                if (Unpack(state.Attributes) is { } position) _ = instance.set3DAttributes(position);
            }
            if (instance.start() == FMOD.RESULT.OK) {
                instances[command.InstanceId] = new(instance, finishNaturally, stream);
                if (command.State?.TimelineMilliseconds is > 0) _ = instance.setTimelinePosition(command.State.TimelineMilliseconds);
            }
            else {
                _ = instance.release();
                throw new InvalidOperationException($"Cannot start replay event {command.EventPath}");
            }
            return;
        }
        if (!instances.TryGetValue(command.InstanceId, out var targetVoice) || !targetVoice.Instance.isValid()) return;
        var target = targetVoice.Instance;
        switch (command.Operation) {
            case "stop" when !targetVoice.FinishNaturally:
                _ = target.stop((FMOD.Studio.STOP_MODE)(int)command.Value.GetValueOrDefault()); break;
            // Released one-shots remain owned until they finish naturally.
            case "release": break;
            case "setPaused": _ = target.setPaused(command.Value.GetValueOrDefault() != 0); break;
            case "setTimelinePosition":
                _ = target.setTimelinePosition((int)command.Value.GetValueOrDefault());
                // A stream seek discards its buffer; prime it again before mixing on.
                if (targetVoice.Stream) instances[command.InstanceId] = targetVoice with { Primed = false };
                break;
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
        private int clipIndex;
        private long clipOutputStart;
        private double clipOutputEndSeconds;
        private readonly ushort busId;
        private long timelineFrame;
        private float[] monoToStereo = [];
        private Exception? failure;
        internal long WrittenFrames { get; private set; }
        /// <summary>While set, mixed blocks belong to no output time and are discarded.</summary>
        internal volatile bool Hold;
        internal double TimeSeconds => timelineFrame / (double)SampleRate;
        private FMOD.DSP dsp = default!;
        private FMOD.DSP_READCALLBACK? callback;
        internal MixerCapture(string path, IReadOnlyList<RecordingClip>? clips, ushort busId) {
            this.clips = clips;
            clipOutputEndSeconds = clips is { Count: > 0 } ? clips[0].DurationSeconds : 0;
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
            if (Hold) return FMOD.RESULT.OK;
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
                    for (int first = 0; first < (int)length;) {
                        long frame = timelineFrame + first;
                        int frames = Math.Min(8192, (int)length - first);
                        if (clips is not null) {
                            while (clipIndex < clips.Count && frame >= (long)Math.Round(clipOutputEndSeconds * SampleRate)) {
                                clipOutputStart = (long)Math.Round(clipOutputEndSeconds * SampleRate);
                                if (++clipIndex < clips.Count) clipOutputEndSeconds += clips[clipIndex].DurationSeconds;
                            }
                            if (clipIndex == clips.Count) break;
                            frames = (int)Math.Min(frames, (long)Math.Round(clipOutputEndSeconds * SampleRate) - frame);
                            // The native muxer consumes source-clock chunks. Map the
                            // continuous mix back to each retained interval, splitting
                            // DSP blocks exactly at edits (never drop a partial block).
                            frame = (long)Math.Round(clips[clipIndex].StartSeconds * SampleRate) + frame - clipOutputStart;
                        }
                        writer.Write((ulong)(frame * 1_000_000_000L / SampleRate));
                        writer.Write(SampleRate); writer.Write((ushort)Channels); writer.Write(busId);
                        writer.Write((uint)frames); writer.Write((uint)(frames * Channels));
                        writer.Write(MemoryMarshal.AsBytes(samples.Slice(first * Channels, frames * Channels)));
                        WrittenFrames += frames;
                        first += frames;
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
