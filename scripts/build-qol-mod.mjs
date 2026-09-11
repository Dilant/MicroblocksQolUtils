import { copyFileSync, cpSync, existsSync, mkdirSync, readFileSync, writeFileSync, readdirSync, rmSync, statSync } from "node:fs";
import { basename, delimiter, dirname, resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { ensureQolFfmpeg, findLibclangDirectory } from "./qol-ffmpeg.mjs";

const root = resolve(import.meta.dirname, "..");
const output = resolve(root, "Build");
const argumentValue = (name) => {
  const index = process.argv.indexOf(name);
  if (index < 0) return null;
  const value = process.argv[index + 1];
  if (!value || value.startsWith("--")) throw new Error(`${name} requires a value`);
  return value;
};
const target = argumentValue("--target");
const disableFfmpeg = process.argv.includes("--no-ffmpeg");
if (disableFfmpeg && process.argv.includes("--install")) {
  throw new Error("Cannot install a --no-ffmpeg build: it cannot record videos. Remove --no-ffmpeg to build and install the complete mod.");
}
const archive = resolve(root, argumentValue("--archive")
  ?? (disableFfmpeg ? "MicroblocksQolUtils-no-recording.zip" : "MicroblocksQolUtils.zip"));
if (disableFfmpeg) console.warn("Diagnostic build only: video recording is unavailable without FFmpeg.");
const managedOutput = resolve(root, "Source/bin/Release/net8.0");
const dll = resolve(managedOutput, "MicroblocksQolUtils.dll");
const celesteRoot = resolve(process.env.CELESTE_ROOT ?? "C:/SteamLibrary/steamapps/common/Celeste");
const targetPlatform = target?.includes("windows")
  ? "win32"
  : target?.includes("apple-darwin")
    ? "darwin"
    : target?.includes("linux")
      ? "linux"
      : process.platform;
const nativeName = targetPlatform === "win32"
  ? "microblocks_qol_native.dll"
  : targetPlatform === "darwin"
    ? "libmicroblocks_qol_native.dylib"
    : "libmicroblocks_qol_native.so";
const nativeFolder = targetPlatform === "win32"
  ? "lib-win-x64"
  : targetPlatform === "darwin"
    ? "lib-osx"
    : "lib-linux";
const cargoTargetRoot = resolve(root, process.env.CARGO_TARGET_DIR ?? "target");
const nativeDirectory = resolve(cargoTargetRoot, ...(target ? [target] : []), "release");
const nativeOutput = resolve(nativeDirectory, nativeName);

const run = (command, args, env = process.env) => {
  const result = spawnSync(command, args, {
    cwd: root,
    stdio: "inherit",
    shell: false,
    env,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
};

const wantsFfmpeg = !disableFfmpeg;
if (wantsFfmpeg && targetPlatform !== process.platform) {
  throw new Error("The FFmpeg-enabled native library must be built on its target operating system");
}
const ffmpeg = wantsFfmpeg ? await ensureQolFfmpeg(root) : null;
const libclangDirectory = ffmpeg ? findLibclangDirectory() : "";
const nativeEnv = ffmpeg
  ? {
      ...process.env,
      FFMPEG_DIR: ffmpeg.root,
      ...(libclangDirectory ? { LIBCLANG_PATH: libclangDirectory } : {}),
      PATH: `${ffmpeg.bin}${delimiter}${process.env.PATH ?? ""}`,
      RUSTFLAGS: [
        process.env.RUSTFLAGS ?? "",
        targetPlatform === "darwin"
          ? "-C link-arg=-Wl,-rpath,@loader_path"
          : targetPlatform === "linux"
            ? "-C link-arg=-Wl,-rpath,$ORIGIN"
            : "",
      ].filter(Boolean).join(" "),
    }
  : process.env;
const cargoArguments = ["build", "-q", "-p", "microblocks-qol-native", "--release", "--locked"];
if (target) cargoArguments.push("--target", target);
if (ffmpeg) cargoArguments.push("--features", "ffmpeg");
run(
  "cargo",
  cargoArguments,
  nativeEnv,
);
if (target?.endsWith("-unknown-linux-musl") && !existsSync(nativeOutput)) {
  const staticLibrary = resolve(nativeDirectory, "libmicroblocks_qol_native.a");
  if (!existsSync(staticLibrary)) {
    throw new Error(`The musl static library was not produced at ${staticLibrary}`);
  }
  run(process.env.MUSL_CC ?? "musl-gcc", [
    "-shared",
    "-Wl,--gc-sections",
    "-Wl,-soname,libmicroblocks_qol_native.so",
    "-o",
    nativeOutput,
    "-Wl,--whole-archive",
    staticLibrary,
    "-Wl,--no-whole-archive",
    "-ldl",
    "-lpthread",
    "-lm",
  ], nativeEnv);
}
if (!existsSync(nativeOutput)) {
  throw new Error(`The native library was not produced at ${nativeOutput}`);
}
run("dotnet", ["build", resolve(root, "Source/MicroblocksQolUtils.csproj"), "-c", "Release"]);
rmSync(output, { recursive: true, force: true });
mkdirSync(resolve(output, "Code"), { recursive: true });
cpSync(dll, resolve(output, "Code/MicroblocksQolUtils.dll"));
for (const dependency of [
  "Dia2Lib.dll",
  "Microsoft.Diagnostics.FastSerialization.dll",
  "Microsoft.Diagnostics.NETCore.Client.dll",
  "Microsoft.Diagnostics.Tracing.TraceEvent.dll",
  "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
  "Microsoft.Extensions.Logging.Abstractions.dll",
  "System.Collections.Immutable.dll",
  "System.IO.Pipelines.dll",
  "System.Reflection.Metadata.dll",
  "System.Text.Encodings.Web.dll",
  "System.Text.Json.dll",
  "TraceReloggerLib.dll",
]) {
  const source = resolve(managedOutput, dependency);
  if (existsSync(source)) cpSync(source, resolve(output, "Code", dependency));
}
const packagedNativeDirectory = resolve(output, "Code", nativeFolder);
mkdirSync(packagedNativeDirectory, { recursive: true });
cpSync(nativeOutput, resolve(packagedNativeDirectory, nativeName));
if (ffmpeg) {
  for (const dependency of ffmpeg.runtimeLibraries) {
    copyFileSync(dependency, resolve(packagedNativeDirectory, dependency.split(/[\\/]/u).at(-1)));
  }
  cpSync(ffmpeg.license, resolve(output, "Code", "FFmpeg-LICENSE.txt"));
  for (const license of ffmpeg.additionalLicenses) {
    cpSync(license, resolve(output, "Code", basename(license)));
  }
}
for (const path of ["everest.yaml", "Dialog", "Graphics"]) {
  const source = resolve(root, path);
  if (!existsSync(source)) continue;
  const target = resolve(output, path);
  mkdirSync(dirname(target), { recursive: true });
  cpSync(source, target, { recursive: true });
}
console.log(`Built ${output}`);
rmSync(archive, { force: true });
const packageEntries = readdirSync(output).sort();
const packaged = targetPlatform === "win32"
  ? spawnSync("tar", ["-a", "-cf", archive, "-C", output, ...packageEntries], {
      cwd: root,
      stdio: "inherit",
      shell: false,
    })
  : spawnSync("zip", ["-q", "-r", archive, ...packageEntries], {
      cwd: output,
      stdio: "inherit",
      shell: false,
    });
if (packaged.error) throw packaged.error;
if (packaged.status !== 0) throw new Error("Cannot create the Everest mod archive");
const archiveBytes = statSync(archive).size;
console.log(`Packaged ${archive} (${(archiveBytes / 1024 / 1024).toFixed(2)} MiB)`);

if (process.argv.includes("--install")) {
  if (!existsSync(resolve(celesteRoot, "Celeste.dll"))) {
    throw new Error(`Celeste.dll was not found under ${celesteRoot}`);
  }
  if (process.platform === "win32") {
    const running = spawnSync(
      "tasklist.exe",
      ["/FI", "IMAGENAME eq Celeste.exe", "/FO", "CSV", "/NH"],
      { encoding: "utf8", windowsHide: true },
    );
    if (running.status === 0 && running.stdout.toLowerCase().includes('"celeste.exe"')) {
      throw new Error("Celeste is running; close it before replacing the installed mod");
    }
  }
  const modsRoot = resolve(celesteRoot, "Mods");
  const installedDirectory = resolve(modsRoot, "MicroblocksQolUtils");
  const installedArchive = resolve(modsRoot, "MicroblocksQolUtils.zip");
  if (dirname(installedDirectory) !== modsRoot || basename(installedDirectory) !== "MicroblocksQolUtils"
    || dirname(installedArchive) !== modsRoot || basename(installedArchive) !== "MicroblocksQolUtils.zip") {
    throw new Error(`Refusing to replace unexpected install targets under ${modsRoot}`);
  }
  mkdirSync(modsRoot, { recursive: true });
  rmSync(installedDirectory, { recursive: true, force: true });
  copyFileSync(archive, installedArchive);
  // Never change the user's graphics backend when installing a capture consumer.
  // Remove only the exact block added by ABI 5's installer, preserving user overrides.
  const launchPath = resolve(celesteRoot, "everest-launch.txt");
  if (existsSync(launchPath)) {
    const launch = readFileSync(launchPath, "utf8");
    const restored = launch.replace(/\r?\n# MicroblocksQolUtils shared SDL capture requires OpenGL\.\r?\n--graphics OpenGL\r?\n/gu, "\n");
    if (restored !== launch) {
      const backup = resolve(root, ".work", `everest-launch-before-restore-${Date.now()}.txt`);
      mkdirSync(dirname(backup), { recursive: true });
      writeFileSync(backup, launch);
      writeFileSync(launchPath, restored);
      console.log(`Removed the old capture-only OpenGL override; backed up ${backup}`);
    }
  }
  console.log(`Installed ${installedArchive}`);
}
