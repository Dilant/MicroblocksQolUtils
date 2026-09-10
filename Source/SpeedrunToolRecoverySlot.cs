using System.Collections;
using System.Reflection;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

internal enum RecoveryResult { Unavailable, Busy, Failed, Success }

// One named slot outside SRT's ten user-facing slots. Keep it in SRT's dictionary
// for scene/action-reinitialization support, but protect it from clearing user slots.
internal static class SpeedrunToolRecoverySlot {
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    internal static string Name { get; } = "MicroblocksQolUtils.Recording." + Guid.NewGuid().ToString("N");
    private static FieldInfo? dictionary, currentSlot, slotManager, preClone, savedLevel;
    private static PropertyInfo? currentName, state, isSaved;
    private static MethodInfo? clear;
    private static Func<string, bool>? switchSlot;
    private static Hook? clearAllHook;
    private static object? ownedSlot;
    private static Action? finishPending;
    private static Task? pendingClone;
    internal static bool Completing => finishPending is not null;

    private static IDictionary Slots => (IDictionary)dictionary!.GetValue(null)!;
    internal static bool HasState => ownedSlot is not null && ReferenceEquals(Slots[Name], ownedSlot)
        && isSaved!.GetValue(slotManager!.GetValue(ownedSlot)) is true;

    // Any user slot in THIS room takes precedence, even if an empty slot is selected.
    // Never switch slots to inspect them, and never count our leased slot as manual.
    internal static bool HasUserState => dictionary is not null && Slots.Values.Cast<object>()
        .Any(slot => !ReferenceEquals(slot, ownedSlot) && isSaved!.GetValue(slotManager!.GetValue(slot)) is true);

    internal static bool HasUserStateIn(Level? level, string? room = null) {
        if (level is null) return HasUserState;
        if (dictionary is null) return false;
        foreach (object slot in Slots.Values) {
            if (ReferenceEquals(slot, ownedSlot)) continue;
            object manager = slotManager!.GetValue(slot)!;
            if (isSaved!.GetValue(manager) is not true) continue;
            // Unknown metadata fails closed; never steal a user's recovery.
            if (savedLevel?.GetValue(manager) is not Level saved) return true;
            if (saved.Session.Area == level.Session.Area && saved.Session.Level == (room ?? level.Session.Level)) return true;
        }
        return false;
    }

    internal static bool OwnedOperationActive => ownedSlot is not null
        && ReferenceEquals(currentSlot?.GetValue(null), ownedSlot);

    internal static bool UserOperationActive => currentSlot?.GetValue(null) is { } slot
        && !ReferenceEquals(slot, ownedSlot)
        && state!.GetValue(slotManager!.GetValue(slot))?.ToString() is "Saving" or "Loading" or "Waiting";

    internal static void Initialize(Assembly assembly) {
        Type slots = assembly.GetType("Celeste.Mod.SpeedrunTool.SaveLoad.SaveSlotsManager", true)!;
        Type slot = assembly.GetType("Celeste.Mod.SpeedrunTool.SaveLoad.SaveSlot", true)
            ?? throw new MissingMemberException("SaveSlot");
        Type manager = assembly.GetType("Celeste.Mod.SpeedrunTool.SaveLoad.StateManager", true)!;
        dictionary = slots.GetField("Dictionary", Static) ?? throw new MissingFieldException("Dictionary");
        currentSlot = slots.GetField("Slot", Static) ?? throw new MissingFieldException("Slot");
        currentName = slots.GetProperty("SlotName", Static) ?? throw new MissingMemberException("SlotName");
        slotManager = slot.GetField("StateManager", Instance) ?? throw new MissingFieldException("StateManager");
        preClone = manager.GetField("preCloneTask", Instance) ?? throw new MissingFieldException("preCloneTask");
        savedLevel = manager.GetField("savedLevel", Instance);
        state = manager.GetProperty("State", Instance) ?? throw new MissingMemberException("State");
        isSaved = manager.GetProperty("IsSaved", Instance) ?? throw new MissingMemberException("IsSaved");
        clear = manager.GetMethod("ClearStateImpl", Instance, [typeof(bool)])
            ?? throw new MissingMethodException("ClearStateImpl");
        switchSlot = slots.GetMethod("SwitchSlot", Static, [typeof(string)])!.CreateDelegate<Func<string, bool>>();
        clearAllHook = new Hook(slots.GetMethod("ClearAll", Static, [])!, (Action<Action>)ClearUserSlots);
    }

