using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

// Optional, version-checked adapter. Never use TAS saves: SavedByTas would disable
// SpeedrunTool's death auto-load and change the semantics of subsequent normal loads.
internal static class SpeedrunToolAutoSave {
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static ILHook? saveHook;
    private static ILHook? markHook;
    private static ILHook? loadHook;
    private static MethodInfo? save;
    private static MethodInfo? load;
    private static PropertyInfo? managerInstance;
    private static PropertyInfo? managerState;
    private static PropertyInfo? savedByTas;
    private static PropertyInfo? settingsInstance;
    private static PropertyInfo? enabled;
    private static PropertyInfo? tasRunning;
    private static Func<bool>? allFree;
    private static HookStatus? hookStatus;

    // Everest Ultra may apply these manipulators later, on startup workers.
    // Hook objects alone do not mean the adapter is ready. A failed generation
    // stays disabled until Load, including when another mod rebuilds the chain.
    private sealed class HookStatus {
        internal volatile bool SaveReady, LoadReady, MarkReady, Initialized, Failed;
        internal bool Ready => Initialized && SaveReady && LoadReady && MarkReady && !Failed;
    }

    internal static bool SavingSilently { get; private set; }
    internal static bool LoadingSilently { get; private set; }
    private static bool suppressMarking;
    internal static bool Available => hookStatus?.Ready is true;
    internal static bool HasState => Available && SpeedrunToolRecoverySlot.HasState;

