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
 function Get-NetTCPConnection { [pscustomobject]@{LocalPort=0;OwningProcess=123} }
 function Test-MetricsProcessActive { return $true }
 if(Test-MetricsEndpointBind -Port 0){throw 'A live owner must block reuse'}
 function Test-MetricsProcessActive { return $false }
 if(-not(Test-MetricsEndpointBind -Port 0)){throw 'Dead owner with an available bind must be accepted'}
 function Test-MetricsProcessActive { throw 'access denied' }
 if(Test-MetricsEndpointBind -Port 0){throw 'Unknown process status must block reuse'}
 function Get-NetTCPConnection { throw 'provider unavailable' }
 if(Test-MetricsEndpointBind -Port 0){throw 'Unknown TCP status must block reuse'}
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
