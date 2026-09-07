param([string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$launcherSource = Join-Path $Root 'tools\turzx_side_screen\StartSideScreenWatchdog-Hidden.vbs'
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryRoot = Join-Path $temporaryParent ('turzx-launcher-test-' + [Guid]::NewGuid().ToString('N'))
$launcherHash = (Get-FileHash -LiteralPath $launcherSource -Algorithm SHA256).Hash
$parseErrors = $null
$parseTokens = $null
$stopAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $Root 'tools\turzx_side_screen\StopSideScreenStack.ps1'), [ref]$parseTokens, [ref]$parseErrors)
if (@($parseErrors).Count -gt 0) { throw 'Unable to parse the production stop script.' }
$stopLauncherFunction = $stopAst.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Stop-TurzxWatchdogLaunchers'
}, $true)
if ($null -eq $stopLauncherFunction) { throw 'Missing explicit launcher stop function.' }
. ([scriptblock]::Create($stopLauncherFunction.Extent.Text))
function Write-StopLog { param([string]$Message) }
$cases = @(
    @{ Name='native-exit'; Code=-1073741819; Expected=2; Stop=$false },
    @{ Name='ordinary-error'; Code=1; Expected=2; Stop=$false },
    @{ Name='normal-exit'; Code=0; Expected=1; Stop=$false },
    @{ Name='stop-during-backoff'; Code=1; Expected=1; Stop=$true }
)
$processes = @()
try {
    foreach ($case in $cases) {
        $caseRoot = Join-Path $temporaryRoot $case.Name
        New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
        # -Root does not redirect the VBS's script path. Copy its exact bytes
        # beside an inert stub, never invoke the production watchdog in tests.
        $launcherCopy = Join-Path $caseRoot 'StartSideScreenWatchdog-Hidden.vbs'
        Copy-Item -LiteralPath $launcherSource -Destination $launcherCopy
        if ((Get-FileHash -LiteralPath $launcherCopy -Algorithm SHA256).Hash -ne $launcherHash) {
            throw 'Launcher test copy differs from production.'
        }
        $stub = @'
param([string]$Root,[string]$Port,[int]$IntervalMs,[switch]$HybridRefresh,[int]$PollSeconds,[switch]$AltHelper)
$counterPath = Join-Path $Root 'attempts.txt'
$count = if (Test-Path -LiteralPath $counterPath) { @(Get-Content -LiteralPath $counterPath).Count } else { 0 }
[IO.File]::AppendAllText($counterPath, [DateTime]::UtcNow.ToString('o') + [Environment]::NewLine)
if ($count -eq 0) { [Environment]::Exit(__EXIT_CODE__) }
exit 0
'@
        [IO.File]::WriteAllText(
            (Join-Path $caseRoot 'StartSideScreenWatchdog.ps1'),
            $stub.Replace('__EXIT_CODE__', [string]$case.Code),
            [Text.UTF8Encoding]::new($false))
        $arguments = '"{0}" -Root "{1}" -Port TEST_ONLY -IntervalMs 3000 -HybridRefresh' -f $launcherCopy, $caseRoot
        $process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\wscript.exe') `
            -ArgumentList $arguments -WorkingDirectory $caseRoot -WindowStyle Hidden -PassThru
        $processes += [pscustomobject]@{
            Case=$case; Process=$process; Counter=(Join-Path $caseRoot 'attempts.txt'); Launcher=$launcherCopy; Stopped=$false
        }
    }
    $started = [DateTime]::UtcNow
    $deadline = $started.AddSeconds(55)
    do {
        Start-Sleep -Milliseconds 250
        foreach ($item in $processes) {
            $item.Process.Refresh()
            $count = if (Test-Path -LiteralPath $item.Counter) { @(Get-Content -LiteralPath $item.Counter).Count } else { 0 }
            if ($item.Case.Stop -and -not $item.Stopped -and $count -eq 1) {
                # The stub has exited; stopping the task's existing launcher
                # during its delay must prevent any later recovery attempt.
                Start-Sleep -Seconds 2
                if ($item.Process.HasExited) { throw 'Failed watchdog was not held by its launcher.' }
                $snapshot = @(Get-CimInstance Win32_Process -Filter ("ProcessId={0}" -f $item.Process.Id))
                Stop-TurzxWatchdogLaunchers -Snapshot $snapshot -LauncherPath $item.Launcher
                if (-not $item.Process.WaitForExit(3000)) { throw 'Explicit launcher stop did not exit.' }
                $item.Process.WaitForExit()
                $item.Stopped = $true
            }
            if (-not $item.Case.Stop -and $item.Process.HasExited -and $count -ne $item.Case.Expected) {
                throw ("{0}: expected {1} attempts, got {2}." -f $item.Case.Name, $item.Case.Expected, $count)
            }
        }
        $running = @($processes | Where-Object { -not $_.Process.HasExited })
        if ($running.Count -eq 0 -and ([DateTime]::UtcNow - $started).TotalSeconds -ge 33) { break }
    } while ([DateTime]::UtcNow -lt $deadline)
    foreach ($item in $processes) {
        if (-not $item.Process.HasExited) { throw "Launcher did not finish: $($item.Case.Name)" }
        $count = @(Get-Content -LiteralPath $item.Counter).Count
        if ($count -ne $item.Case.Expected) { throw "Unexpected retry count for $($item.Case.Name): $count" }
        if ($count -eq 2) {
            $attemptTimes = @(Get-Content -LiteralPath $item.Counter | ForEach-Object { [DateTime]::Parse($_) })
            if (($attemptTimes[1] - $attemptTimes[0]).TotalSeconds -lt 30) { throw 'Crash retries bypassed the cooldown.' }
        }
        if (-not $item.Case.Stop -and $item.Process.ExitCode -ne 0) {
            throw "Successful watchdog exit was not propagated: $($item.Case.Name)"
        }
        Write-Host ("PASS launcher {0}: attempts={1}" -f $item.Case.Name, $count)
    }
}
finally {
    foreach ($item in $processes) {
        if (-not $item.Process.HasExited) { $item.Process.Kill(); $item.Process.WaitForExit() }
        $item.Process.Dispose()
    }
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    if (-not $resolvedTemporaryRoot.StartsWith($temporaryParent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside the test temporary directory.'
    }
    if (Test-Path -LiteralPath $resolvedTemporaryRoot) { Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force }
}
