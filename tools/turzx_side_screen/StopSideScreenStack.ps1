param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [switch]$IncludeWatchdog,
    [switch]$SkipStackEntrypoint,
    [switch]$KeepMetrics,
    [switch]$Quiet,
    [ValidateRange(1, 30)][int]$ProcessSnapshotTimeoutSeconds = 8,
    [string]$RecoveryGate,
    [int]$RecoveryParentId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath $Root).Path
$side = Join-Path $Root "tools\turzx_side_screen"
$weather = Join-Path $Root "tools\turzx_weather_shim"
$outDir = Join-Path $side "out"
$logPath = Join-Path $outDir "side-screen-stop.log"
$ownsStopMutex = $false
$stopMutex = $null
trap {
    if ($ownsStopMutex) { $stopMutex.ReleaseMutex() }
    if ($null -ne $stopMutex) { $stopMutex.Dispose() }
    break
}
$stopMutex = [Threading.Mutex]::new($true, 'Global\TURZX.SideScreen.Stop', [ref]$ownsStopMutex)
if (-not $ownsStopMutex) { throw 'Another TURZX stack stop is still active' }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Write-StopLog {
    param([string]$Message)
    $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    if (-not $Quiet) {
        Write-Host $line
    }
}

$script:stopPhaseWatch = [System.Diagnostics.Stopwatch]::StartNew()
function Write-StopPhase {
    param([string]$Phase)
    Write-StopLog ('phase={0} stopPid={1} elapsedMs={2}' -f $Phase, $PID, $script:stopPhaseWatch.ElapsedMilliseconds)
}
Write-StopPhase 'entry'

