Set-StrictMode -Version Latest

function Get-WindowsDisplayWindowPreservationPlan {
    @(
        [pscustomobject]@{
            Name = "MonitorRemovalRecalcBehavior"
            DesiredValue = 0
            Purpose = "minimize-windows-when-monitor-disconnects"
        },
        [pscustomobject]@{
            Name = "RestorePreviousStateRecalcBehavior"
            DesiredValue = 0
            Purpose = "remember-window-locations-by-monitor-connection"
        }
    )
}

function Get-WindowsDisplayWindowPreservationStatus {
    param(
        [string]$RegistryPath = "HKCU:\Control Panel\Desktop"
    )

    $values = Get-ItemProperty -LiteralPath $RegistryPath -ErrorAction Stop
    $settings = foreach ($operation in @(Get-WindowsDisplayWindowPreservationPlan)) {
        $property = $values.PSObject.Properties[[string]$operation.Name]
        $currentValue = if ($null -eq $property) { $null } else { [int]$property.Value }
        [pscustomobject]@{
            Name = [string]$operation.Name
            Purpose = [string]$operation.Purpose
            CurrentValue = $currentValue
            DesiredValue = [int]$operation.DesiredValue
            Compliant = ($null -ne $currentValue -and $currentValue -eq [int]$operation.DesiredValue)
        }
    }

    return [pscustomobject]@{
        RegistryPath = $RegistryPath
        Compliant = @($settings | Where-Object { -not $_.Compliant }).Count -eq 0
        Settings = @($settings)
    }
}

function Initialize-WindowsDesktopSettingChangeNativeMethods {
    if (-not ("TURZX.SideScreen.DesktopSettingChangeNativeMethods" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace TURZX.SideScreen
{
    public static class DesktopSettingChangeNativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint message,
            UIntPtr wParam,
            string lParam,
            uint flags,
            uint timeoutMilliseconds,
            out UIntPtr result);
    }
}
"@
    }

    return ("TURZX.SideScreen.DesktopSettingChangeNativeMethods" -as [type])
}

function Send-WindowsDesktopSettingChange {
    $nativeMethods = Initialize-WindowsDesktopSettingChangeNativeMethods
    if ($null -eq $nativeMethods) {
        throw "Windows desktop setting-change native methods are unavailable."
    }

    $broadcast = [IntPtr]0xffff
    $wmSettingChange = 0x001a
    $abortIfHung = 0x0002
    $result = [UIntPtr]::Zero
    $sent = $nativeMethods::SendMessageTimeout(
        $broadcast,
        $wmSettingChange,
        [UIntPtr]::Zero,
        "Control Panel\Desktop",
        $abortIfHung,
        1000,
        [ref]$result)
    return $sent -ne [IntPtr]::Zero
}

function Enable-WindowsDisplayWindowPreservation {
    param(
        [string]$RegistryPath = "HKCU:\Control Panel\Desktop",
        [switch]$SkipBroadcast,
        [switch]$DryRun
    )

    $before = Get-WindowsDisplayWindowPreservationStatus -RegistryPath $RegistryPath
    $changed = New-Object "System.Collections.Generic.List[string]"
    if (-not $DryRun) {
        foreach ($setting in @($before.Settings | Where-Object { -not $_.Compliant })) {
            New-ItemProperty `
                -LiteralPath $RegistryPath `
                -Name $setting.Name `
                -PropertyType DWord `
                -Value $setting.DesiredValue `
                -Force | Out-Null
            [void]$changed.Add([string]$setting.Name)
        }
    }

    $broadcasted = $false
    if (-not $DryRun -and -not $SkipBroadcast) {
        $broadcasted = Send-WindowsDesktopSettingChange
    }

    $after = if ($DryRun) {
        $before
    }
    else {
        Get-WindowsDisplayWindowPreservationStatus -RegistryPath $RegistryPath
    }
    if (-not $DryRun -and -not $after.Compliant) {
        throw "Windows display window-preservation policy failed registry read-back."
    }

    return [pscustomobject]@{
        Applied = -not $DryRun
        Compliant = [bool]$after.Compliant
        ChangedSettings = @($changed)
        Broadcasted = $broadcasted
        Before = $before
        After = $after
    }
}
