using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Celeste.Mod.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Material replacement for Everest's global Mod Options page. Everest still builds the
/// TextMenu, so custom entries and binding/config sub-pages keep their original behavior;
/// this class only owns grouping, navigation, layout and mouse interaction.
/// </summary>
public sealed class MaterialModOptions : Oui, IMaterialAcrylicPage {
    private const float ScreenWidth = 1920f;
    private const float ScreenHeight = 1080f;
    private const float TabHeight = MaterialSplitPageLayout.TabHeight;
    private const float TabGap = MaterialSplitPageLayout.TabGap;
    private const float RowGap = 10f;
    private const float StandardRowHeight = 78f;
    private const float DescribedTitleHeight = 48f;
    private const float DescriptionLineHeight = 23f;
    private const float DescriptionTopPadding = 0f;
    private const float DescriptionBottomPadding = 12f;
    private const float RowTextOffset = 58f;
    private const float DropdownItemHeight = 46f;
    private const int DropdownMaxVisibleItems = 8;
    private const int SearchTextLimit = 80;
    private const float CompositePopupHorizontalPadding = 48f;

    private static readonly string[] TabIconPool = [
        "bolt", "layers", "map", "route", "sports_esports", "timer", "visibility",
        "replay", "memory", "analytics", "dashboard", "notifications", "group",
        "deployed_code", "list_alt", "blur_on", "keyboard", "toggle_on", "schedule", "speed"
    ];

    private static readonly Color[] TabAccentSeeds = [
        new Color(126, 99, 184),
        new Color(53, 132, 228),
        new Color(34, 157, 144),
        new Color(84, 148, 96),
        new Color(217, 137, 45),
        new Color(210, 90, 112),
        new Color(154, 103, 196),
        new Color(49, 151, 181)
    ];

