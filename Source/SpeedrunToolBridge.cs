using MonoMod.ModInterop;

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

    internal static bool Available => registration is not null;

    public static void Load() {
        typeof(SpeedrunToolImports).ModInterop();
        if (SpeedrunToolImports.RegisterSaveLoadAction is null) return;
        registration = SpeedrunToolImports.RegisterSaveLoadAction(
            Save,
            Load,
            null,
            level => BeforeManualOperation(level, loading: false),
            level => BeforeManualOperation(level, loading: true),
            null
        );
        SpeedrunToolImports.IgnoreSaveState?.Invoke(typeof(QolHud), false);
        SpeedrunToolAutoSave.Load(registration.GetType().Assembly);
        Logger.Log(LogLevel.Info, "MicroblocksQolUtils", "SpeedrunTool recording timeline integration enabled");
    }

    public static void Unload() {
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
        own[TimelineKey] = snapshot;
        if (!SpeedrunToolAutoSave.SavingSilently) RecordingTransitionAutoSave.Reset();
    }

    private static void BeforeManualOperation(Level level, bool loading) {
        if (SpeedrunToolAutoSave.SavingSilently || SpeedrunToolAutoSave.LoadingSilently) return;
        RecordingTransitionAutoSave.Reset();
        AutoRecorder.SuspendForManualSl(loading);
    }

    private static void Load(Dictionary<Type, Dictionary<string, object>> values, Level level) {
        if (!SpeedrunToolAutoSave.LoadingSilently) RecordingTransitionAutoSave.Reset();
        if (values.TryGetValue(typeof(AutoRecorder), out Dictionary<string, object>? own)
            && own.TryGetValue(TimelineKey, out object? value)
            && value is RecordingTimelineSnapshot snapshot) {
            AutoRecorder.RestoreTimeline(level, snapshot);
        }
        // Manual SL owns gameplay, freezing, wipes and death recovery. Only the
        // video prefix is restored here; do not queue an automatic save/load.
    }
}
