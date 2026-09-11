using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Celeste;
using Celeste.Mod;
using Celeste.Mod.MicroblocksQolUtils;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static T Bare<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
static object? Call(string method, params object[] args) =>
    typeof(AutoRecorder).GetMethod(method, PrivateStatic)!.Invoke(null, args);
static T State<T>(string field) => (T)typeof(AutoRecorder).GetField(field, PrivateStatic)!.GetValue(null)!;
static void SetState(string field, object? value) => typeof(AutoRecorder).GetField(field, PrivateStatic)!.SetValue(null, value);
static void SetField(object owner, string name, object value) => owner.GetType()
    .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

// Use the shipped AutoRecorder/Session/MapData, not a duplicate policy or forced
// reconstruct=true argument. Bare game objects avoid booting Steam or graphics.
var module = new MicroblocksQolUtilsModule();
var settings = Bare<QolSettings>();
typeof(EverestModule).GetProperty("_Settings")!.SetValue(module, settings);
var normal = Bare<LevelData>();
normal.Name = "normal"; normal.Entities = []; normal.Triggers = [];
var sensitive = Bare<LevelData>();
sensitive.Name = "cassette"; sensitive.Entities = [new EntityData { Name = "cassetteBlock" }]; sensitive.Triggers = [];
var triggered = Bare<LevelData>();
triggered.Name = "trigger"; triggered.Entities = []; triggered.Triggers = [new EntityData { Name = "mod/music_sync" }];
var map = Bare<MapData>();
map.Levels = [normal, sensitive, triggered];
var area = new AreaData { SID = "test/room-bgm", Mode = [new ModeProperties { MapData = map }] };
AreaData.Areas = [area];
var session = Bare<Session>();
session.Area = new AreaKey(0); session.Level = normal.Name;
var level = Bare<Level>(); level.Session = session;

settings.BgmMode = BgmRecordingMode.SfxOnlyWithPostMix;
Call("BeginRun", level);
Check(State<bool>("reconstructBgm"), "a cassette elsewhere disabled the whole run's BGM reconstruction");
foreach (var (room, expected) in new[] { (normal, false), (sensitive, true), (normal, false), (triggered, true), (normal, false) }) {
    session.Level = room.Name;
    Call("ObserveRoom", level);
    Check(State<bool>("reconstructBgm"), "room exception changed the run-wide mode");
    Check(State<bool>("recordingRoomBgmFollowsVideo") == expected, $"wrong room policy: {room.Name}");
    Check(State<string>("recordingRoomName") == room.Name, "stale room name");
}
settings.BgmMode = BgmRecordingMode.CaptureGameMix;
Call("BeginRun", level);
Check(!State<bool>("reconstructBgm"), "explicit game-mix mode was ignored");

// Exercise actual clip creation for BOTH sinks with a deterministic stopped clock.
// No native handle, files, hooks, Steam process or game settings are touched.
object Recording(string path) {
    var capture = Bare<NativeCaptureSession>();
    SetField(capture, "gate", new object()); // handle=0 => default stats, no native call
    var recording = RuntimeHelpers.GetUninitializedObject(typeof(AutoRecorder).Assembly
        .GetType("Celeste.Mod.MicroblocksQolUtils.NativeRoomRecording")!);
    SetField(recording, "capture", capture);
    SetField(recording, "<Path>k__BackingField", path);
    return recording;
}
settings.BgmMode = BgmRecordingMode.SfxOnlyWithPostMix;
settings.AutoRecorderEnabled = true;
Call("BeginRun", level); Call("ObserveRoom", level);
var full = Recording("full.mkv"); var death = Recording("death.mkv");
SetState("current", full); SetState("deathReplayCurrent", death);
Call("StartBranchAtCurrentTime", false); Call("StartDeathReplayBranchAtCurrentTime", false);
SetField(full, "lastMediaTime", 2d); SetField(death, "lastMediaTime", 2d);
session.Level = sensitive.Name; Call("ObserveRoom", level);
SetField(full, "lastMediaTime", 4d); SetField(death, "lastMediaTime", 4d);
session.Level = normal.Name; Call("ObserveRoom", level);
SetField(full, "lastMediaTime", 6d); SetField(death, "lastMediaTime", 6d);
foreach (var (prefix, currentMethod) in new[] { ("ActivePrefix", "CurrentClip"), ("DeathReplayPrefix", "CurrentDeathReplayClip") }) {
    var clips = State<List<RecordingClip>>(prefix).Append((RecordingClip)Call(currentMethod, 6d, .02d)!).ToArray();
    Check(clips.Length == 3 && clips.Select(c => c.BgmFollowsVideo).SequenceEqual([false, true, false]),
        $"{prefix}: room flag leaked across branches");
    Check(clips.Select(c => c.StartSeconds).SequenceEqual([0d, 2d, 4d]) && clips.All(c => c.DurationSeconds == 2),
        $"{prefix}: room split lost/duplicated time");
    Check(clips[1].SeamlessFromPrevious && clips[2].SeamlessFromPrevious, $"{prefix}: room split created a visual fade");
}
var saved = AutoRecorder.CaptureTimeline(level)!;
session.Level = sensitive.Name;
AutoRecorder.RestoreTimeline(level, saved);
Call("ObserveRoom", level); Call("StartBranchAtCurrentTime", false);
Check(State<bool>("branchBgmFollowsVideo"), "restored branch inherited the saved room's policy");
Check(saved.Clips.Select(c => c.BgmFollowsVideo).SequenceEqual([false, true, false]),
    "restore retroactively changed saved room policies");
