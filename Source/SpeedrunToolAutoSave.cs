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

    internal static bool SavingSilently { get; private set; }
    internal static bool LoadingSilently { get; private set; }
    private static bool suppressMarking;
    internal static bool Available => saveHook is not null && markHook is not null && loadHook is not null;
    internal static bool HasState => Available && SpeedrunToolRecoverySlot.HasState;

    internal static void Load(Assembly assembly) {
        Unload();
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

            // Install all hooks or none. An unknown implementation must not fall back
            // to an ordinary save, which would unexpectedly mark/freeze gameplay.
            markHook = new ILHook(recolor, il => {
                ILCursor cursor = new(il);
                ILLabel original = cursor.DefineLabel();
                cursor.EmitDelegate(() => suppressMarking);
                cursor.Emit(OpCodes.Brfalse, original);
                cursor.Emit(OpCodes.Ret);
                cursor.MarkLabel(original);
            });
            saveHook = new ILHook(save, SkipInternalWait);
            loadHook = new ILHook(load, SkipInternalWait);
            SpeedrunToolRecoverySlot.Initialize(assembly);
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils", "SpeedrunTool private recording recovery slot enabled");

            void SkipInternalWait(ILContext il) {
                ILCursor cursor = new(il);
                if (!cursor.TryGotoNext(MoveType.After,
                        instruction => instruction.MatchCall(manager, "PreCloneSavedEntities"),
                        instruction => instruction.MatchLdarg(1))
                    || cursor.Next?.OpCode.FlowControl != FlowControl.Cond_Branch) {
                    throw new InvalidOperationException("Unrecognized SpeedrunTool save/load-completion branch");
                }
                // Only skip the animation/wait. The original tas argument,
                // validation, saved state, callbacks and load behavior remain normal.
                // A private load completes synchronously too: no pending wipe may
                // consult StateManager.Instance after we restore the user's slot.
                cursor.EmitDelegate((bool tas) => tas || SavingSilently || LoadingSilently);
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
