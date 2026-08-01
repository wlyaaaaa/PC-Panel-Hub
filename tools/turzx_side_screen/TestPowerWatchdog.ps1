param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$side = Join-Path $Root "tools\turzx_side_screen"
$watchdog = Join-Path $side "StartSideScreenWatchdog.ps1"
$shutdownPolicy = Join-Path $side "SideScreenWatchdogPolicy.ps1"
$watchdogLauncher = Join-Path $side "StartSideScreenWatchdog-Hidden.vbs"
$stack = Join-Path $side "StartSideScreenStack.ps1"
$stop = Join-Path $side "StopSideScreenStack.ps1"
$blank = Join-Path $side "SendBlankFrame.ps1"
$resume = Join-Path $side "RestartSideScreenAfterResume.ps1"
$resumeLauncher = Join-Path $side "RestartSideScreenAfterResume-Hidden.vbs"
$installer = Join-Path $Root "scripts\install-startup-admin.ps1"
$installerCmd = Join-Path $Root "scripts\install-startup-admin.cmd"
$start = Join-Path $Root "scripts\start.ps1"

foreach ($path in @($watchdog, $shutdownPolicy, $watchdogLauncher, $stop, $blank)) {
    if (!(Test-Path -LiteralPath $path)) {
        throw "Missing power management script: $path"
    }
}

$watchdogText = Get-Content -Raw -LiteralPath $watchdog
foreach ($pattern in @(
    "Win32_PowerManagementEvent",
    "Win32_ComputerShutdownEvent",
    "TURZXSideScreenShutdown",
    "Stop-OtherWatchdogs",
    "cleared paused flag at watchdog start",
    "EventType = 4",
    "EventType = 7",
    "EventType = 18",
    "MaxConsecutiveFailures",
    "SendBlankFrame.ps1",
    "StopSideScreenStack.ps1",
    "StartSideScreenStack.ps1",
    "-Worker",
    "QuickBlankTimeoutMs",
    "Global\TURZX.SideScreen.Watchdog",
    "duplicate watchdog",
    "HeartbeatStaleSeconds",
    "stream-heartbeat.json",
    "stream-heartbeat-a.json",
    "stream-heartbeat-b.json",
    "heartbeat unhealthy",
    "restart-on-start.flag",
    "restart request detected"
    "Get-TurzxShutdownEventDecision"
    "ShutdownStartupGraceSeconds"
    "HeartbeatStartupGraceSeconds = 60"
)) {
    if ($watchdogText -notmatch [regex]::Escape($pattern)) {
        throw "Watchdog missing expected pattern: $pattern"
    }
}

foreach ($pattern in @(
    'Send-Blank -Reason "shutdown"'
)) {
    if ($watchdogText -notmatch [regex]::Escape($pattern)) {
        throw "Watchdog must blank the panel during confirmed shutdown; missing: $pattern"
    }
}

if ($watchdogText -match [regex]::Escape('Send-Blank -Reason "watchdog-exit"')) {
    throw "Unexpected watchdog exit must not black the panel before Task Scheduler can restart it."
}

. $shutdownPolicy
$watchdogStartedUtc = [DateTime]::Parse("2026-07-23T02:00:00Z").ToUniversalTime()

$logoffDecision = Get-TurzxShutdownEventDecision `
    -EventType 0 `
    -WatchdogStartedUtc $watchdogStartedUtc `
    -NowUtc $watchdogStartedUtc.AddMinutes(10) `
    -StartupGraceSeconds 180
if ($logoffDecision.Action -ne "Ignore" -or $logoffDecision.Reason -ne "logoff") {
    throw "A logoff event must not black the panel or terminate the watchdog."
}

$startupShutdownDecision = Get-TurzxShutdownEventDecision `
    -EventType 1 `
    -WatchdogStartedUtc $watchdogStartedUtc `
    -NowUtc $watchdogStartedUtc.AddSeconds(47) `
    -StartupGraceSeconds 180
if ($startupShutdownDecision.Action -ne "Ignore" -or $startupShutdownDecision.Reason -ne "startup-grace") {
    throw "A shutdown-class event during startup grace must be ignored."
}

$confirmedShutdownDecision = Get-TurzxShutdownEventDecision `
    -EventType 1 `
    -WatchdogStartedUtc $watchdogStartedUtc `
    -NowUtc $watchdogStartedUtc.AddMinutes(5) `
    -StartupGraceSeconds 180
