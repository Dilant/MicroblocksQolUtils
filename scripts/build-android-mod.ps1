param([string]$Ndk = $env:NDK_HOME, [string]$CargoPath = 'cargo', [string]$Output = '.work/MicroblocksQolUtils-android-arm64.zip')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$Ndk) { throw 'Set NDK_HOME or pass -Ndk with an Android NDK directory.' }
$toolchain = Join-Path $Ndk 'toolchains/llvm/prebuilt/windows-x86_64/bin'
$clang = Join-Path $toolchain 'aarch64-linux-android28-clang.cmd'
if (!(Test-Path -LiteralPath $clang)) { throw "Android ARM64 compiler not found: $clang" }
$env:CC_aarch64_linux_android = $clang
$env:AR_aarch64_linux_android = Join-Path $toolchain 'llvm-ar.exe'
$env:CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER = $clang
$env:CARGO_TARGET_DIR = Join-Path $repo '.work/android-target'
Push-Location $repo
try {
  & $CargoPath build -p microblocks-qol-native --release --locked --target aarch64-linux-android
  if ($LASTEXITCODE) { throw "Android native build failed: $LASTEXITCODE" }
  dotnet build "$repo/Source/MicroblocksQolUtils.csproj" -c Release
  if ($LASTEXITCODE) { throw "Managed build failed: $LASTEXITCODE" }
  $stage = Join-Path $repo ('.work/android-package-' + [Guid]::NewGuid().ToString('N'))
  New-Item (Join-Path $stage 'Code/lib-linux') -ItemType Directory -Force | Out-Null
  Copy-Item "$repo/Source/bin/Release/net8.0/*.dll" (Join-Path $stage 'Code')
  Copy-Item "$env:CARGO_TARGET_DIR/aarch64-linux-android/release/libmicroblocks_qol_native.so" (Join-Path $stage 'Code/lib-linux')
  Copy-Item "$repo/everest.yaml" $stage
  Copy-Item "$repo/Dialog" $stage -Recurse
  $archive = [IO.Path]::GetFullPath((Join-Path $repo $Output))
  New-Item ([IO.Path]::GetDirectoryName($archive)) -ItemType Directory -Force | Out-Null
  Compress-Archive -Path "$stage/*" -DestinationPath $archive -Force
  Write-Warning 'Android package has no FFmpeg video encoder; UI, touch input and native font rasterization are included.'
  Write-Output "Built $archive"
} finally { Pop-Location }
