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
$installerEntry = Get-Content -Raw -LiteralPath (Join-Path $Root "scripts\install-startup-admin.ps1")
$fastRepairPath = Join-Path $Root "scripts\repair-panel.ps1"
$fastRepair = if (Test-Path -LiteralPath $fastRepairPath) { Get-Content -Raw -LiteralPath $fastRepairPath } else { '' }

# Hybrid refresh is the installed one-second production mode.  It must remain
# distinct from the legacy -Diff experiment and keep command 200 as a bounded
# startup/recovery baseline and explicit compatibility fallback.
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
    'DefaultHybridFullResyncEveryFrames = 900',
    'DefaultHybridWarmupFullResyncEveryFrames = 60',
    'DefaultHybridWarmupFullResyncUntilFrame = 180',
    'ShouldSendHybridWarmupFullFrame(frame, options.HybridRefresh, hasPreviousFrame)'
)) {
    if ($streamSource -notmatch [regex]::Escape($pattern)) {
        throw "Hybrid startup baseline contract is missing: $pattern"
    }
}
if ($streamSource -notmatch 'SendDiffWithTimeout[\s\S]{0,500}DiffSendTimeoutMs') {
    throw "Command 204 must use its dedicated bounded send path and 900ms budget."
}
foreach ($pattern in @(
    'HttpCompletionOption.ResponseContentRead',
    'RunWithHardDeadline<Snapshot>',
    'ManualResetEventSlim(false)',
    'Thread cancellationWorker',
    'work.Completed.Wait(timeoutMs)',
    'Timeout.InfiniteTimeSpan',
    'FetchSnapshotForTest',
    'RunHardDeadlineProbeForTest',
    'MaxResponseContentBufferSize = DefaultMaxMetricsPayloadBytes',
    'DefaultHttpTimeoutMs = 750',
    'DefaultHttpTimeoutMillisecondsForTest'
)) {
    if ($streamSource -notmatch [regex]::Escape($pattern)) {
        throw "Metrics fetch must enforce one total response deadline: $pattern"
    }
}
if ($streamSource -match 'serializer\.ReadObject\(response\.GetResponseStream\(\)\)' -or
    $streamSource -match 'HttpWebRequest') {
    throw "Metrics JSON must not deserialize directly from an unbounded network stream."
}
if ($streamStart -notmatch [regex]::Escape('/r:System.Net.Http.dll')) {
    throw "The stream compiler must reference System.Net.Http.dll for bounded metrics fetches."
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
    throw "Explicit full-frame compatibility fallback must remain command 200 at 3000ms."
}
if ($installerEntry -notmatch [regex]::Escape('[switch]$HybridRefresh = $true')) {
    throw "Installed production task must default to one-second HybridRefresh."
}
if ($startEntry -notmatch [regex]::Escape('[switch]$HybridRefresh = $true')) {
    throw "Manual production start must default to one-second HybridRefresh."
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

# A recoverable child/start failure must remain inside the long-lived watchdog.
# The 2026-08-15 incident froze the panel for hours because the first retry
# threw out of the main loop and the scheduled task did not relaunch it.
$restartTokens = $null
$restartParseErrors = $null
$restartAst = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $side "StartSideScreenWatchdog.ps1"),
    [ref]$restartTokens,
    [ref]$restartParseErrors)
if ($restartParseErrors.Count -gt 0) {
    throw "Unable to parse watchdog script for contained restart behavior."
}
$restartFunction = $restartAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq "Invoke-TurzxStackRestartAttempt"
    },
    $true)
