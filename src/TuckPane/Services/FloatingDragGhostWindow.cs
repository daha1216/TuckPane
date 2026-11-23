using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using TuckPane.Core;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Windows.UI.Text;

namespace TuckPane.Services;

internal enum FloatingDragBadgeKind
{
    None = 0,
    MoveToDesktop = 1,
    CopyToDesktop = 2,
    MoveToOrganizer = 3,
    CopyToOrganizer = 4,
    ExternalApp = 5
}

internal sealed record FloatingDragGhostItemDescriptor(
    string FullPath,
    string DisplayName);

internal sealed record FloatingDragGhostDescriptor(
    IReadOnlyList<FloatingDragGhostItemDescriptor> Items,
    double CardWidthDip,
    double CardHeightDip,
    double IconSizeDip,
    double GrabOffsetXDip,
    double GrabOffsetYDip,
    double InitialDpiScale,
    NativeMethods.POINT InitialCursorScreenPx,
    float InitialScale,
    float InitialTiltDeg,
    bool IsDarkTheme,
    uint ThemeAccentArgb);

internal sealed class FloatingDragGhostController : IDisposable
{
    private const string WindowClassName = "TuckPane.FloatingDragGhost";
    private const int MaxIconCacheEntries = 64;
    private const int MaxVisibleDeckItems = 3;

    private static readonly object SharedGate = new();
    private static FloatingDragGhostController? _shared;

    private static readonly object ClassRegistrationGate = new();
    private static readonly NativeMethods.WindowProc RootedWindowProc = GhostWindowProc;
    private static bool _windowClassRegistered;

    private enum GhostPhase
    {
        Idle = 0,
        Dragging = 1,
        Landing = 2,
        Rebounding = 3
    }

    private sealed class CachedIconEntry
    {
        public IconCacheService.IconSnapshot? Snapshot;
        public int RequestedSize;
        public bool Loading;
        public long LastUsedTimestamp;
    }

    private readonly record struct SurfaceMetrics(
        double CardWidthDip,
        double CardHeightDip,
        double IconSizeDip,
        double PadLeftDip,
        double PadTopDip,
        double SurfaceWidthDip,
        double SurfaceHeightDip,
        int CardPadLeftPx,
        int CardPadTopPx,
        int SurfaceWidthPx,
        int SurfaceHeightPx,
        double DpiScale);

    private readonly object _sync = new();
    private readonly AutoResetEvent _wakeEvent = new(initialState: false);
    private readonly Dictionary<string, CachedIconEntry> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private Thread? _renderThread;
    private bool _disposed;

    // Shared session state (guarded by _sync)
    private long _nextSessionToken;
    private long _activeToken;
    private GhostPhase _phase;
    private FloatingDragGhostDescriptor? _activeDescriptor;
    private FloatingDragBadgeKind _targetBadgeKind;
    private string? _targetBadgeText;
    private NativeMethods.POINT? _completionTargetCenterPx;
    private long _sessionGeneration;
    private long _iconRevision;

    // Render-thread-only state (owned exclusively by _renderThread)
    private IntPtr _hwnd;
    private bool _windowVisible;
    private IntPtr _memDc;
    private IntPtr _dibBitmap;
    private IntPtr _oldDibBitmap;
    private IntPtr _dibBits;
    private int _dibWidth;
    private int _dibHeight;
    private byte[]? _softwarePixelBuffer;

    private CanvasDevice? _canvasDevice;
    private CanvasRenderTarget? _renderTarget;
    private int _renderTargetWidth;
    private int _renderTargetHeight;
    private readonly Dictionary<string, (IconCacheService.IconSnapshot Source, CanvasBitmap Bitmap)> _gpuIconBitmaps = new(StringComparer.OrdinalIgnoreCase);

    // Render-thread physics & dirty-tracking state
    private long _renderSessionGeneration = -1;
    private long _renderIconRevision = -1;
    private GhostPhase _renderPhase;
    private long _lastFrameTick;
    private long _completionStartTick;
    private bool _hasLastCursor;
    private NativeMethods.POINT _lastCursorPx;
    private float _smoothedVxPxPerSec;
    private float _smoothedVyPxPerSec;
    private Vector3 _centerPosPx;
    private Vector3 _centerVelPx;
    private float _scale = 1f;
    private float _scaleVel;
    private float _tiltDeg;
    private float _tiltVel;
    private float _masterAlpha = 1f;
    private float _badgeOpacity;
    private float _badgeOpacityVel;
    private float _badgeOffsetY = 6f;
    private float _badgeOffsetYVel;
    private FloatingDragBadgeKind _displayedBadgeKind;
    private string? _displayedBadgeText;

    private bool _hasRenderedSurface;
    private double _lastRenderedDpiScale;
    private float _lastRenderedScale;
    private float _lastRenderedTiltDeg;
    private float _lastRenderedBadgeOpacity;
    private float _lastRenderedBadgeOffsetY;
    private FloatingDragBadgeKind _lastRenderedBadgeKind;
    private string? _lastRenderedBadgeText;
    private byte _lastUploadedMasterAlpha;
    private int _lastWinLeft = int.MinValue;
    private int _lastWinTop = int.MinValue;

    internal static FloatingDragGhostController Shared
    {
        get
        {
            lock (SharedGate)
            {
                return _shared ??= new FloatingDragGhostController();
            }
        }
    }

