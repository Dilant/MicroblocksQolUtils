using System.Reflection;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class RecordingPauseAudio {
    private static readonly FieldInfo? Alt = typeof(Audio).GetField("currentAltMusicEvent", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly List<(FMOD.Studio.EventInstance Instance, bool Paused)> Music = [];
    private static bool active, gameplayPaused, stingsPaused;

    internal static void Pause() {
        if (active) return;
        active = true;
        gameplayPaused = Audio.BusPaused("bus:/gameplay_sfx");
        stingsPaused = Audio.BusPaused("bus:/music/stings");
        Audio.BusPaused("bus:/gameplay_sfx", true);
        Audio.BusPaused("bus:/music/stings", true);
        PauseMusic(Audio.CurrentMusicEventInstance);
        PauseMusic(Alt?.GetValue(null) as FMOD.Studio.EventInstance);
    }

    private static void PauseMusic(FMOD.Studio.EventInstance? instance) {
        if (instance is null || !instance.isValid()) return;
        if (instance.getPaused(out bool paused) != FMOD.RESULT.OK) return;
        Music.Add((instance, paused));
        // Instance commands enter the music journal, unlike a parent bus pause.
        // This keeps continuous post-mix from reading the excluded silent wait.
        instance.setPaused(true);
    }

    internal static void Resume() {
        if (!active) return;
        active = false;
        Audio.BusPaused("bus:/gameplay_sfx", gameplayPaused);
        Audio.BusPaused("bus:/music/stings", stingsPaused);
        foreach (var (instance, paused) in Music)
            if (instance.isValid()) instance.setPaused(paused);
        Music.Clear();
    }
}
