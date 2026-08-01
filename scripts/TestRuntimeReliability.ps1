param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath $Root).Path
$side = Join-Path $Root "tools\turzx_side_screen"
$streamSource = Get-Content -Raw -LiteralPath (Join-Path $side "TURZX.SideScreen.Stream.cs")
$streamStart = Get-Content -Raw -LiteralPath (Join-Path $side "StartVideoStream.ps1")
$stackStart = Get-Content -Raw -LiteralPath (Join-Path $side "StartSideScreenStack.ps1")
$watchdogStart = Get-Content -Raw -LiteralPath (Join-Path $side "StartSideScreenWatchdog.ps1")
$videoTest = Get-Content -Raw -LiteralPath (Join-Path $side "TestVideoStream.ps1")
$rendererTest = Get-Content -Raw -LiteralPath (Join-Path $side "TestRenderer.ps1")
$cadenceTestPath = Join-Path $side "TestStreamCadence.ps1"
$cadenceTest = Get-Content -Raw -LiteralPath $cadenceTestPath
$testEntry = Get-Content -Raw -LiteralPath (Join-Path $Root "scripts\test.ps1")
$runtimeCheck = Get-Content -Raw -LiteralPath (Join-Path $Root "scripts\check-runtime.ps1")

foreach ($pattern in @(
    "WriteFileAtomically",
    "File.Replace",
    "SavePreviewAtomically",
    "WriteHeartbeatCopies",
    "stream-heartbeat-a.json",
    "stream-heartbeat-b.json",
    "preview warning:",
    "PreviewIntervalSeconds",
    "--preview-interval-seconds"
)) {
    if ($streamSource -notmatch [regex]::Escape($pattern)) {
        throw "Stream implementation missing reliability contract: $pattern"
    }
}

if ($streamSource -match [regex]::Escape("bitmap.Save(preview, ImageFormat.Png)")) {
    throw "Production preview must not overwrite stream-last.png directly on every frame."
}
if ($streamSource -match [regex]::Escape("File.WriteAllText(path, json.ToString(), Encoding.UTF8)")) {
    throw "Heartbeat must not overwrite stream-heartbeat.json in place."
}

foreach ($pattern in @(
    '[int]$PreviewIntervalSeconds = 45',
    '[string]$PreviewDir',
    '"--preview-interval-seconds"',
    '[string]$PythonPath',
    '"TURZX.SideScreen.Stream.*.exe"',
    '$staleBuildCutoff',
    'Start-Process',
    '-FilePath $PythonPath'
)) {
    if ($streamStart -notmatch [regex]::Escape($pattern)) {
        throw "StartVideoStream missing reliability contract: $pattern"
    }
}

foreach ($pattern in @(
    "psutil",
    "asyncpg",
    "Test-PythonModules",
    "[ValidateRange(1, 32)][int]`$LogBackupCount",
    "-PythonPath",
    "Write-BoundedLogLine",
    "MaxStackLogBytes",
    "MaxStreamLogBytes",
    "side-screen-stack.stdout.log"
)) {
    if ($stackStart -notmatch [regex]::Escape($pattern)) {
        throw "Stack Python selection missing dependency contract: $pattern"
    }
}

foreach ($pattern in @(
    "Write-BoundedLogLine",
    "MaxWatchdogLogBytes",
    "[ValidateRange(1, 32)][int]`$LogBackupCount",
    "BackupCount",
    '$frameStatus -ne "ok"'
)) {
    if ($watchdogStart -notmatch [regex]::Escape($pattern)) {
        throw "Watchdog log rotation missing bounded-log contract: $pattern"
    }
}
if ($watchdogStart -match [regex]::Escape("-RedirectStandardOutput `$stdoutPath")) {
    throw "Watchdog must not hold stdout open indefinitely; the worker must own bounded stdout logging."
}

if ($rendererTest -notmatch [regex]::Escape('outDir "tests\renderer-preview.png"')) {
    throw "Renderer test preview must stay under the isolated out\\tests directory."
}

foreach ($pattern in @(
    "psutil",
    "asyncpg",
    "Test-PythonModules"
)) {
    if ($runtimeCheck -notmatch [regex]::Escape($pattern)) {
        throw "Runtime checker missing Python dependency contract: $pattern"
    }
}

foreach ($pattern in @(
    "stream-heartbeat.json",
    "Get-ScheduledTask",
    "Get-Process"
)) {
    if ($testEntry -notmatch [regex]::Escape($pattern)) {
        throw "Test entrypoint missing live-stream detection contract: $pattern"
    }
}
if ($testEntry -match [regex]::Escape('$_.CommandLine')) {
    throw "Live-stream detection must not depend on WMI CommandLine visibility."
}
if ($testEntry -match 'elseif\s*\(\s*\$runningStream\s*\)') {
    throw "The isolated dry-run video test must only be skipped when SkipStreamWhenRunning is explicit."
}

foreach ($pattern in @(
    "out\tests",
    "-PreviewDir",
    "finally",
    "Remove-Item"
)) {
    if ($videoTest -notmatch [regex]::Escape($pattern)) {
        throw "Video stream test must isolate production diagnostics; missing: $pattern"
    }
}