if ($null -eq $restartFunction) {
    throw "Watchdog must define Invoke-TurzxStackRestartAttempt."
}
. ([scriptblock]::Create($restartFunction.Extent.Text))
$script:restartAttemptCalls = 0
function Start-Stack {
    $script:restartAttemptCalls++
    if ($script:restartAttemptCalls -eq 1) { throw "synthetic start failure" }
    return [pscustomobject]@{ Id = 4242; HasExited = $false }
}
function Write-WatchdogLog { param([string]$Message) }
function Start-Sleep { param([int]$Seconds, [int]$Milliseconds) }
$failedAttempt = Invoke-TurzxStackRestartAttempt -Reason "test-first"
if ($failedAttempt.Succeeded -or $null -ne $failedAttempt.Child -or $failedAttempt.Error -notmatch "synthetic start failure") {
    throw "A failed stack restart must return a contained failure receipt without escaping the watchdog."
}
$successfulAttempt = Invoke-TurzxStackRestartAttempt -Reason "test-second"
if (-not $successfulAttempt.Succeeded -or $successfulAttempt.Child.Id -ne 4242) {
    throw "A later stack restart must recover after the contained failure."
}
$mainLoopStart = $watchdogStart.IndexOf(
    '$child = Set-TurzxChildHeartbeatStartupWindow -Child $null',
    [StringComparison]::Ordinal)
$mainLoopEnd = $watchdogStart.IndexOf('finally {', $mainLoopStart, [StringComparison]::Ordinal)
if ($mainLoopStart -lt 0 -or $mainLoopEnd -le $mainLoopStart) {
    throw "Unable to locate watchdog main loop for restart containment checks."
}
$mainLoopText = $watchdogStart.Substring($mainLoopStart, $mainLoopEnd - $mainLoopStart)
if ($mainLoopText -match '(?m)^\s*\$child\s*=\s*Start-Stack\s+-Reason') {
    throw "Watchdog main loop must not call Start-Stack without the contained restart adapter."
}
foreach ($pattern in @(
    'if ($null -eq $child)',
    'Invoke-TurzxStackRestartAttempt',
    'stack restart deferred'
)) {
    if ($watchdogStart -notmatch [regex]::Escape($pattern)) {
        throw "Watchdog missing unattended restart containment contract: $pattern"
    }
}

# A stalled local CIM provider previously left the watchdog blocked inside
# pre-start for minutes.  Both the process enumeration and the watchdog's
# child StopSideScreenStack invocation must now have independently testable
# bounds, so an unavailable provider cannot turn into an unbounded restart.
$stopTokens = $null
$stopParseErrors = $null
$stopAst = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $side "StopSideScreenStack.ps1"),
    [ref]$stopTokens,
    [ref]$stopParseErrors)
if ($stopParseErrors.Count -gt 0) {
    throw "Unable to parse StopSideScreenStack.ps1 for bounded recovery behavior."
}
$processSnapshotFunction = $stopAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq "Get-TurzxProcessSnapshot"
    },
    $true)
if ($null -eq $processSnapshotFunction) {
    throw "StopSideScreenStack must define Get-TurzxProcessSnapshot."
}
if ($processSnapshotFunction.Extent.Text -notmatch [regex]::Escape('-OperationTimeoutSec $TimeoutSeconds')) {
    throw "StopSideScreenStack must pass its bounded timeout to Get-CimInstance."
}
& {
    $script:capturedCimTimeoutSeconds = $null
    function Write-StopLog { param([string]$Message) }
    function Get-CimInstance {
        [CmdletBinding()]
        param(
            [string]$ClassName,
            [uint32]$OperationTimeoutSec
        )
        $script:capturedCimTimeoutSeconds = $OperationTimeoutSec
        throw "synthetic CIM provider timeout"
    }

    . ([scriptblock]::Create($processSnapshotFunction.Extent.Text))
    $snapshot = @(Get-TurzxProcessSnapshot -TimeoutSeconds 7)
    if ($snapshot.Count -ne 0 -or $script:capturedCimTimeoutSeconds -ne 7) {
        throw "A failed bounded process snapshot must fall back to an empty safe snapshot."
    }
}

$watchdogStopFunction = $restartAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq "Stop-Stack"
    },
    $true)
