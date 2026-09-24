[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$BindingPath,
    [switch]$Apply
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $Root 'tools\turzx_side_screen\HS2ActiveRecoveryPolicy.ps1')
if ([string]::IsNullOrWhiteSpace($BindingPath)) {
    $BindingPath = Join-Path $Root 'tools\turzx_side_screen\out\hs2-usb-topology-binding.json'
}
$binding = Read-HS2UsbTopologyBinding -Path $BindingPath
if ($null -eq $binding) { throw 'No verified HS2 topology binding exists. Recover normal healthy operation and save its binding first.' }
$snapshot = Get-HS2UsbRecoverySnapshot
$plan = Get-HS2UsbRecoveryPlan -BoundHubInstanceId ([string]$binding.HubInstanceId) `
    -Hubs $snapshot.Hubs -Children $snapshot.Children -Binding $binding -Devices $snapshot.Devices
if (-not $Apply) {
    [pscustomobject]@{ Mode = 'Inspect'; Applicable = [bool]$plan.Applicable; Reason = [string]$plan.Reason; Plan = $plan } | ConvertTo-Json -Depth 8
    return
}
if (-not $plan.Applicable) { throw ('HS2 recovery refused: ' + [string]$plan.Reason) }
if ($PSCmdlet.ShouldProcess([string]$plan.HubInstanceId, 'Restart the bound dedicated HS2 hub; if required, remove the exact failed child and scan devices')) {
    # The existing implementation re-reads the binding and topology before effects.
    $result = Invoke-HS2UsbRecovery -BindingPath $BindingPath
    $result | ConvertTo-Json -Depth 6
    if (-not $result.Recovered) { throw ('HS2 recovery incomplete: ' + [string]$result.Reason) }
}
