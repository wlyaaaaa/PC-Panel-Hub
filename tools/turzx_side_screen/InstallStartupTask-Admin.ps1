param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Port = "COM7",
    [int]$IntervalMs = 3000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$installer = Join-Path $Root "scripts\install-startup-admin.ps1"
if (!(Test-Path -LiteralPath $installer)) {
    throw "Missing repository installer: $installer"
}

powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Root $Root -Port $Port -IntervalMs $IntervalMs
