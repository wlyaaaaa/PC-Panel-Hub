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
$activeRecoveryPolicy = Join-Path $side "HS2ActiveRecoveryPolicy.ps1"
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

foreach ($path in @($watchdog, $shutdownPolicy, $displayPowerPolicy, $activeRecoveryPolicy, $windowPreservationPolicy, $overlayWatchdogPolicy, $brightness, $powerProgram, $watchdogLauncher, $stop, $blank, $overlayManifest, $overlayController, $crystalCardWindow)) {
    if (!(Test-Path -LiteralPath $path)) {
        throw "Missing power management script: $path"
    }
}

. $displayPowerPolicy
. $activeRecoveryPolicy
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

$windowGuardNativeType = Initialize-HS2ExclusiveWindowGuardNativeMethods
foreach ($methodName in @(
        "CaptureMonitors",
        "CaptureWindows",
        "MoveWindowPlacement",
        "MinimizeWindow")) {
    if ($null -eq $windowGuardNativeType.GetMethod(
            $methodName,
            [Reflection.BindingFlags]::Public -bor
            [Reflection.BindingFlags]::Static)) {
        throw "HS2 exclusive-window guard is missing native method: $methodName"
    }
}

$primaryMonitor = [pscustomobject]@{
    DeviceName = "\\.\DISPLAY1"
    IsPrimary = $true
    Left = 0
    Top = 0
    Right = 2560
    Bottom = 1440
    WorkLeft = 0
    WorkTop = 0
    WorkRight = 2560
    WorkBottom = 1400
}
$hs2Monitor = [pscustomobject]@{
    DeviceName = "\\.\DISPLAY20"
    IsPrimary = $false
    Left = 3840
    Top = -1048
    Right = 6128
    Bottom = 0
    WorkLeft = 3840
    WorkTop = -1048
    WorkRight = 6128
    WorkBottom = 0
}
function New-GuardWindow {
    param(
        [int64]$Hwnd,
        [int]$ProcessId,
        [string]$ProcessName,
        [string]$ClassName,
        [string]$MonitorDevice,
        [bool]$Minimized = $false
    )

    return [pscustomobject]@{
        Hwnd = $Hwnd
        ProcessId = $ProcessId
        ProcessName = $ProcessName
        Title = $ProcessName
        ClassName = $ClassName
        MonitorDevice = $MonitorDevice
        IsVisible = $true
        IsMinimized = $Minimized
        IsCloaked = $false
        PlacementLeft = 4000
        PlacementTop = -900
        PlacementRight = 5200
        PlacementBottom = -100
    }
}

