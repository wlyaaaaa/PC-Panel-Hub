param([string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$installer = Join-Path $Root 'tools\hs2_crystal_overlay\Install-HS2Overlay.ps1'

# Exercise the installer contract without registering anything on this computer.
& {
    $global:HS2InstallationTestState = @{
        registrationCount = 0
        location = 'E:\Apps\PC-Panel-Hub\HS2\test-release'
        registeredLocation = 'E:\Apps\PC-Panel-Hub\HS2\test-release'
    }
    function Resolve-Path { param($LiteralPath) [pscustomobject]@{ Path = $global:HS2InstallationTestState.location } }
    function Get-Content {
        param($LiteralPath, [switch]$Raw)
        '<Package><Identity Name="CA842C44-3611-4D66-BE9F-5B383BFCEE75" Publisher="CN=wlyaaaaa" /></Package>'
    }
    function Test-Path { param($LiteralPath, $PathType) $true }
    function Add-AppxPackage {
        param($Register, [switch]$ForceApplicationShutdown)
        if ($Register -ne ($global:HS2InstallationTestState.location + '\AppxManifest.xml')) { throw 'Wrong registration path.' }
        $global:HS2InstallationTestState.registrationCount++
    }
    function Get-AppxPackage {
        param($Name)
        [pscustomobject]@{ InstallLocation = $global:HS2InstallationTestState.registeredLocation; PackageFamilyName = 'test-family' }
    }
    & $installer -PackageDirectory $global:HS2InstallationTestState.location | Out-Null
    if ($global:HS2InstallationTestState.registrationCount -ne 0) { throw 'Inspection registered a package.' }
    & $installer -PackageDirectory $global:HS2InstallationTestState.location -Apply | Out-Null
    if ($global:HS2InstallationTestState.registrationCount -ne 1) { throw 'Apply did not register exactly once.' }

    $global:HS2InstallationTestState.registeredLocation = 'E:\old-install'
    $rejected = $false
    try { & $installer -PackageDirectory $global:HS2InstallationTestState.location -Apply | Out-Null }
    catch { $rejected = $_.Exception.Message -match 'read back' }
    if (-not $rejected) { throw 'Wrong install-location readback was accepted.' }

    $before = $global:HS2InstallationTestState.registrationCount
    $global:HS2InstallationTestState.location = 'E:\project\bin\x64\Debug\AppX'
    $rejected = $false
    try { & $installer -PackageDirectory $global:HS2InstallationTestState.location -Apply | Out-Null }
    catch { $rejected = $_.Exception.Message -match 'permanent' }
    if (-not $rejected -or $global:HS2InstallationTestState.registrationCount -ne $before) {
        throw 'A cleanable build directory was registered.'
    }
    Remove-Variable -Name HS2InstallationTestState -Scope Global
}
Write-Host 'HS2 installation checks passed (mocked registration; no machine changes).'
