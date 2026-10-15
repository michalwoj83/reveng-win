<#
.SYNOPSIS
    Builds and runs the ReaWin development build (Debug by default).

.PARAMETER Configuration
    Build configuration: Debug (default) or Release.

.PARAMETER NoBuild
    Skip the build step and run the last build output.

.EXAMPLE
    ./scripts/run-rea-win.ps1
    ./scripts/run-rea-win.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot '..\src\ReaWin\ReaWin.csproj' | Resolve-Path

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK not found on PATH. Install the .NET 8 SDK.'
}

if (-not $NoBuild) {
    Write-Host "Building ReaWin ($Configuration)..." -ForegroundColor Cyan
    dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit code $LASTEXITCODE)." }
}

Write-Host "Starting ReaWin ($Configuration)..." -ForegroundColor Cyan
dotnet run --project $project -c $Configuration --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw "ReaWin exited with code $LASTEXITCODE." }
