using System.Reflection;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

// Optional: stock versions without blocking-operation indicators need no hook.
// Hook the whole presentation, before it reads/draws the backbuffer, not a
// save/load callback (the first progress presentation precedes those callbacks).
internal static class SpeedrunToolProgress {
    private static Hook? presentHook;
    private static MethodInfo? begin, wait;
    internal static bool Available => presentHook is not null;

    internal static IDisposable? BeginSaveIndicator() {
        if (!Available || begin is null || wait is null) return null;
        try { return begin.Invoke(null, ["SAVE"]) as IDisposable; }
        catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/SpeedrunTool", $"Native progress unavailable; using recording indicator: {exception.GetBaseException().Message}");
            return null;
        }
    }

    internal static void WaitForClone(Task task) {
        // With native UI, SRT refreshes its own PRECLONE hint while synchronously
        // waiting. Keep the private slot selected until this has completed.
        if (Available && wait is not null) wait.Invoke(null, [task]);
        else task.GetAwaiter().GetResult();
    }

    internal static void Load(Assembly assembly) {
        Unload();
        Type? indicator = assembly.GetType("Celeste.Mod.SpeedrunTool.Progress.BusyIndicator");
        if (indicator is null) return;
        try {
            MethodInfo method = indicator.GetMethod("TryPresent", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(indicator.FullName, "TryPresent");
            presentHook = new Hook(method, (Action<Action<object, bool>, object, bool>)Present);
            begin = indicator.GetMethod("Begin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(string)]);
            wait = indicator.GetMethod("Wait", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Task)]);
        } catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/SpeedrunTool",
                $"Cannot isolate progress presentations: {exception.GetBaseException().Message}");
        }
    }

    private static void Present(Action<object, bool> orig, object progress, bool force) {
        bool internalOperation = SpeedrunToolAutoSave.SavingSilently || SpeedrunToolAutoSave.LoadingSilently;
        // Background collection of obsolete versions should remain silent.
        if (SpeedrunToolRecoverySlot.OwnedOperationActive && !internalOperation) return;
        using (CapturePresentationGate.Auxiliary()) {
            // CLEAR/GC/PRECLONE have no BeforeSave/BeforeLoad callback, but their
            // wall-time must be cut too. Never reopen or relabel an existing load.
            if (!internalOperation) AutoRecorder.SuspendForExternalOperation();
            orig(progress, force);
        }
    }

    internal static void Unload() {
        presentHook?.Dispose();
        presentHook = null;
        begin = wait = null;
    }
}