// A very short room is still retained when splitting for metadata only.
SetField(full, "lastMediaTime", 6.01d); session.Level = normal.Name; Call("ObserveRoom", level);
Check(Math.Abs(State<List<RecordingClip>>("ActivePrefix").Last().DurationSeconds - .01) < 1e-9,
    "metadata-only room split dropped a short room");
// Save waits are explicit holes in BOTH timelines, not freeze-frame heuristics.
SetField(full, "lastMediaTime", 7d); SetField(death, "lastMediaTime", 7d);
Call("SuspendForInternalSave", 100UL);
Check(!State<bool>("branchActive") && !State<bool>("deathReplayBranchActive"), "save pause left a sink recording UI");
var pausedSnapshot = AutoRecorder.CaptureTimeline(level);
Check(pausedSnapshot is not null && pausedSnapshot.Clips.Last().StartSeconds + pausedSnapshot.Clips.Last().DurationSeconds == 7d,
    "internal save lost its closed timeline snapshot");
SetField(full, "lastMediaTime", 12d); SetField(death, "lastMediaTime", 12d);
Check(AutoRecorder.CaptureTimeline(level)!.Clips.SequenceEqual(pausedSnapshot!.Clips), "saving wait grew the saved prefix");
Call("ResumeAfterInternalSave", 200UL);
Check(State<double>("branchStartSeconds") == 12d && State<double>("deathReplayBranchStartSeconds") == 12d
    && State<bool>("branchSeamlessFromPrevious") && State<bool>("deathReplayBranchSeamlessFromPrevious"),
    "resume used an old boundary or created a dissolve at the save seam");
