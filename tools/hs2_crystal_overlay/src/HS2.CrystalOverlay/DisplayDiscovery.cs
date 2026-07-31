using System.Runtime.InteropServices;
using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal static class DisplayDiscovery
{
    internal static IReadOnlyList<DisplayGeometry> Enumerate()
    {
        var result = new List<DisplayGeometry>();
        NativeMethods.MonitorEnumProc callback = (
            nint monitor,
            nint _,
            ref NativeMethods.Rect __,
            nint ___) =>
        {
            var info = new NativeMethods.MonitorInfoEx
            {
                Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
                DeviceName = string.Empty,
            };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            result.Add(new DisplayGeometry(
                info.DeviceName,
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top,
                (info.Flags & NativeMethods.MonitorInfoPrimary) != 0));
            return true;
        };

        _ = NativeMethods.EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        return result;
    }
}
