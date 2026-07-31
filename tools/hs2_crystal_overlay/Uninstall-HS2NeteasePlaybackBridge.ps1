[CmdletBinding()]
param(
    [string]$TaskName = "HS2 Netease Playback Bridge"
)

$ErrorActionPreference = "Stop"
$installRoot = Join-Path $env:LOCALAPPDATA `
    "HS2.CrystalOverlay\NeteasePlaybackBridge"
$installedExe = Join-Path $installRoot `
    "HS2.NeteasePlaybackBridge.exe"

$task = Get-ScheduledTask `
    -TaskName $TaskName `
    -ErrorAction SilentlyContinue
if ($task) {
    Stop-ScheduledTask `
        -TaskName $TaskName `
        -ErrorAction SilentlyContinue
    Unregister-ScheduledTask `
        -TaskName $TaskName `
        -Confirm:$false
}

Get-Process -Name "HS2.NeteasePlaybackBridge" `
    -ErrorAction SilentlyContinue |
    Stop-Process -Force

$localRoot = (
    [IO.Path]::GetFullPath($env:LOCALAPPDATA)).TrimEnd('\') + '\'
$resolvedInstall = [IO.Path]::GetFullPath($installRoot)
if (-not $resolvedInstall.StartsWith(
        $localRoot,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove a path outside LocalAppData."
}

if (Test-Path -LiteralPath $resolvedInstall) {
    Remove-Item -LiteralPath $resolvedInstall -Recurse -Force
}
