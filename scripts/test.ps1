param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [switch]$SkipStreamWhenRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$side = Join-Path $Root "tools\turzx_side_screen"

function Get-LiveStreamEvidence {
    $heartbeatPaths = @(
        (Join-Path $side "out\stream\stream-heartbeat.json"),
        (Join-Path $side "out\stream\stream-heartbeat-a.json"),
        (Join-Path $side "out\stream\stream-heartbeat-b.json")
    )
    $heartbeatItems = @(
        $heartbeatPaths |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            ForEach-Object { Get-Item -LiteralPath $_ -ErrorAction SilentlyContinue } |
            Where-Object { $null -ne $_ } |
            Sort-Object LastWriteTimeUtc -Descending
    )
    foreach ($heartbeatItem in $heartbeatItems) {
        try {
            $heartbeatAgeSeconds = ([DateTime]::UtcNow - $heartbeatItem.LastWriteTimeUtc).TotalSeconds
            $heartbeat = Get-Content -Raw -LiteralPath $heartbeatItem.FullName | ConvertFrom-Json
            if ($heartbeatAgeSeconds -le 15 -and
                [int64]$heartbeat.frame -gt 0 -and
                [string]$heartbeat.status -ne "fatal") {
                return [pscustomobject]@{
                    Source = "fresh-heartbeat"
                    Detail = ("frame={0} ageSeconds={1:N1} file={2}" -f [int64]$heartbeat.frame, $heartbeatAgeSeconds, $heartbeatItem.Name)
                }
            }
        }
        catch {
            # Try the other heartbeat slot before falling back to task/process evidence.
        }
    }

    $streamProcess = Get-Process "TURZX.SideScreen.Stream*" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $scheduledTask = Get-ScheduledTask -TaskName "TURZX SideScreen" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($streamProcess -and $scheduledTask -and [string]$scheduledTask.State -eq "Running") {
        return [pscustomobject]@{
            Source = "scheduled-task+process"
            Detail = ("task=Running pid={0}" -f $streamProcess.Id)
        }
    }
    if ($streamProcess) {
        return [pscustomobject]@{
            Source = "process-name"
            Detail = ("pid={0}" -f $streamProcess.Id)
        }
    }
    return $null
}

$runningStream = Get-LiveStreamEvidence

python (Join-Path $side "test_metrics_agent.py")
if ($LASTEXITCODE -ne 0) { throw "test_metrics_agent.py failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $side "TestRenderer.ps1")
if ($LASTEXITCODE -ne 0) { throw "TestRenderer.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $side "TestHttpPipeline.ps1")
if ($LASTEXITCODE -ne 0) { throw "TestHttpPipeline.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $side "TestPowerWatchdog.ps1") -Root $Root
if ($LASTEXITCODE -ne 0) { throw "TestPowerWatchdog.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "scripts\TestShortcutScripts.ps1") -Root $Root
if ($LASTEXITCODE -ne 0) { throw "TestShortcutScripts.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "scripts\TestRefreshDefaults.ps1") -Root $Root
if ($LASTEXITCODE -ne 0) { throw "TestRefreshDefaults.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $side "TestStreamCadence.ps1")
if ($LASTEXITCODE -ne 0) { throw "TestStreamCadence.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "scripts\TestRuntimeReliability.ps1") -Root $Root
if ($LASTEXITCODE -ne 0) { throw "TestRuntimeReliability.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "scripts\test-public-release.ps1")
if ($LASTEXITCODE -ne 0) { throw "test-public-release.ps1 failed" }

if ($runningStream -and $SkipStreamWhenRunning) {
    Write-Host "SKIP TestVideoStream.ps1 because live stream is running: source=$($runningStream.Source) $($runningStream.Detail)"
} else {
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $side "TestVideoStream.ps1")
    if ($LASTEXITCODE -ne 0) { throw "TestVideoStream.ps1 failed" }
}

Write-Host "Core checks completed."
