using System.Text.Json;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class NativeRecordingFinalizer {
    public static async Task<bool> FinishAsync(
        IReadOnlyList<RecordingClip> clips,
        string output,
        string description,
        bool reconstructBgm,
        bool removeFreezeFrames,
        Action<double>? progress = null,
        bool preferVideoCopy = false
    ) {
        try {
            if (clips.Count == 0 || clips.Any(clip => !File.Exists(clip.Source))) return false;
            progress?.Invoke(0d);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            QolSettings settings = MicroblocksQolUtilsModule.Settings;
            await NativeCaptureBridge.FinalizeRecordingAsync(
                clips,
                output,
                settings.RecordingEncoder,
                settings.RecordingBitrateKbps,
                settings.RecordingFrameRate,
                reconstructBgm,
                removeFreezeFrames,
                settings.BgmEventMapFile,
                preferVideoCopy: preferVideoCopy,
                progress: value => progress?.Invoke(value * 0.99d)
            ).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                output + ".timeline.json",
                JsonSerializer.Serialize(new { clips, reconstructBgm, removeFreezeFrames,
                    captureReports = ReadCaptureReports(clips) },
                    new JsonSerializerOptions { WriteIndented = true })
            ).ConfigureAwait(false);
            progress?.Invoke(1d);
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils/Recorder", $"Saved {description}: {output}");
            return true;
        } catch (Exception exception) {
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder");
            return false;
        }
    }

    private static JsonElement[] ReadCaptureReports(IReadOnlyList<RecordingClip> clips) {
        List<JsonElement> reports = [];
        foreach (string source in clips.Select(clip => clip.Source).Distinct()) {
            try {
                string path = source + ".capture.json";
                if (File.Exists(path)) reports.Add(JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path)));
            } catch (Exception exception) {
                Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/CaptureReport");
            }
        }
        return reports.ToArray();
    }
}
