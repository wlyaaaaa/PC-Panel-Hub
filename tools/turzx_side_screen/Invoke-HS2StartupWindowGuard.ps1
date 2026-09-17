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
function Resume-ExistingWallpaper {
    param([Parameter(Mandatory=$true)]$Client)
    # Only the running user's existing engine; never start a missing app, stop it,
    # change its selected content, or continuously override a manual pause.
    $shell = $null
    try {
        $shell = [Activator]::CreateInstance([Type]::GetTypeFromProgID('Shell.Application'))
        $shell.ShellExecute($Client.Path, '-control play', (Split-Path -Parent $Client.Path), 'open', 0)
        return 'play-dispatched'
    }
    catch { return 'play-dispatch-failed' }
    finally {
        if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    }
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
$wallpaperResumeCount = 0
try {
    while ([DateTime]::UtcNow -lt $deadline -and (Test-ParentAlive)) {
        try {
            $hs2 = Invoke-HS2ExclusiveWindowGuard
            $vdd = Invoke-VddWindowReturnGuard
            $totalMoves += @($hs2.AppliedActions).Count + @($vdd.AppliedActions).Count
            $monitors = @($native::CaptureMonitors())
            $topology = Get-WallpaperEngineTopologyFingerprint -Monitors $monitors
            $client = Get-RunningWallpaperClient
            $episode = if ($null -eq $client -or $monitors.Count -eq 0) { '' } else { '{0}|{1}' -f $client.Id,$topology }
            $now = [DateTime]::UtcNow
            if ($episode -cne $wallpaperEpisode) {
                $wallpaperEpisode = $episode
                $episodeSince = $now
                $wallpaperAttempts = 0
                $wallpaperNextAttempt = $now.AddSeconds(2)
                $wallpaperStatus = if ($episode) { 'topology-stabilizing' } else { 'waiting-for-running-engine' }
            }
            if ($episode -and $wallpaperStatus -cne 'play-dispatched' -and
                $wallpaperAttempts -lt 3 -and $now -ge $wallpaperNextAttempt) {
                $wallpaperAttempts++
                $wallpaperStatus = Resume-ExistingWallpaper -Client $client
                if ($wallpaperStatus -eq 'play-dispatched') { $wallpaperResumeCount++ }
                $wallpaperNextAttempt = $now.AddSeconds(10 * $wallpaperAttempts)
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
                    WallpaperStatus=$wallpaperStatus; WallpaperResumeCount=$wallpaperResumeCount
                    WallpaperAttempts=$wallpaperAttempts
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
    })
    $lease.ReleaseMutex(); $lease.Dispose()
}
