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

function Initialize-HS2ExclusiveWindowGuardNativeMethods {
    if (-not ("TURZX.SideScreen.ExclusiveWindowGuardNativeMethods" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TURZX.SideScreen
{
    public sealed class WindowGuardMonitorSnapshot
    {
        public long Handle { get; set; }
        public string DeviceName { get; set; }
        public bool IsPrimary { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public int WorkLeft { get; set; }
        public int WorkTop { get; set; }
        public int WorkRight { get; set; }
        public int WorkBottom { get; set; }
    }

    public sealed class WindowGuardWindowSnapshot
    {
        public long Hwnd { get; set; }
        public int ProcessId { get; set; }
        public string ProcessName { get; set; }
        public string Title { get; set; }
        public string ClassName { get; set; }
        public string MonitorDevice { get; set; }
        public bool IsVisible { get; set; }
        public bool IsMinimized { get; set; }
        public bool IsCloaked { get; set; }
        public int PlacementLeft { get; set; }
        public int PlacementTop { get; set; }
        public int PlacementRight { get; set; }
        public int PlacementBottom { get; set; }
    }

    public static class ExclusiveWindowGuardNativeMethods
    {
        private const int MonitorInfoPrimary = 1;
        private const uint MonitorDefaultToNearest = 2;
        private const int DwmWindowAttributeCloaked = 14;
        private const int ShowMinimized = 6;
        private const int WindowPlacementAsync = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public int Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPlacement
        {
            public int Length;
            public int Flags;
            public int ShowCommand;
            public Point MinimumPosition;
            public Point MaximumPosition;
            public Rect NormalPosition;
        }

        private delegate bool MonitorEnumerationCallback(
            IntPtr monitor,
            IntPtr deviceContext,
            ref Rect monitorRectangle,
            IntPtr data);

        private delegate bool WindowEnumerationCallback(
            IntPtr window,
            IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(
            IntPtr deviceContext,
            IntPtr clipRectangle,
            MonitorEnumerationCallback callback,
            IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(
            IntPtr monitor,
            ref MonitorInfoEx info);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(
            WindowEnumerationCallback callback,
            IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(
            IntPtr window,
            uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromRect(
            ref Rect rectangle,
            uint flags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr window,
            out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(
            IntPtr window,
            StringBuilder text,
            int maximumCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(
            IntPtr window,
            StringBuilder className,
            int maximumCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowPlacement(
            IntPtr window,
            ref WindowPlacement placement);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPlacement(
            IntPtr window,
            [In] ref WindowPlacement placement);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(
            IntPtr window,
            int command);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(
            IntPtr window,
            int attribute,
            out int value,
            int valueSize);

        public static WindowGuardMonitorSnapshot[] CaptureMonitors()
        {
            List<WindowGuardMonitorSnapshot> monitors =
                new List<WindowGuardMonitorSnapshot>();
            MonitorEnumerationCallback callback = delegate(
                IntPtr monitor,
                IntPtr deviceContext,
                ref Rect monitorRectangle,
                IntPtr data)
            {
                MonitorInfoEx info = new MonitorInfoEx();
                info.Size = Marshal.SizeOf(typeof(MonitorInfoEx));
                info.DeviceName = string.Empty;
                if (GetMonitorInfo(monitor, ref info))
                {
                    monitors.Add(new WindowGuardMonitorSnapshot
                    {
                        Handle = monitor.ToInt64(),
                        DeviceName = info.DeviceName,
                        IsPrimary = (info.Flags & MonitorInfoPrimary) != 0,
                        Left = info.Monitor.Left,
                        Top = info.Monitor.Top,
                        Right = info.Monitor.Right,
                        Bottom = info.Monitor.Bottom,
                        WorkLeft = info.Work.Left,
                        WorkTop = info.Work.Top,
                        WorkRight = info.Work.Right,
                        WorkBottom = info.Work.Bottom
                    });
                }
                return true;
            };

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return monitors.ToArray();
        }

        public static WindowGuardWindowSnapshot[] CaptureWindows(
            int[] includedHiddenProcessIds)
        {
            List<WindowGuardWindowSnapshot> windows =
                new List<WindowGuardWindowSnapshot>();
            HashSet<int> includedHidden = new HashSet<int>(
                includedHiddenProcessIds ?? new int[0]);
            WindowEnumerationCallback callback = delegate(
                IntPtr window,
                IntPtr data)
            {
                uint processId;
                GetWindowThreadProcessId(window, out processId);
                bool visible = IsWindowVisible(window);
                if (!visible &&
                    !includedHidden.Contains(unchecked((int)processId)))
                {
                    return true;
                }

                int cloaked = 0;
                bool isCloaked =
                    DwmGetWindowAttribute(
                        window,
                        DwmWindowAttributeCloaked,
                        out cloaked,
                        Marshal.SizeOf(typeof(int))) == 0 &&
                    cloaked != 0;
                WindowPlacement placement = new WindowPlacement();
                placement.Length = Marshal.SizeOf(typeof(WindowPlacement));
                if (!GetWindowPlacement(window, ref placement))
                {
                    return true;
                }

                bool minimized = IsIconic(window);
                Rect monitorRectangle = placement.NormalPosition;
                IntPtr monitor = minimized
                    ? MonitorFromRect(
                        ref monitorRectangle,
                        MonitorDefaultToNearest)
                    : MonitorFromWindow(window, MonitorDefaultToNearest);
                MonitorInfoEx monitorInfo = new MonitorInfoEx();
                monitorInfo.Size = Marshal.SizeOf(typeof(MonitorInfoEx));
                monitorInfo.DeviceName = string.Empty;
                if (!GetMonitorInfo(monitor, ref monitorInfo))
                {
                    return true;
                }

                StringBuilder title = new StringBuilder(1024);
                StringBuilder className = new StringBuilder(256);
                GetWindowText(window, title, title.Capacity);
                GetClassName(window, className, className.Capacity);
                windows.Add(new WindowGuardWindowSnapshot
                {
                    Hwnd = window.ToInt64(),
                    ProcessId = unchecked((int)processId),
                    ProcessName = string.Empty,
                    Title = title.ToString(),
                    ClassName = className.ToString(),
                    MonitorDevice = monitorInfo.DeviceName,
                    IsVisible = visible,
                    IsMinimized = minimized,
                    IsCloaked = isCloaked,
                    PlacementLeft = placement.NormalPosition.Left,
                    PlacementTop = placement.NormalPosition.Top,
                    PlacementRight = placement.NormalPosition.Right,
                    PlacementBottom = placement.NormalPosition.Bottom
                });
                return true;
            };

            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return windows.ToArray();
        }

        public static bool MoveWindowPlacement(
            long windowHandle,
            int left,
            int top,
            int right,
            int bottom)
        {
            IntPtr window = new IntPtr(windowHandle);
            WindowPlacement placement = new WindowPlacement();
            placement.Length = Marshal.SizeOf(typeof(WindowPlacement));
            if (!GetWindowPlacement(window, ref placement))
            {
                return false;
            }

            placement.Flags |= WindowPlacementAsync;
            placement.NormalPosition.Left = left;
            placement.NormalPosition.Top = top;
            placement.NormalPosition.Right = right;
            placement.NormalPosition.Bottom = bottom;
            return SetWindowPlacement(window, ref placement);
        }

        public static bool MinimizeWindow(long windowHandle)
        {
            return ShowWindowAsync(
                new IntPtr(windowHandle),
                ShowMinimized);
        }
    }
}
"@
    }

    return ("TURZX.SideScreen.ExclusiveWindowGuardNativeMethods" -as [type])
}

function Test-HS2ExclusiveWindowGuardExclusion {
    param(
        [Parameter(Mandatory = $true)]$Window,
        [Parameter(Mandatory = $true)]
        [Collections.Generic.HashSet[int]]$OverlayProcessIds
    )

    if ($OverlayProcessIds.Contains([int]$Window.ProcessId)) {
        return $true
    }

    $className = [string]$Window.ClassName
    if ($className -in @(
            "Progman",
            "WorkerW",
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd")) {
        return $true
    }

    $processName = [string]$Window.ProcessName
    return $processName -in @(
        "HS2.CrystalOverlay",
        "wallpaper32",
        "wallpaper64",
        "wallpaperservice32",
        "wallpaperservice64",
        "wallpaper_engine")
}

function Get-HS2ExclusiveWindowGuardPlan {
    param(
        [Parameter(Mandatory = $true)][object[]]$Monitors,
        [Parameter(Mandatory = $true)][object[]]$Windows,
        [AllowNull()][object]$OverlayProcessIds,
        [string]$PreferredTargetMonitorDevice,
        [string]$PreferredSafeMonitorDevice
    )

    $overlayIds = New-Object "System.Collections.Generic.HashSet[int]"
    foreach ($processId in @($OverlayProcessIds)) {
        [void]$overlayIds.Add([int]$processId)
    }

    $overlayWindows = @(
        $Windows |
            Where-Object {
                $overlayIds.Contains([int]$_.ProcessId) -and
                -not [string]::IsNullOrWhiteSpace([string]$_.MonitorDevice)
            }
    )
    $visibleTargetGroup = @(
        $overlayWindows |
            Where-Object { [bool]$_.IsVisible } |
            Group-Object -Property MonitorDevice |
            Sort-Object `
                @{ Expression = "Count"; Descending = $true }, `
                @{ Expression = "Name"; Descending = $false }
    ) | Select-Object -First 1
    $targetDevice = if (-not [string]::IsNullOrWhiteSpace(
            $PreferredTargetMonitorDevice) -and
        @($Monitors | Where-Object {
                [string]$_.DeviceName -ceq
                $PreferredTargetMonitorDevice
            }).Count -gt 0) {
        $PreferredTargetMonitorDevice
    }
    else {
        $geometryTarget = @(
            $Monitors |
                Where-Object {
                    ([int]$_.Right - [int]$_.Left) -eq 2288 -and
                    ([int]$_.Bottom - [int]$_.Top) -eq 1048
                } |
                Sort-Object `
                    @{ Expression = "IsPrimary"; Descending = $false }, `
                    @{ Expression = "DeviceName"; Descending = $false }
        ) | Select-Object -First 1
        if ($null -ne $geometryTarget) {
            [string]$geometryTarget.DeviceName
        }
        elseif ($null -ne $visibleTargetGroup) {
            [string]$visibleTargetGroup.Name
        }
        else {
            $hiddenTargetGroup = @(
                $overlayWindows |
                    Group-Object -Property MonitorDevice |
                    Sort-Object `
                        @{ Expression = "Count"; Descending = $true }, `
                        @{ Expression = "Name"; Descending = $false }
            ) | Select-Object -First 1
            if ($null -ne $hiddenTargetGroup) {
                [string]$hiddenTargetGroup.Name
            }
            else {
                $null
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace([string]$targetDevice)) {
        return [pscustomobject]@{
            Status = "overlay-monitor-unavailable"
            TargetMonitorDevice = $null
            SafeMonitorDevice = $PreferredSafeMonitorDevice
            OverlayPlacementStatus = "target-unavailable"
            OverlayVisibleWindowCount = 0
            MisplacedOverlayWindows = @()
            Actions = @()
        }
    }

    $targetMonitor = @(
        $Monitors |
            Where-Object {
                [string]$_.DeviceName -ceq $targetDevice
            }
    ) | Select-Object -First 1
    if ($null -eq $targetMonitor) {
        return [pscustomobject]@{
            Status = "overlay-monitor-missing"
            TargetMonitorDevice = $targetDevice
            SafeMonitorDevice = $PreferredSafeMonitorDevice
            OverlayPlacementStatus = "target-missing"
            OverlayVisibleWindowCount = 0
            MisplacedOverlayWindows = @()
            Actions = @()
        }
    }

    # Windows can silently remap an already-running top-level overlay window
    # to the primary monitor when the display topology changes.  Process
    # liveness alone is therefore not proof that the overlay is healthy.  Only
    # meaningful visible overlay windows participate: hidden helper, IME and
    # one-pixel staging windows must not cause a recycle loop.
    $visibleOverlayWindows = @(
        $overlayWindows |
            Where-Object {
                $placementWidth =
                    [int]$_.PlacementRight - [int]$_.PlacementLeft
                $placementHeight =
                    [int]$_.PlacementBottom - [int]$_.PlacementTop
                [bool]$_.IsVisible -and
                    -not [bool]$_.IsCloaked -and
                    $placementWidth -gt 32 -and
                    $placementHeight -gt 32
            }
    )
    $misplacedOverlayWindows = @(
        $visibleOverlayWindows |
            Where-Object {
                [bool]$_.IsMinimized -or
                    [string]$_.MonitorDevice -cne $targetDevice
            }
    )
    $overlayPlacementStatus = if ($visibleOverlayWindows.Count -eq 0) {
        "not-visible"
    }
    elseif ($misplacedOverlayWindows.Count -gt 0) {
        "drifted"
    }
    else {
        "healthy"
    }

    $safeMonitor = $null
    if (-not [string]::IsNullOrWhiteSpace($PreferredSafeMonitorDevice)) {
        $safeMonitor = @(
            $Monitors |
                Where-Object {
                    [string]$_.DeviceName -ceq $PreferredSafeMonitorDevice -and
                    [string]$_.DeviceName -cne $targetDevice
                }
        ) | Select-Object -First 1
    }
    if ($null -eq $safeMonitor) {
        $safeMonitor = @(
            $Monitors |
                Where-Object {
                    [bool]$_.IsPrimary -and
                    [string]$_.DeviceName -cne $targetDevice
                }
        ) | Select-Object -First 1
    }
    if ($null -eq $safeMonitor) {
        $safeMonitor = @(
            $Monitors |
                Where-Object {
                    [string]$_.DeviceName -cne $targetDevice
                } |
                Sort-Object @{ Expression = {
                    ([int]$_.WorkRight - [int]$_.WorkLeft) *
                    ([int]$_.WorkBottom - [int]$_.WorkTop)
                }; Descending = $true }
        ) | Select-Object -First 1
    }

    $actions = New-Object "System.Collections.Generic.List[object]"
    foreach ($window in @($Windows | Sort-Object Hwnd)) {
        $placementWidth =
            [int]$window.PlacementRight - [int]$window.PlacementLeft
        $placementHeight =
            [int]$window.PlacementBottom - [int]$window.PlacementTop
        if ([string]$window.MonitorDevice -cne $targetDevice -or
            -not [bool]$window.IsVisible -or
            [bool]$window.IsCloaked -or
            $placementWidth -le 32 -or
            $placementHeight -le 32 -or
            (Test-HS2ExclusiveWindowGuardExclusion `
                -Window $window `
                -OverlayProcessIds $overlayIds)) {
            continue
        }

        if ($null -eq $safeMonitor) {
            if (-not [bool]$window.IsMinimized) {
                [void]$actions.Add([pscustomobject]@{
                    Action = "Minimize"
                    Hwnd = [int64]$window.Hwnd
                    ProcessId = [int]$window.ProcessId
                    ProcessName = [string]$window.ProcessName
                })
            }
            continue
        }

        $safeWidth = [Math]::Max(
            1,
            [int]$safeMonitor.WorkRight - [int]$safeMonitor.WorkLeft)
        $safeHeight = [Math]::Max(
            1,
            [int]$safeMonitor.WorkBottom - [int]$safeMonitor.WorkTop)
        $width = [Math]::Min(
            $safeWidth,
            [Math]::Max(1, $placementWidth))
        $height = [Math]::Min(
            $safeHeight,
            [Math]::Max(1, $placementHeight))
        $offsetX = [Math]::Max(
            0,
            [Math]::Min(
                $safeWidth - $width,
                [int]$window.PlacementLeft -
                [int]$targetMonitor.WorkLeft))
        $offsetY = [Math]::Max(
            0,
            [Math]::Min(
                $safeHeight - $height,
                [int]$window.PlacementTop -
                [int]$targetMonitor.WorkTop))
        $left = [int]$safeMonitor.WorkLeft + $offsetX
        $top = [int]$safeMonitor.WorkTop + $offsetY
        [void]$actions.Add([pscustomobject]@{
            Action = "Move"
            Hwnd = [int64]$window.Hwnd
            ProcessId = [int]$window.ProcessId
            ProcessName = [string]$window.ProcessName
            Left = $left
            Top = $top
            Right = $left + $width
            Bottom = $top + $height
        })
    }

    return [pscustomobject]@{
        Status = if ($null -eq $safeMonitor) {
            "target-only"
        }
        else {
            "active"
        }
        TargetMonitorDevice = $targetDevice
        SafeMonitorDevice = if ($null -eq $safeMonitor) {
            $PreferredSafeMonitorDevice
        }
        else {
            [string]$safeMonitor.DeviceName
        }
        OverlayPlacementStatus = $overlayPlacementStatus
        OverlayVisibleWindowCount = $visibleOverlayWindows.Count
        MisplacedOverlayWindows = @($misplacedOverlayWindows)
        Actions = $actions.ToArray()
    }
}

function Invoke-HS2ExclusiveWindowGuard {
    param(
        [AllowNull()][object]$OverlayProcessIds,
        [string]$PreferredTargetMonitorDevice,
        [string]$PreferredSafeMonitorDevice,
        [switch]$DryRun
    )

    $nativeMethods = Initialize-HS2ExclusiveWindowGuardNativeMethods
    if ($null -eq $nativeMethods) {
        throw "HS2 exclusive-window guard native methods are unavailable."
    }

    $overlayIdArray = [int[]]@($OverlayProcessIds)
    $monitors = @($nativeMethods::CaptureMonitors())
    $windows = @($nativeMethods::CaptureWindows($overlayIdArray))
    $processNames = @{}
    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
        $processNames[[int]$process.Id] = [string]$process.ProcessName
    }
    foreach ($window in $windows) {
        if ($processNames.ContainsKey([int]$window.ProcessId)) {
            $window.ProcessName = $processNames[[int]$window.ProcessId]
        }
    }

    $planArguments = @{
        Monitors = $monitors
        Windows = $windows
        PreferredTargetMonitorDevice = $PreferredTargetMonitorDevice
        PreferredSafeMonitorDevice = $PreferredSafeMonitorDevice
    }
    if ($overlayIdArray.Count -gt 0) {
        $planArguments.OverlayProcessIds = $overlayIdArray
    }
    $plan = Get-HS2ExclusiveWindowGuardPlan @planArguments
    $applied = New-Object "System.Collections.Generic.List[object]"
    $failures = New-Object "System.Collections.Generic.List[object]"
    if (-not $DryRun) {
        foreach ($action in @($plan.Actions)) {
            $succeeded = if ([string]$action.Action -ceq "Move") {
                $nativeMethods::MoveWindowPlacement(
                    [int64]$action.Hwnd,
                    [int]$action.Left,
                    [int]$action.Top,
                    [int]$action.Right,
                    [int]$action.Bottom)
            }
            else {
                $nativeMethods::MinimizeWindow([int64]$action.Hwnd)
            }
            if ($succeeded) {
                [void]$applied.Add($action)
            }
            else {
                [void]$failures.Add($action)
            }
        }
    }

    return [pscustomobject]@{
        Status = [string]$plan.Status
        TargetMonitorDevice = $plan.TargetMonitorDevice
        SafeMonitorDevice = $plan.SafeMonitorDevice
        OverlayPlacementStatus = [string]$plan.OverlayPlacementStatus
        OverlayVisibleWindowCount = [int]$plan.OverlayVisibleWindowCount
        MisplacedOverlayWindows = @($plan.MisplacedOverlayWindows)
        PlannedActions = @($plan.Actions)
        AppliedActions = $applied.ToArray()
        FailedActions = $failures.ToArray()
        DryRun = [bool]$DryRun
    }
}