    internal static RecoveryResult Run(Func<object, RecoveryResult> operation, bool create, bool deferPreClone = false) {
        if (Completing) return RecoveryResult.Busy;
        if (currentSlot is null || (!create && !HasState)) return RecoveryResult.Unavailable;
        object? previousSlot = currentSlot.GetValue(null);
        object? previousName = currentName!.GetValue(null);
        if (previousSlot is null) return RecoveryResult.Unavailable;
        if (!switchSlot!(Name)) return RecoveryResult.Busy;
        ownedSlot = currentSlot.GetValue(null)!;
        object manager = slotManager!.GetValue(ownedSlot)!;
        bool deferred = false;
        try {
            RecoveryResult result = operation(manager);
            if (deferPreClone && result == RecoveryResult.Success) {
                pendingClone = preClone!.GetValue(manager) as Task;
                finishPending = Finish;
                deferred = true;
            }
            return result;
        } finally {
            if (!deferred) Finish();
        }

        void Finish() {
            // Pre-cloning reads StateManager.Instance on its worker. It MUST finish
            // before restoring the user's selection, even on an exception.
            try {
                (preClone!.GetValue(manager) as Task)?.GetAwaiter().GetResult();
            } catch {
                // SRT waits this task again when switching/clearing slots. A
                // faulted task must not poison every subsequent user SL action.
                preClone!.SetValue(manager, null);
                clear!.Invoke(manager, [false]);
                throw;
            } finally {
                try {
                    if (state!.GetValue(manager)?.ToString() is "Saving" or "Loading")
                        clear!.Invoke(manager, [false]);
                } finally {
                    currentSlot!.SetValue(null, previousSlot);
                    currentName!.SetValue(null, previousName);
                }
            }
        }
    }

    // The game/input update stays paused while the worker owns StateManager.Instance.
    // Never move the scene clone itself off the game thread.
    internal static RecoveryResult CompletePending(bool wait = false) {
        if (finishPending is not { } finish) return RecoveryResult.Success;
        if (!wait && pendingClone is { IsCompleted: false }) return RecoveryResult.Busy;
        try { finish(); return RecoveryResult.Success; }
        catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/SpeedrunTool", $"Recovery pre-clone failed: {exception.GetBaseException().Message}");
            return RecoveryResult.Failed;
        } finally { finishPending = null; pendingClone = null; }
    }

    private static void ClearUserSlots(Action orig) {
        object? retained = ownedSlot;
        if (retained is null || !ReferenceEquals(Slots[Name], retained)) { orig(); return; }
        (preClone!.GetValue(slotManager!.GetValue(retained)) as Task)?.GetAwaiter().GetResult();
        Slots.Remove(Name);
        try { orig(); }
        finally { Slots[Name] = retained; }
    }

    internal static bool Release() {
        CompletePending(wait: true);
        if (ownedSlot is null) return true;
        if (!ReferenceEquals(Slots[Name], ownedSlot)) { ownedSlot = null; return true; }
        // The state may have been cleared by scene switching, but still owns a slot.
        object? previous = currentSlot!.GetValue(null);
        object? previousName = currentName!.GetValue(null);
        if (!switchSlot!(Name)) return false;
        try { clear!.Invoke(slotManager!.GetValue(ownedSlot), [false]); }
        catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/SpeedrunTool", $"Recovery slot cleanup failed: {exception.GetBaseException().Message}");
        }
        finally {
            Slots.Remove(Name);
            ownedSlot = null;
            currentSlot.SetValue(null, previous);
            currentName.SetValue(null, previousName);
        }
        return true;
    }

    internal static void Unload() {
        try { Release(); }
        catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/SpeedrunTool", $"Recovery slot cleanup failed: {exception.GetBaseException().Message}");
        }
        clearAllHook?.Dispose();
        clearAllHook = null;
        ownedSlot = null;
        dictionary = currentSlot = slotManager = preClone = savedLevel = null;
        currentName = state = isSaved = null;
        clear = null;
        switchSlot = null;
    }
}
