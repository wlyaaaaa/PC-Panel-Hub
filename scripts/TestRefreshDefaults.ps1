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
        throw "The explicit full-frame compatibility fallback should remain 3000ms in $relative"
    }
}

# The installed task must preserve the user's one-second panel contract.  The
# ordinary start entry also defaults to one second when no task exists; when a
# task exists, an omitted switch still adopts the registered task action.
$startText = Get-Content -Raw -LiteralPath (Join-Path $Root "scripts\start.ps1")
if ($startText -notmatch [regex]::Escape('[switch]$HybridRefresh = $true') -or
    $startText -notmatch [regex]::Escape('$PSBoundParameters.ContainsKey("HybridRefresh")')) {
    throw "Start entry must default to one-second HybridRefresh while retaining registered-task mode adoption."
}
$installerText = Get-Content -Raw -LiteralPath (Join-Path $Root "scripts\install-startup-admin.ps1")
if ($installerText -notmatch [regex]::Escape('[switch]$HybridRefresh = $true')) {
    throw "Installed production task must default to one-second HybridRefresh."
}

# The aggregate test runner must recognize every production transport before
# deciding whether it is safe to launch the entity COM test.  Rejecting the
# active hybrid heartbeat can race TestVideoStream.ps1 against the live COM7
# owner when task/process evidence is temporarily unavailable.
$testRunnerText = Get-Content -Raw -LiteralPath (Join-Path $Root "scripts\test.ps1")
foreach ($transport in @("verified_full_200", "hybrid_diff_204_full_200")) {
    if ($testRunnerText -notmatch [regex]::Escape($transport)) {
        throw "Live stream guard must recognize production transport: $transport"
    }
}

# The HS2 full-feature path preserves whichever controller mode the service
# reports.  Startup artifacts must not preselect a mode switch: only a native
# 17104896 controller may enter the watchdog's one-time 30-second promotion.
$hs2StartupEntries = @(
    "scripts\start.ps1",
    "scripts\install-startup-admin.ps1",
    "tools\turzx_side_screen\InstallStartupTask-Admin.ps1",
    "tools\turzx_side_screen\StartSideScreenStack.ps1",
    "tools\turzx_side_screen\StartSideScreenWatchdog-Hidden.vbs"
)
foreach ($relative in $hs2StartupEntries) {
    $path = Join-Path $Root $relative
    $text = Get-Content -Raw -LiteralPath $path
    if ($text -match '(?i)-EnableSecondaryScreen') {
        throw "Startup entry must not force an HS2 controller mode: $relative"
    }
}
$watchdogText = Get-Content -Raw -LiteralPath (Join-Path $Root "tools\turzx_side_screen\StartSideScreenWatchdog.ps1")
foreach ($pattern in @(
        "Set-HS2PreservedActiveState",
        "Set-HS2VerifiedSecondaryState",
        "Invoke-HS2InitialActiveMaintenance",
        "Set-HS2NativeActiveState",
        "HS2SecondaryPromotionGraceSeconds = 30",
        "Get-HS2SecondaryPromotionDecision",
        "mode=one-attempt-per-startup-or-resume-epoch")) {
    if ($watchdogText -notmatch [regex]::Escape($pattern)) {
        throw "HS2 preserved-mode watchdog contract missing: $pattern"
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
        throw "Hidden launcher must preserve the explicit 3000ms full-frame fallback interval: $relative"
    }
    foreach ($pattern in @(
        'hybridRefresh = False',
        'altHelper = False',
        'Case "-nohybridrefresh"',
        'Case "-noalthelper"'
    )) {
        if ($text -notmatch [regex]::Escape($pattern)) {
            throw "Hidden launcher must preserve and explicitly propagate false mode overrides; missing '$pattern' in $relative"
        }
    }
}

$resumeHidden = Get-Content -Raw -LiteralPath (Join-Path $Root "tools\turzx_side_screen\RestartSideScreenAfterResume-Hidden.vbs")
foreach ($pattern in @(
    'If hybridRefresh Then',
    'If altHelper Then'
)) {
    if ($resumeHidden -notmatch [regex]::Escape($pattern)) {
        throw "Resume hidden launcher must explicitly pass both true and false modes; missing '$pattern'"
    }
}

$resumeText = Get-Content -Raw -LiteralPath (Join-Path $Root "tools\turzx_side_screen\RestartSideScreenAfterResume.ps1")
if ($resumeText -notmatch [regex]::Escape('[switch]$HybridRefresh,') -or
    $resumeText -match [regex]::Escape('[switch]$HybridRefresh = $true')) {
    throw "Resume PowerShell worker must retain an explicit false override so the task action remains the mode owner."
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
        throw "Public entry or docs still advertise an unsupported sub-second panel refresh: $relative"
    }
}

foreach ($relative in @("README.md", "docs\startup.md", "docs\architecture.md")) {
    $text = Get-Content -Raw -LiteralPath (Join-Path $Root $relative)
    foreach ($pattern in @("1 Hz", "3-second compatibility fallback", "60, 120, and 180")) {
        if ($text -notmatch [regex]::Escape($pattern)) {
            throw "One-second installed mode contract missing '$pattern' in $relative"
        }
    }
}

$configPath = Join-Path $Root "tools\turzx_side_screen\config.json"
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    $configPath = Join-Path $Root "tools\turzx_side_screen\config.example.json"
}
$config = Get-Content -Raw -Encoding UTF8 -LiteralPath $configPath | ConvertFrom-Json
if ([int]$config.screen.dataRefreshMs -ne 1000 -or [int]$config.metrics.pollMs -ne 1000) {
    throw "Metrics sampling and the installed Hybrid panel cadence must remain 1000ms."
}
if ([int]$config.ui.maxDiskRows -ne 4) {
    throw "Runtime config must cap the physical-disk UI at four rows."
}

Write-Host "Refresh default checks completed."
