using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    private const int VkMenu = 0x12;
    private const uint WmMouseLeave = 0x02A3;
    private const uint TmeLeave = 0x00000002;
    private bool _canvasResizeRenderingHooked;
    private bool _canvasResizeGpuScaling;
    private bool _canvasResizeCenterSymmetric;
    private bool _hasPendingCanvasResizeFinalMetrics;
    private double _pendingCanvasResizeContentScale = 1;
    private double _pendingCanvasResizePanelWidthDip;
    private double _pendingCanvasResizePanelHeightDip;
    private IntPtr _trackedCanvasResizeEdgeWindow;

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr HwndTrack;
        public uint HoverTime;
    }

    [DllImport("user32.dll", EntryPoint = "TrackMouseEvent", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TrackMouseEventInfo eventTrack);

    private bool TryBeginCanvasResize(CanvasResizeEdge requestedEdge = CanvasResizeEdge.None)
    {
        if (_organizerTitleEdit.IsBusy || _closing || _widgetGesture.IsCompleting || _resizeGesture.IsCompleting) return false;
        if (IsDock || _definition.PlacementMode == OrganizerPlacementMode.Station || !_expanded || _animating ||
            _canvasResize is not null || _shellDragActive || _itemReorderSession is not null ||
            !NativeMethods.GetCursorPos(out NativeMethods.POINT cursor) ||
            !TryGetLogicalWindowRect(out NativeMethods.RECT bounds)) return false;
        CanvasResizeEdge edge = requestedEdge == CanvasResizeEdge.None
            ? GetCanvasResizeEdge(cursor, bounds)
            : requestedEdge;
        if (edge == CanvasResizeEdge.None) return false;

        DisplayInfo display = DisplayPlacementService.ForBounds(bounds) with
        {
            Scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d)
        };
        bool compactList = IsCompactList;
        double startScale = 1;
        int titleHeightPx = GetExpandedTitleBandPx(display.Scale);
        int panelHeightPx = Math.Max(1, bounds.Height - titleHeightPx);
        WindowAlignmentInsets? alignmentInsets = OrganizerInteractionMath.ShouldUseWindowAlignment(
                _host.State.GlobalSettings.WindowAlignmentEnabled, true, _definition.PlacementMode, false, IsPermanentlyExpanded) &&
            TryGetWindowAlignmentFrame(out NativeMethods.RECT alignmentFrame)
                ? WindowAlignmentInsets.From(bounds, alignmentFrame) : null;
        NativeMethods.RECT work = alignmentInsets is WindowAlignmentInsets insets
            ? insets.ToWindow(display.Work)
            : DisplayPlacementService.GetExpandedWorkArea(display);
        double baseWidth = bounds.Width / display.Scale;
        double baseHeight = panelHeightPx / display.Scale;
        var minimumSize = compactList
            ? (OrganizerLimits.MinimumCompactListCanvasWidthDip, OrganizerLimits.MinimumCompactListCanvasHeightDip)
            : DisplayPlacementService.CalculateMinimumExpandedSizeDip(_definition.Layout, .5);
        bool centerSymmetric = !compactList && !_canvasResizeProbeRunning &&
            (NativeMethods.GetAsyncKeyState(VkMenu) & 0x8000) != 0;
        double maximumWidth;
        double maximumHeight;
        if (centerSymmetric)
        {
            int centerX = bounds.Left + bounds.Width / 2;
            int centerY = bounds.Top + titleHeightPx + panelHeightPx / 2;
            maximumWidth = Math.Max(1, 2d * Math.Min(centerX - work.Left, work.Right - centerX)) / display.Scale;
            maximumHeight = Math.Max(1, 2d * Math.Min(centerY - work.Top - titleHeightPx, work.Bottom - centerY)) / display.Scale;
        }
        else
        {
            // Full monitor work-area ceiling independent of where the organizer sits on screen.
            maximumWidth = Math.Max(baseWidth, work.Width / display.Scale);
            maximumHeight = Math.Max(baseHeight, Math.Max(1, work.Height - titleHeightPx) / display.Scale);
        }
        (double minimumScale, double maximumScale) = OrganizerContentScale.FactorRange(
            baseWidth, baseHeight, minimumSize.Item1, minimumSize.Item2, maximumWidth, maximumHeight,
            ContentScale, compactList ? _definition.CompactListItemScale : _definition.ItemScale);

        _ = NativeMethods.SetCapture(_hwnd);
        if (NativeMethods.GetCapture() != _hwnd && !_canvasResizeProbeRunning) return false;
        _dragCurrentBounds = bounds;
        _hasPendingCanvasResizeCursor = false;
        if (!_resizeGesture.TryStart())
        {
            if (NativeMethods.GetCapture() == _hwnd) _ = NativeMethods.ReleaseCapture();
            return false;
        }
        ClearWindowAlignment();
        _canvasResizeCenterSymmetric = centerSymmetric;
        _canvasResize = new(
            edge,
            cursor,
            bounds,
            compactList,
            startScale,
            ContentScale,
            baseWidth,
            baseHeight,
            minimumScale,
            maximumScale,
            display.Scale,
            work,
            alignmentInsets,
            display);
        ResetHoverWave();
        ClearExpandedEdgePointer();
        _trackedCanvasResizeEdgeWindow = IntPtr.Zero;
        SetCanvasResizeCursor(edge);
        // Hide the 4 native child edge windows once for the duration of the drag to eliminate
        // 4x per-frame synchronous SetWindowPos calls.
        UpdateCanvasResizeEdgeWindows(show: false);
        if (!compactList) BeginCanvasResizeGpuScaling(baseWidth, baseHeight);
        _canvasResizeClockBoost ??= CompositorClockBoost.Acquire();
        StartCanvasResizeRendering();
        if (!_canvasResizeInputTimer.IsRunning) _canvasResizeInputTimer.Start();
        return true;
    }

    private void BeginCanvasResizeGpuScaling(double baseWidth, double baseHeight)
    {
        double pinWidth = ExpandedPanel.ActualWidth > 1 ? ExpandedPanel.ActualWidth : baseWidth;
        double pinHeight = ExpandedPanel.ActualHeight > 1 ? ExpandedPanel.ActualHeight : baseHeight;
        ExpandedContentLayer.Width = pinWidth;
        ExpandedContentLayer.Height = pinHeight;
        ExpandedContentLayer.HorizontalAlignment = HorizontalAlignment.Left;
        ExpandedContentLayer.VerticalAlignment = VerticalAlignment.Top;
        Visual contentVisual = GetExpandedContentCompositionVisual();
        contentVisual.StopAnimation("Scale");
        contentVisual.CenterPoint = Vector3.Zero;
        contentVisual.Scale = Vector3.One;
        _canvasResizeGpuScaling = true;
        _hasPendingCanvasResizeFinalMetrics = false;
    }

    private void RestoreCanvasResizeGpuScaling()
    {
        if (!_canvasResizeGpuScaling) return;
        _canvasResizeGpuScaling = false;
        Visual contentVisual = GetExpandedContentCompositionVisual();
        contentVisual.StopAnimation("Scale");
        contentVisual.Scale = Vector3.One;
        ExpandedContentLayer.Width = double.NaN;
        ExpandedContentLayer.Height = double.NaN;
        ExpandedContentLayer.HorizontalAlignment = HorizontalAlignment.Stretch;
        ExpandedContentLayer.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private void CommitPendingCanvasResizeMetrics()
    {
        if (!_hasPendingCanvasResizeFinalMetrics) return;
        _hasPendingCanvasResizeFinalMetrics = false;
        _contentDisplayFit = 1;
        _definition.IconContentScale = OrganizerContentScale.Normalize(_pendingCanvasResizeContentScale);
        double canvasScale = _definition.CanvasScale > 0 ? _definition.CanvasScale : 1;
        _definition.ManualCanvasBaseWidthDip = _pendingCanvasResizePanelWidthDip / canvasScale;
        _definition.ManualCanvasBaseHeightDip = _pendingCanvasResizePanelHeightDip / canvasScale;
    }

    private void PollCanvasResizeInput()
    {
        if (_canvasResize is null)
        {
            _canvasResizeInputTimer.Stop();
            return;
        }
        if (!_expanded || _animating || _canvasResizeProbeRunning)
            return;
        // Watchdog only: live geometry updates are driven exclusively by the single VSync-locked
        // CompositionTarget.Rendering callback to prevent dual-loop resize thrashing.
        bool leftButtonDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        if (!leftButtonDown)
        {
            if (NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
                _ = FinishCanvasResizeAsync(cursor);
            else
                _ = FinishCanvasResizeAsync();
        }
    }

    private void AttachCanvasResizeWindowProc(IntPtr window)
    {
        IntPtr resizeWindowProc = Marshal.GetFunctionPointerForDelegate(_canvasResizeWindowProc);
        if (NativeMethods.GetWindowLongPtr(window, NativeMethods.GWLP_WNDPROC) == resizeWindowProc) return;
        IntPtr previous = NativeMethods.SetWindowLongPtr(
            window,
            NativeMethods.GWLP_WNDPROC,
            resizeWindowProc);
        if (previous != IntPtr.Zero) _canvasResizeOriginalWindowProcs[window] = previous;
    }

    private void UpdateCanvasResizeEdgeWindows(bool show)
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd)) return;
        if (!show || _canvasResize is not null || IsDock || _definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            _trackedCanvasResizeEdgeWindow = IntPtr.Zero;
            foreach (IntPtr edgeWindow in _canvasResizeEdgeWindows)
            {
                _ = NativeMethods.SetWindowPos(
                    edgeWindow,
                    IntPtr.Zero,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_HIDEWINDOW);
            }
            return;
        }
        if (!NativeMethods.GetClientRect(_hwnd, out NativeMethods.RECT client)) return;
        int width = Math.Max(1, client.Width);
        int height = Math.Max(1, client.Height);
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        int panelTopPx = GetExpandedTitleBandPx(scale);
        var rectangles = CanvasResizeHitZones.PanelEdgeRectangles(
            width, height, scale, IsCompactList, panelTopPx);
        while (_canvasResizeEdgeWindows.Count < rectangles.Length)
        {
            IntPtr edgeWindow = NativeMethods.CreateWindowEx(
                NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE,
                "STATIC",
                null,
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE,
                0,
                0,
                1,
                1,
                _hwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            if (edgeWindow == IntPtr.Zero) break;
            _ = NativeMethods.SetLayeredWindowAttributes(edgeWindow, 0, 1, NativeMethods.LWA_ALPHA);
            AttachCanvasResizeWindowProc(edgeWindow);
            _canvasResizeEdgeWindows.Add(edgeWindow);
        }

        for (int index = 0; index < _canvasResizeEdgeWindows.Count; index++)
        {
            if (index >= rectangles.Length)
            {
                _ = NativeMethods.SetWindowPos(
                    _canvasResizeEdgeWindows[index],
                    IntPtr.Zero,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_HIDEWINDOW);
                continue;
            }
            var rectangle = rectangles[index];
            _ = NativeMethods.SetWindowPos(
                _canvasResizeEdgeWindows[index],
                NativeMethods.HWND_TOP,
                rectangle.X,
                rectangle.Y,
                rectangle.Width,
                rectangle.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }

    private void RestoreCanvasResizeWindowProc(IntPtr window)
    {
        if (_trackedCanvasResizeEdgeWindow == window) _trackedCanvasResizeEdgeWindow = IntPtr.Zero;
        if (!_canvasResizeOriginalWindowProcs.Remove(window, out IntPtr previous) || !NativeMethods.IsWindow(window)) return;
        IntPtr current = NativeMethods.GetWindowLongPtr(window, NativeMethods.GWLP_WNDPROC);
        if (current == Marshal.GetFunctionPointerForDelegate(_canvasResizeWindowProc))
            _ = NativeMethods.SetWindowLongPtr(window, NativeMethods.GWLP_WNDPROC, previous);
    }

    private void CommitCanvasResize(NativeMethods.POINT cursor)
    {
        if (_canvasResize is not { } session) return;
        ClearStationTransitionVisuals();
        if (session.CompactList)
        {
            CommitCompactCanvasResize(session, cursor);
            return;
        }
        bool align = session.AlignmentInsets is not null &&
            OrganizerInteractionMath.ShouldUseWindowAlignment(_host.State.GlobalSettings.WindowAlignmentEnabled,
                true, _definition.PlacementMode, false, IsPermanentlyExpanded);
        WindowAlignmentResult? alignment = null;
        int titleHeightPx = GetExpandedTitleBandPx(session.DisplayScale);
        int startPanelHeightPx = Math.Max(1, session.StartBounds.Height - titleHeightPx);
        double canvasScale;
        NativeMethods.RECT bounds;
        int width;
        int height;

        if (_canvasResizeCenterSymmetric)
        {
            double factor = OrganizerInteractionMath.CalculateResizeFactor(
                session.Edge,
                cursor.X - session.StartCursor.X,
                cursor.Y - session.StartCursor.Y,
                session.StartBounds.Width,
                startPanelHeightPx);
            canvasScale = Math.Clamp(
                session.StartCanvasScale * factor,
                session.MinimumCanvasScale,
                session.MaximumCanvasScale);
            int centerX = session.StartBounds.Left + session.StartBounds.Width / 2;
            int centerY = session.StartBounds.Top + titleHeightPx + startPanelHeightPx / 2;
            (int left, int top, width, height) = OrganizerInteractionMath.CreateCenteredBounds(
                centerX,
                centerY,
                session.BaseWidthDip * canvasScale * session.DisplayScale,
                session.BaseHeightDip * canvasScale * session.DisplayScale);
            bounds = new NativeMethods.RECT
            {
                Left = left,
                Top = top - titleHeightPx,
                Right = left + width,
                Bottom = top + height
            };
            if (align && session.AlignmentInsets is WindowAlignmentInsets scaleInsets)
            {
                uint dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(_hwnd));
                WindowAlignmentScaleResult snapped = WindowAlignmentMath.AlignScale(centerX, centerY,
                    session.BaseWidthDip * session.DisplayScale, session.BaseHeightDip * session.DisplayScale,
                    canvasScale, session.MinimumCanvasScale, session.MaximumCanvasScale,
                    session.Display.Work, _host.GetWindowAlignmentTargets(this, session.Display), session.Edge,
                    WindowAlignmentMath.DipToPx(WindowAlignmentMath.SnapDistanceDip, dpi),
                    WindowAlignmentMath.DipToPx(WindowAlignmentMath.ReleaseDistanceDip, dpi), _windowAlignmentState);
                canvasScale = snapped.Scale;
                alignment = snapped.Alignment;
                bounds = scaleInsets.ToWindow(snapped.Alignment.Bounds);
                width = snapped.Alignment.Bounds.Width;
                height = snapped.Alignment.Bounds.Height;
            }
        }
        else
        {
            var startPanel = new NativeMethods.RECT
            {
                Left = session.StartBounds.Left,
                Top = session.StartBounds.Top + titleHeightPx,
                Right = session.StartBounds.Right,
                Bottom = session.StartBounds.Bottom
            };
            var workPanel = new NativeMethods.RECT
            {
                Left = session.WorkArea.Left,
                Top = session.WorkArea.Top + titleHeightPx,
                Right = session.WorkArea.Right,
                Bottom = session.WorkArea.Bottom
            };
            double factor = OrganizerInteractionMath.CalculateAnchoredResizeFactor(
                session.Edge,
                cursor.X - session.StartCursor.X,
                cursor.Y - session.StartCursor.Y,
                startPanel.Width,
                startPanel.Height);
            canvasScale = Math.Clamp(
                session.StartCanvasScale * factor,
                session.MinimumCanvasScale,
                session.MaximumCanvasScale);
            NativeMethods.RECT panelBounds = OrganizerInteractionMath.CreateAnchoredBounds(
                session.Edge,
                startPanel,
                workPanel,
                session.BaseWidthDip * canvasScale * session.DisplayScale,
                session.BaseHeightDip * canvasScale * session.DisplayScale);
            if (align && session.AlignmentInsets is WindowAlignmentInsets scaleInsets)
            {
                bool anchorPreserved =
                    (!session.Edge.HasFlag(CanvasResizeEdge.Left) || panelBounds.Right == startPanel.Right) &&
                    (!session.Edge.HasFlag(CanvasResizeEdge.Right) || panelBounds.Left == startPanel.Left) &&
                    (!session.Edge.HasFlag(CanvasResizeEdge.Top) || panelBounds.Bottom == startPanel.Bottom) &&
                    (!session.Edge.HasFlag(CanvasResizeEdge.Bottom) || panelBounds.Top == startPanel.Top);
                if (anchorPreserved)
                {
                    uint dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(_hwnd));
                    int snap = WindowAlignmentMath.DipToPx(WindowAlignmentMath.SnapDistanceDip, dpi);
                    int release = WindowAlignmentMath.DipToPx(WindowAlignmentMath.ReleaseDistanceDip, dpi);
                    var targets = _host.GetWindowAlignmentTargets(this, session.Display);
                    var raw = new CompactResizeResult(panelBounds, canvasScale, session.MinimumCanvasScale, session.MaximumCanvasScale);
                    WindowAlignmentScaleResult snapped = WindowAlignmentMath.AlignCompactCorner(
                        startPanel, raw, workPanel, targets, session.Edge, snap, release, _windowAlignmentState);
                    if (snapped.Alignment.XGuide is not null || snapped.Alignment.YGuide is not null)
                    {
                        canvasScale = snapped.Scale;
                        panelBounds = CompactResizeMath.IsCorner(session.Edge)
                            ? snapped.Alignment.Bounds
                            : OrganizerInteractionMath.CreateAnchoredBounds(
                                session.Edge,
                                startPanel,
                                workPanel,
                                session.BaseWidthDip * canvasScale * session.DisplayScale,
                                session.BaseHeightDip * canvasScale * session.DisplayScale);
                        alignment = snapped.Alignment with { Bounds = panelBounds };
                    }
                }
                bounds = scaleInsets.ToWindow(panelBounds);
            }
            else
            {
                bounds = new NativeMethods.RECT
                {
                    Left = panelBounds.Left,
                    Top = panelBounds.Top - titleHeightPx,
                    Right = panelBounds.Right,
                    Bottom = panelBounds.Bottom
                };
            }
            width = panelBounds.Width;
            height = panelBounds.Height;
        }

        if (RectsEqual(bounds, _dragCurrentBounds)) return;

        if (_canvasResizeGpuScaling)
        {
            if (!ApplyCanvasResizeBounds(bounds, alignment)) return;
            double panelWidthDip = width / session.DisplayScale;
            double panelHeightDip = height / session.DisplayScale;
            double pinnedWidth = double.IsFinite(ExpandedContentLayer.Width) && ExpandedContentLayer.Width > 0
                ? ExpandedContentLayer.Width
                : session.BaseWidthDip;
            double pinnedHeight = double.IsFinite(ExpandedContentLayer.Height) && ExpandedContentLayer.Height > 0
                ? ExpandedContentLayer.Height
                : session.BaseHeightDip;
            float scaleX = (float)Math.Max(0.05, panelWidthDip / pinnedWidth);
            float scaleY = (float)Math.Max(0.05, panelHeightDip / pinnedHeight);
            GetExpandedContentCompositionVisual().Scale = new Vector3(scaleX, scaleY, 1f);
            _pendingCanvasResizeContentScale = session.StartContentScale * canvasScale;
            _pendingCanvasResizePanelWidthDip = panelWidthDip;
            _pendingCanvasResizePanelHeightDip = panelHeightDip;
            _hasPendingCanvasResizeFinalMetrics = true;
            return;
        }

        double previousFit = _contentDisplayFit;
        double previousStoredScale = _definition.IconContentScale;
        _contentDisplayFit = 1;
        _definition.IconContentScale = session.StartContentScale * canvasScale;
        // SizeChanged may run inside SetWindowPos: expose matching metrics first.
        ApplyExpandedContentInset();
        if (!ApplyCanvasResizeBounds(bounds, alignment))
        {
            _definition.IconContentScale = previousStoredScale;
            _contentDisplayFit = previousFit;
            ApplyExpandedContentInset();
            return;
        }
        double definitionCanvasScale = _definition.CanvasScale > 0 ? _definition.CanvasScale : 1;
        _definition.ManualCanvasBaseWidthDip = width / session.DisplayScale / definitionCanvasScale;
        _definition.ManualCanvasBaseHeightDip = height / session.DisplayScale / definitionCanvasScale;
        ConfigureItemsLayout();
    }

    private void QueueCanvasResizeCompletion()
    {
        long version = _resizeGesture.Version;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closing) _ = RunSafelyAsync(() => FinishCanvasResizeAsync(version: version), "缩放结束失败");
        });
    }

    private Task FinishCanvasResizeAsync(NativeMethods.POINT? finalCursor = null, long? version = null) =>
        _resizeGesture.CompleteAsync(version ?? _resizeGesture.Version, () => FinishCanvasResizeCoreAsync(finalCursor));

    private async Task FinishCanvasResizeCoreAsync(NativeMethods.POINT? finalCursor)
    {
        if (_canvasResize is null || _closing) return;
        NativeMethods.POINT cursor;
        try
        {
            StopCanvasResizeRendering();
            if (finalCursor is { } suppliedCursor) CommitCanvasResize(suppliedCursor);
            else if (NativeMethods.GetCursorPos(out cursor)) CommitCanvasResize(cursor);
        }
        finally
        {
            // ReleaseCapture synchronously sends WM_CAPTURECHANGED. Retire the session first.
            _canvasResize = null;
            _canvasResizeCenterSymmetric = false;
            _canvasResizeInputTimer.Stop();
            _canvasResizeClockBoost?.Dispose();
            _canvasResizeClockBoost = null;
            ClearWindowAlignment();
            if (NativeMethods.GetCapture() == _hwnd) _ = NativeMethods.ReleaseCapture();
            CommitPendingCanvasResizeMetrics();
            ApplyExpandedContentInset();
            RestoreCanvasResizeGpuScaling();
            WindowRoot.UpdateLayout();
            RefreshPerformanceSettings();
        }
        ConfigureItemsLayout();
        UpdateCompactPreviewItemScale();
        UpdateSurfaceClips();
        if (_expanded && !_closing && !_animating) UpdateCanvasResizeEdgeWindows(show: true);
        if (NativeMethods.GetCursorPos(out cursor)) SetCanvasResizeCursor(GetCanvasResizeEdge(cursor));
        _interactionSaveTimer.Stop();
        CaptureExpandedPosition();
        await SaveStateAsync();
        if (!_closing) _desktopLayer?.Reattach();
    }