$guardWindows = @(
    New-GuardWindow `
        -Hwnd 1 `
        -ProcessId 900 `
        -ProcessName "HS2.CrystalOverlay" `
        -ClassName "Static" `
        -MonitorDevice $hs2Monitor.DeviceName
    New-GuardWindow `
        -Hwnd 2 `
        -ProcessId 901 `
        -ProcessName "notepad" `
        -ClassName "Notepad" `
        -MonitorDevice $hs2Monitor.DeviceName
    New-GuardWindow `
        -Hwnd 3 `
        -ProcessId 902 `
        -ProcessName "wallpaper64" `
        -ClassName "Wallpaper" `
        -MonitorDevice $hs2Monitor.DeviceName
    New-GuardWindow `
        -Hwnd 4 `
        -ProcessId 903 `
        -ProcessName "explorer" `
        -ClassName "WorkerW" `
        -MonitorDevice $hs2Monitor.DeviceName
    New-GuardWindow `
        -Hwnd 5 `
        -ProcessId 904 `
        -ProcessName "chrome" `
        -ClassName "Chrome_WidgetWin_1" `
        -MonitorDevice $primaryMonitor.DeviceName
)
$guardPlan = Get-HS2ExclusiveWindowGuardPlan `
    -Monitors @($primaryMonitor, $hs2Monitor) `
    -Windows $guardWindows `
    -OverlayProcessIds @(900)
if ($guardPlan.Status -cne "active" -or
    $guardPlan.TargetMonitorDevice -cne $hs2Monitor.DeviceName -or
    $guardPlan.SafeMonitorDevice -cne $primaryMonitor.DeviceName) {
    throw "HS2 exclusive-window guard failed to identify the overlay and safe monitor."
}
if ($guardPlan.Actions.Count -ne 1 -or
    $guardPlan.Actions[0].Action -cne "Move" -or
    $guardPlan.Actions[0].ProcessId -ne 901 -or
    $guardPlan.Actions[0].Left -lt $primaryMonitor.WorkLeft -or
    $guardPlan.Actions[0].Right -gt $primaryMonitor.WorkRight -or
    $guardPlan.Actions[0].Top -lt $primaryMonitor.WorkTop -or
    $guardPlan.Actions[0].Bottom -gt $primaryMonitor.WorkBottom) {
    throw "HS2 exclusive-window guard must move only ordinary apps into the primary work area."
}

$misplacedOverlay = New-GuardWindow `
    -Hwnd 6 `
    -ProcessId 900 `
    -ProcessName "HS2.CrystalOverlay" `
    -ClassName "Static" `
    -MonitorDevice $primaryMonitor.DeviceName
$geometryGuardPlan = Get-HS2ExclusiveWindowGuardPlan `
    -Monitors @($primaryMonitor, $hs2Monitor) `
    -Windows @($misplacedOverlay, $guardWindows[1]) `
    -OverlayProcessIds @(900)
if ($geometryGuardPlan.TargetMonitorDevice -cne $hs2Monitor.DeviceName -or
    $geometryGuardPlan.SafeMonitorDevice -cne $primaryMonitor.DeviceName -or
    $geometryGuardPlan.Actions.Count -ne 1 -or
    $geometryGuardPlan.Actions[0].ProcessId -ne 901) {
    throw "HS2 geometry must prevent a misplaced overlay window from reversing the guard direction."
}

$targetOnlyMonitor = $hs2Monitor.PSObject.Copy()
$targetOnlyMonitor.IsPrimary = $true
$targetOnlyPlan = Get-HS2ExclusiveWindowGuardPlan `
    -Monitors @($targetOnlyMonitor) `
    -Windows @($guardWindows[0], $guardWindows[1]) `
    -OverlayProcessIds @(900)
if ($targetOnlyPlan.Status -cne "target-only" -or
    $targetOnlyPlan.Actions.Count -ne 1 -or
    $targetOnlyPlan.Actions[0].Action -cne "Minimize") {
    throw "HS2 exclusive-window guard must minimize ordinary apps when HS2 is the only display."
}

$decisionNow = [DateTime]::Parse("2026-08-02T06:00:00Z").ToUniversalTime()
$healthyOverlayCandidate = [pscustomobject]@{
    HasExited = $false
    Threads = @([pscustomobject]@{ Id = 1 })
}
if (-not (Test-HS2OverlayProcessCandidate `
        -Process $healthyOverlayCandidate)) {
    throw "A live HS2 process with a worker thread must be accepted."
}
$ghostOverlayCandidate = [pscustomobject]@{
    HasExited = $false
    Threads = @()
}
if (Test-HS2OverlayProcessCandidate `
        -Process $ghostOverlayCandidate) {
    throw "A zero-thread HS2 crash ghost must not own overlay protection."
}

$overlayRebindRunning = Get-HS2OverlayRebindDecision `
    -RebindRequired $true `
    -IsRunning $true
if ($overlayRebindRunning.Action -cne "Recycle") {
    throw "A live overlay must be recycled after the HS2 display is rebound."
}
$overlayRebindMissing = Get-HS2OverlayRebindDecision `
    -RebindRequired $true `
    -IsRunning $false
if ($overlayRebindMissing.Action -cne "Activate") {
    throw "A missing overlay must activate after the HS2 display is rebound."
}
$overlayRebindIdle = Get-HS2OverlayRebindDecision `
    -RebindRequired $false `
    -IsRunning $true
if ($overlayRebindIdle.Action -cne "None") {
    throw "A verified overlay must not be recycled without a rebind request."
}

$overlayRecycleProbe = Start-Process `
    -FilePath "powershell.exe" `
    -ArgumentList @(
        "-NoProfile",
        "-WindowStyle", "Hidden",
        "-Command", "Start-Sleep -Seconds 30") `
    -WindowStyle Hidden `
    -PassThru
try {
    Start-Sleep -Milliseconds 200
    $overlayRecycleResult = @(
        Stop-HS2OverlayForRebind `
            -Process $overlayRecycleProbe `
            -TimeoutMilliseconds 3000)
    if ($overlayRecycleResult.Count -ne 1 -or
        -not $overlayRecycleResult[0].Attempted -or
        -not $overlayRecycleResult[0].Stopped) {
        throw "Overlay recycle must return exactly one structured result after stopping its exact probe process."
    }
}
finally {
    Stop-Process `
        -Id $overlayRecycleProbe.Id `
        -Force `
        -ErrorAction SilentlyContinue
}

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

