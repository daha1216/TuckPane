using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using WinUIEx;
using WinUIEx.Messaging;
using DwmMargins = TuckPane.Services.NativeMethods.MARGINS;
using Wuc = Windows.UI.Composition;

namespace TuckPane.Services;

/// <summary>
/// Makes a top-level WinUI window transparent. DWM blur-behind is enabled with
/// an empty region only to activate per-pixel alpha composition; on Windows 8
/// and later it does not add visual blur. ThemeBackdrop owns the additional
/// Gaussian blur; its HostBackdrop source can already contain system blur.
/// </summary>
internal sealed class TransparentWindowBackdrop : CompositionBrushBackdrop
{
    private WindowMessageMonitor? _messageMonitor;

    protected override Wuc.CompositionBrush CreateBrush(Wuc.Compositor compositor) =>
        compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop connectedTarget,
        XamlRoot xamlRoot)
    {
        // ICompositionSupportsSystemBackdrop is not guaranteed to be a
        // Window (SystemBackdropElement is a common target), so obtain the
        // HWND from the XamlRoot's AppWindowId just like WinUIEx does.
        IntPtr hwnd = (IntPtr)xamlRoot.ContentIslandEnvironment.AppWindowId.Value;
        Configure(hwnd);
        if (hwnd != IntPtr.Zero)
        {
            _messageMonitor?.Dispose();
            _messageMonitor = new WindowMessageMonitor(hwnd);
            _messageMonitor.WindowMessageReceived += MessageMonitor_WindowMessageReceived;
        }
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _ = ClearBackground(hwnd);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        if (_messageMonitor is not null)
        {
            _messageMonitor.WindowMessageReceived -= MessageMonitor_WindowMessageReceived;
            _messageMonitor.Dispose();
            _messageMonitor = null;
        }
        base.OnTargetDisconnected(disconnectedTarget);
    }

    private void MessageMonitor_WindowMessageReceived(
        object? sender,
        WindowMessageEventArgs e)
    {
        if (e.Message.MessageId == NativeMethods.WM_ERASEBKGND)
        {
            // Clearing to GDI black initializes the client pixels for DWM's
            // alpha-aware composition. The acquired HDC and brush are scoped
            // to this operation so reconnects cannot leak native resources.
            if (ClearBackground(e.Message.Hwnd))
            {
                e.Result = 1;
                e.Handled = true;
            }
            return;
        }

        if (e.Message.MessageId != NativeMethods.WM_DWMCOMPOSITIONCHANGED) return;

        // DWM requires the alpha-composition state to be reapplied whenever
        // composition is toggled. Leave the message unhandled so every other
        // HWND subclass can continue its own frame and border refresh.
        Configure(e.Message.Hwnd);
    }

    internal static void EnsureAlphaComposition(IntPtr hwnd) => Configure(hwnd);

    private static void Configure(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        // Zero margins clear any previous full-client frame extension. The
        // empty blur region below enables alpha without supplying visual blur.
        var margins = new DwmMargins();
        int frameResult = NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        LogResult(frameResult, "DwmExtendFrameIntoClientArea(ZeroMargins)");

        IntPtr emptyRegion = NativeMethods.CreateRectRgn(-2, -2, -1, -1);
        if (emptyRegion == IntPtr.Zero)
        {
            AppLogger.Error(
                $"CreateRectRgn 失败，Win32Error={Marshal.GetLastWin32Error()}。",
                null);
            return;
        }

        try
        {
            var blur = new DwmBlurBehind
            {
                Flags = DwmBlurBehindEnable | DwmBlurBehindBlurRegion,
                Enable = true,
                Region = emptyRegion,
                TransitionOnMaximized = false
            };
            int blurResult = DwmEnableBlurBehindWindow(hwnd, ref blur);
            LogResult(blurResult, "DwmEnableBlurBehindWindow(AlphaComposition)");
        }
        finally
        {
            _ = NativeMethods.DeleteObject(emptyRegion);
        }
    }

    /// <summary>
    /// Fills client pixels with GDI black using the cached stock BLACK_BRUSH when available,
    /// falling back to a temporary brush released via NativeMethods.DeleteObject(brush).
    /// </summary>
    private static bool ClearBackground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        IntPtr hdc = NativeMethods.GetDC(hwnd);
        if (hdc == IntPtr.Zero) return false;
        try
        {
            if (!NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT rect)) return false;

            IntPtr stockBrush = NativeMethods.GetStockObject(NativeMethods.BLACK_BRUSH);
            if (stockBrush != IntPtr.Zero)
            {
                return NativeMethods.FillRect(hdc, ref rect, stockBrush) != 0;
            }

            IntPtr brush = NativeMethods.CreateSolidBrush(0);
            if (brush == IntPtr.Zero) return false;
            try
            {
                return NativeMethods.FillRect(hdc, ref rect, brush) != 0;
            }
            finally
            {
                _ = NativeMethods.DeleteObject(brush);
            }
        }
        finally
        {
            _ = NativeMethods.ReleaseDC(hwnd, hdc);
        }
    }

    private static void LogResult(int hresult, string operation)
    {
        if (hresult >= 0)
            AppLogger.Info($"{operation} 完成，HRESULT=0x{hresult:X8}。");
        else
            AppLogger.Error($"{operation} 失败，HRESULT=0x{hresult:X8}。");
    }

    private const uint DwmBlurBehindEnable = 0x00000001;
    private const uint DwmBlurBehindBlurRegion = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public uint Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Enable;
        public IntPtr Region;
        [MarshalAs(UnmanagedType.Bool)] public bool TransitionOnMaximized;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blurBehind);
}
