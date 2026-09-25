param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [switch]$SkipStreamWhenRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$side = Join-Path $Root "tools\turzx_side_screen"
$hs2Tests = Join-Path $Root "tools\hs2_crystal_overlay\tests\HS2.CrystalOverlay.Tests\HS2.CrystalOverlay.Tests.csproj"

function Get-LiveStreamEvidence {
    $productionTransportModes = @(
        "verified_full_200",
        "hybrid_diff_204_full_200"
    )
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
                [string]$heartbeat.status -ne "fatal" -and
                [string]$heartbeat.transport_mode -in $productionTransportModes) {
                return [pscustomobject]@{
                    Source = "fresh-heartbeat"
                    Detail = ("frame={0} ageSeconds={1:N1} transport={2} file={3}" -f [int64]$heartbeat.frame, $heartbeatAgeSeconds, [string]$heartbeat.transport_mode, $heartbeatItem.Name)
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

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "scripts\TestMetricsEndpointPolicy.ps1")
if ($LASTEXITCODE -ne 0) { throw "TestMetricsEndpointPolicy.ps1 failed" }
python (Join-Path $side "test_metrics_endpoint_recovery.py")
if ($LASTEXITCODE -ne 0) { throw "test_metrics_endpoint_recovery.py failed" }
$runningStream = Get-LiveStreamEvidence

python (Join-Path $side "test_metrics_agent.py")
if ($LASTEXITCODE -ne 0) { throw "test_metrics_agent.py failed" }

python (Join-Path $Root "tools\turzx_weather_shim\test_weather_shim.py")
if ($LASTEXITCODE -ne 0) { throw "test_weather_shim.py failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $side "TestProtocolEncoding.ps1")
if ($LASTEXITCODE -ne 0) { throw "TestProtocolEncoding.ps1 failed" }

& dotnet test $hs2Tests --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "HS2.CrystalOverlay.Tests failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $side "TestRenderer.ps1")
if ($LASTEXITCODE -ne 0) { throw "TestRenderer.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $side "TestFinalDesign.ps1")
if ($LASTEXITCODE -ne 0) { throw "TestFinalDesign.ps1 failed" }

# Runs TestSideScreenApp.ps1 first (sample render, no device), then the HTTP path.
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $side "TestHttpPipeline.ps1")
if ($LASTEXITCODE -ne 0) { throw "TestHttpPipeline.ps1 failed" }

# The diff probe dry run converts frames through the local vendor assembly,
# which the public checkout does not ship; it never opens the serial port.
$vendorAssembly = @("TURZX.weatherfix.metrics.exe", "TURZX.exe") |
    Where-Object { Test-Path -LiteralPath (Join-Path $Root $_) -PathType Leaf } |
    Select-Object -First 1
if ($vendorAssembly) {
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $side "TestDiffProbe.ps1") -Root $Root
    if ($LASTEXITCODE -ne 0) { throw "TestDiffProbe.ps1 failed" }
} else {
    Write-Host "SKIP TestDiffProbe.ps1 because no local TURZX vendor assembly is present under $Root"
}

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $side "TestPowerWatchdog.ps1") -Root $Root
if ($LASTEXITCODE -ne 0) { throw "TestPowerWatchdog.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "scripts\TestShortcutScripts.ps1") -Root $Root
if ($LASTEXITCODE -ne 0) { throw "TestShortcutScripts.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "scripts\TestRefreshDefaults.ps1") -Root $Root
if ($LASTEXITCODE -ne 0) { throw "TestRefreshDefaults.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "scripts\TestPanelDevicePolicy.ps1") -Root $Root
if ($LASTEXITCODE -ne 0) { throw "TestPanelDevicePolicy.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $side "TestStreamCadence.ps1")
if ($LASTEXITCODE -ne 0) { throw "TestStreamCadence.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "scripts\TestRuntimeReliability.ps1") -Root $Root
if ($LASTEXITCODE -ne 0) { throw "TestRuntimeReliability.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $side "TestHiddenProcessLauncher.ps1")
if ($LASTEXITCODE -ne 0) { throw "TestHiddenProcessLauncher.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "scripts\TestWatchdogLauncherRecovery.ps1") -Root $Root
if ($LASTEXITCODE -ne 0) { throw "TestWatchdogLauncherRecovery.ps1 failed" }

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "scripts\test-public-release.ps1")
if ($LASTEXITCODE -ne 0) { throw "test-public-release.ps1 failed" }

if ($runningStream -and $SkipStreamWhenRunning) {
    Write-Host "SKIP TestVideoStream.ps1 because live stream is running: source=$($runningStream.Source) $($runningStream.Detail)"
} else {
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $side "TestVideoStream.ps1")
    if ($LASTEXITCODE -ne 0) { throw "TestVideoStream.ps1 failed" }
}

Write-Host "Core checks completed."
