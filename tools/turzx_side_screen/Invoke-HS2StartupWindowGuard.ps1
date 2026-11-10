#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][int]$ParentProcessId,
    [Parameter(Mandatory = $true)][int64]$ParentStartTimeUtcTicks,
    [ValidateRange(0, 600)][int]$DurationSeconds = 180,
    [ValidateRange(100, 2000)][int]$PollMilliseconds = 250,
    [string]$ResultPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) { throw 'Interactive desktop session required.' }
if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $ResultPath = Join-Path $PSScriptRoot 'out\hs2-startup-window-guard.json'
}
$policyPath = Join-Path $PSScriptRoot 'WindowsDisplayWindowPolicy.ps1'
. $policyPath
. (Join-Path $PSScriptRoot 'HS2ActiveRecoveryPolicy.ps1')
$policyHash = (Get-FileHash -LiteralPath $policyPath -Algorithm SHA256).Hash
$native = Initialize-HS2ExclusiveWindowGuardNativeMethods
$native::UsePerMonitorV2DpiAwareness()

function Test-ParentAlive {
    try {
        $parent = Get-Process -Id $ParentProcessId -ErrorAction Stop
        return [long]$parent.StartTime.ToUniversalTime().Ticks -eq $ParentStartTimeUtcTicks
    }
    catch { return $false }
}
function Write-GuardState {
    param([Parameter(Mandatory = $true)]$Value)
    $resolved = [IO.Path]::GetFullPath($ResultPath)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $resolved)) | Out-Null
    $temporary = "$resolved.$PID.tmp"
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $resolved -Force
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
$script:knownWallpaperClient = $null
function Get-RunningWallpaperClient {
    # A short-lived CLI process must not look like an engine restart and trigger
    # another play command. Reuse only the same live PID and creation time.
    if ($null -ne $script:knownWallpaperClient) {
        try {
            $known = Get-Process -Id $script:knownWallpaperClient.Id -ErrorAction Stop
            if ($known.StartTime.ToUniversalTime().Ticks -eq $script:knownWallpaperClient.StartTicks) {
                return $script:knownWallpaperClient
            }
        } catch { }
        $script:knownWallpaperClient = $null
    }
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $clients = @(Get-Process -Name wallpaper32,wallpaper64 -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
    if ($clients.Count -ne 1) { return $null }
    try {
        if ([string]::IsNullOrWhiteSpace($clients[0].Path)) { return $null }
        $script:knownWallpaperClient = [pscustomobject]@{
            Id=$clients[0].Id; Path=$clients[0].Path
            StartTicks=$clients[0].StartTime.ToUniversalTime().Ticks
        }
        return $script:knownWallpaperClient
    }
    catch { return $null }
}
function Get-WallpaperDesktopShell {
    $application = [Activator]::CreateInstance([Type]::GetTypeFromProgID('Shell.Application'))
    $windows = $application.Windows()
    [int]$desktopHandle = 0
    $desktop = $windows.FindWindowSW(0, 0, 8, [ref]$desktopHandle, 1)
    if ($null -eq $desktop) { throw 'Desktop Explorer shell unavailable.' }
    return $desktop.Document.Application
}
function Resume-ExistingWallpaper {
    param([Parameter(Mandatory=$true)]$Client)
    # Only the running user's existing engine; never start a missing app, stop it,
    # change its selected content, or continuously override a manual pause.
    $shell = $null
    try {
        $shell = Get-WallpaperDesktopShell
        $shell.ShellExecute($Client.Path, '-control play', (Split-Path -Parent $Client.Path), 'open', 0)
        return 'play-dispatched'
    }
    catch { return 'play-dispatch-failed' }
    finally {
        if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    }
}

function Restart-WallpaperEngine {
    param([AllowNull()]$Client)
    $shell = $null
    try {
        $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
        $targets = @(Get-Process -Name wallpaper32,wallpaper64 -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
        if ($targets.Count -eq 0) {
            if ((Get-ItemPropertyValue -LiteralPath 'HKCU:\Software\WallpaperEngine' -Name faultyShutdown) -ne 1) { return 'restart-manual-exit' }
            $executable = Get-ItemPropertyValue -LiteralPath 'HKCU:\Software\WallpaperEngine' -Name installPath
        }
        elseif ($null -ne $Client) {
            if ($targets.Count -ne 1 -or $targets[0].Id -ne $Client.Id -or
                $targets[0].StartTime.ToUniversalTime().Ticks -ne $Client.StartTicks) { return 'restart-target-not-unique' }
            $executable = $targets[0].Path
        }
        else { return 'restart-target-not-unique' }
        if ([IO.Path]::GetFileName($executable) -ine 'wallpaper64.exe' -or -not (Test-Path -LiteralPath $executable -PathType Leaf)) { return 'restart-path-unavailable' }
        $shell = Get-WallpaperDesktopShell
        if ($targets.Count -eq 1) {
            Stop-Process -InputObject $targets[0] -Force -ErrorAction Stop
            if (-not $targets[0].WaitForExit(3000)) { return 'restart-exit-timeout' }
        }
        Set-ItemProperty -LiteralPath 'HKCU:\Software\WallpaperEngine' -Name faultyShutdown -Value 0 -ErrorAction Stop
        $shell.ShellExecute($executable, '-silent', (Split-Path -Parent $executable), 'open', 0)
        $script:knownWallpaperClient = $null
        return 'restart-dispatched'
    }
    catch { return ('restart-failed: ' + $_.Exception.Message) }
    finally { if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) } }
}

