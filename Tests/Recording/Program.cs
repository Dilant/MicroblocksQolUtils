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
    AutoRecorder.Timeline = new([new("run.mkv",0,12,"music",0)], [new("run.mkv",0,10,"music",0)]);
    SpeedrunToolSettings.Instance.Enabled = true; SpeedrunToolSettings.Instance.AutoLoadStateAfterDeath = true; TasUtils.Running = false; Engine.FreezeTimer = 0;
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
Check(RecordingSavePause.Active && !RecordingSavePause.ShowIndicator && !RecordingPauseAudio.Paused,
    "save did not freeze on the exact clean boundary before its indicator");
RecordingTransitionAutoSave.AfterEngineUpdate();
Check(!SpeedrunToolAutoSave.HasState, "save ran before any indicator presentation");
int suspends = AutoRecorder.Suspends, resumes = AutoRecorder.Resumes;
RecordingSavePause.Presented(100);
Check(AutoRecorder.Suspends == suspends + 1 && RecordingSavePause.ShowIndicator && !SpeedrunToolAutoSave.HasState,
    "clean saved pose did not establish the boundary before the UI");
Check(RecordingPauseAudio.Paused, "audio did not pause at the excluded video boundary");
RecordingSavePause.Presented(110);
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
Check(ReferenceEquals(SaveSlotsManager.Slot, originalSlot) && RecordingPauseAudio.Paused
    && RecordingSavePause.Active && !RecordingSavePause.ShowIndicator, "audio resumed before the retained clean frame");
RecordingSavePause.Presented(300);
Check(RecordingSavePause.Active && RecordingPauseAudio.Paused, "game/audio resumed before clean presentation guard");
RecordingSavePause.Presented(400);
Check(RecordingSavePause.Active && !RecordingPauseAudio.Paused, "audio did not resume at the requested retained frame, or simulation resumed early");
RecordingSavePause.Presented(500);
Check(!RecordingSavePause.Active && AutoRecorder.Resumes == resumes + 1, "clean frame failed to resume exactly once");
var delayedStep = new Microsoft.Xna.Framework.GameTime { ElapsedGameTime = TimeSpan.FromSeconds(2) };
Check(RecordingSavePause.BeforeEngineUpdate(ref delayedStep) && delayedStep.ElapsedGameTime == normalStep.ElapsedGameTime,
    "save wall-time became a giant physics step on resume");
int resumeUpdates = 1;
for (int i = 0; i < 30; i++) {
    var catchup = new Microsoft.Xna.Framework.GameTime { ElapsedGameTime = normalStep.ElapsedGameTime };
    if (RecordingSavePause.BeforeEngineUpdate(ref catchup)) resumeUpdates++;
}
Check(resumeUpdates == 1, "fixed-step catch-up advanced multiple physics steps before the first resumed presentation");
RecordingSavePause.Presented(600);
Check(RecordingSavePause.BeforeEngineUpdate(ref normalStep), "first resumed presentation did not release normal updates");
// A draw after capture stops must be sufficient too; no source callback exists.
RecordingSavePause.Begin(level, _ => { });
RecordingSavePause.Cancel();
Check(RecordingSavePause.BeforeEngineUpdate(ref normalStep), "cancel did not permit its first recovery step");
Check(!RecordingSavePause.BeforeEngineUpdate(ref normalStep), "cancel did not guard recovery catch-up updates");
RecordingSavePause.Drawn();
Check(RecordingSavePause.BeforeEngineUpdate(ref normalStep), "ordinary draw did not release recovery guard without capture");
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

