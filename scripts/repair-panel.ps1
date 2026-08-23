param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$TaskName = "TURZX SideScreen",
    [string]$Port = "COM7",
    [ValidateRange(10, 180)][int]$WaitSeconds = 120,
    [switch]$HybridRefresh = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $HybridRefresh) {
    throw "Fast repair is intentionally fixed to the one-second HybridRefresh production mode."
}

$Root = (Resolve-Path -LiteralPath $Root).Path
$startPath = Join-Path $Root "scripts\start.ps1"
$streamOut = Join-Path $Root "tools\turzx_side_screen\out\stream"
$watchdogOut = Join-Path $Root "tools\turzx_side_screen\out"
$watchdogPidPath = Join-Path $watchdogOut "side-screen-watchdog.pid"
$stackChildPidPath = Join-Path $watchdogOut "side-screen-stack-child.pid"
$restartFlag = Join-Path $watchdogOut "restart-on-start.flag"
$expectedTaskAdapter = Join-Path $Root "tools\turzx_side_screen\StartSideScreenWatchdog-Hidden.vbs"
$heartbeatPaths = @(
    (Join-Path $streamOut "stream-heartbeat-a.json"),
    (Join-Path $streamOut "stream-heartbeat-b.json"),
    (Join-Path $streamOut "stream-heartbeat.json")
)

$serial = @(Get-CimInstance Win32_SerialPort -ErrorAction Stop | Where-Object { [string]$_.DeviceID -ieq $Port })
if ($serial.Count -ne 1) {
    throw "Fast repair requires exactly one $Port serial endpoint; found $($serial.Count)."
}
$instanceId = [string]$serial[0].PNPDeviceID
if ($instanceId -notmatch '(?i)^USB\\VID_0525&PID_A4A7\\') {
    throw "Fast repair refused unexpected $Port identity: $instanceId"
}
$device = Get-PnpDevice -InstanceId $instanceId -ErrorAction Stop
if ($device.Present -ne $true -or [string]$device.Status -ne 'OK' -or [int]$device.ConfigManagerErrorCode -ne 0) {
    throw "Fast repair requires healthy exact TURZX COM device: present=$($device.Present) status=$($device.Status) problem=$($device.ConfigManagerErrorCode)"
}

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
$registeredArguments = if ($null -eq $task) {
    ""
}
else {
    (($task.Actions | ForEach-Object { [string]$_.Arguments }) -join " ")
}
$watchdogPid = 0
$watchdogPidValid = (
    (Test-Path -LiteralPath $watchdogPidPath -PathType Leaf) -and
    [int]::TryParse(
        (Get-Content -Raw -LiteralPath $watchdogPidPath).Trim(),
        [ref]$watchdogPid)
)
$watchdogProcess = if ($watchdogPidValid) {
    Get-Process -Id $watchdogPid -ErrorAction SilentlyContinue
}
else {
    $null
}
$liveWatchdogOwner = (
    $null -ne $task -and
    [string]$task.State -eq "Running" -and
    $registeredArguments.IndexOf($expectedTaskAdapter, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
    $registeredArguments.IndexOf($Root, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
    $null -ne $watchdogProcess
)
$priorStackChildPid = 0
$priorStackChildPidValid = (
    (Test-Path -LiteralPath $stackChildPidPath -PathType Leaf) -and
    [int]::TryParse(
        (Get-Content -Raw -LiteralPath $stackChildPidPath).Trim(),
        [ref]$priorStackChildPid)
)
$repairRequestedUtc = [DateTime]::UtcNow

if ($liveWatchdogOwner) {
    New-Item -ItemType Directory -Force -Path $watchdogOut | Out-Null
    $temporaryFlag = "{0}.{1}.tmp" -f $restartFlag, [Guid]::NewGuid().ToString("N")
    try {
        [IO.File]::WriteAllText(
            $temporaryFlag,
            $repairRequestedUtc.ToString("o"),
            [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryFlag -Destination $restartFlag -Force
    }
    finally {
        Remove-Item -LiteralPath $temporaryFlag -Force -ErrorAction SilentlyContinue
    }
    Write-Host ("Requested in-place stack recycle from live watchdog PID={0}; HS2/display ownership preserved." -f $watchdogPid)
}
else {
    & pwsh.exe -NoProfile -ExecutionPolicy Bypass -File $startPath -Root $Root -TaskName $TaskName -Port $Port -HybridRefresh
    if ($LASTEXITCODE -ne 0) {
        throw "scripts\start.ps1 failed with exit code $LASTEXITCODE."
    }
}

$deadline = [datetime]::UtcNow.AddSeconds($WaitSeconds)
do {
    Start-Sleep -Milliseconds 500
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    $streams = @(Get-Process -Name 'TURZX.SideScreen.Stream' -ErrorAction SilentlyContinue)
    $currentStackChildPid = 0
    $currentStackChildPidValid = (
        (Test-Path -LiteralPath $stackChildPidPath -PathType Leaf) -and
        [int]::TryParse(
            (Get-Content -Raw -LiteralPath $stackChildPidPath).Trim(),
            [ref]$currentStackChildPid)
    )
    $currentStackChild = if ($currentStackChildPidValid) {
        Get-Process -Id $currentStackChildPid -ErrorAction SilentlyContinue
    }
    else {
        $null
    }
    $postRepairStackOwner = (
        $null -ne $currentStackChild -and
        (
            -not $priorStackChildPidValid -or
            $currentStackChildPid -ne $priorStackChildPid
        )
    )
    $restartRequestAcknowledged = (-not $liveWatchdogOwner) -or (-not (Test-Path -LiteralPath $restartFlag))
    $heartbeat = $null
    foreach ($path in @($heartbeatPaths | Where-Object { Test-Path -LiteralPath $_ } | Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } -Descending)) {
        try {
            $candidate = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -ErrorAction Stop
            $utc = [datetime]::Parse([string]$candidate.utc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
            if ($utc.ToUniversalTime() -gt $repairRequestedUtc -and
                ([datetime]::UtcNow - $utc.ToUniversalTime()).TotalSeconds -le 5) {
                $heartbeat = $candidate
                break
            }
        }
        catch { }
    }
    if ($null -ne $task -and [string]$task.State -eq 'Running' -and
        $restartRequestAcknowledged -and $postRepairStackOwner -and
        $streams.Count -eq 1 -and $null -ne $heartbeat -and
        [string]$heartbeat.status -eq 'ok' -and [string]$heartbeat.transport_mode -eq 'hybrid_diff_204_full_200' -and
        [bool]$heartbeat.send_attempted -and [int]$heartbeat.frame -ge 2 -and
        [int]$heartbeat.period_ms -ge 500 -and [int]$heartbeat.period_ms -le 2000) {
        Write-Host ("repair-panel healthy task={0} stackOwnerPid={1} streamPid={2} frame={3} period_ms={4} transport={5}" -f `
            $task.State, $currentStackChildPid, $streams[0].Id, $heartbeat.frame, $heartbeat.period_ms, $heartbeat.transport_mode)
        exit 0
    }
} while ([datetime]::UtcNow -lt $deadline)

throw "Fast repair did not reach one-writer fresh 1 Hz Hybrid health within $WaitSeconds seconds."
