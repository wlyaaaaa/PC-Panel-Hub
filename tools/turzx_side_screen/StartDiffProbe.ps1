param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Port = "",
    [int]$Frames = 6,
    [int]$IntervalMs = 1000,
    [int]$TimeoutMs = 90000,
    [switch]$DryRun,
    [switch]$SwapOrder,
    [switch]$Flag,
    [switch]$AltHelper
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot 'PanelDevicePolicy.ps1')
$Port = Get-TurzxConfiguredPort -Root $Root -Port $Port
if (-not $DryRun) {
    Get-TurzxVerifiedSerialEndpoint -Port $Port | Out-Null
    Assert-TurzxStreamStopped
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $scriptDir "out"
$exePath = Join-Path $outDir "TURZX.SideScreen.DiffProbe.exe"
$previewDir = Join-Path $outDir "diff-probe"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
New-Item -ItemType Directory -Force -Path $previewDir | Out-Null

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

$sources = @(
    (Join-Path $scriptDir "SnapshotModels.cs"),
    (Join-Path $scriptDir "TURZX.SideScreen.Renderer.cs"),
    (Join-Path $scriptDir "TURZX.SideScreen.TurzxHelperSender.cs"),
    (Join-Path $scriptDir "TURZX.SideScreen.DiffProbe.cs")
)

& $cscPath /nologo /codepage:65001 /utf8output /target:exe /out:$exePath /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Runtime.Serialization.dll $sources
if ($LASTEXITCODE -ne 0) {
    throw "csc failed with exit code $LASTEXITCODE"
}

$argsList = @(
    "--root", $Root,
    "--port", $Port,
    "--frames", [string]$Frames,
    "--interval-ms", [string]$IntervalMs,
    "--timeout-ms", [string]$TimeoutMs,
    "--preview-dir", $previewDir
)
if ($DryRun) { $argsList += "--dry-run" }
if ($SwapOrder) { $argsList += "--swap-order" }
if ($Flag) { $argsList += "--flag" }
if ($AltHelper) { $argsList += "--alt-helper" }

& $exePath @argsList
if ($LASTEXITCODE -ne 0) {
    throw "DiffProbe failed with exit code $LASTEXITCODE"
}
