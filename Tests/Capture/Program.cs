using Celeste.Mod.MicroblocksQolUtils;
using System.Diagnostics;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static Task Timeout(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));
CaptureFrame Frame(ulong n) => new(new byte[4], 1, 1, n * 16_666_667, n);
CaptureAudio Audio(ulong n) => new(new float[2], 48000, 2, 1, "bus:/gameplay_sfx", n, n);

int removed = 0;
// Queues do NOT sample: even multiple supplied frames within one FPS bucket
// must all be delivered. The source is the sole owner of rate selection.
var sampledEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var sampledRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
List<ulong> sampledTimes = [];
using (var sampled = new CaptureSubscription(frame => {
    sampledTimes.Add(frame.TimestampNanos);
    if(sampledTimes.Count==1) { sampledEntered.SetResult(); sampledRelease.Task.GetAwaiter().GetResult(); }
}, null, _=>{}, maxFrameRate:60)) {
    sampled.Offer(Frame(0)); await Timeout(sampledEntered.Task);
    for(ulong i=1;i<=3;i++) sampled.Offer(new CaptureFrame(new byte[4],1,1,i,i));
    Check(sampled.DroppedFrames==0,"selected frames overflowed recording callbacks");
    sampled.Complete(); sampledRelease.SetResult(); await Timeout(sampled.Completion);
}
Check(sampledTimes.Count==4 && sampledTimes.SequenceEqual(new ulong[]{0,1,2,3}),
    "delivery queue resampled source frames or changed timestamps");
using (var reusedRoute = new CaptureSubscription(_=>{},null,_=>{},maxFrameRate:30) { SourceMask=4 }) {
    Check(!reusedRoute.ReceivesSourceFrame(4,6),"unconfigured route received an in-flight old frame");
    reusedRoute.ActivateSourceRoute(7);
    Check(!reusedRoute.ReceivesSourceFrame(4,6),"reused source slot received its old owner's pending GPU frame");
    Check(!reusedRoute.ReceivesSourceFrame(2,7),"frame was routed to an unselected subscriber");
    Check(reusedRoute.ReceivesSourceFrame(4,7),"correct source selection was rejected");
    reusedRoute.ActivateSourceRoute(8);
    Check(reusedRoute.ReceivesSourceFrame(4,7),"unrelated route change invalidated an existing route's pending frames");
}
// The source already selected global 60-Hz buckets. A late subscriber must not
// drop them on its own first-frame-relative grid, even at fractional refresh.
foreach (ulong renderFps in new ulong[] { 90, 120, 144, 165, 180, 240 }) {
    var accepted = System.Threading.Channels.Channel.CreateUnbounded<ulong>();
    using var sub = new CaptureSubscription(f => accepted.Writer.TryWrite(f.TimestampNanos), null, _ => {}, maxFrameRate:60);
    UInt128? previous = null;
    int selected = 0;
    for (ulong i=0; i<renderFps*2; i++) {
        ulong time = 1_700_000_000_001_000_000 + i*1_000_000_000/renderFps;
        UInt128 tick = ((UInt128)time * 60 + 500_000_000) / 1_000_000_000;
        if (previous == tick) continue;
        previous = tick;
        if (++selected <= 7) continue;
        sub.Offer(new CaptureFrame(new byte[4],1,1,time,i));
        ulong received = await accepted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Check(received==time,"late subscriber changed a source timestamp");
    }
    sub.Complete(); await Timeout(sub.Completion);
    Check(sub.DroppedFrames==0,"source-paced recorder lost frames at delivery");
}
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int calls = 0;
using var slow = new CaptureSubscription(_ => {
    Interlocked.Increment(ref calls); entered.TrySetResult(); release.Task.GetAwaiter().GetResult();
}, _ => {}, _ => Interlocked.Increment(ref removed));
slow.Offer(Frame(1)); await Timeout(entered.Task);
var watch = Stopwatch.StartNew();
for (ulong i = 2; i < 400; i++) { slow.Offer(Frame(i)); slow.Offer(Audio(i)); }
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
var fairEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var fairRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
List<string> order = [];
using var fair = new CaptureSubscription(frame => {
    order.Add("video");
    if (frame.Sequence == 1) { fairEntered.SetResult(); fairRelease.Task.GetAwaiter().GetResult(); }
}, _ => order.Add("audio"), _ => {}, music: _ => order.Add("music"));
fair.Offer(Frame(1)); await Timeout(fairEntered.Task);
fair.Offer(Frame(2)); fair.Offer(Frame(3)); fair.Offer(Audio(1)); fair.Offer(Music(1));
fair.Complete(); fairRelease.SetResult(); await Timeout(fair.Completion);
Check(order.Take(3).SequenceEqual(new[]{"video","music","audio"}), "video starved music/audio");

