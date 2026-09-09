namespace Celeste.Mod.MicroblocksQolUtils;

internal static class RhythmMapDetector {
    private static readonly string[] RhythmMarkers = [
        "cassetteblock",
        "rhythm",
        "musicsync",
        "syncedmusic",
        "beatblock",
        "tempoblock"
    ];

    // A cassette/rhythm room must not change the export policy of its neighbours.
    public static bool IsRhythmSensitive(MapData? map, string roomName) =>
        IsRhythmSensitive(map?.Levels.FirstOrDefault(room => room.Name == roomName));

    public static bool IsRhythmSensitive(LevelData? room) => room is not null
        && (room.Entities.Any(entity => IsRhythmMarker(entity.Name))
            || room.Triggers.Any(trigger => IsRhythmMarker(trigger.Name)));

    private static bool IsRhythmMarker(string name) {
        string normalized = name.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();
        return RhythmMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }
}
