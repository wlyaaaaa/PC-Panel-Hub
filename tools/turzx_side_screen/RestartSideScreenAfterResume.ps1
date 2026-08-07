param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$TaskName = "TURZX SideScreen",
    [string]$Port = "COM7",
    [int]$IntervalMs = 3000,
    [int]$DelaySeconds = 10,
    [string]$DeviceIdPattern = "VID_0525&PID_A4A7",
    [int]$DeviceRestartSettleSeconds = 6,
    [switch]$SkipDeviceRestart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath $Root).Path
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $scriptDir "out"
$logPath = Join-Path $outDir "side-screen-resume.log"
$stopScript = Join-Path $scriptDir "StopSideScreenStack.ps1"
$launcher = Join-Path $scriptDir "StartSideScreenWatchdog-Hidden.vbs"
$resumeFlag = Join-Path $outDir "restart-on-resume.flag"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Write-ResumeLog {
    param([string]$Message)
    $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    Write-Host $line
}

function Format-DeviceLine {
    param($Device)

    if ($null -eq $Device) {
        return "<missing>"
    }

    return "{0} {1} {2}" -f $Device.Status, $Device.Name, $Device.PNPDeviceID
}

function Get-TurzxUsbDevice {
    $devices = @(Get-CimInstance Win32_PnPEntity -ErrorAction Stop |
        Where-Object { $_.PNPDeviceID -like ("*" + $DeviceIdPattern + "*") })

    if ($devices.Count -eq 0) {
        return $null
    }

    return $devices |
        Sort-Object `
            @{ Expression = { if ($_.Name -like ("*(" + $Port + ")*")) { 0 } else { 1 } } },
            Name |
        Select-Object -First 1
}

function Restart-TurzxUsbDevice {
    if ($SkipDeviceRestart) {
        Write-ResumeLog "usb device restart skipped by parameter"
        return
    }

    try {
        $device = Get-TurzxUsbDevice
        if ($null -eq $device) {
            Write-ResumeLog ("usb device not found for pattern={0}; continue without device restart" -f $DeviceIdPattern)
            return
        }

        Write-ResumeLog ("usb device before restart: {0}" -f (Format-DeviceLine $device))
        $restartOutput = & pnputil.exe /restart-device $device.PNPDeviceID 2>&1
        $restartExit = $LASTEXITCODE
        foreach ($line in $restartOutput) {
            Write-ResumeLog ("pnputil restart: {0}" -f $line)
        }

        if ($restartExit -ne 0) {
            Write-ResumeLog ("pnputil restart warning exit={0}; continue startup fallback" -f $restartExit)
            return
        }

        if ($DeviceRestartSettleSeconds -gt 0) {
            Start-Sleep -Seconds $DeviceRestartSettleSeconds
        }

        $after = Get-TurzxUsbDevice
        Write-ResumeLog ("usb device after restart: {0}" -f (Format-DeviceLine $after))
    }
    catch {
        Write-ResumeLog ("usb device restart failed: {0}" -f $_.Exception.Message)
    }
}

Write-ResumeLog ("restart-on-resume requested root={0} task={1} port={2} interval={3} delay={4}" -f $Root, $TaskName, $Port, $IntervalMs, $DelaySeconds)
if ($DelaySeconds -gt 0) {
    Start-Sleep -Seconds $DelaySeconds
}

try {
    powershell -NoProfile -ExecutionPolicy Bypass -File $stopScript -Root $Root -IncludeWatchdog -SkipStackEntrypoint -Quiet | Out-Null
    Write-ResumeLog "stopped stale side-screen stack after resume"
}
catch {
    Write-ResumeLog ("stop stale stack failed: {0}" -f $_.Exception.Message)
}

Restart-TurzxUsbDevice

Set-Content -LiteralPath $resumeFlag -Value (Get-Date -Format "o") -Encoding ASCII

$taskExists = $false
& schtasks.exe /Query /TN $TaskName *> $null
if ($LASTEXITCODE -eq 0) {
    $taskExists = $true
    & schtasks.exe /End /TN $TaskName *> $null
    Start-Sleep -Milliseconds 800
    & schtasks.exe /Run /TN $TaskName | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-ResumeLog ("started scheduled task after resume: {0}" -f $TaskName)
        exit 0
    }
    Write-ResumeLog ("scheduled task start failed exit={0}; using direct hidden launcher" -f $LASTEXITCODE)
}

if (-not (Test-Path -LiteralPath $launcher)) {
    throw "Missing hidden launcher: $launcher"
}

$command = 'wscript.exe "{0}" -Root "{1}" -Port {2} -IntervalMs {3}' -f $launcher, $Root, $Port, $IntervalMs
Start-Process -FilePath "wscript.exe" `
    -ArgumentList @($launcher, "-Root", $Root, "-Port", $Port, "-IntervalMs", [string]$IntervalMs) `
    -WorkingDirectory $scriptDir `
    -WindowStyle Hidden | Out-Null
Write-ResumeLog ("started direct hidden launcher after resume taskExists={0}: {1}" -f $taskExists, $command)
