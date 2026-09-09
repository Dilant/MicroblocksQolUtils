# 共享采集架构（native ABI 5）

## 数据路径

```text
Celeste / FNA3D OpenGL
    SDL_GL_SwapWindow native detour (MonoMod, before original swap)
        -> Rust thread-local GL context state
        -> 3 PBOs + GLsync fences (poll timeout = 0)
        -> bounded CPU queue (3 frames, old frames dropped)
        -> CaptureSource worker: flip RGBA to top-down BGRA / own managed payload
                                                |
FMOD gameplay_sfx / ui_sfx / music               |
    one pass-through DSP per bus                |
        -> sample-clock timestamp + pooled copy |
        -> bounded source queue (32 chunks)     |
        -> CaptureSource worker ----------------+
                                                |
                     CaptureSubscription: per-consumer queues (3 pixels / 32 PCM)
                     serial worker callbacks; exceptions and backpressure isolated
                         /                      |                       \
              full recording              death replay              plugins / probe
              NativeCaptureSession        NativeCaptureSession      custom callbacks
              own encoder/audio writer    own encoder/audio writer
                         \                      /
                            existing finalizer
```

Acquisition is single-instance for the process. `NativeCaptureSession` is now a **sink**:
no window discovery, OS capture thread, DSP installation, portal, or permission handling.
`AutoRecorder` editing/death-replay behavior and the existing FFmpeg finalizer are retained.
PR #2 contributes only its audio-origin/reset fix; its recorder/authorization/UI changes
are deliberately not merged. The vendored capture crate and its dependency graph are removed.

## Public API

```csharp
CaptureSubscription registration = CaptureSource.Subscribe(
    pixels: frame => {
        // Owned read-only BGRA8, top-down, tightly packed. Safe to retain.
        ReadOnlyMemory<byte> bgra = frame.Pixels;
        int stride = frame.Stride;
        ulong originalFrameTime = frame.TimestampNanos;
        ulong sequence = frame.Sequence;
    },
    fmod: audio => {
        // Owned read-only interleaved float PCM; silence may also be delivered.
        // SampleRate, Channels, BusId (1/2/3), BusPath, DspClock, TimestampNanos.
    });

registration.Dispose();            // cancels queued work; safe inside its own callback
await registration.Completion;     // wait elsewhere for any in-flight callback before freeing resources
```

Either callback can be null, but not both. At most 16 registrations are admitted.
Callbacks are serialized **within** a registration, independently scheduled **between**
registrations. They do not run on a game, GL, or FMOD thread. Do not manipulate game/FMOD
objects from callbacks; marshal game work to the game thread. Never synchronously wait on
`Completion` from the registration's own callback. Pixel and PCM timestamps, not callback
arrival order, determine A/V synchronization. Consumers can retain payloads at their own
memory cost; producers never reuse delivered managed arrays. `ReadOnlyMemory` is a
read-only API contract: consumers must not subvert it via unsafe code/MemoryMarshal.

`DroppedFrames`, `DroppedAudioChunks`, `CallbackErrors` are per-registration counters;
`CaptureSource.DroppedAudioChunks` counts source mixer-queue overflow/contention.
`CaptureSource.VideoError` reports hook/readback/worker failure. No frames are fabricated
when minimized, when the renderer stalls, or when a queue/fence is not ready.

A slow callback does not block other registrations or the renderer/mixer. Dropping preserves
source timestamps, leaving a gap rather than changing playback speed. The source polls
at most three video frames and 32 audio chunks per worker batch. Maximum frame storage is
64 MiB (4096 x 4096 RGBA-equivalent); unsupported dimensions produce an explicit error.

## Timing / ownership

- One epoch anchored to Rust `Instant` plus the initial Unix time is shared by video and audio.
  Wall-clock changes cannot move the recording clock backwards.
- Video carries the time/sequence assigned at **submission before swap**, not the later map,
  CPU conversion, callback, encoder, or disk-write time. Reading after swap would read an
  undefined/new back buffer, so the detour always submits before calling original SDL.
- Each bus maps FMOD DSP sample-clock progression into that shared time domain. Normal mixer
  scheduling jitter does not change timestamps; DSP reset/sample-rate changes/suspension
  re-anchor the clock.
