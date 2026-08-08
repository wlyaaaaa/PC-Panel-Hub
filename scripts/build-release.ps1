param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$Version = "source",
    [string]$OutputDir = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")).Path "dist")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path $Root).Path
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$zipPath = Join-Path $OutputDir ("PC-Panel-Hub-{0}.zip" -f $Version)
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("turzx_side_screen_release_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $staging | Out-Null

try {
    foreach ($item in @("README.md", "AGENTS.md", "LICENSE", ".gitignore", "start-side-screen.cmd", "install-startup.cmd", "uninstall-startup.cmd")) {
        Copy-Item -LiteralPath (Join-Path $Root $item) -Destination (Join-Path $staging $item) -Force
    }

    New-Item -ItemType Directory -Force -Path (Join-Path $staging "docs") | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $Root "docs") -File -Filter "*.md" | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path (Join-Path $staging "docs") $_.Name) -Force
    }
    Copy-Item -LiteralPath (Join-Path $Root "scripts") -Destination (Join-Path $staging "scripts") -Recurse -Force

    $toolDest = Join-Path $staging "tools"
    New-Item -ItemType Directory -Force -Path $toolDest | Out-Null

    foreach ($dir in @("turzx_side_screen", "turzx_weather_shim")) {
        $src = Join-Path $Root ("tools\" + $dir)
        $dst = Join-Path $toolDest $dir
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        Get-ChildItem -LiteralPath $src -File | Where-Object {
            $_.Extension -in @(".ps1", ".cmd", ".py", ".cs", ".json", ".md", ".vbs")
        } | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $dst $_.Name) -Force
        }
        $designSrc = Join-Path $src "design"
        if (Test-Path -LiteralPath $designSrc) {
            $designDst = Join-Path $dst "design"
            New-Item -ItemType Directory -Force -Path $designDst | Out-Null
            Get-ChildItem -LiteralPath $designSrc -File -Filter "*.svg" | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $designDst $_.Name) -Force
            }
        }
    }

    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zipPath -Force
    $item = Get-Item -LiteralPath $zipPath
    Write-Host ("Release package: {0} ({1} bytes)" -f $item.FullName, $item.Length)
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
