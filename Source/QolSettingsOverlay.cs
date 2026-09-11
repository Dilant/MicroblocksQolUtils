using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

internal sealed class QolSettingsOverlay : Entity, IMaterialAcrylicPage {
    private const float ScreenWidth = 1920f;
    private const float ScreenHeight = 1080f;
    private const int Columns = 2;
    private const float RowHeight = 92f;
    private const float RowGap = 12f;
    private const float RecorderHeroHeight = 150f;
    private const float RecorderFileHeight = 104f;
    private const float RecorderSettingHeight = 92f;
    private const float DropdownItemHeight = 42f;
    private const int DropdownMaxVisibleItems = 7;
    private const float ProfilerRowHeight = 84f;
    private const float ProfilerRowGap = 10f;
    private const float ProfilerScrollStep = (ProfilerRowHeight + ProfilerRowGap) * 2f;

    private static QolSettingsOverlay? activePage;

    private readonly Level level;
    private readonly int returnIndex;
    private readonly bool minimal;
    private readonly bool oldAllowHudHide;
    private readonly List<SettingsTab> tabs;
    private readonly MaterialScrollController rowScroll = new();
    private readonly MaterialScrollViewport rowViewport = new("mqol-settings-rows");
    private readonly MaterialMotionController motion = new();
    private int selectedTab;
    private int selectedRow;
    private float inputDelay = 0.16f;
    private float ease;
    private float contentEase = 1f;
    private Entity? bindingConfig;
    private SettingRow? editingRow;
    private string editBuffer = "";
    private string imeText = "";
    private bool textInputSubscribed;
    private float editError;
    private SettingRow? draggedSlider;
    private SettingRow? dropdownRow;
    private int dropdownHighlight;
    private int dropdownFirstVisible;
    private CloseDestination closeDestination;
    private readonly List<RecordingLibraryEntry> recordingFiles = [];
    private enum RecorderSettingsSection { Automatic, DeathReplay, Quality, Storage, Controls, Library }
    private RecorderSettingsSection recorderSettingsSection;
    private List<SettingRow>? visibleRecorderRows;
    private RecorderSettingsSection? visibleRecorderSection;
    private AutoRecordingMode visibleAutoMode;
    private int autoRecordingCount;
    private RecordingLibraryKind recordingLibraryKind = RecordingLibraryKind.Full;
    private int fullRecordingCount;
    private int deathReplayCount;
    private int recorderSelectedItem;
    private float recordingRefreshTimer;
    private RecordingLibraryEntry? pendingRecordingDelete;
    private string recordingNotice = "";
    private float recordingNoticeTimer;

    public static QolSettingsOverlay? ActivePage => activePage is { Scene: not null, Visible: true }
        ? activePage
        : null;

    public bool SuppressNormalRender { get; set; }

    public QolSettingsOverlay(Level level, int returnIndex, bool minimal, bool oldAllowHudHide) {
        this.level = level;
        this.returnIndex = returnIndex;
        this.minimal = minimal;
        this.oldAllowHudHide = oldAllowHudHide;
        tabs = BuildTabs();
        foreach (SettingRow row in tabs.SelectMany(tab => tab.Rows)) row.InitializeVisuals();
        Tag = Tags.HUD | Tags.PauseUpdate | Tags.TransitionUpdate;
        Depth = -2_000_000;
    }

    public override void Added(Scene scene) {
        base.Added(scene);
        activePage = this;
        RefreshRecordingFiles();
    }

    public override void Removed(Scene scene) {
        if (activePage == this) activePage = null;
        bindingConfig?.RemoveSelf();
        bindingConfig = null;
        ReleaseFocusedInput();
        rowViewport.Dispose();
        motion.Dispose();
        base.Removed(scene);
    }

    public override void Update() {
        base.Update();
        ease = Calc.Approach(ease, closeDestination == CloseDestination.None ? 1f : 0f,
            Engine.RawDeltaTime * 7.5f);
        contentEase = Calc.Approach(contentEase, 1f, Engine.RawDeltaTime * 9f);
        editError = Calc.Approach(editError, 0f, Engine.RawDeltaTime * 4f);
        recordingNoticeTimer = Math.Max(0f, recordingNoticeTimer - Engine.RawDeltaTime);
        recordingRefreshTimer -= Engine.RawDeltaTime;
        if (IsRecorderTab && IsRecorderLibrary && recordingRefreshTimer <= 0f) RefreshRecordingFiles();

        if (bindingConfig is not null) return;

        OverlayLayout layout = OverlayLayout.Create(1f - Ease.CubeOut(ease));
        rowScroll.Update(MaxRowScroll(layout));
        UpdateRowAnimations(layout);
        UpdateInteractions(layout);

        if (closeDestination != CloseDestination.None) {
            if (ease <= 0.001f) FinishClose();
            return;
        }

        if (pendingRecordingDelete is not null) {
            UpdateRecordingDeleteModal();
            return;
        }

        inputDelay -= Engine.DeltaTime;
        if (editingRow is not null) {
            UpdateTextEdit(layout);
            return;
        }
        if (draggedSlider is not null) {
            UpdateSliderDrag(layout);
            if (draggedSlider is not null) return;
        }
        if (inputDelay > 0f) return;

        if (dropdownRow is not null) {
            UpdateDropdown(layout);
            return;
        }

        if (Input.Pause.Pressed) {
            BeginClose(CloseDestination.Game);
            return;
        }
        if (Input.MenuCancel.Pressed || Input.ESC.Pressed || MInput.Keyboard.Pressed(Keys.Escape)) {
            BeginClose(CloseDestination.PauseMenu);
            return;
        }
        if (MaterialTabNavigation.TryGetShiftVerticalDirection(out int tabDirection)) {
            SelectTab(selectedTab + tabDirection);
            return;
        }

        if (IsRecorderTab) {
            UpdateRecorderPage(layout);
            return;
        }

        if (IsProfilerTab) {
            if (Input.MenuLeft.Pressed) SetProfilerSimpleMode(true);
            else if (Input.MenuRight.Pressed) SetProfilerSimpleMode(false);
            if (Input.MenuUp.Pressed) rowScroll.Scroll(-ProfilerScrollStep, MaxRowScroll(layout));
            else if (Input.MenuDown.Pressed) rowScroll.Scroll(ProfilerScrollStep, MaxRowScroll(layout));
            if (Input.MenuConfirm.Pressed
                || MInput.Keyboard.Pressed(Keys.Enter)
                || MInput.Keyboard.Pressed(Keys.Space)) StartProfilerSampling();
            UpdateMouse(layout);
            return;
        }

        if (CurrentRows.Count > 0) {
            if (Input.MenuUp.Pressed) SelectRow(selectedRow - 1);
            else if (Input.MenuDown.Pressed) SelectRow(selectedRow + 1);
            else if (Input.MenuLeft.Pressed) AdjustSelected(-1);
            else if (Input.MenuRight.Pressed) AdjustSelected(1);
            else if (Input.MenuConfirm.Pressed
                || MInput.Keyboard.Pressed(Keys.Enter)
                || MInput.Keyboard.Pressed(Keys.Space)) ActivateSelected();
        }
        UpdateMouse(layout);
    }

    public override void Render() {
        base.Render();
        if (SuppressNormalRender) return;
        RenderMaterialContent(acrylicActive: false);
    }

    public void RenderMaterialContent(bool acrylicActive) {
        if (ease <= 0f) return;
        OverlayLayout layout = OverlayLayout.Create(1f - Ease.CubeOut(ease));
        MaterialPalette palette = MaterialPalette.FromSeed(new Color(126, 99, 184));
        Draw.Rect(0f, 0f, ScreenWidth, ScreenHeight, palette.Scrim * (0.90f * ease));

        MaterialSplitPageChrome.RenderHeader(layout.Header, "dashboard",
            "Microblock 的 QOL 工具", palette, ease);

        RenderNavigation(layout, palette);
        RenderContent(layout, palette);
        if (dropdownRow is not null) RenderDropdown(layout, palette);

        if (pendingRecordingDelete is not null) RenderRecordingDeleteModal(palette);
        MaterialUiKit.Cursor(MInput.Mouse.Position, palette, ease);
    }

    private void RenderNavigation(OverlayLayout layout, MaterialPalette palette) {
        MaterialSplitPageChrome.RenderNavigationSurface(layout.Navigation, palette, ease);
        MaterialUiKit.Text("功能分类",
            new Vector2(layout.NavigationItems.X + 8f,
                layout.Navigation.Y + OverlayLayout.NavigationHeaderHeight / 2f),
            new Vector2(0f, 0.5f), MaterialTextRole.Label, palette.Primary, ease,
            scaleOverride: 0.26f);

        for (int index = 0; index < tabs.Count; index++) {
            string key = $"settings.tab.{index}";
            MaterialRect tab = layout.Tab(index, tabs.Count);
            bool selected = index == selectedTab;
            SettingsTab settingsTab = tabs[index];
            MaterialSplitPageChrome.RenderSimpleTab(motion, key, tab, settingsTab.Icon,
                settingsTab.Title, selected, palette.Primary, palette, ease);
        }
    }

    private static void RenderContentHeading(string title, string icon, OverlayLayout layout,
        MaterialPalette palette, float alpha) {
        MaterialSplitPageChrome.RenderContentHeading(layout.ContentHeader, icon, title,
            layout.ContentHeader.Width - 56f, palette.Primary, palette, alpha);
    }

    private void RenderContent(OverlayLayout layout, MaterialPalette palette) {
        MaterialSplitPageChrome.RenderContentSurface(layout.Body, palette, ease);
        if (IsRecorderTab) {
            RenderRecorderPage(layout, palette);
            return;
        }
        if (IsProfilerTab) {
            RenderProfilerContent(layout, palette);
            return;
        }
        SettingsTab tab = tabs[selectedTab];
        RenderContentHeading(tab.Title, tab.Icon, layout, palette, ease * contentEase);
        RenderRows(layout, palette);

        float maximum = MaxRowScroll(layout);
        if (maximum <= 0f) return;
        float ratio = layout.Rows.Height / (layout.Rows.Height + maximum);
        float thumbHeight = Math.Max(52f, layout.Rows.Height * ratio);
        float travel = layout.Rows.Height - thumbHeight;
        float y = layout.Rows.Y + (maximum <= 0f ? 0f : rowScroll.Offset / maximum * travel);
        MaterialUi.RoundedRect(layout.Rows.Right + 8f, layout.Rows.Y, 5f, layout.Rows.Height, 2.5f,
            palette.Outline * (0.18f * ease));
        MaterialUi.RoundedRect(layout.Rows.Right + 8f, y, 5f, thumbHeight, 2.5f,
            palette.Primary * (0.68f * ease));
    }

    private void RenderRecorderPage(OverlayLayout layout, MaterialPalette palette) {
        float alpha = ease * contentEase;
        RenderContentHeading("录制中心", "videocam", layout, palette, alpha);
        string summary = recordingNoticeTimer > 0f
            ? recordingNotice
            : IsRecorderLibrary && recordingFiles.Count > 0
                ? $"{RecordingLibraryTitle} {recordingFiles.Count} 个  ·  {FormatBytes(recordingFiles.Sum(file => file.SizeBytes))}"
                : "";
        if (summary.Length > 0) {
            MaterialUiKit.Text(Trim(summary, 48), new Vector2(layout.ContentHeader.Right, layout.ContentHeader.Y + 8f),
                new Vector2(1f, 0f), MaterialTextRole.Caption,
                recordingNoticeTimer > 0f ? palette.Primary : palette.OnSurfaceVariant,
                alpha, scaleOverride: 0.28f);
        }

        RenderRecorderHero(RecorderHeroRect(layout), palette, alpha);
        foreach (RecorderSettingsSection section in Enum.GetValues<RecorderSettingsSection>()) {
            MaterialRect tab = RecorderSectionTabRect(layout, section);
            RenderRecordingLibraryTab(tab, FormatEnum(section), recorderSettingsSection == section,
                palette, alpha, $"settings.recorder.section.{section}");
            if (recorderSelectedItem < 0 && recorderSettingsSection == section)
                MaterialUi.RoundedOutline(tab.X, tab.Y, tab.Width, tab.Height, 18f, 2f, palette.OnSurface * alpha);
        }
        MaterialRect viewport = RecorderViewport(layout);
        rowViewport.Render(viewport, () => {
            if (IsRecorderLibrary) RenderRecordingLibrary(layout, palette, alpha);
            else RenderRecorderSettings(layout, palette, alpha);
        });

        float maximum = MaxRowScroll(layout);
        if (maximum <= 0f) return;
        float ratio = viewport.Height / (viewport.Height + maximum);
        float thumbHeight = Math.Max(52f, viewport.Height * ratio);
        float travel = viewport.Height - thumbHeight;
        float y = viewport.Y + rowScroll.Offset / maximum * travel;
        MaterialUi.RoundedRect(layout.Rows.Right + 8f, viewport.Y, 5f, viewport.Height, 2.5f,
            palette.Outline * (0.18f * ease));
        MaterialUi.RoundedRect(layout.Rows.Right + 8f, y, 5f, thumbHeight, 2.5f,
            palette.Primary * (0.68f * ease));
    }

    private void RenderRecorderHero(MaterialRect hero, MaterialPalette palette, float alpha) {
        MaterialUi.RoundedRect(hero.X, hero.Y + 4f, hero.Width, hero.Height, 28f, Color.Black * (0.12f * alpha));
        MaterialUi.RoundedRect(hero.X, hero.Y, hero.Width, hero.Height, 28f, palette.SurfaceHighest * alpha);
        MaterialUi.RoundedOutline(hero.X, hero.Y, hero.Width, hero.Height, 28f, 1f,
            palette.Primary * (0.30f * alpha));

        bool active = AutoRecorder.IsRecording || AutoRecorder.ManualMode;
        Color statusColor = AutoRecorder.IsRecording ? new Color(244, 91, 105) : palette.Primary;
        MaterialUi.Circle(new Vector2(hero.X + 34f, hero.Y + 39f), 8f, statusColor * alpha);
        MaterialUiKit.Text(RecordingStatus(), new Vector2(hero.X + 54f, hero.Y + 25f), Vector2.Zero,
            MaterialTextRole.Section, palette.OnSurface, alpha, scaleOverride: 0.39f);
        string duration = AutoRecorder.IsRecording ? FormatDuration(AutoRecorder.DisplaySeconds) : "00:00";
        MaterialUiKit.Text(duration, new Vector2(hero.X + 54f, hero.Y + 65f), Vector2.Zero,
            MaterialTextRole.Display, active ? palette.Primary : palette.OnSurfaceVariant,
            alpha, scaleOverride: 0.52f);
        string? detail = AutoRecorder.IsRecording
            ? AutoRecorder.ContinuingAfterGoldenDeath ? "已死亡 · 保留全过程，继续到通关"
                : $"保存至：{(AutoRecorder.ManualMode ? "full" : "auto")}/<区域>"
            : AutoRecorder.AutoRecordingSuppressed ? "本次已停止 · 不会自动重开" : null;
        if (detail is not null) {
            MaterialUiKit.Text(Trim(detail, 48), new Vector2(hero.X + 190f, hero.Y + 75f), Vector2.Zero,
                MaterialTextRole.Caption, palette.OnSurfaceVariant, alpha, scaleOverride: 0.26f);
        }

        RenderRecorderButton(RecorderButtonRect(hero, 0), "打开文件夹", true, palette, alpha,
            "settings.recorder.folder");
        RenderRecorderButton(RecorderButtonRect(hero, 1),
            active ? "停止并保存" : "开始手动录制", true, palette, alpha,
            "settings.recorder.toggle", primary: true);
        RenderRecorderButton(RecorderButtonRect(hero, 2), "丢弃", active, palette, alpha,
            "settings.recorder.discard", danger: active);
    }

