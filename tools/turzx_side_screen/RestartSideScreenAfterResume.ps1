param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$TaskName = "TURZX SideScreen",
    [string]$Port = "COM7",
    [int]$IntervalMs = 3000,
    [int]$DelaySeconds = 10,
    [string]$DeviceIdPattern = "VID_0525&PID_A4A7",
    [int]$DeviceRestartSettleSeconds = 6,
    [switch]$HybridRefresh,
    [switch]$AltHelper,
    [switch]$SkipDeviceRestart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-ScheduledTaskActionMode {
    param(
        [AllowEmptyString()][string]$Arguments,
        [bool]$HybridRefreshEnabled,
        [bool]$AltHelperEnabled
    )

    $hasHybridRefresh = [regex]::IsMatch($Arguments, '(?i)(?:^|\s)-HybridRefresh(?:\s|$)')
    $hasAltHelper = [regex]::IsMatch($Arguments, '(?i)(?:^|\s)-AltHelper(?:\s|$)')
    return ($hasHybridRefresh -eq $HybridRefreshEnabled) -and
        ($hasAltHelper -eq $AltHelperEnabled)
}

$Root = (Resolve-Path -LiteralPath $Root).Path
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $scriptDir "out"
$logPath = Join-Path $outDir "side-screen-resume.log"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Write-ResumeLog {
    param([string]$Message)
    $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    Write-Host $line
}

Write-ResumeLog ("legacy resume compatibility probe root={0} task={1} port={2} interval={3} delay={4}; main watchdog owns resume" -f $Root, $TaskName, $Port, $IntervalMs, $DelaySeconds)
if ($DelaySeconds -gt 0) {
    Start-Sleep -Seconds $DelaySeconds
}

$scheduledTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -eq $scheduledTask) {
    Write-ResumeLog "main watchdog task is missing; compatibility probe remains fail-closed"
    exit 0
}

$registeredArguments = (($scheduledTask.Actions | ForEach-Object { [string]$_.Arguments }) -join ' ')
$modeMatches = Test-ScheduledTaskActionMode `
    -Arguments $registeredArguments `
    -HybridRefreshEnabled ([bool]$HybridRefresh) `
    -AltHelperEnabled ([bool]$AltHelper)
if (-not $modeMatches) {
    Write-ResumeLog ("main task mode mismatch; compatibility probe will not replace the registered owner requestedHybrid={0} requestedAlt={1}" -f [bool]$HybridRefresh, [bool]$AltHelper)
    exit 0
}

if ([string]$scheduledTask.State -eq "Running") {
    Write-ResumeLog "main watchdog already running; no duplicate recovery action required"
    exit 0
}

& schtasks.exe /Run /TN $TaskName | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-ResumeLog ("main watchdog task start failed exit={0}; no fallback owner launched" -f $LASTEXITCODE)
    exit 1
}
Write-ResumeLog ("main watchdog task requested: {0}" -f $TaskName)
