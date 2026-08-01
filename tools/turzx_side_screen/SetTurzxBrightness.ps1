param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Port = "COM7",
    [ValidateRange(0, 255)][int]$Brightness = 170,
    [switch]$DryRun,
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath $Root).Path
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $scriptDir "out"
$exePath = Join-Path $outDir "TURZX.SideScreen.Power.exe"
$protocolSource = Join-Path $scriptDir "TURZX.SideScreen.Protocol.cs"
$powerSource = Join-Path $scriptDir "TURZX.SideScreen.Power.cs"
$rjcpDll = Join-Path $Root "RJCP.SerialPortStream.dll"

foreach ($path in @($protocolSource, $powerSource, $rjcpDll)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required TURZX power-control file is missing: $path"
    }
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$needsBuild = -not (Test-Path -LiteralPath $exePath -PathType Leaf)
if (-not $needsBuild) {
    $exeTime = (Get-Item -LiteralPath $exePath).LastWriteTimeUtc
    $needsBuild = [bool](@($protocolSource, $powerSource) |
        Where-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt $exeTime } |
        Select-Object -First 1)
}

if ($needsBuild) {
    if ($NoBuild) {
        throw "TURZX power controller is missing or stale: $exePath"
    }

    $cscCommand = Get-Command csc.exe -ErrorAction SilentlyContinue
    $cscPath = if ($null -ne $cscCommand) { $cscCommand.Source } else { $null }
    if ([string]::IsNullOrWhiteSpace($cscPath)) {
        $cscPath = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    }
    if (-not (Test-Path -LiteralPath $cscPath -PathType Leaf)) {
        throw "csc.exe was not found; cannot build the TURZX power controller."
    }

    & $cscPath /nologo /codepage:65001 /utf8output /target:exe /out:$exePath `
        /r:System.dll /r:System.Core.dll /r:System.Drawing.dll $protocolSource $powerSource
    if ($LASTEXITCODE -ne 0) {
        throw "TURZX power controller build failed with exit code $LASTEXITCODE"
    }
}

$arguments = @(
    "--port", $Port,
    "--brightness", [string]$Brightness,
    "--rjcp-dll", $rjcpDll
)
if ($DryRun) {
    $arguments += "--dry-run"
}

& $exePath $arguments
if ($LASTEXITCODE -ne 0) {
    throw "TURZX brightness command failed with exit code $LASTEXITCODE"
}