if ($null -eq $watchdogStopFunction) {
    throw "Watchdog must define Stop-Stack."
}
foreach ($pattern in @(
    '[int]$StopStackTimeoutSeconds',
    'WaitForExit',
    'Stop-Process',
    '$script:turzxStackChild',
    'StopSideScreenStack exceeded strict total timeout'
)) {
    if ($watchdogStart -notmatch [regex]::Escape($pattern)) {
        throw "Watchdog Stop-Stack missing strict total timeout contract: $pattern"
    }
}
& {
    $script:stopWaitMilliseconds = $null
    $script:forcedStopIds = @()
    $fakeStopProcess = [pscustomobject]@{
        Id = 7171
        HasExited = $false
        ExitCode = -1
    }
    $fakeStopProcess | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {
        param([int]$Milliseconds)
        $script:stopWaitMilliseconds = $Milliseconds
        return $false
    }
    function Write-WatchdogLog { param([string]$Message) }
    function Start-Process {
        [CmdletBinding()]
        param(
            [string]$FilePath,
            [object]$ArgumentList,
            [object]$WindowStyle,
            [switch]$PassThru
        )
        return $fakeStopProcess
    }
    function Stop-Process {
        [CmdletBinding()]
        param([int]$Id, [switch]$Force)
        $script:forcedStopIds += $Id
    }

    $Root = 'C:\synthetic-turzx'
    $stopScript = 'C:\synthetic-turzx\StopSideScreenStack.ps1'
    $StopProcessSnapshotTimeoutSeconds = 7
    $StopStackTimeoutSeconds = 9
    $script:turzxStackChild = [pscustomobject]@{ Id = 4242; HasExited = $false }
    . ([scriptblock]::Create($watchdogStopFunction.Extent.Text))
    $timeoutError = $null
    try {
        Stop-Stack -Reason 'synthetic-timeout'
    }
    catch {
        $timeoutError = $_.Exception.Message
    }
    if ($script:stopWaitMilliseconds -ne 9000 -or
        $script:forcedStopIds -notcontains 4242 -or
        $script:forcedStopIds -notcontains 7171 -or
        $timeoutError -notmatch 'strict total timeout') {
        throw "Watchdog must stop the tracked child and force-stop a stalled StopSideScreenStack child at its strict total deadline."
    }
}

# A freshly launched child has one monotonic window to produce its first
# heartbeat.  If no heartbeat ever appears, take the circuit path directly
# instead of clearing the slots repeatedly through rapid ordinary retries.
$monotonicClockFunction = $restartAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq "Get-TurzxMonotonicMilliseconds"
    },
    $true)
$startupWindowFunction = $restartAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq "Set-TurzxChildHeartbeatStartupWindow"
    },
    $true)
$startupDeadlineFunction = $restartAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq "Test-TurzxChildHeartbeatStartupDeadline"
    },
    $true)
if ($null -eq $monotonicClockFunction -or
    $null -eq $startupWindowFunction -or
    $null -eq $startupDeadlineFunction) {
    throw "Watchdog must define the monotonic startup-heartbeat deadline helpers."
}
& {
    # This script is exercised by both powershell.exe 5.1 and pwsh.exe 7.
    # Executing the real helper here prevents a PS7-only clock API from being
    # deployed into the Windows PowerShell scheduled-task owner again.
    . ([scriptblock]::Create($monotonicClockFunction.Extent.Text))
    $firstMonotonicMilliseconds = Get-TurzxMonotonicMilliseconds
    Start-Sleep -Milliseconds 2
    $secondMonotonicMilliseconds = Get-TurzxMonotonicMilliseconds
    if ($firstMonotonicMilliseconds -isnot [int64] -or
        $secondMonotonicMilliseconds -lt $firstMonotonicMilliseconds) {
        throw "Monotonic clock must execute and advance safely on the current PowerShell runtime."
    }
}
& {
    $script:syntheticMonotonicMilliseconds = 1000
    function Get-TurzxMonotonicMilliseconds {
        return $script:syntheticMonotonicMilliseconds
    }

    $HeartbeatStartupGraceSeconds = 60
    . ([scriptblock]::Create($startupWindowFunction.Extent.Text))
    . ([scriptblock]::Create($startupDeadlineFunction.Extent.Text))
    Set-TurzxChildHeartbeatStartupWindow -Child ([pscustomobject]@{ Id = 4242 }) | Out-Null
    if ($script:childHeartbeatStartupDeadlineMilliseconds -ne 61000) {
        throw "Startup heartbeat deadline must be based on a monotonic clock."
    }
    $script:syntheticMonotonicMilliseconds = 60999
    if (Test-TurzxChildHeartbeatStartupDeadline) {
        throw "Startup heartbeat deadline fired before the monotonic grace window elapsed."
    }
    $script:syntheticMonotonicMilliseconds = 61000
    if (-not (Test-TurzxChildHeartbeatStartupDeadline)) {
        throw "Startup heartbeat deadline did not fire when the monotonic grace window elapsed."
    }
}
foreach ($pattern in @(
    'Test-TurzxChildHeartbeatStartupDeadline',
    'heartbeat-missing-startup',
    'Invoke-TurzxFailureCircuitBreaker'
)) {
    if ($watchdogStart -notmatch [regex]::Escape($pattern)) {
        throw "Watchdog main loop missing startup heartbeat convergence contract: $pattern"
    }
}

