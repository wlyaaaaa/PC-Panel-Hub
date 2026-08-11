Set-StrictMode -Version Latest

function Get-HS2PowerStatePlan {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("Active", "Sleep", "Shutdown")]
        [string]$State
    )

    switch ($State) {
        "Active" {
            [pscustomobject]@{ Type = "SetOfflineModeClock"; ReadType = "GetOfflineModeClock"; Value = $true }
            [pscustomobject]@{ Type = "SetIsScreenOn"; ReadType = "GetIsScreenOn"; Value = $true }
        }
        "Sleep" {
            [pscustomobject]@{ Type = "SetOfflineModeClock"; ReadType = "GetOfflineModeClock"; Value = $true }
            [pscustomobject]@{ Type = "SetIsScreenOn"; ReadType = "GetIsScreenOn"; Value = $false }
        }
        "Shutdown" {
            [pscustomobject]@{ Type = "SetOfflineModeClock"; ReadType = "GetOfflineModeClock"; Value = $false }
            [pscustomobject]@{ Type = "SetIsScreenOn"; ReadType = "GetIsScreenOn"; Value = $false }
        }
    }
}

function Get-HS2SecondaryPromotionDecision {
    param(
        [Parameter(Mandatory = $true)][bool]$NativeActive,
        [Parameter(Mandatory = $true)][bool]$SecondaryVerified,
        [Parameter(Mandatory = $true)][DateTime]$NativeStableSinceUtc,
        [Parameter(Mandatory = $true)][DateTime]$LastPromotionAttemptUtc,
        [Parameter(Mandatory = $true)][DateTime]$NowUtc,
        [ValidateRange(1, 600)][int]$StabilitySeconds = 30
    )

    if (-not $NativeActive) {
        return [pscustomobject]@{
            Action = "ActivateNative"
            RetryAfterSeconds = 0
        }
    }
    if ($SecondaryVerified) {
        return [pscustomobject]@{
            Action = "Verified"
            RetryAfterSeconds = 0
        }
    }

    $nativeAgeSeconds = if ($NativeStableSinceUtc -eq [DateTime]::MinValue) {
        0
    }
    else {
        [Math]::Max(0, ($NowUtc - $NativeStableSinceUtc).TotalSeconds)
    }
    if ($nativeAgeSeconds -lt $StabilitySeconds) {
        return [pscustomobject]@{
            Action = "WaitNativeStability"
            RetryAfterSeconds = [Math]::Ceiling($StabilitySeconds - $nativeAgeSeconds)
        }
    }

    if ($LastPromotionAttemptUtc -ne [DateTime]::MinValue) {
        return [pscustomobject]@{
            Action = "HoldNative"
            RetryAfterSeconds = 0
        }
    }

    return [pscustomobject]@{
        Action = "PromoteSecondary"
        RetryAfterSeconds = 0
    }
}

function Get-HS2ResumeEventDecision {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet(7, 18)]
        [int]$EventType,
        [Parameter(Mandatory = $true)][DateTime]$LastHandledUtc,
        [Parameter(Mandatory = $true)][DateTime]$NowUtc,
        [ValidateRange(1, 120)][int]$MergeSeconds = 30
    )

    if ($LastHandledUtc -ne [DateTime]::MinValue -and
        ($NowUtc.ToUniversalTime() - $LastHandledUtc.ToUniversalTime()).TotalSeconds -lt $MergeSeconds) {
        return [pscustomobject]@{
            Action = "Ignore"
            EventType = $EventType
            RetryAfterSeconds = [Math]::Max(
                0,
                $MergeSeconds -
                    ($NowUtc.ToUniversalTime() - $LastHandledUtc.ToUniversalTime()).TotalSeconds)
        }
    }

    return [pscustomobject]@{
        Action = "Handle"
        EventType = $EventType
        RetryAfterSeconds = 0
    }
}

function Get-HS2Controller {
    param(
        [ValidateRange(1, 65535)][int]$ServicePort = 11021,
        [ValidateRange(1, 10)][int]$TimeoutSec = 2
    )

    $uri = "http://127.0.0.1:{0}/?action=SyncControllerList" -f $ServicePort
    $controllers = Invoke-RestMethod -Method Post -Uri $uri -TimeoutSec $TimeoutSec
    foreach ($property in $controllers.PSObject.Properties) {
        $controllerType = [int64]$property.Value
        if ($controllerType -eq 17104896 -or $controllerType -eq 17104897) {
            return [pscustomobject]@{
                DevicePath = [string]$property.Name
                ControllerType = $controllerType
                IsSecondaryScreen = ($controllerType -eq 17104897)
            }
        }
    }

    throw "L-Connect did not report an HS2 OLED Curve controller."
}

