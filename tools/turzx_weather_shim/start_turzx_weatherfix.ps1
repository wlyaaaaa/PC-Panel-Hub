$ErrorActionPreference = "Stop"

$shimDir = $PSScriptRoot
$rootDir = (Resolve-Path (Join-Path $shimDir "..\..")).Path
$shimScript = Join-Path $shimDir "turzx_weather_shim.py"
$metricsAppPath = Join-Path $rootDir "TURZX.weatherfix.metrics.exe"
$legacyAppPath = Join-Path $rootDir "TURZX.weatherfix.exe"
$appPath = if (Test-Path -LiteralPath $metricsAppPath) { $metricsAppPath } else { $legacyAppPath }
$appProcessName = [IO.Path]::GetFileNameWithoutExtension($appPath)
$hostAddress = "127.0.0.1"
$port = 18080
$weatherConfigPath = $env:TURZX_WEATHER_CONFIG
if ([string]::IsNullOrWhiteSpace($weatherConfigPath)) {
    $localConfigPath = Join-Path $rootDir "tools\turzx_side_screen\config.json"
    if (Test-Path -LiteralPath $localConfigPath -PathType Leaf) {
        $weatherConfigPath = $localConfigPath
    }
}

function Find-Python {
    $cmd = Get-Command python -ErrorAction SilentlyContinue
    if ($cmd -and (Test-Path -LiteralPath $cmd.Source)) {
        return $cmd.Source
    }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Python\Python311\python.exe"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\python.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw "python.exe not found"
}

$listener = Get-NetTCPConnection -LocalAddress $hostAddress -LocalPort $port -State Listen -ErrorAction SilentlyContinue
if (-not $listener) {
    $python = Find-Python
    $shimArguments = @($shimScript, "--host", $hostAddress, "--port", "$port")
    if (-not [string]::IsNullOrWhiteSpace($weatherConfigPath)) {
        $shimArguments += @("--config", $weatherConfigPath)
    }
    Start-Process -FilePath $python `
        -ArgumentList $shimArguments `
        -WorkingDirectory $shimDir `
        -WindowStyle Hidden

    Start-Sleep -Seconds 2
}

$appProcess = Get-Process $appProcessName -ErrorAction SilentlyContinue
if (-not $appProcess) {
    if ((Split-Path -Leaf $appPath) -eq "TURZX.weatherfix.metrics.exe") {
        Get-Process "TURZX.weatherfix" -ErrorAction SilentlyContinue | Stop-Process -Force
    }
    Start-Process -FilePath $appPath -WorkingDirectory $rootDir
}
