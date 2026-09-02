param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Port = "COM7",
    [int]$IntervalMs = 3000,
    [ValidateRange(3000, 60000)][int]$SendTimeoutMs = 10000,
    [ValidateRange(100, 5000)][int]$DiffSendTimeoutMs = 900,
    [ValidateRange(1, 10)][int]$MaxConsecutiveSendFailures = 1,
    [int]$FullResyncEveryFrames = 900,
    [int]$PreviewIntervalSeconds = 45,
    [int64]$MaxStackLogBytes = 1048576,
    [int64]$MaxStreamLogBytes = 5242880,
    [int64]$MaxWatchdogLogBytes = 2097152,
    [ValidateRange(1, 32)][int]$LogBackupCount = 3,
    [ValidateRange(5, 120)][int]$StopStackTimeoutSeconds = 30,
    [ValidateRange(1, 30)][int]$StopProcessSnapshotTimeoutSeconds = 8,
    [int]$ResumeDelaySeconds = 8,
    [int]$QuickBlankTimeoutMs = 2500,
    [int]$PollSeconds = 2,
    [int]$HeartbeatStaleSeconds = 15,
    [ValidateRange(3000, 60000)][int]$HeartbeatMaxSendMs = 10000,
    [ValidateRange(3000, 120000)][int]$HeartbeatMaxElapsedMs = 12000,
    [ValidateRange(3000, 120000)][int]$HeartbeatMaxPeriodMs = 15000,
    [int]$HeartbeatStartupGraceSeconds = 60,
    [int]$ShutdownStartupGraceSeconds = 180,
    [int]$MaxConsecutiveHeartbeatFailures = 3,
    [ValidateRange(2, 20)][int]$MaxConsecutiveSnapshotStaleHeartbeats = 5,
    [int]$MaxConsecutiveFailures = 3,
    [ValidateRange(10, 3600)][int]$FailureCircuitBreakerSeconds = 30,
    [ValidateRange(2, 20)][int]$TurzxSerialRecoveryFailureThreshold = 3,
    [ValidateRange(30, 3600)][int]$TurzxSerialRecoveryRetrySeconds = 300,
    [ValidateRange(5, 60)][int]$TurzxSerialRecoveryWaitSeconds = 20,
    [ValidateRange(5, 3600)][int]$HS2OverlayRetrySeconds = 30,
    [ValidateRange(5, 300)][int]$HS2ActiveRetrySeconds = 15,
    [ValidateRange(30, 3600)][int]$HS2ActiveSlowRetrySeconds = 60,
    [ValidateRange(10, 3600)][int]$HS2ActiveVerifySeconds = 30,
    [ValidateRange(10, 600)][int]$HS2SecondaryPromotionGraceSeconds = 30,
    [ValidateRange(30, 600)][int]$HS2LConnectRecoveryStartupGraceSeconds = 120,
    [ValidateRange(5, 60)][int]$HS2LConnectRecoveryRetrySeconds = 5,
    [ValidateRange(15, 300)][int]$HS2LConnectRecoveryGraceSeconds = 90,
    [ValidateRange(10, 300)][int]$WallpaperDisplayProbeSeconds = 15,
    [ValidateRange(15, 600)][int]$WallpaperDisplayStabilitySeconds = 30,
    [ValidateRange(300, 7200)][int]$WallpaperRenderRebindCooldownSeconds = 900,
    [ValidateRange(250, 10000)][int]$WallpaperRenderRebindGapMilliseconds = 1500,
    [ValidateRange(0, 255)][int]$ActiveBrightness = 170,
    [ValidateRange(1, 65535)][int]$LConnectServicePort = 11021,
    [switch]$HybridRefresh,
    [switch]$AltHelper,
    [switch]$NoWindowPreservationPolicy,
    [switch]$NoPowerEvents
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath $Root).Path
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $scriptDir "out"
$logPath = Join-Path $outDir "side-screen-watchdog.log"
$hs2UsbTopologyBindingPath = Join-Path $outDir "hs2-usb-topology-binding.json"
$childPidPath = Join-Path $outDir "side-screen-stack-child.pid"
$watchdogPidPath = Join-Path $outDir "side-screen-watchdog.pid"
$pausedPath = Join-Path $outDir "side-screen-watchdog.paused"
$heartbeatPath = Join-Path $outDir "stream\stream-heartbeat.json"
$heartbeatPaths = @(
    $heartbeatPath,
    (Join-Path $outDir "stream\stream-heartbeat-a.json"),
    (Join-Path $outDir "stream\stream-heartbeat-b.json")
)
$restartFlag = Join-Path $outDir "restart-on-start.flag"
$stackScript = Join-Path $scriptDir "StartSideScreenStack.ps1"
$stopScript = Join-Path $scriptDir "StopSideScreenStack.ps1"
$blankScript = Join-Path $scriptDir "SendBlankFrame.ps1"
$brightnessScript = Join-Path $scriptDir "SetTurzxBrightness.ps1"
$shutdownPolicy = Join-Path $scriptDir "SideScreenWatchdogPolicy.ps1"
$displayPowerPolicy = Join-Path $scriptDir "SideScreenDisplayPowerPolicy.ps1"
$activeRecoveryPolicy = Join-Path $scriptDir "HS2ActiveRecoveryPolicy.ps1"
$windowPreservationPolicy = Join-Path $scriptDir "WindowsDisplayWindowPolicy.ps1"
$overlayWatchdogPolicy = Join-Path $scriptDir "HS2OverlayWatchdogPolicy.ps1"
$powerSourceId = "TURZXSideScreenPower"
$shutdownSourceId = "TURZXSideScreenShutdown"
$watchdogStartedUtc = [DateTime]::UtcNow
$script:hs2OverlayLastAttemptUtc = [DateTime]::MinValue
$script:hs2OverlayWasRunning = $false
$script:hs2DisplayStateActive = $false
$script:hs2DisplayStateDesiredActive = $false
$script:hs2NativeDisplayStateActive = $false
$script:hs2NativeStableSinceUtc = [DateTime]::MinValue
$script:hs2NativeLastAttemptUtc = [DateTime]::MinValue
$script:hs2SecondaryLastAttemptUtc = [DateTime]::MinValue
$script:hs2SecondaryPromotionFailures = 0
$script:hs2SecondaryStabilityWaitLogged = $false
$script:hs2SecondaryHoldLogged = $false
$script:hs2LConnectRecoveryAttempted = $false
$script:hs2LConnectRecoveryStartedUtc = [DateTime]::MinValue
$script:hs2LConnectRecoveryGraceLogged = $false
$script:hs2ControllerReadinessWaitReason = $null
$script:hs2DesktopPathWaitReason = $null
$script:hs2ActiveLastAttemptUtc = [DateTime]::MinValue
$script:hs2ActiveLastVerifiedUtc = [DateTime]::MinValue
$script:hs2ActiveConsecutiveFailures = 0
$script:hs2ActiveCurrentRetrySeconds = $HS2ActiveRetrySeconds
$script:hs2OverlayRebindRequired = $false
$script:hs2WindowGuardTargetMonitorDevice = $null
$script:hs2WindowGuardSafeMonitorDevice = $null
$script:hs2WindowGuardLastStatus = $null
$script:hs2WindowGuardLastFailure = $null
$script:hs2LastResumeHandledUtc = [DateTime]::MinValue
$script:hs2Code10LastSignature = $null
$script:wallpaperDisplayLastProbeUtc = [DateTime]::MinValue
$script:wallpaperDisplayBaselineFingerprint = ""
$script:wallpaperDisplayPendingFingerprint = ""
$script:wallpaperDisplayPendingSinceUtc = [DateTime]::MinValue
$script:wallpaperDisplayLastRebindUtc = [DateTime]::MinValue
$script:wallpaperDisplayLastStatus = $null
$script:turzxStackChild = $null
$script:turzxBrightnessConsecutiveFailures = 0
$script:turzxSerialRecoveryLastAttemptUtc = [DateTime]::MinValue

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
. $shutdownPolicy
. $displayPowerPolicy
. $activeRecoveryPolicy
. $windowPreservationPolicy
. $overlayWatchdogPolicy

function Write-BoundedLogLine {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $true)][int64]$MaxBytes,
        [int]$BackupCount = 3
    )

    try {
        $directory = Split-Path -Parent $Path
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            New-Item -ItemType Directory -Force -Path $directory | Out-Null
        }

        $recordBytes = [Text.Encoding]::UTF8.GetByteCount($Message + [Environment]::NewLine)
        if ($MaxBytes -gt 0 -and
            (Test-Path -LiteralPath $Path -PathType Leaf) -and
            ((Get-Item -LiteralPath $Path).Length + $recordBytes -gt $MaxBytes)) {
            for ($index = $BackupCount; $index -ge 1; $index--) {
                $source = if ($index -eq 1) { $Path } else { "{0}.{1}" -f $Path, ($index - 1) }
                $destination = "{0}.{1}" -f $Path, $index
                if (Test-Path -LiteralPath $source -PathType Leaf) {
                    Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
                    if ((Get-Item -LiteralPath $source).Length -gt $MaxBytes) {
                        Remove-Item -LiteralPath $source -Force
                    }
                    else {
                        Move-Item -LiteralPath $source -Destination $destination -Force
                    }
                }
            }
        }

        Add-Content -LiteralPath $Path -Value $Message -Encoding UTF8
    }
    catch {
        # Diagnostics are best effort: a log viewer must never stop the watchdog.
    }
}

function Write-WatchdogLog {
    param([string]$Message)
    $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Write-BoundedLogLine -Path $logPath -Message $line -MaxBytes $MaxWatchdogLogBytes -BackupCount $LogBackupCount
    Write-Host $line
}

