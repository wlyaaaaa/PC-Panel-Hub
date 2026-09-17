Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\turzx_side_screen\MetricsEndpointPolicy.ps1')
if(-not(Test-MetricsProcessActive -ProcessId $PID)){throw 'Current real process must be alive'}
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
'Endpoint regression passed: kernel process status, dead-row reuse and unknown/live fail-closed.'