- Each recording gets its own first-video origin, resets its audio clocks, and rejects audio
  preceding that origin (the PR #2 fix). A bounded PCM preroll preserves eligible blocks
  that arrived while waiting for asynchronous PBO delivery. Newly registered consumers
  reject in-flight frames/chunks from before their registration.
- The encoder uses the **same sink origin** even if its bounded queue dropped the first frame.
  Audio sample gaps are retained, including chunks lost to backpressure.
- Native queues copy borrowed FFI data. The source poll result is native-owned until
  `mqol_source_frame_free`; managed code frees it in `finally` after copying.
- GL APIs run only on the context-owning thread. Readback saves/restores pack buffer,
  read framebuffer/read buffer, alignment, row length, and row/pixel skips. Pending PBOs
  are never mapped. Resize recreates the ring. Context deletion/mod unloading detaches hooks;
  GL deletion is performed only in its owning current context, otherwise context destruction
  reclaims GPU objects. Subscriber count zero stops GL readback on the next swap and removes
  shared DSPs on the next game update. Shared source lifetime itself is module-owned.
- Unregister cancels only that consumer. Native sink disposal drains its callbacks before
  stopping/joining encoder and audio writer threads. Stopped native sinks are single-use.

## Platform contract

- Windows / Linux / macOS use the **same** GL/PBO implementation and SDL entrypoint hooks;
  there are no WGC, ScreenCaptureKit, D-Bus or PipeWire dependencies.
- Requires FNA3D **OpenGL 3.2+**. Stock Everest creates the SDL window before loading
  mods, so changing the renderer in a module initializer is too late. Set
  `--graphics OpenGL` in `everest-launch.txt` (or `FNA3D_FORCE_DRIVER=OpenGL` before
  launch) and restart. `build-qol-mod.mjs --install` adds that launch flag if no explicit
  `--graphics` override exists, backing up the previous file under `.work`. Existing
  overrides are preserved, including non-OpenGL choices; video subscriptions report
  the need to restart. A normal Everest ZIP installation must configure OpenGL manually.
  D3D11/Metal/Vulkan windows cannot be captured by this GL source.
- Linux X11/Wayland both read the game's GL back buffer, not desktop surfaces. macOS needs
  no screen-recording permission. Runtime availability of the appropriate GL driver and
  MonoMod native trampolines remains required.
- The native GL API uses `SDL_GL_GetProcAddress` passed by the host rather than linking
  platform GL/SDL libraries. RGBA readback and GLES3-compatible PBO/sync APIs leave an Android
  extension point. Android ARM64 **compile checking** is not Android runtime support: an
  Everest host, SDL binding/packaging, native trampoline support, and GLES3 device tests are
  still required. D3D11/Metal/Vulkan capture is not implemented or claimed.

## Tests / reproduction

```text
cargo fmt --manifest-path Native/Cargo.toml -- --check
cargo test --workspace --locked
cargo test --workspace --features ffmpeg --locked   # set MQOL_TEST_FFMPEG=1 for codec/file fixtures

dotnet run --project Tests/Capture/Capture.csproj -c Release
```

`Tests/Capture.Integration` runs on Windows with the installed game's genuine SDL/FMOD DLLs,
loads its FMOD master/UI banks, creates a hidden OpenGL window, and uses production source,
hooks, subscriptions, native encoders, and finalizer. It does not boot Celeste or touch saves.
It checks colored-frame orientation/channel order, resize, two simultaneous recordings,
independent disposal, PCM, a slow subscriber, stop/reload, and playable finalized output.
Set `CELESTE_ROOT`, `MQOL_NATIVE_PATH` (FFmpeg-enabled DLL), `MQOL_TEST_OUTPUT` (under `.work`),
and add the FFmpeg DLL directory to `PATH`, then:

```text
dotnet run --project Tests/Capture.Integration/Capture.Integration.csproj -c Release
```

`MICROBLOCKS_QOL_CAPTURE_SMOKE_OUTPUT` additionally enables an in-game test after 180 game
updates; it writes `.passed` or `.failed` alongside the requested output and leaves saves alone.

Local validation on 2026-09-09: Windows native tests, real SDL/FMOD integration, .NET build,
and Rust Linux x64/macOS x64/Android ARM64 cross-target checks. See `docs/capture-validation.md`
for final counts and limits. Cross-target checks are not Linux/macOS runtime tests.

The normal build uses the existing reproducible minimal FFmpeg build. Developers may set
`QOL_FFMPEG_DIR` to an existing redistributable shared LGPL SDK (headers, import libraries,
runtime DLLs, LICENSE.txt) to test/package locally without rebuilding FFmpeg. The native
library must be built on the SDK's matching host architecture. No ffmpeg subprocess runs
in the mod; CLI ffmpeg/ffprobe are used only by external validation.
