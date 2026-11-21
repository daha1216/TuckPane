using System.Runtime.InteropServices;

namespace TuckPane.Services;

/// <summary>
/// Lightweight reusable pure-Win32 layered overlay that holds a 1:1 snapshot of an organizer's
/// live screen pixels across atomic HWND relocation (cloak -> resize/move -> DComp commit -> uncloak),
/// eliminating 1-frame teleport flashes and post-collapse blinks without stealing focus or hit-testing.
/// </summary>
internal sealed class TransitionBridgeOverlay : IDisposable
{
    private IntPtr _hwnd;
    private bool _visible;
    private bool _wasTopmost;
    private NativeMethods.RECT _currentRect;
    private bool _disposed;

    internal bool IsVisible => _visible && _hwnd != IntPtr.Zero;

    internal NativeMethods.RECT CurrentRect => _currentRect;

    internal bool CaptureAndShow(NativeMethods.RECT screenRect, IntPtr targetWindow, bool topmost = false)
    {
        int width = screenRect.Width;
        int height = screenRect.Height;
        if (_disposed || width <= 0 || height <= 0)
            return false;

        if (!EnsureWindow())
            return false;

        if (_visible && RectsEqual(_currentRect, screenRect))
        {
            if (PositionBridgeWindow(targetWindow, topmost))
                return true;
            Hide();
            return false;
        }

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                return FailCapture();

            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
                return FailCapture();

            var bitmapInfo = new NativeMethods.BITMAPINFO
            {
                Header = new NativeMethods.BITMAPINFOHEADER
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    Width = width,
                    Height = -height, // Top-down 32bpp DIB
                    Planes = 1,
                    BitCount = 32,
                    Compression = NativeMethods.BI_RGB
                }
            };

            dib = NativeMethods.CreateDIBSection(
                screenDc,
                ref bitmapInfo,
                NativeMethods.DIB_RGB_COLORS,
                out _,
                IntPtr.Zero,
                0);
            if (dib == IntPtr.Zero)
                return FailCapture();

            oldBitmap = NativeMethods.SelectObject(memDc, dib);
            if (oldBitmap == IntPtr.Zero)
                return FailCapture();

            if (!NativeMethods.BitBlt(
                    memDc,
                    0,
                    0,
                    width,
                    height,
                    screenDc,
                    screenRect.Left,
                    screenRect.Top,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
            {
                return FailCapture();
            }

            var dstPos = new NativeMethods.POINT { X = screenRect.Left, Y = screenRect.Top };
            var size = new NativeMethods.SIZE { Width = width, Height = height };
            var srcPos = new NativeMethods.POINT { X = 0, Y = 0 };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                // CRITICAL: BitBlt from the screen DC leaves alpha bytes as 0x00, so AlphaFormat must be 0
                // (constant opaque alpha 255 over the already-composited screen RGB pixels).
                AlphaFormat = 0
            };

            if (!NativeMethods.UpdateLayeredWindow(
                    _hwnd,
                    screenDc,
                    ref dstPos,
                    ref size,
                    memDc,
                    ref srcPos,
                    0,
                    ref blend,
                    NativeMethods.ULW_ALPHA))
            {
                return FailCapture();
            }

            if (!PositionBridgeWindow(targetWindow, topmost))
                return FailCapture();

            _visible = true;
            _currentRect = screenRect;
            return true;
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero && memDc != IntPtr.Zero)
                _ = NativeMethods.SelectObject(memDc, oldBitmap);
            if (dib != IntPtr.Zero)
                _ = NativeMethods.DeleteObject(dib);
            if (memDc != IntPtr.Zero)
                _ = NativeMethods.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero)
                _ = NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    internal void Hide()
    {
        if (!_visible || _hwnd == IntPtr.Zero)
            return;

        _visible = false;
        _currentRect = default;
        if (NativeMethods.IsWindow(_hwnd))
        {
            _ = NativeMethods.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE |
                NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER |
                NativeMethods.SWP_HIDEWINDOW);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Hide();
        IntPtr hwnd = _hwnd;
        _hwnd = IntPtr.Zero;
        _visible = false;
        _wasTopmost = false;
        _currentRect = default;
        if (hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd))
        {
            _ = NativeMethods.DestroyWindow(hwnd);
        }
    }

    private bool EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            if (NativeMethods.IsWindow(_hwnd))
                return true;

            _hwnd = IntPtr.Zero;
            _visible = false;
            _wasTopmost = false;
            _currentRect = default;
        }

        long exStyle =
            NativeMethods.WS_EX_LAYERED |
            NativeMethods.WS_EX_TRANSPARENT |
            NativeMethods.WS_EX_NOACTIVATE |
            NativeMethods.WS_EX_TOOLWINDOW;

        IntPtr hwnd = NativeMethods.CreateWindowEx(
            exStyle,
            "STATIC",
            null,
            NativeMethods.WS_POPUP,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            return false;

        _ = NativeMethods.DisableWindowTransitions(hwnd);
        _hwnd = hwnd;
        return true;
    }

    private bool PositionBridgeWindow(IntPtr targetWindow, bool topmost)
    {
        if (!topmost && _wasTopmost)
        {
            _ = NativeMethods.SetWindowPos(
                _hwnd,
                NativeMethods.HWND_NOTOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE |
                NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER);
            _wasTopmost = false;
        }

        IntPtr insertAfter;
        bool skipZOrder = false;
        if (topmost)
        {
            insertAfter = NativeMethods.HWND_TOPMOST;
            _wasTopmost = true;
        }
        else if (targetWindow != IntPtr.Zero && NativeMethods.IsWindow(targetWindow))
        {
            IntPtr aboveTarget = NativeMethods.GetWindow(targetWindow, NativeMethods.GW_HWNDPREV);
            if (aboveTarget == _hwnd)
            {
                insertAfter = IntPtr.Zero;
                skipZOrder = true;
            }
            else if (aboveTarget == IntPtr.Zero || !NativeMethods.IsWindow(aboveTarget))
            {
                insertAfter = NativeMethods.HWND_TOP;
            }
            else
            {
                insertAfter = aboveTarget;
            }
        }
        else
        {
            insertAfter = NativeMethods.HWND_TOP;
        }

        uint flags =
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_SHOWWINDOW |
            NativeMethods.SWP_NOOWNERZORDER;
        if (skipZOrder)
            flags |= NativeMethods.SWP_NOZORDER;

        return NativeMethods.SetWindowPos(_hwnd, insertAfter, 0, 0, 0, 0, flags);
    }

    private bool FailCapture()
    {
        if (_visible)
            Hide();
        return false;
    }

    private static bool RectsEqual(NativeMethods.RECT first, NativeMethods.RECT second) =>
        first.Left == second.Left &&
        first.Top == second.Top &&
        first.Right == second.Right &&
        first.Bottom == second.Bottom;
}
