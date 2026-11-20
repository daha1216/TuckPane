using System;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

/// <summary>
/// Coordinates desktop z-order attachment repair, desktop visibility polling,
/// and hover-expand / pointer-leave auto-collapse timers for an organizer window.
/// </summary>
internal sealed class DesktopAttachmentCoordinator
{
    private readonly MainWindow _window;
    private readonly DispatcherQueueTimer _desktopRepairTimer;
    private readonly DispatcherQueueTimer _desktopVisibilityTimer;
    private readonly DispatcherQueueTimer _stationPointerTimer;
    private bool _stationTransitionPending;
    private long _stationHotSince;
    private long _stationOutsideSince;
    private long _ordinaryOutsideSince;

    public DesktopAttachmentCoordinator(MainWindow window)
    {
        _window = window;
        PerformanceTuning tuning = window.Host.State.GlobalSettings.PerformanceTuning;

        _desktopRepairTimer = window.DispatcherQueue.CreateTimer();
        _desktopRepairTimer.Interval = TimeSpan.FromMilliseconds(tuning.DesktopRepairMilliseconds);
        _desktopRepairTimer.IsRepeating = true;
        _desktopRepairTimer.Tick += DesktopRepairTimer_Tick;

        _desktopVisibilityTimer = window.DispatcherQueue.CreateTimer();
        _desktopVisibilityTimer.Interval = TimeSpan.FromMilliseconds(tuning.DesktopVisibilityMilliseconds);
        _desktopVisibilityTimer.IsRepeating = true;
        _desktopVisibilityTimer.Tick += DesktopVisibilityTimer_Tick;

        _stationPointerTimer = window.DispatcherQueue.CreateTimer();
        _stationPointerTimer.Interval = TimeSpan.FromMilliseconds(tuning.PointerPollMilliseconds);
        _stationPointerTimer.IsRepeating = true;
        _stationPointerTimer.Tick += StationPointerTimer_Tick;
    }

    public void ResetOrdinaryOutsideDelay() => _ordinaryOutsideSince = 0;

    public void ResetStationPointerDelay()
    {
        _stationHotSince = 0;
        _stationOutsideSince = 0;
    }

    public void ResetAllPointerDelays()
    {
        _ordinaryOutsideSince = 0;
        _stationHotSince = 0;
        _stationOutsideSince = 0;
    }

    public void PauseForWidgetDrag()
    {
        SetTimerRunning(_desktopRepairTimer, false);
        SetTimerRunning(_desktopVisibilityTimer, false);
        SetTimerRunning(_stationPointerTimer, false);
    }

    public void RefreshSchedule(PerformanceTuning tuning, bool pollPointer, bool repairDesktop, bool watchDesktop)
    {
        _stationPointerTimer.Interval = TimeSpan.FromMilliseconds(tuning.PointerPollMilliseconds);
        _desktopRepairTimer.Interval = TimeSpan.FromMilliseconds(tuning.DesktopRepairMilliseconds);
        _desktopVisibilityTimer.Interval = TimeSpan.FromMilliseconds(tuning.DesktopVisibilityMilliseconds);

        SetTimerRunning(_stationPointerTimer, pollPointer);
        if (!pollPointer)
        {
            _window.StopExternalHoverTimer();
            ResetAllPointerDelays();
        }

        SetTimerRunning(_desktopRepairTimer, repairDesktop);
        SetTimerRunning(_desktopVisibilityTimer, watchDesktop);
    }

    public void Stop()
    {
        _desktopRepairTimer.Stop();
        _desktopVisibilityTimer.Stop();
        _stationPointerTimer.Stop();
    }

    private static void SetTimerRunning(DispatcherQueueTimer timer, bool running)
    {
        if (running && !timer.IsRunning) timer.Start();
        else if (!running && timer.IsRunning) timer.Stop();
    }