function Wait-HS2ControllerMode {
    param(
        [Parameter(Mandatory = $true)][bool]$SecondaryScreen,
        [ValidateRange(1, 65535)][int]$ServicePort = 11021,
        [ValidateRange(1, 30)][int]$TimeoutSec = 20
    )

    $expectedType = if ($SecondaryScreen) { 17104897 } else { 17104896 }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    $lastError = $null
    do {
        try {
            $controller = Get-HS2Controller -ServicePort $ServicePort -TimeoutSec 2
            if ($controller.ControllerType -eq $expectedType) {
                return $controller
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }

        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)

    $suffix = if ([string]::IsNullOrWhiteSpace($lastError)) { "" } else { " Last error: $lastError" }
    throw "HS2 did not enter controller mode $expectedType within $TimeoutSec seconds.$suffix"
}

function Set-HS2SecondaryScreenMode {
    param(
        [Parameter(Mandatory = $true)]$Controller,
        [Parameter(Mandatory = $true)][bool]$Enabled,
        [ValidateRange(1, 65535)][int]$ServicePort = 11021,
        [ValidateRange(1, 30)][int]$TimeoutSec = 20
    )

    if ([bool]$Controller.IsSecondaryScreen -eq $Enabled) {
        return $Controller
    }

    Invoke-HS2DeviceRequest `
        -DevicePath $Controller.DevicePath `
        -Type "SetSecondaryScreen" `
        -Body $Enabled `
        -ServicePort $ServicePort `
        -TimeoutSec 2 | Out-Null
    return Wait-HS2ControllerMode `
        -SecondaryScreen $Enabled `
        -ServicePort $ServicePort `
        -TimeoutSec $TimeoutSec
}

function Start-HS2MonitorModeTransition {
    param(
        [ValidateRange(1, 65535)][int]$ServicePort = 11021,
        [ValidateRange(1, 10)][int]$TimeoutSec = 2
    )

    $controller = Get-HS2Controller -ServicePort $ServicePort -TimeoutSec $TimeoutSec
    if (-not $controller.IsSecondaryScreen) {
        return $false
    }

    Invoke-HS2DeviceRequest `
        -DevicePath $controller.DevicePath `
        -Type "SetSecondaryScreen" `
        -Body $false `
        -ServicePort $ServicePort `
        -TimeoutSec $TimeoutSec | Out-Null
    return $true
}

function Invoke-HS2DeviceRequest {
    param(
        [Parameter(Mandatory = $true)][string]$DevicePath,
        [Parameter(Mandatory = $true)][string]$Type,
        [object]$Body,
        [ValidateRange(1, 65535)][int]$ServicePort = 11021,
        [ValidateRange(1, 10)][int]$TimeoutSec = 2
    )

    $encodedPath = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($DevicePath))
    $uri = "http://127.0.0.1:{0}/?action=Device&devicePath={1}&type={2}" -f `
        $ServicePort,
        [uri]::EscapeDataString($encodedPath),
        [uri]::EscapeDataString($Type)
    $bodyText = if ($PSBoundParameters.ContainsKey("Body")) {
        ConvertTo-Json -InputObject $Body -Compress
    }
    else {
        ""
    }
    $response = Invoke-RestMethod -Method Post -Uri $uri -ContentType "application/json" -Body $bodyText -TimeoutSec $TimeoutSec
    if ($null -eq $response -or -not [bool]$response.Success) {
        $message = if ($null -ne $response) { [string]$response.Message } else { "empty response" }
        throw "L-Connect request $Type failed: $message"
    }

    return $response
}

function Invoke-HS2PowerState {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("Active", "Sleep", "Shutdown")]
        [string]$State,
        [ValidateRange(1, 65535)][int]$ServicePort = 11021,
        [ValidateRange(1, 10)][int]$TimeoutSec = 2,
        [ValidateRange(1, 30)][int]$ModeSwitchTimeoutSec = 20,
        [switch]$MonitorModeAlreadyRequested,
        [switch]$EnableSecondaryScreen,
        [switch]$SkipVerification,
        [switch]$DryRun
    )

    $plan = @(Get-HS2PowerStatePlan -State $State)
    if ($DryRun) {
        return [pscustomobject]@{
            State = $State
            Applied = $false
            Verified = $false
            Plan = $plan
        }
    }

    if ($MonitorModeAlreadyRequested -and $State -eq "Active") {
        throw "MonitorModeAlreadyRequested is only valid for Sleep or Shutdown."
    }

    $controller = if ($MonitorModeAlreadyRequested) {
        Wait-HS2ControllerMode `
            -SecondaryScreen $false `
            -ServicePort $ServicePort `
            -TimeoutSec $ModeSwitchTimeoutSec
    }
    else {
        Get-HS2Controller -ServicePort $ServicePort -TimeoutSec $TimeoutSec
    }
    $changedOperations = New-Object "System.Collections.Generic.List[string]"
    $initialControllerType = if ($MonitorModeAlreadyRequested) { 17104897 } else { $controller.ControllerType }
    if ($MonitorModeAlreadyRequested) {
        [void]$changedOperations.Add("SetSecondaryScreen(false)")
    }
    # Active is deliberately tri-state.  With no override, preserve whichever
    # controller mode survived firmware/boot enumeration.  An explicit false
    # is the only path that demotes to native mode; an explicit true is the
    # only path that promotes into the Windows secondary-display topology.
    # Sleep and shutdown still target native mode before applying their plan.
    $secondaryModePolicy = if ($State -ne "Active") {
        "Native"
    }
    elseif (-not $PSBoundParameters.ContainsKey("EnableSecondaryScreen")) {
        "Preserve"
    }
    elseif ([bool]$EnableSecondaryScreen) {
        "Secondary"
    }
    else {
        "Native"
    }
    $targetSecondaryScreen = switch ($secondaryModePolicy) {
        "Secondary" { $true }
        "Native" { $false }
        default { $null }
    }

    if ($null -ne $targetSecondaryScreen -and
        [bool]$controller.IsSecondaryScreen -ne [bool]$targetSecondaryScreen) {
        $controller = Set-HS2SecondaryScreenMode `
            -Controller $controller `
            -Enabled ([bool]$targetSecondaryScreen) `
            -ServicePort $ServicePort `
            -TimeoutSec $ModeSwitchTimeoutSec
        [void]$changedOperations.Add(
            "SetSecondaryScreen({0})" -f ([bool]$targetSecondaryScreen).ToString().ToLowerInvariant())
    }

    # A mode change replaces the controller/device path and can reset screen
    # state.  Always read, repair, and verify the power plan on the final
    # controller instead of trusting values captured before the transition.
    $currentValues = @{}
    foreach ($operation in $plan) {
        $current = Invoke-HS2DeviceRequest `
            -DevicePath $controller.DevicePath `
            -Type $operation.ReadType `
            -ServicePort $ServicePort `
            -TimeoutSec $TimeoutSec
        $currentValues[$operation.ReadType] = [bool]$current.Data
    }

    foreach ($operation in $plan) {
        if ([bool]$currentValues[$operation.ReadType] -eq [bool]$operation.Value) {
            continue
        }

        Invoke-HS2DeviceRequest `
            -DevicePath $controller.DevicePath `
            -Type $operation.Type `
            -Body ([bool]$operation.Value) `
            -ServicePort $ServicePort `
            -TimeoutSec $TimeoutSec | Out-Null
        [void]$changedOperations.Add([string]$operation.Type)
    }

    if (-not $SkipVerification) {
        foreach ($operation in $plan) {
            $response = Invoke-HS2DeviceRequest `
                -DevicePath $controller.DevicePath `
                -Type $operation.ReadType `
                -ServicePort $ServicePort `
                -TimeoutSec $TimeoutSec
            if ([bool]$response.Data -ne [bool]$operation.Value) {
                throw "L-Connect read-back mismatch for $($operation.ReadType) in state $State."
            }
        }
    }

    return [pscustomobject]@{
        State = $State
        Applied = $true
        Verified = -not $SkipVerification
        ControllerType = $controller.ControllerType
        InitialControllerType = $initialControllerType
        SecondaryScreenPolicy = $secondaryModePolicy
        SecondaryScreenRequested = ($secondaryModePolicy -eq "Secondary")
        ChangedOperations = @($changedOperations)
    }
}
