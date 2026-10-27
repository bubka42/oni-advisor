# Waits for the next request file from the OniAdvisor mod, prints it, and exits.
# Run in the background from Claude Code; the exit wakes the advisor.
param(
    [string]$LiveDir = "$env:USERPROFILE\OneDrive\Documents\Klei\OxygenNotIncluded\advisor\live"
)
$req = Join-Path $LiveDir 'requests'
New-Item -ItemType Directory -Force $req | Out-Null
$start = Get-Date
while ($true) {
    $new = Get-ChildItem $req -Filter *.json -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -gt $start } |
        Sort-Object LastWriteTime | Select-Object -First 1
    if ($new) {
        "REQUEST: $($new.FullName)"
        Get-Content $new.FullName
        exit 0
    }
    Start-Sleep -Seconds 3
}