foreach ($pattern in @(
    "finally",
    "Remove-Item",
    "LastWriteTimeUtc",
    "TestStreamCadenceProgram.*"
)) {
    if ($cadenceTest -notmatch [regex]::Escape($pattern)) {
        throw "Cadence test must clean temporary build artifacts; missing: $pattern"
    }
}

$tokens = $null
$parseErrors = $null
$stackAst = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $side "StartSideScreenStack.ps1"),
    [ref]$tokens,
    [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Unable to parse stack script for bounded-log behavior test."
}
$boundedFunction = $stackAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq "Write-BoundedLogLine"
    },
    $true)
if ($null -eq $boundedFunction) {
    throw "Stack script must define Write-BoundedLogLine."
}

. ([scriptblock]::Create($boundedFunction.Extent.Text))

$watchdogTokens = $null
$watchdogParseErrors = $null
$watchdogAst = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $side "StartSideScreenWatchdog.ps1"),
    [ref]$watchdogTokens,
    [ref]$watchdogParseErrors)
if ($watchdogParseErrors.Count -gt 0) {
    throw "Unable to parse watchdog script for redundant-heartbeat behavior test."
}
$heartbeatFunction = $watchdogAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq "Get-StreamHeartbeatHealth"
    },
    $true)
if ($null -eq $heartbeatFunction) {
    throw "Watchdog must define Get-StreamHeartbeatHealth."
}
. ([scriptblock]::Create($heartbeatFunction.Extent.Text))

$heartbeatTestRoot = Join-Path ([IO.Path]::GetTempPath()) ("turzx-heartbeat-slots-{0}" -f [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $heartbeatTestRoot | Out-Null
$legacyHeartbeat = Join-Path $heartbeatTestRoot "stream-heartbeat.json"
$slotAHeartbeat = Join-Path $heartbeatTestRoot "stream-heartbeat-a.json"
$slotBHeartbeat = Join-Path $heartbeatTestRoot "stream-heartbeat-b.json"
$heartbeatPaths = @($legacyHeartbeat, $slotAHeartbeat, $slotBHeartbeat)
$HeartbeatStaleSeconds = 15
try {
    Set-Content -LiteralPath $slotBHeartbeat -Value '{"frame":41,"status":"ok","error":null}' -Encoding UTF8
    (Get-Item -LiteralPath $slotBHeartbeat).LastWriteTimeUtc = [DateTime]::UtcNow.AddSeconds(-1)
    Set-Content -LiteralPath $legacyHeartbeat -Value '{"frame":40,"status":"ok","error":null}' -Encoding UTF8
    (Get-Item -LiteralPath $legacyHeartbeat).LastWriteTimeUtc = [DateTime]::UtcNow

    $legacyLock = [IO.File]::Open(
        $legacyHeartbeat,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::None)
    try {
        $slotHealth = Get-StreamHeartbeatHealth
    }
    finally {
        $legacyLock.Dispose()
    }
    if (-not $slotHealth.Healthy -or $slotHealth.Reason -notmatch "stream-heartbeat-b.json") {
        throw "Watchdog did not recover from a locked legacy heartbeat via the alternate slot: $($slotHealth.Reason)"
    }
}
finally {
    Remove-Item -LiteralPath $heartbeatTestRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$rotationRoot = Join-Path ([IO.Path]::GetTempPath()) ("turzx-log-rotation-{0}" -f [guid]::NewGuid().ToString("N"))
$rotationPath = Join-Path $rotationRoot "bounded.log"
New-Item -ItemType Directory -Force -Path $rotationRoot | Out-Null
try {
    1..80 | ForEach-Object {
        Write-BoundedLogLine -Path $rotationPath -Message (("{0:D3} " -f $_) + ("x" * 120)) -MaxBytes 1024 -BackupCount 2
    }
    $rotationFiles = @(Get-ChildItem -LiteralPath $rotationRoot -File)
    if ($rotationFiles.Count -gt 3) {
        throw "Bounded log retained too many files: $($rotationFiles.Count)"
    }
    if (!(Test-Path -LiteralPath ($rotationPath + ".1"))) {
        throw "Bounded log did not create a rotated backup."
    }
    if (($rotationFiles | Measure-Object Length -Maximum).Maximum -gt 1200) {
        throw "Bounded log exceeded configured size by more than one record."
    }

    $oversizedPath = Join-Path $rotationRoot "oversized.log"
    Set-Content -LiteralPath $oversizedPath -Value ("z" * 4096) -Encoding UTF8
    Write-BoundedLogLine -Path $oversizedPath -Message "fresh bounded record" -MaxBytes 1024 -BackupCount 2
    $oversizedFiles = @(Get-ChildItem -LiteralPath $rotationRoot -File -Filter "oversized.log*")
    if (($oversizedFiles | Measure-Object Length -Maximum).Maximum -gt 1200) {
        throw "Pre-existing oversized log was retained above the configured bound."
    }

    $lockedPath = Join-Path $rotationRoot "locked.log"
    Set-Content -LiteralPath $lockedPath -Value ("y" * 128) -Encoding UTF8
    $lockedStream = [IO.File]::Open(
        $lockedPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::None)
    try {
        Write-BoundedLogLine -Path $lockedPath -Message "diagnostic write must not stop the worker" -MaxBytes 64 -BackupCount 2
    }
    finally {
        $lockedStream.Dispose()
    }
}
finally {
    Remove-Item -LiteralPath $rotationRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Runtime reliability checks completed."
