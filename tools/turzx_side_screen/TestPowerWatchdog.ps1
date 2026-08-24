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
    throw "A healthy bound HS2 controller endpoint with an empty controller API must restart L-Connect once."
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
    GetOfflineModeClock = $false
    GetIsScreenOn = $false
}
$script:mockHS2ControllerAvailable = $true
$script:mockHS2EndpointAvailable = $true
$script:mockHS2Secondary = $true
$script:mockHS2Writes = New-Object "System.Collections.Generic.List[string]"
$script:mockHS2Events = New-Object "System.Collections.Generic.List[string]"
function Get-HS2Controller {
    if (-not $script:mockHS2ControllerAvailable) {
        return $null
    }

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

    if (-not $script:mockHS2EndpointAvailable) {
        throw "mock HS2 controller endpoint unavailable"
    }

    if ($Type -like "Get*") {
        [void]$script:mockHS2Events.Add(("Read:{0}" -f $Type))
        return [pscustomobject]@{ Success = $true; Data = [bool]$script:mockHS2State[$Type] }
    }

    [void]$script:mockHS2Writes.Add(("{0}={1}" -f $Type, [bool]$Body))
    [void]$script:mockHS2Events.Add(("Write:{0}={1}" -f $Type, [bool]$Body))
    if ($Type -eq "SetOfflineModeClock") {
        $script:mockHS2State.GetOfflineModeClock = [bool]$Body
    }
    elseif ($Type -eq "SetIsScreenOn") {
        $script:mockHS2State.GetIsScreenOn = [bool]$Body
    }
    elseif ($Type -eq "SetSecondaryScreen") {
        $script:mockHS2Secondary = [bool]$Body
        # A real HS2 mode switch re-enumerates the controller.  Its native
        # state must be read/applied again on the newly selected endpoint;
        # otherwise a pre-switch read-back can falsely report success.
        $script:mockHS2State.GetOfflineModeClock = $false
        $script:mockHS2State.GetIsScreenOn = $false
    }
    return [pscustomobject]@{ Success = $true }
}

$script:mockHS2State.GetOfflineModeClock = $false
$script:mockHS2State.GetIsScreenOn = $false
$script:mockHS2Secondary = $true
$script:mockHS2Writes.Clear()
$script:mockHS2Events.Clear()
$defaultSecondaryActive = Invoke-HS2PowerState -State Active
if ($defaultSecondaryActive.ControllerType -ne 17104897 -or -not $script:mockHS2Secondary) {
    throw "HS2 Active without a mode override must preserve the existing secondary controller type 17104897."
}
if (@($script:mockHS2Writes | Where-Object { $_ -like "SetSecondaryScreen=*" }).Count -ne 0) {
    throw "HS2 Active default must not demote an already-secondary controller."
}
if (($script:mockHS2Writes -join ",") -ne "SetOfflineModeClock=True,SetIsScreenOn=True") {
    throw "HS2 Active default must only restore native screen state while preserving the current controller mode."
}

$script:mockHS2State.GetOfflineModeClock = $false
$script:mockHS2State.GetIsScreenOn = $false
$script:mockHS2Secondary = $true
$script:mockHS2Writes.Clear()
$script:mockHS2Events.Clear()
$forcedNativeActive = Invoke-HS2PowerState -State Active -EnableSecondaryScreen:$false
if ($forcedNativeActive.ControllerType -ne 17104896 -or $script:mockHS2Secondary) {
    throw "HS2 Active -EnableSecondaryScreen:`$false must force the native controller type 17104896."
}
if (($script:mockHS2Writes -join ",") -ne "SetSecondaryScreen=False,SetOfflineModeClock=True,SetIsScreenOn=True") {
    throw "HS2 Active -EnableSecondaryScreen:`$false must be the explicit demotion path."
}
$nativeModeIndex = $script:mockHS2Events.IndexOf("Write:SetSecondaryScreen=False")
if ($nativeModeIndex -lt 0 -or
    -not ($script:mockHS2Events | Select-Object -Skip ($nativeModeIndex + 1) |
        Where-Object { $_ -eq "Write:SetOfflineModeClock=True" }) -or
    -not ($script:mockHS2Events | Select-Object -Skip ($nativeModeIndex + 1) |
        Where-Object { $_ -eq "Write:SetIsScreenOn=True" }) -or
    -not ($script:mockHS2Events | Select-Object -Skip ($nativeModeIndex + 1) |
        Where-Object { $_ -eq "Read:GetIsScreenOn" }) -or
    -not $script:mockHS2State.GetOfflineModeClock -or
    -not $script:mockHS2State.GetIsScreenOn) {
    throw "HS2 native mode switch must re-read, re-apply, and verify screen state on the new controller endpoint."
}

$script:mockHS2State.GetOfflineModeClock = $false
$script:mockHS2State.GetIsScreenOn = $false
$script:mockHS2Secondary = $false
$script:mockHS2Writes.Clear()
$script:mockHS2Events.Clear()
$forcedSecondaryActive = Invoke-HS2PowerState -State Active -EnableSecondaryScreen:$true
if ($forcedSecondaryActive.ControllerType -ne 17104897 -or -not $script:mockHS2Secondary) {
    throw "HS2 Active -EnableSecondaryScreen:`$true must force the secondary controller type 17104897."
}
if (($script:mockHS2Writes -join ",") -ne "SetSecondaryScreen=True,SetOfflineModeClock=True,SetIsScreenOn=True") {
    throw "HS2 Active -EnableSecondaryScreen:`$true must re-apply native state after promoting the controller."
}
$secondaryModeIndex = $script:mockHS2Events.IndexOf("Write:SetSecondaryScreen=True")
if ($secondaryModeIndex -lt 0 -or
    -not ($script:mockHS2Events | Select-Object -Skip ($secondaryModeIndex + 1) |
        Where-Object { $_ -eq "Write:SetOfflineModeClock=True" }) -or
    -not ($script:mockHS2Events | Select-Object -Skip ($secondaryModeIndex + 1) |
        Where-Object { $_ -eq "Write:SetIsScreenOn=True" }) -or
    -not ($script:mockHS2Events | Select-Object -Skip ($secondaryModeIndex + 1) |
        Where-Object { $_ -eq "Read:GetIsScreenOn" }) -or
    -not $script:mockHS2State.GetOfflineModeClock -or
    -not $script:mockHS2State.GetIsScreenOn) {
    throw "HS2 secondary mode switch must finish with a verified screen-on state on the new controller endpoint."
}

$script:mockHS2State.GetOfflineModeClock = $false
$script:mockHS2State.GetIsScreenOn = $false
$script:mockHS2Secondary = $false
$script:mockHS2Writes.Clear()
$script:mockHS2Events.Clear()
$defaultNativeActive = Invoke-HS2PowerState -State Active
if ($defaultNativeActive.ControllerType -ne 17104896 -or $script:mockHS2Secondary) {
    throw "HS2 Active without a mode override must keep a native controller native."
}
if (($script:mockHS2Writes -join ",") -ne "SetOfflineModeClock=True,SetIsScreenOn=True") {
    throw "HS2 native Active must not emit a redundant controller-mode request."
}

# A missing controller or an unreachable controller endpoint must fail closed;
# neither path may issue SetSecondaryScreen(false/true) as a speculative repair.
$script:mockHS2Writes.Clear()
$script:mockHS2Events.Clear()
$script:mockHS2ControllerAvailable = $false
try {
    Invoke-HS2PowerState -State Active -EnableSecondaryScreen:$true -SkipVerification | Out-Null
}
catch {
    # A missing API controller is expected to surface as an activation failure.
}
if (@($script:mockHS2Writes | Where-Object { $_ -like "SetSecondaryScreen=*" }).Count -ne 0) {
    throw "HS2 Active with no controller API result must not send a mode switch."
}

