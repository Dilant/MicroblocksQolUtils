using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

public sealed class MaterialChapterSelect : Oui, IMaterialAcrylicPage {
    private const float ScreenWidth = 1920f;
    private const float ScreenHeight = 1080f;
    private const int Columns = 4;
    private const float CardHeight = 164f;
    private const float CardHorizontalGap = 18f;
    private const float CardVerticalGap = 18f;
    private const string RecentGroupId = "__microblocks_recently_played";
    private const float GroupHeaderHeight = 58f;
    private const float GroupHeaderGap = 12f;
    private const float GroupVerticalGap = 24f;
    private const float MetadataTagHeight = 28f;
    private static readonly Regex CollabTagRegex = new(
        "^{cu2_tag(\\s+(?<key>\\w+)=\"(?<value>[^\"]+)\")*}\\s*(?<text>.*)$",
        RegexOptions.Compiled
    );
    private static readonly Regex DialogCommandRegex = new("\\{[^}]*}", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new("[ \\t]+", RegexOptions.Compiled);

    private static Hook? gotoRoutineHook;
    private static bool hookFailed;
    private static bool replaceNextChapterSelect;
    private static bool materialSessionActive;

    private readonly List<ChapterEntry> allEntries = [];
    private readonly List<ChapterEntry> entries = [];
    private readonly List<LevelSetEntry> levelSets = [];
    private readonly List<ChapterEntry> ungroupedEntries = [];
    private readonly List<ChapterSection> sections = [];
    private readonly Dictionary<string, bool> collapsedSections = new(StringComparer.Ordinal);
    private readonly MaterialScrollController cardScroll = new();
    private readonly MaterialScrollController levelSetScroll = new();
    private readonly MaterialScrollViewport cardViewport = new("mqol-chapter-cards");
    private readonly MaterialScrollViewport levelSetViewport = new("mqol-chapter-levelsets");
    private readonly MaterialMotionController motion = new();
    private int selectedIndex;
    private int selectedLevelSet;
    private float ease;
    private bool display;
    private Color paletteSeed = new(126, 99, 184);
    private string searchText = "";
    internal bool AcceptsTouch => Visible && Active && Focused && display && ease > 0.99f;
    private string TouchContext => $"chapters:{selectedLevelSet}:{searchText}";
    private string imeText = "";
    private bool searchFocused;

    public bool SuppressNormalRender { get; set; }

    internal static MaterialChapterSelect? ActivePage {
        get {
            if (Engine.Scene is not Overworld overworld) return null;
            MaterialChapterSelect? page = overworld.Current as MaterialChapterSelect
                ?? overworld.Next as MaterialChapterSelect;
            return page is { Visible: true } ? page : null;
        }
    }

    public static void Load() {
        if (gotoRoutineHook is not null || hookFailed) return;
        try {
            On.Celeste.OuiFileSelectSlot.OnContinueSelected += FileSelectContinue;
            MethodInfo method = typeof(Overworld).GetMethod(
                "GotoRoutine",
                BindingFlags.Instance | BindingFlags.NonPublic
            ) ?? throw new MissingMethodException(typeof(Overworld).FullName, "GotoRoutine");
            gotoRoutineHook = new Hook(method, (GotoRoutineDetour)DetourGotoRoutine);
        } catch (Exception exception) {
            On.Celeste.OuiFileSelectSlot.OnContinueSelected -= FileSelectContinue;
            hookFailed = true;
            Logger.LogDetailed(exception, "MicroblocksQolUtils/MaterialChapterSelect");
        }
    }

    public static void Unload() {
        On.Celeste.OuiFileSelectSlot.OnContinueSelected -= FileSelectContinue;
        gotoRoutineHook?.Dispose();
        gotoRoutineHook = null;
        hookFailed = false;
        replaceNextChapterSelect = false;
        materialSessionActive = false;
    }

    public override bool IsStart(Overworld overworld, Overworld.StartMode start) => false;

    public override IEnumerator Enter(Oui from) {
        Visible = true;
        display = true;
        ease = 0f;
        Overworld.ShowInputUI = false;
        Overworld.Mountain.AllowUserRotation = false;
        Overworld.Maddy.Hide();
        RebuildEntries();
        paletteSeed = entries.Count == 0
            ? new Color(126, 99, 184)
            : entries[Math.Clamp(selectedIndex, 0, entries.Count - 1)].Area.TitleBaseColor;
        materialSessionActive = true;
        Audio.Play("event:/ui/world_map/icon/roll_right");
        yield return null;
    }

    public override IEnumerator Leave(Oui next) {
        display = false;
        SetSearchFocused(false);
        float duration = 0.16f;
        for (float timer = 0f; timer < duration; timer += Engine.DeltaTime) yield return null;
        Visible = false;
        Overworld.ShowInputUI = true;
        Overworld.Mountain.AllowUserRotation = true;
    }

    public override void Update() {
        MaterialTouch.BeginUpdate(this);
        ChapterLayout layout = ChapterLayout.Create(0f);
        cardScroll.Update(MaxCardScroll(layout));
        levelSetScroll.Update(MaxLevelSetScroll(layout));
        ease = Calc.Approach(ease, display ? 1f : 0f, Engine.DeltaTime * 7f);
        UpdateInteractions(layout);
        if (Focused && display) UpdateInput();
        base.Update();
    }

    public override void Removed(Scene scene) {
        SetSearchFocused(false);
        cardViewport.Dispose();
        levelSetViewport.Dispose();
        motion.Dispose();
        base.Removed(scene);
    }

    public override void Render() {
        if (SuppressNormalRender) return;
        RenderMaterialContent(acrylicActive: false);
    }

    public void RenderMaterialContent(bool acrylicActive) {
        if (!Visible || ease <= 0f) return;
        ChapterEntry? selected = entries.Count == 0 ? null : entries[Math.Clamp(selectedIndex, 0, entries.Count - 1)];
        MaterialPalette palette = MaterialPalette.FromSeed(paletteSeed);
        float eased = Ease.CubeOut(ease);
        Draw.Rect(0f, 0f, ScreenWidth, ScreenHeight, palette.Scrim * (0.90f * eased));

        ChapterLayout layout = ChapterLayout.Create(1f - eased);

        MaterialSplitPageChrome.RenderHeader(layout.Header, "map",
            UiText("microblocks_qol_chapter_title", "选择章节"), palette, eased);
        RenderSearchBox(palette, layout, eased);

        RenderLevelSets(palette, layout, eased);
        MaterialSplitPageChrome.RenderContentSurface(layout.Content, palette, eased);
        string levelSetTitle = levelSets.Count == 0
            ? UiText("microblocks_qol_chapter_level_sets", "地图集")
            : levelSets[Math.Clamp(selectedLevelSet, 0, levelSets.Count - 1)].Title;
        MaterialSplitPageChrome.RenderContentHeading(layout.ContentHeader, "folder_open",
            levelSetTitle, layout.ContentHeader.Width - 56f, palette.Primary, palette, eased);
        RenderCards(palette, layout, eased);
        RenderSelectedMetadata(palette, selected, layout, eased);
        RenderFooter(palette, selected, layout, eased);
        RenderMouseCursor(palette, eased);
    }

    private void UpdateInput() {
        ChapterLayout layout = ChapterLayout.Create(0f);
        Vector2 mouse = MaterialTouch.Position;
        if (MaterialTouch.PressedLeftButton && layout.Search.Contains(mouse)) {
            SetSearchFocused(true);
            Audio.Play("event:/ui/main/button_select");
            return;
        }
        if (MInput.Keyboard.Check(Keys.LeftControl, Keys.RightControl)
            && MInput.Keyboard.Pressed(Keys.F)) {
            SetSearchFocused(true);
            return;
        }
        if (searchFocused) {
            if (Input.MenuCancel.Pressed || MaterialTextInputFocus.Pressed(Keys.Escape)) {
                SetSearchFocused(false);
            } else if (MaterialTextInputFocus.Pressed(Keys.Enter)) {
                SetSearchFocused(false);
            } else if (MaterialTouch.PressedLeftButton && !layout.Search.Contains(mouse)) {
                SetSearchFocused(false);
            }
            return;
        }

        if (Input.MenuCancel.Pressed || MInput.Keyboard.Pressed(Keys.Escape)) {
            Audio.Play("event:/ui/main/button_back");
            materialSessionActive = false;
            Overworld.Goto<OuiFileSelect>();
            return;
        }

        if (MaterialTabNavigation.TryGetShiftVerticalDirection(out int tabDirection)) {
            SelectLevelSet(selectedLevelSet + tabDirection);
        } else if (entries.Count > 0) {
            if (Input.MenuLeft.Pressed) MoveSelection(-Vector2.UnitX);
            else if (Input.MenuRight.Pressed) MoveSelection(Vector2.UnitX);
            else if (Input.MenuUp.Pressed) MoveSelection(-Vector2.UnitY);
            else if (Input.MenuDown.Pressed) MoveSelection(Vector2.UnitY);
            else if (Input.MenuConfirm.Pressed || MInput.Keyboard.Pressed(Keys.Enter)) ActivateSelected();
        }

        bool inSidebar = layout.Sidebar.Contains(mouse);
        if (MaterialTouch.WheelDelta != 0) {
            if (inSidebar) {
                levelSetScroll.Scroll(-Math.Sign(MaterialTouch.WheelDelta) * 150f,
                    MaxLevelSetScroll(layout));
            } else {
                cardScroll.Scroll(-Math.Sign(MaterialTouch.WheelDelta) * 220f,
                    MaxCardScroll(layout));
            }
        }

        if (MaterialTouch.WasMoved || MaterialTouch.PressedLeftButton) {
            int sidebar = SidebarIndexAt(mouse, layout);
            if (sidebar >= 0) {
                if (MaterialTouch.PressedLeftButton) SelectLevelSet(sidebar);
                return;
            }
            int header = SectionHeaderIndexAt(mouse, layout);
            if (header >= 0 && MaterialTouch.PressedLeftButton) {
                ToggleSection(header);
                return;
            }
            int card = CardIndexAt(mouse, layout);
            if (card >= 0) {
                if (selectedIndex != card) {
                    selectedIndex = card;
                    Audio.Play("event:/ui/world_map/icon/roll_right");
                }
                if (MaterialTouch.PressedLeftButton) ActivateSelected();
            }
        }
    }

    private void UpdateInteractions(ChapterLayout layout) {
        List<MaterialInteractionTarget> targets = [
            new MaterialInteractionTarget("chapter.search", layout.Search, Focused: searchFocused,
                TouchKind: "text", TouchText: searchText, TouchTextChanged: value => {
                    searchText = value; SetSearchFocused(false); RebuildEntries();
                })
        ];
        if (!Visible || !display || searchFocused) {
            MaterialTouch.Publish(this, targets, TouchContext);
        motion.Update(targets);
            return;
        }

        for (int index = 0; index < levelSets.Count; index++) {
            MaterialRect item = layout.SidebarItem(index, levelSetScroll.Offset);
            if (item.Bottom < layout.SidebarItems.Y || item.Y > layout.SidebarItems.Bottom) continue;
            targets.Add(new MaterialInteractionTarget(
                $"chapter.levelset.{levelSets[index].Id}",
                item,
                Focused: index == selectedLevelSet
            ));
        }

        ChapterContentLayout content = BuildContentLayout(layout, cardScroll.Offset);
        foreach (SectionPlacement placement in content.Sections) {
            if (placement.Rect.Bottom < layout.Cards.Y || placement.Rect.Y > layout.Cards.Bottom) continue;
            targets.Add(new MaterialInteractionTarget(
                $"chapter.section.{sections[placement.SectionIndex].Id}", placement.Rect));
        }
        foreach (CardPlacement placement in content.Cards) {
            if (placement.Rect.Bottom < layout.Cards.Y || placement.Rect.Y > layout.Cards.Bottom) continue;
            targets.Add(new MaterialInteractionTarget(
                $"chapter.card.{entries[placement.EntryIndex].Sid}",
                placement.Rect,
                Focused: placement.EntryIndex == selectedIndex
            ));
        }
        MaterialTouch.Publish(this, targets, TouchContext);
        motion.Update(targets);
    }

    private void RebuildEntries() {
        allEntries.Clear();
        levelSets.Clear();
        levelSets.Add(new LevelSetEntry(RecentGroupId,
            UiText("microblocks_qol_chapter_recent", "最近游玩")));
        levelSets.Add(new LevelSetEntry("", UiText("microblocks_qol_chapter_all_maps", "全部地图")));
        bool showCollabMaps = MicroblocksQolUtilsModule.Settings.ChapterSelectShowCollabMaps;
        SaveData? save = SaveData.Instance;
        foreach (AreaData area in AreaData.Areas) {
            if (area.Mode is null || area.Mode.Length == 0 || area.Mode[0] is null) continue;
            string sid = area.SID ?? area.ID.ToString();
            bool collabMap = CollabUtils2Bridge.IsCollabMap(sid);
            bool collabGym = CollabUtils2Bridge.IsCollabGym(sid);
            bool collabLobby = CollabUtils2Bridge.IsCollabLobby(sid);
            if (!showCollabMaps && (collabMap || collabGym)) continue;
            if (save is not null
                && area.LevelSet == "Celeste"
                && area.ID > save.UnlockedAreas
                && !(save.AssistMode && area.ID <= save.MaxAssistArea)) continue;

            string levelSet = area.LevelSet ?? "Celeste";
            string title = CleanName(area.Name, sid);
            string levelSetTitle = levelSet == "Celeste" ? "Celeste" : Dialog.CleanLevelSet(levelSet);
            // GetCollabNameForSID also recognizes special collab maps such as the prologue,
            // which CollabUtils2 intentionally excludes from IsCollabLobby. Group by the
            // collab root for every SID so the lobby, its maps, gyms, and prologue stay in
            // one tab.
            string? collabName = CollabUtils2Bridge.GetCollabName(sid);
            string groupId = string.IsNullOrWhiteSpace(collabName) ? levelSet : collabName;
            string groupTitle = string.IsNullOrWhiteSpace(collabName)
                ? levelSetTitle
                : CleanGroupTitle(collabName, levelSetTitle);
            string badge = collabLobby ? "LOBBY"
                : collabGym ? "GYM"
                : collabMap ? "COLLAB"
                : levelSet == "Celeste" ? UiText("microblocks_qol_chapter_official", "官方") : "MOD";
            string? lobbySid = collabLobby ? sid : CollabUtils2Bridge.GetLobbyForMap(sid);
            if (string.IsNullOrWhiteSpace(lobbySid)) lobbySid = null;
            ChapterMetadata metadata = ResolveMetadata(area);
            allEntries.Add(new ChapterEntry(
                area, sid, levelSet, groupId, title, levelSetTitle, badge, collabLobby, lobbySid,
                metadata.Author, metadata.Description, metadata.Tags
            ));
            if (levelSets.All(item => !string.Equals(item.Id, groupId, StringComparison.Ordinal)))
                levelSets.Add(new LevelSetEntry(groupId, groupTitle));
        }

        string currentSid = save?.LastArea_Safe.SID ?? "";
        string currentGroup = allEntries.FirstOrDefault(entry => entry.Sid == currentSid)?.GroupId ?? "";
        selectedLevelSet = Math.Max(0, levelSets.FindIndex(item => item.Id == currentGroup));
        FilterEntries(keepArea: true);
    }

    private void FilterEntries(bool keepArea) {
        string? previousSid = keepArea && entries.Count > 0 && selectedIndex < entries.Count
            ? entries[selectedIndex].Sid
            : SaveData.Instance?.LastArea_Safe.SID;
        string group = levelSets.Count == 0 ? "" : levelSets[selectedLevelSet].Id;
        IEnumerable<ChapterEntry> filtered;
        if (group == RecentGroupId) {
            Dictionary<string, int> recentOrder = RecentChapterHistory.Entries
                .Select((sid, index) => (sid, index))
                .GroupBy(item => item.sid, StringComparer.Ordinal)
                .ToDictionary(items => items.Key, items => items.First().index, StringComparer.Ordinal);
            filtered = allEntries
                .Where(entry => recentOrder.ContainsKey(entry.Sid))
                .OrderBy(entry => recentOrder[entry.Sid]);
        } else {
            filtered = group.Length == 0
                ? allEntries
                : allEntries.Where(entry => entry.GroupId == group);
        }
        if (!string.IsNullOrWhiteSpace(searchText)) {
            string search = searchText.Trim();
            filtered = filtered.Where(entry =>
                entry.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || entry.LevelSetTitle.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || entry.Sid.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.Author.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || entry.Description.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || entry.Tags.Any(tag => tag.Text.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            );
        }
        BuildSections(group, filtered.ToList());
        RebuildVisibleEntries(previousSid, resetScroll: true);
    }

    private void BuildSections(string group, List<ChapterEntry> filtered) {
        sections.Clear();
        ungroupedEntries.Clear();

        List<ChapterEntry> groupEntries = group.Length == 0
            ? []
            : allEntries.Where(entry => entry.GroupId == group).ToList();
        List<ChapterEntry> lobbies = groupEntries.Where(entry => entry.CollabLobby).ToList();
        HashSet<string> lobbySids = lobbies.Select(entry => entry.Sid).ToHashSet(StringComparer.Ordinal);
        bool hasGroupedMaps = group.Length > 0
            && lobbies.Count >= 2
            && groupEntries.Any(entry => entry.LobbySid is { } lobbySid
                && lobbySids.Contains(lobbySid)
                && !entry.CollabLobby);

        if (!hasGroupedMaps) {
            ungroupedEntries.AddRange(filtered);
            return;
        }

        ungroupedEntries.AddRange(filtered.Where(entry => entry.LobbySid is not { } lobbySid
            || !lobbySids.Contains(lobbySid)));
        string currentSid = SaveData.Instance?.LastArea_Safe.SID ?? "";
        string? currentLobbySid = allEntries.FirstOrDefault(entry => entry.Sid == currentSid)?.LobbySid;
        foreach (ChapterEntry lobby in lobbies) {
            List<ChapterEntry> sectionEntries = filtered
                .Where(entry => string.Equals(entry.LobbySid, lobby.Sid, StringComparison.Ordinal))
                .ToList();
            if (sectionEntries.Count == 0) continue;
            if (!collapsedSections.ContainsKey(lobby.Sid))
                collapsedSections[lobby.Sid] = !string.Equals(lobby.Sid, currentLobbySid, StringComparison.Ordinal);
            int totalMapCount = Math.Max(0, groupEntries.Count(entry =>
                string.Equals(entry.LobbySid, lobby.Sid, StringComparison.Ordinal)) - 1);
            sections.Add(new ChapterSection(lobby.Sid, lobby, sectionEntries, totalMapCount));
        }
    }

    private void RebuildVisibleEntries(string? previousSid, bool resetScroll) {
        entries.Clear();
        entries.AddRange(ungroupedEntries);
        bool searching = !string.IsNullOrWhiteSpace(searchText);
        foreach (ChapterSection section in sections) {
            section.VisibleStart = entries.Count;
            bool collapsed = !searching && collapsedSections.GetValueOrDefault(section.Id);
            if (!collapsed) entries.AddRange(section.Entries);
            section.VisibleCount = entries.Count - section.VisibleStart;
        }

        selectedIndex = entries.FindIndex(entry => entry.Sid == previousSid);
        selectedIndex = Math.Max(0, selectedIndex);
        if (entries.Count == 0) selectedIndex = 0;
        if (resetScroll) cardScroll.Reset();
        EnsureSelectionVisible();
        EnsureLevelSetVisible();
    }

    private void SelectLevelSet(int index) {
        if (levelSets.Count == 0) return;
        index = (index % levelSets.Count + levelSets.Count) % levelSets.Count;
        if (index == selectedLevelSet) return;
        selectedLevelSet = index;
        Audio.Play("event:/ui/world_map/icon/roll_right");
        FilterEntries(keepArea: false);
    }

    private void MoveSelection(Vector2 direction) {
        if (entries.Count == 0) return;
        ChapterLayout layout = ChapterLayout.Create(0f);
        List<CardPlacement> placements = BuildContentLayout(layout, 0f).Cards;
        CardPlacement? current = placements.FirstOrDefault(item => item.EntryIndex == selectedIndex);
        if (current is null) return;

        int next = selectedIndex;
        float bestScore = float.MaxValue;
        foreach (CardPlacement candidate in placements) {
            if (candidate.EntryIndex == selectedIndex) continue;
            Vector2 offset = candidate.Rect.Center - current.Rect.Center;
            float primary = Vector2.Dot(offset, direction);
            if (primary <= 1f) continue;
            float secondary = Math.Abs(Vector2.Dot(offset,
                direction.X == 0f ? Vector2.UnitX : Vector2.UnitY));
            if (direction.X != 0f && secondary > CardHeight * 0.45f) continue;
            float score = primary * 1000f + secondary;
            if (score >= bestScore) continue;
            bestScore = score;
            next = candidate.EntryIndex;
        }
        if (next == selectedIndex) return;
        selectedIndex = next;
        Audio.Play(direction.X < 0f || direction.Y < 0f
            ? "event:/ui/world_map/icon/roll_left"
            : "event:/ui/world_map/icon/roll_right");
        EnsureSelectionVisible();
    }

    private void EnsureSelectionVisible() {
        if (entries.Count == 0) return;
        ChapterLayout layout = ChapterLayout.Create(0f);
        ChapterContentLayout content = BuildContentLayout(layout, 0f);
        CardPlacement? placement = content.Cards.FirstOrDefault(item => item.EntryIndex == selectedIndex);
        if (placement is null) return;
        float top = placement.Rect.Y - layout.Cards.Y;
        cardScroll.EnsureVisible(top, top + placement.Rect.Height,
            layout.Cards.Height, MaxCardScroll(layout));
    }

    private void EnsureLevelSetVisible() {
        ChapterLayout layout = ChapterLayout.Create(0f);
        float top = selectedLevelSet * (ChapterLayout.SidebarItemHeight + ChapterLayout.SidebarItemGap);
        levelSetScroll.EnsureVisible(top, top + ChapterLayout.SidebarItemHeight,
            layout.SidebarItems.Height, MaxLevelSetScroll(layout));
    }

    private void ActivateSelected() {
        SaveData? save = SaveData.Instance;
        if (entries.Count == 0 || save is null) {
            Audio.Play("event:/ui/main/button_invalid");
            return;
        }
        ChapterEntry entry = entries[selectedIndex];
        AreaKey area = entry.Area.ToKey();
        Audio.Play("event:/ui/world_map/icon/select");
        save.LastArea_Safe = area;
        AlignSelectedAreaStats(save, area);
        Logger.Log(LogLevel.Info, "MicroblocksQolUtils/ChapterSelect",
            $"Selected {entry.Sid} area={entry.Area.ID} levelSet={entry.LevelSet}");

        // CollabUtils stores "save and return to lobby" progress as a serialized Session,
        // not as a vanilla checkpoint. Use its supported continue path instead of opening
        // the normal chapter panel, which otherwise offers a brand-new map start.
        if (CollabUtils2Bridge.HasSavedState(entry.Sid)) {
            Focused = false;
            Add(new Coroutine(ResumeSavedCollabSession(entry)));
            return;
        }

        // Match the vanilla chapter select and leave saving to the normal game flow.
        // Saving here invokes SaveData.AfterInitialize; CollabUtils2 uses that hook to
        // replace a collab map's LastArea with its lobby, so OuiChapterPanel would open
        // (and then start) the lobby instead of the selected map.
        Overworld.Goto<OuiChapterPanel>();
    }

    private static void AlignSelectedAreaStats(SaveData save, AreaKey area) {
        if (area.ID < 0 || area.ID >= save.Areas_Safe.Count || area.GetLevelSet() == "Celeste") return;

        AreaStats? persisted = save.GetAreaStatsFor(area);
        if (persisted is null || ReferenceEquals(save.Areas_Safe[area.ID], persisted)) return;

        // Some modded level-set flows replace their AreaStats list after Everest built the
        // flattened Areas_Safe cache. Rebind this slot by SID before OuiChapterPanel and
        // Session read it, otherwise berries and checkpoints come from a fresh AreaStats.
        save.Areas_Safe[area.ID] = persisted;
        Logger.Log(LogLevel.Info, "MicroblocksQolUtils/ChapterSelect",
            $"Rebound saved stats for {area.SID} at area={area.ID}");
    }

    private IEnumerator ResumeSavedCollabSession(ChapterEntry entry) {
        Overworld.Maddy.Hide(down: false);
        entry.Area.Wipe(Overworld, false, null);
        Audio.SetMusic(null);
        Audio.SetAmbience(null);
        yield return 0.5f;
        if (!CollabUtils2Bridge.ContinueSavedState(entry.Sid))
            LevelEnter.Go(new Session(entry.Area.ToKey()), fromSaveData: false);
    }

    private void RenderLevelSets(
        MaterialPalette palette,
        ChapterLayout layout,
        float alpha
    ) {
        MaterialSplitPageChrome.RenderNavigationSurface(layout.Sidebar, palette, alpha);
        MaterialUiKit.Text(UiText("microblocks_qol_chapter_level_sets", "地图集"),
            new Vector2(layout.SidebarItems.X + 8f,
                layout.Sidebar.Y + ChapterLayout.SidebarHeaderHeight / 2f),
            new Vector2(0f, 0.5f), MaterialTextRole.Label, palette.Primary, alpha,
            scaleOverride: 0.26f);
        levelSetViewport.Render(layout.SidebarItems, () => {
            for (int index = 0; index < levelSets.Count; index++) {
                string key = $"chapter.levelset.{levelSets[index].Id}";
                MaterialRect item = layout.SidebarItem(index, levelSetScroll.Offset);
                if (item.Bottom < layout.SidebarItems.Y || item.Y > layout.SidebarItems.Bottom) continue;
                bool selected = index == selectedLevelSet;
                MaterialSplitPageChrome.RenderSimpleTab(motion, key, item, "folder_open",
                    levelSets[index].Title, selected, palette.Primary, palette, alpha);
            }
        });
    }

    private void RenderCards(
        MaterialPalette palette,
        ChapterLayout layout,
        float alpha
    ) {
        cardViewport.Render(layout.Cards, () => {
            ChapterContentLayout content = BuildContentLayout(layout, cardScroll.Offset);
            foreach (SectionPlacement placement in content.Sections) {
                if (placement.Rect.Bottom < layout.Cards.Y || placement.Rect.Y > layout.Cards.Bottom) continue;
                ChapterSection section = sections[placement.SectionIndex];
                string key = $"chapter.section.{section.Id}";
                MaterialRect header = placement.Rect;
                RenderSectionHeader(section, header,
                    collapsedSections.GetValueOrDefault(section.Id), palette, alpha, key);
            }
            foreach (CardPlacement placement in content.Cards) {
                int index = placement.EntryIndex;
                string key = $"chapter.card.{entries[index].Sid}";
                MaterialRect card = placement.Rect;
                if (card.Bottom < layout.Cards.Y || card.Y > layout.Cards.Bottom) continue;
                bool selected = index == selectedIndex;
                Color surface = selected ? palette.SurfaceHighest : palette.SurfaceHigh;
                MaterialUiKit.Card(card,
                    palette with { SurfaceHigh = surface * (selected ? 0.98f : 0.85f) }, selected, alpha);
                motion.RenderStateLayer(key, card, 30f,
                    selected ? palette.Primary : palette.OnSurface, alpha);
                RenderCard(entries[index], card, selected, palette, alpha);
            }
            if (entries.Count == 0 && sections.Count == 0) {
                string emptyText = levelSets.Count > 0 && levelSets[selectedLevelSet].Id == RecentGroupId
                    ? UiText("microblocks_qol_chapter_recent_empty", "还没有最近游玩的章节")
                    : UiText("microblocks_qol_chapter_empty", "这个地图集中没有可选章节");
                SystemTtfFont.DrawVisual(emptyText,
                    layout.Cards.Center, new Vector2(0.5f), 0.56f, palette.OnSurfaceVariant * alpha);
            }
        });
    }

    private void RenderSectionHeader(
        ChapterSection section,
        MaterialRect header,
        bool collapsed,
        MaterialPalette palette,
        float alpha,
        string key
    ) {
        MaterialUi.RoundedRect(header.X, header.Y, header.Width, header.Height, 20f,
            palette.SurfaceHighest * 0.78f * alpha);
        MaterialUi.RoundedOutline(header.X, header.Y, header.Width, header.Height, 20f, 1f,
            palette.Outline * 0.72f * alpha);
        motion.RenderStateLayer(key, header, 20f, palette.Primary, alpha);

        float iconSize = 34f;
        Vector2 iconCenter = new(header.X + 28f, header.Center.Y);
        if (!string.IsNullOrWhiteSpace(section.Lobby.Area.Icon) && GFX.Gui.Has(section.Lobby.Area.Icon)) {
            MTexture icon = GFX.Gui[section.Lobby.Area.Icon];
            float scale = Math.Min(1f, iconSize / Math.Max(icon.Width, icon.Height));
            icon.DrawCentered(iconCenter, Color.White * alpha, scale);
        } else {
            MaterialUi.RoundedRect(iconCenter.X - iconSize / 2f, iconCenter.Y - iconSize / 2f,
                iconSize, iconSize, 12f, palette.Primary * 0.42f * alpha);
        }

        string count = section.TotalMapCount + " "
            + UiText("microblocks_qol_chapter_group_maps", "张地图");
        float countWidth = SystemTtfFont.MeasureVisible(count, 0.29f).X;
        float titleWidth = Math.Max(0f, header.Width - countWidth - 128f);
        SystemTtfFont.DrawVisual(MaterialTextUtil.Ellipsize(
                section.Lobby.Title, titleWidth, 0.39f, UiFontWeight.Bold),
            new Vector2(header.X + 54f, header.Center.Y), new Vector2(0f, 0.5f), 0.39f,
            palette.OnSurface * alpha, weight: UiFontWeight.Bold);
        SystemTtfFont.DrawVisual(count, new Vector2(header.Right - 54f, header.Center.Y),
            new Vector2(1f, 0.5f), 0.29f, palette.OnSurfaceVariant * alpha);

        Vector2 arrow = new(header.Right - 25f, header.Center.Y);
        if (collapsed) {
            MaterialUi.Line(arrow + new Vector2(-4f, -7f), arrow + new Vector2(4f, 0f),
                2.5f, palette.Primary * alpha);
            MaterialUi.Line(arrow + new Vector2(4f, 0f), arrow + new Vector2(-4f, 7f),
                2.5f, palette.Primary * alpha);
        } else {
            MaterialUi.Line(arrow + new Vector2(-7f, -4f), arrow + new Vector2(0f, 4f),
                2.5f, palette.Primary * alpha);
            MaterialUi.Line(arrow + new Vector2(0f, 4f), arrow + new Vector2(7f, -4f),
                2.5f, palette.Primary * alpha);
        }
    }

    private static void RenderCard(
        ChapterEntry entry,
        MaterialRect card,
        bool selected,
        MaterialPalette palette,
        float alpha
    ) {
        MaterialRect content = card.Inset(20f, 18f);
        float iconSize = 48f;
        if (!string.IsNullOrWhiteSpace(entry.Area.Icon) && GFX.Gui.Has(entry.Area.Icon)) {
            MTexture icon = GFX.Gui[entry.Area.Icon];
            float scale = Math.Min(1f, iconSize / Math.Max(icon.Width, icon.Height));
            icon.DrawCentered(new Vector2(content.X + iconSize / 2f, content.Y + iconSize / 2f),
                Color.White * alpha, scale);
        } else {
            MaterialUi.RoundedRect(content.X, content.Y, iconSize, iconSize, 17f,
                palette.Primary * 0.42f * alpha);
        }
        float textX = content.X + iconSize + 14f;
        float primaryTextWidth = content.Right - textX;
        SystemTtfFont.DrawVisual(MaterialTextUtil.Ellipsize(
                entry.Title, primaryTextWidth, 0.40f, UiFontWeight.Bold), new Vector2(textX, content.Y),
            Vector2.Zero, 0.40f, palette.OnSurface * alpha, weight: UiFontWeight.Bold);
        string subtitle = entry.Author.Length > 0 ? entry.Author : entry.LevelSetTitle;
        SystemTtfFont.DrawVisual(MaterialTextUtil.Ellipsize(subtitle, primaryTextWidth, 0.27f),
            new Vector2(textX, content.Y + 34f),
            Vector2.Zero, 0.27f, palette.OnSurfaceVariant * alpha);
        if (entry.Tags.Count > 0) {
            string tagSummary = string.Join(" · ", entry.Tags.Select(tag => tag.Text));
            SystemTtfFont.DrawVisual(MaterialTextUtil.Ellipsize(tagSummary, content.Width, 0.24f,
                    UiFontWeight.Bold), new Vector2(content.X, content.Y + 68f),
                Vector2.Zero, 0.24f, palette.Primary * alpha, weight: UiFontWeight.Bold);
        }

        AreaStats? stats = SaveData.Instance?.GetAreaStatsFor(entry.Area.ToKey());
        AreaModeStats? mode = stats?.Modes is { Length: > 0 } ? stats.Modes[0] : null;
        const float bottomRowHeight = 32f;
        float bottomRowY = card.Bottom - 48f;
        string state = mode is null
            ? UiText("microblocks_qol_chapter_never_entered", "尚未游玩")
            : UiText(mode.Completed ? "microblocks_qol_chapter_cleared" : "microblocks_qol_chapter_uncleared",
                mode.Completed ? "已完成" : "进行中");
        RenderStatusPill(state, mode, new MaterialRect(content.X, bottomRowY, 86f, bottomRowHeight),
            palette, alpha);
        if (mode is not null) {
            DrawStat("collectables/strawberry", mode.TotalStrawberries,
                new Vector2(content.X + 112f, bottomRowY + bottomRowHeight / 2f),
                palette.OnSurfaceVariant, alpha);
            DrawStat("collectables/skullBlue", mode.Deaths,
                new Vector2(content.X + 180f, bottomRowY + bottomRowHeight / 2f),
                palette.OnSurfaceVariant, alpha);
        }

        MaterialUiKit.Chip(entry.Badge,
            new Vector2(card.Right - 16f, bottomRowY), palette, selected, alpha);
    }

    private static void RenderSelectedMetadata(
        MaterialPalette palette,
        ChapterEntry? selected,
        ChapterLayout layout,
        float alpha
    ) {
        MaterialUiKit.Surface(layout.Details, 24f,
            palette with { SurfaceHigh = palette.SurfaceHigh * 0.82f }, alpha);
        MaterialRect content = layout.Details.Inset(22f, 14f);
        if (selected is null) {
            SystemTtfFont.DrawVisual(UiText("microblocks_qol_chapter_no_available", "没有可用章节"),
                content.Center, new Vector2(0.5f), 0.38f, palette.OnSurfaceVariant * alpha);
            return;
        }

        const float identityWidth = 360f;
        SystemTtfFont.DrawVisual(MaterialTextUtil.Ellipsize(
                selected.Title, identityWidth, 0.38f, UiFontWeight.Bold),
            new Vector2(content.X, content.Y), Vector2.Zero, 0.38f, palette.OnSurface * alpha,
            weight: UiFontWeight.Bold);
        SystemTtfFont.DrawVisual(MaterialTextUtil.Ellipsize(selected.LevelSetTitle, identityWidth, 0.27f),
            new Vector2(content.X, content.Y + 34f), Vector2.Zero, 0.27f,
            palette.OnSurfaceVariant * alpha);
        if (selected.Author.Length > 0) {
            string author = string.Format(UiText("microblocks_qol_chapter_author", "作者：{0}"), selected.Author);
            SystemTtfFont.DrawVisual(MaterialTextUtil.Ellipsize(author, identityWidth, 0.27f),
                new Vector2(content.X, content.Y + 64f), Vector2.Zero, 0.27f,
                palette.Primary * alpha, weight: UiFontWeight.Bold);
        }

        MaterialRect metadata = new(
            content.X + identityWidth + 28f,
            content.Y,
            content.Width - identityWidth - 28f,
            content.Height
        );
        float descriptionY = metadata.Y;
        if (selected.Tags.Count > 0) {
            descriptionY = RenderMetadataTags(selected.Tags,
                new MaterialRect(metadata.X, metadata.Y, metadata.Width, MetadataTagHeight * 2f + 8f),
                palette, alpha) + 8f;
        }
        string description = selected.Description.Length > 0
            ? selected.Description
            : UiText("microblocks_qol_chapter_no_description", "此地图没有提供描述");
        int maxLines = descriptionY > metadata.Y + MetadataTagHeight ? 2 : 3;
        List<string> lines = WrapText(description, metadata.Width, 0.25f, maxLines);
        for (int index = 0; index < lines.Count; index++) {
            SystemTtfFont.DrawVisual(lines[index], new Vector2(metadata.X, descriptionY + index * 25f),
                Vector2.Zero, 0.25f, palette.OnSurfaceVariant * alpha);
        }
    }

    private static float RenderMetadataTags(
        IReadOnlyList<ChapterMetadataTag> tags,
        MaterialRect bounds,
        MaterialPalette palette,
        float alpha
    ) {
        const float scale = 0.25f;
        const float gap = 8f;
        float x = bounds.X;
        float y = bounds.Y;
        float lastBottom = y;
        foreach (ChapterMetadataTag tag in tags) {
            string text = MaterialTextUtil.Ellipsize(
                tag.Text, Math.Min(280f, bounds.Width), scale, UiFontWeight.Bold);
            float width = Math.Min(bounds.Width,
                SystemTtfFont.MeasureVisible(text, scale, UiFontWeight.Bold).X + 24f);
            if (x > bounds.X && x + width > bounds.Right) {
                x = bounds.X;
                y += MetadataTagHeight + gap;
            }
            if (y + MetadataTagHeight > bounds.Bottom) break;

            Color fill = tag.FillColor ?? palette.Primary;
            Color foreground = tag.TextColor ?? ContrastColor(fill);
            Color border = tag.BorderColor ?? fill;
            MaterialUi.RoundedRect(x, y, width, MetadataTagHeight, MetadataTagHeight / 2f,
                fill * 0.92f * alpha);
            MaterialUi.RoundedOutline(x, y, width, MetadataTagHeight, MetadataTagHeight / 2f, 1f,
                border * alpha);
            SystemTtfFont.DrawVisual(text, new Vector2(x + width / 2f, y + MetadataTagHeight / 2f),
                new Vector2(0.5f), scale, foreground * alpha, weight: UiFontWeight.Bold);
            x += width + gap;
            lastBottom = y + MetadataTagHeight;
        }
        return lastBottom;
    }

    private static void RenderStatusPill(
        string text,
        AreaModeStats? mode,
        MaterialRect rect,
        MaterialPalette palette,
        float alpha
    ) {
        bool completed = mode?.Completed == true;
        bool inProgress = mode is not null && !completed;
        Color fill = completed
            ? palette.Primary
            : inProgress ? palette.SurfaceHighest : palette.SurfaceHigh;
        Color foreground = completed
            ? palette.OnPrimary
            : inProgress ? palette.Primary : palette.OnSurfaceVariant;
        MaterialUi.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, rect.Height / 2f,
            fill * (completed ? 0.96f : 0.82f) * alpha);
        if (!completed) {
            MaterialUi.RoundedOutline(rect.X, rect.Y, rect.Width, rect.Height, rect.Height / 2f,
                inProgress ? 2f : 1f, (inProgress ? palette.Primary : palette.Outline) * alpha);
        }
        SystemTtfFont.DrawVisual(text, rect.Center, new Vector2(0.5f), 0.29f, foreground * alpha,
            weight: completed || inProgress ? UiFontWeight.Bold : UiFontWeight.Regular);
    }

    private static void DrawStat(string texture, int value, Vector2 position, Color color, float alpha) {
        MTexture icon = GFX.Gui[texture];
        float scale = 20f / Math.Max(icon.Width, icon.Height);
        icon.DrawCentered(position, Color.White * alpha, scale);
        SystemTtfFont.DrawVisual(value.ToString(), position + new Vector2(16f, 0f), new Vector2(0f, 0.5f), 0.27f,
            color * alpha);
    }

    private static void RenderFooter(
        MaterialPalette palette,
        ChapterEntry? selected,
        ChapterLayout layout,
        float alpha
    ) {
        string detail = selected is null
            ? UiText("microblocks_qol_chapter_no_available", "没有可用章节")
            : selected.Sid;
        string shownDetail = MaterialTextUtil.Ellipsize(
            detail, layout.Footer.Width, 0.31f);
        SystemTtfFont.DrawVisual(shownDetail, new Vector2(layout.Footer.X, layout.Footer.Center.Y),
            new Vector2(0f, 0.5f), 0.31f,
            palette.OnSurfaceVariant * alpha);
    }

    private static void RenderMouseCursor(MaterialPalette palette, float alpha) {
        Vector2 mouse = MaterialTouch.Position;
        MaterialUiKit.Cursor(mouse, palette, alpha);
    }

    private int SidebarIndexAt(Vector2 mouse, ChapterLayout layout) {
        if (!layout.SidebarItems.Contains(mouse)) return -1;
        for (int index = 0; index < levelSets.Count; index++) {
            if (layout.SidebarItem(index, levelSetScroll.Offset).Contains(mouse)) return index;
        }
        return -1;
    }

    private int CardIndexAt(Vector2 mouse, ChapterLayout layout) {
        if (!layout.Cards.Contains(mouse)) return -1;
        foreach (CardPlacement placement in BuildContentLayout(layout, cardScroll.Offset).Cards) {
            if (placement.Rect.Contains(mouse)) return placement.EntryIndex;
        }
        return -1;
    }

    private int SectionHeaderIndexAt(Vector2 mouse, ChapterLayout layout) {
        if (!layout.Cards.Contains(mouse) || !string.IsNullOrWhiteSpace(searchText)) return -1;
        foreach (SectionPlacement placement in BuildContentLayout(layout, cardScroll.Offset).Sections) {
            if (placement.Rect.Contains(mouse)) return placement.SectionIndex;
        }
        return -1;
    }

    private void ToggleSection(int sectionIndex) {
        if (sectionIndex < 0 || sectionIndex >= sections.Count
            || !string.IsNullOrWhiteSpace(searchText)) return;
        string? previousSid = entries.Count == 0 ? null : entries[selectedIndex].Sid;
        ChapterSection section = sections[sectionIndex];
        SetSectionCollapsed(sectionIndex, !collapsedSections.GetValueOrDefault(section.Id),
            previousSid);
    }

    private void SetSectionCollapsed(
        int sectionIndex,
        bool collapsed,
        string? previousSid
    ) {
        ChapterSection section = sections[sectionIndex];
        collapsedSections[section.Id] = collapsed;
        Audio.Play(collapsed
            ? "event:/ui/world_map/icon/roll_left"
            : "event:/ui/world_map/icon/roll_right");
        RebuildVisibleEntries(previousSid, resetScroll: false);
    }

    private ChapterContentLayout BuildContentLayout(ChapterLayout layout, float scrollOffset) {
        List<CardPlacement> cards = [];
        List<SectionPlacement> headers = [];
        float cardWidth = (layout.Cards.Width - CardHorizontalGap * (Columns - 1)) / Columns;
        float y = layout.Cards.Y - scrollOffset;

        AddCardGrid(0, ungroupedEntries.Count);
        if (ungroupedEntries.Count > 0 && sections.Count > 0) y += GroupVerticalGap;

        for (int sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++) {
            ChapterSection section = sections[sectionIndex];
            headers.Add(new SectionPlacement(sectionIndex,
                new MaterialRect(layout.Cards.X, y, layout.Cards.Width, GroupHeaderHeight)));
            y += GroupHeaderHeight + GroupHeaderGap;
            AddCardGrid(section.VisibleStart, section.VisibleCount);
            if (sectionIndex < sections.Count - 1) y += GroupVerticalGap;
        }

        float contentHeight = Math.Max(0f, y - (layout.Cards.Y - scrollOffset));
        return new ChapterContentLayout(cards, headers, contentHeight);

        void AddCardGrid(int start, int count) {
            if (count <= 0) return;
            int rows = (count + Columns - 1) / Columns;
            for (int localIndex = 0; localIndex < count; localIndex++) {
                int column = localIndex % Columns;
                int row = localIndex / Columns;
                cards.Add(new CardPlacement(start + localIndex, new MaterialRect(
                    layout.Cards.X + column * (cardWidth + CardHorizontalGap),
                    y + row * (CardHeight + CardVerticalGap),
                    cardWidth,
                    CardHeight
                )));
            }
            y += rows * CardHeight + (rows - 1) * CardVerticalGap;
        }
    }

    private float MaxCardScroll(ChapterLayout layout) {
        return Math.Max(0f, BuildContentLayout(layout, 0f).ContentHeight - layout.Cards.Height);
    }

    private float MaxLevelSetScroll(ChapterLayout layout) {
        float contentHeight = levelSets.Count == 0
            ? 0f
            : levelSets.Count * ChapterLayout.SidebarItemHeight
                + (levelSets.Count - 1) * ChapterLayout.SidebarItemGap;
        return Math.Max(0f, contentHeight - layout.SidebarItems.Height);
    }

    private void RenderSearchBox(MaterialPalette palette, ChapterLayout layout, float alpha) {
        MaterialRect search = layout.Search;
        Color fill = searchFocused ? palette.SurfaceHighest : palette.SurfaceHigh;
        MaterialUi.RoundedRect(search.X, search.Y, search.Width, search.Height,
            search.Height / 2f, fill * alpha);
        motion.RenderStateLayer("chapter.search", search, search.Height / 2f, palette.Primary, alpha);
        if (searchFocused) {
            MaterialUi.RoundedOutline(search.X, search.Y, search.Width, search.Height,
                search.Height / 2f, 2f, palette.Primary * (0.88f * alpha));
        }
        MaterialUiKit.Icon("search", new Vector2(search.X + 28f, search.Center.Y), 23f,
            searchFocused ? palette.Primary : palette.OnSurfaceVariant, alpha);
        string shown = searchText + (searchFocused ? imeText : "");
        string text = shown.Length == 0 ? "搜索地图、地图集或 SID…" : shown;
        Color color = shown.Length == 0 ? palette.OnSurfaceVariant * 0.68f : palette.OnSurface;
        Vector2 textPosition = new(search.X + 56f, search.Center.Y);
        string visibleText = MaterialTextUtil.Ellipsize(text, search.Width - 78f, 0.36f);
        SystemTtfFont.DrawVisual(visibleText, textPosition, new Vector2(0f, 0.5f), 0.36f, color * alpha);
        if (searchFocused && Scene.BetweenInterval(0.5f)) {
            string visibleInput = MaterialTextUtil.Ellipsize(shown, search.Width - 78f, 0.36f);
            float caretX = textPosition.X + SystemTtfFont.MeasureVisible(visibleInput, 0.36f).X + 2f;
            MaterialUi.Line(new Vector2(caretX, search.Y + 11f),
                new Vector2(caretX, search.Bottom - 11f), 2f, palette.Primary * alpha);
        }
        float xScale = Engine.ViewWidth / ScreenWidth;
        float yScale = Engine.ViewHeight / ScreenHeight;
        TextInputEXT.SetInputRectangle(new Rectangle(
            (int)(layout.Search.X * xScale),
            (int)(layout.Search.Y * yScale),
            Math.Max(1, (int)(layout.Search.Width * xScale)),
            Math.Max(1, (int)(layout.Search.Height * yScale))
        ));
    }

    private void OnTextInput(char character) {
        if (!searchFocused) return;
        if (character == '\b') {
            if (searchText.Length > 0) searchText = searchText[..^1];
        } else if (!char.IsControl(character) && searchText.Length < 80) {
            searchText += character;
        } else {
            return;
        }
        imeText = "";
        FilterEntries(keepArea: true);
    }

    private void SetSearchFocused(bool focused) {
        if (searchFocused == focused) return;
        searchFocused = focused;
        if (focused) {
            TextInput.OnInput += OnTextInput;
            TextInputEXT.TextEditing += OnTextEditing;
            MaterialTextInputFocus.Focus(this);
        } else {
            TextInput.OnInput -= OnTextInput;
            TextInputEXT.TextEditing -= OnTextEditing;
            MaterialTextInputFocus.Blur(this);
            imeText = "";
        }
    }

    private void OnTextEditing(string? text, int start, int length) {
        _ = start;
        _ = length;
        if (searchFocused) imeText = text ?? "";
    }

    private static string CleanGroupTitle(string collabName, string fallback) {
        string localized = Dialog.CleanLevelSet(collabName);
        if (!string.IsNullOrWhiteSpace(localized)
            && !string.Equals(localized, collabName, StringComparison.Ordinal)) return localized;
        string value = System.Text.RegularExpressions.Regex.Replace(collabName, "(?<=[a-z])(?=[A-Z])", " ");
        value = System.Text.RegularExpressions.Regex.Replace(value, "(?<=[0-9])(?=[A-Z][a-z])", " ");
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string CleanName(string dialogKey, string fallback) {
        string value = Dialog.Clean(dialogKey ?? "");
        return string.IsNullOrWhiteSpace(value) || value == dialogKey ? fallback : value;
    }

    private static ChapterMetadata ResolveMetadata(AreaData area) {
        string key = area.Name ?? "";
        string author = CleanDialogValue(key + "_author", multiline: false);
        string description = FirstDialogValue(
            multiline: true,
            key + "_description",
            key + "_collabcredits"
        );
        List<ChapterMetadataTag> tags = ResolveMetadataTags(key + "_collabcreditstags");
        return new ChapterMetadata(author, description, tags);
    }

    private static string FirstDialogValue(bool multiline, params string[] keys) {
        foreach (string key in keys) {
            string value = CleanDialogValue(key, multiline);
            if (value.Length > 0) return value;
        }
        return "";
    }

    private static string CleanDialogValue(string key, bool multiline) {
        if (key.Length == 0 || !Dialog.Has(key)) return "";
        string cleaned = Dialog.Clean(key).Replace("\r", "");
        IEnumerable<string> lines = cleaned.Split('\n')
            .Select(line => WhitespaceRegex.Replace(line, " ").Trim())
            .Where(line => line.Length > 0);
        return multiline ? string.Join("\n", lines) : string.Join(" ", lines);
    }

    private static List<ChapterMetadataTag> ResolveMetadataTags(string key) {
        if (key.Length == 0 || !Dialog.Has(key)) return [];
        string raw = Dialog.Get(key).Replace("{break}", "\n").Replace("{n}", "\n");
        List<ChapterMetadataTag> tags = [];
        foreach (string rawLine in raw.Split('\n')) {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;
            Color? textColor = null;
            Color? borderColor = null;
            Color? fillColor = null;
            Match match = CollabTagRegex.Match(line);
            if (match.Success) {
                line = match.Groups["text"].Value.Trim();
                CaptureCollection keys = match.Groups["key"].Captures;
                CaptureCollection values = match.Groups["value"].Captures;
                for (int index = 0; index < Math.Min(keys.Count, values.Count); index++) {
                    Color? color = TryParseColor(values[index].Value);
                    switch (keys[index].Value) {
                        case "color": textColor = color; break;
                        case "borderColor": borderColor = color; break;
                        case "fillColor": fillColor = color; break;
                    }
                }
            } else {
                line = DialogCommandRegex.Replace(line, "").Trim();
            }
            if (line.Length > 0) tags.Add(new ChapterMetadataTag(line, textColor, borderColor, fillColor));
        }
        return tags;
    }

    private static Color? TryParseColor(string value) {
        try {
            return Calc.HexToColor(value);
        } catch {
            return null;
        }
    }

    private static Color ContrastColor(Color background) {
        float luminance = (background.R * 0.2126f + background.G * 0.7152f + background.B * 0.0722f) / 255f;
        return luminance > 0.58f ? new Color(28, 24, 31) : Color.White;
    }

    private static List<string> WrapText(string value, float maxWidth, float scale, int maxLines) {
        List<string> lines = [];
        bool truncated = false;
        foreach (string paragraph in value.Replace("\r", "").Split('\n')) {
            string remaining = paragraph.Trim();
            if (remaining.Length == 0) continue;
            while (remaining.Length > 0) {
                if (lines.Count >= maxLines) {
                    truncated = true;
                    break;
                }
                int take = remaining.Length;
                while (take > 1 && SystemTtfFont.MeasureVisible(remaining[..take], scale).X > maxWidth) take--;
                if (take < remaining.Length) {
                    int whitespace = remaining.LastIndexOf(' ', take - 1, take);
                    if (whitespace > 0) take = whitespace;
                }
                lines.Add(remaining[..take].Trim());
                remaining = remaining[take..].TrimStart();
            }
            if (truncated) break;
        }
        if (truncated && lines.Count > 0)
            lines[^1] = MaterialTextUtil.Ellipsize(lines[^1] + "…", maxWidth, scale);
        return lines;
    }

    private static string UiText(string key, string fallback) {
        string value = Dialog.Clean(key);
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }

    private static IEnumerator DetourGotoRoutine(GotoRoutineOrig orig, Overworld self, Oui next) {
        if (next is OuiChapterSelect vanilla
            && MicroblocksQolUtilsModule.Settings.ReplaceChapterSelect
            && (replaceNextChapterSelect || materialSessionActive && self.Current is OuiChapterPanel)
            && !IsAutoAdvancing(vanilla)
            && self.GetUI<MaterialChapterSelect>() is { } material) {
            next = material;
        }
        return orig(self, next);
    }

    private static void FileSelectContinue(
        On.Celeste.OuiFileSelectSlot.orig_OnContinueSelected orig,
        OuiFileSelectSlot self
    ) {
        replaceNextChapterSelect = true;
        try {
            orig(self);
        } finally {
            replaceNextChapterSelect = false;
        }
    }

    private static bool IsAutoAdvancing(OuiChapterSelect select) {
        try {
            return (bool?)typeof(OuiChapterSelect)
                .GetField("autoAdvancing", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(select) == true;
        } catch {
            return false;
        }
    }

    private delegate IEnumerator GotoRoutineOrig(Overworld self, Oui next);
    private delegate IEnumerator GotoRoutineDetour(GotoRoutineOrig orig, Overworld self, Oui next);

    private readonly record struct ChapterLayout(
        MaterialRect Frame,
        MaterialRect Header,
        MaterialRect Search,
        MaterialRect Sidebar,
        MaterialRect SidebarItems,
        MaterialRect Content,
        MaterialRect ContentHeader,
        MaterialRect Cards,
        MaterialRect Details,
        MaterialRect Footer
    ) {
        public const float SidebarHeaderHeight = 64f;
        public const float SidebarItemHeight = MaterialSplitPageLayout.TabHeight;
        public const float SidebarItemGap = MaterialSplitPageLayout.TabGap;

        public static ChapterLayout Create(float transition) {
            MaterialSplitPageLayout page = MaterialSplitPageLayout.Create(transition);
            MaterialRect[] content = MaterialLayout.Split(
                page.ContentBody,
                MaterialAxis.Vertical,
                14f,
                MaterialTrack.Flex(),
                MaterialTrack.Fixed(146f),
                MaterialTrack.Fixed(44f)
            );
            MaterialRect search = new(page.Header.Right - 620f, page.Header.Center.Y - 27f, 620f, 54f);
            return new ChapterLayout(page.Frame, page.Header, search, page.Navigation,
                page.NavigationItems(SidebarHeaderHeight), page.Content, page.ContentHeader,
                content[0], content[1], content[2]);
        }

        public MaterialRect SidebarItem(int index, float scrollOffset) =>
            MaterialSplitPageLayout.Tab(SidebarItems, index, scrollOffset);

        public MaterialRect Card(int index, float scrollOffset) {
            float width = (Cards.Width - CardHorizontalGap * (Columns - 1)) / Columns;
            int column = index % Columns;
            int row = index / Columns;
            return new MaterialRect(
                Cards.X + column * (width + CardHorizontalGap),
                Cards.Y + row * (CardHeight + CardVerticalGap) - scrollOffset,
                width,
                CardHeight
            );
        }
    }

    private sealed record ChapterEntry(
        AreaData Area,
        string Sid,
        string LevelSet,
        string GroupId,
        string Title,
        string LevelSetTitle,
        string Badge,
        bool CollabLobby,
        string? LobbySid,
        string Author,
        string Description,
        List<ChapterMetadataTag> Tags
    );

    private sealed record ChapterMetadata(
        string Author,
        string Description,
        List<ChapterMetadataTag> Tags
    );

    private sealed record ChapterMetadataTag(
        string Text,
        Color? TextColor,
        Color? BorderColor,
        Color? FillColor
    );

    private sealed record ChapterSection(
        string Id,
        ChapterEntry Lobby,
        List<ChapterEntry> Entries,
        int TotalMapCount
    ) {
        public int VisibleStart { get; set; }
        public int VisibleCount { get; set; }
    }

    private sealed record CardPlacement(int EntryIndex, MaterialRect Rect);
    private sealed record SectionPlacement(int SectionIndex, MaterialRect Rect);
    private sealed record ChapterContentLayout(
        List<CardPlacement> Cards,
        List<SectionPlacement> Sections,
        float ContentHeight
    );

    private sealed record LevelSetEntry(string Id, string Title);
}
