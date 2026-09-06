# microblock's QoL Utils

[简体中文](README.md)

A Windows-first QoL utility mod for Celeste + Everest. The portable font, icon,
and UI rasterizer can also be built for Linux and macOS. MiaoNet, CollabUtils2,
and SpeedrunTool are optional runtime integrations rather than hard dependencies.

## Features

### UI and system helpers

- Material You-style HUD cards, settings pages, chapter select, and Everest mod
  options. HUD surfaces, acrylic blur, and the global mod-options replacement
  can be toggled independently.
- A custom QOL settings page with category navigation, setting search, mouse,
  keyboard, and controller input. Everest's binding pages and custom setting
  entries keep their original behavior.
- An opt-in replacement chapter browser with recent chapters, level-set
  navigation, search, author/description/tag metadata, and keyboard, controller,
  mouse, and wheel input. It enters chapters through the normal OuiChapterPanel
  flow and honors the official unlock limits.
- Reflection-only CollabUtils2 support. Collab maps and gyms are hidden by
  default, can be exposed by an advanced option, and are grouped into collapsible
  lobby sections when possible.
- Embedded Material Symbols and a portable font renderer. Text is rasterized at
  its physical output size and cached; the default font is Microsoft YaHei UI,
  with a chooser for installed font families and 80%-160% font scaling.
- Windows HiDPI support and optional input-language switching: text fields use a
  Chinese keyboard layout while normal gameplay uses an English layout.
- Optional removal of room transitions and death animation. Collision boxes have
  hidden, overlaid, and collision-only modes.
- A Microblock QOL Tools entry is added to the pause menu.

### HUD and minimap

- Rolling FPS, CPU frame time, and—when Motion Smoothing is available—separate
  physics and render FPS.
- Optional frame-spike notices and a lightweight frame-profiler HUD.
- A circular or square minimap rendered from the live solid-tile grid:
  - configurable size, zoom, and keyboard/controller zoom bindings;
  - room bounds, room backgrounds, opacity, and adaptive colors;
  - cached shortest routes and remaining-room count to the map's inferred goal;
  - strawberry, golden strawberry, moonberry, heart, cassette, key, and gem markers;
  - persistent collection state from the current save, with optional edge markers
    for strawberries in nearby rooms.
- Optional MiaoNet players, avatars, off-screen players, and names on the minimap.
  Names can be hidden, limited to watched players, or shown for everyone.
- Optional suppression of MiaoNet's native off-screen name labels.

### MiaoNet watching and notifications

When MiaoNet is present, the mod reads same-map player positions, rooms, names,
and avatars through reflection. If MiaoNet is absent, the mod still loads normally.

Everest console commands:

~~~text
qol_watch <player>
qol_unwatch <player>
qol_watch_list
~~~

The MiaoNet chat box also registers /qol (alias /mu):

~~~text
/qol watch <player>
/qol unwatch <player>
/qol list
~~~

On Windows, a system notification is shown when a watched player changes rooms
while Celeste is not the foreground application.

### Recording and death replays

Recording is available in the Windows, Linux, and macOS native backends. Windows
uses WGC, macOS uses ScreenCaptureKit, and Linux uses
xdg-desktop-portal/PipeWire. Runtime capture does not launch an ffmpeg executable,
use a managed frame buffer, or create a subprocess.

macOS asks for Screen & System Audio Recording permission on first use. Linux
shows the desktop portal source picker on first use; select the Celeste window or
its display. Portals that support persistent grants restore that selection on later
launches. The portal path works on Wayland and PipeWire-enabled X11 desktops.

- Automatic recording can cover every room or only runs carrying a golden berry.
- Manual recording can be started, saved, and discarded from the settings page or
  through console commands. Starting and stopping/saving can also use separate,
  optional keyboard or controller bindings; both are unbound by default.
- Full recordings, manual recordings, and death replays share one capture
  session (at most one video capture thread and one FMOD audio thread at a
  time); every recording scheme slices from the same source. Death replays
  retain the latest 30 seconds by default, configurable from 10 to 60
  seconds, save after death, and resume automatically after respawn.
- Continuous capture keeps only successful segments. Deaths, room transitions,
  pauses, SpeedrunTool loads, and custom respawn-point changes affect the final
  edit list without putting failed gameplay into the final video.
- On area completion, the native finalizer reads only retained ranges and produces
  a gapless MP4. The in-game recording library shows finalization progress and
  supports separate full/death views, opening, and deletion.
- The default output root is
  %USERPROFILE%\Videos\Celeste\microblocks-qol-recordings. It can be changed in
  settings. Completed files are stored under full/<area> and deaths/<area>;
  each MP4 also has a .timeline.json sidecar.
