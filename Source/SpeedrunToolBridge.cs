using MonoMod.ModInterop;
using MonoMod.RuntimeDetour;
using System.Reflection;

namespace Celeste.Mod.MicroblocksQolUtils;

#pragma warning disable CS0649

[ModImportName("SpeedrunTool.SaveLoad")]
public static class SpeedrunToolImports {
    public delegate object RegisterSaveLoadActionHandler(
        Action<Dictionary<Type, Dictionary<string, object>>, Level>? saveState,
        Action<Dictionary<Type, Dictionary<string, object>>, Level>? loadState,
        Action? clearState,
        Action<Level>? beforeSaveState,
        Action<Level>? beforeLoadState,
        Action? preCloneEntities
    );

    public static RegisterSaveLoadActionHandler? RegisterSaveLoadAction;
    public static Action<object>? Unregister;
    public static Action<Type, bool>? IgnoreSaveState;
}

public static class SpeedrunToolBridge {
    private const string TimelineKey = "recording-timeline";
    private static object? registration;
    private static Hook? saveOperationHook, loadOperationHook;
    private delegate bool Operation(object manager, bool tas, out string popup);
    private delegate bool OperationHook(Operation orig, object manager, bool tas, out string popup);
    private sealed class Transaction {
        internal Level? Level;
        internal RecordingTimelineSnapshot? Snapshot;
    }
    private static Transaction? transaction, internalLoad;
    private static readonly Dictionary<object, RecordingTimelineSnapshot> manualRoots = new(ReferenceEqualityComparer.Instance);

    internal static IEnumerable<string> ReferencedRecoveryVersions(string source) {
        var live = new HashSet<object>(SpeedrunToolRecoverySlot.SavedUserManagers(), ReferenceEqualityComparer.Instance);
        foreach (var pair in manualRoots.ToArray()) {
            if (!live.Contains(pair.Key)) { manualRoots.Remove(pair.Key); continue; }
            if (pair.Value.RecordingSource == source && pair.Value.RecoveryVersionId is { } id) yield return id;
        }
    }

    private static bool SaveOperation(Operation orig, object manager, bool tas, out string popup) {
        Transaction? parent = transaction;
        Transaction current = transaction = new();
        bool success = false;
        try {
            success = orig(manager, tas, out popup);
            if (success && !SpeedrunToolAutoSave.SavingSilently) {
                manualRoots.Remove(manager);
                if (current.Snapshot is { } snapshot) manualRoots[manager] = snapshot;
            }
            return success;
        } finally {
            // A rejected save before callbacks leaves the previous slot intact.
            // A failure after cloning must not keep its former version pinned.
            if (!success && current.Snapshot is not null && !SpeedrunToolAutoSave.SavingSilently) manualRoots.Remove(manager);
            RecordingTransitionAutoSave.InvalidateReferences();
            transaction = parent;
        }
    }

    private static bool LoadOperation(Operation orig, object manager, bool tas, out string popup) {
        Transaction? parent = transaction;
        Transaction current = transaction = new();
        try {
            bool success = orig(manager, tas, out popup);
            if (success) {
                if (SpeedrunToolAutoSave.LoadingSilently) internalLoad = current;
                else CommitLoad(current);
            }
            return success;
        } finally { transaction = parent; }
    }

    internal static void DiscardInternalLoad() => internalLoad = null;
    internal static void CommitInternalLoad() {
        if (internalLoad is { } pending) CommitLoad(pending);
        internalLoad = null;
    }
    private static void CommitLoad(Transaction pending) {
        if (pending.Level is not { } level) return;
        if (pending.Snapshot is { } snapshot && AutoRecorder.TryRestoreTimeline(level, snapshot)) {
            RecordingTransitionAutoSave.RestoreContext(level, snapshot);
        } else {
            AutoRecorder.BreakForUntrackedLoad(level);
            RecordingTransitionAutoSave.Reset();
        }
    }

    internal static bool Available => registration is not null;

