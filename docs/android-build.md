# Android ARM64 builds

The **Android** GitHub Actions workflow builds an API 28+ ARM64 Everest mod ZIP
with the native font/SVG rasterizer, recording backend and FFmpeg 8.1 shared
libraries. Download the `MicroblocksQolUtils-android-arm64` artifact from the run,
extract the artifact wrapper, and put `MicroblocksQolUtils-android-arm64.zip` in
the selected CeleMod game's `Mods` directory. Replace any previous copy of this
mod; do not install two copies. This package is separate from the desktop Linux
package because Everest uses the same `Code/lib-linux` directory for both.

The workflow runs on relevant changes to master, pull requests, and Android CI
branches, and can also be run manually. Each successful run uploads the ZIP and
a SHA-256 checksum for 30 days. It does not modify desktop releases.

To reproduce the full build on Linux, install .NET 8 SDK, Rust stable with the
`aarch64-linux-android` target, Android NDK 28.2.13676358, libclang, make, curl,
Python 3, unzip and xz. Set `NDK_HOME` to the NDK and `CELESTE_ROOT` to Everest
reference assemblies, then run `bash scripts/build-android-mod.sh`. Generated
files are under `.work`. The FFmpeg archive checksum is verified before building;
the package includes its LGPL license, source URL and configuration.

The existing Windows PowerShell script is a **development build without
FFmpeg**. Use the CI/Linux build for recording/export support. Android's automatic
encoder currently uses software MPEG-4 video and AAC audio; hardware H.264 encoding
is not configured. Successful cross compilation is not a device test of GLES
capture, audio or touch interaction.

CeleMod stores games on external storage, which is not executable. At load time,
the mod extracts its own native backend and FFmpeg libraries into the host's
private `DOTNET_ROOT/mod-native-cache` directory, using a content hash for cache
invalidation. Dependencies load before the backend. The loader accepts ZIP and
directory mods and also accepts the older development package without FFmpeg.
