using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

internal enum PlayerLiveState {
    Alive,          // player present and playable
    Dying,          // death registered; dead body animating / death cutscene
    Respawning,     // post-death reload / load: respawn wipe-in / StIntroRespawn, until playable
    EnteringLevel,  // entering a level / room (chapter start, transition, rebuild), until playable
}

internal readonly record struct PlayerLiveSnapshot(
    PlayerLiveState State,
    bool EnteredDying,
    bool LeftDying,
    bool EnteredRespawning,
    bool EnteredAlive,
    bool PlayerAlive,
    bool PlayerDead,
    bool PlayerControllable,
    bool InCutscene,
    PlayerDeadBody? DeadBody);

internal static class PlayerLifecycle {
    private static PlayerLiveState state = PlayerLiveState.Alive;

    private static bool playerAlive, playerDead, playerControllable, inCutscene;
    private static PlayerDeadBody? deadBody;

    internal static PlayerLiveState State => state;
    internal static bool InDeathSequence => state == PlayerLiveState.Dying;
    internal static PlayerLiveSnapshot Latest { get; private set; }
    internal static ulong DeathEpoch { get; private set; }

    // Re-reads the live fields from the scene; the phase machine is untouched.
    // NotifyDeath / Rewind call this too, since they run outside the frame cadence.
    private static void Sample(Level? level) {
        Player? playerNow = level?.Tracker.GetEntity<Player>();
        deadBody = level?.Entities.FindFirst<PlayerDeadBody>();
        playerAlive = playerNow is { Dead: false };
        playerDead = playerNow is { Dead: true };
        playerControllable = level is not null && IsControllable(level, playerNow);
        inCutscene = level is not null && (level.InCutscene || level.SkippingCutscene);
    }

    internal static PlayerLiveSnapshot NotifyDeath(Level? level = null) {
        DeathEpoch++;
        Sample(level ?? Engine.Scene as Level);
        return Enter(PlayerLiveState.Dying);
    }

    internal static PlayerLiveSnapshot Rewind(Level? level = null) {
        Sample(level ?? Engine.Scene as Level);
        return Enter(state == PlayerLiveState.Dying ? PlayerLiveState.Alive : state);
    }

    internal static PlayerLiveSnapshot ResetRun() {
        playerAlive = playerDead = playerControllable = inCutscene = false;
        deadBody = null;
        return Enter(PlayerLiveState.EnteringLevel);
    }

    private static PlayerLiveSnapshot Enter(PlayerLiveState to) {
        PlayerLiveState before = state;
        state = to;
        return Build(before);
    }

    // The single per-frame sample: call it once per frame (AutoRecorder.AfterEngineUpdate),
    // everyone else reads Latest; a second call would consume this frame's LeftDying.
    internal static PlayerLiveSnapshot AdvanceOnce(Level? level) {
        Sample(level);
        PlayerLiveState before = state;
        if (state == PlayerLiveState.Dying && (level is null || DeathSequenceSettled(level, deadBody)))
            state = PlayerLiveState.Respawning;
        else if (state is PlayerLiveState.Respawning or PlayerLiveState.EnteringLevel && playerControllable)
            state = PlayerLiveState.Alive;
        return Build(before);
    }

    private static PlayerLiveSnapshot Build(PlayerLiveState before) {
        PlayerLiveSnapshot snapshot = new(
            State: state,
            EnteredDying: before != PlayerLiveState.Dying && state == PlayerLiveState.Dying,
            LeftDying: before == PlayerLiveState.Dying && state != PlayerLiveState.Dying,
            EnteredRespawning: before != PlayerLiveState.Respawning && state == PlayerLiveState.Respawning,
            EnteredAlive: before != PlayerLiveState.Alive && state == PlayerLiveState.Alive,
            PlayerAlive: playerAlive,
            PlayerDead: playerDead,
            PlayerControllable: playerControllable,
            InCutscene: inCutscene,
            DeadBody: deadBody);
        Latest = snapshot;
        return snapshot;
    }

    internal static bool IsControllable(Level level, Player? candidate) =>
        candidate is { Dead: false }
        && !level.Transitioning
        && candidate.StateMachine.State != Player.StIntroRespawn;

    internal static bool IsAlive(Player? candidate) => candidate is { Dead: false };

    private static bool DeathSequenceSettled(Level level, PlayerDeadBody? deadBody) {
        if (deadBody is null && level.Tracker.GetEntity<Player>() is { Dead: false }) return true;
        if (level.InCutscene || level.SkippingCutscene) return false;
        if (deadBody is not null || level.Transitioning) return false;
        if (level.Wipe is ScreenWipe wipe) return wipe.WipeIn;
        return false;
    }
}