foreach ($pattern in @(
    '[switch]$HybridRefresh = $true',
    'scripts\start.ps1',
    'Get-ScheduledTask',
    'TURZX.SideScreen.Stream',
    'hybrid_diff_204_full_200',
    'period_ms',
    'repair-panel healthy'
)) {
    if ($fastRepair -notmatch [regex]::Escape($pattern)) {
        throw "Fast panel repair entrypoint missing exact 1 Hz recovery contract: $pattern"
    }
}
foreach ($pattern in @(
    '[int]$WaitSeconds = 120',
    'side-screen-watchdog.pid',
    'side-screen-stack-child.pid',
    'restart-on-start.flag',
    'StartSideScreenWatchdog-Hidden.vbs',
    '$liveWatchdogOwner',
    '$repairRequestedUtc',
    '$priorStackChildPid',
    '$postRepairStackOwner',
    '$restartRequestAcknowledged',
    '$utc.ToUniversalTime() -gt $repairRequestedUtc',
    'Requested in-place stack recycle from live watchdog',
    'HS2/display ownership preserved'
)) {
    if ($fastRepair -notmatch [regex]::Escape($pattern)) {
        throw "Fast panel repair must prefer the live watchdog owner without disturbing HS2: $pattern"
    }
}
$liveRecycleIndex = $fastRepair.IndexOf('if ($liveWatchdogOwner)', [StringComparison]::Ordinal)
$fullRestartIndex = $fastRepair.IndexOf('& pwsh.exe', [StringComparison]::OrdinalIgnoreCase)
if ($liveRecycleIndex -lt 0 -or $fullRestartIndex -le $liveRecycleIndex) {
    throw "Fast panel repair must try the in-place watchdog recycle before a full watchdog restart."
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

$requestedModeFunction = $startAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq "Resolve-RequestedSwitchMode"
    },
    $true)
if ($null -eq $requestedModeFunction) {
    throw "scripts/start.ps1 must preserve an explicitly installed task mode when the caller omits mode switches."
}
. ([scriptblock]::Create($requestedModeFunction.Extent.Text))

$requestedModeCases = @(
    @{ Name = "omitted Hybrid adopts registered Hybrid"; Arguments = '-HybridRefresh'; Flag = 'HybridRefresh'; Requested = $false; Explicit = $false; Expected = $true },
    @{ Name = "omitted Hybrid keeps registered full mode"; Arguments = '-Root "E:\repo"'; Flag = 'HybridRefresh'; Requested = $false; Explicit = $false; Expected = $false },
    @{ Name = "explicit false overrides registered Hybrid"; Arguments = '-HybridRefresh'; Flag = 'HybridRefresh'; Requested = $false; Explicit = $true; Expected = $false },
    @{ Name = "explicit true overrides registered full mode"; Arguments = '-Root "E:\repo"'; Flag = 'HybridRefresh'; Requested = $true; Explicit = $true; Expected = $true },
    @{ Name = "omitted Alt adopts registered Alt"; Arguments = '-HybridRefresh -AltHelper'; Flag = 'AltHelper'; Requested = $false; Explicit = $false; Expected = $true }
)
foreach ($case in $requestedModeCases) {
    $actual = Resolve-RequestedSwitchMode `
        -Arguments $case.Arguments `
        -FlagName $case.Flag `
        -RequestedValue $case.Requested `
        -WasExplicit $case.Explicit
    if ([bool]$actual -ne [bool]$case.Expected) {
        throw ("Requested task mode case failed: {0}; expected={1} actual={2}" -f $case.Name, $case.Expected, $actual)
    }
}

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

