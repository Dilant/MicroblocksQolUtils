using Celeste;
using Celeste.Mod;
using Celeste.Mod.MicroblocksQolUtils;
using Monocle;

int checks=0;
void Check(bool pass,string message) {checks++;if(!pass)throw new Exception(message);}
string root=Path.GetFullPath(args.Length>0 ? args[0] : ".work/recorder-tests");
Directory.CreateDirectory(root);
(Level level, Player player, Strawberry berry) Begin(AutoRecordingMode mode, GoldenRecordingDeath death=GoldenRecordingDeath.Discard, GoldenRecordingEnd end=GoldenRecordingEnd.BerryCollected, bool replay=false) {
    AutoRecorder.Unload();
    NativeRoomRecording.Started.Clear(); NativeRecordingFinalizer.Jobs.Clear();
    MicroblocksQolUtilsModule.Settings=new(){AutomaticRecording=mode,GoldenRecordingDeath=death,GoldenRecordingEnd=end,DeathReplayEnabled=replay,RecordingDirectory=root};
    AutoRecorder.Load("");
    var level=new Level(); Engine.Scene=level; SpeedrunToolAutoSave.ManualOperationActive=false;
    var player=new Player{Scene=level}; level.Tracker.Player=player;
    var berry=new Strawberry{Golden=true,Scene=level}; berry.Follower.Entity=berry;
    return(level,player,berry);
}
void Pickup(Player p,Strawberry b){b.Follower.Leader=p.Leader;p.Leader.Followers.Add(b.Follower);}
void Tick(Level level) { AutoRecorder.Update(level); AutoRecorder.AfterEngineUpdate(); }
void Advance(double seconds) { foreach(var r in NativeRoomRecording.Started.Where(r=>!r.Stopped)) r.MediaTimeSeconds+=seconds; }

// Real orchestrator: stop cannot relabel or re-arm automatic sessions.
foreach(bool save in new[]{false,true}) {
    var(l,p,b)=Begin(AutoRecordingMode.Chapter,replay:true); Tick(l); Advance(5);
    Check(AutoRecorder.IsRecording && !AutoRecorder.ManualMode,"chapter starts automatic");
    AutoRecorder.StartManual();
    Check(!AutoRecorder.ManualMode,"start during auto must be a no-op");
    AutoRecorder.StopManual(l,save);
    Check(!NativeRoomRecording.Started[0].Stopped,"stop must defer DSP teardown");
    Tick(l); for(int i=0;i<5;i++)Tick(l);
    Check(!AutoRecorder.IsRecording && !AutoRecorder.ManualMode,"stop must remain stopped");
    Check(AutoRecorder.IsDeathReplayRecording,"auto stop must not stop death replay");
    Check(NativeRecordingFinalizer.Jobs.Count==(save?1:0),"save/discard behavior");
    if(save) Check(RecordingLibrary.KindOf(root,NativeRecordingFinalizer.Jobs[0].Output)==RecordingLibraryKind.Automatic,"auto output directory");
    Pickup(p,b);Tick(l);
    Check(!AutoRecorder.IsRecording,"pickup must not re-arm a stopped chapter recording");
    AutoRecorder.StartManual();Tick(l);Advance(2);AutoRecorder.StopManual(l,true);Tick(l);
    Check(RecordingLibrary.KindOf(root,NativeRecordingFinalizer.Jobs.Last().Output)==RecordingLibraryKind.Full,"explicit manual goes to full");
    Check(!AutoRecorder.ManualMode && !AutoRecorder.IsRecording,"manual stop must not fall back to auto");
}

// Golden success boundaries remain latched after the follower detaches.
foreach(var end in Enum.GetValues<GoldenRecordingEnd>()) {
    var(l,p,b)=Begin(AutoRecordingMode.Golden,end:end);Tick(l);
    Check(!AutoRecorder.IsRecording,"golden mode waits for pickup");
    Pickup(p,b);Tick(l);Advance(8);
    On.Celeste.Strawberry.Raise(b); Tick(l);
    Check(AutoRecorder.IsRecording==(end==GoldenRecordingEnd.ChapterComplete),"golden collection end policy");
    if(end==GoldenRecordingEnd.ChapterComplete) {
        l.Completed=true;Tick(l); // Banking chapter stats is not the actual exit.
        Check(AutoRecorder.IsRecording,"RegisterAreaComplete must not stop chapter-end mode");
        Advance(4);On.Celeste.Level.Complete(l);Tick(l);
    }
    for(int i=0;i<5;i++)Tick(l);
    Check(!AutoRecorder.IsRecording && !AutoRecorder.ManualMode,"successful golden must not restart");
    Check(NativeRecordingFinalizer.Jobs.Count==1,"success saves exactly once");
    Check(Math.Abs(NativeRecordingFinalizer.Jobs[0].Clips.Sum(c=>c.DurationSeconds)-(end==GoldenRecordingEnd.ChapterComplete?12:8))<0.01,"correct success timeline endpoint");
}

