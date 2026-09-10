using System.Reflection;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

// Optional: stock versions without blocking-operation indicators need no hook.
// Hook the whole presentation, before it reads/draws the backbuffer, not a
// save/load callback (the first progress presentation precedes those callbacks).
internal static class SpeedrunToolProgress {
    private static Hook? presentHook;
    internal static bool Available => presentHook is not null;

    internal static void Load(Assembly assembly) {
        Unload();
        Type? indicator = assembly.GetType("Celeste.Mod.SpeedrunTool.Progress.BusyIndicator");
        if (indicator is null) return;
        try {
            MethodInfo method = indicator.GetMethod("TryPresent", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(indicator.FullName, "TryPresent");
            presentHook = new Hook(method, (Action<Action<object, bool>, object, bool>)Present);
        } catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/SpeedrunTool",
                $"Cannot isolate progress presentations: {exception.GetBaseException().Message}");
        }
    }

    private static void Present(Action<object, bool> orig, object progress, bool force) {
        // Our frozen save coordinator already presents an indicator. Nested SRT
        // hints would add a synchronous full-resolution readback and duplicate UI.
        if (SpeedrunToolAutoSave.SavingSilently || SpeedrunToolAutoSave.LoadingSilently
            || SpeedrunToolRecoverySlot.OwnedOperationActive) return;
        using (CapturePresentationGate.Auxiliary()) {
            // CLEAR/GC/PRECLONE have no BeforeSave/BeforeLoad callback, but their
            // wall-time must be cut too. Never reopen or relabel an existing load.
            AutoRecorder.SuspendForExternalOperation();
            orig(progress, force);
        }
    }

    internal static void Unload() {
        presentHook?.Dispose();
        presentHook = null;
    }
}