$lconnectRecoveryStarted = $decisionNow.AddSeconds(-20)
$lconnectFastRetry = Get-HS2LConnectRecoveryFollowUpDecision `
    -DisplayHealthy $true `
    -RecoveryAttempted $true `
    -RecoveryStartedUtc $lconnectRecoveryStarted `
    -NowUtc $decisionNow `
    -GraceSeconds 90
if ($lconnectFastRetry.Action -cne "FastRetry") {
    throw "L-Connect controller warm-up must keep the fast retry cadence."
}
$lconnectExpired = Get-HS2LConnectRecoveryFollowUpDecision `
    -DisplayHealthy $true `
    -RecoveryAttempted $true `
    -RecoveryStartedUtc $decisionNow.AddSeconds(-91) `
    -NowUtc $decisionNow `
    -GraceSeconds 90
if ($lconnectExpired.Action -cne "SlowRetry") {
    throw "L-Connect recovery must stop fast retrying after its bounded grace period."
}
$lconnectInitial = Get-HS2LConnectRecoveryFollowUpDecision `
    -DisplayHealthy $true `
    -RecoveryAttempted $false `
    -RecoveryStartedUtc ([DateTime]::MinValue) `
    -NowUtc $decisionNow `
    -GraceSeconds 90
if ($lconnectInitial.Action -cne "RestartService") {
    throw "A healthy HS2 USB display with a missing controller must restart L-Connect once."
}
$lconnectUnsafe = Get-HS2LConnectRecoveryFollowUpDecision `
    -DisplayHealthy $false `
    -RecoveryAttempted $false `
    -RecoveryStartedUtc ([DateTime]::MinValue) `
    -NowUtc $decisionNow `
    -GraceSeconds 90
if ($lconnectUnsafe.Action -cne "SlowRetry") {
    throw "L-Connect service recovery must fail closed while the USB display is absent."
}

$script:controllerProbeCount = 0
$controllerReady = Wait-HS2LConnectControllerReady `
    -MaximumAttempts 3 `
    -PollMilliseconds 1 `
    -ControllerProbe {
        $script:controllerProbeCount++
        return $script:controllerProbeCount -ge 3
    } `
    -DelayAction { param([int]$Milliseconds) }
if (-not $controllerReady -or $script:controllerProbeCount -ne 3) {
    throw "L-Connect controller readiness must poll until the real controller appears."
}
$script:controllerProbeCount = 0
$controllerPending = Wait-HS2LConnectControllerReady `
    -MaximumAttempts 2 `
    -PollMilliseconds 1 `
    -ControllerProbe {
        $script:controllerProbeCount++
        return $false
    } `
    -DelayAction { param([int]$Milliseconds) }
