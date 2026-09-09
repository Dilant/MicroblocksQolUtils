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
var snapshot = new RecordingTimelineSnapshot([clip], [tail]).Copy();
var restored = JsonSerializer.Deserialize<RecordingTimelineSnapshot>(JsonSerializer.Serialize(snapshot))!;
Check(restored.Clips[0] == clip && restored.RespawnAnchorClips![0] == tail,
    "snapshot/respawn anchor serialization lost room metadata");
var legacy = JsonSerializer.Deserialize<RecordingClip>("""
    {"Source":"run.mkv","StartSeconds":0,"DurationSeconds":1,"MusicEvent":"","MusicTimelineMilliseconds":0}
    """)!;
Check(!legacy.BgmFollowsVideo, "legacy timelines must default to ordinary BGM reconstruction");
Console.WriteLine("PASS production AutoRecorder room policy, both sink branches, restore/short rooms, explicit mix mode, death trim, snapshots and legacy metadata");
