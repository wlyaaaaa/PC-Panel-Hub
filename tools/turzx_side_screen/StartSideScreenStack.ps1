param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Port = "",
    [int]$IntervalMs = 3000,
    [ValidateRange(3000, 60000)][int]$SendTimeoutMs = 10000,
    [ValidateRange(100, 5000)][int]$DiffSendTimeoutMs = 900,
    [ValidateRange(1, 10)][int]$MaxConsecutiveSendFailures = 1,
    [int]$FullResyncEveryFrames = 900,
    [ValidateRange(0, 255)][int]$BaselineBrightness = 170,
    [int]$PreviewIntervalSeconds = 45,
    [int64]$MaxStackLogBytes = 1048576,
    [int64]$MaxStreamLogBytes = 5242880,
    [ValidateRange(1, 32)][int]$LogBackupCount = 3,
    [switch]$HybridRefresh,
    [switch]$Worker
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot 'PanelDevicePolicy.ps1')
$Port = Get-TurzxConfiguredPort -Root $Root -Port $Port

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptDir "HiddenProcessLauncher.ps1")
$outDir = Join-Path $scriptDir "out"
$logPath = Join-Path $outDir "side-screen-stack.log"
$stdoutPath = Join-Path $outDir "side-screen-stack.stdout.log"
$stderrPath = Join-Path $outDir "side-screen-stack.stderr.log"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Write-BoundedLogLine {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $true)][int64]$MaxBytes,
        [int]$BackupCount = 3
    )

    try {
        $directory = Split-Path -Parent $Path
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            New-Item -ItemType Directory -Force -Path $directory | Out-Null
        }

        $recordBytes = [Text.Encoding]::UTF8.GetByteCount($Message + [Environment]::NewLine)
        if ($MaxBytes -gt 0 -and
            (Test-Path -LiteralPath $Path -PathType Leaf) -and
            ((Get-Item -LiteralPath $Path).Length + $recordBytes -gt $MaxBytes)) {
            for ($index = $BackupCount; $index -ge 1; $index--) {
                $source = if ($index -eq 1) { $Path } else { "{0}.{1}" -f $Path, ($index - 1) }
                $destination = "{0}.{1}" -f $Path, $index
                if (Test-Path -LiteralPath $source -PathType Leaf) {
                    Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
                    if ((Get-Item -LiteralPath $source).Length -gt $MaxBytes) {
                        Remove-Item -LiteralPath $source -Force
                    }
                    else {
                        Move-Item -LiteralPath $source -Destination $destination -Force
                    }
                }
            }
        }

        Add-Content -LiteralPath $Path -Value $Message -Encoding UTF8
    }
    catch {
        # Diagnostics are best effort: a log viewer must never stop the display worker.
    }
}

function Write-StackLog {
    param([string]$Message)
    $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Write-BoundedLogLine -Path $logPath -Message $line -MaxBytes $MaxStackLogBytes -BackupCount $LogBackupCount
}

if (-not $Worker) {
    $watchdog = Join-Path $scriptDir "StartSideScreenWatchdog.ps1"
    $stopScript = Join-Path $scriptDir "StopSideScreenStack.ps1"
    $restartFlag = Join-Path $outDir "restart-on-start.flag"
    $stopFlag = Join-Path $outDir "stop-on-start.flag"
    if (!(Test-Path -LiteralPath $watchdog)) {
        throw "Missing watchdog script: $watchdog"
    }

    if (Test-Path -LiteralPath $stopFlag) {
        Remove-Item -LiteralPath $stopFlag -Force -ErrorAction SilentlyContinue
        Write-StackLog "stop-on-start flag detected; stopping stack and exiting"
        powershell -NoProfile -ExecutionPolicy Bypass -File $stopScript -Root $Root -IncludeWatchdog -SkipStackEntrypoint -Quiet
        exit 0
    }

    if (Test-Path -LiteralPath $restartFlag) {
        Remove-Item -LiteralPath $restartFlag -Force -ErrorAction SilentlyContinue
        Write-StackLog "restart-on-start flag detected; stopping stale elevated stack first"
        powershell -NoProfile -ExecutionPolicy Bypass -File $stopScript -Root $Root -IncludeWatchdog -SkipStackEntrypoint -Quiet
        Start-Sleep -Seconds 2
    }

    Write-StackLog ("delegating to watchdog root={0} port={1} interval={2} hybrid={3}" -f $Root, $Port, $IntervalMs, $HybridRefresh.IsPresent)
    $watchdogArguments = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", $watchdog,
            "-Root", $Root,
            "-Port", $Port,
            "-IntervalMs", [string]$IntervalMs
            "-SendTimeoutMs", [string]$SendTimeoutMs
            "-DiffSendTimeoutMs", [string]$DiffSendTimeoutMs
            "-MaxConsecutiveSendFailures", [string]$MaxConsecutiveSendFailures
            "-FullResyncEveryFrames", [string]$FullResyncEveryFrames
            "-ActiveBrightness", [string]$BaselineBrightness
            "-PreviewIntervalSeconds", [string]$PreviewIntervalSeconds
            "-MaxStackLogBytes", [string]$MaxStackLogBytes
            "-MaxStreamLogBytes", [string]$MaxStreamLogBytes
            "-LogBackupCount", [string]$LogBackupCount
        )
    if ($HybridRefresh) {
        $watchdogArguments += "-HybridRefresh"
    }
    Start-HiddenProcess -FilePath "powershell.exe" `
        -ArgumentList $watchdogArguments `
        -WorkingDirectory $scriptDir | Out-Null
    exit 0
}