    private static Hook? gotoRoutineHook;
    private static bool hookFailed;
    private static readonly FieldInfo? textMenuWidthField = typeof(TextMenu).GetField("width",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly List<ModTab> tabs = [];
    private readonly List<ModTab> sourceTabs = [];
    private readonly Dictionary<TextMenu.Item, bool> naturalVisibility = [];
    private readonly Dictionary<TextMenu.Item, ModTab> itemOwners = [];
    private readonly Dictionary<TextMenu.Item, string> favoriteKeys = [];
    private readonly MaterialScrollController tabScroll = new();
    private readonly MaterialScrollController rowScroll = new();
    private readonly MaterialScrollController compositePopupScroll = new();
    private readonly MaterialScrollViewport tabViewport = new("mqol-mod-options-tabs");
    private readonly MaterialScrollViewport rowViewport = new("mqol-mod-options-rows");
    private readonly MaterialScrollViewport compositePopupViewport = new("mqol-mod-options-composite-popup");
    private readonly MaterialMotionController motion = new();

    private TextMenu? menu;
    private Level? pauseLevel;
    private int pauseReturnIndex;
    private bool pauseMinimal;
    private bool oldAllowHudHide;
    private int selectedTab;
    private float ease;
    private bool display;
    private float inputDelay;
    internal bool AcceptsTouch => Visible && Active && display && inputDelay <= 0 && ease > 0.99f
        && (pauseLevel is not null || Selected && Focused);
    private string TouchContext => $"mods:{selectedTab}:{tabSearchText}:{settingSearchText}:{dropdownItem?.GetHashCode()}:{compositePopupItem?.GetHashCode()}";
    private CloseDestination closeDestination;
    private bool saveStarted;
    private string? savedTabId;
    private int savedItemOrdinal = -1;
    private TextMenu.Item? dropdownItem;
    private TextMenu.Item? compositePopupItem;
    private int dropdownHighlight;
    private int dropdownFirstVisible;
    private string tabSearchText = "";
    private string settingSearchText = "";
    private string imeText = "";
    private SearchTarget searchTarget;

    public bool SuppressNormalRender { get; set; }

    internal static MaterialModOptions? ActivePage {
        get {
            if (Engine.Scene is not Overworld overworld) return null;
            MaterialModOptions? page = overworld.Current as MaterialModOptions
                ?? overworld.Next as MaterialModOptions;
            return page is { Visible: true } ? page : null;
        }
    }

    public MaterialModOptions() {
        Tag = Tags.HUD | Tags.PauseUpdate | Tags.TransitionUpdate;
    }

    private MaterialModOptions(Level level, int returnIndex, bool minimal, bool allowHudHide) : this() {
        pauseLevel = level;
        pauseReturnIndex = returnIndex;
        pauseMinimal = minimal;
        oldAllowHudHide = allowHudHide;
        Depth = -2_000_000;
    }

    public static void Load() {
        Everest.Events.Level.OnCreatePauseMenuButtons += ReplacePauseMenuButton;
        if (gotoRoutineHook is not null || hookFailed) return;
        try {
            MethodInfo method = typeof(Overworld).GetMethod(
                "GotoRoutine",
                BindingFlags.Instance | BindingFlags.NonPublic
            ) ?? throw new MissingMethodException(typeof(Overworld).FullName, "GotoRoutine");
            gotoRoutineHook = new Hook(method, (GotoRoutineDetour)DetourGotoRoutine);
        } catch (Exception exception) {
            hookFailed = true;
            Logger.LogDetailed(exception, "MicroblocksQolUtils/MaterialModOptions");
        }
    }

    public static void Unload() {
        Everest.Events.Level.OnCreatePauseMenuButtons -= ReplacePauseMenuButton;
        gotoRoutineHook?.Dispose();
        gotoRoutineHook = null;
        hookFailed = false;
    }

    public override bool IsStart(Overworld overworld, Overworld.StartMode start) => false;

    public override IEnumerator Enter(Oui from) {
        Visible = true;
        display = true;
        ease = 0f;
        inputDelay = 0.18f;
        closeDestination = CloseDestination.None;
        saveStarted = false;
        BuildMenu(inGame: false);
        AttachMenu();
        RestoreTab(from is OuiModOptions.ISubmenu);
        Audio.Play("event:/ui/main/whoosh_large_in");
        yield return null;
    }

    public override IEnumerator Leave(Oui next) {
        display = false;
        SetSearchTarget(SearchTarget.None);
        if (menu is not null) menu.Focused = false;
        SaveTabState();
        for (float timer = 0f; timer < 0.16f; timer += Engine.RawDeltaTime) yield return null;
        yield return Everest.SaveSettings();
        DetachMenu();
        Visible = false;
    }

    public override void Added(Scene scene) {
        base.Added(scene);
        if (pauseLevel is null) return;
        Visible = true;
        display = true;
        ease = 0f;
        inputDelay = 0.18f;
        BuildMenu(inGame: true);
        AttachMenu();
        RestoreTab(restoreSaved: false);
    }

    public override void Removed(Scene scene) {
        SetSearchTarget(SearchTarget.None);
        DetachMenu();
        if (pauseLevel is not null) pauseLevel.AllowHudHide = oldAllowHudHide;
        tabViewport.Dispose();
        rowViewport.Dispose();
        compositePopupViewport.Dispose();
        motion.Dispose();
        base.Removed(scene);
    }

    public override void Update() {
        MaterialTouch.BeginUpdate(this);
        base.Update();
        ModOptionsLayout layout = ModOptionsLayout.Create(1f - Ease.CubeOut(ease));
        tabScroll.Update(MaxTabScroll(layout));
        rowScroll.Update(MaxRowScroll(layout));
        compositePopupScroll.Update(MaxCompositePopupScroll(layout));
        ease = Calc.Approach(ease, display ? 1f : 0f, Engine.RawDeltaTime * 7.5f);
        inputDelay -= Engine.RawDeltaTime;
        UpdateInteractions(layout);

        if (!display) {
            if (pauseLevel is not null && ease <= 0.001f && !saveStarted) {
                saveStarted = true;
                Add(new Coroutine(SaveAndFinishPause()));
            }
            return;
        }

        if (menu is null) return;
        bool acceptsInput = pauseLevel is not null || Selected && Focused;
        if (acceptsInput && inputDelay <= 0f && dropdownItem is null && compositePopupItem is null
            && UpdateSearchInput(layout)) {
            menu.Focused = false;
            menu.Update();
            CaptureNaturalVisibility();
            ApplyTabVisibility(invokeSelectionCallbacks: false);
            return;
        }
        menu.Focused = searchTarget == SearchTarget.None;
        if (!menu.Focused) {
            menu.Update();
            CaptureNaturalVisibility();
            ApplyTabVisibility(invokeSelectionCallbacks: false);
            return;
        }
        if (!acceptsInput || inputDelay > 0f) {
            menu.Focused = false;
            menu.Update();
            menu.Focused = true;
            CaptureNaturalVisibility();
            ApplyTabVisibility(invokeSelectionCallbacks: false);
            return;
        }

        if (dropdownItem is not null) {
            UpdateDropdown(layout);
            return;
        }
        if (compositePopupItem is not null) {
            menu.Focused = false;
            UpdateCompositePopup(layout);
            return;
        }

        if (Input.Pause.Pressed && pauseLevel is not null) {
            BeginClose(CloseDestination.Game);
            return;
        }
        if (Input.MenuCancel.Pressed || Input.ESC.Pressed || MInput.Keyboard.Pressed(Keys.Escape)) {
            if (pauseLevel is null) {
                Audio.Play("event:/ui/main/button_back");
                Overworld.Goto<OuiMainMenu>();
            } else {
                BeginClose(CloseDestination.PauseMenu);
            }
            return;
        }

        if (MaterialTabNavigation.TryGetShiftVerticalDirection(out int tabDirection)) {
            MoveTab(tabDirection);
            return;
        }

        bool control = MInput.Keyboard.Check(Keys.LeftControl, Keys.RightControl);
        if (control && MInput.Keyboard.Pressed(Keys.P)) {
            ToggleTabPin(tabs[selectedTab]);
            return;
        }
        if (control && MInput.Keyboard.Pressed(Keys.D)
            && menu.Current is { } favoriteCandidate
            && CanFavorite(favoriteCandidate)) {
            ToggleFavorite(favoriteCandidate);
            return;
        }

        if ((Input.MenuConfirm.Pressed
                || MInput.Keyboard.Pressed(Keys.Enter)
                || MInput.Keyboard.Pressed(Keys.Space))
            && menu.Current is { } composite
            && IsCompositeMenu(composite)) {
            OpenCompositePopup(composite);
            return;
        }

        if ((Input.MenuConfirm.Pressed
                || MInput.Keyboard.Pressed(Keys.Enter)
                || MInput.Keyboard.Pressed(Keys.Space))
            && menu.Current is { } current
            && TryGetOption(current, out OptionSnapshot option)) {
            if (option.Options.Count == 2) {
                SetOptionIndex(current, option.Index == 0 ? 1 : 0);
                return;
            }
            if (CanUseDropdown(option)) {
                OpenDropdown(current, option);
                return;
            }
        }

        UpdateMouse(layout);
        int previousSelection = menu.Selection;
        menu.Update();
        if (menu.Current is { } expanded && IsCompositeMenu(expanded) && IsExpandedComposite(expanded)) {
            AdoptCompositePopup(expanded);
            return;
        }
        CaptureNaturalVisibility();
        ApplyTabVisibility(invokeSelectionCallbacks: false);
        if (menu.Selection != previousSelection) EnsureSelectionVisible(layout);
    }

    public override void Render() {
        if (SuppressNormalRender) return;
        RenderMaterialContent(acrylicActive: false);
    }

    public void RenderMaterialContent(bool acrylicActive) {
        if (!Visible || ease <= 0f || menu is null) return;
        float alpha = Ease.CubeOut(ease);
        ModOptionsLayout layout = ModOptionsLayout.Create(1f - alpha);
        MaterialPalette palette = MaterialPalette.FromSeed(new Color(126, 99, 184));
        Draw.Rect(0f, 0f, ScreenWidth, ScreenHeight, palette.Scrim * (0.90f * alpha));

        MaterialSplitPageChrome.RenderHeader(layout.Header, "extension",
            UiText("microblocks_qol_modoptions_title", "模组设置"), palette, alpha);
        int shownTabs = FilteredTabIndices().Count;
        string tabCount = shownTabs == tabs.Count ? tabs.Count.ToString() : $"{shownTabs}/{tabs.Count}";
        MaterialUiKit.Text($"{tabCount} {UiText("microblocks_qol_modoptions_tabs", "个分类")}",
            new Vector2(layout.Header.Right, layout.Header.Center.Y), new Vector2(1f, 0.5f),
            MaterialTextRole.Caption, palette.OnSurfaceVariant, alpha, scaleOverride: 0.29f);

        RenderTabs(layout, palette, alpha);
        RenderRows(layout, palette, alpha);
        if (dropdownItem is not null) RenderDropdown(layout, palette, alpha);
        if (compositePopupItem is not null) RenderCompositePopup(layout, palette, alpha);
        MaterialUiKit.Cursor(MaterialTouch.Position, palette, alpha);
    }

    private void BuildMenu(bool inGame) {
        DetachMenu();
        tabs.Clear();
        sourceTabs.Clear();
        naturalVisibility.Clear();
        itemOwners.Clear();
        favoriteKeys.Clear();
        menu = OuiModOptions.CreateMenu(inGame, null!);
        menu.Active = false;
        menu.Visible = false;
        menu.Focused = true;
        menu.AutoScroll = false;
        menu.ItemSpacing = RowGap;

        List<TextMenu.Item> leading = [];
        ModTab? current = null;
        Dictionary<string, int> tabTitleOccurrences = new(StringComparer.OrdinalIgnoreCase);
        foreach (TextMenu.Item item in menu.Items) {
            naturalVisibility[item] = item.Visible;
            if (IsEverestHeaderImage(item)) {
                naturalVisibility[item] = false;
                continue;
            }
            if (item is TextMenu.SubHeader header
                && TrySplitModuleHeader(header.Title, out string title, out string version)) {
                int occurrence = tabTitleOccurrences.GetValueOrDefault(title);
                tabTitleOccurrences[title] = occurrence + 1;
                string persistentId = PersistentTabId(title, occurrence);
                current = new ModTab(persistentId, title, version, [], pinnable: true);
                sourceTabs.Add(current);
                naturalVisibility[item] = false;
                continue;
            }
            if (current is null) leading.Add(item);
            else current.Items.Add(item);
        }

        if (leading.Any(item => naturalVisibility.GetValueOrDefault(item))) {
            sourceTabs.Insert(0, new ModTab("__status",
                UiText("microblocks_qol_modoptions_status", "状态"), "", leading, pinnable: false));
        } else {
            foreach (TextMenu.Item item in leading) naturalVisibility[item] = false;
        }
        if (sourceTabs.Count == 0) {
            sourceTabs.Add(new ModTab("__empty",
                UiText("microblocks_qol_modoptions_empty", "没有可用设置"), "", [], pinnable: false));
        }
        IndexSourceItems();
        RebuildTabs();
    }

    private void IndexSourceItems() {
        foreach (ModTab tab in sourceTabs) {
            int favoriteOrdinal = 0;
            foreach (TextMenu.Item item in tab.Items) {
                itemOwners[item] = tab;
                if (!CanFavorite(item)) continue;
                favoriteKeys[item] = $"{tab.Id}|item:{favoriteOrdinal}";
                favoriteOrdinal++;
            }
        }
    }

    private void RebuildTabs(string? preferredTabId = null, TextMenu.Item? preferredItem = null) {
        string? restoreId = preferredTabId;
        if (restoreId is null && tabs.Count > 0 && selectedTab >= 0 && selectedTab < tabs.Count)
            restoreId = tabs[selectedTab].Id;

        tabs.Clear();
        List<TextMenu.Item> favorites = favoriteKeys
            .Where(pair => IsFavorite(pair.Key))
            .Select(pair => pair.Key)
            .ToList();
        if (favorites.Count > 0) {
            tabs.Add(new ModTab("__favorites",
                UiText("microblocks_qol_modoptions_favorites", "收藏的选项"), "", favorites,
                pinnable: false));
        }
        tabs.AddRange(sourceTabs.Where(IsPinned));
        tabs.AddRange(sourceTabs.Where(tab => !IsPinned(tab)));

        selectedTab = restoreId is null ? 0 : tabs.FindIndex(tab => tab.Id == restoreId);
        if (selectedTab < 0) selectedTab = 0;
        if (preferredItem is not null && tabs.Count > 0)
            tabs[selectedTab].Selection = menu?.IndexOf(preferredItem) ?? -1;
    }

    private static string PersistentTabId(string title, int occurrence) =>
        $"mod:{Uri.EscapeDataString(title)}:{occurrence}";

    private bool IsPinned(ModTab tab) => tab.Pinnable
        && MicroblocksQolUtilsModule.Settings.PinnedModOptionsTabs.Contains(tab.Id,
            StringComparer.Ordinal);

    private bool IsFavorite(TextMenu.Item item) => favoriteKeys.TryGetValue(item, out string? key)
        && MicroblocksQolUtilsModule.Settings.FavoriteModOptionsItems.Contains(key,
            StringComparer.Ordinal);

    private static bool CanFavorite(TextMenu.Item item) =>
        item is not TextMenu.SubHeader
        && item is not TextMenuExt.EaseInSubHeaderExt
        && item.Selectable;

    private ModTab SourceTabFor(TextMenu.Item item, ModTab fallback) =>
        itemOwners.GetValueOrDefault(item) ?? fallback;

    private void ToggleTabPin(ModTab tab) {
        if (!tab.Pinnable) return;
        List<string> pinned = MicroblocksQolUtilsModule.Settings.PinnedModOptionsTabs;
        bool remove = pinned.RemoveAll(id => string.Equals(id, tab.Id, StringComparison.Ordinal)) > 0;
        if (!remove) pinned.Add(tab.Id);
        RebuildTabs(tab.Id);
        ApplyTabVisibility();
        EnsureTabVisible(ModOptionsLayout.Create(0f));
        Audio.Play(remove ? "event:/ui/main/button_toggle_off" : "event:/ui/main/button_toggle_on");
    }

    private void ToggleFavorite(TextMenu.Item item) {
        if (!favoriteKeys.TryGetValue(item, out string? key)) return;
        List<string> favorites = MicroblocksQolUtilsModule.Settings.FavoriteModOptionsItems;
        bool remove = favorites.RemoveAll(value => string.Equals(value, key, StringComparison.Ordinal)) > 0;
        if (!remove) favorites.Add(key);

        string currentTabId = tabs[selectedTab].Id;
        string sourceTabId = SourceTabFor(item, tabs[selectedTab]).Id;
        string destination = currentTabId == "__favorites" && remove
            ? "__favorites"
            : currentTabId;
        RebuildTabs(destination, remove ? null : item);
        if (tabs[selectedTab].Id != destination && currentTabId == "__favorites" && remove) {
            tabSearchText = "";
            RebuildTabs(sourceTabId, item);
        }
        rowScroll.Reset();
        ApplyTabVisibility();
        EnsureSelectionVisible(ModOptionsLayout.Create(0f));
        Audio.Play(remove ? "event:/ui/main/button_toggle_off" : "event:/ui/main/button_toggle_on");
    }

    private void AttachMenu() {
        if (menu is null || Scene is null || menu.Scene is not null) return;
        Scene.Add(menu);
    }

    private void DetachMenu() {
        if (menu is null) return;
        CloseCompositePopup(playSound: false);
        if (menu.Scene is not null) menu.RemoveSelf();
        menu = null;
    }

    private void RestoreTab(bool restoreSaved) {
        int restored = restoreSaved && savedTabId is not null
            ? tabs.FindIndex(tab => tab.Id == savedTabId)
            : -1;
        selectedTab = restored >= 0 ? restored : 0;
        if (restoreSaved && savedItemOrdinal >= 0 && savedItemOrdinal < tabs[selectedTab].Items.Count) {
            tabs[selectedTab].Selection = menu?.IndexOf(tabs[selectedTab].Items[savedItemOrdinal]) ?? -1;
        }
        ApplyTabVisibility();
        EnsureTabVisible(ModOptionsLayout.Create(0f));
    }

    private void SelectTab(int index, bool playSound = true) {
        if (tabs.Count == 0) return;
        CloseDropdown(playSound: false);
        CloseCompositePopup(playSound: false);
        SaveTabState();
        selectedTab = Math.Clamp(index, 0, tabs.Count - 1);
        rowScroll.Reset();
        ApplyTabVisibility();
        EnsureTabVisible(ModOptionsLayout.Create(0f));
        if (playSound) Audio.Play("event:/ui/main/rollover_down");
    }

    private void MoveTab(int direction) {
        List<int> visible = FilteredTabIndices();
        if (visible.Count == 0) return;
        int position = visible.IndexOf(selectedTab);
        if (position < 0) position = 0;
        int nextPosition = (position + Math.Sign(direction) + visible.Count) % visible.Count;
        SelectTab(visible[nextPosition], playSound: false);
        Audio.Play(direction < 0 ? "event:/ui/main/rollover_up" : "event:/ui/main/rollover_down");
    }

    private void SaveTabState() {
        if (menu is null || tabs.Count == 0) return;
        CaptureNaturalVisibility();
        ModTab tab = tabs[Math.Clamp(selectedTab, 0, tabs.Count - 1)];
        savedTabId = tab.Id;
        tab.Selection = menu.Selection;
        savedItemOrdinal = menu.Current is { } current ? tab.Items.IndexOf(current) : -1;
    }

    private void ApplyTabVisibility(bool invokeSelectionCallbacks = true) {
        if (menu is null || tabs.Count == 0) return;
        if (invokeSelectionCallbacks) menu.Current?.OnLeave?.Invoke();
        ModTab tab = tabs[selectedTab];
        HashSet<TextMenu.Item> active = tab.Items.ToHashSet();
        bool tabVisible = FilteredTabIndices().Contains(selectedTab);
        foreach (TextMenu.Item item in menu.Items) {
            bool visible = tabVisible && active.Contains(item) && naturalVisibility.GetValueOrDefault(item);
            if (visible && item is not TextMenuExt.EaseInSubHeaderExt)
                visible = MatchesSettingSearch(SourceTabFor(item, tab), item);
            item.Visible = visible;
        }
        menu.MinWidth = Math.Max(600f, ModOptionsLayout.Create(0f).Rows.Width - 54f);
        menu.RecalculateSize();
        // During the normal update pass, TextMenu has already applied keyboard or mouse
        // navigation. Preserve that live selection instead of restoring the tab's previous
        // selection every frame; otherwise keyboard focus cannot move and a mouse hover after
        // scrolling can restore the first item and pull the viewport back to the top.
        int selection = invokeSelectionCallbacks ? tab.Selection : menu.Selection;
        if ((selection < 0 || selection >= menu.Items.Count || !CanSelect(menu.Items[selection]))
            && tab.Selection >= 0 && tab.Selection < menu.Items.Count
            && CanSelect(menu.Items[tab.Selection])) {
            selection = tab.Selection;
        }
        if (selection < 0 || selection >= menu.Items.Count || !CanSelect(menu.Items[selection])) {
            selection = menu.Items.FindIndex(CanSelect);
        }
        menu.Selection = selection;
        tab.Selection = selection;
        if (invokeSelectionCallbacks && selection >= 0) menu.Items[selection].OnEnter?.Invoke();
    }

    private void CaptureNaturalVisibility() {
        if (menu is null || tabs.Count == 0) return;
        ModTab tab = tabs[selectedTab];
        foreach (TextMenu.Item item in tab.Items) {
            if (item is not TextMenuExt.EaseInSubHeaderExt
                && !MatchesSettingSearch(SourceTabFor(item, tab), item)) continue;
            naturalVisibility[item] = item.Visible;
        }
    }

    private bool UpdateSearchInput(ModOptionsLayout layout) {
        Vector2 mouse = MaterialTouch.Position;
        if (MaterialTouch.PressedLeftButton) {
            if (layout.TabSearch.Contains(mouse)) {
                SetSearchTarget(SearchTarget.Tabs);
                Audio.Play("event:/ui/main/button_select");
                return true;
            }
            if (layout.SettingSearch.Contains(mouse)) {
                SetSearchTarget(SearchTarget.Settings);
                Audio.Play("event:/ui/main/button_select");
                return true;
            }
        }
        if (MInput.Keyboard.Check(Keys.LeftControl, Keys.RightControl)
            && MInput.Keyboard.Pressed(Keys.F)) {
            SetSearchTarget(MInput.Keyboard.Check(Keys.LeftShift, Keys.RightShift)
                ? SearchTarget.Tabs
                : SearchTarget.Settings);
            return true;
        }
        if (searchTarget == SearchTarget.None) return false;
        if (Input.MenuCancel.Pressed || MaterialTextInputFocus.Pressed(Keys.Escape)) {
            SetSearchTarget(SearchTarget.None);
            return true;
        }
        if (MaterialTextInputFocus.Pressed(Keys.Enter)) {
            SetSearchTarget(SearchTarget.None);
            return true;
        }
        if (MaterialTouch.PressedLeftButton
            && !layout.TabSearch.Contains(mouse)
            && !layout.SettingSearch.Contains(mouse)) {
            SetSearchTarget(SearchTarget.None);
            return false;
        }
        return true;
    }

    private void SetSearchTarget(SearchTarget target) {
        if (searchTarget == target) return;
        bool wasFocused = searchTarget != SearchTarget.None;
        bool isFocused = target != SearchTarget.None;
        searchTarget = target;
        imeText = "";
        if (!wasFocused && isFocused) {
            TextInput.OnInput += OnTextInput;
            TextInputEXT.TextEditing += OnTextEditing;
            MaterialTextInputFocus.Focus(this);
        } else if (wasFocused && !isFocused) {
            TextInput.OnInput -= OnTextInput;
            TextInputEXT.TextEditing -= OnTextEditing;
            MaterialTextInputFocus.Blur(this);
        }
    }

    private void OnTextInput(char character) {
        if (searchTarget == SearchTarget.None) return;
        ref string value = ref searchTarget == SearchTarget.Tabs
            ? ref tabSearchText
            : ref settingSearchText;
        if (character == '\b') {
            if (value.Length == 0) return;
            value = value[..^1];
        } else if (!char.IsControl(character) && value.Length < SearchTextLimit) {
            value += character;
        } else {
            return;
        }
        imeText = "";
        SearchChanged();
    }

    private void OnTextEditing(string? text, int start, int length) {
        _ = start;
        _ = length;
        if (searchTarget != SearchTarget.None) imeText = text ?? "";
    }

    private void SearchChanged() {
        CloseDropdown(playSound: false);
        if (searchTarget == SearchTarget.Tabs) {
            tabScroll.Reset();
            List<int> visible = FilteredTabIndices();
            if (visible.Count > 0 && !visible.Contains(selectedTab)) selectedTab = visible[0];
        } else {
            rowScroll.Reset();
        }
        ApplyTabVisibility();
        EnsureTabVisible(ModOptionsLayout.Create(0f));
        EnsureSelectionVisible(ModOptionsLayout.Create(0f));
    }

    private List<int> FilteredTabIndices() {
        List<int> result = [];
        string search = tabSearchText.Trim();
        for (int index = 0; index < tabs.Count; index++) {
            ModTab tab = tabs[index];
            if (search.Length == 0
                || tab.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || tab.Version.Contains(search, StringComparison.CurrentCultureIgnoreCase)) {
                result.Add(index);
            }
        }
        return result;
    }

    private bool MatchesSettingSearch(ModTab tab, TextMenu.Item item) {
        string search = settingSearchText.Trim();
        if (search.Length == 0 || item is TextMenuExt.EaseInSubHeaderExt) return true;
        if (TryGetSearchLabel(item, out string label)
            && label.Contains(search, StringComparison.CurrentCultureIgnoreCase)) return true;
        if (TryGetOption(item, out OptionSnapshot option) && option.Options.Count > 0
            && option.Options[Math.Clamp(option.Index, 0, option.Options.Count - 1)]
                .Contains(search, StringComparison.CurrentCultureIgnoreCase)) return true;
        foreach (TextMenuExt.EaseInSubHeaderExt description in DescriptionCandidates(tab, item)) {
            if (description.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)) return true;
        }
        return false;
    }