// All six golden policy combinations, including real LevelExit golden restarts.
foreach(var death in Enum.GetValues<GoldenRecordingDeath>()) foreach(var end in Enum.GetValues<GoldenRecordingEnd>()) {
    var(l,p,b)=Begin(AutoRecordingMode.Golden,death,end,replay:true);Pickup(p,b);Tick(l);Advance(7);
    On.Celeste.Player.Raise(p,false);Tick(l);Check(AutoRecorder.IsRecording,"invincible hit isn't death");
    On.Celeste.Player.Raise(p);Check(!NativeRoomRecording.Started[0].Stopped,"death hook must defer native stop");
    Tick(l);
    Check(AutoRecorder.IsRecording==(death==GoldenRecordingDeath.Continue),"golden death choice");
    Check(NativeRecordingFinalizer.Jobs.Count(j=>j.Description=="自动录像")== (death==GoldenRecordingDeath.Save?1:0),"save failed attempt only when requested");
    Check(NativeRecordingFinalizer.Jobs.Count(j=>j.Description=="死亡回放")==1,"death replay independently saves");
    var exit=new LevelExit(); Everest.Events.Level.Exit(l,exit,LevelExit.Mode.GoldenBerryRestart); Everest.Events.Level.End(l,exit);
    var resumed=new Level(); var respawned=new Player{Scene=resumed}; resumed.Tracker.Player=respawned;
    Tick(resumed);Advance(3);Tick(resumed);
    Check(AutoRecorder.IsRecording==(death==GoldenRecordingDeath.Continue),"continue survives golden LevelExit without pickup");
    if(death==GoldenRecordingDeath.Continue) {
        Check(AutoRecorder.ContinuingAfterGoldenDeath,"continued run status");
        Check(!AutoRecorder.CanSaveTransitionTimeline,"uncut failed golden run must not arm recovery rewinds");
        On.Celeste.Player.Raise(respawned);Tick(resumed);respawned.Dead=false;Advance(2);Tick(resumed);
        On.Celeste.Level.Complete(resumed); Tick(resumed);
        var job=NativeRecordingFinalizer.Jobs.Single(j=>j.Description=="自动录像");
        Check(Math.Abs(job.Clips.Sum(c=>c.DurationSeconds)-12)<0.01,"continue retains failures instead of trimming to respawn");
        Check(!AutoRecorder.ManualMode && !AutoRecorder.IsRecording,"continued run ends once");
    } else {
        var nextBerry=new Strawberry{Golden=true,Scene=resumed};nextBerry.Follower.Entity=nextBerry;Pickup(respawned,nextBerry);Tick(resumed);
        Check(AutoRecorder.IsRecording,"next golden challenge starts after failure");
    }
}

// Manual SL only edits video: respect all of SRT's Waiting/wipe time, and open
// on its first presented playable frame without our game-freeze/auto-save gate.
{
    var(l,p,b)=Begin(AutoRecordingMode.Chapter,replay:true); Tick(l); Advance(3);
    AutoRecorder.SuspendForManualSl();
    var saved=AutoRecorder.CaptureTimeline(l)!;
    Check(saved.Clips.Sum(c=>c.DurationSeconds)==3,"manual save snapshot did not retain the exact prefix");
    SpeedrunToolAutoSave.ManualOperationActive=true;
    Advance(10); Tick(l); AutoRecorder.ManualSlPresented(100);
    Check(AutoRecorder.CaptureTimeline(l)!.Clips.SequenceEqual(saved.Clips),"SRT save wait leaked into video");
    SpeedrunToolAutoSave.ManualOperationActive=false;
    AutoRecorder.ManualSlPresented(101); Advance(2); Tick(l);
    var full=NativeRoomRecording.Started.First();
    Check(full.RequestedFrame==101,"manual save resume did not request a boundary keyframe");
    AutoRecorder.SuspendForManualSl(loading:true);
    AutoRecorder.RestoreTimeline(l,saved);
    SpeedrunToolAutoSave.ManualOperationActive=true; Advance(20); Tick(l); AutoRecorder.ManualSlPresented(200);
    Check(AutoRecorder.CaptureTimeline(l)!.Clips.SequenceEqual(saved.Clips),"manual load wait reopened the abandoned branch");
    SpeedrunToolAutoSave.ManualOperationActive=false;
    AutoRecorder.ManualSlPresented(201); Advance(2); AutoRecorder.StopManual(l,true); Tick(l);
    var clips=NativeRecordingFinalizer.Jobs.Last().Clips;
    Check(clips.Count==2 && clips[0].DurationSeconds==3 && clips[1].StartSeconds==35
        && clips[1].DurationSeconds==2 && clips[1].SeamlessFromPrevious,"manual SL retained waits, failed gameplay or crossfade");
}

