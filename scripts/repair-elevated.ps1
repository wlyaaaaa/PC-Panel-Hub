param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$TaskName = "TURZX SideScreen",
    [string]$Port = "COM7",
    [int]$IntervalMs = 3000,
    [switch]$NoRestart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "This repair script must run as Administrator."
}

$Root = (Resolve-Path -LiteralPath $Root).Path
$side = Join-Path $Root "tools\turzx_side_screen"
$outDir = Join-Path $side "out"
$logPath = Join-Path $outDir "repair-elevated.log"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Write-RepairLog {
    param([string]$Message)
    $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    Write-Host $line
}

function Invoke-Logged {
    param(
        [string]$Label,
        [scriptblock]$Script
    )

    Write-RepairLog ("BEGIN {0}" -f $Label)
    try {
        & $Script 2>&1 | ForEach-Object { Write-RepairLog ("{0}: {1}" -f $Label, $_) }
    }
    catch {
        Write-RepairLog ("{0} EXCEPTION: {1}" -f $Label, $_.Exception.Message)
    }
    Write-RepairLog ("END {0}" -f $Label)
}

Write-RepairLog ("repair elevated start user={0} root={1}" -f [Security.Principal.WindowsIdentity]::GetCurrent().Name, $Root)

Invoke-Logged "end scheduled task" {
    schtasks.exe /End /TN $TaskName
}

$stopScript = Join-Path $side "StopSideScreenStack.ps1"
if (Test-Path -LiteralPath $stopScript) {
    Invoke-Logged "project stop script" {
        powershell.exe -NoProfile -ExecutionPolicy Bypass -File $stopScript -Root $Root -IncludeWatchdog -Quiet
    }
}

$names = @(
    "TURZX.SideScreen.Stream",
    "TURZX.SideScreen.Stream.*",
    "TURZX.SideScreen",
    "TURZX",
    "TURZX.weatherfix",
    "TURZX.weatherfix.metrics"
)

foreach ($name in $names) {
    Invoke-Logged ("stop-process " + $name) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Output ("Stop-Process PID={0} Name={1}" -f $_.Id, $_.ProcessName)
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

$streamProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "TURZX.SideScreen.Stream*" })
foreach ($proc in $streamProcesses) {
    Invoke-Logged ("taskkill stream pid " + $proc.ProcessId) {
        taskkill.exe /PID $proc.ProcessId /F /T
    }
    Invoke-Logged ("wmi delete stream pid " + $proc.ProcessId) {
        $target = Get-CimInstance Win32_Process -Filter ("ProcessId={0}" -f $proc.ProcessId) -ErrorAction SilentlyContinue
        if ($target) {
            Invoke-CimMethod -InputObject $target -MethodName Terminate | Format-List | Out-String
        }
    }
}

Invoke-Logged "taskkill by image" {
    taskkill.exe /IM TURZX.SideScreen.Stream.exe /F /T
}

Start-Sleep -Seconds 2

$remaining = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "TURZX.SideScreen.Stream*" })
if ($remaining.Count -gt 0) {
    Write-RepairLog ("remaining stream process count={0}" -f $remaining.Count)
    foreach ($proc in $remaining) {
        Write-RepairLog ("remaining PID={0} Parent={1} Name={2} CommandLine={3}" -f $proc.ProcessId, $proc.ParentProcessId, $proc.Name, $proc.CommandLine)
    }
    Write-RepairLog "repair could not terminate every stream process; Windows restart or USB device reset may be required."
    exit 2
}

Write-RepairLog "all stream processes cleared"

if (-not $NoRestart) {
    $startScript = Join-Path $Root "scripts\start.ps1"
    if (Test-Path -LiteralPath $startScript) {
        Invoke-Logged "restart side screen task" {
            powershell.exe -NoProfile -ExecutionPolicy Bypass -File $startScript -Root $Root -TaskName $TaskName -Port $Port -IntervalMs $IntervalMs
        }
    }
}

Write-RepairLog "repair elevated complete"
