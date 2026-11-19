using Microsoft.UI.Dispatching;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class MainWindow
{
    private readonly WindowResourceScope _resourceScope = new();
    private DispatcherQueueTimer? _displayReconcileTimer;
    private bool _startupCloaked;

    private void BeginStartupCloak()
    {
        if (_hwnd == IntPtr.Zero) return;
        _ = NativeMethods.DisableWindowTransitions(_hwnd);
        if (NativeMethods.SetWindowCloaked(_hwnd, true))
            _startupCloaked = true;
    }

    private void ReleaseStartupCloak()
    {
        if (!_startupCloaked) return;
        _startupCloaked = false;
        if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
            _ = NativeMethods.SetWindowCloaked(_hwnd, false);
    }

    private void PrePositionCompactHostWindow()
    {
        if (_hwnd == IntPtr.Zero || _definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            return;
        }

        if (IsDock)
        {
            DisplayInfo dockDisplay = DisplayPlacementService.GetDisplay(_definition.DockCenter?.MonitorDevice);
            DockGeometry estimated = DockLayoutMath.Calculate(
                Math.Max(0, _definition.ItemOrder.Count),
                _definition.DockIconSizeDip,
                _definition.DockOrientation,
                _definition.DockSpacingFactor);
            double centerX = _definition.DockCenter is { } savedCenter
                ? dockDisplay.Work.Left / dockDisplay.Scale + savedCenter.XDip
                : (dockDisplay.Work.Left + dockDisplay.Work.Width / 2d) / dockDisplay.Scale;
            double centerY = _definition.DockCenter is { } savedCenterY
                ? dockDisplay.Work.Top / dockDisplay.Scale + savedCenterY.YDip
                : (dockDisplay.Work.Top + dockDisplay.Work.Height / 2d) / dockDisplay.Scale;
            NativeMethods.RECT dockBounds = DockLayoutMath.CalculateBounds(
                estimated,
                new System.Numerics.Vector2((float)centerX, (float)centerY),
                dockDisplay.Scale);
            _ = NativeMethods.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                dockBounds.Left,
                dockBounds.Top,
                Math.Max(1, dockBounds.Width),
                Math.Max(1, dockBounds.Height),
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
            return;
        }

        double compactScale = _definition.PlacementMode == OrganizerPlacementMode.Positioned
            ? _definition.CompactScale
            : _host.State.GlobalSettings.ResolveCompactScale(_definition.PlacementMode, _definition.CompactScale);
        _appliedCompactScale = compactScale;
        DisplayInfo display = DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
        NativeMethods.RECT initialBounds = RestoreCompactBoundsForDisplay(display, compactScale);
        _compactBounds = initialBounds;

        // Size and position the HWND before Activate() while cloaked so WinUI never
        // lays out at a giant default overlapped window and never flashes black.
        _ = NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            initialBounds.Left,
            initialBounds.Top,
            initialBounds.Width,
            initialBounds.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    private NativeMethods.RECT RestoreCompactBoundsForDisplay(DisplayInfo display, double compactScale)
    {
        double nameScale = _host.State.GlobalSettings.ResolveCompactNameScale(_definition.PlacementMode);
        double widthDip = _definition.PlacementMode == OrganizerPlacementMode.Positioned && _positionedCompactWidthDip > 0
            ? _positionedCompactWidthDip
            : OrganizerLimits.CalculateCompactWindowWidthDip(compactScale, nameScale);
        double heightDip = _definition.PlacementMode == OrganizerPlacementMode.Positioned && _positionedCompactHeightDip > 0
            ? _positionedCompactHeightDip
            : OrganizerLimits.CalculateCompactWindowHeightDip(compactScale, nameScale);

        int compactWidth = DipToPx(widthDip, display.Scale);
        int compactHeight = DipToPx(heightDip, display.Scale);
        bool useAlignmentFrame = CompactPlacementMath.ShouldUseAlignmentFrameOnRestore(
            _definition.PlacementMode,
            _host.State.GlobalSettings.WindowAlignmentEnabled,
            _definition.Position,
            compactWidth,
            compactHeight,
            display);

        WindowAlignmentInsets? restoreInsets = useAlignmentFrame
            ? CompactPlacementMath.CalculateCompactAlignmentInsets(
                compactWidth,
                compactHeight,
                compactScale,
                display.Scale)
            : null;

        return DisplayPlacementService.RestoreToDisplay(
            _definition.Position,
            display,
            compactWidth,
            compactHeight,
            restoreInsets);
    }

    private double ResolveCompactDisplayScale(NativeMethods.RECT bounds)
    {
        DisplayInfo display = DisplayPlacementService.ForBounds(bounds);
        double scale = display.Scale;
        if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
        {
            double windowDpiScale = NativeMethods.GetDpiForWindow(_hwnd) / 96d;
            if (double.IsFinite(windowDpiScale) && windowDpiScale > 0)
                scale = Math.Max(scale, windowDpiScale);
        }
        double xamlScale = WindowRoot.XamlRoot?.RasterizationScale ?? 0;
        if (double.IsFinite(xamlScale) && xamlScale > 0)
            scale = Math.Max(scale, xamlScale);
        return Math.Max(1.0, scale);
    }

    private WindowAlignmentInsets CalculateDeterministicCompactAlignmentInsets(
        NativeMethods.RECT windowBounds,
        double? displayScale = null)
    {
        double scale = displayScale ?? ResolveCompactDisplayScale(windowBounds);
        return CompactPlacementMath.CalculateCompactAlignmentInsets(
            windowBounds.Width,
            windowBounds.Height,
            _appliedCompactScale,
            scale);
    }

    private void ScheduleDisplayChangeReconciliation()
    {
        if (_closing || IsDock || _definition.PlacementMode == OrganizerPlacementMode.Station) return;
        if (_displayReconcileTimer is null)
        {
            _displayReconcileTimer = DispatcherQueue.CreateTimer();
            _displayReconcileTimer.Interval = TimeSpan.FromMilliseconds(250);
            _displayReconcileTimer.IsRepeating = false;
            _displayReconcileTimer.Tick += (_, _) => ReconcileDisplayChange();
            _resourceScope.Track(() => _displayReconcileTimer?.Stop());
        }
        _displayReconcileTimer.Stop();
        _displayReconcileTimer.Start();
        _ = _host.NotifyDisplayTopologyChangedAsync();
    }

    internal void ReconcileDisplayChange()
    {
        if (_closing || _animating || _pressActive || _widgetDragging || _canvasResize is not null ||
            _hwnd == IntPtr.Zero || IsDock || _definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            return;
        }

        DisplayInfo display = DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
        double windowScale = Math.Max(1.0, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        if (display.Monitor.Left == 0 && display.Monitor.Top == 0)
            windowScale = Math.Max(windowScale, NativeMethods.GetDpiForSystem() / 96d);
        if (WindowRoot.XamlRoot is { RasterizationScale: > 0 } xamlRoot)
            windowScale = Math.Max(windowScale, xamlRoot.RasterizationScale);
        display = display with { Scale = Math.Max(display.Scale, windowScale) };

        if (_definition.PlacementMode == OrganizerPlacementMode.Floating)
        {
            NativeMethods.RECT restored = RestoreCompactBoundsForDisplay(display, _appliedCompactScale);
            if (!RectsEqual(restored, _compactBounds))
            {
                _compactBounds = restored;
                if (!_expanded && !IsContained)
                {
                    ApplyBounds(_compactBounds, show: _runtimeVisible, preserveZOrder: true);
                    UpdateSurfaceClips();
                }
            }
        }
        else if (_definition.PlacementMode == OrganizerPlacementMode.Positioned && !_expanded && !IsContained)
        {
            NativeMethods.RECT restored = RestoreCompactBoundsForDisplay(display, _appliedCompactScale);
            if (_host.FindCurrentPositionedPlacement(_definition.Id, restored) is { } placement &&
                !RectsEqual(placement.Bounds, _compactBounds))
            {
                MoveToPositionedPlacement(placement.Bounds, placement.CompactScale);
            }
        }
    }
}
