namespace Celeste.Mod.MicroblocksQolUtils;

public sealed record RecordingClip(
    string Source,
    double StartSeconds,
    double DurationSeconds,
    string MusicEvent,
    int MusicTimelineMilliseconds,
    bool SeamlessFromPrevious = false,
    bool BgmFollowsVideo = false,
    string RoomName = ""
) {
    public RecordingClip RetainTail(double durationSeconds) {
        double duration = Math.Clamp(durationSeconds, 0, DurationSeconds);
        double offset = DurationSeconds - duration;
        return this with {
            StartSeconds = StartSeconds + offset,
            DurationSeconds = duration,
            MusicTimelineMilliseconds = MusicTimelineMilliseconds + (int)Math.Round(offset * 1_000d)
        };
    }
}

public sealed record RecordingTimelineSnapshot(
    IReadOnlyList<RecordingClip> Clips,
    IReadOnlyList<RecordingClip>? RespawnAnchorClips = null,
    string? RecoveryVersionId = null,
    string? RecordingSource = null
) {
    public RecordingTimelineSnapshot Copy() => new(
        Array.AsReadOnly(Clips.ToArray()),
        RespawnAnchorClips is null ? null : Array.AsReadOnly(RespawnAnchorClips.ToArray()),
        RecoveryVersionId,
        RecordingSource
    );
}
