if (-not ('TurzxHiddenProcessLauncher' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'HiddenProcessLauncher.cs')
}

function Start-HiddenProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = (Get-Location).Path,
        [string]$RedirectStandardOutput,
        [string]$RedirectStandardError
    )

    $executable = if (Test-Path -LiteralPath $FilePath -PathType Leaf) {
        (Get-Item -LiteralPath $FilePath -ErrorAction Stop).FullName
    } else {
        (Get-Command -Name $FilePath -CommandType Application -ErrorAction Stop).Source
    }
    $stdout = if ([string]::IsNullOrEmpty($RedirectStandardOutput)) { $null } else { $RedirectStandardOutput }
    $stderr = if ([string]::IsNullOrEmpty($RedirectStandardError)) { $null } else { $RedirectStandardError }
    [TurzxHiddenProcessLauncher]::Start(
        $executable, $ArgumentList, $WorkingDirectory,
        $stdout, $stderr)
}
