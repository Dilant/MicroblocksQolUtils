using Celeste;
using Celeste.Mod.MicroblocksQolUtils;
using Celeste.Mod.SpeedrunTool;
using Celeste.Mod.SpeedrunTool.SaveLoad;
using Celeste.Mod.SpeedrunTool.ModInterop;
using Monocle;
using MonoMod.ModInterop;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Mono.Cecil.Cil;

int assertions = 0;
void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
typeof(TestExports).ModInterop();
SaveSlotsManager.SwitchSlot("user");
SpeedrunToolBridge.Load();
RecordingDeathRecovery.Load();
Check(SpeedrunToolAutoSave.Available, "private SL hooks did not install");
StateManager Private() => SaveSlotsManager.Dictionary[SpeedrunToolRecoverySlot.Name].StateManager;
Level Reset() {
    SaveSlotsManager.Free = true; StateManager.Instance.State = State.None;
    SpeedrunToolRecoverySlot.Release(); SaveSlotsManager.ClearAll();
    RecordingTransitionAutoSave.Reset();
    MicroblocksQolUtilsModule.Settings = new();
    AutoRecorder.IsRecording = AutoRecorder.CanSaveTransitionTimeline = true;
    AutoRecorder.CurrentPath = "run.mkv"; AutoRecorder.Restored = null;
    SpeedrunToolSettings.Instance.Enabled = true; TasUtils.Running = false; Engine.FreezeTimer = 0;
    SaveData.Instance = new();
    Level level = new(); Engine.Scene = level; return level;
}
void Tick() {
    RecordingDeathRecovery.AfterEngineUpdate(); RecordingTransitionAutoSave.AfterEngineUpdate();
    long deadline = Environment.TickCount64 + 5000;
    while (RecordingSavePause.Active) {
        if (Environment.TickCount64 > deadline) throw new Exception("save pause did not finish");
        RecordingSavePause.Presented(123);
        if (!RecordingSavePause.Active) break;
        RecordingTransitionAutoSave.AfterEngineUpdate();
        Thread.Sleep(1);
    }
}
void Queue(Level level) => RecordingTransitionAutoSave.Queue(level, level.Session.Level);
void SaveHere(Level level) { Queue(level); Tick(); Check(RecordingTransitionAutoSave.CanRecover(level), "recovery anchor not saved"); }
PlayerDeadBody Die(Level level) {
    level.Tracker.Player = null; level.Session.Deaths++; level.Session.DeathsInCurrentLevel++;
    return new PlayerDeadBody { Scene = level };
}

Level level = Reset();
// A real presentation must precede cloning. The worker owns the private slot
// across frames, and two clean presentations must precede simulation resume.
StateManager.CloneGate = new();
var originalSlot = SaveSlotsManager.Slot;
var normalStep = new Microsoft.Xna.Framework.GameTime { ElapsedGameTime = TimeSpan.FromSeconds(1d / 60) };
Check(RecordingSavePause.BeforeEngineUpdate(ref normalStep), "idle gate blocked gameplay");
Queue(level); RecordingTransitionAutoSave.AfterEngineUpdate();
Check(RecordingSavePause.Active && RecordingSavePause.ShowIndicator && RecordingPauseAudio.Paused,
    "save did not pause before presenting its indicator");
RecordingTransitionAutoSave.AfterEngineUpdate();
Check(!SpeedrunToolAutoSave.HasState, "save ran before any indicator presentation");
int suspends = AutoRecorder.Suspends, resumes = AutoRecorder.Resumes;
RecordingSavePause.Presented(100);
RecordingTransitionAutoSave.AfterEngineUpdate();
Check(AutoRecorder.Suspends == suspends + 1 && SpeedrunToolRecoverySlot.Completing
    && SaveSlotsManager.SlotName == SpeedrunToolRecoverySlot.Name, "pre-clone lost its private slot lease");
Check(!RecordingTransitionAutoSave.CanRecover(level), "unfinished clone was published as a recovery point");
RecordingSavePause.Presented(200); RecordingTransitionAutoSave.AfterEngineUpdate();
Check(RecordingSavePause.ShowIndicator && AutoRecorder.Resumes == resumes, "worker wait leaked into gameplay");
double position = 100d;
for (int i = 0; i < 200; i++) {
    var elapsed = new Microsoft.Xna.Framework.GameTime { ElapsedGameTime = TimeSpan.FromSeconds(2) };
    if (RecordingSavePause.BeforeEngineUpdate(ref elapsed)) position += 200;
}
Check(position == 100d, "physics advanced from A to B during saving/catch-up updates");
StateManager.CloneGate.SetResult(); StateManager.CloneGate = null;
while (SpeedrunToolRecoverySlot.Completing) { Thread.Sleep(1); RecordingTransitionAutoSave.AfterEngineUpdate(); }
Check(ReferenceEquals(SaveSlotsManager.Slot, originalSlot) && !RecordingPauseAudio.Paused
    && RecordingSavePause.Active && !RecordingSavePause.ShowIndicator, "clone did not restore audio/selection before clean frame");
