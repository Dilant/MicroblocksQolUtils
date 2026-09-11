using System.Text.Json;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class MusicEventReplayRenderer {
    private sealed record Row(double Time, string Kind, string Track, string Event,
        int Timeline, bool Paused, string PlaybackState, Dictionary<string, float> Parameters);
    private sealed class TrackState {
        internal string Event = "";
        internal ulong Id;
        internal Dictionary<string, float> Parameters = new(StringComparer.Ordinal);
        internal bool Paused;
    }

    internal static bool RenderToSidecar(string journalPath, string sidecarPath,
        IReadOnlyList<RecordingClip> clips, bool reconstruct) {
        if (clips.Count == 0) return false;
        List<AudioCommand> commands = Plan(journalPath, clips, reconstruct);
        return AudioEventReplayRenderer.RenderCommandsToSidecar(commands, sidecarPath,
            clips.Sum(clip => clip.DurationSeconds));
    }

    internal static List<AudioCommand> Plan(string journalPath,
        IReadOnlyList<RecordingClip> clips, bool reconstruct) =>
        BuildCommands(Read(journalPath), clips, reconstruct);

    private static List<Row> Read(string path) {
        List<Row> rows = [];
        bool header = false, complete = false;
        foreach (string line in File.ReadLines(path)) {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement row = document.RootElement;
            if (complete) throw new InvalidDataException("Music journal has data after its footer");
            string? type = row.GetProperty("type").GetString();
            if (type == "header") {
                if (header || row.GetProperty("version").GetInt32() != 1)
                    throw new InvalidDataException("Unsupported music journal header");
                header = true; continue;
            }
            if (!header) throw new InvalidDataException("Music journal is missing its header");
            if (type == "end") {
                if (!row.GetProperty("complete").GetBoolean())
                    throw new InvalidDataException("Music event capture is incomplete");
                complete = true; continue;
            }
            if (type != "event") throw new InvalidDataException("Unknown music journal entry");
            Dictionary<string, float> parameters = [];
            if (row.TryGetProperty("parameters", out JsonElement values)
                && values.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty value in values.EnumerateObject())
                    if (value.Value.TryGetSingle(out float number)) parameters[value.Name] = number;
            rows.Add(new(
                row.GetProperty("time_nanos").GetUInt64() / 1_000_000_000d,
                row.GetProperty("kind").GetString() ?? "snapshot",
                row.GetProperty("track").GetString() ?? "main",
                row.GetProperty("event").GetString() ?? "",
                row.GetProperty("timeline_milliseconds").GetInt32(),
                row.GetProperty("paused").GetBoolean(),
                row.GetProperty("playback_state").GetString() ?? "STOPPED",
                parameters));
        }
        if (!header || !complete) throw new InvalidDataException("Truncated music event capture");
        return rows.OrderBy(row => row.Time).ToList();
    }

    private static List<AudioCommand> BuildCommands(List<Row> rows,
        IReadOnlyList<RecordingClip> clips, bool reconstruct) {
        List<AudioCommand> result = [];
        Dictionary<string, TrackState> tracks = new(StringComparer.Ordinal);
        ulong nextId = 1_000_000;
        double output = 0;
        foreach (RecordingClip clip in clips) {
            double start = clip.StartSeconds, end = start + clip.DurationSeconds;
            bool forceSync = !reconstruct || clip.BgmFollowsVideo;
            foreach (string track in new[] { "main", "alt" }) {
                TrackState state = tracks.GetValueOrDefault(track) ?? new();
                Row? initial = rows.LastOrDefault(row => row.Track == track && row.Time <= start);
                ApplyInitial(initial, state, start, output, forceSync, ref nextId, result);
                tracks[track] = state;
            }
            foreach (Row row in rows.Where(row => row.Time > start && row.Time < end)) {
                TrackState state = tracks.GetValueOrDefault(row.Track) ?? new();
                ApplyRow(row, state, output + row.Time - start, ref nextId, result);
                tracks[row.Track] = state;
            }
            output += clip.DurationSeconds;
        }
        return result.OrderBy(command => command.TimestampNanos).ToList();
    }

    private static void ApplyInitial(Row? row, TrackState state, double sourceStart, double output,
        bool forceSync, ref ulong nextId, List<AudioCommand> result) {
        if (row is null || string.IsNullOrWhiteSpace(row.Event)
            || row.PlaybackState is "STOPPED" or "STOPPING") {
            if (state.Event.Length != 0) Stop(state, output, result);
            return;
        }
        if (state.Event.Length != 0
            && (forceSync || !string.Equals(state.Event, row.Event, StringComparison.Ordinal)))
            Stop(state, output, result);
        if (state.Event.Length == 0) {
            state.Event = row.Event; state.Id = nextId++;
            state.Parameters = new(row.Parameters, StringComparer.Ordinal);
            state.Paused = row.Paused;
            int timeline = row.Timeline + (row.Paused ? 0 : (int)Math.Max(0, (sourceStart - row.Time) * 1000));
            result.Add(Command(output, state.Id, state.Event, "start", state, timeline, row.Paused));
        } else {
            foreach (var pair in row.Parameters)
                SetParameter(state, output, pair.Key, pair.Value, result);
            if (state.Paused != row.Paused) {
                state.Paused = row.Paused;
                result.Add(Command(output, state.Id, state.Event, "setPaused", state,
                    value: row.Paused ? 1 : 0));
            }
        }
    }

    private static void ApplyRow(Row row, TrackState state, double output,
        ref ulong nextId, List<AudioCommand> result) {
        if (row.Kind is "start" or "switch" or "snapshot") {
            if (string.IsNullOrWhiteSpace(row.Event)
                || row.PlaybackState is "STOPPED" or "STOPPING") {
                if (state.Event.Length != 0) Stop(state, output, result);
                return;
            }
            if (state.Event.Length != 0) Stop(state, output, result);
            state.Event = row.Event; state.Id = nextId++;
            state.Parameters = new(row.Parameters, StringComparer.Ordinal);
            state.Paused = row.Paused;
            result.Add(Command(output, state.Id, state.Event, "start", state, row.Timeline, row.Paused));
        } else if (row.Kind.StartsWith("stop", StringComparison.Ordinal)) {
            if (state.Event.Length != 0) Stop(state, output, result);
        } else if (row.Kind is "pause" or "resume") {
            state.Paused = row.Paused;
            if (state.Event.Length != 0)
                result.Add(Command(output, state.Id, state.Event, "setPaused", state,
                    value: row.Paused ? 1 : 0));
        } else if (row.Kind == "seek") {
            if (state.Event.Length != 0)
                result.Add(Command(output, state.Id, state.Event, "setTimelinePosition", state,
                    value: row.Timeline));
        } else if (row.Kind == "parameter") {
            foreach (var pair in row.Parameters)
                SetParameter(state, output, pair.Key, pair.Value, result);
        }
    }

    private static void SetParameter(TrackState state, double output, string name, float value,
        List<AudioCommand> result) {
        state.Parameters[name] = value;
        if (state.Event.Length != 0)
            result.Add(Command(output, state.Id, state.Event, "setParameterValue", state,
                parameter: name, value: value));
    }

    private static void Stop(TrackState state, double output, List<AudioCommand> result) {
        result.Add(Command(output, state.Id, state.Event, "stop", state));
        state.Event = ""; state.Id = 0;
    }

    private static AudioCommand Command(double seconds, ulong id, string path, string operation,
        TrackState state, int? timeline = null, bool? paused = null, string? parameter = null,
        float? value = null) => new((ulong)Math.Max(0, seconds * 1_000_000_000d), id, path,
            operation, parameter, value, "music", new(new(state.Parameters, StringComparer.Ordinal), 1, 1,
                paused ?? state.Paused, timeline ?? 0, null));
}
