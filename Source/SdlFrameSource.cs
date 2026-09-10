using System.Runtime.InteropServices;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>Native presentation router. SDL GL swaps or SDL/FNA's DXGI presents; GPU calls stay on the renderer thread.</summary>
internal static class SdlFrameSource {
    private const string Library = "microblocks_qol_native";
    private static NativeHook? swapHook, deleteHook;
    private static bool dxgiHooked;
    private static bool dxgiPending;
    private static int tracePresents;
    private static uint appliedFrameRate = uint.MaxValue;
    private static nint window;
    private static nint library;
    private static bool wasCapturing;
    private static long sequence;
    private static long lastPresentAt;
    private static long awaitingSince;
    private static string? failure;
    internal static string? Failure => Volatile.Read(ref failure);
    internal static string Backend { get; private set; } = "awaiting presentation";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Swap(nint window);
    private delegate void SwapDetour(Swap orig, nint window);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DeleteContext(nint context);
    private delegate void DeleteDetour(DeleteContext orig, nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PresentCallback(nint chain, uint interval, uint flags);
    private static readonly PresentCallback presentCallback = OnPresent;
    [StructLayout(LayoutKind.Sequential, Size = 256)] private struct WmInfo {
        public byte Major, Minor, Patch, Padding;
        public uint Subsystem;
        public nint Handle;
    }
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_GetWindowWMInfo(nint value, ref WmInfo info);
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
        tracePresents = 0;
        appliedFrameRate = uint.MaxValue;
        try {
            // Stock Everest loads mods after SDL window creation. Changing an environment
            // variable here is too late to change the selected FNA3D driver/window flags.
            for (uint id = 1; id <= 64; id++) {
                nint candidate = SDL_GetWindowFromID(id);
                if (candidate == 0) continue;
                string title = Marshal.PtrToStringUTF8(SDL_GetWindowTitle(candidate)) ?? "";
                if (title.StartsWith("Celeste", StringComparison.OrdinalIgnoreCase)) {
                    uint flags = SDL_GetWindowFlags(candidate);
                    if ((flags & 0x30000000) != 0 || ((flags & 2) == 0 && !OperatingSystem.IsWindows()))
                        throw new NotSupportedException("This renderer is not supported by capture. OpenGL and Windows D3D11 are supported; Vulkan/Metal/SDL_GPU require separate readback implementations.");
                    window = candidate;
                    break;
                }
            }
            if (!NativeHook.CanCallOriginal) throw new NotSupportedException("Native SDL hook trampolines are unavailable on this architecture");
            // NativeLibrary.Load honors Everest's registered resolver when invoked via MonoMod's DynDll.
            library = MonoMod.Utils.DynDll.OpenLibrary("SDL2");
            swapHook = new NativeHook(MonoMod.Utils.DynDll.GetExport(library, "SDL_GL_SwapWindow"), (SwapDetour)OnSwap);
            deleteHook = new NativeHook(MonoMod.Utils.DynDll.GetExport(library, "SDL_GL_DeleteContext"), (DeleteDetour)OnDelete);
            if (OperatingSystem.IsWindows() && (window == 0 || (SDL_GetWindowFlags(window) & 2) == 0)) {
                // Do not create a D3D device during mod loading. Steam/other overlays
                // are still installing their hooks before FNA creates the real device.
                dxgiPending = true;
            }
            failure = null;
        } catch (Exception e) {
            swapHook?.Dispose(); swapHook = null;
            deleteHook?.Dispose(); deleteHook = null;
            if (dxgiHooked) { _ = UninstallDxgi(); dxgiHooked = false; }
            if (library != 0) { MonoMod.Utils.DynDll.CloseLibrary(library); library = 0; }
            failure = $"Cannot hook native presentation: {e.Message}";
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Capture", failure);
        }
    }

