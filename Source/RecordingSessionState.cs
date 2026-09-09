namespace Celeste.Mod.MicroblocksQolUtils;

public enum AutoRecordingMode { Off, Chapter, Golden }
public enum GoldenRecordingEnd { BerryCollected, ChapterComplete }
public enum GoldenRecordingDeath { Discard, Continue, Save }
internal enum RecordingSessionKind { None, Manual, AutoChapter, AutoGolden }
internal enum RecordingDeathAction { TrimFailedBranch, Continue, Discard, Save }

// Owns trigger/stop semantics, independently of encoder and timeline lifetime.
internal sealed class RecordingSessionState {
    public RecordingSessionKind Kind { get; private set; }
    public bool ManualRequested { get; private set; }
    public bool ManualMode => ManualRequested || Kind == RecordingSessionKind.Manual;
    public bool Automatic => Kind is RecordingSessionKind.AutoChapter or RecordingSessionKind.AutoGolden;
    public bool Suppressed { get; private set; }
    public bool ChapterCompleted { get; private set; }
    public bool ContinuingAfterDeath { get; private set; }
    private bool observedGolden;
    private AutoRecordingMode observedMode;
    private GoldenRecordingEnd goldenEnd;
    private GoldenRecordingDeath goldenDeath;

    public void Observe(AutoRecordingMode mode, bool carryingGolden) {
        if (mode != observedMode || (carryingGolden && !observedGolden)) Suppressed = false;
        observedMode = mode;
        observedGolden = carryingGolden;
    }

    public RecordingSessionKind Next(bool carryingGolden) {
        if (Kind != RecordingSessionKind.None) return Kind;
        if (ManualRequested) return RecordingSessionKind.Manual;
        if (Suppressed || ChapterCompleted) return RecordingSessionKind.None;
        return observedMode switch {
            AutoRecordingMode.Chapter => RecordingSessionKind.AutoChapter,
            AutoRecordingMode.Golden when carryingGolden => RecordingSessionKind.AutoGolden,
            _ => RecordingSessionKind.None
        };
    }

    public bool RequestManual() {
        // A start hotkey during automatic recording must never relabel its output or
        // leave a hidden manual request behind for after the automatic session ends.
        if (Kind != RecordingSessionKind.None) return false;
        ManualRequested = true;
        return true;
    }

    public void Start(RecordingSessionKind kind, GoldenRecordingEnd end, GoldenRecordingDeath death) {
        Kind = kind;
        ManualRequested = false;
        goldenEnd = end;
        goldenDeath = death;
        ContinuingAfterDeath = false;
    }

    public bool CollectGolden() => Kind == RecordingSessionKind.AutoGolden
        && !ContinuingAfterDeath && goldenEnd == GoldenRecordingEnd.BerryCollected;

    public RecordingDeathAction Die() {
        observedGolden = false;
        if (Kind != RecordingSessionKind.AutoGolden) return RecordingDeathAction.TrimFailedBranch;
        if (goldenDeath == GoldenRecordingDeath.Continue) {
            ContinuingAfterDeath = true;
            return RecordingDeathAction.Continue;
        }
        return goldenDeath == GoldenRecordingDeath.Save ? RecordingDeathAction.Save : RecordingDeathAction.Discard;
    }

    public void CompleteChapter() => ChapterCompleted = true;

    public void Stop() {
        Kind = RecordingSessionKind.None;
        ManualRequested = false;
        ContinuingAfterDeath = false;
        Suppressed = true;
    }

    public void ResetRun(bool keepManualRequest = false) {
        bool requested = keepManualRequest && ManualRequested;
        Stop();
        ManualRequested = requested;
        Suppressed = false;
        ChapterCompleted = false;
        observedGolden = false;
        observedMode = AutoRecordingMode.Off;
    }
}
