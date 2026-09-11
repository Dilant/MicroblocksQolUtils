using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>Managed FMOD command observer. No callbacks are replaced and no I/O runs in hooks.</summary>
internal static class AudioEventCapture {
    private static readonly List<Hook> Hooks = [];
    private static readonly Dictionary<IntPtr, ulong> Instances = [];
    private static readonly object Gate = new();
    private static ulong nextId;
    [ThreadStatic] internal static bool Suppress;
    internal static string? Failure { get; private set; }
    internal static void Load() {
        if (Hooks.Count != 0) return;
        Failure = null;
        try {
            Hooks.Add(new Hook(typeof(FMOD.Studio.EventInstance).GetMethod("start")!, (startHook)start));
            Hooks.Add(new Hook(typeof(FMOD.Studio.EventDescription).GetMethod("createInstance")!, (createInstanceHook)createInstance));
            Hooks.Add(new Hook(typeof(FMOD.Studio.EventInstance).GetMethod("stop")!, (stopHook)stop));
            Hooks.Add(new Hook(typeof(FMOD.Studio.EventInstance).GetMethod("release")!, (releaseHook)release));
            Hooks.Add(new Hook(typeof(FMOD.Studio.EventInstance).GetMethod("setPaused")!, (setPausedHook)setPaused));
            Hooks.Add(new Hook(typeof(FMOD.Studio.EventInstance).GetMethod("setTimelinePosition")!, (setTimelinePositionHook)setTimelinePosition));
            Hooks.Add(new Hook(typeof(FMOD.Studio.EventInstance).GetMethod("setParameterValue")!, (setParameterValueHook)setParameterValue));
            Hooks.Add(new Hook(typeof(FMOD.Studio.EventInstance).GetMethod("setVolume")!, (setVolumeHook)setVolume));
            Hooks.Add(new Hook(typeof(FMOD.Studio.EventInstance).GetMethod("setPitch")!, (setPitchHook)setPitch));
            Hooks.Add(new Hook(typeof(FMOD.Studio.EventInstance).GetMethod("set3DAttributes")!, (set3DHook)set3D));
            Hooks.Add(new Hook(typeof(FMOD.Studio.System).GetMethod("setListenerAttributes")!, (listenerHook)listener));
        } catch (Exception e) { Unload(); Failure = e.Message; }
    }
    internal static void Unload() {
        foreach (var hook in Hooks) hook.Dispose();
        Hooks.Clear();
        lock (Gate) Instances.Clear();
    }
    private static void Record(FMOD.Studio.EventInstance instance, string operation, string? parameter, float? value,
        float[]? attributes = null) {
        if (Suppress) return;
        try {
            lock (Gate) {
                var raw = instance.getRaw();
                if (!Instances.TryGetValue(raw, out ulong id)) Instances[raw] = id = ++nextId;
                string path = Audio.GetEventName(instance) ?? "";
                // Music remains on its existing PCM/event path. SFX journaling must
                // not duplicate the adaptive music graph or its long-lived instances.
                if (!path.StartsWith("event:/music/", StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith("snapshot:/", StringComparison.OrdinalIgnoreCase))
                    AudioEventJournal.Publish(new(SdlFrameSource.ClockNanos(), id,
                        path, operation, parameter, value, State: operation == "start" ? ReadState(instance) : null,
                        Attributes: attributes));
                // release invalidates the native handle asynchronously. Keep the
                // identity until the next create/start so one-shot tails remain
                // replayable after Celeste releases its managed wrapper.
            }
        } catch (Exception e) { Failure ??= e.Message; }
    }
    private static AudioInstanceState ReadState(FMOD.Studio.EventInstance instance) {
        Dictionary<string, float> parameters = new(StringComparer.Ordinal);
        if (instance.getParameterCount(out int count) == FMOD.RESULT.OK) {
            for (int i = 0; i < Math.Min(count, 256); i++) {
                if (instance.getParameterByIndex(i, out var parameter) == FMOD.RESULT.OK
                    && parameter.getDescription(out var description) == FMOD.RESULT.OK
                    && parameter.getValue(out float value) == FMOD.RESULT.OK)
                    parameters[description.name] = value;
            }
        }
        _ = instance.getVolume(out float volume, out _);
        _ = instance.getPitch(out float pitch, out _);
        _ = instance.getPaused(out bool paused);
        _ = instance.getTimelinePosition(out int timeline);
        float[]? attributes = instance.get3DAttributes(out var position) == FMOD.RESULT.OK ? Pack(position) : null;
        return new(parameters, volume, pitch, paused, timeline, attributes);
    }
    private static float[] Pack(FMOD.Studio._3D_ATTRIBUTES value) => [
        value.position.x, value.position.y, value.position.z,
        value.velocity.x, value.velocity.y, value.velocity.z,
        value.forward.x, value.forward.y, value.forward.z,
        value.up.x, value.up.y, value.up.z
    ];
    internal static void SeedListener(AudioEventJournal journal) {
        if (Audio.System is { } system && system.isValid()
            && system.getListenerAttributes(0, out var attributes) == FMOD.RESULT.OK)
            journal.Accept(new(SdlFrameSource.ClockNanos(), 0, "", "listener", Value: 0, Attributes: Pack(attributes)));
    }
    private delegate FMOD.RESULT set3DOrig(FMOD.Studio.EventInstance instance, FMOD.Studio._3D_ATTRIBUTES attributes);
    private delegate FMOD.RESULT set3DHook(set3DOrig orig, FMOD.Studio.EventInstance instance, FMOD.Studio._3D_ATTRIBUTES attributes);
    private static FMOD.RESULT set3D(set3DOrig orig, FMOD.Studio.EventInstance instance, FMOD.Studio._3D_ATTRIBUTES attributes) {
        var result = orig(instance, attributes);
        if (result == FMOD.RESULT.OK) Record(instance, "attributes", null, null, Pack(attributes));
        return result;
    }
    private delegate FMOD.RESULT listenerOrig(FMOD.Studio.System system, int index, FMOD.Studio._3D_ATTRIBUTES attributes);
    private delegate FMOD.RESULT listenerHook(listenerOrig orig, FMOD.Studio.System system, int index, FMOD.Studio._3D_ATTRIBUTES attributes);
    private static FMOD.RESULT listener(listenerOrig orig, FMOD.Studio.System system, int index, FMOD.Studio._3D_ATTRIBUTES attributes) {
        var result = orig(system, index, attributes);
        if (!Suppress && result == FMOD.RESULT.OK && Audio.System?.getRaw() == system.getRaw())
            AudioEventJournal.Publish(new(SdlFrameSource.ClockNanos(), 0, "", "listener", Value: index, Attributes: Pack(attributes)));
        return result;
    }
    private delegate FMOD.RESULT createInstanceOrig(FMOD.Studio.EventDescription description,
        out FMOD.Studio.EventInstance instance);
    private delegate FMOD.RESULT createInstanceHook(createInstanceOrig orig,
        FMOD.Studio.EventDescription description, out FMOD.Studio.EventInstance instance);
    private static FMOD.RESULT createInstance(createInstanceOrig orig,
        FMOD.Studio.EventDescription description, out FMOD.Studio.EventInstance instance) {
        var result = orig(description, out instance);
        if (!Suppress && result == FMOD.RESULT.OK && instance.isValid()) {
            lock (Gate) Instances[instance.getRaw()] = ++nextId;
        }
        return result;
    }
    private delegate FMOD.RESULT startOrig(FMOD.Studio.EventInstance instance);
    private delegate FMOD.RESULT startHook(startOrig orig, FMOD.Studio.EventInstance instance);
    private static FMOD.RESULT start(startOrig orig, FMOD.Studio.EventInstance instance) {
        var result = orig(instance);
        if (result == FMOD.RESULT.OK) Record(instance, "start", null, null);
        return result;
    }
    private delegate FMOD.RESULT stopOrig(FMOD.Studio.EventInstance instance, FMOD.Studio.STOP_MODE mode);
    private delegate FMOD.RESULT stopHook(stopOrig orig, FMOD.Studio.EventInstance instance, FMOD.Studio.STOP_MODE mode);
    private static FMOD.RESULT stop(stopOrig orig, FMOD.Studio.EventInstance instance, FMOD.Studio.STOP_MODE mode) {
        var result = orig(instance, mode);
        if (result == FMOD.RESULT.OK) Record(instance, "stop", null, (float)mode);
        return result;
    }
    private delegate FMOD.RESULT releaseOrig(FMOD.Studio.EventInstance instance);
    private delegate FMOD.RESULT releaseHook(releaseOrig orig, FMOD.Studio.EventInstance instance);
    private static FMOD.RESULT release(releaseOrig orig, FMOD.Studio.EventInstance instance) {
        Record(instance, "release", null, null);
        var result = orig(instance);
        return result;
    }
    private delegate FMOD.RESULT setPausedOrig(FMOD.Studio.EventInstance instance, bool value);
    private delegate FMOD.RESULT setPausedHook(setPausedOrig orig, FMOD.Studio.EventInstance instance, bool value);
    private static FMOD.RESULT setPaused(setPausedOrig orig, FMOD.Studio.EventInstance instance, bool value) {
        var result = orig(instance, value);
        if (result == FMOD.RESULT.OK) Record(instance, "setPaused", null, value ? 1f : 0f);
        return result;
    }
    private delegate FMOD.RESULT setTimelinePositionOrig(FMOD.Studio.EventInstance instance, int value);
    private delegate FMOD.RESULT setTimelinePositionHook(setTimelinePositionOrig orig, FMOD.Studio.EventInstance instance, int value);
    private static FMOD.RESULT setTimelinePosition(setTimelinePositionOrig orig, FMOD.Studio.EventInstance instance, int value) {
        var result = orig(instance, value);
        if (result == FMOD.RESULT.OK) Record(instance, "setTimelinePosition", null, value);
        return result;
    }
    private delegate FMOD.RESULT setParameterValueOrig(FMOD.Studio.EventInstance instance, string name, float value);
    private delegate FMOD.RESULT setParameterValueHook(setParameterValueOrig orig, FMOD.Studio.EventInstance instance, string name, float value);
    private static FMOD.RESULT setParameterValue(setParameterValueOrig orig, FMOD.Studio.EventInstance instance, string name, float value) {
        var result = orig(instance, name, value);
        if (result == FMOD.RESULT.OK) Record(instance, "setParameterValue", name, value);
        return result;
    }
    private delegate FMOD.RESULT setVolumeOrig(FMOD.Studio.EventInstance instance, float value);
    private delegate FMOD.RESULT setVolumeHook(setVolumeOrig orig, FMOD.Studio.EventInstance instance, float value);
    private static FMOD.RESULT setVolume(setVolumeOrig orig, FMOD.Studio.EventInstance instance, float value) {
        var result = orig(instance, value);
        if (result == FMOD.RESULT.OK) Record(instance, "setVolume", null, value);
        return result;
    }
    private delegate FMOD.RESULT setPitchOrig(FMOD.Studio.EventInstance instance, float value);
    private delegate FMOD.RESULT setPitchHook(setPitchOrig orig, FMOD.Studio.EventInstance instance, float value);
    private static FMOD.RESULT setPitch(setPitchOrig orig, FMOD.Studio.EventInstance instance, float value) {
        var result = orig(instance, value);
        if (result == FMOD.RESULT.OK) Record(instance, "setPitch", null, value);
        return result;
    }
}
