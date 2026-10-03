using System.Reflection;
using Celeste.Mod.MicroblocksQolUtils;

internal static class AudioReplayTests {
    private static void Check(bool condition, string message) {
        if (!condition) throw new Exception(message);
    }
    private static void Ok(FMOD.RESULT result) => Check(result == FMOD.RESULT.OK, result.ToString());

    internal static void Run(FMOD.Studio.System studio, string output) {
        string journalPath = Path.Combine(output, "music-start.music.jsonl");
        using (var journal = new MusicJournal(journalPath)) {
            using var subscription = CaptureSource.Subscribe(music: journal.Accept);
            journal.Start(SdlFrameSource.ClockNanos());
            Ok(studio.getEvent("event:/music/lvl1/main", out var description));
            Ok(description.createInstance(out var song));
            // Match Audio.SetMusic: start is issued before the new instance is assigned.
            Ok(song.start());
            typeof(Celeste.Audio).GetField("currentMusicEvent", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, song);
            CaptureSource.Update();
            for (int i = 0; i < 40; i++) {
                Ok(studio.update()); Thread.Sleep(10); CaptureSource.Update();
            }
            subscription.Complete(); subscription.Completion.GetAwaiter().GetResult();
            Check(CaptureSource.MusicError is null, "Music capture failed: " + CaptureSource.MusicError);
            journal.Finish(true);
            Ok(song.stop(FMOD.Studio.STOP_MODE.IMMEDIATE)); Ok(song.release());
        }
        RecordingClip[] clips = [new("run.mkv", 0, 8, "", 0)];
        var commands = MusicEventReplayRenderer.Plan(journalPath, clips, true);
        Check(commands.Any(c => c.Operation == "start"), "A newly started song produced no replay start command");
        string sidecar = Path.Combine(output, "music-start.bgmchunks");
        Check(MusicEventReplayRenderer.RenderToSidecar(journalPath, sidecar, clips, true), "Music render failed");
        var samples = ReadSamples(sidecar);
        for (int second = 1; second < 7; second++) {
            double rms = Rms(samples, second, 1);
            Console.WriteLine($"BGM second {second}: RMS {rms:F6}");
            Check(rms > 0.0001, $"BGM second {second} is silent");
        }
        // Recorded STOPPED -> PLAYING races must not leave an entire take silent.
        string delayedPath = Path.Combine(output, "delayed.music.jsonl");
        using (var journal = new MusicJournal(delayedPath)) {
            journal.Start(0);
            journal.Accept(new(0, 1, "switch", "main", "event:/music/lvl1/main", 1, 0, false, "STOPPED", new Dictionary<string, float>()));
            journal.Accept(new(10_000_000, 2, "playback", "main", "event:/music/lvl1/main", 1, 0, false, "PLAYING", new Dictionary<string, float>()));
            journal.Finish(true);
        }
        Check(MusicEventReplayRenderer.Plan(delayedPath, clips, true).Count(c => c.Operation == "start") == 1,
            "Delayed playback was never started");
        using (var journal = new MusicJournal(delayedPath)) {
            journal.Start(0);
            journal.Accept(new(0, 1, "start", "main", "event:/music/lvl1/main", 1, 0, false, "STOPPED", new Dictionary<string, float>()));
            journal.Accept(new(10_000_000, 2, "stop-IMMEDIATE", "main", "event:/music/lvl1/main", 1, 0, false, "PLAYING", new Dictionary<string, float>()));
            journal.Accept(new(20_000_000, 3, "stop-IMMEDIATE", "main", "event:/music/lvl1/main", 1, 0, false, "PLAYING", new Dictionary<string, float>()));
            journal.Finish(true);
        }
        var delayedCommands = MusicEventReplayRenderer.Plan(delayedPath, clips, true);
        Check(delayedCommands.Count(c => c.Operation == "start") == 1 && delayedCommands.Count(c => c.Operation == "stop") == 1,
            "Asynchronous start/stop commands were lost or restarted stopped music");
        Ok(studio.getEvent("event:/music/lvl1/main", out var vanilla));
        Ok(vanilla.getID(out Guid vanillaId));
        Check(AudioEventReplayRenderer.RenderCommandsToSidecar(
            [new(0, 1, "guid://" + vanillaId, "start", Bus: "music")], sidecar, 3), "GUID music render failed");
        Check(Rms(ReadSamples(sidecar), 1, 1) > 0.0001, "GUID music is silent");
        Check(!AudioEventReplayRenderer.RenderCommandsToSidecar(
            [new(0, 1, "event:/missing/replay-regression", "start", Bus: "music")], sidecar, 1),
            "Missing music event was reported as success");
        TestStreamPrime(studio, output);
        TestModBank(studio, output);
        TestSfx(studio, output);
    }

