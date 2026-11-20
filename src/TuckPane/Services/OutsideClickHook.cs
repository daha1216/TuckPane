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

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hook;
    private long _generation;
    private volatile bool _running;

    internal OutsideClickHook(IntPtr window, DispatcherQueue dispatcher, Action<NativeMethods.POINT> outsideClick)
    {
        _window = window;
        _dispatcher = dispatcher;
        _outsideClick = outsideClick;
        _callback = OnMouse;
    }

    public void Start()
    {
        lock (_lifecycleSync)
        {
            if (_running) return;
            _running = true;
            using var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() => HookThreadMain(ready))
            {
                IsBackground = true,
                Name = "TuckPane Outside Click Hook",
                Priority = ThreadPriority.AboveNormal
            };
            _hookThread = thread;
            thread.Start();
            ready.Wait(TimeSpan.FromMilliseconds(500));
        }
    }

    public void Stop() => StopNow();

    public void Dispose() => StopNow();

    private void HookThreadMain(ManualResetEventSlim ready)
    {
        _hookThreadId = NativeMethods.GetCurrentThreadId();
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
            AppLogger.Error("无法安装外部点击捕获钩子。");
            return;
        }

        try
        {
            while (_running && NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                _ = NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !_running)
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

        int message = wParam.ToInt32();
        if (message is not (NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN))
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

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
                    if (_running && generation == Volatile.Read(ref _generation))
                        _outsideClick(clickPoint);
                });
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void StopNow()
    {
        Thread? threadToJoin;
        uint threadId;
        lock (_lifecycleSync)
        {
            // Retire callbacks already queued on the UI dispatcher, even if Start follows immediately.
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
}
