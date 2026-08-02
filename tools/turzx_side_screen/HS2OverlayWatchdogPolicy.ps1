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

function Get-HS2OverlayProcess {
    param(
        [string]$ProcessName = "HS2.CrystalOverlay",
        [int]$SessionId = (Get-Process -Id $PID).SessionId
    )

    return Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $SessionId } |
        Select-Object -First 1
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
