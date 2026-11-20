using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using TuckPane.Core;

namespace TuckPane.Services;

/// <summary>
/// Ultra-lightweight, non-blocking low-level mouse hook that observes marquee rubber-band
/// drag rectangles started directly on the Windows Desktop (SysListView32 / SHELLDLL_DefView)
/// so TuckPane floating organizers synchronize natively with Windows desktop marquee selection.
/// Never swallows or delays any mouse event; ignores mouse moves when not actively tracking
/// a desktop drag so idle CPU overhead is effectively zero.
/// </summary>
internal sealed class DesktopMarqueeHookService : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<NativeMethods.RECT, bool> _onMarqueeUpdated;
    private readonly Action<NativeMethods.RECT, bool, bool> _onMarqueeCompleted;
    private readonly Action _onDesktopEmptyClick;
    private readonly NativeMethods.HookProc _callback;

    private IntPtr _hook;
    private long _generation;
    private bool _trackingDesktopPress;
    private bool _marqueeActivated;
    private bool _ctrlAtStart;
    private NativeMethods.POINT _startPoint;
    private long _lastMovePostTimestamp;

    internal DesktopMarqueeHookService(
        DispatcherQueue dispatcher,
        Action<NativeMethods.RECT, bool> onMarqueeUpdated,
        Action<NativeMethods.RECT, bool, bool> onMarqueeCompleted,
        Action onDesktopEmptyClick)
    {
        _dispatcher = dispatcher;
        _onMarqueeUpdated = onMarqueeUpdated;
        _onMarqueeCompleted = onMarqueeCompleted;
        _onDesktopEmptyClick = onDesktopEmptyClick;
        _callback = OnMouse;
    }

    internal bool IsRunning => _hook != IntPtr.Zero;

    internal void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _callback,
            NativeMethods.GetModuleHandle(null),
            0);
        if (_hook == IntPtr.Zero)
            AppLogger.Error($"无法安装桌面框选联动钩子，Win32={Marshal.GetLastWin32Error()}。");
    }

    internal void Stop()
    {
        Interlocked.Increment(ref _generation);
        _trackingDesktopPress = false;
        _marqueeActivated = false;
        if (_hook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || _hook == IntPtr.Zero)
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

        int message = wParam.ToInt32();

        // Fast path: when not tracking a desktop press, ignore every WM_MOUSEMOVE / UP immediately.
        if (!_trackingDesktopPress)
        {
            if (message == NativeMethods.WM_LBUTTONDOWN)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                if (IsDesktopSurfaceAt(data.Point))
                {
                    _trackingDesktopPress = true;
                    _marqueeActivated = false;
                    _startPoint = data.Point;
                    _ctrlAtStart = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
                    _lastMovePostTimestamp = 0;
                }
            }
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (message == NativeMethods.WM_MOUSEMOVE)
        {
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            if (!_marqueeActivated && !MarqueeSelectionMath.IsMarqueeActive(_startPoint, data.Point))
                return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

            _marqueeActivated = true;
            long now = Stopwatch.GetTimestamp();
            if (_lastMovePostTimestamp == 0 ||
                Stopwatch.GetElapsedTime(_lastMovePostTimestamp, now).TotalMilliseconds >= 16)
            {
                _lastMovePostTimestamp = now;
                NativeMethods.RECT rect = MarqueeSelectionMath.NormalizeRect(_startPoint, data.Point);
                bool ctrl = _ctrlAtStart;
                long generation = Volatile.Read(ref _generation);
                _ = _dispatcher.TryEnqueue(() =>
                {
                    if (_hook != IntPtr.Zero && generation == Volatile.Read(ref _generation))
                        _onMarqueeUpdated(rect, ctrl);
                });
            }
        }
        else if (message is NativeMethods.WM_LBUTTONUP or NativeMethods.WM_RBUTTONDOWN)
        {
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            bool wasMarquee = _marqueeActivated;
            bool ctrl = _ctrlAtStart;
            NativeMethods.RECT rect = MarqueeSelectionMath.NormalizeRect(_startPoint, data.Point);
            _trackingDesktopPress = false;
            _marqueeActivated = false;
            long generation = Volatile.Read(ref _generation);
            _ = _dispatcher.TryEnqueue(() =>
            {
                if (_hook == IntPtr.Zero || generation != Volatile.Read(ref _generation)) return;
                if (wasMarquee) _onMarqueeCompleted(rect, ctrl, true);
                else if (message == NativeMethods.WM_LBUTTONUP) _onDesktopEmptyClick();
            });
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsDesktopSurfaceAt(NativeMethods.POINT point)
    {
        IntPtr hit = NativeMethods.WindowFromPoint(point);
        if (hit == IntPtr.Zero) return false;

        IntPtr desktopView = DesktopLayerService.GetCachedDesktopIconView();
        if (desktopView == IntPtr.Zero) return false;

        if (hit == desktopView || NativeMethods.IsChild(desktopView, hit))
            return true;

        IntPtr parent = NativeMethods.GetParent(desktopView);
        return parent != IntPtr.Zero && hit == parent;
    }
}