$script:mockHS2ControllerAvailable = $true
$script:mockHS2EndpointAvailable = $false
$script:mockHS2Secondary = $true
try {
    Invoke-HS2PowerState -State Active -EnableSecondaryScreen:$false -SkipVerification | Out-Null
}
catch {
    # A missing device endpoint is expected to surface as an activation failure.
}
if (@($script:mockHS2Writes | Where-Object { $_ -like "SetSecondaryScreen=*" }).Count -ne 0) {
    throw "HS2 Active with no controller endpoint must not send a mode switch."
}
$script:mockHS2EndpointAvailable = $true

# Sleep/Shutdown still leave Windows display mode before turning the panel off.
$script:mockHS2State.GetOfflineModeClock = $false
$script:mockHS2State.GetIsScreenOn = $false
$script:mockHS2Secondary = $true
$script:mockHS2Writes.Clear()
$script:mockHS2Events.Clear()
$transitionStarted = Start-HS2MonitorModeTransition
if (-not $transitionStarted -or $script:mockHS2Secondary) {
    throw "HS2 Sleep must initiate the monitor-mode transition from Windows display mode."
}
Invoke-HS2PowerState -State Sleep -MonitorModeAlreadyRequested -SkipVerification | Out-Null
if (($script:mockHS2Writes -join ",") -ne "SetSecondaryScreen=False,SetOfflineModeClock=True") {
    throw "HS2 Sleep must leave Windows display mode before turning the panel off."
}

$writesAfterSleep = $script:mockHS2Writes.Count
Invoke-HS2PowerState -State Sleep -SkipVerification | Out-Null
if ($script:mockHS2Writes.Count -ne $writesAfterSleep) {
    throw "Repeating HS2 Sleep must be idempotent."
}

Invoke-HS2PowerState -State Shutdown -SkipVerification | Out-Null
if (($script:mockHS2Writes -join ",") -ne "SetSecondaryScreen=False,SetOfflineModeClock=True,SetOfflineModeClock=False") {
    throw "HS2 Shutdown must disable the offline clock while preserving an already-off screen."
}

$promotionNow = [DateTime]::Parse("2026-08-11T14:00:00Z").ToUniversalTime()
$nativeNeededPromotion = Get-HS2SecondaryPromotionDecision `
    -NativeActive $false `
    -SecondaryVerified $false `
    -NativeStableSinceUtc ([DateTime]::MinValue) `
    -LastPromotionAttemptUtc ([DateTime]::MinValue) `
    -NowUtc $promotionNow `
    -StabilitySeconds 30
if ($nativeNeededPromotion.Action -ne "ActivateNative") {
    throw "HS2 promotion must first recover the native controller."
}
$stabilizingPromotion = Get-HS2SecondaryPromotionDecision `
    -NativeActive $true `
    -SecondaryVerified $false `
    -NativeStableSinceUtc $promotionNow.AddSeconds(-29) `
    -LastPromotionAttemptUtc ([DateTime]::MinValue) `
    -NowUtc $promotionNow `
    -StabilitySeconds 30
if ($stabilizingPromotion.Action -ne "WaitNativeStability") {
    throw "HS2 promotion must keep the native display stable through its configured grace window."
}
$readyPromotion = Get-HS2SecondaryPromotionDecision `
    -NativeActive $true `
    -SecondaryVerified $false `
    -NativeStableSinceUtc $promotionNow.AddSeconds(-30) `
    -LastPromotionAttemptUtc ([DateTime]::MinValue) `
    -NowUtc $promotionNow `
    -StabilitySeconds 30
if ($readyPromotion.Action -ne "PromoteSecondary") {
    throw "HS2 must make one secondary-display promotion after the native stabilization window."
}
$heldPromotion = Get-HS2SecondaryPromotionDecision `
    -NativeActive $true `
    -SecondaryVerified $false `
    -NativeStableSinceUtc $promotionNow.AddMinutes(-2) `
    -LastPromotionAttemptUtc $promotionNow.AddSeconds(-1) `
    -NowUtc $promotionNow `
    -StabilitySeconds 30
if ($heldPromotion.Action -ne "HoldNative") {
    throw "HS2 must not repeatedly switch into the Windows topology after a promotion attempt in the same epoch."
}

if ($null -eq (Get-Command Get-HS2ResumeEventDecision -ErrorAction SilentlyContinue)) {
    throw "HS2 resume events must have a pure 30-second merge-window decision function."
}
$resumeEpochNow = [DateTime]::Parse("2026-08-11T14:00:00Z").ToUniversalTime()
$firstResume = Get-HS2ResumeEventDecision `
    -EventType 7 `
    -LastHandledUtc ([DateTime]::MinValue) `
    -NowUtc $resumeEpochNow `
    -MergeSeconds 30
if ($firstResume.Action -ne "Handle") {
    throw "The first resume EventType 7 must start one active watchdog epoch."
}
$mergedResume = Get-HS2ResumeEventDecision `
    -EventType 18 `
    -LastHandledUtc $resumeEpochNow `
    -NowUtc $resumeEpochNow.AddSeconds(10) `
    -MergeSeconds 30
if ($mergedResume.Action -ne "Ignore") {
    throw "Resume EventType 7/18 arrivals within 30 seconds must merge into one epoch."
}
$afterSuspendResetResume = Get-HS2ResumeEventDecision `
    -EventType 18 `
    -LastHandledUtc ([DateTime]::MinValue) `
    -NowUtc $resumeEpochNow.AddSeconds(10) `
    -MergeSeconds 30
if ($afterSuspendResetResume.Action -ne "Handle") {
    throw "A suspend EventType 4 reset must allow the next resume EventType 18 within 30 seconds."
}
$nextResume = Get-HS2ResumeEventDecision `
    -EventType 18 `
    -LastHandledUtc $resumeEpochNow `
    -NowUtc $resumeEpochNow.AddSeconds(31) `
    -MergeSeconds 30
if ($nextResume.Action -ne "Handle") {
    throw "A resume event after the 30-second merge window must start a new epoch."
}
$unsupportedResumeRejected = $false
try {
    $unsupportedResume = Get-HS2ResumeEventDecision `
        -EventType 4 `
        -LastHandledUtc ([DateTime]::MinValue) `
        -NowUtc $resumeEpochNow `
        -MergeSeconds 30
    if ($unsupportedResume.Action -ne "Ignore") {
        throw "unsupported resume action"
    }
}
catch {
    $unsupportedResumeRejected = $true
}
if (-not $unsupportedResumeRejected) {
    throw "Suspend EventType 4 must not be treated as a resume epoch."
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
    Status = "OK"
    ProblemCode = 0
}
$validChild = [pscustomobject]@{
    InstanceId = "USB\VID_0000&PID_0002\failed-port-two"
    ParentInstanceId = $validHub.InstanceId
    ProblemCode = 43
    LocationInfo = "Port_#0002.Hub_#0012"
    Present = $true
}
$validBinding = [pscustomobject]@{
    SchemaVersion = 2
    HubInstanceId = $validHub.InstanceId
    DisplayInstanceId = "USB\VID_1A86&PID_AD23\verified-hs2-display"
    DisplayInterfaceInstanceId = "USB\VID_1A86&PID_AD23&MI_00\verified-hs2-display-interface"
    LedInstanceId = "USB\VID_0416&PID_8051\verified-hs2-led"
}
$validLedSibling = [pscustomobject]@{
    InstanceId = $validBinding.LedInstanceId
    ParentInstanceId = $validHub.InstanceId
    Present = $true
    Status = "OK"
    ProblemCode = 0
}
$validUsbPlan = Get-HS2UsbRecoveryPlan `
    -BoundHubInstanceId $validHub.InstanceId `
    -Hubs @($validHub) `
    -Children @($validChild) `
    -Binding $validBinding `
    -Devices @($validLedSibling)
if (-not $validUsbPlan.Applicable -or
    $validUsbPlan.HubInstanceId -cne $validHub.InstanceId -or
    $validUsbPlan.ChildInstanceId -cne $validChild.InstanceId -or
    ($validUsbPlan.Operations -join ",") -cne "RestartDedicatedHub,RemoveExactFailedChild,ScanDevices") {
    throw "HS2 USB recovery must target only the dedicated hub and its exact port-two Code 43 child."
}

# A missing AD23 with no exact Code 43 child is not safe evidence for any PnP
# mutation.  It can be normal boot enumeration, a firmware wedge, or a hot
# unplug in progress, so automatic recovery must fail closed.
$displayMissingPlan = Get-HS2UsbRecoveryPlan `
    -BoundHubInstanceId $validHub.InstanceId `
    -Hubs @($validHub) `
    -Children @() `
    -Binding $validBinding `
    -Devices @($validLedSibling)
