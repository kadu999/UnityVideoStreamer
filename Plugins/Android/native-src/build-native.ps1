param(
    [string]$UnityEditor = "C:\Program Files\Unity\Hub\Editor\6000.3.19f1\Editor",
    [switch]$OnlyArm64
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$pluginApi = Join-Path $UnityEditor "Data\PluginAPI"
$ndk = Join-Path $UnityEditor "Data\PlaybackEngines\AndroidPlayer\NDK"
$toolchain = Join-Path $ndk "toolchains\llvm\prebuilt\windows-x86_64"

if (-not (Test-Path $pluginApi)) {
    Write-Error "Unity Plugin API not found under $pluginApi"
}
if (-not (Test-Path $toolchain)) {
    Write-Error "NDK toolchain not found under $toolchain"
}

$source = Join-Path $scriptDir "UnityVideoStreamerNative.cpp.in"
$udpSource = Join-Path $scriptDir "VideoStreamNativeUdp.cpp"
$targets = @(
    @{ Abi = "arm64-v8a"; Clang = "aarch64-linux-android24-clang++.cmd" },
    @{ Abi = "armeabi-v7a"; Clang = "armv7a-linux-androideabi24-clang++.cmd" },
    @{ Abi = "x86_64"; Clang = "x86_64-linux-android24-clang++.cmd" }
)

if ($OnlyArm64) {
    $targets = $targets | Where-Object { $_.Abi -eq "arm64-v8a" }
}

foreach ($target in $targets) {
    $clang = Join-Path $toolchain "bin\$($target.Clang)"
    if (-not (Test-Path $clang)) {
        Write-Error "Clang not found: $clang"
    }

    $outDir = Join-Path $scriptDir "..\libs\$($target.Abi)"
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $out = Join-Path $outDir "libunity-video-streamer-native.so"

    & $clang -std=c++17 -x c++ -fPIC -shared -O2 -Wall `
        "-I$pluginApi" `
        $source `
        $udpSource `
        -o $out `
        -landroid -llog -lEGL -lGLESv3 `
        "-Wl,-soname,libunity-video-streamer-native.so"

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed for $($target.Abi)"
    }

    Write-Host "BUILD OK -> $out"
}