. (Join-Path $PSScriptRoot 'MetricsEndpointPolicy.ps1')

function Stop-ProcessByCommandLine {
    param(
        [string]$NamePattern,
        [string]$CommandPattern
    )

    Get-CimInstance Win32_Process |
        Where-Object { $_.Name -like $NamePattern -and $_.CommandLine -like $CommandPattern } |
        ForEach-Object {
            Write-StackLog ("stopping PID={0} CMD={1}" -f $_.ProcessId, $_.CommandLine)
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
}

Write-StackLog ("starting stack root={0} port={1} interval={2} hybrid={3}" -f $Root, $Port, $IntervalMs, $HybridRefresh.IsPresent)

# Keep the custom side-screen stack authoritative.
Get-Process "TURZX", "TURZX.weatherfix", "TURZX.weatherfix.metrics" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Stop-ProcessByCommandLine -NamePattern "python*" -CommandPattern "*turzx_side_screen\metrics_agent.py*"
Stop-ProcessByCommandLine -NamePattern "python*" -CommandPattern "*turzx_side_screen\top_processes_helper.py*"
Stop-ProcessByCommandLine -NamePattern "python*" -CommandPattern "*turzx_weather_shim\turzx_weather_shim.py*"
Stop-ProcessByCommandLine -NamePattern "TURZX.SideScreen.Stream.exe" -CommandPattern "*turzx_side_screen*"

$python = Find-Python
Write-StackLog ("python selected path={0} timeauditModulesRequired={1}" -f $python, (
    -not [string]::IsNullOrWhiteSpace($env:TIMEAUDIT_DSN) -or
    (-not [string]::IsNullOrWhiteSpace($env:TIMEAUDIT_DB_PASSWORD) -and -not [string]::IsNullOrWhiteSpace($env:TIMEAUDIT_DB_USER))
))
$weatherShim = Join-Path $Root "tools\turzx_weather_shim\turzx_weather_shim.py"
$weatherDir = Split-Path -Parent $weatherShim
$weatherConfig = Join-Path $Root 'tools\turzx_side_screen\config.json'
$weatherStdout = Join-Path $outDir 'weather-shim.stdout.log'
$weatherStderr = Join-Path $outDir 'weather-shim.stderr.log'
$weatherArgs = @('-u', $weatherShim, '--host', '127.0.0.1', '--port', '18080')
if (Test-Path -LiteralPath $weatherConfig -PathType Leaf) {
    $weatherArgs += @('--config', $weatherConfig)
}
$weatherProcess = Start-HiddenProcess -FilePath $python `
    -ArgumentList $weatherArgs -WorkingDirectory $weatherDir `
    -RedirectStandardOutput $weatherStdout -RedirectStandardError $weatherStderr
if ($weatherProcess.WaitForExit(1000)) {
    $reason = @(Get-Content -LiteralPath $weatherStderr -Tail 2 -ErrorAction SilentlyContinue) -join ' '
    if ([string]::IsNullOrWhiteSpace($reason)) { $reason = "exit code $($weatherProcess.ExitCode)" }
    Write-StackLog "weather shim exited during startup: $reason"
} else {
    Write-StackLog ("weather shim started pid={0}" -f $weatherProcess.Id)
}

Start-HiddenProcess -FilePath $python `
    -ArgumentList @((Join-Path $scriptDir "top_processes_helper.py"), "--cache-path", (Join-Path $outDir "top-processes.json"), "--interval-seconds", "3", "--limit", "5") `
    -WorkingDirectory $scriptDir | Out-Null
Write-StackLog "top processes helper launched"

Start-Sleep -Milliseconds 900

$streamScript = Join-Path $scriptDir "StartVideoStream.ps1"
try {
    # HybridRefresh is explicit and reversible: it uses a vendor-shaped command-200
    # baseline plus bounded command-204 deltas. The default remains full command 200.
    & $streamScript -Root $Root -Port $Port -IntervalMs $IntervalMs -Frames 0 -SendTimeoutMs $SendTimeoutMs -DiffSendTimeoutMs $DiffSendTimeoutMs -MaxConsecutiveSendFailures $MaxConsecutiveSendFailures -FullResyncEveryFrames $FullResyncEveryFrames -BaselineBrightness $BaselineBrightness -PreviewIntervalSeconds $PreviewIntervalSeconds -PythonPath $python -HybridRefresh:$HybridRefresh *>&1 |
        ForEach-Object {
            Write-BoundedLogLine -Path $stdoutPath -Message ([string]$_) -MaxBytes $MaxStreamLogBytes -BackupCount $LogBackupCount
        }
}
catch {
    Write-BoundedLogLine -Path $stderrPath -Message ("{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $_.Exception.Message) -MaxBytes $MaxStackLogBytes -BackupCount $LogBackupCount
    throw
}
