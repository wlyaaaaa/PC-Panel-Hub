Set-StrictMode -Version Latest

function Get-TurzxSerialEndpointRecoveryDecision {
    [CmdletBinding()]
    param(
        [ValidateRange(0, 1000)]
        [int]$ConsecutiveBrightnessFailures,

        [Parameter(Mandatory)]
        [DateTime]$LastAttemptUtc,

        [DateTime]$NowUtc = [DateTime]::UtcNow,

        [ValidateRange(2, 20)]
        [int]$FailureThreshold = 3,

        [ValidateRange(30, 3600)]
        [int]$RetrySeconds = 300
    )

    if ($ConsecutiveBrightnessFailures -lt $FailureThreshold) {
        return [pscustomobject]@{
            Action = "None"
            RetryAfterSeconds = 0
        }
    }

    if ($LastAttemptUtc -eq [DateTime]::MinValue) {
        return [pscustomobject]@{
            Action = "RestartEndpoint"
            RetryAfterSeconds = 0
        }
    }

    $elapsedSeconds = [Math]::Max(
        0,
        ($NowUtc.ToUniversalTime() -
            $LastAttemptUtc.ToUniversalTime()).TotalSeconds)
    if ($elapsedSeconds -ge $RetrySeconds) {
        return [pscustomobject]@{
            Action = "RestartEndpoint"
            RetryAfterSeconds = 0
        }
    }

    return [pscustomobject]@{
        Action = "Wait"
        RetryAfterSeconds = [int][Math]::Ceiling(
            $RetrySeconds - $elapsedSeconds)
    }
}

function Get-TurzxShutdownEventDecision {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$EventType,

        [Parameter(Mandatory)]
        [DateTime]$WatchdogStartedUtc,

        [DateTime]$NowUtc = [DateTime]::UtcNow,

        [ValidateRange(0, 3600)]
        [int]$StartupGraceSeconds = 180
    )

    $parsedType = 0
    if ($null -eq $EventType -or -not [int]::TryParse([string]$EventType, [ref]$parsedType)) {
        return [pscustomobject]@{
            Action = "Ignore"
            Reason = "missing-or-invalid-type"
            Type = $null
            AgeSeconds = [Math]::Max(0, ($NowUtc.ToUniversalTime() - $WatchdogStartedUtc.ToUniversalTime()).TotalSeconds)
        }
    }

    $ageSeconds = [Math]::Max(0, ($NowUtc.ToUniversalTime() - $WatchdogStartedUtc.ToUniversalTime()).TotalSeconds)
    if ($parsedType -eq 0) {
        return [pscustomobject]@{
            Action = "Ignore"
            Reason = "logoff"
            Type = $parsedType
            AgeSeconds = $ageSeconds
        }
    }

    if ($parsedType -ne 1) {
        return [pscustomobject]@{
            Action = "Ignore"
            Reason = "unsupported-type"
            Type = $parsedType
            AgeSeconds = $ageSeconds
        }
    }

    if ($ageSeconds -lt $StartupGraceSeconds) {
        return [pscustomobject]@{
            Action = "Ignore"
            Reason = "startup-grace"
            Type = $parsedType
            AgeSeconds = $ageSeconds
        }
    }

    return [pscustomobject]@{
        Action = "Shutdown"
        Reason = "confirmed"
        Type = $parsedType
        AgeSeconds = $ageSeconds
    }
}
