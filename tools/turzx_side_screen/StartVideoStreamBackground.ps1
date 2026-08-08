param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Port = "COM7",
    [int]$IntervalMs = 3000,
    [ValidateRange(3000, 60000)][int]$SendTimeoutMs = 10000,
    [ValidateRange(100, 5000)][int]$DiffSendTimeoutMs = 900,
    [ValidateRange(1, 10)][int]$MaxConsecutiveSendFailures = 1,
    [int]$FullResyncEveryFrames = 0,
    [switch]$HybridRefresh,
    [switch]$NoDiff,
    [switch]$ExperimentalDiff,
    [switch]$AltHelper
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $scriptDir "out"
$logPath = Join-Path $outDir "video-stream.log"
$errPath = Join-Path $outDir "video-stream.err.log"
$pidPath = Join-Path $outDir "video-stream.pid"
$script = Join-Path $scriptDir "StartVideoStream.ps1"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if (Test-Path -LiteralPath $pidPath) {
    $oldPid = (Get-Content -LiteralPath $pidPath -ErrorAction SilentlyContinue | Select-Object -First 1)
    if ($oldPid -and (Get-Process -Id ([int]$oldPid) -ErrorAction SilentlyContinue)) {
        throw "Video stream already running with PID $oldPid. Stop it first."
    }
}

$arguments = @(
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-File", $script,
    "-Root", $Root,
    "-Port", $Port,
    "-IntervalMs", [string]$IntervalMs,
    "-SendTimeoutMs", [string]$SendTimeoutMs,
    "-DiffSendTimeoutMs", [string]$DiffSendTimeoutMs,
    "-MaxConsecutiveSendFailures", [string]$MaxConsecutiveSendFailures,
    "-FullResyncEveryFrames", [string]$FullResyncEveryFrames,
    "-Frames", "0"
)
if ($HybridRefresh) {
    $arguments += "-HybridRefresh"
}
if ($ExperimentalDiff -and !$NoDiff) {
    $arguments += @("-Diff", "-AllowUnverifiedDifferentialProtocol")
}
if ($AltHelper) {
    $arguments += "-AltHelper"
}

$process = Start-Process -FilePath powershell -ArgumentList $arguments -WindowStyle Hidden -RedirectStandardOutput $logPath -RedirectStandardError $errPath -PassThru
Set-Content -LiteralPath $pidPath -Value $process.Id -Encoding ASCII

Write-Host ("Started TURZX video stream PID {0}" -f $process.Id)
Write-Host ("Log: {0}" -f $logPath)
Write-Host ("Err: {0}" -f $errPath)
Write-Host ("PID: {0}" -f $pidPath)