function Get-TurzxProcessSnapshot {
    param([ValidateRange(1, 30)][int]$TimeoutSeconds, [int[]]$RecordedIds = @())

    $projectFilters = foreach ($path in @($side, $weather)) {
        $escaped = $path.Replace('\', '\\').Replace("'", "\'").Replace('"', '\"').Replace('[', '[[]').Replace('%', '[%]').Replace('_', '[_]')
        "CommandLine LIKE '%$escaped\\%'"
    }
    $filter = "Name LIKE 'TURZX.SideScreen.Stream%.exe' OR ((Name='powershell.exe' OR Name='pwsh.exe' OR Name LIKE 'python%.exe' OR Name='wscript.exe' OR Name='cscript.exe') AND ($($projectFilters -join ' OR ')))"
    foreach ($recordedId in $RecordedIds) { if ($recordedId -gt 0) { $filter += " OR ProcessId=$recordedId" } }
    try {
        return @(Get-CimInstance -ClassName Win32_Process -Filter $filter -OperationTimeoutSec $TimeoutSeconds `
                -Property Name,ProcessId,ParentProcessId,CommandLine,CreationDate,ExecutablePath -ErrorAction Stop)
    }
    catch {
        # A local CIM provider can stall while a device/driver is unhealthy.
        # The caller still runs the name-bound stream stop and proof path, but
        # skips command-line based helper cleanup instead of blocking forever.
        Write-StopLog ("process snapshot unavailable timeoutSeconds={0}: {1}" -f `
                $TimeoutSeconds, $_.Exception.Message)
        if ($IncludeWatchdog) { throw }
        return @()
    }
}

function Stop-MatchingProcess {
    param(
        [scriptblock]$Predicate,
        [string]$Reason,
        [switch]$RequireExit
    )

    $stoppedProcessIds = New-Object 'System.Collections.Generic.List[int]'
    $processSnapshot |
        Where-Object {
            $_.ProcessId -ne $PID -and (& $Predicate $_)
        } |
        ForEach-Object {
            $candidate = $_
            [void]$stoppedProcessIds.Add([int]$candidate.ProcessId)
            Write-StopLog ("stopping PID={0} reason={1} CMD={2}" -f $candidate.ProcessId, $Reason, $candidate.CommandLine)
            $process = $null
            try {
                $process = Get-SnapshotProcess $candidate
                if ($process) {
                    try { $process.Kill() }
                    catch { if (-not $process.HasExited) { throw } }
                    if ($RequireExit -and -not $process.WaitForExit(5000)) { throw 'Process did not exit' }
                }
            }
            catch {
                Write-StopLog ("failed to stop PID={0} reason={1}: {2}" -f $candidate.ProcessId, $Reason, $_.Exception.Message)
                if ($RequireExit) { throw }
            }
            finally { if ($process) { $process.Dispose() } }
        }
    return $stoppedProcessIds.ToArray()
}

Write-StopPhase 'metrics-policy-start'
. (Join-Path $PSScriptRoot 'MetricsEndpointPolicy.ps1')
Write-StopPhase 'metrics-policy-done'

function Assert-ManagedMetricsAgentExitAndPortRelease {
    param(
        [int[]]$ProcessIds,
        [ValidateRange(1, 30)][int]$TimeoutSeconds = 8
    )

    $endpoint = Get-TurzxMetricsEndpoint
    if (Wait-ManagedMetricsAgentExitAndPortRelease -ProcessIds $ProcessIds -Endpoint $endpoint -TimeoutSeconds $TimeoutSeconds) { return }
    $remaining = @($ProcessIds | Where-Object { Test-MetricsProcessActive -ProcessId $_ })
    $probeErrorVariable = Get-Variable -Name metricsPortProbeError -Scope Script -ErrorAction SilentlyContinue
    $portProbeError = if ($null -ne $probeErrorVariable) { [string]$probeErrorVariable.Value } else { 'unavailable' }
    $listenerOwners = 'unavailable'
    try {
        $ownerIds = @(
            Get-NetTCPConnection -State Listen -LocalPort $endpoint.Port -ErrorAction Stop |
                Select-Object -ExpandProperty OwningProcess -Unique |
                Sort-Object
        )
        $listenerOwners = if ($ownerIds.Count -eq 0) { 'none' } else { $ownerIds -join ',' }
    }
    catch {
        $listenerOwners = 'probe-failed: ' + $_.Exception.Message
    }
    $remainingText = if ($remaining.Count -eq 0) { 'none' } else { $remaining -join ',' }
    $evidence = "remainingPids={0} metricsPort={1} metricsPortOwners={2} metricsPortProbeError={3}" -f `
        $remainingText, $endpoint.Port, $listenerOwners, $portProbeError
    Write-StopLog ("metrics stop proof failed {0}" -f $evidence)
    throw "Managed metrics agent did not exit or port $($endpoint.Port) did not release; $evidence"
}

function Wait-TurzxStreamProcessesExit {
    param([ValidateRange(1, 60)][int]$TimeoutSeconds = 10)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $remaining = @(Get-Process "TURZX.SideScreen.Stream*" -ErrorAction SilentlyContinue)
        if ($remaining.Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    $remaining = @(Get-Process "TURZX.SideScreen.Stream*" -ErrorAction SilentlyContinue)
    if ($remaining.Count -gt 0) {
        $ids = @($remaining | ForEach-Object { $_.Id }) -join ","
        Write-StopLog ("stream processes did not exit before timeout pids={0}" -f $ids)
        throw "TURZX stream processes did not exit before timeout: $ids"
    }
}

function Stop-TurzxWatchdogLaunchers {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Snapshot,
        [Parameter(Mandatory = $true)][string]$LauncherPath
    )

    $launcherArgument = '(?i)(?:^|[\s"])' + [regex]::Escape($LauncherPath) + '(?:[\s"]|$)'
    foreach ($candidate in $Snapshot) {
        if ([string]$candidate.Name -notin @('wscript.exe', 'cscript.exe') -or
            [string]$candidate.CommandLine -notmatch $launcherArgument) {
            continue
        }
        Write-StopLog ("stopping watchdog launcher PID={0}" -f $candidate.ProcessId)
        Stop-Process -Id $candidate.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Stop-RecordedWatchdogProcess {
    param(
        [int]$RecordedPid,
        [int]$RecordedChildPid,
        [object[]]$Snapshot
    )

    if (-not $RecordedPid) { return }

    $candidate = @($Snapshot | Where-Object { [int]$_.ProcessId -eq $recordedPid })
    if ($candidate.Count -eq 0) {
        if (Test-MetricsProcessActive -ProcessId $recordedPid) { throw "Recorded watchdog identity not verified: $recordedPid" }
        Write-StopLog ("recorded watchdog already exited PID={0}" -f $recordedPid)
        return
    }
    if ($candidate.Count -ne 1) {
        throw "Recorded watchdog PID is ambiguous: $recordedPid"
    }

    $processName = [string]$candidate[0].Name
    $commandIdentityMatches = Test-OwnedStreamParent $candidate[0] $side 'StartSideScreenWatchdog.ps1'
    $treeIdentityMatches = $processName -in @('powershell.exe', 'pwsh.exe') -and
        [string]::IsNullOrWhiteSpace([string]$candidate[0].CommandLine) -and @($Snapshot | Where-Object {
            [int]$_.ProcessId -eq $recordedChildPid -and [int]$_.ParentProcessId -eq $recordedPid -and
                (Test-OwnedStreamParent $_ $side 'StartSideScreenStack.ps1')
        }).Count -eq 1

    if (-not ($commandIdentityMatches -or $treeIdentityMatches)) {
        Write-StopLog ("recorded watchdog identity not verified PID={0} name={1}; refusing to stop" -f $recordedPid, $processName)
        throw "Recorded watchdog identity not verified: $recordedPid"
    }

    Write-StopLog ("stopping recorded watchdog PID={0} identity={1}" -f `
            $recordedPid,
            $(if ($commandIdentityMatches) { "command-line" } else { "process-tree" }))
    [void](Stop-MatchingProcess -Reason 'recorded-watchdog' -RequireExit -Predicate { param($p) $p.ProcessId -eq $recordedPid })
}