RecordingSavePause.Presented(300);
Check(RecordingSavePause.Active, "resumed before clean presentation guard");
RecordingSavePause.Presented(400);
Check(!RecordingSavePause.Active && AutoRecorder.Resumes == resumes + 1, "clean frame failed to resume exactly once");
var delayedStep = new Microsoft.Xna.Framework.GameTime { ElapsedGameTime = TimeSpan.FromSeconds(2) };
Check(RecordingSavePause.BeforeEngineUpdate(ref delayedStep) && delayedStep.ElapsedGameTime == normalStep.ElapsedGameTime,
    "save wall-time became a giant physics step on resume");
level = Reset();
StateManager.FailClone = true; Queue(level); Tick(); StateManager.FailClone = false;
Check(!RecordingSavePause.Active && !RecordingPauseAudio.Paused && !RecordingTransitionAutoSave.CanRecover(level)
    && SaveSlotsManager.SlotName == "user", "failed background clone leaked pause/slot/anchor");
level = Reset(); Queue(level); RecordingTransitionAutoSave.AfterEngineUpdate();
RecordingTransitionAutoSave.Reset();
Check(!RecordingSavePause.Active && !RecordingPauseAudio.Paused, "cancel leaked saving pause");

RecordingDeathAudio.Load();
var predeath = new FMOD.Studio.EventInstance("event:/char/madeline/predeath");
var deathSound = new FMOD.Studio.EventInstance("event:/char/madeline/death");
var goldenSound = new FMOD.Studio.EventInstance("event:/new_content/char/madeline/death_golden");
var dashSound = new FMOD.Studio.EventInstance("event:/char/madeline/dash_red_left");
predeath.start(); deathSound.start(); goldenSound.start(); dashSound.start();
Check(predeath.Stops == 0 && deathSound.Stops == 0, "death audio suppressed before a retained attempt resumes");
RecordingDeathAudio.StopRemainder();
Check(predeath.Stops == 1 && deathSound.Stops == 1 && goldenSound.Stops == 0 && dashSound.Stops == 0
    && Audio.System.Flushes == 1, "death tail cleanup affected unrelated audio or did not flush stops");
RecordingDeathAudio.StopRemainder();
Check(deathSound.Stops == 1, "stale death audio was stopped twice");
RecordingDeathAudio.Unload();

level = Reset();
StateManager user = StateManager.Instance;
level.Transitioning = true; Queue(level); Tick();
Check(!SpeedrunToolAutoSave.HasState, "saved during transition");
level.Transitioning = false; AutoRecorder.CanSaveTransitionTimeline = false; Tick();
Check(!SpeedrunToolAutoSave.HasState, "saved before recorder anchor stabilized");
AutoRecorder.CanSaveTransitionTimeline = true; Tick(); Tick();
Check(Private().Saves == 1 && Private().Clones == 1, "transition must save exactly once");
Check(user.Saves == 0 && ReferenceEquals(StateManager.Instance, user) && SaveSlotsManager.SlotName == "user",
    "automatic save overwrote user slot or left private slot selected");
Check(Private().PreCloneObservedSlot == SpeedrunToolRecoverySlot.Name, "selection changed before pre-clone completed");
Check(!level.TimerMarked && !level.GoldenMarked && !Private().SavedByTas && Private().Freezes == 0,
    "private save marked/froze gameplay or used TAS semantics");
Check(Private().Values.ContainsKey(typeof(AutoRecorder)), "timeline callback did not run");

level.Position = 100; level.Session.Time = 456; SaveData.Instance.Time = 987; SaveData.Instance.TotalDeaths = 30;
PlayerDeadBody body = Die(level); body.CallEnd(); body.CallEnd();
Check(Private().Loads == 0 && body.OriginalCalls == 0 && body.Coroutine.Cancelled, "death loaded inside entity update or ran twice");
Tick();
Check(Private().Loads == 1 && Private().Freezes == 0 && level.Position == 0, "ordinary death did not recover once without a wipe");
Check(!level.TimerMarked && !level.GoldenMarked && StateManager.Instance == user, "internal death load added marks/changed selection");
Check(level.Session.Time == 456 && level.Session.Deaths == 1 && SaveData.Instance.TotalDeaths == 30 && SaveData.Instance.Time == 987,
    "normal death statistics were rewound");