    private async void StationPointerTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            await StationPointerTimerTickCoreAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("边缘轮询定时器处理异常。", ex);
        }
    }

    private async Task StationPointerTimerTickCoreAsync()
    {
        OrganizerDefinition definition = _window.Definition;
        GlobalSettings settings = _window.Host.State.GlobalSettings;
        OrganizerInteractionSnapshot interaction = _window.CaptureInteractionState();

        if (definition.PlacementMode != OrganizerPlacementMode.Station)
        {
            bool pointerOverWindow = NativeMethods.GetCursorPos(out NativeMethods.POINT pointer) &&
                _window.IsPointerOverWindow(pointer);
            if (!_window.IsExpandedCore && !_window.IsAnimating && !_window.IsChangingExpansionMode)
                _window.ObserveHoverGuardPointer(pointerOverWindow);

            if (!_window.IsClosing && pointerOverWindow && _window.ShouldStartIdleHoverExpand(interaction))
            {
                if (!_window.IsExternalHoverTimerRunning) _window.StartHoverExpandCore(scrollToEnd: false);
            }
            else if (!_window.IsHoverExpandScrollToEnd)
            {
                _window.StopExternalHoverTimer();
            }

            if (_window.IsClosing || !settings.CollapseOnPointerLeave || !_window.IsExpandedCore ||
                interaction.BlocksPointerLeaveCollapse)
            {
                _ordinaryOutsideSince = 0;
                return;
            }

            if (pointerOverWindow || _window.Host.HasExpandedContainedChild(definition.Id))
            {
                _ordinaryOutsideSince = 0;
                return;
            }

            long ordinaryNow = Stopwatch.GetTimestamp();
            _ordinaryOutsideSince = _ordinaryOutsideSince == 0 ? ordinaryNow : _ordinaryOutsideSince;
            if (Stopwatch.GetElapsedTime(_ordinaryOutsideSince, ordinaryNow).TotalMilliseconds <
                settings.PointerLeaveCollapseDelayMs) return;
            _ordinaryOutsideSince = 0;
            await _window.RunWindowSafelyAsync(_window.CollapseOrganizerAsync, "指针离开收起失败");
            return;
        }

        if (!_window.IsStationVisible || !_window.IsStationReady || _window.IsClosing ||
            _stationTransitionPending || !NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
        {
            ResetStationPointerDelay();
            return;
        }

        if (interaction.BlocksPointerLeaveCollapse)
        {
            ResetStationPointerDelay();
            return;
        }

        if (_window.IsExpandedCore && _window.Host.HasExpandedContainedChild(definition.Id))
        {
            ResetStationPointerDelay();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (!_window.IsExpandedCore)
        {
            DisplayInfo display = _window.GetOrResolveStationDisplay();
            if (!DisplayPlacementService.IsStationHotZone(
                    cursor,
                    display,
                    definition.DockEdge,
                    settings.StationActivationDistanceDip))
            {
                _stationHotSince = 0;
                return;
            }

            _stationHotSince = _stationHotSince == 0 ? now : _stationHotSince;
            if (Stopwatch.GetElapsedTime(_stationHotSince, now).TotalMilliseconds <
                settings.StationHoverExpandDelayMs) return;
            _stationTransitionPending = true;
            AppLogger.Performance($"station-expand-request id={_window.OrganizerId} display={display.Device}");
            ResetStationPointerDelay();
            try { await _window.RunWindowSafelyAsync(() => _window.ExpandOrganizerAsync(), "中转站悬停展开失败"); }
            finally { _stationTransitionPending = false; }
            return;
        }

        if (!_window.TryGetWindowRect(out NativeMethods.RECT bounds))
        {
            _stationOutsideSince = 0;
            return;
        }

        DisplayInfo expandedDisplay = _window.GetOrResolveStationDisplay();
        if (DisplayPlacementService.IsStationExpandedSafeRegion(
                cursor,
                expandedDisplay,
                definition.DockEdge,
                bounds,
                settings.StationActivationDistanceDip))
        {
            _stationOutsideSince = 0;
            return;
        }

        _stationOutsideSince = _stationOutsideSince == 0 ? now : _stationOutsideSince;
        if (Stopwatch.GetElapsedTime(_stationOutsideSince, now).TotalMilliseconds <
            settings.StationPointerLeaveCollapseDelayMs) return;
        _stationTransitionPending = true;
        ResetStationPointerDelay();
        try { await _window.RunWindowSafelyAsync(_window.CollapseOrganizerAsync, "中转站离开收起失败"); }
        finally { _stationTransitionPending = false; }
    }

    private void DesktopRepairTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_window.IsExpandedCore || _window.IsAnimating || _window.IsPressActive) return;

        OrganizerDefinition definition = _window.Definition;
        if (definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            DisplayInfo display = DisplayPlacementService.GetDisplay(definition.Position?.MonitorDevice);
            _window.SetStationDisplay(display);
            NativeMethods.RECT anchor = DisplayPlacementService.CalculateStationAnchor(display, definition.DockEdge, definition.Position);
            WidgetPosition position = DisplayPlacementService.Capture(anchor);
            if (!MainWindow.AreRectsEqual(anchor, _window.CompactBounds) ||
                !MainWindow.ArePositionsEqual(definition.Position, position))
            {
                _window.SetCompactBounds(anchor, applyAndShow: false);
                definition.Position = position;
                _ = _window.SaveOrganizerStateAsync();
            }
            _window.HideAppWindow();
            return;
        }

        if (definition.PlacementMode == OrganizerPlacementMode.Positioned)
        {
            DesktopGridPlacement? placement = _window.Host.FindCurrentPositionedPlacement(definition.Id, _window.CompactBounds);
            if (placement is not null && !MainWindow.AreRectsEqual(placement.Bounds, _window.CompactBounds))
            {
                _window.MoveToPositionedPlacement(placement.Bounds, placement.CompactScale);
                definition.Position = DisplayPlacementService.Capture(_window.CompactBounds, _window.Hwnd);
                _ = _window.SaveOrganizerStateAsync();
            }
        }
        else
        {
            NativeMethods.RECT compactBounds = _window.CompactBounds;
            DisplayInfo currentDisplay = DisplayPlacementService.ForBounds(compactBounds);
            NativeMethods.RECT work = currentDisplay.Work;
            bool hasVisibleEdgePosition = compactBounds.Left < work.Left || compactBounds.Top < work.Top ||
                compactBounds.Right > work.Right || compactBounds.Bottom > work.Bottom;
            WindowAlignmentInsets alignmentInsets = default;
            bool clampAlignmentFrame = definition.PlacementMode == OrganizerPlacementMode.Floating &&
                (_window.Host.State.GlobalSettings.WindowAlignmentEnabled || hasVisibleEdgePosition) &&
                _window.TryGetCompactAlignmentInsetsForRepair(compactBounds, out alignmentInsets);
            NativeMethods.RECT corrected = clampAlignmentFrame
                ? WindowAlignmentMath.ClampFrame(compactBounds, work, alignmentInsets)
                : DisplayPlacementService.Clamp(compactBounds, work);
            if (!MainWindow.AreRectsEqual(corrected, compactBounds))
            {
                _window.SetCompactBounds(corrected, applyAndShow: true);
                // Do not overwrite canonical user coordinates when the display work area
                // transiently shrank (boot display init, RDP, sleep/wake, fullscreen games,
                // or monitor hot-plug). WM_DISPLAYCHANGE reconciles back to saved Position.
                if (definition.PlacementMode != OrganizerPlacementMode.Floating ||
                    CompactPlacementMath.ShouldPersistClampedFloatingPosition(definition.Position, currentDisplay))
                {
                    definition.Position = DisplayPlacementService.Capture(_window.CompactBounds, _window.Hwnd);
                    _ = _window.SaveOrganizerStateAsync();
                }
            }
        }

        if (!_window.CaptureInteractionState().BlocksDesktopLayerRepair && _window.DesktopLayer is { } desktopLayer)
        {
            bool healthChecked = definition.PlacementMode is
                OrganizerPlacementMode.Floating or OrganizerPlacementMode.Positioned;
            DesktopAttachmentState attachment = DesktopAttachmentState.Ok;
            DesktopAttachmentState remaining = DesktopAttachmentState.Ok;
            if (healthChecked)
            {
                attachment = desktopLayer.RepairDesktopAttachment(out remaining);
            }

            if (attachment != DesktopAttachmentState.Ok)
            {
                AppLogger.Performance(
                    $"desktop repair pass state={attachment} remaining={remaining} {desktopLayer.DescribeAttachment()}");
            }
            else if (!healthChecked)
            {
                desktopLayer.Reattach();
            }
        }
    }

    private void DesktopVisibilityTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_window.IsClosing || _window.IsAnimating || _window.IsPressActive ||
            _window.IsContainedWindow || !_window.IsRuntimeVisible) return;
        if (_window.Definition.PlacementMode is not (
                OrganizerPlacementMode.Floating or OrganizerPlacementMode.Positioned or OrganizerPlacementMode.Dock)) return;
        if (_window.CaptureInteractionState().BlocksDesktopLayerRepair || _window.DesktopLayer is not { } desktopLayer) return;

        DesktopAttachmentState state = desktopLayer.RepairDesktopAttachment(out DesktopAttachmentState remaining);
        if (state == DesktopAttachmentState.Ok) return;

        AppLogger.Performance(
            $"desktop layer repaired state={state} remaining={remaining} {desktopLayer.DescribeAttachment()}");
    }
}