if ($controllerPending -or $script:controllerProbeCount -ne 2) {
    throw "L-Connect controller readiness must stop after its bounded attempt budget."
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

$decisionNow = [DateTime]::Parse("2026-08-06T12:00:00Z").ToUniversalTime()
$idleDecision = Get-HS2ActiveRecoveryDecision `
    -DesiredActive $false `
    -VerifiedActive $false `
    -LastAttemptUtc ([DateTime]::MinValue) `
    -LastVerifiedUtc ([DateTime]::MinValue) `
    -NowUtc $decisionNow
if ($idleDecision.Action -ne "Idle") {
    throw "HS2 recovery must remain idle while the display is not desired active."
}

$activateDecision = Get-HS2ActiveRecoveryDecision `
    -DesiredActive $true `
    -VerifiedActive $false `
    -LastAttemptUtc ([DateTime]::MinValue) `
    -LastVerifiedUtc ([DateTime]::MinValue) `
    -NowUtc $decisionNow
if ($activateDecision.Action -ne "Activate") {
    throw "An unverified HS2 display must receive an immediate activation attempt."
}

$waitDecision = Get-HS2ActiveRecoveryDecision `
    -DesiredActive $true `
    -VerifiedActive $false `
    -LastAttemptUtc $decisionNow.AddSeconds(-14) `
    -LastVerifiedUtc ([DateTime]::MinValue) `
    -NowUtc $decisionNow `
    -RetrySeconds 15
if ($waitDecision.Action -ne "Wait") {
    throw "HS2 activation failures must observe the configured retry backoff."
}

$retryDecision = Get-HS2ActiveRecoveryDecision `
    -DesiredActive $true `
    -VerifiedActive $false `
    -LastAttemptUtc $decisionNow.AddSeconds(-15) `
    -LastVerifiedUtc ([DateTime]::MinValue) `
    -NowUtc $decisionNow `
    -RetrySeconds 15
if ($retryDecision.Action -ne "Activate") {
    throw "HS2 activation must retry after the backoff expires."
}

$healthyDecision = Get-HS2ActiveRecoveryDecision `
    -DesiredActive $true `
    -VerifiedActive $true `
    -LastAttemptUtc $decisionNow.AddSeconds(-20) `
    -LastVerifiedUtc $decisionNow.AddSeconds(-29) `
    -NowUtc $decisionNow `
    -VerifySeconds 30
if ($healthyDecision.Action -ne "Healthy") {
    throw "A recently verified HS2 display must remain healthy without extra writes."
}

$verifyDecision = Get-HS2ActiveRecoveryDecision `
    -DesiredActive $true `
    -VerifiedActive $true `
    -LastAttemptUtc $decisionNow.AddSeconds(-30) `
    -LastVerifiedUtc $decisionNow.AddSeconds(-30) `
    -NowUtc $decisionNow `
    -VerifySeconds 30
if ($verifyDecision.Action -ne "Verify") {
    throw "A verified HS2 display must be periodically re-verified."
}

$validHub = [pscustomobject]@{
    InstanceId = "USB\VID_1A86&PID_8091\dedicated-hs2-hub"
    Present = $true
}
$validChild = [pscustomobject]@{
    InstanceId = "USB\VID_0000&PID_0002\failed-port-two"
    ParentInstanceId = $validHub.InstanceId
    ProblemCode = 43
    LocationInfo = "Port_#0002.Hub_#0012"
    Present = $true
}
$validUsbPlan = Get-HS2UsbRecoveryPlan `
    -BoundHubInstanceId $validHub.InstanceId `
    -Hubs @($validHub) `
    -Children @($validChild)
if (-not $validUsbPlan.Applicable -or
    $validUsbPlan.HubInstanceId -cne $validHub.InstanceId -or
    $validUsbPlan.ChildInstanceId -cne $validChild.InstanceId -or
    ($validUsbPlan.Operations -join ",") -cne "RestartDedicatedHub,RemoveExactFailedChild,ScanDevices") {
    throw "HS2 USB recovery must target only the dedicated hub and its exact port-two Code 43 child."
}

$initialUsbDispatch = Get-HS2UsbRecoveryDispatchDecision `
    -RecoveryAttempted $false `
    -ContinuationPending $false
if ($initialUsbDispatch.Action -cne "RestartDedicatedHub") {
    throw "The first verified HS2 recovery pass must restart only the dedicated hub."
}

$continuationUsbDispatch = Get-HS2UsbRecoveryDispatchDecision `
    -RecoveryAttempted $true `
    -ContinuationPending $true
if ($continuationUsbDispatch.Action -cne "RemoveExactChild") {
    throw "A post-restart topology gap must continue with the revalidated exact child, not restart the hub again."
}

$exhaustedUsbDispatch = Get-HS2UsbRecoveryDispatchDecision `
    -RecoveryAttempted $true `
    -ContinuationPending $false
if ($exhaustedUsbDispatch.Action -cne "Wait") {
    throw "HS2 USB recovery must remain bounded after its hub and exact-child passes are consumed."
}

# Restarting the dedicated hub gives the same physical port-two failure a new
# transient VID_0000 instance id.  That must not abort recovery: the immutable
# safety identity is the previously verified dedicated hub + exact port 2,
# which Get-HS2UsbRecoveryPlan revalidates on the fresh snapshot.
$freshChild = [pscustomobject]@{
    InstanceId = "USB\VID_0000&PID_0002\fresh-failed-port-two"
    ParentInstanceId = $validHub.InstanceId
    ProblemCode = 43
    LocationInfo = "Port_#0002.Hub_#0012"
    Present = $true
}
$freshUsbPlan = Get-HS2UsbRecoveryPlan `
    -BoundHubInstanceId $validHub.InstanceId `
    -Hubs @($validHub) `
    -Children @($freshChild)
if (-not (Test-HS2UsbRecoveryPlanContinuity `
        -InitialPlan $validUsbPlan `
        -FreshPlan $freshUsbPlan)) {
    throw "HS2 recovery must accept a new transient child id on the same verified hub and exact port two."
}

$differentHubPlan = [pscustomobject]@{
    Applicable = $true
    HubInstanceId = "USB\VID_1A86&PID_8091\different-hub"
    ChildInstanceId = $freshChild.InstanceId
}
if (Test-HS2UsbRecoveryPlanContinuity `
        -InitialPlan $validUsbPlan `
        -FreshPlan $differentHubPlan) {
    throw "HS2 recovery continuity must reject a fresh plan on a different hub."
}

$script:postRestartSnapshotAttempts = 0
$eventualFreshPlan = Wait-HS2UsbRecoveryPlan `
    -BoundHubInstanceId $validHub.InstanceId `
    -TimeoutSeconds 1 `
    -PollMilliseconds 1 `
    -SnapshotProvider {
        $script:postRestartSnapshotAttempts++
        [pscustomobject]@{
            Hubs = @($validHub)
            Children = if ($script:postRestartSnapshotAttempts -lt 2) {
                @()
            }
            else {
                @($freshChild)
            }
        }
    } `
    -DelayAction { param($Milliseconds) }
if ($null -eq $eventualFreshPlan -or
    $eventualFreshPlan.ChildInstanceId -cne $freshChild.InstanceId -or
    $script:postRestartSnapshotAttempts -ne 2) {
    throw "HS2 recovery must tolerate the post-restart gap before the exact failed child reappears."
}

$rejectedUsbCases = @(
    [pscustomobject]@{
        Name = "wrong hub"
        Hubs = @([pscustomobject]@{ InstanceId = "USB\VID_1234&PID_5678\root"; Present = $true })
        Children = @($validChild)
    },
    [pscustomobject]@{
        Name = "ambiguous hubs"
        Hubs = @($validHub, [pscustomobject]@{ InstanceId = $validHub.InstanceId; Present = $true })
        Children = @($validChild)
    },
    [pscustomobject]@{
        Name = "wrong parent"
        Hubs = @($validHub)
        Children = @([pscustomobject]@{ InstanceId = $validChild.InstanceId; ParentInstanceId = "USB\ROOT_HUB30\broad"; ProblemCode = 43; LocationInfo = $validChild.LocationInfo; Present = $true })
    },
    [pscustomobject]@{
        Name = "not Code 43"
        Hubs = @($validHub)
        Children = @([pscustomobject]@{ InstanceId = $validChild.InstanceId; ParentInstanceId = $validHub.InstanceId; ProblemCode = 0; LocationInfo = $validChild.LocationInfo; Present = $true })
    },
    [pscustomobject]@{
        Name = "wrong port"
        Hubs = @($validHub)
        Children = @([pscustomobject]@{ InstanceId = $validChild.InstanceId; ParentInstanceId = $validHub.InstanceId; ProblemCode = 43; LocationInfo = "Port_#0003.Hub_#0012"; Present = $true })
    },
    [pscustomobject]@{
        Name = "ambiguous port-two children"
        Hubs = @($validHub)
        Children = @(
            $validChild,
            [pscustomobject]@{ InstanceId = "USB\VID_0000&PID_0002\second-port-two"; ParentInstanceId = $validHub.InstanceId; ProblemCode = 43; LocationInfo = $validChild.LocationInfo; Present = $true })
    }
)
foreach ($rejectedCase in $rejectedUsbCases) {
    $rejectedPlan = Get-HS2UsbRecoveryPlan `
        -BoundHubInstanceId $validHub.InstanceId `
        -Hubs $rejectedCase.Hubs `
        -Children $rejectedCase.Children
    if ($rejectedPlan.Applicable) {
        throw "HS2 USB recovery must fail closed for $($rejectedCase.Name)."
    }
}