    private static void TestStreamPrime(FMOD.Studio.System studio, string output) {
        // Streamed music decodes on FMOD's wall-clock stream thread. Offline replay
        // runs far faster than real time and must not record its buffering gap.
        const string remix = "event:/music/remix/04_cliffside";
        Ok(studio.getEvent(remix, out var description));
        Ok(description.isStream(out bool stream)); Check(stream, "Test music is not streamed");
        string sidecar = Path.Combine(output, "stream-prime.bgmchunks");
        AudioInstanceState At(int timeline) => new(new Dictionary<string, float>(), 1, 1, false, timeline, null);
        // Studio schedules every new event a few mixer blocks ahead; that latency is
        // also present live. Only silence beyond a sample-loaded event's start is a gap.
        double Lead(string path, int timeline, double restartAt) {
            // IMMEDIATE stop, so the restart measures a stream opened mid-render.
            AudioCommand[] commands = [
                new(0, 1, path, "start", Bus: "music", State: At(timeline)),
                new((ulong)((restartAt - 0.5) * 1e9), 1, path, "stop", Bus: "music", Value: 1),
                new((ulong)(restartAt * 1e9), 2, path, "start", Bus: "music", State: At(timeline + 3000))];
            Check(AudioEventReplayRenderer.RenderCommandsToSidecar(commands, sidecar, restartAt + 3), "Stream render failed");
            float[] samples = ReadSamples(sidecar);
            return Math.Max(LeadingSilence(samples, 0), LeadingSilence(samples, restartAt));
        }
        double reference = Lead("event:/music/lvl1/main", 0, 3);
        Console.WriteLine($"Sample-loaded music start latency: {reference * 1000:F1}ms");
        foreach (int timeline in new[] { 40_000, 0 }) {
            for (int attempt = 0; attempt < 3; attempt++) {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                double lead = Lead(remix, timeline, 3);
                Console.WriteLine($"Stream @{timeline}ms try {attempt}: worst start silence {lead * 1000:F1}ms, " +
                    $"render {clock.ElapsedMilliseconds}ms");
                Check(lead < reference + 0.03, $"Streamed BGM started with {lead:F3}s of silence");
            }
        }
    }

    private static double LeadingSilence(float[] samples, double from) {
        for (int i = (int)(from * 48000) * 2; i < samples.Length; i++)
            if (Math.Abs(samples[i]) > 1e-4f) return i / 96000d - from;
        return samples.Length / 96000d - from;
    }

