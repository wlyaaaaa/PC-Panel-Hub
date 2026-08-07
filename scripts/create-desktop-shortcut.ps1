param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$ShortcutName = "TURZX SideScreen Start.lnk",
    [string]$StartMenuShortcutName = "TURZX SideScreen.lnk",
    [string]$Port = "COM7",
    [int]$IntervalMs = 3000,
    [switch]$NoDesktop,
    [switch]$NoStartMenu,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath $Root).Path
$startScript = Join-Path $Root "scripts\start.ps1"
if (!(Test-Path -LiteralPath $startScript)) {
    throw "Missing start script: $startScript"
}

function New-SideScreenShortcut {
    param(
        [string]$ShortcutPath,
        [string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($ShortcutPath)) {
        throw "$Label shortcut path not found."
    }

    if ($DryRun) {
        Write-Host ("{0} shortcut: {1}" -f $Label, $ShortcutPath)
        return
    }

    $parent = Split-Path -Parent $ShortcutPath
    New-Item -ItemType Directory -Force -Path $parent | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
    $shortcut.Arguments = ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -Root "{1}" -Port "{2}" -IntervalMs {3}' -f $startScript, $Root, $Port, $IntervalMs)
    $shortcut.WorkingDirectory = $Root
    $shortcut.IconLocation = Join-Path $env:WINDIR "System32\shell32.dll,25"
    $shortcut.Description = "Start or restart the TURZX SideScreen watchdog."
    $shortcut.Save()

    Write-Host ("{0} shortcut: {1}" -f $Label, $ShortcutPath)
}

if (-not $NoDesktop) {
    $desktop = [Environment]::GetFolderPath("Desktop")
    if ([string]::IsNullOrWhiteSpace($desktop)) {
        throw "Desktop path not found."
    }
    New-SideScreenShortcut -ShortcutPath (Join-Path $desktop $ShortcutName) -Label "Desktop"
}

if (-not $NoStartMenu) {
    $programs = [Environment]::GetFolderPath("Programs")
    if ([string]::IsNullOrWhiteSpace($programs)) {
        throw "Start Menu programs path not found."
    }
    New-SideScreenShortcut -ShortcutPath (Join-Path $programs $StartMenuShortcutName) -Label "Start menu"
}