Check(AutoRecorder.Restored!.Clips.SequenceEqual(AutoRecorder.Timeline.Clips)
    && AutoRecorder.Restored.RespawnAnchorClips!.SequenceEqual(AutoRecorder.Timeline.RespawnAnchorClips!), "timeline/respawn clips not restored");

// Multiple manual SL branches followed by both clear-current and clear-all must
// leave an independent, rebased recording recovery point.
level = Reset(); SaveHere(level); user = StateManager.Instance;
level.Position = 15; user.SaveStateImpl(false, out _); user.State = State.None;
Check(level.TimerMarked && level.GoldenMarked && user.Freezes == 1, "manual save behavior changed");
for (int i = 0; i < 3; i++) {
    level.Position = 99; user.LoadStateImpl(false, out _); user.State = State.None; Tick();
    Check(level.Position == 15 && Private().Saves == i + 2, "manual load did not rebase private recovery");
}
user.ClearStateImpl(false); Check(Private().IsSaved, "clearing user slot erased private state");
SaveSlotsManager.ClearAll(); Check(Private().IsSaved, "clearing all user slots erased private state");
level.Position = 50; body = Die(level); body.CallEnd(); Tick();
Check(level.Position == 15 && Private().Loads == 1 && !StateManager.Instance.IsSaved,
    "recovery failed after repeated SL and clearing user slots");
Check(level.TimerMarked && level.GoldenMarked, "recovery erased existing manual SL marks");

level = Reset(); SaveHere(level); SaveSlotsManager.SwitchSlot("user-2");
level.Position = 8; body = Die(level); body.CallEnd(); Tick();
Check(SaveSlotsManager.SlotName == "user-2" && level.Position == 0, "death changed selected user slot");

// SRT's own automatic death hook (installed later) must not redirect recovery to
// the user's slot. Our priority hook intercepts only eligible ordinary deaths.
level = Reset(); SaveHere(level); int srtAutoLoads = 0;
On.Celeste.PlayerDeadBody.hook_End srtDeathHook = (orig, dead) => { srtAutoLoads++; orig(dead); };
On.Celeste.PlayerDeadBody.End += srtDeathHook;
body = Die(level); body.CallEnd(); Tick();
Check(srtAutoLoads == 0 && Private().Loads == 1, "SRT's later death hook ran before private recovery");
On.Celeste.PlayerDeadBody.End -= srtDeathHook;

foreach (Action<Level, PlayerDeadBody> invalidate in new Action<Level, PlayerDeadBody>[] {
    (_, b) => b.HasGolden = true,
    (_, b) => b.DeathAction = () => { },
    (l, _) => l.Session.Level = "other-room",
    (l, _) => l.Session.Area = new(1),
    (l, _) => l.Session.RespawnPoint = new(100, 100),
    (l, _) => l.Completed = true,
    (l, _) => l.Transitioning = true,
    (l, _) => l.Entities.Add(new PlayerSeeker()),
    (_, _) => AutoRecorder.CurrentPath = "another-run.mkv",
    (_, _) => MicroblocksQolUtilsModule.Settings.RecordingAutoSaveOnTransition = false,
    (_, _) => AutoRecorder.IsRecording = false,
    (_, _) => TasUtils.Running = true,
}) {
    level = Reset(); SaveHere(level); body = Die(level); invalidate(level, body); body.CallEnd();
    Check(body.OriginalCalls == 1 && Private().Loads == 0, "non-room respawn/stale run was hijacked");
}
level = Reset(); body = Die(level); body.CallEnd();
Check(body.OriginalCalls == 1, "death without a private save was hijacked");
level = Reset(); SaveHere(level); body = Die(level); body.DeathAction = level.Reload; body.CallEnd(); Tick();
Check(body.OriginalCalls == 0 && Private().Loads == 1, "explicit normal Reload delegate was not recognized");

