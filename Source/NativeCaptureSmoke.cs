namespace Celeste.Mod.MicroblocksQolUtils;

internal static class NativeCaptureSmoke {
    private const string OutputVariable = "MICROBLOCKS_QOL_CAPTURE_SMOKE_OUTPUT";
    private static CancellationTokenSource? cancellation;
    private static NativeCaptureSession? active;
    private static FmodSfxTap? activeTap;

    public static void Load() {
        string? configured = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(configured)) return;
        string output = Path.GetFullPath(configured);
        cancellation = new CancellationTokenSource();
        _ = RunAsync(output, cancellation.Token);
    }

    public static void Unload() {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        Interlocked.Exchange(ref activeTap, null)?.Dispose();
        Interlocked.Exchange(ref active, null)?.Dispose();
    }

    private static async Task RunAsync(string output, CancellationToken token) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await Task.Delay(3_000, token).ConfigureAwait(false);
            NativeCaptureSession capture = CaptureBackend.Current.StartRecording(
                30,
                output,
                "libopenh264",
                2_000
            );
            Interlocked.Exchange(ref active, capture)?.Dispose();
            FmodSfxTap tap = FmodSfxTap.Attach(capture, includeUiSfx: true)
                ?? throw new InvalidOperationException("FMOD SFX tap did not attach to any bus");
            Interlocked.Exchange(ref activeTap, tap)?.Dispose();
            Audio.Play("event:/ui/main/button_select");
            await Task.Delay(2_000, token).ConfigureAwait(false);
            CaptureStatistics statistics = capture.Statistics;
            Interlocked.Exchange(ref activeTap, null)?.Dispose();
            Interlocked.Exchange(ref active, null)?.Dispose();
            FileInfo file = new(output);
            long length = file.Exists ? file.Length : 0;
            if (length < 1_000) {
                throw new InvalidDataException(
                    $"native capture output is missing or too small: {length} bytes; "
                    + $"running={statistics.Running} captured={statistics.FramesCaptured} "
                    + $"consumed={statistics.FramesConsumed} dropped={statistics.FramesDropped}; "
                    + $"nativeError={NativeCaptureBridge.LastError()}"
                );
            }
            string sidecar = output + ".sfxchunks";
            byte[] audio = File.Exists(sidecar) ? File.ReadAllBytes(sidecar) : [];
            if (statistics.AudioFramesCaptured == 0
                || audio.Length <= 8
                || !audio.AsSpan(0, 8).SequenceEqual("MQOLAUD1"u8)) {
                throw new InvalidDataException(
                    $"FMOD SFX sidecar is invalid: bytes={audio.Length} "
                    + $"frames={statistics.AudioFramesCaptured} dropped={statistics.AudioChunksDropped}"
                );
            }
            string finalized = output + ".final.mp4";
            await CaptureBackend.Current.FinalizeRecordingAsync(
                [new RecordingClip(output, 0, Math.Max(0.1, statistics.MediaTimeSeconds), "", 0)],
                finalized,
                "libopenh264",
                2_000,
                30,
                false,
                false,
                ""
            ).ConfigureAwait(false);
            if (!File.Exists(finalized) || new FileInfo(finalized).Length < 1_000)
                throw new InvalidDataException("native A/V finalizer did not produce an MP4");
            Logger.Log(
                LogLevel.Info,
                "MicroblocksQolUtils/Recorder",
                $"QOL_CAPTURE_SMOKE_PASSED {output} finalized={finalized}"
            );
        } catch (OperationCanceledException) {
        } catch (Exception exception) {
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/CaptureSmoke");
        } finally {
            Interlocked.Exchange(ref activeTap, null)?.Dispose();
            Interlocked.Exchange(ref active, null)?.Dispose();
        }
    }
}
