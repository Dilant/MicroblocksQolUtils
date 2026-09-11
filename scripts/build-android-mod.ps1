param(
  [string]$Output = "Build-Android"
)

# Build the managed Everest module and the portable ARM64 native rasterizer for
# CeleMod's Android host. Android's Everest loader uses the existing lib-linux
# convention, and NativeCaptureBridge supplies the resolver for this folder.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$env:CARGO_HOME) { $env:CARGO_HOME = "$repo/.work/cargo" }
if (!$env:RUSTUP_HOME) { $env:RUSTUP_HOME = "$repo/.work/rustup" }
$ndk = if ($env:NDK_HOME) { $env:NDK_HOME } else { 'G:/CeleMod/.local/android-sdk/ndk/28.2.13676358' }
$clang = Join-Path $ndk 'toolchains/llvm/prebuilt/windows-x86_64/bin/aarch64-linux-android28-clang.cmd'
$env:CC_aarch64_linux_android = $clang
$env:CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER = $clang
$cargo = (Get-Command cargo -ErrorAction Stop).Source
& $cargo build -p microblocks-qol-native --release --locked --target aarch64-linux-android
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet build "$repo/Source/MicroblocksQolUtils.csproj" -c Release
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$destination = Join-Path $repo $Output
Remove-Item $destination -Recurse -Force -ErrorAction SilentlyContinue
New-Item (Join-Path $destination 'Code/lib-linux') -ItemType Directory -Force | Out-Null
Copy-Item "$repo/Source/bin/Release/net8.0/MicroblocksQolUtils.dll" (Join-Path $destination 'Code')
Copy-Item "$repo/target/aarch64-linux-android/release/libmicroblocks_qol_native.so" (Join-Path $destination 'Code/lib-linux')
Copy-Item "$repo/everest.yaml" $destination
Copy-Item "$repo/Dialog" $destination -Recurse
Write-Output "Built Android mod at $destination"
