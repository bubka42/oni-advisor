# Builds the mod, MapQuery and the companion, and copies the mod into the game's Dev mods folder.
# Close the game first. Usage:  powershell -File tools\deploy.ps1   (from the repo root)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

if (Get-Process -Name OxygenNotIncluded -ErrorAction SilentlyContinue) {
    Write-Warning 'Oxygen Not Included is running; the mod DLL may be locked. Close the game and run again.'
}

& $dotnet build (Join-Path $repo 'src\OniAdvisor\OniAdvisor.csproj') -c Release -p:Deploy=true
& $dotnet build (Join-Path $repo 'tools\MapQuery\MapQuery.csproj') -c Release
& $dotnet build (Join-Path $repo 'tools\Companion\Companion.csproj') -c Release

Write-Host ''
Write-Host 'Done. Start the game, enable "ONI Advisor" in Mods (first time only), restart the game.'
Write-Host ('Companion: ' + (Join-Path $repo 'tools\run-companion.cmd'))