    internal bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && _phase != GhostPhase.Idle && _activeToken != 0;
            }
        }
    }

    internal void Prewarm(IReadOnlyList<FloatingDragGhostItemDescriptor> items, int iconPixelSize)
    {
        if (AppPaths.IsTestMode || items is null || items.Count == 0) return;

        int safeSize = Math.Clamp(iconPixelSize, 48, 256);
        int count = Math.Min(items.Count, MaxVisibleDeckItems);
        for (int i = 0; i < count; i++)
        {
            string path = items[i].FullPath;
            if (!string.IsNullOrWhiteSpace(path))
                RequestIconExtraction(path, safeSize);
        }
    }

    internal long BeginSession(FloatingDragGhostDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        long token;
        lock (_sync)
        {
            if (_disposed) return 0;

            token = ++_nextSessionToken;
            _activeToken = token;
            _activeDescriptor = descriptor;
            _phase = GhostPhase.Dragging;
            _targetBadgeKind = FloatingDragBadgeKind.None;
            _targetBadgeText = null;
            _completionTargetCenterPx = null;
            _sessionGeneration++;

            if (!AppPaths.IsTestMode)
                EnsureRenderThreadLocked();
        }

        if (!AppPaths.IsTestMode)
        {
            double safeDpi = double.IsFinite(descriptor.InitialDpiScale) ? Math.Clamp(descriptor.InitialDpiScale, 1.0, 4.0) : 1.0;
            double safeIconDip = double.IsFinite(descriptor.IconSizeDip) ? Math.Clamp(descriptor.IconSizeDip, 28.0, 192.0) : 56.0;
            int desiredIconPx = Math.Clamp((int)Math.Ceiling(safeIconDip * safeDpi * 2.0), 96, 256);
            Prewarm(descriptor.Items, desiredIconPx);
            _wakeEvent.Set();
        }

        return token;
    }

    internal void UpdateBadge(long sessionToken, FloatingDragBadgeKind kind, string? text)
    {
        string? normalizedText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (normalizedText is null) kind = FloatingDragBadgeKind.None;

        bool changed = false;
        lock (_sync)
        {
            if (_disposed || sessionToken == 0 || sessionToken != _activeToken || _phase != GhostPhase.Dragging)
                return;

            if (_targetBadgeKind != kind || !string.Equals(_targetBadgeText, normalizedText, StringComparison.Ordinal))
            {
                _targetBadgeKind = kind;
                _targetBadgeText = normalizedText;
                changed = true;
            }
        }

        if (changed && !AppPaths.IsTestMode)
            _wakeEvent.Set();
    }

    internal void CompleteWithLanding(long sessionToken, NativeMethods.POINT? landingCenterScreenPx = null)
    {
        bool wake = false;
        lock (_sync)
        {
            if (_disposed || sessionToken == 0 || sessionToken != _activeToken)
                return;

            if (AppPaths.IsTestMode)
            {
                _phase = GhostPhase.Idle;
                _activeToken = 0;
                _activeDescriptor = null;
                _targetBadgeKind = FloatingDragBadgeKind.None;
                _targetBadgeText = null;
                _completionTargetCenterPx = null;
                return;
            }

            _phase = GhostPhase.Landing;
            _completionTargetCenterPx = landingCenterScreenPx;
            _targetBadgeKind = FloatingDragBadgeKind.None;
            _targetBadgeText = null;
            wake = true;
        }

        if (wake) _wakeEvent.Set();
    }

    internal void CompleteWithRebound(long sessionToken, NativeMethods.POINT originCenterScreenPx)
    {
        bool wake = false;
        lock (_sync)
        {
            if (_disposed || sessionToken == 0 || sessionToken != _activeToken)
                return;

            if (AppPaths.IsTestMode)
            {
                _phase = GhostPhase.Idle;
                _activeToken = 0;
                _activeDescriptor = null;
                _targetBadgeKind = FloatingDragBadgeKind.None;
                _targetBadgeText = null;
                _completionTargetCenterPx = null;
                return;
            }

            _phase = GhostPhase.Rebounding;
            _completionTargetCenterPx = originCenterScreenPx;
            _targetBadgeKind = FloatingDragBadgeKind.None;
            _targetBadgeText = null;
            wake = true;
        }

        if (wake) _wakeEvent.Set();
    }

    internal void CancelImmediate(long sessionToken = 0)
    {
        bool wake = false;
        lock (_sync)
        {
            if (_disposed) return;
            if (sessionToken != 0 && sessionToken != _activeToken) return;

            _phase = GhostPhase.Idle;
            _activeToken = 0;
            _activeDescriptor = null;
            _targetBadgeKind = FloatingDragBadgeKind.None;
            _targetBadgeText = null;
            _completionTargetCenterPx = null;
            _sessionGeneration++;
            wake = !AppPaths.IsTestMode && _renderThread is not null;
        }

        if (wake) _wakeEvent.Set();
    }

    internal static void ShutdownShared()
    {
        FloatingDragGhostController? instance;
        lock (SharedGate)
        {
            instance = _shared;
            _shared = null;
        }
        instance?.Dispose();
    }

    public void Dispose()
    {
        Thread? threadToJoin;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _phase = GhostPhase.Idle;
            _activeToken = 0;
            _activeDescriptor = null;
            _targetBadgeKind = FloatingDragBadgeKind.None;
            _targetBadgeText = null;
            _completionTargetCenterPx = null;
            threadToJoin = _renderThread;
            _renderThread = null;
        }

        _wakeEvent.Set();
        if (threadToJoin is not null && threadToJoin != Thread.CurrentThread)
        {
            try { _ = threadToJoin.Join(millisecondsTimeout: 600); }
            catch { }
        }

        _wakeEvent.Dispose();
    }

    private void EnsureRenderThreadLocked()
    {
        if (_renderThread is not null || _disposed) return;

        var thread = new Thread(RenderThreadMain)
        {
            Name = "TuckPane.FloatingDragGhost",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _renderThread = thread;
        thread.Start();
    }

    private void RequestIconExtraction(string path, int desiredSize)
    {
        string key = NormalizeCacheKey(path);
        bool shouldStart = false;

        lock (_sync)
        {
            if (_disposed) return;

            if (_iconCache.TryGetValue(key, out CachedIconEntry? existing))
            {
                existing.LastUsedTimestamp = Stopwatch.GetTimestamp();
                // Accept any already-cached high-resolution icon (>= 64px and >= half of desired supersample size)
                // or already in-flight load of sufficient size.
                if (existing.Snapshot is not null && existing.Snapshot.Width >= Math.Min(desiredSize, 96))
                    return;
                if (existing.Loading && existing.RequestedSize >= desiredSize)
                    return;
                existing.Loading = true;
                existing.RequestedSize = Math.Max(existing.RequestedSize, desiredSize);
                shouldStart = true;
            }
            else
            {
                EvictOldestIconCacheLocked();
                _iconCache[key] = new CachedIconEntry
                {
                    RequestedSize = desiredSize,
                    Loading = true,
                    LastUsedTimestamp = Stopwatch.GetTimestamp()
                };
                shouldStart = true;
            }
        }

        if (!shouldStart) return;

        _ = Task.Run(() =>
        {
            IconCacheService.IconSnapshot? snapshot = IconCacheService.CreatePremultipliedIconSnapshot(path, desiredSize);
            bool wake = false;
            lock (_sync)
            {
                if (_disposed) return;
                if (_iconCache.TryGetValue(key, out CachedIconEntry? entry))
                {
                    entry.Loading = false;
                    entry.LastUsedTimestamp = Stopwatch.GetTimestamp();
                    if (snapshot is not null && (entry.Snapshot is null || snapshot.Width >= entry.Snapshot.Width))
                    {
                        entry.Snapshot = snapshot;
                        _iconRevision++;
                        wake = _phase != GhostPhase.Idle;
                    }
                }
            }
            if (wake) _wakeEvent.Set();
        });
    }

    private IconCacheService.IconSnapshot? TryGetCachedIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string key = NormalizeCacheKey(path);
        lock (_sync)
        {
            if (_iconCache.TryGetValue(key, out CachedIconEntry? entry))
            {
                entry.LastUsedTimestamp = Stopwatch.GetTimestamp();
                return entry.Snapshot;
            }
            return null;
        }
    }

    private void EvictOldestIconCacheLocked()
    {
        if (_iconCache.Count < MaxIconCacheEntries) return;
        string? oldestKey = null;
        long oldestStamp = long.MaxValue;
        foreach (KeyValuePair<string, CachedIconEntry> pair in _iconCache)
        {
            if (pair.Value.LastUsedTimestamp < oldestStamp)
            {
                oldestStamp = pair.Value.LastUsedTimestamp;
                oldestKey = pair.Key;
            }
        }
        if (oldestKey is not null) _iconCache.Remove(oldestKey);
    }

    private void RenderThreadMain()
    {
        try
        {
            _ = FloatingDragNativeMethods.SetThreadDpiAwarenessContext(
                FloatingDragNativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch
        {
            // Ignore on downlevel environments where PerMonitorV2 is already set via manifest.
        }

        try
        {
            while (true)
            {
                PumpThreadMessages();

                bool disposed;
                GhostPhase phase;
                long sessionToken;
                long sessionGen;
                long iconRev;
                FloatingDragGhostDescriptor? descriptor;
                FloatingDragBadgeKind badgeKind;
                string? badgeText;
                NativeMethods.POINT? completionTarget;

                lock (_sync)
                {
                    disposed = _disposed;
                    phase = _phase;
                    sessionToken = _activeToken;
                    sessionGen = _sessionGeneration;
                    iconRev = _iconRevision;
                    descriptor = _activeDescriptor;
                    badgeKind = _targetBadgeKind;
                    badgeText = _targetBadgeText;
                    completionTarget = _completionTargetCenterPx;
                }

                if (disposed) break;

                if (phase == GhostPhase.Idle || descriptor is null || sessionToken == 0)
                {
                    HideNativeWindow();
                    _renderPhase = GhostPhase.Idle;
                    _hasLastCursor = false;
                    // Sleep at 0.00% CPU until BeginSession or Dispose signals _wakeEvent.
                    _wakeEvent.WaitOne();
                    continue;
                }

                bool isNewSession = sessionGen != _renderSessionGeneration;
                if (!isNewSession)
                {
                    WaitForNextVsync();
                    PumpThreadMessages();

                    // Re-read state right after VSync in case CancelImmediate / Complete arrived during DwmFlush.
                    lock (_sync)
                    {
                        disposed = _disposed;
                        phase = _phase;
                        sessionToken = _activeToken;
                        sessionGen = _sessionGeneration;
                        iconRev = _iconRevision;
                        descriptor = _activeDescriptor;
                        badgeKind = _targetBadgeKind;
                        badgeText = _targetBadgeText;
                        completionTarget = _completionTargetCenterPx;
                    }

                    if (disposed) break;
                    if (phase == GhostPhase.Idle || descriptor is null || sessionToken == 0)
                    {
                        HideNativeWindow();
                        _renderPhase = GhostPhase.Idle;
                        continue;
                    }
                    isNewSession = sessionGen != _renderSessionGeneration;
                }

                try
                {
                    bool finished = StepAndPresentFrame(
                        isNewSession,
                        sessionGen,
                        iconRev,
                        phase,
                        descriptor,
                        badgeKind,
                        badgeText,
                        completionTarget);

                    if (finished)
                    {
                        HideNativeWindow();
                        lock (_sync)
                        {
                            if (_activeToken == sessionToken && _sessionGeneration == sessionGen)
                            {
                                _phase = GhostPhase.Idle;
                                _activeToken = 0;
                                _activeDescriptor = null;
                                _targetBadgeKind = FloatingDragBadgeKind.None;
                                _targetBadgeText = null;
                                _completionTargetCenterPx = null;
                            }
                        }
                        _renderPhase = GhostPhase.Idle;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("悬浮拖拽幽灵窗渲染帧异常，已安全回退隐藏。", ex);
                    HideNativeWindow();
                    _wakeEvent.WaitOne(16);
                }
            }
        }
        finally
        {
            CleanupRenderThreadResources();
        }
    }

    private void WaitForNextVsync()
    {
        if (FloatingDragNativeMethods.DwmIsCompositionEnabled(out bool enabled) >= 0 && enabled)
        {
            if (NativeMethods.DwmFlush() >= 0)
                return;
        }
        _wakeEvent.WaitOne(8);
    }

    private static void PumpThreadMessages()
    {
        int guard = 0;
        while (guard++ < 32 &&
               NativeMethods.PeekMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0, FloatingDragNativeMethods.PM_REMOVE))
        {
            _ = FloatingDragNativeMethods.TranslateMessage(ref msg);
            _ = FloatingDragNativeMethods.DispatchMessage(ref msg);
        }
    }

    private bool StepAndPresentFrame(
        bool isNewSession,
        long sessionGen,
        long iconRev,
        GhostPhase phase,
        FloatingDragGhostDescriptor descriptor,
        FloatingDragBadgeKind targetBadgeKind,
        string? targetBadgeText,
        NativeMethods.POINT? completionTargetCenterPx)
    {
        if (!EnsureNativeWindow()) return true;

        long nowTick = Stopwatch.GetTimestamp();
        double dt;

        NativeMethods.POINT cursorPx = descriptor.InitialCursorScreenPx;
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT liveCursor))
            cursorPx = liveCursor;

        double fallbackDpi = double.IsFinite(descriptor.InitialDpiScale)
            ? Math.Clamp(descriptor.InitialDpiScale, 1.0, 4.0)
            : 1.0;

        if (isNewSession)
        {
            _renderSessionGeneration = sessionGen;
            _renderIconRevision = -1;
            _renderPhase = phase;
            _lastFrameTick = nowTick;
            _completionStartTick = phase is GhostPhase.Landing or GhostPhase.Rebounding ? nowTick : 0;
            _hasLastCursor = false;
            _smoothedVxPxPerSec = 0f;
            _smoothedVyPxPerSec = 0f;
            _scale = float.IsFinite(descriptor.InitialScale) ? Math.Clamp(descriptor.InitialScale, 0.7f, 1.35f) : 1.04f;
            _scaleVel = 0f;
            _tiltDeg = float.IsFinite(descriptor.InitialTiltDeg) ? Math.Clamp(descriptor.InitialTiltDeg, -12f, 12f) : 0f;
            _tiltVel = 0f;
            _masterAlpha = 1f;
            _badgeOpacity = 0f;
            _badgeOpacityVel = 0f;
            _badgeOffsetY = 6f;
            _badgeOffsetYVel = 0f;
            _displayedBadgeKind = FloatingDragBadgeKind.None;
            _displayedBadgeText = null;
            _hasRenderedSurface = false;
            _lastWinLeft = int.MinValue;
            _lastWinTop = int.MinValue;
            dt = 0.008;

            double initDpi = DetectMonitorDpiScale(cursorPx, fallbackDpi);
            SurfaceMetrics initMetrics = ComputeSurfaceMetrics(descriptor, initDpi);
            double initGrabX = double.IsFinite(descriptor.GrabOffsetXDip)
                ? Math.Clamp(descriptor.GrabOffsetXDip, 0.0, initMetrics.CardWidthDip)
                : initMetrics.CardWidthDip * 0.5;
            double initGrabY = double.IsFinite(descriptor.GrabOffsetYDip)
                ? Math.Clamp(descriptor.GrabOffsetYDip, 0.0, initMetrics.CardHeightDip)
                : initMetrics.CardHeightDip * 0.5;
            (int initWinLeft, int initWinTop) = CrossWindowDragMath.ComputeGhostWindowOriginPx(
                cursorPx.X,
                cursorPx.Y,
                (int)Math.Round(initGrabX * initDpi),
                (int)Math.Round(initGrabY * initDpi),
                initMetrics.CardPadLeftPx,
                initMetrics.CardPadTopPx);
            _centerPosPx = new Vector3(
                initWinLeft + initMetrics.CardPadLeftPx + (float)(initMetrics.CardWidthDip * 0.5 * initDpi),
                initWinTop + initMetrics.CardPadTopPx + (float)(initMetrics.CardHeightDip * 0.5 * initDpi),
                0f);
            _centerVelPx = Vector3.Zero;
        }
        else
        {
            dt = Stopwatch.GetElapsedTime(_lastFrameTick, nowTick).TotalSeconds;
            dt = double.IsFinite(dt) ? Math.Clamp(dt, 0.001, 0.050) : 0.008;
            _lastFrameTick = nowTick;
        }

        if (_renderPhase != phase)
        {
            _renderPhase = phase;
            if (phase is GhostPhase.Landing or GhostPhase.Rebounding)
            {
                _completionStartTick = nowTick;
                // Cap inherited fling velocity for controlled, silky landing/rebound curves.
                float speed = _centerVelPx.Length();
                if (speed > 2600f)
                    _centerVelPx = Vector3.Normalize(_centerVelPx) * 2600f;
            }
        }

        NativeMethods.POINT dpiProbePoint = phase == GhostPhase.Dragging
            ? cursorPx
            : new NativeMethods.POINT
            {
                X = (int)Math.Round(_centerPosPx.X),
                Y = (int)Math.Round(_centerPosPx.Y)
            };
        double liveDpiScale = DetectMonitorDpiScale(dpiProbePoint, fallbackDpi);

        SurfaceMetrics metrics = ComputeSurfaceMetrics(descriptor, liveDpiScale);

        double grabOffsetXDip = double.IsFinite(descriptor.GrabOffsetXDip)
            ? Math.Clamp(descriptor.GrabOffsetXDip, 0.0, metrics.CardWidthDip)
            : metrics.CardWidthDip * 0.5;
        double grabOffsetYDip = double.IsFinite(descriptor.GrabOffsetYDip)
            ? Math.Clamp(descriptor.GrabOffsetYDip, 0.0, metrics.CardHeightDip)
            : metrics.CardHeightDip * 0.5;
        int grabOffsetXPx = (int)Math.Round(grabOffsetXDip * liveDpiScale);
        int grabOffsetYPx = (int)Math.Round(grabOffsetYDip * liveDpiScale);

        int winLeft;
        int winTop;
        bool animationFinished = false;

        if (phase == GhostPhase.Dragging)
        {
            if (_hasLastCursor && dt > 0.0005)
            {
                float instVx = (float)((cursorPx.X - _lastCursorPx.X) / dt);
                float instVy = (float)((cursorPx.Y - _lastCursorPx.Y) / dt);
                float ema = 1f - MathF.Exp((float)(-dt / 0.025));
                _smoothedVxPxPerSec += (instVx - _smoothedVxPxPerSec) * ema;
                _smoothedVyPxPerSec += (instVy - _smoothedVyPxPerSec) * ema;
            }
            else
            {
                _smoothedVxPxPerSec = 0f;
                _smoothedVyPxPerSec = 0f;
            }
            _lastCursorPx = cursorPx;
            _hasLastCursor = true;

            float targetTilt = CrossWindowDragMath.ComputeVelocityTiltTargetDeg(_smoothedVxPxPerSec, liveDpiScale);
            _ = ItemMotionMath.StepScalarSpring(ref _tiltDeg, ref _tiltVel, targetTilt, dt, responseSeconds: 0.16, dampingFraction: 0.72);
            _ = ItemMotionMath.StepScalarSpring(ref _scale, ref _scaleVel, 1.08f, dt, responseSeconds: 0.20, dampingFraction: 0.80);
            _masterAlpha = 1f;

            bool wantsBadge = targetBadgeKind != FloatingDragBadgeKind.None && !string.IsNullOrWhiteSpace(targetBadgeText);
            if (wantsBadge)
            {
                _displayedBadgeKind = targetBadgeKind;
                _displayedBadgeText = targetBadgeText;
            }
            _ = ItemMotionMath.StepScalarSpring(ref _badgeOpacity, ref _badgeOpacityVel, wantsBadge ? 1f : 0f, dt, responseSeconds: 0.18, dampingFraction: 0.86);
            _badgeOpacity = Math.Clamp(_badgeOpacity, 0f, 1f);
            _ = ItemMotionMath.StepScalarSpring(ref _badgeOffsetY, ref _badgeOffsetYVel, wantsBadge ? 0f : 6f, dt, responseSeconds: 0.18, dampingFraction: 0.80);
            if (!wantsBadge && _badgeOpacity <= 0.01f)
            {
                _badgeOpacity = 0f;
                _displayedBadgeKind = FloatingDragBadgeKind.None;
                _displayedBadgeText = null;
            }

            (winLeft, winTop) = CrossWindowDragMath.ComputeGhostWindowOriginPx(
                cursorPx.X,
                cursorPx.Y,
                grabOffsetXPx,
                grabOffsetYPx,
                metrics.CardPadLeftPx,
                metrics.CardPadTopPx);

            _centerPosPx = new Vector3(
                winLeft + metrics.CardPadLeftPx + (float)(metrics.CardWidthDip * 0.5 * liveDpiScale),
                winTop + metrics.CardPadTopPx + (float)(metrics.CardHeightDip * 0.5 * liveDpiScale),
                0f);
            _centerVelPx = new Vector3(_smoothedVxPxPerSec, _smoothedVyPxPerSec, 0f);
        }
        else if (phase == GhostPhase.Landing)
        {
            double elapsed = Stopwatch.GetElapsedTime(_completionStartTick, nowTick).TotalSeconds;
            Vector3 targetCenter = completionTargetCenterPx is NativeMethods.POINT pt
                ? new Vector3(pt.X, pt.Y, 0f)
                : _centerPosPx;

            bool posSettled = ItemMotionMath.StepIosSpring(
                ref _centerPosPx,
                ref _centerVelPx,
                targetCenter,
                dt,
                responseSeconds: 0.14,
                dampingFraction: 0.90,
                positionToleranceSquared: 0.25f,
                velocityToleranceSquared: 4.0f);
            _ = ItemMotionMath.StepScalarSpring(ref _scale, ref _scaleVel, 0.96f, dt, responseSeconds: 0.14, dampingFraction: 0.90);
            _ = ItemMotionMath.StepScalarSpring(ref _tiltDeg, ref _tiltVel, 0f, dt, responseSeconds: 0.14, dampingFraction: 0.90);
            _ = ItemMotionMath.StepScalarSpring(ref _badgeOpacity, ref _badgeOpacityVel, 0f, dt, responseSeconds: 0.10, dampingFraction: 0.95);
            _badgeOpacity = Math.Clamp(_badgeOpacity, 0f, 1f);
            if (_badgeOpacity <= 0.01f)
            {
                _badgeOpacity = 0f;
                _displayedBadgeKind = FloatingDragBadgeKind.None;
                _displayedBadgeText = null;
            }

            double fadeT = Math.Clamp((elapsed - 0.030) / 0.155, 0.0, 1.0);
            double smoothStep = fadeT * fadeT * (3.0 - 2.0 * fadeT);
            _masterAlpha = (float)(1.0 - smoothStep);

            winLeft = (int)Math.Round(_centerPosPx.X - (metrics.PadLeftDip + metrics.CardWidthDip * 0.5) * liveDpiScale);
            winTop = (int)Math.Round(_centerPosPx.Y - (metrics.PadTopDip + metrics.CardHeightDip * 0.5) * liveDpiScale);

            if (elapsed >= 0.190 || (posSettled && _masterAlpha <= 0.01f))
                animationFinished = true;
        }
        else // Rebounding
        {
            double elapsed = Stopwatch.GetElapsedTime(_completionStartTick, nowTick).TotalSeconds;
            Vector3 targetCenter = completionTargetCenterPx is NativeMethods.POINT pt
                ? new Vector3(pt.X, pt.Y, 0f)
                : _centerPosPx;

            bool posSettled = ItemMotionMath.StepIosSpring(
                ref _centerPosPx,
                ref _centerVelPx,
                targetCenter,
                dt,
                responseSeconds: 0.19,
                dampingFraction: 0.80,
                positionToleranceSquared: 0.5f,
                velocityToleranceSquared: 6.0f);
            _ = ItemMotionMath.StepScalarSpring(ref _scale, ref _scaleVel, 1.0f, dt, responseSeconds: 0.19, dampingFraction: 0.80);
            _ = ItemMotionMath.StepScalarSpring(ref _tiltDeg, ref _tiltVel, 0f, dt, responseSeconds: 0.19, dampingFraction: 0.80);
            _ = ItemMotionMath.StepScalarSpring(ref _badgeOpacity, ref _badgeOpacityVel, 0f, dt, responseSeconds: 0.10, dampingFraction: 0.95);
            _badgeOpacity = Math.Clamp(_badgeOpacity, 0f, 1f);
            if (_badgeOpacity <= 0.01f)
            {
                _badgeOpacity = 0f;
                _displayedBadgeKind = FloatingDragBadgeKind.None;
                _displayedBadgeText = null;
            }

            double fadeT = Math.Clamp((elapsed - 0.145) / 0.095, 0.0, 1.0);
            double smoothStep = fadeT * fadeT * (3.0 - 2.0 * fadeT);
            _masterAlpha = (float)(1.0 - smoothStep);

            winLeft = (int)Math.Round(_centerPosPx.X - (metrics.PadLeftDip + metrics.CardWidthDip * 0.5) * liveDpiScale);
            winTop = (int)Math.Round(_centerPosPx.Y - (metrics.PadTopDip + metrics.CardHeightDip * 0.5) * liveDpiScale);

            if (elapsed >= 0.250 || (posSettled && _masterAlpha <= 0.01f))
                animationFinished = true;
        }

        if (animationFinished) return true;

        byte masterAlphaByte = (byte)Math.Clamp((int)Math.Round(_masterAlpha * 255f), 0, 255);
        bool iconsDirty = iconRev != _renderIconRevision;
        bool visualDirty =
            !_hasRenderedSurface ||
            iconsDirty ||
            Math.Abs(liveDpiScale - _lastRenderedDpiScale) > 0.005 ||
            Math.Abs(_scale - _lastRenderedScale) > 0.001f ||
            Math.Abs(_tiltDeg - _lastRenderedTiltDeg) > 0.04f ||
            Math.Abs(_badgeOpacity - _lastRenderedBadgeOpacity) > 0.005f ||
            Math.Abs(_badgeOffsetY - _lastRenderedBadgeOffsetY) > 0.05f ||
            _displayedBadgeKind != _lastRenderedBadgeKind ||
            !string.Equals(_displayedBadgeText, _lastRenderedBadgeText, StringComparison.Ordinal);

        if (!visualDirty)
        {
            bool alphaChanged = masterAlphaByte != _lastUploadedMasterAlpha;
            bool posChanged = winLeft != _lastWinLeft || winTop != _lastWinTop;
            if (!alphaChanged && !posChanged && _windowVisible)
                return false;

            var dstPos = new NativeMethods.POINT { X = winLeft, Y = winTop };
            bool moved;
            if (alphaChanged)
            {
                var blend = new NativeMethods.BLENDFUNCTION
                {
                    BlendOp = NativeMethods.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = masterAlphaByte,
                    AlphaFormat = NativeMethods.AC_SRC_ALPHA
                };
                moved = FloatingDragNativeMethods.UpdateLayeredWindowPositionAndAlpha(
                    _hwnd,
                    IntPtr.Zero,
                    ref dstPos,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    ref blend,
                    NativeMethods.ULW_ALPHA);
            }
            else
            {
                moved = FloatingDragNativeMethods.UpdateLayeredWindowPositionOnly(
                    _hwnd,
                    IntPtr.Zero,
                    ref dstPos,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    IntPtr.Zero,
                    0);
            }

            if (moved)
            {
                _lastWinLeft = winLeft;
                _lastWinTop = winTop;
                _lastUploadedMasterAlpha = masterAlphaByte;
                EnsureWindowShownTopmost();
                return false;
            }
            // Fall through to full upload if position-only UpdateLayeredWindow ever fails.
        }

        if (!EnsureDibSurface(metrics.SurfaceWidthPx, metrics.SurfaceHeightPx))
            return false;

        RenderToDibBuffer(descriptor, metrics);

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            var dstPos = new NativeMethods.POINT { X = winLeft, Y = winTop };
            var size = new NativeMethods.SIZE { Width = metrics.SurfaceWidthPx, Height = metrics.SurfaceHeightPx };
            var srcPos = new NativeMethods.POINT { X = 0, Y = 0 };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = masterAlphaByte,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };

            if (NativeMethods.UpdateLayeredWindow(
                    _hwnd,
                    screenDc,
                    ref dstPos,
                    ref size,
                    _memDc,
                    ref srcPos,
                    0,
                    ref blend,
                    NativeMethods.ULW_ALPHA))
            {
                _hasRenderedSurface = true;
                _renderIconRevision = iconRev;
                _lastRenderedDpiScale = liveDpiScale;
                _lastRenderedScale = _scale;
                _lastRenderedTiltDeg = _tiltDeg;
                _lastRenderedBadgeOpacity = _badgeOpacity;
                _lastRenderedBadgeOffsetY = _badgeOffsetY;
                _lastRenderedBadgeKind = _displayedBadgeKind;
                _lastRenderedBadgeText = _displayedBadgeText;
                _lastUploadedMasterAlpha = masterAlphaByte;
                _lastWinLeft = winLeft;
                _lastWinTop = winTop;
                EnsureWindowShownTopmost();
            }
        }
        finally
        {
            if (screenDc != IntPtr.Zero) _ = NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }

        return false;
    }

    private void RenderToDibBuffer(FloatingDragGhostDescriptor descriptor, SurfaceMetrics metrics)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (TryRenderWin2D(descriptor, metrics))
                    return;
                break;
            }
            catch (Exception ex) when (attempt == 0 && _canvasDevice is not null && _canvasDevice.IsDeviceLost(ex.HResult))
            {
                ResetWin2DResources();
            }
            catch
            {
                ResetWin2DResources();
                break;
            }
        }

        RenderSoftwareFallback(descriptor, metrics);
    }

    private bool TryRenderWin2D(FloatingDragGhostDescriptor descriptor, SurfaceMetrics metrics)
    {
        _canvasDevice ??= CanvasDevice.GetSharedDevice();
        if (_renderTarget is null ||
            _renderTargetWidth != metrics.SurfaceWidthPx ||
            _renderTargetHeight != metrics.SurfaceHeightPx)
        {
            _renderTarget?.Dispose();
            _renderTarget = new CanvasRenderTarget(
                _canvasDevice,
                metrics.SurfaceWidthPx,
                metrics.SurfaceHeightPx,
                96f,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                CanvasAlphaMode.Premultiplied);
            _renderTargetWidth = metrics.SurfaceWidthPx;
            _renderTargetHeight = metrics.SurfaceHeightPx;
        }

        IReadOnlyList<FloatingDragGhostItemDescriptor> items = descriptor.Items ?? Array.Empty<FloatingDragGhostItemDescriptor>();
        CanvasBitmap? primaryIcon = items.Count > 0 ? GetOrCreateGpuIconBitmap(_canvasDevice, items[0].FullPath) : null;
        CanvasBitmap? secondIcon = items.Count > 1 ? GetOrCreateGpuIconBitmap(_canvasDevice, items[1].FullPath) : null;
        CanvasBitmap? thirdIcon = items.Count > 2 ? GetOrCreateGpuIconBitmap(_canvasDevice, items[2].FullPath) : null;

        float dpi = (float)metrics.DpiScale;
        Matrix3x2 dpiMatrix = Matrix3x2.CreateScale(dpi);
        float cardLeft = (float)metrics.PadLeftDip;
        float cardTop = (float)metrics.PadTopDip;
        float cardW = (float)metrics.CardWidthDip;
        float cardH = (float)metrics.CardHeightDip;
        var cardRect = new Rect(cardLeft, cardTop, cardW, cardH);
        var cardCenter = new Vector2(cardLeft + cardW * 0.5f, cardTop + cardH * 0.5f);
        float cornerRadius = Math.Clamp(MathF.Min(cardW, cardH) * 0.19f, 14f, 18f);

        Matrix3x2 primaryCardTransform =
            Matrix3x2.CreateTranslation(-cardCenter) *
            Matrix3x2.CreateScale(_scale) *
            Matrix3x2.CreateRotation(_tiltDeg * (MathF.PI / 180f)) *
            Matrix3x2.CreateTranslation(cardCenter) *
            dpiMatrix;

        Color accentColor = UnpackAccentColor(descriptor.ThemeAccentArgb);
        bool dark = descriptor.IsDarkTheme;

        using (CanvasDrawingSession ds = _renderTarget.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(0, 0, 0, 0));
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;

            // 1. Two-layer Apple elevation shadow (wide ambient + crisp contact shadow)
            using (var shadowSource = new CanvasCommandList(_canvasDevice))
            {
                using (CanvasDrawingSession sds = shadowSource.CreateDrawingSession())
                {
                    sds.Antialiasing = CanvasAntialiasing.Antialiased;
                    if (items.Count >= 3)
                    {
                        sds.Transform = CreateDeckCardTransform(cardCenter, -5f, 8f, -5.5f, 0.93f, _scale, _tiltDeg, dpiMatrix);
                        sds.FillRoundedRectangle(cardRect, cornerRadius, cornerRadius, Color.FromArgb(200, 0, 0, 0));
                    }
                    if (items.Count >= 2)
                    {
                        sds.Transform = CreateDeckCardTransform(cardCenter, 6f, 5f, 6.0f, 0.96f, _scale, _tiltDeg, dpiMatrix);
                        sds.FillRoundedRectangle(cardRect, cornerRadius, cornerRadius, Color.FromArgb(220, 0, 0, 0));
                    }
                    sds.Transform = primaryCardTransform;
                    sds.FillRoundedRectangle(cardRect, cornerRadius, cornerRadius, Color.FromArgb(255, 0, 0, 0));
                }

                ds.Transform = Matrix3x2.Identity;
                using (var ambientShadow = new ShadowEffect
                {
                    Source = shadowSource,
                    BlurAmount = 14f * dpi,
                    ShadowColor = dark ? Color.FromArgb(132, 0, 0, 0) : Color.FromArgb(68, 14, 22, 38),
                    Optimization = EffectOptimization.Balanced
                })
                {
                    ds.DrawImage(ambientShadow, 0f, 10f * dpi);
                }

                using (var contactShadow = new ShadowEffect
                {
                    Source = shadowSource,
                    BlurAmount = 5f * dpi,
                    ShadowColor = dark ? Color.FromArgb(110, 0, 0, 0) : Color.FromArgb(50, 12, 18, 30),
                    Optimization = EffectOptimization.Balanced
                })
                {
                    ds.DrawImage(contactShadow, 0f, 3f * dpi);
                }
            }

            // 2. Multi-select fanned backing deck cards
            if (items.Count >= 3)
            {
                Matrix3x2 back2Transform = CreateDeckCardTransform(cardCenter, -5f, 8f, -5.5f, 0.93f, _scale, _tiltDeg, dpiMatrix);
                DrawBackingDeckCard(ds, cardRect, cornerRadius, back2Transform, thirdIcon, (float)metrics.IconSizeDip, dark, opacity: 0.72f);
            }
            if (items.Count >= 2)
            {
                Matrix3x2 back1Transform = CreateDeckCardTransform(cardCenter, 6f, 5f, 6.0f, 0.96f, _scale, _tiltDeg, dpiMatrix);
                DrawBackingDeckCard(ds, cardRect, cornerRadius, back1Transform, secondIcon, (float)metrics.IconSizeDip, dark, opacity: 0.86f);
            }

            // 3. Primary Liquid Glass Card
            ds.Transform = primaryCardTransform;
            DrawPrimaryLiquidGlassCard(
                ds,
                _canvasDevice,
                cardRect,
                cornerRadius,
                (float)metrics.IconSizeDip,
                primaryIcon,
                items.Count > 0 ? ResolveDisplayLabel(items[0]) : string.Empty,
                dark,
                accentColor);

            // 4. Glossy iOS multi-select counter pill (top-right of card)
            if (items.Count > 1)
            {
                DrawMultiSelectCountBadge(ds, _canvasDevice, cardRect, items.Count);
            }

            // 5. Adaptive Liquid Glass Action Pill (level horizontally below card)
            if (_badgeOpacity > 0.01f &&
                _displayedBadgeKind != FloatingDragBadgeKind.None &&
                !string.IsNullOrWhiteSpace(_displayedBadgeText))
            {
                ds.Transform = dpiMatrix;
                float pillTopDip = cardCenter.Y + cardH * 0.5f * _scale + 10f + _badgeOffsetY;
                DrawActionPill(
                    ds,
                    _canvasDevice,
                    cardCenter.X,
                    pillTopDip,
                    _displayedBadgeKind,
                    _displayedBadgeText!,
                    _badgeOpacity,
                    dark,
                    accentColor);
            }
        }

        byte[] pixelBytes = _renderTarget.GetPixelBytes();
        int expectedLength = checked(metrics.SurfaceWidthPx * metrics.SurfaceHeightPx * 4);
        if (pixelBytes.Length != expectedLength) return false;
        Marshal.Copy(pixelBytes, 0, _dibBits, expectedLength);
        return true;
    }

    private static Matrix3x2 CreateDeckCardTransform(
        Vector2 cardCenter,
        float offsetXDip,
        float offsetYDip,
        float extraTiltDeg,
        float relativeScale,
        float primaryScale,
        float primaryTiltDeg,
        Matrix3x2 dpiMatrix)
    {
        return Matrix3x2.CreateTranslation(-cardCenter) *
               Matrix3x2.CreateScale(primaryScale * relativeScale) *
               Matrix3x2.CreateRotation((primaryTiltDeg + extraTiltDeg) * (MathF.PI / 180f)) *
               Matrix3x2.CreateTranslation(cardCenter + new Vector2(offsetXDip, offsetYDip)) *
               dpiMatrix;
    }

    private static void DrawBackingDeckCard(
        CanvasDrawingSession ds,
        Rect cardRect,
        float cornerRadius,
        Matrix3x2 transform,
        CanvasBitmap? iconBitmap,
        float iconSizeDip,
        bool dark,
        float opacity)
    {
        ds.Transform = transform;
        using (ds.CreateLayer(opacity))
        {
            Color fillColor = dark
                ? Color.FromArgb(196, 30, 35, 46)
                : Color.FromArgb(204, 245, 248, 253);
            Color rimColor = dark
                ? Color.FromArgb(72, 255, 255, 255)
                : Color.FromArgb(180, 255, 255, 255);
            Color hairlineColor = dark
                ? Color.FromArgb(72, 0, 0, 0)
                : Color.FromArgb(30, 15, 23, 42);

            ds.FillRoundedRectangle(cardRect, cornerRadius, cornerRadius, fillColor);

            if (iconBitmap is not null)
            {
                float drawSize = MathF.Min(iconSizeDip * 0.90f, (float)Math.Min(cardRect.Width - 16.0, cardRect.Height - 24.0));
                float topPad = MathF.Max(8f, ((float)cardRect.Height - drawSize - 24f) * 0.42f);
                var iconDest = new Rect(
                    cardRect.X + (cardRect.Width - drawSize) * 0.5,
                    cardRect.Y + topPad,
                    drawSize,
                    drawSize);
                var iconSrc = new Rect(0, 0, iconBitmap.SizeInPixels.Width, iconBitmap.SizeInPixels.Height);
                ds.DrawImage(iconBitmap, iconDest, iconSrc, 0.72f, CanvasImageInterpolation.HighQualityCubic);
            }

            ds.DrawRoundedRectangle(cardRect, cornerRadius, cornerRadius, rimColor, 1.0f);
            ds.DrawRoundedRectangle(
                new Rect(cardRect.X - 0.5, cardRect.Y - 0.5, cardRect.Width + 1.0, cardRect.Height + 1.0),
                cornerRadius + 0.5f,
                cornerRadius + 0.5f,
                hairlineColor,
                0.75f);
        }
    }

    private static void DrawPrimaryLiquidGlassCard(
        CanvasDrawingSession ds,
        CanvasDevice device,
        Rect cardRect,
        float cornerRadius,
        float iconSizeDip,
        CanvasBitmap? primaryIcon,
        string displayLabel,
        bool dark,
        Color accentColor)
    {
        float x = (float)cardRect.X;
        float y = (float)cardRect.Y;
        float w = (float)cardRect.Width;
        float h = (float)cardRect.Height;

        // Milky light / smoked dark Liquid Glass gradient fill
        using (var fillBrush = new CanvasLinearGradientBrush(
            device,
            dark
                ? new[]
                {
                    new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(216, 46, 52, 66) },
                    new CanvasGradientStop { Position = 0.52f, Color = Color.FromArgb(204, 34, 38, 48) },
                    new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(196, 25, 29, 38) }
                }
                : new[]
                {
                    new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(224, 255, 255, 255) },
                    new CanvasGradientStop { Position = 0.55f, Color = Color.FromArgb(206, 246, 249, 253) },
                    new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(192, 234, 240, 248) }
                })
        {
            StartPoint = new Vector2(x, y),
            EndPoint = new Vector2(x, y + h)
        })
        {
            ds.FillRoundedRectangle(cardRect, cornerRadius, cornerRadius, fillBrush);
        }

        // Delicate theme-accent top atmospheric tint + specular inner sheen clipped to card squircle
        using (CanvasGeometry clipGeometry = CanvasGeometry.CreateRoundedRectangle(device, cardRect, cornerRadius, cornerRadius))
        using (ds.CreateLayer(1f, clipGeometry))
        {
            using (var accentWash = new CanvasLinearGradientBrush(
                device,
                new[]
                {
                    new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(dark ? (byte)34 : (byte)22, accentColor.R, accentColor.G, accentColor.B) },
                    new CanvasGradientStop { Position = 0.65f, Color = Color.FromArgb(0, accentColor.R, accentColor.G, accentColor.B) }
                })
            {
                StartPoint = new Vector2(x + w * 0.5f, y),
                EndPoint = new Vector2(x + w * 0.5f, y + h * 0.65f)
            })
            {
                ds.FillRectangle(cardRect, accentWash);
            }

            using (var topSheen = new CanvasLinearGradientBrush(
                device,
                new[]
                {
                    new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(dark ? (byte)42 : (byte)92, 255, 255, 255) },
                    new CanvasGradientStop { Position = 0.42f, Color = Color.FromArgb(dark ? (byte)8 : (byte)16, 255, 255, 255) },
                    new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(0, 255, 255, 255) }
                })
            {
                StartPoint = new Vector2(x, y),
                EndPoint = new Vector2(x, y + h * 0.48f)
            })
            {
                ds.FillRectangle(new Rect(x, y, w, h * 0.48), topSheen);
            }
        }

        // Crisp supersampled icon
        float clampedIconSize = Math.Clamp(iconSizeDip, 28f, MathF.Max(28f, MathF.Min(w - 14f, h - 28f)));
        float topPadding = MathF.Max(8f, (h - clampedIconSize - 28f) * 0.42f);
        var iconDest = new Rect(x + (w - clampedIconSize) * 0.5, y + topPadding, clampedIconSize, clampedIconSize);

        if (primaryIcon is not null)
        {
            var iconSrc = new Rect(0, 0, primaryIcon.SizeInPixels.Width, primaryIcon.SizeInPixels.Height);
            ds.DrawImage(primaryIcon, iconDest, iconSrc, 1.0f, CanvasImageInterpolation.HighQualityCubic);
        }
        else
        {
            DrawPlaceholderIconGlyph(ds, iconDest, dark, accentColor);
        }

        // Clean 1-2 line centered display caption (never a raw file path)
        if (!string.IsNullOrWhiteSpace(displayLabel))
        {
            float captionTop = (float)iconDest.Bottom + 4.5f;
            float captionHeight = MathF.Max(16f, (y + h - 4f) - captionTop);
            float captionWidth = MathF.Max(20f, w - 12f);
            float fontSize = Math.Clamp(w * 0.122f, 10.5f, 12.2f);

            using var textFormat = new CanvasTextFormat
            {
                FontFamily = AppStrings.FontFamily,
                FontSize = fontSize,
                FontWeight = new FontWeight { Weight = 500 },
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Top,
                WordWrapping = CanvasWordWrapping.Wrap,
                TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
                TrimmingSign = CanvasTrimmingSign.Ellipsis
            };
            using var layout = new CanvasTextLayout(device, displayLabel, textFormat, captionWidth, captionHeight);

            float textX = x + 6f;
            Color shadowColor = dark ? Color.FromArgb(115, 0, 0, 0) : Color.FromArgb(110, 255, 255, 255);
            Color fgColor = dark ? Color.FromArgb(244, 255, 255, 255) : Color.FromArgb(236, 18, 22, 30);

            ds.DrawTextLayout(layout, textX, captionTop + 0.75f, shadowColor);
            ds.DrawTextLayout(layout, textX, captionTop, fgColor);
        }

        // Luminous 1px top-to-bottom specular rim + subtle contrast hairline
        using (var rimBrush = new CanvasLinearGradientBrush(
            device,
            dark
                ? new[]
                {
                    new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(148, 255, 255, 255) },
                    new CanvasGradientStop { Position = 0.45f, Color = Color.FromArgb(56, 255, 255, 255) },
                    new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(26, 255, 255, 255) }
                }
                : new[]
                {
                    new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(238, 255, 255, 255) },
                    new CanvasGradientStop { Position = 0.50f, Color = Color.FromArgb(118, 255, 255, 255) },
                    new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(54, 148, 163, 184) }
                })
        {
            StartPoint = new Vector2(x, y),
            EndPoint = new Vector2(x, y + h)
        })
        {
            ds.DrawRoundedRectangle(
                new Rect(x + 0.5, y + 0.5, w - 1.0, h - 1.0),
                cornerRadius - 0.5f,
                cornerRadius - 0.5f,
                rimBrush,
                1.0f);
        }

        ds.DrawRoundedRectangle(
            cardRect,
            cornerRadius,
            cornerRadius,
            dark ? Color.FromArgb(84, 0, 0, 0) : Color.FromArgb(32, 15, 23, 42),
            0.75f);
    }

    private static void DrawPlaceholderIconGlyph(CanvasDrawingSession ds, Rect iconRect, bool dark, Color accent)
    {
        float radius = (float)Math.Clamp(iconRect.Width * 0.24, 6.0, 14.0);
        Color fill = dark
            ? Color.FromArgb(90, accent.R, accent.G, accent.B)
            : Color.FromArgb(54, accent.R, accent.G, accent.B);
        Color stroke = dark
            ? Color.FromArgb(140, 255, 255, 255)
            : Color.FromArgb(110, accent.R, accent.G, accent.B);
        ds.FillRoundedRectangle(iconRect, radius, radius, fill);
        ds.DrawRoundedRectangle(iconRect, radius, radius, stroke, 1.0f);
    }

    private static void DrawMultiSelectCountBadge(CanvasDrawingSession ds, CanvasDevice device, Rect cardRect, int count)
    {
        string text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
        using var format = new CanvasTextFormat
        {
            FontFamily = AppStrings.FontFamily,
            FontSize = 11.5f,
            FontWeight = new FontWeight { Weight = 700 },
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap
        };
        using var layout = new CanvasTextLayout(device, text, format, 64f, 22f);

        float pillH = 22f;
        float pillW = MathF.Max(22f, (float)layout.LayoutBounds.Width + 13f);
        float centerX = (float)cardRect.Right - 3f;
        float centerY = (float)cardRect.Top + 3f;
        var pillRect = new Rect(centerX - pillW * 0.5f, centerY - pillH * 0.5f, pillW, pillH);
        float radius = pillH * 0.5f;

        // Soft badge elevation shadow
        ds.FillRoundedRectangle(
            new Rect(pillRect.X, pillRect.Y + 2.0, pillRect.Width, pillRect.Height),
            radius,
            radius,
            Color.FromArgb(78, 0, 0, 0));

        // Glossy Apple Blue pill gradient
        using (var pillFill = new CanvasLinearGradientBrush(
            device,
            new[]
            {
                new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(255, 34, 146, 255) },
                new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(255, 0, 114, 245) }
            })
        {
            StartPoint = new Vector2((float)pillRect.X, (float)pillRect.Y),
            EndPoint = new Vector2((float)pillRect.X, (float)pillRect.Bottom)
        })
        {
            ds.FillRoundedRectangle(pillRect, radius, radius, pillFill);
        }

        // Crisp 1.25px white rim
        ds.DrawRoundedRectangle(pillRect, radius, radius, Color.FromArgb(245, 255, 255, 255), 1.25f);

        using var centeredLayout = new CanvasTextLayout(device, text, format, pillW, pillH);
        ds.DrawTextLayout(centeredLayout, (float)pillRect.X, (float)pillRect.Y - 0.5f, Color.FromArgb(255, 255, 255, 255));
    }

    private static void DrawActionPill(
        CanvasDrawingSession ds,
        CanvasDevice device,
        float centerX,
        float topY,
        FloatingDragBadgeKind kind,
        string text,
        float opacity,
        bool dark,
        Color themeAccent)
    {
        const float pillHeight = 26f;
        const float cornerRadius = 13f;
        const float iconDiameter = 16f;
        const float leftPad = 7f;
        const float iconTextGap = 6f;
        const float rightPad = 11f;
        const float maxTextWidth = 196f;

        using var textFormat = new CanvasTextFormat
        {
            FontFamily = AppStrings.FontFamily,
            FontSize = 12.0f,
            FontWeight = new FontWeight { Weight = 600 },
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis
        };
        using var textLayout = new CanvasTextLayout(device, text, textFormat, maxTextWidth, pillHeight);
        float measuredTextWidth = MathF.Min(maxTextWidth, (float)Math.Ceiling(textLayout.LayoutBounds.Width) + 2f);
        float pillWidth = Math.Clamp(leftPad + iconDiameter + iconTextGap + measuredTextWidth + rightPad, 56f, 236f);

        var pillRect = new Rect(centerX - pillWidth * 0.5f, topY, pillWidth, pillHeight);

        using (ds.CreateLayer(Math.Clamp(opacity, 0f, 1f)))
        {
            // Soft pill elevation shadow
            ds.FillRoundedRectangle(
                new Rect(pillRect.X, pillRect.Y + 3.0, pillRect.Width, pillRect.Height),
                cornerRadius,
                cornerRadius,
                Color.FromArgb(dark ? (byte)112 : (byte)56, 0, 0, 0));

            // Frosted Liquid Glass capsule fill
            using (var pillFill = new CanvasLinearGradientBrush(
                device,
                dark
                    ? new[]
                    {
                        new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(232, 42, 47, 60) },
                        new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(222, 26, 30, 40) }
                    }
                    : new[]
                    {
                        new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(238, 255, 255, 255) },
                        new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(226, 240, 245, 252) }
                    })
            {
                StartPoint = new Vector2((float)pillRect.X, (float)pillRect.Y),
                EndPoint = new Vector2((float)pillRect.X, (float)pillRect.Bottom)
            })
            {
                ds.FillRoundedRectangle(pillRect, cornerRadius, cornerRadius, pillFill);
            }

            // Specular capsule rim
            using (var rim = new CanvasLinearGradientBrush(
                device,
                dark
                    ? new[]
                    {
                        new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(132, 255, 255, 255) },
                        new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(30, 255, 255, 255) }
                    }
                    : new[]
                    {
                        new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(235, 255, 255, 255) },
                        new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(74, 148, 163, 184) }
                    })
            {
                StartPoint = new Vector2((float)pillRect.X, (float)pillRect.Y),
                EndPoint = new Vector2((float)pillRect.X, (float)pillRect.Bottom)
            })
            {
                ds.DrawRoundedRectangle(
                    new Rect(pillRect.X + 0.5, pillRect.Y + 0.5, pillRect.Width - 1.0, pillRect.Height - 1.0),
                    cornerRadius - 0.5f,
                    cornerRadius - 0.5f,
                    rim,
                    1.0f);
            }

            // Accent glyph circle + crisp vector symbol
            float circleCx = (float)pillRect.X + leftPad + iconDiameter * 0.5f;
            float circleCy = (float)pillRect.Y + pillHeight * 0.5f;
            Color badgeColor = ResolveBadgeAccentColor(kind, themeAccent);
            ds.FillCircle(circleCx, circleCy, iconDiameter * 0.5f, badgeColor);
            DrawActionPillGlyph(ds, kind, circleCx, circleCy);

            // Crisp pill label
            float textX = (float)pillRect.X + leftPad + iconDiameter + iconTextGap;
            Color labelColor = dark ? Color.FromArgb(245, 255, 255, 255) : Color.FromArgb(238, 18, 24, 34);
            ds.DrawTextLayout(textLayout, textX, (float)pillRect.Y - 0.5f, labelColor);
        }
    }

    private static Color ResolveBadgeAccentColor(FloatingDragBadgeKind kind, Color themeAccent) => kind switch
    {
        FloatingDragBadgeKind.MoveToDesktop => Color.FromArgb(255, 0, 122, 255),     // Luminous Apple Blue
        FloatingDragBadgeKind.CopyToDesktop => Color.FromArgb(255, 52, 199, 89),     // Vibrant Apple Green
        FloatingDragBadgeKind.CopyToOrganizer => Color.FromArgb(255, 52, 199, 89),   // Vibrant Apple Green
        FloatingDragBadgeKind.MoveToOrganizer => EnsureVibrantBadgeColor(themeAccent),
        FloatingDragBadgeKind.ExternalApp => Color.FromArgb(255, 10, 132, 255),
        _ => Color.FromArgb(255, 0, 122, 255)
    };

    private static Color EnsureVibrantBadgeColor(Color accent)
    {
        double luma = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
        if (luma < 0.16 || luma > 0.86)
            return Color.FromArgb(255, 88, 86, 214); // Apple Indigo fallback for near-black/near-white theme tints
        return Color.FromArgb(255, accent.R, accent.G, accent.B);
    }

    private static void DrawActionPillGlyph(CanvasDrawingSession ds, FloatingDragBadgeKind kind, float cx, float cy)
    {
        Color white = Color.FromArgb(255, 255, 255, 255);
        using var stroke = new CanvasStrokeStyle
        {
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
            LineJoin = CanvasLineJoin.Round
        };

        switch (kind)
        {
            case FloatingDragBadgeKind.MoveToDesktop:
                // Crisp down-arrow
                ds.DrawLine(cx, cy - 3.6f, cx, cy + 3.4f, white, 1.65f, stroke);
                ds.DrawLine(cx - 2.9f, cy + 0.6f, cx, cy + 3.5f, white, 1.65f, stroke);
                ds.DrawLine(cx + 2.9f, cy + 0.6f, cx, cy + 3.5f, white, 1.65f, stroke);
                break;

            case FloatingDragBadgeKind.CopyToDesktop:
            case FloatingDragBadgeKind.CopyToOrganizer:
                // Crisp plus symbol
                ds.DrawLine(cx - 3.4f, cy, cx + 3.4f, cy, white, 1.75f, stroke);
                ds.DrawLine(cx, cy - 3.4f, cx, cy + 3.4f, white, 1.75f, stroke);
                break;

            case FloatingDragBadgeKind.MoveToOrganizer:
                // Crisp tray/folder-in chevron
                ds.DrawRoundedRectangle(cx - 3.8f, cy - 2.6f, 7.6f, 5.4f, 1.4f, 1.4f, white, 1.4f);
                ds.DrawLine(cx - 3.4f, cy - 0.6f, cx + 3.4f, cy - 0.6f, white, 1.1f, stroke);
                break;

            default:
                // External app northeast arrow
                ds.DrawLine(cx - 2.8f, cy + 2.8f, cx + 2.8f, cy - 2.8f, white, 1.6f, stroke);
                ds.DrawLine(cx - 0.6f, cy - 2.8f, cx + 2.8f, cy - 2.8f, white, 1.6f, stroke);
                ds.DrawLine(cx + 2.8f, cy - 2.8f, cx + 2.8f, cy + 0.6f, white, 1.6f, stroke);
                break;
        }
    }

    private void RenderSoftwareFallback(FloatingDragGhostDescriptor descriptor, SurfaceMetrics metrics)
    {
        int width = metrics.SurfaceWidthPx;
        int height = metrics.SurfaceHeightPx;
        int byteCount = checked(width * height * 4);
        if (_softwarePixelBuffer is null || _softwarePixelBuffer.Length != byteCount)
            _softwarePixelBuffer = new byte[byteCount];
        else
            Array.Clear(_softwarePixelBuffer, 0, byteCount);

        double dpi = metrics.DpiScale;
        int cardW = Math.Max(16, (int)Math.Round(metrics.CardWidthDip * dpi * _scale));
        int cardH = Math.Max(16, (int)Math.Round(metrics.CardHeightDip * dpi * _scale));
        int cardLeft = Math.Clamp((width - cardW) / 2, 0, Math.Max(0, width - cardW));
        int cardTop = Math.Clamp((int)Math.Round((metrics.PadTopDip + metrics.CardHeightDip * 0.5) * dpi) - cardH / 2, 0, Math.Max(0, height - cardH));
        float radius = MathF.Max(8f, MathF.Min(cardW, cardH) * 0.18f);

        bool dark = descriptor.IsDarkTheme;
        byte bgR = dark ? (byte)34 : (byte)248;
        byte bgG = dark ? (byte)38 : (byte)250;
        byte bgB = dark ? (byte)48 : (byte)253;
        byte bgA = dark ? (byte)204 : (byte)214;

        // Soft shadow + rounded glass card in premultiplied BGRA32
        DrawSoftwareRoundedRect(_softwarePixelBuffer, width, height, cardLeft, cardTop + (int)Math.Round(4 * dpi), cardW, cardH, radius, 0, 0, 0, 68);
        DrawSoftwareRoundedRect(_softwarePixelBuffer, width, height, cardLeft, cardTop, cardW, cardH, radius, bgR, bgG, bgB, bgA);

        IReadOnlyList<FloatingDragGhostItemDescriptor> items = descriptor.Items ?? Array.Empty<FloatingDragGhostItemDescriptor>();
        if (items.Count > 0)
        {
            IconCacheService.IconSnapshot? icon = TryGetCachedIcon(items[0].FullPath);
            if (icon is not null && icon.Width > 0 && icon.Height > 0)
            {
                int iconSizePx = Math.Clamp((int)Math.Round(metrics.IconSizeDip * dpi * _scale), 16, Math.Max(16, Math.Min(cardW - 8, cardH - 8)));
                int iconLeft = cardLeft + (cardW - iconSizePx) / 2;
                int iconTop = cardTop + (cardH - iconSizePx) / 2;
                BlitSoftwarePremulIcon(_softwarePixelBuffer, width, height, icon, iconLeft, iconTop, iconSizePx);
            }
        }

        Marshal.Copy(_softwarePixelBuffer, 0, _dibBits, byteCount);
    }

    private static void DrawSoftwareRoundedRect(
        byte[] buffer,
        int surfaceW,
        int surfaceH,
        int rectX,
        int rectY,
        int rectW,
        int rectH,
        float radius,
        byte r,
        byte g,
        byte b,
        byte a)
    {
        int x0 = Math.Max(0, rectX);
        int y0 = Math.Max(0, rectY);
        int x1 = Math.Min(surfaceW, rectX + rectW);
        int y1 = Math.Min(surfaceH, rectY + rectH);
        float halfW = rectW * 0.5f;
        float halfH = rectH * 0.5f;
        float cx = rectX + halfW;
        float cy = rectY + halfH;
        float innerW = MathF.Max(0f, halfW - radius);
        float innerH = MathF.Max(0f, halfH - radius);

        for (int py = y0; py < y1; py++)
        {
            float dy = MathF.Max(0f, MathF.Abs((py + 0.5f) - cy) - innerH);
            int rowOffset = py * surfaceW * 4;
            for (int px = x0; px < x1; px++)
            {
                float dx = MathF.Max(0f, MathF.Abs((px + 0.5f) - cx) - innerW);
                float dist = MathF.Sqrt(dx * dx + dy * dy) - radius;
                if (dist >= 0.5f) continue;

                float coverage = dist <= -0.5f ? 1f : 0.5f - dist;
                int srcA = (int)Math.Round(a * coverage);
                if (srcA <= 0) continue;

                int srcB = (b * srcA + 127) / 255;
                int srcG = (g * srcA + 127) / 255;
                int srcR = (r * srcA + 127) / 255;
                BlendPremulPixel(buffer, rowOffset + px * 4, srcB, srcG, srcR, srcA);
            }
        }
    }

    private static void BlitSoftwarePremulIcon(
        byte[] dst,
        int dstW,
        int dstH,
        IconCacheService.IconSnapshot src,
        int destX,
        int destY,
        int destSize)
    {
        int x0 = Math.Max(0, destX);
        int y0 = Math.Max(0, destY);
        int x1 = Math.Min(dstW, destX + destSize);
        int y1 = Math.Min(dstH, destY + destSize);
        if (x1 <= x0 || y1 <= y0) return;

        float scaleX = (float)src.Width / destSize;
        float scaleY = (float)src.Height / destSize;

        for (int py = y0; py < y1; py++)
        {
            int sy = Math.Clamp((int)((py - destY + 0.5f) * scaleY), 0, src.Height - 1);
            int dstRow = py * dstW * 4;
            int srcRow = sy * src.Width * 4;
            for (int px = x0; px < x1; px++)
            {
                int sx = Math.Clamp((int)((px - destX + 0.5f) * scaleX), 0, src.Width - 1);
                int srcIdx = srcRow + sx * 4;
                int srcA = src.Pixels[srcIdx + 3];
                if (srcA == 0) continue;
                BlendPremulPixel(dst, dstRow + px * 4, src.Pixels[srcIdx], src.Pixels[srcIdx + 1], src.Pixels[srcIdx + 2], srcA);
            }
        }
    }

    private static void BlendPremulPixel(byte[] buffer, int offset, int srcB, int srcG, int srcR, int srcA)
    {
        int invA = 255 - srcA;
        buffer[offset] = (byte)Math.Min(255, srcB + (buffer[offset] * invA + 127) / 255);
        buffer[offset + 1] = (byte)Math.Min(255, srcG + (buffer[offset + 1] * invA + 127) / 255);
        buffer[offset + 2] = (byte)Math.Min(255, srcR + (buffer[offset + 2] * invA + 127) / 255);
        buffer[offset + 3] = (byte)Math.Min(255, srcA + (buffer[offset + 3] * invA + 127) / 255);
    }

    private CanvasBitmap? GetOrCreateGpuIconBitmap(CanvasDevice device, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        IconCacheService.IconSnapshot? snapshot = TryGetCachedIcon(fullPath);
        if (snapshot is null || snapshot.Width <= 0 || snapshot.Height <= 0) return null;

        string key = NormalizeCacheKey(fullPath);
        if (_gpuIconBitmaps.TryGetValue(key, out (IconCacheService.IconSnapshot Source, CanvasBitmap Bitmap) existing))
        {
            if (ReferenceEquals(existing.Source, snapshot))
                return existing.Bitmap;
            existing.Bitmap.Dispose();
            _gpuIconBitmaps.Remove(key);
        }

        if (_gpuIconBitmaps.Count >= 32)
        {
            foreach ((_, CanvasBitmap oldBitmap) in _gpuIconBitmaps.Values)
                oldBitmap.Dispose();
            _gpuIconBitmaps.Clear();
        }

        CanvasBitmap bitmap = CanvasBitmap.CreateFromBytes(
            device,
            snapshot.Pixels,
            snapshot.Width,
            snapshot.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            96f,
            CanvasAlphaMode.Premultiplied);
        _gpuIconBitmaps[key] = (snapshot, bitmap);
        return bitmap;
    }

    private static SurfaceMetrics ComputeSurfaceMetrics(FloatingDragGhostDescriptor descriptor, double dpiScale)
    {
        double cardW = double.IsFinite(descriptor.CardWidthDip) ? Math.Clamp(descriptor.CardWidthDip, 52.0, 260.0) : 96.0;
        double cardH = double.IsFinite(descriptor.CardHeightDip) ? Math.Clamp(descriptor.CardHeightDip, 56.0, 280.0) : 108.0;
        double iconDip = double.IsFinite(descriptor.IconSizeDip) ? Math.Clamp(descriptor.IconSizeDip, 28.0, Math.Min(cardW - 14.0, cardH - 26.0)) : 52.0;

        double padTopDip = 48.0;
        double padBottomDip = 88.0;
        double baseSidePadDip = Math.Max(56.0, Math.Ceiling(cardH * 0.14 + 42.0));
        double minSurfaceWidthDip = Math.Max(cardW + baseSidePadDip * 2.0, 284.0);
        double padLeftDip = Math.Ceiling((minSurfaceWidthDip - cardW) * 0.5);
        double surfaceWidthDip = cardW + padLeftDip * 2.0;
        double surfaceHeightDip = cardH + padTopDip + padBottomDip;

        int cardPadLeftPx = (int)Math.Round(padLeftDip * dpiScale);
        int cardPadTopPx = (int)Math.Round(padTopDip * dpiScale);
        int surfaceWidthPx = Math.Max(32, (int)Math.Round(surfaceWidthDip * dpiScale));
        int surfaceHeightPx = Math.Max(32, (int)Math.Round(surfaceHeightDip * dpiScale));

        return new SurfaceMetrics(
            cardW,
            cardH,
            iconDip,
            padLeftDip,
            padTopDip,
            surfaceWidthDip,
            surfaceHeightDip,
            cardPadLeftPx,
            cardPadTopPx,
            surfaceWidthPx,
            surfaceHeightPx,
            dpiScale);
    }

    private static double DetectMonitorDpiScale(NativeMethods.POINT screenPoint, double fallbackScale)
    {
        IntPtr monitor = NativeMethods.MonitorFromPoint(screenPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero &&
            NativeMethods.GetDpiForMonitor(monitor, FloatingDragNativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 &&
            dpiX > 0)
        {
            return Math.Clamp(dpiX / 96.0, 1.0, 4.0);
        }
        return fallbackScale;
    }

    private bool EnsureNativeWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            if (NativeMethods.IsWindow(_hwnd)) return true;
            _hwnd = IntPtr.Zero;
            _windowVisible = false;
        }

        if (!EnsureWindowClassRegistered()) return false;

        long exStyle =
            NativeMethods.WS_EX_LAYERED |
            NativeMethods.WS_EX_TRANSPARENT |
            NativeMethods.WS_EX_TOOLWINDOW |
            FloatingDragNativeMethods.WS_EX_TOPMOST |
            NativeMethods.WS_EX_NOACTIVATE;

        IntPtr hwnd = NativeMethods.CreateWindowEx(
            exStyle,
            WindowClassName,
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

        if (hwnd == IntPtr.Zero) return false;

        _ = NativeMethods.DisableWindowTransitions(hwnd);
        int excludePeek = 1;
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd,
            FloatingDragNativeMethods.DWMWA_EXCLUDED_FROM_PEEK,
            ref excludePeek,
            sizeof(int));

        _hwnd = hwnd;
        _windowVisible = false;
        return true;
    }

    private void EnsureWindowShownTopmost()
    {
        if (_hwnd == IntPtr.Zero || _windowVisible) return;
        _ = FloatingDragNativeMethods.ShowWindow(_hwnd, FloatingDragNativeMethods.SW_SHOWNOACTIVATE);
        _ = NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_SHOWWINDOW |
            NativeMethods.SWP_NOOWNERZORDER);
        _windowVisible = true;
    }

    private void HideNativeWindow()
    {
        if (_hwnd != IntPtr.Zero && _windowVisible)
        {
            _ = FloatingDragNativeMethods.ShowWindow(_hwnd, FloatingDragNativeMethods.SW_HIDE);
            _windowVisible = false;
        }
        _hasRenderedSurface = false;
        _lastWinLeft = int.MinValue;
        _lastWinTop = int.MinValue;
    }

    private bool EnsureDibSurface(int widthPx, int heightPx)
    {
        if (_memDc != IntPtr.Zero &&
            _dibBitmap != IntPtr.Zero &&
            _dibBits != IntPtr.Zero &&
            _dibWidth == widthPx &&
            _dibHeight == heightPx)
        {
            return true;
        }

        ReleaseDibSurface();

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return false;

            var bitmapInfo = new NativeMethods.BITMAPINFO
            {
                Header = new NativeMethods.BITMAPINFOHEADER
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    Width = widthPx,
                    Height = -heightPx, // Top-down 32bpp premultiplied BGRA DIB
                    Planes = 1,
                    BitCount = 32,
                    Compression = NativeMethods.BI_RGB,
                    SizeImage = (uint)checked(widthPx * heightPx * 4)
                }
            };

            IntPtr dib = NativeMethods.CreateDIBSection(
                screenDc,
                ref bitmapInfo,
                NativeMethods.DIB_RGB_COLORS,
                out IntPtr bits,
                IntPtr.Zero,
                0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                if (dib != IntPtr.Zero) _ = NativeMethods.DeleteObject(dib);
                _ = NativeMethods.DeleteDC(memDc);
                return false;
            }

            IntPtr oldBitmap = NativeMethods.SelectObject(memDc, dib);
            if (oldBitmap == IntPtr.Zero)
            {
                _ = NativeMethods.DeleteObject(dib);
                _ = NativeMethods.DeleteDC(memDc);
                return false;
            }

            _memDc = memDc;
            _dibBitmap = dib;
            _oldDibBitmap = oldBitmap;
            _dibBits = bits;
            _dibWidth = widthPx;
            _dibHeight = heightPx;
            return true;
        }
        finally
        {
            if (screenDc != IntPtr.Zero) _ = NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void ReleaseDibSurface()
    {
        if (_oldDibBitmap != IntPtr.Zero && _memDc != IntPtr.Zero)
            _ = NativeMethods.SelectObject(_memDc, _oldDibBitmap);
        if (_dibBitmap != IntPtr.Zero)
            _ = NativeMethods.DeleteObject(_dibBitmap);
        if (_memDc != IntPtr.Zero)
            _ = NativeMethods.DeleteDC(_memDc);

        _oldDibBitmap = IntPtr.Zero;
        _dibBitmap = IntPtr.Zero;
        _memDc = IntPtr.Zero;
        _dibBits = IntPtr.Zero;
        _dibWidth = 0;
        _dibHeight = 0;
    }

    private void ResetWin2DResources()
    {
        foreach ((_, CanvasBitmap bitmap) in _gpuIconBitmaps.Values)
        {
            try { bitmap.Dispose(); }
            catch { }
        }
        _gpuIconBitmaps.Clear();

        if (_renderTarget is not null)
        {
            try { _renderTarget.Dispose(); }
            catch { }
            _renderTarget = null;
        }
        _renderTargetWidth = 0;
        _renderTargetHeight = 0;
        // Do NOT dispose CanvasDevice.GetSharedDevice(); only drop our local reference.
        _canvasDevice = null;
    }

    private void CleanupRenderThreadResources()
    {
        HideNativeWindow();
        ResetWin2DResources();
        ReleaseDibSurface();
        _softwarePixelBuffer = null;

        IntPtr hwnd = _hwnd;
        _hwnd = IntPtr.Zero;
        _windowVisible = false;
        if (hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd))
        {
            _ = NativeMethods.DestroyWindow(hwnd);
        }
    }

    private static bool EnsureWindowClassRegistered()
    {
        lock (ClassRegistrationGate)
        {
            if (_windowClassRegistered) return true;

            var windowClass = new NativeMethods.WNDCLASSEX
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                WindowProc = Marshal.GetFunctionPointerForDelegate(RootedWindowProc),
                Instance = NativeMethods.GetModuleHandle(null),
                ClassName = WindowClassName
            };
            ushort atom = NativeMethods.RegisterClassEx(ref windowClass);
            if (atom != 0 || Marshal.GetLastWin32Error() == 1410) // ERROR_CLASS_ALREADY_EXISTS
            {
                _windowClassRegistered = true;
                return true;
            }
            return false;
        }
    }

    private static IntPtr GhostWindowProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == NativeMethods.WM_NCHITTEST)
            return new IntPtr(NativeMethods.HTTRANSPARENT);
        if (message == NativeMethods.WM_MOUSEACTIVATE)
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        if (message == NativeMethods.WM_ERASEBKGND)
            return new IntPtr(1);
        return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
    }

    private static Color UnpackAccentColor(uint argb)
    {
        byte a = (byte)((argb >> 24) & 0xFF);
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);
        return a == 0 ? Color.FromArgb(255, 0, 122, 255) : Color.FromArgb(255, r, g, b);
    }

    private static string ResolveDisplayLabel(FloatingDragGhostItemDescriptor item)
    {
        string raw = !string.IsNullOrWhiteSpace(item.DisplayName) ? item.DisplayName.Trim() : item.FullPath;
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // Never leak raw filesystem paths onto the floating card caption.
        if (raw.Contains('\\') || raw.Contains('/'))
        {
            string trimmed = raw.TrimEnd('\\', '/');
            string fileName = Path.GetFileName(trimmed);
            raw = string.IsNullOrWhiteSpace(fileName) ? trimmed : fileName;
        }

        if (raw.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
            raw.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            string stripped = raw[..^4].Trim();
            if (stripped.Length > 0) raw = stripped;
        }

        return raw;
    }

    private static string NormalizeCacheKey(string path) => path.Trim().ToUpperInvariant();
}
