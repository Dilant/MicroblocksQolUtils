using System.Text.Json;
using System.Threading.Channels;

namespace Celeste.Mod.MicroblocksQolUtils;

internal sealed record AudioCommand(ulong TimestampNanos, ulong InstanceId, string EventPath,
    string Operation, string? Parameter = null, float? Value = null,
    string? Bus = null, AudioInstanceState? State = null, float[]? Attributes = null);

internal sealed record AudioInstanceState(Dictionary<string, float> Parameters, float Volume,
    float Pitch, bool Paused, int TimelineMilliseconds, float[]? Attributes);

/// <summary>Per-recording asynchronous journal. Publishing never writes files or waits for disk.</summary>
internal sealed class AudioEventJournal : IDisposable {
    private static readonly object Gate = new();
    private static readonly List<AudioEventJournal> Sinks = [];
    private readonly Channel<object> queue;
    private readonly Task writerTask;
    private long sequence;
    private bool closed, incomplete;
    private Exception? failure;

    internal AudioEventJournal(string path, int capacity = 16384) {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
        queue = Channel.CreateBounded<object>(new BoundedChannelOptions(capacity) {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        writerTask = Task.Run(async () => {
            try {
                await writer.WriteLineAsync("{\"type\":\"header\",\"version\":2,\"clock\":\"capture-monotonic-nanos\"}");
                await foreach (object entry in queue.Reader.ReadAllAsync())
                    await writer.WriteLineAsync(JsonSerializer.Serialize(entry));
                await writer.WriteLineAsync(JsonSerializer.Serialize(new {
                    type = "footer", complete = !incomplete, events = sequence
                }));
            } catch (Exception e) { failure = e; }
            finally { await writer.DisposeAsync(); }
        });
        lock (Gate) Sinks.Add(this);
    }

    internal static void Publish(AudioCommand command) {
        lock (Gate) foreach (var sink in Sinks) sink.AcceptLocked(command);
    }
    internal void Accept(AudioCommand command) {
        lock (Gate) AcceptLocked(command);
    }
    private void AcceptLocked(AudioCommand command) {
        Enqueue(new {
            type = "event", sequence = ++sequence, command.TimestampNanos,
            command.InstanceId, command.EventPath, command.Operation, command.Parameter, command.Value
            , bus = command.Bus ?? (command.EventPath.StartsWith("event:/music/", StringComparison.Ordinal)
                ? "music" : "sfx"), command.State, command.Attributes
        });
    }
    internal void Start(ulong timestampNanos) {
        lock (Gate) Enqueue(new { type = "origin", timestampNanos });
    }
    private void Enqueue(object value) {
        if (!closed && !queue.Writer.TryWrite(value)) incomplete = true;
    }
    internal void Finish(bool complete) {
        lock (Gate) {
            if (!closed) {
                incomplete |= !complete;
                closed = true;
                Sinks.Remove(this);
                queue.Writer.TryComplete();
            }
        }
        writerTask.GetAwaiter().GetResult();
        if (failure is not null) throw new IOException("Audio event journal writer failed.", failure);
        if (incomplete) throw new InvalidDataException("Audio event journal is incomplete.");
    }
    public void Dispose() {
        lock (Gate) {
            if (!closed) {
                incomplete = true;
                closed = true;
                Sinks.Remove(this);
                queue.Writer.TryComplete();
            }
        }
        writerTask.GetAwaiter().GetResult();
    }
}
