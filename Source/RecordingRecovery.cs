using System.Text.Json;

namespace Celeste.Mod.MicroblocksQolUtils;

// Source files survive until a successful export or an explicit discard. The
// checkpoint is replaced atomically, so an interrupted write keeps the old one.
internal static class RecordingRecovery {
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Active = new(StringComparer.OrdinalIgnoreCase);
    private static int recovering;
    internal static bool IsRecovering => Volatile.Read(ref recovering) != 0;
    internal static string Status { get; private set; } = "恢复崩溃或退出前未保存的录像";
    internal static string ManifestPath(string source) => source + ".recovery.json";
    internal static void Register(string source) => Active[Path.GetFullPath(source)] = 0;
    internal static void Release(string source) => Active.TryRemove(Path.GetFullPath(source), out _);

    internal static void Checkpoint(string source, double seconds) {
        string path = ManifestPath(source);
        string temporary = path + ".new";
        try {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new CheckpointData(1, seconds)));
            File.Move(temporary, path, overwrite: true);
        } catch (Exception exception) {
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/RecoveryCheckpoint");
        }
    }

    internal static IEnumerable<RecordingClip> Candidates(string root) {
        string working = Path.Combine(root, ".working");
        if (!Directory.Exists(working)) return [];
        List<RecordingClip> candidates = [];
        foreach (string path in Directory.EnumerateFiles(working, "*.mkv.recovery.json", SearchOption.AllDirectories)) {
            string source = Path.GetFullPath(path[..^".recovery.json".Length]);
            if (Active.ContainsKey(source) || !File.Exists(source)) continue;
            try {
                var checkpoint = JsonSerializer.Deserialize<CheckpointData>(File.ReadAllText(path));
                if (checkpoint is { Version: 1 } && double.IsFinite(checkpoint.Seconds) && checkpoint.Seconds > 0)
                    candidates.Add(new(source, 0, checkpoint.Seconds, "", 0, BgmFollowsVideo: true));
            } catch (Exception exception) { Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/RecoveryScan"); }
        }
        return candidates;
    }

    internal static async void Recover() {
        if (Interlocked.Exchange(ref recovering, 1) != 0) return;
        string root = AutoRecorder.RecordingRoot;
        Status = "恢复中…";
        int saved = 0, failed = 0;
        try {
            await Task.Run(async () => {
                foreach (RecordingClip clip in Candidates(root)) {
                    // Stable output name makes retries idempotent after a crash
                    // between exporting the MP4 and removing the checkpoint.
                    string output = Path.Combine(root, "full", "recovered", Path.GetFileNameWithoutExtension(clip.Source) + ".mp4");
                    if (await NativeRecordingFinalizer.FinishAsync([clip], output, "恢复录像", false, false,
                        _ => { }, preferVideoCopy: true).ConfigureAwait(false)) {
                        saved++;
                        foreach (string file in SourceFiles(clip.Source)) {
                            try { File.Delete(file); } catch { }
                        }
                    } else failed++;
                }
            }).ConfigureAwait(false);
            Status = saved == 0 && failed == 0 ? "没有可恢复的录像" : $"已恢复 {saved} 个，失败 {failed} 个（失败源文件已保留）";
        } catch (Exception exception) {
            Status = "恢复失败，源文件已保留";
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/Recover");
        } finally { Volatile.Write(ref recovering, 0); }
    }

    internal static string[] SourceFiles(string source) => [source, source + ".sfxchunks", source + ".bgmchunks",
        source + ".music.jsonl", source + ".capture.json", ManifestPath(source), ManifestPath(source) + ".new"];
    private sealed record CheckpointData(int Version, double Seconds);
}
