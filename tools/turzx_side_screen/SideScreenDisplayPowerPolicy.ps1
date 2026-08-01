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
    $currentValues = @{}
    foreach ($operation in $plan) {
        $current = Invoke-HS2DeviceRequest `
            -DevicePath $controller.DevicePath `
            -Type $operation.ReadType `
            -ServicePort $ServicePort `
            -TimeoutSec $TimeoutSec
        $currentValues[$operation.ReadType] = [bool]$current.Data
    }

    $requiresNativeChange = @($plan | Where-Object {
        [bool]$currentValues[$_.ReadType] -ne [bool]$_.Value
    }).Count -gt 0
    $targetSecondaryScreen = ($State -eq "Active")

    if ($controller.IsSecondaryScreen -and ($requiresNativeChange -or -not $targetSecondaryScreen)) {
        $controller = Set-HS2SecondaryScreenMode `
            -Controller $controller `
            -Enabled $false `
            -ServicePort $ServicePort `
            -TimeoutSec $ModeSwitchTimeoutSec
        [void]$changedOperations.Add("SetSecondaryScreen(false)")
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

    if ($targetSecondaryScreen -and -not $controller.IsSecondaryScreen) {
        $controller = Set-HS2SecondaryScreenMode `
            -Controller $controller `
            -Enabled $true `
            -ServicePort $ServicePort `
            -TimeoutSec $ModeSwitchTimeoutSec
        [void]$changedOperations.Add("SetSecondaryScreen(true)")
    }

    return [pscustomobject]@{
        State = $State
        Applied = $true
        Verified = -not $SkipVerification
        ControllerType = $controller.ControllerType
        InitialControllerType = $initialControllerType
        ChangedOperations = @($changedOperations)
    }
}
