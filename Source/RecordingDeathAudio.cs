using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

// These one-shots are not SoundSources on the saved entity list. Restoring that
// list alone does not stop their tails (especially with instant respawns).
internal static class RecordingDeathAudio {
    private static Hook? hook;
    private static readonly List<FMOD.Studio.EventInstance> Playing = [];
    private static readonly object Gate = new();
    private delegate FMOD.RESULT StartOrig(FMOD.Studio.EventInstance instance);
    private delegate FMOD.RESULT StartHook(StartOrig orig, FMOD.Studio.EventInstance instance);

    internal static bool IsDeathEvent(string path) => path is
        "event:/char/madeline/predeath" or "event:/char/madeline/death";

    internal static void Load() {
        hook = new Hook(typeof(FMOD.Studio.EventInstance).GetMethod("start")!, (StartHook)Start);
    }

    private static FMOD.RESULT Start(StartOrig orig, FMOD.Studio.EventInstance instance) {
        FMOD.RESULT result = orig(instance);
        if (result != FMOD.RESULT.OK || !AutoRecorder.IsRecording) return result;
        if (IsDeathEvent(Audio.GetEventName(instance))) {
            lock (Gate) {
                Playing.RemoveAll(item => !item.isValid());
                if (!Playing.Contains(instance)) Playing.Add(instance);
            }
        }
        return result;
    }

    internal static void StopRemainder() {
        bool stopped = false;
        lock (Gate) {
            foreach (var instance in Playing) {
                // Recheck identity: FMOD may already have released a one-shot.
                if (instance.isValid() && IsDeathEvent(Audio.GetEventName(instance))) {
                    instance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
                    stopped = true;
                }
            }
            Playing.Clear();
        }
        if (stopped) Audio.System?.flushCommands();
    }

    internal static void Unload() {
        hook?.Dispose(); hook = null;
        lock (Gate) Playing.Clear();
    }
}
