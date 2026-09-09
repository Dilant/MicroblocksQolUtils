import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import {
  copyFileSync,
  createWriteStream,
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { rename } from "node:fs/promises";
import { cpus, homedir } from "node:os";
import { dirname, resolve, sep } from "node:path";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";

const ffmpegVersion = "8.1";
const archiveName = `ffmpeg-${ffmpegVersion}.tar.xz`;
const extractedName = `ffmpeg-${ffmpegVersion}`;
const buildName = process.platform === "win32"
  ? `ffmpeg-${ffmpegVersion}-qol-minimal`
  : `ffmpeg-${ffmpegVersion}-qol-minimal-${process.platform}-${process.arch}`;
const buildId = process.platform === "win32"
  ? "ffmpeg-8.1-qol-minimal-v2"
  : `ffmpeg-8.1-qol-minimal-v4-${process.platform}-${process.arch}`;
const sourceUrl = `https://ffmpeg.org/releases/${archiveName}`;
const sourceDigest = "sha256:b072aed6871998cce9b36e7774033105ca29e33632be5b6347f3206898e0756a";
const downloadAttempts = 3;
const downloadTimeoutMs = 2 * 60 * 1000;
const extractTimeoutMs = 2 * 60 * 1000;
const requiredLibraries = ["avcodec", "avformat", "avutil", "swresample", "swscale"];
const windowsRuntimeLibraries = [
  "avcodec-62.dll",
  "avformat-62.dll",
  "avutil-60.dll",
  "swresample-6.dll",
  "swscale-9.dll",
];

const commonConfigureArguments = [
  "--enable-shared",
  "--disable-static",
  "--disable-everything",
  "--disable-autodetect",
  "--disable-network",
  "--disable-avdevice",
  "--disable-avfilter",
  "--disable-programs",
  "--disable-doc",
  "--disable-debug",
  "--enable-small",
  "--enable-avcodec",
  "--enable-avformat",
  "--enable-avutil",
  "--enable-swscale",
  "--enable-swresample",
  "--enable-protocol=file",
  "--enable-demuxer=mov,matroska,ogg,flac,mp3,wav,aac",
  "--enable-muxer=mov,mp4,ipod,matroska",
  "--enable-decoder=h264,mpeg4,aac,alac,flac,vorbis,opus,mp3,mp3float,pcm_u8,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,pcm_f64le",
  "--enable-parser=h264,mpeg4video,aac,flac,mpegaudio,vorbis,opus",
];

const configureArguments = process.platform === "win32"
  ? [
      "--toolchain=msvc",
      "--cc=clang-cl",
      ...commonConfigureArguments,
      "--enable-mediafoundation",
      "--enable-d3d11va",
      "--enable-encoder=aac,h264_mf",
      "--x86asmexe=nasm",
    ]
  : process.platform === "darwin"
    ? [
        ...commonConfigureArguments,
        "--disable-x86asm",
        "--enable-videotoolbox",
        "--enable-audiotoolbox",
        "--enable-encoder=aac,h264_videotoolbox,mpeg4",
        "--install-name-dir=@rpath",
      ]
    : [
        ...commonConfigureArguments,
        "--disable-x86asm",
        "--enable-ffnvcodec",
        "--enable-nvenc",
        "--enable-libvpl",
        "--enable-encoder=aac,h264_nvenc,h264_qsv,mpeg4",
      ];

export async function ensureQolFfmpeg(root) {
  if (!["win32", "darwin", "linux"].includes(process.platform)) return null;
  // Explicit development/CI override; the default remains the reproducible minimal build.
  // Supply a redistributable LGPL shared SDK matching the FFmpeg headers/runtime ABI.
  if (process.env.QOL_FFMPEG_DIR) {
    const sdk = resolve(process.env.QOL_FFMPEG_DIR);
    if (!complete(sdk)) throw new Error(`Incomplete QOL_FFMPEG_DIR SDK at ${sdk}`);
    console.log(`Using supplied FFmpeg SDK at ${sdk}`);
    return { ...ffmpegLayout(sdk), digest: "external-sdk" };
  }
  const cache = resolve(root, ".cache", "ffmpeg");
  const archive = resolve(cache, archiveName);
  const source = resolve(cache, extractedName);
  const output = resolve(cache, buildName);
  const manifestPath = resolve(output, "manifest.json");
  mkdirSync(cache, { recursive: true });

  let manifest;
  try {
    manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
  } catch {
    manifest = null;
  }
  if (manifest?.buildId === buildId && manifest?.sourceDigest === sourceDigest && complete(output)) {
    console.log(`Using cached minimal FFmpeg runtime from ${output}`);
    return ffmpegLayout(output);
  }

  await ensureSourceArchive(archive);
  if (!existsSync(resolve(source, "configure"))) {
    console.log(`Extracting ${archiveName}`);
    removeWithin(cache, source);
    extractSourceArchive(cache, archive, source);
  }

  console.log(`Building minimal FFmpeg ${ffmpegVersion} runtime`);
  removeWithin(cache, output);
  mkdirSync(output, { recursive: true });
  buildMinimalFfmpeg(root, cache, source, output);
  if (process.platform === "win32") {
    mkdirSync(resolve(output, "lib"), { recursive: true });
    for (const library of requiredLibraries) {
      copyFileSync(resolve(output, "bin", `${library}.lib`), resolve(output, "lib", `${library}.lib`));
    }
  }
  copyFileSync(resolve(source, "COPYING.LGPLv2.1"), resolve(output, "LICENSE.txt"));
  writeFileSync(
    manifestPath,
    `${JSON.stringify({ buildId, sourceDigest, configureArguments }, null, 2)}\n`,
    "utf8",
  );
  if (!complete(output)) throw new Error("Minimal FFmpeg build completed without all required files");
  return ffmpegLayout(output);
}

function extractSourceArchive(cache, archive, source) {
  const sevenZip = firstExisting([
    process.env.ProgramFiles ? resolve(process.env.ProgramFiles, "7-Zip", "7z.exe") : null,
    resolve(homedir(), "scoop", "shims", "7z.exe"),
    findOnPath("7z.exe"),
  ]);
  if (sevenZip) {
    // Windows' inbox bsdtar has hung indefinitely while reading this .tar.xz on
    // GitHub-hosted runners. 7-Zip handles the two archive layers separately.
    const tarArchive = resolve(cache, archiveName.slice(0, -3));
    rmSync(tarArchive, { force: true });
    runExtraction(sevenZip, ["x", archive, `-o${cache}`, "-y"]);
    if (!existsSync(tarArchive)) throw new Error(`Extracting ${archiveName} did not create ${tarArchive}`);
    try {
      runExtraction(sevenZip, ["x", tarArchive, `-o${cache}`, "-y"]);
    } finally {
      rmSync(tarArchive, { force: true });
    }
  } else {
    console.warn("7-Zip was not found; falling back to tar");
    runExtraction("tar", ["-xf", archive, "-C", cache]);
  }
  if (!existsSync(resolve(source, "configure"))) {
    throw new Error(`Cannot extract ${archiveName}: ${resolve(source, "configure")} is missing`);
  }
  console.log(`Extracted ${archiveName}`);
}

function runExtraction(command, args) {
  console.log(`Running ${command} ${args.join(" ")} (timeout ${extractTimeoutMs / 1000}s)`);
  const result = spawnSync(command, args, {
    stdio: "inherit",
    windowsHide: true,
    timeout: extractTimeoutMs,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(`Archive extraction failed with exit ${result.status ?? "unknown"}`);
  }
}

export function findLibclangDirectory() {
  if (process.env.LIBCLANG_PATH) return process.env.LIBCLANG_PATH;
  if (process.platform !== "win32") {
    const llvmConfig = spawnSync("llvm-config", ["--libdir"], { encoding: "utf8" });
    const directory = llvmConfig.status === 0 ? llvmConfig.stdout.trim() : "";
    if (directory) return directory;
    const xcrun = process.platform === "darwin"
      ? spawnSync("xcrun", ["--find", "clang"], { encoding: "utf8" })
      : null;
    const xcodeClang = xcrun?.status === 0 ? xcrun.stdout.trim() : "";
    const llvmDirectories = existsSync("/usr/lib")
      ? readdirSync("/usr/lib")
        .filter((name) => /^llvm-\d+$/u.test(name))
        .map((name) => resolve("/usr/lib", name, "lib"))
      : [];
    for (const candidate of [
      ...llvmDirectories,
      xcodeClang ? resolve(dirname(xcodeClang), "..", "lib") : "",
      "/opt/homebrew/opt/llvm/lib",
      "/usr/local/opt/llvm/lib",
    ]) {
      if (existsSync(candidate)) return candidate;
    }
    return "";
  }
  for (const executable of ["libclang.dll", "clang.exe"]) {
    const found = findOnPath(executable);
    if (found) return dirname(found);
  }
  throw new Error(
    "libclang.dll is required to build the FFmpeg Rust bindings; install LLVM or set LIBCLANG_PATH",
  );
}

async function ensureSourceArchive(archive) {
  if (existsSync(archive) && sha256(archive) === sourceDigest) {
    console.log(`Using cached ${archiveName}`);
    return;
  }
  rmSync(archive, { force: true });
  const temporary = `${archive}.download`;
  let lastError;
  for (let attempt = 1; attempt <= downloadAttempts; attempt += 1) {
    rmSync(temporary, { force: true });
    console.log(`Downloading ${archiveName} (attempt ${attempt}/${downloadAttempts}, timeout ${downloadTimeoutMs / 1000}s)`);
    try {
      const response = await fetch(sourceUrl, {
        redirect: "follow",
        signal: AbortSignal.timeout(downloadTimeoutMs),
      });
      if (!response.ok || !response.body) throw new Error(`HTTP ${response.status}`);
      await pipeline(Readable.fromWeb(response.body), createWriteStream(temporary));
      const digest = sha256(temporary);
      if (digest !== sourceDigest) {
        throw new Error(`digest mismatch: expected ${sourceDigest}, got ${digest}`);
      }
      await rename(temporary, archive);
      console.log(`Downloaded ${archiveName}`);
      return;
    } catch (error) {
      lastError = error;
      rmSync(temporary, { force: true });
      console.warn(`Cannot download ${archiveName}: ${error.message}`);
    }
  }
  throw new Error(`Cannot download ${archiveName} after ${downloadAttempts} attempts`, {
    cause: lastError,
  });
}

function buildMinimalFfmpeg(root, cache, source, output) {
  if (process.platform !== "win32") {
    buildMinimalFfmpegUnix(source, output);
    return;
  }
  const visualStudio = findVisualStudio();
  const vcvars = resolve(visualStudio, "VC", "Auxiliary", "Build", "vcvars64.bat");
  const msvcBin = findMsvcBin(visualStudio);
  const msysRoot = findMsys2Root();
  const bash = resolve(msysRoot, "usr", "bin", "bash.exe");
  const make = resolve(msysRoot, "usr", "bin", "make.exe");
  const nasm = firstExisting([
    resolve(msysRoot, "mingw64", "bin", "nasm.exe"),
    resolve(msysRoot, "usr", "bin", "nasm.exe"),
    findOnPath("nasm.exe"),
  ]);
  const llvmBin = findLlvmBin();
  if (!existsSync(vcvars)) throw new Error(`Visual Studio vcvars64.bat was not found at ${vcvars}`);
  if (!existsSync(bash) || !existsSync(make)) {
    throw new Error("MSYS2 with GNU make is required to build the minimal FFmpeg runtime");
  }
  if (!nasm) {
    throw new Error(
      `NASM is required; run ${resolve(msysRoot, "usr", "bin", "pacman.exe")} -S --needed mingw-w64-x86_64-nasm`,
    );
  }

  const shellScript = resolve(cache, "build-minimal-ffmpeg.sh");
  const batchScript = resolve(cache, "build-minimal-ffmpeg.cmd");
  const jobs = Math.max(1, Math.min(8, cpus().length));
  writeFileSync(
    shellScript,
    [
      "#!/usr/bin/env bash",
      "set -euo pipefail",
      'export PATH="/usr/bin:/bin:$PATH"',
      'export PATH="$(cygpath -u "$QOL_MSVC_BIN"):$(cygpath -u "$QOL_LLVM_BIN"):$(cygpath -u "$QOL_NASM_BIN"):$PATH"',
      'cd "$(cygpath -u "$QOL_SOURCE")"',
      'echo "Cleaning previous FFmpeg build state"',
      "make distclean >/dev/null 2>&1 || true",
      'echo "Configuring minimal FFmpeg runtime (10 minute timeout)"',
      `timeout 10m ./configure --prefix="$(cygpath -m "$QOL_PREFIX")" ${configureArguments.join(" ")} &`,
      "configure_pid=$!",
      'while kill -0 "$configure_pid" 2>/dev/null; do',
      "  sleep 30",
      '  if kill -0 "$configure_pid" 2>/dev/null; then',
      '    echo "FFmpeg configure is still running; latest probe:"',
      '    tail -n 1 ffbuild/config.log 2>/dev/null || true',
      "  fi",
      "done",
      'if wait "$configure_pid"; then',
      "  :",
      "else",
      "  status=$?",
      '  echo "FFmpeg configure failed or timed out (exit $status)"',
      "  tail -n 20 ffbuild/config.log 2>/dev/null || true",
      '  exit "$status"',
      "fi",
      'echo "Compiling minimal FFmpeg runtime with $QOL_JOBS jobs"',
      'timeout 15m make -j"$QOL_JOBS"',
      'echo "Installing minimal FFmpeg runtime"',
      "timeout 5m make install",
      "",
    ].join("\n"),
    "utf8",
  );
  writeFileSync(
    batchScript,
    [
      "@echo off",
      "setlocal",
      `call "${batchValue(vcvars)}" >nul`,
      "if errorlevel 1 exit /b %errorlevel%",
      `set "QOL_SOURCE=${batchValue(source)}"`,
      `set "QOL_PREFIX=${batchValue(output)}"`,
      `set "QOL_MSVC_BIN=${batchValue(msvcBin)}"`,
      `set "QOL_LLVM_BIN=${batchValue(llvmBin)}"`,
      `set "QOL_NASM_BIN=${batchValue(dirname(nasm))}"`,
      `set "QOL_JOBS=${jobs}"`,
      `"${batchValue(bash)}" "${batchValue(toMsysPath(shellScript))}"`,
      "exit /b %errorlevel%",
      "",
    ].join("\r\n"),
    "utf8",
  );
  const build = spawnSync("cmd.exe", ["/d", "/c", batchScript], {
    cwd: root,
    stdio: "inherit",
    windowsHide: true,
  });
  if (build.error) throw build.error;
  if (build.status !== 0) throw new Error(`Cannot build minimal FFmpeg runtime (exit ${build.status})`);
}

function buildMinimalFfmpegUnix(source, output) {
  const jobs = Math.max(1, Math.min(8, cpus().length));
  spawnSync("make", ["distclean"], { cwd: source, stdio: "ignore" });
  const configure = spawnSync(resolve(source, "configure"), [
    `--prefix=${output}`,
    ...configureArguments,
  ], { cwd: source, stdio: "inherit" });
  if (configure.status !== 0) throw new Error(`Cannot configure minimal FFmpeg (exit ${configure.status})`);
  const build = spawnSync("make", [`-j${jobs}`], { cwd: source, stdio: "inherit" });
  if (build.status !== 0) throw new Error(`Cannot build minimal FFmpeg (exit ${build.status})`);
  const install = spawnSync("make", ["install"], { cwd: source, stdio: "inherit" });
  if (install.status !== 0) throw new Error(`Cannot install minimal FFmpeg (exit ${install.status})`);
  if (process.platform === "linux") installLinuxVplRuntime(output);
}

function installLinuxVplRuntime(output) {
  const pkgConfig = spawnSync("pkg-config", ["--variable=libdir", "vpl"], { encoding: "utf8" });
  const libdir = pkgConfig.status === 0 ? pkgConfig.stdout.trim() : "";
  const library = libdir && existsSync(libdir)
    ? readdirSync(libdir)
      .filter((name) => /^libvpl\.so\.\d+$/u.test(name))
      .sort()
      .at(-1)
    : null;
  if (!library) throw new Error("libvpl runtime library was not found after the FFmpeg build");
  copyFileSync(resolve(libdir, library), resolve(output, "lib", library));
  for (const runtime of runtimeLibraries(output)) {
    const patched = spawnSync("patchelf", ["--set-rpath", "$ORIGIN", runtime], { stdio: "inherit" });
    if (patched.status !== 0) throw new Error(`Cannot set the Linux runtime path on ${runtime}`);
  }

  const prefix = spawnSync("pkg-config", ["--variable=prefix", "vpl"], { encoding: "utf8" });
  const vplPrefix = prefix.status === 0 ? prefix.stdout.trim() : "";
  const license = firstExisting([
    "/usr/share/doc/libvpl2/copyright",
    vplPrefix ? resolve(vplPrefix, "share", "licenses", "oneVPL", "LICENSE") : null,
    vplPrefix ? resolve(vplPrefix, "share", "licenses", "libvpl", "LICENSE") : null,
    vplPrefix ? resolve(vplPrefix, "share", "licenses", "vpl", "LICENSE") : null,
  ]);
  if (!license) throw new Error("libvpl license file was not found after the FFmpeg build");
  copyFileSync(license, resolve(output, "VPL-LICENSE.txt"));
}

function complete(root) {
  const libraries = runtimeLibraries(root).map((path) => path.split(/[\\/]/u).at(-1));
  return requiredLibraries.every((library) => libraries.some((name) =>
    process.platform === "win32"
      ? name?.startsWith(`${library}-`) && name.endsWith(".dll")
      : process.platform === "darwin"
        ? name === `lib${library}.dylib` || name?.startsWith(`lib${library}.`) && name.endsWith(".dylib")
        : name === `lib${library}.so` || name?.startsWith(`lib${library}.so.`)))
    && (process.platform !== "win32"
      || requiredLibraries.every((name) => existsSync(resolve(root, "lib", `${name}.lib`))))
    && (process.platform !== "linux" || libraries.some((name) => /^libvpl\.so\.\d+$/u.test(name)))
    && existsSync(resolve(root, "include", "libavcodec", "avcodec.h"))
    && existsSync(resolve(root, "LICENSE.txt"))
    && (process.platform !== "linux" || existsSync(resolve(root, "VPL-LICENSE.txt")));
}

function ffmpegLayout(root) {
  return {
    root,
    bin: resolve(root, "bin"),
    license: resolve(root, "LICENSE.txt"),
    additionalLicenses: process.platform === "linux" ? [resolve(root, "VPL-LICENSE.txt")] : [],
    runtimeLibraries: runtimeLibraries(root),
    digest: sourceDigest,
  };
}

function runtimeLibraries(root) {
  if (process.platform === "win32") {
    return windowsRuntimeLibraries
      .map((name) => resolve(root, "bin", name))
      .filter(existsSync);
  }
  const directory = resolve(root, "lib");
  if (!existsSync(directory)) return [];
  return readdirSync(directory)
    .filter((name) => requiredLibraries.some((library) => process.platform === "darwin"
      ? new RegExp(`^lib${library}\\.\\d+\\.dylib$`, "u").test(name)
      : new RegExp(`^lib${library}\\.so\\.\\d+$`, "u").test(name))
      || process.platform === "linux" && /^libvpl\.so\.\d+$/u.test(name))
    .map((name) => resolve(directory, name));
}

function findVisualStudio() {
  const vswhere = firstExisting([
    process.env["ProgramFiles(x86)"]
      ? resolve(process.env["ProgramFiles(x86)"], "Microsoft Visual Studio", "Installer", "vswhere.exe")
      : null,
    resolve(homedir(), "scoop", "apps", "vswhere", "current", "vswhere.exe"),
    findOnPath("vswhere.exe"),
  ]);
  if (!vswhere) throw new Error("Visual Studio Build Tools and vswhere.exe are required");
  const result = spawnSync(vswhere, [
    "-latest",
    "-products",
    "*",
    "-requires",
    "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
    "-property",
    "installationPath",
  ], { encoding: "utf8", windowsHide: true });
  const path = result.stdout?.split(/\r?\n/u).find(Boolean)?.trim();
  if (result.status !== 0 || !path) throw new Error("A Visual Studio C++ x64 toolchain was not found");
  return path;
}

function findMsvcBin(visualStudio) {
  const tools = resolve(visualStudio, "VC", "Tools", "MSVC");
  const versions = readdirSync(tools, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => entry.name)
    .filter((version) => existsSync(resolve(tools, version, "bin", "Hostx64", "x64", "link.exe")))
    .sort(compareVersions)
    .reverse();
  if (versions.length === 0) throw new Error("Visual Studio x64 linker was not found");
  return resolve(tools, versions[0], "bin", "Hostx64", "x64");
}

function findMsys2Root() {
  const roots = [
    process.env.MSYS2_ROOT,
    resolve(homedir(), "scoop", "apps", "msys2", "current"),
    "C:/msys64",
  ];
  const pathBash = findOnPath("bash.exe");
  if (pathBash?.toLowerCase().includes("msys2")) roots.push(resolve(pathBash, "..", "..", ".."));
  const root = roots.find((candidate) => candidate && existsSync(resolve(candidate, "usr", "bin", "bash.exe")));
  if (!root) throw new Error("MSYS2 is required to build the minimal FFmpeg runtime");
  return resolve(root);
}

function findLlvmBin() {
  const libclang = findLibclangDirectory();
  if (existsSync(resolve(libclang, "clang-cl.exe"))) return resolve(libclang);
  const clangCl = findOnPath("clang-cl.exe");
  if (clangCl) return dirname(clangCl);
  throw new Error("clang-cl.exe is required; install LLVM and place it on PATH");
}

function findOnPath(name) {
  if (!name) return null;
  const command = process.platform === "win32" ? "where.exe" : "which";
  const where = spawnSync(command, [name], { encoding: "utf8", windowsHide: true });
  if (where.status !== 0) return null;
  return where.stdout.split(/\r?\n/u).find(Boolean)?.trim() ?? null;
}

function firstExisting(candidates) {
  return candidates.find((candidate) => candidate && existsSync(candidate)) ?? null;
}

function compareVersions(left, right) {
  const a = left.split(".").map(Number);
  const b = right.split(".").map(Number);
  for (let index = 0; index < Math.max(a.length, b.length); index += 1) {
    const difference = (a[index] ?? 0) - (b[index] ?? 0);
    if (difference !== 0) return difference;
  }
  return left.localeCompare(right);
}

function toMsysPath(path) {
  const normalized = resolve(path).replaceAll("\\", "/");
  const match = /^([A-Za-z]):\/(.*)$/u.exec(normalized);
  if (!match) throw new Error(`MSYS2 build path must use a drive letter: ${path}`);
  return `/${match[1].toLowerCase()}/${match[2]}`;
}

function batchValue(value) {
  return String(value).replaceAll("%", "%%").replaceAll("^", "^^");
}

function removeWithin(root, target) {
  const base = resolve(root);
  const path = resolve(target);
  if (!path.startsWith(`${base}${sep}`)) throw new Error(`Refusing to remove path outside ${base}: ${path}`);
  rmSync(path, { recursive: true, force: true });
}

function sha256(path) {
  return `sha256:${createHash("sha256").update(readFileSync(path)).digest("hex")}`;
}