// Reproduce the real Engine.Update -> synchronous SRT load -> catch-up updates
// -> presentation/async GPU delivery ordering. No camera/player/scene step may
// happen between the clone restore and the first accepted recovery frame.
level = Reset(); level.Position = 123; level.CameraPosition = 72; level.SceneFrame = 901;
SaveHere(level);
for (int attempt = 0; attempt < 3; attempt++) {
    level.Position = 999; level.CameraPosition = 888; level.SceneFrame = 1500;
    body = Die(level); body.CallEnd();
    AutoRecorder.ResumeReady = false;
    RecordingDeathRecovery.AfterEngineUpdate();
    Check(RecordingSavePause.Active && level.Position == 123 && level.CameraPosition == 72 && level.SceneFrame == 901,
        "restored pose was not frozen immediately after internal load");
    for (int frame = 0; frame < 12; frame++) {
        for (int catchup = 0; catchup < 10; catchup++) {
            var elapsed = new Microsoft.Xna.Framework.GameTime { ElapsedGameTime = TimeSpan.FromSeconds(2) };
            if (RecordingSavePause.BeforeEngineUpdate(ref elapsed)) {
                level.Position += 20; level.CameraPosition += 8; level.SceneFrame++;
            }
        }
        RecordingSavePause.Presented((ulong)(1000 + frame));
    }
    Check(level.Position == 123 && level.CameraPosition == 72 && level.SceneFrame == 901 && RecordingSavePause.Active,
        "load catch-up/slow GPU skipped the saved pose before it could be recorded");
    AutoRecorder.ResumeReady = true;
    RecordingSavePause.Presented(2000);
Check(!RecordingSavePause.Active, "accepted recovery frame did not release physics");
    var resume = new Microsoft.Xna.Framework.GameTime { ElapsedGameTime = TimeSpan.FromSeconds(2) };
    Check(RecordingSavePause.BeforeEngineUpdate(ref resume) && resume.ElapsedGameTime <= TimeSpan.FromSeconds(1d / 60),
        "load wall-time leaked into the first resumed physics step");
}

level = Reset(); SaveHere(level); body = Die(level); body.CallEnd();
RecordingDeathRecovery.AfterEngineUpdate();
Engine.Scene = new Level(); RecordingTransitionAutoSave.AfterEngineUpdate();
Check(!RecordingSavePause.Active && !RecordingPauseAudio.Paused, "scene switch leaked the recovery gate");

// Progress presents are not gameplay, even before SRT has changed State.None.
level = Reset();
Check(SpeedrunToolProgress.Available, "progress isolation hook did not install");
int auxiliaryCalls = 0;
Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.Drawing = () => {
    Check(!CapturePresentationGate.AcceptGameplay, "progress entered gameplay capture");
    auxiliaryCalls++;
    RecordingSavePause.Presented(10);
};
Queue(level); RecordingTransitionAutoSave.AfterEngineUpdate();
int beforeAuxSuspends = AutoRecorder.Suspends;
Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.Show();
Check(auxiliaryCalls == 1 && AutoRecorder.Suspends == beforeAuxSuspends && !RecordingSavePause.ShowIndicator,
    "auxiliary present advanced the clean boundary gate");
Check(CapturePresentationGate.AcceptGameplay, "progress leaked presentation exclusion");
Tick();
SpeedrunToolRecoverySlot.Run(_ => {
    Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.Show();
    return RecoveryResult.Success;
}, create: false);
Check(auxiliaryCalls == 1, "private slot rendered duplicate SRT progress/readback");
Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.Drawing = () => throw new InvalidOperationException("test draw failure");
try { Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.Show(); } catch (InvalidOperationException) { }
Check(CapturePresentationGate.AcceptGameplay, "failed progress render poisoned game capture");
Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.Drawing = null;

// Native SRT hint is used inside the blocked update, with no intervening QolHud
// indicator draw. Its extra presents cannot advance the boundary/clean gates.
level = Reset();
Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.NativeEnabled = true;
int hints = Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.Begins;
int waits = Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.Waits;
int disposes = Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.Disposes;
Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.Drawing = () => {
    Check(!CapturePresentationGate.AcceptGameplay && RecordingPauseAudio.Paused, "native hint escaped audio/video suspension");
    for (int i = 0; i < 5; i++) RecordingSavePause.Presented(150);
};
Queue(level); RecordingTransitionAutoSave.AfterEngineUpdate(); RecordingSavePause.Presented(100);
RecordingTransitionAutoSave.AfterEngineUpdate();
Check(Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.Begins == hints + 1
    && Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.Disposes == disposes + 1
    && Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.Waits > waits, "SRT did not own save hint and preclone wait");
Check(RecordingSavePause.Active && !RecordingSavePause.ShowIndicator && RecordingPauseAudio.Paused
    && RecordingTransitionAutoSave.CanRecover(level), "native hint rendered fallback HUD or released clean gate early");
