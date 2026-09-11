using System.Text.Json;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>A per-sink durable music event stream. The recording phase stores no audio PCM.</summary>
internal sealed class MusicJournal : IDisposable {
    private readonly StreamWriter writer;
    private readonly List<CaptureMusic> pending = new(256);
    private ulong? origin;
    private bool overflow;
    internal MusicJournal(string path) {
        writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
        writer.WriteLine("{\"type\":\"header\",\"version\":1,\"clock\":\"first-video-frame\"}");
    }
    internal void Start(ulong timestamp) {
        origin = timestamp;
        // A recorder may subscribe halfway through a song. Keep state at its own zero,
        // without restarting any global observer or another recorder's music timeline.
        foreach (var value in pending.Where(v => v.TimestampNanos <= timestamp)
            .GroupBy(v => v.Track).Select(g => g.Last()).OrderBy(v => v.Sequence)) Write(value);
        foreach (var value in pending.Where(v => v.TimestampNanos > timestamp)) Write(value);
        pending.Clear();
    }
    internal void Accept(CaptureMusic value) {
        if (origin is null) {
            if (pending.Count == 256) { overflow = true; return; }
            pending.Add(value);
        } else Write(value);
    }
    private void Write(CaptureMusic value) {
        ulong time = value.TimestampNanos > origin!.Value ? value.TimestampNanos - origin.Value : 0;
        writer.WriteLine(JsonSerializer.Serialize(new {
            type = "event", time_nanos = time, sequence = value.Sequence, kind = value.Kind,
            track = value.Track, @event = value.Event, instance_id = value.InstanceId,
            timeline_milliseconds = value.TimelineMilliseconds, paused = value.Paused,
            playback_state = value.PlaybackState, parameters = value.Parameters
        }));
    }
    internal void Finish(bool complete) {
        bool valid = complete && !overflow;
        writer.WriteLine(JsonSerializer.Serialize(new { type = "end", complete = valid }));
        writer.Flush();
        if (!valid) throw new InvalidDataException("Music event journal is incomplete; refusing to silently reconstruct the wrong BGM.");
    }
    public void Dispose() => writer.Dispose();
}