function Write-HS2Code10FailClosedWarning {
    param([object]$Readiness)

    $deviceIds = @()
    if ($null -ne $Readiness -and
        $null -ne $Readiness.PSObject.Properties["Code10DeviceInstanceIds"]) {
        $deviceIds = @($Readiness.Code10DeviceInstanceIds | Sort-Object -Unique)
    }
    $signature = if ($deviceIds.Count -gt 0) {
        $deviceIds -join "|"
    }
    else {
        "bound-lian-li-code10"
    }
    if ($signature -cne $script:hs2Code10LastSignature) {
        Write-WatchdogLog (
            "HS2 LIAN LI Code10 fail-closed devices={0}; no automatic PnP, hub restart, device removal, or scan will run. Action: avoid Device Manager retry loops; power down before inspecting the HS2 USB header, cable, and auxiliary power path." -f `
                $deviceIds.Count)
        $script:hs2Code10LastSignature = $signature
    }
}

function Resolve-WallpaperEngineControlExecutable {
    $currentSessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $controllerCandidates = New-Object "System.Collections.Generic.List[string]"
    foreach ($engineProcess in @(Get-Process -Name "wallpaper64" -ErrorAction SilentlyContinue)) {
        if ($engineProcess.SessionId -ne $currentSessionId) {
            continue
        }
        $enginePath = $null
        try {
            $enginePath = [string]$engineProcess.Path
        }
        catch {
            continue
        }
        if ([string]::IsNullOrWhiteSpace($enginePath)) {
            continue
        }
        $controllerPath = Join-Path (Split-Path -Parent $enginePath) "wallpaper32.exe"
        if (Test-Path -LiteralPath $controllerPath -PathType Leaf) {
            [void]$controllerCandidates.Add($controllerPath)
        }
    }

    $uniqueControllers = @($controllerCandidates | Sort-Object -Unique)
    if ($uniqueControllers.Count -ne 1) {
        return $null
    }
    return [string]$uniqueControllers[0]
}

function Invoke-WallpaperEngineRenderRebind {
    $controlExecutable = Resolve-WallpaperEngineControlExecutable
    if ([string]::IsNullOrWhiteSpace($controlExecutable)) {
        return [pscustomobject]@{
            Dispatched = $false
            Status = "control-client-unavailable"
        }
    }

    $currentSessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $userShells = @(
        Get-Process -Name "explorer" -ErrorAction SilentlyContinue |
            Where-Object { $_.SessionId -eq $currentSessionId }
    )
    if ($userShells.Count -eq 0) {
        return [pscustomobject]@{
            Dispatched = $false
            Status = "user-shell-unavailable"
        }
    }

    $shellApplication = $null
    try {
        $shellApplicationType = [Type]::GetTypeFromProgID("Shell.Application")
        if ($null -eq $shellApplicationType) {
            throw "Shell.Application is unavailable."
        }
        $shellApplication = [Activator]::CreateInstance($shellApplicationType)
        if ($null -eq $shellApplication) {
            throw "Shell.Application could not be created."
        }

        # Dispatch through the existing interactive shell, rather than creating
        # an elevated wallpaper worker from this Highest watchdog process.
        $workingDirectory = Split-Path -Parent $controlExecutable
        $shellApplication.ShellExecute(
            $controlExecutable,
            "-control stop",
            $workingDirectory,
            "open",
            0)
        Start-Sleep -Milliseconds $WallpaperRenderRebindGapMilliseconds
        $shellApplication.ShellExecute(
            $controlExecutable,
            "-control play",
            $workingDirectory,
            "open",
            0)
        return [pscustomobject]@{
            Dispatched = $true
            Status = "control-stop-play-dispatched"
        }
    }
    catch {
        return [pscustomobject]@{
            Dispatched = $false
            Status = "control-dispatch-failed"
            Error = $_.Exception.Message
        }
    }
    finally {
        if ($null -ne $shellApplication) {
            try {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shellApplication)
            }
            catch { }
        }
    }
}

function Invoke-WallpaperEngineDisplayRecovery {
    $nowUtc = [DateTime]::UtcNow
    if ($script:wallpaperDisplayLastProbeUtc -ne [DateTime]::MinValue -and
        ($nowUtc - $script:wallpaperDisplayLastProbeUtc).TotalSeconds -lt
        $WallpaperDisplayProbeSeconds) {
        return
    }
    $script:wallpaperDisplayLastProbeUtc = $nowUtc

    try {
        $nativeMethods = Initialize-HS2ExclusiveWindowGuardNativeMethods
        if ($null -eq $nativeMethods) {
            throw "display monitor snapshot methods are unavailable."
        }
        $monitors = @($nativeMethods::CaptureMonitors())
        $mttMonitorNodes = @(
            Get-PnpDevice `
                -PresentOnly `
                -InstanceId "DISPLAY\MTT1337\*" `
                -ErrorAction Stop
        )
        $mttBackingNodes = @(
            Get-PnpDevice `
                -PresentOnly `
                -InstanceId "ROOT\DISPLAY\*" `
                -ErrorAction Stop
        )
        $mttBinding = Get-WallpaperEngineMttBindingDecision `
            -MttMonitorNodes $mttMonitorNodes `
            -BackingNodes $mttBackingNodes `
            -PropertyReader {
                param($InstanceId, $KeyName)
                Get-HS2PnpPropertyValue `
                    -InstanceId ([string]$InstanceId) `
                    -KeyName ([string]$KeyName)
            }
        $mttDevices = if ($mttBinding.Found) {
            @($mttBinding.Device)
        }
        else {
            @()
        }
        $hs2BindingHealthy = $false
        if ($script:hs2DisplayStateActive) {
            $hs2BindingHealthy = Test-HS2CurrentSecondaryBindingHealthy
        }
        $health = Get-WallpaperEngineDisplayHealthDecision `
            -Monitors $monitors `
            -MttDevices $mttDevices `
            -Hs2SecondaryActive $script:hs2DisplayStateActive `
            -Hs2BindingHealthy $hs2BindingHealthy
        $currentFingerprint = Get-WallpaperEngineTopologyFingerprint `
            -Monitors $monitors
    }
    catch {
        $status = "probe-failed"
        if ($status -cne $script:wallpaperDisplayLastStatus) {
            Write-WatchdogLog (
                "Wallpaper Engine display recovery action={0}: {1}" -f `
                    $status,
                    $_.Exception.Message)
            $script:wallpaperDisplayLastStatus = $status
        }
        return
    }

    $decision = Get-WallpaperEngineRebindDecision `
        -BaselineFingerprint $script:wallpaperDisplayBaselineFingerprint `
        -PendingFingerprint $script:wallpaperDisplayPendingFingerprint `
        -CurrentFingerprint $currentFingerprint `
        -PendingSinceUtc $script:wallpaperDisplayPendingSinceUtc `
        -LastRebindUtc $script:wallpaperDisplayLastRebindUtc `
        -NowUtc $nowUtc `
        -Healthy $health.Eligible `
        -StabilitySeconds $WallpaperDisplayStabilitySeconds `
        -CooldownSeconds $WallpaperRenderRebindCooldownSeconds
    $script:wallpaperDisplayPendingFingerprint = [string]$decision.PendingFingerprint
    $script:wallpaperDisplayPendingSinceUtc = [DateTime]$decision.PendingSinceUtc

    $rebindStatus = $null
    switch ([string]$decision.Action) {
        "Baseline" {
            $script:wallpaperDisplayBaselineFingerprint = $currentFingerprint
        }
        "Rebind" {
            $rebind = Invoke-WallpaperEngineRenderRebind
            # A failed dispatch still consumes this topology event.  Repeating
            # stop/play from a High-integrity watcher is riskier than waiting
            # for a later real display change after the long cooldown.
            $script:wallpaperDisplayLastRebindUtc = $nowUtc
            $script:wallpaperDisplayBaselineFingerprint = $currentFingerprint
            $script:wallpaperDisplayPendingFingerprint = ""
            $script:wallpaperDisplayPendingSinceUtc = [DateTime]::MinValue
            $rebindStatus = [string]$rebind.Status
        }
    }

    $status = "{0}|health={1}|rebind={2}" -f `
        $decision.Action,
        $health.Reason,
        $rebindStatus
    if ($status -cne $script:wallpaperDisplayLastStatus) {
        Write-WatchdogLog (
            "Wallpaper Engine display recovery action={0} health={1} stableSeconds={2} cooldownSeconds={3} retryAfterSeconds={4} rebind={5}" -f `
                $decision.Action,
                $health.Reason,
                $WallpaperDisplayStabilitySeconds,
                $WallpaperRenderRebindCooldownSeconds,
                $decision.RetryAfterSeconds,
                $rebindStatus)
        $script:wallpaperDisplayLastStatus = $status
    }
}

function Stop-Stack {
    param([string]$Reason)
    Write-WatchdogLog ("stop stack reason={0}" -f $Reason)
    $trackedChild = $script:turzxStackChild
    if ($null -ne $trackedChild) {
        try {
            if (-not $trackedChild.HasExited) {
                # This handle came from this watchdog's own Start-Process call.
                # Retire it before accepting the bounded CIM fallback, otherwise
                # an old stack could wake up later and create another COM writer.
                Write-WatchdogLog ("stopping tracked stack child pid={0} reason={1}" -f $trackedChild.Id, $Reason)
                Stop-Process -Id $trackedChild.Id -Force -ErrorAction Stop
            }
        }
        catch {
            Write-WatchdogLog ("tracked stack child stop deferred reason={0} error={1}" -f `
                    $Reason, $_.Exception.Message)
        }
    }
    $stopArguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $stopScript,
        "-Root", $Root,
        "-Quiet",
        "-ProcessSnapshotTimeoutSeconds", [string]$StopProcessSnapshotTimeoutSeconds
    )
    $stopProcess = Start-Process -FilePath "powershell.exe" -ArgumentList $stopArguments -WindowStyle Hidden -PassThru
    $completed = $false
    try {
        $completed = $stopProcess.WaitForExit($StopStackTimeoutSeconds * 1000)
    }
    catch {
        throw "StopSideScreenStack wait failed: $($_.Exception.Message)"
    }

    if (-not $completed) {
        try {
            Stop-Process -Id $stopProcess.Id -Force -ErrorAction SilentlyContinue
        }
        catch { }
        Write-WatchdogLog ("StopSideScreenStack strict total timeout reason={0} timeoutSeconds={1}; restart deferred" -f `
                $Reason, $StopStackTimeoutSeconds)
        throw "StopSideScreenStack exceeded strict total timeout: $StopStackTimeoutSeconds seconds"
    }
    if ($stopProcess.ExitCode -ne 0) {
        throw "StopSideScreenStack failed to prove the previous stream exited: exitCode=$($stopProcess.ExitCode)"
    }
}

function Get-TurzxExactSerialEndpoint {
    $serial = @(
        Get-CimInstance Win32_SerialPort -ErrorAction Stop |
            Where-Object { [string]$_.DeviceID -ieq $Port }
    )
    if ($serial.Count -ne 1) {
        throw "Expected exactly one TURZX serial endpoint on $Port; found $($serial.Count)."
    }

    $instanceId = [string]$serial[0].PNPDeviceID
    if ($instanceId -notmatch '(?i)^USB\\VID_0525&PID_A4A7\\') {
        throw "Refusing unexpected TURZX serial identity on ${Port}: $instanceId"
    }

    $device = Get-PnpDevice -InstanceId $instanceId -ErrorAction Stop
    if (-not [bool]$device.Present -or
        [string]$device.Status -cne "OK" -or
        [int]$device.ConfigManagerErrorCode -ne 0) {
        throw (
            "Exact TURZX serial endpoint is not healthy: " +
            "present={0} status={1} problem={2}" -f
                [bool]$device.Present,
                [string]$device.Status,
                [int]$device.ConfigManagerErrorCode)
    }

    return [pscustomobject]@{
        Port = [string]$serial[0].DeviceID
        InstanceId = $instanceId
    }
}

function Wait-TurzxExactSerialEndpointHealthy {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedInstanceId,
        [ValidateRange(5, 60)][int]$TimeoutSeconds = 20
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $endpoint = Get-TurzxExactSerialEndpoint
            if ([string]$endpoint.InstanceId -ieq $ExpectedInstanceId) {
                return $endpoint
            }
        }
        catch {
            # The exact endpoint may disappear briefly during re-enumeration.
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    return $null
}

function Invoke-TurzxSerialEndpointRecovery {
    $nowUtc = [DateTime]::UtcNow
    $decision = Get-TurzxSerialEndpointRecoveryDecision `
        -ConsecutiveBrightnessFailures `
            $script:turzxBrightnessConsecutiveFailures `
        -LastAttemptUtc $script:turzxSerialRecoveryLastAttemptUtc `
        -NowUtc $nowUtc `
        -FailureThreshold $TurzxSerialRecoveryFailureThreshold `
        -RetrySeconds $TurzxSerialRecoveryRetrySeconds
    if ($decision.Action -ne "RestartEndpoint") {
        return [pscustomobject]@{
            Action = [string]$decision.Action
            Recovered = $false
            RetryAfterSeconds = [int]$decision.RetryAfterSeconds
            Reason = if ($decision.Action -eq "Wait") {
                "serial-restart-rate-limited"
            }
            else {
                "serial-failure-threshold-not-reached"
            }
        }
    }

    $streamOwners = @(
        Get-Process "TURZX.SideScreen.Stream*" -ErrorAction SilentlyContinue)
    if ($streamOwners.Count -ne 0) {
        Write-WatchdogLog (
            "TURZX exact serial recovery refused; active stream owners={0}" -f `
                (@($streamOwners | ForEach-Object Id) -join ","))
        return [pscustomobject]@{
            Action = "Blocked"
            Recovered = $false
            RetryAfterSeconds = 0
            Reason = "active-stream-owner"
        }
    }

    $script:turzxSerialRecoveryLastAttemptUtc = $nowUtc
    try {
        $endpoint = Get-TurzxExactSerialEndpoint
        $restartOutput = & pnputil.exe `
            /restart-device `
            ([string]$endpoint.InstanceId) 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "pnputil restart failed exitCode=$LASTEXITCODE"
        }

        $ready = Wait-TurzxExactSerialEndpointHealthy `
            -ExpectedInstanceId ([string]$endpoint.InstanceId) `
            -TimeoutSeconds $TurzxSerialRecoveryWaitSeconds
        if ($null -eq $ready) {
            throw "exact endpoint did not return before timeout"
        }

        Write-WatchdogLog (
            "TURZX exact serial endpoint recovered port={0} identity=VID_0525&PID_A4A7" -f `
                [string]$ready.Port)
        return [pscustomobject]@{
            Action = "RestartEndpoint"
            Recovered = $true
            RetryAfterSeconds = 0
            Reason = "exact-endpoint-restarted"
        }
    }
    catch {
        Write-WatchdogLog (
            "TURZX exact serial endpoint recovery failed: {0}" -f `
                $_.Exception.Message)
        return [pscustomobject]@{
            Action = "Failed"
            Recovered = $false
            RetryAfterSeconds = $TurzxSerialRecoveryRetrySeconds
            Reason = $_.Exception.Message
        }
    }
}

