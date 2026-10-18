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
'Endpoint regression passed: kernel process completion including exit 259, dead-row reuse and unknown/live fail-closed.'
