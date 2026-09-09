using Celeste.Mod.MicroblocksQolUtils;
using System.Diagnostics;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static Task Timeout(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));
CaptureFrame Frame(ulong n) => new(new byte[4], 1, 1, n * 16_666_667, n);
CaptureAudio Audio(ulong n) => new(new float[2], 48000, 2, 1, "bus:/gameplay_sfx", n, n);

int removed = 0;
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int calls = 0;
using var slow = new CaptureSubscription(_ => {
    Interlocked.Increment(ref calls); entered.TrySetResult(); release.Task.GetAwaiter().GetResult();
}, _ => {}, _ => Interlocked.Increment(ref removed));
slow.Offer(Frame(1)); await Timeout(entered.Task);
var watch = Stopwatch.StartNew();
for (ulong i = 2; i < 100; i++) { slow.Offer(Frame(i)); slow.Offer(Audio(i)); }
Check(watch.ElapsedMilliseconds < 500, "slow callback blocked producer");
Check(slow.DroppedFrames > 0 && slow.DroppedAudioChunks > 0, "queues were not bounded");
slow.Dispose(); slow.Dispose();
Check(!slow.Completion.IsCompleted, "Completion must wait for the in-flight callback");
release.SetResult(); await Timeout(slow.Completion);
slow.Offer(Frame(200));
Check(calls == 1 && removed == 1, "dispose did not cancel pending callbacks exactly once");

var healthyDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var healthy = new CaptureSubscription(_ => healthyDone.TrySetResult(), null, _ => {});
using var broken = new CaptureSubscription(_ => throw new Exception("expected callback failure"), null, _ => {});
broken.Offer(Frame(1)); healthy.Offer(Frame(1)); await Timeout(healthyDone.Task);
for (int i=0; i<100 && broken.CallbackErrors==0; i++) await Task.Delay(5);
Check(broken.CallbackErrors == 1, "callback exception wasn't isolated");
broken.Dispose(); await Timeout(broken.Completion);
healthy.Dispose(); await Timeout(healthy.Completion);

CaptureSubscription? self = null;
self = new CaptureSubscription(_ => self!.Dispose(), null, _ => {});
self.Offer(Frame(1)); await Timeout(self.Completion);

int staleCalls = 0;
using var fresh = new CaptureSubscription(_ => Interlocked.Increment(ref staleCalls), null, _ => {}, 100);
fresh.Offer(new CaptureFrame(new byte[4],1,1,99,1));
await Task.Delay(20); fresh.Dispose(); await Timeout(fresh.Completion);
Check(staleCalls == 0,"new subscription received an old in-flight frame");

var clock = new FmodCaptureClock();
Check(clock.Timestamp(48000,48000,1_000_000_000)==1_000_000_000, "FMOD anchor");
Check(clock.Timestamp(48480,48000,1_019_000_000)==1_010_000_000, "mixer jitter changed sample clock");
Check(clock.Timestamp(49440,48000,1_031_000_000)==1_030_000_000, "dropped chunk lost its duration");
Check(clock.Timestamp(0,48000,2_000_000_000)==2_000_000_000, "DSP reset not reanchored");
Check(clock.Timestamp(480,48000,5_000_000_000)==5_000_000_000, "suspend not reanchored");
Check(clock.Timestamp(441,44100,5_020_000_000)==5_020_000_000, "sample rate change not reanchored");
Console.WriteLine("PASS: bounded queues, slow/throwing isolation, unregister/drain, self-unsubscribe, FMOD clocks");

namespace Celeste.Mod.MicroblocksQolUtils {
    internal enum LogLevel { Warn }
    internal static class Logger { internal static void Log(LogLevel level, string tag, string text) {} }
}
