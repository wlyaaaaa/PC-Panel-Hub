param([string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$side = Join-Path $Root 'tools\turzx_side_screen'
$tokens = $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $side 'StartSideScreenWatchdog.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Unable to parse the production watchdog.' }
$loop = $ast.Find({ param($node) $node -is [Management.Automation.Language.WhileStatementAst] -and
    'Invoke-WallpaperEngineDisplayRecovery' -cin $node.Body.Statements.Extent.Text }, $true)
$statements = @($loop.Body.Statements.Extent.Text)
$afterHs2 = [Array]::IndexOf($statements, 'Invoke-WallpaperEngineDisplayRecovery') + 1
if (-not $afterHs2) { throw 'Missing the watchdog polling loop recovery block.' }
# Run the real TURZX portion of a normal poll without starting the watchdog.
$poll = [scriptblock]::Create(($statements[$afterHs2..($statements.Count - 1)] -join "`n"))
. (Join-Path $side 'SideScreenWatchdogPolicy.ps1')
. (Join-Path $side 'PanelDevicePolicy.ps1')
$functions = @('Get-StreamHeartbeatHealth', 'Start-Stack', 'Invoke-TurzxStackRestartAttempt', 'Set-TurzxChildHeartbeatStartupWindow', 'Get-TurzxMonotonicMilliseconds',
    'Test-TurzxChildHeartbeatStartupDeadline', 'Get-TurzxWatchdogHeartbeat', 'Test-TurzxStreamRecovered', 'Invoke-TurzxFailureCircuitBreaker', 'Get-TurzxSnapshotStaleDecision',
    'Invoke-TurzxSerialFaultRecovery', 'Invoke-TurzxSerialEndpointRecovery', 'Get-TurzxExactSerialEndpoint', 'Wait-TurzxExactSerialEndpointHealthy')
foreach ($definition in $ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -in $functions }) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
$actions = [Collections.Generic.List[string]]::new()
$Port = 'COM241'
$instanceId = 'USB\VID_0525&PID_A4A7\TEST-ONLY'
function Write-WatchdogLog { param($Message) }
function Start-Sleep { param($Seconds, $Milliseconds)
    if ($Seconds -notin @(3, 30)) { throw 'Unexpected recovery delay' }
    if ($script:frameDuringDelay) { Publish-TestFrame $script:frameDuringDelay; $script:frameDuringDelay = 0 }
}
function Stop-Stack { param($Reason, [switch]$KeepMetrics) $actions.Add('stop-stack') }
function Set-TurzxPanelBrightness { param($Brightness) $actions.Add('brightness'); return $false }
function Get-Process { [CmdletBinding()]param($Name) }
function Get-CimInstance { [CmdletBinding()]param($ClassName) [pscustomobject]@{ DeviceID = $Port; PNPDeviceID = $instanceId } }
function Get-PnpDevice { [CmdletBinding()]param($InstanceId) $actions.Add('verify-endpoint')
    [pscustomobject]@{ Present = $true; Status = 'OK'; ConfigManagerErrorCode = $case.ConfigCode }
}
function Invoke-TurzxExactSerialEndpointRestart { param($InstanceId) $actions.Add("restart:$InstanceId") }
function Start-HiddenProcess {
    param($FilePath, $ArgumentList)
    if ($ArgumentList) { $actions.Add($(if ('-Worker' -in $ArgumentList -and '-StreamOnly' -in $ArgumentList) { 'launch-stream' } else { 'launch-stack' })) }
    $worker = [pscustomobject]@{ Id = 8123; HasExited = $true; ExitCode = 1 }
    $worker | Add-Member ScriptMethod WaitForExit { param($Milliseconds) $actions.Add('exit-writer'); $this.HasExited = $true; return $true }
    return $worker
}
$heartbeatPaths = @(Join-Path $PSScriptRoot ('.serial-replay-' + [Guid]::NewGuid().ToString('N')))
$childPidPath = "$($heartbeatPaths[0]).pid"; $stackScript = 'synthetic-stack.ps1'; $HybridRefresh = [switch]$true
$stackStdoutPath = "$($heartbeatPaths[0]).stages"; $pausedPath = "$($heartbeatPaths[0]).paused"
$turzxSuspended = $script:turzxForceRestart = $false; $script:frameDuringDelay = 0; $FailureCircuitBreakerSeconds = 30
$HeartbeatMaxSendMs = 10000; $HeartbeatMaxElapsedMs = 12000; $HeartbeatMaxPeriodMs = 15000
$MaxConsecutiveHeartbeatFailures = 1; $MaxConsecutiveSnapshotStaleHeartbeats = 5
function Get-TurzxMonotonicMilliseconds { $script:testClockMs }
$script:testClockMs = 1000L
function Publish-TestFrame([int]$Frame, [switch]$Unresponsive) {
    $record = @{ frame = $Frame; status = 'ok'; snapshot_status = 'fresh'; utc = [DateTime]::UtcNow.ToString('o'); port = $Port;
       send_attempted = $true; transport_mode = 'hybrid_diff_204_full_200'; frame_transport = 'full_200'; last_full_frame = $Frame;
       full_resync_every_frames = 900; send_ms = 20; elapsed_ms = 40; period_ms = 1000 }
    if ($Unresponsive) { $record.status = 'fatal'; $record.serial_fault = 'endpoint-unresponsive' }
    $record | ConvertTo-Json -Compress | Set-Content -LiteralPath $heartbeatPaths[0] -Encoding ASCII
}
$HeartbeatStartupGraceSeconds = 150; $HeartbeatStaleSeconds = 15; $MaxConsecutiveFailures = 3
$IntervalMs = 1000; $SendTimeoutMs = 10000; $DiffSendTimeoutMs = 900; $MaxConsecutiveSendFailures = 1; $FullResyncEveryFrames = 900
$ActiveBrightness = 170; $PreviewIntervalSeconds = 45; $MaxStackLogBytes = 1048576; $MaxStreamLogBytes = 5242880; $LogBackupCount = 3
$TurzxSerialRecoveryFailureThreshold = 3; $TurzxSerialRecoveryRetrySeconds = 300; $TurzxSerialRecoveryWaitSeconds = 20
$script:turzxBrightnessConsecutiveFailures = $heartbeatFailures = $snapshotStaleHeartbeats = 0
$child = Set-TurzxChildHeartbeatStartupWindow -Child (Start-HiddenProcess)
$cases = @(
    @{ Name = 'foreign port'; Port = 'COM242'; Offset = 4; ConfigCode = 0; Expected = 'stop-stack, brightness, launch-stack' }
    @{ Name = 'previous worker'; Port = $Port; Offset = -1; ConfigCode = 0; Expected = 'stop-stack, brightness, launch-stack' }
    @{ Name = 'DIFF timeout'; Port = $Port; Offset = 4; ConfigCode = 0; Expected = "exit-writer, verify-endpoint, restart:$instanceId, verify-endpoint, launch-stream" }
    @{ Name = 'endpoint cooldown'; Port = $Port; Offset = 4; ConfigCode = 0; Expected = 'exit-writer' }
    @{ Name = 'missing endpoint health'; Port = $Port; Offset = 4; ConfigCode = $null; Expected = 'exit-writer, verify-endpoint, stop-stack, brightness, launch-stack' }
)
try {
    foreach ($case in $cases) {
        $actions.Clear(); $consecutiveFailures = 0
        if ($case.Name -ne 'endpoint cooldown') { $script:turzxSerialRecoveryLastAttemptUtc = [DateTime]::MinValue }
        if ($null -eq $case.ConfigCode) { $child = Set-TurzxChildHeartbeatStartupWindow -Child (Start-HiddenProcess) }
        $script:turzxStackStartedUtc = [DateTime]::UtcNow.AddSeconds(-24)
        $recordedUtc = $script:turzxStackStartedUtc.AddSeconds($case.Offset)
        @{ frame = 12; status = 'fatal'; error = 'DIFF timeout; FULL IOException: Device Error'; utc = $recordedUtc.ToString('o')
           serial_fault = 'endpoint-unresponsive'; port = $case.Port; send_attempted = $true; transport_mode = 'hybrid_diff_204_full_200' } |
            ConvertTo-Json -Compress | Set-Content -LiteralPath $heartbeatPaths[0] -Encoding ASCII
        [IO.File]::SetLastWriteTimeUtc($heartbeatPaths[0], $recordedUtc)
        do { . $poll } while ($false)
        $actual = $actions -join ', '
        if ($actual -ne $case.Expected) { throw "Unexpected $($case.Name) recovery sequence: [$actual]" }
        Write-Host "PASS $($case.Name) recovery: [$actual]"
    }

    $actions.Clear(); $consecutiveFailures = 0
    Remove-Item -LiteralPath $heartbeatPaths[0] -ErrorAction SilentlyContinue
    $script:turzxStackStartedUtc = [DateTime]::UtcNow.AddSeconds(-301)
    $child = Set-TurzxChildHeartbeatStartupWindow -Child ([pscustomobject]@{ Id = 8123; HasExited = $false })
    Set-Content $stackStdoutPath ('stage=stream-execute-start pid=8123 utc={0:o}' -f $script:turzxStackStartedUtc.AddSeconds(95))
    $script:testClockMs += 153000L
    do { . $poll } while ($false)
    if ($actions.Count) { throw 'A current worker progressing at the old deadline must keep starting.' }
    # Also replay a startup that first reaches that stage one second before the hard cap.
    Set-Content $stackStdoutPath ('stage=stream-execute-start pid=8123 utc={0:o}' -f $script:turzxStackStartedUtc.AddSeconds(299))
    $script:testClockMs += (300L - 153L) * 1000
    do { . $poll } while ($false)
    if (($actions -join ', ') -ne 'stop-stack, brightness, launch-stack') { throw 'A stalled startup must hit the hard deadline with one proved stop.' }

    $actions.Clear(); $script:turzxStackStartedUtc = [DateTime]::MinValue; $child = Set-TurzxChildHeartbeatStartupWindow -Child $null
    Publish-TestFrame 12
    if ($null -ne (Invoke-TurzxStackRestartAttempt -Reason 'watchdog-start' -CheckFrameProgress)) { throw 'One fresh orphan frame must defer initial stop within its bounded window.' }
    $script:testClockMs += 10000
    do { . $poll } while ($false)
    if ($actions.Count -or $script:childHeartbeatObserved) { throw 'A ten-second full frame may still be in flight during orphan adoption.' }
    Publish-TestFrame 13
    do { . $poll } while ($false)
    if ($actions.Count -or -not $script:childHeartbeatObserved) { throw 'Verified orphan advancement must keep a stream alive.' }
    $case = $cases[2]; $script:turzxSerialRecoveryLastAttemptUtc = [DateTime]::MinValue
    Publish-TestFrame 14 -Unresponsive
    do { . $poll } while ($false)
    if (($actions -join ', ') -ne "verify-endpoint, restart:$instanceId, verify-endpoint, launch-stream") { throw 'A verified orphan fault must recover only its serial stream.' }
    $actions.Clear()

    $child = Set-TurzxChildHeartbeatStartupWindow -Child ([pscustomobject]@{ Id = 8123; HasExited = $false })
    Publish-TestFrame 14
    do { . $poll } while ($false)
    if ($actions.Count -or -not $script:childHeartbeatObserved) { throw 'A first verified frame must enter stream supervision immediately.' }
    $script:testClockMs += 15001; $script:frameDuringDelay = 15
    do { . $poll } while ($false)
    if ($actions.Count -or $script:frameDuringDelay) { throw 'A stall during initial startup grace must be detected, and a frame in the delay must prevent stop.' }
    $child.HasExited = $true
    do { . $poll } while ($false)
    if ($actions.Count) { throw 'The active stream must survive its PowerShell parent exiting.' }
    $script:testClockMs += 15001
    do { . $poll } while ($false)
    if (($actions -join ', ') -ne 'stop-stack, brightness, launch-stack') { throw 'A stream that actually stalls after parent exit must recover.' }
    Write-Host 'PASS busy startup deadline, orphan adoption, delay recheck, and real stream stall'
}
finally { Remove-Item -LiteralPath ($heartbeatPaths + $childPidPath + $stackStdoutPath + $pausedPath) -Force -ErrorAction SilentlyContinue }
