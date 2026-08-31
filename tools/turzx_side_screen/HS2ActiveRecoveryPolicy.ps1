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

function Get-HS2RecoveryEscalationDecision {
    param(
        [Parameter(Mandatory = $true)][DateTime]$WatchdogStartedUtc,
        [Parameter(Mandatory = $true)][DateTime]$NowUtc,
        [ValidateRange(1, 3600)][int]$GraceSeconds = 120
    )

    $ageSeconds = [Math]::Max(
        0,
        ($NowUtc.ToUniversalTime() -
            $WatchdogStartedUtc.ToUniversalTime()).TotalSeconds)
    return [pscustomobject]@{
        Action = if ($ageSeconds -ge $GraceSeconds) { "Allow" } else { "Wait" }
        AgeSeconds = $ageSeconds
        RetryAfterSeconds = [Math]::Max(0, $GraceSeconds - $ageSeconds)
    }
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

function Get-HS2BoundDeviceCode10Decision {
    param(
        [object]$Binding,
        [object]$Snapshot
    )

    if ($null -eq $Binding -or $null -eq $Snapshot -or
        $null -eq $Snapshot.PSObject.Properties["Devices"]) {
        return [pscustomobject]@{
            Action = "Continue"
            Reason = "binding-or-snapshot-unavailable"
            DeviceInstanceIds = @()
        }
    }

    $boundIds = @(
        [string]$Binding.HubInstanceId,
        [string]$Binding.DisplayInstanceId,
        [string]$Binding.DisplayInterfaceInstanceId,
        [string]$Binding.LedInstanceId
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    if ($boundIds.Count -eq 0) {
        return [pscustomobject]@{
            Action = "Continue"
            Reason = "binding-identities-unavailable"
            DeviceInstanceIds = @()
        }
    }

    $code10Devices = @(
        @($Snapshot.Devices) | Where-Object {
            $isPresent = ($null -eq $_.PSObject.Properties["Present"]) -or [bool]$_.Present
            $isPresent -and
            $boundIds -contains [string]$_.InstanceId -and
            [int]$_.ProblemCode -eq 10
        }
    )
    if ($code10Devices.Count -gt 0) {
        return [pscustomobject]@{
            Action = "FailClosed"
            Reason = "bound-lian-li-code10-fail-closed"
            DeviceInstanceIds = @($code10Devices | ForEach-Object { [string]$_.InstanceId } | Sort-Object -Unique)
        }
    }

    return [pscustomobject]@{
        Action = "Continue"
        Reason = "no-bound-lian-li-code10"
        DeviceInstanceIds = @()
    }
}

function Get-WallpaperEngineTopologyFingerprint {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Monitors
    )

    $identityParts = @(
        $Monitors |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace([string]$_.DeviceName)
            } |
            ForEach-Object {
                "{0}|primary={1}" -f `
                    ([string]$_.DeviceName).ToUpperInvariant(),
                    ([bool]$_.IsPrimary)
            } |
            Sort-Object -Unique
    )
    return ($identityParts -join ";")
}

function Get-WallpaperEngineMttBindingDecision {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$MttMonitorNodes,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$BackingNodes,
        [Parameter(Mandatory = $true)][scriptblock]$PropertyReader
    )

    $mttMonitors = @(
        $MttMonitorNodes | Where-Object {
            [string]$_.InstanceId -like "DISPLAY\MTT1337\*"
        }
    )
    if ($MttMonitorNodes.Count -ne 1 -or $mttMonitors.Count -ne 1) {
        return [pscustomobject]@{
            Found = $false
            Reason = "mtt-monitor-ambiguous"
            Device = $null
        }
    }

    $verifiedBackings = New-Object "System.Collections.Generic.List[object]"
    foreach ($backingNode in @($BackingNodes | Where-Object {
                [string]$_.InstanceId -like "ROOT\DISPLAY\*"
            })) {
        $hardwareIds = @(
            & $PropertyReader `
                ([string]$backingNode.InstanceId) `
                "DEVPKEY_Device_HardwareIds" |
                ForEach-Object { [string]$_ }
        )
        if ($hardwareIds.Count -eq 1 -and
            $hardwareIds[0] -ceq "Root\MttVDD") {
            [void]$verifiedBackings.Add($backingNode)
        }
    }
    if ($verifiedBackings.Count -ne 1) {
        return [pscustomobject]@{
            Found = $false
            Reason = "mtt-backing-ambiguous"
            Device = $null
        }
    }

    $mttMonitor = $mttMonitors[0]
    $mttBacking = $verifiedBackings[0]
    $mttPresent = & $PropertyReader `
        ([string]$mttMonitor.InstanceId) `
        "DEVPKEY_Device_IsPresent"
    $mttProblemCode = & $PropertyReader `
        ([string]$mttMonitor.InstanceId) `
        "DEVPKEY_Device_ProblemCode"
    $backingPresent = & $PropertyReader `
        ([string]$mttBacking.InstanceId) `
        "DEVPKEY_Device_IsPresent"
    $backingProblemCode = & $PropertyReader `
        ([string]$mttBacking.InstanceId) `
        "DEVPKEY_Device_ProblemCode"
    $device = [pscustomobject]@{
        InstanceId = [string]$mttMonitor.InstanceId
        Present = [bool]$mttPresent
        Status = [string]$mttMonitor.Status
        ProblemCode = if ($null -eq $mttProblemCode) { -1 } else { [int]$mttProblemCode }
        BackingInstanceId = [string]$mttBacking.InstanceId
        BackingPresent = [bool]$backingPresent
        BackingStatus = [string]$mttBacking.Status
        BackingProblemCode = if ($null -eq $backingProblemCode) { -1 } else { [int]$backingProblemCode }
        HardwareIdVerified = $true
    }
    $healthy = (
        [bool]$device.Present -and
        [string]$device.Status -ceq "OK" -and
        [int]$device.ProblemCode -eq 0 -and
        [bool]$device.BackingPresent -and
        [string]$device.BackingStatus -ceq "OK" -and
        [int]$device.BackingProblemCode -eq 0
    )
    return [pscustomobject]@{
        Found = $true
        Reason = if ($healthy) { "verified-mtt-binding" } else { "mtt-binding-not-healthy" }
        Device = $device
    }
}

function Get-WallpaperEngineDisplayHealthDecision {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Monitors,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$MttDevices,
        [Parameter(Mandatory = $true)][bool]$Hs2SecondaryActive,
        [Parameter(Mandatory = $true)][bool]$Hs2BindingHealthy
    )

    $activeMonitors = @(
        $Monitors | Where-Object {
            -not [string]::IsNullOrWhiteSpace([string]$_.DeviceName)
        }
    )
    $primaryMonitors = @($activeMonitors | Where-Object { [bool]$_.IsPrimary })
    if ($primaryMonitors.Count -ne 1) {
        return [pscustomobject]@{
            Eligible = $false
            Reason = "primary-display-ambiguous"
            PrimaryMonitorDevice = $null
            MttDeviceInstanceId = $null
        }
    }
    if ($activeMonitors.Count -lt 3) {
        return [pscustomobject]@{
            Eligible = $false
            Reason = "active-display-topology-incomplete"
            PrimaryMonitorDevice = [string]$primaryMonitors[0].DeviceName
            MttDeviceInstanceId = $null
        }
    }
    if ($activeMonitors.Count -gt 3) {
        return [pscustomobject]@{
            Eligible = $false
            Reason = "active-display-topology-ambiguous"
            PrimaryMonitorDevice = [string]$primaryMonitors[0].DeviceName
            MttDeviceInstanceId = $null
        }
    }

    $healthyMttDevices = @(
        $MttDevices | Where-Object {
            $backingPresent = (
                $null -ne $_.PSObject.Properties["BackingPresent"] -and
                [bool]$_.BackingPresent)
            $backingStatus = if ($null -eq $_.PSObject.Properties["BackingStatus"]) {
                ""
            }
            else {
                [string]$_.BackingStatus
            }
            $backingProblemCode = if (
                $null -eq $_.PSObject.Properties["BackingProblemCode"]) {
                -1
            }
            else {
                [int]$_.BackingProblemCode
            }
            $hardwareIdVerified = (
                $null -ne $_.PSObject.Properties["HardwareIdVerified"] -and
                [bool]$_.HardwareIdVerified)
            ([bool]$_.Present) -and
            ([string]$_.Status -ceq "OK") -and
            ([int]$_.ProblemCode -eq 0) -and
            $backingPresent -and
            ($backingStatus -ceq "OK") -and
            ($backingProblemCode -eq 0) -and
            $hardwareIdVerified
        }
    )
    if ($healthyMttDevices.Count -ne 1) {
        return [pscustomobject]@{
            Eligible = $false
            Reason = "mtt-display-not-healthy"
            PrimaryMonitorDevice = [string]$primaryMonitors[0].DeviceName
            MttDeviceInstanceId = $null
        }
    }
    if (-not $Hs2SecondaryActive -or -not $Hs2BindingHealthy) {
        return [pscustomobject]@{
            Eligible = $false
            Reason = "lian-li-display-binding-not-healthy"
            PrimaryMonitorDevice = [string]$primaryMonitors[0].DeviceName
            MttDeviceInstanceId = [string]$healthyMttDevices[0].InstanceId
        }
    }

    return [pscustomobject]@{
        Eligible = $true
        Reason = "three-display-bindings-healthy"
        PrimaryMonitorDevice = [string]$primaryMonitors[0].DeviceName
        MttDeviceInstanceId = [string]$healthyMttDevices[0].InstanceId
    }
}

function Get-WallpaperEngineRebindDecision {
    param(
        [AllowEmptyString()][string]$BaselineFingerprint,
        [AllowEmptyString()][string]$PendingFingerprint,
        [AllowEmptyString()][string]$CurrentFingerprint,
        [Parameter(Mandatory = $true)][DateTime]$PendingSinceUtc,
        [Parameter(Mandatory = $true)][DateTime]$LastRebindUtc,
        [Parameter(Mandatory = $true)][DateTime]$NowUtc,
        [Parameter(Mandatory = $true)][bool]$Healthy,
        [ValidateRange(5, 600)][int]$StabilitySeconds = 30,
        [ValidateRange(60, 7200)][int]$CooldownSeconds = 900
    )

    if ([string]::IsNullOrWhiteSpace($CurrentFingerprint)) {
        return [pscustomobject]@{
            Action = "WaitForTopology"
            PendingFingerprint = ""
            PendingSinceUtc = [DateTime]::MinValue
            RetryAfterSeconds = 0
        }
    }
    if ([string]::IsNullOrWhiteSpace($BaselineFingerprint)) {
        if (-not $Healthy) {
            return [pscustomobject]@{
                Action = "WaitForHealth"
                PendingFingerprint = ""
                PendingSinceUtc = [DateTime]::MinValue
                RetryAfterSeconds = 0
            }
        }
        return [pscustomobject]@{
            Action = "Baseline"
            PendingFingerprint = ""
            PendingSinceUtc = [DateTime]::MinValue
            RetryAfterSeconds = 0
        }
    }
    if ($CurrentFingerprint -ceq $BaselineFingerprint) {
        return [pscustomobject]@{
            Action = if ($Healthy) { "Healthy" } else { "WaitForHealth" }
            PendingFingerprint = ""
            PendingSinceUtc = [DateTime]::MinValue
            RetryAfterSeconds = 0
        }
    }

    if ($PendingFingerprint -cne $CurrentFingerprint -or
        $PendingSinceUtc -eq [DateTime]::MinValue) {
        return [pscustomobject]@{
            Action = "Stabilizing"
            PendingFingerprint = $CurrentFingerprint
            PendingSinceUtc = $NowUtc
            RetryAfterSeconds = $StabilitySeconds
        }
    }
    if (-not $Healthy) {
        return [pscustomobject]@{
            Action = "WaitForHealth"
            PendingFingerprint = $PendingFingerprint
            PendingSinceUtc = $PendingSinceUtc
            RetryAfterSeconds = 0
        }
    }

    $stableForSeconds = [Math]::Max(
        0,
        ($NowUtc.ToUniversalTime() - $PendingSinceUtc.ToUniversalTime()).TotalSeconds)
    if ($stableForSeconds -lt $StabilitySeconds) {
        return [pscustomobject]@{
            Action = "Stabilizing"
            PendingFingerprint = $PendingFingerprint
            PendingSinceUtc = $PendingSinceUtc
            RetryAfterSeconds = [Math]::Ceiling($StabilitySeconds - $stableForSeconds)
        }
    }

    if ($LastRebindUtc -ne [DateTime]::MinValue) {
        $elapsedSinceRebind = [Math]::Max(
            0,
            ($NowUtc.ToUniversalTime() - $LastRebindUtc.ToUniversalTime()).TotalSeconds)
        if ($elapsedSinceRebind -lt $CooldownSeconds) {
            return [pscustomobject]@{
                Action = "Cooldown"
                PendingFingerprint = $PendingFingerprint
                PendingSinceUtc = $PendingSinceUtc
                RetryAfterSeconds = [Math]::Ceiling($CooldownSeconds - $elapsedSinceRebind)
            }
        }
    }

    return [pscustomobject]@{
        Action = "Rebind"
        PendingFingerprint = $PendingFingerprint
        PendingSinceUtc = $PendingSinceUtc
        RetryAfterSeconds = 0
    }
}

function Get-HS2UsbRecoveryPlan {
    param(
        [Parameter(Mandatory = $true)][string]$BoundHubInstanceId,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Hubs,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Children,
        [object]$Binding,
        [AllowEmptyCollection()][object[]]$Devices = @()
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
    $boundHubIsHealthy = (
        [string]$matchingHubs[0].Status -eq "OK" -and
        [int]$matchingHubs[0].ProblemCode -eq 0
    )
    if (-not $boundHubIsHealthy) {
        return [pscustomobject]@{
            Applicable = $false
            Reason = "bound-hub-not-healthy"
            RecoveryKind = $null
            HubInstanceId = $hubInstanceId
            ChildInstanceId = $null
            Operations = @()
        }
    }

    if ($null -eq $Binding -or
        [string]$Binding.HubInstanceId -ine $BoundHubInstanceId -or
        [string]$Binding.DisplayInstanceId -notlike "USB\VID_1A86&PID_AD23\*" -or
        [string]$Binding.LedInstanceId -notlike "USB\VID_0416&PID_8051\*") {
        return [pscustomobject]@{
            Applicable = $false
            Reason = "verified-sibling-binding-missing-or-invalid"
            RecoveryKind = $null
            HubInstanceId = $hubInstanceId
            ChildInstanceId = $null
            Operations = @()
        }
    }

    $matchingLedSiblings = @(
        $Devices | Where-Object {
            [bool]$_.Present -and
            [string]$_.InstanceId -ieq [string]$Binding.LedInstanceId -and
            [string]$_.ParentInstanceId -ieq $hubInstanceId -and
            [string]$_.Status -eq "OK" -and
            [int]$_.ProblemCode -eq 0
        }
    )
    if ($matchingLedSiblings.Count -ne 1) {
        return [pscustomobject]@{
            Applicable = $false
            Reason = "bound-led-sibling-not-healthy-or-ambiguous"
            RecoveryKind = $null
            HubInstanceId = $hubInstanceId
            ChildInstanceId = $null
            Operations = @()
        }
    }

    $presentBoundDisplays = @(
        $Devices | Where-Object {
            [bool]$_.Present -and
            [string]$_.InstanceId -ieq [string]$Binding.DisplayInstanceId
        }
    )
    if ($presentBoundDisplays.Count -gt 0) {
        return [pscustomobject]@{
            Applicable = $false
            Reason = "bound-ad23-already-present"
            RecoveryKind = $null
            HubInstanceId = $hubInstanceId
            ChildInstanceId = $null
            Operations = @()
        }
    }

    $matchingChildren = @(
        $Children | Where-Object {
            [bool]$_.Present -and
            [string]$_.ParentInstanceId -ieq $hubInstanceId -and
            [string]$_.InstanceId -like "USB\VID_0000&PID_0002\*" -and
            [int]$_.ProblemCode -eq 43 -and
            [string]$_.LocationInfo -like "Port_#0002.*"
        }
    )
    if ($matchingChildren.Count -gt 1) {
        return [pscustomobject]@{
            Applicable = $false
            Reason = "exact-code43-child-ambiguous"
            RecoveryKind = $null
            HubInstanceId = $hubInstanceId
            ChildInstanceId = $null
            Operations = @()
        }
    }

    if ($matchingChildren.Count -eq 1) {
        return [pscustomobject]@{
            Applicable = $true
            Reason = "exact-hs2-code43-topology"
            RecoveryKind = "ExactCode43"
            HubInstanceId = $hubInstanceId
            ChildInstanceId = [string]$matchingChildren[0].InstanceId
            Operations = @("RestartDedicatedHub", "RemoveExactFailedChild", "ScanDevices")
        }
    }
    return [pscustomobject]@{
        Applicable = $false
        Reason = "exact-code43-child-not-found"
        RecoveryKind = $null
        HubInstanceId = $hubInstanceId
        ChildInstanceId = $null
        Operations = @()
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

function Get-HS2UsbAutomaticRecoveryDecision {
    param(
        [Parameter(Mandatory = $true)][bool]$Enabled,
        [Parameter(Mandatory = $true)][bool]$PlanApplicable
    )

    if (-not $PlanApplicable) {
        return [pscustomobject]@{
            Action = "NoPlan"
            Reason = "exact-recovery-plan-unavailable"
        }
    }

    if (-not $Enabled) {
        return [pscustomobject]@{
            Action = "Suppress"
            Reason = "automatic-usb-pnp-recovery-disabled"
        }
    }

    return [pscustomobject]@{
        Action = "Dispatch"
        Reason = "explicit-usb-pnp-recovery-opt-in"
    }
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
        $schemaVersion = [int]$binding.SchemaVersion
        if ($schemaVersion -notin @(1, 2) -or
            [string]$binding.HubInstanceId -notlike "USB\VID_1A86&PID_8091\*" -or
            [string]$binding.DisplayInstanceId -notlike "USB\VID_1A86&PID_AD23\*" -or
            [string]$binding.LedInstanceId -notlike "USB\VID_0416&PID_8051\*") {
            return $null
        }
        if ($schemaVersion -eq 2 -and
            [string]$binding.DisplayInterfaceInstanceId -notlike
                "USB\VID_1A86&PID_AD23&MI_00\*") {
            return $null
        }

        return $binding
    }
    catch {
        return $null
    }
}

function Get-HS2HealthyUsbTopologyBinding {
    param([object]$Snapshot)

    $snapshot = if ($null -eq $Snapshot) {
        Get-HS2UsbRecoverySnapshot
    }
    else {
        $Snapshot
    }
    $healthyHubs = @(
        $snapshot.Devices | Where-Object {
            [bool]$_.Present -and
            [string]$_.InstanceId -like "USB\VID_1A86&PID_8091\*" -and
            [string]$_.Status -eq "OK" -and
            [int]$_.ProblemCode -eq 0
        }
    )
    $healthyDisplays = @(
        $snapshot.Devices | Where-Object {
            [bool]$_.Present -and
            [string]$_.InstanceId -like "USB\VID_1A86&PID_AD23\*" -and
            [string]$_.Status -eq "OK" -and
            [int]$_.ProblemCode -eq 0
        }
    )
    $healthyDisplayInterfaces = @(
        $snapshot.Devices | Where-Object {
            [bool]$_.Present -and
            [string]$_.InstanceId -like "USB\VID_1A86&PID_AD23&MI_00\*" -and
            [string]$_.Status -eq "OK" -and
            [int]$_.ProblemCode -eq 0
        }
    )
    $healthyLeds = @(
        $snapshot.Devices | Where-Object {
            [bool]$_.Present -and
            [string]$_.InstanceId -like "USB\VID_0416&PID_8051\*" -and
            [string]$_.Status -eq "OK" -and
            [int]$_.ProblemCode -eq 0
        }
    )

    $matches = New-Object "System.Collections.Generic.List[object]"
    foreach ($display in $healthyDisplays) {
        $displayParent = [string]$display.ParentInstanceId
        $hub = @(
            $healthyHubs | Where-Object {
                [string]$_.InstanceId -ieq $displayParent
            }
        )
        if ($hub.Count -ne 1) {
            continue
        }

        $displayInterfaces = @(
            $healthyDisplayInterfaces | Where-Object {
                [string]$_.ParentInstanceId -ieq [string]$display.InstanceId
            }
        )
        if ($displayInterfaces.Count -ne 1) {
            continue
        }

        $siblingLeds = @(
            $healthyLeds | Where-Object {
                [string]$_.ParentInstanceId -ieq $displayParent
            }
        )
        if ($siblingLeds.Count -ne 1) {
            continue
        }

        [void]$matches.Add([pscustomobject]@{
            SchemaVersion = 2
            HubInstanceId = [string]$hub[0].InstanceId
            DisplayInstanceId = [string]$display.InstanceId
            DisplayInterfaceInstanceId = [string]$displayInterfaces[0].InstanceId
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
        [int]$existing.SchemaVersion -eq [int]$binding.SchemaVersion -and
        [string]$existing.HubInstanceId -ieq [string]$binding.HubInstanceId -and
        [string]$existing.DisplayInstanceId -ieq [string]$binding.DisplayInstanceId -and
        [string]$existing.DisplayInterfaceInstanceId -ieq
            [string]$binding.DisplayInterfaceInstanceId -and
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
    # Get-PnpDevice -PresentOnly can still return a historical disconnected
    # devnode.  Hardware recovery must use DEVPKEY_Device_IsPresent instead of
    # inferring physical presence from that cmdlet's result set.
    $candidateDevices = @(
        Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object {
            [string]$_.InstanceId -like "USB\VID_1A86&PID_8091\*" -or
            [string]$_.InstanceId -like "USB\VID_0416&PID_8051\*" -or
            [string]$_.InstanceId -like "USB\VID_1A86&PID_AD23&MI_00\*" -or
            [string]$_.InstanceId -like "USB\VID_1A86&PID_AD23\*" -or
            [string]$_.InstanceId -like "USB\VID_1CBE&PID_A068\*" -or
            [string]$_.InstanceId -like "USB\VID_A108&PID_EAEF\*" -or
            [string]$_.InstanceId -like "USB\VID_0000&PID_0002\*"
        }
    )
    $devices = @(
        $candidateDevices | ForEach-Object {
            $instanceId = [string]$_.InstanceId
            $isPresent = Get-HS2PnpPropertyValue `
                -InstanceId $instanceId `
                -KeyName "DEVPKEY_Device_IsPresent"
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
                Present = [bool]$isPresent
                Status = [string]$_.Status
            }
        }
    )
    $hubs = @(
        $devices | Where-Object {
            [bool]$_.Present -and
            [string]$_.InstanceId -like "USB\VID_1A86&PID_8091\*"
        }
    )
    $children = @(
        $devices | Where-Object {
            [bool]$_.Present -and
            [string]$_.InstanceId -like "USB\VID_0000&PID_0002\*"
        }
    )

    return [pscustomobject]@{
        Hubs = $hubs
        Children = $children
        Devices = $devices
    }
}

function Resolve-HS2ControllerHub {
    param(
        [Parameter(Mandatory = $true)]$Binding,
        [Parameter(Mandatory = $true)]$Snapshot
    )

    $healthyHubs = @(
        $Snapshot.Devices | Where-Object {
            [bool]$_.Present -and
            [string]$_.InstanceId -like "USB\VID_1A86&PID_8091\*" -and
            [string]$_.Status -eq "OK" -and
            [int]$_.ProblemCode -eq 0
        }
    )
    $boundHubs = @(
        $healthyHubs | Where-Object {
            [string]$_.InstanceId -ieq [string]$Binding.HubInstanceId
        }
    )
    if ($boundHubs.Count -eq 1) {
        return [pscustomobject]@{
            Hub = $boundHubs[0]
            RebindRequired = $false
            Reason = "verified-bound-hub"
        }
    }
    if ($boundHubs.Count -gt 1) {
        return [pscustomobject]@{
            Hub = $null
            RebindRequired = $false
            Reason = "bound-hub-ambiguous"
        }
    }

    # A direct-header correction changes the Windows instance id of the same
    # physical 8091 hub. Admit that change only from one complete sibling
    # topology: controller on internal port two and LED on internal port three.
    # This is provisional identity evidence only; the normal AD23 + MI_00 path
    # still has to verify and atomically replace the persisted schema-2 binding.
    $candidates = New-Object "System.Collections.Generic.List[object]"
    foreach ($hub in $healthyHubs) {
        $hubId = [string]$hub.InstanceId
        $leds = @(
            $Snapshot.Devices | Where-Object {
                [bool]$_.Present -and
                [string]$_.ParentInstanceId -ieq $hubId -and
                [string]$_.InstanceId -like "USB\VID_0416&PID_8051\*" -and
                [string]$_.Status -eq "OK" -and
                [int]$_.ProblemCode -eq 0 -and
                [string]$_.LocationInfo -like "Port_#0003*"
            }
        )
        $normalEndpoints = @(
            $Snapshot.Devices | Where-Object {
                [bool]$_.Present -and
                [string]$_.ParentInstanceId -ieq $hubId -and
                [string]$_.Status -eq "OK" -and
                [int]$_.ProblemCode -eq 0 -and
                [string]$_.LocationInfo -like "Port_#0002*" -and
                (
                    [string]$_.InstanceId -like "USB\VID_1CBE&PID_A068\*" -or
                    [string]$_.InstanceId -like "USB\VID_1A86&PID_AD23\*"
                )
            }
        )
        $bootloaderEndpoints = @(
            $Snapshot.Devices | Where-Object {
                [bool]$_.Present -and
                [string]$_.ParentInstanceId -ieq $hubId -and
                [string]$_.InstanceId -like "USB\VID_A108&PID_EAEF\*" -and
                [string]$_.LocationInfo -like "Port_#0002*"
            }
        )
        if ($leds.Count -eq 1 -and
            ($normalEndpoints.Count + $bootloaderEndpoints.Count) -eq 1) {
            [void]$candidates.Add([pscustomobject]@{
                Hub = $hub
                RebindRequired = $true
                Reason = "unique-reenumerated-controller-topology"
            })
        }
    }

    if ($candidates.Count -eq 1) {
        return $candidates[0]
    }
    return [pscustomobject]@{
        Hub = $null
        RebindRequired = $false
        Reason = if ($candidates.Count -gt 1) {
            "controller-topology-rebind-ambiguous"
        }
        else {
            "bound-hub-not-healthy"
        }
    }
}

function Get-HS2ControllerReadiness {
    param(
        [Parameter(Mandatory = $true)][string]$BindingPath,
        [object]$Snapshot
    )

    $binding = Read-HS2UsbTopologyBinding -Path $BindingPath
    if ($null -eq $binding) {
        return [pscustomobject]@{
            Action = "Wait"
            Reason = "verified-hub-binding-missing"
            EndpointInstanceId = $null
        }
    }

    $snapshot = if ($null -eq $Snapshot) {
        Get-HS2UsbRecoverySnapshot
    }
    else {
        $Snapshot
    }
    $code10Decision = Get-HS2BoundDeviceCode10Decision `
        -Binding $binding `
        -Snapshot $snapshot
    if ($code10Decision.Action -ceq "FailClosed") {
        return [pscustomobject]@{
            Action = "Wait"
            Reason = [string]$code10Decision.Reason
            EndpointInstanceId = $null
            ResolvedHubInstanceId = $null
            RebindRequired = $false
            Code10DeviceInstanceIds = @($code10Decision.DeviceInstanceIds)
        }
    }
    $hubResolution = Resolve-HS2ControllerHub `
        -Binding $binding `
        -Snapshot $snapshot
    if ($null -eq $hubResolution.Hub) {
        return [pscustomobject]@{
            Action = "Wait"
            Reason = [string]$hubResolution.Reason
            EndpointInstanceId = $null
            ResolvedHubInstanceId = $null
            RebindRequired = $false
        }
    }
    $resolvedHubInstanceId = [string]$hubResolution.Hub.InstanceId

    $normalEndpoints = @(
        $snapshot.Devices | Where-Object {
            [bool]$_.Present -and
            [string]$_.ParentInstanceId -ieq $resolvedHubInstanceId -and
            [string]$_.Status -eq "OK" -and
            [int]$_.ProblemCode -eq 0 -and
            (
                [string]$_.InstanceId -like "USB\VID_1CBE&PID_A068\*" -or
                [string]$_.InstanceId -like "USB\VID_1A86&PID_AD23\*"
            )
        }
    )
    $bootloaderEndpoints = @(
        $snapshot.Devices | Where-Object {
            [bool]$_.Present -and
            [string]$_.ParentInstanceId -ieq $resolvedHubInstanceId -and
            [string]$_.InstanceId -like "USB\VID_A108&PID_EAEF\*"
        }
    )

    if ($normalEndpoints.Count -gt 1 -or
        $bootloaderEndpoints.Count -gt 1 -or
        ($normalEndpoints.Count -gt 0 -and $bootloaderEndpoints.Count -gt 0)) {
        return [pscustomobject]@{
            Action = "Wait"
            Reason = "bound-controller-endpoint-ambiguous"
            EndpointInstanceId = $null
            ResolvedHubInstanceId = $resolvedHubInstanceId
            RebindRequired = [bool]$hubResolution.RebindRequired
        }
    }
    if ($normalEndpoints.Count -eq 1) {
        return [pscustomobject]@{
            Action = "Ready"
            Reason = "bound-controller-endpoint-ready"
            EndpointInstanceId = [string]$normalEndpoints[0].InstanceId
            ResolvedHubInstanceId = $resolvedHubInstanceId
            RebindRequired = [bool]$hubResolution.RebindRequired
        }
    }
    if ($bootloaderEndpoints.Count -eq 1) {
        $locationProperty = $bootloaderEndpoints[0].PSObject.Properties["LocationInfo"]
        $locationInfo = if ($null -eq $locationProperty) {
            ""
        }
        else {
            [string]$locationProperty.Value
        }
        if ($locationInfo -notlike "Port_#0002*") {
            return [pscustomobject]@{
                Action = "Wait"
                Reason = "bound-controller-endpoint-ambiguous"
                EndpointInstanceId = $null
                ResolvedHubInstanceId = $resolvedHubInstanceId
                RebindRequired = [bool]$hubResolution.RebindRequired
            }
        }
        return [pscustomobject]@{
            Action = "Wait"
            Reason = "bound-controller-in-bootloader-mode"
            EndpointInstanceId = [string]$bootloaderEndpoints[0].InstanceId
            ResolvedHubInstanceId = $resolvedHubInstanceId
            RebindRequired = [bool]$hubResolution.RebindRequired
        }
    }

    return [pscustomobject]@{
        Action = "Wait"
        Reason = "bound-controller-endpoint-missing"
        EndpointInstanceId = $null
        ResolvedHubInstanceId = $resolvedHubInstanceId
        RebindRequired = [bool]$hubResolution.RebindRequired
    }
}

function Get-HS2LConnectServiceRecoveryEligibility {
    param(
        [Parameter(Mandatory = $true)][string]$BindingPath,
        [object]$Snapshot
    )

    # A native-first boot intentionally has no AD23 Windows display.  Service
    # recovery may therefore use the native A068 controller endpoint, but only
    # when it is on the same previously verified dedicated hub.  This is a
    # read-only eligibility gate; it never mutates USB/PnP state.
    $binding = Read-HS2UsbTopologyBinding -Path $BindingPath
    if ($null -eq $binding) {
        return [pscustomobject]@{
            Eligible = $false
            Reason = "verified-hub-binding-missing"
            EndpointInstanceId = $null
        }
    }

    $snapshot = if ($null -eq $Snapshot) {
        Get-HS2UsbRecoverySnapshot
    }
    else {
        $Snapshot
    }
    $code10Decision = Get-HS2BoundDeviceCode10Decision `
        -Binding $binding `
        -Snapshot $snapshot
    if ($code10Decision.Action -ceq "FailClosed") {
        return [pscustomobject]@{
            Eligible = $false
            Reason = [string]$code10Decision.Reason
            EndpointInstanceId = $null
            ResolvedHubInstanceId = $null
            RebindRequired = $false
            Code10DeviceInstanceIds = @($code10Decision.DeviceInstanceIds)
        }
    }
    $hubResolution = Resolve-HS2ControllerHub `
        -Binding $binding `
        -Snapshot $snapshot
    if ($null -eq $hubResolution.Hub) {
        return [pscustomobject]@{
            Eligible = $false
            Reason = [string]$hubResolution.Reason
            EndpointInstanceId = $null
            ResolvedHubInstanceId = $null
            RebindRequired = $false
        }
    }
    $resolvedHubInstanceId = [string]$hubResolution.Hub.InstanceId

    $controllerEndpoints = @(
        $snapshot.Devices | Where-Object {
            [bool]$_.Present -and
            [string]$_.ParentInstanceId -ieq $resolvedHubInstanceId -and
            [string]$_.Status -eq "OK" -and
            [int]$_.ProblemCode -eq 0 -and
            (
                [string]$_.InstanceId -like "USB\VID_1CBE&PID_A068\*" -or
                [string]$_.InstanceId -like "USB\VID_1A86&PID_AD23\*"
            )
        }
    )
    if ($controllerEndpoints.Count -ne 1) {
        return [pscustomobject]@{
            Eligible = $false
            Reason = if ($controllerEndpoints.Count -eq 0) {
                "bound-native-or-ad23-endpoint-not-healthy"
            }
            else {
                "bound-controller-endpoint-ambiguous"
            }
            EndpointInstanceId = $null
            ResolvedHubInstanceId = $resolvedHubInstanceId
            RebindRequired = [bool]$hubResolution.RebindRequired
        }
    }

    return [pscustomobject]@{
        Eligible = $true
        Reason = "bound-controller-endpoint-healthy"
        EndpointInstanceId = [string]$controllerEndpoints[0].InstanceId
        ResolvedHubInstanceId = $resolvedHubInstanceId
        RebindRequired = [bool]$hubResolution.RebindRequired
    }
}

function Wait-HS2UsbRecoveryPlan {
    param(
        [Parameter(Mandatory = $true)][string]$BoundHubInstanceId,
        [Parameter(Mandatory = $true)][object]$Binding,
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
                -Children @($snapshot.Children) `
                -Binding $Binding `
                -Devices @($snapshot.Devices)
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
    $devices = @(Get-PnpDevice -PresentOnly -ErrorAction Stop)
    $composites = @(
        $devices | Where-Object {
            [string]$_.InstanceId -like "USB\VID_1A86&PID_AD23\*" -and
            [string]$_.Status -eq "OK"
        }
    )
    $displayInterfaces = @(
        $devices | Where-Object {
            [string]$_.InstanceId -like "USB\VID_1A86&PID_AD23&MI_00\*" -and
            [string]$_.Status -eq "OK"
        }
    )

    foreach ($composite in $composites) {
        $compositeId = [string]$composite.InstanceId
        $compositePresent = Get-HS2PnpPropertyValue `
            -InstanceId $compositeId `
            -KeyName "DEVPKEY_Device_IsPresent"
        $compositeProblem = Get-HS2PnpPropertyValue `
            -InstanceId $compositeId `
            -KeyName "DEVPKEY_Device_ProblemCode"
        if (-not [bool]$compositePresent -or $null -eq $compositeProblem -or
            [int]$compositeProblem -ne 0) {
            continue
        }

        foreach ($display in $displayInterfaces) {
            $displayId = [string]$display.InstanceId
            $displayParent = Get-HS2PnpPropertyValue `
                -InstanceId $displayId `
                -KeyName "DEVPKEY_Device_Parent"
            if ([string]$displayParent -ine $compositeId) {
                continue
            }
            $displayPresent = Get-HS2PnpPropertyValue `
                -InstanceId $displayId `
                -KeyName "DEVPKEY_Device_IsPresent"
            $displayProblem = Get-HS2PnpPropertyValue `
                -InstanceId $displayId `
                -KeyName "DEVPKEY_Device_ProblemCode"
            if ([bool]$displayPresent -and $null -ne $displayProblem -and
                [int]$displayProblem -eq 0) {
                return $true
            }
        }
    }
    return $false
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
    param(
        [ValidateRange(1, 30)][int]$TimeoutSeconds = 10,
        [ValidateRange(1, 5000)][int]$PollMilliseconds = 500,
        [ValidateRange(1, 10)][int]$RequiredConsecutiveSamples = 2,
        [scriptblock]$HealthProbe = { Test-HS2UsbDisplayHealthy },
        [scriptblock]$DelayAction = {
            param([int]$Milliseconds)
            Start-Sleep -Milliseconds $Milliseconds
        }
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $consecutiveHealthySamples = 0
    do {
        if ([bool](& $HealthProbe)) {
            $consecutiveHealthySamples++
            if ($consecutiveHealthySamples -ge $RequiredConsecutiveSamples) {
                return $true
            }
        }
        else {
            $consecutiveHealthySamples = 0
        }
        if ([DateTime]::UtcNow -lt $deadline) {
            & $DelayAction $PollMilliseconds
        }
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
        -Children $snapshot.Children `
        -Binding $binding `
        -Devices $snapshot.Devices
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
            -Binding $binding `
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
        [Parameter(Mandatory = $true)][string]$BindingPath,
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
    $eligibility = Get-HS2LConnectServiceRecoveryEligibility `
        -BindingPath $BindingPath
    if (-not $eligibility.Eligible) {
        return [pscustomobject]@{
            Attempted = $false
            Recovered = $false
            Reason = [string]$eligibility.Reason
        }
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