var ring = new FmodPcmQueue();
var framePool = new CaptureFramePool();
var held = Enumerable.Range(0,32).Select(_=>framePool.Rent(4)!).ToArray();
Check(framePool.Rent(4) is null,"borrowed pool was not bounded");
held[0].Buffer[0]=42;
var borrowedFrame = new CaptureFrame(held[0].Buffer,1,1,1,1) { Lease=held[0] };
var snapshot = borrowedFrame.Snapshot();
var borrowedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var borrowedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var borrowedSub = new CaptureSubscription(frame=>{
    borrowedEntered.TrySetResult(); borrowedRelease.Task.GetAwaiter().GetResult();
    Check(frame.Pixels.Span[0]==42,"in-flight borrowed pixels were recycled");
},null,_=>{},borrowsPixels:true);
borrowedSub.Offer(borrowedFrame); held[0].Release();
await Timeout(borrowedEntered.Task);
Check(framePool.Rent(4) is null,"callback did not retain its buffer lease");
borrowedSub.Dispose();
Check(framePool.Rent(4) is null,"Dispose released an in-flight lease early");
borrowedRelease.SetResult(); await Timeout(borrowedSub.Completion);
var recycled=framePool.Rent(4)!; recycled.Buffer[0]=99;
Check(snapshot.Pixels.Span[0]==42,"owned snapshot changed after pool reuse");
recycled.Release(); foreach(var lease in held.Skip(1)) lease.Release();
// Cancelled pending leases must also return to the pool.
var cancelLease=framePool.Rent(4)!;
var cancelSub=new CaptureSubscription(_=>{},null,_=>{},borrowsPixels:true);
cancelSub.Offer(new CaptureFrame(cancelLease.Buffer,1,1,2,2){Lease=cancelLease});
cancelLease.Release(); cancelSub.Dispose(); await Timeout(cancelSub.Completion);

for (ulong i = 0; i < FmodPcmQueue.Capacity; i++)
    Check(ring.TryWrite(new[]{(float)i,-(float)i},48000,2,i,i),"ring filled too early");
Check(!ring.TryWrite(new float[2],48000,2,0,0),"ring wasn't bounded");
for (ulong i = 0; i < FmodPcmQueue.Capacity; i++) {
    Check(ring.TryRead(3,out var chunk) && chunk!.DspClock==i && chunk.Samples.Span[0]==i,"PCM ring ordering/ownership");
}
// Exercise publication/slot reuse under real concurrent producer/consumer traffic.
const int total = 20_000;
var producer = Task.Run(() => {
    float[] samples = new float[32];
    for (int i=0;i<total;i++) {
        Array.Fill(samples,(float)i);
        while (!ring.TryWrite(samples,48000,2,(ulong)i,(ulong)i)) Thread.Yield();
    }
});
var consumer = Task.Run(() => {
    for (int i=0;i<total;i++) {
        CaptureAudio? chunk;
        while (!ring.TryRead(3,out chunk)) Thread.Yield();
        Check(chunk!.DspClock==(ulong)i && chunk.Samples.Span.ToArray().All(x=>x==i),"PCM publication corrupted a slot");
    }
});
await Timeout(Task.WhenAll(producer,consumer));
Console.WriteLine("PASS: queues, fair delivery, bounded PCM rings/concurrency, callback isolation, cancellation/drain, clocks, music overflow and per-sink journals");

namespace Celeste.Mod.MicroblocksQolUtils {
    internal enum LogLevel { Warn }
    internal static class Logger { internal static void Log(LogLevel level, string tag, string text) {} }
}
