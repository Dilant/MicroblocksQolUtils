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
            long pixelCallbacks = 0, audioCallbacks = 0, visibleFrames = 0, skippedPresentations = 0;
            long sourceDrops = CaptureSource.DroppedAudioChunks;
            long sourceFrameDrops = CaptureSource.DroppedFrames;
            TimeSpan gcPause = GC.GetTotalPauseDuration();
            int gc2 = GC.CollectionCount(2);
            ulong previousSequence = 0;
            using CaptureSubscription observer = CaptureSource.SubscribeBorrowed(frame => {
                if (frame.Sequence <= previousSequence || frame.Pixels.Length != frame.Width * frame.Height * 4)
                    throw new InvalidDataException("Invalid pixel metadata/order");
                if (previousSequence != 0) skippedPresentations += (long)(frame.Sequence - previousSequence - 1);
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
            using CaptureSubscription slow = CaptureSource.SubscribeBorrowed(_ => Thread.Sleep(200));
            NativeCaptureSession capture = NativeCaptureBridge.StartRecording(30, path, "auto", 2_000);
            active = capture;
            await Task.Delay(1_000, token).ConfigureAwait(false);
            second = NativeCaptureBridge.StartRecording(60, path + ".second.mkv", "auto", 2_000);
            int seconds = int.TryParse(Environment.GetEnvironmentVariable("MICROBLOCKS_QOL_CAPTURE_SMOKE_SECONDS"), out int duration)
                ? Math.Clamp(duration, 2, 120) : 2;
            List<string> samples = [];
            for (int i = 0; i < seconds; i++) {
                await Task.Delay(1_000, token).ConfigureAwait(false);
                samples.Add($"t={i+2}s first={capture.Statistics} second={second.Statistics} gen2={GC.CollectionCount(2)-gc2} gcPauseMs={(GC.GetTotalPauseDuration()-gcPause).TotalMilliseconds:F1}");
                File.WriteAllLines(path + ".progress", samples);
            }
            capture.Stop();
            CaptureStatistics statistics = capture.Statistics;
            samples.Add($"firstDelivery={capture.DeliveryStatistics}");
            Interlocked.Exchange(ref active, null)?.Dispose();
            ulong before = second.Statistics.FramesCaptured;
            await Task.Delay(1_000, token).ConfigureAwait(false);
            if (second.Statistics.FramesCaptured <= before) throw new Exception("Unsubscribing first recorder stopped second recorder");
            second.Stop();
            samples.Add($"secondFinal={second.Statistics} secondDelivery={second.DeliveryStatistics} sourceAudioDrops={CaptureSource.DroppedAudioChunks-sourceDrops} observerAudioDrops={observer.DroppedAudioChunks} observerFrameDrops={observer.DroppedFrames}");
            if (Environment.GetEnvironmentVariable("MICROBLOCKS_QOL_CAPTURE_SMOKE_REQUIRE_LOSSLESS") == "1") {
                static bool Lost(CaptureDeliveryStatistics s) => s.DroppedFrames != 0 || s.DroppedAudioChunks != 0
                    || s.DroppedMusicEvents != 0 || s.CallbackErrors != 0;
                if (statistics.FramesDropped != 0 || statistics.AudioChunksDropped != 0 || Lost(capture.DeliveryStatistics)
                    || second.Statistics.FramesDropped != 0 || second.Statistics.AudioChunksDropped != 0 || Lost(second.DeliveryStatistics)
                    || CaptureSource.DroppedAudioChunks != sourceDrops || CaptureSource.DroppedFrames != sourceFrameDrops
                    || observer.DroppedAudioChunks != 0 || observer.DroppedFrames != 0
                    || statistics.FramesCaptured < statistics.MediaTimeSeconds * 30 * 0.98
                    || second.Statistics.FramesCaptured < second.Statistics.MediaTimeSeconds * 60 * 0.98)
                    throw new Exception("Lossless throughput regression failed:\n" + string.Join("\n", samples));
            }
            Interlocked.Exchange(ref second, null)?.Dispose();
            observer.Dispose(); await observer.Completion.ConfigureAwait(false);
            slow.Dispose(); await slow.Completion.ConfigureAwait(false);
            if (pixelCallbacks < 10 || audioCallbacks == 0 || observer.CallbackErrors != 0 || slow.DroppedFrames == 0)
                throw new Exception($"Callback isolation failed: pixels={pixelCallbacks} audio={audioCallbacks} errors={observer.CallbackErrors} slowDrops={slow.DroppedFrames}");
            if (!File.Exists(path) || new FileInfo(path).Length < 1_000 || statistics.FramesCaptured < 10)
                throw new Exception($"No captured video; {statistics}; source={CaptureSource.VideoError}; native={NativeCaptureBridge.LastError()}");
            if (visibleFrames < 10) throw new Exception($"Captured only {visibleFrames} visibly varied frames; a solid-color game window is not a successful visual smoke test.");
            if (!File.Exists(path + ".sfxevents") || !File.ReadLines(path + ".sfxevents").Last().Contains("\"complete\":true"))
                throw new Exception("Invalid SFX event journal");
            if (!File.Exists(path + ".music.jsonl") || !File.ReadLines(path + ".music.jsonl").Last().Contains("\"complete\":true"))
                throw new Exception("Invalid music event journal");
            string finalized = path + ".final.mp4";
            await NativeCaptureBridge.FinalizeRecordingAsync(
                [new RecordingClip(path, 0, Math.Max(0.1, statistics.MediaTimeSeconds), "", 0)],
                finalized, "auto", 2_000, 30, false, false, "").ConfigureAwait(false);
            if (new FileInfo(finalized).Length < 1_000) throw new Exception("Finalization failed");
            File.WriteAllText(path + ".passed", $"pixels={pixelCallbacks} audioCallbacks={audioCallbacks} slowDrops={slow.DroppedFrames} skippedPresentations={skippedPresentations} sourcePoolDrops={CaptureSource.DroppedFrames-sourceFrameDrops}\n{statistics}\n" + string.Join("\n",samples));
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
