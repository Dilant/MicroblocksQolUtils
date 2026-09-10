using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class InstantDeaths {
    private static readonly System.Reflection.MethodInfo? EndMethod = typeof(PlayerDeadBody).GetMethod(
        "End",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
    );
    private static readonly System.Reflection.FieldInfo? FinishedField = typeof(PlayerDeadBody).GetField(
        "finished",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
    );
    private static PlayerDeadBody? reloadedBody;

    public static void Load() {
        On.Celeste.Level.Reload += LevelReload;
    }

    public static void Unload() {
        On.Celeste.Level.Reload -= LevelReload;
        Reset();
    }

    public static void AfterEngineUpdate() {
        if (!MicroblocksQolUtilsModule.Settings.RemoveDeathAnimation || SpeedrunToolAutoSave.HasManualState
            || Engine.Scene is not Level level) {
            reloadedBody = null;
            return;
        }

        // PlayerDeadBody is not tracked by vanilla Monocle. Ask Everest to register the
        // type on demand instead of indexing Tracker.Entities through GetEntity<T>().
        PlayerDeadBody? body = level.Tracker
            .GetEntitiesTrackIfNeeded<PlayerDeadBody>()
            .FirstOrDefault() as PlayerDeadBody;
        if (body is null) {
            reloadedBody = null;
            return;
        }

        if (ReferenceEquals(body, reloadedBody)) return;
        reloadedBody = body;

        // End can reload the level before the vanilla death coroutine has finished. In
        // particular, a manual retry may unload the dead body while DeathRoutine is still
        // scheduled for a later update, where it then dereferences the unloaded level.
        // The animation is being skipped anyway, so stop that coroutine before ending.
        CancelDeathRoutine(body);

        // DeathRoutine is commonly detoured by other mods, so replacing its enumerator is
        // not reliable. Run after the frame instead: the body is in the scene, and recorder
        // death handling has already completed before we finish the death. Invoke End first so
        // hooks such as SpeedrunTool's auto-load-after-death logic can take over. If vanilla End
        // ran, cancel the screen wipe it just created and reload immediately instead.
        if (EndMethod is null || FinishedField is null) {
            (body.DeathAction ?? level.Reload)();
            return;
        }

        ScreenWipe? previousWipe = level.Wipe;
        EndMethod.Invoke(body, null);
        if (FinishedField.GetValue(body) is not true
            || level.Wipe is not ScreenWipe deathWipe
            || ReferenceEquals(deathWipe, previousWipe)) return;

        deathWipe.OnComplete = null;
        deathWipe.Cancel();
        (body.DeathAction ?? level.Reload)();
    }

    public static void Reset() {
        reloadedBody = null;
    }

    private static void LevelReload(On.Celeste.Level.orig_Reload orig, Level self) {
        if (MicroblocksQolUtilsModule.Settings.RemoveDeathAnimation) {
            foreach (Entity entity in self.Tracker.GetEntitiesTrackIfNeeded<PlayerDeadBody>()) {
                if (entity is PlayerDeadBody body) CancelDeathRoutine(body);
            }
        }

        orig(self);
    }

    private static void CancelDeathRoutine(PlayerDeadBody body) {
        body.Get<Coroutine>()?.Cancel();
    }
}
