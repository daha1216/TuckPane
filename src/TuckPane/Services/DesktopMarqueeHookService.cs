using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using TuckPane.Core;

namespace TuckPane.Services;

/// <summary>
/// Ultra-lightweight, zero-UI-thread-contention low-level mouse hook that runs on its OWN
/// dedicated high-priority native Win32 message-pump thread (NEVER on the WinUI 3 UI thread!).
/// Observes marquee rubber-band drags started directly on the Windows Desktop so TuckPane
/// floating organizers synchronize natively with Windows desktop marquee selection without
/// ever stalling mouse input, Settings scrolling, or UI animations.
/// </summary>
internal sealed class DesktopMarqueeHookService : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<NativeMethods.RECT, bool> _onMarqueeUpdated;
    private readonly Action<NativeMethods.RECT, bool, bool> _onMarqueeCompleted;
    private readonly Action _onDesktopEmptyClick;
    private readonly NativeMethods.HookProc _callback;
    private readonly object _lifecycleSync = new();

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hook;
    private long _generation;
    private volatile bool _running;

    // Accessed exclusively on the dedicated hook thread.
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

    internal bool IsRunning => _running;

    internal void Start()
    {
        lock (_lifecycleSync)
        {
            if (_running) return;
            _running = true;
            using var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() => HookThreadMain(ready))
            {
                IsBackground = true,
                Name = "TuckPane Desktop Marquee Hook",
                Priority = ThreadPriority.AboveNormal
            };
            _hookThread = thread;
            thread.Start();
            ready.Wait(TimeSpan.FromMilliseconds(500));
        }
    }

    internal void Stop()
    {
        Thread? threadToJoin;
        uint threadId;
        lock (_lifecycleSync)
        {
            Interlocked.Increment(ref _generation);
            if (!_running && _hookThread is null) return;
            _running = false;
            threadToJoin = _hookThread;
            threadId = _hookThreadId;
            _hookThread = null;
            _hookThreadId = 0;
        }

        if (threadId != 0)
            _ = NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        if (threadToJoin is not null && threadToJoin != Thread.CurrentThread)
            _ = threadToJoin.Join(TimeSpan.FromMilliseconds(400));
    }

    public void Dispose() => Stop();

    private void HookThreadMain(ManualResetEventSlim ready)
    {
        _hookThreadId = NativeMethods.GetCurrentThreadId();
        _trackingDesktopPress = false;
        _marqueeActivated = false;
        _lastMovePostTimestamp = 0;

        // Force message queue creation before signaling ready so PostThreadMessage(WM_QUIT) never races.
        _ = NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);

        IntPtr installed = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _callback,
            NativeMethods.GetModuleHandle(null),
            0);
        _hook = installed;
        ready.Set();

        if (installed == IntPtr.Zero)
        {
            _running = false;
            AppLogger.Error($"无法安装桌面框选联动钩子，Win32={Marshal.GetLastWin32Error()}。");
            return;
        }

        try
        {
            while (_running && NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
                // Low-level hooks are dispatched automatically inside GetMessage.
            }
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                _ = NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            _trackingDesktopPress = false;
            _marqueeActivated = false;
        }
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !_running)
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

        int message = wParam.ToInt32();

        // Ultra-fast path: when not tracking a desktop press, bypass EVERY WM_MOUSEMOVE / UP / WHEEL
        // in ~15 nanoseconds with zero marshaling and zero Win32 queries!
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
                    if (_running && generation == Volatile.Read(ref _generation))
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
                if (!_running || generation != Volatile.Read(ref _generation)) return;
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
        if (desktopView == IntPtr.Zero)
            desktopView = DesktopLayerService.FindDesktopIconView(allowWorkerSpawn: false);
        if (desktopView == IntPtr.Zero) return false;

        if (hit == desktopView || NativeMethods.IsChild(desktopView, hit))
            return true;

        IntPtr parent = NativeMethods.GetParent(desktopView);
        return parent != IntPtr.Zero && hit == parent;
    }
}