var clockCapture = Bare<NativeCaptureSession>();
SetField(clockCapture, "origin", 1_000_000_000UL);
var timeAt = typeof(NativeCaptureSession).GetMethod("TimeAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
Check((double)timeAt.Invoke(clockCapture, [9_000_000_000UL])! == 8d
    && (double)timeAt.Invoke(clockCapture, [500_000_000UL])! == 0d,
    "event clock used stale delivered frame statistics or underflowed before origin");
SetField(full, "targetFrameRate", 60);
SetField(full, "lastMediaTime", 12.001d);
var frameTime = full.GetType().GetMethod("FrameTimeAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
Check((double)frameTime.Invoke(full, [0UL, false])! == 12d
    && Math.Abs((double)frameTime.Invoke(full, [0UL, true])! - (12d + 1d / 60)) < 1e-9,
    "encoder PTS rounding can leak an indicator frame into a retained clip");
SetField(clockCapture, "origin", 1_009_000_000UL);
SetField(full, "capture", clockCapture);
Check((double)frameTime.Invoke(full, [13_020_000_000UL, false])! == 12d
    && Math.Abs((double)frameTime.Invoke(full, [13_020_000_000UL, true])! - (12d+1d/60)) < 1e-9,
    "late source origin moved edit boundaries off the encoder PTS grid");
var encodedTime = full.GetType().GetMethod("EncodedFrameTimeAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
Check((double)encodedTime.Invoke(full, [13_020_000_000UL])! == 12d,
    "saved resume frame did not use its actual encoder PTS");
SetState("current", null); SetState("deathReplayCurrent", null);
Call("ResetTimelineState");

var clip = new RecordingClip("run.mkv", 5, 3, "music/a", 5000, true, true, "cassette");
var tail = clip.RetainTail(1);
Check(tail.StartSeconds == 7 && tail.DurationSeconds == 1 && tail.MusicTimelineMilliseconds == 7000,
    "death replay tail positions changed");
Check(tail.BgmFollowsVideo && tail.RoomName == "cassette" && tail.SeamlessFromPrevious,
    "death replay trimming lost room metadata");
var shortRoom = clip with { DurationSeconds = .01 };
var recent = (List<RecordingClip>)Call("CaptureRecentClips", new RecordingClip[] { shortRoom, tail }, 2d)!;
Check(recent.Count == 2 && recent[0] == shortRoom, "death replay trimming dropped a short sensitive room");
var snapshot = new RecordingTimelineSnapshot([clip], [tail], "version-A", "run.mkv").Copy();
var restored = JsonSerializer.Deserialize<RecordingTimelineSnapshot>(JsonSerializer.Serialize(snapshot))!;
Check(restored.Clips[0] == clip && restored.RespawnAnchorClips![0] == tail
    && restored.RecoveryVersionId == "version-A" && restored.RecordingSource == "run.mkv",
    "snapshot/respawn anchor serialization lost room metadata");
var legacy = JsonSerializer.Deserialize<RecordingClip>("""
    {"Source":"run.mkv","StartSeconds":0,"DurationSeconds":1,"MusicEvent":"","MusicTimelineMilliseconds":0}
    """)!;
Check(!legacy.BgmFollowsVideo, "legacy timelines must default to ordinary BGM reconstruction");
// Reports must distinguish actual submitted/encoded cadence from configured FPS.
var stats = new CaptureStatistics(false,2560,1506,0,601,381,220,0,0,10_000_000_000,0,0);
var reportType = typeof(AutoRecorder).Assembly.GetType("Celeste.Mod.MicroblocksQolUtils.RecordingCaptureReport")!;
var report = Activator.CreateInstance(reportType,60,stats,new CaptureDeliveryStatistics(0,0,0,0),0L,0L)!;
using(var reportJson=JsonDocument.Parse(JsonSerializer.Serialize(report,reportType))) {
    var r=reportJson.RootElement;
    Check(r.GetProperty("SubmittedFps").GetDouble()==60 && r.GetProperty("EncoderInputFps").GetDouble()==38
        && r.GetProperty("UnderTarget").GetBoolean(),"capture report hid encoder loss behind nominal FPS");
}
// Check the production death-job factory, not a standalone forced fast-export call.
settings.RecordingDirectory = Path.Combine(Path.GetTempPath(), "mqol-policy-output");
var pendingType = typeof(AutoRecorder).GetNestedType("PendingDeathReplay", BindingFlags.NonPublic)!;
var pending = (System.Collections.IList)typeof(AutoRecorder)
    .GetField("PendingDeathReplays", PrivateStatic)!.GetValue(null)!;
pending.Add(Activator.CreateInstance(pendingType,
    new object[] { new[] { clip }, DateTime.Now, "test/area", "room", true, true })!);
var jobs = ((System.Collections.IEnumerable)Call("TakeDeathReplayJobs")!).Cast<object>().ToArray();
Check(jobs.Length == 1 && (bool)jobs[0].GetType().GetProperty("PreferVideoCopy")!.GetValue(jobs[0])!,
    "death replay job did not request packet copy");
Check((bool)jobs[0].GetType().GetProperty("RemoveFreezeFrames")!.GetValue(jobs[0])!,
    "fast replay silently disabled freeze-frame editing");
Check(pending.Count == 0, "death replay job was not consumed");
LibraryProgress.Run(settings);
Console.WriteLine("PASS production AutoRecorder room policy, both sink branches, restore/short rooms, explicit mix mode, death trim, snapshots and legacy metadata");