    private static bool TryGetSearchLabel(TextMenu.Item item, out string label) {
        if (item is TextMenu.SubHeader header) {
            label = header.Title;
            return label.Length > 0;
        }
        return TryGetLabel(item, out label);
    }

    private List<RowEntry> VisibleRows() {
        List<RowEntry> rows = [];
        if (tabs.Count == 0 || !FilteredTabIndices().Contains(selectedTab)) return rows;
        ModTab tab = tabs[selectedTab];
        foreach (TextMenu.Item item in tab.Items) {
            if (item is TextMenuExt.EaseInSubHeaderExt || !item.Visible) continue;
            rows.Add(new RowEntry(item, SelectDescription(SourceTabFor(item, tab), item)));
        }
        return rows;
    }

    private static List<TextMenuExt.EaseInSubHeaderExt> DescriptionCandidates(ModTab tab,
        TextMenu.Item owner) {
        List<TextMenuExt.EaseInSubHeaderExt> result = [];
        int index = tab.Items.IndexOf(owner);
        for (int next = index + 1; next < tab.Items.Count; next++) {
            if (tab.Items[next] is not TextMenuExt.EaseInSubHeaderExt description) break;
            result.Add(description);
        }
        return result;
    }

    private static TextMenuExt.EaseInSubHeaderExt? SelectDescription(ModTab tab, TextMenu.Item owner) {
        List<TextMenuExt.EaseInSubHeaderExt> candidates = DescriptionCandidates(tab, owner);
        if (candidates.Count == 0) return null;
        string relaunch = Dialog.Clean("MODOPTIONS_NEEDSRELAUNCH");
        TextMenuExt.EaseInSubHeaderExt? warning = candidates.LastOrDefault(description =>
            description.FadeVisible
            && !string.Equals(description.Title, relaunch, StringComparison.CurrentCultureIgnoreCase)
            && IsWarningColor(description.TextColor));
        if (warning is not null) return warning;
        TextMenuExt.EaseInSubHeaderExt? regular = candidates.FirstOrDefault(description =>
            description.TextColor == Color.Gray);
        if (regular is not null) return regular;
        TextMenuExt.EaseInSubHeaderExt? active = candidates.FirstOrDefault(description =>
            description.FadeVisible
            && !string.Equals(description.Title, relaunch, StringComparison.CurrentCultureIgnoreCase));
        if (active is not null) return active;
        return candidates.FirstOrDefault(description =>
            string.Equals(description.Title, relaunch, StringComparison.CurrentCultureIgnoreCase));
    }

    private static bool IsWarningColor(Color color) =>
        color == Color.OrangeRed || color == Color.Goldenrod || color == Color.Red || color == Color.Yellow;

    private void UpdateMouse(ModOptionsLayout layout) {
        if (menu is null) return;
        Vector2 mouse = MaterialTouch.Position;
        bool scrolled = false;
        if (MaterialTouch.WheelDelta != 0) {
            float direction = -Math.Sign(MaterialTouch.WheelDelta);
            if (layout.Navigation.Contains(mouse)) {
                tabScroll.Scroll(direction * 180f, MaxTabScroll(layout));
                scrolled = true;
            } else if (layout.Rows.Contains(mouse)) {
                rowScroll.Scroll(direction * 220f, MaxRowScroll(layout));
                scrolled = true;
            }
        }

        // Wheel input can also make Everest report the mouse as moved. Do not retarget the
        // selection in that frame: selecting the row that was under the pointer before the
        // scroll would immediately pull the scroll target back toward it.
        if (scrolled) return;
        if (!MaterialTouch.WasMoved && !MaterialTouch.PressedLeftButton) return;
        int tabIndex = TabIndexAt(mouse, layout);
        if (tabIndex >= 0) {
            ModTab tab = tabs[tabIndex];
            MaterialRect tabRect = layout.Tab(FilteredTabIndices().IndexOf(tabIndex), tabScroll.Offset);
            if (MaterialTouch.PressedLeftButton && tab.Pinnable && TabPinRect(tabRect).Contains(mouse)) {
                ToggleTabPin(tab);
            } else if (MaterialTouch.PressedLeftButton && tabIndex != selectedTab) {
                SelectTab(tabIndex);
            }
            return;
        }

        RowPlacement? placement = RowAt(mouse, layout);
        if (placement is null || !placement.Value.Item.Hoverable) return;
        SelectItem(placement.Value.Item);
        if (!MaterialTouch.PressedLeftButton) return;
        TextMenu.Item item = placement.Value.Item;
        if (CanFavorite(item) && FavoriteRect(PrimaryRect(placement.Value)).Contains(mouse)) {
            ToggleFavorite(item);
            motion.Pulse(FavoriteKey(item), mouse);
            return;
        }
        if (item.Disabled) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        MaterialRect rect = placement.Value.Rect;
        if (TryGetOption(item, out OptionSnapshot option)) {
            if (option.Options.Count == 2) {
                SetOptionIndex(item, option.Index == 0 ? 1 : 0);
            } else if (CanUseDropdown(option)) {
                OpenDropdown(item, option);
            } else if (SliderControlArea(rect).Contains(mouse)) {
                SetOptionFromMouse(item, option, SliderControlRect(rect), mouse.X);
            }
        } else if (TryGetIntSlider(item, out IntSliderSnapshot slider)) {
            if (SliderControlArea(rect).Contains(mouse))
                SetIntSliderFromMouse(item, slider, SliderControlRect(rect), mouse.X);
        } else if (IsCompositeMenu(item)) {
            OpenCompositePopup(item);
        } else {
            ActivateItem(item);
        }
        motion.Pulse(ItemKey(item), mouse);
    }

    private static void ActivateItem(TextMenu.Item item) {
        item.ConfirmPressed();
        item.OnPressed?.Invoke();
    }

    private void UpdateDropdown(ModOptionsLayout layout) {
        TextMenu.Item? item = dropdownItem;
        if (item is null || !item.Visible || !TryGetOption(item, out OptionSnapshot option)
            || !CanUseDropdown(option)) {
            CloseDropdown(playSound: false);
            return;
        }
        if (Input.Pause.Pressed && pauseLevel is not null) {
            CloseDropdown(playSound: false);
            BeginClose(CloseDestination.Game);
            return;
        }
        if (Input.MenuCancel.Pressed || Input.ESC.Pressed || MInput.Keyboard.Pressed(Keys.Escape)) {
            CloseDropdown();
            return;
        }
        if (Input.MenuUp.Pressed) {
            MoveDropdown(-1, option.Options.Count);
            return;
        }
        if (Input.MenuDown.Pressed) {
            MoveDropdown(1, option.Options.Count);
            return;
        }
        if (Input.MenuConfirm.Pressed
            || MInput.Keyboard.Pressed(Keys.Enter)
            || MInput.Keyboard.Pressed(Keys.Space)) {
            CommitDropdown(option);
            return;
        }

        MaterialRect dropdown = DropdownRect(layout, item, option.Options.Count);
        int visibleCount = DropdownVisibleCount(option.Options.Count);
        if (MaterialTouch.WheelDelta != 0 && dropdown.Contains(MaterialTouch.Position)) {
            dropdownFirstVisible = Math.Clamp(
                dropdownFirstVisible - Math.Sign(MaterialTouch.WheelDelta),
                0,
                Math.Max(0, option.Options.Count - visibleCount)
            );
            dropdownHighlight = Math.Clamp(dropdownHighlight, dropdownFirstVisible,
                dropdownFirstVisible + visibleCount - 1);
        }
        if (MaterialTouch.WasMoved || MaterialTouch.PressedLeftButton) {
            for (int visibleIndex = 0; visibleIndex < visibleCount; visibleIndex++) {
                MaterialRect row = DropdownItemRect(dropdown, visibleIndex);
                if (!row.Contains(MaterialTouch.Position)) continue;
                dropdownHighlight = dropdownFirstVisible + visibleIndex;
                if (MaterialTouch.PressedLeftButton) CommitDropdown(option);
                return;
            }
        }
        if (MaterialTouch.PressedLeftButton) CloseDropdown();
    }