function Invoke-TurzxFailureCircuitBreaker {
    param([Parameter(Mandatory = $true)][string]$Reason)

    Set-Content -LiteralPath $pausedPath -Value (Get-Date -Format "o") -Encoding ASCII
    Write-WatchdogLog ("failure circuit open reason={0} cooldownSeconds={1}" -f $Reason, $FailureCircuitBreakerSeconds)
    try {
        try {
            Stop-Stack -Reason ("failure-circuit/{0}" -f $Reason)
        }
        catch {
            Write-WatchdogLog ("failure circuit stop proof deferred reason={0} type={1} error={2}" -f `
                $Reason, $_.Exception.GetType().FullName, $_.Exception.Message)
        }
        $serialRecovery = Invoke-TurzxSerialEndpointRecovery
        $cooldownSeconds = if ([bool]$serialRecovery.Recovered) {
            2
        }
        else {
            $FailureCircuitBreakerSeconds
        }
        Write-WatchdogLog (
            "failure circuit serial recovery action={0} recovered={1} reason={2} cooldownSeconds={3}" -f `
                [string]$serialRecovery.Action,
                [bool]$serialRecovery.Recovered,
                [string]$serialRecovery.Reason,
                $cooldownSeconds)
        Start-Sleep -Seconds $cooldownSeconds
    }
    finally {
        Remove-Item -LiteralPath $pausedPath -Force -ErrorAction SilentlyContinue
    }
    Write-WatchdogLog ("failure circuit closed reason={0}; retrying stack" -f $Reason)
    return Invoke-TurzxStackRestartAttempt -Reason ("failure-circuit/{0}" -f $Reason)
}

function Set-TurzxPanelBrightness {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateRange(0, 255)]
        [int]$Brightness
    )

    try {
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $brightnessScript `
            -Root $Root `
            -Port $Port `
            -Brightness $Brightness 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw ($output -join " ")
        }
        $script:turzxBrightnessConsecutiveFailures = 0
        Write-WatchdogLog ("TURZX brightness applied brightness={0}" -f $Brightness)
        return $true
    }
    catch {
        $script:turzxBrightnessConsecutiveFailures++
        Write-WatchdogLog ("TURZX brightness failed brightness={0}: {1}" -f $Brightness, $_.Exception.Message)
        return $false
    }
}

function Set-HS2NativeStateFromResult {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Reason,
        [switch]$ResetStabilityWindow,
        [switch]$PreservePromotionBackoff
    )

    if ($null -eq $Result -or -not [bool]$Result.Verified -or
        [int64]$Result.ControllerType -ne 17104896) {
        throw "HS2 native Active did not return verified controller type 17104896."
    }

    $nowUtc = [DateTime]::UtcNow
    $wasNative = $script:hs2NativeDisplayStateActive
    $modeChanged = [int64]$Result.InitialControllerType -ne 17104896
    $script:hs2NativeDisplayStateActive = $true
    $script:hs2DisplayStateActive = $false
    if ($ResetStabilityWindow -or -not $wasNative -or $modeChanged -or
        $script:hs2NativeStableSinceUtc -eq [DateTime]::MinValue) {
        $script:hs2NativeStableSinceUtc = $nowUtc
        $script:hs2SecondaryStabilityWaitLogged = $false
        if (-not $PreservePromotionBackoff) {
            $script:hs2SecondaryLastAttemptUtc = [DateTime]::MinValue
            $script:hs2SecondaryPromotionFailures = 0
            $script:hs2SecondaryHoldLogged = $false
        }
    }
    $script:hs2ActiveLastVerifiedUtc = $nowUtc
    $script:hs2ActiveConsecutiveFailures = 0
    $script:hs2ActiveCurrentRetrySeconds = $HS2ActiveRetrySeconds
    $script:hs2OverlayRebindRequired = $true
    return $Result
}

function Get-HS2CurrentSecondaryDesktopPathDecision {
    $nativeMethods = Initialize-HS2ExclusiveWindowGuardNativeMethods
    if ($null -eq $nativeMethods) {
        return [pscustomobject]@{
            Active = $false
            Reason = "desktop-monitor-snapshot-unavailable"
            TargetMonitorDevice = $null
        }
    }

    try {
        return Get-HS2SecondaryDesktopTopologyDecision `
            -Monitors @($nativeMethods::CaptureMonitors())
    }
    catch {
        return [pscustomobject]@{
            Active = $false
            Reason = "desktop-monitor-snapshot-failed"
            TargetMonitorDevice = $null
        }
    }
}

function Wait-HS2SecondaryDesktopPathActive {
    param(
        [ValidateRange(2, 30)][int]$TimeoutSeconds = 10,
        [ValidateRange(1, 5)][int]$PollSeconds = 2,
        [ValidateRange(2, 3)][int]$RequiredConsecutiveSamples = 2
    )

    $deadlineUtc = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $consecutiveSamples = 0
    $lastDecision = $null
    do {
        $lastDecision = Get-HS2CurrentSecondaryDesktopPathDecision
        if ([bool]$lastDecision.Active) {
            $consecutiveSamples++
            if ($consecutiveSamples -ge $RequiredConsecutiveSamples) {
                return $lastDecision
            }
        }
        else {
            $consecutiveSamples = 0
        }
        Start-Sleep -Seconds $PollSeconds
    } while ([DateTime]::UtcNow -lt $deadlineUtc)

    return $lastDecision
}

function Set-HS2VerifiedSecondaryState {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Reason,
        [switch]$PreservedAtStartup
    )

    if ($null -eq $Result -or -not [bool]$Result.Verified -or
        [int64]$Result.ControllerType -ne 17104897) {
        throw "HS2 secondary Active did not return verified controller type 17104897."
    }
    if (-not (Wait-HS2UsbDisplayHealthy -TimeoutSeconds 15)) {
        throw "HS2 secondary controller returned, but the bound AD23/LED topology did not become physically healthy."
    }
    if (-not (Save-HS2UsbTopologyBinding -Path $hs2UsbTopologyBindingPath)) {
        throw "HS2 secondary topology binding was not uniquely verified."
    }
    $desktopPath = Wait-HS2SecondaryDesktopPathActive
    if (-not [bool]$desktopPath.Active) {
        throw "HS2 secondary USB binding is healthy, but its Windows desktop path is inactive: $($desktopPath.Reason)."
    }

    $nowUtc = [DateTime]::UtcNow
    $script:hs2NativeDisplayStateActive = $false
    $script:hs2DisplayStateActive = $true
    $script:hs2ActiveLastVerifiedUtc = $nowUtc
    $script:hs2ActiveConsecutiveFailures = 0
    $script:hs2SecondaryPromotionFailures = 0
    $script:hs2ActiveCurrentRetrySeconds = $HS2ActiveRetrySeconds
    $script:hs2OverlayRebindRequired = $true
    if ($PreservedAtStartup -and
        $script:hs2SecondaryLastAttemptUtc -eq [DateTime]::MinValue) {
        # Treat the already-present secondary mode as this epoch's one allowed
        # topology state.  If it later fails, the epoch cannot oscillate.
        $script:hs2SecondaryLastAttemptUtc = $nowUtc
    }
    Enable-DesktopWindowPreservation
    if ($PreservedAtStartup) {
        Write-WatchdogLog (
            "HS2 secondary preserve verified reason={0}; AD23 binding saved and overlay may activate" -f `
                $Reason)
    }
    else {
        Write-WatchdogLog (
            "HS2 secondary promotion verified reason={0}; AD23 binding saved and overlay may activate" -f `
                $Reason)
    }
    return $Result
}

