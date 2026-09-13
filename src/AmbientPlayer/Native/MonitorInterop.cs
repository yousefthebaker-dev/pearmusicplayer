using System.Runtime.InteropServices;

namespace AmbientPlayer.Native;

/// <summary>
/// Minimal Win32 monitor enumeration so the window can "remember which
/// monitor it was last on" (AMBIENT_PLAYER_SPEC.md section 9) without
/// pulling in a WinForms/System.Windows.Forms reference just for
/// <c>Screen</c>.
/// </summary>
public static class MonitorInterop
{
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
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
        public Rect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    /// <summary>The stable device name (e.g. "\\.\DISPLAY1") of the monitor a window handle currently sits on.</summary>
    public static string? GetDeviceNameForWindow(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return null;

        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        return GetMonitorInfo(monitor, ref info) ? info.Device : null;
    }

    /// <summary>The full monitor rect (in desktop coordinates) for a previously-saved device name, or null if it's no longer connected.</summary>
    public static Rect? GetMonitorRectByDeviceName(string deviceName)
    {
        Rect? found = null;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Rect _, IntPtr _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(hMonitor, ref info) && string.Equals(info.Device, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                found = info.Monitor;
                return false; // stop enumeration - found it
            }
            return true;
        }, IntPtr.Zero);

        return found;
    }
}