#if DEBUG
    private async Task RunCanvasResizeProbeAsync()
    {
        await Task.Delay(100);
        if (!TryGetLogicalWindowRect(out NativeMethods.RECT startBounds))
            throw new InvalidOperationException("Resize probe could not read the organizer bounds.");

        var startCursor = new NativeMethods.POINT
        {
            X = startBounds.Right - 8,
            Y = startBounds.Top + startBounds.Height / 2
        };
        _ = NativeMethods.SetCursorPos(startCursor.X, startCursor.Y);
        await Task.Delay(50);
        CanvasResizeEdge edge = GetCanvasResizeEdge(startCursor, startBounds);
        _canvasResizeProbeRunning = true;
        if (!TryBeginCanvasResize(edge)) throw new InvalidOperationException("Resize probe could not start resizing.");
        bool cursorMatches = SetCanvasResizeCursor(edge) && GetCanvasResizeHitTest(edge) == NativeMethods.HTRIGHT;

        var samples = new List<NativeMethods.RECT>();
        NativeMethods.POINT finalCursor = startCursor;
        for (int step = 1; step <= 12; step++)
        {
            finalCursor = new NativeMethods.POINT { X = startCursor.X - step * 8, Y = startCursor.Y };
            QueueCanvasResize(finalCursor);
            await Task.Delay(25);
            if (TryGetLogicalWindowRect(out NativeMethods.RECT sample)) samples.Add(sample);
        }

        var releaseClock = Stopwatch.StartNew();
        Task finish = FinishCanvasResizeAsync(finalCursor);
        _canvasResizeProbeRunning = false;
        long releaseSettledMs = releaseClock.ElapsedMilliseconds;
        _ = TryGetLogicalWindowRect(out NativeMethods.RECT beforeFreeMove);
        _ = NativeMethods.SetCursorPos(finalCursor.X - 120, finalCursor.Y);
        await Task.Delay(200);
        _ = TryGetLogicalWindowRect(out NativeMethods.RECT afterFreeMove);
        await finish;
        WindowRoot.UpdateLayout();

        int distinctWidths = samples.Select(sample => sample.Width).Distinct().Count();
        double dpiScale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        int titleHeightPx = GetExpandedTitleBandPx(dpiScale);
        double startRatio = startBounds.Width / (double)Math.Max(1, startBounds.Height - titleHeightPx);
        double maximumAspectErrorPx = samples.Count == 0
            ? double.PositiveInfinity
            : samples.Max(sample => Math.Abs(sample.Width - Math.Max(1, sample.Height - titleHeightPx) * startRatio));
        bool surfaceMatchesClient = NativeMethods.GetClientRect(_hwnd, out NativeMethods.RECT client) &&
            Math.Abs(ExpandedSurfaceHost.ActualWidth - client.Width / dpiScale) <= 1 &&
            Math.Abs(ExpandedSurfaceHost.ActualHeight + DisplayPlacementService.ExpandedTitleBandDip - client.Height / dpiScale) <= 1;
        Visual expandedVisual = GetExpandedCompositionVisual();
        bool transitionVisualReset = (_stationTransitionClip is null || expandedVisual.Clip != _stationTransitionClip) &&
            Vector3.Distance(expandedVisual.Scale, Vector3.One) <= .001f &&
            Vector3.Distance(GetExpandedContentVisualTranslation(), Vector3.Zero) <= .001f;
        bool followedAfterRelease = !RectsEqual(beforeFreeMove, afterFreeMove);
        bool passed = edge == CanvasResizeEdge.Right &&
            GetCanvasResizeHitTest(edge) == NativeMethods.HTRIGHT &&
            cursorMatches && distinctWidths >= 9 && maximumAspectErrorPx <= 1 &&
            surfaceMatchesClient && transitionVisualReset && releaseSettledMs <= 120 && !followedAfterRelease;
        string resultPath = Path.Combine(AppPaths.LocalRoot, "resize-probe.json");
        string result = JsonSerializer.Serialize(new
        {
            Passed = passed,
            CursorMatchesNativeResize = cursorMatches,
            ResizeHitTest = GetCanvasResizeHitTest(edge),
            DistinctLiveWidths = distinctWidths,
            LiveResponseRatio = Math.Round(distinctWidths / 12d, 3),
            MaximumAspectErrorPx = Math.Round(maximumAspectErrorPx, 3),
            SurfaceMatchesClient = surfaceMatchesClient,
            TransitionVisualReset = transitionVisualReset,
            ReleaseSettledMs = releaseSettledMs,
            FollowedAfterRelease = followedAfterRelease
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(resultPath, result);
    }
#endif

    private void StartCanvasResizeRendering()
    {
        if (_canvasResizeRenderingHooked) return;
        CompositionTarget.Rendering += OnCanvasResizeRendering;
        _canvasResizeRenderingHooked = true;
    }

    private void OnCanvasResizeRendering(object? sender, object args)
    {
        if (_canvasResize is null || _closing)
        {
            StopCanvasResizeRendering();
            return;
        }
        if (_canvasResizeCommitQueued) return;
        _canvasResizeCommitQueued = true;
        try
        {
            if (!_canvasResizeProbeRunning)
            {
                bool leftButtonDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
                if (!leftButtonDown)
                {
                    if (NativeMethods.GetCursorPos(out NativeMethods.POINT releaseCursor))
                        _ = FinishCanvasResizeAsync(releaseCursor);
                    else
                        _ = FinishCanvasResizeAsync();
                    return;
                }
                if (!_hasPendingCanvasResizeCursor && NativeMethods.GetCursorPos(out NativeMethods.POINT liveCursor))
                {
                    _pendingCanvasResizeCursor = liveCursor;
                    _hasPendingCanvasResizeCursor = true;
                }
            }
            if (!_hasPendingCanvasResizeCursor) return;
            NativeMethods.POINT cursor = _pendingCanvasResizeCursor;
            _hasPendingCanvasResizeCursor = false;
            CommitCanvasResize(cursor);
        }
        finally
        {
            _canvasResizeCommitQueued = false;
        }
    }

    private void QueueCanvasResize(NativeMethods.POINT cursor)
    {
        _pendingCanvasResizeCursor = cursor;
        _hasPendingCanvasResizeCursor = true;
        if (_canvasResizeProbeRunning)
        {
            _hasPendingCanvasResizeCursor = false;
            CommitCanvasResize(cursor);
        }
    }

    private void StopCanvasResizeRendering()
    {
        if (_canvasResizeRenderingHooked)
        {
            CompositionTarget.Rendering -= OnCanvasResizeRendering;
            _canvasResizeRenderingHooked = false;
        }
        _canvasResizeCommitQueued = false;
        _hasPendingCanvasResizeCursor = false;
    }

    private static CanvasResizeEdge GetCanvasResizeEdge(Point point, double width, double height)
        => CanvasResizeHitZones.HitTest(point.X, point.Y, width, height, compactList: false);

    private CanvasResizeEdge GetCanvasResizeEdge(NativeMethods.POINT cursor)
    {
        if (_definition.PlacementMode == OrganizerPlacementMode.Station || !_expanded || _animating ||
            _itemReorderSession is not null || _shellDragActive ||
            !TryGetLogicalWindowRect(out NativeMethods.RECT bounds)) return CanvasResizeEdge.None;
        return GetCanvasResizeEdge(cursor, bounds);
    }

    private CanvasResizeEdge GetCanvasResizeEdge(NativeMethods.POINT cursor, NativeMethods.RECT bounds)
    {
        if (IsDock || _definition.PlacementMode == OrganizerPlacementMode.Station || !_expanded || _animating ||
            _itemReorderSession is not null || _shellDragActive) return CanvasResizeEdge.None;
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        double widthDip = bounds.Width / scale;
        double heightDip = bounds.Height / scale;
        double panelTopDip = GetExpandedTitleBandPx(scale) / scale;
        Point point = new((cursor.X - bounds.Left) / scale, (cursor.Y - bounds.Top) / scale);
        bool compactList = IsCompactList;
        CanvasResizeEdge edge = CanvasResizeHitZones.HitTestPanel(
            point.X, point.Y, widthDip, heightDip, compactList, panelTopDip);
        if (edge == CanvasResizeEdge.None) return CanvasResizeEdge.None;
        if (IsCursorOverExpandedResizeExclusion(cursor)) return CanvasResizeEdge.None;
        // Let the outer glass rim always own resize even on packed grids, while letting
        // magnified hover items keep priority inside the inner corner arc zone.
        if (!CanvasResizeHitZones.IsOnOuterPanelRim(point.X, point.Y, widthDip, heightDip, compactList, panelTopDip) &&
            HasHoverPresentation && TryScreenToHoverViewport(cursor, out Point hoverPoint) &&
            HoverItemAtViewportPoint(hoverPoint) is not null)
        {
            return CanvasResizeEdge.None;
        }
        return edge;
    }

    private bool IsCursorOverExpandedResizeExclusion(NativeMethods.POINT cursor) =>
        IsCursorOverVisibleElement(CollapseButton, cursor) ||
        IsCursorOverVisibleElement(SelectionCountBadge, cursor) ||
        IsCursorOverVisibleElement(ExpandedFilterBar, cursor);

    private bool IsCursorOverVisibleElement(FrameworkElement element, NativeMethods.POINT cursor) =>
        element.Visibility == Visibility.Visible &&
        element.ActualWidth > 0 &&
        element.ActualHeight > 0 &&
        TryGetElementScreenBounds(element, out NativeMethods.RECT rect) &&
        cursor.X >= rect.Left && cursor.X < rect.Right &&
        cursor.Y >= rect.Top && cursor.Y < rect.Bottom;

    private bool SetCanvasResizeCursor(CanvasResizeEdge edge)
    {
        uint cursorId = edge switch
        {
            CanvasResizeEdge.None => NativeMethods.IDC_ARROW,
            CanvasResizeEdge.Left or CanvasResizeEdge.Right => NativeMethods.IDC_SIZEWE,
            CanvasResizeEdge.Top or CanvasResizeEdge.Bottom => NativeMethods.IDC_SIZENS,
            CanvasResizeEdge.Left | CanvasResizeEdge.Top or CanvasResizeEdge.Right | CanvasResizeEdge.Bottom => NativeMethods.IDC_SIZENWSE,
            _ => NativeMethods.IDC_SIZENESW
        };
        IntPtr cursor = NativeMethods.LoadCursor(IntPtr.Zero, new UIntPtr(cursorId));
        if (cursor == IntPtr.Zero) return false;
        _ = NativeMethods.SetCursor(cursor);
        return true;
    }

    private static int GetCanvasResizeHitTest(CanvasResizeEdge edge) => edge switch
    {
        CanvasResizeEdge.Left => NativeMethods.HTLEFT,
        CanvasResizeEdge.Right => NativeMethods.HTRIGHT,
        CanvasResizeEdge.Top => NativeMethods.HTTOP,
        CanvasResizeEdge.Bottom => NativeMethods.HTBOTTOM,
        CanvasResizeEdge.Left | CanvasResizeEdge.Top => NativeMethods.HTTOPLEFT,
        CanvasResizeEdge.Right | CanvasResizeEdge.Top => NativeMethods.HTTOPRIGHT,
        CanvasResizeEdge.Left | CanvasResizeEdge.Bottom => NativeMethods.HTBOTTOMLEFT,
        CanvasResizeEdge.Right | CanvasResizeEdge.Bottom => NativeMethods.HTBOTTOMRIGHT,
        _ => NativeMethods.HTCLIENT
    };

    private static CanvasResizeEdge GetCanvasResizeEdgeFromHitTest(int hitTest) => hitTest switch
    {
        NativeMethods.HTLEFT => CanvasResizeEdge.Left,
        NativeMethods.HTRIGHT => CanvasResizeEdge.Right,
        NativeMethods.HTTOP => CanvasResizeEdge.Top,
        NativeMethods.HTBOTTOM => CanvasResizeEdge.Bottom,
        NativeMethods.HTTOPLEFT => CanvasResizeEdge.Left | CanvasResizeEdge.Top,
        NativeMethods.HTTOPRIGHT => CanvasResizeEdge.Right | CanvasResizeEdge.Top,
        NativeMethods.HTBOTTOMLEFT => CanvasResizeEdge.Left | CanvasResizeEdge.Bottom,
        NativeMethods.HTBOTTOMRIGHT => CanvasResizeEdge.Right | CanvasResizeEdge.Bottom,
        _ => CanvasResizeEdge.None
    };

    private IntPtr CanvasResizeWindowProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (TryHandleCanvasResizeWindowMessage(hWnd, message, wParam, out IntPtr result)) return result;
            HandleCanvasResizeEdgeHoverMessage(hWnd, message);
        }
        catch (Exception ex)
        {
            AppLogger.Error("缩放边缘窗口消息处理异常。", ex);
        }
        return _canvasResizeOriginalWindowProcs.TryGetValue(hWnd, out IntPtr previous)
            ? NativeMethods.CallWindowProc(previous, hWnd, message, wParam, lParam)
            : IntPtr.Zero;
    }

    private void HandleCanvasResizeEdgeHoverMessage(IntPtr hWnd, uint message)
    {
        if (_closing || _canvasResize is not null || hWnd == _hwnd) return;
        if (message == NativeMethods.WM_MOUSEMOVE)
        {
            if (!_expanded || _animating || !NativeMethods.GetCursorPos(out NativeMethods.POINT hoverCursor))
                return;
            if (_trackedCanvasResizeEdgeWindow != hWnd)
            {
                var track = new TrackMouseEventInfo
                {
                    Size = (uint)Marshal.SizeOf<TrackMouseEventInfo>(),
                    Flags = TmeLeave,
                    HwndTrack = hWnd
                };
                if (TrackMouseEvent(ref track))
                    _trackedCanvasResizeEdgeWindow = hWnd;
            }
            UpdateExpandedEdgePointerFromScreenCursor(hoverCursor);
            return;
        }
        if (message == WmMouseLeave)
        {
            if (_trackedCanvasResizeEdgeWindow == hWnd)
                _trackedCanvasResizeEdgeWindow = IntPtr.Zero;
            if (!NativeMethods.GetCursorPos(out NativeMethods.POINT leaveCursor) ||
                !IsCursorInsideExpandedWindow(leaveCursor))
            {
                ClearExpandedEdgePointer();
            }
        }
    }

    private void UpdateExpandedEdgePointerFromScreenCursor(NativeMethods.POINT cursor)
    {
        if (_closing || !_expanded || _animating) return;
        if (!_host.State.GlobalSettings.EdgeGlowEnabled || !UseCustomAnimations || DesktopTypographyService.IsHighContrast())
        {
            ClearExpandedEdgePointer();
            return;
        }
        if (!TryGetElementScreenBounds(ExpandedEdgeOverlay, out NativeMethods.RECT edgeBounds)) return;
        double scale = Math.Max(1.0, ExpandedEdgeOverlay.XamlRoot?.RasterizationScale
            ?? (NativeMethods.GetDpiForWindow(_hwnd) / 96d));
        double xDip = (cursor.X - edgeBounds.Left) / scale;
        double yDip = (cursor.Y - edgeBounds.Top) / scale;
        _expandedEdgeSurface.UpdatePointerRim(xDip, yDip);
    }

    private bool IsCursorInsideExpandedWindow(NativeMethods.POINT cursor)
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd) ||
            !TryGetLogicalWindowRect(out NativeMethods.RECT bounds))
        {
            return false;
        }
        if (cursor.X < bounds.Left || cursor.X >= bounds.Right ||
            cursor.Y < bounds.Top || cursor.Y >= bounds.Bottom)
        {
            return false;
        }
        IntPtr overWindow = NativeMethods.WindowFromPoint(cursor);
        return overWindow == _hwnd || (overWindow != IntPtr.Zero && NativeMethods.IsChild(_hwnd, overWindow));
    }

    private bool TryHandleCanvasResizeWindowMessage(IntPtr hWnd, uint message, UIntPtr wParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (message == NativeMethods.WM_NCHITTEST && NativeMethods.GetCursorPos(out NativeMethods.POINT hitCursor))
        {
            if (hWnd != _hwnd)
            {
                CanvasResizeEdge childEdge = _canvasResize?.Edge ?? GetCanvasResizeEdge(hitCursor);
                result = new IntPtr(childEdge != CanvasResizeEdge.None
                    ? NativeMethods.HTCLIENT
                    : NativeMethods.HTTRANSPARENT);
                return true;
            }
            CanvasResizeEdge edge = _canvasResize?.Edge ?? GetCanvasResizeEdge(hitCursor);
            if (edge != CanvasResizeEdge.None)
            {
                result = new IntPtr(GetCanvasResizeHitTest(edge));
                return true;
            }
        }
        if (message == NativeMethods.WM_SETCURSOR && NativeMethods.GetCursorPos(out NativeMethods.POINT hoverCursor))
        {
            CanvasResizeEdge edge = _canvasResize?.Edge ?? GetCanvasResizeEdge(hoverCursor);
            if (edge != CanvasResizeEdge.None)
            {
                SetCanvasResizeCursor(edge);
                result = new IntPtr(1);
                return true;
            }
        }
        if (message == NativeMethods.WM_NCLBUTTONDOWN)
        {
            CanvasResizeEdge edge = GetCanvasResizeEdgeFromHitTest(unchecked((int)wParam.ToUInt64()));
            if (edge != CanvasResizeEdge.None && TryBeginCanvasResize(edge)) return true;
        }
        if (message == NativeMethods.WM_LBUTTONDOWN && NativeMethods.GetCursorPos(out NativeMethods.POINT pressCursor))
        {
            CanvasResizeEdge edge = GetCanvasResizeEdge(pressCursor);
            if (edge != CanvasResizeEdge.None && TryBeginCanvasResize(edge)) return true;
        }
        if (_canvasResize is not null && message == NativeMethods.WM_MOUSEMOVE &&
            NativeMethods.GetCursorPos(out NativeMethods.POINT resizeCursor))
        {
            if (!_canvasResizeProbeRunning)
            {
                if ((wParam.ToUInt64() & NativeMethods.MK_LBUTTON) == 0) QueueCanvasResizeCompletion();
                else QueueCanvasResize(resizeCursor);
            }
            return true;
        }
        if (_canvasResize is not null && message is NativeMethods.WM_LBUTTONUP or NativeMethods.WM_NCLBUTTONUP or NativeMethods.WM_CAPTURECHANGED)
        {
            QueueCanvasResizeCompletion();
            return true;
        }
        return false;
    }
}
