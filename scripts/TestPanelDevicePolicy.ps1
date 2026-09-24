param([string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $Root 'tools\turzx_side_screen\PanelDevicePolicy.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('panel-device-policy-' + [Guid]::NewGuid().ToString('N'))
$configDir = Join-Path $testRoot 'tools\turzx_side_screen'
New-Item -ItemType Directory -Path $configDir -Force | Out-Null
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Expected the device policy to reject this input.' }
}
try {
    if ((Get-TurzxConfiguredPort -Root $testRoot) -ne 'COM7') { throw 'Default port changed.' }
    [IO.File]::WriteAllText((Join-Path $configDir 'config.json'), '{"serial":{"port":"COM12"}}')
    if ((Get-TurzxConfiguredPort -Root $testRoot) -ne 'COM12') { throw 'Configured port ignored.' }
    if ((Get-TurzxConfiguredPort -Root $testRoot -Port 'com9') -ne 'COM9') { throw 'Explicit port ignored.' }
    Assert-Rejected { Get-TurzxConfiguredPort -Root $testRoot -Port 'COM7 -Other' }
    [IO.File]::WriteAllText((Join-Path $configDir 'config.json'), '{"serial":{"port":""}}')
    Assert-Rejected { Get-TurzxConfiguredPort -Root $testRoot }

    # Providers below are synthetic; this test never opens or modifies a device.
    $script:ports = @([pscustomobject]@{DeviceID='COM12';PNPDeviceID='USB\VID_0525&PID_A4A7\synthetic'})
    $script:device = [pscustomobject]@{Present=$true;Status='OK';ConfigManagerErrorCode=0}
    function Get-CimInstance { param($ClassName,$ErrorAction) return $script:ports }
    function Get-PnpDevice { param($InstanceId,$ErrorAction) return $script:device }
    $result = Get-TurzxVerifiedSerialEndpoint -Port COM12
    if ($result.Port -ne 'COM12') { throw 'Correct device was not selected.' }
    Assert-Rejected { Get-TurzxVerifiedSerialEndpoint -Port COM7 }
    $script:ports += $script:ports[0]
    Assert-Rejected { Get-TurzxVerifiedSerialEndpoint -Port COM12 }
    $script:ports = @([pscustomobject]@{DeviceID='COM12';PNPDeviceID='USB\VID_1234&PID_5678\synthetic'})
    Assert-Rejected { Get-TurzxVerifiedSerialEndpoint -Port COM12 }
    $script:ports[0].PNPDeviceID='USB\VID_0525&PID_A4A7\synthetic'
    $script:device.Present=$false
    Assert-Rejected { Get-TurzxVerifiedSerialEndpoint -Port COM12 }
    $script:device.Present=$true
    $script:device.ConfigManagerErrorCode=43
    Assert-Rejected { Get-TurzxVerifiedSerialEndpoint -Port COM12 }

    # An installed Hybrid task must not swallow an explicit FullFrame request
    # through the old watchdog's singleton mutex and report success.
    [IO.File]::WriteAllText((Join-Path $configDir 'config.json'), '{"serial":{"port":"COM12"}}')
    Copy-Item -LiteralPath (Join-Path $Root 'tools\turzx_side_screen\PanelDevicePolicy.ps1') -Destination $configDir
    foreach ($name in @('StartSideScreenWatchdog.ps1','StopSideScreenStack.ps1')) {
        [IO.File]::WriteAllText((Join-Path $configDir $name), "throw 'Unexpected process launch in configuration test'")
    }
    function Get-ScheduledTask {
        param($TaskName,$ErrorAction)
        return [pscustomobject]@{ Actions = @([pscustomobject]@{ Arguments = '-Port COM12 -HybridRefresh' }); State = 'Running' }
    }
    $modeRejected = $false
    try { & (Join-Path $Root 'scripts\start.ps1') -Root $testRoot -FullFrame }
    catch { $modeRejected = $_.Exception.Message -like 'The scheduled task uses a different refresh/helper mode.*' }
    if (-not $modeRejected) { throw 'Explicit mode mismatch must fail before attempting a duplicate watchdog.' }
    Write-Host 'Panel configuration and serial identity checks passed (synthetic devices only).'
}
finally {
    if ([IO.Path]::GetFullPath($testRoot).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