function Set-HS2PreservedActiveState {
    param(
        [Parameter(Mandatory = $true)][string]$Reason,
        [switch]$ResetStabilityWindow,
        [switch]$PreservePromotionBackoff
    )

    # With no mode override, this call may repair screen-on/offline-clock state
    # but cannot issue SetSecondaryScreen.  Preserve AD23 when firmware/boot
    # already provided it; only an actual 17104896 controller enters the
    # stabilization-and-promotion path.
    $result = Invoke-HS2PowerState -State Active -ServicePort $LConnectServicePort
    switch ([int64]$result.ControllerType) {
        17104897 {
            return Set-HS2VerifiedSecondaryState `
                -Result $result `
                -Reason $Reason `
                -PreservedAtStartup
        }
        17104896 {
            return Set-HS2NativeStateFromResult `
                -Result $result `
                -Reason $Reason `
                -ResetStabilityWindow:$ResetStabilityWindow `
                -PreservePromotionBackoff:$PreservePromotionBackoff
        }
        default {
            throw "L-Connect returned unsupported HS2 controller type $($result.ControllerType)."
        }
    }
}

function Test-HS2CurrentSecondaryBindingHealthy {
    $expected = Read-HS2UsbTopologyBinding -Path $hs2UsbTopologyBindingPath
    $current = Get-HS2HealthyUsbTopologyBinding
    if ($null -eq $expected -or $null -eq $current) {
        return $false
    }

    $usbBindingHealthy = (
        [int]$expected.SchemaVersion -eq 2 -and
        [int]$current.SchemaVersion -eq 2 -and
        [string]$current.HubInstanceId -ieq [string]$expected.HubInstanceId -and
        [string]$current.DisplayInstanceId -ieq [string]$expected.DisplayInstanceId -and
        [string]$current.DisplayInterfaceInstanceId -ieq
            [string]$expected.DisplayInterfaceInstanceId -and
        [string]$current.LedInstanceId -ieq [string]$expected.LedInstanceId)
    if (-not $usbBindingHealthy) {
        return $false
    }

    return [bool](Get-HS2CurrentSecondaryDesktopPathDecision).Active
}

function Invoke-HS2SecondaryActiveMaintenance {
    $nowUtc = [DateTime]::UtcNow
    $decision = Get-HS2ActiveRecoveryDecision `
        -DesiredActive $script:hs2DisplayStateDesiredActive `
        -VerifiedActive $script:hs2DisplayStateActive `
        -LastAttemptUtc $script:hs2ActiveLastAttemptUtc `
        -LastVerifiedUtc $script:hs2ActiveLastVerifiedUtc `
        -NowUtc $nowUtc `
        -RetrySeconds $HS2ActiveRetrySeconds `
        -VerifySeconds $HS2ActiveVerifySeconds
    if ($decision.Action -ne "Verify") {
        return
    }

    $script:hs2ActiveLastAttemptUtc = $nowUtc
    try {
        # Preserve is non-topology-mutating: it may repair screen-on state on
        # the existing controller, but it cannot call SetSecondaryScreen.
        $result = Invoke-HS2PowerState -State Active -ServicePort $LConnectServicePort
        if ([int64]$result.ControllerType -eq 17104896) {
            if ($script:hs2SecondaryLastAttemptUtc -eq [DateTime]::MinValue) {
                $script:hs2SecondaryLastAttemptUtc = $nowUtc
            }
            Set-HS2NativeStateFromResult `
                -Result $result `
                -Reason "secondary-health-returned-native" `
                -ResetStabilityWindow `
                -PreservePromotionBackoff | Out-Null
            Write-WatchdogLog "HS2 secondary health returned native controller; holding native mode for this epoch"
            return
        }
        if ([int64]$result.ControllerType -ne 17104897 -or
            -not (Test-HS2CurrentSecondaryBindingHealthy)) {
            throw "HS2 secondary controller or its exact AD23/LED binding is no longer healthy."
        }

        $script:hs2ActiveLastVerifiedUtc = [DateTime]::UtcNow
        $script:hs2ActiveConsecutiveFailures = 0
        $script:hs2ActiveCurrentRetrySeconds = $HS2ActiveRetrySeconds
    }
    catch {
        # Do not attempt a mode transition while a controller/bus is failing.
        # Fail closed, stop the overlay in the same loop, and wait for natural
        # controller discovery.  The epoch marker prevents later oscillation.
        if ($script:hs2SecondaryLastAttemptUtc -eq [DateTime]::MinValue) {
            $script:hs2SecondaryLastAttemptUtc = $nowUtc
        }
        $script:hs2DisplayStateActive = $false
        $script:hs2NativeDisplayStateActive = $false
        $script:hs2OverlayRebindRequired = $true
        $script:hs2ActiveConsecutiveFailures++
        $script:hs2ActiveCurrentRetrySeconds = $HS2ActiveSlowRetrySeconds
        Write-WatchdogLog (
            "HS2 secondary health lost; preserving topology and waiting for controller recovery: {0}" -f `
                $_.Exception.Message)
    }
}

function Invoke-HS2InitialActiveMaintenance {
    param([string]$Reason = "watchdog-loop")

    if (-not $script:hs2DisplayStateDesiredActive) {
        return
    }

    $nowUtc = [DateTime]::UtcNow
    $retrySeconds = $script:hs2ActiveCurrentRetrySeconds
    if ($script:hs2NativeLastAttemptUtc -ne [DateTime]::MinValue -and
        ($nowUtc - $script:hs2NativeLastAttemptUtc).TotalSeconds -lt $retrySeconds) {
        return
    }

    $wasNative = $script:hs2NativeDisplayStateActive
    $preservePromotionAttempt =
        $script:hs2SecondaryLastAttemptUtc -ne [DateTime]::MinValue
    $script:hs2NativeLastAttemptUtc = $nowUtc

    $controllerReadiness = $null
    try {
        $controllerReadiness = Get-HS2ControllerReadiness `
            -BindingPath $hs2UsbTopologyBindingPath
    }
    catch {
        $controllerReadiness = [pscustomobject]@{
            Action = "Wait"
            Reason = "controller-readiness-probe-failed"
            EndpointInstanceId = $null
        }
    }
    if ($controllerReadiness.Action -ne "Ready") {
        if ([string]$controllerReadiness.Reason -ceq
            "bound-lian-li-code10-fail-closed") {
            Write-HS2Code10FailClosedWarning -Readiness $controllerReadiness
        }
        else {
            $script:hs2Code10LastSignature = $null
        }
        $script:hs2NativeDisplayStateActive = $false
        $script:hs2DisplayStateActive = $false
        $script:hs2ActiveCurrentRetrySeconds = $HS2ActiveRetrySeconds
        if ([string]$script:hs2ControllerReadinessWaitReason -cne
            [string]$controllerReadiness.Reason) {
            Write-WatchdogLog (
                "HS2 controller readiness wait reason={0} endpoint={1}; no L-Connect mode command or USB/PnP recovery will run" -f `
                    $controllerReadiness.Reason,
                    $controllerReadiness.EndpointInstanceId)
            $script:hs2ControllerReadinessWaitReason = [string]$controllerReadiness.Reason
        }
        return
    }
    $script:hs2Code10LastSignature = $null
    if ($null -ne $script:hs2ControllerReadinessWaitReason) {
        Write-WatchdogLog (
            "HS2 controller endpoint ready endpoint={0}; resuming preserved-mode L-Connect activation" -f `
                $controllerReadiness.EndpointInstanceId)
        $script:hs2ControllerReadinessWaitReason = $null
    }

    if ([string]$controllerReadiness.EndpointInstanceId -match
        '(?i)VID_1A86&PID_AD23') {
        $desktopPath = Get-HS2CurrentSecondaryDesktopPathDecision
        if (-not [bool]$desktopPath.Active) {
            $script:hs2NativeDisplayStateActive = $false
            $script:hs2DisplayStateActive = $false
            $script:hs2ActiveCurrentRetrySeconds = $HS2ActiveSlowRetrySeconds
            if ([string]$script:hs2DesktopPathWaitReason -cne
                [string]$desktopPath.Reason) {
                Write-WatchdogLog (
                    "HS2 secondary controller is present but Windows desktop path is inactive reason={0}; overlay stopped and no L-Connect mode command will run" -f `
                        $desktopPath.Reason)
                $script:hs2DesktopPathWaitReason = [string]$desktopPath.Reason
            }
            return
        }
        $script:hs2DesktopPathWaitReason = $null
    }

    try {
        $result = Set-HS2PreservedActiveState `
            -Reason $Reason `
            -ResetStabilityWindow:(-not $wasNative) `
            -PreservePromotionBackoff:$preservePromotionAttempt
        if ([int64]$result.ControllerType -eq 17104896 -and -not $wasNative) {
            Write-WatchdogLog (
                "HS2 native controller ready reason={0} controllerType=17104896; secondary promotion waits {1}s" -f `
                    $Reason,
                    $HS2SecondaryPromotionGraceSeconds)
        }
    }
    catch {
        $script:hs2NativeDisplayStateActive = $false
        $script:hs2DisplayStateActive = $false
        $script:hs2ActiveConsecutiveFailures++
        $script:hs2ActiveCurrentRetrySeconds = if (
            $script:hs2ActiveConsecutiveFailures -ge 3) {
            $HS2ActiveSlowRetrySeconds
        }
        else {
            $HS2ActiveRetrySeconds
        }
        Write-WatchdogLog (
            "HS2 initial active recovery failed reason={0} count={1} retrySeconds={2}: {3}" -f `
                $Reason,
                $script:hs2ActiveConsecutiveFailures,
                $script:hs2ActiveCurrentRetrySeconds,
                $_.Exception.Message)
        Invoke-HS2NativeLConnectServiceRecovery
    }
}

function Invoke-HS2NativeLConnectServiceRecovery {
    # L-Connect restart is intentionally narrower than a USB/PnP recovery:
    # it is permitted once only when the previously bound dedicated hub has a
    # physically healthy native A068 or Secondary AD23 endpoint but the
    # controller API cannot answer.  It never clears the secondary-promotion
    # marker, so it cannot cause topology oscillation.
    if ($script:hs2LConnectRecoveryAttempted) {
        return
    }

    $nowUtc = [DateTime]::UtcNow
    $startupDecision = Get-HS2RecoveryEscalationDecision `
        -WatchdogStartedUtc $watchdogStartedUtc `
        -NowUtc $nowUtc `
        -GraceSeconds $HS2LConnectRecoveryStartupGraceSeconds
    if ($startupDecision.Action -eq "Wait") {
        if (-not $script:hs2LConnectRecoveryGraceLogged) {
            Write-WatchdogLog (
                "HS2 L-Connect recovery deferred for startup stabilization remainingSeconds={0:N0}" -f `
                    $startupDecision.RetryAfterSeconds)
            $script:hs2LConnectRecoveryGraceLogged = $true
        }
        return
    }

    $eligibility = $null
    try {
        $eligibility = Get-HS2LConnectServiceRecoveryEligibility `
            -BindingPath $hs2UsbTopologyBindingPath
    }
    catch {
        Write-WatchdogLog (
            "HS2 L-Connect recovery eligibility probe failed safely: {0}" -f `
                $_.Exception.Message)
        return
    }
    $decision = Get-HS2LConnectRecoveryFollowUpDecision `
        -DisplayHealthy ([bool]$eligibility.Eligible) `
        -RecoveryAttempted $script:hs2LConnectRecoveryAttempted `
        -RecoveryStartedUtc $script:hs2LConnectRecoveryStartedUtc `
        -NowUtc $nowUtc `
        -GraceSeconds $HS2LConnectRecoveryGraceSeconds
    if ($decision.Action -ne "RestartService") {
        if ($decision.Action -eq "SlowRetry" -and -not $eligibility.Eligible) {
            Write-WatchdogLog (
                "HS2 L-Connect recovery skipped reason={0}; secondaryAttemptPreserved={1}" -f `
                    $eligibility.Reason,
                    ($script:hs2SecondaryLastAttemptUtc -ne [DateTime]::MinValue))
        }
        return
    }

    $script:hs2LConnectRecoveryAttempted = $true
    $script:hs2LConnectRecoveryStartedUtc = $nowUtc
    $serviceRecovery = Invoke-HS2LConnectServiceRecovery `
        -BindingPath $hs2UsbTopologyBindingPath `
        -ServicePort $LConnectServicePort
    Write-WatchdogLog (
        "HS2 L-Connect recovery attempted={0} recovered={1} reason={2} endpoint={3}; secondaryAttemptPreserved={4}" -f `
            $serviceRecovery.Attempted,
            $serviceRecovery.Recovered,
            $serviceRecovery.Reason,
            $eligibility.EndpointInstanceId,
            ($script:hs2SecondaryLastAttemptUtc -ne [DateTime]::MinValue))
    if ($serviceRecovery.Recovered) {
        $script:hs2ActiveCurrentRetrySeconds = $HS2LConnectRecoveryRetrySeconds
        $script:hs2NativeLastAttemptUtc = [DateTime]::MinValue
    }
    else {
        $script:hs2ActiveCurrentRetrySeconds = $HS2ActiveSlowRetrySeconds
    }
}

function Invoke-HS2SecondaryPromotion {
    param([string]$Reason = "watchdog-loop")

    if (-not $script:hs2DisplayStateDesiredActive -or
        $script:hs2DisplayStateActive) {
        return
    }

    $nowUtc = [DateTime]::UtcNow
    $decision = Get-HS2SecondaryPromotionDecision `
        -NativeActive $script:hs2NativeDisplayStateActive `
        -SecondaryVerified $script:hs2DisplayStateActive `
        -NativeStableSinceUtc $script:hs2NativeStableSinceUtc `
        -LastPromotionAttemptUtc $script:hs2SecondaryLastAttemptUtc `
        -NowUtc $nowUtc `
        -StabilitySeconds $HS2SecondaryPromotionGraceSeconds
    if ($decision.Action -eq "WaitNativeStability") {
        if (-not $script:hs2SecondaryStabilityWaitLogged) {
            Write-WatchdogLog (
                "HS2 secondary promotion deferred while native display settles remainingSeconds={0:N0}" -f `
                    $decision.RetryAfterSeconds)
            $script:hs2SecondaryStabilityWaitLogged = $true
        }
        return
    }
    if ($decision.Action -eq "HoldNative") {
        if (-not $script:hs2SecondaryHoldLogged) {
            Write-WatchdogLog "HS2 secondary promotion already attempted; holding native display until the next startup or resume epoch"
            $script:hs2SecondaryHoldLogged = $true
        }
        return
    }
    if ($decision.Action -ne "PromoteSecondary") {
        return
    }

    $script:hs2SecondaryLastAttemptUtc = $nowUtc
    try {
        $result = Invoke-HS2PowerState `
            -State Active `
            -EnableSecondaryScreen `
            -ServicePort $LConnectServicePort
        if ($null -eq $result -or -not [bool]$result.Verified -or
            [int64]$result.ControllerType -ne 17104897) {
            throw "L-Connect did not return verified HS2 secondary controller type 17104897."
        }
        Set-HS2VerifiedSecondaryState `
            -Result $result `
            -Reason $Reason | Out-Null
    }
    catch {
        $script:hs2DisplayStateActive = $false
        $script:hs2NativeDisplayStateActive = $false
        $script:hs2OverlayRebindRequired = $true
        $script:hs2SecondaryPromotionFailures++
        Write-WatchdogLog (
            "HS2 secondary promotion failed reason={0} count={1}; preserving the requested secondary mode and waiting for Windows desktop topology recovery: {2}" -f `
                $Reason,
                $script:hs2SecondaryPromotionFailures,
                $_.Exception.Message)
    }
}

