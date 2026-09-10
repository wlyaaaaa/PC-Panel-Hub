param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [switch]$IncludeWatchdog,
    [switch]$SkipStackEntrypoint,
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
        [string]$Reason,
        [switch]$FailOnStopError
    )

    $stoppedProcessIds = New-Object 'System.Collections.Generic.List[int]'
    $processSnapshot |
        Where-Object {
            $_.ProcessId -ne $PID -and (& $Predicate $_)
        } |
        ForEach-Object {
            $candidate = $_
            Write-StopLog ("stopping PID={0} reason={1} CMD={2}" -f $candidate.ProcessId, $Reason, $candidate.CommandLine)
            try {
                Stop-Process -Id $candidate.ProcessId -Force -ErrorAction Stop
                [void]$stoppedProcessIds.Add([int]$candidate.ProcessId)
            }
            catch {
                Write-StopLog ("failed to stop PID={0} reason={1}: {2}" -f $candidate.ProcessId, $Reason, $_.Exception.Message)
                if ($FailOnStopError -and
                    $null -ne (Get-Process -Id $candidate.ProcessId -ErrorAction SilentlyContinue)) {
                    throw
                }
            }
        }
    return $stoppedProcessIds.ToArray()
}

function Test-MetricsPortAvailable {
    $listener = $null
    try {
        $listener = [System.Net.Sockets.TcpListener]::new(
            [System.Net.IPAddress]::Parse('127.0.0.1'),
            18765)
        $listener.Server.ExclusiveAddressUse = $true
        $listener.Start()
        return $true
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $listener) {
            $listener.Stop()
        }
    }
}

function Wait-ManagedMetricsAgentExitAndPortRelease {
    param(
        [int[]]$ProcessIds,
        [ValidateRange(1, 30)][int]$TimeoutSeconds = 8
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $remaining = @()
    do {
        $remaining = @(
            foreach ($processId in $ProcessIds) {
                if ($null -ne (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
                    $processId
                }
            }
        )
        if ($remaining.Count -eq 0 -and (Test-MetricsPortAvailable)) {
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    $listenerOwners = 'unavailable'
    try {
        $ownerIds = @(
            Get-NetTCPConnection -State Listen -LocalPort 18765 -ErrorAction Stop |
                Select-Object -ExpandProperty OwningProcess -Unique |
                Sort-Object
        )
        $listenerOwners = if ($ownerIds.Count -eq 0) { 'none' } else { $ownerIds -join ',' }
    }
    catch {
        $listenerOwners = 'probe-failed: ' + $_.Exception.Message
    }
    $remainingText = if ($remaining.Count -eq 0) { 'none' } else { $remaining -join ',' }
    Write-StopLog ("metrics stop proof failed remainingPids={0} port18765Owners={1}" -f $remainingText, $listenerOwners)
    throw "Managed metrics agent did not exit or port 18765 did not release; remainingPids=$remainingText port18765Owners=$listenerOwners"
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

$stoppedMetricsProcessIds = @(Stop-MatchingProcess -Reason "metrics-agent" -FailOnStopError -Predicate {
    param($p)
    $p.Name -like "python*" -and $p.CommandLine -like "*turzx_side_screen\metrics_agent.py*" -and $p.CommandLine -like $sidePattern
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

Wait-ManagedMetricsAgentExitAndPortRelease `
    -ProcessIds $stoppedMetricsProcessIds `
    -TimeoutSeconds $ProcessSnapshotTimeoutSeconds

$streamParents = @($processSnapshot |
    Where-Object { $_.Name -like "TURZX.SideScreen.Stream*" } |
    Select-Object -ExpandProperty ParentProcessId -Unique |
    Where-Object { $_ -and $_ -ne $PID })
foreach ($parentPid in $streamParents) {
    try {
        Write-StopLog ("stopping stream parent PID={0}" -f $parentPid)
        Stop-Process -Id $parentPid -Force -ErrorAction SilentlyContinue
        $parentKillOutput = & taskkill.exe /PID $parentPid /F /T 2>&1
        foreach ($line in $parentKillOutput) {
            Write-StopLog ("taskkill stream parent: {0}" -f $line)
        }
    }
    catch {
        Write-StopLog ("stream parent kill failed PID={0}: {1}" -f $parentPid, $_.Exception.Message)
    }
}

try {
    $taskkillOutput = & taskkill.exe /IM "TURZX.SideScreen.Stream.exe" /F /T 2>&1
    foreach ($line in $taskkillOutput) {
        Write-StopLog ("taskkill stream: {0}" -f $line)
    }
}
catch {
    Write-StopLog ("taskkill stream failed: {0}" -f $_.Exception.Message)
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

foreach ($pidFile in @("video-stream.pid", "side-screen-stack-child.pid", "side-screen-stack.pid")) {
    $path = Join-Path $outDir $pidFile
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

Write-StopLog "stop complete"
