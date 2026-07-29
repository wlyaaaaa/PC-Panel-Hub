param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath $Root).Path

$defaultFiles = @(
    "scripts\start.ps1",
    "scripts\install-startup-admin.ps1",
    "scripts\create-desktop-shortcut.ps1",
    "scripts\repair-elevated.ps1",
    "tools\turzx_side_screen\StartSideScreenStack.ps1",
    "tools\turzx_side_screen\StartSideScreenWatchdog.ps1",
    "tools\turzx_side_screen\InstallStartupTask-Admin.ps1"
)

foreach ($relative in $defaultFiles) {
    $path = Join-Path $Root $relative
    $text = Get-Content -Raw -LiteralPath $path
    if ($text -notmatch [regex]::Escape('[int]$IntervalMs = 1000')) {
        throw "Main refresh default should be 1000ms in $relative"
    }
}

$explicitEntries = @(
    "start-side-screen.cmd",
    "scripts\repair-elevated.cmd",
    "README.md",
    "docs\startup.md",
    "docs\architecture.md"
)

foreach ($relative in $explicitEntries) {
    $path = Join-Path $Root $relative
    $text = Get-Content -Raw -LiteralPath $path
    if ($text -match [regex]::Escape("-IntervalMs 500") -or
        $text -match [regex]::Escape("0.5s updates") -or
        $text -match [regex]::Escape('Default refresh: `500ms`')) {
        throw "Public entry or docs still advertise 500ms refresh: $relative"
    }
}

$config = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $Root "tools\turzx_side_screen\config.json") | ConvertFrom-Json
if ([int]$config.screen.dataRefreshMs -ne 1000 -or [int]$config.metrics.pollMs -ne 1000) {
    throw "Runtime config must match the actual 1000ms stream refresh."
}
if ([int]$config.ui.maxDiskRows -ne 4) {
    throw "Runtime config must cap the physical-disk UI at four rows."
}

Write-Host "Refresh default checks completed."
