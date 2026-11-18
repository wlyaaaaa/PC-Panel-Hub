param([string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$side = Join-Path $Root 'tools\turzx_side_screen'
$tokens = $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $side 'StartSideScreenWatchdog.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Unable to parse the production watchdog.' }
$loop = $ast.Find({ param($node) $node -is [Management.Automation.Language.WhileStatementAst] }, $true)
$statements = @($loop.Body.Statements.Extent.Text)
$afterHs2 = [Array]::IndexOf($statements, 'Invoke-WallpaperEngineDisplayRecovery') + 1
if (-not $afterHs2) { throw 'Missing the watchdog polling loop recovery block.' }
# Run the real TURZX portion of a normal poll without starting the watchdog.
$poll = [scriptblock]::Create(($statements[$afterHs2..($statements.Count - 1)] -join "`n"))
. (Join-Path $side 'SideScreenWatchdogPolicy.ps1')
. (Join-Path $side 'PanelDevicePolicy.ps1')
$functions = @('Get-StreamHeartbeatHealth', 'Start-Stack', 'Invoke-TurzxStackRestartAttempt', 'Set-TurzxChildHeartbeatStartupWindow', 'Get-TurzxMonotonicMilliseconds',
    'Invoke-TurzxSerialFaultRecovery', 'Invoke-TurzxSerialEndpointRecovery', 'Get-TurzxExactSerialEndpoint', 'Wait-TurzxExactSerialEndpointHealthy')
foreach ($definition in $ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -in $functions }) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
$actions = [Collections.Generic.List[string]]::new()
$Port = 'COM241'
$instanceId = 'USB\VID_0525&PID_A4A7\TEST-ONLY'
function Write-WatchdogLog { param($Message) }
function Start-Sleep { param($Seconds, $Milliseconds) if ($Seconds -ne 3) { throw 'Unexpected recovery delay' } }
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
$HeartbeatStartupGraceSeconds = 60; $HeartbeatStaleSeconds = 15; $MaxConsecutiveFailures = 3
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
}
finally { Remove-Item -LiteralPath ($heartbeatPaths + $childPidPath) -Force -ErrorAction SilentlyContinue }
