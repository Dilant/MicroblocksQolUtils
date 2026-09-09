namespace Celeste.Mod.MicroblocksQolUtils;

// Opt-in only. Starts after live Engine updates, not during parallel mod loading.
internal static class NativeCaptureSmoke {
    private static string? output;
    private static int updates;
    private static CancellationTokenSource? cancellation;
    private static NativeCaptureSession? active, second;
    public static void Load() {
        output = Environment.GetEnvironmentVariable("MICROBLOCKS_QOL_CAPTURE_SMOKE_OUTPUT");
        updates = 0;
    }
    public static void Update() {
        if (string.IsNullOrWhiteSpace(output) || cancellation is not null
            || Monocle.Engine.Scene is not (Overworld or Level) || ++updates < 180) return;
        cancellation = new();
        _ = RunAsync(Path.GetFullPath(output), cancellation.Token);
    }
    public static void Unload() {
        cancellation?.Cancel();
        Interlocked.Exchange(ref active, null)?.Dispose();
        Interlocked.Exchange(ref second, null)?.Dispose();
        output = null;
    }
    private static async Task RunAsync(string path, CancellationToken token) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            long pixelCallbacks = 0, audioCallbacks = 0, visibleFrames = 0;
            ulong previousSequence = 0;
            using CaptureSubscription observer = CaptureSource.Subscribe(frame => {
                if (frame.Sequence <= previousSequence || frame.Pixels.Length != frame.Width * frame.Height * 4)
                    throw new InvalidDataException("Invalid pixel metadata/order");
                previousSequence = frame.Sequence;
                var pixels = frame.Pixels.Span;
                bool varied = false;
                for (int i = 0; i < pixels.Length; i += Math.Max(4, (pixels.Length / 1024) & ~3)) {
                    if (Math.Abs(pixels[i] - pixels[0]) + Math.Abs(pixels[i + 1] - pixels[1])
                        + Math.Abs(pixels[i + 2] - pixels[2]) > 24) { varied = true; break; }
                }
                if (varied) Interlocked.Increment(ref visibleFrames);
                Interlocked.Increment(ref pixelCallbacks);
            }, chunk => {
                if (chunk.SampleRate <= 0 || chunk.Samples.Length % chunk.Channels != 0 || chunk.TimestampNanos == 0)
                    throw new InvalidDataException("Invalid audio metadata");
                Interlocked.Increment(ref audioCallbacks);
            });
            using CaptureSubscription slow = CaptureSource.Subscribe(_ => Thread.Sleep(200));
            NativeCaptureSession capture = NativeCaptureBridge.StartRecording(30, path, "auto", 2_000);
            active = capture;
            await Task.Delay(1_000, token).ConfigureAwait(false);
            second = NativeCaptureBridge.StartRecording(60, path + ".second.mkv", "auto", 2_000);
            await Task.Delay(2_000, token).ConfigureAwait(false);
            CaptureStatistics statistics = capture.Statistics;
            Interlocked.Exchange(ref active, null)?.Dispose();
            ulong before = second.Statistics.FramesCaptured;
            await Task.Delay(1_000, token).ConfigureAwait(false);
            if (second.Statistics.FramesCaptured <= before) throw new Exception("Unsubscribing first recorder stopped second recorder");
            Interlocked.Exchange(ref second, null)?.Dispose();
            observer.Dispose(); await observer.Completion.ConfigureAwait(false);
            slow.Dispose(); await slow.Completion.ConfigureAwait(false);
            if (pixelCallbacks < 10 || audioCallbacks == 0 || observer.CallbackErrors != 0 || slow.DroppedFrames == 0)
                throw new Exception($"Callback isolation failed: pixels={pixelCallbacks} audio={audioCallbacks} errors={observer.CallbackErrors} slowDrops={slow.DroppedFrames}");
            if (!File.Exists(path) || new FileInfo(path).Length < 1_000 || statistics.FramesCaptured < 10)
                throw new Exception($"No captured video; {statistics}; source={CaptureSource.VideoError}; native={NativeCaptureBridge.LastError()}");
            if (visibleFrames < 10) throw new Exception($"Captured only {visibleFrames} visibly varied frames; a solid-color game window is not a successful visual smoke test.");
            byte[] audio = File.ReadAllBytes(path + ".sfxchunks");
            if (audio.Length < 8 || !audio.AsSpan(0,8).SequenceEqual("MQOLAUD1"u8)) throw new Exception("Invalid PCM sidecar");
            string finalized = path + ".final.mp4";
            await NativeCaptureBridge.FinalizeRecordingAsync(
                [new RecordingClip(path, 0, Math.Max(0.1, statistics.MediaTimeSeconds), "", 0)],
                finalized, "auto", 2_000, 30, false, false, "").ConfigureAwait(false);
            if (new FileInfo(finalized).Length < 1_000) throw new Exception("Finalization failed");
            File.WriteAllText(path + ".passed", $"pixels={pixelCallbacks} audioCallbacks={audioCallbacks} slowDrops={slow.DroppedFrames}\n{statistics}\n");
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils/Recorder", $"QOL_CAPTURE_SMOKE_PASSED {path}");
        } catch (OperationCanceledException) { }
        catch (Exception exception) {
            File.WriteAllText(path + ".failed", exception.ToString());
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/CaptureSmoke");
        } finally {
            Interlocked.Exchange(ref active, null)?.Dispose();
            Interlocked.Exchange(ref second, null)?.Dispose();
        }
    }
}
