param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Dry run only: renders and converts frames through the local TURZX assembly.
# It never resolves, opens or writes the serial endpoint.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$preview = Join-Path $scriptDir "out\diff-probe\diff-last.png"
if (Test-Path -LiteralPath $preview) {
    Remove-Item -LiteralPath $preview -Force
}

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptDir "StartDiffProbe.ps1") -Root $Root -DryRun -Frames 2 -IntervalMs 10 | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "StartDiffProbe.ps1 dry run failed with exit code $LASTEXITCODE"
}

if (!(Test-Path -LiteralPath $preview)) {
    throw "Missing diff probe preview: $preview"
}

$item = Get-Item -LiteralPath $preview
if ($item.Length -le 0) {
    throw "Diff probe preview is empty: $preview"
}

Write-Host ("OK {0} bytes -> {1}" -f $item.Length, $item.FullName)
