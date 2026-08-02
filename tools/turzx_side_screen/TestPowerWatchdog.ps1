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
$displayPowerPolicy = Join-Path $side "SideScreenDisplayPowerPolicy.ps1"
$windowPreservationPolicy = Join-Path $side "WindowsDisplayWindowPolicy.ps1"
$overlayWatchdogPolicy = Join-Path $side "HS2OverlayWatchdogPolicy.ps1"
$brightness = Join-Path $side "SetTurzxBrightness.ps1"
$powerProgram = Join-Path $side "TURZX.SideScreen.Power.cs"
$resume = Join-Path $side "RestartSideScreenAfterResume.ps1"
$resumeLauncher = Join-Path $side "RestartSideScreenAfterResume-Hidden.vbs"
$installer = Join-Path $Root "scripts\install-startup-admin.ps1"
$installerCmd = Join-Path $Root "scripts\install-startup-admin.cmd"
$start = Join-Path $Root "scripts\start.ps1"
$overlayManifest = Join-Path $Root "tools\hs2_crystal_overlay\src\HS2.CrystalOverlay\Package.appxmanifest"
$overlayController = Join-Path $Root "tools\hs2_crystal_overlay\src\HS2.CrystalOverlay\OverlayController.cs"
$crystalCardWindow = Join-Path $Root "tools\hs2_crystal_overlay\src\HS2.CrystalOverlay\CrystalCardWindow.cs"

foreach ($path in @($watchdog, $shutdownPolicy, $displayPowerPolicy, $windowPreservationPolicy, $overlayWatchdogPolicy, $brightness, $powerProgram, $watchdogLauncher, $stop, $blank, $overlayManifest, $overlayController, $crystalCardWindow)) {
    if (!(Test-Path -LiteralPath $path)) {
        throw "Missing power management script: $path"
    }
}

. $displayPowerPolicy
. $windowPreservationPolicy
. $overlayWatchdogPolicy

$windowPreservationPlan = @(Get-WindowsDisplayWindowPreservationPlan)
if ($windowPreservationPlan.Count -ne 2) {
    throw "Windows display preservation must configure exactly two native policies."
}
$expectedWindowPolicies = @{
    MonitorRemovalRecalcBehavior = "minimize-windows-when-monitor-disconnects"
    RestorePreviousStateRecalcBehavior = "remember-window-locations-by-monitor-connection"
}
foreach ($operation in $windowPreservationPlan) {
    if (-not $expectedWindowPolicies.ContainsKey([string]$operation.Name)) {
        throw "Unexpected Windows display preservation setting: $($operation.Name)"
    }
    if ([int]$operation.DesiredValue -ne 0) {
        throw "Windows display preservation settings must use the native enabled value 0."
    }
    if ([string]$operation.Purpose -cne [string]$expectedWindowPolicies[[string]$operation.Name]) {
        throw "Windows display preservation purpose mismatch for $($operation.Name)."
    }
}

