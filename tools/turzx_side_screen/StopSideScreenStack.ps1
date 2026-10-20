param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [switch]$IncludeWatchdog,
    [switch]$SkipStackEntrypoint,
    [switch]$KeepMetrics,
    [switch]$Quiet,
    [ValidateRange(1, 30)][int]$ProcessSnapshotTimeoutSeconds = 8
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath $Root).Path
$side = Join-Path $Root "tools\turzx_side_screen"
$weather = Join-Path $Root "tools\turzx_weather_shim"
$outDir = Join-Path $side "out"
$logPath = Join-Path $outDir "side-screen-stop.log"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Write-StopLog {
    param([string]$Message)
    $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    if (-not $Quiet) {
        Write-Host $line
    }
}

function Get-TurzxProcessSnapshot {
    param([ValidateRange(1, 30)][int]$TimeoutSeconds)

    try {
        return @(Get-CimInstance -ClassName Win32_Process -OperationTimeoutSec $TimeoutSeconds -ErrorAction Stop)
    }
    catch {
        # A local CIM provider can stall while a device/driver is unhealthy.
        # The caller still runs the name-bound stream stop and proof path, but
        # skips command-line based helper cleanup instead of blocking forever.
        Write-StopLog ("process snapshot unavailable timeoutSeconds={0}: {1}" -f `
                $TimeoutSeconds, $_.Exception.Message)
        return @()
    }
}

function Stop-MatchingProcess {
    param(
        [scriptblock]$Predicate,
        [string]$Reason
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
            try {
                Stop-Process -Id $candidate.ProcessId -Force -ErrorAction Stop
            }
            catch {
                Write-StopLog ("failed to stop PID={0} reason={1}: {2}" -f $candidate.ProcessId, $Reason, $_.Exception.Message)
            }
        }
    return $stoppedProcessIds.ToArray()
}

. (Join-Path $PSScriptRoot 'MetricsEndpointPolicy.ps1')

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
        [Parameter(Mandatory = $true)][object[]]$Snapshot,
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
        [Parameter(Mandatory = $true)][string]$PidPath,
        [Parameter(Mandatory = $true)][string]$StackChildPidPath,
        [Parameter(Mandatory = $true)][object[]]$Snapshot
    )

    if (-not (Test-Path -LiteralPath $PidPath)) {
        return
    }

    $rawPid = (Get-Content -Raw -LiteralPath $PidPath -ErrorAction Stop).Trim()
    $recordedPid = 0
    if (-not [int]::TryParse($rawPid, [ref]$recordedPid) -or $recordedPid -le 0) {
        throw "Invalid recorded watchdog PID: $rawPid"
    }

    $candidate = @($Snapshot | Where-Object { [int]$_.ProcessId -eq $recordedPid })
    if ($candidate.Count -eq 0) {
        Write-StopLog ("recorded watchdog already exited PID={0}" -f $recordedPid)
        return
    }
    if ($candidate.Count -ne 1) {
        throw "Recorded watchdog PID is ambiguous: $recordedPid"
    }

    $processName = [string]$candidate[0].Name
    $commandLine = [string]$candidate[0].CommandLine
    $commandIdentityMatches =
        ($processName -like "powershell*" -or $processName -like "pwsh*") -and
        $commandLine -like "*StartSideScreenWatchdog.ps1*" -and
        $commandLine -like $sidePattern

    $treeIdentityMatches = $false
    if (Test-Path -LiteralPath $StackChildPidPath) {
        $rawChildPid = (Get-Content -Raw -LiteralPath $StackChildPidPath -ErrorAction SilentlyContinue).Trim()
        $recordedChildPid = 0
        if ([int]::TryParse($rawChildPid, [ref]$recordedChildPid) -and $recordedChildPid -gt 0) {
            $recordedChild = @(
                $Snapshot | Where-Object {
                    [int]$_.ProcessId -eq $recordedChildPid -and
                    [int]$_.ParentProcessId -eq $recordedPid -and
                    ($_.Name -like "powershell*" -or $_.Name -like "pwsh*")
                }
            )
            $treeIdentityMatches = $recordedChild.Count -eq 1
        }
    }

    if (-not ($commandIdentityMatches -or $treeIdentityMatches)) {
        Write-StopLog ("recorded watchdog identity not verified PID={0} name={1}; refusing to stop" -f $recordedPid, $processName)
        throw "Recorded watchdog identity not verified: $recordedPid"
    }

    Write-StopLog ("stopping recorded watchdog PID={0} identity={1}" -f `
            $recordedPid,
            $(if ($commandIdentityMatches) { "command-line" } else { "process-tree" }))
    Stop-Process -Id $recordedPid -Force -ErrorAction Stop

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if ($null -eq (Get-Process -Id $recordedPid -ErrorAction SilentlyContinue)) {
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Recorded watchdog did not exit before timeout: $recordedPid"
}

$sidePattern = "*" + $side + "*"
$weatherPattern = "*" + $weather + "*"
$processSnapshot = @(Get-TurzxProcessSnapshot -TimeoutSeconds $ProcessSnapshotTimeoutSeconds)

if ($IncludeWatchdog) {
    Stop-RecordedWatchdogProcess `
        -PidPath (Join-Path $outDir "side-screen-watchdog.pid") `
        -StackChildPidPath (Join-Path $outDir "side-screen-stack-child.pid") `
        -Snapshot $processSnapshot

    # Also catch an additional visible-command-line watchdog, but do it before
    # stopping the stack so no old owner can respawn a COM writer mid-stop.
    [void](Stop-MatchingProcess -Reason "watchdog-script" -Predicate {
        param($p)
        ($p.Name -like "powershell*" -or $p.Name -like "pwsh*") -and
            $p.CommandLine -like "*-File*StartSideScreenWatchdog.ps1*" -and
            $p.CommandLine -like "*StartSideScreenWatchdog.ps1*" -and
            $p.CommandLine -like $sidePattern
    })

    # An explicit stop also owns the same task's hidden launcher, including
    # its crash cooldown. Otherwise it would undo this stop after 30 seconds.
    Stop-TurzxWatchdogLaunchers -Snapshot $processSnapshot `
        -LauncherPath (Join-Path $side 'StartSideScreenWatchdog-Hidden.vbs')
}

$stoppedMetricsProcessIds = @(Stop-MatchingProcess -Reason "metrics-agent" -Predicate {
    param($p)
    -not $KeepMetrics -and $p.Name -like "python*" -and $p.CommandLine -like "*turzx_side_screen\metrics_agent.py*" -and $p.CommandLine -like $sidePattern
})

[void](Stop-MatchingProcess -Reason "top-processes-helper" -Predicate {
    param($p)
    $p.Name -like "python*" -and $p.CommandLine -like "*turzx_side_screen\top_processes_helper.py*" -and $p.CommandLine -like $sidePattern
})

[void](Stop-MatchingProcess -Reason "weather-shim" -Predicate {
    param($p)
    $p.Name -like "python*" -and $p.CommandLine -like "*turzx_weather_shim\turzx_weather_shim.py*" -and $p.CommandLine -like $weatherPattern
})

[void](Stop-MatchingProcess -Reason "stream-exe" -Predicate {
    param($p)
    $p.Name -like "TURZX.SideScreen.Stream*" -and $p.CommandLine -like $sidePattern
})

# A parent PID from an old CIM snapshot is not permission to kill a process
# tree. Revalidate the exact project -File entrypoint and its creation time.
function Test-OwnedStreamParent {
    param($Candidate, [string]$SideRoot)
    if ($null -eq $Candidate -or [string]$Candidate.Name -notin @('powershell.exe','pwsh.exe')) { return $false }
    $fileArgument = [regex]::Match([string]$Candidate.CommandLine,
        '(?i)(?:^|\s)-File\s+(?:"([^"]+)"|(\S+))')
    if (-not $fileArgument.Success) { return $false }
    $path = if ($fileArgument.Groups[1].Success) { $fileArgument.Groups[1].Value } else { $fileArgument.Groups[2].Value }
    foreach ($name in @('StartVideoStream.ps1','StartSideScreenStack.ps1')) {
        if ([string]::Equals($path, (Join-Path $SideRoot $name), [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}
$streamParents = @($processSnapshot |
    Where-Object { $_.Name -like 'TURZX.SideScreen.Stream*' -and $_.CommandLine -like $sidePattern } |
    Select-Object -ExpandProperty ParentProcessId -Unique |
    Where-Object { $_ -and $_ -ne $PID })
foreach ($parentPid in $streamParents) {
    $prior = @($processSnapshot | Where-Object { [int]$_.ProcessId -eq [int]$parentPid })
    if ($prior.Count -ne 1 -or -not (Test-OwnedStreamParent -Candidate $prior[0] -SideRoot $side)) {
        Write-StopLog ("not stopping unverified stream parent PID={0}" -f $parentPid)
        continue
    }
    try {
        $fresh = Get-CimInstance Win32_Process -Filter ("ProcessId={0}" -f [int]$parentPid) -ErrorAction Stop
        if (-not (Test-OwnedStreamParent -Candidate $fresh -SideRoot $side) -or
            $fresh.CreationDate -ne $prior[0].CreationDate -or
            -not (Test-MetricsProcessActive -ProcessId ([int]$parentPid))) { continue }
        Write-StopLog ("stopping verified project stream parent PID={0}" -f $parentPid)
        Stop-Process -Id $parentPid -Force -ErrorAction Stop
        # Never /T: unrelated descendants may have been reparented or reused.
    }
    catch { Write-StopLog ("verified parent stop failed PID={0}: {1}" -f $parentPid,$_.Exception.Message) }
}

# Hard fallback stays exact-PID and exact project image. A machine-wide /IM or
# tree kill can affect another task; it is not a valid COM-ownership proof.
$freshStreams = @(Get-CimInstance Win32_Process -Filter "Name='TURZX.SideScreen.Stream.exe'" -ErrorAction Stop)
foreach ($stream in $freshStreams) {
    if ([string]$stream.CommandLine -notlike $sidePattern -or
        [string]$stream.ExecutablePath -notlike ($side + '\*')) { continue }
    try {
        if (-not (Test-MetricsProcessActive -ProcessId ([int]$stream.ProcessId))) { continue }
        $fallbackOutput = & taskkill.exe /PID ([int]$stream.ProcessId) /F 2>&1
        foreach ($line in $fallbackOutput) { Write-StopLog ("taskkill exact stream: {0}" -f $line) }
    }
    catch { Write-StopLog ("exact stream stop failed PID={0}: {1}" -f $stream.ProcessId,$_.Exception.Message) }
}

if (-not $SkipStackEntrypoint) {
    [void](Stop-MatchingProcess -Reason "stack-script" -Predicate {
        param($p)
        ($p.Name -like "powershell*" -or $p.Name -like "pwsh*") -and
            $p.CommandLine -like "*-File*StartSideScreenStack.ps1*" -and
            $p.CommandLine -like "*StartSideScreenStack.ps1*" -and
            $p.CommandLine -like $sidePattern
    })
}

# A new COM writer must never start until the previous stream process has
# actually released the device.  taskkill success text alone is not proof.
Wait-TurzxStreamProcessesExit

# Metrics owns no serial handle. A stuck collector must not block a new stream;
# its PID/port evidence remains available while the watchdog retries it separately.
try {
    if (-not $KeepMetrics) {
        Assert-ManagedMetricsAgentExitAndPortRelease `
            -ProcessIds $stoppedMetricsProcessIds `
            -TimeoutSeconds $ProcessSnapshotTimeoutSeconds
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