$snapshotDecisionFunction = $watchdogAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq "Get-TurzxSnapshotStaleDecision"
    },
    $true)
if ($null -eq $snapshotDecisionFunction) {
    throw "Watchdog must define Get-TurzxSnapshotStaleDecision."
}
. ([scriptblock]::Create($snapshotDecisionFunction.Extent.Text))

$freshSnapshotDecision = Get-TurzxSnapshotStaleDecision `
    -SnapshotStatus "fresh" `
    -ConsecutiveFailures 4 `
    -Threshold 5
if ($freshSnapshotDecision.IsStale -or
    $freshSnapshotDecision.ConsecutiveFailures -ne 0 -or
    $freshSnapshotDecision.FailureThresholdReached) {
    throw "A fresh snapshot must clear the consecutive stale counter without recovery."
}
$firstStaleSnapshotDecision = Get-TurzxSnapshotStaleDecision `
    -SnapshotStatus "stale:TimeoutException" `
    -ConsecutiveFailures 0 `
    -Threshold 5
if (-not $firstStaleSnapshotDecision.IsStale -or
    $firstStaleSnapshotDecision.ConsecutiveFailures -ne 1 -or
    $firstStaleSnapshotDecision.FailureThresholdReached) {
    throw "One snapshot timeout must be tolerated without opening recovery."
}
$fourthStaleSnapshotDecision = Get-TurzxSnapshotStaleDecision `
    -SnapshotStatus "stale:TimeoutException" `
    -ConsecutiveFailures 3 `
    -Threshold 5
if ($fourthStaleSnapshotDecision.FailureThresholdReached) {
    throw "Snapshot recovery must remain closed below its consecutive threshold."
}
$fifthStaleSnapshotDecision = Get-TurzxSnapshotStaleDecision `
    -SnapshotStatus "empty:TimeoutException" `
    -ConsecutiveFailures 4 `
    -Threshold 5
if (-not $fifthStaleSnapshotDecision.FailureThresholdReached -or
    $fifthStaleSnapshotDecision.ConsecutiveFailures -ne 5) {
    throw "A sustained empty/timeout snapshot sequence must reach the bounded recovery threshold."
}
foreach ($pattern in @(
    'MaxConsecutiveSnapshotStaleHeartbeats',
    '$snapshotStaleHeartbeats',
    'Get-TurzxSnapshotStaleDecision',
    'FailureThresholdReached',
    'snapshot stale tolerated',
    'snapshot stale threshold reached'
)) {
    if ($watchdogStart -notmatch [regex]::Escape($pattern)) {
        throw "Watchdog missing bounded snapshot stale recovery contract: $pattern"
    }
}

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
    Set-Content -LiteralPath $slotBHeartbeat -Value '{"frame":41,"status":"ok","snapshot_status":"stale:TimeoutException","error":null,"transport_mode":"verified_full_200","send_attempted":true,"send_ms":40,"elapsed_ms":80,"period_ms":3000}' -Encoding UTF8
    (Get-Item -LiteralPath $slotBHeartbeat).LastWriteTimeUtc = [DateTime]::UtcNow
    $staleSnapshotHealth = Get-StreamHeartbeatHealth
    if (-not $staleSnapshotHealth.Healthy -or
        $staleSnapshotHealth.SnapshotStatus -ne "stale:TimeoutException") {
        throw "Watchdog must expose a stale snapshot status for the bounded decision layer."
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
