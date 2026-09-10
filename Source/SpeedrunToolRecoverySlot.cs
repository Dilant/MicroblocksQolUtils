using System.Collections;
using System.Reflection;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

internal enum RecoveryResult { Unavailable, Busy, Failed, Success }

// Immutable private savestate versions outside the user UI slots. Keep live
// versions in SRT for clone/action lifecycle support; user clear-all excludes them.
internal static class SpeedrunToolRecoverySlot {
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const string Prefix = "MicroblocksQolUtils.Recording.";
    internal static string Name { get; private set; } = Prefix + Guid.NewGuid().ToString("N");
    private static readonly Dictionary<string, object> ownedSlots = [];
    internal static IReadOnlyCollection<string> VersionIds => ownedSlots.Keys;
    internal static bool Contains(string id) => ownedSlots.TryGetValue(id, out object? slot)
        && ReferenceEquals(Slots[id], slot) && isSaved!.GetValue(slotManager!.GetValue(slot)) is true;
    internal static void SelectVersion(string id) => Name = id;
    private static bool IsOwned(object? slot) => slot is not null && ownedSlots.Values.Any(value => ReferenceEquals(value, slot));
    private static FieldInfo? dictionary, currentSlot, slotManager, preClone;
    private static PropertyInfo? currentName, state, isSaved;
    private static MethodInfo? clear;
    private static Func<string, bool>? switchSlot;
    private static Hook? clearAllHook;
    private static Action? finishPending;
    private static Task? pendingClone;
    internal static bool Completing => finishPending is not null;

    private static IDictionary Slots => (IDictionary)dictionary!.GetValue(null)!;
    internal static bool HasState => dictionary is not null && Contains(Name);
    internal static bool HasUserState => SavedUserManagers().Any();
    internal static IEnumerable<object> SavedUserManagers() {
        if (dictionary is null) yield break;
        foreach (object slot in Slots.Values) {
            if (IsOwned(slot)) continue;
            object manager = slotManager!.GetValue(slot)!;
            if (isSaved!.GetValue(manager) is true) yield return manager;
        }
    }
    internal static bool SelectedUserSaved => currentSlot?.GetValue(null) is { } slot && !IsOwned(slot)
        && isSaved!.GetValue(slotManager!.GetValue(slot)) is true;
    internal static bool OwnedOperationActive => IsOwned(currentSlot?.GetValue(null));
    internal static bool UserOperationActive => currentSlot?.GetValue(null) is { } slot && !IsOwned(slot)
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
        string previousVersion = Name;
        string target = create ? Prefix + Guid.NewGuid().ToString("N") : Name;
        if (!switchSlot!(target)) return RecoveryResult.Busy;
        Name = target;
        object ownedSlot = currentSlot.GetValue(null)!;
        ownedSlots[Name] = ownedSlot;
        RecordingTransitionAutoSave.InvalidateReferences();
        object manager = slotManager!.GetValue(ownedSlot)!;
        bool deferred = false, succeeded = false;
        try {
            RecoveryResult result = operation(manager);
            succeeded = result == RecoveryResult.Success;
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
                if (preClone!.GetValue(manager) is Task clone) SpeedrunToolProgress.WaitForClone(clone);
            } catch {
                succeeded = false;
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
                    if (create && !succeeded) Name = previousVersion;
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
        var retained = ownedSlots.Where(pair => ReferenceEquals(Slots[pair.Key], pair.Value)).ToArray();
        try {
            foreach (var pair in retained) {
                (preClone!.GetValue(slotManager!.GetValue(pair.Value)) as Task)?.GetAwaiter().GetResult();
                Slots.Remove(pair.Key);
            }
            orig();
        }
        finally { foreach (var pair in retained) Slots[pair.Key] = pair.Value; }
    }

    internal static bool Release() {
        CompletePending(wait: true);
        bool ok = true;
        foreach (string id in ownedSlots.Keys.ToArray()) ok &= ReleaseVersion(id);
        return ok;
    }

    internal static bool ReleaseVersion(string id) {
        if (Completing) return false;
        if (!ownedSlots.TryGetValue(id, out object? ownedSlot)) return true;
        if (!ReferenceEquals(Slots[id], ownedSlot)) { ownedSlots.Remove(id); return true; }
        object? previous = currentSlot!.GetValue(null);
        object? previousName = currentName!.GetValue(null);
        try {
            if (!switchSlot!(id)) return false;
            clear!.Invoke(slotManager!.GetValue(ownedSlot), [false]);
        }
        catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/SpeedrunTool", $"Recovery slot cleanup failed: {exception.GetBaseException().Message}");
            return false;
        }
        finally {
            currentSlot.SetValue(null, previous);
            currentName.SetValue(null, previousName);
        }
        Slots.Remove(id);
        ownedSlots.Remove(id);
        return true;
    }

    internal static void Unload() {
        try { Release(); }
        catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/SpeedrunTool", $"Recovery slot cleanup failed: {exception.GetBaseException().Message}");
        }
        clearAllHook?.Dispose();
        clearAllHook = null;
        ownedSlots.Clear();
        dictionary = currentSlot = slotManager = preClone = null;
        currentName = state = isSaved = null;
        clear = null;
        switchSlot = null;
    }
}
