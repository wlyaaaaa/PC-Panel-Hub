[CmdletBinding()]
param(
    [string]$TaskName = "HS2 Netease Playback Bridge"
)

$ErrorActionPreference = "Stop"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this installer as administrator."
}

$toolRoot = $PSScriptRoot
$project = Join-Path $toolRoot `
    "src\HS2.NeteasePlaybackBridge\HS2.NeteasePlaybackBridge.csproj"
$installRoot = Join-Path $env:LOCALAPPDATA `
    "HS2.CrystalOverlay\NeteasePlaybackBridge"
$stagingRoot = Join-Path $env:TEMP `
    ("HS2.NeteasePlaybackBridge.Publish." + $PID)

try {
    New-Item -ItemType Directory -Force -Path $stagingRoot |
        Out-Null
    & dotnet publish $project `
        --configuration Release `
        --runtime win-x64 `
        --self-contained false `
        --output $stagingRoot `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Bridge publish failed with exit code $LASTEXITCODE."
    }

    $existingTask = Get-ScheduledTask `
        -TaskName $TaskName `
        -ErrorAction SilentlyContinue
    if ($existingTask) {
        Stop-ScheduledTask `
            -TaskName $TaskName `
            -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 300
    }

    $installedExe = Join-Path $installRoot `
        "HS2.NeteasePlaybackBridge.exe"
    Get-Process -Name "HS2.NeteasePlaybackBridge" `
        -ErrorAction SilentlyContinue |
        Stop-Process -Force
    Get-Process -Name "HS2.NeteasePlaybackBridge" `
        -ErrorAction SilentlyContinue |
        Wait-Process -Timeout 5 -ErrorAction SilentlyContinue

    New-Item -ItemType Directory -Force -Path $installRoot |
        Out-Null
    Copy-Item `
        -Path (Join-Path $stagingRoot "*") `
        -Destination $installRoot `
        -Recurse `
        -Force

    if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
        throw "Published bridge executable was not installed."
    }

    $userId = $identity.Name
    $action = New-ScheduledTaskAction `
        -Execute $installedExe `
        -WorkingDirectory $installRoot
    $trigger = New-ScheduledTaskTrigger `
        -AtLogOn `
        -User $userId
    $taskPrincipal = New-ScheduledTaskPrincipal `
        -UserId $userId `
        -LogonType Interactive `
        -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -RestartCount 3 `
        -RestartInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $action `
        -Trigger $trigger `
        -Principal $taskPrincipal `
        -Settings $settings `
        -Description `
            "Read-only playback-position bridge for HS2 Crystal Overlay." `
        -Force |
        Out-Null
    Start-ScheduledTask -TaskName $TaskName
    Start-Sleep -Milliseconds 750

    $task = Get-ScheduledTaskInfo -TaskName $TaskName
    [pscustomobject]@{
        TaskName = $TaskName
        InstalledExecutable = $installedExe
        LastTaskResult = $task.LastTaskResult
        NextRunTime = $task.NextRunTime
    }
}
finally {
    $resolvedTemp = (
        [IO.Path]::GetFullPath($env:TEMP)).TrimEnd('\') + '\'
    $resolvedStaging = [IO.Path]::GetFullPath($stagingRoot)
    if ($resolvedStaging.StartsWith(
            $resolvedTemp,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedStaging)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
