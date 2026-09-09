using System.Diagnostics;

namespace Celeste.Mod.MicroblocksQolUtils;

internal enum RecordingLibraryKind {
    DeathReplay,
    Full,
    Automatic
}

internal readonly record struct RecordingLibraryEntry(
    string Path,
    string FileName,
    string RelativeDirectory,
    DateTime ModifiedAt,
    long SizeBytes,
    RecordingLibraryKind Kind
);

internal static class RecordingLibrary {
    public static IReadOnlyList<RecordingLibraryEntry> Scan() {
        string root = AutoRecorder.RecordingRoot;
        if (!Directory.Exists(root)) return [];

        try {
            return Directory
                .EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories)
                .Where(path => !IsWorkingFile(root, path))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => new RecordingLibraryEntry(
                    file.FullName,
                    file.Name,
                    RelativeDirectory(root, file.DirectoryName),
                    file.LastWriteTime,
                    file.Length,
                    KindOf(root, file.FullName)
                ))
                .ToArray();
        } catch (Exception exception) {
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/Library");
            return [];
        }
    }

    public static bool OpenFolder(out string error) {
        try {
            string root = AutoRecorder.RecordingRoot;
            Directory.CreateDirectory(root);
            OpenShell(root);
            error = "";
            return true;
        } catch (Exception exception) {
            error = exception.Message;
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/OpenFolder");
            return false;
        }
    }

    public static bool OpenRecording(string path, out string error) {
        try {
            if (AutoRecorder.TryGetFinalizationProgress(path, out _, out _)) {
                error = "录像仍在生成中";
                return false;
            }
            if (!IsSafeRecording(path) || !File.Exists(path)) {
                error = "录像文件不存在";
                return false;
            }
            OpenShell(path);
            error = "";
            return true;
        } catch (Exception exception) {
            error = exception.Message;
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/OpenFile");
            return false;
        }
    }

    public static bool DeleteRecording(string path, out string error) {
        try {
            if (AutoRecorder.TryGetFinalizationProgress(path, out _, out _)) {
                error = "录像仍在生成中";
                return false;
            }
            if (!IsSafeRecording(path)) {
                error = "录像路径不在当前输出目录中";
                return false;
            }
            if (File.Exists(path)) File.Delete(path);
            TryDelete(path + ".timeline.json");
            error = "";
            return true;
        } catch (Exception exception) {
            error = exception.Message;
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/DeleteFile");
            return false;
        }
    }

    private static void OpenShell(string path) {
        Process.Start(new ProcessStartInfo {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static bool IsSafeRecording(string path) {
        if (!string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase)) return false;
        string root = Path.GetFullPath(AutoRecorder.RecordingRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !IsWorkingFile(root, fullPath);
    }

    private static bool IsWorkingFile(string root, string path) {
        return IsUnderDirectory(path, Path.Combine(root, ".working"));
    }

    internal static RecordingLibraryKind KindOf(string root, string path) {
        return IsUnderDirectory(path, Path.Combine(root, "deaths"))
            ? RecordingLibraryKind.DeathReplay
            : IsUnderDirectory(path, Path.Combine(root, "auto"))
                ? RecordingLibraryKind.Automatic
                : RecordingLibraryKind.Full;
    }

    private static bool IsUnderDirectory(string path, string directory) {
        string fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string RelativeDirectory(string root, string? directory) {
        if (string.IsNullOrWhiteSpace(directory)) return "输出目录";
        string relative = Path.GetRelativePath(root, directory);
        return relative == "." ? "输出目录" : relative;
    }

    private static void TryDelete(string path) {
        try { File.Delete(path); } catch { }
    }
}
