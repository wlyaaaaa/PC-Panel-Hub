Set-StrictMode -Version Latest

function Get-HS2ActiveRecoveryDecision {
    param(
        [Parameter(Mandatory = $true)][bool]$DesiredActive,
        [Parameter(Mandatory = $true)][bool]$VerifiedActive,
        [Parameter(Mandatory = $true)][DateTime]$LastAttemptUtc,
        [Parameter(Mandatory = $true)][DateTime]$LastVerifiedUtc,
        [Parameter(Mandatory = $true)][DateTime]$NowUtc,
        [ValidateRange(1, 3600)][int]$RetrySeconds = 15,
        [ValidateRange(1, 3600)][int]$VerifySeconds = 30
    )

    if (-not $DesiredActive) {
        return [pscustomobject]@{ Action = "Idle"; Reason = "not-requested" }
    }

    if (-not $VerifiedActive) {
        if ($LastAttemptUtc -eq [DateTime]::MinValue -or
            ($NowUtc - $LastAttemptUtc).TotalSeconds -ge $RetrySeconds) {
            return [pscustomobject]@{ Action = "Activate"; Reason = "not-verified" }
        }

        return [pscustomobject]@{ Action = "Wait"; Reason = "retry-backoff" }
    }

    if ($LastVerifiedUtc -eq [DateTime]::MinValue -or
        ($NowUtc - $LastVerifiedUtc).TotalSeconds -ge $VerifySeconds) {
        return [pscustomobject]@{ Action = "Verify"; Reason = "verification-due" }
    }

    return [pscustomobject]@{ Action = "Healthy"; Reason = "verified" }
}

function Get-HS2LConnectRecoveryFollowUpDecision {
    param(
        [Parameter(Mandatory = $true)][bool]$DisplayHealthy,
        [Parameter(Mandatory = $true)][bool]$RecoveryAttempted,
        [Parameter(Mandatory = $true)][DateTime]$RecoveryStartedUtc,
        [Parameter(Mandatory = $true)][DateTime]$NowUtc,
        [ValidateRange(15, 300)][int]$GraceSeconds = 90
    )

    if (-not $DisplayHealthy) {
        return [pscustomobject]@{ Action = "SlowRetry" }
    }

    if (-not $RecoveryAttempted) {
        return [pscustomobject]@{ Action = "RestartService" }
    }

    if ($RecoveryStartedUtc -ne [DateTime]::MinValue -and
        ($NowUtc.ToUniversalTime() -
            $RecoveryStartedUtc.ToUniversalTime()).TotalSeconds -lt
        $GraceSeconds) {
        return [pscustomobject]@{ Action = "FastRetry" }
    }

    return [pscustomobject]@{ Action = "SlowRetry" }
}

function Get-HS2UsbRecoveryPlan {
    param(
        [Parameter(Mandatory = $true)][string]$BoundHubInstanceId,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Hubs,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Children
    )

    if ($BoundHubInstanceId -notlike "USB\VID_1A86&PID_8091\*") {
        return [pscustomobject]@{
            Applicable = $false
            Reason = "verified-hub-binding-invalid"
            HubInstanceId = $null
            ChildInstanceId = $null
            Operations = @()
        }
    }

    $matchingHubs = @(
        $Hubs | Where-Object {
            [bool]$_.Present -and
            [string]$_.InstanceId -ieq $BoundHubInstanceId
        }
    )
    if ($matchingHubs.Count -ne 1) {
        return [pscustomobject]@{
            Applicable = $false
            Reason = if ($matchingHubs.Count -eq 0) { "dedicated-hub-not-found" } else { "dedicated-hub-ambiguous" }
            HubInstanceId = $null
            ChildInstanceId = $null
            Operations = @()
        }
    }

    $hubInstanceId = [string]$matchingHubs[0].InstanceId
    $matchingChildren = @(
        $Children | Where-Object {
            [bool]$_.Present -and
            [string]$_.ParentInstanceId -ieq $hubInstanceId -and
            [string]$_.InstanceId -like "USB\VID_0000&PID_0002\*" -and
            [int]$_.ProblemCode -eq 43 -and
            [string]$_.LocationInfo -like "Port_#0002.*"
        }
    )
    if ($matchingChildren.Count -ne 1) {
        return [pscustomobject]@{
            Applicable = $false
            Reason = if ($matchingChildren.Count -eq 0) { "exact-code43-child-not-found" } else { "exact-code43-child-ambiguous" }
            HubInstanceId = $hubInstanceId
            ChildInstanceId = $null
            Operations = @()
        }
    }

    return [pscustomobject]@{
        Applicable = $true
        Reason = "exact-hs2-code43-topology"
        HubInstanceId = $hubInstanceId
        ChildInstanceId = [string]$matchingChildren[0].InstanceId
        Operations = @("RestartDedicatedHub", "RemoveExactFailedChild", "ScanDevices")
    }
}