Tick();
Check(!RecordingSavePause.Active && !RecordingPauseAudio.Paused, "native hint leaked the game/audio pause");
// Missing/unavailable native renderer falls back to the existing indicator.
Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.Drawing = null;
Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.NativeEnabled = false;
Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.FailBegin = true;
level = Reset(); Queue(level); RecordingTransitionAutoSave.AfterEngineUpdate(); RecordingSavePause.Presented(100);
RecordingTransitionAutoSave.AfterEngineUpdate();
Check(RecordingSavePause.ShowIndicator && !SpeedrunToolAutoSave.HasState, "unavailable SRT UI skipped the fallback indicator");
Tick(); Celeste.Mod.SpeedrunTool.Progress.BusyIndicator.FailBegin = false;
Check(RecordingTransitionAutoSave.CanRecover(level), "fallback after native UI failure could not save");

// Branch graph: a1 -> M -> route X -> a2 -> S2, load M -> route Y -> a2' -> S3.
// Manual slots affect death routing ONLY. Every normal checkpoint still saves.
level = Reset(); level.Session.RespawnPoint = new(1,0); level.Position = 1;
SaveHere(level); string a1 = RecordingTransitionAutoSave.CurrentVersionId!;
var a1Timeline = AutoRecorder.Timeline.Copy();
void UserSave(string slot, int position, RecordingTimelineSnapshot? timeline = null) {
    Check(SaveSlotsManager.SwitchSlot(slot), "cannot select user save slot");
    level.Position = position;
    if (timeline is not null) AutoRecorder.Timeline = timeline;
    Check(StateManager.Instance.SaveStateImpl(false, out _), "manual save failed");
    StateManager.Instance.State = State.None; Tick();
}
void UserLoad(string slot) {
    Check(SaveSlotsManager.SwitchSlot(slot), "cannot select user load slot");
    Check(StateManager.Instance.LoadStateImpl(false, out _), "manual load failed");
    StateManager.Instance.State = State.None; Tick();
}
RecordingTimelineSnapshot Route(string name, double offset) => new([
    new("run.mkv",0,10,"shared",0), new("run.mkv",offset,10,name,0)
]);
UserSave("M",10,new([new("run.mkv",0,10,"shared",0)]));
var manualM = StateManager.Instance;
Check(!RecordingTransitionAutoSave.CanRecover(level) && SpeedrunToolRecoverySlot.Contains(a1),
    "manual priority deleted the fallback instead of only selecting a death target");
level.Session.RespawnPoint = new(2,0); level.Position = 20; AutoRecorder.Timeline = Route("X",100);
Queue(level); Tick(); string a2x = RecordingTransitionAutoSave.CurrentVersionId!;
Check(a2x != a1 && SpeedrunToolRecoverySlot.Contains(a2x) && !RecordingTransitionAutoSave.CanRecover(level)
    && StateManager.Instance == manualM, "manual save blocked automatic a2 or changed selection");
var xPrefix = AutoRecorder.Timeline.Copy();
UserSave("S2",25);
UserLoad("M");
Check(level.Position == 10 && level.Session.RespawnPoint == new Microsoft.Xna.Framework.Vector2(1,0)
    && RecordingTransitionAutoSave.CurrentVersionId == a1 && AutoRecorder.Restored!.Clips.Count == 1,
    "loading M used future a2 or failed to restore M's video branch");
level.Session.RespawnPoint = new(2,0); level.Position = 21; AutoRecorder.Timeline = Route("Y",200);
Queue(level); Tick(); string a2y = RecordingTransitionAutoSave.CurrentVersionId!;
Check(a2y != a2x && SpeedrunToolRecoverySlot.Contains(a2x), "new a2 overwrote the version pinned by S2");
var yPrefix = AutoRecorder.Timeline.Copy(); UserSave("S3",26);
UserLoad("S2");
Check(level.Position == 25 && RecordingTransitionAutoSave.CurrentVersionId == a2x
    && AutoRecorder.Restored!.Clips.SequenceEqual(xPrefix.Clips), "S2 borrowed the newer Y branch");
StateManager.Instance.ClearStateImpl(false); Tick();
Check(RecordingTransitionAutoSave.CanRecover(level) && RecordingTransitionAutoSave.CurrentVersionId == a2x,
    "clearing S2 changed fallback context or ignored its still-valid automatic save");
level.Position = 99; body = Die(level); body.CallEnd(); Tick();
Check(body.OriginalCalls == 0 && level.Position == 20 && AutoRecorder.Restored!.Clips.SequenceEqual(xPrefix.Clips),
    "death after clearing S2 did not restore matching X gameplay and video");
