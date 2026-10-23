Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\turzx_side_screen\MetricsEndpointPolicy.ps1')
if(-not(Test-MetricsProcessActive -ProcessId $PID)){throw 'Current real process must be alive'}
$hostProcess=[Diagnostics.Process]::GetCurrentProcess()
try { $hostExecutable=$hostProcess.MainModule.FileName } finally { $hostProcess.Dispose() }
$startInfo=[Diagnostics.ProcessStartInfo]::new($hostExecutable)
$startInfo.UseShellExecute=$false
$startInfo.CreateNoWindow=$true
$startInfo.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$startInfo.Arguments='-NoProfile -NonInteractive -Command "exit 259"'
$child=[Diagnostics.Process]::Start($startInfo)
try {
 $heldHandle=$child.Handle # Keep the completed process object available to OpenProcess.
 if(-not$child.WaitForExit(10000)){throw 'Exit-259 child did not complete in time'}
 if($child.ExitCode-ne259){throw 'Exit-259 child returned an unexpected exit code'}
 if(Test-MetricsProcessActive -ProcessId $child.Id){throw 'A completed process with exit code 259 must be inactive'}
} finally {
 if(-not$child.HasExited){$child.Kill();$child.WaitForExit()}
 $child.Dispose()
}
& {
 function Get-NetTCPConnection { throw 'provider unavailable' }
 $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
 try {
  $listener.Start()
  $port=$listener.LocalEndpoint.Port
  if(Test-MetricsEndpointBind -Port $port){throw 'An active loopback listener must block reuse'}
  $listener.Stop()
  if(-not(Test-MetricsEndpointBind -Port $port)){throw 'A released loopback endpoint must be available without the TCP provider'}
 } finally { $listener.Stop() }
}
& {
 $ownershipRoot=Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\turzx_side_screen'
 $script:ownershipCommand='python.exe -u "'+(Join-Path $ownershipRoot 'metrics_agent.py')+'" --port 18766'
 function Get-NetTCPConnection { [CmdletBinding()]param($State,$LocalPort) [pscustomobject]@{OwningProcess=123} }
 function Get-CimInstance { [CmdletBinding()]param($ClassName,$Filter,$OperationTimeoutSec) [pscustomobject]@{Name='python.exe';CommandLine=$script:ownershipCommand} }
 function Test-MetricsProcessActive {param($ProcessId) $true }
 $endpoint=[pscustomobject]@{HostName='127.0.0.1';Port=18766}
 if(-not(Get-TurzxMetricsEndpointOwnership -SideRoot $ownershipRoot -Endpoint $endpoint).Managed){throw 'A live canonical collector must be reusable'}
 $script:ownershipCommand='python.exe -u E:\deleted-worktree\tools\turzx_side_screen\metrics_agent.py --port 18766'
 $ownership=Get-TurzxMetricsEndpointOwnership -SideRoot $ownershipRoot -Endpoint $endpoint
 if($ownership.Managed-or$ownership.Reason-notmatch 'unexpected-agent-source pid=123'){throw 'Healthy HTTP from another checkout must not prove collector ownership'}
 $script:ownershipCommand='python.exe -u "'+(Join-Path $ownershipRoot 'metrics_agent.py')+'.old" --port 18766'
 if((Get-TurzxMetricsEndpointOwnership -SideRoot $ownershipRoot -Endpoint $endpoint).Managed){throw 'A source path prefix must not prove collector ownership'}
 function Get-NetTCPConnection { [CmdletBinding()]param($State,$LocalPort) @([pscustomobject]@{OwningProcess=123},[pscustomobject]@{OwningProcess=456}) }
 if((Get-TurzxMetricsEndpointOwnership -SideRoot $ownershipRoot -Endpoint $endpoint).Managed){throw 'Multiple listener owners must not be silently accepted'}
}
& {
 $recoveryRoot=Join-Path ([IO.Path]::GetTempPath()) ('turzx-metrics-'+[guid]::NewGuid().ToString('N'))
 $script:agentActive=$true; $script:portAvailable=$false; $script:launches=0; $script:stops=@()
 function Get-TurzxMetricsEndpoint {param($ConfigPath) if($ConfigPath-ne(Join-Path $recoveryRoot 'config.json')){throw 'Maintenance must use its SideRoot endpoint'}; [pscustomobject]@{HostName='127.0.0.1';Port=18766} }
 function Get-CimInstance { [CmdletBinding()]param($ClassName,$OperationTimeoutSec) @(
  [pscustomobject]@{Name='python.exe';ProcessId=123;CommandLine=(Join-Path $recoveryRoot 'metrics_agent.py')},
  [pscustomobject]@{Name='python.exe';ProcessId=456;CommandLine='E:\other\metrics_agent.py'}) }
 function Stop-Process { [CmdletBinding()]param($Id,[switch]$Force) $script:stops+= $Id }
 function Test-MetricsProcessActive {param($ProcessId) $script:agentActive }
 function Test-MetricsEndpointBind {param($Port,$HostName) $script:portAvailable }
 function Test-PythonModules {param($Path,$Modules) $true }
 function Get-NetTCPConnection { [CmdletBinding()]param($State) [pscustomobject]@{LocalPort=18766;OwningProcess=123} }
 function Start-HiddenProcess {param($FilePath,$WorkingDirectory,$ArgumentList,$RedirectStandardOutput,$RedirectStandardError) $script:launches++; [pscustomobject]@{Id=789} }
 try {
  $blocked=Invoke-TurzxMetricsAgentMaintenance -SideRoot $recoveryRoot -PythonPath 'fake-python' -Restart
  if($blocked.Status-notmatch 'remainingPids=123 metricsPort=18766 metricsPortOwners=123' -or $script:launches-ne0 -or ($script:stops -join ',')-ne'123'){throw 'Only the project collector may be retried; a live owner blocks replacement'}
  $script:agentActive=$false
  $blocked=Invoke-TurzxMetricsAgentMaintenance -SideRoot $recoveryRoot -PythonPath 'fake-python' -Restart
  if($script:launches-ne0){throw 'An unreleased port must block replacement even after process exit'}
  $script:agentActive=$true; $script:portAvailable=$false; $script:polls=0
  function Start-Sleep {param($Milliseconds) $script:polls++; $script:agentActive=$script:polls-lt2; $script:portAvailable=$script:polls-ge3 }
  $started=Invoke-TurzxMetricsAgentMaintenance -SideRoot $recoveryRoot -PythonPath 'fake-python' -Restart
  if($script:launches-ne1 -or $started.Process.Id-ne789 -or $script:polls-ne3){throw 'Delayed PID exit and port release must allow replacement in the same maintenance attempt'}
 } finally { if(Test-Path -LiteralPath $recoveryRoot){Remove-Item -LiteralPath $recoveryRoot -Recurse -Force} }
}
'Endpoint regression passed: kernel completion, exclusive bind and independent collector recovery.'