    internal static void Update() {
        if (!CaptureSource.WantsPixels) { awaitingSince = 0; return; }
        uint requestedRate = CaptureSource.RequestedFrameRate;
        if (requestedRate != appliedFrameRate) {
            if (SetFrameRate(requestedRate) != 0) failure = NativeCaptureBridge.LastError();
            else appliedFrameRate = requestedRate;
        }
        if (dxgiPending) {
            dxgiPending = false;
            try {
                if (InstallDxgi(presentCallback) != 0) failure = NativeCaptureBridge.LastError();
                else dxgiHooked = true;
            } catch (Exception e) { failure = e.Message; }
        }
        long now = Environment.TickCount64;
        if (awaitingSince == 0) awaitingSince = now;
        if (Volatile.Read(ref lastPresentAt) == 0 && now - awaitingSince > 5_000 && failure is null) {
            failure = "No supported native presentation observed (OpenGL or Windows D3D11). Capture will not change the game's renderer; Vulkan/Metal/SDL_GPU are not implemented.";
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
                Backend = "OpenGL";
                Volatile.Write(ref lastPresentAt, Environment.TickCount64);
                bool enabled = CaptureSource.WantsPixels && failure is null;
                if (!enabled && wasCapturing) _ = Release(SDL_GL_GetCurrentContext());
                wasCapturing = enabled;
                if (enabled) {
                    SDL_GL_GetDrawableSize(value, out int width, out int height);
                    if (width > 0 && height > 0) {
                        ulong timestamp = ClockNanos();
                        AutoRecorder.ManualSlPresented(timestamp);
                        RecordingSavePause.Presented(timestamp);
                        int status = Frame(resolver, SDL_GL_GetCurrentContext(), (uint)width, (uint)height,
                            timestamp, (ulong)Interlocked.Increment(ref sequence));
                        if (status != 0) failure = NativeCaptureBridge.LastError();
                    }
                }
            }
        } catch (Exception e) { failure = e.Message; }
        // Back buffer is undefined AFTER swap; capture was submitted above, not here.
        orig(value);
    }

    private static void OnPresent(nint chain, uint interval, uint flags) {
        bool trace = Interlocked.Increment(ref tracePresents) <= 3 && Environment.GetEnvironmentVariable("MICROBLOCKS_QOL_CAPTURE_TRACE") == "1";
        if (trace) Logger.Log(LogLevel.Info, "MicroblocksQolUtils/Capture", $"DXGI entry chain={chain:X} window={window:X}");
        try {
            // DXGI_PRESENT_TEST is an occlusion query, not a presented frame.
            if ((flags & 1) == 0) {
                if (window == 0) {
                    for (uint id = 1; id <= 64; id++) {
                        nint candidate = SDL_GetWindowFromID(id);
                        if (candidate != 0 && (Marshal.PtrToStringUTF8(SDL_GetWindowTitle(candidate)) ?? "")
                            .StartsWith("Celeste", StringComparison.OrdinalIgnoreCase)) { window = candidate; break; }
                    }
                }
                WmInfo info = new() { Major = 2, Minor = 0, Patch = 0 };
                if (window != 0 && SDL_GetWindowWMInfo(window, ref info) != 0 && info.Subsystem == 1) {
                    if (trace) Logger.Log(LogLevel.Info, "MicroblocksQolUtils/Capture", $"DXGI HWND={info.Handle:X}");
                    ulong timestamp = ClockNanos();
                    AutoRecorder.ManualSlPresented(timestamp);
                    RecordingSavePause.Presented(timestamp);
                    int status = D3dFrame(chain, info.Handle, CaptureSource.WantsPixels && failure is null ? 1u : 0u,
                        timestamp, (ulong)Interlocked.Increment(ref sequence));
                    if (status == 1) { Backend = "D3D11"; Volatile.Write(ref lastPresentAt, Environment.TickCount64); }
                    else if (status < 0) failure = NativeCaptureBridge.LastError();
                    if (trace) Logger.Log(LogLevel.Info, "MicroblocksQolUtils/Capture", $"DXGI readback status={status}");
                }
            }
        } catch (Exception e) { failure = e.Message; }

    }

    private static void OnDelete(DeleteContext orig, nint context) {
        try { _ = Release(SDL_GL_GetCurrentContext()); window = 0; wasCapturing = false; } catch { }
        orig(context);
    }
    internal static void Unload() {
        swapHook?.Dispose(); swapHook = null;
        deleteHook?.Dispose(); deleteHook = null;
        if (dxgiHooked) { _ = UninstallDxgi(); dxgiHooked = false; }
        try { _ = Release(SDL_GL_GetCurrentContext()); } catch { }
        if (OperatingSystem.IsWindows()) { try { _ = D3dRelease(); } catch { } }
        if (library != 0) { MonoMod.Utils.DynDll.CloseLibrary(library); library = 0; }
        window = 0; wasCapturing = false; lastPresentAt = 0; awaitingSince = 0;
        Backend = "awaiting presentation";
        dxgiPending = false;
    }
    [DllImport(Library, EntryPoint = "mqol_source_clock_nanos", CallingConvention = CallingConvention.Cdecl)] internal static extern ulong ClockNanos();
    [DllImport(Library, EntryPoint = "mqol_source_set_frame_rate", CallingConvention = CallingConvention.Cdecl)] private static extern int SetFrameRate(uint fps);
    [DllImport(Library, EntryPoint = "mqol_source_gl_frame", CallingConvention = CallingConvention.Cdecl)] private static extern int Frame(Resolve resolve, nint context, uint width, uint height, ulong timestamp, ulong sequence);
    [DllImport(Library, EntryPoint = "mqol_source_gl_release", CallingConvention = CallingConvention.Cdecl)] private static extern int Release(nint context);
    [DllImport(Library, EntryPoint = "mqol_source_dxgi_install", CallingConvention = CallingConvention.Cdecl)] private static extern int InstallDxgi(PresentCallback callback);
    [DllImport(Library, EntryPoint = "mqol_source_dxgi_uninstall", CallingConvention = CallingConvention.Cdecl)] private static extern int UninstallDxgi();
    [DllImport(Library, EntryPoint = "mqol_source_d3d11_frame", CallingConvention = CallingConvention.Cdecl)] private static extern int D3dFrame(nint chain, nint window, uint enabled, ulong timestamp, ulong sequence);
    [DllImport(Library, EntryPoint = "mqol_source_d3d11_release", CallingConvention = CallingConvention.Cdecl)] private static extern int D3dRelease();
    [DllImport(Library, EntryPoint = "mqol_source_poll", CallingConvention = CallingConvention.Cdecl)] internal static extern int Poll(out NativeFrame frame);
    [DllImport(Library, EntryPoint = "mqol_source_frame_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void Free(nint pixels, nuint length);
    [StructLayout(LayoutKind.Sequential)] internal struct NativeFrame {
        public nint Pixels; public nuint Length; public uint Width, Height; public ulong Timestamp, Sequence;
    }
}
