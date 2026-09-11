#!/usr/bin/env bash
# Linux/CI: Android API 28+, ARM64, FFmpeg software MPEG-4 video + AAC audio.
set -euo pipefail
cd "$(dirname "$0")/.."
repo="$PWD"
work="$repo/.work"
ndk="${NDK_HOME:?Set NDK_HOME to an Android NDK directory}"
llvm="$ndk/toolchains/llvm/prebuilt/linux-x86_64"
export CC_aarch64_linux_android="$llvm/bin/aarch64-linux-android28-clang"
export AR_aarch64_linux_android="$llvm/bin/llvm-ar"
export CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER="$CC_aarch64_linux_android"
export CARGO_TARGET_DIR="$work/android-target"
export BINDGEN_EXTRA_CLANG_ARGS_aarch64_linux_android="--sysroot=$llvm/sysroot --target=aarch64-linux-android28"
export FFMPEG_DIR="$work/android-ffmpeg/sdk"
export PKG_CONFIG_ALLOW_CROSS=1
export PKG_CONFIG_LIBDIR="$FFMPEG_DIR/lib/pkgconfig"
mkdir -p "$work/android-ffmpeg"
version=8.1
digest=b072aed6871998cce9b36e7774033105ca29e33632be5b6347f3206898e0756a
source="$work/android-ffmpeg/ffmpeg-$version"
if [[ ! -f "$FFMPEG_DIR/.complete" ]]; then
  archive="$work/android-ffmpeg/ffmpeg-$version.tar.xz"
  curl --fail --location --retry 3 "https://ffmpeg.org/releases/ffmpeg-$version.tar.xz" -o "$archive"
  echo "$digest  $archive" | sha256sum --check
  tar -xf "$archive" -C "$work/android-ffmpeg"
  build="$work/android-ffmpeg/build"
  mkdir -p "$build"
  (
    cd "$build"
    "$source/configure" --prefix="$FFMPEG_DIR" \
      --target-os=android --arch=aarch64 --enable-cross-compile \
      --cc="$CC_aarch64_linux_android" --cxx="$llvm/bin/aarch64-linux-android28-clang++" \
      --ar="$AR_aarch64_linux_android" --nm="$llvm/bin/llvm-nm" \
      --ranlib="$llvm/bin/llvm-ranlib" --strip="$llvm/bin/llvm-strip" \
      --sysroot="$llvm/sysroot" --enable-shared --disable-static --enable-pic \
      --extra-ldflags=-Wl,-z,max-page-size=16384 \
      --disable-everything --disable-autodetect --disable-network \
      --disable-avdevice --disable-avfilter --disable-programs --disable-doc --disable-debug --enable-small \
      --enable-avcodec --enable-avformat --enable-avutil --enable-swscale --enable-swresample \
      --enable-protocol=file --enable-demuxer=mov,matroska,ogg,flac,mp3,wav,aac \
      --enable-muxer=mov,mp4,ipod,matroska \
      --enable-decoder=h264,mpeg4,aac,alac,flac,vorbis,opus,mp3,mp3float,pcm_u8,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,pcm_f64le \
      --enable-parser=h264,mpeg4video,aac,flac,mpegaudio,vorbis,opus \
      --enable-encoder=aac,mpeg4
    make -j"$(nproc)"
    make install
    cp config.h "$FFMPEG_DIR/config.h"
    cp ffbuild/config.mak "$FFMPEG_DIR/config.mak"
    touch "$FFMPEG_DIR/.complete"
  )
fi
export RUSTFLAGS="${RUSTFLAGS:-} -C link-arg=-Wl,-z,max-page-size=16384 -C link-arg=-Wl,-rpath,\$ORIGIN"
cargo build --release --locked -p microblocks-qol-native --target aarch64-linux-android --features ffmpeg
dotnet build Source/MicroblocksQolUtils.csproj -c Release
stage="$(mktemp -d "$work/android-package.XXXXXX")"
libs="$stage/Code/lib-linux"
mkdir -p "$libs"
cp Source/bin/Release/net8.0/*.dll "$stage/Code/"
cp "$CARGO_TARGET_DIR/aarch64-linux-android/release/libmicroblocks_qol_native.so" "$libs/"
for name in avutil swresample swscale avcodec avformat; do
  cp -L "$FFMPEG_DIR/lib/lib$name.so" "$libs/"
done
for lib in "$libs"/*.so; do
  # Do not use grep -q with pipefail: llvm-readelf can receive SIGPIPE when
  # grep exits early, which makes the whole build fail with status 74.
  "$llvm/bin/llvm-readelf" -h "$lib" | grep 'Machine:.*AArch64' >/dev/null
  "$llvm/bin/llvm-readelf" -d "$lib"
  "$llvm/bin/llvm-strip" --strip-unneeded "$lib"
done
cp "$source/COPYING.LGPLv2.1" "$stage/Code/FFmpeg-LICENSE.txt"
cp "$FFMPEG_DIR/config.h" "$FFMPEG_DIR/config.mak" "$stage/Code/"
printf 'FFmpeg %s\nSource: https://ffmpeg.org/releases/ffmpeg-%s.tar.xz\nSHA-256: %s\nBuild: scripts/build-android-mod.sh\n' "$version" "$version" "$digest" > "$stage/Code/FFmpeg-SOURCE.txt"
cp everest.yaml "$stage/"
cp -r Dialog "$stage/"
archive="$work/MicroblocksQolUtils-android-arm64.zip"
python3 - "$stage" "$archive" <<'PY'
import pathlib, sys, zipfile
stage, output = map(pathlib.Path, sys.argv[1:])
with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(stage.rglob('*')):
        if path.is_file():
            archive.write(path, path.relative_to(stage).as_posix())
PY
(cd "$work" && sha256sum MicroblocksQolUtils-android-arm64.zip > MicroblocksQolUtils-android-arm64.zip.sha256)
echo "Built $archive (includes FFmpeg)"
