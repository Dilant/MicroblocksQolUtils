using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class AutoRecorder {
    private const double MinimumClipSeconds = 0.02;
    private const int MusicTimelineDiscontinuityMilliseconds = 750;
    private const string FullRecordingsDirectory = "full";
    private const string DeathReplaysDirectory = "deaths";

    private static readonly List<RecordingClip> ActivePrefix = [];
    private static readonly List<RecordingClip> DeathReplayPrefix = [];
    private static readonly List<PendingDeathReplay> PendingDeathReplays = [];
    private static readonly object FinalizationProgressLock = new();
    private static readonly Dictionary<long, FinalizationProgressState> ActiveFinalizations = [];
    private static readonly Dictionary<string, FinalizationOutputProgressState> ActiveFinalizationOutputs =
        new(StringComparer.OrdinalIgnoreCase);
    private static NativeRoomRecording? current;
    private static NativeRoomRecording? deathReplayCurrent;
    private static RecordingTimelineSnapshot? respawnAnchor;
    private static Vector2? observedRespawnPoint;
    private static MusicPosition branchMusicStart;
    private static MusicPosition deathReplayMusicStart;
    private static double branchStartSeconds;
    private static double deathReplayBranchStartSeconds;
    private static bool branchSeamlessFromPrevious;
    private static bool deathReplayBranchSeamlessFromPrevious;
    private static string? recordingRoomName;
    private static bool recordingRoomBgmFollowsVideo;
    private static string branchRoomName = "";
    private static string deathReplayBranchRoomName = "";
    private static bool branchBgmFollowsVideo;
    private static bool deathReplayBranchBgmFollowsVideo;
    private static double? pauseResumeAfterMediaSeconds;
    private static double? deathReplayPauseResumeAfterMediaSeconds;
    private static string runKey = "";
    private static string areaSid = "";
    private static bool branchActive;
    private static bool waitingForStablePlayer;
    private static bool pauseSuspended;
    private static bool transitioningRoom;
    private static bool deathReplayBranchActive;
    private static bool deathReplayWaitingForStablePlayer;
    private static bool deathReplayPauseSuspended;
    private static bool deathReplayFinalizeRequested;
    private static bool fullRecordingEnabled;
    private static bool reconstructBgm;
    private static readonly RecordingSessionState SessionState = new();
    private static PendingRecordingStop? pendingRecordingStop;
    private static Scene? continuingGoldenExit;
    private static int finalizingCount;
    private static int cleanupRunning;
    private static long nextFinalizationId;
    private static long finalizationUpdateSequence;
    private static string lastOutput = "";
    private static string lastCleanupStatus = "—";

    public static bool ManualMode => SessionState.ManualMode;
    public static bool AutoRecordingSuppressed => SessionState.Suppressed;
    public static bool ContinuingAfterGoldenDeath => SessionState.ContinuingAfterDeath;
    public static bool IsRecording => current is not null;
    internal static bool CanSaveTransitionTimeline => current is not null && branchActive
        && !waitingForStablePlayer && !pauseSuspended && !transitioningRoom
        && !SessionState.ContinuingAfterDeath;
    public static bool IsDeathReplayRecording => deathReplayCurrent is not null;
    public static bool IsFullRecordingEnabled => fullRecordingEnabled;
    public static bool IsFinalizing => Volatile.Read(ref finalizingCount) > 0;
    public static bool IsCleaning => Volatile.Read(ref cleanupRunning) != 0;
    public static double CurrentSeconds => current?.MediaTimeSeconds ?? 0;
    public static double DisplaySeconds => CurrentSeconds;
    public static bool HasAudioTap => StatusRecording?.HasAudioTap ?? false;
    public static ulong AudioFramesCaptured => StatusRecording?.Statistics.AudioFramesCaptured ?? 0;
    public static ulong AudioChunksDropped => StatusRecording?.Statistics.AudioChunksDropped ?? 0;
    public static double DeathReplaySeconds => Math.Min(
        deathReplayCurrent?.MediaTimeSeconds ?? 0,
        Math.Clamp(MicroblocksQolUtilsModule.Settings.DeathReplayBufferSeconds, 10, 60)
    );
    public static string CurrentPath => current?.Path ?? "";
    public static string LastOutput => lastOutput;
    public static string LastCleanupStatus => lastCleanupStatus;
    public static int PendingDeathReplayCount => PendingDeathReplays.Count;
    private static NativeRoomRecording? StatusRecording => current ?? deathReplayCurrent;
    public static double FinalizationProgress {
        get {
            lock (FinalizationProgressLock) {
                double totalWeight = ActiveFinalizations.Values.Sum(state => state.Weight);
                return totalWeight <= 0d
                    ? 0d
                    : ActiveFinalizations.Values.Sum(state => state.Progress * state.Weight) / totalWeight;
            }
        }
    }
    public static string FinalizationDescription {
        get {
            lock (FinalizationProgressLock) {
                return ActiveFinalizations.Values
                    .OrderByDescending(state => state.UpdateSequence)
                    .Select(state => state.Description)
                    .FirstOrDefault() ?? "视频";
            }
        }
    }
    internal static bool TryGetFinalizationProgress(string output, out double progress, out string description) {
        lock (FinalizationProgressLock) {
            if (ActiveFinalizationOutputs.TryGetValue(Path.GetFullPath(output), out FinalizationOutputProgressState? state)) {
                progress = state.Progress;
                description = state.Description;
                return true;
            }
        }
        progress = 0d;
        description = "";
        return false;
    }
    public static string RecordingRoot => ResolveRecordingRoot();
    public static string FullRecordingRoot => Path.Combine(ResolveRecordingRoot(), FullRecordingsDirectory);
    public static string AutoRecordingRoot => Path.Combine(ResolveRecordingRoot(), "auto");
    public static string DeathReplayRoot => Path.Combine(ResolveRecordingRoot(), DeathReplaysDirectory);

    public static void Load(string directory) {
        _ = directory;
        On.Celeste.Player.Die += PlayerDie;
        On.Celeste.Level.TransitionTo += LevelTransitionTo;
        On.Celeste.Strawberry.OnCollect += StrawberryOnCollect;
        On.Celeste.Level.CompleteArea_bool_bool_bool += CompleteArea;
        Everest.Events.Level.OnEnd += LevelEnd;
        Everest.Events.Level.OnExit += LevelExitStarted;
        SpeedrunToolBridge.Load();
        RecordingDeathRecovery.Load();
        CleanupRecordings();
    }

    public static void Unload() {
        SessionState.ResetRun();
        RecordingDeathRecovery.Unload();
        SpeedrunToolBridge.Unload();
        Everest.Events.Level.OnEnd -= LevelEnd;
        Everest.Events.Level.OnExit -= LevelExitStarted;
        On.Celeste.Strawberry.OnCollect -= StrawberryOnCollect;
        On.Celeste.Level.CompleteArea_bool_bool_bool -= CompleteArea;
        On.Celeste.Level.TransitionTo -= LevelTransitionTo;
        On.Celeste.Player.Die -= PlayerDie;
        StopAndReset(deleteSource: true);
    }

    public static void Update(Level level) {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        Player? player = level.Tracker.GetEntity<Player>();
        if (player is null) return;
        string key = RunKey(level);
        if (!string.Equals(key, runKey, StringComparison.Ordinal)) {
            if (runKey.Length > 0) StopAndReset(deleteSource: true);
            BeginRun(level);
        }

        ObserveRoom(level);
        bool carryingGolden = !player.Dead && CarriesGolden(player);
        SessionState.Observe(settings.AutomaticRecording, carryingGolden);
        // Mode changes are explicit user actions; save an existing auto session rather
        // than discarding it or silently changing its ownership.
        if (current is not null && SessionState.Automatic
            && (settings.AutomaticRecording == AutoRecordingMode.Off
                || (SessionState.Kind == RecordingSessionKind.AutoGolden) != (settings.AutomaticRecording == AutoRecordingMode.Golden)))
            RequestStop(level, save: true);
        fullRecordingEnabled = SessionState.Next(carryingGolden) != RecordingSessionKind.None;
        UpdateFullRecording(level, player, settings);
        UpdateDeathReplayRecording(level, player, settings);
    }

    public static void AfterEngineUpdate() {
        FlushPendingRecordingStop();
        if (deathReplayFinalizeRequested) {
            deathReplayFinalizeRequested = false;
            // Finish the failed attempt before an internal load replaces its entities.
            FinalizeDeathReplayCapture();
        }
        RecordingDeathRecovery.AfterEngineUpdate();
        RecordingTransitionAutoSave.AfterEngineUpdate();
    }

    private static void UpdateFullRecording(Level level, Player player, QolSettings settings) {
        if (pendingRecordingStop is not null) return;
        if (!fullRecordingEnabled) {
            if (current is not null) DiscardCurrentRecording();
            return;
        }
        if (level.Paused) {
            SuspendForPause();
            return;
        }
        if (current is null && PlayerIsRecordable(level, player)) StartRunRecording(level);
        NativeRoomRecording? recording = current;
        if (recording is null) return;

        if (pauseSuspended && PlayerIsRecordable(level, player) && PauseOverlayCleared(level)) {
            ResumeFullRecordingAfterPause(recording);
        } else if (waitingForStablePlayer && PlayerIsRecordable(level, player)) {
            StartBranchAtCurrentTime();
        }

        if (branchActive && reconstructBgm)
            ObserveMusicTimeline(recording);

        if (transitioningRoom) {
            if (level.Transitioning) return;
            transitioningRoom = false;
            respawnAnchor = new RecordingTimelineSnapshot(CaptureCurrentClips(recording));
            observedRespawnPoint = level.Session.RespawnPoint;
        }

        Vector2? respawn = level.Session.RespawnPoint;
        if (branchActive && RespawnPointChanged(observedRespawnPoint, respawn)) {
            respawnAnchor = new RecordingTimelineSnapshot(CaptureCurrentClips(recording));
            RecordingTransitionAutoSave.Queue(level, level.Session.Level);
        }
        observedRespawnPoint = respawn;
    }

    private static void UpdateDeathReplayRecording(Level level, Player player, QolSettings settings) {
        if (!settings.DeathReplayEnabled) {
            if (deathReplayCurrent is not null || deathReplayFinalizeRequested)
                DiscardDeathReplayRecording();
            return;
        }
        if (deathReplayFinalizeRequested) return;
        if (level.Paused) {
            SuspendDeathReplayForPause();
            return;
        }
        if (deathReplayCurrent is null && PlayerIsRecordable(level, player))
            StartDeathReplayRecording();
        NativeRoomRecording? recording = deathReplayCurrent;
        if (recording is null) return;

        if (deathReplayPauseSuspended && PlayerIsRecordable(level, player) && PauseOverlayCleared(level)) {
            ResumeDeathReplayAfterPause(recording);
        } else if (deathReplayWaitingForStablePlayer && PlayerIsRecordable(level, player)) {
            StartDeathReplayBranchAtCurrentTime();
        }

        if (deathReplayBranchActive && reconstructBgm)
            ObserveDeathReplayMusicTimeline(recording);
    }

    public static void StartManual() => SessionState.RequestManual();

    // Legacy API name retained for console/interop; stops the active session of either origin.
    public static void StopManual(Level? level, bool save) {
        if (current is null) {
            SessionState.Stop();
            return;
        }
        RequestStop(level, save);
    }

    private static void RequestStop(Level? level, bool save) {
        if (pendingRecordingStop is not null || current is null) return;
        // Freeze the timeline now, but detach FMOD only after EntityList.Update.
        List<RecordingClip> clips = CaptureCurrentClips(current);
        ActivePrefix.Clear();
        ActivePrefix.AddRange(clips);
        branchActive = false;
        pendingRecordingStop = new PendingRecordingStop(level, save);
    }

    private static void FlushPendingRecordingStop() {
        PendingRecordingStop? request = pendingRecordingStop;
        pendingRecordingStop = null;
        if (request is null) return;
        if (request.Save && request.Level is not null) FinalizeCurrent(request.Level);
        else DiscardCurrentRecording();
        SessionState.Stop();
    }

    public static void CleanupRecordings() {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        int fullRetentionCount = Math.Max(0, settings.RecordingRetentionCount);
        int autoRetentionCount = Math.Max(0, settings.AutoRecordingRetentionCount);
        int deathRetentionCount = Math.Max(0, settings.DeathReplayRetentionCount);
        if (fullRetentionCount == 0 && autoRetentionCount == 0 && deathRetentionCount == 0) {
            lastCleanupStatus = "未启用保留上限";
            return;
        }
        if (Interlocked.Exchange(ref cleanupRunning, 1) != 0) return;
        lastCleanupStatus = "清理中";
        _ = Task.Run(() => {
            try {
                string root = ResolveRecordingRoot();
                int deleted = 0;
                if (fullRetentionCount > 0) {
                    deleted += DeleteOldCompletedRecordings(root, RecordingLibraryKind.Full, fullRetentionCount);
                }
                if (autoRetentionCount > 0) {
                    deleted += DeleteOldCompletedRecordings(root, RecordingLibraryKind.Automatic, autoRetentionCount);
                }
                if (deathRetentionCount > 0) {
                    deleted += DeleteOldCompletedRecordings(root, RecordingLibraryKind.DeathReplay, deathRetentionCount);
                }
                lastCleanupStatus = deleted == 0 ? "无需清理" : $"已清理 {deleted} 个";
            } catch (Exception exception) {
                lastCleanupStatus = "清理失败";
                Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/Cleanup");
            } finally {
                Volatile.Write(ref cleanupRunning, 0);
            }
        });
    }

    public static RecordingTimelineSnapshot? CaptureTimeline(Level level) {
        NativeRoomRecording? recording = current;
        if (recording is null
            || !branchActive
            || !string.Equals(RunKey(level), runKey, StringComparison.Ordinal)) {
            return null;
        }
        return new RecordingTimelineSnapshot(
            CaptureCurrentClips(recording),
            respawnAnchor?.Clips.ToArray()
        );
    }

    public static void RestoreTimeline(Level level, RecordingTimelineSnapshot snapshot) {
        NativeRoomRecording? recording = current;
        if (recording is null || pendingRecordingStop is not null) return;
        if (!string.Equals(RunKey(level), runKey, StringComparison.Ordinal)) return;
        if (snapshot.Clips.Any(clip => !string.Equals(clip.Source, recording.Path, StringComparison.OrdinalIgnoreCase))) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Recorder", "Ignored SpeedrunTool timeline from another recording session.");
            return;
        }
        ActivePrefix.Clear();
        ActivePrefix.AddRange(snapshot.Clips);
        respawnAnchor = snapshot.RespawnAnchorClips is null
            ? null
            : new RecordingTimelineSnapshot(snapshot.RespawnAnchorClips.ToArray());
        branchActive = false;
        branchSeamlessFromPrevious = false;
        waitingForStablePlayer = true;
        pauseSuspended = false;
        transitioningRoom = false;
        observedRespawnPoint = level.Session.RespawnPoint;
    }

    private static PlayerDeadBody? PlayerDie(
        On.Celeste.Player.orig_Die orig,
        Player self,
        Vector2 direction,
        bool evenIfInvincible,
        bool registerDeathInStats
    ) {
        PlayerDeadBody? body = orig(self, direction, evenIfInvincible, registerDeathInStats);
        if (body is null) return body;
        RecordingTransitionAutoSave.Cancel();

        if (deathReplayCurrent is not null) {
            QueueDeathReplay(self, deathReplayCurrent);
            deathReplayBranchActive = false;
            deathReplayBranchSeamlessFromPrevious = false;
            deathReplayWaitingForStablePlayer = true;
            deathReplayPauseSuspended = false;
            // Player.Die runs from inside EntityList.Update; the capture is stopped later by
            // AfterEngineUpdate rather than doing synchronous FMOD teardown in this hook.
            deathReplayFinalizeRequested = true;
        }

        if (current is null || pendingRecordingStop is not null) return body;
        RecordingDeathAction action = SessionState.Die();
        if (action is RecordingDeathAction.Save or RecordingDeathAction.Discard) {
            RequestStop(self.Scene as Level, save: action == RecordingDeathAction.Save);
            return body;
        }
        // Continuing a failed golden attempt is intentionally uncut: keep the death
        // and respawn, and stay latched even after the berry leaves the followers.
        if (action == RecordingDeathAction.Continue) {
            RecordingTransitionAutoSave.Reset();
            return body;
        }
        ActivePrefix.Clear();
        if (respawnAnchor is not null) ActivePrefix.AddRange(respawnAnchor.Clips);
        branchActive = false;
        branchSeamlessFromPrevious = false;
        waitingForStablePlayer = true;
        pauseSuspended = false;
        return body;
    }

    private static void LevelTransitionTo(
        On.Celeste.Level.orig_TransitionTo orig,
        Level self,
        LevelData next,
        Vector2 direction
    ) {
        orig(self, next, direction);
        if (current is not null) {
            transitioningRoom = true;
            RecordingTransitionAutoSave.Queue(self, next.Name);
        }
    }

    private static void StrawberryOnCollect(On.Celeste.Strawberry.orig_OnCollect orig, Strawberry self) {
        bool golden = self.Golden && self.Follower.Leader is not null;
        Level? level = self.Scene as Level;
        orig(self);
        if (golden && level is not null && SessionState.CollectGolden()) RequestStop(level, save: true);
    }

    private static ScreenWipe CompleteArea(On.Celeste.Level.orig_CompleteArea_bool_bool_bool orig,
        Level self, bool spotlightWipe, bool skipScreenWipe, bool skipCompleteScreen) {
        // RegisterAreaComplete is also used to bank collectibles before a chapter
        // actually ends. CompleteArea is the real chapter-exit boundary.
        SessionState.CompleteChapter();
        RequestStop(self, save: true);
        return orig(self, spotlightWipe, skipScreenWipe, skipCompleteScreen);
    }

    private static void LevelExitStarted(Level level, LevelExit exit, LevelExit.Mode mode,
        Session session, HiresSnow snow) {
        // Golden deaths rebuild the entire Level, unlike ordinary room respawns.
        // Carry only the explicitly continued recording through that exact exit.
        continuingGoldenExit = mode == LevelExit.Mode.GoldenBerryRestart
            && SessionState.ContinuingAfterDeath ? exit : null;
    }

    private static void LevelEnd(
        Level level,
        Scene nextScene,
        ref bool shouldReloadPortraits,
        ref bool shouldDissociateEntities
    ) {
        if (current is not null && ReferenceEquals(nextScene, continuingGoldenExit)) {
            continuingGoldenExit = null;
            RecordingTransitionAutoSave.Reset();
            FinalizeDeathReplayCapture();
            respawnAnchor = null;
            observedRespawnPoint = null;
            transitioningRoom = false;
            return;
        }
        // Some modded exits bypass CompleteArea; do not lose an already completed run.
        if (current is not null && level.Completed) RequestStop(level, save: true);
        FlushPendingRecordingStop();
        _ = nextScene;
        _ = shouldReloadPortraits;
        _ = shouldDissociateEntities;
        if (current is not null || deathReplayCurrent is not null || runKey.Length > 0)
            StopAndReset(deleteSource: true);
    }

    private static void BeginRun(Level level) {
        SessionState.ResetRun(keepManualRequest: true);
        runKey = RunKey(level);
        areaSid = level.Session.Area.SID;
        observedRespawnPoint = level.Session.RespawnPoint;
        respawnAnchor = null;
        ActivePrefix.Clear();
        branchActive = false;
        branchSeamlessFromPrevious = false;
        waitingForStablePlayer = false;
        pauseSuspended = false;
        pauseResumeAfterMediaSeconds = null;
        transitioningRoom = false;
        ResetDeathReplayState(waitForStablePlayer: false);
        fullRecordingEnabled = false;
        // The selected mode is run-wide; rhythm exceptions are attached to individual clips.
        reconstructBgm = MicroblocksQolUtilsModule.Settings.BgmMode == BgmRecordingMode.SfxOnlyWithPostMix;
        recordingRoomName = null;
    }

    private static void StartRunRecording(Level level) {
        string tempRoot = Path.Combine(ResolveRecordingRoot(), ".working", Sanitize(runKey));
        Directory.CreateDirectory(tempRoot);
        string path = Path.Combine(tempRoot, $"full-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.mkv");
        current = NativeRoomRecording.Start(path);
        if (current is null) return;
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        SessionState.Start(SessionState.Next(CarriesGolden(level.Tracker.GetEntity<Player>())),
            settings.GoldenRecordingEnd, settings.GoldenRecordingDeath);
        ActivePrefix.Clear();
        respawnAnchor = null;
        observedRespawnPoint = level.Session.RespawnPoint;
        StartBranchAtCurrentTime();
        RecordingTransitionAutoSave.Queue(level, level.Session.Level);
    }

    private static void StartDeathReplayRecording() {
        string tempRoot = Path.Combine(ResolveRecordingRoot(), ".working", Sanitize(runKey));
        Directory.CreateDirectory(tempRoot);
        string path = Path.Combine(tempRoot,
            $"death-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.mkv");
        deathReplayCurrent = NativeRoomRecording.Start(path);
        if (deathReplayCurrent is null) return;
        ResetDeathReplayState(waitForStablePlayer: false, keepRecording: true);
        StartDeathReplayBranchAtCurrentTime();
    }

    private static void StartBranchAtCurrentTime(bool seamlessFromPrevious = false) {
        NativeRoomRecording? recording = current;
        if (recording is null) return;
        branchStartSeconds = recording.MediaTimeSeconds;
        branchMusicStart = MusicPosition.Read();
        branchSeamlessFromPrevious = seamlessFromPrevious;
        branchRoomName = recordingRoomName ?? "";
        branchBgmFollowsVideo = recordingRoomBgmFollowsVideo;
        branchActive = true;
        waitingForStablePlayer = false;
    }

    private static void StartDeathReplayBranchAtCurrentTime(bool seamlessFromPrevious = false) {
        NativeRoomRecording? recording = deathReplayCurrent;
        if (recording is null) return;
        deathReplayBranchStartSeconds = recording.MediaTimeSeconds;
        deathReplayMusicStart = MusicPosition.Read();
        deathReplayBranchSeamlessFromPrevious = seamlessFromPrevious;
        deathReplayBranchRoomName = recordingRoomName ?? "";
        deathReplayBranchBgmFollowsVideo = recordingRoomBgmFollowsVideo;
        deathReplayBranchActive = true;
        deathReplayWaitingForStablePlayer = false;
    }

    private static void SuspendForPause() {
        if (pauseSuspended) return;
        NativeRoomRecording? recording = current;
        if (recording is not null && branchActive) {
            RecordingClip? completed = CurrentClip(recording.MediaTimeSeconds);
            if (completed is not null) ActivePrefix.Add(completed);
            branchActive = false;
        }
        pauseSuspended = true;
        pauseResumeAfterMediaSeconds = null;
    }

    private static void SuspendDeathReplayForPause() {
        if (deathReplayPauseSuspended) return;
        NativeRoomRecording? recording = deathReplayCurrent;
        if (recording is not null && deathReplayBranchActive) {
            RecordingClip? completed = CurrentDeathReplayClip(recording.MediaTimeSeconds);
            if (completed is not null) DeathReplayPrefix.Add(completed);
            deathReplayBranchActive = false;
        }
        deathReplayPauseSuspended = true;
        deathReplayPauseResumeAfterMediaSeconds = null;
    }

    private static void ResumeFullRecordingAfterPause(NativeRoomRecording recording) {
        double now = recording.MediaTimeSeconds;
        if (pauseResumeAfterMediaSeconds is not double clearedAt) {
            pauseResumeAfterMediaSeconds = now;
            return;
        }
        if (now - clearedAt < MinimumClipSeconds) return;
        pauseSuspended = false;
        pauseResumeAfterMediaSeconds = null;
        StartBranchAtCurrentTime(seamlessFromPrevious: true);
    }

    private static void ResumeDeathReplayAfterPause(NativeRoomRecording recording) {
        double now = recording.MediaTimeSeconds;
        if (deathReplayPauseResumeAfterMediaSeconds is not double clearedAt) {
            deathReplayPauseResumeAfterMediaSeconds = now;
            return;
        }
        if (now - clearedAt < MinimumClipSeconds) return;
        deathReplayPauseSuspended = false;
        deathReplayPauseResumeAfterMediaSeconds = null;
        StartDeathReplayBranchAtCurrentTime(seamlessFromPrevious: true);
    }

    private static void ObserveMusicTimeline(NativeRoomRecording recording) {
        double now = recording.MediaTimeSeconds;
        MusicPosition observed = MusicPosition.Read();
        bool eventChanged = !string.Equals(observed.Event, branchMusicStart.Event, StringComparison.Ordinal);
        int expectedTimeline = branchMusicStart.TimelineMilliseconds
            + (int)Math.Round(Math.Max(0, now - branchStartSeconds) * 1_000.0);
        bool timelineJumped = observed.Event.Length > 0
            && Math.Abs((long)observed.TimelineMilliseconds - expectedTimeline)
                > MusicTimelineDiscontinuityMilliseconds;
        if (!eventChanged && !timelineJumped) return;

        RecordingClip? completed = CurrentClip(now);
        if (completed is not null) ActivePrefix.Add(completed);
        branchStartSeconds = now;
        branchMusicStart = observed;
        branchSeamlessFromPrevious = false;
    }

    private static void ObserveDeathReplayMusicTimeline(NativeRoomRecording recording) {
        double now = recording.MediaTimeSeconds;
        MusicPosition observed = MusicPosition.Read();
        bool eventChanged = !string.Equals(observed.Event, deathReplayMusicStart.Event, StringComparison.Ordinal);
        int expectedTimeline = deathReplayMusicStart.TimelineMilliseconds
            + (int)Math.Round(Math.Max(0, now - deathReplayBranchStartSeconds) * 1_000.0);
        bool timelineJumped = observed.Event.Length > 0
            && Math.Abs((long)observed.TimelineMilliseconds - expectedTimeline)
                > MusicTimelineDiscontinuityMilliseconds;
        if (!eventChanged && !timelineJumped) return;

        RecordingClip? completed = CurrentDeathReplayClip(now);
        if (completed is not null) DeathReplayPrefix.Add(completed);
        deathReplayBranchStartSeconds = now;
        deathReplayMusicStart = observed;
        deathReplayBranchSeamlessFromPrevious = false;
    }

    private static void FinalizeCurrent(Level level) {
        NativeRoomRecording? recording = current;
        if (recording is null
            || !string.Equals(RunKey(level), runKey, StringComparison.Ordinal)) {
            return;
        }
        List<RecordingClip> clips = [.. ActivePrefix];
        if (branchActive) {
            RecordingClip? finalClip = CurrentClip(recording.MediaTimeSeconds);
            if (finalClip is not null) clips.Add(finalClip);
        }
        current = null;
        Task stop = recording.StopAsync();
        List<RecordingFinalizationJob> jobs = [];
        if (clips.Count > 0) {
            string output = Path.Combine(
                SessionState.Automatic ? AutoRecordingRoot : FullRecordingRoot,
                Sanitize(areaSid),
                $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Sanitize(areaSid)}-{Guid.NewGuid():N}.mp4"
            );
            lastOutput = output;
            jobs.Insert(0, new RecordingFinalizationJob(clips, output, SessionState.Automatic ? "自动录像" : "手动录像", reconstructBgm,
                MicroblocksQolUtilsModule.Settings.RecordingRemoveFreezeFrames));
        }
        FinishStoppedRecording(recording, stop, jobs);
        ResetFullRecordingState();
    }

    private static void QueueDeathReplay(Player player, NativeRoomRecording recording) {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (!settings.DeathReplayEnabled) return;

        double bufferSeconds = Math.Clamp(settings.DeathReplayBufferSeconds, 10, 60);
        List<RecordingClip> clips = CaptureRecentDeathReplayClips(recording, bufferSeconds);
        if (clips.Count == 0) return;

        Level? level = player.Scene as Level;
        PendingDeathReplays.Add(new PendingDeathReplay(
            clips,
            DateTime.Now,
            level?.Session.Area.SID ?? areaSid,
            level?.Session.Level ?? "room",
            reconstructBgm,
            MicroblocksQolUtilsModule.Settings.RecordingRemoveFreezeFrames
        ));
        int retentionCount = Math.Max(0, settings.DeathReplayRetentionCount);
        if (retentionCount > 0 && PendingDeathReplays.Count > retentionCount) {
            PendingDeathReplays.RemoveRange(0, PendingDeathReplays.Count - retentionCount);
        }
    }

    private static RecordingClip? CurrentClip(double endSeconds, double minimumDuration = MinimumClipSeconds) {
        NativeRoomRecording? recording = current;
        if (recording is null || !branchActive) return null;
        double duration = endSeconds - branchStartSeconds;
        if (duration <= 0 || duration < minimumDuration) return null;
        return new RecordingClip(
            recording.Path,
            Math.Max(0, branchStartSeconds),
            duration,
            branchMusicStart.Event,
            branchMusicStart.TimelineMilliseconds,
            branchSeamlessFromPrevious,
            branchBgmFollowsVideo,
            branchRoomName
        );
    }

    private static RecordingClip? CurrentDeathReplayClip(double endSeconds, double minimumDuration = MinimumClipSeconds) {
        NativeRoomRecording? recording = deathReplayCurrent;
        if (recording is null || !deathReplayBranchActive) return null;
        double duration = endSeconds - deathReplayBranchStartSeconds;
        if (duration <= 0 || duration < minimumDuration) return null;
        return new RecordingClip(
            recording.Path,
            Math.Max(0, deathReplayBranchStartSeconds),
            duration,
            deathReplayMusicStart.Event,
            deathReplayMusicStart.TimelineMilliseconds,
            deathReplayBranchSeamlessFromPrevious,
            deathReplayBranchBgmFollowsVideo,
            deathReplayBranchRoomName
        );
    }

    private static List<RecordingClip> CaptureCurrentClips(NativeRoomRecording recording) {
        List<RecordingClip> clips = [.. ActivePrefix];
        RecordingClip? currentClip = CurrentClip(recording.MediaTimeSeconds);
        if (currentClip is not null) clips.Add(currentClip);
        return clips;
    }

    private static List<RecordingClip> CaptureCurrentDeathReplayClips(NativeRoomRecording recording) {
        List<RecordingClip> clips = [.. DeathReplayPrefix];
        RecordingClip? currentClip = CurrentDeathReplayClip(recording.MediaTimeSeconds);
        if (currentClip is not null) clips.Add(currentClip);
        return clips;
    }

    private static List<RecordingClip> CaptureRecentDeathReplayClips(
        NativeRoomRecording recording,
        double seconds
    ) {
        return CaptureRecentClips(CaptureCurrentDeathReplayClips(recording), seconds);
    }

    private static List<RecordingClip> CaptureRecentClips(
        IReadOnlyList<RecordingClip> source,
        double seconds
    ) {
        List<RecordingClip> result = [];
        double remaining = Math.Max(0d, seconds);
        foreach (RecordingClip clip in source.Reverse()) {
            if (remaining <= 0) break;
            double duration = Math.Min(remaining, clip.DurationSeconds);
            if (duration <= 0) continue;
            result.Insert(0, clip.RetainTail(duration));
            remaining -= duration;
        }
        return result;
    }

    private static void FinalizeDeathReplayCapture() {
        NativeRoomRecording? recording = deathReplayCurrent;
        deathReplayCurrent = null;
        if (recording is not null) {
            FinishStoppedRecording(recording, recording.StopAsync(), TakeDeathReplayJobs());
        }
        ResetDeathReplayState(waitForStablePlayer: true);
    }

    private static bool CarriesGolden(Player? player) => player?.Leader.Followers
        .Any(follower => follower.Entity is Strawberry { Golden: true }) == true;

    private static bool PlayerIsRecordable(Level level, Player player) {
        // A freshly respawned Player is already non-dead while the respawn wipe/animation is
        // still running. Starting a retained branch there puts the tail of the death sequence
        // back into manually and automatically saved videos.
        return !player.Dead
            && !level.Transitioning
            && player.StateMachine.State != Player.StIntroRespawn;
    }

    private static bool PauseOverlayCleared(Level level) {
        return !level.Paused
            && QolSettingsOverlay.ActivePage is null
            && level.Entities.FindFirst<TextMenu>() is null
            && level.Entities.FindFirst<MaterialModOptions>() is null;
    }

    private static void ObserveRoom(Level level) {
        string room = level.Session.Level;
        if (recordingRoomName == room) return;
        // Close with the OLD room's metadata before selecting the new policy. This also
        // handles teleports and restored saves, not just normal TransitionTo calls.
        NativeRoomRecording? full = current;
        NativeRoomRecording? death = deathReplayCurrent;
        double fullTime = full?.MediaTimeSeconds ?? 0;
        double deathTime = death?.MediaTimeSeconds ?? 0;
        if (branchActive && CurrentClip(fullTime, minimumDuration: 0) is { } fullClip) ActivePrefix.Add(fullClip);
        if (deathReplayBranchActive && CurrentDeathReplayClip(deathTime, minimumDuration: 0) is { } deathClip)
            DeathReplayPrefix.Add(deathClip);

        recordingRoomName = room;
        recordingRoomBgmFollowsVideo = RhythmMapDetector.IsRhythmSensitive(level.Session.MapData, room);
        if (branchActive) {
            branchStartSeconds = fullTime;
            branchMusicStart = MusicPosition.Read();
            branchSeamlessFromPrevious = true;
            branchRoomName = room;
            branchBgmFollowsVideo = recordingRoomBgmFollowsVideo;
        }
        if (deathReplayBranchActive) {
            deathReplayBranchStartSeconds = deathTime;
            deathReplayMusicStart = MusicPosition.Read();
            deathReplayBranchSeamlessFromPrevious = true;
            deathReplayBranchRoomName = room;
            deathReplayBranchBgmFollowsVideo = recordingRoomBgmFollowsVideo;
        }
        if (reconstructBgm && recordingRoomBgmFollowsVideo)
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils/Recorder",
                $"Rhythm-sensitive room '{room}': only this room's clips keep captured music synchronized with video.");
    }

    private static void DiscardCurrentRecording() {
        RecordingTransitionAutoSave.Reset();
        NativeRoomRecording? recording = current;
        current = null;
        if (recording is not null) {
            FinishStoppedRecording(recording, recording.StopAsync(), []);
        }
        ActivePrefix.Clear();
        respawnAnchor = null;
        branchActive = false;
        branchSeamlessFromPrevious = false;
        waitingForStablePlayer = false;
        pauseSuspended = false;
        pauseResumeAfterMediaSeconds = null;
        transitioningRoom = false;
    }

    private static void DiscardDeathReplayRecording() {
        deathReplayFinalizeRequested = false;
        NativeRoomRecording? recording = deathReplayCurrent;
        deathReplayCurrent = null;
        if (recording is not null) FinishStoppedRecording(recording, recording.StopAsync(), []);
        PendingDeathReplays.Clear();
        ResetDeathReplayState(waitForStablePlayer: false);
    }

    private static void StopAndReset(bool deleteSource) {
        NativeRoomRecording? recording = current;
        NativeRoomRecording? deathRecording = deathReplayCurrent;
        current = null;
        deathReplayCurrent = null;
        if (recording is not null) {
            Task stop = recording.StopAsync();
            if (deleteSource) FinishStoppedRecording(recording, stop, []);
        }
        if (deathRecording is not null) {
            Task stop = deathRecording.StopAsync();
            if (deleteSource) FinishStoppedRecording(deathRecording, stop, TakeDeathReplayJobs());
        }
        pendingRecordingStop = null;
        continuingGoldenExit = null;
        SessionState.ResetRun();
        ResetTimelineState();
    }

    private static List<RecordingFinalizationJob> TakeDeathReplayJobs() {
        int retentionCount = Math.Max(0, MicroblocksQolUtilsModule.Settings.DeathReplayRetentionCount);
        IEnumerable<PendingDeathReplay> retained = retentionCount > 0
            ? PendingDeathReplays.TakeLast(retentionCount)
            : PendingDeathReplays;
        List<RecordingFinalizationJob> jobs = retained.Select(death => {
            string area = Sanitize(death.AreaSid);
            string room = Sanitize(death.Room);
            string unique = Guid.NewGuid().ToString("N")[..8];
            string fileName = $"{death.OccurredAt:yyyyMMdd-HHmmss-fff}-{room}-death-{unique}.mp4";
            string output = Path.Combine(DeathReplayRoot, area, fileName);
            return new RecordingFinalizationJob(death.Clips, output, "死亡回放", death.ReconstructBgm,
                death.RemoveFreezeFrames, PreferVideoCopy: true);
        }).ToList();
        PendingDeathReplays.Clear();
        return jobs;
    }

    private static void FinishStoppedRecording(
        NativeRoomRecording recording,
        Task stop,
        IReadOnlyList<RecordingFinalizationJob> jobs
    ) {
        string[] temporaryFiles = [recording.Path, recording.AudioPath, recording.BgmPath, recording.MusicEventsPath];
        if (jobs.Count == 0) {
            _ = stop.ContinueWith(_ => DeleteTemporaryFiles(temporaryFiles), TaskScheduler.Default);
            return;
        }

        long finalizationId = BeginFinalization(jobs);
        Interlocked.Increment(ref finalizingCount);
        _ = FinishStoppedRecordingAsync(stop, temporaryFiles, jobs, finalizationId);
    }

    private static async Task FinishStoppedRecordingAsync(
        Task stop,
        IReadOnlyCollection<string> temporaryFiles,
        IReadOnlyList<RecordingFinalizationJob> jobs,
        long finalizationId
    ) {
        bool completed = true;
        try {
            await stop.ConfigureAwait(false);
            double totalWeight = jobs.Sum(job => job.Weight);
            double completedWeight = 0d;
            foreach (RecordingFinalizationJob job in jobs) {
                double capturedCompletedWeight = completedWeight;
                if (!await NativeRecordingFinalizer.FinishAsync(
                    job.Clips,
                    job.Output,
                    job.Description,
                    job.ReconstructBgm,
                    job.RemoveFreezeFrames,
                    progress => UpdateFinalization(
                        finalizationId,
                        job.Output,
                        (capturedCompletedWeight + job.Weight * progress) / totalWeight,
                        progress,
                        job.Description
                    ),
                    preferVideoCopy: job.PreferVideoCopy
                ).ConfigureAwait(false)) {
                    completed = false;
                }
                completedWeight += job.Weight;
            }
            if (completed) DeleteTemporaryFiles(temporaryFiles);
            else {
                Logger.Log(
                    LogLevel.Warn,
                    "MicroblocksQolUtils/Recorder",
                    $"Finalization failed; preserved continuous recording files under {Path.GetDirectoryName(temporaryFiles.FirstOrDefault() ?? "")}"
                );
            }
            CleanupRecordings();
        } catch (Exception exception) {
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder");
        } finally {
            EndFinalization(finalizationId);
            Interlocked.Decrement(ref finalizingCount);
        }
    }

    private static long BeginFinalization(IReadOnlyList<RecordingFinalizationJob> jobs) {
        long id = Interlocked.Increment(ref nextFinalizationId);
        double weight = jobs.Sum(job => job.Weight);
        lock (FinalizationProgressLock) {
            ActiveFinalizations[id] = new FinalizationProgressState(
                weight,
                0d,
                jobs[0].Description,
                ++finalizationUpdateSequence
            );
            foreach (RecordingFinalizationJob job in jobs) {
                ActiveFinalizationOutputs[Path.GetFullPath(job.Output)] =
                    new FinalizationOutputProgressState(0d, job.Description, id);
            }
        }
        return id;
    }

    private static void UpdateFinalization(
        long id,
        string output,
        double progress,
        double outputProgress,
        string description
    ) {
        lock (FinalizationProgressLock) {
            if (!ActiveFinalizations.TryGetValue(id, out FinalizationProgressState? state)) return;
            state.Progress = Math.Clamp(progress, 0d, 1d);
            state.Description = description;
            state.UpdateSequence = ++finalizationUpdateSequence;
            string path = Path.GetFullPath(output);
            if (ActiveFinalizationOutputs.TryGetValue(path, out FinalizationOutputProgressState? outputState)
                && outputState.FinalizationId == id) {
                outputState.Progress = Math.Clamp(outputProgress, 0d, 1d);
                outputState.Description = description;
            }
        }
    }

    private static void EndFinalization(long id) {
        lock (FinalizationProgressLock) {
            ActiveFinalizations.Remove(id);
            foreach (string output in ActiveFinalizationOutputs
                         .Where(pair => pair.Value.FinalizationId == id)
                         .Select(pair => pair.Key)
                         .ToArray()) {
                ActiveFinalizationOutputs.Remove(output);
            }
        }
    }

    private static void DeleteTemporaryFiles(IEnumerable<string> files) {
        foreach (string file in files) {
            try { File.Delete(file); } catch { }
        }
    }

    private static void ResetTimelineState() {
        ResetFullRecordingState();
        runKey = "";
        areaSid = "";
        reconstructBgm = false;
        PendingDeathReplays.Clear();
        ResetDeathReplayState(waitForStablePlayer: false);
    }

    private static void ResetFullRecordingState() {
        RecordingTransitionAutoSave.Reset();
        ActivePrefix.Clear();
        respawnAnchor = null;
        observedRespawnPoint = null;
        branchStartSeconds = 0;
        branchMusicStart = default;
        branchActive = false;
        branchSeamlessFromPrevious = false;
        waitingForStablePlayer = false;
        pauseSuspended = false;
        pauseResumeAfterMediaSeconds = null;
        transitioningRoom = false;
        fullRecordingEnabled = false;
    }

    private static void ResetDeathReplayState(bool waitForStablePlayer, bool keepRecording = false) {
        DeathReplayPrefix.Clear();
        deathReplayBranchStartSeconds = 0;
        deathReplayMusicStart = default;
        deathReplayBranchActive = false;
        deathReplayBranchSeamlessFromPrevious = false;
        deathReplayWaitingForStablePlayer = waitForStablePlayer;
        deathReplayPauseSuspended = false;
        deathReplayPauseResumeAfterMediaSeconds = null;
        deathReplayFinalizeRequested = false;
        if (!keepRecording) deathReplayCurrent = null;
    }

    private static int DeleteOldCompletedRecordings(
        string root,
        RecordingLibraryKind kind,
        int retentionCount
    ) {
        if (!Directory.Exists(root)) return 0;
        FileInfo[] completed = Directory
            .EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => File.Exists(file.FullName + ".timeline.json")
                && RecordingLibrary.KindOf(root, file.FullName) == kind)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        int deleted = 0;
        foreach (FileInfo file in completed.Skip(retentionCount)) {
            try {
                File.Delete(file.FullName);
                try { File.Delete(file.FullName + ".timeline.json"); } catch { }
                deleted++;
            } catch (Exception exception) {
                Logger.Log(
                    LogLevel.Warn,
                    "MicroblocksQolUtils/Recorder/Cleanup",
                    $"Cannot delete old recording {file.FullName}: {exception.Message}"
                );
            }
        }
        return deleted;
    }

    private static string ResolveRecordingRoot() {
        string configured = Environment.ExpandEnvironmentVariables(MicroblocksQolUtilsModule.Settings.RecordingDirectory.Trim());
        if (configured.Length > 0) return configured;
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (videos.Length == 0) videos = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(videos, "Celeste", "microblocks-qol-recordings");
    }

    private static string RunKey(Level level) {
        return $"{level.Session.Area.SID}|{(int)level.Session.Area.Mode}";
    }

    private static bool RespawnPointChanged(Vector2? previous, Vector2? currentPoint) {
        if (previous.HasValue != currentPoint.HasValue) return true;
        return previous.HasValue
            && Vector2.DistanceSquared(previous.Value, currentPoint!.Value) > 0.01f;
    }

    private static string Sanitize(string value) {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '_' : character).ToArray());
    }

    private sealed record PendingRecordingStop(Level? Level, bool Save);

    private sealed record PendingDeathReplay(
        IReadOnlyList<RecordingClip> Clips,
        DateTime OccurredAt,
        string AreaSid,
        string Room,
        bool ReconstructBgm,
        bool RemoveFreezeFrames
    );

    private sealed record RecordingFinalizationJob(
        IReadOnlyList<RecordingClip> Clips,
        string Output,
        string Description,
        bool ReconstructBgm,
        bool RemoveFreezeFrames,
        bool PreferVideoCopy = false
    ) {
        public double Weight => Math.Max(0.1d, Clips.Sum(clip => clip.DurationSeconds));
    }

    private sealed class FinalizationProgressState(
        double weight,
        double progress,
        string description,
        long updateSequence
    ) {
        public double Weight { get; } = weight;
        public double Progress { get; set; } = progress;
        public string Description { get; set; } = description;
        public long UpdateSequence { get; set; } = updateSequence;
    }

    private sealed class FinalizationOutputProgressState(
        double progress,
        string description,
        long finalizationId
    ) {
        public double Progress { get; set; } = progress;
        public string Description { get; set; } = description;
        public long FinalizationId { get; } = finalizationId;
    }
}
