using System.Runtime.InteropServices;
using XiaobianPet.Models;

namespace XiaobianPet.Services;

internal readonly record struct DesktopPointerMonitorSnapshot(
    PetFollowPoint Cursor,
    PetFollowBounds WorkArea);

internal static class DesktopPointerTargetService
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public static bool TryCapturePointerMonitor(
        out DesktopPointerMonitorSnapshot snapshot)
    {
        snapshot = default;
        if (!GetCursorPos(out var cursor))
        {
            return false;
        }

        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        var workArea = monitorInfo.WorkArea;
        if (workArea.Right <= workArea.Left || workArea.Bottom <= workArea.Top)
        {
            return false;
        }

        snapshot = new DesktopPointerMonitorSnapshot(
            new PetFollowPoint(cursor.X, cursor.Y),
            new PetFollowBounds(
                workArea.Left,
                workArea.Top,
                workArea.Right - workArea.Left,
                workArea.Bottom - workArea.Top));
        return true;
    }

    public static bool TryGetWindowBounds(IntPtr windowHandle, out PetFollowBounds bounds)
    {
        bounds = default;
        if (windowHandle == IntPtr.Zero ||
            !GetWindowRect(windowHandle, out var rect) ||
            rect.Right <= rect.Left ||
            rect.Bottom <= rect.Top)
        {
            return false;
        }

        bounds = new PetFollowBounds(
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top);
        return true;
    }

    public static double GetWindowDpiScale(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return 1;
        }

        try
        {
            var dpi = GetDpiForWindow(windowHandle);
            return dpi > 0 ? dpi / 96d : 1;
        }
        catch (EntryPointNotFoundException)
        {
            return 1;
        }
        catch (DllNotFoundException)
        {
            return 1;
        }
    }

    public static bool TryMoveWindow(
        IntPtr windowHandle,
        PetFollowPoint topLeft) =>
        windowHandle != IntPtr.Zero &&
        SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            checked((int)Math.Round(topLeft.X)),
            checked((int)Math.Round(topLeft.Y)),
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(
        NativePoint point,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitor,
        ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr windowHandle,
        out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
