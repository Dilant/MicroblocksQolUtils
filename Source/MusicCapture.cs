using System.Collections.ObjectModel;
using System.Reflection;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>One music observer for all consumers. Never installs FMOD event callbacks (which would
/// replace callbacks belonging to the game/mods). Managed command hooks plus state observation
/// preserve switches, same-event restarts, seeks/loops, pause, and parameter changes.</summary>
internal static class MusicCapture {
    private static readonly FieldInfo? alt = typeof(Audio).GetField("currentAltMusicEvent", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly List<Hook> hooks = [];
    private static CaptureMusic[] snapshots = [];
    private static ulong sequence;
    private static bool observing;
    internal static string? Failure { get; private set; }
    private static readonly object observationGate = new();
    internal static CaptureMusic[] Snapshots => Volatile.Read(ref snapshots);

    internal static void Load() {
        snapshots = []; sequence = 0; Failure = null;
        try {
            // Hook the command boundary as well as polling so a switch away and back in one
            // Engine update is not mistaken for uninterrupted playback of the same song.
            Add(typeof(Audio).GetMethod("SetMusic"), (SetMusicHook)SetMusic);
            Add(typeof(Audio).GetMethod("SetAltMusic"), (SetAltHook)SetAlt);
            Add(typeof(Audio).GetMethod("SetMusicParam"), (SetParamHook)SetParam);
            Add(typeof(FMOD.Studio.EventInstance).GetMethod("start"), (StartHook)Start);
            Add(typeof(FMOD.Studio.EventInstance).GetMethod("stop"), (StopHook)Stop);
            Add(typeof(FMOD.Studio.EventInstance).GetMethod("setTimelinePosition"), (SeekHook)Seek);
            Add(typeof(FMOD.Studio.EventInstance).GetMethod("setPaused"), (PauseHook)Pause);
            Add(typeof(FMOD.Studio.EventInstance).GetMethod("setParameterValue"), (ParameterHook)Parameter);
        } catch (Exception e) { Unload(); Fail(e); }
    }
    private static void Add(MethodInfo? method, Delegate detour) {
        if (method is null) throw new MissingMethodException($"Music command hook {detour.Method.Name} is unavailable");
        hooks.Add(new Hook(method, detour));
    }
    private delegate bool SetMusicOrig(string path, bool startPlaying, bool allowFadeOut);
    private delegate bool SetMusicHook(SetMusicOrig orig, string path, bool startPlaying, bool allowFadeOut);
    private static bool SetMusic(SetMusicOrig orig, string path, bool startPlaying, bool allowFadeOut) {
        bool changed = orig(path, startPlaying, allowFadeOut);
        Observe();
        return changed;
    }
    private delegate void SetAltOrig(string path);
    private delegate void SetAltHook(SetAltOrig orig, string path);
    private static void SetAlt(SetAltOrig orig, string path) { orig(path); Observe(); }
    private delegate void SetParamOrig(string name, float value);
    private delegate void SetParamHook(SetParamOrig orig, string name, float value);
    private static void SetParam(SetParamOrig orig, string name, float value) { orig(name, value); Observe(); }
    private delegate FMOD.RESULT StartOrig(FMOD.Studio.EventInstance instance);
    private delegate FMOD.RESULT StartHook(StartOrig orig, FMOD.Studio.EventInstance instance);
    private static FMOD.RESULT Start(StartOrig orig, FMOD.Studio.EventInstance instance) {
        var result = orig(instance); if (result == FMOD.RESULT.OK) Command(instance, "start"); return result;
    }
    private delegate FMOD.RESULT StopOrig(FMOD.Studio.EventInstance instance, FMOD.Studio.STOP_MODE mode);
    private delegate FMOD.RESULT StopHook(StopOrig orig, FMOD.Studio.EventInstance instance, FMOD.Studio.STOP_MODE mode);
    private static FMOD.RESULT Stop(StopOrig orig, FMOD.Studio.EventInstance instance, FMOD.Studio.STOP_MODE mode) {
        var result = orig(instance, mode); if (result == FMOD.RESULT.OK) Command(instance, "stop-" + mode); return result;
    }
    private delegate FMOD.RESULT SeekOrig(FMOD.Studio.EventInstance instance, int position);
    private delegate FMOD.RESULT SeekHook(SeekOrig orig, FMOD.Studio.EventInstance instance, int position);
    private static FMOD.RESULT Seek(SeekOrig orig, FMOD.Studio.EventInstance instance, int position) {
        var result = orig(instance, position); if (result == FMOD.RESULT.OK) Command(instance, "seek", position: position); return result;
    }
    private delegate FMOD.RESULT PauseOrig(FMOD.Studio.EventInstance instance, bool paused);
    private delegate FMOD.RESULT PauseHook(PauseOrig orig, FMOD.Studio.EventInstance instance, bool paused);
    private static FMOD.RESULT Pause(PauseOrig orig, FMOD.Studio.EventInstance instance, bool paused) {
        var result = orig(instance, paused); if (result == FMOD.RESULT.OK) Command(instance, paused ? "pause" : "resume", paused: paused); return result;
    }
    private delegate FMOD.RESULT ParameterOrig(FMOD.Studio.EventInstance instance, string name, float value);
    private delegate FMOD.RESULT ParameterHook(ParameterOrig orig, FMOD.Studio.EventInstance instance, string name, float value);
    private static FMOD.RESULT Parameter(ParameterOrig orig, FMOD.Studio.EventInstance instance, string name, float value) {
        var result = orig(instance, name, value); if (result == FMOD.RESULT.OK) Command(instance, "parameter", parameter: name, value: value); return result;
    }
    private static void Command(FMOD.Studio.EventInstance instance, string kind, int? position = null, bool? paused = null, string? parameter = null, float value = 0) {
        try { RecordCommand(instance, kind, position, paused, parameter, value); }
        catch (Exception e) { Fail(e); }
    }
    private static void RecordCommand(FMOD.Studio.EventInstance instance, string kind, int? position, bool? paused, string? parameter, float value) {
        // Observe only the game's music instances, not every SFX event in the process.
        lock (observationGate) {
            var current = Audio.CurrentMusicEventInstance;
            var alternate = alt?.GetValue(null) as FMOD.Studio.EventInstance;
            string? track = current?.getRaw() == instance.getRaw() ? "main"
                : alternate?.getRaw() == instance.getRaw() ? "alt" : null;
            if (track is null) return;
            CaptureMusic? previous = Snapshots.FirstOrDefault(s => s.Track == track);
            // Celeste sets e.g. the mountain music fade parameter every update, even
            // when it stays at 1. Redundant setters must not split a continuous take.
            if (parameter is not null && previous?.Parameters.TryGetValue(parameter, out float old) == true && old == value) return;
            if (paused is bool requested && previous?.Paused == requested) return;
            var change = Read(track, instance, SdlFrameSource.ClockNanos()) with { Kind = kind, Sequence = ++sequence };
            if (position is int p) change = change with { TimelineMilliseconds = p };
            if (paused is bool pause) change = change with { Paused = pause };
            if (parameter is not null) {
                var parameters = change.Parameters.ToDictionary(p => p.Key, p => p.Value);
                parameters[parameter] = value;
                change = change with { Parameters = new ReadOnlyDictionary<string, float>(parameters) };
            }
            var next = Snapshots.Where(s => s.Track != track).Append(change).OrderBy(s => s.Track == "main" ? 0 : 1).ToArray();
            Volatile.Write(ref snapshots, next);
            CaptureSource.PublishMusic(next, [change]);
        }
    }
    internal static void Update() => Observe();
    internal static void Unload() { foreach (var hook in hooks) hook.Dispose(); hooks.Clear(); snapshots = []; }

    private static void Observe() {
        lock (observationGate) ObserveLocked();
    }
    private static void ObserveLocked() {
        // Commands/update are normally on the game thread; reentrant calls only need the outer snapshot.
        if (observing) return;
        observing = true;
        try {
            ulong now = SdlFrameSource.ClockNanos();
            CaptureMusic[] previous = Snapshots;
            CaptureMusic[] next = [Read("main", Audio.CurrentMusicEventInstance, now),
                Read("alt", alt?.GetValue(null) as FMOD.Studio.EventInstance, now)];
            List<CaptureMusic> changes = [];
            for (int i = 0; i < next.Length; i++) {
                CaptureMusic current = next[i];
                CaptureMusic? old = previous.FirstOrDefault(s => s.Track == current.Track);
                string? kind = old is null ? "snapshot"
                    : current.InstanceId != old.InstanceId || current.Event != old.Event ? (current.InstanceId == 0 ? "stop" : "switch")
                    : current.Paused != old.Paused ? (current.Paused ? "pause" : "resume")
                    : current.PlaybackState != old.PlaybackState ? (current.PlaybackState is "STOPPED" or "STOPPING" ? "stop" : "playback")
                    : !SameParameters(current.Parameters, old.Parameters) ? "parameter"
                    : current.InstanceId != 0 && Math.Abs(current.TimelineMilliseconds - old.TimelineMilliseconds
                        - (old.Paused ? 0 : (long)((now - old.TimestampNanos) / 1_000_000))) > 150 ? "seek-or-loop"
                    : null;
                if (kind is not null) {
                    current = current with { Sequence = ++sequence, Kind = kind };
                    changes.Add(current);
                } else current = current with { Sequence = old!.Sequence };
                next[i] = current;
            }
            Volatile.Write(ref snapshots, next);
            CaptureSource.PublishMusic(next, changes);
        } catch (Exception e) {
            Fail(e);
        } finally { observing = false; }
    }
    private static void Fail(Exception e) {
        if (Failure is not null) return;
        Failure = e.Message;
        Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Capture/Music", $"Music journal cannot be guaranteed complete: {e.Message}");
    }
    private static bool SameParameters(IReadOnlyDictionary<string, float> a, IReadOnlyDictionary<string, float> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out float value) && value == pair.Value);
    private static CaptureMusic Read(string track, FMOD.Studio.EventInstance? instance, ulong now) {
        Dictionary<string, float> parameters = new(StringComparer.Ordinal);
        string name = "", playback = "STOPPED"; ulong id = 0; int timeline = 0; bool paused = false;
        if (instance is not null && instance.isValid()) {
            id = (ulong)instance.getRaw();
            name = Audio.GetEventName(instance) ?? "";
            _ = instance.getTimelinePosition(out timeline); _ = instance.getPaused(out paused);
            if (instance.getPlaybackState(out var state) == FMOD.RESULT.OK) playback = state.ToString();
            if (instance.getParameterCount(out int count) == FMOD.RESULT.OK) {
                for (int i = 0; i < Math.Min(count, 256); i++) {
                    if (instance.getParameterByIndex(i, out var parameter) == FMOD.RESULT.OK
                        && parameter.getDescription(out var description) == FMOD.RESULT.OK
                        && parameter.getValue(out float value) == FMOD.RESULT.OK) parameters[description.name] = value;
                }
            }
        }
        return new(now, 0, "snapshot", track, name, id, Math.Max(0, timeline), paused, playback,
            new ReadOnlyDictionary<string, float>(parameters));
    }
}
