param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Port = "COM7",
    [int]$IntervalMs = 1000,
    [int]$FullResyncEveryFrames = 300,
    [int]$PreviewIntervalSeconds = 45,
    [int64]$MaxStackLogBytes = 1048576,
    [int64]$MaxStreamLogBytes = 5242880,
    [int64]$MaxWatchdogLogBytes = 2097152,
    [ValidateRange(1, 32)][int]$LogBackupCount = 3,
    [int]$ResumeDelaySeconds = 8,
    [int]$QuickBlankTimeoutMs = 2500,
    [int]$PollSeconds = 2,
    [int]$HeartbeatStaleSeconds = 15,
    [int]$HeartbeatStartupGraceSeconds = 60,
    [int]$ShutdownStartupGraceSeconds = 180,
    [int]$MaxConsecutiveHeartbeatFailures = 3,
    [int]$MaxConsecutiveFailures = 3,
    [ValidateRange(5, 3600)][int]$HS2OverlayRetrySeconds = 30,
    [ValidateRange(0, 255)][int]$ActiveBrightness = 170,
    [ValidateRange(1, 65535)][int]$LConnectServicePort = 11021,
    [switch]$NoPowerEvents
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath $Root).Path
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $scriptDir "out"
$logPath = Join-Path $outDir "side-screen-watchdog.log"
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
$overlayWatchdogPolicy = Join-Path $scriptDir "HS2OverlayWatchdogPolicy.ps1"
$powerSourceId = "TURZXSideScreenPower"
$shutdownSourceId = "TURZXSideScreenShutdown"
$watchdogStartedUtc = [DateTime]::UtcNow
$script:hs2OverlayLastAttemptUtc = [DateTime]::MinValue
$script:hs2OverlayWasRunning = $false
$script:hs2DisplayStateActive = $false

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
. $shutdownPolicy
. $displayPowerPolicy
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
function Stop-Stack {
    param([string]$Reason)
    Write-WatchdogLog ("stop stack reason={0}" -f $Reason)
    powershell -NoProfile -ExecutionPolicy Bypass -File $stopScript -Root $Root -Quiet | Out-Null
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
        Write-WatchdogLog ("TURZX brightness applied brightness={0}" -f $Brightness)
        return $true
    }
    catch {
        Write-WatchdogLog ("TURZX brightness failed brightness={0}: {1}" -f $Brightness, $_.Exception.Message)
        return $false
    }
}

function Set-ActiveDisplayState {
    param([string]$Reason)

    $script:hs2DisplayStateActive = $true
    try {
        Invoke-HS2PowerState -State Active -ServicePort $LConnectServicePort | Out-Null
        Write-WatchdogLog ("HS2 power state=Active reason={0} verified=true" -f $Reason)
    }
    catch {
        Write-WatchdogLog ("HS2 power state=Active reason={0} failed: {1}" -f $Reason, $_.Exception.Message)
    }
}

