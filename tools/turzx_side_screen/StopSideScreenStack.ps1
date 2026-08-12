param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [switch]$IncludeWatchdog,
    [switch]$SkipStackEntrypoint,
    [switch]$Quiet
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

function Stop-MatchingProcess {
    param(
        [scriptblock]$Predicate,
        [string]$Reason
    )

    $processSnapshot |
        Where-Object {
            $_.ProcessId -ne $PID -and (& $Predicate $_)
        } |
        ForEach-Object {
            Write-StopLog ("stopping PID={0} reason={1} CMD={2}" -f $_.ProcessId, $Reason, $_.CommandLine)
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
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
$processSnapshot = @(Get-CimInstance Win32_Process)

if ($IncludeWatchdog) {
    Stop-RecordedWatchdogProcess `
        -PidPath (Join-Path $outDir "side-screen-watchdog.pid") `
        -StackChildPidPath (Join-Path $outDir "side-screen-stack-child.pid") `
        -Snapshot $processSnapshot

    # Also catch an additional visible-command-line watchdog, but do it before
    # stopping the stack so no old owner can respawn a COM writer mid-stop.
    Stop-MatchingProcess -Reason "watchdog-script" -Predicate {
        param($p)
        ($p.Name -like "powershell*" -or $p.Name -like "pwsh*") -and
            $p.CommandLine -like "*-File*StartSideScreenWatchdog.ps1*" -and
            $p.CommandLine -like "*StartSideScreenWatchdog.ps1*" -and
            $p.CommandLine -like $sidePattern
    }
}

Stop-MatchingProcess -Reason "metrics-agent" -Predicate {
    param($p)
    $p.Name -like "python*" -and $p.CommandLine -like "*turzx_side_screen\metrics_agent.py*" -and $p.CommandLine -like $sidePattern
}

Stop-MatchingProcess -Reason "top-processes-helper" -Predicate {
    param($p)
    $p.Name -like "python*" -and $p.CommandLine -like "*turzx_side_screen\top_processes_helper.py*" -and $p.CommandLine -like $sidePattern
}

Stop-MatchingProcess -Reason "weather-shim" -Predicate {
    param($p)
    $p.Name -like "python*" -and $p.CommandLine -like "*turzx_weather_shim\turzx_weather_shim.py*" -and $p.CommandLine -like $weatherPattern
}

Stop-MatchingProcess -Reason "stream-exe" -Predicate {
    param($p)
    $p.Name -like "TURZX.SideScreen.Stream*" -and $p.CommandLine -like $sidePattern
}

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
    Stop-MatchingProcess -Reason "stack-script" -Predicate {
        param($p)
        ($p.Name -like "powershell*" -or $p.Name -like "pwsh*") -and
            $p.CommandLine -like "*-File*StartSideScreenStack.ps1*" -and
            $p.CommandLine -like "*StartSideScreenStack.ps1*" -and
            $p.CommandLine -like $sidePattern
    }
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