foreach ((Action<Level> block, Action<Level> unblock) in new (Action<Level>, Action<Level>)[] {
    (l => l.Paused = true, l => l.Paused = false),
    (l => l.InCutscene = true, l => l.InCutscene = false),
    (l => l.SkippingCutscene = true, l => l.SkippingCutscene = false),
    (_ => Engine.FreezeTimer = 1, _ => Engine.FreezeTimer = 0),
    (l => l.Tracker.Player = null, l => l.Tracker.Player = new()),
    (l => l.Tracker.Player!.StateMachine.State = Player.StIntroRespawn, l => l.Tracker.Player!.StateMachine.State = 0),
    (_ => StateManager.Instance.State = State.Waiting, _ => StateManager.Instance.State = State.None),
    (_ => SaveSlotsManager.Free = false, _ => SaveSlotsManager.Free = true),
}) {
    level = Reset(); Queue(level); block(level); Tick();
    Check(!SpeedrunToolAutoSave.HasState, "unsafe/busy frame saved");
    unblock(level); Tick(); Tick(); Check(Private().Saves == 1, "safe frame did not consume request once");
}
foreach (Action<Level> invalidate in new Action<Level>[] {
    _ => MicroblocksQolUtilsModule.Settings.Enabled = false,
    _ => AutoRecorder.IsRecording = false,
    _ => Engine.Scene = new Level(),
    l => l.Session = new(), l => l.Completed = true,
    l => l.Tracker.Player!.Dead = true,
}) {
    level = Reset(); Queue(level); invalidate(level); Tick();
    Check(!SpeedrunToolAutoSave.HasState, "stale/disabled request saved");
}

level = Reset(); SaveHere(level); user = StateManager.Instance; Private().Throw = true;
body = Die(level); body.CallEnd(); Tick();
Check(body.OriginalCalls == 1 && !SpeedrunToolAutoSave.LoadingSilently && StateManager.Instance == user,
    "failed death load did not fall back/reset scope/restore selection");
level = Reset(); SaveHere(level); Private().Reject = true; Queue(level); Tick();
Check(!RecordingTransitionAutoSave.CanRecover(level), "failed save retained a stale recovery anchor");
level = Reset(); SaveHere(level); AutoRecorder.IsRecording = false; Tick();
Check(!SaveSlotsManager.Dictionary.ContainsKey(SpeedrunToolRecoverySlot.Name), "stopping recording leaked the private slot");

level = Reset(); SaveHere(level); body = Die(level); body.CallEnd();
Engine.Scene = new Level(); Tick(); Check(body.OriginalCalls == 0, "pending old-scene death ran after scene switch");

level = Reset(); SaveHere(level); RecordingDeathRecovery.Unload(); SpeedrunToolBridge.Unload();
Check(!SpeedrunToolAutoSave.Available && !SaveSlotsManager.Dictionary.ContainsKey(SpeedrunToolRecoverySlot.Name), "unload leaked hooks/slot");
user = StateManager.Instance; user.SaveStateImpl(false, out _);
Check(level.TimerMarked && user.Freezes == 1, "unload did not restore manual behavior");
user.State = State.None;
SpeedrunToolAutoSave.Load(typeof(string).Assembly);
Check(!SpeedrunToolAutoSave.Available && SpeedrunToolAutoSave.TrySave() == RecoveryResult.Unavailable, "missing SRT did not fail closed");
using (var incompatible = new ILHook(typeof(StateManager).GetMethod("LoadStateImpl")!, il => {
    ILCursor cursor = new(il);
    cursor.GotoNext(MoveType.After, instruction => instruction.MatchCall(typeof(StateManager), "PreCloneSavedEntities"));
    cursor.Emit(OpCodes.Nop);
})) {
    SpeedrunToolAutoSave.Load(typeof(StateManager).Assembly);
    Check(!SpeedrunToolAutoSave.Available, "changed load implementation accepted");
    level = Reset(); StateManager.Instance.SaveStateImpl(false, out _);
    Check(level.TimerMarked && StateManager.Instance.Freezes == 1, "partial hook installation did not roll back");
}
Console.WriteLine($"PASS: {assertions} private recording recovery assertions");

[ModExportName("SpeedrunTool.SaveLoad")]
public static class TestExports {
    public static object RegisterSaveLoadAction(
        Action<Dictionary<Type, Dictionary<string, object>>, Level>? save,
        Action<Dictionary<Type, Dictionary<string, object>>, Level>? load,
        Action? clear, Action<Level>? beforeSave, Action<Level>? beforeLoad, Action? preClone) {
        SaveLoadAction.Save = save; SaveLoadAction.Load = load; return new SaveLoadAction();
    }
    public static void Unregister(object registration) => SaveLoadAction.Save = SaveLoadAction.Load = null;
}
