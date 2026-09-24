Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HiddenProcessLauncher.ps1')

$taskId = if ($env:CODEX_SESSION_ID -match '^[0-9a-fA-F-]{36}$') { $env:CODEX_SESSION_ID } else { 'standalone' }
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'pc-panel-hub-hidden-launch-' + $taskId + '-' + [guid]::NewGuid().ToString('N'))
$child = $null
$launcher = $null
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $childScript = Join-Path $temporaryRoot 'child script.ps1'
    $launcherScript = Join-Path $temporaryRoot 'parent launcher.ps1'
    $childPidPath = Join-Path $temporaryRoot 'child pid.txt'
    $stdoutPath = Join-Path $temporaryRoot 'standard out.log'
    $stderrPath = Join-Path $temporaryRoot 'standard error.log'
    $launcherOutPath = Join-Path $temporaryRoot 'launcher out.log'
    $launcherErrPath = Join-Path $temporaryRoot 'launcher err.log'
    $childSource = @'
$ErrorActionPreference = 'Stop'
Add-Type -Namespace TurzxTest -Name ConsoleProbe -MemberDefinition '[DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();' | Out-Null
Start-Sleep -Milliseconds 650
[Console]::Out.WriteLine((ConvertTo-Json -InputObject ([pscustomobject]@{
    Arguments = [string[]]$args
    NoConsole = ([TurzxTest.ConsoleProbe]::GetConsoleWindow() -eq [IntPtr]::Zero)
}) -Compress))
[Console]::Error.WriteLine('stderr after launcher exit')
exit 23
'@
    [IO.File]::WriteAllText($childScript, $childSource, [Text.UTF8Encoding]::new($true))
    $arguments = @('plain', 'two words', 'embedded"quote', 'C:\path with space\', '', 'a\\"b')
    $payload = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes((ConvertTo-Json -InputObject $arguments -Compress)))
    $helperPath = Join-Path $PSScriptRoot 'HiddenProcessLauncher.ps1'
    $launcherSource = @'
param([string]$Helper, [string]$Child, [string]$Payload, [string]$Out, [string]$Err, [string]$PidPath)
. $Helper
$childArgs = ConvertFrom-Json ([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($Payload)))
$process = Start-HiddenProcess -FilePath powershell.exe -ArgumentList (@('-NoProfile', '-File', $Child) + [string[]]$childArgs) -RedirectStandardOutput $Out -RedirectStandardError $Err
[IO.File]::WriteAllText($PidPath, ('{0}|{1}' -f $process.Id, $process.StartTime.ToUniversalTime().Ticks))
'@
    [IO.File]::WriteAllText($launcherScript, $launcherSource, [Text.UTF8Encoding]::new($true))
    $launcher = Start-HiddenProcess -FilePath powershell.exe -ArgumentList @(
        '-NoProfile', '-File', $launcherScript,
        '-Helper', $helperPath, '-Child', $childScript, '-Payload', $payload,
        '-Out', $stdoutPath, '-Err', $stderrPath, '-PidPath', $childPidPath) -RedirectStandardOutput $launcherOutPath -RedirectStandardError $launcherErrPath
    $launcherCompleted = $launcher.WaitForExit(15000)
    if (-not $launcherCompleted -or $launcher.ExitCode -ne 0) {
        throw "Launcher failed: type=$($launcher.GetType().FullName) id=$($launcher.Id) completed=$launcherCompleted code=$($launcher.ExitCode) pidFile=$(Test-Path -LiteralPath $childPidPath)"
    }
    if (-not (Test-Path -LiteralPath $childPidPath)) { throw 'Launcher did not record child PID.' }
    $childIdentity = ([IO.File]::ReadAllText($childPidPath)).Split('|')
    $child = Get-Process -Id ([int]$childIdentity[0]) -ErrorAction Stop
    if ($child.StartTime.ToUniversalTime().Ticks -ne [long]$childIdentity[1]) {
        throw 'Child PID was reused before verification.'
    }
    $childHandle = $child.Handle
    if (-not $child.WaitForExit(15000) -or $child.ExitCode -ne 23) {
        throw "Child exit code mismatch: $($child.ExitCode)"
    }
    $result = ConvertFrom-Json ([IO.File]::ReadAllText($stdoutPath))
    [string[]]$actual = $result.Arguments
    if (-not $result.NoConsole) { throw 'Child acquired a console window.' }
    if ($actual.Count -ne $arguments.Count) { throw 'Argument count changed.' }
    for ($index = 0; $index -lt $arguments.Count; $index++) {
        if ($actual[$index] -cne $arguments[$index]) { throw "Argument $index changed." }
    }
    if ([IO.File]::ReadAllText($stderrPath).Trim() -cne 'stderr after launcher exit') {
        throw 'stderr was lost after the launcher exited.'
    }
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $fast = Start-HiddenProcess -FilePath cmd.exe -ArgumentList @('/d', '/c', 'exit', '17')
        try {
            if (-not $fast.WaitForExit(5000) -or $fast.ExitCode -ne 17) {
                throw "Fast-exit child $attempt did not preserve its handle and exit code."
            }
        }
        finally {
            if (-not $fast.HasExited) {
                Stop-Process -Id $fast.Id -Force -ErrorAction Stop
                if (-not $fast.WaitForExit(5000)) { throw "Fast-exit child $attempt remains running." }
            }
            $fast.Dispose()
        }
    }
    Write-Host 'Hidden process launcher: no console, arguments, logs, exit code and detached lifetime passed.'
}
finally {
    if ($null -eq $child -and (Test-Path -LiteralPath $childPidPath)) {
        $childIdentity = ([IO.File]::ReadAllText($childPidPath)).Split('|')
        $candidate = Get-Process -Id ([int]$childIdentity[0]) -ErrorAction SilentlyContinue
        if ($candidate -and $candidate.StartTime.ToUniversalTime().Ticks -eq [long]$childIdentity[1]) {
            $child = $candidate
        }
    }
    foreach ($ownedProcess in @($child, $launcher)) {
        if ($null -eq $ownedProcess) { continue }
        if (-not $ownedProcess.HasExited) {
            Stop-Process -Id $ownedProcess.Id -Force -ErrorAction Stop
            if (-not $ownedProcess.WaitForExit(5000)) { throw "Test process $($ownedProcess.Id) did not exit." }
        }
        $ownedProcess.Dispose()
    }
    $absoluteRoot = [IO.Path]::GetFullPath($temporaryRoot).TrimEnd('\')
    $allowedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $absoluteRoot.StartsWith($allowedParent, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($absoluteRoot).StartsWith('pc-panel-hub-hidden-launch-')) {
        throw "Refusing to remove a test directory outside the task temp root: $absoluteRoot"
    }
    Remove-Item -LiteralPath $absoluteRoot -Recurse -Force -ErrorAction Stop
    if (Test-Path -LiteralPath $absoluteRoot) { throw "Test directory remains: $absoluteRoot" }
}
