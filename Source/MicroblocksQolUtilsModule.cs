using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

public sealed class MicroblocksQolUtilsModule : EverestModule {
    public static MicroblocksQolUtilsModule Instance { get; private set; } = null!;
    public static QolSettings Settings => (QolSettings)Instance._Settings;

    public override Type SettingsType => typeof(QolSettings);
    public override Type SaveDataType => typeof(QolSaveData);

    public MicroblocksQolUtilsModule() {
        Instance = this;
    }

    public override void OnInputInitialize() {
        base.OnInputInitialize();
        Settings.MigrateMiniMapBindings();
    }

    public override void Load() {
        Logger.Log(LogLevel.Info, "MicroblocksQolUtils", "Loading microblock's QoL Utils");
        FrameRateCounter.Reset();
        WindowsInputLanguage.Load();
        MaterialTextInputFocus.Load();
        CollabUtils2Bridge.Load();
        MaterialChapterSelect.Load();
        MaterialModOptions.Load();
        QolPauseMenu.Load();
        MaterialAcrylicRenderer.Load();
        MaterialUiSmoke.Load();
        NativeCaptureBridge.InitializeFromMod(Metadata);
        CaptureSource.Load();
        NativeCaptureSmoke.Load();
        FrameProfiler.Load();
        CollisionBoxRenderer.Load();
        InstantTransitions.Load();
        InstantDeaths.Load();
        AutoRecorder.Load(Path.GetDirectoryName(Metadata.DLL) ?? "");
        Everest.Events.Level.OnLoadLevel += OnLoadLevel;
        On.Monocle.Engine.Update += EngineUpdate;
        On.Monocle.Engine.Draw += EngineDraw;
    }

    public override void Unload() {
        On.Monocle.Engine.Draw -= EngineDraw;
        On.Monocle.Engine.Update -= EngineUpdate;
        Everest.Events.Level.OnLoadLevel -= OnLoadLevel;
        NativeCaptureSmoke.Unload();
        MaterialAcrylicRenderer.Unload();
        MaterialModOptions.Unload();
        MaterialChapterSelect.Unload();
        QolPauseMenu.Unload();
        MaterialUiSmoke.Unload();
        NativeCaptureCommands.Unload();
        AutoRecorder.Unload();
        CaptureSource.Unload();
        CollisionBoxRenderer.Unload();
        InstantDeaths.Unload();
        InstantTransitions.Unload();
        FrameProfiler.Unload();
        MiaoNetBridge.Unload();
        MotionSmoothingBridge.Unload();
        MaterialTextInputFocus.Unload();
        WindowsInputLanguage.Unload();
        FrameRateCounter.Reset();
        MaterialUi.Dispose();
        MiniMapRenderer.Dispose();
        MaterialIcon.Dispose();
        SystemTtfFont.Dispose();
    }

    private static void OnLoadLevel(Level level, Player.IntroTypes intro, bool fromLoader) {
        RecentChapterHistory.Record(level.Session.Area.SID);
        if (level.Tracker.GetEntity<QolHud>() is null) level.Add(new QolHud());
    }

    private static void EngineUpdate(On.Monocle.Engine.orig_Update orig, Engine self, Microsoft.Xna.Framework.GameTime gameTime) {
        HiDpiSupport.UpdateScale();
        MaterialTextInputFocus.BeginFrame();
        FrameProfiler.BeginUpdate();
        try {
            // Freeze simulation AND input/SL hotkeys, but keep rendering, audio
            // capture and the main-thread save coordinator alive.
            if (RecordingSavePause.BeforeEngineUpdate(ref gameTime)) orig(self, gameTime);
            CaptureSource.Update();
            NativeCaptureSmoke.Update();
            AutoRecorder.AfterEngineUpdate();
            InstantDeaths.AfterEngineUpdate();
            MaterialUiSmoke.Update();
            WindowsInputLanguage.Update();
        } finally {
            FrameProfiler.EndUpdate();
            FrameRateCounter.TickUpdate();
        }
    }

    private static void EngineDraw(On.Monocle.Engine.orig_Draw orig, Engine self, Microsoft.Xna.Framework.GameTime gameTime) {
        FrameProfiler.BeginRender();
        try {
            orig(self, gameTime);
        } finally {
            RecordingSavePause.Drawn();
            FrameProfiler.EndRender();
            FrameRateCounter.TickRender();
        }
    }
}
