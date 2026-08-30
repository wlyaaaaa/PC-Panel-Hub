Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$checker = Join-Path $root "scripts\check-runtime.ps1"
$builder = Join-Path $root "scripts\build-release.ps1"
if (!(Test-Path -LiteralPath $checker)) {
    throw "Missing runtime checker: $checker"
}
if (!(Test-Path -LiteralPath $builder)) {
    throw "Missing release builder: $builder"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("turzx_public_release_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

try {
    $missingOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $checker -Root $tempRoot -AsJson 2>$null
    if ($LASTEXITCODE -eq 0) {
        throw "Runtime checker should fail when vendor runtime files are missing."
    }

    $missing = $missingOutput | ConvertFrom-Json
    if (-not ($missing.missing -contains "RJCP.SerialPortStream.dll")) {
        throw "Missing output did not mention RJCP.SerialPortStream.dll"
    }
    if (-not ($missing.missing -contains "TURZX.exe or TURZX.weatherfix.metrics.exe")) {
        throw "Missing output did not mention TURZX runtime executable"
    }

    New-Item -ItemType File -Force -Path (Join-Path $tempRoot "RJCP.SerialPortStream.dll") | Out-Null
    New-Item -ItemType File -Force -Path (Join-Path $tempRoot "TURZX.weatherfix.metrics.exe") | Out-Null
    $stackDir = Join-Path $tempRoot "tools\turzx_side_screen"
    New-Item -ItemType Directory -Force -Path $stackDir | Out-Null
    New-Item -ItemType File -Force -Path (Join-Path $stackDir "StartSideScreenStack.ps1") | Out-Null

    $okOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $checker -Root $tempRoot -AsJson
    if ($LASTEXITCODE -ne 0) {
        throw "Runtime checker should pass with required vendor runtime files present."
    }

    $ok = $okOutput | ConvertFrom-Json
    if ($ok.ready -ne $true) {
        throw "Runtime checker JSON did not report ready=true"
    }

    $releaseOutput = Join-Path $tempRoot "release"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $builder `
        -Root $root `
        -Version "public-test" `
        -OutputDir $releaseOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Source release build failed."
    }

    $zipPath = Join-Path $releaseOutput "PC-Panel-Hub-public-test.zip"
    if (!(Test-Path -LiteralPath $zipPath -PathType Leaf)) {
        throw "Release builder did not create the expected ZIP."
    }

    $expanded = Join-Path $tempRoot "expanded"
    Expand-Archive -LiteralPath $zipPath -DestinationPath $expanded -Force
    $entries = @(
        Get-ChildItem -LiteralPath $expanded -Recurse -File | ForEach-Object {
            ($_.FullName.Substring($expanded.Length) -replace '^[\\/]+', '') -replace '\\', '/'
        }
    )

    $requiredEntries = @(
        "scripts/build-release.ps1",
        "tools/turzx_side_screen/metrics_agent.py",
        "tools/turzx_side_screen/config.example.json",
        "tools/turzx_weather_shim/turzx_weather_shim.py",
        "tools/turzx_weather_shim/start_turzx_weatherfix.ps1",
        "tools/hs2_crystal_overlay/HS2.CrystalOverlay.slnx",
        "tools/hs2_crystal_overlay/Publish-HS2Task.ps1",
        "tools/hs2_crystal_overlay/src/HS2.CrystalOverlay/HS2.CrystalOverlay.csproj",
        "tools/hs2_crystal_overlay/src/HS2.CrystalOverlay.Core/HS2.CrystalOverlay.Core.csproj",
        "tools/hs2_crystal_overlay/tests/HS2.CrystalOverlay.Tests/HS2.CrystalOverlay.Tests.csproj"
    )
    foreach ($requiredEntry in $requiredEntries) {
        if ($entries -notcontains $requiredEntry) {
            throw "Release ZIP is missing required entry: $requiredEntry"
        }
    }

    $exampleConfig = Get-Content -LiteralPath (Join-Path $expanded "tools\turzx_side_screen\config.example.json") -Raw | ConvertFrom-Json
    if ($null -ne $exampleConfig.weather.latitude -or $null -ne $exampleConfig.weather.longitude) {
        throw "Public config example must not carry weather coordinates."
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$exampleConfig.network.publicInterface)) {
        throw "Public config example must not carry a machine interface name."
    }
    $weatherStart = Get-Content -LiteralPath (Join-Path $expanded "tools\turzx_weather_shim\start_turzx_weatherfix.ps1") -Raw
    if ($weatherStart -notmatch "TURZX_WEATHER_CONFIG" -or $weatherStart -notmatch '"--config"') {
        throw "Weather launcher must pass the private config to the fail-closed shim."
    }

    $forbiddenEntries = @(
        $entries | Where-Object {
            $_ -match '(^|/)(out|bin|obj|AppPackages|__pycache__)(/|$)' -or
            $_ -match '\.(dll|exe|pdb|msix|zip|log|db|sqlite)$' -or
            ($_.EndsWith(".json", [StringComparison]::OrdinalIgnoreCase) -and $_ -ne "tools/turzx_side_screen/config.example.json")
        }
    )
    if ($forbiddenEntries.Count -gt 0) {
        throw "Release ZIP contains generated, binary, or local-config entries: $($forbiddenEntries -join ', ')"
    }

    $textExtensions = @(
        ".cmd", ".cs", ".csproj", ".json", ".manifest", ".md", ".ps1", ".py", ".slnx", ".svg", ".vbs", ".xaml"
    )
    $privacyPatterns = @{
        "bundled weather location map" = ('KNOWN_' + 'LOCATIONS\s*=')
        "bundled QWeather endpoint" = ('qweather' + 'api\.com')
        "bundled QWeather numeric location" = '(?<!\d)10\d{7}(?!\d)'
    }
    Get-ChildItem -LiteralPath $expanded -Recurse -File | Where-Object {
        $_.Extension.ToLowerInvariant() -in $textExtensions
    } | ForEach-Object {
        $content = Get-Content -LiteralPath $_.FullName -Raw
        foreach ($label in $privacyPatterns.Keys) {
            if ([regex]::IsMatch($content, $privacyPatterns[$label])) {
                $relativePath = ($_.FullName.Substring($expanded.Length) -replace '^[\\/]+', '') -replace '\\', '/'
                throw "Release ZIP contains $label in $relativePath"
            }
        }
        if ($_.Extension -eq ".svg" -and $content -match 'RTSS') {
            $relativePath = ($_.FullName.Substring($expanded.Length) -replace '^[\\/]+', '') -replace '\\', '/'
            throw "Release ZIP contains stale RTSS design text in $relativePath"
        }
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Write-Host "Public release runtime and ZIP boundary checks verified."