function Test-HS2UsbRecoveryPlanContinuity {
    param(
        [Parameter(Mandatory = $true)][object]$InitialPlan,
        [Parameter(Mandatory = $true)][object]$FreshPlan
    )

    # A hub restart legitimately replaces the transient VID_0000 instance id.
    # Both plans have already proved there is exactly one Code 43 child on port
    # 2 of the previously verified dedicated HS2 hub, so requiring the old
    # ephemeral child id makes the safe recovery path fail after every reboot.
    return (
        [bool]$InitialPlan.Applicable -and
        [bool]$FreshPlan.Applicable -and
        [string]$FreshPlan.HubInstanceId -ieq [string]$InitialPlan.HubInstanceId
    )
}

function Get-HS2UsbRecoveryDispatchDecision {
    param(
        [Parameter(Mandatory = $true)][bool]$RecoveryAttempted,
        [Parameter(Mandatory = $true)][bool]$ContinuationPending
    )

    if (-not $RecoveryAttempted) {
        return [pscustomobject]@{ Action = "RestartDedicatedHub" }
    }

    if ($ContinuationPending) {
        # The first pass already restarted the exact verified hub.  If Windows
        # published the exact port-two Code 43 node only after that bounded
        # wait, resume at the narrowly scoped child-removal phase instead of
        # repeatedly power-cycling the hub.
        return [pscustomobject]@{ Action = "RemoveExactChild" }
    }

    return [pscustomobject]@{ Action = "Wait" }
}

function Read-HS2UsbTopologyBinding {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    try {
        $binding = Get-Content -Raw -LiteralPath $Path -ErrorAction Stop |
            ConvertFrom-Json -ErrorAction Stop
        if ([int]$binding.SchemaVersion -ne 1 -or
            [string]$binding.HubInstanceId -notlike "USB\VID_1A86&PID_8091\*" -or
            [string]$binding.DisplayInstanceId -notlike "USB\VID_1A86&PID_AD23\*" -or
            [string]$binding.LedInstanceId -notlike "USB\VID_0416&PID_8051\*") {
            return $null
        }

        return $binding
    }
    catch {
        return $null
    }
}

function Get-HS2HealthyUsbTopologyBinding {
    $presentDevices = @(Get-PnpDevice -PresentOnly -ErrorAction Stop)
    $healthyHubs = @(
        $presentDevices | Where-Object {
            [string]$_.InstanceId -like "USB\VID_1A86&PID_8091\*" -and
            [string]$_.Status -eq "OK"
        }
    )
    $healthyDisplays = @(
        $presentDevices | Where-Object {
            [string]$_.InstanceId -like "USB\VID_1A86&PID_AD23\*" -and
            [string]$_.Status -eq "OK"
        }
    )
    $healthyLeds = @(
        $presentDevices | Where-Object {
            [string]$_.InstanceId -like "USB\VID_0416&PID_8051\*" -and
            [string]$_.Status -eq "OK"
        }
    )

    $matches = New-Object "System.Collections.Generic.List[object]"
    foreach ($display in $healthyDisplays) {
        $displayParent = [string](Get-HS2PnpPropertyValue `
            -InstanceId ([string]$display.InstanceId) `
            -KeyName "DEVPKEY_Device_Parent")
        $hub = @(
            $healthyHubs | Where-Object {
                [string]$_.InstanceId -ieq $displayParent
            }
        )
        if ($hub.Count -ne 1) {
            continue
        }

        $siblingLeds = @(
            $healthyLeds | Where-Object {
                [string](Get-HS2PnpPropertyValue `
                    -InstanceId ([string]$_.InstanceId) `
                    -KeyName "DEVPKEY_Device_Parent") -ieq $displayParent
            }
        )
        if ($siblingLeds.Count -ne 1) {
            continue
        }

        [void]$matches.Add([pscustomobject]@{
            SchemaVersion = 1
            HubInstanceId = [string]$hub[0].InstanceId
            DisplayInstanceId = [string]$display.InstanceId
            LedInstanceId = [string]$siblingLeds[0].InstanceId
            ConfirmedAtUtc = [DateTime]::UtcNow.ToString("o")
        })
    }

    if ($matches.Count -eq 1) {
        return $matches[0]
    }
    return $null
}