- Video prefers the platform H.264 encoder (Media Foundation on Windows,
  VideoToolbox on macOS, and NVENC/QSV on Linux). Linux falls back to MPEG-4 Part 2
  in the MP4 container when no directly usable H.264 encoder is available. Audio
  uses AAC. Frame rate, bitrate, encoder preference, UI SFX capture, and retention
  limits are configurable.
- FMOD DSP taps capture gameplay_sfx, music, and optional ui_sfx. Chunks are
  streamed by bus to an .sfxchunks sidecar, then SFX edits and the BGM post-mix
  timeline are handled separately during finalization instead of buffering an
  entire run in memory. MKV files under `.working` are therefore silent
  intermediates; play the finalized MP4 files under `full` or `deaths` instead.
- BGM can use the captured game mix or SfxOnlyWithPostMix. The latter edits only
  gameplay/UI SFX against the video timeline and lays the separately captured
  music bus onto a continuous post-mix timeline across deaths and pauses. A clean
  mapped file replaces only its matching event segment. Maps containing cassette
  blocks, rhythm entities, or music-sync entities automatically retain the
  captured game mix to preserve timing.

BgmEventMapFile is an optional JSON object. Relative paths are resolved from the
directory containing the JSON file:

~~~json
{
  "event:/music/lvl1/main": "D:/Celeste-BGM/first_steps.flac"
}
~~~

Without a mapping, the separately captured music bus is used as the post-mix BGM source.

The recording setting **Remove freeze frames** is disabled by default. When enabled, finalization
detects stalls in the captured timeline and removes those intervals from the video, audio, and BGM
timelines. The enabled state is highlighted in the settings page.

### Profiler

The Profiler page can start a 10-second in-process EventPipe stack sample:

- update and render are reported separately;
- entries show exclusive CPU time, percentage, owning mod assembly, and MonoMod
  hook target;
- a Mod-only simple view and a full professional view are available;
- CSV and .nettrace reports are written to
  %LOCALAPPDATA%\MicroblocksQolUtils\profiles;
- the lightweight frame-time HUD remains available without a full sample.

## Console commands

In addition to the watching commands above, recording and native capture expose:

~~~text
qol_capture_probe_start
qol_capture_probe_stats
qol_capture_probe_stop

qol_record_start
qol_record_save
qol_record_discard
qol_record_status
~~~

The capture probe is for development diagnostics: it reports platform scap capture,
queue depth, dropped frames, and media time without enabling normal recording.

## Build and install

The default Celeste path is:

~~~text
C:\SteamLibrary\steamapps\common\Celeste
~~~

The build entry point requires Node.js, the .NET 8 SDK, Rust, and the native
toolchain for the selected target. A complete Windows recording build additionally
requires:

- Visual Studio C++ Build Tools;
- LLVM/Clang, including libclang.dll and clang-cl.exe;
- MSYS2 with GNU make, Perl, and NASM;
- tar.

A complete Linux build also needs Clang/libclang, GNU make, pkg-config, and the
PipeWire and D-Bus development packages. A complete macOS build needs Xcode
Command Line Tools. Every platform builds a verified FFmpeg 8.1 source archive
into the minimal LGPL shared runtime packaged with the mod.

The repository script builds the managed mod, native rasterizer, and the current
platform's capture/recording backend, then writes Build plus MicroblocksQolUtils.zip:

~~~powershell
node scripts/build-qol-mod.mjs
~~~

Build and install to the default Celeste installation:

~~~powershell
node scripts/build-qol-mod.mjs --install
~~~

Set CELESTE_ROOT when Celeste is installed elsewhere. Close the game first; the
script refuses to replace DLLs loaded by a running Celeste process:

~~~powershell
$env:CELESTE_ROOT = "D:\Games\Celeste"
node scripts/build-qol-mod.mjs --install
~~~

Targets used by CI can be selected explicitly:

~~~powershell
node scripts/build-qol-mod.mjs --target x86_64-pc-windows-msvc
node scripts/build-qol-mod.mjs --target x86_64-unknown-linux-gnu
node scripts/build-qol-mod.mjs --target x86_64-apple-darwin
~~~

Windows, Linux, and macOS builds all include recording and their FFmpeg shared
runtime. The universal archive places them in Everest's `Code/lib-win-x64`,
`Code/lib-linux`, and `Code/lib-osx` platform directories so Everest only loads
the current platform's variant. Only Windows system notifications remain
platform-specific. Runtime code does not package or invoke an ffmpeg executable.

GitHub Actions runs Rust formatting/tests and builds Windows x64, Linux x64,
and macOS x64 variants, then merges them into one universal
`MicroblocksQolUtils.zip`. Every commit pushed to master updates the nightly
pre-release; tags matching v* publish the same universal archive as a versioned
release.

## Dependency notes

- MiaoNet, CollabUtils2, and SpeedrunTool are optional runtime bridges.
- Material Symbols are embedded in the repository.
- third_party/scap is pinned and locally patched.
