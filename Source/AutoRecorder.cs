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
    private static NativeRoomRecording? source;
    private static RecordingTimelineSnapshot? respawnAnchor;
    private static Vector2? observedRespawnPoint;
    private static MusicPosition branchMusicStart;
    private static MusicPosition deathReplayMusicStart;
    private static double branchStartSeconds;
    private static double deathReplayBranchStartSeconds;
    private static double fullSegmentBaseSeconds;
    private static bool branchSeamlessFromPrevious;
    private static bool deathReplayBranchSeamlessFromPrevious;
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
    private static bool fullRecordingStopped;
    private static bool reconstructBgm;
    private static bool completing;
    private static bool manualMode;
    private static Task<bool>? recordingAuthorizationTask;
    private static bool recordingStartFailed;
    private static bool recordingStartAwaiting;
    private static bool recordingSwitchesInitialized;
    private static bool autoRecorderWasEnabled;
    private static bool deathReplayWasEnabled;
    private static int finalizingCount;
    private static long currentFinalizationId;
    private static int cleanupRunning;
    private static long nextFinalizationId;
    private static string lastOutput = "";
    private static string lastCleanupStatus = "—";

    public static bool ManualMode => manualMode;
    public static bool IsRecording => source is not null && fullRecordingEnabled;
    public static bool IsDeathReplayRecording =>
        source is not null && MicroblocksQolUtilsModule.Settings.DeathReplayEnabled;
    public static bool IsFullRecordingEnabled => fullRecordingEnabled;
    public static bool IsFinalizing => Volatile.Read(ref finalizingCount) > 0;
    public static int PendingFinalizationCount => Volatile.Read(ref finalizingCount);
    public static bool IsCleaning => Volatile.Read(ref cleanupRunning) != 0;
    public static double CurrentSeconds => (source?.MediaTimeSeconds ?? 0) - fullSegmentBaseSeconds;
    public static double DisplaySeconds => CurrentSeconds;
    public static bool HasAudioTap => source?.HasAudioTap ?? false;
    public static ulong AudioFramesCaptured => source?.Statistics.AudioFramesCaptured ?? 0;
    public static ulong AudioChunksDropped => source?.Statistics.AudioChunksDropped ?? 0;
    public static double DeathReplaySeconds => Math.Min(
        source?.MediaTimeSeconds ?? 0,
        Math.Clamp(MicroblocksQolUtilsModule.Settings.DeathReplayBufferSeconds, 10, 60)
    );
    public static string CurrentPath => source?.Path ?? "";
    public static string LastOutput => lastOutput;
    public static string LastCleanupStatus => lastCleanupStatus;
    public static int PendingDeathReplayCount => PendingDeathReplays.Count;
    public static double FinalizationProgress {
        get {
            lock (FinalizationProgressLock) {
                // Only the currently-running (serialized) finalization drives the shown
                // progress; queued ones have not started yet.
                long id = Volatile.Read(ref currentFinalizationId);
                return id != 0 && ActiveFinalizations.TryGetValue(id, out FinalizationProgressState? state)
                    ? state.Progress
                    : 0d;
            }
        }
    }
    public static string FinalizationDescription {
        get {
            lock (FinalizationProgressLock) {
                long id = Volatile.Read(ref currentFinalizationId);
                return id != 0 && ActiveFinalizations.TryGetValue(id, out FinalizationProgressState? state)
                    ? state.Description
                    : "视频";
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
    public static string DeathReplayRoot => Path.Combine(ResolveRecordingRoot(), DeathReplaysDirectory);

    public static void Load(string directory) {
        _ = directory;
        On.Celeste.Player.Die += PlayerDie;
        On.Celeste.Level.TransitionTo += LevelTransitionTo;
        On.Celeste.Level.RegisterAreaComplete += RegisterAreaComplete;
        Everest.Events.Level.OnEnd += LevelEnd;
        SpeedrunToolBridge.Load();
        CleanupRecordings();
    }

    public static void Unload() {
        manualMode = false;
        SpeedrunToolBridge.Unload();
        Everest.Events.Level.OnEnd -= LevelEnd;
        On.Celeste.Level.RegisterAreaComplete -= RegisterAreaComplete;
        On.Celeste.Level.TransitionTo -= LevelTransitionTo;
        On.Celeste.Player.Die -= PlayerDie;
        StopAndReset(deleteSource: true);
    }

    public static void Update(Level level) {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        Player? player = level.Tracker.GetEntity<Player>();
        if (player is null) return;
        bool fullWanted = manualMode
            || (!fullRecordingStopped && settings.AutoRecorderEnabled && ShouldRecord(player, settings));
        bool deathWanted = settings.DeathReplayEnabled;
        if (!fullWanted && !deathWanted) {
            if (source is not null || runKey.Length > 0)
                StopAndReset(deleteSource: true);
            return;
        }

        string key = RunKey(level);
        bool newRun = !string.Equals(key, runKey, StringComparison.Ordinal);
        if (newRun) {
            if (runKey.Length > 0 && !completing) StopAndReset(deleteSource: true);
            BeginRun(level);
        }

        fullRecordingEnabled = fullWanted;
        if (newRun && (settings.AutoRecorderEnabled || settings.DeathReplayEnabled))
            _ = EnsureRecordingAuthorization();
        UpdateCapture(level, player, settings);
    }

    public static void AfterEngineUpdate() {
        if (!deathReplayFinalizeRequested) return;
        deathReplayFinalizeRequested = false;
        // The shared capture source keeps running after a death (the full/auto recording may
        // continue into the respawn). Finalize the just-queued death replay clips against the
        // still-open source without stopping it or deleting its temporary files.
        FinalizeDeathReplayCapture();
    }

    private static void UpdateCapture(Level level, Player player, QolSettings settings) {
        if (level.Paused) {
            SuspendForPause();
            SuspendDeathReplayForPause();
            return;
        }
        if (source is null && PlayerIsRecordable(level, player)) StartSource(level);
        NativeRoomRecording? recording = source;
        if (recording is null) return;

        if (fullRecordingEnabled) {
            if (pauseSuspended && PlayerIsRecordable(level, player) && PauseOverlayCleared(level)) {
                ResumeFullRecordingAfterPause(recording);
            } else if (waitingForStablePlayer && PlayerIsRecordable(level, player)) {
                StartBranchAtCurrentTime();
            } else if (!branchActive && PlayerIsRecordable(level, player)) {
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
            if (branchActive && RespawnPointChanged(observedRespawnPoint, respawn))
                respawnAnchor = new RecordingTimelineSnapshot(CaptureCurrentClips(recording));
            observedRespawnPoint = respawn;
        } else {
            DiscardFullTimeline();
        }

        if (settings.DeathReplayEnabled) {
            if (deathReplayPauseSuspended && PlayerIsRecordable(level, player) && PauseOverlayCleared(level)) {
                ResumeDeathReplayAfterPause(recording);
            } else if (deathReplayWaitingForStablePlayer && PlayerIsRecordable(level, player)) {
                StartDeathReplayBranchAtCurrentTime();
            } else if (!deathReplayBranchActive && PlayerIsRecordable(level, player)) {
                StartDeathReplayBranchAtCurrentTime();
            }

            if (deathReplayBranchActive && reconstructBgm)
                ObserveDeathReplayMusicTimeline(recording);
        } else {
            DiscardDeathTimeline();
        }
    }

    public static void StartManual() {
        manualMode = true;
        fullRecordingStopped = false;
        recordingStartFailed = false;
        // A fresh manual segment counts from zero even though it slices the same shared
        // source the auto/death-replay recording is already writing to.
        fullSegmentBaseSeconds = source?.MediaTimeSeconds ?? 0;
        _ = EnsureRecordingAuthorization();
    }

    public static bool AuthorizationInFlight => recordingAuthorizationTask is { IsCompleted: false };

    public static Task<bool>? AuthorizationTask => recordingAuthorizationTask;

    public static void ReauthorizeRecording() {
        _ = EnsureRecordingAuthorization(force: true);
    }

    private static Task<bool> EnsureRecordingAuthorization(bool force = false) {
        if (!CaptureBackend.Current.AuthorizationSupported) return Task.FromResult(true);
        if (!force && recordingAuthorizationTask is { IsCompleted: false }) return recordingAuthorizationTask;
        recordingAuthorizationTask = CaptureBackend.Current.AuthorizeRecordingAsync(force);
        return recordingAuthorizationTask;
    }

    public static void StopManual(Level? level, bool save) {
        manualMode = false;
        // Stopping the full recording (manual or auto) never destroys the shared source: it
        // must live until no scheme uses it anymore. If the player saves, the current full
        // segment is finalized in the background from the still-running source; if they discard,
        // only the current full segment is dropped. Death replay (if enabled) keeps using the
        // same source. fullRecordingStopped latches the stop so auto recording does not
        // immediately re-arm; it resets on a new run, a manual start, or re-enabling auto.
        fullRecordingStopped = true;
        if (save && level is not null) FinalizeCurrent(level);
        else DiscardFullTimeline();
    }

    public static void UpdateRecordingSwitches() {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (!recordingSwitchesInitialized) {
            autoRecorderWasEnabled = settings.AutoRecorderEnabled;
            deathReplayWasEnabled = settings.DeathReplayEnabled;
            recordingSwitchesInitialized = true;
            return;
        }
        if (settings.AutoRecorderEnabled && !autoRecorderWasEnabled) {
            fullRecordingStopped = false;
            recordingStartFailed = false;
            _ = EnsureRecordingAuthorization();
        }
        if (settings.DeathReplayEnabled && !deathReplayWasEnabled) {
            recordingStartFailed = false;
            _ = EnsureRecordingAuthorization();
        }
        autoRecorderWasEnabled = settings.AutoRecorderEnabled;
        deathReplayWasEnabled = settings.DeathReplayEnabled;
    }

    public static void CleanupRecordings() {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        int fullRetentionCount = Math.Max(0, settings.RecordingRetentionCount);
        int deathRetentionCount = Math.Max(0, settings.DeathReplayRetentionCount);
        if (fullRetentionCount == 0 && deathRetentionCount == 0) {
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
        NativeRoomRecording? recording = source;
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
        NativeRoomRecording? recording = source;
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if ((!settings.AutoRecorderEnabled && !manualMode) || recording is null) return;
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

        bool deathReplayWanted = MicroblocksQolUtilsModule.Settings.DeathReplayEnabled;
        if (source is not null && deathReplayWanted) {
            QueueDeathReplay(self, source);
            deathReplayBranchActive = false;
            deathReplayBranchSeamlessFromPrevious = false;
            deathReplayWaitingForStablePlayer = true;
            deathReplayPauseSuspended = false;
            // Player.Die runs from inside EntityList.Update; the death replay capture is finalized
            // later by AfterEngineUpdate rather than doing synchronous FMOD teardown in this hook.
            deathReplayFinalizeRequested = true;
        }

        if (source is null) return body;
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
        if (source is not null) transitioningRoom = true;
    }

    private static void RegisterAreaComplete(On.Celeste.Level.orig_RegisterAreaComplete orig, Level self) {
        Complete(self);
        orig(self);
    }

    private static void LevelEnd(
        Level level,
        Scene nextScene,
        ref bool shouldReloadPortraits,
        ref bool shouldDissociateEntities
    ) {
        _ = level;
        _ = nextScene;
        _ = shouldReloadPortraits;
        _ = shouldDissociateEntities;
        if (source is not null || runKey.Length > 0)
            StopAndReset(deleteSource: true);
    }

    private static void BeginRun(Level level) {
        completing = false;
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
        // Manual recording is scoped to a single run: it ends when the player moves to the
        // next area, so auto recording (if enabled) takes over cleanly on the new level.
        manualMode = false;
        fullRecordingEnabled = false;
        fullRecordingStopped = false;
        recordingStartFailed = false;
        recordingStartAwaiting = false;
        reconstructBgm = ShouldReconstructBgm(level);
    }

    private static void StartSource(Level level) {
        // Re-confirm the authorization right before the capture grabs the screen.
        // A failed attempt is latched until the next trigger.
        if (recordingStartFailed) return;
        Task<bool> authorization = ResolveStartAuthorization(ref recordingStartAwaiting);
        if (!authorization.IsCompleted) return;
        recordingStartAwaiting = false;
        if (!authorization.Result) {
            recordingStartFailed = true;
            return;
        }
        ActuallyStartSource(level);
    }

    private static void ActuallyStartSource(Level level) {
        string tempRoot = Path.Combine(ResolveRecordingRoot(), ".working", Sanitize(runKey));
        Directory.CreateDirectory(tempRoot);
        string path = Path.Combine(tempRoot, $"full-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.mkv");
        source = NativeRoomRecording.Start(path);
        if (source is null) return;
        fullSegmentBaseSeconds = source.MediaTimeSeconds;
        ActivePrefix.Clear();
        respawnAnchor = null;
        observedRespawnPoint = level.Session.RespawnPoint;
        StartBranchAtCurrentTime();
        ResetDeathReplayState(waitForStablePlayer: false);
        StartDeathReplayBranchAtCurrentTime();
    }

    private static Task<bool> ResolveStartAuthorization(ref bool awaiting) {
        if (awaiting) return recordingAuthorizationTask ?? Task.FromResult(false);
        if (recordingAuthorizationTask is { IsCompleted: false } inFlight) {
            awaiting = true;
            return inFlight;
        }
        // A previously declined confirmation is latched so the picker is not re-shown mid-run;
        // an accepted one is re-validated with a fresh round-trip right before capturing.
        if (recordingAuthorizationTask is { IsCompleted: true, Result: false })
            return Task.FromResult(false);
        awaiting = true;
        return EnsureRecordingAuthorization();
    }

    private static void StartBranchAtCurrentTime(bool seamlessFromPrevious = false) {
        NativeRoomRecording? recording = source;
        if (recording is null) return;
        branchStartSeconds = recording.MediaTimeSeconds;
        branchMusicStart = MusicPosition.Read();
        branchSeamlessFromPrevious = seamlessFromPrevious;
        branchActive = true;
        waitingForStablePlayer = false;
    }

    private static void StartDeathReplayBranchAtCurrentTime(bool seamlessFromPrevious = false) {
        NativeRoomRecording? recording = source;
        if (recording is null) return;
        deathReplayBranchStartSeconds = recording.MediaTimeSeconds;
        deathReplayMusicStart = MusicPosition.Read();
        deathReplayBranchSeamlessFromPrevious = seamlessFromPrevious;
        deathReplayBranchActive = true;
        deathReplayWaitingForStablePlayer = false;
    }

    private static void SuspendForPause() {
        if (pauseSuspended) return;
        NativeRoomRecording? recording = source;
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
        NativeRoomRecording? recording = source;
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

    private static void Complete(Level level) {
        FinalizeCurrent(level);
    }

    private static void FinalizeCurrent(Level level) {
        NativeRoomRecording? recording = source;
        if (completing
            || recording is null
            || !string.Equals(RunKey(level), runKey, StringComparison.Ordinal)) {
            return;
        }
        completing = true;
        List<RecordingClip> clips = [.. ActivePrefix];
        if (branchActive) {
            RecordingClip? finalClip = CurrentClip(recording.MediaTimeSeconds);
            if (finalClip is not null) clips.Add(finalClip);
        }
        // Save the current full-recording segment from the still-running shared source.
        // The source is NOT destroyed here: other schemes (e.g. death replay) may still need
        // it, and it must live until nothing uses it anymore. Tearing it down is done by
        // StopAndReset once no scheme is active.
        if (fullRecordingEnabled && clips.Count > 0) {
            string output = Path.Combine(
                FullRecordingRoot,
                Sanitize(areaSid),
                $"{DateTime.Now:yyyyMMdd-HHmmss}-{Sanitize(areaSid)}.mp4"
            );
            lastOutput = output;
            FinalizeFromSource([new RecordingFinalizationJob(clips, output, "完整录像", reconstructBgm,
                MicroblocksQolUtilsModule.Settings.RecordingRemoveFreezeFrames)]);
        }
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

    private static RecordingClip? CurrentClip(double endSeconds) {
        NativeRoomRecording? recording = source;
        if (recording is null || !branchActive) return null;
        double duration = endSeconds - branchStartSeconds;
        if (duration < MinimumClipSeconds) return null;
        return new RecordingClip(
            recording.Path,
            Math.Max(0, branchStartSeconds),
            duration,
            branchMusicStart.Event,
            branchMusicStart.TimelineMilliseconds,
            branchSeamlessFromPrevious
        );
    }

    private static RecordingClip? CurrentDeathReplayClip(double endSeconds) {
        NativeRoomRecording? recording = source;
        if (recording is null || !deathReplayBranchActive) return null;
        double duration = endSeconds - deathReplayBranchStartSeconds;
        if (duration < MinimumClipSeconds) return null;
        return new RecordingClip(
            recording.Path,
            Math.Max(0, deathReplayBranchStartSeconds),
            duration,
            deathReplayMusicStart.Event,
            deathReplayMusicStart.TimelineMilliseconds,
            deathReplayBranchSeamlessFromPrevious
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
            if (remaining < MinimumClipSeconds) break;
            double duration = Math.Min(remaining, clip.DurationSeconds);
            if (duration < MinimumClipSeconds) continue;
            double retainedStart = clip.StartSeconds + clip.DurationSeconds - duration;
            int musicOffset = (int)Math.Round((retainedStart - clip.StartSeconds) * 1_000d);
            result.Insert(0, new RecordingClip(
                clip.Source,
                retainedStart,
                duration,
                clip.MusicEvent,
                clip.MusicTimelineMilliseconds + musicOffset,
                clip.SeamlessFromPrevious
            ));
            remaining -= duration;
        }
        return result;
    }

    private static void FinalizeDeathReplayCapture() {
        NativeRoomRecording? recording = source;
        if (recording is not null) {
            // Finalize the queued death replay clips against the still-running shared source.
            // Do not stop the source and do not delete its temporary files; the full/auto
            // recording keeps using them.
            FinalizeFromSource(TakeDeathReplayJobs());
        }
        ResetDeathReplayState(waitForStablePlayer: true);
    }

    private static bool ShouldRecord(Player player, QolSettings settings) {
        if (settings.RecordingPolicy == RecordingPolicy.EveryRoom) return true;
        return player.Leader.Followers.Any(follower => follower.Entity is Strawberry { Golden: true });
    }

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

    private static bool ShouldReconstructBgm(Level level) {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (settings.BgmMode != BgmRecordingMode.SfxOnlyWithPostMix) return false;
        bool rhythmSensitive = RhythmMapDetector.IsRhythmSensitive(level.Session.MapData);
        if (rhythmSensitive) {
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils/Recorder",
                "Rhythm-sensitive map detected; keeping the captured game mix for timing accuracy.");
        }
        return !rhythmSensitive;
    }

    private static void DiscardFullTimeline() {
        ActivePrefix.Clear();
        respawnAnchor = null;
        observedRespawnPoint = null;
        branchActive = false;
        branchSeamlessFromPrevious = false;
        waitingForStablePlayer = false;
        pauseSuspended = false;
        pauseResumeAfterMediaSeconds = null;
        transitioningRoom = false;
    }

    private static void DiscardDeathTimeline() {
        deathReplayFinalizeRequested = false;
        PendingDeathReplays.Clear();
        ResetDeathReplayState(waitForStablePlayer: false);
    }

    private static void StopAndReset(bool deleteSource) {
        NativeRoomRecording? recording = source;
        source = null;
        if (recording is not null) {
            Task stop = recording.StopAsync();
            if (deleteSource) FinishStoppedRecording(recording, stop, TakeDeathReplayJobs());
        }
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
                death.RemoveFreezeFrames);
        }).ToList();
        PendingDeathReplays.Clear();
        return jobs;
    }

    private static void FinishStoppedRecording(
        NativeRoomRecording recording,
        Task stop,
        IReadOnlyList<RecordingFinalizationJob> jobs
    ) {
        string[] temporaryFiles = [recording.Path, recording.AudioPath];
        // Every finalization that reads the shared source runs through one serialized worker
        // queue, so at most one reader touches the (possibly still-growing) source at a time.
        // The run-end teardown is enqueued after any in-flight death-replay/full finalizations,
        // guaranteeing the source's temporary files are only deleted once all readers drained.
        if (jobs.Count == 0) {
            EnqueueFinalization(async () => {
                await stop.ConfigureAwait(false);
                DeleteTemporaryFiles(temporaryFiles);
            });
            return;
        }
        long finalizationId = BeginFinalization(jobs);
        Interlocked.Increment(ref finalizingCount);
        EnqueueFinalization(async () => {
            try {
                await stop.ConfigureAwait(false);
                Volatile.Write(ref currentFinalizationId, finalizationId);
                await FinishJobsCore(jobs, finalizationId, temporaryFiles).ConfigureAwait(false);
            } finally {
                Interlocked.Decrement(ref finalizingCount);
            }
        });
    }

    private static void FinalizeFromSource(IReadOnlyList<RecordingFinalizationJob> jobs) {
        if (jobs.Count == 0) return;
        long finalizationId = BeginFinalization(jobs);
        Interlocked.Increment(ref finalizingCount);
        EnqueueFinalization(async () => {
            try {
                Volatile.Write(ref currentFinalizationId, finalizationId);
                await FinishJobsCore(jobs, finalizationId).ConfigureAwait(false);
            } finally {
                Interlocked.Decrement(ref finalizingCount);
            }
        });
    }

    private static async Task FinishJobsCore(
        IReadOnlyList<RecordingFinalizationJob> jobs,
        long finalizationId,
        IReadOnlyCollection<string>? temporaryFiles = null
    ) {
        bool completed = true;
        try {
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
                    )
                ).ConfigureAwait(false)) {
                    completed = false;
                }
                completedWeight += job.Weight;
            }
            if (temporaryFiles is not null) {
                if (completed) DeleteTemporaryFiles(temporaryFiles);
                else {
                    Logger.Log(
                        LogLevel.Warn,
                        "MicroblocksQolUtils/Recorder",
                        $"Finalization failed; preserved continuous recording files under {Path.GetDirectoryName(temporaryFiles.FirstOrDefault() ?? "")}"
                    );
                }
            }
            CleanupRecordings();
        } catch (Exception exception) {
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder");
        } finally {
            EndFinalization(finalizationId);
        }
    }

    private static long BeginFinalization(IReadOnlyList<RecordingFinalizationJob> jobs) {
        long id = Interlocked.Increment(ref nextFinalizationId);
        lock (FinalizationProgressLock) {
            ActiveFinalizations[id] = new FinalizationProgressState(
                0d,
                jobs[0].Description
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
            if (Volatile.Read(ref currentFinalizationId) == id)
                Volatile.Write(ref currentFinalizationId, 0);
            ActiveFinalizations.Remove(id);
            foreach (string output in ActiveFinalizationOutputs
                         .Where(pair => pair.Value.FinalizationId == id)
                         .Select(pair => pair.Key)
                         .ToArray()) {
                ActiveFinalizationOutputs.Remove(output);
            }
        }
    }

    private static readonly Queue<Func<Task>> FinalizationQueue = new();
    private static readonly object FinalizationQueueLock = new();
    private static bool finalizationWorkerRunning;

    private static void EnqueueFinalization(Func<Task> task) {
        bool startWorker;
        lock (FinalizationQueueLock) {
            FinalizationQueue.Enqueue(task);
            startWorker = !finalizationWorkerRunning;
            if (startWorker) finalizationWorkerRunning = true;
        }
        if (startWorker) _ = RunFinalizationWorker();
    }

    private static async Task RunFinalizationWorker() {
        while (true) {
            Func<Task>? task;
            lock (FinalizationQueueLock) {
                if (FinalizationQueue.Count == 0) {
                    finalizationWorkerRunning = false;
                    return;
                }
                task = FinalizationQueue.Dequeue();
            }
            try {
                await task().ConfigureAwait(false);
            } catch (Exception exception) {
                Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/Finalize");
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
        fullSegmentBaseSeconds = 0;
        fullRecordingEnabled = false;
        completing = false;
    }

    private static void ResetDeathReplayState(bool waitForStablePlayer) {
        DeathReplayPrefix.Clear();
        deathReplayBranchStartSeconds = 0;
        deathReplayMusicStart = default;
        deathReplayBranchActive = false;
        deathReplayBranchSeamlessFromPrevious = false;
        deathReplayWaitingForStablePlayer = waitForStablePlayer;
        deathReplayPauseSuspended = false;
        deathReplayPauseResumeAfterMediaSeconds = null;
        deathReplayFinalizeRequested = false;
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
        bool RemoveFreezeFrames
    ) {
        public double Weight => Math.Max(0.1d, Clips.Sum(clip => clip.DurationSeconds));
    }

    private sealed class FinalizationProgressState(
        double progress,
        string description
    ) {
        public double Progress { get; set; } = progress;
        public string Description { get; set; } = description;
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
