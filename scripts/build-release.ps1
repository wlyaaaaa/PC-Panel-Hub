param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$Version = "source",
    [string]$OutputDir = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")).Path "dist")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path $Root).Path
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# The index defines the package's file set. Tracked edits are still read from
# the working tree, so this does not require a commit before local packaging.
$trackedPaths = @(& git -C $Root ls-files --cached --)
if ($LASTEXITCODE -ne 0 -or $trackedPaths.Count -eq 0) {
    throw "Cannot list tracked release inputs from Git: $Root"
}

$publicToolExtensions = @(
    ".appxmanifest",
    ".cmd",
    ".cs",
    ".csproj",
    ".ico",
    ".json",
    ".manifest",
    ".md",
    ".png",
    ".ps1",
    ".py",
    ".slnx",
    ".svg",
    ".txt",
    ".vbs",
    ".xaml"
)
$excludedToolDirectories = @(
    ".vs",
    "AppPackages",
    "bin",
    "obj",
    "out",
    "__pycache__"
)

function Copy-PublicToolTree {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,
        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $sourceRelative = ($Source.Substring($Root.Length) -replace '^[\\/]+', '') -replace '\\', '/'
    $trackedPaths | Where-Object { $_.StartsWith("$sourceRelative/", [StringComparison]::Ordinal) } | ForEach-Object {
        $relativePath = $_.Substring($sourceRelative.Length + 1)
        $relativeDirectory = Split-Path -Parent $relativePath
        $directoryNames = if ([string]::IsNullOrWhiteSpace($relativeDirectory)) {
            @()
        }
        else {
            @($relativeDirectory -split '[\\/]')
        }
        $hasExcludedDirectory = @(
            $directoryNames | Where-Object { $_ -in $excludedToolDirectories }
        ).Count -gt 0
        $extension = [System.IO.Path]::GetExtension($relativePath).ToLowerInvariant()
        $normalizedRelativePath = $relativePath -replace '\\', '/'
        $isApprovedHs2Asset = (
            (Split-Path -Leaf $Source) -eq "hs2_crystal_overlay" -and
            $normalizedRelativePath -match '^src/HS2\.CrystalOverlay/Assets/[^/]+\.(ico|png)$'
        )
        $isApprovedConfigExample = (
            (Split-Path -Leaf $Source) -eq "turzx_side_screen" -and
            $normalizedRelativePath -eq "config.example.json"
        )
        $isApprovedRequirements = (
            (Split-Path -Leaf $Source) -eq "turzx_side_screen" -and
            $normalizedRelativePath -eq "requirements.txt"
        )

        if (
            $hasExcludedDirectory -or
            $extension -notin $publicToolExtensions -or
            ($extension -eq ".json" -and -not $isApprovedConfigExample) -or
            ($extension -eq ".txt" -and -not $isApprovedRequirements) -or
            ($extension -in @(".ico", ".png") -and -not $isApprovedHs2Asset)
        ) {
            return
        }

        $target = Join-Path $Destination $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $Source $relativePath) -Destination $target -Force
    }
}

$zipPath = Join-Path $OutputDir ("PC-Panel-Hub-{0}.zip" -f $Version)
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("turzx_side_screen_release_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $staging | Out-Null

try {
    foreach ($item in @("README.md", "AGENTS.md", "LICENSE", ".gitignore", "start-side-screen.cmd", "install-startup.cmd", "uninstall-startup.cmd")) {
        if ($trackedPaths -cnotcontains $item) {
            throw "Required release input is not tracked by Git: $item"
        }
        Copy-Item -LiteralPath (Join-Path $Root $item) -Destination (Join-Path $staging $item) -Force
    }

    New-Item -ItemType Directory -Force -Path (Join-Path $staging "docs") | Out-Null
    $trackedPaths | Where-Object { $_ -cmatch '^docs/[^/]+\.md$' } | ForEach-Object {
        Copy-Item -LiteralPath (Join-Path $Root $_) -Destination (Join-Path $staging $_) -Force
    }
    Copy-PublicToolTree `
        -Source (Join-Path $Root "scripts") `
        -Destination (Join-Path $staging "scripts")

    $toolDest = Join-Path $staging "tools"
    New-Item -ItemType Directory -Force -Path $toolDest | Out-Null

    foreach ($dir in @("turzx_side_screen", "turzx_weather_shim", "hs2_crystal_overlay")) {
        $src = Join-Path $Root ("tools\" + $dir)
        $dst = Join-Path $toolDest $dir
        Copy-PublicToolTree -Source $src -Destination $dst
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