UserLoad("S3");
Check(level.Position == 26 && RecordingTransitionAutoSave.CurrentVersionId == a2y, "S3 lost Y fallback");
// Reusing a slot replaces its reference, not versions pinned by other slots.
UserSave("S2",27); Tick();
Check(!SpeedrunToolRecoverySlot.Contains(a2x), "unreferenced superseded X savestate was not collected");
int versionsBeforeClear = SpeedrunToolRecoverySlot.VersionIds.Count;
SaveSlotsManager.ClearAll(); Tick();
Check(SpeedrunToolRecoverySlot.VersionIds.Count == versionsBeforeClear && RecordingTransitionAutoSave.CanRecover(level),
    "clear-all destroyed auto versions or created an unsolicited save");
body = Die(level); body.CallEnd(); Tick();
Check(level.Position == 21 && AutoRecorder.Restored!.Clips.SequenceEqual(yPrefix.Clips),
    "clear-all death combined Y state with X video");

// Loading backwards and clearing M must use a1, never the most recently created a2.
level = Reset(); level.Position = 1; level.Session.RespawnPoint = new(1,0); SaveHere(level);
a1 = RecordingTransitionAutoSave.CurrentVersionId!;
UserSave("M",10); level.Session.RespawnPoint = new(2,0); level.Position = 20; Queue(level); Tick();
UserLoad("M"); StateManager.Instance.ClearStateImpl(false); Tick();
Check(RecordingTransitionAutoSave.CurrentVersionId == a1, "clear after rewind selected a future checkpoint");
body = Die(level); body.CallEnd(); Tick(); Check(level.Position == 1, "rewound death did not restore a1");

// Existing manual state does not suppress recording-start autosave; no auto is
// created just by clearing a slot when no normal trigger has happened.
level = Reset(); UserSave("M",10); Tick();
StateManager.Instance.ClearStateImpl(false); Tick();
Check(!SpeedrunToolRecoverySlot.HasState && !RecordingSavePause.Active, "clear without an anchor fabricated a save");
UserSave("M",10); Queue(level); Tick();
Check(SpeedrunToolRecoverySlot.HasState && !RecordingSavePause.Active, "manual slot blocked recording-start autosave");

// Selected slot, not 'any nonempty slot', determines SRT's actual death target.
SaveSlotsManager.SwitchSlot("empty"); Tick();
Check(RecordingTransitionAutoSave.CanRecover(level), "an unselected user slot disabled recovery globally");
SaveSlotsManager.SwitchSlot("M"); SpeedrunToolSettings.Instance.AutoLoadStateAfterDeath = false;
Check(RecordingTransitionAutoSave.CanRecover(level), "disabled manual autoload still blocked internal recovery");
SpeedrunToolSettings.Instance.AutoLoadStateAfterDeath = true;
int delegatedDeaths = 0;
On.Celeste.PlayerDeadBody.hook_End manualDeathHook = (orig, dead) => {
    delegatedDeaths++; StateManager.Instance.LoadStateImpl(false,out _);
};
On.Celeste.PlayerDeadBody.End += manualDeathHook;
body = Die(level); body.CallEnd();
Check(delegatedDeaths == 1 && !body.Coroutine.Cancelled && StateManager.Instance.State == State.Waiting,
    "private recovery intercepted SRT's selected manual death target");
On.Celeste.PlayerDeadBody.End -= manualDeathHook; StateManager.Instance.State = State.None;

// Cross-room context lives in each manual snapshot too.
level = Reset(); level.Position = 1; SaveHere(level); UserSave("A",10);
level.Transitioning = true; RecordingTransitionAutoSave.Queue(level,"B"); RecordingTransitionAutoSave.AfterEngineUpdate();
level.Session.Level = "B"; level.Position = 30; level.Transitioning = false; Tick();
string bVersion = RecordingTransitionAutoSave.CurrentVersionId!; UserSave("B",35);
UserLoad("A"); Check(level.Session.Level == "next" && level.Position == 10, "cross-room user load failed");
UserLoad("B"); StateManager.Instance.ClearStateImpl(false); Tick();
body = Die(level); body.CallEnd(); Tick();
Check(level.Position == 30 && RecordingTransitionAutoSave.CurrentVersionId == bVersion, "B fallback used A's branch");

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
level = Reset(); level.Position = 5; SaveHere(level);
string previousVersion = RecordingTransitionAutoSave.CurrentVersionId!;
StateManager.RejectNextSave = true; level.Position = 9; Queue(level); Tick(); Tick();
Check(RecordingTransitionAutoSave.CanRecover(level) && SpeedrunToolRecoverySlot.Name == previousVersion
    && SpeedrunToolRecoverySlot.VersionIds.Count == 1, "failed replacement lost the valid version or leaked its failed slot");