    private static void TestSfx(FMOD.Studio.System studio, string output) {
        const string oneShot = "event:/char/madeline/death";
        const string loop = "event:/char/madeline/dreamblock_travel";
        Ok(studio.getEvent(oneShot, out var shotDescription));
        Ok(shotDescription.isOneshot(out bool isShot)); Check(isShot, "Test sound isn't a one-shot");
        Ok(studio.getEvent(loop, out var loopDescription));
        Ok(loopDescription.isOneshot(out bool isLoopShot)); Check(!isLoopShot, "Test loop isn't looping");
        float[] Render(string name, AudioCommand[] commands, RecordingClip[] clips) {
            string journalPath = Path.Combine(output, name + ".sfxevents");
            using (var journal = new AudioEventJournal(journalPath)) {
                journal.Start(0);
                foreach (var command in commands) journal.Accept(command);
                journal.Finish(true);
            }
            string sidecar = Path.Combine(output, name + ".sfxchunks");
            Check(AudioEventReplayRenderer.RenderToSidecar(journalPath, sidecar, clips), name + " failed");
            // Verify the source-clock contract consumed by the native muxer, not
            // just the concatenated PCM: no gap or overlap may straddle a cut.
            using (var reader = new BinaryReader(File.OpenRead(sidecar))) {
                reader.ReadBytes(8);
                long outputFrame = 0, clipOutputStart = 0;
                double endSeconds = clips[0].DurationSeconds;
                int clipIndex = 0;
                while (reader.BaseStream.Position < reader.BaseStream.Length) {
                    long frame = (long)Math.Round(reader.ReadUInt64() * 48000d / 1_000_000_000d);
                    reader.ReadInt32(); reader.ReadUInt16(); reader.ReadUInt16();
                    uint frames = reader.ReadUInt32(), count = reader.ReadUInt32();
                    while (outputFrame >= (long)Math.Round(endSeconds * 48000)) {
                        clipOutputStart = (long)Math.Round(endSeconds * 48000);
                        endSeconds += clips[++clipIndex].DurationSeconds;
                    }
                    Check(frame == (long)Math.Round(clips[clipIndex].StartSeconds * 48000) + outputFrame - clipOutputStart,
                        name + " has incorrect source-clock sample placement");
                    Check(outputFrame + frames <= (long)Math.Round(endSeconds * 48000), name + " chunk crosses a source cut");
                    outputFrame += frames;
                    reader.BaseStream.Seek(count * 4, SeekOrigin.Current);
                }
            }
            float[] samples = ReadSamples(sidecar);
            Check(samples.Length == (int)Math.Round(clips.Sum(c => c.DurationSeconds) * 48000) * 2,
                name + " lost samples at a partial DSP block boundary");
            return samples;
        }
        RecordingClip[] uncut = [new("run.mkv", 0, 4, "", 0)];
        RecordingClip[] edited = [new("run.mkv", 0, 0.137, "", 0), new("run.mkv", 20, 3.863, "", 0)];
        AudioCommand start = new(0, 1, oneShot, "start", Bus: "sfx");
        float[] natural = Render("sfx-natural", [start], uncut);
        float[] stopped = Render("sfx-stop", [start, new(50_000_000, 1, oneShot, "stop", Value: 1)], uncut);
        float[] cut = Render("sfx-cut", [start, new(50_000_000, 1, oneShot, "release"),
            new(1_000_000_000, 2, "event:/discarded/must-not-load", "start")], edited);
        double tail = Rms(natural, .2, .5);
        Console.WriteLine($"One-shot tails: natural={tail:F6}, stopped={Rms(stopped, .2, .5):F6}, cut={Rms(cut, .2, .5):F6}");
        Check(tail > .0001 && Rms(stopped, .2, .5) > tail * .8 && Rms(cut, .2, .5) > tail * .8,
            "One-shot tail was cut by stop/release or a video edit");
        AudioCommand loopStart = new(0, 1, loop, "start", Bus: "sfx");
        float[] loopNatural = Render("loop-natural", [loopStart], uncut);
        float[] loopCut = Render("loop-cut", [loopStart], edited);
        float[] loopStopped = Render("loop-stop", [loopStart, new(137_000_000, 1, loop, "stop", Value: 1)], uncut);
        Check(Rms(loopNatural, 2, 1) > .0001, "Test loop isn't audible");
        Check(Rms(loopCut, 2, 1) < .00001 && Rms(loopStopped, 2, 1) < .00001, "Loop leaked after a cut/stop");
        RecordingClip[] split = [new("run.mkv", 0, .137, "", 0), new("run.mkv", .137, 3.863, "", 0)];
        float[] loopSplit = Render("loop-metadata", [loopStart], split);
        Check(Rms(loopSplit, 2, 1) > .0001, "Metadata-only split cut a continuous loop");
        var plan = AudioEventReplayRenderer.PlanSfxCommands(new(0, [start,
            new(137_000_000, 2, oneShot, "start"), new(20_000_000_000, 1, oneShot, "start")]), edited);
        Check(plan.Count(c => c.Operation == "start") == 2 && plan.Count(c => c.Operation == "cut") == 1,
            "Discarded boundary start leaked into SFX plan");
        Check(plan.Where(c => c.Operation == "start").Select(c => c.InstanceId).Distinct().Count() == 2,
            "A reused source instance controls the previous branch's tail");
        Console.WriteLine("PASS one-shot completion, loop stops, discarded events, metadata splits and exact sample counts");
    }