    private void RenderRecorderButton(MaterialRect rect, string text, bool enabled,
        MaterialPalette palette, float alpha, string key, bool primary = false, bool danger = false) {
        float emphasis = motion.Emphasis(key);
        Color dangerColor = new(205, 78, 92);
        Color fill = danger
            ? dangerColor
            : primary
                ? palette.Primary
                : palette.Surface;
        float fillAlpha = enabled ? MathHelper.Lerp(primary || danger ? 0.88f : 0.76f, 1f, emphasis) : 0.24f;
        MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, 17f, fill * (alpha * fillAlpha));
        motion.RenderStateLayer(key, rect, 17f, primary || danger ? palette.OnPrimary : palette.Primary, alpha);
        if (!primary && !danger) {
            MaterialUi.RoundedOutline(rect.X, rect.Y, rect.Width, rect.Height, 17f, 1f,
                palette.Outline * (alpha * MathHelper.Lerp(0.45f, 0.90f, emphasis)));
        }
        Color textColor = enabled
            ? primary || danger ? palette.OnPrimary : palette.OnSurface
            : palette.OnSurfaceVariant * 0.50f;
        MaterialUiKit.Text(text, rect.Center, new Vector2(0.5f),
            MaterialTextRole.Label, textColor, alpha, scaleOverride: 0.28f);
    }

    private void RenderRecordingLibrary(OverlayLayout layout, MaterialPalette palette, float alpha) {
        float headerY = RecorderLibraryHeaderY(layout);
        MaterialUiKit.Text("录像库", new Vector2(layout.Rows.X + 4f, headerY), Vector2.Zero,
            MaterialTextRole.Section, palette.OnSurface, alpha, scaleOverride: 0.36f);

        RenderRecordingLibraryTab(
            RecorderLibraryTabRect(layout, 0),
            AutoRecorder.PendingDeathReplayCount > 0
                ? $"最近死亡  {deathReplayCount}  ·  待保存 {AutoRecorder.PendingDeathReplayCount}"
                : $"最近死亡  {deathReplayCount}",
            recordingLibraryKind == RecordingLibraryKind.DeathReplay,
            palette,
            alpha,
            "settings.recorder.library.deaths"
        );
        RenderRecordingLibraryTab(
            RecorderLibraryTabRect(layout, 1),
            $"手动录像  {fullRecordingCount}",
            recordingLibraryKind == RecordingLibraryKind.Full,
            palette,
            alpha,
            "settings.recorder.library.full"
        );
        RenderRecordingLibraryTab(RecorderLibraryTabRect(layout, 2), $"自动录像  {autoRecordingCount}",
            recordingLibraryKind == RecordingLibraryKind.Automatic, palette, alpha,
            "settings.recorder.library.auto");

        if (recordingFiles.Count == 0) {
            MaterialRect empty = RecorderEmptyRect(layout);
            MaterialUi.RoundedRect(empty.X, empty.Y, empty.Width, empty.Height, 22f,
                palette.SurfaceHigh * (0.64f * alpha));
            if (recorderSelectedItem == CurrentRows.Count) {
                MaterialUi.RoundedOutline(empty.X, empty.Y, empty.Width, empty.Height, 22f, 2f,
                    palette.Primary * (0.68f * alpha));
            }
            string emptyTitle = $"暂无{RecordingLibraryTitle}";
            MaterialUiKit.Text(emptyTitle, empty.Center,
                new Vector2(0.5f), MaterialTextRole.Section, palette.OnSurfaceVariant, alpha,
                scaleOverride: 0.34f);
            return;
        }

        for (int index = 0; IsRecorderLibrary && index < recordingFiles.Count; index++) {
            MaterialRect rect = RecorderFileRect(layout, index);
            if (rect.Bottom < layout.Rows.Y || rect.Y > layout.Rows.Bottom) continue;
            RenderRecordingFile(recordingFiles[index], rect,
                recorderSelectedItem == CurrentRows.Count + index, palette, alpha);
        }
    }

    private void RenderRecordingFile(RecordingLibraryEntry file, MaterialRect rect, bool selected,
        MaterialPalette palette, float alpha) {
        bool finalizing = AutoRecorder.TryGetFinalizationProgress(file.Path, out double progress, out _);
        string key = $"settings.recorder.file.{file.Path}";
        float emphasis = Math.Max(selected ? 1f : 0f, motion.Emphasis(key));
        Color surface = finalizing
            ? Color.Lerp(palette.SurfaceHigh, palette.Primary, 0.10f)
            : palette.SurfaceHigh * 0.72f;
        Color fill = Color.Lerp(surface, palette.SurfaceHighest, emphasis);
        MaterialUi.RoundedRect(rect.X, rect.Y + 2f, rect.Width, rect.Height, 24f,
            Color.Black * (0.08f * alpha));
        MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, 24f, fill * alpha);
        motion.RenderStateLayer(key, rect, 24f, palette.Primary, alpha);
        if (emphasis > 0.01f) {
            MaterialUi.RoundedOutline(rect.X, rect.Y, rect.Width, rect.Height, 24f,
                1f + emphasis, palette.Primary * (alpha * MathHelper.Lerp(0.28f, 0.78f, emphasis)));
        }

        MaterialRect icon = new(rect.X + 20f, rect.Y + (finalizing ? 18f : 28f), 48f, 48f);
        MaterialUi.RoundedRect(icon.X, icon.Y, icon.Width, icon.Height, 16f,
            palette.Primary * ((finalizing ? 0.18f : 0.10f) * alpha));
        MaterialIcon.Draw(file.Kind == RecordingLibraryKind.DeathReplay ? "replay" : "movie",
            icon.Center, 27f, palette.Primary, alpha);

        float textX = rect.X + 84f;
        float titleY = rect.Y + (finalizing ? 20f : 29f);
        MaterialRect status = new(rect.Right - 218f, rect.Y + 16f, 198f, 38f);
        float textRight = (finalizing ? status.X : RecorderFileOpenRect(rect).X) - 20f;
        float textWidth = Math.Max(0f, textRight - textX);
        MaterialUiKit.Text(MaterialTextUtil.Ellipsize(file.FileName, textWidth, 0.30f, UiFontWeight.Bold),
            new Vector2(textX, titleY), Vector2.Zero, MaterialTextRole.Label,
            palette.OnSurface, alpha, scaleOverride: 0.30f);
        string metadata = finalizing
            ? $"{file.ModifiedAt:yyyy-MM-dd HH:mm}  ·  {file.RelativeDirectory}"
            : $"{file.ModifiedAt:yyyy-MM-dd HH:mm}  ·  {FormatBytes(file.SizeBytes)}  ·  {file.RelativeDirectory}";
        MaterialUiKit.Text(MaterialTextUtil.Ellipsize(metadata, textWidth, 0.24f),
            new Vector2(textX, titleY + 30f), Vector2.Zero, MaterialTextRole.Caption,
            palette.OnSurfaceVariant, alpha, scaleOverride: 0.24f);

        if (finalizing) {
            RenderRecordingGeneration(status, new MaterialRect(textX, rect.Bottom - 22f,
                rect.Right - 20f - textX, 6f), progress, palette, alpha);
        } else {
            RenderFileAction(RecorderFileOpenRect(rect), "播放", false, palette, alpha, key + ".open");
            RenderFileAction(RecorderFileDeleteRect(rect), "删除", true, palette, alpha, key + ".delete");
        }
    }

    private static void RenderRecordingGeneration(MaterialRect status, MaterialRect track,
        double value, MaterialPalette palette, float alpha) {
        float progress = (float)Math.Clamp(value, 0d, 1d);
        string label = progress <= 0f ? "准备生成" : progress >= 0.99f ? "即将完成" : "正在生成";
        MaterialUi.RoundedRect(status.X, status.Y, status.Width, status.Height, status.Height / 2f,
            palette.Primary * (0.16f * alpha));
        MaterialUiKit.Text(label, new Vector2(status.X + 16f, status.Center.Y), new Vector2(0f, 0.5f),
            MaterialTextRole.Label, palette.Primary, alpha, scaleOverride: 0.26f);
        // Reserve 100% for a finished file, even when the native encoder rounds up early.
        string percentage = $"{Math.Min(99, (int)Math.Floor(progress * 100d))}%";
        MaterialUiKit.Text(percentage, new Vector2(status.Right - 16f, status.Center.Y), new Vector2(1f, 0.5f),
            MaterialTextRole.Label, palette.Primary, alpha, scaleOverride: 0.29f);

        float filled = track.Width * progress;
        // The gap and end stop follow Material's determinate linear progress treatment.
        float remainingX = track.X + Math.Max(0f, filled > 0f ? filled + 5f : 0f);
        if (remainingX < track.Right) {
            MaterialUi.RoundedRect(remainingX, track.Y, track.Right - remainingX, track.Height,
                track.Height / 2f, palette.Primary * (0.16f * alpha));
        }
        if (filled > 0f) {
            MaterialUi.RoundedRect(track.X, track.Y, filled, track.Height, track.Height / 2f,
                palette.Primary * alpha);
        }
        MaterialUi.Circle(new Vector2(track.Right - 2f, track.Y + track.Height / 2f), 2f,
            palette.Primary * alpha);
    }

    private void RenderRecordingLibraryTab(
        MaterialRect rect,
        string text,
        bool selected,
        MaterialPalette palette,
        float alpha,
        string key
    ) {
        float emphasis = motion.Emphasis(key);
        Color fill = selected ? palette.Primary : Color.Lerp(palette.SurfaceHigh, palette.SurfaceHighest, emphasis);
        MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, 18f,
            fill * (alpha * (selected ? 0.90f : MathHelper.Lerp(0.66f, 0.92f, emphasis))));
        motion.RenderStateLayer(key, rect, 18f, selected ? palette.OnPrimary : palette.Primary, alpha);
        if (!selected) {
            MaterialUi.RoundedOutline(rect.X, rect.Y, rect.Width, rect.Height, 18f, 1f,
                palette.Outline * (alpha * 0.48f));
        }
        MaterialUiKit.Text(text, rect.Center, new Vector2(0.5f), MaterialTextRole.Label,
            selected ? palette.OnPrimary : palette.OnSurfaceVariant, alpha, scaleOverride: 0.28f);
    }

    private void RenderFileAction(MaterialRect rect, string text, bool danger,
        MaterialPalette palette, float alpha, string key) {
        float emphasis = motion.Emphasis(key);
        Color dangerColor = new(205, 78, 92);
        Color fill = danger ? dangerColor : palette.Primary;
        MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, 15f,
            fill * (alpha * MathHelper.Lerp(0.30f, 0.92f, emphasis)));
        motion.RenderStateLayer(key, rect, 15f, palette.OnPrimary, alpha);
        MaterialUiKit.Text(text, rect.Center, new Vector2(0.5f),
            MaterialTextRole.Label, emphasis > 0.35f ? palette.OnPrimary : danger ? new Color(255, 205, 210) : palette.Primary,
            alpha, scaleOverride: 0.25f);
    }

    private void RenderRecorderSettings(OverlayLayout layout, MaterialPalette palette, float alpha) {
        for (int index = 0; index < CurrentRows.Count; index++) {
            MaterialRect rect = RecorderSettingRect(layout, index);
            if (rect.Bottom < layout.Rows.Y || rect.Y > layout.Rows.Bottom) continue;
            RenderRow(CurrentRows[index], rect, palette, alpha, $"settings.row.{selectedTab}.{index}");
        }
    }

    private void RenderRows(OverlayLayout layout, MaterialPalette palette) {
        List<SettingRow> rows = CurrentRows;
        rowViewport.Render(layout.Rows, () => {
            for (int index = 0; index < rows.Count; index++) {
                SettingRow row = rows[index];
                MaterialRect rect = layout.Row(index, rowScroll.Offset)
                    .Offset(0f, (1f - contentEase) * 14f);
                if (rect.Bottom < layout.Rows.Y || rect.Y > layout.Rows.Bottom) continue;
                RenderRow(row, rect, palette, ease * contentEase, $"settings.row.{selectedTab}.{index}");
            }
        });
    }

    private void RenderRow(SettingRow row, MaterialRect rect, MaterialPalette palette, float alpha, string key) {
        bool enabled = row.Enabled();
        bool highlighted = row.HighlightWhenOn && row.ToggleValue?.Invoke() == true;
        float emphasis = Math.Max(row.Pulse * 0.42f,
            Math.Max(motion.Emphasis(key), Math.Max(row.FocusAnimation, row.HoverAnimation * 0.72f)));
        Color fill = highlighted
            ? Color.Lerp(palette.Primary * 0.18f, palette.Primary * 0.34f, Math.Max(emphasis, 0.35f))
            : Color.Lerp(palette.SurfaceHigh * 0.66f, palette.SurfaceHighest, emphasis);
        if (emphasis > 0.02f) {
            MaterialUi.RoundedRect(rect.X, rect.Y + 3f, rect.Width, rect.Height, 23f,
                Color.Black * (0.08f * alpha * emphasis));
        }
        MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, 23f,
            fill * (alpha * (enabled ? 1f : 0.48f)));
        motion.RenderStateLayer(key, rect, 23f, palette.Primary, alpha * (enabled ? 1f : 0.45f));
        if (emphasis > 0.01f) {
            MaterialUi.RoundedOutline(rect.X, rect.Y, rect.Width, rect.Height, 23f,
                1f + emphasis, palette.Primary * (alpha * emphasis));
        }
        if (highlighted) {
            MaterialUi.RoundedOutline(rect.X, rect.Y, rect.Width, rect.Height, 23f,
                2f, palette.Primary * (0.92f * alpha));
        }

        Color labelColor = highlighted
            ? palette.Primary
            : enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.55f;
        bool centeredLabel = row.Kind is SettingKind.Toggle or SettingKind.Action;
        Vector2 labelPosition = centeredLabel
            ? new Vector2(rect.X + 18f, rect.Center.Y)
            : new Vector2(rect.X + 18f, rect.Y + 12f);
        MaterialUiKit.Text(row.Label, labelPosition,
            centeredLabel ? new Vector2(0f, 0.5f) : Vector2.Zero,
            MaterialTextRole.Label, labelColor, alpha, scaleOverride: 0.31f);

        switch (row.Kind) {
            case SettingKind.Toggle:
                RenderToggle(row, rect, palette, alpha, enabled);
                break;
            case SettingKind.Range:
                RenderRange(row, rect, palette, alpha, enabled);
                break;
            case SettingKind.Enum:
                RenderEnum(row, rect, palette, alpha, enabled);
                break;
            case SettingKind.Text:
                RenderTextField(row, rect, palette, alpha, enabled);
                break;
            case SettingKind.Action:
                RenderAction(row, rect, palette, alpha, enabled);
                break;
            case SettingKind.Status:
                RenderStatus(row, rect, palette, alpha);
                break;
        }
    }

    private static void RenderToggle(SettingRow row, MaterialRect rect, MaterialPalette palette,
        float alpha, bool enabled) {
        MaterialRect control = ToggleRect(rect);
        float state = row.ToggleAnimation;
        Color track = Color.Lerp(palette.Outline * 0.45f, palette.Primary, state);
        MaterialUi.RoundedRect(control.X, control.Y, control.Width, control.Height, control.Height / 2f,
            track * (alpha * (enabled ? 1f : 0.45f)));
        float knobX = MathHelper.Lerp(control.X + 14f, control.Right - 14f, state);
        MaterialUi.Circle(new Vector2(knobX, control.Center.Y), 10f,
            Color.Lerp(palette.OnSurfaceVariant, palette.OnPrimary, state) * alpha);
        MaterialUiKit.Text(row.ToggleValue?.Invoke() == true ? "开" : "关",
            new Vector2(control.X - 14f, control.Center.Y), new Vector2(1f, 0.5f),
            MaterialTextRole.Caption, palette.OnSurfaceVariant, alpha, scaleOverride: 0.27f);
    }

    private void RenderRange(SettingRow row, MaterialRect rect, MaterialPalette palette,
        float alpha, bool enabled) {
        MaterialRect track = SliderRect(rect);
        float state = row.SliderAnimation;
        MaterialUi.RoundedRect(track.X, track.Y, track.Width, track.Height, track.Height / 2f,
            palette.Outline * (0.32f * alpha));
        float fillWidth = Math.Max(track.Height, track.Width * state);
        MaterialUi.RoundedRect(track.X, track.Y, fillWidth, track.Height, track.Height / 2f,
            palette.Primary * (alpha * (enabled ? 1f : 0.45f)));
        MaterialUi.Circle(new Vector2(track.X + track.Width * state, track.Center.Y), 10f,
            palette.Primary * (alpha * (enabled ? 1f : 0.45f)));
        MaterialRect valueRect = RangeValueRect(rect);
        bool editing = editingRow == row;
        Color outline = editError > 0f && editing ? Color.OrangeRed : editing ? palette.Primary : palette.Outline;
        MaterialUi.RoundedRect(valueRect.X, valueRect.Y, valueRect.Width, valueRect.Height, 15f,
            palette.Surface * (0.76f * alpha));
        MaterialUi.RoundedOutline(valueRect.X, valueRect.Y, valueRect.Width, valueRect.Height, 15f,
            editing ? 2f : 1f, outline * (alpha * (editing ? 1f : 0.48f)));
        string shown = editing ? editBuffer + imeText : row.Value();
        MaterialUiKit.Text(Trim(shown, 13), valueRect.Center,
            new Vector2(0.5f), MaterialTextRole.Label,
            enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.5f,
            alpha, scaleOverride: 0.26f);
        if (editing && Scene.BetweenInterval(0.5f)) {
            float caretX = Math.Min(valueRect.Right - 8f,
                valueRect.Center.X + SystemTtfFont.MeasureVisible(Trim(shown, 13), 0.26f,
                    UiFontWeight.Bold).X / 2f + 2f);
            MaterialUi.Line(new Vector2(caretX, valueRect.Y + 7f),
                new Vector2(caretX, valueRect.Bottom - 7f), 2f, palette.Primary * alpha);
            SetTextInputRectangle(valueRect);
        }
    }

    private void RenderEnum(SettingRow row, MaterialRect rect, MaterialPalette palette,
        float alpha, bool enabled) {
        MaterialRect control = WideControlRect(rect);
        bool open = dropdownRow == row;
        MaterialUi.RoundedRect(control.X, control.Y, control.Width, control.Height, 16f,
            palette.Surface * (0.72f * alpha));
        MaterialUi.RoundedOutline(control.X, control.Y, control.Width, control.Height, 16f,
            open ? 2f : 1f, (open ? palette.Primary : palette.Outline) * (alpha * (open ? 1f : 0.48f)));
        MaterialUiKit.Text(Trim(row.Value(), 34), new Vector2(control.X + 14f, control.Center.Y),
            new Vector2(0f, 0.5f), MaterialTextRole.Label,
            enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.5f, alpha, scaleOverride: 0.28f);
        MaterialUiKit.Text(open ? "▲" : "▼", new Vector2(control.Right - 16f, control.Center.Y),
            new Vector2(1f, 0.5f), MaterialTextRole.Label, palette.OnSurfaceVariant, alpha,
            scaleOverride: 0.20f);
    }

    private void RenderDropdown(OverlayLayout layout, MaterialPalette palette) {
        SettingRow? row = dropdownRow;
        if (row?.Options is null || row.Options.Count == 0) return;
        MaterialRect menu = DropdownRect(layout, row);
        float alpha = ease * contentEase;
        MaterialUi.RoundedRect(menu.X, menu.Y + 5f, menu.Width, menu.Height, 18f,
            Color.Black * (0.24f * alpha));
        MaterialUi.RoundedRect(menu.X, menu.Y, menu.Width, menu.Height, 18f,
            palette.SurfaceHighest * alpha);
        MaterialUi.RoundedOutline(menu.X, menu.Y, menu.Width, menu.Height, 18f, 1f,
            palette.Outline * (0.58f * alpha));

        int visibleCount = DropdownVisibleCount(row);
        int selected = row.SelectedOption?.Invoke() ?? -1;
        for (int visibleIndex = 0; visibleIndex < visibleCount; visibleIndex++) {
            int optionIndex = dropdownFirstVisible + visibleIndex;
            string key = $"settings.dropdown.{optionIndex}";
            MaterialRect item = DropdownItemRect(menu, visibleIndex);
            bool highlighted = optionIndex == dropdownHighlight;
            bool current = optionIndex == selected;
            if (highlighted) {
                MaterialUi.RoundedRect(item.X, item.Y, item.Width, item.Height, 13f,
                    palette.Primary * (0.88f * alpha));
            }
            motion.RenderStateLayer(key, item, 13f,
                highlighted ? palette.OnPrimary : palette.Primary, alpha);
            MaterialUiKit.Text(Trim(row.Options[optionIndex], 38),
                new Vector2(item.X + 14f, item.Center.Y), new Vector2(0f, 0.5f),
                MaterialTextRole.Label,
                highlighted ? palette.OnPrimary : palette.OnSurface,
                alpha, scaleOverride: 0.27f);
            if (current) {
                MaterialUiKit.Text("✓", new Vector2(item.Right - 14f, item.Center.Y),
                    new Vector2(1f, 0.5f), MaterialTextRole.Label,
                    highlighted ? palette.OnPrimary : palette.Primary,
                    alpha, scaleOverride: 0.25f);
            }
        }

        if (row.Options.Count > visibleCount) {
            float trackHeight = menu.Height - 16f;
            float thumbHeight = Math.Max(28f, trackHeight * visibleCount / row.Options.Count);
            float maximum = row.Options.Count - visibleCount;
            float thumbY = menu.Y + 8f + (trackHeight - thumbHeight) * dropdownFirstVisible / maximum;
            MaterialUi.RoundedRect(menu.Right - 5f, menu.Y + 8f, 3f, trackHeight, 1.5f,
                palette.Outline * (0.28f * alpha));
            MaterialUi.RoundedRect(menu.Right - 5f, thumbY, 3f, thumbHeight, 1.5f,
                palette.Primary * (0.82f * alpha));
        }
    }

    private void RenderTextField(SettingRow row, MaterialRect rect, MaterialPalette palette,
        float alpha, bool enabled) {
        MaterialRect control = WideControlRect(rect);
        bool editing = editingRow == row;
        Color outline = editError > 0f && editing ? Color.OrangeRed : editing ? palette.Primary : palette.Outline;
        MaterialUi.RoundedRect(control.X, control.Y, control.Width, control.Height, 15f,
            palette.Surface * (0.76f * alpha));
        MaterialUi.RoundedOutline(control.X, control.Y, control.Width, control.Height, 15f,
            editing ? 2f : 1f, outline * (alpha * (editing ? 1f : 0.48f)));
        string shown = editing ? editBuffer + imeText : row.Value();
        string placeholder = row.Placeholder ?? "点击输入";
        string text = shown.Length == 0 ? placeholder : shown;
        Color color = shown.Length == 0 ? palette.OnSurfaceVariant * 0.55f : palette.OnSurface;
        MaterialUiKit.Text(TrimFromLeft(text, 46), new Vector2(control.X + 14f, control.Center.Y),
            new Vector2(0f, 0.5f), MaterialTextRole.Caption, enabled ? color : color * 0.45f, alpha,
            scaleOverride: 0.27f);
        if (editing && Scene.BetweenInterval(0.5f)) {
            string visible = TrimFromLeft(shown, 46);
            float caretX = Math.Min(control.Right - 12f,
                control.X + 14f + SystemTtfFont.MeasureVisible(visible, 0.27f).X + 2f);
            MaterialUi.Line(new Vector2(caretX, control.Y + 7f),
                new Vector2(caretX, control.Bottom - 7f), 2f, palette.Primary * alpha);
        }
        if (editing) SetTextInputRectangle(control);
    }

    private static void RenderAction(SettingRow row, MaterialRect rect, MaterialPalette palette,
        float alpha, bool enabled) {
        MaterialRect control = ActionControlRect(row, rect);
        MaterialUi.RoundedRect(control.X, control.Y, control.Width, control.Height, 16f,
            (enabled ? palette.Primary : palette.Outline) * (alpha * (enabled ? 0.88f : 0.25f)));
        MaterialUiKit.Text(row.Value(), control.Center, new Vector2(0.5f),
            MaterialTextRole.Label, enabled ? palette.OnPrimary : palette.OnSurfaceVariant * 0.55f,
            alpha, scaleOverride: 0.28f);
    }

    private static void RenderStatus(SettingRow row, MaterialRect rect, MaterialPalette palette, float alpha) {
        MaterialUiKit.Text(Trim(row.Value(), 42), new Vector2(rect.X + 18f, rect.Bottom - 31f),
            Vector2.Zero, MaterialTextRole.Body, palette.Primary, alpha, scaleOverride: 0.30f);
    }

    private void RenderRecordingDeleteModal(MaterialPalette palette) {
        RecordingLibraryEntry? pending = pendingRecordingDelete;
        if (pending is null) return;
        MaterialRect modal = RecordingDeleteModalRect();
        Draw.Rect(0f, 0f, ScreenWidth, ScreenHeight, Color.Black * (0.58f * ease));
        MaterialUiKit.Surface(modal, 32f, palette, ease);
        MaterialUiKit.Text("删除这段录像？", new Vector2(modal.Center.X, modal.Y + 42f),
            new Vector2(0.5f, 0f), MaterialTextRole.Title, palette.OnSurface, ease, scaleOverride: 0.46f);
        MaterialUiKit.Text(Trim(pending.Value.FileName, 52), new Vector2(modal.Center.X, modal.Y + 103f),
            new Vector2(0.5f, 0f), MaterialTextRole.Body, palette.OnSurfaceVariant, ease,
            scaleOverride: 0.29f);
        MaterialUiKit.Text("文件会从磁盘永久删除，此操作无法撤销。", new Vector2(modal.Center.X, modal.Y + 145f),
            new Vector2(0.5f, 0f), MaterialTextRole.Caption, new Color(255, 183, 190), ease,
            scaleOverride: 0.27f);
        RenderRecorderButton(RecordingDeleteCancelRect(modal), "取消", true, palette, ease,
            "settings.delete.cancel");
        RenderRecorderButton(RecordingDeleteConfirmRect(modal), "永久删除", true, palette, ease,
            "settings.delete.confirm", danger: true);
    }

    private void UpdateRecorderPage(OverlayLayout layout) {
        if (MInput.Mouse.WheelDelta != 0 && layout.Body.Contains(MInput.Mouse.Position)) {
            rowScroll.Scroll(-Math.Sign(MInput.Mouse.WheelDelta) * 178f, MaxRowScroll(layout));
        }

        if (MInput.Keyboard.Pressed(Keys.F)) OpenRecordingFolder();
        if (MInput.Keyboard.Pressed(Keys.R)) ToggleManualRecording();
        if (MInput.Keyboard.Pressed(Keys.X) && RecorderActive) DiscardManualRecording();

        if (MInput.Keyboard.Pressed(Keys.Q) || MInput.Keyboard.Pressed(Keys.E)) {
            SelectRecorderSection((int)recorderSettingsSection + (MInput.Keyboard.Pressed(Keys.Q) ? -1 : 1));
            return;
        }
        if (Input.MenuUp.Pressed) SelectRecorderItem(recorderSelectedItem - 1, layout);
        else if (Input.MenuDown.Pressed) SelectRecorderItem(recorderSelectedItem + 1, layout);
        else if (Input.MenuLeft.Pressed || Input.MenuRight.Pressed) {
            SettingRow? row = SelectedRecorderSetting();
            int direction = Input.MenuLeft.Pressed ? -1 : 1;
            if (recorderSelectedItem < 0) SelectRecorderSection((int)recorderSettingsSection + direction);
            else if (row is not null) AdjustRecorderSetting(row, direction);
            else if (IsRecorderLibrary) SelectRecordingLibraryKind(direction, layout);
        } else if (Input.MenuConfirm.Pressed
            || MInput.Keyboard.Pressed(Keys.Enter)
            || MInput.Keyboard.Pressed(Keys.Space)) {
            ActivateRecorderSelection();
        }
        if (MInput.Keyboard.Pressed(Keys.Delete)) RequestSelectedRecordingDelete();

        UpdateRecorderMouse(layout);
    }

    private void UpdateRecorderMouse(OverlayLayout layout) {
        Vector2 mouse = MInput.Mouse.Position;
        if (!MInput.Mouse.WasMoved && !MInput.Mouse.PressedLeftButton) return;

        for (int index = 0; index < tabs.Count; index++) {
            if (!layout.Tab(index, tabs.Count).Contains(mouse)) continue;
            if (MInput.Mouse.PressedLeftButton) SelectTab(index);
            return;
        }
        if (!layout.Rows.Contains(mouse)) return;

        MaterialRect hero = RecorderHeroRect(layout);
        if (MInput.Mouse.PressedLeftButton) {
            if (RecorderButtonRect(hero, 0).Contains(mouse)) {
                OpenRecordingFolder();
                return;
            }
            if (RecorderButtonRect(hero, 1).Contains(mouse)) {
                ToggleManualRecording();
                return;
            }
            if (RecorderButtonRect(hero, 2).Contains(mouse)) {
                if (RecorderActive) DiscardManualRecording();
                else Audio.Play("event:/ui/main/button_invalid");
                return;
            }
        }

        foreach (RecorderSettingsSection section in Enum.GetValues<RecorderSettingsSection>()) {
            if (!RecorderSectionTabRect(layout, section).Contains(mouse)) continue;
            if (MInput.Mouse.PressedLeftButton) SelectRecorderSection((int)section);
            return;
        }
        if (!RecorderViewport(layout).Contains(mouse)) return;

        for (int index = 0; IsRecorderLibrary && index < 3; index++) {
            if (!RecorderLibraryTabRect(layout, index).Contains(mouse)) continue;
            if (MInput.Mouse.PressedLeftButton)
                SelectRecordingLibraryKind((RecordingLibraryKind)index, layout);
            return;
        }

        for (int index = 0; index < CurrentRows.Count; index++) {
            MaterialRect rect = RecorderSettingRect(layout, index);
            if (!rect.Contains(mouse)) continue;
            recorderSelectedItem = index;
            if (MInput.Mouse.PressedLeftButton) ActivateMouse(CurrentRows[index], rect, mouse);
            return;
        }

        for (int index = 0; IsRecorderLibrary && index < recordingFiles.Count; index++) {
            MaterialRect rect = RecorderFileRect(layout, index);
            if (!rect.Contains(mouse)) continue;
            recorderSelectedItem = CurrentRows.Count + index;
            if (!MInput.Mouse.PressedLeftButton) return;
            if (IsRecordingFinalizing(recordingFiles[index])) {
                Audio.Play("event:/ui/main/button_invalid");
                return;
            }
            if (RecorderFileDeleteRect(rect).Contains(mouse)) {
                pendingRecordingDelete = recordingFiles[index];
                Audio.Play("event:/ui/main/button_select");
            } else {
                OpenRecording(recordingFiles[index]);
            }
            return;
        }
    }

    private void UpdateRecordingDeleteModal() {
        if (MInput.Keyboard.Pressed(Keys.Escape) || Input.MenuCancel.Pressed) {
            pendingRecordingDelete = null;
            Audio.Play("event:/ui/main/button_back");
            return;
        }
        if (MInput.Keyboard.Pressed(Keys.Enter) || Input.MenuConfirm.Pressed) {
            ConfirmRecordingDelete();
            return;
        }
        if (!MInput.Mouse.PressedLeftButton) return;
        MaterialRect modal = RecordingDeleteModalRect();
        if (RecordingDeleteCancelRect(modal).Contains(MInput.Mouse.Position)) {
            pendingRecordingDelete = null;
            Audio.Play("event:/ui/main/button_back");
        } else if (RecordingDeleteConfirmRect(modal).Contains(MInput.Mouse.Position)) {
            ConfirmRecordingDelete();
        }
    }

    private void ActivateRecorderSelection() {
        if (recorderSelectedItem < 0) {
            SelectRecorderItem(0, OverlayLayout.Create(1f - Ease.CubeOut(ease)));
            return;
        }
        SettingRow? row = SelectedRecorderSetting();
        if (row is not null) {
            Activate(row);
            return;
        }
        if (!IsRecorderLibrary) return;
        int fileIndex = recorderSelectedItem - CurrentRows.Count;
        if (fileIndex < 0 || fileIndex >= recordingFiles.Count) return;
        if (IsRecordingFinalizing(recordingFiles[fileIndex])) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        OpenRecording(recordingFiles[fileIndex]);
    }

    private void AdjustRecorderSetting(SettingRow row, int direction) {
        if (!row.Enabled() || row.Change is null
            || row.Kind is SettingKind.Action or SettingKind.Status or SettingKind.Text) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        row.Change(direction);
        row.Pulse = 1f;
        Audio.Play("event:/ui/main/button_toggle_on");
    }

    private SettingRow? SelectedRecorderSetting() {
        int index = recorderSelectedItem;
        return index >= 0 && index < CurrentRows.Count ? CurrentRows[index] : null;
    }

    private void RequestSelectedRecordingDelete() {
        if (!IsRecorderLibrary || recorderSelectedItem < 0) return;
        int fileIndex = recorderSelectedItem - CurrentRows.Count;
        if (fileIndex < 0 || fileIndex >= recordingFiles.Count) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        if (IsRecordingFinalizing(recordingFiles[fileIndex])) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        pendingRecordingDelete = recordingFiles[fileIndex];
        Audio.Play("event:/ui/main/button_select");
    }

    private void ConfirmRecordingDelete() {
        RecordingLibraryEntry? pending = pendingRecordingDelete;
        pendingRecordingDelete = null;
        if (pending is null) return;
        if (RecordingLibrary.DeleteRecording(pending.Value.Path, out string error)) {
            ShowRecordingNotice($"已删除 {pending.Value.FileName}");
            RefreshRecordingFiles();
            Audio.Play("event:/ui/main/button_toggle_off");
        } else {
            ShowRecordingNotice($"删除失败：{error}");
            Audio.Play("event:/ui/main/button_invalid");
        }
    }

    private void OpenRecordingFolder() {
        if (RecordingLibrary.OpenFolder(out string error)) {
            ShowRecordingNotice("已打开录制文件夹");
            Audio.Play("event:/ui/main/button_select");
        } else {
            ShowRecordingNotice($"无法打开文件夹：{error}");
            Audio.Play("event:/ui/main/button_invalid");
        }
    }

    private void OpenRecording(RecordingLibraryEntry file) {
        if (RecordingLibrary.OpenRecording(file.Path, out string error)) {
            ShowRecordingNotice($"正在打开 {file.FileName}");
            Audio.Play("event:/ui/main/button_select");
        } else {
            ShowRecordingNotice($"无法打开录像：{error}");
            RefreshRecordingFiles();
            Audio.Play("event:/ui/main/button_invalid");
        }
    }

    private void ToggleManualRecording() {
        bool wasActive = RecorderActive;
        if (wasActive) AutoRecorder.StopManual(level, save: true);
        else AutoRecorder.StartManual();
        ShowRecordingNotice(wasActive ? "已停止，正在保存录像" : "已开启手动录制");
        Audio.Play("event:/ui/main/button_toggle_on");
    }

    private void DiscardManualRecording() {
        AutoRecorder.StopManual(level, save: false);
        ShowRecordingNotice("已丢弃当前录制片段");
        Audio.Play("event:/ui/main/button_toggle_off");
    }

    private void SelectRecorderItem(int index, OverlayLayout layout) {
        int count = IsRecorderLibrary ? Math.Max(1, recordingFiles.Count) : CurrentRows.Count;
        recorderSelectedItem = Math.Clamp(index, -1, Math.Max(-1, count - 1));
        EnsureRecorderItemVisible(layout);
        Audio.Play("event:/ui/main/rollover_down");
    }

    private void SelectRecordingLibraryKind(int direction, OverlayLayout layout) {
        SelectRecordingLibraryKind(
            (RecordingLibraryKind)(((int)recordingLibraryKind + Math.Sign(direction) + 3) % 3),
            layout
        );
    }

    private void SelectRecordingLibraryKind(RecordingLibraryKind kind, OverlayLayout layout) {
        if (recordingLibraryKind == kind) return;
        bool fileSelected = recorderSelectedItem >= CurrentRows.Count;
        recordingLibraryKind = kind;
        RefreshRecordingFiles();
        if (fileSelected) recorderSelectedItem = CurrentRows.Count;
        rowScroll.EnsureVisible(
            0f,
            98f + RecorderFileHeight,
            RecorderViewport(layout).Height,
            MaxRowScroll(layout)
        );
        contentEase = 0f;
        Audio.Play("event:/ui/main/rollover_down");
    }

    private void UpdateMouse(OverlayLayout layout) {
        Vector2 mouse = MInput.Mouse.Position;
        if (MInput.Mouse.WheelDelta != 0 && layout.Body.Contains(mouse)) {
            float step = IsProfilerTab ? ProfilerScrollStep : 178f;
            rowScroll.Scroll(-Math.Sign(MInput.Mouse.WheelDelta) * step, MaxRowScroll(layout));
        }
        if (!MInput.Mouse.WasMoved && !MInput.Mouse.PressedLeftButton) return;

        for (int index = 0; index < tabs.Count; index++) {
            if (!layout.Tab(index, tabs.Count).Contains(mouse)) continue;
            if (MInput.Mouse.PressedLeftButton) SelectTab(index);
            return;
        }
        if (IsProfilerTab) {
            if (MInput.Mouse.PressedLeftButton) {
                if (ProfilerStartRect(layout).Contains(mouse)) StartProfilerSampling();
                else if (ProfilerSimpleModeRect(layout).Contains(mouse)) SetProfilerSimpleMode(true);
                else if (ProfilerProfessionalModeRect(layout).Contains(mouse)) SetProfilerSimpleMode(false);
            }
            return;
        }
        if (!layout.Rows.Contains(mouse)) return;
        for (int index = 0; index < CurrentRows.Count; index++) {
            MaterialRect rect = layout.Row(index, rowScroll.Offset);
            if (!rect.Contains(mouse)) continue;
            selectedRow = index;
            SettingRow row = CurrentRows[index];
            if (MInput.Mouse.PressedLeftButton) ActivateMouse(row, rect, mouse);
            return;
        }
    }

    private void UpdateDropdown(OverlayLayout layout) {
        SettingRow? row = dropdownRow;
        if (row?.Options is null || row.Options.Count == 0 || !CurrentRows.Contains(row)) {
            CloseDropdown();
            return;
        }
        if (Input.Pause.Pressed) {
            CloseDropdown(playSound: false);
            BeginClose(CloseDestination.Game);
            return;
        }
        if (Input.MenuCancel.Pressed || Input.ESC.Pressed || MInput.Keyboard.Pressed(Keys.Escape)) {
            CloseDropdown();
            return;
        }
        if (Input.MenuUp.Pressed) {
            MoveDropdownHighlight(-1);
            return;
        }
        if (Input.MenuDown.Pressed) {
            MoveDropdownHighlight(1);
            return;
        }
        if (Input.MenuConfirm.Pressed
            || MInput.Keyboard.Pressed(Keys.Enter)
            || MInput.Keyboard.Pressed(Keys.Space)) {
            CommitDropdown();
            return;
        }

        MaterialRect menu = DropdownRect(layout, row);
        if (MInput.Mouse.WheelDelta != 0 && menu.Contains(MInput.Mouse.Position)) {
            int visibleCount = DropdownVisibleCount(row);
            int maximum = Math.Max(0, row.Options.Count - visibleCount);
            dropdownFirstVisible = Math.Clamp(
                dropdownFirstVisible - Math.Sign(MInput.Mouse.WheelDelta), 0, maximum);
            dropdownHighlight = Math.Clamp(dropdownHighlight, dropdownFirstVisible,
                dropdownFirstVisible + visibleCount - 1);
        }
        if (MInput.Mouse.WasMoved || MInput.Mouse.PressedLeftButton) {
            int visibleCount = DropdownVisibleCount(row);
            for (int visibleIndex = 0; visibleIndex < visibleCount; visibleIndex++) {
                if (!DropdownItemRect(menu, visibleIndex).Contains(MInput.Mouse.Position)) continue;
                dropdownHighlight = dropdownFirstVisible + visibleIndex;
                if (MInput.Mouse.PressedLeftButton) CommitDropdown();
                return;
            }
        }
        if (MInput.Mouse.PressedLeftButton) CloseDropdown();
    }

    private void OpenDropdown(SettingRow row) {
        if (row.Options is null || row.Options.Count == 0 || row.SelectOption is null) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        dropdownRow = row;
        dropdownHighlight = Math.Clamp(row.SelectedOption?.Invoke() ?? 0, 0, row.Options.Count - 1);
        dropdownFirstVisible = 0;
        EnsureDropdownHighlightVisible();
        Audio.Play("event:/ui/main/button_select");
    }

    private void MoveDropdownHighlight(int direction) {
        SettingRow? row = dropdownRow;
        if (row?.Options is null || row.Options.Count == 0) return;
        dropdownHighlight = (dropdownHighlight + Math.Sign(direction) + row.Options.Count) % row.Options.Count;
        EnsureDropdownHighlightVisible();
        Audio.Play("event:/ui/main/rollover_down");
    }

    private void EnsureDropdownHighlightVisible() {
        SettingRow? row = dropdownRow;
        if (row?.Options is null) return;
        int visibleCount = DropdownVisibleCount(row);
        if (dropdownHighlight < dropdownFirstVisible) dropdownFirstVisible = dropdownHighlight;
        else if (dropdownHighlight >= dropdownFirstVisible + visibleCount)
            dropdownFirstVisible = dropdownHighlight - visibleCount + 1;
        dropdownFirstVisible = Math.Clamp(dropdownFirstVisible, 0,
            Math.Max(0, row.Options.Count - visibleCount));
    }

    private void CommitDropdown() {
        SettingRow? row = dropdownRow;
        if (row?.Options is null || row.SelectOption is null || row.Options.Count == 0) {
            CloseDropdown();
            return;
        }
        row.SelectOption(Math.Clamp(dropdownHighlight, 0, row.Options.Count - 1));
        row.Pulse = 1f;
        dropdownRow = null;
        Audio.Play("event:/ui/main/button_toggle_on");
    }

    private void CloseDropdown(bool playSound = true) {
        if (dropdownRow is null) return;
        dropdownRow = null;
        if (playSound) Audio.Play("event:/ui/main/button_back");
    }

    private void ActivateMouse(SettingRow row, MaterialRect rect, Vector2 mouse) {
        if (!row.Enabled() || row.Kind == SettingKind.Status) {
            if (!row.Enabled()) Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        if (row.Kind == SettingKind.Range) {
            if (RangeValueRect(rect).Contains(mouse)) {
                StartEdit(row);
                return;
            }
            draggedSlider = row;
            ApplySliderMouse(row, SliderRect(rect), mouse.X);
            Audio.Play("event:/ui/main/button_select");
            return;
        }
        if (row.Kind == SettingKind.Text) {
            StartEdit(row);
            return;
        }
        Activate(row);
    }

    private void UpdateSliderDrag(OverlayLayout layout) {
        SettingRow row = draggedSlider!;
        int index = CurrentRows.IndexOf(row);
        if (index < 0 || !MInput.Mouse.CheckLeftButton) {
            draggedSlider = null;
            return;
        }
        MaterialRect rect = IsRecorderTab
            ? RecorderSettingRect(layout, index)
            : layout.Row(index, rowScroll.Offset);
        ApplySliderMouse(row, SliderRect(rect), MInput.Mouse.Position.X);
        if (MInput.Mouse.ReleasedLeftButton) draggedSlider = null;
    }

    private static void ApplySliderMouse(SettingRow row, MaterialRect track, float mouseX) {
        float normalized = Math.Clamp((mouseX - track.X) / Math.Max(1f, track.Width), 0f, 1f);
        row.SetNormalized?.Invoke(normalized);
        row.Pulse = 1f;
    }

    private void UpdateTextEdit(OverlayLayout layout) {
        if (MaterialTextInputFocus.Pressed(Keys.Escape) || Input.MenuCancel.Pressed) {
            CancelEdit();
            return;
        }
        if (MaterialTextInputFocus.Pressed(Keys.Enter)) {
            CommitEdit();
            return;
        }
        if (MInput.Mouse.PressedLeftButton) {
            int index = CurrentRows.IndexOf(editingRow!);
            MaterialRect control = index < 0
                ? default
                : editingRow!.Kind == SettingKind.Range
                    ? RangeValueRect(IsRecorderTab
                        ? RecorderSettingRect(layout, index)
                        : layout.Row(index, rowScroll.Offset))
                    : WideControlRect(IsRecorderTab
                        ? RecorderSettingRect(layout, index)
                        : layout.Row(index, rowScroll.Offset));
            if (index < 0 || !control.Contains(MInput.Mouse.Position)) CommitEdit();
        }
    }

    private void StartEdit(SettingRow row) {
        if (row.EditValue is null || row.CommitEdit is null) return;
        editingRow = row;
        editBuffer = row.EditValue();
        imeText = "";
        editError = 0f;
        SubscribeTextInput();
        MaterialTextInputFocus.Focus(this);
        Audio.Play("event:/ui/main/button_select");
    }

    private void CommitEdit() {
        SettingRow? row = editingRow;
        if (row?.CommitEdit is null) {
            CancelEdit();
            return;
        }
        if (!row.CommitEdit(editBuffer)) {
            editError = 1f;
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        row.Pulse = 1f;
        editingRow = null;
        editBuffer = "";
        imeText = "";
        ReleaseFocusedInput();
        Audio.Play("event:/ui/main/button_select");
    }

    private void CancelEdit() {
        editingRow = null;
        editBuffer = "";
        imeText = "";
        editError = 0f;
        ReleaseFocusedInput();
        Audio.Play("event:/ui/main/button_back");
    }

    private void OnTextInput(char character) {
        if (editingRow is null) return;
        if (character == '\b') {
            if (editBuffer.Length > 0) editBuffer = editBuffer[..^1];
        } else if (!char.IsControl(character) && editBuffer.Length < editingRow.MaxInputLength) {
            if (editingRow.NumericInput && !char.IsDigit(character)) return;
            editBuffer += character;
        } else {
            return;
        }
        imeText = "";
        editError = 0f;
    }

    private void OnTextEditing(string? text, int start, int length) {
        _ = start;
        _ = length;
        if (editingRow is not null) imeText = text ?? "";
    }

    private void ActivateSelected() {
        if (CurrentRows.Count == 0) return;
        Activate(CurrentRows[Math.Clamp(selectedRow, 0, CurrentRows.Count - 1)]);
    }

    private void Activate(SettingRow row) {
        if (!row.Enabled()) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        switch (row.Kind) {
            case SettingKind.Toggle:
                row.Change?.Invoke(row.ToggleValue?.Invoke() == true ? -1 : 1);
                break;
            case SettingKind.Range:
            case SettingKind.Text:
                StartEdit(row);
                return;
            case SettingKind.Enum:
                OpenDropdown(row);
                return;
            case SettingKind.Action:
                row.Change?.Invoke(1);
                break;
            case SettingKind.Status:
                return;
        }
        row.Pulse = 1f;
        Audio.Play("event:/ui/main/button_toggle_on");
    }

    private void AdjustSelected(int direction) {
        if (CurrentRows.Count == 0) return;
        SettingRow row = CurrentRows[Math.Clamp(selectedRow, 0, CurrentRows.Count - 1)];
        if (!row.Enabled() || row.Change is null
            || row.Kind is SettingKind.Action or SettingKind.Status or SettingKind.Text) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        row.Change(direction);
        row.Pulse = 1f;
        Audio.Play("event:/ui/main/button_toggle_on");
    }

    private void SelectTab(int index) {
        int next = (index % tabs.Count + tabs.Count) % tabs.Count;
        if (next == selectedTab) return;
        selectedTab = next;
        selectedRow = 0;
        rowScroll.Reset();
        contentEase = 0f;
        draggedSlider = null;
        dropdownRow = null;
        if (IsRecorderTab) RefreshRecordingFiles();
        Audio.Play("event:/ui/main/rollover_down");
    }

    private void SelectRow(int index) {
        int count = CurrentRows.Count;
        if (count == 0) return;
        selectedRow = (index % count + count) % count;
        EnsureRowVisible();
        Audio.Play("event:/ui/main/rollover_down");
    }

    private void UpdateInteractions(OverlayLayout layout) {
        List<MaterialInteractionTarget> targets = [];

        if (pendingRecordingDelete is not null) {
            MaterialRect modal = RecordingDeleteModalRect();
            targets.Add(new MaterialInteractionTarget("settings.delete.cancel",
                RecordingDeleteCancelRect(modal)));
            targets.Add(new MaterialInteractionTarget("settings.delete.confirm",
                RecordingDeleteConfirmRect(modal)));
            motion.Update(targets);
            return;
        }

        if (dropdownRow is not null) {
            MaterialRect menu = DropdownRect(layout, dropdownRow);
            int count = DropdownVisibleCount(dropdownRow);
            for (int visibleIndex = 0; visibleIndex < count; visibleIndex++) {
                int optionIndex = dropdownFirstVisible + visibleIndex;
                targets.Add(new MaterialInteractionTarget(
                    $"settings.dropdown.{optionIndex}",
                    DropdownItemRect(menu, visibleIndex),
                    Focused: optionIndex == dropdownHighlight
                ));
            }
            motion.Update(targets);
            return;
        }

        for (int index = 0; index < tabs.Count; index++) {
            targets.Add(new MaterialInteractionTarget(
                $"settings.tab.{index}",
                layout.Tab(index, tabs.Count),
                Focused: index == selectedTab
            ));
        }

        if (IsRecorderTab) {
            MaterialRect hero = RecorderHeroRect(layout);
            bool active = AutoRecorder.IsRecording || AutoRecorder.ManualMode;
            targets.Add(new MaterialInteractionTarget("settings.recorder.folder", RecorderButtonRect(hero, 0)));
            targets.Add(new MaterialInteractionTarget("settings.recorder.toggle", RecorderButtonRect(hero, 1)));
            targets.Add(new MaterialInteractionTarget("settings.recorder.discard", RecorderButtonRect(hero, 2),
                Enabled: active));
            foreach (RecorderSettingsSection section in Enum.GetValues<RecorderSettingsSection>())
                targets.Add(new MaterialInteractionTarget($"settings.recorder.section.{section}",
                    RecorderSectionTabRect(layout, section), Focused: recorderSettingsSection == section));
            if (IsRecorderLibrary) {
                targets.Add(new MaterialInteractionTarget("settings.recorder.library.deaths",
                    RecorderLibraryTabRect(layout, 0), Focused: recordingLibraryKind == RecordingLibraryKind.DeathReplay));
                targets.Add(new MaterialInteractionTarget("settings.recorder.library.full",
                    RecorderLibraryTabRect(layout, 1), Focused: recordingLibraryKind == RecordingLibraryKind.Full));
                targets.Add(new MaterialInteractionTarget("settings.recorder.library.auto",
                    RecorderLibraryTabRect(layout, 2), Focused: recordingLibraryKind == RecordingLibraryKind.Automatic));
            }

            for (int index = 0; index < CurrentRows.Count; index++) {
                MaterialRect rect = RecorderSettingRect(layout, index);
                if (rect.Bottom < layout.Rows.Y || rect.Y > layout.Rows.Bottom) continue;
                targets.Add(new MaterialInteractionTarget($"settings.row.{selectedTab}.{index}", rect,
                    CurrentRows[index].Enabled(), recorderSelectedItem == index));
            }
            for (int index = 0; IsRecorderLibrary && index < recordingFiles.Count; index++) {
                RecordingLibraryEntry file = recordingFiles[index];
                bool enabled = !IsRecordingFinalizing(file);
                MaterialRect rect = RecorderFileRect(layout, index);
                if (rect.Bottom < layout.Rows.Y || rect.Y > layout.Rows.Bottom) continue;
                string key = $"settings.recorder.file.{file.Path}";
                targets.Add(new MaterialInteractionTarget(key, rect, Enabled: enabled,
                    Focused: recorderSelectedItem == CurrentRows.Count + index));
                targets.Add(new MaterialInteractionTarget(key + ".open", RecorderFileOpenRect(rect), Enabled: enabled));
                targets.Add(new MaterialInteractionTarget(key + ".delete", RecorderFileDeleteRect(rect), Enabled: enabled));
            }
        } else if (IsProfilerTab) {
            targets.Add(new MaterialInteractionTarget("settings.profiler.start", ProfilerStartRect(layout),
                Enabled: !ManagedCpuSampler.IsBusy));
            bool simple = MicroblocksQolUtilsModule.Settings.ProfilerSimpleMode;
            targets.Add(new MaterialInteractionTarget("settings.profiler.simple", ProfilerSimpleModeRect(layout),
                Focused: simple));
            targets.Add(new MaterialInteractionTarget("settings.profiler.professional",
                ProfilerProfessionalModeRect(layout), Focused: !simple));
        } else {
            for (int index = 0; index < CurrentRows.Count; index++) {
                MaterialRect rect = layout.Row(index, rowScroll.Offset);
                if (rect.Bottom < layout.Rows.Y || rect.Y > layout.Rows.Bottom) continue;
                targets.Add(new MaterialInteractionTarget($"settings.row.{selectedTab}.{index}", rect,
                    CurrentRows[index].Enabled(), index == selectedRow));
            }
        }

        motion.Update(targets);
    }

    private void UpdateRowAnimations(OverlayLayout layout) {
        Vector2 mouse = MInput.Mouse.Position;
        for (int tabIndex = 0; tabIndex < tabs.Count; tabIndex++) {
            List<SettingRow> animatedRows = tabIndex == selectedTab ? CurrentRows : tabs[tabIndex].Rows;
            for (int index = 0; index < animatedRows.Count; index++) {
                SettingRow row = animatedRows[index];
                bool current = tabIndex == selectedTab;
                bool recorder = current && tabs[tabIndex].RecorderPage;
                bool selected = current && (recorder
                    ? recorderSelectedItem == index
                    : index == selectedRow);
                bool hovered = current && layout.Rows.Contains(mouse) && (recorder
                    ? RecorderSettingRect(layout, index).Contains(mouse)
                    : layout.Row(index, rowScroll.Offset).Contains(mouse));
                row.FocusAnimation = Calc.Approach(row.FocusAnimation, selected ? 1f : 0f,
                    Engine.RawDeltaTime * 8f);
                row.HoverAnimation = Calc.Approach(row.HoverAnimation, hovered ? 1f : 0f,
                    Engine.RawDeltaTime * 10f);
                if (row.ToggleValue is not null) {
                    row.ToggleAnimation = Calc.Approach(row.ToggleAnimation,
                        row.ToggleValue() ? 1f : 0f, Engine.RawDeltaTime * 10f);
                }
                if (row.Normalized is not null) {
                    row.SliderAnimation = Calc.Approach(row.SliderAnimation,
                        row.Normalized(), Engine.RawDeltaTime * 8f);
                }
                row.Pulse = Calc.Approach(row.Pulse, 0f, Engine.RawDeltaTime * 3.5f);
            }
        }
    }

    private void EnsureRowVisible() {
        OverlayLayout layout = OverlayLayout.Create(1f - Ease.CubeOut(ease));
        int band = selectedRow / Columns;
        float top = band * (RowHeight + RowGap);
        rowScroll.EnsureVisible(top, top + RowHeight, layout.Rows.Height, MaxRowScroll(layout));
    }

    private float MaxRowScroll(OverlayLayout layout) {
        if (IsRecorderTab) return Math.Max(0f, RecorderContentHeight(layout) - RecorderViewport(layout).Height);
        if (IsProfilerTab) return ProfilerMaxScroll(layout);
        int bands = (CurrentRows.Count + Columns - 1) / Columns;
        float contentHeight = bands == 0 ? 0f : bands * RowHeight + (bands - 1) * RowGap;
        return Math.Max(0f, contentHeight - layout.Rows.Height);
    }

    private void OpenBindingConfig(bool controller) {
        if (bindingConfig is not null) return;

        void OnClose() {
            bindingConfig = null;
            Visible = true;
            inputDelay = 0.16f;
            MicroblocksQolUtilsModule.Instance.SaveSettings();
        }

        bindingConfig = controller
            ? new ModuleSettingsButtonConfigUI(MicroblocksQolUtilsModule.Instance) { OnClose = OnClose }
            : new ModuleSettingsKeyboardConfigUI(MicroblocksQolUtilsModule.Instance) { OnClose = OnClose };
        Visible = false;
        level.Add(bindingConfig);
        level.OnEndOfFrame += () => level.Entities.UpdateLists();
    }

    private void BeginClose(CloseDestination destination) {
        if (closeDestination != CloseDestination.None) return;
        if (editingRow is not null) editingRow.CommitEdit!(editBuffer);
        editingRow = null;
        closeDestination = destination;
        draggedSlider = null;
        dropdownRow = null;
        ReleaseFocusedInput();
        Audio.Play("event:/ui/main/button_back");
    }

    private void SubscribeTextInput() {
        if (textInputSubscribed) return;
        textInputSubscribed = true;
        TextInput.OnInput += OnTextInput;
        TextInputEXT.TextEditing += OnTextEditing;
    }

    private void ReleaseFocusedInput() {
        if (textInputSubscribed) {
            textInputSubscribed = false;
            TextInput.OnInput -= OnTextInput;
            TextInputEXT.TextEditing -= OnTextEditing;
        }
        MaterialTextInputFocus.Blur(this);
    }

    private void FinishClose() {
        CloseDestination destination = closeDestination;
        closeDestination = CloseDestination.None;
        level.AllowHudHide = oldAllowHudHide;
        RemoveSelf();
        if (destination == CloseDestination.PauseMenu) level.Pause(returnIndex, minimal);
        else level.Paused = false;
        MicroblocksQolUtilsModule.Instance.SaveSettings();
    }

    private List<SettingRow> CurrentRows {
        get {
            if (!IsRecorderTab) return tabs[selectedTab].Rows;
            AutoRecordingMode mode = MicroblocksQolUtilsModule.Settings.AutomaticRecording;
            if (visibleRecorderRows is null || visibleRecorderSection != recorderSettingsSection || visibleAutoMode != mode) {
                visibleRecorderRows = tabs[selectedTab].Rows.Where(row => row.IsVisible?.Invoke() != false).ToList();
                visibleRecorderSection = recorderSettingsSection;
                visibleAutoMode = mode;
            }
            return visibleRecorderRows;
        }
    }


    private bool IsProfilerTab => tabs[selectedTab].ProfilerPage;

    private List<SettingsTab> BuildTabs() {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        return [
            new SettingsTab("HUD", "dashboard", [
                Toggle("启用 QOL 工具", () => settings.Enabled, value => settings.Enabled = value),
                Toggle("HUD 信息卡阴影、背景与边框", () => settings.HudMaterialSurfaces,
                    value => settings.HudMaterialSurfaces = value),
                Toggle("显示帧率", () => settings.ShowFps, value => settings.ShowFps = value),
                Toggle("显示 CPU 帧耗时", () => settings.ShowFrameTime, value => settings.ShowFrameTime = value),
                Toggle("物理与渲染帧率", () => settings.ShowPhysicalAndRenderFps,
                    value => settings.ShowPhysicalAndRenderFps = value),
                Toggle("显示帧率分析", () => settings.EnableFrameProfiler,
                    value => settings.EnableFrameProfiler = value),
                Range("卡顿采样阈值", () => settings.FrameSpikeThresholdMs,
                    value => settings.FrameSpikeThresholdMs = value, 20, 250, 5, value => $"{value} ms"),
                Toggle("显示还剩多少面", () => settings.ShowRoomsRemaining, value => settings.ShowRoomsRemaining = value),
                Toggle("显示地图人数", () => settings.ShowMapPlayerCount, value => settings.ShowMapPlayerCount = value),
                Toggle("显示当前时间", () => settings.ShowClock, value => settings.ShowClock = value)
            ]),
            new SettingsTab("Profiler", "speed", [], ProfilerPage: true),
            new SettingsTab("小地图", "map", [
                Toggle("启用小地图", () => settings.MiniMapEnabled, value => settings.MiniMapEnabled = value),
                EnumRow("裁剪形状", () => settings.MiniMapShape, value => settings.MiniMapShape = value),
                Range("地图尺寸", () => settings.MiniMapSize, value => settings.MiniMapSize = value,
                    96, 384, 16, value => $"{value} px"),
                Range("缩放档位", () => settings.MiniMapZoom, value => settings.MiniMapZoom = value,
                    0, 12, 1, value => value == 0 ? "当前房间" : value.ToString()),
                Action("键盘快捷键", "使用 Everest 设置", () => OpenBindingConfig(controller: false)),
                Action("手柄按键", "使用 Everest 设置", () => OpenBindingConfig(controller: true)),
                Toggle("显示背景", () => settings.MiniMapBackground, value => settings.MiniMapBackground = value),
                Range("背景不透明度", () => settings.MiniMapBackgroundOpacity,
                    value => settings.MiniMapBackgroundOpacity = value, 0, 10, 1, value => $"{value * 10}%"),
                Toggle("显示地图边框", () => settings.MiniMapBorder, value => settings.MiniMapBorder = value),
                Toggle("显示房间背景", () => settings.MiniMapRoomBackgrounds,
                    value => settings.MiniMapRoomBackgrounds = value),
                Range("房间背景不透明度", () => settings.MiniMapRoomBackgroundOpacity,
                    value => settings.MiniMapRoomBackgroundOpacity = value, 0, 10, 1, value => $"{value * 10}%"),
                Toggle("显示房间边缘线", () => settings.MiniMapRoomBounds, value => settings.MiniMapRoomBounds = value),
                Toggle("高亮正路房间", () => settings.MiniMapHighlightRoute,
                    value => settings.MiniMapHighlightRoute = value),
                Toggle("标注收集品", () => settings.MiniMapCollectibles,
                    value => settings.MiniMapCollectibles = value),
                Toggle("边框显示附近房间草莓", () => settings.MiniMapShowNearbyRoomStrawberries,
                    value => settings.MiniMapShowNearbyRoomStrawberries = value),
                Toggle("自适应地图颜色", () => settings.MiniMapAdaptiveColors,
                    value => settings.MiniMapAdaptiveColors = value),
                Toggle("显示 MiaoNet 玩家", () => settings.ShowMiaoNetPlayers,
                    value => settings.ShowMiaoNetPlayers = value),
                Toggle("边框显示越界玩家", () => settings.MiniMapShowOffscreenPlayers,
                    value => settings.MiniMapShowOffscreenPlayers = value),
                EnumRow("头像裁剪形状", () => settings.MiniMapAvatarShape,
                    value => settings.MiniMapAvatarShape = value),
                EnumRow("玩家名字", () => settings.MiniMapNames, value => settings.MiniMapNames = value),
                Toggle("隐藏原生越界名字", () => settings.HideMiaoNetOffscreenNames,
                    value => settings.HideMiaoNetOffscreenNames = value)
            ]),
            new SettingsTab("录制", "videocam", BuildRecorderRows(settings), RecorderPage: true),
            new SettingsTab("界面与系统", "settings", [
                Toggle("亚克力模糊背景", () => settings.MaterialAcrylicBackground,
                    value => settings.MaterialAcrylicBackground = value),
                Range("模糊强度", () => settings.MaterialAcrylicBlurStrength,
                    value => settings.MaterialAcrylicBlurStrength = value, 1, 12, 1, value => value.ToString()),
                Choice("界面字体", UiFontCatalog.InstalledFamilies, () => settings.FontFamily, value => {
                    settings.FontFamily = value;
                    settings.FontFile = "";
                }),
                Range("字体缩放", () => settings.FontScalePercent, value => settings.FontScalePercent = value,
                    80, 160, 5, value => $"{value}%"),
                Toggle("取代原版选关页", () => settings.ReplaceChapterSelect,
                    value => settings.ReplaceChapterSelect = value),
                Toggle("取代 Everest 模组设置页", () => settings.ReplaceEverestModOptions,
                    value => settings.ReplaceEverestModOptions = value),
                Toggle("选关页显示 Collab 地图", () => settings.ChapterSelectShowCollabMaps,
                    value => settings.ChapterSelectShowCollabMaps = value),
                ..WindowsSystemRows(settings),
                Toggle("完全移除场景过渡", () => settings.RemoveRoomTransitions,
                    value => settings.RemoveRoomTransitions = value),
                Toggle("完全移除死亡动画", () => settings.RemoveDeathAnimation,
                    value => settings.RemoveDeathAnimation = value),
                EnumRow("碰撞箱", () => settings.CollisionBoxes, value => settings.CollisionBoxes = value),
                Toggle("关心玩家过面通知", () => settings.WatchedPlayerNotifications,
                    value => settings.WatchedPlayerNotifications = value)
            ])
        ];
    }

    private List<SettingRow> BuildRecorderRows(QolSettings settings) {
        List<SettingRow> rows = [];
        void Group(RecorderSettingsSection section, params SettingRow[] items) {
            foreach (SettingRow row in items) {
                Func<bool>? condition = row.IsVisible;
                row.IsVisible = () => recorderSettingsSection == section && (condition?.Invoke() ?? true);
                rows.Add(row);
            }
        }
        SettingRow Golden(SettingRow row) {
            row.IsVisible = () => settings.AutomaticRecording == AutoRecordingMode.Golden;
            return row;
        }
        Group(RecorderSettingsSection.Automatic,
            EnumRow("自动录制", () => settings.AutomaticRecording, value => settings.AutomaticRecording = value),
            Golden(EnumRow("成功时录到", () => settings.GoldenRecordingEnd, value => settings.GoldenRecordingEnd = value)),
            Golden(EnumRow("死亡后", () => settings.GoldenRecordingDeath, value => settings.GoldenRecordingDeath = value)));
        Group(RecorderSettingsSection.DeathReplay,
            Toggle("保存死亡回放", () => settings.DeathReplayEnabled, value => settings.DeathReplayEnabled = value),
            Range("死亡回放时长", () => settings.DeathReplayBufferSeconds, value => settings.DeathReplayBufferSeconds = value,
                10, 60, 5, value => $"最近 {value} 秒"));
        Group(RecorderSettingsSection.Quality,
            Toggle("切面自动存档（保持视频连续）", () => settings.RecordingAutoSaveOnTransition,
                value => settings.RecordingAutoSaveOnTransition = value),
            EnumRow("BGM 拼接", () => settings.BgmMode, value => settings.BgmMode = value),
            Toggle("录制 UI 音效", () => settings.RecordingIncludeUiSfx, value => settings.RecordingIncludeUiSfx = value),
            Toggle("剪辑冻结帧", () => settings.RecordingRemoveFreezeFrames, value => settings.RecordingRemoveFreezeFrames = value, highlightWhenOn: true),
            Range("录制帧率", () => settings.RecordingFrameRate, value => settings.RecordingFrameRate = value,
                30, 120, 30, value => $"{value} FPS"),
            Range("录制码率", () => settings.RecordingBitrateKbps, value => settings.RecordingBitrateKbps = value,
                2000, 50000, 1000, value => $"{value / 1000f:0.#} Mbps"),
            Text("编码器", () => settings.RecordingEncoder, value => settings.RecordingEncoder = value, "auto / nvenc / qsv / amf…", 48));
        Group(RecorderSettingsSection.Storage,
            Range("最多保留手动录像", () => settings.RecordingRetentionCount, value => settings.RecordingRetentionCount = value,
                0, 500, 10, value => value == 0 ? "不限" : $"{value} 个"),
            Range("最多保留自动录像", () => settings.AutoRecordingRetentionCount, value => settings.AutoRecordingRetentionCount = value,
                0, 500, 10, value => value == 0 ? "不限" : $"{value} 个"),
            Range("最多保留死亡回放", () => settings.DeathReplayRetentionCount, value => settings.DeathReplayRetentionCount = value,
                0, 200, 5, value => value == 0 ? "不限" : $"{value} 个"),
            Action("立即清理旧录像", "清理", AutoRecorder.CleanupRecordings,
                () => (settings.RecordingRetentionCount > 0 || settings.AutoRecordingRetentionCount > 0 || settings.DeathReplayRetentionCount > 0)
                    && !AutoRecorder.IsCleaning),
            Text("输出根目录", () => settings.RecordingDirectory, value => settings.RecordingDirectory = value, "留空使用默认目录", 240));
        Group(RecorderSettingsSection.Controls,
            Action("键盘快捷键", "使用 Everest 设置", () => OpenBindingConfig(controller: false)),
            Action("手柄按键", "使用 Everest 设置", () => OpenBindingConfig(controller: true)),
            Toggle("显示录制红点", () => settings.ShowRecordingIndicator, value => settings.ShowRecordingIndicator = value),
            Toggle("显示录制时长", () => settings.ShowRecordingDuration, value => settings.ShowRecordingDuration = value));
        return rows;
    }

    private static IEnumerable<SettingRow> WindowsSystemRows(QolSettings settings) {
        if (!OperatingSystem.IsWindows()) yield break;

        yield return Toggle("输入框中文、游戏内英文输入法", () => settings.AutoSwitchInputLanguage,
            value => settings.AutoSwitchInputLanguage = value);
    }

    private static SettingRow Toggle(string label, Func<bool> get, Action<bool> set,
        bool highlightWhenOn = false) => new(
        label,
        SettingKind.Toggle,
        () => get() ? "开" : "关",
        direction => set(direction > 0),
        toggleValue: get,
        highlightWhenOn: highlightWhenOn
    );

    private static SettingRow Range(
        string label,
        Func<int> get,
        Action<int> set,
        int min,
        int max,
        int step,
        Func<int, string> format
    ) => new(
        label,
        SettingKind.Range,
        () => format(get()),
        direction => set(Math.Clamp(get() + Math.Sign(direction) * step, min, max)),
        normalized: () => (get() - min) / (float)Math.Max(1, max - min),
        setNormalized: normalized => {
            int raw = min + (int)MathF.Round((max - min) * normalized / step) * step;
            set(Math.Clamp(raw, min, max));
        },
        editValue: () => get().ToString(),
        commitEdit: text => {
            if (!int.TryParse(text, out int value) || value < min || value > max) return false;
            int snapped = min + (int)MathF.Round((value - min) / (float)step) * step;
            set(Math.Clamp(snapped, min, max));
            return true;
        },
        numericInput: true,
        maxInputLength: 10
    );

    private static SettingRow EnumRow<T>(string label, Func<T> get, Action<T> set) where T : struct, Enum {
        T[] values = Enum.GetValues<T>();
        return new SettingRow(label, SettingKind.Enum, () => FormatEnum(get()), direction => {
            int index = Array.IndexOf(values, get());
            index = (index + Math.Sign(direction) + values.Length) % values.Length;
            set(values[index]);
        }, options: values.Select(value => FormatEnum(value)).ToArray(),
            selectedOption: () => Array.IndexOf(values, get()),
            selectOption: index => set(values[Math.Clamp(index, 0, values.Length - 1)]));
    }

    private static SettingRow Text(
        string label,
        Func<string> get,
        Action<string> set,
        string placeholder,
        int maxLength
    ) => new(
        label,
        SettingKind.Text,
        get,
        editValue: get,
        commitEdit: value => {
            set(value.Trim());
            return true;
        },
        placeholder: placeholder,
        maxInputLength: maxLength
    );

    private static SettingRow Choice(
        string label,
        IReadOnlyList<string> values,
        Func<string> get,
        Action<string> set
    ) {
        string[] options = values.ToArray();

        int SelectedIndex() {
            int index = -1;
            for (int candidate = 0; candidate < options.Length; candidate++) {
                if (!string.Equals(options[candidate], get(), StringComparison.OrdinalIgnoreCase)) continue;
                index = candidate;
                break;
            }
            return index;
        }

        return new SettingRow(label, SettingKind.Enum, get, direction => {
            if (options.Length == 0) return;
            int index = (SelectedIndex() + Math.Sign(direction) + options.Length) % options.Length;
            set(options[index]);
        }, options: options, selectedOption: SelectedIndex,
            selectOption: index => set(options[Math.Clamp(index, 0, options.Length - 1)]));
    }

    private static SettingRow Action(string label, string buttonText, System.Action action,
        Func<bool>? enabled = null) => new(
        label,
        SettingKind.Action,
        () => buttonText,
        _ => action(),
        isEnabled: enabled
    );

    private static SettingRow Status(string label, Func<string> value) => new(
        label,
        SettingKind.Status,
        value
    );

    private static string RecordingStatus() {
        if (AutoRecorder.IsRecording) {
            if (AutoRecorder.ManualMode) return "手动录制中";
            return "自动录制中";
        }
        if (AutoRecorder.IsFinalizing) return "正在生成视频";
        if (AutoRecorder.ManualMode) return "已开启，等待游戏画面";
        return AutoRecorder.AutoRecordingSuppressed ? "本次录制已停止" : "空闲";
    }

    private static string ShortPath(string path) => string.IsNullOrWhiteSpace(path)
        ? "—"
        : Path.GetFileName(path);

    private void StartProfilerSampling() {
        if (!ManagedCpuSampler.Start(10d)) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        Audio.Play("event:/ui/main/button_select");
        BeginClose(CloseDestination.Game);
    }

    private void RenderProfilerContent(OverlayLayout layout, MaterialPalette palette) {
        float alpha = ease * contentEase;
        RenderContentHeading("Profiler", "speed", layout, palette, alpha);
        ManagedSamplingStage stage = ManagedCpuSampler.Stage;
        ManagedProfileReport? report = ManagedCpuSampler.LatestReport;
        string status = stage switch {
            ManagedSamplingStage.WarmingUp => "准备返回游戏…",
            ManagedSamplingStage.Sampling => $"采样中 · 剩余 {ManagedCpuSampler.RemainingSeconds:0.0} 秒",
            ManagedSamplingStage.Analyzing => "正在解析托管调用栈…",
            ManagedSamplingStage.Complete => "报告已生成",
            ManagedSamplingStage.Failed => "采样失败",
            _ => report is null ? "尚未采样" : "上次报告"
        };
        MaterialUiKit.Text(status, new Vector2(layout.Rows.Right, layout.ContentHeader.Y + 8f),
            new Vector2(1f, 0f), MaterialTextRole.Caption,
            stage == ManagedSamplingStage.Failed ? Color.OrangeRed : palette.Primary,
            alpha, scaleOverride: 0.29f);

        MaterialRect summary = ProfilerSummaryRect(layout);
        MaterialUi.RoundedRect(summary.X, summary.Y, summary.Width, summary.Height, 25f,
            palette.SurfaceHigh * (0.82f * alpha));
        MaterialUi.RoundedOutline(summary.X, summary.Y, summary.Width, summary.Height, 25f, 1f,
            palette.Outline * (0.42f * alpha));

        string headline;
        string? detail = null;
        if (stage == ManagedSamplingStage.Failed) {
            headline = "无法启动 .NET 采样器";
            detail = Trim(ManagedCpuSampler.Failure, 96);
        } else if (stage is ManagedSamplingStage.WarmingUp or ManagedSamplingStage.Sampling) {
            headline = "正在记录主线程 Update / Render 调用栈";
        } else if (stage == ManagedSamplingStage.Analyzing) {
            headline = "采样完成，正在生成报告";
        } else if (report is not null) {
            headline = $"{report.DurationSeconds:0} 秒 · {report.StackSamples} 个主循环样本 · 最慢帧 {report.MaximumFrameMilliseconds:0.0} ms";
            detail = $"平均帧 {report.AverageFrameMilliseconds:0.0} ms · Update 峰值 {report.MaximumUpdateMilliseconds:0.0} ms · Render 峰值 {report.MaximumRenderMilliseconds:0.0} ms";
        } else {
            headline = "尚未生成报告";
        }
        MaterialRect startButtonBounds = ProfilerStartRect(layout);
        float summaryTextWidth = Math.Max(0f, startButtonBounds.X - summary.X - 48f);
        string visibleHeadline = MaterialTextUtil.Ellipsize(
            headline, summaryTextWidth, 0.34f, UiFontWeight.Bold);
        MaterialUiKit.Text(visibleHeadline, new Vector2(summary.X + 24f, summary.Y + 24f), Vector2.Zero,
            MaterialTextRole.Label, palette.OnSurface, alpha, scaleOverride: 0.34f);
        if (detail is not null) {
            string visibleDetail = MaterialTextUtil.Ellipsize(detail, summaryTextWidth, 0.27f);
            MaterialUiKit.Text(visibleDetail, new Vector2(summary.X + 24f, summary.Y + 62f), Vector2.Zero,
                MaterialTextRole.Caption, palette.OnSurfaceVariant, alpha, scaleOverride: 0.27f);
        }

        MaterialRect button = ProfilerStartRect(layout);
        bool busy = ManagedCpuSampler.IsBusy;
        float buttonEmphasis = motion.Emphasis("settings.profiler.start");
        MaterialUi.RoundedRect(button.X, button.Y, button.Width, button.Height, 18f,
            (busy ? palette.Outline : palette.Primary)
                * (alpha * (busy ? 0.26f : MathHelper.Lerp(0.88f, 1f, buttonEmphasis))));
        motion.RenderStateLayer("settings.profiler.start", button, 18f, palette.OnPrimary, alpha);
        MaterialUiKit.Text(busy ? "正在采样" : "开始 10 秒采样", button.Center,
            new Vector2(0.5f), MaterialTextRole.Label,
            busy ? palette.OnSurfaceVariant : palette.OnPrimary, alpha, scaleOverride: 0.29f);

        if (busy) {
            float progress = ManagedCpuSampler.Progress;
            MaterialUi.RoundedRect(summary.X + 24f, summary.Bottom - 16f, summary.Width - 48f, 5f, 2.5f,
                palette.Outline * (0.32f * alpha));
            MaterialUi.RoundedRect(summary.X + 24f, summary.Bottom - 16f,
                (summary.Width - 48f) * progress, 5f, 2.5f, palette.Primary * alpha);
        }

        bool simpleMode = MicroblocksQolUtilsModule.Settings.ProfilerSimpleMode;
        MaterialRect mode = ProfilerModeRect(layout);
        MaterialUi.RoundedRect(mode.X, mode.Y, mode.Width, mode.Height, 16f,
            palette.SurfaceHigh * (0.72f * alpha));
        MaterialRect simpleRect = ProfilerSimpleModeRect(layout);
        MaterialRect professionalRect = ProfilerProfessionalModeRect(layout);
        MaterialRect selectedMode = simpleMode ? simpleRect : professionalRect;
        MaterialUi.RoundedRect(selectedMode.X, selectedMode.Y, selectedMode.Width, selectedMode.Height, 14f,
            palette.Primary * (0.88f * alpha));
        motion.RenderStateLayer("settings.profiler.simple", simpleRect, 14f,
            simpleMode ? palette.OnPrimary : palette.Primary, alpha);
        motion.RenderStateLayer("settings.profiler.professional", professionalRect, 14f,
            simpleMode ? palette.Primary : palette.OnPrimary, alpha);
        MaterialUiKit.Text("简单 · 仅 Mod", simpleRect.Center,
            new Vector2(0.5f), MaterialTextRole.Label,
            simpleMode ? palette.OnPrimary : palette.OnSurfaceVariant, alpha, scaleOverride: 0.25f);
        MaterialUiKit.Text("专业 · 全部", professionalRect.Center,
            new Vector2(0.5f), MaterialTextRole.Label,
            simpleMode ? palette.OnSurfaceVariant : palette.OnPrimary, alpha, scaleOverride: 0.25f);

        if (report is null) return;

        float traceWidth = Math.Max(0f, layout.Rows.Right - mode.Right - 32f);
        string trace = MaterialTextUtil.Ellipsize(
            $"原始 trace · {Path.GetFileName(report.TracePath)}", traceWidth, 0.22f);
        MaterialUiKit.Text(trace, new Vector2(layout.Rows.Right, mode.Y + 11f),
            new Vector2(1f, 0f), MaterialTextRole.Caption,
            palette.OnSurfaceVariant * 0.72f, alpha, scaleOverride: 0.22f);

        float gap = 16f;
        MaterialRect reportViewport = ProfilerReportViewport(layout);
        float columnWidth = (layout.Rows.Width - gap) / 2f;
        MaterialRect updateColumn = new(layout.Rows.X, reportViewport.Y, columnWidth, reportViewport.Height);
        MaterialRect renderColumn = new(updateColumn.Right + gap, reportViewport.Y, columnWidth,
            reportViewport.Height);
        RenderProfileColumnHeader(updateColumn, "UPDATE", report.UpdateCpuMilliseconds, palette, alpha);
        RenderProfileColumnHeader(renderColumn, "RENDER", report.RenderCpuMilliseconds, palette, alpha);

        MaterialRect listViewport = ProfilerListViewport(layout);
        rowViewport.Render(listViewport, () => {
            RenderProfileRows(updateColumn, listViewport, report.Update, simpleMode, palette, alpha, rowScroll.Offset);
            RenderProfileRows(renderColumn, listViewport, report.Render, simpleMode, palette, alpha, rowScroll.Offset);
        });

        float maximum = ProfilerMaxScroll(layout);
        if (maximum > 0f) RenderProfilerScrollbar(listViewport, maximum, palette, alpha);
    }

    private static void RenderProfileColumnHeader(
        MaterialRect bounds,
        string title,
        double totalMilliseconds,
        MaterialPalette palette,
        float alpha
    ) {
        MaterialUiKit.Text(title, new Vector2(bounds.X + 4f, bounds.Y + 2f), Vector2.Zero,
            MaterialTextRole.Label, palette.Primary, alpha, scaleOverride: 0.30f);
        MaterialUiKit.Text($"独占 CPU · {totalMilliseconds:0} ms",
            new Vector2(bounds.Right - 4f, bounds.Y + 4f),
            new Vector2(1f, 0f), MaterialTextRole.Caption, palette.OnSurfaceVariant,
            alpha, scaleOverride: 0.23f);
    }

    private static void RenderProfileRows(
        MaterialRect column,
        MaterialRect viewport,
        IReadOnlyList<ManagedProfileEntry> entries,
        bool simpleMode,
        MaterialPalette palette,
        float alpha,
        float scrollOffset
    ) {
        int visibleIndex = 0;
        foreach (ManagedProfileEntry entry in entries) {
            if (simpleMode && !entry.IsMod) continue;
            MaterialRect row = new(column.X,
                viewport.Y + visibleIndex * (ProfilerRowHeight + ProfilerRowGap) - scrollOffset,
                column.Width, ProfilerRowHeight);
            visibleIndex++;
            if (row.Bottom < viewport.Y || row.Y > viewport.Bottom) continue;
            MaterialUi.RoundedRect(row.X, row.Y, row.Width, row.Height, 16f,
                palette.SurfaceHigh * (0.68f * alpha));
            float bar = Math.Clamp((float)entry.Percent / 100f, 0f, 1f);
            MaterialUi.RoundedRect(row.X, row.Bottom - 4f, row.Width * bar, 4f, 2f,
                palette.Primary * (0.82f * alpha));
            string owner = entry.HookTarget is null ? entry.Owner : entry.Owner + "  ·  HOOK";
            MaterialUiKit.Text(MaterialTextUtil.Ellipsize(owner, row.Width - 88f, 0.22f),
                new Vector2(row.X + 12f, row.Y + 9f), Vector2.Zero,
                MaterialTextRole.Caption, entry.HookTarget is null ? palette.OnSurfaceVariant : Color.Orange,
                alpha, scaleOverride: 0.22f);
            MaterialUiKit.Text(MaterialTextUtil.Ellipsize(
                    entry.Method, row.Width - 24f, 0.24f, UiFontWeight.Bold),
                new Vector2(row.X + 12f, row.Y + 34f), Vector2.Zero,
                MaterialTextRole.Label, palette.OnSurface, alpha, scaleOverride: 0.24f);
            MaterialUiKit.Text($"{entry.Percent:0.0}%", new Vector2(row.Right - 12f, row.Y + 9f),
                new Vector2(1f, 0f), MaterialTextRole.Caption, palette.Primary,
                alpha, scaleOverride: 0.22f);
            if (entry.HookTarget is not null) {
                MaterialUiKit.Text(MaterialTextUtil.Ellipsize(
                        "→ " + entry.HookTarget, row.Width - 24f, 0.18f),
                    new Vector2(row.X + 12f, row.Bottom - 21f),
                    Vector2.Zero, MaterialTextRole.Caption, palette.OnSurfaceVariant,
                    alpha, scaleOverride: 0.18f);
            }
        }
        if (visibleIndex == 0) {
            string empty = simpleMode ? "没有捕获到 Mod 的主线程样本" : "没有捕获到该阶段的主线程样本";
            MaterialUiKit.Text(empty, new Vector2(column.X + 4f, viewport.Y + 14f),
                Vector2.Zero, MaterialTextRole.Caption, palette.OnSurfaceVariant,
                alpha, scaleOverride: 0.25f);
        }
    }

    private void RenderProfilerScrollbar(
        MaterialRect viewport,
        float maximum,
        MaterialPalette palette,
        float alpha
    ) {
        float contentHeight = viewport.Height + maximum;
        float thumbHeight = Math.Max(48f, viewport.Height * viewport.Height / contentHeight);
        float travel = viewport.Height - thumbHeight;
        float y = viewport.Y + rowScroll.Offset / maximum * travel;
        MaterialUi.RoundedRect(viewport.Right + 8f, viewport.Y, 5f, viewport.Height, 2.5f,
            palette.Outline * (0.18f * alpha));
        MaterialUi.RoundedRect(viewport.Right + 8f, y, 5f, thumbHeight, 2.5f,
            palette.Primary * (0.68f * alpha));
    }

    private static MaterialRect ProfilerSummaryRect(OverlayLayout layout) =>
        new(layout.Rows.X, layout.Rows.Y, layout.Rows.Width, 112f);

    private static MaterialRect ProfilerStartRect(OverlayLayout layout) {
        MaterialRect summary = ProfilerSummaryRect(layout);
        return new MaterialRect(summary.Right - 232f, summary.Y + 26f, 204f, 54f);
    }

    private static MaterialRect ProfilerModeRect(OverlayLayout layout) {
        MaterialRect summary = ProfilerSummaryRect(layout);
        return new MaterialRect(layout.Rows.X, summary.Bottom + 12f, 300f, 42f);
    }

    private static MaterialRect ProfilerSimpleModeRect(OverlayLayout layout) {
        MaterialRect mode = ProfilerModeRect(layout);
        return new MaterialRect(mode.X + 3f, mode.Y + 3f, mode.Width / 2f - 3f, mode.Height - 6f);
    }

    private static MaterialRect ProfilerProfessionalModeRect(OverlayLayout layout) {
        MaterialRect mode = ProfilerModeRect(layout);
        return new MaterialRect(mode.X + mode.Width / 2f, mode.Y + 3f,
            mode.Width / 2f - 3f, mode.Height - 6f);
    }

    private static MaterialRect ProfilerReportViewport(OverlayLayout layout) {
        MaterialRect mode = ProfilerModeRect(layout);
        float top = mode.Bottom + 12f;
        return new MaterialRect(layout.Rows.X, top, layout.Rows.Width,
            Math.Max(0f, layout.Rows.Bottom - top));
    }

    private static MaterialRect ProfilerListViewport(OverlayLayout layout) {
        MaterialRect report = ProfilerReportViewport(layout);
        float availableHeight = Math.Max(0f, report.Height - 34f);
        float rowStride = ProfilerRowHeight + ProfilerRowGap;
        int visibleRows = Math.Max(1, (int)MathF.Floor(
            (availableHeight + ProfilerRowGap) / rowStride));
        float height = Math.Min(availableHeight,
            visibleRows * ProfilerRowHeight + Math.Max(0, visibleRows - 1) * ProfilerRowGap);
        return new MaterialRect(report.X, report.Y + 34f, report.Width, height);
    }

    private float ProfilerMaxScroll(OverlayLayout layout) {
        ManagedProfileReport? report = ManagedCpuSampler.LatestReport;
        if (report is null) return 0f;
        bool simpleMode = MicroblocksQolUtilsModule.Settings.ProfilerSimpleMode;
        int updateCount = simpleMode ? report.Update.Count(entry => entry.IsMod) : report.Update.Count;
        int renderCount = simpleMode ? report.Render.Count(entry => entry.IsMod) : report.Render.Count;
        int count = Math.Max(updateCount, renderCount);
        float contentHeight = count == 0
            ? 0f
            : count * ProfilerRowHeight + (count - 1) * ProfilerRowGap;
        return Math.Max(0f, contentHeight - ProfilerListViewport(layout).Height);
    }

    private void SetProfilerSimpleMode(bool simple) {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (settings.ProfilerSimpleMode == simple) return;
        settings.ProfilerSimpleMode = simple;
        rowScroll.Reset();
        contentEase = 0f;
        Audio.Play("event:/ui/main/rollover_down");
    }

    private bool IsRecorderLibrary => recorderSettingsSection == RecorderSettingsSection.Library;

    private bool IsRecorderTab => tabs[selectedTab].RecorderPage;

    private static bool RecorderActive => AutoRecorder.ManualMode || AutoRecorder.IsRecording;

    private static bool IsRecordingFinalizing(RecordingLibraryEntry file) =>
        AutoRecorder.TryGetFinalizationProgress(file.Path, out _, out _);

    private void RefreshRecordingFiles() {
        string selectedPath = IsRecorderLibrary && recorderSelectedItem >= 0 && recorderSelectedItem < recordingFiles.Count
            ? recordingFiles[recorderSelectedItem].Path : "";
        IReadOnlyList<RecordingLibraryEntry> allRecordings = RecordingLibrary.Scan();
        autoRecordingCount = allRecordings.Count(file => file.Kind == RecordingLibraryKind.Automatic);
        fullRecordingCount = allRecordings.Count(file => file.Kind == RecordingLibraryKind.Full);
        deathReplayCount = allRecordings.Count(file => file.Kind == RecordingLibraryKind.DeathReplay);
        recordingFiles.Clear();
        recordingFiles.AddRange(allRecordings.Where(file => file.Kind == recordingLibraryKind));
        if (IsRecorderLibrary && recorderSelectedItem >= 0) {
            int matching = recordingFiles.FindIndex(file => string.Equals(file.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            recorderSelectedItem = matching >= 0 ? matching : Math.Clamp(recorderSelectedItem, 0, Math.Max(0, recordingFiles.Count - 1));
        }
        recordingRefreshTimer = 1f;
    }

    private void SelectRecorderSection(int index) {
        int count = Enum.GetValues<RecorderSettingsSection>().Length;
        recorderSettingsSection = (RecorderSettingsSection)((index % count + count) % count);
        recorderSelectedItem = -1;
        draggedSlider = null;
        dropdownRow = null;
        rowScroll.Reset();
        if (IsRecorderLibrary) RefreshRecordingFiles();
        Audio.Play("event:/ui/main/rollover_down");
    }

    private void ShowRecordingNotice(string notice) {
        recordingNotice = notice;
        recordingNoticeTimer = 4f;
    }

    private void EnsureRecorderItemVisible(OverlayLayout layout) {
        if (recorderSelectedItem < 0) return;
        MaterialRect rect;
        if (recorderSelectedItem < CurrentRows.Count) {
            rect = RecorderSettingRect(layout, recorderSelectedItem);
        } else {
            int fileIndex = recorderSelectedItem - CurrentRows.Count;
            if (recordingFiles.Count == 0 && fileIndex == 0) rect = RecorderEmptyRect(layout);
            else {
                if (fileIndex < 0 || fileIndex >= recordingFiles.Count) return;
                rect = RecorderFileRect(layout, fileIndex);
            }
        }
        MaterialRect viewport = RecorderViewport(layout);
        float top = rect.Y - viewport.Y + rowScroll.Offset;
        rowScroll.EnsureVisible(top, top + rect.Height, viewport.Height, MaxRowScroll(layout));
    }

    private MaterialRect RecorderHeroRect(OverlayLayout layout) => new(
        layout.Rows.X,
        layout.Rows.Y,
        layout.Rows.Width,
        RecorderHeroHeight
    );

    private static MaterialRect RecorderViewport(OverlayLayout layout) => new(
        layout.Rows.X, layout.Rows.Y + RecorderHeroHeight + 82f,
        layout.Rows.Width, Math.Max(1f, layout.Rows.Height - RecorderHeroHeight - 82f));

    private static MaterialRect RecorderSectionTabRect(OverlayLayout layout, RecorderSettingsSection section) {
        const float gap = 8f;
        int count = Enum.GetValues<RecorderSettingsSection>().Length;
        float width = (layout.Rows.Width - gap * (count - 1)) / count;
        return new MaterialRect(layout.Rows.X + (int)section * (width + gap),
            layout.Rows.Y + RecorderHeroHeight + 16f, width, 46f);
    }

    private float RecorderSettingsStartY(OverlayLayout layout) => RecorderViewport(layout).Y - rowScroll.Offset;

    private MaterialRect RecorderSettingRect(OverlayLayout layout, int index) {
        const float columnGap = 12f;
        int column = index % Columns;
        int band = index / Columns;
        float width = (layout.Rows.Width - columnGap) / Columns;
        return new MaterialRect(
            layout.Rows.X + column * (width + columnGap),
            RecorderSettingsStartY(layout) + band * (RecorderSettingHeight + RowGap),
            width,
            RecorderSettingHeight
        );
    }

    private float RecorderSettingsHeight {
        get {
            int bands = (CurrentRows.Count + Columns - 1) / Columns;
            return bands == 0 ? 0f : bands * RecorderSettingHeight + (bands - 1) * RowGap;
        }
    }

    private float RecorderLibraryHeaderY(OverlayLayout layout) =>
        RecorderViewport(layout).Y - rowScroll.Offset;

    private MaterialRect RecorderLibraryTabRect(OverlayLayout layout, int index) {
        const float gap = 12f;
        float width = (layout.Rows.Width - gap * 2f) / 3f;
        return new MaterialRect(
            layout.Rows.X + index * (width + gap),
            RecorderLibraryHeaderY(layout) + 42f,
            width,
            44f
        );
    }

    private float RecorderFilesStartY(OverlayLayout layout) => RecorderLibraryHeaderY(layout) + 98f;

    private MaterialRect RecorderEmptyRect(OverlayLayout layout) => new(
        layout.Rows.X,
        RecorderFilesStartY(layout),
        layout.Rows.Width,
        104f
    );

    private MaterialRect RecorderFileRect(OverlayLayout layout, int index) => new(
        layout.Rows.X,
        RecorderFilesStartY(layout) + index * (RecorderFileHeight + 10f),
        layout.Rows.Width,
        RecorderFileHeight
    );

    private float RecorderHistoryHeight => recordingFiles.Count == 0
        ? 104f
        : recordingFiles.Count * RecorderFileHeight + Math.Max(0, recordingFiles.Count - 1) * 10f;

    private float RecorderContentHeight(OverlayLayout layout) {
        return IsRecorderLibrary ? 98f + RecorderHistoryHeight + 8f : RecorderSettingsHeight + 8f;
    }

    private string RecordingLibraryTitle => recordingLibraryKind switch {
        RecordingLibraryKind.DeathReplay => "死亡回放",
        RecordingLibraryKind.Automatic => "自动录像",
        _ => "手动录像"
    };

    private static MaterialRect RecorderButtonRect(MaterialRect hero, int index) {
        const float gap = 10f;
        float[] widths = [164f, 196f, 112f];
        float total = widths.Sum() + gap * (widths.Length - 1);
        float x = hero.Right - total - 20f;
        for (int current = 0; current < index; current++) x += widths[current] + gap;
        return new MaterialRect(x, hero.Y + 92f, widths[Math.Clamp(index, 0, widths.Length - 1)], 42f);
    }

    private static MaterialRect RecorderFileOpenRect(MaterialRect row) =>
        new(row.Right - 214f, row.Center.Y - 19f, 92f, 38f);

    private static MaterialRect RecorderFileDeleteRect(MaterialRect row) =>
        new(row.Right - 110f, row.Center.Y - 19f, 92f, 38f);

    private static MaterialRect RecordingDeleteModalRect() => new(610f, 380f, 700f, 320f);

    private static MaterialRect RecordingDeleteCancelRect(MaterialRect modal) =>
        new(modal.X + 46f, modal.Bottom - 76f, 284f, 46f);

    private static MaterialRect RecordingDeleteConfirmRect(MaterialRect modal) =>
        new(modal.Right - 330f, modal.Bottom - 76f, 284f, 46f);

    private static string FormatDuration(double seconds) {
        TimeSpan duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string FormatBytes(long bytes) {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024d && unit < units.Length - 1) {
            value /= 1024d;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private static string FormatEnum<T>(T value) where T : struct, Enum => value switch {
        MiniMapShape.Circle => "圆形",
        MiniMapShape.Square => "方形",
        MiniMapAvatarShape.Circle => "圆形",
        MiniMapAvatarShape.Square => "方形",
        MiniMapNameMode.None => "不显示",
        MiniMapNameMode.WatchedOnly => "仅关心的人",
        MiniMapNameMode.Everyone => "所有人",
        RecorderSettingsSection.Automatic => "自动录制",
        RecorderSettingsSection.DeathReplay => "死亡回放",
        RecorderSettingsSection.Quality => "画质声音",
        RecorderSettingsSection.Storage => "存储清理",
        RecorderSettingsSection.Controls => "快捷键",
        RecorderSettingsSection.Library => "录像库",
        AutoRecordingMode.Off => "关闭",
        AutoRecordingMode.Chapter => "完整流程",
        AutoRecordingMode.Golden => "金草莓挑战",
        GoldenRecordingEnd.BerryCollected => "金草莓结算",
        GoldenRecordingEnd.ChapterComplete => "整关完成",
        GoldenRecordingDeath.Discard => "丢弃录制",
        GoldenRecordingDeath.Continue => "不停止录制",
        GoldenRecordingDeath.Save => "保存录像",
        RecordingPolicy.EveryRoom => "完整流程",
        RecordingPolicy.GoldenRunsOnly => "仅金草莓",
        BgmRecordingMode.CaptureGameMix => "跟随游戏现场",
        BgmRecordingMode.SfxOnlyWithPostMix => "自动对齐与拼接",
        CollisionBoxDisplayMode.Hidden => "不显示碰撞箱",
        CollisionBoxDisplayMode.Visible => "显示碰撞箱",
        CollisionBoxDisplayMode.Only => "只显示碰撞箱",
        _ => value.ToString()
    };

    private static MaterialRect ToggleRect(MaterialRect row) => new(
        row.Right - 78f, row.Center.Y - 14f, 58f, 28f);

    private static MaterialRect SliderRect(MaterialRect row) => new(row.X + 20f, row.Y + 61f,
        Math.Max(40f, row.Width - 154f), 7f);

    private static MaterialRect RangeValueRect(MaterialRect row) => new(row.Right - 118f, row.Y + 46f, 98f, 34f);

    private static MaterialRect WideControlRect(MaterialRect row) => new(row.X + 18f, row.Y + 45f,
        row.Width - 36f, 35f);

    private static MaterialRect ActionControlRect(SettingRow row, MaterialRect rect) {
        float textWidth = SystemTtfFont.MeasureVisible(row.Value(), 0.28f, UiFontWeight.Bold).X;
        float width = Math.Clamp(textWidth + 38f, 98f, 250f);
        return new MaterialRect(rect.Right - width - 18f, rect.Center.Y - 19f, width, 38f);
    }

    private MaterialRect DropdownControlRect(OverlayLayout layout, SettingRow row) {
        int index = CurrentRows.IndexOf(row);
        if (index < 0) return default;
        MaterialRect rowRect = IsRecorderTab
            ? RecorderSettingRect(layout, index)
            : layout.Row(index, rowScroll.Offset);
        return WideControlRect(rowRect);
    }

    private MaterialRect DropdownRect(OverlayLayout layout, SettingRow row) {
        MaterialRect control = DropdownControlRect(layout, row);
        float height = DropdownVisibleCount(row) * DropdownItemHeight + 12f;
        float y = control.Bottom + 6f;
        if (y + height > layout.Body.Bottom) y = control.Y - height - 6f;
        y = Math.Clamp(y, layout.Body.Y, Math.Max(layout.Body.Y, layout.Body.Bottom - height));
        return new MaterialRect(control.X, y, control.Width, height);
    }

    private static MaterialRect DropdownItemRect(MaterialRect menu, int visibleIndex) => new(
        menu.X + 6f,
        menu.Y + 6f + visibleIndex * DropdownItemHeight,
        menu.Width - 12f,
        DropdownItemHeight
    );

    private static int DropdownVisibleCount(SettingRow row) =>
        Math.Min(DropdownMaxVisibleItems, row.Options?.Count ?? 0);

    private static void SetTextInputRectangle(MaterialRect rect) {
        float xScale = Engine.ViewWidth / ScreenWidth;
        float yScale = Engine.ViewHeight / ScreenHeight;
        TextInputEXT.SetInputRectangle(new Rectangle(
            (int)(rect.X * xScale),
            (int)(rect.Y * yScale),
            Math.Max(1, (int)(rect.Width * xScale)),
            Math.Max(1, (int)(rect.Height * yScale))
        ));
    }

    private static string Trim(string value, int maxCharacters) => value.Length <= maxCharacters
        ? value
        : value[..Math.Max(1, maxCharacters - 1)] + "…";

    private static string TrimFromLeft(string value, int maxCharacters) => value.Length <= maxCharacters
        ? value
        : "…" + value[^Math.Max(1, maxCharacters - 1)..];

    private enum SettingKind {
        Toggle,
        Range,
        Enum,
        Text,
        Action,
        Status
    }

    private enum CloseDestination {
        None,
        PauseMenu,
        Game
    }

    private sealed record SettingsTab(
        string Title,
        string Icon,
        List<SettingRow> Rows,
        bool RecorderPage = false,
        bool ProfilerPage = false
    );

    private sealed class SettingRow {
        public string Label { get; }
        public SettingKind Kind { get; }
        public Func<string> Value { get; }
        public Action<int>? Change { get; }
        public Func<bool>? IsVisible { get; set; }
        public Func<bool>? IsEnabled { get; }
        public Func<bool>? ToggleValue { get; }
        public bool HighlightWhenOn { get; }
        public Func<float>? Normalized { get; }
        public Action<float>? SetNormalized { get; }
        public IReadOnlyList<string>? Options { get; }
        public Func<int>? SelectedOption { get; }
        public Action<int>? SelectOption { get; }
        public Func<string>? EditValue { get; }
        public Func<string, bool>? CommitEdit { get; }
        public bool NumericInput { get; }
        public string? Placeholder { get; }
        public int MaxInputLength { get; }
        public float FocusAnimation { get; set; }
        public float HoverAnimation { get; set; }
        public float ToggleAnimation { get; set; }
        public float SliderAnimation { get; set; }
        public float Pulse { get; set; }

        public SettingRow(
            string label,
            SettingKind kind,
            Func<string> value,
            Action<int>? change = null,
            Func<bool>? isEnabled = null,
            Func<bool>? toggleValue = null,
            bool highlightWhenOn = false,
            Func<float>? normalized = null,
            Action<float>? setNormalized = null,
            IReadOnlyList<string>? options = null,
            Func<int>? selectedOption = null,
            Action<int>? selectOption = null,
            Func<string>? editValue = null,
            Func<string, bool>? commitEdit = null,
            bool numericInput = false,
            string? placeholder = null,
            int maxInputLength = 120
        ) {
            Label = label;
            Kind = kind;
            Value = value;
            Change = change;
            IsEnabled = isEnabled;
            ToggleValue = toggleValue;
            HighlightWhenOn = highlightWhenOn;
            Normalized = normalized;
            SetNormalized = setNormalized;
            Options = options;
            SelectedOption = selectedOption;
            SelectOption = selectOption;
            EditValue = editValue;
            CommitEdit = commitEdit;
            NumericInput = numericInput;
            Placeholder = placeholder;
            MaxInputLength = maxInputLength;
        }

        public bool Enabled() => IsEnabled?.Invoke() ?? true;

        public void InitializeVisuals() {
            ToggleAnimation = ToggleValue?.Invoke() == true ? 1f : 0f;
            SliderAnimation = Normalized?.Invoke() ?? 0f;
        }
    }

    private readonly record struct OverlayLayout(
        MaterialRect Panel,
        MaterialRect Header,
        MaterialRect Navigation,
        MaterialRect NavigationItems,
        MaterialRect Body,
        MaterialRect ContentHeader,
        MaterialRect Rows
    ) {
        public const float NavigationHeaderHeight = 64f;

        public static OverlayLayout Create(float transition) {
            MaterialSplitPageLayout page = MaterialSplitPageLayout.Create(transition);
            return new OverlayLayout(page.Frame, page.Header, page.Navigation,
                page.NavigationItems(NavigationHeaderHeight), page.Content, page.ContentHeader,
                page.ContentBody);
        }

        public MaterialRect Tab(int index, int count) {
            _ = count;
            return MaterialSplitPageLayout.Tab(NavigationItems, index);
        }

        public MaterialRect Row(int index, float scrollOffset) {
            int column = index % Columns;
            int band = index / Columns;
            float width = (Rows.Width - RowGap) / Columns;
            return new MaterialRect(
                Rows.X + column * (width + RowGap),
                Rows.Y + band * (RowHeight + RowGap) - scrollOffset,
                width,
                RowHeight
            );
        }
    }
}
