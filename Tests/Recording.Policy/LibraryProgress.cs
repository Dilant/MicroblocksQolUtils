using System.Collections;
using System.Reflection;
using Celeste.Mod.MicroblocksQolUtils;

internal static class LibraryProgress {
    public static void Run(QolSettings settings) {
        const BindingFlags hidden = BindingFlags.Static | BindingFlags.NonPublic;
        Type recorder = typeof(AutoRecorder);
        Type library = recorder.Assembly.GetType("Celeste.Mod.MicroblocksQolUtils.RecordingLibrary")!;
        Type jobType = recorder.GetNestedType("RecordingFinalizationJob", BindingFlags.NonPublic)!;
        string root = Path.GetFullPath(Path.Combine(".work", "library-progress-tests", Guid.NewGuid().ToString("N")));
        settings.RecordingDirectory = root;
        string first = Path.Combine(root, "full", "map", "first.mp4");
        string second = Path.Combine(root, "deaths", "map", "second.mp4");
        Array jobs = Array.CreateInstance(jobType, 2);
        for (int i = 0; i < 2; i++) jobs.SetValue(Activator.CreateInstance(jobType,
            new object[] { new[] { new RecordingClip("source.mkv", 0, 1, "", 0) },
                i == 0 ? first : second, "录像", false, false, false }), i);
        long id = (long)recorder.GetMethod("BeginFinalization", hidden)!.Invoke(null, [jobs])!;
        string[] Scan() => ((IEnumerable)library.GetMethod("Scan")!.Invoke(null, null)!).Cast<object>()
            .Select(entry => (string)entry.GetType().GetProperty("Path")!.GetValue(entry)!).ToArray();
        static void Check(bool condition, string message) {
            if (!condition) throw new Exception(message);
        }
        try {
            Check(!Directory.Exists(root) && Scan().Order().SequenceEqual(new[] { first, second }.Order()),
                "queued output rows require a physical MP4 to exist");
            recorder.GetMethod("UpdateFinalization", hidden)!.Invoke(null, [id, first, .21d, .42d, "录像"]);
            object[] progressArgs = [first, 0d, ""];
            Check((bool)recorder.GetMethod("TryGetFinalizationProgress", hidden)!.Invoke(null, progressArgs)!
                && (double)progressArgs[1] == .42d, "library destination did not resolve its live per-file progress");

            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            string video = Path.ChangeExtension(first, "working.video.mp4");
            string mux = Path.ChangeExtension(first, "working.mp4");
            File.WriteAllText(video, "temporary video");
            File.WriteAllText(mux, "temporary mux");
            string completed = Path.Combine(Path.GetDirectoryName(first)!, "completed.mp4");
            File.WriteAllText(completed, "completed video");
            string hiddenCapture = Path.Combine(root, ".working", "hidden.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(hiddenCapture)!);
            File.WriteAllText(hiddenCapture, "capture source");
            Check(Scan().Order().SequenceEqual(new[] { first, second, completed }.Order()),
                "temporary video/mux artifacts duplicated or replaced the progress row");
            foreach (string action in new[] { "OpenRecording", "DeleteRecording" }) {
                object[] arguments = [first, ""];
                Check(!(bool)library.GetMethod(action)!.Invoke(null, arguments)!, "active output accepted " + action);
            }
            File.Move(mux, first);
            Check(Scan().Count(path => path == first) == 1, "atomic publication duplicated the output row");
        } finally {
            recorder.GetMethod("EndFinalization", hidden)!.Invoke(null, [id]);
        }
        Check(Scan().Contains(first) && !Scan().Contains(second) && Scan().Length == 2,
            "completed row did not retain its identity or stale working files leaked into the library");
        string stale = Path.ChangeExtension(first, "working.video.mp4");
        object[] deleteArgs = [stale, ""];
        Check(!(bool)library.GetMethod("DeleteRecording")!.Invoke(null, deleteArgs)! && File.Exists(stale),
            "library deletion accepted an internal finalizer file");
        Console.WriteLine("PASS production library: queued destinations, 42% progress, hidden video/mux files, action guards and completion identity");
    }
}
