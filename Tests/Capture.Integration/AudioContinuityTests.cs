using System.Collections.Concurrent;
using System.Text.Json;
using Celeste.Mod.MicroblocksQolUtils;

internal static class AudioContinuityTests {
    internal static void Run(FMOD.Studio.System studio, FMOD.System lowLevel, string output) {
        static void Ok(FMOD.RESULT result) { if (result != FMOD.RESULT.OK) throw new Exception(result.ToString()); }
        var chunks = new ConcurrentQueue<(ulong Timestamp, ulong Clock, ulong Observed, int Frames, double Rms)>();
        using var sub = CaptureSource.Subscribe(fmod: chunk => {
            if (chunk.BusId != 1) return;
            double energy = 0;
            foreach (float value in chunk.Samples.Span) energy += value * value;
            chunks.Enqueue((chunk.TimestampNanos, chunk.DspClock, SdlFrameSource.ClockNanos(),
                chunk.Samples.Length / chunk.Channels, Math.Sqrt(energy / chunk.Samples.Length)));
        });
        CaptureSource.Update();
        Ok(studio.getBus("bus:/gameplay_sfx", out var bus));
        Ok(bus.getChannelGroup(out var group));
        Ok(lowLevel.createDSPByType(FMOD.DSP_TYPE.OSCILLATOR, out var tone));
        Ok(tone.setParameterInt(0, 0)); // sine, continuous and deterministic
        Ok(tone.setParameterFloat(1, 440));
        Ok(lowLevel.playDSP(tone, group, false, out var channel));
        Ok(channel.setVolume(0.15f));
        var pauses = new List<object>();
        void Wait(int ms) {
            long end = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < end) { Ok(studio.update()); Thread.Sleep(5); }
        }
        Wait(250);
        foreach (int ms in new[] { 120, 180, 450 }) {
            Ok(group.getDSPClock(out var localBefore, out var parentBefore));
            Ok(bus.setPaused(true)); Ok(studio.flushCommands()); Wait(ms);
            Ok(group.getDSPClock(out var localAfter, out var parentAfter));
            ulong resumed = SdlFrameSource.ClockNanos();
            Ok(bus.setPaused(false)); Ok(studio.flushCommands()); Wait(250);
            pauses.Add(new { ms, resumed, localBefore, localAfter, parentBefore, parentAfter });
        }
        Ok(channel.stop()); Ok(tone.release());
        sub.Complete(); sub.Completion.GetAwaiter().GetResult(); CaptureSource.Update();
        var data = chunks.ToArray();
        File.WriteAllText(Path.Combine(output, "audio-clock.json"), JsonSerializer.Serialize(new {
            pauses, chunks = data.Select(c => new { c.Timestamp, c.Clock, c.Observed, c.Frames, c.Rms })
        }));
        double drift = data.Max(c => ((double)c.Observed - c.Timestamp) / 1e6)
            - data.Take(5).Average(c => ((double)c.Observed - c.Timestamp) / 1e6);
        Console.WriteLine($"Audio pause clock drift: {drift:F1} ms; {data.Length} PCM blocks");
        if (drift > 70) throw new Exception("Pausing a gameplay bus shifted captured PCM away from the video clock");
    }
}
