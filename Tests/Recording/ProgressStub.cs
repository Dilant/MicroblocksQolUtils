using System.Runtime.CompilerServices;

namespace Celeste.Mod.SpeedrunTool.Progress;

internal static class BusyIndicator {
    internal static Action? Drawing;
    internal static bool NativeEnabled, FailBegin;
    internal static int Begins, Disposes, Waits;
    private sealed class Scope : IDisposable { public void Dispose() => Disposes++; }
    internal static IDisposable? Begin(string stage) {
        if (FailBegin) throw new InvalidOperationException("expected unavailable UI");
        if (!NativeEnabled) return null;
        Begins++; Show(); return new Scope();
    }
    internal static void Wait(Task task) {
        Waits++;
        if (NativeEnabled) Show();
        task.GetAwaiter().GetResult();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TryPresent(object progress, bool force) => Drawing?.Invoke();
    internal static void Show() => TryPresent(new object(), true);
}
