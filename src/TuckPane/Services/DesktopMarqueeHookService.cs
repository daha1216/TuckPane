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

    private sealed class HookSession
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private int _readyRefs = 2;

        internal Thread? Thread;
        internal uint ThreadId;
        internal volatile bool StopRequested;

        internal void SignalReady()
        {
            try { _ready.Set(); }
            finally { ReleaseReadyRef(); }
        }

        internal void WaitReady(TimeSpan timeout)
        {
            try { _ = _ready.Wait(timeout); }
            finally { ReleaseReadyRef(); }
        }

        internal void AbandonBeforeStart()
        {
            ReleaseReadyRef();
            ReleaseReadyRef();
        }

        private void ReleaseReadyRef()
        {
            if (Interlocked.Decrement(ref _readyRefs) == 0)
                _ready.Dispose();
        }
    }

    private HookSession? _session;
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
        HookSession session;
        lock (_lifecycleSync)
        {
            if (_running && _session is not null) return;
            _running = true;
            session = new HookSession();
            var thread = new Thread(() => HookThreadMain(session))
            {
                IsBackground = true,
                Name = "TuckPane Desktop Marquee Hook",
                Priority = ThreadPriority.AboveNormal
            };
            session.Thread = thread;
            _session = session;
            try
            {
                thread.Start();
            }
            catch
            {
                _session = null;
                _running = false;
                session.AbandonBeforeStart();
                throw;
            }
        }

        // Wait outside _lifecycleSync so HookThreadMain can safely publish ThreadId under _lifecycleSync.
        session.WaitReady(TimeSpan.FromMilliseconds(500));
    }

    internal void Stop()
    {
        Thread? threadToJoin;
        uint threadId;
        lock (_lifecycleSync)
        {
            Interlocked.Increment(ref _generation);
            HookSession? session = _session;
            if (!_running && session is null) return;
            _running = false;
            _session = null;
            if (session is not null)
            {
                session.StopRequested = true;
                threadToJoin = session.Thread;
                threadId = session.ThreadId;
            }
            else
            {
                threadToJoin = null;
                threadId = 0;
            }
        }

        if (threadId != 0)
            _ = NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        if (threadToJoin is not null && threadToJoin != Thread.CurrentThread)
            _ = threadToJoin.Join(TimeSpan.FromMilliseconds(400));
    }

    public void Dispose() => Stop();

    private void HookThreadMain(HookSession session)
    {
        IntPtr installed = IntPtr.Zero;
        bool signaled = false;
        try
        {
            _trackingDesktopPress = false;
            _marqueeActivated = false;
            _lastMovePostTimestamp = 0;

            // Force message queue creation before publishing ThreadId so PostThreadMessage(WM_QUIT) never races.
            _ = NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);

            lock (_lifecycleSync)
            {
                if (session.StopRequested) return;
                session.ThreadId = NativeMethods.GetCurrentThreadId();
            }

            installed = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL,
                _callback,
                NativeMethods.GetModuleHandle(null),
                0);
            Volatile.Write(ref _hook, installed);

            if (installed == IntPtr.Zero)
            {
                lock (_lifecycleSync)
                {
                    if (ReferenceEquals(_session, session))
                    {
                        _session = null;
                        _running = false;
                    }
                }
                AppLogger.Error($"无法安装桌面框选联动钩子，Win32={Marshal.GetLastWin32Error()}。");
                return;
            }

            signaled = true;
            session.SignalReady();

            while (!session.StopRequested && _running &&
                   NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
                // Low-level hooks are dispatched automatically inside GetMessage.
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("桌面框选钩子线程异常退出。", ex);
        }
        finally
        {
            if (!signaled) session.SignalReady();
            if (installed != IntPtr.Zero)
            {
                _ = NativeMethods.UnhookWindowsHookEx(installed);
                _ = Interlocked.CompareExchange(ref _hook, IntPtr.Zero, installed);
            }
            lock (_lifecycleSync)
            {
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                    _running = false;
                }
            }
            _trackingDesktopPress = false;
            _marqueeActivated = false;
        }
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        IntPtr hook = Volatile.Read(ref _hook);
        if (code < 0 || !_running)
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);

        try
        {
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
                return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
            }

            if (message == NativeMethods.WM_MOUSEMOVE)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                if (!_marqueeActivated && !MarqueeSelectionMath.IsMarqueeActive(_startPoint, data.Point))
                    return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);

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
                bool commit = message == NativeMethods.WM_LBUTTONUP;
                NativeMethods.RECT rect = MarqueeSelectionMath.NormalizeRect(_startPoint, data.Point);
                _trackingDesktopPress = false;
                _marqueeActivated = false;
                long generation = Volatile.Read(ref _generation);
                _ = _dispatcher.TryEnqueue(() =>
                {
                    if (!_running || generation != Volatile.Read(ref _generation)) return;
                    if (wasMarquee)
                    {
                        if (commit) _onMarqueeCompleted(rect, ctrl, true);
                        else
                        {
                            _onMarqueeCompleted(rect, ctrl, false);
                            if (!ctrl) _onDesktopEmptyClick();
                        }
                    }
                    else if (commit) _onDesktopEmptyClick();
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("桌面框选钩子回调异常。", ex);
        }

        return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
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
