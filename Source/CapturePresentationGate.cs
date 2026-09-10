namespace Celeste.Mod.MicroblocksQolUtils;

// Auxiliary UI can present repeatedly without a game draw. It must neither
// consume a source cadence slot nor acknowledge a saved/restored game pose.
internal static class CapturePresentationGate {
    [ThreadStatic] private static int auxiliaryDepth;
    internal static bool AcceptGameplay => auxiliaryDepth == 0;
    internal static IDisposable Auxiliary() {
        auxiliaryDepth++;
        return new Scope();
    }
    private sealed class Scope : IDisposable {
        private bool disposed;
        public void Dispose() {
            if (disposed) return;
            disposed = true;
            auxiliaryDepth--;
        }
    }
}
