using System.Runtime.InteropServices;

namespace TuckPane.Services;

internal static partial class NativeMethods
{
    internal static bool SupportsWindows11DwmAttributes =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    internal static bool SetHostBackdropBrushEnabled(IntPtr window, bool enabled)
    {
        if (!SupportsWindows11DwmAttributes || window == IntPtr.Zero) return false;
        int value = enabled ? 1 : 0;
        int result = DwmSetWindowAttribute(window, DWMWA_USE_HOSTBACKDROPBRUSH, ref value, sizeof(int));
        string state = enabled ? "启用" : "禁用";
        if (result >= 0)
            AppLogger.Info($"DwmSetWindowAttribute(DWMWA_USE_HOSTBACKDROPBRUSH，{state}) 完成，HRESULT=0x{result:X8}。");
        else
            AppLogger.Error($"DwmSetWindowAttribute(DWMWA_USE_HOSTBACKDROPBRUSH，{state}) 失败，HRESULT=0x{result:X8}。");
        return result >= 0;
    }

    internal static bool EnableHostBackdropBrush(IntPtr window) =>
        SetHostBackdropBrushEnabled(window, enabled: true);

    internal static bool TrySetCompositorClockBoost(bool enabled)
    {
        try
        {
            return DCompositionBoostCompositorClock(enabled) >= 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmFlush();

    [DllImport("dcomp.dll")]
    private static extern int DCompositionBoostCompositorClock([MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    internal static extern int GetObject(IntPtr value, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    internal static extern int GetDIBits(
        IntPtr hdc,
        IntPtr bitmap,
        uint start,
        uint lines,
        byte[] bits,
        ref BITMAPINFO bitmapInfo,
        uint usage);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bitmapInfo, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
}
