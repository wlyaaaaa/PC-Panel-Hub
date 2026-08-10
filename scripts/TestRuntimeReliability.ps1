param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath $Root).Path
$side = Join-Path $Root "tools\turzx_side_screen"
$streamSource = Get-Content -Raw -LiteralPath (Join-Path $side "TURZX.SideScreen.Stream.cs")
$senderSource = Get-Content -Raw -LiteralPath (Join-Path $side "TURZX.SideScreen.TurzxHelperSender.cs")
$streamStartPath = Join-Path $side "StartVideoStream.ps1"
$streamStart = Get-Content -Raw -LiteralPath $streamStartPath
$streamBackground = Get-Content -Raw -LiteralPath (Join-Path $side "StartVideoStreamBackground.ps1")
$stackStart = Get-Content -Raw -LiteralPath (Join-Path $side "StartSideScreenStack.ps1")
$watchdogStart = Get-Content -Raw -LiteralPath (Join-Path $side "StartSideScreenWatchdog.ps1")
$videoTest = Get-Content -Raw -LiteralPath (Join-Path $side "TestVideoStream.ps1")
$rendererTest = Get-Content -Raw -LiteralPath (Join-Path $side "TestRenderer.ps1")
$cadenceTestPath = Join-Path $side "TestStreamCadence.ps1"
$cadenceTest = Get-Content -Raw -LiteralPath $cadenceTestPath
$testEntry = Get-Content -Raw -LiteralPath (Join-Path $Root "scripts\test.ps1")
$runtimeCheck = Get-Content -Raw -LiteralPath (Join-Path $Root "scripts\check-runtime.ps1")
$startEntryPath = Join-Path $Root "scripts\start.ps1"
$startEntry = Get-Content -Raw -LiteralPath $startEntryPath

# Hybrid refresh is an explicit production mode.  It must not weaken the default
# command-200 path or turn the legacy -Diff experiment into an implicit default.
foreach ($entry in @(
    @{ Name = "stack"; Text = $stackStart },
    @{ Name = "watchdog"; Text = $watchdogStart },
    @{ Name = "stream launcher"; Text = $streamStart }
)) {
    foreach ($pattern in @(
        '[switch]$HybridRefresh',
        '[int]$DiffSendTimeoutMs = 900',
        '[int]$FullResyncEveryFrames = 900'
    )) {
        if ($entry.Text -notmatch [regex]::Escape($pattern)) {
            throw ("{0} missing explicit hybrid refresh contract: {1}" -f $entry.Name, $pattern)
        }
    }
}
foreach ($pattern in @(
    '-HybridRefresh:$HybridRefresh',
    '-DiffSendTimeoutMs $DiffSendTimeoutMs'
)) {
    if ($stackStart -notmatch [regex]::Escape($pattern)) {
        throw "Stack worker must preserve the hybrid option into StartVideoStream: $pattern"
    }
}

foreach ($entry in @(
    @{ Name = "stack to watchdog"; Text = $stackStart; Flag = '"-HybridRefresh"' },
    @{ Name = "watchdog to worker"; Text = $watchdogStart; Flag = '"-HybridRefresh"' },
    @{ Name = "stream launcher to C#"; Text = $streamStart; Flag = '"--hybrid-refresh"' }
)) {
    if ($entry.Text -notmatch [regex]::Escape($entry.Flag)) {
        throw ("HybridRefresh was not propagated through {0}." -f $entry.Name)
    }
}

foreach ($entry in @(
    @{ Name = "stack"; Text = $stackStart; Flag = '"-DiffSendTimeoutMs"' },
    @{ Name = "watchdog"; Text = $watchdogStart; Flag = '"-DiffSendTimeoutMs"' },
    @{ Name = "stream launcher"; Text = $streamStart; Flag = '"--diff-send-timeout-ms"' }
)) {
    if ($entry.Text -notmatch [regex]::Escape($entry.Flag)) {
        throw ("DiffSendTimeoutMs was not propagated through {0}." -f $entry.Name)
    }
}