    private void OpenDropdown(TextMenu.Item item, OptionSnapshot option) {
        dropdownItem = item;
        dropdownHighlight = Math.Clamp(option.Index, 0, option.Options.Count - 1);
        dropdownFirstVisible = 0;
        EnsureDropdownHighlightVisible(option.Options.Count);
        Audio.Play("event:/ui/main/button_select");
    }

    private void MoveDropdown(int direction, int optionCount) {
        dropdownHighlight = (dropdownHighlight + Math.Sign(direction) + optionCount) % optionCount;
        EnsureDropdownHighlightVisible(optionCount);
        Audio.Play(direction < 0 ? "event:/ui/main/rollover_up" : "event:/ui/main/rollover_down");
    }

    private void EnsureDropdownHighlightVisible(int optionCount) {
        int visibleCount = DropdownVisibleCount(optionCount);
        if (dropdownHighlight < dropdownFirstVisible) dropdownFirstVisible = dropdownHighlight;
        else if (dropdownHighlight >= dropdownFirstVisible + visibleCount)
            dropdownFirstVisible = dropdownHighlight - visibleCount + 1;
        dropdownFirstVisible = Math.Clamp(dropdownFirstVisible, 0,
            Math.Max(0, optionCount - visibleCount));
    }

    private void CommitDropdown(OptionSnapshot option) {
        TextMenu.Item? item = dropdownItem;
        if (item is null) return;
        SetOptionIndex(item, Math.Clamp(dropdownHighlight, 0, option.Options.Count - 1));
        dropdownItem = null;
    }

    private void CloseDropdown(bool playSound = true) {
        if (dropdownItem is null) return;
        dropdownItem = null;
        if (playSound) Audio.Play("event:/ui/main/button_back");
    }

    private void UpdateCompositePopup(ModOptionsLayout layout) {
        TextMenu.Item? item = compositePopupItem;
        if (menu is null || item is null || !item.Visible || !IsCompositeMenu(item)
            || !IsExpandedComposite(item)) {
            ReleaseCompositePopup();
            return;
        }

        if (!Input.MenuConfirm.Pressed
            && (Input.MenuCancel.Pressed || Input.ESC.Pressed
                || MInput.Keyboard.Pressed(Keys.Escape))) {
            ConsumeCompositePopupCancelInput();
            CloseCompositePopup();
            return;
        }

        MaterialRect popup = CompositePopupRect(layout);
        MaterialRect body = CompositePopupBody(layout);
        MaterialRect close = CompositePopupCloseRect(layout);
        Vector2 mouse = MaterialTouch.Position;
        if (MaterialTouch.WheelDelta != 0 && body.Contains(mouse)) {
            compositePopupScroll.Scroll(-Math.Sign(MaterialTouch.WheelDelta) * 220f,
                MaxCompositePopupScroll(layout));
        }
        if (MaterialTouch.PressedLeftButton && (close.Contains(mouse) || !popup.Contains(mouse))) {
            CloseCompositePopup();
            return;
        }

        menu.Update();
        if (!IsExpandedComposite(item)) {
            ReleaseCompositePopup();
            return;
        }
        EnsureCompositePopupSelectionVisible(layout, item);
    }

    private void OpenCompositePopup(TextMenu.Item item) {
        if (menu is null || item.Disabled || !IsCompositeMenu(item)) return;
        CloseDropdown(playSound: false);
        if (compositePopupItem != item) CloseCompositePopup(playSound: false);
        if (!IsExpandedComposite(item)) {
            item.ConfirmPressed();
            item.OnPressed?.Invoke();
        }
        if (IsExpandedComposite(item)) AdoptCompositePopup(item);
    }

    private void AdoptCompositePopup(TextMenu.Item item) {
        compositePopupItem = item;
        compositePopupScroll.Reset();
        if (menu is not null) menu.Focused = false;
        EnsureCompositePopupSelectionVisible(ModOptionsLayout.Create(0f), item);
    }

    private void CloseCompositePopup(bool playSound = true) {
        TextMenu.Item? item = compositePopupItem;
        if (item is null) return;
        if (item is TextMenuExt.SubMenu submenu) {
            submenu.Current?.OnLeave?.Invoke();
            submenu.Focused = false;
            submenu.ItemsVisible = false;
            if (menu is not null) {
                menu.AutoScroll = submenu.AutoScroll;
                menu.Focused = true;
            }
        } else if (item is TextMenuExt.OptionSubMenu optionSubMenu) {
            optionSubMenu.Current?.OnLeave?.Invoke();
            optionSubMenu.Focused = false;
            if (menu is not null) menu.Focused = true;
        }
        ReleaseCompositePopup();
        if (playSound) Audio.Play("event:/ui/main/button_back");
    }

    private void ReleaseCompositePopup() {
        compositePopupItem = null;
        compositePopupScroll.Reset();
        if (menu is not null) menu.Focused = searchTarget == SearchTarget.None;
    }

    private void EnsureCompositePopupSelectionVisible(ModOptionsLayout layout, TextMenu.Item item) {
        if (!TryGetCompositeContents(item, out float titleHeight, out float spacing,
                out List<TextMenu.Item> items, out TextMenu.Item? current) || current is null) return;
        float top = titleHeight + spacing;
        foreach (TextMenu.Item child in items) {
            if (!child.Visible) continue;
            float bottom = top + child.Height();
            if (child == current) {
                MaterialRect body = CompositePopupBody(layout);
                compositePopupScroll.EnsureVisible(top - 14f, bottom + 14f, body.Height,
                    MaxCompositePopupScroll(layout));
                return;
            }
            top = bottom + spacing;
        }
    }

    private void SelectItem(TextMenu.Item item) {
        if (menu is null || menu.Current == item) return;
        menu.Current?.OnLeave?.Invoke();
        menu.Current = item;
        item.OnEnter?.Invoke();
        item.SelectWiggler?.Start();
    }

    private void EnsureSelectionVisible(ModOptionsLayout layout) {
        if (menu is null || menu.Selection < 0) return;
        float top = 0f;
        foreach (RowEntry row in VisibleRows()) {
            if (row.Item == menu.Current) {
                float height = RowHeight(row, layout.Rows.Width);
                rowScroll.EnsureVisible(top, top + height, layout.Rows.Height, MaxRowScroll(layout));
                return;
            }
            top += RowHeight(row, layout.Rows.Width) + RowGap;
        }
    }

    private void EnsureTabVisible(ModOptionsLayout layout) {
        List<int> visible = FilteredTabIndices();
        int position = visible.IndexOf(selectedTab);
        if (position < 0) return;
        float top = position * (TabHeight + TabGap);
        tabScroll.EnsureVisible(top, top + TabHeight, layout.NavigationItems.Height, MaxTabScroll(layout));
    }

    private void UpdateInteractions(ModOptionsLayout layout) {
        List<MaterialInteractionTarget> targets = [
            new MaterialInteractionTarget("mod-options.search.tabs", layout.TabSearch,
                Focused: searchTarget == SearchTarget.Tabs, TouchKind: "text", TouchText: tabSearchText,
                TouchMaxLength: SearchTextLimit, TouchTextChanged: value => TouchSearch(SearchTarget.Tabs, value)),
            new MaterialInteractionTarget("mod-options.search.settings", layout.SettingSearch,
                Focused: searchTarget == SearchTarget.Settings, TouchKind: "text", TouchText: settingSearchText,
                TouchMaxLength: SearchTextLimit, TouchTextChanged: value => TouchSearch(SearchTarget.Settings, value))
        ];
        // Modal popups own their entire input surface. Their existing hit testing
        // handles taps/scrolls; do not expose text fields underneath the popup.
        if (dropdownItem is not null || compositePopupItem is not null) {
            MaterialTouch.Publish(this, [], TouchContext);
            motion.Update([]);
            return;
        }
        List<int> visibleTabs = FilteredTabIndices();
        for (int position = 0; position < visibleTabs.Count; position++) {
            int index = visibleTabs[position];
            MaterialRect rect = layout.Tab(position, tabScroll.Offset);
            if (rect.Bottom < layout.NavigationItems.Y || rect.Y > layout.NavigationItems.Bottom) continue;
            targets.Add(new MaterialInteractionTarget($"mod-options.tab.{tabs[index].Id}", rect,
                Focused: index == selectedTab));
            if (tabs[index].Pinnable) {
                targets.Add(new MaterialInteractionTarget(TabPinKey(tabs[index]), TabPinRect(rect),
                    Focused: IsPinned(tabs[index])));
            }
        }
        if (menu is not null && tabs.Count > 0) {
            foreach (RowPlacement placement in RowPlacements(layout)) {
                if (!placement.Item.Hoverable) continue;
                if (placement.Rect.Bottom < layout.Rows.Y || placement.Rect.Y > layout.Rows.Bottom) continue;
                targets.Add(new MaterialInteractionTarget(ItemKey(placement.Item), placement.Rect,
                    Focused: menu.Current == placement.Item));
                if (placement.Item is TextMenu.Slider) {
                    targets.Add(new MaterialInteractionTarget(ItemKey(placement.Item) + ".touch-slider",
                        SliderControlRect(PrimaryRect(placement)), TouchKind: "slider"));
                }
                if (CanFavorite(placement.Item)) {
                    targets.Add(new MaterialInteractionTarget(FavoriteKey(placement.Item),
                        FavoriteRect(PrimaryRect(placement)), Focused: IsFavorite(placement.Item)));
                }
            }
        }
        MaterialTouch.Publish(this, targets, TouchContext);
        motion.Update(targets);
    }

    private void TouchSearch(SearchTarget target, string value) {
        SetSearchTarget(target);
        if (target == SearchTarget.Tabs) tabSearchText = value;
        else settingSearchText = value;
        SearchChanged();
        SetSearchTarget(SearchTarget.None);
    }

