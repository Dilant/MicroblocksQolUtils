using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Celeste.Mod.MicroblocksQolUtils;

internal static class FrameRoutingTests {
    internal static void Run(nint window, Action<int> draw, string output, string encoder) {
        var times30 = new ConcurrentQueue<ulong>();
        var times60 = new ConcurrentQueue<ulong>();
        using var route30 = CaptureSource.SubscribeRecording(30, f => times30.Enqueue(f.TimestampNanos), null, null);
        using var route60 = CaptureSource.SubscribeRecording(60, f => times60.Enqueue(f.TimestampNanos), null, null);
        using var unlimited = CaptureSource.Subscribe(_ => { });
        using var first = NativeCaptureBridge.StartRecording(60, Path.Combine(output,"route60.mkv"), encoder, 1000);
        using var lower = NativeCaptureBridge.StartRecording(30, Path.Combine(output,"route30.mkv"), encoder, 1000);
        NativeCaptureSession? late = null;
        var watch = Stopwatch.StartNew();
        for (int i=0; i<480; i++) {
            if (i==24) late = NativeCaptureBridge.StartRecording(60, Path.Combine(output,"late60.mkv"), encoder, 1000);
            if (i==240) unlimited.Dispose(); // Routes change with GPU work still pending.
            while (watch.Elapsed.TotalSeconds < i/120d) {
                if (i/120d-watch.Elapsed.TotalSeconds > .002) Thread.Sleep(1);
                else Thread.SpinWait(20);
            }
            Sdl.Pump(); CaptureSource.Update(); draw(i); Sdl.Swap(window);
        }
        first.Stop(); lower.Stop(); late!.Stop();
        route30.Complete(); route60.Complete();
        Task.WaitAll(route30.Completion,route60.Completion);
        var reports = new List<object>();
        foreach (var (capture, rate, timestamps) in new[] {
            (first,60,times60.ToArray()), (lower,30,times30.ToArray()), (late,60,times60.ToArray())
        }) {
            var stats = capture.Statistics;
            ulong start = stats.LastFrameUnixNanos-stats.MediaTimeNanos;
            int selected = timestamps.Count(t=>t>=start && t<=stats.LastFrameUnixNanos);
            if ((ulong)selected != stats.FramesCaptured || stats.FramesConsumed != stats.FramesCaptured
                || stats.FramesDropped != 0 || capture.DeliveryStatistics.DroppedFrames != 0)
                throw new Exception($"Source-selected frames were lost downstream: {selected}, {stats}, {capture.DeliveryStatistics}");
            double fps = stats.FramesCaptured / stats.MediaTimeSeconds;
            if (fps < rate*.97) throw new Exception($"Unexpected source cadence: {fps:F2}/{rate}");
            reports.Add(new { rate, selected, fps, stats, delivery=capture.DeliveryStatistics });
            Console.WriteLine($"PASS source-only {rate}fps routing: selected/delivered/encoded={selected}, actual={fps:F2}fps");
        }
        late.Dispose();
        File.WriteAllText(Path.Combine(output,"routes.json"), JsonSerializer.Serialize(reports));
    }
}