function Save-HS2UsbTopologyBinding {
    param([Parameter(Mandatory = $true)][string]$Path)

    $binding = Get-HS2HealthyUsbTopologyBinding
    if ($null -eq $binding) {
        return $false
    }

    $existing = Read-HS2UsbTopologyBinding -Path $Path
    if ($null -ne $existing -and
        [string]$existing.HubInstanceId -ieq [string]$binding.HubInstanceId -and
        [string]$existing.DisplayInstanceId -ieq [string]$binding.DisplayInstanceId -and
        [string]$existing.LedInstanceId -ieq [string]$binding.LedInstanceId) {
        return $true
    }

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temporaryPath = "{0}.{1}.tmp" -f $Path, [Guid]::NewGuid().ToString("N")
    try {
        $json = $binding | ConvertTo-Json -Compress
        [IO.File]::WriteAllText(
            $temporaryPath,
            $json,
            [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
        return $true
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-HS2PnpPropertyValue {
    param(
        [Parameter(Mandatory = $true)][string]$InstanceId,
        [Parameter(Mandatory = $true)][string]$KeyName
    )

    try {
        $property = Get-PnpDeviceProperty `
            -InstanceId $InstanceId `
            -KeyName $KeyName `
            -ErrorAction Stop
        return $property.Data
    }
    catch {
        return $null
    }
}

function Get-HS2UsbRecoverySnapshot {
    $presentDevices = @(Get-PnpDevice -PresentOnly -ErrorAction Stop)
    $hubs = @(
        $presentDevices |
            Where-Object { [string]$_.InstanceId -like "USB\VID_1A86&PID_8091\*" } |
            ForEach-Object {
                [pscustomobject]@{
                    InstanceId = [string]$_.InstanceId
                    Present = $true
                }
            }
    )
    $children = @(
        $presentDevices |
            Where-Object { [string]$_.InstanceId -like "USB\VID_0000&PID_0002\*" } |
            ForEach-Object {
                $instanceId = [string]$_.InstanceId
                $problemCode = Get-HS2PnpPropertyValue `
                    -InstanceId $instanceId `
                    -KeyName "DEVPKEY_Device_ProblemCode"
                [pscustomobject]@{
                    InstanceId = $instanceId
                    ParentInstanceId = [string](Get-HS2PnpPropertyValue `
                        -InstanceId $instanceId `
                        -KeyName "DEVPKEY_Device_Parent")
                    ProblemCode = if ($null -eq $problemCode) { -1 } else { [int]$problemCode }
                    LocationInfo = [string](Get-HS2PnpPropertyValue `
                        -InstanceId $instanceId `
                        -KeyName "DEVPKEY_Device_LocationInfo")
                    Present = $true
                }
            }
    )

    return [pscustomobject]@{
        Hubs = $hubs
        Children = $children
    }
}

function Wait-HS2UsbRecoveryPlan {
    param(
        [Parameter(Mandatory = $true)][string]$BoundHubInstanceId,
        [ValidateRange(1, 30)][int]$TimeoutSeconds = 10,
        [ValidateRange(1, 5000)][int]$PollMilliseconds = 500,
        [scriptblock]$SnapshotProvider = { Get-HS2UsbRecoverySnapshot },
        [scriptblock]$DelayAction = {
            param([int]$Milliseconds)
            Start-Sleep -Milliseconds $Milliseconds
        }
    )

    # A dedicated-hub restart can briefly remove the failed port node before
    # Windows publishes its replacement.  Poll for the same already-verified
    # hub + exact port-two Code 43 topology instead of consuming the one safe
    # recovery attempt on a single transiently empty snapshot.
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $snapshot = & $SnapshotProvider
        if ($null -ne $snapshot) {
            $plan = Get-HS2UsbRecoveryPlan `
                -BoundHubInstanceId $BoundHubInstanceId `
                -Hubs @($snapshot.Hubs) `
                -Children @($snapshot.Children)
            if ($plan.Applicable) {
                return $plan
            }
        }

        if ([DateTime]::UtcNow -lt $deadline) {
            & $DelayAction $PollMilliseconds
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    return $null
}

function Test-HS2UsbDisplayHealthy {
    $device = Get-PnpDevice -PresentOnly -ErrorAction Stop |
        Where-Object {
            [string]$_.InstanceId -like "USB\VID_1A86&PID_AD23\*"
        } |
        Where-Object { [string]$_.Status -eq "OK" } |
        Select-Object -First 1
    return $null -ne $device
}

function Wait-HS2LConnectControllerReady {
    param(
        [ValidateRange(1, 60)][int]$MaximumAttempts = 10,
        [ValidateRange(1, 5000)][int]$PollMilliseconds = 500,
        [ValidateRange(1, 65535)][int]$ServicePort = 11021,
        [scriptblock]$ControllerProbe,
        [scriptblock]$DelayAction = {
            param([int]$Milliseconds)
            Start-Sleep -Milliseconds $Milliseconds
        }
    )

    if ($null -eq $ControllerProbe) {
        $ControllerProbe = {
            param([int]$Port)
            try {
                $controller = Get-HS2Controller `
                    -ServicePort $Port `
                    -TimeoutSec 1
                return $null -ne $controller
            }
            catch {
                return $false
            }
        }
    }

    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        if ([bool](& $ControllerProbe $ServicePort)) {
            return $true
        }
        if ($attempt -lt $MaximumAttempts) {
            & $DelayAction $PollMilliseconds
        }
    }

    return $false
}

function Invoke-HS2PnPUtil {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $pnputil = Join-Path $env:SystemRoot "System32\pnputil.exe"
    $output = @(& $pnputil @Arguments 2>&1)
    return [pscustomobject]@{
        ExitCode = [int]$LASTEXITCODE
        Output = @($output | ForEach-Object { [string]$_ })
    }
}

function Wait-HS2UsbDisplayHealthy {
    param([ValidateRange(1, 30)][int]$TimeoutSeconds = 10)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (Test-HS2UsbDisplayHealthy) {
            return $true
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

function Invoke-HS2UsbRecovery {
    param(
        [Parameter(Mandatory = $true)][string]$BindingPath,
        [ValidateRange(1, 30)][int]$ReenumerationTimeoutSeconds = 10,
        [switch]$SkipDedicatedHubRestart
    )

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        return [pscustomobject]@{
            Attempted = $false
            Recovered = $false
            Reason = "requires-elevation"
            Operations = @()
        }
    }

    $binding = Read-HS2UsbTopologyBinding -Path $BindingPath
    if ($null -eq $binding) {
        return [pscustomobject]@{
            Attempted = $false
            Recovered = $false
            Reason = "verified-hub-binding-missing"
            Operations = @()
        }
    }

    $snapshot = Get-HS2UsbRecoverySnapshot
    $plan = Get-HS2UsbRecoveryPlan `
        -BoundHubInstanceId ([string]$binding.HubInstanceId) `
        -Hubs $snapshot.Hubs `
        -Children $snapshot.Children
    if (-not $plan.Applicable) {
        return [pscustomobject]@{
            Attempted = $false
            Recovered = $false
            Reason = $plan.Reason
            Operations = @()
        }
    }

    $operations = New-Object "System.Collections.Generic.List[string]"
    $freshPlan = $plan
    if (-not $SkipDedicatedHubRestart) {
        $restart = Invoke-HS2PnPUtil -Arguments @("/restart-device", $plan.HubInstanceId)
        [void]$operations.Add("RestartDedicatedHub")
        if ($restart.ExitCode -ne 0) {
            return [pscustomobject]@{
                Attempted = $true
                Recovered = $false
                Reason = "dedicated-hub-restart-failed"
                Operations = @($operations)
            }
        }

        if (Wait-HS2UsbDisplayHealthy -TimeoutSeconds $ReenumerationTimeoutSeconds) {
            return [pscustomobject]@{
                Attempted = $true
                Recovered = $true
                Reason = "display-returned-after-hub-restart"
                Operations = @($operations)
            }
        }

        $freshPlan = Wait-HS2UsbRecoveryPlan `
            -BoundHubInstanceId ([string]$binding.HubInstanceId) `
            -TimeoutSeconds $ReenumerationTimeoutSeconds
        if ($null -eq $freshPlan -or
            -not (Test-HS2UsbRecoveryPlanContinuity `
                -InitialPlan $plan `
                -FreshPlan $freshPlan)) {
            return [pscustomobject]@{
                Attempted = $true
                Recovered = $false
                Reason = "topology-changed-after-hub-restart"
                Operations = @($operations)
            }
        }
    }

    $remove = Invoke-HS2PnPUtil -Arguments @("/remove-device", $freshPlan.ChildInstanceId)
    [void]$operations.Add("RemoveExactFailedChild")
    if ($remove.ExitCode -ne 0) {
        return [pscustomobject]@{
            Attempted = $true
            Recovered = $false
            Reason = "exact-child-remove-failed"
            Operations = @($operations)
        }
    }

    $scan = Invoke-HS2PnPUtil -Arguments @("/scan-devices")
    [void]$operations.Add("ScanDevices")
    if ($scan.ExitCode -ne 0) {
        return [pscustomobject]@{
            Attempted = $true
            Recovered = $false
            Reason = "device-scan-failed"
            Operations = @($operations)
        }
    }

    $recovered = Wait-HS2UsbDisplayHealthy -TimeoutSeconds $ReenumerationTimeoutSeconds
    return [pscustomobject]@{
        Attempted = $true
        Recovered = $recovered
        Reason = if ($recovered) { "display-returned-after-exact-reenumeration" } else { "cold-power-cycle-may-be-required" }
        Operations = @($operations)
    }
}

function Invoke-HS2LConnectServiceRecovery {
    param(
        [ValidateRange(1, 30)][int]$RunningTimeoutSeconds = 10,
        [ValidateRange(1, 60)][int]$ControllerReadyAttempts = 10,
        [ValidateRange(1, 5000)][int]$ControllerPollMilliseconds = 500,
        [ValidateRange(1, 65535)][int]$ServicePort = 11021
    )

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        return [pscustomobject]@{ Attempted = $false; Recovered = $false; Reason = "requires-elevation" }
    }
    if (-not (Test-HS2UsbDisplayHealthy)) {
        return [pscustomobject]@{ Attempted = $false; Recovered = $false; Reason = "hs2-usb-display-not-present" }
    }

    $service = Get-Service -Name "LConnectService" -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return [pscustomobject]@{ Attempted = $false; Recovered = $false; Reason = "lconnect-service-not-found" }
    }

    try {
        Restart-Service -Name "LConnectService" -Force -ErrorAction Stop
        $service = Get-Service -Name "LConnectService" -ErrorAction Stop
        $service.WaitForStatus(
            [ServiceProcess.ServiceControllerStatus]::Running,
            [TimeSpan]::FromSeconds($RunningTimeoutSeconds))
        $controllerReady = Wait-HS2LConnectControllerReady `
            -MaximumAttempts $ControllerReadyAttempts `
            -PollMilliseconds $ControllerPollMilliseconds `
            -ServicePort $ServicePort
        return [pscustomobject]@{
            Attempted = $true
            Recovered = $controllerReady
            Reason = if ($controllerReady) {
                "lconnect-controller-ready"
            }
            else {
                "lconnect-service-running-controller-pending"
            }
        }
    }
    catch {
        return [pscustomobject]@{ Attempted = $true; Recovered = $false; Reason = "lconnect-service-restart-failed" }
    }
}