    private void RenderTabs(ModOptionsLayout layout, MaterialPalette palette, float alpha) {
        MaterialSplitPageChrome.RenderNavigationSurface(layout.Navigation, palette, alpha);
        RenderSearchBox(layout.TabSearch, tabSearchText, searchTarget == SearchTarget.Tabs,
            UiText("microblocks_qol_modoptions_search_tabs", "搜索模组…"), "mod-options.search.tabs",
            palette, alpha);
        MaterialUiKit.Text(UiText("microblocks_qol_modoptions_mods", "模组与分类"),
            new Vector2(layout.NavigationItems.X + 8f, layout.Navigation.Y + 91f),
            new Vector2(0f, 0.5f), MaterialTextRole.Label, palette.Primary, alpha,
            scaleOverride: 0.26f);

        tabViewport.Render(layout.NavigationItems, () => {
            List<int> visibleTabs = FilteredTabIndices();
            for (int position = 0; position < visibleTabs.Count; position++) {
                int index = visibleTabs[position];
                ModTab tab = tabs[index];
                MaterialRect rect = layout.Tab(position, tabScroll.Offset);
                if (rect.Bottom < layout.NavigationItems.Y || rect.Y > layout.NavigationItems.Bottom) continue;
                bool selected = index == selectedTab;
                TabVisual visual = TabVisualFor(tab, palette);
                MaterialSplitPageChrome.RenderTabLead(motion, $"mod-options.tab.{tab.Id}", rect,
                    visual.Icon, selected, palette.Primary, palette, alpha);
                float titleWidth = rect.Width - (tab.Pinnable ? 122f : 78f);
                string title = MaterialTextUtil.Ellipsize(tab.Title, titleWidth, 0.29f, UiFontWeight.Bold);
                float titleY = tab.Version.Length == 0 ? rect.Center.Y : rect.Center.Y - 9f;
                MaterialUiKit.Text(title, new Vector2(rect.X + 58f, titleY), new Vector2(0f, 0.5f),
                    MaterialTextRole.Label, selected ? palette.OnSurface : palette.OnSurfaceVariant,
                    alpha, scaleOverride: 0.29f);
                if (tab.Version.Length > 0) {
                    string version = MaterialTextUtil.Ellipsize(tab.Version, titleWidth, 0.21f);
                    MaterialUiKit.Text(version, new Vector2(rect.X + 58f, rect.Center.Y + 14f),
                        new Vector2(0f, 0.5f), MaterialTextRole.Caption,
                        palette.OnSurfaceVariant * 0.78f, alpha, scaleOverride: 0.21f);
                }
                if (tab.Pinnable) {
                    MaterialRect pin = TabPinRect(rect);
                    motion.RenderStateLayer(TabPinKey(tab), pin, pin.Width / 2f,
                        visual.Accent, alpha);
                    MaterialUiKit.Icon("push_pin", pin.Center, 19f,
                        visual.Accent, alpha, filled: IsPinned(tab));
                }
            }
            if (visibleTabs.Count == 0) {
                MaterialUiKit.Text(UiText("microblocks_qol_modoptions_no_tabs", "没有匹配的模组"),
                    layout.NavigationItems.Center, new Vector2(0.5f), MaterialTextRole.Caption,
                    palette.OnSurfaceVariant, alpha, scaleOverride: 0.29f);
            }
        });
    }

    private void RenderRows(ModOptionsLayout layout, MaterialPalette palette, float alpha) {
        ModTab tab = tabs[selectedTab];
        TabVisual visual = TabVisualFor(tab, palette);
        MaterialSplitPageChrome.RenderContentSurface(layout.Content, palette, alpha);
        float titleWidth = Math.Max(140f, layout.SettingSearch.X - layout.ContentHeader.X - 210f);
        MaterialSplitPageChrome.RenderContentHeading(layout.ContentHeader, visual.Icon, tab.Title,
            titleWidth, visual.Accent, palette, alpha);
        if (tab.Version.Length > 0) {
            MaterialUiKit.Chip(tab.Version, new Vector2(layout.SettingSearch.X - 16f, layout.ContentHeader.Y + 13f),
                palette, selected: false, alpha);
        }
        RenderSearchBox(layout.SettingSearch, settingSearchText, searchTarget == SearchTarget.Settings,
            UiText("microblocks_qol_modoptions_search_settings", "搜索当前设置…"),
            "mod-options.search.settings", palette, alpha);

        menu!.Alpha = alpha;
        menu.HighlightColor = visual.Accent;
        menu.MinWidth = Math.Max(600f, layout.Rows.Width - 54f);
        rowViewport.Render(layout.Rows, () => {
            bool renderedAny = false;
            foreach (RowPlacement placement in RowPlacements(layout)) {
                TextMenu.Item item = placement.Item;
                MaterialRect rect = placement.Rect;
                if (rect.Bottom < layout.Rows.Y || rect.Y > layout.Rows.Bottom) continue;
                renderedAny = true;
                bool selected = menu.Current == item && item.Hoverable;
                if (item is TextMenu.SubHeader header) {
                    string title = MaterialTextUtil.Ellipsize(header.Title, rect.Width - 16f,
                        0.34f, UiFontWeight.Bold);
                    MaterialUiKit.Text(title, new Vector2(rect.X + 8f, rect.Center.Y),
                        new Vector2(0f, 0.5f), MaterialTextRole.Section,
                        palette.Primary, alpha, scaleOverride: 0.34f);
                    continue;
                }
                Color rowFill = selected
                    ? Color.Lerp(palette.SurfaceHighest, visual.Accent, 0.10f)
                    : palette.SurfaceHigh;
                MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, 18f,
                    rowFill * ((selected ? 0.96f : 0.48f) * alpha));
                MaterialUi.RoundedOutline(rect.X, rect.Y, rect.Width, rect.Height, 18f, 1f,
                    (selected ? visual.Accent : palette.Outline) * ((selected ? 0.34f : 0.12f) * alpha));
                motion.RenderStateLayer(ItemKey(item), rect, 18f, visual.Accent, alpha);
                MaterialRect primary = PrimaryRect(placement);
                bool hasDescription = placement.Description is not null;
                if (CanFavorite(item)) {
                    MaterialRect favorite = FavoriteRect(primary);
                    motion.RenderStateLayer(FavoriteKey(item), favorite, favorite.Width / 2f,
                        visual.Accent, alpha);
                    MaterialUiKit.Icon("star", favorite.Center, 21f, visual.Accent, alpha,
                        filled: IsFavorite(item));
                }
                if (!RenderStandardItem(item, primary, palette, alpha, selected, hasDescription)) {
                    item.Render(new Vector2(primary.X + 60f,
                        hasDescription ? primary.Y + DescribedTitleHeight / 2f : primary.Center.Y), selected);
                }
                if (placement.Description is { } description) {
                    RenderDescription(description.Title,
                        new MaterialRect(rect.X, rect.Y + DescribedTitleHeight,
                            rect.Width, rect.Bottom - rect.Y - DescribedTitleHeight),
                        palette, alpha, embedded: true);
                }
            }
            if (!renderedAny) {
                string empty = FilteredTabIndices().Count == 0
                    ? UiText("microblocks_qol_modoptions_no_tabs", "没有匹配的模组")
                    : string.IsNullOrWhiteSpace(settingSearchText)
                    ? UiText("microblocks_qol_modoptions_empty", "没有可用设置")
                    : UiText("microblocks_qol_modoptions_no_settings", "没有匹配的设置");
                MaterialUiKit.Text(empty,
                    layout.Rows.Center, new Vector2(0.5f), MaterialTextRole.Body,
                    palette.OnSurfaceVariant, alpha, scaleOverride: 0.36f);
            }
        });

