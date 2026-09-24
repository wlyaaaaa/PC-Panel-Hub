param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Port = "",
    [int]$IntervalMs = 3000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$installer = Join-Path $Root "scripts\install-startup-admin.ps1"
if (!(Test-Path -LiteralPath $installer)) {
    throw "Missing repository installer: $installer"
}

$installerArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installer, '-Root', $Root, '-IntervalMs', [string]$IntervalMs)
if ($PSBoundParameters.ContainsKey('Port')) { $installerArguments += @('-Port', $Port) }
powershell @installerArguments
