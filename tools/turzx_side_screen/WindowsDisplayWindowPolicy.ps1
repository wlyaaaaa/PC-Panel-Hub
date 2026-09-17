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

    public sealed class WindowGuardMonitorIdentitySnapshot
    {
        public string DeviceName { get; set; }
        public string MonitorDeviceId { get; set; }
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
        public bool IsMaximized { get; set; }
        public bool IsCloaked { get; set; }
        public bool HasExtendedFrameBounds { get; set; }
        public int ExtendedFrameLeft { get; set; }
        public int ExtendedFrameTop { get; set; }
        public int ExtendedFrameRight { get; set; }
        public int ExtendedFrameBottom { get; set; }
        public int PlacementLeft { get; set; }
        public int PlacementTop { get; set; }
        public int PlacementRight { get; set; }
        public int PlacementBottom { get; set; }
    }

    public static class ExclusiveWindowGuardNativeMethods
    {
        private const int MonitorInfoPrimary = 1;
        private const int DisplayDeviceAttachedToDesktop = 1;
        private const uint MonitorDefaultToNearest = 2;
        private const int DwmWindowAttributeExtendedFrameBounds = 9;
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
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
        private static extern bool EnumDisplayDevices(
            string device,
            uint deviceNumber,
            ref DisplayDevice displayDevice,
            uint flags);

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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after,
            int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr window, int index);

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

        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static extern int DwmGetWindowAttributeRect(
            IntPtr window,
            int attribute,
            out Rect value,
            int valueSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetThreadDpiAwarenessContext(
            IntPtr dpiContext);

        public static void UsePerMonitorV2DpiAwareness()
        {
            // Monitor and DWM frame coordinates must share one physical-pixel
            // space.  This is thread-scoped, so it does not alter another UI.
            SetThreadDpiAwarenessContext(new IntPtr(-4));
        }

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

        public static WindowGuardMonitorIdentitySnapshot[] CaptureMonitorIdentities()
        {
            List<WindowGuardMonitorIdentitySnapshot> identities =
                new List<WindowGuardMonitorIdentitySnapshot>();
            for (uint adapterIndex = 0; adapterIndex < 64; adapterIndex++)
            {
                DisplayDevice adapter = new DisplayDevice();
                adapter.Size = Marshal.SizeOf(typeof(DisplayDevice));
                if (!EnumDisplayDevices(null, adapterIndex, ref adapter, 0)) break;
                if (String.IsNullOrWhiteSpace(adapter.DeviceName) ||
                    (adapter.StateFlags & DisplayDeviceAttachedToDesktop) == 0) continue;
                for (uint monitorIndex = 0; monitorIndex < 16; monitorIndex++)
                {
                    DisplayDevice monitor = new DisplayDevice();
                    monitor.Size = Marshal.SizeOf(typeof(DisplayDevice));
                    if (!EnumDisplayDevices(
                        adapter.DeviceName,
                        monitorIndex,
                        ref monitor,
                        0)) break;
                    if (String.IsNullOrWhiteSpace(monitor.DeviceId) ||
                        (monitor.StateFlags & DisplayDeviceAttachedToDesktop) == 0) continue;
                    identities.Add(new WindowGuardMonitorIdentitySnapshot
                    {
                        DeviceName = adapter.DeviceName,
                        MonitorDeviceId = monitor.DeviceId
                    });
                }
            }
            return identities.ToArray();
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
                Rect extendedFrame = placement.NormalPosition;
                bool hasExtendedFrame = !minimized &&
                    DwmGetWindowAttributeRect(
                        window,
                        DwmWindowAttributeExtendedFrameBounds,
                        out extendedFrame,
                        Marshal.SizeOf(typeof(Rect))) == 0 &&
                    extendedFrame.Right > extendedFrame.Left &&
                    extendedFrame.Bottom > extendedFrame.Top;
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

                if ((GetWindowLong(window, -20) & 0x80) == 0)
                {
                    Rect normal = placement.NormalPosition;
                    MonitorInfoEx normalMonitor = new MonitorInfoEx();
                    normalMonitor.Size = Marshal.SizeOf(typeof(MonitorInfoEx));
                    if (GetMonitorInfo(MonitorFromRect(ref normal, MonitorDefaultToNearest), ref normalMonitor))
                    {
                        int dx=normalMonitor.Work.Left-normalMonitor.Monitor.Left;
                        int dy=normalMonitor.Work.Top-normalMonitor.Monitor.Top;
                        placement.NormalPosition.Left += dx;
                        placement.NormalPosition.Right += dx;
                        placement.NormalPosition.Top += dy;
                        placement.NormalPosition.Bottom += dy;
                        if (!hasExtendedFrame) extendedFrame=placement.NormalPosition;
                    }
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
                    IsMaximized = placement.ShowCommand == 3,
                    IsCloaked = isCloaked,
                    HasExtendedFrameBounds = hasExtendedFrame,
                    ExtendedFrameLeft = extendedFrame.Left,
                    ExtendedFrameTop = extendedFrame.Top,
                    ExtendedFrameRight = extendedFrame.Right,
                    ExtendedFrameBottom = extendedFrame.Bottom,
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

            // WINDOWPLACEMENT is workspace-relative for ordinary top-level windows.
            // The planner and DWM snapshots use physical screen coordinates.
            Rect desired = new Rect { Left=left, Top=top, Right=right, Bottom=bottom };
            IntPtr target = MonitorFromRect(ref desired, MonitorDefaultToNearest);
            MonitorInfoEx targetInfo = new MonitorInfoEx();
            targetInfo.Size = Marshal.SizeOf(typeof(MonitorInfoEx));
            if (!GetMonitorInfo(target, ref targetInfo)) return false;
            bool toolWindow = (GetWindowLong(window, -20) & 0x80) != 0;
            int workspaceX = toolWindow ? 0 : targetInfo.Work.Left-targetInfo.Monitor.Left;
            int workspaceY = toolWindow ? 0 : targetInfo.Work.Top-targetInfo.Monitor.Top;
            bool maximized = placement.ShowCommand == 3;
            placement.Flags |= WindowPlacementAsync;
            placement.NormalPosition.Left = left-workspaceX;
            placement.NormalPosition.Top = top-workspaceY;
            placement.NormalPosition.Right = right-workspaceX;
            placement.NormalPosition.Bottom = bottom-workspaceY;
            if (!SetWindowPlacement(window, ref placement)) return false;
            if (!maximized) return true;

            // Updating rcNormalPosition alone leaves Chromium's maximized frame
            // on its previous monitor. Move the current frame as well, without
            // restoring, activating or changing Z-order. Keep native frame insets.
            Rect outer, frame;
            int insetLeft=0, insetTop=0, insetRight=0, insetBottom=0;
            if (GetWindowRect(window, out outer) &&
                DwmGetWindowAttributeRect(window, DwmWindowAttributeExtendedFrameBounds,
                    out frame, Marshal.SizeOf(typeof(Rect))) == 0)
            {
                insetLeft=Math.Max(0, Math.Min(32, frame.Left-outer.Left));
                insetTop=Math.Max(0, Math.Min(32, frame.Top-outer.Top));
                insetRight=Math.Max(0, Math.Min(32, outer.Right-frame.Right));
                insetBottom=Math.Max(0, Math.Min(32, outer.Bottom-frame.Bottom));
            }
            return SetWindowPos(window, IntPtr.Zero,
                targetInfo.Work.Left-insetLeft, targetInfo.Work.Top-insetTop,
                targetInfo.Work.Right-targetInfo.Work.Left+insetLeft+insetRight,
                targetInfo.Work.Bottom-targetInfo.Work.Top+insetTop+insetBottom,
                0x4000 | 0x0200 | 0x0010 | 0x0004);
        }

        public static bool MoveWindowPlacementChecked(long windowHandle, int expectedProcessId,
            int left, int top, int right, int bottom)
        {
            uint processId;
            if (GetWindowThreadProcessId(new IntPtr(windowHandle), out processId) == 0 ||
                processId != unchecked((uint)expectedProcessId)) return false;
            return MoveWindowPlacement(windowHandle, left, top, right, bottom);
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
        [AllowEmptyCollection()]
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
        "Bubbles",
        "Bubbles.scr",
        "EmeraldVeil",
        "PrimaryOledBlackout",
        "HS2.CrystalOverlay",
        "wallpaper32",
        "wallpaper64",
        "wallpaperservice32",
        "wallpaperservice64",
        "wallpaper_engine")
}

function Get-HS2ExclusiveWindowGuardPlan {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()]
        [object[]]$Monitors,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()]
        [object[]]$Windows,
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
        if (-not [bool]$window.IsVisible -or
            [bool]$window.IsCloaked -or
            $placementWidth -le 32 -or
            $placementHeight -le 32 -or
            (Test-HS2ExclusiveWindowGuardExclusion `
                -Window $window `
                -OverlayProcessIds $overlayIds)) {
            continue
        }

        $targetOwned = [string]$window.MonitorDevice -ceq $targetDevice
        $visibleBounds = Get-WindowGuardVisibleBounds -Window $window
        $maximized = $window.PSObject.Properties['IsMaximized']
        if (-not $targetOwned -and
            ([bool]$window.IsMinimized -or
            ($null -ne $maximized -and [bool]$maximized.Value) -or
            -not (Test-WindowGuardBoundsIntersectMonitor -Bounds $visibleBounds -Monitor $targetMonitor))) {
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

        if (-not $targetOwned) {
            $placement = Get-WindowGuardMainContainmentPlacement `
                -Window $window -VisibleBounds $visibleBounds -MainMonitor $safeMonitor
            [void]$actions.Add([pscustomobject]@{
                Action = "Move"
                Hwnd = [int64]$window.Hwnd
                ProcessId = [int]$window.ProcessId
                ProcessName = [string]$window.ProcessName
                Left = $placement.Left
                Top = $placement.Top
                Right = $placement.Right
                Bottom = $placement.Bottom
            })
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

function Test-WindowGuardMonitorHardwareId {
    param(
        [Parameter(Mandatory = $true)]$Identity,
        [Parameter(Mandatory = $true)][string]$HardwareId
    )

    $deviceId = [string]$Identity.MonitorDeviceId
    return -not [string]::IsNullOrWhiteSpace($deviceId) -and
        $deviceId.StartsWith(
            ("MONITOR\{0}\" -f $HardwareId.Trim()),
            [StringComparison]::OrdinalIgnoreCase)
}

function Get-WindowGuardVisibleBounds {
    param(
        [Parameter(Mandatory = $true)]$Window
    )

    $hasExtendedFrameProperty =
        $Window.PSObject.Properties['HasExtendedFrameBounds']
    $hasExtendedFrame =
        -not [bool]$Window.IsMinimized -and
        $null -ne $hasExtendedFrameProperty -and
        [bool]$hasExtendedFrameProperty.Value
    if ($hasExtendedFrame) {
        return [pscustomobject]@{
            Left = [int]$Window.ExtendedFrameLeft
            Top = [int]$Window.ExtendedFrameTop
            Right = [int]$Window.ExtendedFrameRight
            Bottom = [int]$Window.ExtendedFrameBottom
            Source = 'extended-frame'
        }
    }

    return [pscustomobject]@{
        Left = [int]$Window.PlacementLeft
        Top = [int]$Window.PlacementTop
        Right = [int]$Window.PlacementRight
        Bottom = [int]$Window.PlacementBottom
        Source = 'placement'
    }
}

function Test-WindowGuardBoundsIntersectMonitor {
    param(
        [Parameter(Mandatory = $true)]$Bounds,
        [Parameter(Mandatory = $true)]$Monitor
    )

    return [int]$Bounds.Left -lt [int]$Monitor.Right -and
        [int]$Bounds.Right -gt [int]$Monitor.Left -and
        [int]$Bounds.Top -lt [int]$Monitor.Bottom -and
        [int]$Bounds.Bottom -gt [int]$Monitor.Top
}

function Get-WindowGuardMainContainmentPlacement {
    param(
        [Parameter(Mandatory = $true)]$Window,
        [Parameter(Mandatory = $true)]$VisibleBounds,
        [Parameter(Mandatory = $true)]$MainMonitor
    )

    $frameWidth = [int]$VisibleBounds.Right - [int]$VisibleBounds.Left
    $frameHeight = [int]$VisibleBounds.Bottom - [int]$VisibleBounds.Top
    $mainWidth = [int]$MainMonitor.WorkRight - [int]$MainMonitor.WorkLeft
    $mainHeight = [int]$MainMonitor.WorkBottom - [int]$MainMonitor.WorkTop
    $targetFrameWidth = [Math]::Min($frameWidth, $mainWidth)
    $targetFrameHeight = [Math]::Min($frameHeight, $mainHeight)
    $targetFrameLeft = [Math]::Max(
        [int]$MainMonitor.WorkLeft,
        [Math]::Min(
            [int]$MainMonitor.WorkRight - $targetFrameWidth,
            [int]$VisibleBounds.Left))
    $targetFrameTop = [Math]::Max(
        [int]$MainMonitor.WorkTop,
        [Math]::Min(
            [int]$MainMonitor.WorkBottom - $targetFrameHeight,
            [int]$VisibleBounds.Top))
    $leftInset = [int]$VisibleBounds.Left - [int]$Window.PlacementLeft
    $topInset = [int]$VisibleBounds.Top - [int]$Window.PlacementTop
    $rightInset = [int]$Window.PlacementRight - [int]$VisibleBounds.Right
    $bottomInset = [int]$Window.PlacementBottom - [int]$VisibleBounds.Bottom

    return [pscustomobject]@{
        Left = $targetFrameLeft - $leftInset
        Top = $targetFrameTop - $topInset
        Right = $targetFrameLeft + $targetFrameWidth + $rightInset
        Bottom = $targetFrameTop + $targetFrameHeight + $bottomInset
    }
}

function Get-VddWindowReturnPlan {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Monitors,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Windows,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$MonitorIdentities,
        [AllowNull()][object]$OverlayProcessIds,
        [string]$MainMonitorHardwareId = 'PHLC34B',
        [string]$VddMonitorHardwareId = 'MTT1337'
    )

    $overlayIds = New-Object "System.Collections.Generic.HashSet[int]"
    foreach ($processId in @($OverlayProcessIds)) {
        [void]$overlayIds.Add([int]$processId)
    }

    $mainDeviceNames = @(
        $MonitorIdentities |
            Where-Object {
                Test-WindowGuardMonitorHardwareId `
                    -Identity $_ `
                    -HardwareId $MainMonitorHardwareId
            } |
            ForEach-Object { [string]$_.DeviceName } |
            Sort-Object -Unique
    )
    $mainMonitors = @(
        $Monitors | Where-Object {
            $mainDeviceNames -contains [string]$_.DeviceName
        }
    )
    if ($mainMonitors.Count -ne 1) {
        return [pscustomobject]@{
            Status = if ($mainMonitors.Count -eq 0) {
                'physical-main-unavailable'
            }
            else {
                'physical-main-ambiguous'
            }
            MainMonitorDevice = $null
            VddMonitorDevice = $null
            Actions = @()
        }
    }

    $vddDeviceNames = @(
        $MonitorIdentities |
            Where-Object {
                Test-WindowGuardMonitorHardwareId `
                    -Identity $_ `
                    -HardwareId $VddMonitorHardwareId
            } |
            ForEach-Object { [string]$_.DeviceName } |
            Sort-Object -Unique
    )
    $vddMonitors = @(
        $Monitors | Where-Object {
            $vddDeviceNames -contains [string]$_.DeviceName
        }
    )
    if ($vddMonitors.Count -ne 1) {
        return [pscustomobject]@{
            Status = if ($vddMonitors.Count -eq 0) {
                'vdd-unavailable'
            }
            else {
                'vdd-ambiguous'
            }
            MainMonitorDevice = [string]$mainMonitors[0].DeviceName
            VddMonitorDevice = $null
            Actions = @()
        }
    }

    $mainMonitor = $mainMonitors[0]
    $vddMonitor = $vddMonitors[0]
    if ([string]$mainMonitor.DeviceName -ceq [string]$vddMonitor.DeviceName) {
        return [pscustomobject]@{
            Status = 'main-vdd-device-collision'
            MainMonitorDevice = [string]$mainMonitor.DeviceName
            VddMonitorDevice = [string]$vddMonitor.DeviceName
            Actions = @()
        }
    }
    $mainWidth = [Math]::Max(
        1,
        [int]$mainMonitor.WorkRight - [int]$mainMonitor.WorkLeft)
    $mainHeight = [Math]::Max(
        1,
        [int]$mainMonitor.WorkBottom - [int]$mainMonitor.WorkTop)
    $actions = New-Object "System.Collections.Generic.List[object]"
    foreach ($window in @($Windows | Sort-Object Hwnd)) {
        $placementWidth =
            [int]$window.PlacementRight - [int]$window.PlacementLeft
        $placementHeight =
            [int]$window.PlacementBottom - [int]$window.PlacementTop
        if (-not [bool]$window.IsVisible -or
            [bool]$window.IsCloaked -or
            $placementWidth -le 32 -or
            $placementHeight -le 32 -or
            (Test-HS2ExclusiveWindowGuardExclusion `
                -Window $window `
                -OverlayProcessIds $overlayIds)) {
            continue
        }

        $visibleBounds = Get-WindowGuardVisibleBounds -Window $window
        $isVddOwned = [string]$window.MonitorDevice -ceq [string]$vddMonitor.DeviceName
        $intersectsVdd = Test-WindowGuardBoundsIntersectMonitor `
            -Bounds $visibleBounds `
            -Monitor $vddMonitor
        if (-not $isVddOwned -and -not $intersectsVdd) {
            continue
        }

        if ($isVddOwned) {
            $width = [Math]::Min($mainWidth, [Math]::Max(1, $placementWidth))
            $height = [Math]::Min($mainHeight, [Math]::Max(1, $placementHeight))
            $offsetX = [Math]::Max(
                0,
                [Math]::Min(
                    $mainWidth - $width,
                    [int]$window.PlacementLeft - [int]$vddMonitor.WorkLeft))
            $offsetY = [Math]::Max(
                0,
                [Math]::Min(
                    $mainHeight - $height,
                    [int]$window.PlacementTop - [int]$vddMonitor.WorkTop))
            $placement = [pscustomobject]@{
                Left = [int]$mainMonitor.WorkLeft + $offsetX
                Top = [int]$mainMonitor.WorkTop + $offsetY
                Right = [int]$mainMonitor.WorkLeft + $offsetX + $width
                Bottom = [int]$mainMonitor.WorkTop + $offsetY + $height
            }
        }
        else {
            # MonitorFromWindow picks the majority monitor.  A window can still
            # visibly spill into VDD, so translate its saved placement just far
            # enough to contain the DWM-rendered frame on the physical main.
            $placement = Get-WindowGuardMainContainmentPlacement `
                -Window $window `
                -VisibleBounds $visibleBounds `
                -MainMonitor $mainMonitor
        }
        [void]$actions.Add([pscustomobject]@{
                Action = 'Move'
                Hwnd = [int64]$window.Hwnd
                ProcessId = [int]$window.ProcessId
                ProcessName = [string]$window.ProcessName
                Left = [int]$placement.Left
                Top = [int]$placement.Top
                Right = [int]$placement.Right
                Bottom = [int]$placement.Bottom
            })
    }

    return [pscustomobject]@{
        Status = 'active'
        MainMonitorDevice = [string]$mainMonitor.DeviceName
        VddMonitorDevice = [string]$vddMonitor.DeviceName
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

    $nativeMethods::UsePerMonitorV2DpiAwareness()
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
                $nativeMethods::MoveWindowPlacementChecked(
                    [int64]$action.Hwnd,
                    [int]$action.ProcessId,
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

function Invoke-VddWindowReturnGuard {
    param(
        [AllowNull()][object]$OverlayProcessIds,
        [switch]$DryRun
    )

    $nativeMethods = Initialize-HS2ExclusiveWindowGuardNativeMethods
    if ($null -eq $nativeMethods) {
        throw 'VDD window-return native methods are unavailable.'
    }

    $nativeMethods::UsePerMonitorV2DpiAwareness()
    $overlayIdArray = [int[]]@($OverlayProcessIds)
    $monitors = @($nativeMethods::CaptureMonitors())
    $identities = @($nativeMethods::CaptureMonitorIdentities())
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
        MonitorIdentities = $identities
    }
    if ($overlayIdArray.Count -gt 0) {
        $planArguments.OverlayProcessIds = $overlayIdArray
    }
    $plan = Get-VddWindowReturnPlan @planArguments
    $applied = New-Object 'System.Collections.Generic.List[object]'
    $failures = New-Object 'System.Collections.Generic.List[object]'
    if (-not $DryRun) {
        foreach ($action in @($plan.Actions)) {
            if ($nativeMethods::MoveWindowPlacementChecked(
                    [int64]$action.Hwnd,
                    [int]$action.ProcessId,
                    [int]$action.Left,
                    [int]$action.Top,
                    [int]$action.Right,
                    [int]$action.Bottom)) {
                [void]$applied.Add($action)
            }
            else {
                [void]$failures.Add($action)
            }
        }
    }

    # An asynchronous Win32 return is dispatch evidence, not migration evidence.
    # Replan on fresh physical snapshots and count only converged original HWNDs.
    $dispatchedCount = $applied.Count
    if (-not $DryRun -and $dispatchedCount -gt 0) {
        $pending = @($applied.ToArray())
        $verified = New-Object 'System.Collections.Generic.List[object]'
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Milliseconds 30
            $readbackWindows = @($nativeMethods::CaptureWindows($overlayIdArray))
            foreach ($window in $readbackWindows) {
                if ($processNames.ContainsKey([int]$window.ProcessId)) {
                    $window.ProcessName = $processNames[[int]$window.ProcessId]
                }
            }
            $remainingPlan = Get-VddWindowReturnPlan `
                -Monitors @($nativeMethods::CaptureMonitors()) `
                -Windows $readbackWindows `
                -MonitorIdentities @($nativeMethods::CaptureMonitorIdentities()) `
                -OverlayProcessIds $overlayIdArray
            $remainingHandles = @($remainingPlan.Actions | ForEach-Object { [int64]$_.Hwnd })
            $next = @()
            foreach ($action in $pending) {
                if ($remainingPlan.Status -eq 'active' -and [int64]$action.Hwnd -notin $remainingHandles) {
                    [void]$verified.Add($action)
                }
                else { $next += $action }
            }
            $pending = @($next)
        } while ($pending.Count -gt 0 -and $deadline.ElapsedMilliseconds -lt 450)
        foreach ($action in $pending) { [void]$failures.Add($action) }
        $applied = $verified
    }

    return [pscustomobject]@{
        Status = [string]$plan.Status
        MainMonitorDevice = [string]$plan.MainMonitorDevice
        VddMonitorDevice = [string]$plan.VddMonitorDevice
        PlannedActions = @($plan.Actions)
        AppliedActions = $applied.ToArray()
        FailedActions = $failures.ToArray()
        DispatchedCount = $dispatchedCount
        VerifiedCount = $applied.Count
        DryRun = [bool]$DryRun
    }
}
