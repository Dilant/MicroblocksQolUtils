// Minimal game/encoder doubles. Tests execute the real AutoRecorder and library code.
using Microsoft.Xna.Framework;
using Monocle;
namespace Microsoft.Xna.Framework {
    public struct Vector2 { public static float DistanceSquared(Vector2 a, Vector2 b) => 0; }
}
namespace Monocle {
    public static class Engine { public static Scene? Scene; }
    public class Scene { }
    public class Entity { public Scene? Scene { get; set; } }
    public class Tracker { public Celeste.Player? Player; public T? GetEntity<T>() where T:class => Player as T; }
    public class EntityList { public T? FindFirst<T>() where T:class => null; }
}
namespace Celeste {
    public class Level : Scene {
        public Tracker Tracker = new(); public EntityList Entities = new(); public Session Session = new();
        public bool Paused, Transitioning, Completed;
        public ScreenWipe? Wipe;
    }
    public class Session { public AreaKey Area = new(); public Vector2? RespawnPoint; public object MapData = new(); public string Level = "room"; }
    public class AreaKey { public string SID = "test/chapter"; public int Mode; }
    public class Player : Entity { public const int StIntroRespawn=14; public bool Dead; public Leader Leader = new(); public StateMachine StateMachine = new(); }
    public class StateMachine { public int State; }
    public class Leader { public List<Follower> Followers = []; }
    public class Follower { public Entity Entity = null!; public Leader? Leader; }
    public class Strawberry : Entity { public bool Golden; public Follower Follower = new(); }
    public class PlayerDeadBody { }
    public class LevelData { public string Name="next"; }
    public class TextMenu { }
    public class ScreenWipe { }
    public class HiresSnow { }
    public class LevelExit : Scene { public enum Mode { GoldenBerryRestart, GiveUp } }
}
namespace On.Celeste {
    public static class Player {
        public delegate global::Celeste.PlayerDeadBody? orig_Die(global::Celeste.Player p, Vector2 v, bool a, bool b);
        public delegate global::Celeste.PlayerDeadBody? hook_Die(orig_Die orig, global::Celeste.Player p, Vector2 v, bool a, bool b);
        public static event hook_Die? Die;
        public static void Raise(global::Celeste.Player p, bool real=true) => Die?.Invoke((p,v,a,b)=> {
            if(!real) return null; p.Dead=true; p.Leader.Followers.Clear(); return new();
        },p,default,false,true);
    }
    public static class Strawberry {
        public delegate void orig_OnCollect(global::Celeste.Strawberry s);
        public delegate void hook_OnCollect(orig_OnCollect orig, global::Celeste.Strawberry s);
        public static event hook_OnCollect? OnCollect;
        public static void Raise(global::Celeste.Strawberry s) => OnCollect?.Invoke(s=>{
            s.Follower.Leader?.Followers.Remove(s.Follower); s.Follower.Leader=null;
        },s);
    }
    public static class Level {
        public delegate void orig_TransitionTo(global::Celeste.Level l, global::Celeste.LevelData n, Vector2 d);
        public delegate void hook_TransitionTo(orig_TransitionTo orig, global::Celeste.Level l, global::Celeste.LevelData n, Vector2 d);
        public delegate global::Celeste.ScreenWipe orig_CompleteArea_bool_bool_bool(global::Celeste.Level l, bool a, bool b, bool c);
        public delegate global::Celeste.ScreenWipe hook_CompleteArea_bool_bool_bool(orig_CompleteArea_bool_bool_bool orig, global::Celeste.Level l, bool a, bool b, bool c);
        public static event hook_TransitionTo? TransitionTo;
        public static event hook_CompleteArea_bool_bool_bool? CompleteArea_bool_bool_bool;
        public static void Complete(global::Celeste.Level l) => CompleteArea_bool_bool_bool?.Invoke((l,a,b,c)=>{l.Completed=true;return new();},l,false,false,false);
        public static void Transition(global::Celeste.Level l) => TransitionTo?.Invoke((l,n,d)=>{},l,new(),default);
    }
}
namespace Celeste.Mod {
    public enum LogLevel { Warn, Info }
    public static class Logger { public static void LogDetailed(Exception e,string tag)=>throw new Exception(tag,e); public static void Log(LogLevel l,string t,string m) {} }
    public static class Everest { public static class Events { public static class Level {
        public delegate void EndHandler(global::Celeste.Level l, Scene s, ref bool a, ref bool b);
        public static event EndHandler? OnEnd;
        public static event Action<global::Celeste.Level,LevelExit,LevelExit.Mode,Session,HiresSnow>? OnExit;
        public static void End(global::Celeste.Level l, Scene s) { bool a=false,b=false; OnEnd?.Invoke(l,s,ref a,ref b); }
        public static void Exit(global::Celeste.Level l, LevelExit exit, LevelExit.Mode mode) => OnExit?.Invoke(l,exit,mode,l.Session,new());
    } } }
}
namespace Celeste.Mod.MicroblocksQolUtils {
    public static class RecordingSavePause { public static bool Active; }
    public enum BgmRecordingMode { CaptureGameMix, SfxOnlyWithPostMix }
    public class QolSettings {
        public AutoRecordingMode AutomaticRecording;
        public bool AutoRecorderEnabled => AutomaticRecording != AutoRecordingMode.Off;
        public bool DeathReplayEnabled, RecordingRemoveFreezeFrames, RecordingEditingEnabled = true,
            RecordingKeepPausedFrames, RecordingKeepFailedAttempts;
        public int DeathReplayBufferSeconds=30, RecordingRetentionCount, AutoRecordingRetentionCount, DeathReplayRetentionCount;
        public string RecordingDirectory="";
        public GoldenRecordingEnd GoldenRecordingEnd;
        public GoldenRecordingDeath GoldenRecordingDeath;
        public BgmRecordingMode BgmMode;
    }
    public static class MicroblocksQolUtilsModule { public static QolSettings Settings = new(); }
    public static class RecordingDeathRecovery { public static void Load(){} public static void Unload(){} public static void AfterEngineUpdate(){} }
    public static class RecordingDeathAudio { public static void Load(){} public static void Unload(){} public static void StopRemainder(){} }
    public static class RecordingTransitionAutoSave { public static string? CurrentVersionId => null; public static void Queue(Level l,string room){} public static void Cancel(){} public static void Reset(){} public static void AfterEngineUpdate(){} }
    public static class SpeedrunToolBridge { public static void Load(){} public static void Unload(){} }
    public static class SpeedrunToolAutoSave { public static bool ManualOperationActive; }
    public static class RhythmMapDetector { public static bool IsRhythmSensitive(object map, string room) => false; }
    public class MaterialModOptions { }
    public static class QolSettingsOverlay { public static object? ActivePage => null; }
    public readonly record struct MusicPosition(string Event,int TimelineMilliseconds) { public static MusicPosition Read()=>new("",0); }
    public class NativeRoomRecording {
        public static List<NativeRoomRecording> Started=[];
        public static bool FailNextStart;
        public string Path=""; public string AudioPath=>Path+".audio"; public string BgmPath=>Path+".bgm"; public string MusicEventsPath=>Path+".music"; public string CaptureReportPath=>Path+".capture.json"; public string RecoveryManifestPath=>Path+".recovery.json";
        public void CheckpointRecovery() { }
        public double MediaTimeSeconds; public bool HasAudioTap=>true;
        public double TimelineTimeSeconds => MediaTimeSeconds;
        public double TimeAt(ulong timestamp) => MediaTimeSeconds;
        public double FrameTimeAt(ulong timestamp, bool roundUp) => MediaTimeSeconds;
          public double? AcceptedFrameTime;
          public double EncodedFrameTimeAt(ulong timestamp) => AcceptedFrameTime ?? MediaTimeSeconds;
          public ulong RequestedFrame;
          public void RequestResumeFrame(ulong timestamp) { RequestedFrame = timestamp; }
          public ulong ResumeFrameTimestamp { get; set; } = 1;
        public (ulong AudioFramesCaptured, ulong AudioChunksDropped) Statistics => (0,0);
        public bool Stopped;
        public static NativeRoomRecording? Start(string path) {
            if (FailNextStart) {FailNextStart=false;return null;}
            var r=new NativeRoomRecording{Path=path}; Started.Add(r); return r;
        }
        public Task StopAsync(){Stopped=true;return Task.CompletedTask;}
    }
    public static class NativeRecordingFinalizer {
          public record Job(IReadOnlyList<RecordingClip> Clips,string Output,string Description,bool PreferVideoCopy);
        public static List<Job> Jobs=[];
        public static Task<bool> FinishAsync(IReadOnlyList<RecordingClip> clips,string output,string description,bool bgm,bool freeze,Action<double> progress,bool preferVideoCopy=false) {
              Jobs.Add(new(clips,output,description,preferVideoCopy)); progress(1); return Task.FromResult(true);
        }
    }
}
