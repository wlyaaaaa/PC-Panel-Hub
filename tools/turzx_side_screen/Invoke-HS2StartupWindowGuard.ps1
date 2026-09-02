#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][int]$ParentProcessId,
    [Parameter(Mandatory = $true)][int64]$ParentStartTimeUtcTicks,
    [ValidateRange(10, 600)][int]$DurationSeconds = 180,
    [ValidateRange(100, 2000)][int]$PollMilliseconds = 250,
    [string]$ResultPath = (Join-Path $PSScriptRoot 'out\hs2-startup-window-guard.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WindowsDisplayWindowPolicy.ps1')

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
    [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $resolved -Force
}

$deadline = [DateTime]::UtcNow.AddSeconds($DurationSeconds)
$lastWriteUtc = [DateTime]::MinValue
$lastSignature = $null
while ([DateTime]::UtcNow -lt $deadline -and (Test-ParentAlive)) {
    try {
        $result = Invoke-HS2ExclusiveWindowGuard
        $signature = '{0}|{1}|{2}|{3}|{4}' -f `
            [string]$result.Status,
            [string]$result.TargetMonitorDevice,
            [string]$result.SafeMonitorDevice,
            @($result.AppliedActions).Count,
            @($result.FailedActions).Count
        if ($signature -cne $lastSignature -or ([DateTime]::UtcNow - $lastWriteUtc).TotalSeconds -ge 5) {
            Write-GuardState ([pscustomobject]@{
                    Schema = 'turzx.hs2-startup-window-guard.v1'
                    Status = 'running'
                    ObservedAtUtc = [DateTime]::UtcNow.ToString('o')
                    GuardStatus = [string]$result.Status
                    TargetMonitorAvailable = -not [string]::IsNullOrWhiteSpace([string]$result.TargetMonitorDevice)
                    SafeMonitorAvailable = -not [string]::IsNullOrWhiteSpace([string]$result.SafeMonitorDevice)
                    AppliedCount = @($result.AppliedActions).Count
                    FailedCount = @($result.FailedActions).Count
                })
            $lastSignature = $signature
            $lastWriteUtc = [DateTime]::UtcNow
        }
    }
    catch {
        Write-GuardState ([pscustomobject]@{
                Schema = 'turzx.hs2-startup-window-guard.v1'
                Status = 'error'
                ObservedAtUtc = [DateTime]::UtcNow.ToString('o')
                ErrorType = $_.Exception.GetType().FullName
                Error = $_.Exception.Message
            })
    }
    Start-Sleep -Milliseconds $PollMilliseconds
}
Write-GuardState ([pscustomobject]@{
        Schema = 'turzx.hs2-startup-window-guard.v1'
        Status = 'complete'
        ObservedAtUtc = [DateTime]::UtcNow.ToString('o')
        Reason = if (Test-ParentAlive) { 'startup-window-complete' } else { 'parent-exited' }
    })