$windowPolicyNativeType = Initialize-WindowsDesktopSettingChangeNativeMethods
if ($null -eq $windowPolicyNativeType -or -not $windowPolicyNativeType.IsPublic) {
    throw "Windows display preservation broadcast type must be public to PowerShell."
}
$windowPolicyBroadcastMethod = $windowPolicyNativeType.GetMethod(
    "SendMessageTimeout",
    [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static)
if ($null -eq $windowPolicyBroadcastMethod) {
    throw "Windows display preservation broadcast method must be publicly callable."
}

$decisionNow = [DateTime]::Parse("2026-08-02T06:00:00Z").ToUniversalTime()
$healthyOverlay = Get-HS2OverlayWatchdogDecision `
    -IsRunning $true `
    -LastAttemptUtc ([DateTime]::MinValue) `
    -NowUtc $decisionNow `
    -RetrySeconds 30
if ($healthyOverlay.Action -ne "Healthy") {
    throw "A running HS2 overlay must be considered healthy."
}

$initialOverlay = Get-HS2OverlayWatchdogDecision `
    -IsRunning $false `
    -LastAttemptUtc ([DateTime]::MinValue) `
    -NowUtc $decisionNow `
    -RetrySeconds 30
if ($initialOverlay.Action -ne "Activate") {
    throw "A missing HS2 overlay must be activated immediately on first check."
}

$waitingOverlay = Get-HS2OverlayWatchdogDecision `
    -IsRunning $false `
    -LastAttemptUtc $decisionNow.AddSeconds(-10) `
    -NowUtc $decisionNow `
    -RetrySeconds 30
if ($waitingOverlay.Action -ne "Wait" -or $waitingOverlay.RetryAfterSeconds -ne 20) {
    throw "HS2 overlay retries must be rate limited without losing the remaining delay."
}

$retryOverlay = Get-HS2OverlayWatchdogDecision `
    -IsRunning $false `
    -LastAttemptUtc $decisionNow.AddSeconds(-30) `
    -NowUtc $decisionNow `
    -RetrySeconds 30
if ($retryOverlay.Action -ne "Activate") {
    throw "A missing HS2 overlay must be retried after the retry interval."
}

[xml]$manifestXml = Get-Content -Raw -LiteralPath $overlayManifest
$manifestNamespaces = New-Object System.Xml.XmlNamespaceManager($manifestXml.NameTable)
$manifestNamespaces.AddNamespace("desktop", "http://schemas.microsoft.com/appx/manifest/desktop/windows10")
$startupExtension = $manifestXml.SelectSingleNode(
    "//desktop:Extension[@Category='windows.startupTask']",
    $manifestNamespaces)
if ($null -eq $startupExtension -or
    [string]$startupExtension.Executable -cne "HS2.CrystalOverlay.exe") {
    throw "HS2 startupTask must name the real executable instead of leaving an unresolved build token."
}

$overlayControllerText = Get-Content -Raw -LiteralPath $overlayController
foreach ($pattern in @("RenderCore", "RecordRenderFailure", "Render recovered")) {
    if ($overlayControllerText -notmatch [regex]::Escape($pattern)) {
        throw "HS2 overlay controller is missing render-failure containment: $pattern"
    }
}

$crystalCardWindowText = Get-Content -Raw -LiteralPath $crystalCardWindow
if ($crystalCardWindowText -notmatch [regex]::Escape("new Bitmap(source)")) {
    throw "HS2 artwork cache must detach decoded images from replaceable source files."
}

function Assert-HS2Plan {
    param(
        [string]$State,
        [bool]$OfflineClock,
        [bool]$ScreenOn
    )

    $plan = @(Get-HS2PowerStatePlan -State $State)
    if ($plan.Count -ne 2) {
        throw "HS2 $State plan must contain exactly two ordered operations."
    }
    if ($plan[0].Type -ne "SetOfflineModeClock" -or [bool]$plan[0].Value -ne $OfflineClock) {
        throw "HS2 $State plan must set offline clock first to $OfflineClock."
    }
    if ($plan[0].ReadType -ne "GetOfflineModeClock") {
        throw "HS2 $State plan must read the offline clock state before writing."
    }
    if ($plan[1].Type -ne "SetIsScreenOn" -or [bool]$plan[1].Value -ne $ScreenOn) {
        throw "HS2 $State plan must set screen state second to $ScreenOn."
    }
    if ($plan[1].ReadType -ne "GetIsScreenOn") {
        throw "HS2 $State plan must read the screen state before writing."
    }
}

Assert-HS2Plan -State Active -OfflineClock $true -ScreenOn $true
Assert-HS2Plan -State Sleep -OfflineClock $true -ScreenOn $false
Assert-HS2Plan -State Shutdown -OfflineClock $false -ScreenOn $false

$script:mockHS2State = @{
    GetOfflineModeClock = $true
    GetIsScreenOn = $true
}
$script:mockHS2Secondary = $true
$script:mockHS2Writes = New-Object "System.Collections.Generic.List[string]"
function Get-HS2Controller {
    $type = if ($script:mockHS2Secondary) { 17104897 } else { 17104896 }
    [pscustomobject]@{
        DevicePath = "mock-hs2"
        ControllerType = $type
        IsSecondaryScreen = $script:mockHS2Secondary
    }
}
function Invoke-HS2DeviceRequest {
    param(
        [string]$DevicePath,
        [string]$Type,
        [object]$Body,
        [int]$ServicePort,
        [int]$TimeoutSec
    )

    if ($Type -like "Get*") {
        return [pscustomobject]@{ Success = $true; Data = [bool]$script:mockHS2State[$Type] }
    }

    [void]$script:mockHS2Writes.Add(("{0}={1}" -f $Type, [bool]$Body))
    if ($Type -eq "SetOfflineModeClock") {
        $script:mockHS2State.GetOfflineModeClock = [bool]$Body
    }
    elseif ($Type -eq "SetIsScreenOn") {
        $script:mockHS2State.GetIsScreenOn = [bool]$Body
    }
    elseif ($Type -eq "SetSecondaryScreen") {
        $script:mockHS2Secondary = [bool]$Body
    }
    return [pscustomobject]@{ Success = $true }
}

Invoke-HS2PowerState -State Active | Out-Null
if ($script:mockHS2Writes.Count -ne 0) {
    throw "HS2 Active must not rewrite values that already match the requested state."
}

$transitionStarted = Start-HS2MonitorModeTransition
if (-not $transitionStarted) {
    throw "HS2 Sleep must initiate the monitor-mode transition from Windows display mode."
}
Invoke-HS2PowerState -State Sleep -MonitorModeAlreadyRequested -SkipVerification | Out-Null
if (($script:mockHS2Writes -join ",") -ne "SetSecondaryScreen=False,SetIsScreenOn=False") {
    throw "HS2 Sleep must leave Windows display mode before turning the panel off."
}

Invoke-HS2PowerState -State Sleep -SkipVerification | Out-Null
if ($script:mockHS2Writes.Count -ne 2) {
    throw "Repeating HS2 Sleep must be idempotent."
}

Invoke-HS2PowerState -State Shutdown -SkipVerification | Out-Null
if (($script:mockHS2Writes -join ",") -ne "SetSecondaryScreen=False,SetIsScreenOn=False,SetOfflineModeClock=False") {
    throw "HS2 Shutdown must disable the offline clock while preserving an already-off screen."
}

Invoke-HS2PowerState -State Active | Out-Null
if (($script:mockHS2Writes -join ",") -ne "SetSecondaryScreen=False,SetIsScreenOn=False,SetOfflineModeClock=False,SetOfflineModeClock=True,SetIsScreenOn=True,SetSecondaryScreen=True") {
    throw "HS2 Active must restore screen state before returning the device to Windows display mode."
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
    "SideScreenDisplayPowerPolicy.ps1",
    "WindowsDisplayWindowPolicy.ps1",
    "HS2OverlayWatchdogPolicy.ps1",
    "Enable-DesktopWindowPreservation",
    "Windows display window preservation compliant=",
    "Invoke-HS2OverlayHealthCheck",
    'hs2DisplayStateActive = $false',
    "HS2 overlay watchdog=enabled",
    "HS2OverlayRetrySeconds = 30",
    "SetTurzxBrightness.ps1",
    "ActiveBrightness = 170",
    "Enter-SleepDisplayState",
    "Enter-ShutdownDisplayState",
    "Set-ActiveDisplayState",
    "fallback=black-frame",
    "display power policy=hs2-transition-first/turzx-brightness-123",
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
    'Enter-ShutdownDisplayState'
)) {
    if ($watchdogText -notmatch [regex]::Escape($pattern)) {
        throw "Watchdog must enter the confirmed shutdown display state; missing: $pattern"
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
    -Anchor 'function Enter-SleepDisplayState' `
    -First 'Start-HS2MonitorModeTransition' `
    -Second 'Stop-Stack -Reason "suspend"' `
    -Message "Suspend handling must request HS2 monitor mode before stopping the TURZX stack."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'function Enter-ShutdownDisplayState' `
    -First 'Start-HS2MonitorModeTransition' `
    -Second 'Stop-Stack -Reason "shutdown"' `
    -Message "Shutdown handling must request HS2 monitor mode before stopping the TURZX stack."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'function Enter-SleepDisplayState' `
    -First 'Stop-Stack -Reason "suspend"' `
    -Second 'Set-TurzxPanelBrightness -Brightness 0' `
    -Message "Suspend handling must release COM before sending the verified TURZX brightness-off command."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'function Enter-SleepDisplayState' `
    -First 'Set-TurzxPanelBrightness -Brightness 0' `
    -Second 'Invoke-HS2PowerState -State Sleep' `
    -Message "Suspend handling must turn TURZX off before waiting for the slow HS2 mode re-enumeration."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'function Start-Stack' `
    -First 'Stop-Stack -Reason ("pre-start/{0}" -f $Reason)' `
    -Second 'Set-TurzxPanelBrightness -Brightness $ActiveBrightness' `
    -Message "Stack startup must restore the configured TURZX brightness after releasing stale COM owners."

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
    'Execute "powershell.exe"',
    "-WindowStyle Hidden",
    "StartSideScreenWatchdog.ps1",
    "TURZX SideScreen Resume",
    "RestartSideScreenAfterResume.ps1",
    "RestartSideScreenAfterResume-Hidden.vbs",
    "DisallowStartIfOnBatteries",
    "StopIfGoingOnBatteries"
)) {
    if ($installerText -notmatch [regex]::Escape($pattern)) {
        throw "Startup installer must make Task Scheduler own the hidden watchdog process; missing: $pattern"
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
