param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$TaskName = "TURZX SideScreen",
    [string]$Port = "COM7",
    [int]$IntervalMs = 3000,
    [switch]$HybridRefresh,
    [switch]$AltHelper,
    [switch]$Direct
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$hybridRefreshWasExplicit = $PSBoundParameters.ContainsKey("HybridRefresh")
$altHelperWasExplicit = $PSBoundParameters.ContainsKey("AltHelper")
$effectiveHybridRefresh = [bool]$HybridRefresh
$effectiveAltHelper = [bool]$AltHelper

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

function Resolve-RequestedSwitchMode {
    param(
        [AllowEmptyString()][string]$Arguments,
        [Parameter(Mandatory = $true)][string]$FlagName,
        [bool]$RequestedValue,
        [bool]$WasExplicit
    )

    if ($WasExplicit) {
        return $RequestedValue
    }

    $pattern = '(?i)(?:^|\s)-{0}(?:\s|$)' -f [regex]::Escape($FlagName)
    return [regex]::IsMatch($Arguments, $pattern)
}

$watchdog = Join-Path $Root "tools\turzx_side_screen\StartSideScreenWatchdog.ps1"
if (!(Test-Path -LiteralPath $watchdog)) {
    throw "Missing watchdog script: $watchdog"
}
$stopper = Join-Path $Root "tools\turzx_side_screen\StopSideScreenStack.ps1"
if (!(Test-Path -LiteralPath $stopper)) {
    throw "Missing stack stop script: $stopper"
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
        $effectiveHybridRefresh = Resolve-RequestedSwitchMode `
            -Arguments $registeredArguments `
            -FlagName "HybridRefresh" `
            -RequestedValue ([bool]$HybridRefresh) `
            -WasExplicit $hybridRefreshWasExplicit
        $effectiveAltHelper = Resolve-RequestedSwitchMode `
            -Arguments $registeredArguments `
            -FlagName "AltHelper" `
            -RequestedValue ([bool]$AltHelper) `
            -WasExplicit $altHelperWasExplicit
        $modeMatches = Test-ScheduledTaskActionMode `
            -Arguments $registeredArguments `
            -HybridRefreshEnabled $effectiveHybridRefresh `
            -AltHelperEnabled $effectiveAltHelper
        if (-not $modeMatches) {
            Write-Warning ("Scheduled task mode does not match the requested mode; using the direct launcher. Re-run install-startup-admin.ps1 to persist the change. registered=<{0}> requestedHybrid={1} requestedAlt={2}" -f $registeredArguments, $effectiveHybridRefresh, $effectiveAltHelper)
        }
        else {
            $outDir = Join-Path $Root "tools\turzx_side_screen\out"
            New-Item -ItemType Directory -Force -Path $outDir | Out-Null
            Set-Content -LiteralPath (Join-Path $outDir "restart-on-start.flag") -Value (Get-Date -Format "o") -Encoding ASCII
            & schtasks.exe /End /TN $TaskName *> $null
            Start-Sleep -Milliseconds 600

            # The hidden wscript task adapter can be ended while its elevated
            # PowerShell watchdog remains alive. Reclaim the exact project stack
            # before /Run so the new watchdog actually loads current defaults.
            $stopArguments = @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $stopper,
                "-Root", $Root, "-IncludeWatchdog", "-SkipStackEntrypoint", "-Quiet"
            )
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            $principal = [Security.Principal.WindowsPrincipal]::new($identity)
            $isElevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            if ($isElevated) {
                & powershell.exe @stopArguments
            }
            else {
                $sudo = Get-Command sudo.exe -ErrorAction SilentlyContinue
                if ($null -eq $sudo) {
                    throw "The elevated watchdog outlived its hidden task parent. Re-run this command as Administrator."
                }
                & $sudo.Source powershell.exe @stopArguments
            }
            if ($LASTEXITCODE -ne 0) {
                throw "Unable to reclaim the previous side-screen watchdog (exit $LASTEXITCODE)."
            }
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
if ($effectiveHybridRefresh) { $directArguments += "-HybridRefresh" }
if ($effectiveAltHelper) { $directArguments += "-AltHelper" }
& powershell @directArguments
