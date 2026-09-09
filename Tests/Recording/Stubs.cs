using System.Runtime.CompilerServices;
using Celeste.Mod.MicroblocksQolUtils;
using MonoMod.RuntimeDetour;

namespace Microsoft.Xna.Framework {
    public readonly record struct Vector2(float X, float Y);
    public class GameTime {
        public TimeSpan ElapsedGameTime { get; set; }
        public TimeSpan TotalGameTime { get; set; }
        public bool IsRunningSlowly { get; set; }
        public GameTime() { }
        public GameTime(TimeSpan total, TimeSpan elapsed, bool slow) { TotalGameTime = total; ElapsedGameTime = elapsed; IsRunningSlowly = slow; }
    }
}
namespace Monocle {
    public class Scene;
    public class Entity {
        public Scene? Scene;
        public Coroutine Coroutine = new();
        public T? Get<T>() where T : class => Coroutine as T;
    }
    public class Coroutine { public bool Cancelled; public void Cancel() => Cancelled = true; }
    public class EntityList : List<Entity> { public T? FindFirst<T>() where T : class => this.OfType<T>().FirstOrDefault(); }
    public static class Engine { public static Scene? Scene; public static float FreezeTimer; }
}
namespace Celeste {
    public static class Audio {
        public static FMOD.Studio.System System = new();
        public static string GetEventName(FMOD.Studio.EventInstance instance) => instance.Path;
    }
    public readonly record struct AreaKey(int ID, int Mode = 0);
    public class Session {
        public string Level = "next";
        public AreaKey Area;
        public Microsoft.Xna.Framework.Vector2? RespawnPoint;
        public long Time;
        public int Deaths, DeathsInCurrentLevel;
        public HashSet<string> Flags = [];
        public bool GetFlag(string name) => Flags.Contains(name);
        public void SetFlag(string name, bool value = true) { if (value) Flags.Add(name); else Flags.Remove(name); }
        public Session Copy() { Session s = (Session)MemberwiseClone(); s.Flags = [.. Flags]; return s; }
    }
    public class Level : Monocle.Scene {
        public Session Session = new();
        public Tracker Tracker = new();
        public Monocle.EntityList Entities = [];
        public bool Completed, Transitioning, Paused, InCutscene, SkippingCutscene;
        public bool GoldenMarked;
        public bool TimerMarked => Session.GetFlag("SpeedrunTool_SavedSate");
        public int Position, Reloads;
        public void Reload() => Reloads++;
    }
    public class Tracker {
        public Player? Player = new();
        public T? GetEntity<T>() where T : class => Player as T;
    }
    public class Player { public const int StIntroRespawn = 14; public bool Dead; public StateMachine StateMachine = new(); }
    public class PlayerSeeker : Monocle.Entity;
    public class StateMachine { public int State; }
    public class PlayerDeadBody : Monocle.Entity {
        public Action? DeathAction;
        public bool HasGolden;
        private bool finished;
        public int OriginalCalls;
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void End() { OriginalCalls++; if (finished) return; finished = true; (DeathAction ?? ((Level)Scene!).Reload)(); }
        public void CallEnd() => End();
    }
    public class SaveData {
        public static SaveData Instance = new();
        public long Time; public int TotalDeaths;
        public AreaStats[] Areas_Safe = [new(), new()];
    }
    public class AreaStats { public ModeStats[] Modes = [new()]; }
    public class ModeStats { public long TimePlayed; public int Deaths; }
}
namespace On.Celeste {
    public static class PlayerDeadBody {
        public delegate void orig_End(global::Celeste.PlayerDeadBody body);
        public delegate void hook_End(orig_End orig, global::Celeste.PlayerDeadBody body);
        private static readonly Dictionary<hook_End, Hook> Hooks = [];
        public static event hook_End End {
            add => Hooks.Add(value, new Hook(typeof(global::Celeste.PlayerDeadBody).GetMethod("End",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!, value));
            remove { if (Hooks.Remove(value, out Hook? hook)) hook.Dispose(); }
        }
    }
}
namespace Celeste.Mod {
    public enum LogLevel { Info, Warn }
    public static class Logger { public static void Log(LogLevel level, string tag, string message) => Console.WriteLine($"{level}: {message}"); }
}
namespace Celeste.Mod.MicroblocksQolUtils {
    public class QolHud;
    public class QolSettings { public bool Enabled = true, RecordingAutoSaveOnTransition = true; }
    public static class MicroblocksQolUtilsModule { public static QolSettings Settings = new(); }
    public static class InstantDeaths { public static void Reset() { } }
    public static class RecordingPauseAudio {
        public static bool Paused;
        public static void Pause() => Paused = true;
        public static void Resume() => Paused = false;
    }
    public static class AutoRecorder {
        public static int Suspends, Resumes;
        public static void SuspendForInternalSave(ulong time) => Suspends++;
        public static void ResumeAfterInternalSave(ulong time) => Resumes++;
        public static void CancelInternalSave() { }
        public static bool IsRecording = true, CanSaveTransitionTimeline = true;
        public static string CurrentPath = "run.mkv";
        public static RecordingTimelineSnapshot Timeline = new([new("run.mkv", 0, 12, "music", 0)],
            [new("run.mkv", 0, 10, "music", 0)]);
        public static RecordingTimelineSnapshot? Restored;
        public static RecordingTimelineSnapshot CaptureTimeline(Level level) => Timeline.Copy();
        public static void RestoreTimeline(Level level, RecordingTimelineSnapshot snapshot) => Restored = snapshot;
    }
}
namespace Celeste.Mod.SpeedrunTool {
    public class SpeedrunToolSettings {
        public static SpeedrunToolSettings Instance { get; } = new();
        public bool Enabled { get; set; } = true;
    }
}
namespace Celeste.Mod.SpeedrunTool.ModInterop { public static class TasUtils { public static bool Running { get; set; } } }
namespace Celeste.Mod.SpeedrunTool.SaveLoad {
    public enum State { None, Saving, Loading, Waiting }
    public class SaveLoadAction { public static Action<Dictionary<Type, Dictionary<string, object>>, Level>? Save, Load; }
    public class SaveSlot(string name) { public StateManager StateManager = new(); public string Name = name; }
    public static class SaveSlotsManager {
        public static bool Free = true;
        public static Dictionary<string, SaveSlot> Dictionary = [];
        public static SaveSlot Slot = new("user");
        public static string SlotName { get; private set; } = "user";
        public static bool IsAllFree() => Free && Dictionary.Values.All(s => s.StateManager.State is State.None or State.Waiting);
        public static bool SwitchSlot(string name) {
            if (!IsAllFree()) return false;
            foreach (SaveSlot slot in Dictionary.Values) slot.StateManager.preCloneTask?.Wait();
            if (!Dictionary.TryGetValue(name, out var next)) Dictionary[name] = next = new(name);
            Slot = next; SlotName = name; return true;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ClearAll() {
            foreach (var slot in Dictionary.Values) { Slot = slot; slot.StateManager.ClearStateImpl(false); }
            Dictionary = []; SwitchSlot("user");
        }
    }
    public class StateManager {
        public static StateManager Instance => SaveSlotsManager.Slot.StateManager;
        public State State { get; set; }
        public bool SavedByTas { get; set; }
        public bool IsSaved { get; private set; }
        public int Saves, Loads, Clones, Freezes;
        public bool Throw, Reject;
        public Task? preCloneTask;
        public string? PreCloneObservedSlot;
        public Dictionary<Type, Dictionary<string, object>> Values = [];
        private Session? savedSession;
        private int savedPosition;
        private bool savedGolden;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool SaveStateImpl(bool tas, out string popup) {
            popup = "test";
            if (Reject) return false;
            SavedByTas = tas; State = State.Saving;
            if (Throw) throw new InvalidOperationException("expected failure");
            Values.Clear();
            Level level = (Level)Monocle.Engine.Scene!;
            savedSession = level.Session.Copy(); savedPosition = level.Position; savedGolden = level.GoldenMarked;
            IsSaved = true;
            Utils.StateMarkUtils.ReColor(Values, level);
            SaveLoadAction.Save?.Invoke(Values, level);
            PreCloneSavedEntities();
            if (tas) State = State.None;
            else { Freezes++; State = State.Waiting; }
            Saves++; return true;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool LoadStateImpl(bool tas, out string popup) {
            popup = "test";
            if (Reject || !IsSaved) return false;
            State = State.Loading;
            if (Throw) throw new InvalidOperationException("expected failure");
            Level level = (Level)Monocle.Engine.Scene!;
            level.Session = savedSession!.Copy(); level.Position = savedPosition; level.GoldenMarked = savedGolden;
            level.Tracker.Player = new();
            Utils.StateMarkUtils.ReColor(Values, level);
            SaveLoadAction.Load?.Invoke(Values, level);
            PreCloneSavedEntities();
            if (tas) LoadStateComplete(level);
            else { Freezes++; State = State.Waiting; }
            Loads++; return true;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void PreCloneSavedEntities() {
            Clones++;
            preCloneTask = Task.Run(async () => {
                if (CloneGate is { } gate) await gate.Task;
                await Task.Delay(2);
                PreCloneObservedSlot = SaveSlotsManager.SlotName;
                if (FailClone) throw new Exception("expected clone failure");
            });
        }
        public static TaskCompletionSource? CloneGate;
        public static bool FailClone;
        private void LoadStateComplete(Level level) => State = State.None;
        public bool ClearStateImpl(bool gc) {
            preCloneTask?.Wait(); IsSaved = false; savedSession = null; Values.Clear(); State = State.None; return true;
        }
    }
}
namespace FMOD {
    public enum RESULT { OK }
}
namespace FMOD.Studio {
    public enum STOP_MODE { IMMEDIATE }
    public sealed class System { public int Flushes; public void flushCommands() => Flushes++; }
    public class EventInstance(string path) {
        public string Path = path;
        public bool Valid = true;
        public int Stops;
        public bool isValid() => Valid;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public FMOD.RESULT start() => FMOD.RESULT.OK;
        public void stop(STOP_MODE mode) => Stops++;
    }
}
namespace Celeste.Mod.SpeedrunTool.SaveLoad.Utils {
    public static class StateMarkUtils {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReColor(Dictionary<Type, Dictionary<string, object>> values, Level level) {
            if (SaveLoad.StateManager.Instance.SavedByTas) return;
            level.Session.SetFlag("SpeedrunTool_SavedSate"); level.GoldenMarked = true;
        }
    }
}
