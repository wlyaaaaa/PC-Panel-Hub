param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Port = "",
    [string]$DevCode = "VID_0525&PID_A4A7",
    [string]$PngPath = "",
    [switch]$Sample,
    [int]$TimeoutMs = 240000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot 'PanelDevicePolicy.ps1')
$Port = Get-TurzxConfiguredPort -Root $Root -Port $Port
Get-TurzxVerifiedSerialEndpoint -Port $Port | Out-Null
Assert-TurzxStreamStopped

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $PSScriptRoot 'MetricsEndpointPolicy.ps1')
. (Join-Path $PSScriptRoot 'HiddenProcessLauncher.ps1')
$metricsEndpoint=Get-TurzxMetricsEndpoint
$outDir = Join-Path $scriptDir "out"
$sideExe = Join-Path $outDir "TURZX.SideScreen.exe"
$sendPng = if ([string]::IsNullOrWhiteSpace($PngPath)) { Join-Path $outDir "side-screen-send.png" } else { $PngPath }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if (!(Test-Path -LiteralPath $sideExe)) {
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptDir "TestSideScreenApp.ps1") | Out-Host
}

if ([string]::IsNullOrWhiteSpace($PngPath)) {
    if ($Sample) {
        & $sideExe --sample --output $sendPng
        if ($LASTEXITCODE -ne 0) {
            throw "Render sample failed with exit code $LASTEXITCODE"
        }
    } else {
        $agent = Join-Path $scriptDir "metrics_agent.py"
        $process = $null
        try {
            $process = Start-HiddenProcess -FilePath python -ArgumentList @($agent, "--host", $metricsEndpoint.HostName, "--port", [string]$metricsEndpoint.Port)
            Start-Sleep -Milliseconds 900
            & $sideExe --metrics-url $metricsEndpoint.Url --timeout-ms 5000 --output $sendPng
            if ($LASTEXITCODE -ne 0) {
                throw "Render HTTP snapshot failed with exit code $LASTEXITCODE"
            }
        }
        finally {
            if ($process -and !$process.HasExited) {
                Stop-Process -Id $process.Id -Force
            }
        }
    }
}

if (!(Test-Path -LiteralPath $sendPng)) {
    throw "PNG not found: $sendPng"
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

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("turzx_helper_sender_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
$programPath = Join-Path $tempRoot "SendPngProgram.cs"
$exePath = Join-Path $tempRoot "SendPngProgram.exe"

$program = @'
using System;
using TURZX.SideScreen;

internal static class SendPngProgram
{
    private static int Main(string[] args)
    {
        string message;
        bool ok = TurzxHelperSender.SendPng(args[0], args[1], args[2], args[3], int.Parse(args[4]), out message);
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }
}
'@

try {
    Set-Content -LiteralPath $programPath -Value $program -Encoding UTF8
    $senderSource = Join-Path $scriptDir "TURZX.SideScreen.TurzxHelperSender.cs"
    & $cscPath /nologo /codepage:65001 /utf8output /target:exe /out:$exePath /r:System.dll /r:System.Core.dll /r:System.Drawing.dll $senderSource $programPath
    if ($LASTEXITCODE -ne 0) {
        throw "csc failed with exit code $LASTEXITCODE"
    }

    Write-Host ("Sending {0} to {1} using TURZX helper..." -f $sendPng, $Port)
    & $exePath $Root $Port $sendPng $DevCode $TimeoutMs
    if ($LASTEXITCODE -ne 0) {
        throw "TURZX helper send failed with exit code $LASTEXITCODE"
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
