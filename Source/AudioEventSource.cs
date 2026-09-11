using System.Text.Json;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>Validated command source, independent of FMOD and the rendering backend.</summary>
internal sealed record AudioEventSource(ulong OriginNanos, IReadOnlyList<AudioCommand> Commands) {
    internal static AudioEventSource Read(string path) {
        ulong? origin = null;
        bool header = false, footer = false;
        long sequence = 0;
        List<AudioCommand> commands = [];
        foreach (string line in File.ReadLines(path)) {
            using var json = JsonDocument.Parse(line);
            var row = json.RootElement;
            if (footer) throw new InvalidDataException("Audio journal contains data after its footer.");
            string? type = row.GetProperty("type").GetString();
            if (!header && type != "header") throw new InvalidDataException("Missing audio journal header.");
            switch (type) {
                case "header":
                    if (header || row.GetProperty("version").GetInt32() != 2)
                        throw new InvalidDataException("Unsupported audio journal version.");
                    header = true;
                    break;
                case "origin":
                    if (origin is not null) throw new InvalidDataException("Duplicate audio origin.");
                    origin = row.GetProperty("timestampNanos").GetUInt64();
                    break;
                case "event":
                    if (row.GetProperty("sequence").GetInt64() != ++sequence)
                        throw new InvalidDataException("Missing or unordered audio command.");
                    commands.Add(new(row.GetProperty("TimestampNanos").GetUInt64(),
                        row.GetProperty("InstanceId").GetUInt64(), row.GetProperty("EventPath").GetString()!,
                        row.GetProperty("Operation").GetString()!,
                        row.GetProperty("Parameter").ValueKind == JsonValueKind.Null ? null : row.GetProperty("Parameter").GetString(),
                        row.GetProperty("Value").ValueKind == JsonValueKind.Null ? null : row.GetProperty("Value").GetSingle(),
                        row.TryGetProperty("bus", out var bus) ? bus.GetString() : null,
                        row.TryGetProperty("State", out var state) && state.ValueKind != JsonValueKind.Null
                            ? state.Deserialize<AudioInstanceState>() : null,
                        row.TryGetProperty("Attributes", out var attributes) && attributes.ValueKind != JsonValueKind.Null
                            ? attributes.Deserialize<float[]>() : null));
                    break;
                case "footer":
                    if (!row.GetProperty("complete").GetBoolean() || row.GetProperty("events").GetInt64() != sequence)
                        throw new InvalidDataException("Audio event capture is incomplete.");
                    footer = true;
                    break;
                default: throw new InvalidDataException($"Unknown audio journal entry: {type}");
            }
        }
        if (!header || !footer || origin is null) throw new InvalidDataException("Truncated audio event capture.");
        return new(origin.Value, commands);
    }
}
