# Capture refactor validation — 2026-09-09

## Passed locally (Windows x64)

| Check | Result |
|---|---|
| `cargo fmt --manifest-path Native/Cargo.toml -- --check` | Passed |
| `cargo test --workspace --locked` | 17 passed |
| `MQOL_TEST_FFMPEG=1 cargo test --workspace --features ffmpeg --locked` | 33 passed, including actual encoder/finalizer file fixtures |
| `dotnet run --project Tests/Capture/Capture.csproj -c Release` | Passed: bounded queues, slow/throwing isolation, cancellation/drain, self-unsubscribe, pre-registration frame rejection, DSP jitter/reset/stall/rate-change clocks |
| `dotnet build Source/MicroblocksQolUtils.csproj -c Release` (.NET 8 SDK) | 0 warnings, 0 errors |
| `Tests/Capture.Integration` with release native DLL and `MQOL_TEST_ENCODER=auto` | 238 pixel callbacks, 201 non-silent audio callbacks; 0 pixel/order errors; slow consumer dropped 199 frames without interrupting normal consumers |
| Real SDL / FMOD integration scenarios | Two simultaneous recordings; first disposed while second continued; drawable resize; correctly oriented red/blue BGRA; shared 3-bus DSP source; source unload/reload; non-GL video rejected while audio-only still worked |
| FFmpeg decode of both source MKVs and finalized MP4 | No decode errors |
| ffprobe final MP4 | H.264 160×90, 1.500 s video; AAC 1.514 s audio (AAC frame padding) |

The release test used the real SDL2, FMOD, MonoMod and reference assemblies from the installed
Celeste/Everest environment, plus its Master/UI banks. It created a hidden SDL window rather
than booting the game. An earlier software-encoder run also passed (238 frames, 202 non-silent
PCM callbacks). On this machine automatic encoder probing reported unavailable NVIDIA CUDA
and successfully fell back to another encoder; this is expected, not a capture failure.

Native fake-GL tests additionally assert zero-timeout fence polling, no mapping of pending
GPU work, bounded PBO capacity, pack/framebuffer/read-buffer state restoration and resize
cleanup. Native sync tests cover first-video gating, dropped audio block duration, clock
reset, independent sink disposal and FPS sampling without cumulative jitter.

## Cross-target checks (not runtime tests)

All passed with `cargo check --workspace --locked --target <target>`:

- `x86_64-unknown-linux-gnu`
- `x86_64-apple-darwin`
- `aarch64-linux-android`

These checks cover the native source **without FFmpeg linking**. Linux/macOS devices were
not available for actual SDL/driver/FMOD execution here. Existing platform CI builds continue
to compile/link each desktop target on its own runner; CI was updated but not remotely run
or pushed in this task. Android is an extension point, not a shipped runtime claim.

## Game launch limitation

An isolated copy under `.work/Celeste-test` was attempted without modifying the user's saves
or mod selection. Celeste exited with `Steam not found!` before mod loading. Therefore no
in-level/full-game smoke pass is claimed. The opt-in in-game smoke runner remains available
via `MICROBLOCKS_QOL_CAPTURE_SMOKE_OUTPUT` once Steam startup works.

## Local build / installation

- Worktree: `.work/capture-refactor`, branch `codex/capture-refactor`.
- Local FFmpeg SDK: BtbN shared **LGPL** FFmpeg 8.1, passed through `QOL_FFMPEG_DIR`.
  Download archive SHA256: `B74C95A1976622F93F9C3CE73551683F4167646B155D9BFE9F2E132E5503E7EB`.
  The repository's default minimal FFmpeg build is unchanged; the supplied SDK is opt-in.
- Installed to `C:\SteamLibrary\steamapps\common\Celeste\Mods\MicroblocksQolUtils.zip`.
  The requested game directory did not exist, so it is a directory junction to the actual
  installation at `E:\SteamLibrary\steamapps\common\Celeste`. No game content was duplicated.
- Installer added `--graphics OpenGL` to `everest-launch.txt`; prior configuration is backed
  up at `.work/capture-refactor/.work/everest-launch-before-1788952261644.txt`.
- Package and installed file SHA256 matched:
  `4859515B732D0F7289181D23AD254E1D59ED34D06BA31276EEDAD39029B18B39`.
- Runtime/frame outputs and temporary tools are under `.work`; they are not committed.

One pre-existing C# null-conditional assignment in `MaterialModOptions.cs` was rewritten as
an equivalent explicit null check so the repository's configured .NET 8 SDK can compile it.