foreach ($pattern in @(
    '--hybrid-refresh',
    '--diff-send-timeout-ms',
    'hybrid_diff_204_full_200',
    'DefaultDifferentialSendTimeoutMilliseconds = 900',
    'ResolveRefreshIntervalMilliseconds',
    'ResolveTransportMode'
)) {
    if ($streamSource -notmatch [regex]::Escape($pattern)) {
        throw "C# stream missing hybrid refresh contract: $pattern"
    }
}
foreach ($pattern in @(
    'public int FullResyncEveryFrames = DefaultHybridFullResyncEveryFrames;',
    'ResolveFullBaselineRepeatCount(options.HybridRefresh, hasPreviousFrame)',
    'ShouldPrimeFullBaseline(options.HybridRefresh, hasPreviousFrame)',
    'DefaultHybridFullResyncEveryFrames = 900'
)) {
    if ($streamSource -notmatch [regex]::Escape($pattern)) {
        throw "Hybrid startup baseline contract is missing: $pattern"
    }
}
if ($streamSource -notmatch 'SendDiffWithTimeout[\s\S]{0,500}DiffSendTimeoutMs') {
    throw "Command 204 must use its dedicated bounded send path and 900ms budget."
}
if ($senderSource -notmatch [regex]::Escape('SendDiffWithTimeout')) {
    throw "TURZX helper sender must expose a bounded command-204 send operation."
}
foreach ($pattern in @(
    '_abandoned = true;',
    'if (_abandoned)',
    'throw new InvalidOperationException(diffMessage);'
)) {
    if ($senderSource -notmatch [regex]::Escape($pattern) -and
        $streamSource -notmatch [regex]::Escape($pattern)) {
        throw "A command-204 timeout must abandon the blocked session and force worker exit: $pattern"
    }
}
if ($stackStart -notmatch [regex]::Escape('[int]$IntervalMs = 3000') -or
    $watchdogStart -notmatch [regex]::Escape('[int]$IntervalMs = 3000') -or
    $streamStart -notmatch [regex]::Escape('[int]$IntervalMs = 3000')) {
    throw "Default production refresh must remain verified full command 200 at 3000ms."
}

foreach ($pattern in @(
    'Get-ScheduledTask',
    'Test-ScheduledTaskActionMode'
)) {
    if ($startEntry -notmatch [regex]::Escape($pattern)) {
        throw "Start entrypoint must validate the registered task action before running it: $pattern"
    }
}
if ($startEntry -notmatch 'if\s*\(\s*-not\s+\$modeMatches\s*\)[\s\S]{0,4000}schtasks\.exe\s+/Run') {
    throw "Start entrypoint must branch on task action mode before invoking schtasks /Run."
}
foreach ($pattern in @(
    'StopSideScreenStack.ps1',
    '-IncludeWatchdog',
    '-SkipStackEntrypoint',
    '-Quiet'
)) {
    if ($startEntry -notmatch [regex]::Escape($pattern)) {
        throw "Scheduled-task restart must reclaim an orphaned hidden watchdog before relaunch: $pattern"
    }
}
$taskEndIndex = $startEntry.IndexOf('schtasks.exe /End', [StringComparison]::OrdinalIgnoreCase)
$watchdogStopIndex = $startEntry.IndexOf('-IncludeWatchdog', [StringComparison]::OrdinalIgnoreCase)
$taskRunIndex = $startEntry.IndexOf('schtasks.exe /Run', [StringComparison]::OrdinalIgnoreCase)
if ($taskEndIndex -lt 0 -or $watchdogStopIndex -le $taskEndIndex -or $taskRunIndex -le $watchdogStopIndex) {
    throw "Scheduled-task restart ordering must be /End -> exact watchdog stop -> /Run."
}

$startTokens = $null
$startParseErrors = $null
$startAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $startEntryPath,
    [ref]$startTokens,
    [ref]$startParseErrors)
if ($startParseErrors.Count -gt 0) {
    throw "Unable to parse scripts/start.ps1 for scheduled-task mode tests."
}
$taskModeFunction = $startAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq "Test-ScheduledTaskActionMode"
    },
    $true)
if ($null -eq $taskModeFunction) {
    throw "scripts/start.ps1 must define Test-ScheduledTaskActionMode."
}
. ([scriptblock]::Create($taskModeFunction.Extent.Text))

