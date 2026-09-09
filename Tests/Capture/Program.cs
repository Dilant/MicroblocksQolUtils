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
CaptureMusic Music(ulong n, string kind = "switch") => new(n, n, kind, "main", "music/a", 1, (int)n, false, "PLAYING", new Dictionary<string,float>());
var musicEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var musicRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int musicCalls = 0;
using var musicSub = new CaptureSubscription(null, null, _=>{}, music: _=>{
    Interlocked.Increment(ref musicCalls); musicEntered.TrySetResult(); musicRelease.Task.GetAwaiter().GetResult();
});
musicSub.Offer(Music(1)); await Timeout(musicEntered.Task);
for(ulong n=2;n<300;n++) musicSub.Offer(Music(n));
Check(musicSub.DroppedMusicEvents>0,"music overflow was silent");
musicSub.Complete(); musicRelease.SetResult(); await Timeout(musicSub.Completion);
Check(musicCalls==257,"graceful completion discarded pending music events");
musicSub.Offer(Music(400)); Check(musicCalls==257,"completed subscription accepted new music");
string journalDirectory = Path.Combine(Path.GetTempPath(),"mqol-journal-"+Guid.NewGuid());
Directory.CreateDirectory(journalDirectory);
string journalPath=Path.Combine(journalDirectory,"first.music.jsonl");
using(var journal=new MusicJournal(journalPath)) {
    journal.Accept(Music(10));journal.Accept(Music(20));journal.Start(30);journal.Accept(Music(50,"seek"));journal.Finish(true);
}
var journalLines=File.ReadAllLines(journalPath);
Check(journalLines.Length==4 && journalLines[1].Contains("\"sequence\":20")
    && journalLines[1].Contains("\"time_nanos\":0") && journalLines[2].Contains("\"time_nanos\":20"),"per-sink music origin/bootstrap");
Check(journalLines[3].Contains("\"complete\":true"),"music journal wasn't completed");
using(var invalid=new MusicJournal(Path.Combine(journalDirectory,"invalid.music.jsonl"))) {
    bool refused=false;try {invalid.Finish(false);}catch(InvalidDataException){refused=true;}
    Check(refused,"incomplete journal wasn't rejected");
}
Console.WriteLine("PASS: queues, callback isolation, cancellation/drain, clocks, music overflow and per-sink journals");

namespace Celeste.Mod.MicroblocksQolUtils {
    internal enum LogLevel { Warn }
    internal static class Logger { internal static void Log(LogLevel level, string tag, string text) {} }
}
