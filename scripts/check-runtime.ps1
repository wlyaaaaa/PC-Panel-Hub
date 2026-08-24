param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Find-Csc {
    $command = Get-Command csc -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $frameworkCsc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (Test-Path -LiteralPath $frameworkCsc) {
        return $frameworkCsc
    }
    return $null
}

function Test-PythonModules {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string[]]$Modules = @()
    )

    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    if ($Modules.Count -eq 0) {
        return $true
    }

    & $Path -c "import importlib.util,sys;sys.exit(0 if all(importlib.util.find_spec(name) is not None for name in sys.argv[1:]) else 1)" @Modules 2>$null
    return $LASTEXITCODE -eq 0
}

function Find-Python {
    $requiresTimeAudit = (
        -not [string]::IsNullOrWhiteSpace($env:TIMEAUDIT_DSN) -or
        -not [string]::IsNullOrWhiteSpace($env:TIMEAUDIT_DB_PASSWORD)
    )
    $requiredModules = if ($requiresTimeAudit) { @("psutil", "asyncpg") } else { @() }
    $command = Get-Command python -ErrorAction SilentlyContinue
    $commandPath = if ($command) { $command.Source } else { $null }
    $candidates = if ($requiresTimeAudit) {
        @(
            (Join-Path $env:LOCALAPPDATA "Programs\Python\Python311\python.exe"),
            $commandPath,
            (Join-Path $env:LOCALAPPDATA "Programs\Python\Python314\python.exe"),
            (Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\python.exe")
        )
    } else {
        @(
            $commandPath,
            (Join-Path $env:LOCALAPPDATA "Programs\Python\Python311\python.exe"),
            (Join-Path $env:LOCALAPPDATA "Programs\Python\Python314\python.exe"),
            (Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\python.exe")
        )
    }

    foreach ($candidate in @($candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        if (Test-PythonModules -Path $candidate -Modules $requiredModules) {
            return $candidate
        }
    }
    return $null
}

$Root = (Resolve-Path $Root -ErrorAction SilentlyContinue).Path
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = (Get-Location).Path
}

$missing = New-Object System.Collections.Generic.List[string]
$found = [ordered]@{}

$python = Find-Python
if ([string]::IsNullOrWhiteSpace($python)) {
    if (
        [string]::IsNullOrWhiteSpace($env:TIMEAUDIT_DSN) -and
        [string]::IsNullOrWhiteSpace($env:TIMEAUDIT_DB_PASSWORD)
    ) {
        $missing.Add("python")
    } else {
        $missing.Add("python with psutil+asyncpg")
    }
} else {
    $found.python = $python
    $found.python_timeaudit_ready = Test-PythonModules -Path $python -Modules @("psutil", "asyncpg")
}

$csc = Find-Csc
if ([string]::IsNullOrWhiteSpace($csc)) {
    $missing.Add("csc.exe")
} else {
    $found.csc = $csc
}

$rjcp = Join-Path $Root "RJCP.SerialPortStream.dll"
if (!(Test-Path -LiteralPath $rjcp)) {
    $missing.Add("RJCP.SerialPortStream.dll")
} else {
    $found.rjcp = $rjcp
}

$patched = Join-Path $Root "TURZX.weatherfix.metrics.exe"
$stock = Join-Path $Root "TURZX.exe"
if (!(Test-Path -LiteralPath $patched) -and !(Test-Path -LiteralPath $stock)) {
    $missing.Add("TURZX.exe or TURZX.weatherfix.metrics.exe")
} else {
    $found.turzx = if (Test-Path -LiteralPath $patched) { $patched } else { $stock }
}

$stack = Join-Path $Root "tools\turzx_side_screen\StartSideScreenStack.ps1"
if (!(Test-Path -LiteralPath $stack)) {
    $missing.Add("tools\\turzx_side_screen\\StartSideScreenStack.ps1")
} else {
    $found.stack = $stack
}

$payload = [ordered]@{
    ready = ($missing.Count -eq 0)
    root = $Root
    missing = @($missing)
    found = $found
}

if ($AsJson) {
    $payload | ConvertTo-Json -Depth 5 -Compress
} else {
    if ($payload.ready) {
        Write-Host "Runtime ready: $Root"
        $found.GetEnumerator() | ForEach-Object { Write-Host ("OK {0}: {1}" -f $_.Key, $_.Value) }
    } else {
        Write-Host "Runtime is missing required dependencies under: $Root"
        $missing | ForEach-Object { Write-Host ("MISSING " + $_) }
        Write-Host ""
        Write-Host "Install/copy the stock TURZX runtime files next to this repository root:"
        Write-Host "- RJCP.SerialPortStream.dll"
        Write-Host "- TURZX.exe or TURZX.weatherfix.metrics.exe"
    }
}

exit ($(if ($missing.Count -eq 0) { 0 } else { 1 }))