    private sealed class BankAsset(byte[] data) : Celeste.Mod.ModAsset(null!) {
        protected override void Open(out Stream stream, out bool async) {
            stream = new MemoryStream(data, false); async = false;
        }
    }

    private static void TestModBank(FMOD.Studio.System studio, string output) {
        string? zipPath = Environment.GetEnvironmentVariable("MQOL_TEST_MOD_BANK_ZIP");
        if (zipPath is null) { Console.WriteLine("SKIP optional custom-bank replay (MQOL_TEST_MOD_BANK_ZIP)"); return; }
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.First(e => e.FullName.EndsWith(".bank", StringComparison.OrdinalIgnoreCase));
        using var input = entry.Open(); using var bytes = new MemoryStream(); input.CopyTo(bytes);
        byte[] data = bytes.ToArray();
        Ok(studio.loadBankMemory(data, FMOD.Studio.LOAD_BANK_FLAGS.NORMAL, out var bank));
        var asset = new BankAsset(data) { PathVirtual = entry.FullName };
        Celeste.Audio.Banks.ModCache.Add(asset, bank);
        try {
            Ok(bank.getEventList(out var events));
            Check(events.Length > 0, "Test mod bank has no events");
            Ok(events[0].getID(out Guid id));
            // Simulate Everest's .guids alias without shipping someone else's bank.
            var aliases = (Dictionary<Guid, string>)typeof(Celeste.Audio)
                .GetField("cachedPaths", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null)!;
            string alias = "event:/music/mqol-custom-bank-test";
            bool hadAlias = aliases.TryGetValue(id, out string? oldAlias);
            aliases[id] = alias;
            try {
                string sidecar = Path.Combine(output, "custom.bgmchunks");
                Check(AudioEventReplayRenderer.RenderCommandsToSidecar(
                    [new(0, 1, alias, "start", Bus: "music")], sidecar, 8), "Custom bank alias render failed");
                double rms = Rms(ReadSamples(sidecar), 0, 8);
                Console.WriteLine($"Custom bank BGM RMS: {rms:F6}");
                Check(rms > 0.0001, "Custom bank music is silent");
            } finally {
                if (hadAlias) aliases[id] = oldAlias!; else aliases.Remove(id);
            }
        } finally { Celeste.Audio.Banks.ModCache.Remove(asset); Ok(bank.unload()); }
    }

    internal static float[] ReadSamples(string path) {
        using var reader = new BinaryReader(File.OpenRead(path));
        Check(System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) == "MQOLAUD1", "bad sidecar header");
        List<float> samples = [];
        while (reader.BaseStream.Position < reader.BaseStream.Length) {
            _ = reader.ReadUInt64();
            Check(reader.ReadInt32() == 48000 && reader.ReadUInt16() == 2, "unexpected audio format");
            _ = reader.ReadUInt16(); _ = reader.ReadUInt32();
            uint count = reader.ReadUInt32();
            for (int i = 0; i < count; i++) samples.Add(reader.ReadSingle());
        }
        return samples.ToArray();
    }
    internal static double Rms(float[] samples, double start, double duration) {
        var values = samples.Skip((int)(start * 96000)).Take((int)(duration * 96000)).ToArray();
        return values.Length == 0 ? 0 : Math.Sqrt(values.Average(v => (double)v * v));
    }
}
