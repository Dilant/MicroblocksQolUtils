namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Selects the active <see cref="ICaptureBackend"/> used by the recorder. Today this is the
/// desktop-portal / FFmpeg backend; a future engine-level frame source would be registered
/// here instead, leaving the timeline/finalizer/UI untouched.
/// </summary>
public static class CaptureBackend {
    private static ICaptureBackend? current;

    public static ICaptureBackend Current => current ??= new PortalCaptureBackend();
}