function Invoke-HS2OverlayHealthCheck {
    if (-not $script:hs2DisplayStateActive) {
        return
    }

    $nowUtc = [DateTime]::UtcNow
    $overlayProcess = Get-HS2OverlayProcess
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

function Enter-SleepDisplayState {
    $script:hs2DisplayStateActive = $false
    $hs2TransitionStarted = $false
    try {
        $hs2TransitionStarted = Start-HS2MonitorModeTransition -ServicePort $LConnectServicePort
        Write-WatchdogLog ("HS2 monitor-mode transition requested state=Sleep started={0}" -f $hs2TransitionStarted)
    }
    catch {
        Write-WatchdogLog ("HS2 monitor-mode request state=Sleep failed: {0}" -f $_.Exception.Message)
    }

    Stop-Stack -Reason "suspend"
    if (-not (Set-TurzxPanelBrightness -Brightness 0)) {
        Write-WatchdogLog "TURZX power-off reason=suspend fallback=black-frame"
        Send-Blank -Reason "power-off-fallback/suspend" -TimeoutMs $QuickBlankTimeoutMs
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
    $script:hs2DisplayStateActive = $false
    $hs2TransitionStarted = $false
    try {
        $hs2TransitionStarted = Start-HS2MonitorModeTransition -ServicePort $LConnectServicePort
        Write-WatchdogLog ("HS2 monitor-mode transition requested state=Shutdown started={0}" -f $hs2TransitionStarted)
    }
    catch {
        Write-WatchdogLog ("HS2 monitor-mode request state=Shutdown failed: {0}" -f $_.Exception.Message)
    }

    Stop-Stack -Reason "shutdown"
    if (-not (Set-TurzxPanelBrightness -Brightness 0)) {
        Write-WatchdogLog "TURZX power-off reason=shutdown fallback=black-frame"
        Send-Blank -Reason "power-off-fallback/shutdown" -TimeoutMs $QuickBlankTimeoutMs
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
    Write-WatchdogLog ("start stack reason={0} root={1} port={2} interval={3}" -f $Reason, $Root, $Port, $IntervalMs)
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $stackScript,
        "-Root", $Root,
        "-Port", $Port,
        "-IntervalMs", [string]$IntervalMs,
        "-FullResyncEveryFrames", [string]$FullResyncEveryFrames,
        "-PreviewIntervalSeconds", [string]$PreviewIntervalSeconds,
        "-MaxStackLogBytes", [string]$MaxStackLogBytes,
        "-MaxStreamLogBytes", [string]$MaxStreamLogBytes,
        "-LogBackupCount", [string]$LogBackupCount,
        "-Worker"
    )
    $process = Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -WindowStyle Hidden -PassThru
    Set-Content -LiteralPath $childPidPath -Value $process.Id -Encoding ASCII
    Write-WatchdogLog ("stack child pid={0}" -f $process.Id)
    return $process
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
            if ($frameStatus -ne "ok") {
                return [pscustomobject]@{
                    Healthy = $false
                    Reason = ("frame-status={0} error={1}" -f $frameStatus, [string]$heartbeat.error)
                }
            }
            return [pscustomobject]@{
                Healthy = $true
                Reason = ("frame={0} source={1}" -f [int64]$heartbeat.frame, $item.Name)
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

Set-Content -LiteralPath $watchdogPidPath -Value $PID -Encoding ASCII

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
Write-WatchdogLog ("display power policy=hs2-transition-first/turzx-brightness-123 activeBrightness={0} lconnectPort={1}" -f `
    $ActiveBrightness, $LConnectServicePort)
Write-WatchdogLog ("HS2 overlay watchdog=enabled retrySeconds={0}" -f $HS2OverlayRetrySeconds)

$child = $null
$consecutiveFailures = 0
$heartbeatFailures = 0
$childStartedUtc = [DateTime]::UtcNow
try {
    Set-ActiveDisplayState -Reason "watchdog-start"
    Invoke-HS2OverlayHealthCheck
    $child = Start-Stack -Reason "watchdog-start"
    $childStartedUtc = [DateTime]::UtcNow
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
                        Enter-SleepDisplayState
                        $child = $null
                        $heartbeatFailures = 0
                    }
                    elseif ($eventType -eq 7 -or $eventType -eq 18) {
                        $consecutiveFailures = 0
                        Remove-Item -LiteralPath $pausedPath -Force -ErrorAction SilentlyContinue
                        Stop-Stack -Reason "resume"
                        Start-Sleep -Seconds $ResumeDelaySeconds
                        Set-ActiveDisplayState -Reason ("resume-event-{0}" -f $eventType)
                        $child = Start-Stack -Reason ("resume-event-{0}" -f $eventType)
                        $childStartedUtc = [DateTime]::UtcNow
                        $heartbeatFailures = 0
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
            finally {
                Remove-Event -EventIdentifier $event.EventIdentifier -ErrorAction SilentlyContinue
            }
        }

        if (Test-Path -LiteralPath $restartFlag -PathType Leaf) {
            Write-WatchdogLog "restart request detected; recycling stack through active watchdog"
            $consecutiveFailures = 0
            $heartbeatFailures = 0
            $child = Start-Stack -Reason "restart-request"
            $childStartedUtc = [DateTime]::UtcNow
            continue
        }

        Invoke-HS2OverlayHealthCheck

        if ($child -and $child.HasExited) {
            $consecutiveFailures++
            Write-WatchdogLog ("stack child exited code={0}; consecutiveFailures={1}" -f $child.ExitCode, $consecutiveFailures)
            if ($consecutiveFailures -ge $MaxConsecutiveFailures) {
                Set-Content -LiteralPath $pausedPath -Value (Get-Date -Format "o") -Encoding ASCII
                Write-WatchdogLog ("max consecutive failures reached ({0}); watchdog paused" -f $MaxConsecutiveFailures)
                break
            }
            Start-Sleep -Seconds 3
            $child = Start-Stack -Reason "child-exit"
            $childStartedUtc = [DateTime]::UtcNow
            $heartbeatFailures = 0
        }
        elseif ($child -and (([DateTime]::UtcNow - $childStartedUtc).TotalSeconds -ge $HeartbeatStartupGraceSeconds)) {
            $heartbeatHealth = Get-StreamHeartbeatHealth
            if ($heartbeatHealth.Healthy) {
                if ($heartbeatFailures -gt 0) {
                    Write-WatchdogLog ("heartbeat recovered {0}" -f $heartbeatHealth.Reason)
                }
                $heartbeatFailures = 0
                $consecutiveFailures = 0
            }
            else {
                $heartbeatFailures++
                Write-WatchdogLog ("heartbeat unhealthy reason={0}; consecutiveHeartbeatFailures={1}" -f $heartbeatHealth.Reason, $heartbeatFailures)
                if ($heartbeatFailures -ge $MaxConsecutiveHeartbeatFailures) {
                    $consecutiveFailures++
                    if ($consecutiveFailures -ge $MaxConsecutiveFailures) {
                        Set-Content -LiteralPath $pausedPath -Value (Get-Date -Format "o") -Encoding ASCII
                        Write-WatchdogLog ("max consecutive failures reached ({0}) after heartbeat stalls; watchdog paused" -f $MaxConsecutiveFailures)
                        break
                    }
                    Stop-Stack -Reason "heartbeat-unhealthy"
                    Start-Sleep -Seconds 3
                    $child = Start-Stack -Reason "heartbeat-unhealthy"
                    $childStartedUtc = [DateTime]::UtcNow
                    $heartbeatFailures = 0
                }
            }
        }
    }
}
finally {
    foreach ($eventSourceId in @($powerSourceId, $shutdownSourceId)) {
        Get-EventSubscriber -SourceIdentifier $eventSourceId -ErrorAction SilentlyContinue |
            Unregister-Event -ErrorAction SilentlyContinue
        Get-Event -SourceIdentifier $eventSourceId -ErrorAction SilentlyContinue |
            Remove-Event -ErrorAction SilentlyContinue
    }
    Stop-Stack -Reason "watchdog-exit"
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
