param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$TaskName = "TURZX SideScreen",
    [string]$Port = "COM7",
    [int]$IntervalMs = 3000,
    [switch]$Direct
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$watchdog = Join-Path $Root "tools\turzx_side_screen\StartSideScreenWatchdog.ps1"
if (!(Test-Path -LiteralPath $watchdog)) {
    throw "Missing watchdog script: $watchdog"
}

$checker = Join-Path $Root "scripts\check-runtime.ps1"
if (Test-Path -LiteralPath $checker) {
    powershell -NoProfile -ExecutionPolicy Bypass -File $checker -Root $Root
    if ($LASTEXITCODE -ne 0) {
        throw "Runtime check failed. See missing dependency list above."
    }
}

if (-not $Direct) {
    & schtasks.exe /Query /TN $TaskName *> $null
    if ($LASTEXITCODE -eq 0) {
        $outDir = Join-Path $Root "tools\turzx_side_screen\out"
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
        Set-Content -LiteralPath (Join-Path $outDir "restart-on-start.flag") -Value (Get-Date -Format "o") -Encoding ASCII
        & schtasks.exe /End /TN $TaskName *> $null
        Start-Sleep -Milliseconds 600
        & schtasks.exe /Run /TN $TaskName
        if ($LASTEXITCODE -eq 0) {
            Write-Host ("Started scheduled task: {0}" -f $TaskName)
            exit 0
        }
    }
}

powershell -NoProfile -ExecutionPolicy Bypass -File $watchdog -Root $Root -Port $Port -IntervalMs $IntervalMs
