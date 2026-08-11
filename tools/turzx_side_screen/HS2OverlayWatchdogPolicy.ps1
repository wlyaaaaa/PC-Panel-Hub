Set-StrictMode -Version Latest

function Get-HS2OverlayWatchdogDecision {
    param(
        [Parameter(Mandatory = $true)][bool]$IsRunning,
        [Parameter(Mandatory = $true)][DateTime]$LastAttemptUtc,
        [Parameter(Mandatory = $true)][DateTime]$NowUtc,
        [ValidateRange(5, 3600)][int]$RetrySeconds = 30
    )

    if ($IsRunning) {
        return [pscustomobject]@{
            Action = "Healthy"
            RetryAfterSeconds = 0
        }
    }

    if ($LastAttemptUtc -eq [DateTime]::MinValue) {
        return [pscustomobject]@{
            Action = "Activate"
            RetryAfterSeconds = 0
        }
    }

    $elapsedSeconds = [Math]::Max(
        0,
        ($NowUtc.ToUniversalTime() -
            $LastAttemptUtc.ToUniversalTime()).TotalSeconds)
    if ($elapsedSeconds -ge $RetrySeconds) {
        return [pscustomobject]@{
            Action = "Activate"
            RetryAfterSeconds = 0
        }
    }

    return [pscustomobject]@{
        Action = "Wait"
        RetryAfterSeconds = [int][Math]::Ceiling(
            $RetrySeconds - $elapsedSeconds)
    }
}

function Get-HS2OverlayRebindDecision {
    param(
        [Parameter(Mandatory = $true)][bool]$RebindRequired,
        [Parameter(Mandatory = $true)][bool]$IsRunning
    )

    if (-not $RebindRequired) {
        return [pscustomobject]@{ Action = "None" }
    }

    return [pscustomobject]@{
        Action = if ($IsRunning) { "Recycle" } else { "Activate" }
    }
}

function Test-HS2OverlayProcessCandidate {
    param(
        [Parameter(Mandatory = $true)]$Process
    )

    try {
        if ([bool]$Process.HasExited) {
            return $false
        }

        return @($Process.Threads).Count -gt 0
    }
    catch {
        return $false
    }
}

function Get-HS2OverlayProcess {
    param(
        [string]$ProcessName = "HS2.CrystalOverlay",
        [int]$SessionId = (Get-Process -Id $PID).SessionId
    )

    return Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $SessionId } |
        Where-Object { Test-HS2OverlayProcessCandidate -Process $_ } |
        Select-Object -First 1
}

function Stop-HS2OverlayForRebind {
    param(
        [Parameter(Mandatory = $true)]$Process,
        [ValidateRange(100, 10000)][int]$TimeoutMilliseconds = 3000
    )

    try {
        $processId = [int]$Process.Id
        Stop-Process -Id $processId -Force -ErrorAction Stop
        try {
            [void]$Process.WaitForExit($TimeoutMilliseconds)
        }
        catch {
            # Stop-Process may invalidate the original Process handle after
            # the exact process has already exited.  The authoritative check
            # below resolves the PID again instead of trusting that handle.
        }

        $stillRunning = Get-Process -Id $processId -ErrorAction SilentlyContinue
        return [pscustomobject]@{
            Attempted = $true
            Stopped = ($null -eq $stillRunning)
            ProcessId = $processId
            Reason = if ($null -eq $stillRunning) {
                "stopped-for-display-rebind"
            }
            else {
                "process-still-running"
            }
        }
    }
    catch {
        return [pscustomobject]@{
            Attempted = $true
            Stopped = $false
            ProcessId = [int]$Process.Id
            Reason = "stop-failed"
        }
    }
}

function Start-HS2OverlayActivation {
    param(
        [string]$Aumid =
            "CA842C44-3611-4D66-BE9F-5B383BFCEE75_e7nve0azs1zfw!App",
        [string]$ProcessName = "HS2.CrystalOverlay",
        [int]$SessionId = (Get-Process -Id $PID).SessionId
    )

    $existing = Get-HS2OverlayProcess `
        -ProcessName $ProcessName `
        -SessionId $SessionId
    if ($null -ne $existing) {
        return [pscustomobject]@{
            Status = "AlreadyRunning"
            ProcessId = $existing.Id
        }
    }

    $shell = Get-Process -Name "explorer" -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $SessionId } |
        Select-Object -First 1
    if ($null -eq $shell) {
        return [pscustomobject]@{
            Status = "ShellNotReady"
            ProcessId = $null
        }
    }

    $registered = Get-StartApps -ErrorAction Stop |
        Where-Object { $_.AppID -ceq $Aumid } |
        Select-Object -First 1
    if ($null -eq $registered) {
        return [pscustomobject]@{
            Status = "IdentityMissing"
            ProcessId = $null
        }
    }

    Start-Process `
        -FilePath "explorer.exe" `
        -ArgumentList ("shell:AppsFolder\{0}" -f $Aumid) `
        -WindowStyle Hidden
    return [pscustomobject]@{
        Status = "ActivationRequested"
        ProcessId = $null
    }
}
