[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path.TrimEnd('\')
if ($package -match '(?i)[\\/](bin|obj|temp|cache)[\\/]') {
    throw 'Publish to a permanent installation directory outside build/cache/temp before registering.'
}
$manifestPath = Join-Path $package 'AppxManifest.xml'
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
if ([string]$manifest.Package.Identity.Name -ne 'CA842C44-3611-4D66-BE9F-5B383BFCEE75' -or
    [string]$manifest.Package.Identity.Publisher -ne 'CN=wlyaaaaa') {
    throw 'The package does not have the existing HS2 application identity.'
}
foreach ($relative in @('HS2.CrystalOverlay.exe', 'HS2.CrystalOverlay.dll',
        'HS2.CrystalOverlay.Core.dll', 'HS2.CrystalOverlay.runtimeconfig.json', 'resources.pri')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $relative) -PathType Leaf)) {
        throw "Incomplete published package: $relative"
    }
}
if (-not $Apply) {
    Write-Output "Validated package at $package. Pass -Apply in the target user's session to register it."
    return
}

# Preserve the package family, local state, notification permission and AUMID.
# Existing startup/watchdog activation follows this registration automatically.
Add-AppxPackage -Register $manifestPath -ForceApplicationShutdown
$registered = @(Get-AppxPackage -Name ([string]$manifest.Package.Identity.Name))
if ($registered.Count -ne 1 -or
    -not [string]::Equals($registered[0].InstallLocation.TrimEnd('\'), $package,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Registration did not read back from the requested permanent directory.'
}
Write-Output "Registered $($registered[0].PackageFamilyName) at $package. Activation and physical-screen acceptance remain separate."