body = Die(level); body.CallEnd(); Tick();
Check(level.Position == 5, "failed replacement redirected death to an incomplete slot");
level.Session.RespawnPoint = new(4,4); StateManager.RejectNextSave = true; Queue(level); Tick(); Tick();
Check(!RecordingTransitionAutoSave.CanRecover(level), "failed checkpoint save reused a mismatched previous checkpoint");

// Completion callbacks stage snapshots; failed operations must never publish
// their timeline/context, even after SRT has invoked the callbacks.
foreach (bool throws in new[] { false, true }) {
    level = Reset(); SaveHere(level); UserSave("failed-load", 10, Route("saved",100));
    StateManager failed = StateManager.Instance;
    AutoRecorder.Timeline = Route("current",200); AutoRecorder.Restored = null;
    var before = AutoRecorder.Timeline.Copy();
    string? beforeContext = RecordingTransitionAutoSave.CurrentVersionId;
    failed.ThrowAfterCallback = throws; failed.RejectAfterCallback = !throws;
    try { Check(!failed.LoadStateImpl(false,out _), "failed manual load reported success"); }
    catch (InvalidOperationException) when (throws) { }
    failed.State = State.None;
    Check(AutoRecorder.Restored is null && AutoRecorder.Timeline.Clips.SequenceEqual(before.Clips)
        && RecordingTransitionAutoSave.CurrentVersionId == beforeContext, "failed callback transaction committed a video/fallback branch");
    failed.ThrowAfterCallback = failed.RejectAfterCallback = false;
    failed.ClearStateImpl(false);
    // Same transaction rule applies to the private death target.
    var automatic = Private(); automatic.ThrowAfterCallback = throws; automatic.RejectAfterCallback = !throws;
    Check(SpeedrunToolAutoSave.TryLoad(level) == RecoveryResult.Failed && AutoRecorder.Restored is null,
        "failed private load committed its staged prefix");
    automatic.ThrowAfterCallback = automatic.RejectAfterCallback = false;
}
// A save rejected before callbacks keeps the old slot reference; one rejected
// after overwriting clears it, allowing an obsolete automatic version to die.
level = Reset(); SaveHere(level); previousVersion = RecordingTransitionAutoSave.CurrentVersionId!;
UserSave("overwrite",10); var overwrite = StateManager.Instance;
Queue(level); Tick(); string replacement = RecordingTransitionAutoSave.CurrentVersionId!;
overwrite.Reject = true;
Check(!overwrite.SaveStateImpl(false,out _), "rejected save succeeded"); Tick();
Check(SpeedrunToolRecoverySlot.Contains(previousVersion), "pre-callback rejection unpinned the intact manual save");
overwrite.Reject = false; overwrite.RejectAfterCallback = true;
Check(!overwrite.SaveStateImpl(false,out _), "post-callback rejection succeeded"); Tick();
Check(!SpeedrunToolRecoverySlot.Contains(previousVersion) && SpeedrunToolRecoverySlot.Contains(replacement),
    "failed overwrite retained an obsolete root or removed the live version");
overwrite.RejectAfterCallback = false;
// A failed preclone cannot publish a new version or destroy its predecessor.
level = Reset(); SaveHere(level); previousVersion = RecordingTransitionAutoSave.CurrentVersionId!;
StateManager.FailClone = true; Queue(level); Tick(); StateManager.FailClone = false; Tick();
Check(RecordingTransitionAutoSave.CanRecover(level) && SpeedrunToolRecoverySlot.Name == previousVersion
    && SpeedrunToolRecoverySlot.VersionIds.Count == 1, "failed preclone replaced/leaked a private version");
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
        SaveLoadAction.Save = save; SaveLoadAction.Load = load;
        SaveLoadAction.Clear = clear;
        SaveLoadAction.BeforeSave = beforeSave; SaveLoadAction.BeforeLoad = beforeLoad;
        return new SaveLoadAction();
    }
    public static void Unregister(object registration) { SaveLoadAction.Save = SaveLoadAction.Load = null; SaveLoadAction.Clear = null; }
}
