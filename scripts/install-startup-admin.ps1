param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$TaskName = "TURZX SideScreen",
    [string]$ResumeTaskName = "TURZX SideScreen Resume",
    [string]$Port = "COM7",
    [int]$IntervalMs = 3000,
    [switch]$HybridRefresh,
    [switch]$AltHelper,
    [switch]$DoNotDisableOldTasks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Please run as Administrator, or use scripts\install-startup-admin.cmd for a UAC prompt."
}

$Root = (Resolve-Path $Root).Path
$script = Join-Path $Root "tools\turzx_side_screen\StartSideScreenWatchdog.ps1"
$launcher = Join-Path $Root "tools\turzx_side_screen\StartSideScreenWatchdog-Hidden.vbs"
$resumeScript = Join-Path $Root "tools\turzx_side_screen\RestartSideScreenAfterResume.ps1"
$resumeLauncher = Join-Path $Root "tools\turzx_side_screen\RestartSideScreenAfterResume-Hidden.vbs"
if (!(Test-Path -LiteralPath $script)) {
    throw "Missing watchdog script: $script"
}
if (!(Test-Path -LiteralPath $launcher)) {
    throw "Missing watchdog hidden launcher: $launcher"
}
if (!(Test-Path -LiteralPath $resumeScript)) {
    throw "Missing resume recovery script: $resumeScript"
}
if (!(Test-Path -LiteralPath $resumeLauncher)) {
    throw "Missing resume recovery hidden launcher: $resumeLauncher"
}

$checker = Join-Path $Root "scripts\check-runtime.ps1"
if (Test-Path -LiteralPath $checker) {
    powershell -NoProfile -ExecutionPolicy Bypass -File $checker -Root $Root
    if ($LASTEXITCODE -ne 0) {
        throw "Runtime check failed. Startup task was not installed."
    }
}

$workingDir = Split-Path -Parent $script
$wscriptPath = Join-Path $env:WINDIR "System32\wscript.exe"
$refreshArguments = ""
if ($HybridRefresh) { $refreshArguments += " -HybridRefresh" }
if ($AltHelper) { $refreshArguments += " -AltHelper" }
$launcherArguments = ('"{0}" -Root "{1}" -Port "{2}" -IntervalMs {3}{4}' -f $launcher, $Root, $Port, $IntervalMs, $refreshArguments)
$action = New-ScheduledTaskAction `
    -Execute $wscriptPath `
    -Argument $launcherArguments `
    -WorkingDirectory $workingDir

$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal `
    -UserId ("{0}\{1}" -f $env:USERDOMAIN, $env:USERNAME) `
    -LogonType Interactive `
    -RunLevel Highest

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -MultipleInstances IgnoreNew `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit (New-TimeSpan -Hours 0)

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description ("Start TURZX SideScreen from {0}" -f $Root) `
    -Force | Out-Null

$resumeRefreshArguments = ""
if ($HybridRefresh) { $resumeRefreshArguments += " -HybridRefresh" }
else { $resumeRefreshArguments += " -NoHybridRefresh" }
if ($AltHelper) { $resumeRefreshArguments += " -AltHelper" }
else { $resumeRefreshArguments += " -NoAltHelper" }
$resumeAction = 'wscript.exe "{0}" -Root "{1}" -Port {2} -IntervalMs {3}{4}' -f $resumeLauncher, $Root, $Port, $IntervalMs, $resumeRefreshArguments
$resumeEventQuery = "*[System[(Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1) or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=107)]]"
$resumeCreateOutput = & schtasks.exe /Create /TN $ResumeTaskName /SC ONEVENT /EC System /MO $resumeEventQuery /TR $resumeAction /RL HIGHEST /F 2>&1
if ($LASTEXITCODE -ne 0) {
    $resumeCreateText = ($resumeCreateOutput | Out-String).Trim()
    throw "Failed to register resume recovery task: $resumeCreateText"
}
$resumeTask = Get-ScheduledTask -TaskName $ResumeTaskName -ErrorAction Stop
$resumeTask.Settings.DisallowStartIfOnBatteries = $false
$resumeTask.Settings.StopIfGoingOnBatteries = $false
Set-ScheduledTask -InputObject $resumeTask | Out-Null

if (-not $DoNotDisableOldTasks) {
    foreach ($oldTask in @("TURZX WeatherFix", "TURZX_88inch_AdminStart")) {
        $existing = Get-ScheduledTask -TaskName $oldTask -ErrorAction SilentlyContinue
        if ($existing) {
            Disable-ScheduledTask -TaskName $oldTask -ErrorAction SilentlyContinue | Out-Null
            Write-Host "Disabled old startup task: $oldTask"
        }
    }
}

$shortcutScript = Join-Path $Root "scripts\create-desktop-shortcut.ps1"
if (Test-Path -LiteralPath $shortcutScript) {
    try {
        powershell -NoProfile -ExecutionPolicy Bypass -File $shortcutScript -Root $Root -Port $Port -IntervalMs $IntervalMs | Out-Host
    }
    catch {
        Write-Warning ("Startup task installed, but shortcut creation failed: {0}" -f $_.Exception.Message)
    }
}

Get-ScheduledTask | Where-Object { $_.TaskName -in @($TaskName, $ResumeTaskName, "TURZX WeatherFix", "TURZX_88inch_AdminStart") } |
    Select-Object TaskName, State, @{Name="RunLevel";Expression={$_.Principal.RunLevel}},
        @{Name="Action";Expression={($_.Actions | ForEach-Object { $_.Execute + " " + $_.Arguments }) -join " | "}} |
    Format-List

Write-Host "Installed highest-privilege startup task: $TaskName"
Write-Host "Installed resume recovery task: $ResumeTaskName"
Write-Host "Root: $Root"
