using System.Runtime.InteropServices;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>Native SDL entrypoint hooks; all GL calls remain on SDL's current-context thread.</summary>
internal static class SdlFrameSource {
    private const string Library = "microblocks_qol_native";
    private static NativeHook? swapHook, deleteHook;
    private static nint window;
    private static nint library;
    private static bool wasCapturing;
    private static long sequence;
    private static long lastPresentAt;
    private static long awaitingSince;
    private static string? failure;
    internal static string? Failure => Volatile.Read(ref failure);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Swap(nint window);
    private delegate void SwapDetour(Swap orig, nint window);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DeleteContext(nint context);
    private delegate void DeleteDetour(DeleteContext orig, nint context);
    // Everest resolves these P/Invokes to the process SDL library (never bundle another SDL).
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern nint SDL_GL_GetCurrentContext();
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_GL_GetDrawableSize(nint value, out int width, out int height);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern nint SDL_GetWindowTitle(nint value);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern nint SDL_GetWindowFromID(uint id);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern uint SDL_GetWindowFlags(nint value);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern nint SDL_GL_GetProcAddress(nint name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Resolve(nint name);
    private static readonly Resolve resolver = SDL_GL_GetProcAddress;

    internal static void Load() {
        try {
            // Stock Everest loads mods after SDL window creation. Changing an environment
            // variable here is too late to change the selected FNA3D driver/window flags.
            for (uint id = 1; id <= 64; id++) {
                nint candidate = SDL_GetWindowFromID(id);
                if (candidate == 0) continue;
                string title = Marshal.PtrToStringUTF8(SDL_GetWindowTitle(candidate)) ?? "";
                if (title.StartsWith("Celeste", StringComparison.OrdinalIgnoreCase)) {
                    if ((SDL_GetWindowFlags(candidate) & 2) == 0)
                        throw new NotSupportedException("SDL capture requires OpenGL. Add --graphics OpenGL to everest-launch.txt and restart Celeste.");
                    window = candidate;
                    break;
                }
            }
            if (!NativeHook.CanCallOriginal) throw new NotSupportedException("Native SDL hook trampolines are unavailable on this architecture");
            // NativeLibrary.Load honors Everest's registered resolver when invoked via MonoMod's DynDll.
            library = MonoMod.Utils.DynDll.OpenLibrary("SDL2");
            swapHook = new NativeHook(MonoMod.Utils.DynDll.GetExport(library, "SDL_GL_SwapWindow"), (SwapDetour)OnSwap);
            deleteHook = new NativeHook(MonoMod.Utils.DynDll.GetExport(library, "SDL_GL_DeleteContext"), (DeleteDetour)OnDelete);
            failure = null;
        } catch (Exception e) {
            swapHook?.Dispose(); swapHook = null;
            deleteHook?.Dispose(); deleteHook = null;
            if (library != 0) { MonoMod.Utils.DynDll.CloseLibrary(library); library = 0; }
            failure = $"Cannot hook SDL GL presentation: {e.Message}";
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Capture", failure);
        }
    }

    internal static void Update() {
        if (!CaptureSource.WantsPixels) { awaitingSince = 0; return; }
        long now = Environment.TickCount64;
        if (awaitingSince == 0) awaitingSince = now;
        if (Volatile.Read(ref lastPresentAt) == 0 && now - awaitingSince > 5_000 && failure is null) {
            failure = "No SDL GL presentation observed. Restart Celeste with FNA3D_FORCE_DRIVER=OpenGL (D3D11/Metal/Vulkan capture is not available).";
            Logger.Log(LogLevel.Error, "MicroblocksQolUtils/Capture", failure);
        }
    }

    private static void OnSwap(Swap orig, nint value) {
        try {
            if (window == 0) {
                string title = Marshal.PtrToStringUTF8(SDL_GetWindowTitle(value)) ?? "";
                if (title.StartsWith("Celeste", StringComparison.OrdinalIgnoreCase)) window = value;
            }
            if (value == window) {
                Volatile.Write(ref lastPresentAt, Environment.TickCount64);
                bool enabled = CaptureSource.WantsPixels && failure is null;
                if (!enabled && wasCapturing) _ = Release(SDL_GL_GetCurrentContext());
                wasCapturing = enabled;
                if (enabled) {
                    SDL_GL_GetDrawableSize(value, out int width, out int height);
                    if (width > 0 && height > 0) {
                        int status = Frame(resolver, SDL_GL_GetCurrentContext(), (uint)width, (uint)height,
                            ClockNanos(), (ulong)Interlocked.Increment(ref sequence));
                        if (status != 0) failure = NativeCaptureBridge.LastError();
                    }
                }
            }
        } catch (Exception e) { failure = e.Message; }
        // Back buffer is undefined AFTER swap; capture was submitted above, not here.
        orig(value);
    }

    private static void OnDelete(DeleteContext orig, nint context) {
        try { _ = Release(SDL_GL_GetCurrentContext()); window = 0; wasCapturing = false; } catch { }
        orig(context);
    }
    internal static void Unload() {
        swapHook?.Dispose(); swapHook = null;
        deleteHook?.Dispose(); deleteHook = null;
        try { _ = Release(SDL_GL_GetCurrentContext()); } catch { }
        if (library != 0) { MonoMod.Utils.DynDll.CloseLibrary(library); library = 0; }
        window = 0; wasCapturing = false; lastPresentAt = 0; awaitingSince = 0;
    }
    [DllImport(Library, EntryPoint = "mqol_source_clock_nanos", CallingConvention = CallingConvention.Cdecl)] internal static extern ulong ClockNanos();
    [DllImport(Library, EntryPoint = "mqol_source_gl_frame", CallingConvention = CallingConvention.Cdecl)] private static extern int Frame(Resolve resolve, nint context, uint width, uint height, ulong timestamp, ulong sequence);
    [DllImport(Library, EntryPoint = "mqol_source_gl_release", CallingConvention = CallingConvention.Cdecl)] private static extern int Release(nint context);
    [DllImport(Library, EntryPoint = "mqol_source_poll", CallingConvention = CallingConvention.Cdecl)] internal static extern int Poll(out NativeFrame frame);
    [DllImport(Library, EntryPoint = "mqol_source_frame_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void Free(nint pixels, nuint length);
    [StructLayout(LayoutKind.Sequential)] internal struct NativeFrame {
        public nint Pixels; public nuint Length; public uint Width, Height; public ulong Timestamp, Sequence;
    }
}