        float maximum = MaxRowScroll(layout);
        if (maximum > 0f) {
            float ratio = layout.Rows.Height / (layout.Rows.Height + maximum);
            float thumbHeight = Math.Max(52f, layout.Rows.Height * ratio);
            float travel = layout.Rows.Height - thumbHeight;
            float y = layout.Rows.Y + rowScroll.Offset / maximum * travel;
            MaterialUi.RoundedRect(layout.Rows.Right + 8f, layout.Rows.Y, 5f, layout.Rows.Height, 2.5f,
                palette.Outline * (0.20f * alpha));
            MaterialUi.RoundedRect(layout.Rows.Right + 8f, y, 5f, thumbHeight, 2.5f,
                palette.Primary * (0.72f * alpha));
        }
    }

    private bool RenderStandardItem(TextMenu.Item item, MaterialRect rect, MaterialPalette palette,
        float alpha, bool selected, bool hasDescription) {
        bool enabled = !item.Disabled;
        Color labelColor = enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.5f;
        if (TryGetOption(item, out OptionSnapshot option)) {
            MaterialRect control = option.Options.Count == 2
                ? SwitchControlArea(rect)
                : CanUseDropdown(option)
                    ? DropdownControlRect(rect)
                    : SliderControlArea(rect);
            RenderItemLabel(option.Label, rect, control.X - rect.X - 62f, labelColor, alpha,
                hasDescription);
            if (option.Options.Count == 2) {
                RenderSwitch(option, rect, palette, alpha, enabled);
            } else if (CanUseDropdown(option)) {
                RenderOptionControl(item, option, rect, palette, alpha, enabled);
            } else {
                RenderOptionSlider(option, rect, palette, alpha, enabled);
            }
            return true;
        }
        if (TryGetIntSlider(item, out IntSliderSnapshot slider)) {
            MaterialRect control = SliderControlArea(rect);
            RenderItemLabel(slider.Label, rect, control.X - rect.X - 62f, labelColor, alpha,
                hasDescription);
            RenderIntSlider(slider, rect, palette, alpha, enabled);
            return true;
        }
        if (item is TextMenu.Button button) {
            MaterialRect action = ActionControlRect(rect);
            RenderItemLabel(button.Label, rect, action.X - rect.X - 62f, labelColor, alpha,
                hasDescription);
            RenderActionControl(action, UiText("microblocks_qol_modoptions_open", "打开"),
                palette, alpha, enabled);
            return true;
        }
        if (item is TextMenu.Setting setting) {
            MaterialRect action = ActionControlRect(rect);
            RenderItemLabel(setting.Label, rect, action.X - rect.X - 62f, labelColor, alpha,
                hasDescription);
            string value = setting.Values.Count == 0
                ? UiText("microblocks_qol_modoptions_unbound", "未绑定")
                : string.Format(UiText("microblocks_qol_modoptions_bound", "已绑定 {0} 项"), setting.Values.Count);
            RenderActionControl(action, value, palette, alpha, enabled);
            return true;
        }
        if (TryGetLabel(item, out string label)) {
            MaterialRect action = ActionControlRect(rect);
            RenderItemLabel(label, rect, action.X - rect.X - 62f, labelColor, alpha,
                hasDescription);
            RenderActionControl(action, compositePopupItem == item
                    ? UiText("microblocks_qol_modoptions_active", "弹窗中")
                    : UiText("microblocks_qol_modoptions_open", "打开"),
                palette, alpha, enabled);
            return true;
        }
        return false;
    }

    private static void RenderItemLabel(string label, MaterialRect rect, float maximumWidth,
        Color color, float alpha, bool hasDescription) {
        string shown = MaterialTextUtil.Ellipsize(label, Math.Max(80f, maximumWidth),
            0.34f, UiFontWeight.Bold);
        float y = hasDescription ? rect.Y + DescribedTitleHeight / 2f : rect.Center.Y;
        MaterialUiKit.Text(shown,
            new Vector2(rect.X + RowTextOffset, y), new Vector2(0f, 0.5f),
            MaterialTextRole.Label, color, alpha, scaleOverride: 0.34f);
    }

    private static void RenderSwitch(OptionSnapshot option, MaterialRect rect, MaterialPalette palette,
        float alpha, bool enabled) {
        bool on = option.Index > 0;
        MaterialRect track = SwitchRect(rect);
        Color trackColor = on ? palette.Primary : palette.Outline * 0.52f;
        MaterialUi.RoundedRect(track.X, track.Y, track.Width, track.Height, track.Height / 2f,
            trackColor * (alpha * (enabled ? 1f : 0.45f)));
        float knobX = on ? track.Right - 15f : track.X + 15f;
        MaterialUi.Circle(new Vector2(knobX, track.Center.Y), 11f,
            (on ? palette.OnPrimary : palette.OnSurfaceVariant) * alpha);
        string current = option.Options.Count == 0
            ? ""
            : option.Options[Math.Clamp(option.Index, 0, option.Options.Count - 1)];
        MaterialUiKit.Text(MaterialTextUtil.Ellipsize(current, 250f, 0.29f, UiFontWeight.Bold),
            new Vector2(track.X - 18f, track.Center.Y), new Vector2(1f, 0.5f),
            MaterialTextRole.Label, enabled ? palette.OnSurfaceVariant : palette.OnSurfaceVariant * 0.5f,
            alpha, scaleOverride: 0.29f);
    }

    private void RenderOptionControl(TextMenu.Item item, OptionSnapshot option, MaterialRect rect,
        MaterialPalette palette, float alpha, bool enabled) {
        MaterialRect control = DropdownControlRect(rect);
        bool open = dropdownItem == item;
        MaterialUi.RoundedRect(control.X, control.Y, control.Width, control.Height, 16f,
            palette.Surface * (0.76f * alpha));
        MaterialUi.RoundedOutline(control.X, control.Y, control.Width, control.Height, 16f,
            open ? 2f : 1f, (open ? palette.Primary : palette.Outline) * (alpha * (open ? 1f : 0.52f)));
        string current = option.Options.Count == 0
            ? ""
            : option.Options[Math.Clamp(option.Index, 0, option.Options.Count - 1)];
        MaterialUiKit.Text(MaterialTextUtil.Ellipsize(current, control.Width - 52f, 0.28f, UiFontWeight.Bold),
            new Vector2(control.X + 15f, control.Center.Y), new Vector2(0f, 0.5f),
            MaterialTextRole.Label, enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.5f,
            alpha, scaleOverride: 0.28f);
        MaterialUiKit.Text(open ? "▲" : "▼", new Vector2(control.Right - 16f, control.Center.Y),
            new Vector2(1f, 0.5f), MaterialTextRole.Label, palette.OnSurfaceVariant,
            alpha, scaleOverride: 0.20f);
    }

    private static void RenderOptionSlider(OptionSnapshot option, MaterialRect rect,
        MaterialPalette palette, float alpha, bool enabled) {
        MaterialRect track = SliderControlRect(rect);
        float amount = option.Options.Count <= 1 ? 0f : option.Index / (float)(option.Options.Count - 1);
        RenderSliderTrack(track, amount, palette, alpha, enabled);
        string value = option.Options.Count == 0
            ? ""
            : option.Options[Math.Clamp(option.Index, 0, option.Options.Count - 1)];
        MaterialUiKit.Text(MaterialTextUtil.Ellipsize(value, 390f, 0.27f, UiFontWeight.Bold),
            new Vector2(track.Center.X, track.Y - 12f), new Vector2(0.5f, 1f),
            MaterialTextRole.Label, palette.OnSurfaceVariant, alpha, scaleOverride: 0.27f);
    }

    private static void RenderIntSlider(IntSliderSnapshot slider, MaterialRect rect,
        MaterialPalette palette, float alpha, bool enabled) {
        MaterialRect track = SliderControlRect(rect);
        float amount = slider.Maximum == slider.Minimum
            ? 0f
            : (slider.Value - slider.Minimum) / (float)(slider.Maximum - slider.Minimum);
        RenderSliderTrack(track, amount, palette, alpha, enabled);
        MaterialUiKit.Text(slider.Value.ToString(), new Vector2(track.Center.X, track.Y - 12f),
            new Vector2(0.5f, 1f), MaterialTextRole.Label, palette.OnSurfaceVariant,
            alpha, scaleOverride: 0.27f);
    }

    private static void RenderSliderTrack(MaterialRect track, float amount, MaterialPalette palette,
        float alpha, bool enabled) {
        amount = Math.Clamp(amount, 0f, 1f);
        MaterialUi.RoundedRect(track.X, track.Y, track.Width, track.Height, track.Height / 2f,
            palette.Outline * (0.34f * alpha));
        float fill = Math.Max(track.Height, track.Width * amount);
        MaterialUi.RoundedRect(track.X, track.Y, fill, track.Height, track.Height / 2f,
            palette.Primary * (alpha * (enabled ? 1f : 0.45f)));
        MaterialUi.Circle(new Vector2(track.X + track.Width * amount, track.Center.Y), 10f,
            palette.Primary * (alpha * (enabled ? 1f : 0.45f)));
    }

    private static void RenderActionControl(MaterialRect control, string value, MaterialPalette palette,
        float alpha, bool enabled) {
        MaterialUi.RoundedRect(control.X, control.Y, control.Width, control.Height, 16f,
            palette.Primary * (0.26f * alpha));
        MaterialUiKit.Text(MaterialTextUtil.Ellipsize(value, control.Width - 48f, 0.27f, UiFontWeight.Bold),
            new Vector2(control.X + 14f, control.Center.Y), new Vector2(0f, 0.5f),
            MaterialTextRole.Label, enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.5f,
            alpha, scaleOverride: 0.27f);
        MaterialUiKit.Icon("arrow_forward", new Vector2(control.Right - 18f, control.Center.Y), 18f,
            enabled ? palette.OnSurface : palette.OnSurfaceVariant * 0.5f, alpha);
    }

    private static void RenderDescription(string text, MaterialRect rect, MaterialPalette palette, float alpha,
        bool embedded = false) {
        float inset = embedded ? 24f : 12f;
        if (!embedded) MaterialUi.RoundedRect(rect.X + inset, rect.Y, rect.Width - inset * 2f, rect.Height, 18f,
            palette.Primary * (0.13f * alpha));
        float contentY = rect.Y + DescriptionTopPadding;
        float textX = embedded ? rect.X + RowTextOffset : rect.X + inset + 30f;
        if (!embedded) {
            MaterialUiKit.Icon("info", new Vector2(rect.X + inset + 12f, contentY + 12f), 17f,
                palette.Primary, alpha, filled: true);
        }
        List<string> lines = DescriptionLines(text, rect.Width, embedded);
        float y = contentY;
        foreach (string line in lines) {
            MaterialUiKit.Text(line, new Vector2(textX, y), Vector2.Zero,
                MaterialTextRole.Caption, palette.OnSurfaceVariant, alpha, scaleOverride: 0.25f);
            y += DescriptionLineHeight;
        }
    }

    private void RenderSearchBox(MaterialRect rect, string value, bool focused, string placeholder,
        string interactionKey, MaterialPalette palette, float alpha) {
        MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, rect.Height / 2f,
            (focused ? palette.SurfaceHighest : palette.SurfaceHigh) * (0.96f * alpha));
        motion.RenderStateLayer(interactionKey, rect, rect.Height / 2f, palette.Primary, alpha);
        if (focused) {
            MaterialUi.RoundedOutline(rect.X, rect.Y, rect.Width, rect.Height, rect.Height / 2f,
                2f, palette.Primary * (alpha * 0.88f));
        }
        MaterialUiKit.Icon("search", new Vector2(rect.X + 27f, rect.Center.Y), 22f,
            focused ? palette.Primary : palette.OnSurfaceVariant, alpha);
        string shown = value + (focused ? imeText : "");
        string text = shown.Length == 0 ? placeholder : shown;
        Color color = shown.Length == 0 ? palette.OnSurfaceVariant * 0.64f : palette.OnSurface;
        Vector2 textPosition = new(rect.X + 54f, rect.Center.Y);
        string visibleText = MaterialTextUtil.Ellipsize(text, rect.Width - 74f, 0.28f);
        SystemTtfFont.DrawVisual(visibleText, textPosition, new Vector2(0f, 0.5f), 0.28f, color * alpha);
        if (focused && Scene.BetweenInterval(0.5f)) {
            string visibleInput = MaterialTextUtil.Ellipsize(shown, rect.Width - 74f, 0.28f);
            float caretX = textPosition.X + SystemTtfFont.MeasureVisible(visibleInput, 0.28f).X + 2f;
            MaterialUi.Line(new Vector2(caretX, rect.Y + 9f), new Vector2(caretX, rect.Bottom - 9f),
                2f, palette.Primary * alpha);
        }
        if (!focused) return;
        float xScale = Engine.ViewWidth / ScreenWidth;
        float yScale = Engine.ViewHeight / ScreenHeight;
        TextInputEXT.SetInputRectangle(new Rectangle(
            (int)(rect.X * xScale),
            (int)(rect.Y * yScale),
            Math.Max(1, (int)(rect.Width * xScale)),
            Math.Max(1, (int)(rect.Height * yScale))
        ));
    }

    private void RenderDropdown(ModOptionsLayout layout, MaterialPalette palette, float alpha) {
        TextMenu.Item? item = dropdownItem;
        if (item is null || !TryGetOption(item, out OptionSnapshot option)) return;
        MaterialRect dropdown = DropdownRect(layout, item, option.Options.Count);
        MaterialUi.RoundedRect(dropdown.X, dropdown.Y + 5f, dropdown.Width, dropdown.Height, 18f,
            Color.Black * (0.24f * alpha));
        MaterialUi.RoundedRect(dropdown.X, dropdown.Y, dropdown.Width, dropdown.Height, 18f,
            palette.SurfaceHighest * alpha);
        MaterialUi.RoundedOutline(dropdown.X, dropdown.Y, dropdown.Width, dropdown.Height, 18f,
            1f, palette.Outline * (0.62f * alpha));

        int visibleCount = DropdownVisibleCount(option.Options.Count);
        for (int visibleIndex = 0; visibleIndex < visibleCount; visibleIndex++) {
            int optionIndex = dropdownFirstVisible + visibleIndex;
            MaterialRect row = DropdownItemRect(dropdown, visibleIndex);
            bool highlighted = optionIndex == dropdownHighlight;
            bool current = optionIndex == option.Index;
            if (highlighted) {
                MaterialUi.RoundedRect(row.X, row.Y, row.Width, row.Height, 13f,
                    palette.Primary * (0.90f * alpha));
            }
            MaterialUiKit.Text(MaterialTextUtil.Ellipsize(option.Options[optionIndex], row.Width - 52f,
                    0.28f, UiFontWeight.Bold),
                new Vector2(row.X + 14f, row.Center.Y), new Vector2(0f, 0.5f),
                MaterialTextRole.Label, highlighted ? palette.OnPrimary : palette.OnSurface,
                alpha, scaleOverride: 0.28f);
            if (current) {
                MaterialUiKit.Icon("check", new Vector2(row.Right - 20f, row.Center.Y), 19f,
                    highlighted ? palette.OnPrimary : palette.Primary, alpha, filled: true);
            }
        }

        if (option.Options.Count > visibleCount) {
            float trackHeight = dropdown.Height - 16f;
            float thumbHeight = Math.Max(28f, trackHeight * visibleCount / option.Options.Count);
            float maximum = option.Options.Count - visibleCount;
            float thumbY = dropdown.Y + 8f
                + (trackHeight - thumbHeight) * dropdownFirstVisible / maximum;
            MaterialUi.RoundedRect(dropdown.Right - 5f, dropdown.Y + 8f, 3f, trackHeight, 1.5f,
                palette.Outline * (0.28f * alpha));
            MaterialUi.RoundedRect(dropdown.Right - 5f, thumbY, 3f, thumbHeight, 1.5f,
                palette.Primary * (0.84f * alpha));
        }
    }

    private void RenderCompositePopup(ModOptionsLayout layout, MaterialPalette palette, float alpha) {
        TextMenu.Item? item = compositePopupItem;
        if (item is null || menu is null) return;
        MaterialRect popup = CompositePopupRect(layout);
        MaterialRect body = CompositePopupBody(layout);
        MaterialRect close = CompositePopupCloseRect(layout);

        Draw.Rect(0f, 0f, ScreenWidth, ScreenHeight, Color.Black * (0.58f * alpha));
        MaterialUi.RoundedRect(popup.X, popup.Y + 10f, popup.Width, popup.Height, 32f,
            Color.Black * (0.30f * alpha));
        MaterialUi.RoundedRect(popup.X, popup.Y, popup.Width, popup.Height, 32f,
            palette.SurfaceHigh * alpha);
        MaterialUi.RoundedOutline(popup.X, popup.Y, popup.Width, popup.Height, 32f, 2f,
            palette.Outline * (0.72f * alpha));

        MaterialUiKit.Icon("tune", new Vector2(popup.X + 48f, popup.Y + 43f), 25f,
            palette.Primary, alpha, filled: true);
        string title = TryGetLabel(item, out string label)
            ? MaterialTextUtil.Ellipsize(label, popup.Width - 240f, 0.40f, UiFontWeight.Bold)
            : UiText("microblocks_qol_modoptions_open", "打开");
        MaterialUiKit.Text(title, new Vector2(popup.X + 78f, popup.Y + 43f), new Vector2(0f, 0.5f),
            MaterialTextRole.Title, palette.OnSurface, alpha, scaleOverride: 0.40f);
        MaterialUi.RoundedRect(close.X, close.Y, close.Width, close.Height, close.Height / 2f,
            palette.SurfaceHighest * (0.88f * alpha));
        MaterialUiKit.Icon("close", close.Center, 20f, palette.OnSurfaceVariant, alpha, filled: true);
        MaterialUi.Line(new Vector2(popup.X + 34f, body.Y - 18f),
            new Vector2(popup.Right - 34f, body.Y - 18f), 1f, palette.Outline * (0.42f * alpha));

        compositePopupViewport.Render(body, () => {
            float itemHeight = Math.Max(ActiveFont.LineHeight, item.Height());
            float originalWidth = menu.Width;
            float renderWidth = Math.Min(originalWidth,
                Math.Max(320f, body.Width - CompositePopupHorizontalPadding * 2f));
            bool widthOverridden = TrySetTextMenuRenderWidth(menu, renderWidth);
            try {
                float actualWidth = widthOverridden ? renderWidth : originalWidth;
                float x = body.X + Math.Max(18f, (body.Width - actualWidth) / 2f);
                float y = body.Y - compositePopupScroll.Offset + itemHeight / 2f;
                item.Render(new Vector2(x, y), highlighted: true);
            } finally {
                if (widthOverridden) TrySetTextMenuRenderWidth(menu, originalWidth);
            }
        });

        float maximum = MaxCompositePopupScroll(layout);
        if (maximum > 0f) {
            float ratio = body.Height / (body.Height + maximum);
            float thumbHeight = Math.Max(46f, body.Height * ratio);
            float travel = body.Height - thumbHeight;
            float y = body.Y + compositePopupScroll.Offset / maximum * travel;
            MaterialUi.RoundedRect(body.Right + 8f, body.Y, 5f, body.Height, 2.5f,
                palette.Outline * (0.24f * alpha));
            MaterialUi.RoundedRect(body.Right + 8f, y, 5f, thumbHeight, 2.5f,
                palette.Primary * (0.82f * alpha));
        }
        MaterialUiKit.Text(UiText("microblocks_qol_modoptions_popup_help", "方向键操作  ·  Esc 返回"),
            new Vector2(popup.Right - 88f, popup.Bottom - 24f), new Vector2(1f, 0.5f),
            MaterialTextRole.Caption, palette.OnSurfaceVariant, alpha, scaleOverride: 0.25f);
    }
    private List<RowPlacement> RowPlacements(ModOptionsLayout layout) {
        List<RowPlacement> placements = [];
        float y = layout.Rows.Y - rowScroll.Offset;
        foreach (RowEntry row in VisibleRows()) {
            float height = RowHeight(row, layout.Rows.Width);
            placements.Add(new RowPlacement(row.Item, row.Description,
                new MaterialRect(layout.Rows.X, y, layout.Rows.Width, height)));
            y += height + RowGap;
        }
        return placements;
    }

    private float MaxRowScroll(ModOptionsLayout layout) {
        if (tabs.Count == 0) return 0f;
        float height = 0f;
        int count = 0;
        foreach (RowEntry row in VisibleRows()) {
            height += RowHeight(row, layout.Rows.Width);
            count++;
        }
        height += Math.Max(0, count - 1) * RowGap;
        return Math.Max(0f, height - layout.Rows.Height);
    }

    private float MaxCompositePopupScroll(ModOptionsLayout layout) {
        if (compositePopupItem is null) return 0f;
        return Math.Max(0f, compositePopupItem.Height() - CompositePopupBody(layout).Height);
    }

    private float MaxTabScroll(ModOptionsLayout layout) {
        int count = FilteredTabIndices().Count;
        return Math.Max(0f, count * (TabHeight + TabGap) - TabGap - layout.NavigationItems.Height);
    }

    private int TabIndexAt(Vector2 point, ModOptionsLayout layout) {
        if (!layout.NavigationItems.Contains(point)) return -1;
        List<int> visible = FilteredTabIndices();
        for (int position = 0; position < visible.Count; position++) {
            if (layout.Tab(position, tabScroll.Offset).Contains(point)) return visible[position];
        }
        return -1;
    }

    private RowPlacement? RowAt(Vector2 point, ModOptionsLayout layout) {
        if (!layout.Rows.Contains(point)) return null;
        foreach (RowPlacement placement in RowPlacements(layout)) {
            if (placement.Rect.Contains(point)) return placement;
        }
        return null;
    }

    private static bool TryGetOption(TextMenu.Item item, out OptionSnapshot snapshot) {
        FieldInfo? labelField = null;
        FieldInfo? indexField = null;
        FieldInfo? previousIndexField = null;
        FieldInfo? valuesField = null;
        FieldInfo? changeField = null;
        for (Type? type = item.GetType(); type is not null; type = type.BaseType) {
            const BindingFlags declaredInstance = BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            labelField ??= type.GetField("Label", declaredInstance);
            indexField ??= type.GetField("Index", declaredInstance);
            previousIndexField ??= type.GetField("PreviousIndex", declaredInstance);
            valuesField ??= type.GetField("Values", declaredInstance);
            changeField ??= type.GetField("OnValueChange", declaredInstance);
        }
        if (labelField is null || indexField is null || valuesField is null
            || indexField.FieldType != typeof(int)
            || valuesField.GetValue(item) is not IEnumerable values) {
            snapshot = default!;
            return false;
        }
        List<string> labels = [];
        List<object> entries = [];
        foreach (object? entry in values) {
            if (entry is null) continue;
            object? label = entry.GetType().GetProperty("Item1")?.GetValue(entry)
                ?? entry.GetType().GetField("Item1")?.GetValue(entry);
            labels.Add(label?.ToString() ?? "");
            entries.Add(entry);
        }
        snapshot = new OptionSnapshot(
            labelField.GetValue(item)?.ToString() ?? "",
            Math.Clamp((int)(indexField.GetValue(item) ?? 0), 0, Math.Max(0, labels.Count - 1)),
            labels,
            entries,
            indexField,
            previousIndexField,
            changeField
        );
        return true;
    }

    private static bool TryGetIntSlider(TextMenu.Item item, out IntSliderSnapshot snapshot) {
        if (item is not TextMenuExt.IntSlider slider) {
            snapshot = default;
            return false;
        }
        Type type = typeof(TextMenuExt.IntSlider);
        FieldInfo? minimumField = type.GetField("min", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? maximumField = type.GetField("max", BindingFlags.Instance | BindingFlags.NonPublic);
        int minimum = (int)(minimumField?.GetValue(slider) ?? slider.Index);
        int maximum = (int)(maximumField?.GetValue(slider) ?? slider.Index);
        snapshot = new IntSliderSnapshot(slider.Label, slider.Index, minimum, maximum);
        return true;
    }

    private static bool TryGetLabel(TextMenu.Item item, out string label) {
        for (Type? type = item.GetType(); type is not null; type = type.BaseType) {
            FieldInfo? field = type.GetField("Label",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field?.GetValue(item) is string value && value.Length > 0) {
                label = value;
                return true;
            }
        }
        label = item.SearchLabel() ?? "";
        return label.Length > 0;
    }

    private static bool IsExpandedComposite(TextMenu.Item item) {
        for (Type? type = item.GetType(); type is not null; type = type.BaseType) {
            FieldInfo? field = type.GetField("Focused",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field?.FieldType == typeof(bool) && field.GetValue(item) is true) return true;
        }
        return false;
    }

    private static bool IsCompositeMenu(TextMenu.Item item) =>
        item is TextMenuExt.SubMenu or TextMenuExt.OptionSubMenu;

    private static void ConsumeCompositePopupCancelInput() {
        Input.MenuCancel.ConsumePress();
        Input.MenuCancel.ConsumeBuffer();
        Input.ESC.ConsumePress();
        Input.ESC.ConsumeBuffer();
    }

    private static bool TrySetTextMenuRenderWidth(TextMenu target, float width) {
        if (textMenuWidthField?.FieldType != typeof(float)) return false;
        textMenuWidthField.SetValue(target, width);
        return true;
    }

    private static bool TryGetCompositeContents(TextMenu.Item item, out float titleHeight,
        out float spacing, out List<TextMenu.Item> items, out TextMenu.Item? current) {
        if (item is TextMenuExt.SubMenu submenu) {
            titleHeight = submenu.TitleHeight;
            spacing = submenu.ItemSpacing;
            items = submenu.Items;
            current = submenu.Current;
            return true;
        }
        if (item is TextMenuExt.OptionSubMenu optionSubMenu
            && optionSubMenu.CurrentMenu is { } currentMenu) {
            titleHeight = optionSubMenu.TitleHeight;
            spacing = optionSubMenu.ItemSpacing;
            items = currentMenu;
            current = optionSubMenu.Current;
            return true;
        }
        titleHeight = 0f;
        spacing = 0f;
        items = [];
        current = null;
        return false;
    }

    private static bool CanUseDropdown(OptionSnapshot option) =>
        option.Options.Count >= 3;

    private static void SetOptionIndex(TextMenu.Item item, int targetIndex) {
        if (!TryGetOption(item, out OptionSnapshot option) || option.Options.Count == 0) return;
        targetIndex = Math.Clamp(targetIndex, 0, option.Options.Count - 1);
        if (targetIndex == option.Index) return;
        option.PreviousIndexField?.SetValue(item, option.Index);
        option.IndexField.SetValue(item, targetIndex);
        object entry = option.Entries[targetIndex];
        object? value = entry.GetType().GetProperty("Item2")?.GetValue(entry)
            ?? entry.GetType().GetField("Item2")?.GetValue(entry);
        if (option.ChangeField?.GetValue(item) is Delegate changed) changed.DynamicInvoke(value);
        item.ValueWiggler?.Start();
        Audio.Play(targetIndex > option.Index
            ? "event:/ui/main/button_toggle_on"
            : "event:/ui/main/button_toggle_off");
    }

    private static void SetOptionFromMouse(TextMenu.Item item, OptionSnapshot option,
        MaterialRect track, float mouseX) {
        if (option.Options.Count == 0) return;
        float amount = Math.Clamp((mouseX - track.X) / Math.Max(1f, track.Width), 0f, 1f);
        SetOptionIndex(item, (int)MathF.Round(amount * (option.Options.Count - 1)));
    }

    private static void SetIntSliderFromMouse(TextMenu.Item item, IntSliderSnapshot slider,
        MaterialRect track, float mouseX) {
        if (item is not TextMenuExt.IntSlider intSlider) return;
        float amount = Math.Clamp((mouseX - track.X) / Math.Max(1f, track.Width), 0f, 1f);
        int value = (int)MathF.Round(MathHelper.Lerp(slider.Minimum, slider.Maximum, amount));
        value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        if (value == intSlider.Index) return;
        intSlider.PreviousIndex = intSlider.Index;
        intSlider.Index = value;
        intSlider.OnValueChange?.Invoke(value);
        intSlider.ValueWiggler?.Start();
        Audio.Play(value > slider.Value
            ? "event:/ui/main/button_toggle_on"
            : "event:/ui/main/button_toggle_off");
    }

    private MaterialRect DropdownRect(ModOptionsLayout layout, TextMenu.Item item, int optionCount) {
        RowPlacement? placement = RowPlacements(layout).FirstOrDefault(row => row.Item == item);
        MaterialRect control = placement is { Item: not null } row
            ? DropdownControlRect(row.Rect)
            : DropdownControlRect(new MaterialRect(layout.Rows.X, layout.Rows.Y, layout.Rows.Width, 78f));
        float height = DropdownVisibleCount(optionCount) * DropdownItemHeight + 12f;
        float y = control.Bottom + 6f;
        if (y + height > layout.Content.Bottom - 12f) y = control.Y - height - 6f;
        y = Math.Clamp(y, layout.Content.Y + 10f, Math.Max(layout.Content.Y + 10f,
            layout.Content.Bottom - height - 10f));
        return new MaterialRect(control.X, y, control.Width, height);
    }

    private static MaterialRect DropdownItemRect(MaterialRect dropdown, int visibleIndex) => new(
        dropdown.X + 6f,
        dropdown.Y + 6f + visibleIndex * DropdownItemHeight,
        dropdown.Width - 12f,
        DropdownItemHeight
    );

    private static int DropdownVisibleCount(int optionCount) =>
        Math.Min(DropdownMaxVisibleItems, optionCount);

    private static MaterialRect SwitchControlArea(MaterialRect rect) => new(
        rect.Right - 360f,
        rect.Y,
        336f,
        rect.Height
    );

    private static MaterialRect SwitchRect(MaterialRect rect) => new(
        rect.Right - 86f,
        rect.Center.Y - 16f,
        62f,
        32f
    );

    private static MaterialRect DropdownControlRect(MaterialRect rect) => new(
        rect.Right - 424f,
        rect.Center.Y - 24f,
        400f,
        48f
    );

    private static MaterialRect SliderControlArea(MaterialRect rect) => new(
        rect.Right - 504f,
        rect.Y,
        480f,
        rect.Height
    );

    private static MaterialRect SliderControlRect(MaterialRect rect) => new(
        rect.Right - 474f,
        rect.Center.Y + 9f,
        430f,
        8f
    );

    private static MaterialRect ActionControlRect(MaterialRect rect) => new(
        rect.Right - 304f,
        rect.Center.Y - 22f,
        280f,
        44f
    );

    private static MaterialRect CompositePopupRect(ModOptionsLayout layout) =>
        layout.Frame.Inset(126f, 54f, 126f, 58f);

    private static MaterialRect CompositePopupBody(ModOptionsLayout layout) =>
        CompositePopupRect(layout).Inset(42f, 92f, 54f, 52f);

    private static MaterialRect CompositePopupCloseRect(ModOptionsLayout layout) {
        MaterialRect popup = CompositePopupRect(layout);
        return new MaterialRect(popup.Right - 70f, popup.Y + 20f, 46f, 46f);
    }

    private static MaterialRect TabPinRect(MaterialRect rect) => new(
        rect.Right - 44f,
        rect.Center.Y - 20f,
        40f,
        40f
    );

    private static MaterialRect FavoriteRect(MaterialRect rect) => new(
        rect.X + 8f,
        rect.Center.Y - 20f,
        40f,
        40f
    );

    private static MaterialRect PrimaryRect(RowPlacement placement) => new(
        placement.Rect.X,
        placement.Rect.Y,
        placement.Rect.Width,
        placement.Rect.Height
    );

    private static string TabPinKey(ModTab tab) => $"mod-options.tab-pin.{tab.Id}";

    private static string FavoriteKey(TextMenu.Item item) =>
        $"mod-options.favorite.{RuntimeHelpers.GetHashCode(item)}";

    private void BeginClose(CloseDestination destination) {
        if (pauseLevel is null || closeDestination != CloseDestination.None) return;
        CloseDropdown(playSound: false);
        CloseCompositePopup(playSound: false);
        closeDestination = destination;
        display = false;
        if (menu is not null) menu.Focused = false;
        SaveTabState();
        Audio.Play("event:/ui/main/button_back");
    }

    private IEnumerator SaveAndFinishPause() {
        yield return Everest.SaveSettings();
        Level? level = pauseLevel;
        if (level is null) yield break;
        level.AllowHudHide = oldAllowHudHide;
        CloseDestination destination = closeDestination;
        RemoveSelf();
        if (destination == CloseDestination.PauseMenu) {
            level.Pause(pauseReturnIndex, pauseMinimal, false);
        } else {
            level.Paused = false;
            Engine.FreezeTimer = 0.15f;
        }
    }

    private static void ReplacePauseMenuButton(Level level, TextMenu pauseMenu, bool minimal) {
        if (!MicroblocksQolUtilsModule.Settings.ReplaceEverestModOptions) return;
        string label = Dialog.Clean("menu_pause_modoptions");
        TextMenu.Button? original = pauseMenu.Items.OfType<TextMenu.Button>()
            .FirstOrDefault(button => button.Label == label);
        if (original is null) return;
        int index = pauseMenu.IndexOf(original);
        pauseMenu.Remove(original);
        TextMenu.Item replacement = new TextMenu.Button(label);
        replacement.Pressed(() => {
            int returnIndex = pauseMenu.IndexOf(replacement);
            pauseMenu.RemoveSelf();
            level.PauseMainMenuOpen = false;
            level.Paused = true;
            bool allowHudHide = level.AllowHudHide;
            level.AllowHudHide = false;
            level.Add(new MaterialModOptions(level, returnIndex, minimal, allowHudHide));
        });
        pauseMenu.Insert(index, replacement);
    }

    private static IEnumerator DetourGotoRoutine(GotoRoutineOrig orig, Overworld self, Oui next) {
        if (next is OuiModOptions
            && MicroblocksQolUtilsModule.Settings.ReplaceEverestModOptions
            && self.GetUI<MaterialModOptions>() is { } material) {
            next = material;
        }
        return orig(self, next);
    }

    private static bool TrySplitModuleHeader(string value, out string title, out string version) {
        const string marker = " | v.";
        int markerIndex = value.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0 || markerIndex + marker.Length >= value.Length) {
            title = "";
            version = "";
            return false;
        }
        title = value[..markerIndex].Trim();
        version = "v." + value[(markerIndex + marker.Length)..].Trim();
        return title.Length > 0;
    }

    private static bool IsEverestHeaderImage(TextMenu.Item item) =>
        item.GetType().Name.Contains("HeaderImage", StringComparison.Ordinal);

    private static bool CanSelect(TextMenu.Item item) => item.Visible && item.Selectable;

    private static float RowHeight(RowEntry row, float rowWidth) {
        float height = PrimaryRowHeight(row.Item);
        if (row.Description is { } description) {
            height = Math.Max(height,
                DescribedTitleHeight + DescriptionHeight(description.Title, rowWidth, embedded: true));
        }
        return height;
    }

    private static float PrimaryRowHeight(TextMenu.Item item) {
        if (item is TextMenu.SubHeader) return 54f;
        return StandardRowHeight;
    }

    private static float DescriptionHeight(string text, float rowWidth, bool embedded) =>
        DescriptionTopPadding
        + Math.Max(1, DescriptionLines(text, rowWidth, embedded).Count) * DescriptionLineHeight
        + DescriptionBottomPadding;

    private static List<string> DescriptionLines(string text, float rowWidth, bool embedded) {
        float inset = embedded ? 24f : 12f;
        float width = embedded
            ? Math.Max(80f, rowWidth - RowTextOffset - 456f)
            : Math.Max(80f, rowWidth - inset * 2f - 38f);
        return MaterialTextUtil.WrapLines(text, width, 0.25f, 4);
    }

    private static string ItemKey(TextMenu.Item item) =>
        $"mod-options.item.{RuntimeHelpers.GetHashCode(item)}";

    private static TabVisual TabVisualFor(ModTab tab, MaterialPalette palette) {
        Color seeded = MaterialPalette.FromSeed(TabAccentSeeds[tab.AccentIndex]).Primary;
        return new TabVisual(tab.Icon, Color.Lerp(palette.Primary, seeded, 0.72f));
    }

    private static string ResolveTabIcon(string id, string title) {
        if (id == "__favorites") return "star";
        if (id is "__status" or "__empty") return "info";
        string value = title.ToLowerInvariant();
        if (ContainsAny(value, "achievement", "成就")) return "dashboard";
        if (ContainsAny(value, "everest", "core", "settings", "config", "设置", "配置")) return "settings";
        if (ContainsAny(value, "boss", "death", "skull", "死亡", "首领", "bosses")) return "skull";
        if (ContainsAny(value, "map", "collab", "lobby", "地图", "大厅")) return "map";
        if (ContainsAny(value, "route", "path", "路线")) return "route";
        if (ContainsAny(value, "bounce", "dash", "ultra", "speed", "movement", "移动", "加速"))
            return "speed";
        if (ContainsAny(value, "record", "video", "movie", "录像", "录制")) return "movie";
        if (ContainsAny(value, "discord", "chat", "social", "聊天")) return "group";
        if (ContainsAny(value, "font", "text", "language", "字体", "语言")) return "font_download";
        if (ContainsAny(value, "key", "input", "keyboard", "按键", "输入")) return "keyboard";
        if (ContainsAny(value, "mirror", "database", "镜像", "数据库")) return "deployed_code";
        if (ContainsAny(value, "berry", "gem", "refill", "草莓", "水晶")) return "blur_on";
        if (ContainsAny(value, "bolt", "电气", "螺栓")) return "bolt";
        return TabIconPool[(int)(StableHash(id + "|" + title) % (uint)TabIconPool.Length)];
    }

    private static bool ContainsAny(string value, params string[] fragments) =>
        fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static uint StableHash(string value) {
        uint hash = 2166136261;
        foreach (char character in value) {
            hash ^= character;
            hash *= 16777619;
        }
        return hash;
    }

    private static string UiText(string key, string fallback) {
        string value = Dialog.Clean(key);
        return string.IsNullOrWhiteSpace(value)
               || value == key
               || value == $"{{{key}}}"
            ? fallback
            : value;
    }

    private delegate IEnumerator GotoRoutineOrig(Overworld self, Oui next);
    private delegate IEnumerator GotoRoutineDetour(GotoRoutineOrig orig, Overworld self, Oui next);

    private sealed class ModTab(string id, string title, string version, List<TextMenu.Item> items,
        bool pinnable) {
        public string Id { get; } = id;
        public string Title { get; } = title;
        public string Version { get; } = version;
        public List<TextMenu.Item> Items { get; } = items;
        public string Icon { get; } = ResolveTabIcon(id, title);
        public int AccentIndex { get; } = (int)(StableHash(id) % (uint)TabAccentSeeds.Length);
        public bool Pinnable { get; } = pinnable;
        public int Selection { get; set; } = -1;
    }

    private readonly record struct TabVisual(string Icon, Color Accent);

    private sealed record OptionSnapshot(
        string Label,
        int Index,
        List<string> Options,
        List<object> Entries,
        FieldInfo IndexField,
        FieldInfo? PreviousIndexField,
        FieldInfo? ChangeField
    );

    private readonly record struct IntSliderSnapshot(
        string Label,
        int Value,
        int Minimum,
        int Maximum
    );

    private readonly record struct RowEntry(
        TextMenu.Item Item,
        TextMenuExt.EaseInSubHeaderExt? Description
    );

    private readonly record struct RowPlacement(
        TextMenu.Item Item,
        TextMenuExt.EaseInSubHeaderExt? Description,
        MaterialRect Rect
    );

    private enum CloseDestination {
        None,
        PauseMenu,
        Game
    }

    private enum SearchTarget {
        None,
        Tabs,
        Settings
    }

    private readonly record struct ModOptionsLayout(
        MaterialRect Frame,
        MaterialRect Header,
        MaterialRect Navigation,
        MaterialRect TabSearch,
        MaterialRect NavigationItems,
        MaterialRect Content,
        MaterialRect ContentHeader,
        MaterialRect SettingSearch,
        MaterialRect Rows
    ) {
        public static ModOptionsLayout Create(float transition) {
            MaterialSplitPageLayout page = MaterialSplitPageLayout.Create(transition);
            MaterialRect tabSearch = new(
                page.Navigation.X + 12f,
                page.Navigation.Y + 14f,
                page.Navigation.Width - 24f,
                52f
            );
            MaterialRect navigationItems = page.NavigationItems(116f);
            MaterialRect contentHeader = page.ContentHeader;
            MaterialRect settingSearch = new(
                contentHeader.Right - 430f,
                contentHeader.Center.Y - 23f,
                430f,
                46f
            );
            return new ModOptionsLayout(page.Frame, page.Header, page.Navigation, tabSearch,
                navigationItems, page.Content, contentHeader, settingSearch, page.ContentBody);
        }

        public MaterialRect Tab(int index, float scrollOffset) =>
            MaterialSplitPageLayout.Tab(NavigationItems, index, scrollOffset);
    }
}
