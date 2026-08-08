param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Port = "COM7",
    [int]$IntervalMs = 3000,
    [ValidateRange(3000, 60000)][int]$SendTimeoutMs = 10000,
    [ValidateRange(100, 5000)][int]$DiffSendTimeoutMs = 900,
    [ValidateRange(1, 10)][int]$MaxConsecutiveSendFailures = 1,
    [int]$FullResyncEveryFrames = 0,
    [ValidateRange(0, 255)][int]$BaselineBrightness = 170,
    [int]$PreviewIntervalSeconds = 45,
    [int64]$MaxStackLogBytes = 1048576,
    [int64]$MaxStreamLogBytes = 5242880,
    [ValidateRange(1, 32)][int]$LogBackupCount = 3,
    [switch]$HybridRefresh,
    [switch]$AltHelper,
    [switch]$Worker
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
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
    if ($AltHelper) {
        $watchdogArguments += "-AltHelper"
    }
    Start-Process -FilePath "powershell.exe" `
        -ArgumentList $watchdogArguments `
        -WorkingDirectory $scriptDir `
        -WindowStyle Hidden | Out-Null
    exit 0
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
    $requiresTimeAudit = -not [string]::IsNullOrWhiteSpace($env:TIMEAUDIT_DSN)
    $requiredModules = if ($requiresTimeAudit) { @("psutil", "asyncpg") } else { @() }
    $cmd = Get-Command python -ErrorAction SilentlyContinue
    $commandPath = if ($cmd) { $cmd.Source } else { $null }
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

    if ($requiresTimeAudit) {
        throw "No Python interpreter with required modules psutil+asyncpg was found for TimeAudit FPS."
    }
    throw "python.exe not found"
}

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

Write-StackLog ("starting stack root={0} port={1} interval={2} hybrid={3} altHelper={4}" -f $Root, $Port, $IntervalMs, $HybridRefresh.IsPresent, $AltHelper.IsPresent)

# Keep the custom side-screen stack authoritative.
Get-Process "TURZX", "TURZX.weatherfix", "TURZX.weatherfix.metrics" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Stop-ProcessByCommandLine -NamePattern "python*" -CommandPattern "*turzx_side_screen\metrics_agent.py*"
Stop-ProcessByCommandLine -NamePattern "python*" -CommandPattern "*turzx_side_screen\top_processes_helper.py*"
Stop-ProcessByCommandLine -NamePattern "python*" -CommandPattern "*turzx_weather_shim\turzx_weather_shim.py*"
Stop-ProcessByCommandLine -NamePattern "TURZX.SideScreen.Stream.exe" -CommandPattern "*turzx_side_screen*"

$python = Find-Python
Write-StackLog ("python selected path={0} timeauditModulesRequired={1}" -f $python, (-not [string]::IsNullOrWhiteSpace($env:TIMEAUDIT_DSN)))
$weatherShim = Join-Path $Root "tools\turzx_weather_shim\turzx_weather_shim.py"
$weatherDir = Split-Path -Parent $weatherShim
Start-Process -FilePath $python `
    -ArgumentList @($weatherShim, "--host", "127.0.0.1", "--port", "18080") `
    -WorkingDirectory $weatherDir `
    -WindowStyle Hidden
Write-StackLog "weather shim launched"

Start-Process -FilePath $python `
    -ArgumentList @((Join-Path $scriptDir "top_processes_helper.py"), "--cache-path", (Join-Path $outDir "top-processes.json"), "--interval-seconds", "3", "--limit", "5") `
    -WorkingDirectory $scriptDir `
    -WindowStyle Hidden
Write-StackLog "top processes helper launched"

Start-Sleep -Milliseconds 900

$streamScript = Join-Path $scriptDir "StartVideoStream.ps1"
try {
    # HybridRefresh is explicit and reversible: it uses a vendor-shaped command-200
    # baseline plus bounded command-204 deltas. The default remains full command 200.
    & $streamScript -Root $Root -Port $Port -IntervalMs $IntervalMs -Frames 0 -SendTimeoutMs $SendTimeoutMs -DiffSendTimeoutMs $DiffSendTimeoutMs -MaxConsecutiveSendFailures $MaxConsecutiveSendFailures -FullResyncEveryFrames $FullResyncEveryFrames -BaselineBrightness $BaselineBrightness -PreviewIntervalSeconds $PreviewIntervalSeconds -PythonPath $python -HybridRefresh:$HybridRefresh -AltHelper:$AltHelper *>&1 |
        ForEach-Object {
            Write-BoundedLogLine -Path $stdoutPath -Message ([string]$_) -MaxBytes $MaxStreamLogBytes -BackupCount $LogBackupCount
        }
}
catch {
    Write-BoundedLogLine -Path $stderrPath -Message ("{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $_.Exception.Message) -MaxBytes $MaxStackLogBytes -BackupCount $LogBackupCount
    throw
}
