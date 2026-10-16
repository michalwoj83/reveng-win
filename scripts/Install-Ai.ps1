<#
.SYNOPSIS
    Installs the local AI the app uses to describe functions: the pinned PrismML llama.cpp build and the Ternary Bonsai 2 27B model.

.DESCRIPTION
    Puts two things where the app looks for them (%LocalAppData%\ReverseEngineerAI\ai by default):
      ai\llama-server.exe (+ DLLs)   the PrismML fork of llama.cpp. Stock llama.cpp cannot load this model.
      ai\model\default.gguf          Ternary-Bonsai-2-27B PTQ1_0, about 5.9 GB.
    Every download is pinned (URL and SHA-256 in ai-pins.json) and verified; nothing is run by this script.
    The bundling and verification are done by the shared installer kit (ai-tooling/windows/llm-installer).

    The model is not downloaded again when a verified copy already exists on this PC. In this order the script looks at:
      1. a file given with -ModelFile;
      2. the Hugging Face cache (HF_HUB_CACHE, HUGGINGFACE_HUB_CACHE, HF_HOME\hub, XDG_CACHE_HOME\huggingface\hub, then
         %USERPROFILE%\.cache\huggingface\hub), where the app's Setup and any Hugging Face tool keep it;
      3. the model of an installed copy of the app (%LocalAppData%\Programs\Reverse.Engineer.AI\ai\model\default.gguf);
      4. this script's own download folder (<ai folder>\..\cache\model).
    A copy is used only after its SHA-256 matches the pin. It is hard-linked into the ai folder when the drive allows it (one copy on
    disk), otherwise copied. Only when none is found is the model downloaded.

    Engine choices (-Engine):
      (default)   CUDA 13.3 build: NVIDIA GPU, newest cards (RTX 50 series). About 0.6 GB of downloads.
      cuda-12.4   CUDA 12.4 build: the maker's fallback on Windows if the 13.3 build exits without a message.
      cpu         CPU only: works everywhere, but a 27B model is slow on the CPU.
      vulkan      Vulkan build (see the model's known issues before choosing it).

    Afterwards open Settings in the app and turn AI on.

.PARAMETER Engine       Which engine build to install (see above).
.PARAMETER Destination  The ai folder to fill (default %LocalAppData%\ReverseEngineerAI\ai).
.PARAMETER Kit          The installer kit folder (default: ..\ai-tooling\windows\llm-installer next to this repository, or $env:AISLOP_KIT).
.PARAMETER SkipModel    Install only the engine (for example to switch engine without touching the model).
.PARAMETER SkipEngine   Install only the model.
.PARAMETER ModelFile    A .gguf you already have; it must match the pinned SHA-256.
.PARAMETER Pins         The pins file (default scripts\ai-pins.json).

.EXAMPLE
    ./scripts/Install-Ai.ps1
    ./scripts/Install-Ai.ps1 -Engine cuda-12.4
    ./scripts/Install-Ai.ps1 -SkipEngine -ModelFile D:\models\Ternary-Bonsai-2-27B-PTQ1_0.gguf
#>
[CmdletBinding()]
param(
    [ValidateSet("", "cuda-12.4", "cpu", "vulkan")][string]$Engine = "",
    [string]$Destination = (Join-Path $env:LOCALAPPDATA "ReverseEngineerAI\ai"),
    [string]$Kit = "",
    [switch]$SkipModel,
    [switch]$SkipEngine,
    [string]$ModelFile = "",
    [string]$Pins = ""
)
$ErrorActionPreference = "Stop"
$pinsFile = if ($Pins) { (Resolve-Path $Pins).Path } else { Join-Path $PSScriptRoot "ai-pins.json" }
$repoRoot = Split-Path $PSScriptRoot -Parent

if (-not $Kit) {
    $Kit = if ($env:AISLOP_KIT) { $env:AISLOP_KIT } else { Join-Path (Split-Path $repoRoot -Parent) "ai-tooling\windows\llm-installer" }
}
if (-not $SkipEngine -and -not (Test-Path (Join-Path $Kit "scripts\Bundle-LlamaServer.ps1"))) {
    throw "The installer kit was not found at '$Kit'. Clone https://github.com/michalwoj83/ai-tooling next to this repository, or pass -Kit / set AISLOP_KIT."
}

$pinData = Get-Content $pinsFile -Raw | ConvertFrom-Json   # not $pins: that is the typed parameter, and PowerShell names are case-insensitive
$model = $pinData.models.en
$cache = Join-Path (Split-Path $Destination -Parent) "cache"

# ---- where an already downloaded model may be ---------------------------------------------------------------

function Get-HubDirs {
    # as huggingface_hub resolves it, plus the default location, without repeats
    $dirs = [System.Collections.Generic.List[string]]::new()
    $explicit = @($env:HF_HUB_CACHE, $env:HUGGINGFACE_HUB_CACHE) | Where-Object { $_ } | Select-Object -First 1
    if ($explicit) { $dirs.Add($explicit) }
    else {
        $base = if ($env:HF_HOME) { $env:HF_HOME }
                elseif ($env:XDG_CACHE_HOME) { Join-Path $env:XDG_CACHE_HOME "huggingface" }
                else { Join-Path $env:USERPROFILE ".cache\huggingface" }
        $dirs.Add((Join-Path $base "hub"))
    }
    $default = Join-Path $env:USERPROFILE ".cache\huggingface\hub"
    if (-not ($dirs | Where-Object { [string]::Equals($_, $default, [StringComparison]::OrdinalIgnoreCase) })) { $dirs.Add($default) }
    $dirs
}

function Get-ModelCandidates($m) {
    # (label, path) in the order they are tried
    $found = [System.Collections.Generic.List[object]]::new()
    if ($ModelFile) { $found.Add(@{ Label = "the file given with -ModelFile"; Path = $ModelFile; Explicit = $true }) }

    if ($m.url -match '^https://huggingface\.co/(?<repo>[^/]+/[^/]+)/resolve/(?<rev>[0-9a-f]{40})/(?<file>[^?#]+)$') {
        $repoDir = "models--" + ($Matches.repo -replace '/', '--')
        foreach ($hub in Get-HubDirs) {
            $found.Add(@{ Label = "the Hugging Face cache ($hub)"; Path = (Join-Path $hub "$repoDir\blobs\$($m.sha256.ToLower())") })
            $found.Add(@{ Label = "the Hugging Face cache ($hub)"; Path = (Join-Path $hub "$repoDir\snapshots\$($Matches.rev)\$($Matches.file -replace '/', '\')") })
        }
    }
    $found.Add(@{ Label = "the installed app"; Path = (Join-Path $env:LOCALAPPDATA "Programs\Reverse.Engineer.AI\ai\model\default.gguf") })
    $found.Add(@{ Label = "this script's download folder"; Path = (Join-Path $cache "model\en.gguf") })
    $found
}

function Find-VerifiedModel($m) {
    $expected = $m.sha256.ToUpper()
    $checked = @{}
    foreach ($c in Get-ModelCandidates $m) {
        if (-not $c.Path -or -not (Test-Path -LiteralPath $c.Path -PathType Leaf)) {
            # a file the user named must be usable: silently installing some other copy instead would hide the mistake
            if ($c.Explicit) { throw "The file given with -ModelFile does not exist: $($c.Path)" }
            continue
        }
        $full = (Resolve-Path -LiteralPath $c.Path).Path
        if ($checked.ContainsKey($full)) { continue }
        $checked[$full] = $true
        if ((Get-Item -LiteralPath $full).Length -ne [long]$m.size) {
            if ($c.Explicit) { throw "The file given with -ModelFile is not the pinned model (its size is not $($m.size) bytes): $full" }
            Write-Host "Skipping $full ($($c.Label)): its size is not the pinned $($m.size) bytes."
            continue
        }
        Write-Host "Found a model in $($c.Label); checking its SHA-256 (about half a minute)..."
        if ((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -eq $expected) { return @{ Path = $full; Label = $c.Label } }
        if ($c.Explicit) { throw "The file given with -ModelFile does not match the pinned SHA-256, so it is not the pinned model: $full" }
        Write-Host "  It does not match the pin, so it is not used: $full"
    }
    $null
}

function Install-ModelFile($source, $target) {
    New-Item -ItemType Directory (Split-Path $target -Parent) -Force | Out-Null
    if (Test-Path -LiteralPath $target) { [IO.File]::Delete($target) }
    try {
        # one copy on disk: the ai folder gets another name for the same bytes (needs NTFS and the same drive)
        New-Item -ItemType HardLink -Path $target -Target $source -ErrorAction Stop | Out-Null
        Write-Host "Linked the model into place (no second copy on disk)."
    }
    catch {
        Copy-Item -LiteralPath $source -Destination $target -Force
        Write-Host "Copied the model into place (a link was not possible: $($_.Exception.Message))."
    }
}

Write-Host "Engine : $(if ($SkipEngine) { 'skipped' } elseif ($Engine) { $pinData.llamaAlternatives.$Engine.name } else { $pinData.llama.name })"
Write-Host "Model  : $($model.name) ($([math]::Round($model.size / 1e9, 2)) GB)"
Write-Host "Target : $Destination"
Write-Host ""

# 1. the engine (and its CUDA runtime) into <ai>
if (-not $SkipEngine) {
    $bundleArgs = @{ Pins = $pinsFile; Destination = $Destination; CacheDir = $cache }
    if ($Engine) { $bundleArgs.Engine = $Engine }
    & (Join-Path $Kit "scripts\Bundle-LlamaServer.ps1") @bundleArgs
}

# 2. the model into <ai>\model\default.gguf: kept if it already has the pinned hash, else reused from a copy found on this PC,
#    else downloaded
if (-not $SkipModel) {
    $target = Join-Path $Destination "model\default.gguf"
    $expected = $model.sha256.ToUpper()
    if ((Test-Path $target) -and (Get-FileHash $target -Algorithm SHA256).Hash -eq $expected) {
        Write-Host "Model already installed with the pinned hash."
    } elseif ($existing = Find-VerifiedModel $model) {
        Install-ModelFile $existing.Path $target
        Write-Host "Model installed from $($existing.Label)."
    } else {
        Write-Host "No verified copy of the model found on this PC; downloading it."
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
