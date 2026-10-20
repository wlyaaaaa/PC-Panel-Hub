param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Port = "",
    [int]$IntervalMs = 3000,
    [int]$Frames = 0,
    [ValidateRange(3000, 60000)][int]$SendTimeoutMs = 10000,
    [ValidateRange(100, 5000)][int]$DiffSendTimeoutMs = 900,
    [ValidateRange(1, 10)][int]$MaxConsecutiveSendFailures = 1,
    [int]$FullResyncEveryFrames = 900,
    [ValidateRange(0, 255)][int]$BaselineBrightness = 170,
    [int]$PreviewIntervalSeconds = 45,
    [string]$PreviewDir,
    [string]$PythonPath,
    [string]$ExecutablePath,
    [switch]$Sample,
    [switch]$DryRun,
    [switch]$KeepMetrics,
    [switch]$HybridRefresh,
    [switch]$Diff,
    [switch]$AllowUnverifiedDifferentialProtocol
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot 'PanelDevicePolicy.ps1')
$Port = Get-TurzxConfiguredPort -Root $Root -Port $Port
if ($Diff -and -not $DryRun -and -not $AllowUnverifiedDifferentialProtocol) {
    throw "Live differential command 204 is unverified. Production must use the full-frame transport."
}
if ($HybridRefresh -and $Diff) {
    throw "HybridRefresh is a distinct guarded mode; do not combine it with the legacy Diff switch."
}

if (-not $DryRun) {
    Get-TurzxVerifiedSerialEndpoint -Port $Port | Out-Null
    Assert-TurzxStreamStopped
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptDir "HiddenProcessLauncher.ps1")
$outDir = Join-Path $scriptDir "out"
$hasExplicitExecutablePath = -not [string]::IsNullOrWhiteSpace($ExecutablePath)
$exePath = if ($hasExplicitExecutablePath) { $ExecutablePath } else { Join-Path $outDir "TURZX.SideScreen.Stream.exe" }
. (Join-Path $PSScriptRoot 'MetricsEndpointPolicy.ps1')
$metricsEndpoint = Get-TurzxMetricsEndpoint
$metricsHost = $metricsEndpoint.HostName
$metricsPort = $metricsEndpoint.Port
$metricsUrl = $metricsEndpoint.Url
if ([string]::IsNullOrWhiteSpace($PreviewDir)) {
    $PreviewDir = Join-Path $outDir "stream"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
New-Item -ItemType Directory -Force -Path $PreviewDir | Out-Null

if (!$hasExplicitExecutablePath) {
    $staleBuildCutoff = [DateTime]::UtcNow.AddDays(-1)
    Get-ChildItem -LiteralPath $outDir -File -Filter "TURZX.SideScreen.Stream.*.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -lt $staleBuildCutoff } |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
        }
}

function Test-MetricsEndpointReady {
    try {
        $healthUri = [UriBuilder]::new($metricsUrl)
        $healthUri.Path = '/health'
        $healthUri.Query = ''
        $probe = Invoke-WebRequest -UseBasicParsing -Uri $healthUri.Uri -TimeoutSec 2
        $health = $probe.Content | ConvertFrom-Json
        return $probe.StatusCode -eq 200 -and $health.service -eq 'turzx-metrics' -and $health.status -eq 'ok'
    }
    catch {
        return $false
    }
}

$cscCommand = Get-Command csc -ErrorAction SilentlyContinue
$cscPath = $null
if ($null -ne $cscCommand) {
    $cscPath = $cscCommand.Source
}
if ([string]::IsNullOrWhiteSpace($cscPath)) {
    $frameworkCsc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (Test-Path $frameworkCsc) {
        $cscPath = $frameworkCsc
    }
}
if ([string]::IsNullOrWhiteSpace($cscPath)) {
    throw "csc.exe not found."
}

if (!$hasExplicitExecutablePath -and (Get-Process "TURZX.SideScreen.Stream*" -ErrorAction SilentlyContinue)) {
    $exePath = Join-Path $outDir ("TURZX.SideScreen.Stream.{0}.exe" -f $PID)
}

$sources = @(
    (Join-Path $scriptDir "SnapshotModels.cs"),
    (Join-Path $scriptDir "TURZX.SideScreen.Renderer.cs"),
    (Join-Path $scriptDir "TURZX.SideScreen.TurzxHelperSender.cs"),
    (Join-Path $scriptDir "TURZX.SideScreen.Stream.cs")
)

& $cscPath /nologo /codepage:65001 /utf8output /target:exe /out:$exePath /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Net.Http.dll /r:System.Runtime.Serialization.dll $sources
if ($LASTEXITCODE -ne 0) {
    throw "csc failed with exit code $LASTEXITCODE"
}

$agentProcess = $null
if (!$Sample -and !$DryRun) {
    try {
        if (-not (Test-MetricsEndpointReady)) {
            $maintenance = Invoke-TurzxMetricsAgentMaintenance -SideRoot $scriptDir -PythonPath $PythonPath
            $agentProcess = $maintenance.Process
            Write-Host ("metrics startup {0}; stream may use cached data" -f $maintenance.Status)
        }
    }
    catch { Write-Warning ("metrics startup deferred; stream may use cached data: {0}" -f $_.Exception.Message) }
}

try {
    $argsList = @("--metrics-url", $metricsUrl, "--root", $Root, "--port", $Port, "--interval-ms", [string]$IntervalMs, "--frames", [string]$Frames, "--send-timeout-ms", [string]$SendTimeoutMs, "--diff-send-timeout-ms", [string]$DiffSendTimeoutMs, "--baseline-brightness", [string]$BaselineBrightness, "--max-consecutive-send-failures", [string]$MaxConsecutiveSendFailures, "--full-resync-every-frames", [string]$FullResyncEveryFrames, "--preview-dir", $PreviewDir, "--preview-interval-seconds", [string]$PreviewIntervalSeconds)
    if ($Sample) { $argsList += "--sample" }
    if ($DryRun) { $argsList += "--dry-run" }
    if ($HybridRefresh) { $argsList += "--hybrid-refresh" }
    if ($Diff) { $argsList += "--diff" }
    if ($AllowUnverifiedDifferentialProtocol) { $argsList += "--allow-unverified-diff" }
    & $exePath @argsList
    if ($LASTEXITCODE -ne 0) {
        throw "stream failed with exit code $LASTEXITCODE"
    }
}
finally {
    if (!$KeepMetrics -and $agentProcess -and !$agentProcess.HasExited) {
        Stop-Process -Id $agentProcess.Id -Force
    }
}