    internal static void Load(Assembly assembly) {
        Unload();
        HookStatus status = new();
        hookStatus = status;
        try {
            Type manager = RequiredType("SaveLoad.StateManager");
            Type slots = RequiredType("SaveLoad.SaveSlotsManager");
            Type settings = RequiredType("SpeedrunToolSettings");
            Type marks = RequiredType("SaveLoad.Utils.StateMarkUtils");
            managerInstance = RequiredProperty(manager, "Instance", Static);
            managerState = RequiredProperty(manager, "State", Instance);
            savedByTas = RequiredProperty(manager, "SavedByTas", Instance);
            settingsInstance = RequiredProperty(settings, "Instance", Static);
            enabled = RequiredProperty(settings, "Enabled", Instance);
            tasRunning = RequiredProperty(RequiredType("ModInterop.TasUtils"), "Running", Static);
            allFree = slots.GetMethod("IsAllFree", Static, [])!.CreateDelegate<Func<bool>>();
            save = manager.GetMethod("SaveStateImpl", Instance, [typeof(bool), typeof(string).MakeByRefType()])
                ?? throw new MissingMethodException(manager.FullName, "SaveStateImpl");
            load = manager.GetMethod("LoadStateImpl", Instance, [typeof(bool), typeof(string).MakeByRefType()])
                ?? throw new MissingMethodException(manager.FullName, "LoadStateImpl");
            MethodInfo recolor = marks.GetMethod("ReColor", Static,
                [typeof(Dictionary<Type, Dictionary<string, object>>), typeof(Level)])
                ?? throw new MissingMethodException(marks.FullName, "ReColor");

            // Activate only when ALL manipulators have succeeded. Unknown IL must
            // disable recovery, never fall back to an ordinary save or throw out of
            // a deferred startup transaction (outside this method's try/catch).
            markHook = new ILHook(recolor, il => {
                status.MarkReady = false;
                ILCursor cursor = new(il);
                ILLabel original = cursor.DefineLabel();
                cursor.EmitDelegate(() => status.Ready && suppressMarking);
                cursor.Emit(OpCodes.Brfalse, original);
                cursor.Emit(OpCodes.Ret);
                cursor.MarkLabel(original);
                status.MarkReady = true;
            });
            saveHook = new ILHook(save, il => SkipInternalWait(il, loading: false));
            loadHook = new ILHook(load, il => SkipInternalWait(il, loading: true));
            SpeedrunToolRecoverySlot.Initialize(assembly);
            status.Initialized = true;
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils", "SpeedrunTool private recovery hooks registered; activation requires all IL hooks to succeed");

            void SkipInternalWait(ILContext il, bool loading) {
                if (loading) status.LoadReady = false;
                else status.SaveReady = false;
                ILCursor cursor = new(il);
                if (!cursor.TryGotoNext(MoveType.After,
                        // Everest's relinker can turn the original call into callvirt.
                        instruction => instruction.MatchCallOrCallvirt(manager, "PreCloneSavedEntities"),
                        instruction => instruction.MatchLdarg(1))
                    || cursor.Next?.OpCode.Code is not (Code.Brfalse or Code.Brfalse_S or Code.Brtrue or Code.Brtrue_S)) {
                    status.Failed = true;
                    Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/SpeedrunTool",
                        $"Silent recovery disabled: unrecognized completion branch in {il.Method.FullName}");
                    return; // No IL has been modified; ordinary SRT behavior is intact.
                }
                // Only skip the animation/wait. The original tas argument,
                // validation, saved state, callbacks and load behavior remain normal.
                // A private load completes synchronously too: no pending wipe may
                // consult StateManager.Instance after we restore the user's slot.
                cursor.EmitDelegate((bool tas) => tas || (status.Ready && (SavingSilently || LoadingSilently)));
                if (loading) status.LoadReady = true;
                else status.SaveReady = true;
            }

            Type RequiredType(string name) => assembly.GetType("Celeste.Mod.SpeedrunTool." + name, true)!;
        } catch (Exception exception) {
            Unload();
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/SpeedrunTool", $"Silent saves unavailable: {exception.Message}");
        }
    }

    private static PropertyInfo RequiredProperty(Type type, string name, BindingFlags flags) =>
        type.GetProperty(name, flags) ?? throw new MissingMemberException(type.FullName, name);

    internal static RecoveryResult TrySave() => Run(loadState: false, preserveMarks: false);
    internal static RecoveryResult TryLoad(Level level) => Run(loadState: true,
        preserveMarks: level.Session.GetFlag("SpeedrunTool_SavedSate"));

    internal static bool CanUse {
        get {
            if (!Available) return false;
            object? settings = settingsInstance!.GetValue(null);
            return settings is not null && enabled!.GetValue(settings) is true && tasRunning!.GetValue(null) is not true;
        }
    }

    private static RecoveryResult Run(bool loadState, bool preserveMarks) {
        if (!Available) return RecoveryResult.Unavailable;
        try {
            if (!CanUse) return RecoveryResult.Unavailable;
            object? manager = managerInstance!.GetValue(null);
            if (manager is null || savedByTas!.GetValue(manager) is true) return RecoveryResult.Unavailable;
            if (!allFree!() || managerState!.GetValue(manager)?.ToString() != "None") return RecoveryResult.Busy;

            SavingSilently = !loadState;
            LoadingSilently = loadState;
            suppressMarking = !preserveMarks;
            return SpeedrunToolRecoverySlot.Run(ownedManager => {
                object?[] arguments = [false, null];
                if ((loadState ? load : save)!.Invoke(ownedManager, arguments) is true) return RecoveryResult.Success;
                Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/SpeedrunTool", $"Recording recovery skipped: {arguments[1]}");
                return RecoveryResult.Failed;
            }, create: !loadState);
        } catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/SpeedrunTool", $"Recording recovery failed: {exception.GetBaseException().Message}");
            return RecoveryResult.Failed;
        } finally {
            SavingSilently = LoadingSilently = suppressMarking = false;
        }
    }

    internal static void Unload() {
        if (hookStatus is { } status) status.Failed = true;
        hookStatus = null;
        SpeedrunToolRecoverySlot.Unload();
        saveHook?.Dispose();
        markHook?.Dispose();
        loadHook?.Dispose();
        saveHook = markHook = loadHook = null;
        save = load = null;
        managerInstance = managerState = savedByTas = settingsInstance = enabled = tasRunning = null;
        allFree = null;
        SavingSilently = LoadingSilently = suppressMarking = false;
    }
}
