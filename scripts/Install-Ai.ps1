<#
.SYNOPSIS
    Installs the local AI the app uses to describe functions: the pinned PrismML llama.cpp build and the Ternary Bonsai 2 27B model.

.DESCRIPTION
    Puts two things where the app looks for them (%LocalAppData%\ReverseEngineerAI\ai by default):
      ai\llama-server.exe (+ DLLs)   the PrismML fork of llama.cpp. Stock llama.cpp cannot load this model.
      ai\model\default.gguf          Ternary-Bonsai-2-27B PTQ1_0, about 5.9 GB.
    Every download is pinned (URL and SHA-256 in ai-pins.json) and verified; nothing is run by this script.
    The bundling and verification are done by the shared installer kit (ai-tooling/windows/llm-installer).

    Engine choices (-Engine):
      (default)   CUDA 13.3 build: NVIDIA GPU, newest cards (RTX 50 series). About 0.6 GB of downloads.
      cuda-12.4   CUDA 12.4 build: the maker's fallback on Windows if the 13.3 build exits without a message.
      cpu         CPU only: works everywhere, but a 27B model is slow on the CPU.
      vulkan      Vulkan build (see the model's known issues before choosing it).

    Afterwards open Settings in the app and turn AI on.

.PARAMETER Engine       Which engine build to install (see above).
.PARAMETER Destination  The ai folder to fill (default %LocalAppData%\ReverseEngineerAI\ai).
.PARAMETER Kit          The installer kit folder (default: ..\ai-tooling\windows\llm-installer next to this repository, or $env:AISLOP_KIT).
.PARAMETER SkipModel    Install only the engine (for example to switch engine without downloading the model again).

.EXAMPLE
    ./scripts/Install-Ai.ps1
    ./scripts/Install-Ai.ps1 -Engine cuda-12.4
#>
[CmdletBinding()]
param(
    [ValidateSet("", "cuda-12.4", "cpu", "vulkan")][string]$Engine = "",
    [string]$Destination = (Join-Path $env:LOCALAPPDATA "ReverseEngineerAI\ai"),
    [string]$Kit = "",
    [switch]$SkipModel
)
$ErrorActionPreference = "Stop"
$pinsFile = Join-Path $PSScriptRoot "ai-pins.json"
$repoRoot = Split-Path $PSScriptRoot -Parent

if (-not $Kit) {
    $Kit = if ($env:AISLOP_KIT) { $env:AISLOP_KIT } else { Join-Path (Split-Path $repoRoot -Parent) "ai-tooling\windows\llm-installer" }
}
if (-not (Test-Path (Join-Path $Kit "scripts\Bundle-LlamaServer.ps1"))) {
    throw "The installer kit was not found at '$Kit'. Clone https://github.com/michalwoj83/ai-tooling next to this repository, or pass -Kit / set AISLOP_KIT."
}

$pins = Get-Content $pinsFile -Raw | ConvertFrom-Json
$model = $pins.models.en
$cache = Join-Path (Split-Path $Destination -Parent) "cache"

Write-Host "Engine : $(if ($Engine) { $pins.llamaAlternatives.$Engine.name } else { $pins.llama.name })"
Write-Host "Model  : $($model.name) ($([math]::Round($model.size / 1GB, 2)) GB)"
Write-Host "Target : $Destination"
Write-Host ""

# 1. the engine (and its CUDA runtime) into <ai>
$bundleArgs = @{ Pins = $pinsFile; Destination = $Destination; CacheDir = $cache }
if ($Engine) { $bundleArgs.Engine = $Engine }
& (Join-Path $Kit "scripts\Bundle-LlamaServer.ps1") @bundleArgs

# 2. the model into <ai>\model\default.gguf (kept if it already has the pinned hash)
if (-not $SkipModel) {
    $target = Join-Path $Destination "model\default.gguf"
    $expected = $model.sha256.ToUpper()
    if ((Test-Path $target) -and (Get-FileHash $target -Algorithm SHA256).Hash -eq $expected) {
        Write-Host "Model already installed with the pinned hash."
    } else {
        $modelCache = Join-Path $cache "model"
        & (Join-Path $Kit "scripts\Get-Models.ps1") -Pins $pinsFile -OutDir $modelCache -Language en
        New-Item -ItemType Directory (Split-Path $target -Parent) -Force | Out-Null
        Move-Item (Join-Path $modelCache "en.gguf") $target -Force
        Write-Host "Model installed."
    }
}

Write-Host ""
Write-Host "Done. Check the engine with:  & '$Destination\llama-server.exe' --version"
Write-Host "          and the GPU with:   & '$Destination\llama-server.exe' --list-devices"
Write-Host "Then open Settings in Reverse.Engineer.AI and turn AI on."
