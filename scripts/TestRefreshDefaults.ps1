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
    "tools\turzx_side_screen\InstallStartupTask-Admin.ps1",
    "tools\turzx_side_screen\RestartSideScreenAfterResume.ps1",
    "tools\turzx_side_screen\StartVideoStreamBackground.ps1"
)

foreach ($relative in $defaultFiles) {
    $path = Join-Path $Root $relative
    $text = Get-Content -Raw -LiteralPath $path
    if ($text -notmatch [regex]::Escape('[int]$IntervalMs = 3000')) {
        throw "Verified full-frame panel refresh default should be 3000ms in $relative"
    }
}

$hiddenLaunchers = @(
    "tools\turzx_side_screen\StartSideScreenWatchdog-Hidden.vbs",
    "tools\turzx_side_screen\RestartSideScreenAfterResume-Hidden.vbs"
)

foreach ($relative in $hiddenLaunchers) {
    $path = Join-Path $Root $relative
    $text = Get-Content -Raw -LiteralPath $path
    if ($text -notmatch [regex]::Escape('intervalMs = "3000"')) {
        throw "Hidden launcher must preserve the safe 3000ms full-frame interval: $relative"
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
        $text -match [regex]::Escape("-IntervalMs 1000") -or
        $text -match [regex]::Escape("0.5s updates") -or
        $text -match [regex]::Escape('Default refresh: `500ms`') -or
        $text -match [regex]::Escape('Default refresh: `1000ms`')) {
        throw "Public entry or docs still advertise an unsafe saturated panel refresh: $relative"
    }
}

$config = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $Root "tools\turzx_side_screen\config.json") | ConvertFrom-Json
if ([int]$config.screen.dataRefreshMs -ne 1000 -or [int]$config.metrics.pollMs -ne 1000) {
    throw "Metrics sampling must remain 1000ms even though verified panel transfers are paced at 3000ms."
}
if ([int]$config.ui.maxDiskRows -ne 4) {
    throw "Runtime config must cap the physical-disk UI at four rows."
}

Write-Host "Refresh default checks completed."