function Get-RecordedProcessId {
    param([string]$Name, [switch]$RequireValid)
    $path = Join-Path $outDir $Name
    if (-not (Test-Path -LiteralPath $path)) { return 0 }
    $raw = [string](Get-Content -Raw -LiteralPath $path -ErrorAction Stop)
    $processId = 0
    if ([int]::TryParse($raw.Trim(), [ref]$processId) -and $processId -gt 0) { return $processId }
    if ($RequireValid) { throw "Invalid recorded watchdog PID: $raw" }
    return 0
}

function Test-OwnedStreamImage {
    param($Candidate)
    $Candidate.Name -like 'TURZX.SideScreen.Stream*.exe' -and
        ([string]$Candidate.ExecutablePath).StartsWith($side + '\', [StringComparison]::OrdinalIgnoreCase)
}

$recordedStackId = Get-RecordedProcessId 'side-screen-stack-child.pid'
$recordedWatchdogId = if ($IncludeWatchdog) { Get-RecordedProcessId 'side-screen-watchdog.pid' -RequireValid } else { 0 }
Write-StopPhase 'process-snapshot-start'
$processSnapshot = @(Get-TurzxProcessSnapshot -TimeoutSeconds $ProcessSnapshotTimeoutSeconds -RecordedIds @($recordedStackId, $recordedWatchdogId))
Write-StopPhase 'process-snapshot-done'

if ($RecoveryGate) {
    $ready = [Threading.EventWaitHandle]::OpenExisting("$RecoveryGate.ready")
    $permit = [Threading.EventWaitHandle]::OpenExisting("$RecoveryGate.permit")
    $parent = Get-Process -Id $RecoveryParentId -ErrorAction Stop
    try {
        [void]$ready.Set()
        while (-not $permit.WaitOne(200)) {
            if ($parent.HasExited -or $script:stopPhaseWatch.Elapsed.TotalSeconds -ge 120) { throw 'Stop recovery permit expired' }
        }
        if ($parent.HasExited) { throw 'Stop recovery parent exited' }
    }
    finally { $ready.Dispose(); $permit.Dispose(); $parent.Dispose() }
}

if ($recordedStackId -gt 0) {
    $recordedProcess = Get-Process -Id $recordedStackId -ErrorAction SilentlyContinue
    if ($recordedProcess -and $recordedProcess.ProcessName -in @('powershell', 'pwsh') -and
        -not @($processSnapshot | Where-Object { $_.ProcessId -eq $recordedStackId -and $_.CommandLine }).Count) {
        throw "Cannot verify active recorded stack process PID=$recordedStackId; stop deferred"
    }
}

if ($IncludeWatchdog) {
    Stop-RecordedWatchdogProcess `
        -RecordedPid $recordedWatchdogId -RecordedChildPid $recordedStackId `
        -Snapshot $processSnapshot

    # Also catch an additional visible-command-line watchdog, but do it before
    # stopping the stack so no old owner can respawn a COM writer mid-stop.
    [void](Stop-MatchingProcess -Reason "watchdog-script" -RequireExit -Predicate {
        param($p)
        Test-OwnedStreamParent $p $side 'StartSideScreenWatchdog.ps1'
    })

    # An explicit stop also owns the same task's hidden launcher, including
    # its crash cooldown. Otherwise it would undo this stop after 30 seconds.
    Stop-TurzxWatchdogLaunchers -Snapshot $processSnapshot `
        -LauncherPath (Join-Path $side 'StartSideScreenWatchdog-Hidden.vbs')
}

$ownedStreams = @(if ($SkipStackEntrypoint) { $processSnapshot | Where-Object { Test-OwnedStreamImage $_ } })
[void](Stop-MatchingProcess -Reason "stack-script" -RequireExit -Predicate {
    param($p)
    if (-not (Test-OwnedStreamParent $p $side)) { return $false }
    if (-not $SkipStackEntrypoint) { return $true }
    $stackWorker = $p.CommandLine -match '^(?:"[^"]*"|[^"])*?(?<!\S)-Worker(?=\s|$)'
    if ($stackWorker -and $p.ProcessId -eq $recordedStackId) { return $true }
    if (-not $stackWorker -and -not (Test-OwnedStreamParent $p $side 'StartVideoStream.ps1')) { return $false }
    return @($ownedStreams | Where-Object {
        $_.ParentProcessId -eq $p.ProcessId -and $_.CreationDate -and $p.CreationDate -and
            $_.CreationDate -ge $p.CreationDate
    }).Count -gt 0
})

$stoppedMetricsProcessIds = @(Stop-MatchingProcess -Reason "metrics-agent" -Predicate {
    param($p)
    -not $KeepMetrics -and (Test-TurzxPythonScriptProcess -Process $p -ScriptPath (Join-Path $side 'metrics_agent.py'))
})

[void](Stop-MatchingProcess -Reason "top-processes-helper" -Predicate {
    param($p)
    Test-TurzxPythonScriptProcess -Process $p -ScriptPath (Join-Path $side 'top_processes_helper.py')
})

[void](Stop-MatchingProcess -Reason "weather-shim" -Predicate {
    param($p)
    Test-TurzxPythonScriptProcess -Process $p -ScriptPath (Join-Path $weather 'turzx_weather_shim.py')
})

[void](Stop-MatchingProcess -Reason "stream-exe" -Predicate {
    param($p)
    Test-OwnedStreamImage $p
})

# Hard fallback stays exact-PID and exact project image. A machine-wide /IM or
# tree kill can affect another task; it is not a valid COM-ownership proof.
$freshStreams = @()
if (@(Get-Process 'TURZX.SideScreen.Stream*' -ErrorAction SilentlyContinue).Count -gt 0) {
    Write-StopPhase 'fresh-stream-cim-start'
    $freshStreams = @(Get-CimInstance Win32_Process -Filter "Name LIKE 'TURZX.SideScreen.Stream%.exe'" `
            -Property Name,ProcessId,ExecutablePath,CreationDate -OperationTimeoutSec $ProcessSnapshotTimeoutSeconds -ErrorAction Stop)
    Write-StopPhase 'fresh-stream-cim-done'
}
else { Write-StopPhase 'fresh-stream-cim-skipped-no-stream' }
foreach ($stream in $freshStreams) {
    if (-not (Test-OwnedStreamImage $stream)) { continue }
    $streamProcess = $null
    try {
        $streamProcess = Get-SnapshotProcess $stream
        if (-not $streamProcess) { continue }
        Write-StopPhase 'taskkill-start'
        $fallbackOutput = & taskkill.exe /PID ([int]$stream.ProcessId) /F 2>&1
        Write-StopPhase 'taskkill-done'
        foreach ($line in $fallbackOutput) { Write-StopLog ("taskkill exact stream: {0}" -f $line) }
    }
    catch { Write-StopLog ("exact stream stop failed PID={0}: {1}" -f $stream.ProcessId,$_.Exception.Message) }
    finally { if ($streamProcess) { $streamProcess.Dispose() } }
}

# A new COM writer must never start until the previous stream process has
# actually released the device.  taskkill success text alone is not proof.
Write-StopPhase 'stream-exit-wait-start'
Wait-TurzxStreamProcessesExit
Write-StopPhase 'stream-exit-wait-done'

# Metrics owns no serial handle. A stuck collector must not block a new stream;
# its PID/port evidence remains available while the watchdog retries it separately.
try {
    if (-not $KeepMetrics) {
        Write-StopPhase 'metrics-exit-proof-start'
        Assert-ManagedMetricsAgentExitAndPortRelease `
            -ProcessIds $stoppedMetricsProcessIds `
            -TimeoutSeconds $ProcessSnapshotTimeoutSeconds
        Write-StopPhase 'metrics-exit-proof-done'
    }
}
catch { Write-StopLog ("metrics cleanup deferred; stream exit proved: {0}" -f $_.Exception.Message) }

foreach ($pidFile in @("video-stream.pid", "side-screen-stack-child.pid", "side-screen-stack.pid")) {
    $path = Join-Path $outDir $pidFile
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

Write-StopLog "stop complete"
$stopMutex.ReleaseMutex()
$stopMutex.Dispose()