# Reuse the existing startup helper for the parent's whole lifetime. No service,
# new scheduled task, driver operation or second desktop controller is introduced.
$lease = [Threading.Mutex]::new($false, 'Local\TURZX.DesktopContinuity')
$held = $false
try { $held = $lease.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
if (-not $held) { $lease.Dispose(); exit 0 }
$deadline = if ($DurationSeconds -eq 0) { [DateTime]::MaxValue } else { [DateTime]::UtcNow.AddSeconds($DurationSeconds) }
$lastWriteUtc = [DateTime]::MinValue
$lastSignature = $null
$wallpaperEpisode = ''
$episodeSince = [DateTime]::UtcNow
$wallpaperAttempts = 0
$wallpaperNextAttempt = [DateTime]::MinValue
$wallpaperStatus = 'waiting-for-running-engine'
$totalMoves = 0
$recentActions = @()
$wallpaperResumeCount = 0
$wallpaperRestartTopology = ''
$wallpaperRestarted = $false
$wallpaperLastRestartUtc = [DateTime]::MinValue
$wallpaperMismatchSince = [DateTime]::MinValue
$wallpaperHungSince = [DateTime]::MinValue
$wallpaperHungOwner = ''
$wallpaperMissingSince = [DateTime]::MinValue
$wallpaperLayoutStatus = 'waiting-for-render-windows'
$wallpaperRestartReason = ''
$wallpaperRestartResult = ''
$wallpaperRestartCount = 0
try {
    if (Test-Path -LiteralPath $ResultPath) {
        $previous = Get-Content -Raw -LiteralPath $ResultPath | ConvertFrom-Json
        $wallpaperLastRestartUtc = [DateTime]::Parse($previous.WallpaperLastRestartUtc).ToUniversalTime()
        $wallpaperRestartTopology = [string]$previous.WallpaperRestartTopology
        $wallpaperRestartCount = [int]$previous.WallpaperRestartCount
        $wallpaperRestarted = [bool]$previous.WallpaperRestartAttempted -and [long]$previous.WallpaperRecoveryEpoch -eq $ParentStartTimeUtcTicks
        $wallpaperRestartReason = [string]$previous.WallpaperRestartReason
        $wallpaperRestartResult = [string]$previous.WallpaperRestartResult
    }
} catch { } # Older state files have no restart history.
$script:hs2GuardArguments = @{}
function Invoke-RememberedHS2WindowGuard {
    $result = Invoke-HS2ExclusiveWindowGuard @script:hs2GuardArguments
    foreach ($property in @('TargetMonitorDevice', 'SafeMonitorDevice')) {
        if (-not [string]::IsNullOrWhiteSpace([string]$result.$property)) {
            $script:hs2GuardArguments["Preferred$property"] = [string]$result.$property
        }
    }
    return $result
}
try {
    while ([DateTime]::UtcNow -lt $deadline -and (Test-ParentAlive)) {
        try {
            $hs2 = Invoke-RememberedHS2WindowGuard
            $vdd = Invoke-VddWindowReturnGuard
            $totalMoves += @($hs2.AppliedActions).Count + @($vdd.AppliedActions).Count
            foreach ($source in @(@{ Name='hs2'; Actions=@($hs2.AppliedActions)+@($hs2.BackoffEvents) },
                                  @{ Name='vdd'; Actions=@($vdd.AppliedActions) })) {
                foreach ($action in $source.Actions) {
                    $recentActions += [pscustomobject]@{
                        ObservedAtUtc=[DateTime]::UtcNow.ToString('o')
                        Guard=$source.Name; Action=[string]$action.Action
                        ProcessId=[int]$action.ProcessId
                        ProcessName=[string]$action.ProcessName
                        Hwnd=[int64]$action.Hwnd
                        Left=[int]$action.Left; Top=[int]$action.Top
                        Right=[int]$action.Right; Bottom=[int]$action.Bottom
                    }
                }
            }
            if ($recentActions.Count -gt 20) { $recentActions = @($recentActions | Select-Object -Last 20) }
            $monitors = @($native::CaptureMonitors())
            $topology = Get-WallpaperEngineTopologyFingerprint -Monitors $monitors
            $topology += '|' + ((@($monitors | ForEach-Object { '{0},{1},{2},{3}' -f $_.Left,$_.Top,$_.Right,$_.Bottom } | Sort-Object)) -join ';')
            $client = Get-RunningWallpaperClient
            $episode = if ($null -eq $client -or $monitors.Count -eq 0) { '' } else { '{0}|{1}' -f $client.Id,$topology }
            $now = [DateTime]::UtcNow
            if ($episode -cne $wallpaperEpisode) {
                $wallpaperEpisode = $episode
                $episodeSince = $now
                $wallpaperAttempts = 0
                $wallpaperNextAttempt = $now.AddSeconds(2)
                $wallpaperStatus = if ($episode) { 'topology-stabilizing' } else { 'waiting-for-running-engine' }
                $wallpaperMismatchSince = [DateTime]::MinValue
            }
            if ($episode -and $wallpaperStatus -cne 'play-dispatched' -and
                $wallpaperAttempts -lt 3 -and $now -ge $wallpaperNextAttempt) {
                $wallpaperAttempts++
                $wallpaperStatus = Resume-ExistingWallpaper -Client $client
                if ($wallpaperStatus -eq 'play-dispatched') { $wallpaperResumeCount++ }
                $wallpaperNextAttempt = $now.AddSeconds(10 * $wallpaperAttempts)
            }
            if ($topology -cne $wallpaperRestartTopology) {
                $wallpaperRestartTopology = $topology
                $wallpaperRestarted = $wallpaperRestarted -and $wallpaperRestartResult -cne 'restart-verified'
                $wallpaperMismatchSince = [DateTime]::MinValue
            }
            $renderWindows = @(if ($null -ne $client) { $native::CaptureWallpaperWindows($client.Id) })
            $layoutAligned = Test-WallpaperEngineLayoutAligned -Monitors $monitors -Windows $renderWindows
            $hungOwner = if ($null -eq $client) { '' } else { '{0}|{1}' -f $client.Id,$client.StartTicks }
            if ($hungOwner -cne $wallpaperHungOwner) { $wallpaperHungOwner = $hungOwner; $wallpaperHungSince = [DateTime]::MinValue }
            $uiHung = $null -ne $client -and $native::WallpaperUiHung($client.Id)
            if (-not $uiHung) { $wallpaperHungSince = [DateTime]::MinValue }
            elseif ($wallpaperHungSince -eq [DateTime]::MinValue) { $wallpaperHungSince = $now }
            if ($layoutAligned -or $null -eq $client -or $monitors.Count -eq 0 -or $wallpaperAttempts -eq 0) { $wallpaperMismatchSince = [DateTime]::MinValue }
            elseif ($wallpaperMismatchSince -eq [DateTime]::MinValue) { $wallpaperMismatchSince = $now }
            $processCount = @(Get-Process -Name wallpaper32,wallpaper64 -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq [Diagnostics.Process]::GetCurrentProcess().SessionId }).Count
            if ($processCount -eq 1 -and $null -eq $client) { $processCount = 2 } # Unreadable identity is not a crash.
            if ($processCount -ne 0) { $wallpaperMissingSince = [DateTime]::MinValue }
            elseif ($wallpaperMissingSince -eq [DateTime]::MinValue) { $wallpaperMissingSince = $now }
            $faultyShutdown = Get-ItemPropertyValue -LiteralPath 'HKCU:\Software\WallpaperEngine' -Name faultyShutdown -ErrorAction SilentlyContinue
            $decision = Get-WallpaperEngineKeepAliveDecision -ProcessCount $processCount -FaultyShutdown ([int]$faultyShutdown) `
                -HungSeconds $(if ($wallpaperHungSince -eq [DateTime]::MinValue) { 0 } else { ($now-$wallpaperHungSince).TotalSeconds }) `
                -MismatchSeconds $(if ($wallpaperMismatchSince -eq [DateTime]::MinValue) { 0 } else { ($now-$wallpaperMismatchSince).TotalSeconds }) `
                -MissingSeconds $(if ($wallpaperMissingSince -eq [DateTime]::MinValue) { 0 } else { ($now-$wallpaperMissingSince).TotalSeconds }) `
                -SecondsSinceRestart ($now-$wallpaperLastRestartUtc).TotalSeconds -EpisodeRestarted $wallpaperRestarted
            $wallpaperLayoutStatus = $decision
            if ($decision -in @('hung','mismatch','crashed')) {
                $wallpaperRestarted = $true; $wallpaperLastRestartUtc = $now; $wallpaperRestartCount++
                $wallpaperRestartReason = $decision
                $wallpaperRestartResult = Restart-WallpaperEngine -Client $client
            }
            elseif ($wallpaperRestartResult -in @('restart-dispatched','restart-pending')) {
                $wallpaperRestartResult = if ($processCount -eq 1 -and $layoutAligned -and -not $uiHung) { 'restart-verified' }
                    elseif (($now-$wallpaperLastRestartUtc).TotalSeconds -lt 90) { 'restart-pending' } else { 'restart-unverified' }
                # Keep-alive: a verified recovery re-arms later faults (still 15 minutes apart);
                # only an unverified recovery stops further restarts until a new watchdog start.
                if ($wallpaperRestartResult -eq 'restart-verified') { $wallpaperRestarted = $false }
            }
            $signature = '{0}|{1}|{2}|{3}|{4}|{5}' -f $hs2.Status,$vdd.Status,
                $totalMoves,@($hs2.FailedActions).Count,@($vdd.FailedActions).Count,$wallpaperStatus
            if ($signature -cne $lastSignature -or ($now-$lastWriteUtc).TotalSeconds -ge 5) {
                Write-GuardState ([pscustomobject]@{
                    Schema='turzx.hs2-startup-window-guard.v1'
                    Status='running'; ObservedAtUtc=$now.ToString('o')
                    NextCheckUtc=$now.AddMilliseconds($PollMilliseconds).ToString('o')
                    ProcessId=$PID; ParentProcessId=$ParentProcessId
                    PolicySha256=$policyHash; PollMilliseconds=$PollMilliseconds
                    Lifetime=if ($DurationSeconds -eq 0) { 'parent-process' } else { 'bounded-test' }
                    GuardStatus=[string]$hs2.Status; VddStatus=[string]$vdd.Status
                    TargetMonitorAvailable=-not [string]::IsNullOrWhiteSpace([string]$hs2.TargetMonitorDevice)
                    SafeMonitorAvailable=-not [string]::IsNullOrWhiteSpace([string]$hs2.SafeMonitorDevice)
                    AppliedCount=@($hs2.AppliedActions).Count+@($vdd.AppliedActions).Count
                    FailedCount=@($hs2.FailedActions).Count+@($vdd.FailedActions).Count
                    VddVerifiedCount=$vdd.VerifiedCount; TotalMoves=$totalMoves
                    RecentActions=@($recentActions)
                    WallpaperStatus=$wallpaperStatus; WallpaperResumeCount=$wallpaperResumeCount
                    WallpaperAttempts=$wallpaperAttempts
                    WallpaperLayoutStatus=$wallpaperLayoutStatus; WallpaperLayoutAligned=$layoutAligned
                    WallpaperUiHung=$uiHung; WallpaperRestartReason=$wallpaperRestartReason; WallpaperRestartResult=$wallpaperRestartResult
                    WallpaperRestartCount=$wallpaperRestartCount; WallpaperLastRestartUtc=$wallpaperLastRestartUtc.ToString('o')
                    WallpaperRestartTopology=$wallpaperRestartTopology
                    WallpaperRestartAttempted=$wallpaperRestarted
                    WallpaperRecoveryEpoch=$ParentStartTimeUtcTicks
                })
                $lastSignature=$signature; $lastWriteUtc=$now
            }
        }
        catch {
            Write-GuardState ([pscustomobject]@{
                Schema='turzx.hs2-startup-window-guard.v1';Status='error'
                ObservedAtUtc=[DateTime]::UtcNow.ToString('o')
                ProcessId=$PID; ParentProcessId=$ParentProcessId; PolicySha256=$policyHash
                ErrorType=$_.Exception.GetType().FullName; Error=$_.Exception.Message
                WallpaperLastRestartUtc=$wallpaperLastRestartUtc.ToString('o'); WallpaperRestartTopology=$wallpaperRestartTopology
                WallpaperRestartAttempted=$wallpaperRestarted; WallpaperRestartCount=$wallpaperRestartCount
                WallpaperRestartReason=$wallpaperRestartReason; WallpaperRestartResult=$wallpaperRestartResult
                WallpaperRecoveryEpoch=$ParentStartTimeUtcTicks
            })
            Start-Sleep -Seconds 1
        }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
}
finally {
    Write-GuardState ([pscustomobject]@{
        Schema='turzx.hs2-startup-window-guard.v1'; Status='complete'
        ObservedAtUtc=[DateTime]::UtcNow.ToString('o'); ProcessId=$PID
        Reason=if (Test-ParentAlive) { 'startup-window-complete' } else { 'parent-exited' }
        WallpaperLastRestartUtc=$wallpaperLastRestartUtc.ToString('o'); WallpaperRestartTopology=$wallpaperRestartTopology
        WallpaperRestartAttempted=$wallpaperRestarted; WallpaperRestartCount=$wallpaperRestartCount
        WallpaperRestartReason=$wallpaperRestartReason; WallpaperRestartResult=$wallpaperRestartResult
        WallpaperRecoveryEpoch=$ParentStartTimeUtcTicks
    })
    $lease.ReleaseMutex(); $lease.Dispose()
}
