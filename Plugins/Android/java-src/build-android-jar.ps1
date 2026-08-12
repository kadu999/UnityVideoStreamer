param(
    [string]$AndroidSdk = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDir = Join-Path $scriptDir "build"
$jarDest = Join-Path $scriptDir "..\VideoStreamEncoder.jar"

if (-not $AndroidSdk) {
    if ($env:ANDROID_HOME) {
        $AndroidSdk = $env:ANDROID_HOME
    } elseif ($env:ANDROID_SDK_ROOT) {
        $AndroidSdk = $env:ANDROID_SDK_ROOT
    } else {
        $localProperties = Join-Path $scriptDir "..\..\..\..\VideoStreaming\android\local.properties"
        if (Test-Path $localProperties) {
            $line = Get-Content $localProperties | Where-Object { $_ -match '^sdk\.dir=' } | Select-Object -First 1
            if ($line) {
                $AndroidSdk = $line -replace '^sdk\.dir=', ''
                $AndroidSdk = $AndroidSdk.Trim().Trim('"', "'")
            }
        }
    }
}

if (-not $AndroidSdk -or -not (Test-Path $AndroidSdk)) {
    Write-Error "Android SDK not found. Pass -AndroidSdk or set ANDROID_HOME."
}

$androidJar = Get-ChildItem -Path (Join-Path $AndroidSdk "platforms") -Filter "android.jar" -Recurse -ErrorAction SilentlyContinue |
    Sort-Object {
        $v = $_.Directory.Name -replace 'android-', ''
        if ($v -match '\.') { [version]$v } else { [version]"$v.0" }
    } |
    Select-Object -Last 1

if (-not $androidJar) {
    Write-Error "android.jar not found under $AndroidSdk"
}

$sources = Get-ChildItem -Path (Join-Path $scriptDir "com") -Filter "*.java" -Recurse | Select-Object -ExpandProperty FullName
if (-not $sources) {
    Write-Error "No Java sources found"
}

$classesDir = Join-Path $buildDir "classes"
New-Item -ItemType Directory -Force -Path $classesDir | Out-Null

& javac -encoding utf8 -source 1.8 -target 1.8 `
    -bootclasspath $androidJar.FullName `
    -d $classesDir `
    $sources

if ($LASTEXITCODE -ne 0) {
    Write-Error "javac failed"
}

Push-Location $classesDir
try {
    & jar cf $jarDest com/videostream/stream/*.class
} finally {
    Pop-Location
}

if ($LASTEXITCODE -ne 0) {
    Write-Error "jar failed"
}

Remove-Item -LiteralPath $buildDir -Recurse -Force
Write-Host "BUILD OK -> $jarDest"