{
    var(l,p,b)=Begin(AutoRecordingMode.Off); Tick(l);
    AutoRecorder.SuspendForManualSl(); // A manual save made before recording.
    AutoRecorder.StartManual(); Tick(l);
    Check(AutoRecorder.IsRecording,"pre-existing manual SL blocked starting a recording with no capture callbacks");
}

{
    var(l,p,b)=Begin(AutoRecordingMode.Chapter); Tick(l); Advance(1);
    l.Transitioning=true; On.Celeste.Level.Transition(l); AutoRecorder.Update(l);
    Check(!AutoRecorder.CanSaveTransitionTimeline,"save armed before transition coroutine finished");
    l.Transitioning=false; // Finishes AFTER the QolHud update in the same tick.
    AutoRecorder.AfterEngineUpdate();
    Check(AutoRecorder.CanSaveTransitionTimeline,"transition save waited for an unnecessary next physics update");
}

// Default manual/chapter editing still removes failed branches.
{
    var(l,p,b)=Begin(AutoRecordingMode.Chapter); Tick(l); Advance(3);
    var saved = AutoRecorder.CaptureTimeline(l)!;
    Advance(4); On.Celeste.Player.Raise(p); Tick(l);
    p.Dead=false; AutoRecorder.RestoreTimeline(l,saved); Tick(l); Advance(2);
    AutoRecorder.StopManual(l,true); Tick(l);
    var clips=NativeRecordingFinalizer.Jobs.Single().Clips;
    Check(clips.Count==2 && clips[1].SeamlessFromPrevious,"validated saved-state recovery still crossfades");
    Check(clips.Sum(c=>c.DurationSeconds)==5,"seamless recovery kept failed gameplay");
}
// Real orchestrator: an internal recovery opens from the accepted source frame,
// not the later wall clock / first QolHud.Update. Full and death sinks may lag
// independently; both must be ready before opening either branch.
{
    var(l,p,b)=Begin(AutoRecordingMode.Chapter,replay:true); Tick(l); Advance(3);
    AutoRecorder.SuspendForInternalSave(100);
    var saved=AutoRecorder.CaptureTimeline(l)!;
    Advance(1); AutoRecorder.ResumeAfterInternalSave(200); Advance(2);
    On.Celeste.Player.Raise(p); AutoRecorder.AfterEngineUpdate();
    var full=NativeRoomRecording.Started.First();
    p.Dead=false; AutoRecorder.RestoreTimeline(l,saved);
    AutoRecorder.StageInternalRecoveryResume();
    var death=NativeRoomRecording.Started.Last();
    Check(!ReferenceEquals(full,death) && !death.Stopped,"recovery did not create a fresh death sink while frozen");
    full.ResumeFrameTimestamp=death.ResumeFrameTimestamp=0;
    full.MediaTimeSeconds=10; death.MediaTimeSeconds=1;
    AutoRecorder.PrepareInternalSaveResume(1000);
    Check(full.RequestedFrame==1000 && death.RequestedFrame==1000,"recovery did not request a GOP boundary from both sinks");
    full.ResumeFrameTimestamp=1001; full.AcceptedFrameTime=10;
    Advance(0.5);
    Check(!AutoRecorder.TryResumeAfterInternalSave(),"full sink released recovery before death sink accepted the frame");
    Check(AutoRecorder.CaptureTimeline(l)!.Clips.SequenceEqual(saved.Clips),"GPU wait entered the saved prefix");
    death.ResumeFrameTimestamp=1002; death.AcceptedFrameTime=1.2;
    Check(AutoRecorder.TryResumeAfterInternalSave(),"both accepted frames did not resume recovery");
    Advance(2); On.Celeste.Player.Raise(p); AutoRecorder.AfterEngineUpdate();
    var replay=NativeRecordingFinalizer.Jobs.Last(j=>j.Description=="死亡回放");
    Check(replay.PreferVideoCopy,"recovery regressed death replay to mandatory full video transcode");
    Check(replay.Clips.Count==1 && replay.Clips[0].StartSeconds==1.2 && replay.Clips[0].SeamlessFromPrevious,
        "death replay did not start at its accepted clean keyframe");
    p.Dead=false; AutoRecorder.RestoreTimeline(l,saved); AutoRecorder.StageInternalRecoveryResume();
    full.AcceptedFrameTime=20; full.MediaTimeSeconds=20.3;
    AutoRecorder.PrepareInternalSaveResume(2000);
    Check(AutoRecorder.TryResumeAfterInternalSave(),"repeated recovery could not resume");
    Advance(2); AutoRecorder.StopManual(l,true); AutoRecorder.AfterEngineUpdate();
    var clips=NativeRecordingFinalizer.Jobs.Last(j=>j.Description!="死亡回放").Clips;
    Check(clips.Count==2 && clips[0].DurationSeconds==3 && clips[1].StartSeconds==20
        && clips[1].SeamlessFromPrevious,"repeated recovery retained failed gameplay or used resume wall time/crossfade");
}
foreach(var mode in new[]{AutoRecordingMode.Off,AutoRecordingMode.Chapter}) {
    var(l,p,b)=Begin(mode);if(mode==AutoRecordingMode.Off)AutoRecorder.StartManual();Tick(l);Advance(5);
    On.Celeste.Player.Raise(p);Tick(l);p.Dead=false;Tick(l);Advance(2);AutoRecorder.StopManual(l,true);Tick(l);
    Check(Math.Abs(NativeRecordingFinalizer.Jobs.Single().Clips.Sum(c=>c.DurationSeconds)-2)<0.01,"legacy success-only editing preserved");
}