if ($displayMissingPlan.Applicable -or
    $displayMissingPlan.Reason -cne "exact-code43-child-not-found" -or
    @($displayMissingPlan.Operations).Count -ne 0) {
    throw "An absent AD23 without the exact Code 43 child must never trigger PnP recovery."
}

foreach ($invalidExactCode43Case in @(
        [pscustomobject]@{
            Name = "unhealthy dedicated hub"
            Hubs = @([pscustomobject]@{
                    InstanceId = $validHub.InstanceId
                    Present = $true
                    Status = "Unknown"
                    ProblemCode = 43
                })
            Devices = @($validLedSibling)
        },
        [pscustomobject]@{
            Name = "missing LED sibling"
            Hubs = @($validHub)
            Devices = @()
        },
        [pscustomobject]@{
            Name = "LED on a different parent"
            Hubs = @($validHub)
            Devices = @([pscustomobject]@{
                    InstanceId = $validBinding.LedInstanceId
                    ParentInstanceId = "USB\VID_1A86&PID_8091\different-hub"
                    Present = $true
                    Status = "OK"
                    ProblemCode = 0
                })
        }
    )) {
    $invalidExactCode43Plan = Get-HS2UsbRecoveryPlan `
        -BoundHubInstanceId $validHub.InstanceId `
        -Hubs $invalidExactCode43Case.Hubs `
        -Children @($validChild) `
        -Binding $validBinding `
        -Devices $invalidExactCode43Case.Devices
    if ($invalidExactCode43Plan.Applicable) {
        throw "Exact Code 43 recovery must fail closed for $($invalidExactCode43Case.Name)."
    }
}

$graceStart = [DateTime]::Parse("2026-08-11T12:00:00Z").ToUniversalTime()
$startupGraceWait = Get-HS2RecoveryEscalationDecision `
    -WatchdogStartedUtc $graceStart `
    -NowUtc $graceStart.AddSeconds(119) `
    -GraceSeconds 120
if ($startupGraceWait.Action -cne "Wait") {
    throw "HS2 startup recovery escalation must remain passive during the stabilization window."
}
$startupGraceElapsed = Get-HS2RecoveryEscalationDecision `
    -WatchdogStartedUtc $graceStart `
    -NowUtc $graceStart.AddSeconds(120) `
    -GraceSeconds 120
if ($startupGraceElapsed.Action -cne "Allow") {
    throw "HS2 recovery escalation may be evaluated after the stabilization window expires."
}

# Native-controller recovery intentionally has no AD23 display.  A previously
# verified dedicated hub with a present, healthy A068 endpoint is still
# sufficient physical evidence for the one bounded L-Connect restart when its
# API is empty.  An A068 endpoint on any other hub must not be accepted.
$nativeLConnectBindingPath = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("hs2-native-lconnect-binding-{0}.json" -f [Guid]::NewGuid().ToString("N"))
try {
    [IO.File]::WriteAllText(
        $nativeLConnectBindingPath,
        ($validBinding | ConvertTo-Json -Compress),
        [Text.UTF8Encoding]::new($false))
    $nativeRecoveryHub = [pscustomobject]@{
        InstanceId = $validHub.InstanceId
        ParentInstanceId = ""
        Present = $true
        Status = "OK"
        ProblemCode = 0
    }
    $nativeA068Endpoint = [pscustomobject]@{
        InstanceId = "USB\VID_1CBE&PID_A068\native-hs2-controller"
        ParentInstanceId = $validHub.InstanceId
        Present = $true
        Status = "OK"
        ProblemCode = 0
    }
    $bootloaderEndpoint = [pscustomobject]@{
        InstanceId = "USB\VID_A108&PID_EAEF\bootloader-hs2-controller"
        ParentInstanceId = $validHub.InstanceId
        Present = $true
        Status = "Error"
        ProblemCode = 28
        LocationInfo = "Port_#0002.Hub_#0012"
    }
    $bootloaderReadiness = Get-HS2ControllerReadiness `
        -BindingPath $nativeLConnectBindingPath `
        -Snapshot ([pscustomobject]@{ Devices = @($nativeRecoveryHub, $bootloaderEndpoint) })
    if ($bootloaderReadiness.Action -cne "Wait" -or
        $bootloaderReadiness.Reason -cne "bound-controller-in-bootloader-mode") {
        throw "An exact A108/EAEF endpoint on bound hub port two must wait without calling L-Connect or mutating PnP."
    }
    $nativeControllerReadiness = Get-HS2ControllerReadiness `
        -BindingPath $nativeLConnectBindingPath `
        -Snapshot ([pscustomobject]@{ Devices = @($nativeRecoveryHub, $nativeA068Endpoint) })
    if ($nativeControllerReadiness.Action -cne "Ready" -or
        $nativeControllerReadiness.EndpointInstanceId -cne $nativeA068Endpoint.InstanceId) {
        throw "A single healthy A068 endpoint on the bound hub must resume normal L-Connect activation."
    }
    $missingControllerReadiness = Get-HS2ControllerReadiness `
        -BindingPath $nativeLConnectBindingPath `
        -Snapshot ([pscustomobject]@{ Devices = @($nativeRecoveryHub) })
    if ($missingControllerReadiness.Action -cne "Wait" -or
        $missingControllerReadiness.Reason -cne "bound-controller-endpoint-missing") {
        throw "A missing controller endpoint must wait without repeatedly probing L-Connect."
    }
    $nativeLConnectEligibility = Get-HS2LConnectServiceRecoveryEligibility `
        -BindingPath $nativeLConnectBindingPath `
        -Snapshot ([pscustomobject]@{ Devices = @($nativeRecoveryHub, $nativeA068Endpoint) })
    if (-not $nativeLConnectEligibility.Eligible -or
        $nativeLConnectEligibility.EndpointInstanceId -cne $nativeA068Endpoint.InstanceId) {
        throw "A present A068 endpoint on the previously bound healthy hub must permit the one L-Connect recovery decision."
    }

    # Moving the AIO cable from an unsupported inline USB hub to the
    # motherboard header changes the Windows instance id of the same 8091
    # device. A unique new 8091 + port-two controller + port-three LED topology
    # must be admitted provisionally so the verified AD23 path can replace the
    # persisted binding. Identity ambiguity must still fail closed.
    $reenumeratedHub = [pscustomobject]@{
        InstanceId = "USB\VID_1A86&PID_8091\reenumerated-direct-hub"
        ParentInstanceId = "USB\ROOT_HUB30\direct-root"
        Present = $true
        Status = "OK"
        ProblemCode = 0
    }
    $reenumeratedLed = [pscustomobject]@{
        InstanceId = $validBinding.LedInstanceId
        ParentInstanceId = $reenumeratedHub.InstanceId
        Present = $true
        Status = "OK"
        ProblemCode = 0
        LocationInfo = "Port_#0003.Hub_#0008"
    }
    $reenumeratedA068 = [pscustomobject]@{
        InstanceId = "USB\VID_1CBE&PID_A068\reenumerated-controller"
        ParentInstanceId = $reenumeratedHub.InstanceId
        Present = $true
        Status = "OK"
        ProblemCode = 0
        LocationInfo = "Port_#0002.Hub_#0008"
    }
    $reenumeratedSnapshot = [pscustomobject]@{
        Devices = @($reenumeratedHub, $reenumeratedLed, $reenumeratedA068)
    }
    $reenumeratedReadiness = Get-HS2ControllerReadiness `
        -BindingPath $nativeLConnectBindingPath `
        -Snapshot $reenumeratedSnapshot
    if ($reenumeratedReadiness.Action -cne "Ready" -or
        -not [bool]$reenumeratedReadiness.RebindRequired -or
        $reenumeratedReadiness.ResolvedHubInstanceId -cne $reenumeratedHub.InstanceId -or
        $reenumeratedReadiness.EndpointInstanceId -cne $reenumeratedA068.InstanceId) {
        throw "A unique healthy HS2 topology re-enumerated on a direct motherboard header must resume activation and request binding replacement."
    }
    $reenumeratedEligibility = Get-HS2LConnectServiceRecoveryEligibility `
        -BindingPath $nativeLConnectBindingPath `
        -Snapshot $reenumeratedSnapshot
    if (-not $reenumeratedEligibility.Eligible -or
        -not [bool]$reenumeratedEligibility.RebindRequired -or
        $reenumeratedEligibility.ResolvedHubInstanceId -cne $reenumeratedHub.InstanceId) {
        throw "A uniquely re-enumerated direct-header topology must permit the one bounded L-Connect service recovery."
    }
    $reenumeratedBootloader = $bootloaderEndpoint.PSObject.Copy()
    $reenumeratedBootloader.ParentInstanceId = $reenumeratedHub.InstanceId
    $reenumeratedBootloader.LocationInfo = "Port_#0002.Hub_#0008"
    $reenumeratedBootloaderReadiness = Get-HS2ControllerReadiness `
        -BindingPath $nativeLConnectBindingPath `
        -Snapshot ([pscustomobject]@{
            Devices = @($reenumeratedHub, $reenumeratedLed, $reenumeratedBootloader)
        })
    if ($reenumeratedBootloaderReadiness.Action -cne "Wait" -or
        $reenumeratedBootloaderReadiness.Reason -cne "bound-controller-in-bootloader-mode" -or
        -not [bool]$reenumeratedBootloaderReadiness.RebindRequired) {
        throw "A re-enumerated A108 bootloader topology must remain passive while retaining the future binding-replacement receipt."
    }
    $secondReenumeratedHub = $reenumeratedHub.PSObject.Copy()
    $secondReenumeratedHub.InstanceId = "USB\VID_1A86&PID_8091\second-direct-hub"
    $secondReenumeratedLed = $reenumeratedLed.PSObject.Copy()
    $secondReenumeratedLed.InstanceId = "USB\VID_0416&PID_8051\second-led"
    $secondReenumeratedLed.ParentInstanceId = $secondReenumeratedHub.InstanceId
    $secondReenumeratedA068 = $reenumeratedA068.PSObject.Copy()
    $secondReenumeratedA068.InstanceId = "USB\VID_1CBE&PID_A068\second-controller"
    $secondReenumeratedA068.ParentInstanceId = $secondReenumeratedHub.InstanceId
    $ambiguousRebindReadiness = Get-HS2ControllerReadiness `
        -BindingPath $nativeLConnectBindingPath `
        -Snapshot ([pscustomobject]@{
            Devices = @(
                $reenumeratedHub,
                $reenumeratedLed,
                $reenumeratedA068,
                $secondReenumeratedHub,
                $secondReenumeratedLed,
                $secondReenumeratedA068)
        })
    if ($ambiguousRebindReadiness.Action -cne "Wait" -or
        $ambiguousRebindReadiness.Reason -cne "controller-topology-rebind-ambiguous") {
        throw "Multiple complete re-enumerated HS2 topologies must fail closed."
    }
    $nativeApiEmptyFirstDecision = Get-HS2LConnectRecoveryFollowUpDecision `
        -DisplayHealthy $nativeLConnectEligibility.Eligible `
        -RecoveryAttempted $false `
        -RecoveryStartedUtc ([DateTime]::MinValue) `
        -NowUtc $graceStart.AddSeconds(120) `
        -GraceSeconds 90
    if ($nativeApiEmptyFirstDecision.Action -cne "RestartService") {
        throw "A068 present plus controller API failure must allow exactly one L-Connect restart."
    }
    $nativeApiEmptyAfterAttemptDecision = Get-HS2LConnectRecoveryFollowUpDecision `
        -DisplayHealthy $nativeLConnectEligibility.Eligible `
        -RecoveryAttempted $true `
        -RecoveryStartedUtc $graceStart.AddSeconds(120) `
        -NowUtc $graceStart.AddSeconds(121) `
        -GraceSeconds 90
    if ($nativeApiEmptyAfterAttemptDecision.Action -ceq "RestartService") {
        throw "An A068-backed L-Connect recovery must not restart the service more than once per watchdog epoch."
    }
    $foreignA068Endpoint = [pscustomobject]@{
        InstanceId = "USB\VID_1CBE&PID_A068\foreign-controller"
        ParentInstanceId = "USB\VID_1A86&PID_8091\other-hub"
        Present = $true
        Status = "OK"
        ProblemCode = 0
    }
    $foreignNativeEligibility = Get-HS2LConnectServiceRecoveryEligibility `
        -BindingPath $nativeLConnectBindingPath `
        -Snapshot ([pscustomobject]@{ Devices = @($nativeRecoveryHub, $foreignA068Endpoint) })
    if ($foreignNativeEligibility.Eligible) {
        throw "An A068 endpoint not parented by the previously bound hub must fail closed."
    }

    $duplicateAd23Endpoint = [pscustomobject]@{
        InstanceId = "USB\VID_1A86&PID_AD23\duplicate-controller"
        ParentInstanceId = $validHub.InstanceId
        Present = $true
        Status = "OK"
        ProblemCode = 0
    }
    $ambiguousControllerEligibility = Get-HS2LConnectServiceRecoveryEligibility `
        -BindingPath $nativeLConnectBindingPath `
        -Snapshot ([pscustomobject]@{
            Devices = @($nativeRecoveryHub, $nativeA068Endpoint, $duplicateAd23Endpoint)
        })
    if ($ambiguousControllerEligibility.Eligible -or
        $ambiguousControllerEligibility.Reason -cne "bound-controller-endpoint-ambiguous") {
        throw "Multiple healthy controller endpoints on the bound hub must fail closed."
    }
    $ambiguousControllerReadiness = Get-HS2ControllerReadiness `
        -BindingPath $nativeLConnectBindingPath `
        -Snapshot ([pscustomobject]@{
            Devices = @($nativeRecoveryHub, $nativeA068Endpoint, $bootloaderEndpoint)
        })
    if ($ambiguousControllerReadiness.Action -cne "Wait" -or
        $ambiguousControllerReadiness.Reason -cne "bound-controller-endpoint-ambiguous") {
        throw "A simultaneous normal and bootloader identity on the bound hub must fail closed."
    }
}
finally {
    Remove-Item -LiteralPath $nativeLConnectBindingPath -Force -ErrorAction SilentlyContinue
}

$stableProbeValues = [Collections.Generic.Queue[bool]]::new()
foreach ($value in @($false, $true, $false, $true, $true)) {
    $stableProbeValues.Enqueue($value)
}
$stableProbeCalls = 0
$stableDisplayReady = Wait-HS2UsbDisplayHealthy `
    -TimeoutSeconds 1 `
    -PollMilliseconds 1 `
    -RequiredConsecutiveSamples 2 `
    -HealthProbe {
        $script:stableProbeCalls++
        return $script:stableProbeValues.Dequeue()
    } `
    -DelayAction { param([int]$Milliseconds) }
if (-not $stableDisplayReady -or $stableProbeCalls -ne 5) {
    throw "HS2 display admission must require two consecutive healthy physical samples."
}

$healthyDisplayInterface = [pscustomobject]@{
    InstanceId = $validBinding.DisplayInterfaceInstanceId
    ParentInstanceId = $validBinding.DisplayInstanceId
    Present = $true
    Status = "OK"
    ProblemCode = 0
}
$healthyBindingSnapshot = [pscustomobject]@{
    Devices = @(
        $validHub,
        [pscustomobject]@{
            InstanceId = $validBinding.DisplayInstanceId
            ParentInstanceId = $validHub.InstanceId
            Present = $true
            Status = "OK"
            ProblemCode = 0
        },
        $healthyDisplayInterface,
        [pscustomobject]@{
            InstanceId = $validBinding.LedInstanceId
            ParentInstanceId = $validHub.InstanceId
            Present = $true
            Status = "OK"
            ProblemCode = 0
        }
    )
}
$healthyBinding = Get-HS2HealthyUsbTopologyBinding -Snapshot $healthyBindingSnapshot
if ($null -eq $healthyBinding -or
    [string]$healthyBinding.DisplayInstanceId -ine [string]$validBinding.DisplayInstanceId) {
    throw "HS2 display admission must resolve one exact healthy hub/display/LED topology."
}
$missingDisplayInterfaceBinding = Get-HS2HealthyUsbTopologyBinding -Snapshot ([pscustomobject]@{
        Devices = @($healthyBindingSnapshot.Devices | Where-Object {
                [string]$_.InstanceId -notlike "*`&MI_00`*"
            })
    })
if ($null -ne $missingDisplayInterfaceBinding) {
    throw "An AD23 composite without its MI_00 display interface must fail closed."
}
$failedDisplayInterface = $healthyDisplayInterface.PSObject.Copy()
$failedDisplayInterface.Status = "Unknown"
$failedDisplayInterface.ProblemCode = 31
$failedInterfaceBinding = Get-HS2HealthyUsbTopologyBinding -Snapshot ([pscustomobject]@{
        Devices = @($healthyBindingSnapshot.Devices | Where-Object {
                [string]$_.InstanceId -ine [string]$healthyDisplayInterface.InstanceId
            }) + @($failedDisplayInterface)
    })
if ($null -ne $failedInterfaceBinding) {
    throw "An AD23 MI_00 interface with a nonzero problem code must fail closed."
}
$foreignDisplayInterface = $healthyDisplayInterface.PSObject.Copy()
$foreignDisplayInterface.ParentInstanceId = "USB\VID_1A86&PID_AD23\different-composite"
$foreignInterfaceBinding = Get-HS2HealthyUsbTopologyBinding -Snapshot ([pscustomobject]@{
        Devices = @($healthyBindingSnapshot.Devices | Where-Object {
                [string]$_.InstanceId -ine [string]$healthyDisplayInterface.InstanceId
            }) + @($foreignDisplayInterface)
    })
if ($null -ne $foreignInterfaceBinding) {
    throw "An AD23 MI_00 interface parented by another composite must fail closed."
}
$missingLedBinding = Get-HS2HealthyUsbTopologyBinding -Snapshot ([pscustomobject]@{
        Devices = @($healthyBindingSnapshot.Devices | Where-Object {
                [string]$_.InstanceId -ine [string]$validBinding.LedInstanceId
            })
    })
if ($null -ne $missingLedBinding) {
    throw "An AD23 display without its exact healthy LED sibling must fail closed."
}

$automaticUsbRecoverySuppressed = Get-HS2UsbAutomaticRecoveryDecision `
    -Enabled $false `
    -PlanApplicable $true
if ($automaticUsbRecoverySuppressed.Action -cne "Suppress" -or
    $automaticUsbRecoverySuppressed.Reason -cne "automatic-usb-pnp-recovery-disabled") {
    throw "Scheduled HS2 recovery must suppress PnP mutation unless it is explicitly enabled."
}

$automaticUsbRecoveryDispatch = Get-HS2UsbAutomaticRecoveryDecision `
    -Enabled $true `
    -PlanApplicable $true
if ($automaticUsbRecoveryDispatch.Action -cne "Dispatch") {
    throw "An explicit manual opt-in must still expose the bounded HS2 PnP recovery path."
}

$automaticUsbRecoveryNoPlan = Get-HS2UsbAutomaticRecoveryDecision `
    -Enabled $true `
    -PlanApplicable $false
if ($automaticUsbRecoveryNoPlan.Action -cne "NoPlan") {
    throw "HS2 PnP recovery must remain unavailable when no exact recovery plan exists."
}

foreach ($invalidMissingDisplayCase in @(
        [pscustomobject]@{
            Name = "missing LED sibling"
            Binding = $validBinding
            Hubs = $null
            Devices = @()
        },
        [pscustomobject]@{
            Name = "LED on a different parent"
            Binding = $validBinding
            Hubs = $null
            Devices = @([pscustomobject]@{
                    InstanceId = $validBinding.LedInstanceId
                    ParentInstanceId = "USB\VID_1A86&PID_8091\different-hub"
                    Present = $true
                    Status = "OK"
                    ProblemCode = 0
                })
        },
        [pscustomobject]@{
            Name = "unhealthy LED sibling"
            Binding = $validBinding
            Hubs = $null
            Devices = @([pscustomobject]@{
                    InstanceId = $validBinding.LedInstanceId
                    ParentInstanceId = $validHub.InstanceId
                    Present = $true
                    Status = "Unknown"
                    ProblemCode = 43
                })
        },
        [pscustomobject]@{
            Name = "unhealthy dedicated hub"
            Binding = $validBinding
            Hubs = @([pscustomobject]@{
                    InstanceId = $validHub.InstanceId
                    Present = $true
                    Status = "Unknown"
                    ProblemCode = 43
                })
            Devices = @($validLedSibling)
        },
        [pscustomobject]@{
            Name = "AD23 already healthy"
            Binding = $validBinding
            Hubs = $null
            Devices = @(
                $validLedSibling,
                [pscustomobject]@{
                    InstanceId = $validBinding.DisplayInstanceId
                    ParentInstanceId = $validHub.InstanceId
                    Present = $true
                    Status = "OK"
                    ProblemCode = 0
                })
        }
    )) {
    $invalidMissingDisplayPlan = Get-HS2UsbRecoveryPlan `
        -BoundHubInstanceId $validHub.InstanceId `
        -Hubs $(if ($null -ne $invalidMissingDisplayCase.Hubs) { $invalidMissingDisplayCase.Hubs } else { @($validHub) }) `
        -Children @() `
        -Binding $invalidMissingDisplayCase.Binding `
        -Devices $invalidMissingDisplayCase.Devices
    if ($invalidMissingDisplayPlan.Applicable) {
        throw "Bound HS2 display-missing recovery must fail closed for $($invalidMissingDisplayCase.Name)."
    }
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
    -Children @($freshChild) `
    -Binding $validBinding `
    -Devices @($validLedSibling)
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
    -Binding $validBinding `
    -TimeoutSeconds 1 `
    -PollMilliseconds 1 `
    -SnapshotProvider {
        $script:postRestartSnapshotAttempts++
        [pscustomobject]@{
            Hubs = @($validHub)
            Devices = @($validLedSibling)
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
$script:mockHS2PnpPresent = $true
function Get-PnpDevice {
    [CmdletBinding()]
    param([switch]$PresentOnly)
    return $script:mockHS2PnpDevices
}
function Get-PnpDeviceProperty {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$InstanceId,
        [Parameter(Mandatory = $true)][string]$KeyName
    )
    $device = @(
        $script:mockHS2PnpDevices | Where-Object {
            [string]$_.InstanceId -ieq $InstanceId
        }
    )[0]
    $data = switch ($KeyName) {
        "DEVPKEY_Device_IsPresent" {
            if ($null -ne $device -and $device.PSObject.Properties.Name -contains "Present") {
                [bool]$device.Present -and $script:mockHS2PnpPresent
            }
            else {
                $script:mockHS2PnpPresent
            }
            break
        }
        "DEVPKEY_Device_ProblemCode" {
            if ($null -ne $device -and $device.PSObject.Properties.Name -contains "ProblemCode") {
                [int]$device.ProblemCode
            }
            else {
                0
            }
            break
        }
        "DEVPKEY_Device_Parent" {
            if ($null -ne $device -and $device.PSObject.Properties.Name -contains "ParentInstanceId") {
                [string]$device.ParentInstanceId
            }
            else {
                $null
            }
            break
        }
        default { $null; break }
    }
    return [pscustomobject]@{ Data = $data }
}
try {
    $script:mockHS2PnpDevices = @(
        [pscustomobject]@{
            InstanceId = "USB\VID_1CBE&PID_A068\native-mode"
            Present = $true
            Status = "OK"
            ProblemCode = 0
        }
    )
    if (Test-HS2UsbDisplayHealthy) {
        throw "Native A068 controller mode must not be treated as a healthy Windows display."
    }

    $script:mockHS2PnpDevices = @(
        [pscustomobject]@{
            InstanceId = "USB\VID_1A86&PID_AD23\failed-display"
            ParentInstanceId = "USB\VID_1A86&PID_8091\verified-hs2-hub"
            Present = $true
            Status = "Unknown"
            ProblemCode = 43
        },
        [pscustomobject]@{
            InstanceId = "USB\VID_1A86&PID_AD23&MI_00\failed-display-interface"
            ParentInstanceId = "USB\VID_1A86&PID_AD23\failed-display"
            Present = $true
            Status = "Unknown"
            ProblemCode = 43
        }
    )
    if (Test-HS2UsbDisplayHealthy) {
        throw "An unhealthy AD23 device must not trigger L-Connect-only recovery."
    }

    $script:mockHS2PnpDevices = @(
        [pscustomobject]@{
            InstanceId = "USB\VID_1A86&PID_AD23\healthy-display"
            ParentInstanceId = "USB\VID_1A86&PID_8091\verified-hs2-hub"
            Present = $true
            Status = "OK"
            ProblemCode = 0
        },
        [pscustomobject]@{
            InstanceId = "USB\VID_1A86&PID_AD23&MI_00\healthy-display-interface"
            ParentInstanceId = "USB\VID_1A86&PID_AD23\healthy-display"
            Present = $true
            Status = "OK"
            ProblemCode = 0
        }
    )
    $script:mockHS2PnpPresent = $false
    if (Test-HS2UsbDisplayHealthy) {
        throw "A disconnected historical AD23 devnode must not be treated as a healthy Windows display."
    }
    $script:mockHS2PnpPresent = $true
    if (-not (Test-HS2UsbDisplayHealthy)) {
        throw "A healthy AD23 display must permit bounded L-Connect recovery."
    }
}
finally {
    Remove-Item Function:\Get-PnpDevice -Force
    Remove-Item Function:\Get-PnpDeviceProperty -Force
}

$activeRecoveryText = Get-Content -Raw -LiteralPath $activeRecoveryPolicy
if ($activeRecoveryText -notmatch [regex]::Escape('USB\VID_1A86&PID_AD23&MI_00\*')) {
    throw "HS2 USB recovery snapshots must retain the AD23 MI_00 display interface."
}
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
foreach ($productionPath in @($watchdog, $displayPowerPolicy, $activeRecoveryPolicy)) {
    $productionText = Get-Content -Raw -LiteralPath $productionPath
    foreach ($forbiddenPrimaryOrVddToken in @(
            "MTT1337",
            "PHLC34B",
            "ensure_only_display",
            "ensure_primary",
            "ChangeDisplaySettings",
            "SetDisplayConfig",
            "DisplaySwitch")) {
        if ($productionText -match [regex]::Escape($forbiddenPrimaryOrVddToken)) {
            throw "HS2 production must not touch the physical primary or VDD display path: $forbiddenPrimaryOrVddToken"
        }
    }
}
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
    "Set-HS2PreservedActiveState",
    "Set-HS2VerifiedSecondaryState",
    "Invoke-HS2InitialActiveMaintenance",
    "Test-HS2CurrentSecondaryBindingHealthy",
    "Invoke-HS2SecondaryActiveMaintenance",
    "Set-HS2NativeActiveState",
    "Invoke-HS2SecondaryPromotion",
    "Get-HS2SecondaryPromotionDecision",
    "Get-HS2ResumeEventDecision",
    "hs2LastResumeHandledUtc",
    "HS2SecondaryPromotionGraceSeconds = 30",
    "HS2LConnectRecoveryStartupGraceSeconds = 120",
        "Invoke-HS2NativeLConnectServiceRecovery",
        "Get-HS2ControllerReadiness",
        "hs2ControllerReadinessWaitReason",
        "Get-HS2LConnectServiceRecoveryEligibility",
    "Get-HS2LConnectRecoveryFollowUpDecision",
    "HS2ActiveSlowRetrySeconds",
    "hs2-usb-topology-binding.json",
    "HS2 overlay watchdog=secondary-verified-only",
    "mode=one-attempt-per-startup-or-resume-epoch",
    "fallback=verified-native",
    "HS2OverlayRetrySeconds = 30",
    "SetTurzxBrightness.ps1",
    "ActiveBrightness = 170",
    "Enter-SleepDisplayState",
    "Enter-ShutdownDisplayState",
    "Set-ActiveDisplayState",
    "fallback=black-frame",
    "display power policy=hs2-preserve-current-mode-then-verified-secondary/turzx-brightness-123",
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
    "FailureCircuitBreakerSeconds = 30"
    "failure circuit open"
    "failure circuit closed"
)) {
    if ($watchdogText -notmatch [regex]::Escape($pattern)) {
        throw "Watchdog missing expected pattern: $pattern"
    }
}
if ($watchdogText -match 'watchdog paused') {
    throw "Repeated stream failures must enter a bounded retry circuit instead of permanently pausing the watchdog."
}

$stopText = Get-Content -Raw -LiteralPath $stop
foreach ($pattern in @(
        "Wait-TurzxStreamProcessesExit",
        "stream processes did not exit",
        'side-screen-stack.pid',
        'Stop-RecordedWatchdogProcess',
        'side-screen-watchdog.pid',
        'recorded watchdog identity not verified')) {
    if ($stopText -notmatch [regex]::Escape($pattern)) {
        throw "Stack stop must prove the old stream released COM before restart; missing: $pattern"
    }
}
if ($stopText -notmatch '(?s)if\s*\(\$IncludeWatchdog\).*?Stop-RecordedWatchdogProcess.*?Stop-MatchingProcess\s+-Reason\s+"stream-exe"') {
    throw "IncludeWatchdog must stop the verified recorded owner before stopping the stream, otherwise the old watchdog can respawn it."
}
if ($watchdogText -notmatch '(?s)function Stop-Stack.*?\$LASTEXITCODE\s+-ne\s+0.*?throw') {
    throw "Watchdog Start-Stack must fail closed when StopSideScreenStack cannot prove stream exit."
}
foreach ($powerTransition in @(
        [pscustomobject]@{ Function = 'Enter-SleepDisplayState'; Reason = 'suspend'; State = 'Sleep' },
        [pscustomobject]@{ Function = 'Enter-ShutdownDisplayState'; Reason = 'shutdown'; State = 'Shutdown' }
    )) {
    $pattern = '(?s)function\s+{0}.*?try\s*\{{\s*Stop-Stack\s+-Reason\s+"{1}"\s*\}}\s*catch.*?Invoke-HS2PowerState\s+-State\s+{2}' -f `
        [regex]::Escape($powerTransition.Function),
        [regex]::Escape($powerTransition.Reason),
        [regex]::Escape($powerTransition.State)
    if ($watchdogText -notmatch $pattern) {
        throw "$($powerTransition.State) must still execute the HS2 power policy when stream-stop proof fails."
    }
}

if ($watchdogText -match '(?s)function Set-ActiveDisplayState.*?\$script:hs2DisplayStateActive\s*=\s*\$true.*?Invoke-HS2PowerState') {
    throw "HS2 Active must never be marked verified before L-Connect read-back succeeds."
}
if ($watchdogText -notmatch '\$null\s*-eq\s*\$result\s*-or\s*-not\s*\[bool\]\$result\.Verified') {
    throw "HS2 Active requires an explicit Verified=true result."
}
if ($watchdogText -match '(?i)Invoke-HS2UsbRecovery|EnableHS2UsbPnPRecovery') {
    throw "The preserved-mode recovery epoch must never mutate USB/PnP after a secondary-display failure."
}
foreach ($startupEntry in @($installer, $watchdogLauncher, $stack, $start)) {
    $startupEntryText = Get-Content -Raw -LiteralPath $startupEntry
    if ($startupEntryText -match '(?i)-EnableSecondaryScreen|-EnableHS2UsbPnPRecovery') {
        throw "Production startup must not force a secondary-display or USB recovery mode: $startupEntry"
    }
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

function Get-WatchdogFunctionText {
    param([Parameter(Mandatory = $true)][string]$Name)

    $matches = @(
        $watchdogAst.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -ceq $Name
            },
            $true))
    if ($matches.Count -ne 1) {
        throw "Watchdog must define exactly one $Name function."
    }
    return $matches[0].Extent.Text
}

$preservedActiveText = Get-WatchdogFunctionText -Name "Set-HS2PreservedActiveState"
foreach ($pattern in @(
        "Invoke-HS2PowerState",
        "State Active",
        "17104897",
        "Set-HS2VerifiedSecondaryState",
        "17104896")) {
    if ($preservedActiveText -notmatch [regex]::Escape($pattern)) {
        throw "Preserved HS2 Active is missing its current-controller contract: $pattern"
    }
}
if ($preservedActiveText -match '(?i)-EnableSecondaryScreen') {
    throw "Preserved HS2 Active must not force either controller mode."
}
if ($preservedActiveText -match '(?i)Set-HS2NativeActiveState') {
    throw "An already-secondary HS2 controller must not be demoted by the preserved Active path."
}
if ($preservedActiveText -notmatch '(?s)17104897.*?Set-HS2VerifiedSecondaryState') {
    throw "An already-secondary HS2 controller must be verified/bound before overlay activation."
}
if ($preservedActiveText -notmatch '(?s)17104896.*?(Set-HS2NativeStateFromResult|hs2NativeDisplayStateActive\s*=\s*\$true)') {
    throw "A native HS2 controller must remain native and enter the 30-second promotion phase."
}

$verifiedSecondaryText = Get-WatchdogFunctionText -Name "Set-HS2VerifiedSecondaryState"
foreach ($pattern in @(
        "17104897",
        "Wait-HS2UsbDisplayHealthy",
        "Save-HS2UsbTopologyBinding",
        "Enable-DesktopWindowPreservation",
        "hs2DisplayStateActive = `$true")) {
    if ($verifiedSecondaryText -notmatch [regex]::Escape($pattern)) {
        throw "Initial secondary HS2 verification is missing its AD23/binding/window contract: $pattern"
    }
}
if ($verifiedSecondaryText -match '(?i)-EnableSecondaryScreen') {
    throw "Initial secondary verification must not demote or re-promote an already-secondary controller."
}

$nativeActiveText = Get-WatchdogFunctionText -Name "Set-HS2NativeActiveState"
foreach ($pattern in @(
        "Invoke-HS2PowerState",
        "Set-HS2NativeStateFromResult",
        "PreservePromotionBackoff",
        "-EnableSecondaryScreen:`$false")) {
    if ($nativeActiveText -notmatch [regex]::Escape($pattern)) {
        throw "Native HS2 fallback is missing its explicit native-mode contract: $pattern"
    }
}
foreach ($forbiddenPattern in @(
        "Wait-HS2UsbDisplayHealthy",
        "Save-HS2UsbTopologyBinding")) {
    if ($nativeActiveText -match [regex]::Escape($forbiddenPattern)) {
        throw "Native HS2 fallback must not claim secondary topology health: $forbiddenPattern"
    }
}

$promotionText = Get-WatchdogFunctionText -Name "Invoke-HS2SecondaryPromotion"
foreach ($pattern in @(
        "Get-HS2SecondaryPromotionDecision",
        "WaitNativeStability",
        "HoldNative",
        "-EnableSecondaryScreen",
        "17104897",
        "Set-HS2VerifiedSecondaryState",
        "Set-HS2NativeActiveState",
        "PreservePromotionBackoff")) {
    if ($promotionText -notmatch [regex]::Escape($pattern)) {
        throw "HS2 secondary promotion is missing its two-phase safety gate: $pattern"
    }
}
if (([regex]::Matches(
            $promotionText,
            [regex]::Escape("-EnableSecondaryScreen"))).Count -ne 1) {
    throw "Each watchdog epoch may issue SetSecondaryScreen(true) through exactly one promotion site."
}
if ($promotionText -notmatch '(?s)-NativeActive\s+\$script:hs2NativeDisplayStateActive') {
    throw "HS2 secondary promotion must be gated by a verified native 17104896 state."
}

$maintenanceText = Get-WatchdogFunctionText -Name "Invoke-HS2ActiveMaintenance"
$initialMaintenanceText = Get-WatchdogFunctionText -Name "Invoke-HS2InitialActiveMaintenance"
$secondaryMaintenanceText = Get-WatchdogFunctionText -Name "Invoke-HS2SecondaryActiveMaintenance"
$secondaryBindingHealthText = Get-WatchdogFunctionText -Name "Test-HS2CurrentSecondaryBindingHealthy"
foreach ($pattern in @(
        "Get-HS2ActiveRecoveryDecision",
        "HS2ActiveVerifySeconds",
        "Test-HS2CurrentSecondaryBindingHealthy",
        "hs2DisplayStateActive = `$false",
        "hs2OverlayRebindRequired = `$true")) {
    if ($secondaryMaintenanceText -notmatch [regex]::Escape($pattern)) {
        throw "Verified secondary HS2 health maintenance is missing its fail-closed/read-back contract: $pattern"
    }
}
if ($secondaryMaintenanceText -match '(?mi)^(?!\s*#).*?(-EnableSecondaryScreen|SetSecondaryScreen)') {
    throw "Secondary health loss must not issue a controller mode switch."
}
if ($secondaryMaintenanceText -match '\$script:hs2SecondaryLastAttemptUtc\s*=\s*\[DateTime\]::MinValue') {
    throw "Secondary health loss must preserve the current epoch promotion marker."
}
if ($secondaryBindingHealthText -match '(?i)-EnableSecondaryScreen|SetSecondaryScreen') {
    throw "Secondary binding health must remain a read-only controller/AD23/binding probe."
}
if ($maintenanceText -match '(?s)\$script:hs2DisplayStateActive\)\s*\{\s*return\s*\}') {
    throw "A verified secondary display must receive a low-frequency health/read-back check instead of returning forever."
}
if ($maintenanceText -notmatch '(?s)Invoke-HS2SecondaryActiveMaintenance') {
    throw "Active maintenance must dispatch the 30-second verified-secondary health check."
}
if ($initialMaintenanceText -notmatch '(?s)Set-HS2PreservedActiveState') {
    throw "Every startup/resume Active epoch must preserve and verify the controller mode before promotion."
}
$readinessIndex = $initialMaintenanceText.IndexOf(
    "Get-HS2ControllerReadiness",
    [StringComparison]::Ordinal)
$preserveIndex = $initialMaintenanceText.IndexOf(
    "Set-HS2PreservedActiveState",
    [StringComparison]::Ordinal)
if ($readinessIndex -lt 0 -or $preserveIndex -lt 0 -or $readinessIndex -ge $preserveIndex -or
    $initialMaintenanceText -notmatch '(?s)Action\s+-ne\s+"Ready".*?return') {
    throw "HS2 startup/resume must prove one bound normal endpoint before it calls L-Connect; bootloader/missing identities must wait read-only."
}
if ($maintenanceText -notmatch '(?s)Invoke-HS2InitialActiveMaintenance.*?Invoke-HS2SecondaryPromotion') {
    throw "Native-only HS2 Active must reach the once-per-epoch promotion gate after preservation."
}
if ($maintenanceText -notmatch '(?s)Invoke-HS2InitialActiveMaintenance') {
    throw "HS2 Active maintenance must delegate startup/resume recovery to the preserved-mode path."
}
$nativeMaintenanceText = $initialMaintenanceText
if ($nativeMaintenanceText -notmatch '(?s)hs2SecondaryLastAttemptUtc.*?PreservePromotionBackoff:\$preservePromotionAttempt') {
    throw "A delayed native fallback must preserve the one-attempt secondary marker for the full watchdog epoch."
}
if ($nativeMaintenanceText -notmatch '(?s)catch\s*\{.*?Invoke-HS2NativeLConnectServiceRecovery') {
    throw "Only a native controller-readback failure may consider the bounded L-Connect service recovery."
}
$lconnectRecoveryText = Get-WatchdogFunctionText -Name "Invoke-HS2NativeLConnectServiceRecovery"
foreach ($pattern in @(
        "Get-HS2RecoveryEscalationDecision",
        "HS2LConnectRecoveryStartupGraceSeconds",
        "Get-HS2LConnectServiceRecoveryEligibility",
        "Get-HS2LConnectRecoveryFollowUpDecision",
        "Invoke-HS2LConnectServiceRecovery",
        "hs2LConnectRecoveryAttempted = `$true",
        "secondaryAttemptPreserved")) {
    if ($lconnectRecoveryText -notmatch [regex]::Escape($pattern)) {
        throw "The bounded L-Connect recovery is missing its safe eligibility/read-back contract: $pattern"
    }
}
if ($lconnectRecoveryText -match 'hs2SecondaryLastAttemptUtc\s*=') {
    throw "L-Connect service recovery must not clear the one-attempt secondary promotion marker."
}
$activeEpochText = Get-WatchdogFunctionText -Name "Set-ActiveDisplayState"
if ($activeEpochText -notmatch '\$script:hs2SecondaryLastAttemptUtc\s*=\s*\[DateTime\]::MinValue') {
    throw "Only a new startup or resume epoch may clear the one-attempt secondary promotion marker."
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
$overlayHealthAst = @(
    $watchdogAst.FindAll(
        {
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -ceq "Invoke-HS2OverlayHealthCheck"
        },
        $true)
)[0]
if ($overlayHealthAst.Extent.Text -notmatch '(?s)-not\s+\$script:hs2DisplayStateActive.*?Get-HS2OverlayProcess.*?Stop-HS2OverlayForRebind.*?return') {
    throw "An inactive or missing HS2 display must recycle a stale overlay before returning."
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
    -Anchor 'function Set-HS2NativeActiveState' `
    -First '-EnableSecondaryScreen:$false' `
    -Second 'Set-HS2NativeStateFromResult' `
    -Message "HS2 native fallback must explicitly request native mode before applying the verified native state."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'function Set-HS2NativeStateFromResult' `
    -First 'ControllerType -ne 17104896' `
    -Second '$script:hs2NativeDisplayStateActive = $true' `
    -Message "HS2 must establish the native controller before it records a stable native state."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'function Invoke-HS2SecondaryPromotion' `
    -First '-EnableSecondaryScreen' `
    -Second 'Set-HS2VerifiedSecondaryState' `
    -Message "HS2 must enter secondary-display mode before it can verify the AD23 topology."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'function Set-HS2VerifiedSecondaryState' `
    -First 'Wait-HS2UsbDisplayHealthy' `
    -Second 'Save-HS2UsbTopologyBinding' `
    -Message "HS2 must save the exact healthy topology only after the Windows display is present."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'function Set-HS2VerifiedSecondaryState' `
    -First 'Save-HS2UsbTopologyBinding' `
    -Second '$script:hs2DisplayStateActive = $true' `
    -Message "HS2 must be marked secondary-active only after its healthy AD23 binding is persisted."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'function Invoke-HS2SecondaryPromotion' `
    -First 'HS2 secondary promotion failed' `
    -Second 'Set-HS2NativeActiveState' `
    -Message "A failed secondary promotion must restore the native display in the same watchdog epoch."

if ($watchdogText -notmatch '\$script:hs2LastResumeHandledUtc\s*=\s*\[DateTime\]::MinValue') {
    throw "HS2 resume merge state must start empty for a fresh watchdog epoch."
}
if ($watchdogText -notmatch '(?s)Get-HS2ResumeEventDecision.*?-MergeSeconds\s+30') {
    throw "HS2 resume EventType 7/18 handling must use the 30-second merge window."
}
if ($watchdogText -notmatch '(?s)\$resumeDecision\.Action\s+-eq\s+"Ignore"') {
    throw "Duplicate resume events must be ignored before restarting the side-screen stack."
}
Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'elseif ($eventType -eq 7 -or $eventType -eq 18)' `
    -First 'Get-HS2ResumeEventDecision' `
    -Second 'Set-ActiveDisplayState -Reason ("resume-event-{0}" -f $eventType)' `
    -Message "Resume EventType 7/18 must pass the merge decision before resetting the active epoch."

Assert-OrderAfter `
    -Text $watchdogText `
    -Anchor 'if ($eventType -eq 4)' `
    -First '$script:hs2LastResumeHandledUtc = [DateTime]::MinValue' `
    -Second 'Enter-SleepDisplayState' `
    -Message "Suspend EventType 4 must clear the resume merge marker before the next 7/18 event."

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
    "Disable-ScheduledTask",
    "AllowStartIfOnBatteries",
    "DontStopIfGoingOnBatteries"
    "RestartCount 999"
)) {
    if ($installerText -notmatch [regex]::Escape($pattern)) {
        throw "Startup installer must make Task Scheduler own the hidden watchdog process; missing: $pattern"
    }
}
if ($installerText -match [regex]::Escape('-Execute "powershell.exe"')) {
    throw "Startup installer must not execute PowerShell directly in the interactive logon task."
}
if ($installerText -match '(?i)/SC\s+ONEVENT|resumeEventQuery|RestartSideScreenAfterResume-Hidden\.vbs') {
    throw "The installer must not register a second resume recovery owner."
}

if (!(Test-Path -LiteralPath $resume)) {
    throw "Missing resume recovery script: $resume"
}
if (!(Test-Path -LiteralPath $resumeLauncher)) {
    throw "Missing resume recovery hidden launcher: $resumeLauncher"
}

$resumeText = Get-Content -Raw -LiteralPath $resume
foreach ($pattern in @(
    "DelaySeconds",
    "Get-ScheduledTask",
    "Test-ScheduledTaskActionMode",
    "main watchdog owns resume",
    "schtasks.exe",
    "/Run"
)) {
    if ($resumeText -notmatch [regex]::Escape($pattern)) {
        throw "Resume recovery script missing expected pattern: $pattern"
    }
}
foreach ($forbiddenPattern in @(
        "StopSideScreenStack.ps1",
        "pnputil.exe",
        "/restart-device",
        "/End",
        "Restart-TurzxUsbDevice",
        "IncludeWatchdog"
    )) {
    if ($resumeText -match [regex]::Escape($forbiddenPattern)) {
        throw "Deprecated resume compatibility script must be non-destructive; found: $forbiddenPattern"
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

$processSnapshotQueryCount = [regex]::Matches(
    $stopText,
    'Get-CimInstance\s+-ClassName\s+Win32_Process'
).Count
$processSnapshotCaptureCount = [regex]::Matches(
    $stopText,
    '\$processSnapshot\s*=\s*@\(Get-TurzxProcessSnapshot'
).Count
if ($processSnapshotQueryCount -ne 1 -or $processSnapshotCaptureCount -ne 1) {
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
