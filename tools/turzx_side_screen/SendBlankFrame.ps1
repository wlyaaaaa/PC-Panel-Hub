param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Port = "",
    [string]$DevCode = "VID_0525&PID_A4A7",
    [int]$TimeoutMs = 15000,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot 'PanelDevicePolicy.ps1')
$Port = Get-TurzxConfiguredPort -Root $Root -Port $Port

$Root = (Resolve-Path -LiteralPath $Root).Path
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $scriptDir "out"
$blankPng = Join-Path $outDir "blank-screen.png"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 480, 1920
try {
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Black)
    }
    finally {
        $graphics.Dispose()
    }

    $bitmap.Save($blankPng, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $bitmap.Dispose()
}

Write-Host ("Blank frame prepared: {0}" -f $blankPng)
if ($DryRun) {
    exit 0
}

$sender = Join-Path $scriptDir "SendRenderedToDevice.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File $sender -Root $Root -Port $Port -DevCode $DevCode -PngPath $blankPng -TimeoutMs $TimeoutMs
if ($LASTEXITCODE -ne 0) {
    throw "Blank frame send failed with exit code $LASTEXITCODE"
}
