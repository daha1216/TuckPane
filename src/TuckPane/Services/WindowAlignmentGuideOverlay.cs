using System.Runtime.InteropServices;
using TuckPane.Core;
using Windows.UI.ViewManagement;

namespace TuckPane.Services;

internal sealed class WindowAlignmentGuideOverlay : IDisposable
{
    internal const string ClassName = "TuckPane.WindowAlignmentGuide";
    internal const string XWindowName = "TuckPane.AlignmentGuide.X";
    internal const string YWindowName = "TuckPane.AlignmentGuide.Y";

    private static readonly object RegistrationGate = new();
    private static readonly NativeMethods.WindowProc WindowProc = GuideWindowProc;
    private static bool _registered;

    private IntPtr _xWindow;
    private IntPtr _yWindow;
    private NativeMethods.RECT _xRect;
    private NativeMethods.RECT _yRect;
    private bool _xVisible;
    private bool _yVisible;

    internal WindowAlignmentGuideOverlay(IntPtr owner)
    {
        EnsureRegistered();
        _xWindow = Create(owner, XWindowName);
        _yWindow = Create(owner, YWindowName);
    }

    internal void Show(WindowAlignmentGuide? xGuide, WindowAlignmentGuide? yGuide, uint dpi)
    {
        int thickness = WindowAlignmentMath.DipToPx(1, dpi);
        ShowLine(_xWindow, xGuide, thickness, ref _xVisible, ref _xRect);
        ShowLine(_yWindow, yGuide, thickness, ref _yVisible, ref _yRect);
    }

    internal void Hide()
    {
        HideLine(_xWindow, ref _xVisible, ref _xRect);
        HideLine(_yWindow, ref _yVisible, ref _yRect);
    }

    public void Dispose()
    {
        if (_xWindow != IntPtr.Zero) _ = NativeMethods.DestroyWindow(_xWindow);
        if (_yWindow != IntPtr.Zero) _ = NativeMethods.DestroyWindow(_yWindow);
        _xWindow = IntPtr.Zero;
        _yWindow = IntPtr.Zero;
        _xVisible = false;
        _yVisible = false;
        _xRect = default;
        _yRect = default;
    }

    private static void EnsureRegistered()
    {
        lock (RegistrationGate)
        {
            if (_registered) return;
            Windows.UI.Color accent = new UISettings().GetColorValue(UIColorType.Accent);
            uint color = accent.R | ((uint)accent.G << 8) | ((uint)accent.B << 16);
            IntPtr background = NativeMethods.CreateSolidBrush(color);
            var windowClass = new NativeMethods.WNDCLASSEX
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                WindowProc = Marshal.GetFunctionPointerForDelegate(WindowProc),
                Instance = NativeMethods.GetModuleHandle(null),
                Background = background,
                ClassName = ClassName
            };
            _registered = NativeMethods.RegisterClassEx(ref windowClass) != 0;
            if (!_registered)
            {
                if (background != IntPtr.Zero) _ = NativeMethods.DeleteObject(background);
                AppLogger.Error($"无法注册窗口对齐指示线，Win32={Marshal.GetLastWin32Error()}。");
            }
        }
    }

    private static IntPtr Create(IntPtr owner, string name)
    {
        if (!_registered) return IntPtr.Zero;
        IntPtr window = NativeMethods.CreateWindowEx(
            unchecked((uint)(NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)),
            ClassName,
            name,
            unchecked((uint)NativeMethods.WS_POPUP),
            0,
            0,
            1,
            1,
            owner,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);
        if (window != IntPtr.Zero) _ = NativeMethods.SetLayeredWindowAttributes(window, 0, 224, NativeMethods.LWA_ALPHA);
        return window;
    }

    private static void ShowLine(
        IntPtr window,
        WindowAlignmentGuide? guide,
        int thickness,
        ref bool visible,
        ref NativeMethods.RECT lastRect)
    {
        if (window == IntPtr.Zero || guide is not WindowAlignmentGuide line || line.End <= line.Start)
        {
            HideLine(window, ref visible, ref lastRect);
            return;
        }

        int left = line.Vertical ? line.Coordinate - thickness / 2 : line.Start;
        int top = line.Vertical ? line.Start : line.Coordinate - thickness / 2;
        int width = Math.Max(1, line.Vertical ? thickness : line.End - line.Start);
        int height = Math.Max(1, line.Vertical ? line.End - line.Start : thickness);
        var nextRect = new NativeMethods.RECT
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height
        };
        if (visible &&
            lastRect.Left == nextRect.Left &&
            lastRect.Top == nextRect.Top &&
            lastRect.Right == nextRect.Right &&
            lastRect.Bottom == nextRect.Bottom)
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            window,
            NativeMethods.HWND_TOP,
            left,
            top,
            width,
            height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_SHOWWINDOW);
        visible = true;
        lastRect = nextRect;
    }

    private static void HideLine(IntPtr window, ref bool visible, ref NativeMethods.RECT lastRect)
    {
        if (window == IntPtr.Zero || !visible) return;
        _ = NativeMethods.SetWindowPos(
            window,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_HIDEWINDOW);
        visible = false;
        lastRect = default;
    }

    private static IntPtr GuideWindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam) =>
        message == NativeMethods.WM_NCHITTEST
            ? new IntPtr(NativeMethods.HTTRANSPARENT)
            : NativeMethods.DefWindowProc(window, message, wParam, lParam);
}
