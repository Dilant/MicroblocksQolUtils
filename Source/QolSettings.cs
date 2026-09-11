using System.ComponentModel;
using Microsoft.Xna.Framework.Input;

namespace Celeste.Mod.MicroblocksQolUtils;

public enum MiniMapShape {
    Circle,
    Square
}

public enum MiniMapAvatarShape {
    Circle,
    Square
}

public enum MiniMapNameMode {
    None,
    WatchedOnly,
    Everyone
}

public enum RecordingPolicy {
    EveryRoom,
    GoldenRunsOnly
}

public enum BgmRecordingMode {
    CaptureGameMix,
    SfxOnlyWithPostMix
}

public enum CollisionBoxDisplayMode {
    Hidden,
    Visible,
    Only
}

public sealed class QolSettings : EverestModuleSettings {
    [DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    [DefaultValue(true)]
    [SettingName("高速特效")]
    public bool HighSpeedEffects { get; set; } = true;

    [SettingRange(200, 2000)]
    [DefaultValue(600)]
    [SettingName("高速阈值")]
    public int HighSpeedThreshold { get; set; } = 600;

    [SettingRange(25, 200)]
    [DefaultValue(100)]
    [SettingName("高速特效强度")]
    public int HighSpeedEffectIntensity { get; set; } = 100;

    [SettingNeedsRelaunch]
    [DefaultValue(true)]
    public bool HiDpiFix { get; set; } = true;

    public void CreateHiDpiFixEntry(TextMenu menu, bool inGame) {
        if (!OperatingSystem.IsWindows()) return;

        TextMenu.OnOff entry = new(Dialog.Clean("modoptions_microblocksqolutils_hidpifix"), HiDpiFix);
        entry.Change(value => HiDpiFix = value);
        menu.Add(entry);
        entry.NeedsRelaunch(menu, true);
    }

    [DefaultValue(true)]
    public bool MaterialAcrylicBackground { get; set; } = true;

    [DefaultValue(true)]
    public bool HudMaterialSurfaces { get; set; } = true;

    [SettingRange(1, 12)]
    [DefaultValue(7)]
    public int MaterialAcrylicBlurStrength { get; set; } = 7;

    [DefaultValue(false)]
    public bool ReplaceChapterSelect { get; set; }

    [DefaultValue(true)]
    public bool ReplaceEverestModOptions { get; set; } = true;

    [SettingIgnore]
    public List<string> PinnedModOptionsTabs { get; set; } = [];

    [SettingIgnore]
    public List<string> FavoriteModOptionsItems { get; set; } = [];

    [DefaultValue(false)]
    public bool ChapterSelectShowCollabMaps { get; set; }

    [DefaultValue(false)]
    public bool AutoSwitchInputLanguage { get; set; }

    public void CreateAutoSwitchInputLanguageEntry(TextMenu menu, bool inGame) {
        if (!OperatingSystem.IsWindows()) return;

        TextMenu.OnOff entry = new(
            Dialog.Clean("modoptions_microblocksqolutils_autoswitchinputlanguage"),
            AutoSwitchInputLanguage
        );
        entry.Change(value => AutoSwitchInputLanguage = value);
        menu.Add(entry);
    }

    [DefaultValue("Microsoft YaHei UI")]
    public string FontFamily { get; set; } = "Microsoft YaHei UI";

    [SettingRange(80, 160)]
    [DefaultValue(120)]
    public int FontScalePercent { get; set; } = 120;

    [SettingIgnore]
    [DefaultValue("")]
    public string FontFile { get; set; } = "";

    public void CreateFontFamilyEntry(TextMenu menu, bool inGame) {
        string current = UiFontCatalog.ResolveFamily(FontFamily);
        FontFamily = current;
        TextMenu.Option<string> fonts = new(Dialog.Clean("modoptions_microblocksqolutils_fontfamily"));

        foreach (string family in UiFontCatalog.InstalledFamilies)
            fonts.Add(family, family, string.Equals(family, current, StringComparison.OrdinalIgnoreCase));

        fonts.Change(family => {
            FontFamily = family;
            FontFile = "";
        });
        menu.Add(fonts);
    }

    [DefaultValue(true)]
    public bool MiniMapEnabled { get; set; } = true;

    [SettingRange(96, 384)]
    [DefaultValue(220)]
    public int MiniMapSize { get; set; } = 220;

    [SettingRange(0, 12)]
    [DefaultValue(3)]
    public int MiniMapZoom { get; set; } = 3;

    [SettingIgnore]
    [DefaultValue(Keys.OemPlus)]
    public Keys MiniMapZoomInKey { get; set; } = Keys.OemPlus;

    [SettingIgnore]
    [DefaultValue(Keys.OemMinus)]
    public Keys MiniMapZoomOutKey { get; set; } = Keys.OemMinus;

    [SettingName("放大小地图")]
    [DefaultButtonBinding(0, Keys.OemPlus)]
    public ButtonBinding MiniMapZoomInBinding { get; set; } = new(0, Keys.OemPlus);

    [SettingName("缩小小地图")]
    [DefaultButtonBinding(0, Keys.OemMinus)]
    public ButtonBinding MiniMapZoomOutBinding { get; set; } = new(0, Keys.OemMinus);

    [SettingIgnore]
    [DefaultValue(false)]
    public bool MiniMapBindingsMigrated { get; set; }

    [DefaultValue(MiniMapShape.Circle)]
    public MiniMapShape MiniMapShape { get; set; } = MiniMapShape.Circle;

    [DefaultValue(true)]
    public bool MiniMapBackground { get; set; } = true;

    [DefaultValue(true)]
    public bool MiniMapBorder { get; set; } = true;

    [DefaultValue(true)]
    public bool MiniMapRoomBounds { get; set; } = true;

    [DefaultValue(true)]
    public bool MiniMapRoomBackgrounds { get; set; } = true;

    [SettingRange(0, 10)]
    [DefaultValue(3)]
    public int MiniMapRoomBackgroundOpacity { get; set; } = 3;

    [DefaultValue(true)]
    public bool MiniMapHighlightRoute { get; set; } = true;

    [DefaultValue(true)]
    public bool MiniMapCollectibles { get; set; } = true;

    [DefaultValue(false)]
    public bool MiniMapShowNearbyRoomStrawberries { get; set; }

    [SettingRange(0, 10)]
    [DefaultValue(6)]
    public int MiniMapBackgroundOpacity { get; set; } = 6;

    [DefaultValue(true)]
    public bool MiniMapAdaptiveColors { get; set; } = true;

    [DefaultValue(true)]
    public bool ShowMiaoNetPlayers { get; set; } = true;

    [DefaultValue(true)]
    public bool MiniMapShowOffscreenPlayers { get; set; } = true;

    [DefaultValue(MiniMapAvatarShape.Circle)]
    public MiniMapAvatarShape MiniMapAvatarShape { get; set; } = MiniMapAvatarShape.Circle;

    [DefaultValue(MiniMapNameMode.WatchedOnly)]
    public MiniMapNameMode MiniMapNames { get; set; } = MiniMapNameMode.WatchedOnly;

    [DefaultValue(true)]
    public bool HideMiaoNetOffscreenNames { get; set; } = true;

    [DefaultValue(true)]
    public bool ShowRoomsRemaining { get; set; } = true;

    [DefaultValue(true)]
    public bool ShowMapPlayerCount { get; set; } = true;

    [DefaultValue(true)]
    public bool ShowClock { get; set; } = true;

    [DefaultValue(true)]
    public bool WatchedPlayerNotifications { get; set; } = true;

    public List<string> WatchedPlayers { get; set; } = [];

    [DefaultValue(false)]
    public bool RemoveRoomTransitions { get; set; }

    [DefaultValue(false)]
    public bool RemoveDeathAnimation { get; set; }

    [DefaultValue(CollisionBoxDisplayMode.Hidden)]
    public CollisionBoxDisplayMode CollisionBoxes { get; set; } = CollisionBoxDisplayMode.Hidden;

    [DefaultValue(true)]
    public bool ShowFps { get; set; } = true;

    [DefaultValue(true)]
    public bool ShowFrameTime { get; set; } = true;

    [DefaultValue(false)]
    public bool ShowPhysicalAndRenderFps { get; set; }

    [DefaultValue(false)]
    public bool EnableFrameProfiler { get; set; }

    [DefaultValue(true)]
    public bool ProfilerSimpleMode { get; set; } = true;

    [SettingRange(20, 250)]
    [DefaultValue(34)]
    public int FrameSpikeThresholdMs { get; set; } = 34;

    [SettingIgnore]
    [DefaultValue(false)]
    public bool AutoRecorderEnabled { get; set; }

    [DefaultValue(true)]
    public bool RecordingAutoSaveOnTransition { get; set; } = true;

    [SettingName("开始录制")]
    [DefaultButtonBinding(0, Keys.None)]
    public ButtonBinding StartRecordingBinding { get; set; } = new(0, Keys.None);

    [SettingName("结束录制")]
    [DefaultButtonBinding(0, Keys.None)]
    public ButtonBinding StopRecordingBinding { get; set; } = new(0, Keys.None);

    [DefaultValue(true)]
    public bool ShowRecordingIndicator { get; set; } = true;

    [DefaultValue(true)]
    public bool ShowRecordingDuration { get; set; } = true;

    [SettingIgnore]
    [DefaultValue(RecordingPolicy.EveryRoom)]
    public RecordingPolicy RecordingPolicy { get; set; } = RecordingPolicy.EveryRoom;

    // Keep the legacy serialized fields as the source of truth, so existing settings
    // retain their mode without a one-shot migration or conflicting saved properties.
    [YamlDotNet.Serialization.YamlIgnore]
    [SettingName("自动录制")]
    public AutoRecordingMode AutomaticRecording {
        get => !AutoRecorderEnabled ? AutoRecordingMode.Off
            : RecordingPolicy == RecordingPolicy.GoldenRunsOnly ? AutoRecordingMode.Golden : AutoRecordingMode.Chapter;
        set {
            AutoRecorderEnabled = value != AutoRecordingMode.Off;
            if (value != AutoRecordingMode.Off)
                RecordingPolicy = value == AutoRecordingMode.Golden ? RecordingPolicy.GoldenRunsOnly : RecordingPolicy.EveryRoom;
        }
    }

    [SettingName("金草莓成功时录到")]
    [DefaultValue(GoldenRecordingEnd.BerryCollected)]
    public GoldenRecordingEnd GoldenRecordingEnd { get; set; } = GoldenRecordingEnd.BerryCollected;

    [SettingName("金草莓死亡后")]
    [DefaultValue(GoldenRecordingDeath.Discard)]
    public GoldenRecordingDeath GoldenRecordingDeath { get; set; } = GoldenRecordingDeath.Discard;

    [SettingName("最多保留自动录像")]
    [DefaultValue(100)]
    [SettingRange(0, 500)]
    public int AutoRecordingRetentionCount { get; set; } = 100;

    [DefaultValue(BgmRecordingMode.SfxOnlyWithPostMix)]
    public BgmRecordingMode BgmMode { get; set; } = BgmRecordingMode.SfxOnlyWithPostMix;

    [DefaultValue(true)]
    public bool RecordingIncludeUiSfx { get; set; } = true;

    [DefaultValue(false)]
    public bool RecordingRemoveFreezeFrames { get; set; }

    [DefaultValue(true)]
    public bool RecordingEditingEnabled { get; set; } = true;

    [DefaultValue(false)]
    public bool RecordingKeepPausedFrames { get; set; }

    [DefaultValue(false)]
    public bool RecordingKeepFailedAttempts { get; set; }

    [DefaultValue(60)]
    [SettingRange(30, 120)]
    public int RecordingFrameRate { get; set; } = 60;

    [DefaultValue(12000)]
    [SettingRange(2000, 50000)]
    public int RecordingBitrateKbps { get; set; } = 12000;

    [DefaultValue(100)]
    [SettingRange(0, 500)]
    public int RecordingRetentionCount { get; set; } = 100;

    [DefaultValue(false)]
    public bool DeathReplayEnabled { get; set; }

    [DefaultValue(30)]
    [SettingRange(10, 60)]
    public int DeathReplayBufferSeconds { get; set; } = 30;

    [DefaultValue(30)]
    [SettingRange(0, 200)]
    public int DeathReplayRetentionCount { get; set; } = 30;

    [DefaultValue("")]
    public string RecordingDirectory { get; set; } = "";

    [DefaultValue("auto")]
    public string RecordingEncoder { get; set; } = "auto";

    [DefaultValue("")]
    public string BgmEventMapFile { get; set; } = "";

    internal void MigrateMiniMapBindings() {
        if (MiniMapBindingsMigrated) return;
        SetLegacyKey(MiniMapZoomInBinding, MiniMapZoomInKey);
        SetLegacyKey(MiniMapZoomOutBinding, MiniMapZoomOutKey);
        MiniMapBindingsMigrated = true;
    }

    private static void SetLegacyKey(ButtonBinding binding, Keys key) {
        binding.Keys.Clear();
        if (key != Keys.None) binding.Keys.Add(key);
    }
}