function Invoke-HS2ActiveMaintenance {
    param([string]$Reason = "watchdog-loop")

    if (-not $script:hs2DisplayStateDesiredActive) {
        return
    }
    if ($script:hs2DisplayStateActive) {
        Invoke-HS2SecondaryActiveMaintenance
        return
    }
    if (-not $script:hs2NativeDisplayStateActive) {
        Invoke-HS2InitialActiveMaintenance -Reason $Reason
        return
    }

    Invoke-HS2SecondaryPromotion -Reason $Reason
}

function Set-ActiveDisplayState {
    param([string]$Reason)

    $script:hs2DisplayStateDesiredActive = $true
    $script:hs2DisplayStateActive = $false
    $script:hs2NativeDisplayStateActive = $false
    $script:hs2NativeStableSinceUtc = [DateTime]::MinValue
    $script:hs2NativeLastAttemptUtc = [DateTime]::MinValue
    $script:hs2SecondaryLastAttemptUtc = [DateTime]::MinValue
    $script:hs2SecondaryPromotionFailures = 0
    $script:hs2SecondaryStabilityWaitLogged = $false
    $script:hs2SecondaryHoldLogged = $false
    $script:hs2LConnectRecoveryAttempted = $false
    $script:hs2LConnectRecoveryStartedUtc = [DateTime]::MinValue
    $script:hs2LConnectRecoveryGraceLogged = $false
    $script:hs2ControllerReadinessWaitReason = $null
    $script:hs2ActiveLastAttemptUtc = [DateTime]::MinValue
    $script:hs2ActiveLastVerifiedUtc = [DateTime]::MinValue
    $script:hs2ActiveConsecutiveFailures = 0
    $script:hs2ActiveCurrentRetrySeconds = $HS2ActiveRetrySeconds
    $script:hs2OverlayRebindRequired = $true
    Invoke-HS2ActiveMaintenance -Reason $Reason
}

