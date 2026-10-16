<#
.SYNOPSIS
  Builds Setup.exe: publishes the app, bundles the pinned PrismML llama-server (no model) and compiles the Inno Setup script.
  The model is not bundled; Setup downloads it (pinned URL + SHA-256 from scripts/ai-pins.json) and will not finish without it.

.PARAMETER Version      Defaults to <Version> in src/ReaWin/ReaWin.csproj.
.PARAMETER Engine       Which engine build to bundle: empty = CUDA 13.3; cuda-12.4, cpu or vulkan (see scripts/ai-pins.json).
.PARAMETER Kit          The installer kit folder (default: $env:AISLOP_KIT, or ..\ai-tooling\windows\llm-installer next to this repository).
.PARAMETER CacheDir     Where engine archives are cached (default: artifacts\cache).
.PARAMETER Pins         Pins file (default scripts\ai-pins.json); a test pins file can point at a fake model.
.PARAMETER Iscc         Path to ISCC.exe; found automatically when omitted.
.PARAMETER SkipPublish  Reuse artifacts\app from the last build.

.EXAMPLE
  ./installer/build-installer.ps1
  ./installer/build-installer.ps1 -Engine cuda-12.4
#>
param(
  [string]$Version = "",
  [ValidateSet("", "cuda-12.4", "cpu", "vulkan")][string]$Engine = "",
  [string]$Kit = "",
  [string]$CacheDir = "",
  [string]$Pins = "",
  [string]$Iscc = "",
  [switch]$SkipPublish
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repo "artifacts"
$appDir = Join-Path $out "app"
if (-not $CacheDir) { $CacheDir = Join-Path $out "cache" }
if (-not $Pins) { $Pins = Join-Path $repo "scripts\ai-pins.json" }
$csproj = Join-Path $repo "src\ReaWin\ReaWin.csproj"
if (-not $Version) { $Version = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not $Version) { throw "No <Version> in $csproj; pass -Version." }

if (-not $Kit) {
  $Kit = if ($env:AISLOP_KIT) { $env:AISLOP_KIT } else { Join-Path (Split-Path $repo -Parent) "ai-tooling\windows\llm-installer" }
}
if (-not (Test-Path (Join-Path $Kit "scripts\Bundle-LlamaServer.ps1"))) {
  throw "The installer kit was not found at '$Kit'. Clone https://github.com/michalwoj83/ai-tooling next to this repository, or pass -Kit / set AISLOP_KIT."
}

# the tools, found up front so a missing one fails before anything is downloaded or built
if (-not $Iscc) {
  $Iscc = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
  ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
  if (-not $Iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install it with: winget install JRSoftware.InnoSetup  (or pass -Iscc <path>)" }
}
if (-not $SkipPublish -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw ".NET 8 SDK (dotnet) not found. Install it with: winget install Microsoft.DotNet.SDK.8"
}

# 1. the app: self-contained, so the user installs no .NET runtime
if (-not $SkipPublish) {
  if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
  dotnet publish (Join-Path $repo "src\ReaWin") -c Release -r win-x64 --self-contained true -p:Version=$Version -o $appDir
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}
if (-not (Test-Path (Join-Path $appDir "ReaWin.exe"))) { throw "$appDir\ReaWin.exe not found." }

# 2. the pinned PrismML llama-server (and CUDA runtime) into <app>\ai, verified by SHA-256; no model
$bundle = @{ Pins = $Pins; Destination = (Join-Path $appDir "ai"); CacheDir = $CacheDir }
if ($Engine) { $bundle.Engine = $Engine }
& (Join-Path $Kit "scripts\Bundle-LlamaServer.ps1") @bundle

# 3. Setup.exe (English only: the pins have no "pl" model, so no Polish defines)
$defines = @(& (Join-Path $Kit "scripts\Get-IsccDefines.ps1") -Pins $Pins)
$isccArgs = @("/DVersion=$Version", "/DAppDir=$appDir", "/DOutDir=$out", "/DKitDir=$Kit", "/DPrerequisitesFile=$(Join-Path $PSScriptRoot 'prerequisites.txt')")
$icon = Join-Path $repo "src\ReaWin\Assets\AppIcon.ico"
if (Test-Path $icon) { $isccArgs += "/DIconFile=$icon" }
& $Iscc @isccArgs @defines (Join-Path $PSScriptRoot "ReaWin.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Write-Host "Built $out\ReverseEngineerAI-Setup-$Version.exe"