{
    var(l,p,b)=Begin(AutoRecordingMode.Chapter);Tick(l);Advance(3);
    l.Paused=true;Tick(l);Advance(10);AutoRecorder.StopManual(l,true);Tick(l);
    Check(NativeRecordingFinalizer.Jobs.Single().Clips.Sum(c=>c.DurationSeconds)==3,"stop during pause excludes overlay");
    l.Paused=false;Tick(l);Check(!AutoRecorder.IsRecording,"unpause doesn't restart stopped auto");
    MicroblocksQolUtilsModule.Settings.AutomaticRecording=AutoRecordingMode.Off;Tick(l);
    MicroblocksQolUtilsModule.Settings.AutomaticRecording=AutoRecordingMode.Chapter;Tick(l);
    Check(AutoRecorder.IsRecording,"deliberate off/on re-arms auto");
    Advance(1);MicroblocksQolUtilsModule.Settings.AutomaticRecording=AutoRecordingMode.Off;Tick(l);
    Check(!AutoRecorder.IsRecording && NativeRecordingFinalizer.Jobs.Count==2,"disabling auto saves the active session");
}
{
    var(l,p,b)=Begin(AutoRecordingMode.Golden,GoldenRecordingDeath.Continue);Pickup(p,b);Tick(l);Advance(3);
    On.Celeste.Player.Raise(p);Tick(l);var exit=new LevelExit();Everest.Events.Level.Exit(l,exit,LevelExit.Mode.GiveUp);Everest.Events.Level.End(l,exit);
    Check(!AutoRecorder.IsRecording,"give-up must not leak a continued recording");
}
{
    var(l,p,b)=Begin(AutoRecordingMode.Golden,GoldenRecordingDeath.Save);Pickup(p,b);Tick(l);Advance(1);
    MicroblocksQolUtilsModule.Settings.GoldenRecordingDeath=GoldenRecordingDeath.Discard;
    On.Celeste.Player.Raise(p);Tick(l);
    Check(NativeRecordingFinalizer.Jobs.Count==1,"policy snapshot isn't changed halfway through a run");
    var job=NativeRecordingFinalizer.Jobs.Single();
    Check(job.Clips.Sum(c=>c.DurationSeconds)==1,"death save retains failed branch");
}
{
    var(l,p,b)=Begin(AutoRecordingMode.Off);AutoRecorder.StartManual();NativeRoomRecording.FailNextStart=true;Tick(l);
    Check(AutoRecorder.ManualMode && !AutoRecorder.IsRecording,"failed encoder start keeps explicit pending request");
    Tick(l);Advance(1);On.Celeste.Level.Complete(l);Tick(l);Tick(l);
    Check(!AutoRecorder.ManualMode && !AutoRecorder.IsRecording,"manual completion clears the request");
    AutoRecorder.StartManual();Tick(l);Check(AutoRecorder.ManualMode && AutoRecorder.IsRecording,"explicit manual can start after a previous completion");
}
Check(RecordingLibrary.KindOf(root,Path.Combine(root,"auto","map","a.mp4"))==RecordingLibraryKind.Automatic,"classify automatic");
Check(RecordingLibrary.KindOf(root,Path.Combine(root,"autonomous","a.mp4"))==RecordingLibraryKind.Full,"directory boundary must be exact");
Check(RecordingLibrary.KindOf(root,Path.Combine(root,"deaths","map","a.mp4"))==RecordingLibraryKind.DeathReplay,"classify deaths");
Check(RecordingLibrary.KindOf(root,Path.Combine(root,"legacy-map","a.mp4"))==RecordingLibraryKind.Full,"legacy output preserved");
AutoRecorder.Unload();
Console.WriteLine($"Recorder regression checks passed: {checks}");