    internal static void InstallTransactionHooks(Assembly assembly) {
        Type manager = assembly.GetType("Celeste.Mod.SpeedrunTool.SaveLoad.StateManager", true)!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        saveOperationHook = new Hook(manager.GetMethod("SaveStateImpl", flags)!, (OperationHook)SaveOperation);
        loadOperationHook = new Hook(manager.GetMethod("LoadStateImpl", flags)!, (OperationHook)LoadOperation);
    }

    public static void Load() {
        typeof(SpeedrunToolImports).ModInterop();
        if (SpeedrunToolImports.RegisterSaveLoadAction is null) return;
        registration = SpeedrunToolImports.RegisterSaveLoadAction(
            Save,
            Load,
            RecordingTransitionAutoSave.InvalidateReferences,
            level => BeforeManualOperation(level, loading: false),
            level => BeforeManualOperation(level, loading: true),
            null
        );
        SpeedrunToolImports.IgnoreSaveState?.Invoke(typeof(QolHud), false);
        SpeedrunToolAutoSave.Load(registration.GetType().Assembly);
        SpeedrunToolProgress.Load(registration.GetType().Assembly);
        try {
            InstallTransactionHooks(registration.GetType().Assembly);
        } catch (Exception exception) {
            saveOperationHook?.Dispose(); loadOperationHook?.Dispose();
            saveOperationHook = loadOperationHook = null;
            SpeedrunToolAutoSave.Unload();
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/SpeedrunTool", $"Transactional recovery unavailable: {exception.GetBaseException().Message}");
        }
        Logger.Log(LogLevel.Info, "MicroblocksQolUtils", "SpeedrunTool recording timeline integration enabled");
    }

    public static void Unload() {
        saveOperationHook?.Dispose(); loadOperationHook?.Dispose();
        saveOperationHook = loadOperationHook = null;
        transaction = internalLoad = null;
        manualRoots.Clear();
        SpeedrunToolProgress.Unload();
        RecordingTransitionAutoSave.Reset();
        SpeedrunToolAutoSave.Unload();
        if (registration is not null) SpeedrunToolImports.Unregister?.Invoke(registration);
        registration = null;
    }

    private static void Save(Dictionary<Type, Dictionary<string, object>> values, Level level) {
        RecordingTimelineSnapshot? snapshot = AutoRecorder.CaptureTimeline(level);
        if (snapshot is null) return;
        if (!values.TryGetValue(typeof(AutoRecorder), out Dictionary<string, object>? own)) {
            own = [];
            values[typeof(AutoRecorder)] = own;
        }
        snapshot = (snapshot with {
            RecoveryVersionId = SpeedrunToolAutoSave.SavingSilently ? SpeedrunToolRecoverySlot.Name : RecordingTransitionAutoSave.CurrentVersionId,
            RecordingSource = AutoRecorder.CurrentPath
        }).Copy();
        own[TimelineKey] = snapshot;
        if (transaction is { } active) { active.Level = level; active.Snapshot = snapshot; }
    }

    private static void BeforeManualOperation(Level level, bool loading) {
        if (SpeedrunToolAutoSave.SavingSilently || SpeedrunToolAutoSave.LoadingSilently) return;
        // Saving does not cancel a normal checkpoint request; loading invalidates
        // requests for the abandoned scene/session, but keeps all pinned versions.
        if (loading) RecordingTransitionAutoSave.Cancel();
        AutoRecorder.SuspendForManualSl(loading);
    }

    private static void Load(Dictionary<Type, Dictionary<string, object>> values, Level level) {
        if (transaction is { } active) active.Level = level;
        if (values.TryGetValue(typeof(AutoRecorder), out Dictionary<string, object>? own)
            && own.TryGetValue(TimelineKey, out object? value)
            && value is RecordingTimelineSnapshot snapshot) {
            if (transaction is { } pending) pending.Snapshot = snapshot;
        }
        // Manual SL owns gameplay, freezing, wipes and death recovery. Only the
        // video prefix is restored here; do not queue an automatic save/load.
    }
}