if ($confirmedShutdownDecision.Action -ne "Shutdown" -or $confirmedShutdownDecision.Reason -ne "confirmed") {
    throw "A type=1 event after startup grace must remain a confirmed shutdown/restart signal."
}

foreach ($invalidType in @($null, "invalid", 7)) {
    $decision = Get-TurzxShutdownEventDecision `
        -EventType $invalidType `
        -WatchdogStartedUtc $watchdogStartedUtc `
        -NowUtc $watchdogStartedUtc.AddMinutes(5) `
        -StartupGraceSeconds 180
    if ($decision.Action -ne "Ignore") {
        throw "Unknown or unsupported shutdown event types must fail safe by keeping the watchdog alive."
    }
}

function Assert-OrderAfter {
    param(
        [string]$Text,
        [string]$Anchor,
        [string]$First,
        [string]$Second,
        [string]$Message
    )

    $anchorIndex = $Text.IndexOf($Anchor, [StringComparison]::Ordinal)
    if ($anchorIndex -lt 0) {
        throw "Missing order anchor: $Anchor"
    }
    $firstIndex = $Text.IndexOf($First, $anchorIndex, [StringComparison]::Ordinal)
    $secondIndex = $Text.IndexOf($Second, $anchorIndex, [StringComparison]::Ordinal)
    if ($firstIndex -lt 0 -or $secondIndex -lt 0 -or $firstIndex -gt $secondIndex) {
        throw $Message
    }
}

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'if ($eventType -eq 4)' `
    -First 'Send-Blank -Reason "suspend"' `
    -Second 'Stop-Stack -Reason "suspend"' `
    -Message "Suspend handling must send the blank frame before stopping the stack."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'if ($shutdownDecision.Action -eq "Shutdown")' `
    -First 'Send-Blank -Reason "shutdown"' `
    -Second 'Stop-Stack -Reason "shutdown"' `
    -Message "Shutdown/sleep fallback handling must send the blank frame before stopping the stack."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'if (-not $watchdogMutexCreated)' `
    -First 'exit 0' `
    -Second 'Set-Content -LiteralPath $watchdogPidPath -Value $PID' `
    -Message "A duplicate watchdog must exit before the live watchdog PID file is written."

$stackText = Get-Content -Raw -LiteralPath $stack
foreach ($pattern in @("StartSideScreenWatchdog.ps1", '[switch]$Worker')) {
    if ($stackText -notmatch [regex]::Escape($pattern)) {
        throw "Stack entrypoint must delegate to watchdog unless called as worker; missing: $pattern"
    }
}

$installerText = Get-Content -Raw -LiteralPath $installer
foreach ($pattern in @(
    "wscript.exe",
    "StartSideScreenWatchdog-Hidden.vbs",
    "TURZX SideScreen Resume",
    "RestartSideScreenAfterResume.ps1",
    "RestartSideScreenAfterResume-Hidden.vbs",
    "DisallowStartIfOnBatteries",
    "StopIfGoingOnBatteries"
)) {
    if ($installerText -notmatch [regex]::Escape($pattern)) {
        throw "Startup installer must point the scheduled task at hidden watchdog launcher; missing: $pattern"
    }
}

if (!(Test-Path -LiteralPath $resume)) {
    throw "Missing resume recovery script: $resume"
}
if (!(Test-Path -LiteralPath $resumeLauncher)) {
    throw "Missing resume recovery hidden launcher: $resumeLauncher"
}

$resumeText = Get-Content -Raw -LiteralPath $resume
foreach ($pattern in @(
    "StopSideScreenStack.ps1",
    "StartSideScreenWatchdog-Hidden.vbs",
    "restart-on-resume",
    "DelaySeconds",
    "pnputil.exe",
    "/restart-device",
    "VID_0525&PID_A4A7",
    "Restart-TurzxUsbDevice"
)) {
    if ($resumeText -notmatch [regex]::Escape($pattern)) {
        throw "Resume recovery script missing expected pattern: $pattern"
    }
}

$resumeLauncherText = Get-Content -Raw -LiteralPath $resumeLauncher
foreach ($pattern in @("RestartSideScreenAfterResume.ps1", "shell.Run(command, 0, True)", "DelaySeconds")) {
    if ($resumeLauncherText -notmatch [regex]::Escape($pattern)) {
        throw "Resume hidden launcher missing expected pattern: $pattern"
    }
}

$watchdogLauncherText = Get-Content -Raw -LiteralPath $watchdogLauncher
foreach ($pattern in @("StartSideScreenWatchdog.ps1", "shell.Run(command, 0, True)")) {
    if ($watchdogLauncherText -notmatch [regex]::Escape($pattern)) {
        throw "Hidden watchdog launcher missing expected pattern: $pattern"
    }
}

$installerCmdText = Get-Content -Raw -LiteralPath $installerCmd
foreach ($pattern in @("Start-Process", "-Verb RunAs", "install-startup-admin.ps1", "-Root")) {
    if ($installerCmdText -notmatch [regex]::Escape($pattern)) {
        throw "Admin startup cmd wrapper missing expected pattern: $pattern"
    }
}
if ($installerCmdText -match [regex]::Escape("-NoExit")) {
    throw "Admin startup cmd wrapper must not leave an elevated PowerShell window open."
}

$startText = Get-Content -Raw -LiteralPath $start
foreach ($pattern in @("schtasks.exe", "TURZX SideScreen", "StartSideScreenWatchdog.ps1", "restart-on-start.flag")) {
    if ($startText -notmatch [regex]::Escape($pattern)) {
        throw "Desktop start path missing expected pattern: $pattern"
    }
}

$streamStart = Join-Path $side "StartVideoStream.ps1"
$streamStartText = Get-Content -Raw -LiteralPath $streamStart
foreach ($pattern in @(
    "Wait-MetricsEndpointReady",
    "Wait-MetricsEndpointOrPortAvailable",
    "Test-MetricsPortAvailable",
    'if ($metricsState -eq "available")',
    "metrics endpoint did not become ready"
)) {
    if ($streamStartText -notmatch [regex]::Escape($pattern)) {
        throw "Stream startup must wait for a real metrics response before sending the first full frame; missing: $pattern"
    }
}
if ($streamStartText -match [regex]::Escape('$existingAgent')) {
    throw "Stream startup must not confuse a stale process-table entry with a healthy metrics endpoint."
}

$stopText = Get-Content -Raw -LiteralPath $stop
if ($stopText -notmatch [regex]::Escape("SkipStackEntrypoint")) {
    throw "Stop script must support SkipStackEntrypoint for elevated self-cleanup."
}

$processSnapshotCount = [regex]::Matches($stopText, [regex]::Escape("Get-CimInstance Win32_Process")).Count
if ($processSnapshotCount -ne 1 -or $stopText -notmatch [regex]::Escape('$processSnapshot')) {
    throw "Stop script must take one Win32_Process snapshot so watchdog recovery is not delayed by repeated WMI scans."
}

foreach ($pattern in @("restart-on-start.flag", "StopSideScreenStack.ps1", "SkipStackEntrypoint")) {
    if ($stackText -notmatch [regex]::Escape($pattern)) {
        throw "Stack entrypoint missing elevated cleanup pattern: $pattern"
    }
}

if ($stackText -notmatch [regex]::Escape("-FullResyncEveryFrames")) {
    throw "Stack worker must enable periodic full-frame transport resynchronization."
}

if ($stopText -notmatch [regex]::Escape("taskkill.exe")) {
    throw "Stop script must use taskkill.exe as a hard fallback for crashed elevated stream processes."
}

if ($stopText -notmatch [regex]::Escape("ParentProcessId")) {
    throw "Stop script must kill parent PowerShell processes for crashed stream children."
}

foreach ($pattern in @(
    '*-File*StartSideScreenStack.ps1*',
    '*-File*StartSideScreenWatchdog.ps1*'
)) {
    if ($stopText -notmatch [regex]::Escape($pattern)) {
        throw "Stop script must only match real script entrypoints, not diagnostic commands containing the script name; missing: $pattern"
    }
}

powershell -NoProfile -ExecutionPolicy Bypass -File $blank -Root $Root -DryRun | Out-Host
$blankPng = Join-Path $side "out\blank-screen.png"
if (!(Test-Path -LiteralPath $blankPng)) {
    throw "Blank PNG was not created: $blankPng"
}

$item = Get-Item -LiteralPath $blankPng
if ($item.Length -le 1000) {
    throw "Blank PNG is unexpectedly small: $($item.Length)"
}

Write-Host "Power watchdog checks completed."
