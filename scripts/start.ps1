param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$TaskName = "TURZX SideScreen",
    [string]$Port = "COM7",
    [int]$IntervalMs = 3000,
    [switch]$HybridRefresh = $true,
    [switch]$AltHelper,
    [switch]$Direct
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-ScheduledTaskActionMode {
    param(
        [AllowEmptyString()][string]$Arguments,
        [bool]$HybridRefreshEnabled,
        [bool]$AltHelperEnabled
    )

    $hasHybridRefresh = [regex]::IsMatch($Arguments, '(?i)(?:^|\s)-HybridRefresh(?:\s|$)')
    $hasAltHelper = [regex]::IsMatch($Arguments, '(?i)(?:^|\s)-AltHelper(?:\s|$)')
    return ($hasHybridRefresh -eq $HybridRefreshEnabled) -and
        ($hasAltHelper -eq $AltHelperEnabled)
}

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
    $scheduledTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($null -ne $scheduledTask) {
        $registeredArguments = (($scheduledTask.Actions | ForEach-Object { [string]$_.Arguments }) -join ' ')
        $modeMatches = Test-ScheduledTaskActionMode `
            -Arguments $registeredArguments `
            -HybridRefreshEnabled ([bool]$HybridRefresh) `
            -AltHelperEnabled ([bool]$AltHelper)
        if (-not $modeMatches) {
            Write-Warning ("Scheduled task mode does not match the requested mode; using the direct launcher. Re-run install-startup-admin.ps1 to persist the change. registered=<{0}> requestedHybrid={1} requestedAlt={2}" -f $registeredArguments, [bool]$HybridRefresh, [bool]$AltHelper)
        }
        else {
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
}

$directArguments = @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $watchdog,
    "-Root", $Root, "-Port", $Port, "-IntervalMs", [string]$IntervalMs
)
if ($HybridRefresh) { $directArguments += "-HybridRefresh" }
if ($AltHelper) { $directArguments += "-AltHelper" }
& powershell @directArguments