$wrongBoundHubPlan = Get-HS2UsbRecoveryPlan `
    -BoundHubInstanceId "USB\VID_1A86&PID_8091\different-machine-hub" `
    -Hubs @($validHub) `
    -Children @($validChild)
if ($wrongBoundHubPlan.Applicable) {
    throw "HS2 USB recovery must require the exact previously verified machine binding."
}

$script:mockHS2PnpDevices = @()
function Get-PnpDevice {
    [CmdletBinding()]
    param([switch]$PresentOnly)
    return $script:mockHS2PnpDevices
}
try {
    $script:mockHS2PnpDevices = @(
        [pscustomobject]@{
            InstanceId = "USB\VID_1CBE&PID_A068\native-mode"
            Status = "OK"
        }
    )
    if (Test-HS2UsbDisplayHealthy) {
        throw "Native A068 controller mode must not be treated as a healthy Windows display."
    }

    $script:mockHS2PnpDevices = @(
        [pscustomobject]@{
            InstanceId = "USB\VID_1A86&PID_AD23\failed-display"
            Status = "Unknown"
        }
    )
    if (Test-HS2UsbDisplayHealthy) {
        throw "An unhealthy AD23 device must not trigger L-Connect-only recovery."
    }

    $script:mockHS2PnpDevices = @(
        [pscustomobject]@{
            InstanceId = "USB\VID_1A86&PID_AD23\healthy-display"
            Status = "OK"
        }
    )
    if (-not (Test-HS2UsbDisplayHealthy)) {
        throw "A healthy AD23 display must permit bounded L-Connect recovery."
    }
}
finally {
    Remove-Item Function:\Get-PnpDevice -Force
}

$activeRecoveryText = Get-Content -Raw -LiteralPath $activeRecoveryPolicy
$missingBindingPath = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("absent-hs2-binding-{0}.json" -f [Guid]::NewGuid().ToString("N"))
if ($null -ne (Read-HS2UsbTopologyBinding -Path $missingBindingPath)) {
    throw "Missing HS2 topology binding must fail closed."
}
foreach ($forbidden in @(
    '/restart-device "USB\ROOT',
    '/disable-device "USB\ROOT',
    'Get-PnpDevice | Disable-PnpDevice',
    'Get-PnpDevice | Restart-PnpDevice'
)) {
    if ($activeRecoveryText -match [regex]::Escape($forbidden)) {
        throw "HS2 recovery must never reset the whole USB tree: $forbidden"
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
    "SideScreenDisplayPowerPolicy.ps1",
    "HS2ActiveRecoveryPolicy.ps1",
    "WindowsDisplayWindowPolicy.ps1",
    "HS2OverlayWatchdogPolicy.ps1",
    "Enable-DesktopWindowPreservation",
    "Windows display window preservation compliant=",
    "Invoke-HS2OverlayHealthCheck",
    "Get-HS2OverlayRebindDecision",
    "Stop-HS2OverlayForRebind",
    "hs2OverlayRebindRequired",
    "Invoke-HS2ExclusiveWindowProtection",
    "Invoke-HS2ExclusiveWindowGuard",
    "HS2 exclusive-window guard corrected",
    'hs2DisplayStateActive = $false',
    'hs2DisplayStateDesiredActive = $false',
    "Invoke-HS2ActiveMaintenance",
    "Invoke-HS2UsbRecovery",
    "HS2UsbRecoveryAfterFailures",
    "Get-HS2LConnectRecoveryFollowUpDecision",
    "HS2LConnectRecoveryRetrySeconds = 5",
    "HS2LConnectRecoveryGraceSeconds = 90",
    "HS2ActiveSlowRetrySeconds",
    "HS2 recovery helper failed safely",
    "hs2-usb-topology-binding.json",
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

if ($watchdogText -match '(?s)function Set-ActiveDisplayState.*?\$script:hs2DisplayStateActive\s*=\s*\$true.*?Invoke-HS2PowerState') {
    throw "HS2 Active must never be marked verified before L-Connect read-back succeeds."
}
if ($watchdogText -notmatch '\$null\s*-eq\s*\$result\s*-or\s*-not\s*\[bool\]\$result\.Verified') {
    throw "HS2 Active requires an explicit Verified=true result."
}
if ($watchdogText -notmatch '(?s)function Invoke-HS2ActiveMaintenance.*?try\s*\{.*?Get-HS2UsbRecoveryPlan.*?catch\s*\{.*?HS2 recovery helper failed safely') {
    throw "HS2 PnP and recovery helpers must be isolated from the watchdog main loop."
}
if ($activeRecoveryText -notmatch '(?s)function Invoke-HS2LConnectServiceRecovery.*?WaitForStatus.*?Wait-HS2LConnectControllerReady') {
    throw "L-Connect recovery must wait for the real controller after the service reports Running."
}

$watchdogTokens = $null
$watchdogParseErrors = $null
$watchdogAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $watchdog,
    [ref]$watchdogTokens,
    [ref]$watchdogParseErrors)
if ($watchdogParseErrors.Count -gt 0) {
    throw "Unable to parse watchdog for verified-state gate tests."
}
foreach ($functionName in @(
        "Invoke-HS2OverlayHealthCheck",
        "Invoke-HS2ExclusiveWindowProtection")) {
    $functionAst = @(
        $watchdogAst.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -ceq $functionName
            },
            $true)
    )
    if ($functionAst.Count -ne 1 -or
        $functionAst[0].Extent.Text -notmatch '-not\s+\$script:hs2DisplayStateActive') {
        throw "$functionName must fail closed before HS2 verification."
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
    -Anchor 'while ($true)' `
    -First 'Invoke-HS2ActiveMaintenance' `
    -Second 'Invoke-HS2OverlayHealthCheck' `
    -Message "The watchdog loop must verify or recover HS2 before checking the overlay."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'function Set-ActiveDisplayState' `
    -First '$script:hs2OverlayRebindRequired = $true' `
    -Second 'Invoke-HS2ActiveMaintenance -Reason $Reason' `
    -Message "Every startup or resume must request one overlay display rebind before activation."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'function Invoke-HS2OverlayHealthCheck' `
    -First 'Stop-HS2OverlayForRebind' `
    -Second 'Start-HS2OverlayActivation' `
    -Message "A surviving stale overlay must be stopped before the rebound display is activated."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'function Invoke-HS2ActiveMaintenance' `
    -First 'Invoke-HS2PowerState -State Active' `
    -Second '$script:hs2DisplayStateActive = $true' `
    -Message "HS2 must be marked active only after the verified L-Connect request returns."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'function Invoke-HS2ActiveMaintenance' `
    -First 'Get-HS2UsbRecoveryPlan' `
    -Second 'Get-HS2LConnectRecoveryFollowUpDecision' `
    -Message "An exact bound Code 43 plan must take precedence over L-Connect service recovery."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'if ($usbPlan.Applicable)' `
    -First 'Get-HS2UsbRecoveryDispatchDecision' `
    -Second 'Invoke-HS2UsbRecovery' `
    -Message "USB recovery must choose a bounded phase before invoking the destructive helper."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'if ($usbDispatch.Action -ne "Wait")' `
    -First '$script:hs2UsbRecoveryContinuationPending = $false' `
    -Second 'Invoke-HS2UsbRecovery' `
    -Message "The bounded exact-child continuation must be consumed before it can invoke recovery."

if ($watchdogText -notmatch [regex]::Escape('-SkipDedicatedHubRestart:$skipDedicatedHubRestart')) {
    throw "The exact-child continuation must not restart the dedicated hub a second time."
}

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'if ($lconnectDecision.Action -eq "RestartService")' `
    -First '$script:hs2LConnectRecoveryAttempted = $true' `
    -Second 'Invoke-HS2LConnectServiceRecovery' `
    -Message "L-Connect recovery must be consumed before restarting the service."

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
    "System32\wscript.exe",
    "StartSideScreenWatchdog-Hidden.vbs",
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
if ($installerText -match [regex]::Escape('-Execute "powershell.exe"')) {
    throw "Startup installer must not execute PowerShell directly in the interactive logon task."
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