$modeCases = @(
    @{ Name = "full defaults match no flags"; Arguments = '-Root "E:\\repo"'; Hybrid = $false; Alt = $false; Expected = $true },
    @{ Name = "hybrid and alt flags match explicit request"; Arguments = '-HybridRefresh -AltHelper'; Hybrid = $true; Alt = $true; Expected = $true },
    @{ Name = "old full task cannot satisfy hybrid request"; Arguments = '-Root "E:\\repo"'; Hybrid = $true; Alt = $false; Expected = $false },
    @{ Name = "old hybrid task cannot satisfy full request"; Arguments = '-HybridRefresh'; Hybrid = $false; Alt = $false; Expected = $false },
    @{ Name = "alt helper mismatch is rejected independently"; Arguments = '-HybridRefresh'; Hybrid = $true; Alt = $true; Expected = $false },
    @{ Name = "negative flags never impersonate positive mode"; Arguments = '-NoHybridRefresh -NoAltHelper'; Hybrid = $false; Alt = $false; Expected = $true }
)
foreach ($case in $modeCases) {
    $actual = Test-ScheduledTaskActionMode `
        -Arguments $case.Arguments `
        -HybridRefreshEnabled $case.Hybrid `
        -AltHelperEnabled $case.Alt
    if ([bool]$actual -ne [bool]$case.Expected) {
        throw ("Scheduled-task action mode case failed: {0}; expected={1} actual={2}" -f $case.Name, $case.Expected, $actual)
    }
}

foreach ($pattern in @(
    "WriteFileAtomically",
    "File.Replace",
    "SavePreviewAtomically",
    "TryQueuePreview",
    "TryReservePreviewWorker",
    "ProcessPriorityClass.AboveNormal",
    "ThreadPriority.AboveNormal",
    "WriteHeartbeatCopies",
    '"send_attempted"',
    '"send_ms"',
    '"full_resync_every_frames"',
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

$unverifiedDiffRejected = $false
try {
    & $streamStartPath -Root $Root -Diff -Frames 1 2>&1 | Out-Null
}
catch {
    if ($_.Exception.Message -match "differential command 204 is unverified") {
        $unverifiedDiffRejected = $true
    }
    else {
        throw
    }
}
if (-not $unverifiedDiffRejected) {
    throw "Live differential transport must fail closed before touching COM7."
}

foreach ($pattern in @(
    '[int]$PreviewIntervalSeconds = 45',
    '[string]$PreviewDir',
    '"--preview-interval-seconds"',
    '[int]$SendTimeoutMs = 10000',
    '"--send-timeout-ms"',
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

$sendIndex = $streamSource.IndexOf("TurzxHelperSender.SendBitmap", [StringComparison]::Ordinal)
$previewIndex = $streamSource.IndexOf("TryQueuePreview", [StringComparison]::Ordinal)
if ($sendIndex -lt 0 -or $previewIndex -lt 0 -or $previewIndex -lt $sendIndex) {
    throw "Diagnostic preview must be queued only after the primary full-frame send path."
}

if ($stackStart -match '(?m)&\s+\$streamScript[^\r\n]*\s-Diff(?:\s|$)') {
    throw "Production stack must use the verified full-frame transport; differential command 204 remains experimental."
}
foreach ($pattern in @(
    '"-SendTimeoutMs", [string]$SendTimeoutMs',
    '"-MaxConsecutiveSendFailures", [string]$MaxConsecutiveSendFailures'
)) {
    if ($stackStart -notmatch [regex]::Escape($pattern)) {
        throw "Top-level stack delegation must preserve the configured recovery boundary: $pattern"
    }
}
if ($streamBackground -match 'if\s*\(\s*!\$NoDiff\s*\)') {
    throw "Background launcher must not enable differential command 204 by default."
}
$experimentalDiffGate = [regex]::Escape('if ($ExperimentalDiff -and !$NoDiff)') +
    '[\s\S]{0,200}' +
    [regex]::Escape('$arguments += @("-Diff", "-AllowUnverifiedDifferentialProtocol")')
if ($streamBackground -notmatch $experimentalDiffGate) {
    throw "Background differential command 204 must be gated by ExperimentalDiff and its explicit live opt-in."
}
if (([regex]::Matches($streamBackground, [regex]::Escape('"-Diff"'))).Count -ne 1) {
    throw "Background launcher must have exactly one differential argument insertion point."
}
foreach ($pattern in @('ExperimentalDiff', 'AllowUnverifiedDifferentialProtocol')) {
    if ($streamBackground -notmatch [regex]::Escape($pattern)) {
        throw "Background differential transport must remain explicitly experimental: $pattern"
    }
}
foreach ($pattern in @(
    'transport_mode',
    'verified_full_200',
    'AllowUnverifiedDifferentialProtocol'
)) {
    if ($streamSource -notmatch [regex]::Escape($pattern) -and
        $streamStart -notmatch [regex]::Escape($pattern)) {
        throw "Verified transport guard is missing: $pattern"
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
    "transport_mode",
    "verified_full_200",
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
$HeartbeatMaxSendMs = 10000
$HeartbeatMaxElapsedMs = 12000
$HeartbeatMaxPeriodMs = 15000
$DiffSendTimeoutMs = 900
$FullResyncEveryFrames = 900
$HybridRefresh = $false
try {
    Set-Content -LiteralPath $slotBHeartbeat -Value '{"frame":41,"status":"ok","error":null,"transport_mode":"verified_full_200","send_attempted":true,"send_ms":2300,"elapsed_ms":2400,"period_ms":3000}' -Encoding UTF8
    (Get-Item -LiteralPath $slotBHeartbeat).LastWriteTimeUtc = [DateTime]::UtcNow.AddSeconds(-1)
    Set-Content -LiteralPath $legacyHeartbeat -Value '{"frame":40,"status":"ok","error":null,"transport_mode":"verified_full_200"}' -Encoding UTF8
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
    # Keep subsequent fixtures deterministic even on filesystems whose write-time
    # resolution makes two immediate writes compare equal.
    (Get-Item -LiteralPath $legacyHeartbeat).LastWriteTimeUtc = [DateTime]::UtcNow.AddSeconds(-5)

    Set-Content -LiteralPath $slotBHeartbeat -Value '{"frame":42,"status":"ok","error":null,"transport_mode":"verified_full_200","send_attempted":false,"send_ms":0,"elapsed_ms":20,"period_ms":3000}' -Encoding UTF8
    (Get-Item -LiteralPath $slotBHeartbeat).LastWriteTimeUtc = [DateTime]::UtcNow
    $noSendHealth = Get-StreamHeartbeatHealth
    if ($noSendHealth.Healthy -or $noSendHealth.Reason -notmatch "send-not-attempted") {
        throw "Watchdog must not accept a dry-run heartbeat that never wrote to COM7."
    }

    Set-Content -LiteralPath $slotBHeartbeat -Value '{"frame":42,"status":"ok","error":null,"transport_mode":"verified_full_200","send_attempted":true,"send_ms":-1,"elapsed_ms":2400,"period_ms":3000}' -Encoding UTF8
    (Get-Item -LiteralPath $slotBHeartbeat).LastWriteTimeUtc = [DateTime]::UtcNow
    $negativeTimingHealth = Get-StreamHeartbeatHealth
    if ($negativeTimingHealth.Healthy -or $negativeTimingHealth.Reason -notmatch "timing-invalid") {
        throw "Watchdog must reject negative timing values instead of bypassing upper bounds."
    }

    Set-Content -LiteralPath $slotBHeartbeat -Value '{"frame":42,"status":"ok","error":null,"transport_mode":"experimental_diff_204"}' -Encoding UTF8
    (Get-Item -LiteralPath $slotBHeartbeat).LastWriteTimeUtc = [DateTime]::UtcNow
    $experimentalHealth = Get-StreamHeartbeatHealth
    if ($experimentalHealth.Healthy -or $experimentalHealth.Reason -notmatch "transport-unverified") {
        throw "Watchdog must reject an experimental differential transport heartbeat."
    }

    Set-Content -LiteralPath $slotBHeartbeat -Value '{"frame":43,"status":"ok","error":null,"transport_mode":"hybrid_diff_204_full_200","frame_transport":"diff_204","last_full_frame":1,"full_resync_every_frames":900,"send_attempted":true,"send_ms":40,"elapsed_ms":80,"period_ms":1000}' -Encoding UTF8
    (Get-Item -LiteralPath $slotBHeartbeat).LastWriteTimeUtc = [DateTime]::UtcNow
    $hybridDisabledHealth = Get-StreamHeartbeatHealth
    if ($hybridDisabledHealth.Healthy -or $hybridDisabledHealth.Reason -notmatch "transport-unverified") {
        throw "Watchdog must reject a hybrid heartbeat unless HybridRefresh was explicitly enabled."
    }

    $HybridRefresh = $true
    $hybridEnabledHealth = Get-StreamHeartbeatHealth
    if (-not $hybridEnabledHealth.Healthy) {
        throw "Watchdog must accept a valid hybrid heartbeat when HybridRefresh is explicit: $($hybridEnabledHealth.Reason)"
    }

    Set-Content -LiteralPath $slotBHeartbeat -Value '{"frame":44,"status":"ok","error":null,"transport_mode":"hybrid_diff_204_full_200","frame_transport":"diff_204","last_full_frame":1,"full_resync_every_frames":900,"send_attempted":true,"send_ms":901,"elapsed_ms":940,"period_ms":1000}' -Encoding UTF8
    (Get-Item -LiteralPath $slotBHeartbeat).LastWriteTimeUtc = [DateTime]::UtcNow
    $hybridDiffOverrunHealth = Get-StreamHeartbeatHealth
    if ($hybridDiffOverrunHealth.Healthy -or $hybridDiffOverrunHealth.Reason -notmatch "send-overrun") {
        throw "Watchdog must apply the dedicated 900ms budget to a hybrid command-204 frame."
    }

    Set-Content -LiteralPath $slotBHeartbeat -Value '{"frame":45,"status":"ok","error":null,"transport_mode":"hybrid_diff_204_full_200","frame_transport":"full_200","last_full_frame":45,"full_resync_every_frames":900,"send_attempted":true,"send_ms":4600,"elapsed_ms":4800,"period_ms":5000}' -Encoding UTF8
    (Get-Item -LiteralPath $slotBHeartbeat).LastWriteTimeUtc = [DateTime]::UtcNow
    $hybridBaselineHealth = Get-StreamHeartbeatHealth
    if (-not $hybridBaselineHealth.Healthy) {
        throw "Watchdog must allow a bounded hybrid startup/recovery command-200 baseline: $($hybridBaselineHealth.Reason)"
    }

    Set-Content -LiteralPath $slotBHeartbeat -Value '{"frame":1800,"status":"ok","error":null,"transport_mode":"hybrid_diff_204_full_200","frame_transport":"diff_204","last_full_frame":900,"full_resync_every_frames":900,"send_attempted":true,"send_ms":40,"elapsed_ms":80,"period_ms":1000}' -Encoding UTF8
    (Get-Item -LiteralPath $slotBHeartbeat).LastWriteTimeUtc = [DateTime]::UtcNow
    $hybridResyncOverdueHealth = Get-StreamHeartbeatHealth
    if ($hybridResyncOverdueHealth.Healthy -or $hybridResyncOverdueHealth.Reason -notmatch "full-resync-overdue") {
        throw "Watchdog must restart a hybrid stream that silently stopped producing full recovery frames."
    }

    Set-Content -LiteralPath $slotBHeartbeat -Value '{"frame":46,"status":"ok","error":null,"transport_mode":"hybrid_diff_204_full_200","frame_transport":"diff_204","last_full_frame":45,"full_resync_every_frames":0,"send_attempted":true,"send_ms":40,"elapsed_ms":80,"period_ms":1000}' -Encoding UTF8
    (Get-Item -LiteralPath $slotBHeartbeat).LastWriteTimeUtc = [DateTime]::UtcNow
    $hybridResyncDisabledHealth = Get-StreamHeartbeatHealth
    if ($hybridResyncDisabledHealth.Healthy -or $hybridResyncDisabledHealth.Reason -notmatch "full-resync-config-mismatch") {
        throw "Watchdog must reject a hybrid worker that silently disabled periodic panel recovery."
    }
    $HybridRefresh = $false

    Set-Content -LiteralPath $slotBHeartbeat -Value '{"frame":43,"status":"ok","error":null,"transport_mode":"verified_full_200","send_attempted":true,"send_ms":10001,"elapsed_ms":10100,"period_ms":3000}' -Encoding UTF8
    (Get-Item -LiteralPath $slotBHeartbeat).LastWriteTimeUtc = [DateTime]::UtcNow
    $slowSendHealth = Get-StreamHeartbeatHealth
    if ($slowSendHealth.Healthy -or $slowSendHealth.Reason -notmatch "send-overrun") {
        throw "Watchdog must reject a full-frame send that exceeded the bounded send budget."
    }

    Set-Content -LiteralPath $slotBHeartbeat -Value '{"frame":44,"status":"ok","error":null,"transport_mode":"verified_full_200","send_attempted":true,"send_ms":2300,"elapsed_ms":12001,"period_ms":15001}' -Encoding UTF8
    (Get-Item -LiteralPath $slotBHeartbeat).LastWriteTimeUtc = [DateTime]::UtcNow
    $slowFrameHealth = Get-StreamHeartbeatHealth
    if ($slowFrameHealth.Healthy -or $slowFrameHealth.Reason -notmatch "frame-overrun") {
        throw "Watchdog must reject a frame loop that was starved beyond its timing budget."
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
