using System.Runtime.CompilerServices;

namespace Celeste.Mod.SpeedrunTool.Progress;

internal static class BusyIndicator {
    internal static Action? Drawing;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TryPresent(object progress, bool force) => Drawing?.Invoke();
    internal static void Show() => TryPresent(new object(), true);
}
