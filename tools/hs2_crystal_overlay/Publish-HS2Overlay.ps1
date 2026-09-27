[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Build and copy only. Never register, activate, stop a process or change a task.
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) {
    throw "Choose a new release directory; existing files will not be overwritten: $destination"
}
$project = Join-Path $PSScriptRoot 'src\HS2.CrystalOverlay\HS2.CrystalOverlay.csproj'
& dotnet build $project -c Release -p:Platform=x64 --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'HS2 Release build failed.' }

[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$framework = $projectXml.SelectSingleNode('//TargetFramework').InnerText
$buildDirectory = Join-Path (Split-Path $project) "bin\x64\Release\$framework\win-x64"
$recipePath = Join-Path $buildDirectory 'HS2.CrystalOverlay.build.appxrecipe'
[xml]$recipe = Get-Content -LiteralPath $recipePath -Raw
# The generated recipe includes assets and native dependencies that a plain bin
# copy misses. Copy file contents, never hardlink back into cleanable build output.
$entries = @($recipe.SelectNodes("//*[local-name()='AppXManifest' or local-name()='AppxPackagedFile']"))
if ($entries.Count -eq 0) { throw 'Build produced no package layout entries.' }
$files = foreach ($entry in $entries) {
    $source = [string]$entry.Include
    $relative = [string]$entry.PackagePath
    $target = [IO.Path]::GetFullPath((Join-Path $destination $relative))
    if (-not $target.StartsWith($destination.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Invalid package path: $relative"
    }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Missing package input: $source"
    }
    [pscustomobject]@{ Source = $source; Target = $target }
}
foreach ($file in $files) {
    New-Item -ItemType Directory -Force -Path (Split-Path $file.Target) | Out-Null
    Copy-Item -LiteralPath $file.Source -Destination $file.Target
    if ((Get-FileHash -LiteralPath $file.Source).Hash -ne (Get-FileHash -LiteralPath $file.Target).Hash) {
        throw "Package copy verification failed: $($file.Target)"
    }
}
Write-Output "Published $($files.Count) verified files to $destination. No installation or activation performed."