function Enable-DesktopWindowPreservation {
    if ($NoWindowPreservationPolicy) {
        Write-WatchdogLog "Windows display window preservation disabled by parameter"
        return
    }

    try {
        $result = Enable-WindowsDisplayWindowPreservation
        $changed = if ($result.ChangedSettings.Count -eq 0) {
            "none"
        }
        else {
            $result.ChangedSettings -join ","
        }
        Write-WatchdogLog (
            "Windows display window preservation compliant={0} changed={1} broadcast={2}" -f `
                $result.Compliant,
                $changed,
                $result.Broadcasted)
    }
    catch {
        Write-WatchdogLog (
            "Windows display window preservation failed: {0}" -f $_.Exception.Message)
    }
}

function Invoke-HS2OverlayHealthCheck {
    if (-not $script:hs2DisplayStateActive) {
        if ($script:hs2OverlayRebindRequired) {
            $staleOverlayProcess = Get-HS2OverlayProcess
            if ($null -ne $staleOverlayProcess) {
                $recycle = Stop-HS2OverlayForRebind `
                    -Process $staleOverlayProcess
                Write-WatchdogLog (
                    "HS2 overlay inactive-display cleanup attempted={0} stopped={1} pid={2} reason={3}" -f `
                        $recycle.Attempted,
                        $recycle.Stopped,
                        $recycle.ProcessId,
                        $recycle.Reason)
                if ($recycle.Stopped) {
                    $script:hs2OverlayWasRunning = $false
                    $script:hs2OverlayLastAttemptUtc = [DateTime]::MinValue
                    $script:hs2OverlayRebindRequired = $false
                }
            }
        }
        return
    }

    $nowUtc = [DateTime]::UtcNow
    $overlayProcess = Get-HS2OverlayProcess
    $rebindDecision = Get-HS2OverlayRebindDecision `
        -RebindRequired $script:hs2OverlayRebindRequired `
        -IsRunning ($null -ne $overlayProcess)
    if ($rebindDecision.Action -eq "Recycle") {
        $recycle = Stop-HS2OverlayForRebind -Process $overlayProcess
        Write-WatchdogLog (
            "HS2 overlay display rebind attempted={0} stopped={1} pid={2} reason={3}" -f `
                $recycle.Attempted,
                $recycle.Stopped,
                $recycle.ProcessId,
                $recycle.Reason)
        if (-not $recycle.Stopped) {
            return
        }
        $overlayProcess = $null
        $script:hs2OverlayWasRunning = $false
        $script:hs2OverlayLastAttemptUtc = [DateTime]::MinValue
        $script:hs2OverlayRebindRequired = $false
    }
    elseif ($rebindDecision.Action -eq "Activate") {
        $script:hs2OverlayWasRunning = $false
        $script:hs2OverlayLastAttemptUtc = [DateTime]::MinValue
        $script:hs2OverlayRebindRequired = $false
    }
    $decision = Get-HS2OverlayWatchdogDecision `
        -IsRunning ($null -ne $overlayProcess) `
        -LastAttemptUtc $script:hs2OverlayLastAttemptUtc `
        -NowUtc $nowUtc `
        -RetrySeconds $HS2OverlayRetrySeconds

    if ($decision.Action -eq "Healthy") {
        if (-not $script:hs2OverlayWasRunning) {
            Write-WatchdogLog (
                "HS2 overlay healthy pid={0} session={1}" -f `
                    $overlayProcess.Id,
                    $overlayProcess.SessionId)
        }
        $script:hs2OverlayWasRunning = $true
        return
    }

    if ($script:hs2OverlayWasRunning) {
        Write-WatchdogLog "HS2 overlay process disappeared; recovery scheduled"
        $script:hs2OverlayWasRunning = $false
    }
    if ($decision.Action -ne "Activate") {
        return
    }

    $script:hs2OverlayLastAttemptUtc = $nowUtc
    try {
        $activation = Start-HS2OverlayActivation
        Write-WatchdogLog (
            "HS2 overlay activation status={0}" -f $activation.Status)
    }
    catch {
        Write-WatchdogLog (
            "HS2 overlay activation failed: {0}" -f $_.Exception.Message)
    }
}

function Invoke-HS2ExclusiveWindowProtection {
    if ($NoWindowPreservationPolicy) {
        return
    }

    $overlayProcess = Get-HS2OverlayProcess
    $overlayProcessIds = if ($null -eq $overlayProcess) {
        @()
    }
    else {
        @([int]$overlayProcess.Id)
    }

    try {
        $guardArguments = @{
            PreferredTargetMonitorDevice = $script:hs2WindowGuardTargetMonitorDevice
            PreferredSafeMonitorDevice = $script:hs2WindowGuardSafeMonitorDevice
        }
        if ($overlayProcessIds.Count -gt 0) {
            $guardArguments.OverlayProcessIds = $overlayProcessIds
        }
        $result = Invoke-HS2ExclusiveWindowGuard @guardArguments
        if (-not [string]::IsNullOrWhiteSpace(
                [string]$result.TargetMonitorDevice)) {
            $script:hs2WindowGuardTargetMonitorDevice =
                [string]$result.TargetMonitorDevice
        }
        if (-not [string]::IsNullOrWhiteSpace(
                [string]$result.SafeMonitorDevice)) {
            $script:hs2WindowGuardSafeMonitorDevice =
                [string]$result.SafeMonitorDevice
        }

        if ([string]$result.OverlayPlacementStatus -ceq "drifted") {
            # Let the existing display-rebind path recreate every overlay
            # window from the verified HS2 geometry on the next watchdog
            # cycle.  Moving only the currently visible HWND would leave
            # hidden card windows with stale coordinates.
            $script:hs2OverlayRebindRequired = $true
        }

        $status = "{0}|target={1}|safe={2}|overlay={3}|visible={4}" -f `
            [string]$result.Status,
            [string]$result.TargetMonitorDevice,
            [string]$result.SafeMonitorDevice,
            [string]$result.OverlayPlacementStatus,
            [int]$result.OverlayVisibleWindowCount
        if ($status -cne $script:hs2WindowGuardLastStatus) {
            Write-WatchdogLog ("HS2 exclusive-window guard {0}" -f $status)
            $script:hs2WindowGuardLastStatus = $status
        }

        $applied = @($result.AppliedActions)
        if ($applied.Count -gt 0) {
            $moved = @($applied | Where-Object { $_.Action -ceq "Move" }).Count
            $minimized = @(
                $applied |
                    Where-Object { $_.Action -ceq "Minimize" }
            ).Count
            $processes = @(
                $applied |
                    ForEach-Object { [string]$_.ProcessName } |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                    Sort-Object -Unique
            ) -join ","
            Write-WatchdogLog (
                "HS2 exclusive-window guard corrected moved={0} minimized={1} processes={2}" -f `
                    $moved,
                    $minimized,
                    $processes)
        }

        $failures = @($result.FailedActions)
        $failureSignature = if ($failures.Count -eq 0) {
            $null
        }
        else {
            @(
                $failures |
                    ForEach-Object {
                        "{0}:{1}:{2}" -f `
                            $_.Action,
                            $_.ProcessId,
                            $_.Hwnd
                    }
            ) -join ","
        }
        if ($failureSignature -cne $script:hs2WindowGuardLastFailure) {
            if ($null -ne $failureSignature) {
                Write-WatchdogLog (
                    "HS2 exclusive-window guard correction failed actions={0}" -f `
                        $failureSignature)
            }
            $script:hs2WindowGuardLastFailure = $failureSignature
        }
    }
    catch {
        $failure = $_.Exception.Message
        if ($failure -cne $script:hs2WindowGuardLastFailure) {
            Write-WatchdogLog (
                "HS2 exclusive-window guard failed: {0}" -f $failure)
            $script:hs2WindowGuardLastFailure = $failure
        }
    }
}

function Start-WatchdogParentLivenessGuard {
    $self = Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction Stop
    $parentId = [int]$self.ParentProcessId
    if ($parentId -le 0) { throw "Unable to resolve TURZX watchdog parent process." }
    $parent = Get-Process -Id $parentId -ErrorAction Stop
    $parentStartTicks = [long]$parent.StartTime.ToUniversalTime().Ticks
    if (-not ("TURZX.SideScreen.WatchdogParentLiveness" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Diagnostics;
using System.Threading;
namespace TURZX.SideScreen {
    public static class WatchdogParentLiveness {
        private static Timer timer;
        public static void Start(int parentProcessId, long parentStartTimeUtcTicks) {
            Stop();
            timer = new Timer(_ => {
                try {
                    using (var parent = Process.GetProcessById(parentProcessId)) {
                        if (parent.StartTime.ToUniversalTime().Ticks == parentStartTimeUtcTicks) return;
                    }
                }
                catch { }
                try { Process.GetCurrentProcess().Kill(); }
                catch { Environment.FailFast("TURZX watchdog parent launcher exited."); }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        public static void Stop() {
            var current = Interlocked.Exchange(ref timer, null);
            if (current != null) current.Dispose();
        }
    }
}
"@
    }
    [TURZX.SideScreen.WatchdogParentLiveness]::Start($parentId, $parentStartTicks)
}

function Stop-WatchdogParentLivenessGuard {
    if ("TURZX.SideScreen.WatchdogParentLiveness" -as [type]) {
        [TURZX.SideScreen.WatchdogParentLiveness]::Stop()
    }
}

function Enter-SleepDisplayState {
    $script:hs2DisplayStateDesiredActive = $false
    $script:hs2DisplayStateActive = $false
    $hs2TransitionStarted = $false
    try {
        $hs2TransitionStarted = Start-HS2MonitorModeTransition -ServicePort $LConnectServicePort
        Write-WatchdogLog ("HS2 monitor-mode transition requested state=Sleep started={0}" -f $hs2TransitionStarted)
    }
    catch {
        Write-WatchdogLog ("HS2 monitor-mode request state=Sleep failed: {0}" -f $_.Exception.Message)
    }

    try {
        Stop-Stack -Reason "suspend"
    }
    catch {
        Write-WatchdogLog ("TURZX stop proof failed reason=suspend; continuing HS2 power policy: {0}" -f $_.Exception.Message)
    }
    try {
        if (-not (Set-TurzxPanelBrightness -Brightness 0)) {
            Write-WatchdogLog "TURZX power-off reason=suspend fallback=black-frame"
            Send-Blank -Reason "power-off-fallback/suspend" -TimeoutMs $QuickBlankTimeoutMs
        }
    }
    catch {
        Write-WatchdogLog ("TURZX power-off reason=suspend failed; continuing HS2 power policy: {0}" -f $_.Exception.Message)
    }

    try {
        Invoke-HS2PowerState -State Sleep `
            -ServicePort $LConnectServicePort `
            -MonitorModeAlreadyRequested:$hs2TransitionStarted `
            -SkipVerification | Out-Null
        Write-WatchdogLog "HS2 power state=Sleep offline-clock=true screen-on=false"
    }
    catch {
        Write-WatchdogLog ("HS2 power state=Sleep failed: {0}" -f $_.Exception.Message)
    }
}

function Enter-ShutdownDisplayState {
    $script:hs2DisplayStateDesiredActive = $false
    $script:hs2DisplayStateActive = $false
    $hs2TransitionStarted = $false
    try {
        $hs2TransitionStarted = Start-HS2MonitorModeTransition -ServicePort $LConnectServicePort
        Write-WatchdogLog ("HS2 monitor-mode transition requested state=Shutdown started={0}" -f $hs2TransitionStarted)
    }
    catch {
        Write-WatchdogLog ("HS2 monitor-mode request state=Shutdown failed: {0}" -f $_.Exception.Message)
    }

    try {
        Stop-Stack -Reason "shutdown"
    }
    catch {
        Write-WatchdogLog ("TURZX stop proof failed reason=shutdown; continuing HS2 power policy: {0}" -f $_.Exception.Message)
    }
    try {
        if (-not (Set-TurzxPanelBrightness -Brightness 0)) {
            Write-WatchdogLog "TURZX power-off reason=shutdown fallback=black-frame"
            Send-Blank -Reason "power-off-fallback/shutdown" -TimeoutMs $QuickBlankTimeoutMs
        }
    }
    catch {
        Write-WatchdogLog ("TURZX power-off reason=shutdown failed; continuing HS2 power policy: {0}" -f $_.Exception.Message)
    }

    try {
        Invoke-HS2PowerState -State Shutdown `
            -ServicePort $LConnectServicePort `
            -MonitorModeAlreadyRequested:$hs2TransitionStarted `
            -SkipVerification | Out-Null
        Write-WatchdogLog "HS2 power state=Shutdown offline-clock=false screen-on=false"
    }
    catch {
        Write-WatchdogLog ("HS2 power state=Shutdown failed: {0}" -f $_.Exception.Message)
    }
}

function Get-TurzxMonotonicMilliseconds {
    # Environment.TickCount64 is unavailable in Windows PowerShell 5.1 on
    # this host, while the scheduled watchdog intentionally runs there.
    # Stopwatch is monotonic and is supported by both Windows PowerShell 5.1
    # and PowerShell 7, so sleep/resume or wall-clock changes cannot shorten
    # the bounded startup-heartbeat window.
    return [int64]((
            [System.Diagnostics.Stopwatch]::GetTimestamp() * 1000.0) /
        [System.Diagnostics.Stopwatch]::Frequency)
}

function Set-TurzxChildHeartbeatStartupWindow {
    param([AllowNull()][object]$Child)

    $script:turzxStackChild = $Child
    $script:childHeartbeatObserved = $false
    if ($null -eq $Child) {
        $script:childHeartbeatStartupDeadlineMilliseconds = $null
        return $null
    }

    $script:childHeartbeatStartupDeadlineMilliseconds =
        (Get-TurzxMonotonicMilliseconds) + ([int64]$HeartbeatStartupGraceSeconds * 1000)
    return $Child
}

function Test-TurzxChildHeartbeatStartupDeadline {
    if ($null -eq $script:childHeartbeatStartupDeadlineMilliseconds) {
        return $false
    }
    return ((Get-TurzxMonotonicMilliseconds) -ge $script:childHeartbeatStartupDeadlineMilliseconds)
}

function Start-Stack {
    param([string]$Reason)
    Stop-Stack -Reason ("pre-start/{0}" -f $Reason)
    if (-not (Set-TurzxPanelBrightness -Brightness $ActiveBrightness)) {
        Write-WatchdogLog ("TURZX brightness restore failed reason={0}; starting stream for recovery" -f $Reason)
    }
    Remove-Item -LiteralPath $restartFlag -Force -ErrorAction SilentlyContinue
    foreach ($candidatePath in $heartbeatPaths) {
        Remove-Item -LiteralPath $candidatePath -Force -ErrorAction SilentlyContinue
    }
    Write-WatchdogLog ("start stack reason={0} root={1} port={2} interval={3} hybrid={4} altHelper={5}" -f $Reason, $Root, $Port, $IntervalMs, $HybridRefresh.IsPresent, $AltHelper.IsPresent)
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $stackScript,
        "-Root", $Root,
        "-Port", $Port,
        "-IntervalMs", [string]$IntervalMs,
        "-SendTimeoutMs", [string]$SendTimeoutMs,
        "-DiffSendTimeoutMs", [string]$DiffSendTimeoutMs,
        "-MaxConsecutiveSendFailures", [string]$MaxConsecutiveSendFailures,
        "-FullResyncEveryFrames", [string]$FullResyncEveryFrames,
        "-BaselineBrightness", [string]$ActiveBrightness,
        "-PreviewIntervalSeconds", [string]$PreviewIntervalSeconds,
        "-MaxStackLogBytes", [string]$MaxStackLogBytes,
        "-MaxStreamLogBytes", [string]$MaxStreamLogBytes,
        "-LogBackupCount", [string]$LogBackupCount,
        "-Worker"
    )
    if ($HybridRefresh) {
        $arguments += "-HybridRefresh"
    }
    if ($AltHelper) {
        $arguments += "-AltHelper"
    }
    $process = Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -WindowStyle Hidden -PassThru
    Set-Content -LiteralPath $childPidPath -Value $process.Id -Encoding ASCII
    Write-WatchdogLog ("stack child pid={0}" -f $process.Id)
    return $process
}

function Invoke-TurzxStackRestartAttempt {
    param(
        [Parameter(Mandatory = $true)][string]$Reason,
        [ValidateRange(0, 300)][int]$DelaySeconds = 0
    )

    if ($DelaySeconds -gt 0) {
        Start-Sleep -Seconds $DelaySeconds
    }
    try {
        $newChild = Start-Stack -Reason $Reason
        return [pscustomobject]@{
            Succeeded = $true
            Child = $newChild
            Error = $null
            ErrorType = $null
        }
    }
    catch {
        $errorType = $_.Exception.GetType().FullName
        $errorMessage = $_.Exception.Message
        Write-WatchdogLog ("stack restart deferred reason={0} type={1} error={2}; watchdog remains active" -f `
            $Reason, $errorType, $errorMessage)
        return [pscustomobject]@{
            Succeeded = $false
            Child = $null
            Error = $errorMessage
            ErrorType = $errorType
        }
    }
}

# A single metrics timeout is intentionally tolerated because the stream keeps
# sending the last complete snapshot.  Only a bounded consecutive run should
# enter the existing heartbeat recovery path.
function Get-TurzxSnapshotStaleDecision {
    param(
        [AllowEmptyString()][string]$SnapshotStatus,
        [int]$ConsecutiveFailures = 0,
        [ValidateRange(2, 20)][int]$Threshold = 5
    )

    $staleKind = if ($SnapshotStatus -match '^(stale|empty):') {
        [string]$Matches[1]
    }
    else {
        $null
    }
    if ([string]::IsNullOrWhiteSpace($staleKind)) {
        return [pscustomobject]@{
            IsStale = $false
            ConsecutiveFailures = 0
            FailureThresholdReached = $false
            Reason = "snapshot-fresh"
        }
    }

    $nextFailures = [Math]::Max(0, $ConsecutiveFailures) + 1
    return [pscustomobject]@{
        IsStale = $true
        ConsecutiveFailures = $nextFailures
        FailureThresholdReached = ($nextFailures -ge $Threshold)
        Reason = "snapshot-{0}-consecutive={1}/{2}" -f `
            $staleKind, $nextFailures, $Threshold
    }
}

function Get-StreamHeartbeatHealth {
    $candidates = @(
        $heartbeatPaths |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            ForEach-Object { Get-Item -LiteralPath $_ -ErrorAction SilentlyContinue } |
            Where-Object { $null -ne $_ } |
            Sort-Object LastWriteTimeUtc -Descending
    )
    if ($candidates.Count -eq 0) {
        return [pscustomobject]@{ Healthy = $false; Reason = "missing" }
    }

    $invalidReasons = @()
    foreach ($item in $candidates) {
        $ageSeconds = ([DateTime]::UtcNow - $item.LastWriteTimeUtc).TotalSeconds
        if ($ageSeconds -gt $HeartbeatStaleSeconds) {
            $invalidReasons += ("stale {0} ageSeconds={1:N1}" -f $item.Name, $ageSeconds)
            continue
        }

        try {
            $heartbeat = Get-Content -Raw -LiteralPath $item.FullName | ConvertFrom-Json
            if ($null -eq $heartbeat.frame -or [int64]$heartbeat.frame -le 0) {
                $invalidReasons += ("frame-invalid {0}" -f $item.Name)
                continue
            }
            $frameStatus = [string]$heartbeat.status
            $snapshotStatusProperty = $heartbeat.PSObject.Properties["snapshot_status"]
            $snapshotStatus = if ($null -eq $snapshotStatusProperty) {
                $null
            }
            else {
                [string]$snapshotStatusProperty.Value
            }
            if ($frameStatus -ne "ok") {
                return [pscustomobject]@{
                    Healthy = $false
                    Reason = ("frame-status={0} error={1}" -f $frameStatus, [string]$heartbeat.error)
                }
            }
            $transportMode = [string]$heartbeat.transport_mode
            $expectedTransportMode = if ($HybridRefresh) { "hybrid_diff_204_full_200" } else { "verified_full_200" }
            if ($transportMode -ne $expectedTransportMode) {
                return [pscustomobject]@{
                    Healthy = $false
                    Reason = ("transport-unverified mode={0} expected={1}" -f $transportMode, $expectedTransportMode)
                }
            }
            if ($null -eq $heartbeat.send_attempted -or
                $null -eq $heartbeat.send_ms -or
                $null -eq $heartbeat.elapsed_ms -or
                $null -eq $heartbeat.period_ms) {
                return [pscustomobject]@{
                    Healthy = $false
                    Reason = "timing-missing"
                }
            }
            $sendAttempted = [bool]$heartbeat.send_attempted
            $sendMs = [int64]$heartbeat.send_ms
            $elapsedMs = [int64]$heartbeat.elapsed_ms
            $periodMs = [int64]$heartbeat.period_ms
            if (-not $sendAttempted) {
                return [pscustomobject]@{
                    Healthy = $false
                    Reason = "send-not-attempted"
                }
            }
            if ($sendMs -lt 0 -or $elapsedMs -lt 0 -or $periodMs -lt 0) {
                return [pscustomobject]@{
                    Healthy = $false
                    Reason = ("timing-invalid sendMs={0} elapsedMs={1} periodMs={2}" -f `
                        $sendMs, $elapsedMs, $periodMs)
                }
            }
            $sendLimitMs = $HeartbeatMaxSendMs
            if ($HybridRefresh) {
                $frameTransport = [string]$heartbeat.frame_transport
                if ($frameTransport -ne "diff_204" -and $frameTransport -ne "full_200") {
                    return [pscustomobject]@{
                        Healthy = $false
                        Reason = ("frame-transport-invalid mode={0}" -f $frameTransport)
                    }
                }
                if ($frameTransport -eq "diff_204") {
                    $sendLimitMs = $DiffSendTimeoutMs
                }
                if ($FullResyncEveryFrames -gt 0) {
                    if ($null -eq $heartbeat.last_full_frame -or
                        $null -eq $heartbeat.full_resync_every_frames) {
                        return [pscustomobject]@{
                            Healthy = $false
                            Reason = "full-resync-missing"
                        }
                    }
                    $lastFullFrame = [int64]$heartbeat.last_full_frame
                    $reportedFullResyncEveryFrames = [int64]$heartbeat.full_resync_every_frames
                    $currentFrame = [int64]$heartbeat.frame
                    if ($reportedFullResyncEveryFrames -ne $FullResyncEveryFrames) {
                        return [pscustomobject]@{
                            Healthy = $false
                            Reason = ("full-resync-config-mismatch reported={0} expected={1}" -f `
                                $reportedFullResyncEveryFrames, $FullResyncEveryFrames)
                        }
                    }
                    if ($lastFullFrame -le 0 -or
                        $lastFullFrame -gt $currentFrame -or
                        ($currentFrame - $lastFullFrame) -ge $FullResyncEveryFrames) {
                        return [pscustomobject]@{
                            Healthy = $false
                            Reason = ("full-resync-overdue frame={0} lastFullFrame={1} limit={2}" -f `
                                $currentFrame, $lastFullFrame, $FullResyncEveryFrames)
                        }
                    }
                }
            }
            if ($sendMs -gt $sendLimitMs) {
                return [pscustomobject]@{
                    Healthy = $false
                    Reason = ("send-overrun sendMs={0} limitMs={1}" -f $sendMs, $sendLimitMs)
                }
            }
            if ($elapsedMs -gt $HeartbeatMaxElapsedMs -or
                $periodMs -gt $HeartbeatMaxPeriodMs) {
                return [pscustomobject]@{
                    Healthy = $false
                    Reason = ("frame-overrun elapsedMs={0}/{1} periodMs={2}/{3}" -f `
                        $elapsedMs, $HeartbeatMaxElapsedMs, $periodMs, $HeartbeatMaxPeriodMs)
                }
            }
            return [pscustomobject]@{
                Healthy = $true
                Reason = ("frame={0} source={1}" -f [int64]$heartbeat.frame, $item.Name)
                SnapshotStatus = $snapshotStatus
            }
        }
        catch {
            $invalidReasons += ("invalid {0}: {1}" -f $item.Name, $_.Exception.Message)
        }
    }

    return [pscustomobject]@{
        Healthy = $false
        Reason = ($invalidReasons -join "; ")
    }
}

function Send-Blank {
    param(
        [string]$Reason,
        [int]$TimeoutMs = 15000
    )
    Write-WatchdogLog ("send blank reason={0} timeoutMs={1}" -f $Reason, $TimeoutMs)
    try {
        powershell -NoProfile -ExecutionPolicy Bypass -File $blankScript -Root $Root -Port $Port -TimeoutMs $TimeoutMs | Out-Null
    }
    catch {
        Write-WatchdogLog ("blank failed: {0}" -f $_.Exception.Message)
    }
}

function Stop-OtherWatchdogs {
    param([string]$Reason)

    $rootPattern = "*" + $Root + "*"
    $stopped = 0
    Get-CimInstance Win32_Process |
        Where-Object {
            $_.ProcessId -ne $PID -and
            ($_.Name -like "powershell*" -or $_.Name -like "pwsh*") -and
            $_.CommandLine -like "*-File*StartSideScreenWatchdog.ps1*" -and
            $_.CommandLine -like "*StartSideScreenWatchdog.ps1*" -and
            $_.CommandLine -like $rootPattern
        } |
        ForEach-Object {
            $stopped++
            Write-WatchdogLog ("stopping old watchdog PID={0} reason={1}" -f $_.ProcessId, $Reason)
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }

    if ($stopped -gt 0) {
        Start-Sleep -Milliseconds 1500
    }
}

$watchdogMutexName = "Global\TURZX.SideScreen.Watchdog"
$watchdogMutexCreated = $false
$watchdogMutex = $null
try {
    $watchdogMutex = New-Object System.Threading.Mutex($true, $watchdogMutexName, [ref]$watchdogMutexCreated)
}
catch {
    Write-WatchdogLog ("failed to create watchdog mutex: {0}" -f $_.Exception.Message)
    exit 1
}

if (-not $watchdogMutexCreated) {
    Write-WatchdogLog "duplicate watchdog detected; keeping existing instance"
    $watchdogMutex.Dispose()
    exit 0
}

Start-WatchdogParentLivenessGuard
Set-Content -LiteralPath $watchdogPidPath -Value $PID -Encoding ASCII
try {
    $watchdogProcess = Get-Process -Id $PID -ErrorAction Stop
    if ($watchdogProcess.PriorityClass -in @(
        [Diagnostics.ProcessPriorityClass]::Idle,
        [Diagnostics.ProcessPriorityClass]::BelowNormal,
        [Diagnostics.ProcessPriorityClass]::Normal)) {
        $watchdogProcess.PriorityClass = [Diagnostics.ProcessPriorityClass]::AboveNormal
    }
    Write-WatchdogLog ("watchdog process priority={0}" -f $watchdogProcess.PriorityClass)
}
catch {
    Write-WatchdogLog ("watchdog priority warning: {0}" -f $_.Exception.Message)
}

foreach ($eventSourceId in @($powerSourceId, $shutdownSourceId)) {
    Get-EventSubscriber -SourceIdentifier $eventSourceId -ErrorAction SilentlyContinue |
        Unregister-Event -ErrorAction SilentlyContinue
    Get-Event -SourceIdentifier $eventSourceId -ErrorAction SilentlyContinue |
        Remove-Event -ErrorAction SilentlyContinue
}

Stop-OtherWatchdogs -Reason "watchdog-start"
Remove-Item -LiteralPath $pausedPath -Force -ErrorAction SilentlyContinue
Write-WatchdogLog "cleared paused flag at watchdog start"

$powerSubscription = $null
$shutdownSubscription = $null
if (-not $NoPowerEvents) {
    $powerQuery = "SELECT * FROM Win32_PowerManagementEvent WHERE EventType = 4 OR EventType = 7 OR EventType = 18"
    $powerSubscription = Register-WmiEvent -Query $powerQuery -SourceIdentifier $powerSourceId
    Write-WatchdogLog "registered Win32_PowerManagementEvent watcher: EventType = 4 suspend, EventType = 7 resume, EventType = 18 automatic resume"
    $shutdownSubscription = Register-WmiEvent -Query "SELECT * FROM Win32_ComputerShutdownEvent" -SourceIdentifier $shutdownSourceId
    Write-WatchdogLog "registered Win32_ComputerShutdownEvent watcher for shutdown/restart blanking"
}
Write-WatchdogLog ("display power policy=hs2-preserve-current-mode-then-verified-secondary/turzx-brightness-123 activeBrightness={0} lconnectPort={1}" -f `
    $ActiveBrightness, $LConnectServicePort)
Write-WatchdogLog (
    "TURZX exact serial recovery=enabled identity=VID_0525&PID_A4A7 failureThreshold={0} retrySeconds={1} waitSeconds={2}" -f `
        $TurzxSerialRecoveryFailureThreshold,
        $TurzxSerialRecoveryRetrySeconds,
        $TurzxSerialRecoveryWaitSeconds)
Write-WatchdogLog ("HS2 secondary promotion graceSeconds={0} mode=one-attempt-per-startup-or-resume-epoch" -f `
    $HS2SecondaryPromotionGraceSeconds)
Write-WatchdogLog ("HS2 overlay watchdog=secondary-verified-only retrySeconds={0}" -f $HS2OverlayRetrySeconds)
Write-WatchdogLog (
    "HS2 native recovery=enabled retrySeconds={0} verifySeconds={1}" -f `
        $HS2ActiveRetrySeconds,
        $HS2ActiveVerifySeconds)
Write-WatchdogLog (
    "Wallpaper Engine render recovery=topology-or-primary-only probeSeconds={0} stabilitySeconds={1} cooldownSeconds={2}; HDR-only changes are ignored" -f `
        $WallpaperDisplayProbeSeconds,
        $WallpaperDisplayStabilitySeconds,
        $WallpaperRenderRebindCooldownSeconds)

$child = Set-TurzxChildHeartbeatStartupWindow -Child $null
$consecutiveFailures = 0
$heartbeatFailures = 0
$snapshotStaleHeartbeats = 0
try {
    Write-WatchdogLog "HS2 ordinary-window protection starts before controller and overlay verification"
    Invoke-HS2ExclusiveWindowProtection
    Set-ActiveDisplayState -Reason "watchdog-start"
    Invoke-HS2OverlayHealthCheck
    Invoke-HS2ExclusiveWindowProtection
    Invoke-WallpaperEngineDisplayRecovery
    $initialAttempt = Invoke-TurzxStackRestartAttempt -Reason "watchdog-start"
    $child = Set-TurzxChildHeartbeatStartupWindow -Child $initialAttempt.Child
    if (-not $initialAttempt.Succeeded) { $consecutiveFailures = 1 }
    while ($true) {
        $event = $null
        if (-not $NoPowerEvents) {
            $event = Wait-Event -Timeout $PollSeconds
        }
        else {
            Start-Sleep -Seconds $PollSeconds
        }

        if ($event) {
            try {
                if ($event.SourceIdentifier -eq $powerSourceId) {
                    $eventType = [int]$event.SourceEventArgs.NewEvent.EventType
                    Write-WatchdogLog ("power event type={0}" -f $eventType)
                    if ($eventType -eq 4) {
                        # A new suspend starts a new power epoch.  Do not let a
                        # resume handled just before this suspend suppress the
                        # next real resume as though it were the duplicate
                        # 7/18 notification from the earlier epoch.
                        $script:hs2LastResumeHandledUtc = [DateTime]::MinValue
                        Enter-SleepDisplayState
                        $child = Set-TurzxChildHeartbeatStartupWindow -Child $null
                        $heartbeatFailures = 0
                        $snapshotStaleHeartbeats = 0
                    }
                    elseif ($eventType -eq 7 -or $eventType -eq 18) {
                        $resumeNowUtc = [DateTime]::UtcNow
                        $resumeDecision = Get-HS2ResumeEventDecision `
                            -EventType $eventType `
                            -LastHandledUtc $script:hs2LastResumeHandledUtc `
                            -NowUtc $resumeNowUtc `
                            -MergeSeconds 30
                        if ($resumeDecision.Action -eq "Ignore") {
                            Write-WatchdogLog (
                                "duplicate resume event type={0} ignored mergeRemainingSeconds={1:N0}" -f `
                                    $eventType,
                                    $resumeDecision.RetryAfterSeconds)
                        }
                        else {
                            $script:hs2LastResumeHandledUtc = $resumeNowUtc
                            $consecutiveFailures = 0
                            Remove-Item -LiteralPath $pausedPath -Force -ErrorAction SilentlyContinue
                            try {
                                Stop-Stack -Reason "resume"
                            }
                            catch {
                                Write-WatchdogLog ("resume stop proof deferred type={0} error={1}; watchdog remains active" -f `
                                    $_.Exception.GetType().FullName, $_.Exception.Message)
                                $child = Set-TurzxChildHeartbeatStartupWindow -Child $null
                                continue
                            }
                            Start-Sleep -Seconds $ResumeDelaySeconds
                            Set-ActiveDisplayState -Reason ("resume-event-{0}" -f $eventType)
                            $resumeAttempt = Invoke-TurzxStackRestartAttempt -Reason ("resume-event-{0}" -f $eventType)
                            $child = Set-TurzxChildHeartbeatStartupWindow -Child $resumeAttempt.Child
                            if (-not $resumeAttempt.Succeeded) { $consecutiveFailures++ }
                            $heartbeatFailures = 0
                            $snapshotStaleHeartbeats = 0
                        }
                    }
                }
                elseif ($event.SourceIdentifier -eq $shutdownSourceId) {
                    $typeProperty = $event.SourceEventArgs.NewEvent.PSObject.Properties["Type"]
                    $shutdownEventType = if ($null -ne $typeProperty) { $typeProperty.Value } else { $null }
                    $shutdownDecision = Get-TurzxShutdownEventDecision `
                        -EventType $shutdownEventType `
                        -WatchdogStartedUtc $watchdogStartedUtc `
                        -NowUtc ([DateTime]::UtcNow) `
                        -StartupGraceSeconds $ShutdownStartupGraceSeconds
                    Write-WatchdogLog ("computer shutdown event type={0} action={1} reason={2} watchdogAgeSeconds={3:N1}" -f `
                        $shutdownDecision.Type, $shutdownDecision.Action, $shutdownDecision.Reason, $shutdownDecision.AgeSeconds)
                    if ($shutdownDecision.Action -eq "Shutdown") {
                        Enter-ShutdownDisplayState
                        break
                    }
                }
                else {
                    Write-WatchdogLog ("ignored event source={0}" -f $event.SourceIdentifier)
                }
            }
            catch {
                Write-WatchdogLog ("power event handling deferred type={0} error={1}; watchdog remains active" -f `
                    $_.Exception.GetType().FullName, $_.Exception.Message)
            }
            finally {
                Remove-Event -EventIdentifier $event.EventIdentifier -ErrorAction SilentlyContinue
            }
        }

        if (Test-Path -LiteralPath $restartFlag -PathType Leaf) {
            Write-WatchdogLog "restart request detected; recycling stack through active watchdog"
            $consecutiveFailures = 0
            $heartbeatFailures = 0
            $restartAttempt = Invoke-TurzxStackRestartAttempt -Reason "restart-request"
            $child = Set-TurzxChildHeartbeatStartupWindow -Child $restartAttempt.Child
            if (-not $restartAttempt.Succeeded) { $consecutiveFailures++ }
            $snapshotStaleHeartbeats = 0
            continue
        }

        Invoke-HS2ActiveMaintenance
        Invoke-HS2OverlayHealthCheck
        Invoke-HS2ExclusiveWindowProtection
        Invoke-WallpaperEngineDisplayRecovery

        if ($null -eq $child) {
            $consecutiveFailures++
            Write-WatchdogLog ("stack child unavailable; consecutiveFailures={0}; retry remains inside watchdog" -f $consecutiveFailures)
            if ($consecutiveFailures -ge $MaxConsecutiveFailures) {
                $missingChildAttempt = Invoke-TurzxFailureCircuitBreaker -Reason "child-unavailable"
                $consecutiveFailures = 0
            }
            else {
                $missingChildAttempt = Invoke-TurzxStackRestartAttempt -Reason "child-unavailable" -DelaySeconds 3
            }
            $child = Set-TurzxChildHeartbeatStartupWindow -Child $missingChildAttempt.Child
            $heartbeatFailures = 0
            $snapshotStaleHeartbeats = 0
            continue
        }
        if ($child.HasExited) {
            $consecutiveFailures++
            Write-WatchdogLog ("stack child exited code={0}; consecutiveFailures={1}" -f $child.ExitCode, $consecutiveFailures)
            if ($consecutiveFailures -ge $MaxConsecutiveFailures) {
                $childExitAttempt = Invoke-TurzxFailureCircuitBreaker -Reason "child-exit"
                $child = Set-TurzxChildHeartbeatStartupWindow -Child $childExitAttempt.Child
                $consecutiveFailures = 0
                $heartbeatFailures = 0
                $snapshotStaleHeartbeats = 0
                continue
            }
            $childExitAttempt = Invoke-TurzxStackRestartAttempt -Reason "child-exit" -DelaySeconds 3
            $child = Set-TurzxChildHeartbeatStartupWindow -Child $childExitAttempt.Child
            $heartbeatFailures = 0
            $snapshotStaleHeartbeats = 0
        }
        elseif (Test-TurzxChildHeartbeatStartupDeadline) {
            $heartbeatHealth = Get-StreamHeartbeatHealth
            if ($heartbeatHealth.Healthy) {
                $previousSnapshotStaleHeartbeats = $snapshotStaleHeartbeats
                $snapshotDecision = Get-TurzxSnapshotStaleDecision `
                    -SnapshotStatus $heartbeatHealth.SnapshotStatus `
                    -ConsecutiveFailures $snapshotStaleHeartbeats `
                    -Threshold $MaxConsecutiveSnapshotStaleHeartbeats
                $snapshotStaleHeartbeats = $snapshotDecision.ConsecutiveFailures
                if ($snapshotDecision.IsStale -and
                    $snapshotDecision.ConsecutiveFailures -eq 1) {
                    Write-WatchdogLog (
                        "snapshot stale tolerated reason={0}" -f $snapshotDecision.Reason)
                }
                elseif ($snapshotDecision.IsStale -and
                    $snapshotDecision.ConsecutiveFailures -eq $MaxConsecutiveSnapshotStaleHeartbeats) {
                    Write-WatchdogLog (
                        "snapshot stale threshold reached reason={0}" -f $snapshotDecision.Reason)
                }
                if ($snapshotDecision.FailureThresholdReached) {
                    $heartbeatHealth = [pscustomobject]@{
                        Healthy = $false
                        Reason = $snapshotDecision.Reason
                    }
                }
                else {
                    $script:childHeartbeatObserved = $true
                    if ($heartbeatFailures -gt 0) {
                        Write-WatchdogLog ("heartbeat recovered {0}" -f $heartbeatHealth.Reason)
                    }
                    elseif ($previousSnapshotStaleHeartbeats -gt 0) {
                        Write-WatchdogLog (
                            "snapshot recovered after consecutive={0}" -f $previousSnapshotStaleHeartbeats)
                    }
                    $heartbeatFailures = 0
                    $consecutiveFailures = 0
                }
            }
            if (-not $heartbeatHealth.Healthy) {
                if (-not $script:childHeartbeatObserved -and $heartbeatHealth.Reason -eq "missing") {
                    $consecutiveFailures++
                    Write-WatchdogLog (
                        "startup heartbeat deadline reached reason=missing; converging through failure circuit before another stack start")
                    $heartbeatAttempt = Invoke-TurzxFailureCircuitBreaker -Reason "heartbeat-missing-startup"
                    $child = Set-TurzxChildHeartbeatStartupWindow -Child $heartbeatAttempt.Child
                    $consecutiveFailures = 0
                    $heartbeatFailures = 0
                    $snapshotStaleHeartbeats = 0
                    continue
                }
                $heartbeatFailures++
                Write-WatchdogLog ("heartbeat unhealthy reason={0}; consecutiveHeartbeatFailures={1}" -f $heartbeatHealth.Reason, $heartbeatFailures)
                if ($heartbeatFailures -ge $MaxConsecutiveHeartbeatFailures) {
                    $consecutiveFailures++
                    if ($consecutiveFailures -ge $MaxConsecutiveFailures) {
                        $heartbeatAttempt = Invoke-TurzxFailureCircuitBreaker -Reason "heartbeat-stalls"
                        $child = Set-TurzxChildHeartbeatStartupWindow -Child $heartbeatAttempt.Child
                        $consecutiveFailures = 0
                        $heartbeatFailures = 0
                        $snapshotStaleHeartbeats = 0
                        continue
                    }
                    $heartbeatAttempt = Invoke-TurzxStackRestartAttempt -Reason "heartbeat-unhealthy" -DelaySeconds 3
                    $child = Set-TurzxChildHeartbeatStartupWindow -Child $heartbeatAttempt.Child
                    $heartbeatFailures = 0
                    $snapshotStaleHeartbeats = 0
                }
            }
        }
    }
}
finally {
    Stop-WatchdogParentLivenessGuard
    foreach ($eventSourceId in @($powerSourceId, $shutdownSourceId)) {
        Get-EventSubscriber -SourceIdentifier $eventSourceId -ErrorAction SilentlyContinue |
            Unregister-Event -ErrorAction SilentlyContinue
        Get-Event -SourceIdentifier $eventSourceId -ErrorAction SilentlyContinue |
            Remove-Event -ErrorAction SilentlyContinue
    }
    try {
        Stop-Stack -Reason "watchdog-exit"
    }
    catch {
        Write-WatchdogLog ("watchdog exit stop proof failed type={0} error={1}" -f `
            $_.Exception.GetType().FullName, $_.Exception.Message)
    }
    try {
        if ((Get-Content -Raw -LiteralPath $watchdogPidPath -ErrorAction Stop).Trim() -eq [string]$PID) {
            Remove-Item -LiteralPath $watchdogPidPath -Force -ErrorAction SilentlyContinue
        }
    }
    catch { }
    if ($watchdogMutex) {
        $watchdogMutex.ReleaseMutex()
        $watchdogMutex.Dispose()
    }
}
