using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace TuckPane.Services;

/// <summary>
/// Dedicated native-thread WH_MOUSE_LL outside-click detector. Runs off the WinUI 3 UI thread
/// so mouse movement and clicks while an organizer is expanded NEVER stall UI animations or input.
/// </summary>
internal sealed class OutsideClickHook : IDisposable
{
    private readonly IntPtr _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<NativeMethods.POINT> _outsideClick;
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
    private volatile bool _armed;
    private volatile bool _disposed;

    internal OutsideClickHook(IntPtr window, DispatcherQueue dispatcher, Action<NativeMethods.POINT> outsideClick)
    {
        _window = window;
        _dispatcher = dispatcher;
        _outsideClick = outsideClick;
        _callback = OnMouse;
    }

    public void Start()
    {
        HookSession? created = null;
        lock (_lifecycleSync)
        {
            if (_disposed) return;
            _armed = true;
            // Fast path: keep the dedicated hook pump thread warm across expand/collapse transitions.
            if (_session is { StopRequested: false }) return;

            created = new HookSession();
            var thread = new Thread(() => HookThreadMain(created))
            {
                IsBackground = true,
                Name = "TuckPane Outside Click Hook",
                Priority = ThreadPriority.AboveNormal
            };
            created.Thread = thread;
            _session = created;
            try
            {
                thread.Start();
            }
            catch
            {
                _session = null;
                created.AbandonBeforeStart();
                throw;
            }
        }

        created.WaitReady(TimeSpan.FromMilliseconds(500));
    }

    public void Stop()
    {
        _armed = false;
        // Retire callbacks already queued on the UI dispatcher, even if Start follows immediately.
        Interlocked.Increment(ref _generation);
    }

    public void Dispose()
    {
        Thread? threadToJoin = null;
        uint threadId = 0;
        lock (_lifecycleSync)
        {
            _armed = false;
            Interlocked.Increment(ref _generation);
            if (_disposed && _session is null) return;
            _disposed = true;
            HookSession? session = _session;
            _session = null;
            if (session is not null)
            {
                session.StopRequested = true;
                threadToJoin = session.Thread;
                threadId = session.ThreadId;
            }
        }

        if (threadId != 0)
            _ = NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        if (threadToJoin is not null && threadToJoin != Thread.CurrentThread)
            _ = threadToJoin.Join(TimeSpan.FromMilliseconds(400));
    }

    private void HookThreadMain(HookSession session)
    {
        IntPtr installed = IntPtr.Zero;
        bool signaled = false;
        try
        {
            _ = NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);

            lock (_lifecycleSync)
            {
                if (session.StopRequested || _disposed) return;
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
                    if (ReferenceEquals(_session, session)) _session = null;
                }
                AppLogger.Error("无法安装外部点击捕获钩子。");
                return;
            }

            signaled = true;
            session.SignalReady();

            while (!session.StopRequested && !_disposed && NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("外部点击捕获钩子线程异常退出。", ex);
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
                if (ReferenceEquals(_session, session)) _session = null;
            }
        }
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        IntPtr hook = Volatile.Read(ref _hook);
        if (code < 0 || !_armed || _disposed)
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);

        try
        {
            int message = wParam.ToInt32();
            if (message is not (NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN))
                return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);

            if (NativeMethods.GetWindowRect(_window, out NativeMethods.RECT windowRect))
            {
                NativeMethods.MSLLHOOKSTRUCT data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                bool outside = data.Point.X < windowRect.Left || data.Point.X >= windowRect.Right ||
                               data.Point.Y < windowRect.Top || data.Point.Y >= windowRect.Bottom;
                if (outside)
                {
                    NativeMethods.POINT clickPoint = data.Point;
                    long generation = Volatile.Read(ref _generation);
                    _ = _dispatcher.TryEnqueue(() =>
                    {
                        if (_armed && !_disposed && generation == Volatile.Read(ref _generation))
                            _outsideClick(clickPoint);
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("外部点击捕获钩子回调异常。", ex);
        }

        return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
    }
}
