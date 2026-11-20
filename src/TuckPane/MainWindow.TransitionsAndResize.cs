using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices;
using TuckPane.Core;
using TuckPane.Controls;
using TuckPane.Models;
using TuckPane.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;

namespace TuckPane;

public sealed partial class MainWindow
{
    private Task ExpandAsync(bool scrollToEnd = false) => ExpandTransitionAsync(scrollToEnd);

    private async Task ExpandTransitionAsync(bool scrollToEnd = false, bool forMode = false, CancellationToken modeCancellation = default)
    {
        if (IsDock) { if (_runtimeVisible) ApplyDockLayout(); return; }
        if (_closing || (_changingExpansionMode && !forMode) ||
            (_expanded && !_animating && _transitionProgress >= 1) || _hwnd == IntPtr.Zero ||
            (!_runtimeVisible && !IsContained && _definition.PlacementMode != OrganizerPlacementMode.Station)) return;
        long requestVersion = ++_transitionRequestVersion;
        _transitionCancellation?.Cancel();
        bool IsCurrent() => IsCurrentTransitionRequest(requestVersion, modeCancellation);
        using AnimationTraceSession? trace = BeginAnimationTrace("expand");
        if (_definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            if (!_stationReady) { ReleaseTransitionLock(requestVersion); return; }
            try { await (_stationContentTask ??= PrepareStationContentAsync()); }
            catch { _stationContentTask = null; throw; }
            if (!IsCurrent() || !_stationVisible) { ReleaseTransitionLock(requestVersion); return; }
        }
        await _host.PrepareToExpandAsync(this, IsCurrent);
        if (!IsCurrent() || _definition.PlacementMode == OrganizerPlacementMode.Station && !_stationVisible) { ReleaseTransitionLock(requestVersion); return; }
        bool contained = IsContained;
        NativeMethods.RECT compactOrigin = contained ? _containedAnchorBounds : _compactBounds;
        WidgetPosition? rememberedPosition = ShouldRememberExpandedPosition()
            ? _definition.ExpandedPosition
            : null;
        DisplayInfo targetDisplay = rememberedPosition is not null
            ? DisplayPlacementService.GetDisplay(rememberedPosition.MonitorDevice)
            : (_definition.PlacementMode == OrganizerPlacementMode.Station
            ? DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice)
            : DisplayPlacementService.ForBounds(compactOrigin));
        if (rememberedPosition is null) targetDisplay = targetDisplay with
        {
            Scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d)
        };
        if (NormalizeVisualScales(targetDisplay))
        {
            UpdateRealizedItems();
            RenderCompactPreview();
            await SaveStateAsync();
        }

        if (!IsCurrent()) { ReleaseTransitionLock(requestVersion); return; }
        bool continuingPath = UseCustomAnimations && _collapseTransitionGeometry is not null && _transitionProgress > 0 && _transitionProgress < 1;
        CancellationTokenSource transition = StartTransition(modeCancellation);
        _expanded = true;
        _animating = true;
        try
        {
            RefreshPerformanceSettings();
            if (_definition.PlacementMode != OrganizerPlacementMode.Station && !contained) _desktopLayer?.SetExpanded(true);

            NativeMethods.RECT currentBounds = compactOrigin;
            if (_definition.PlacementMode != OrganizerPlacementMode.Station && !contained &&
                !TryGetLogicalWindowRect(out currentBounds))
            {
                currentBounds = compactOrigin;
            }
            if (_definition.PlacementMode != OrganizerPlacementMode.Station && !contained &&
                RectsEqual(currentBounds, _compactBounds)) _compactBounds = currentBounds;
            NativeMethods.RECT expandedBounds = CalculateExpandedBounds(
                compactOrigin,
                rememberedPosition is null ? null : targetDisplay);
            if (rememberedPosition is not null)
            {
                expandedBounds = DisplayPlacementService.RestoreToDisplay(
                    rememberedPosition,
                    targetDisplay,
                    expandedBounds.Width,
                    expandedBounds.Height);
                expandedBounds = DisplayPlacementService.Clamp(
                    expandedBounds,
                    DisplayPlacementService.GetExpandedWorkArea(targetDisplay));
            }

            if (continuingPath) expandedBounds = _collapseTransitionGeometry!.ExpandedBounds;
            double initialProgress = Math.Clamp(_transitionProgress, 0, 1);
            bool reducedMotion = !UseCustomAnimations;
            if (!reducedMotion && _definition.PlacementMode != OrganizerPlacementMode.Station && !contained)
            {
                if (!continuingPath)
                {
                    PrepareTransitionAnchor(expandedBounds, compactOrigin, handoffAtCompactOrigin: true);
                    _collapseTransitionGeometry = CreateCollapseTransitionGeometry(expandedBounds, compactOrigin);
                }
            }
            else
            {
                PrepareTransitionAnchor(expandedBounds, compactOrigin);
                _collapseTransitionGeometry = null;
            }
            trace?.Advance(AnimationTraceStage.Layout);
            ExpandedView.Visibility = Visibility.Visible;
            CompactView.Visibility = _definition.PlacementMode == OrganizerPlacementMode.Station || contained
                ? Visibility.Collapsed
                : Visibility.Visible;
            ApplyTransitionFrame(initialProgress, reducedMotion);
            if (_definition.PlacementMode == OrganizerPlacementMode.Station || contained)
            {
                _desktopLayer?.SetExpanded(true, stayTopmost: true);
                if (_definition.PlacementMode == OrganizerPlacementMode.Station)
                    _host.RaiseActiveCompactOrganizerDrags(this);
            }
            NativeMethods.RECT animationBounds = expandedBounds;
            if (!reducedMotion && _collapseTransitionGeometry is { } path)
            {
                var position = new TransitionPositionPath(path.ExpandedBounds.Left, path.ExpandedBounds.Top,
                    path.EndExpandedLeft, path.EndExpandedTop).PositionAt(initialProgress);
                animationBounds = new NativeMethods.RECT
                {
                    Left = (int)Math.Round(position.X), Top = (int)Math.Round(position.Y),
                    Right = (int)Math.Round(position.X) + expandedBounds.Width,
                    Bottom = (int)Math.Round(position.Y) + expandedBounds.Height
                };
                _lastTransitionWindowLeft = animationBounds.Left;
                _lastTransitionWindowTop = animationBounds.Top;
            }
            ApplyBounds(animationBounds, show: _runtimeVisible || contained || _stationVisible);
            ConfigureItemsLayout();
            await WaitForNextRenderAsync(transition.Token);
            if (!IsCurrent() || transition.IsCancellationRequested) return;
            UpdateSurfaceClips();
            ApplyTransitionFrame(initialProgress, reducedMotion);
            trace?.Advance(AnimationTraceStage.Animation);
            await RunVisualTransitionAsync(1, transition.Token);
        }
        catch (OperationCanceledException)
        {
            if (trace is not null) trace.Outcome = "cancelled";
            return;
        }
        catch
        {
            if (!IsCurrent()) return;
            if (trace is not null) trace.Outcome = "failed";
            RecoverFailedTransition(transition);
            throw;
        }
        finally
        {
            trace?.Advance(AnimationTraceStage.Handoff);
            if (IsCurrent() && _transitionCancellation == transition && !transition.IsCancellationRequested)
            {
                _animating = false;
                _host.NotifyDockOpenStateChanged();
                CompactView.Visibility = Visibility.Collapsed;
                CompactView.Opacity = 1;
                CompactView.Translation = Vector3.Zero;
                ExpandedView.Opacity = 1;
                GetExpandedCompositionVisual().Scale = Vector3.One;
                ClearStationTransitionVisuals();
                if (IsPermanentlyExpanded && rememberedPosition is not null &&
                    TryGetLogicalWindowRect(out NativeMethods.RECT restoredWindow) &&
                    TryGetVisibleAlignmentFrame(ExpandedPanel, out NativeMethods.RECT restoredFrame))
                {
                    // Allow the title band outside the work area when the visible panel is aligned to its edge.
                    ApplyBounds(DisplayPlacementService.RestoreToDisplay(rememberedPosition, targetDisplay,
                        restoredWindow.Width, restoredWindow.Height,
                        WindowAlignmentInsets.From(restoredWindow, restoredFrame)), show: _runtimeVisible);
                }
                ApplyOutsideClickSetting();
                if (scrollToEnd) ScrollToEnd(animated: false);
                if (_definition.PlacementMode != OrganizerPlacementMode.Station)
                    WindowRoot.Focus(FocusState.Programmatic);
                UpdateCanvasResizeEdgeWindows(show: true);
                _canvasResizeLeftButtonDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
                _canvasResizeInputTimer.Start();
                if (trace is not null) trace.Outcome = "completed";
            }
        }
    }

    private Task CollapseAsync() => _organizerTitleEdit.IsBusy || ReceivingDrop ? Task.CompletedTask : CollapseTransitionAsync();

    private async Task CollapseTransitionAsync(bool forMode = false, CancellationToken modeCancellation = default)
    {
        if (StaysExpanded || _changingExpansionMode && !forMode || !_runtimeVisible && _pendingExpansionModeApply) return;
        if (_closing || (!_expanded && !_animating && _transitionProgress <= 0) || _hwnd == IntPtr.Zero || _shellDragActive) return;
        _compactClicks.Reset();
        long requestVersion = ++_transitionRequestVersion;
        _transitionCancellation?.Cancel();
        bool IsCurrent() => IsCurrentTransitionRequest(requestVersion, modeCancellation);
        using AnimationTraceSession? trace = BeginAnimationTrace("collapse");
        await _host.CollapseContainedChildrenAsync(_definition.Id, IsCurrent);
        if (!IsCurrent() || StaysExpanded || _changingExpansionMode && !forMode || !_runtimeVisible && _pendingExpansionModeApply) { ReleaseTransitionLock(requestVersion); return; }
        _outsideClickHook?.Stop();
        if (_itemReorderSession is not null) CancelItemReorder();
        ShutdownItemDragBoundaryHook();

        bool contained = IsContained;
        NativeMethods.RECT compactTarget = contained ? _containedAnchorBounds : _compactBounds;
        bool continuingPath = UseCustomAnimations && _collapseTransitionGeometry is not null && _transitionProgress > 0 && _transitionProgress < 1;
        CancellationTokenSource transition = StartTransition(modeCancellation);
        _expanded = false;
        _animating = true;
        try
        {
            RefreshPerformanceSettings();
            _canvasResizeInputTimer.Stop();
            _canvasResizeLeftButtonDown = false;
            UpdateCanvasResizeEdgeWindows(show: false);
            _externalHoverTimer.Stop();
            if (!TryGetLogicalWindowRect(out NativeMethods.RECT expandedBounds)) expandedBounds = CalculateExpandedBounds(compactTarget);
            if (!continuingPath)
            {
                PrepareTransitionAnchor(expandedBounds, compactTarget, handoffAtCompactOrigin: true);
                _collapseTransitionGeometry = _definition.PlacementMode == OrganizerPlacementMode.Station
                    ? null
                    : CreateCollapseTransitionGeometry(expandedBounds, compactTarget);
            }
            _lastTransitionWindowLeft = expandedBounds.Left;
            _lastTransitionWindowTop = expandedBounds.Top;
            RenderCompactPreview();
            CompactView.Visibility = _definition.PlacementMode == OrganizerPlacementMode.Station || contained
                ? Visibility.Collapsed
                : Visibility.Visible;

            trace?.Advance(AnimationTraceStage.Animation);
            await RunVisualTransitionAsync(0, transition.Token);
        }
        catch (OperationCanceledException)
        {
            if (trace is not null) trace.Outcome = "cancelled";
            return;
        }
        catch
        {
            if (!IsCurrent()) return;
            if (trace is not null) trace.Outcome = "failed";
            RecoverFailedTransition(transition);
            throw;
        }
        finally
        {
            trace?.Advance(AnimationTraceStage.Handoff);
            if (IsCurrent() && _transitionCancellation == transition && !transition.IsCancellationRequested)
            {
                _animating = false;
                _host.NotifyDockOpenStateChanged();
                CompactView.Opacity = 1;
                if (_definition.PlacementMode == OrganizerPlacementMode.Station)
                {
                    ExpandedView.Visibility = Visibility.Collapsed;
                    ExpandedView.Opacity = 0;
                    GetExpandedCompositionVisual().Scale = Vector3.One;
                    ClearStationTransitionVisuals();
                }
                else if (contained)
                {
                    CompactView.Translation = Vector3.Zero;
                    CompactView.Visibility = Visibility.Collapsed;
                    ExpandedView.Visibility = Visibility.Collapsed;
                    ExpandedView.Opacity = 0;
                    GetExpandedCompositionVisual().Scale = Vector3.One;
                    _appWindow?.Hide();
                }
                else
                {
                    CommitCompactHandoff(compactTarget);
                }
                _collapseTransitionGeometry = null;
                ClearExpandedFilter(releaseInputActivation: true);
                CancelItemMarquee();
                ClearItemSelection();
                _keyboardFocusedIndex = -1;
                if (_definition.PlacementMode == OrganizerPlacementMode.Station || contained) _appWindow?.Hide();
                _desktopLayer?.SetExpanded(false, showWindow: false);
                RefreshPerformanceSettings();
                _host.NotifyCollapsed(this);
                if (trace is not null) trace.Outcome = "completed";
            }
        }
    }

    private void RecoverFailedTransition(CancellationTokenSource transition)
    {
        if (_closing || !ReferenceEquals(_transitionCancellation, transition)) return;
        // Clear logical locks before any native/XAML cleanup, which can itself fail or reenter.
        _transitionCancellation = null;
        transition.Cancel();
        transition.Dispose();
        _animating = false;
        _host.NotifyDockOpenStateChanged();
        _expanded = false;
        _transitionProgress = 0;
        _transitionVelocity = 0;
        _collapseTransitionGeometry = null;
        _canvasResizeInputTimer.Stop();
        _outsideClickHook?.Stop();
        UpdateCanvasResizeEdgeWindows(show: false);
        ExpandedView.Visibility = Visibility.Collapsed;
        bool hidden = IsContained || _definition.PlacementMode == OrganizerPlacementMode.Station;
        CompactView.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        if (hidden) _appWindow?.Hide();
        else ApplyBounds(_compactBounds, show: _runtimeVisible, preserveZOrder: true);
        _desktopLayer?.SetExpanded(false, showWindow: false);
        RefreshPerformanceSettings();
        _host.NotifyCollapsed(this);
    }

    private void CommitCompactHandoff(NativeMethods.RECT compactTarget)
    {
        Visual expandedVisual = GetExpandedCompositionVisual();
        _compactBounds = compactTarget;
        ApplyBounds(compactTarget, show: true, preserveZOrder: true);
        CompactView.Translation = Vector3.Zero;
        CompactView.Opacity = 1;
        ExpandedView.Visibility = Visibility.Collapsed;
        ExpandedView.Opacity = 0;
        expandedVisual.Scale = Vector3.One;
        _animationTrace?.Metrics.RecordWork(AnimationTraceWork.UpdateLayout);
        WindowRoot.UpdateLayout();
        UpdateSurfaceClips();
    }

    // A transition claims its request slot (++_transitionRequestVersion) before it starts, so a
    // newer call can supersede it while it is still preparing. The superseded call then skips its
    // own finally block (IsCurrent() is false), and the newer call can return before it ever sets
    // _animating -- leaving the flag stuck true. Every _animating guard would then reject input for
    // the rest of the session, so each early exit after the version bump has to release the lock.
    private void ReleaseTransitionLock(long requestVersion)
    {
        if (_closing || requestVersion != _transitionRequestVersion) return;
        CancellationTokenSource? cancelled = _transitionCancellation;
        _transitionCancellation = null;
        if (cancelled is not null)
        {
            try { cancelled.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
            cancelled.Dispose();
        }
        _animating = false;
        _transitionVelocity = 0;
        _host.NotifyDockOpenStateChanged();
        RefreshPerformanceSettings();
    }

    private CancellationTokenSource StartTransition(CancellationToken modeCancellation = default)
    {
        ResetHoverWave();
        _transitionCancellation?.Cancel();
        _transitionCancellation?.Dispose();
        _transitionCancellation = CancellationTokenSource.CreateLinkedTokenSource(modeCancellation, _windowLifetime.Token);
        return _transitionCancellation;
    }

    private async Task RunVisualTransitionAsync(double target, CancellationToken cancellationToken)
    {
        bool reducedMotion = !UseCustomAnimations;
        long previous = Stopwatch.GetTimestamp();
        if (NativeMethods.TrySetCompositorClockBoost(true)) { }
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long now = Stopwatch.GetTimestamp();
                double measuredMilliseconds = Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
                double delta = Math.Clamp(measuredMilliseconds / 1000d, 1d / 240d, 1d / 30d);
                previous = now;
                if (reducedMotion)
                {
                    double step = delta / (ReducedMotionDurationMs / 1000d);
                    _transitionProgress = MoveTowards(_transitionProgress, target, step);
                    _transitionVelocity = 0;
                }
                else
                {
                    double response = _definition.PlacementMode == OrganizerPlacementMode.Station
                        ? StationTransitionResponseSeconds
                        : TransitionResponseSeconds;
                    double omega = 9.2 / response;
                    double displacement = _transitionProgress - target;
                    double helper = _transitionVelocity + omega * displacement;
                    double decay = Math.Exp(-omega * delta);
                    _transitionProgress = Math.Clamp(target + (displacement + helper * delta) * decay, 0, 1);
                    _transitionVelocity = (_transitionVelocity - omega * helper * delta) * decay;
                }
                ApplyTransitionFrame(_transitionProgress, reducedMotion);
                if (Math.Abs(target - _transitionProgress) <= .001 && Math.Abs(_transitionVelocity) <= .01)
                {
                    _transitionProgress = target;
                    _transitionVelocity = 0;
                    ApplyTransitionFrame(target, reducedMotion);
                    if (target == 0 && _definition.PlacementMode != OrganizerPlacementMode.Station)
                        await WaitForNextRenderedAsync(cancellationToken);
                    else
                        await WaitForNextRenderAsync(cancellationToken);
                    break;
                }
                await WaitForNextRenderAsync(cancellationToken);
            }
        }
        finally
        {
            if (_transitionCancellation is null || _transitionCancellation.Token == cancellationToken)
            {
                _ = NativeMethods.TrySetCompositorClockBoost(false);
            }
        }
    }

    private void ApplyTransitionFrame(double progress, bool reducedMotion)
    {
        _animationTrace?.Metrics.RecordWork(AnimationTraceWork.VisualFrame);
        if (_definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            ApplyStationTransitionFrame(progress, reducedMotion);
            return;
        }

        ClearStationTransitionVisuals();
        Visual expandedVisual = GetExpandedCompositionVisual();
        expandedVisual.CenterPoint = new Vector3(_transitionAnchorDip, 0);
        CompactView.Translation = _transitionCompactTranslation;
        expandedVisual.Scale = reducedMotion
            ? Vector3.One
            : new Vector3(
                _transitionStartScale.X + (1 - _transitionStartScale.X) * (float)progress,
                _transitionStartScale.Y + (1 - _transitionStartScale.Y) * (float)progress,
                1);
        ExpandedView.Opacity = Math.Clamp((progress - .02) / .82, 0, 1);
        CompactView.Opacity = 1 - Math.Clamp(progress / .34, 0, 1);
        UpdateExpandedClipRadius(progress);
        ApplyCollapseWindowPosition(progress, reducedMotion);
    }

    private void ApplyStationTransitionFrame(double progress, bool reducedMotion)
    {
        double width = Math.Max(1, ExpandedView.ActualWidth);
        double height = Math.Max(1, ExpandedView.ActualHeight);
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        StationTransitionFrame frame = StationTransitionMath.GetFrame(
            _definition.DockEdge,
            width,
            height,
            progress,
            1 / scale,
            reducedMotion);

        Visual expandedVisual = GetExpandedCompositionVisual();
        expandedVisual.Scale = Vector3.One;
        _stationTransitionClip ??= expandedVisual.Compositor.CreateRectangleClip();
        _stationTransitionClip.Left = (float)frame.ClipLeft;
        _stationTransitionClip.Top = (float)frame.ClipTop;
        _stationTransitionClip.Right = (float)frame.ClipRight;
        _stationTransitionClip.Bottom = (float)frame.ClipBottom;
        expandedVisual.Clip = _stationTransitionClip;

        ExpandedContentLayer.Translation = new Vector3((float)frame.TranslationX, (float)frame.TranslationY, 0);
        ExpandedView.Opacity = frame.Opacity;
        CompactView.Opacity = 0;
        CompactView.Translation = Vector3.Zero;
        UpdateExpandedClipRadius(1);
    }

    private void ClearStationTransitionVisuals()
    {
        Visual expandedVisual = GetExpandedCompositionVisual();
        if (_stationTransitionClip is not null && expandedVisual.Clip == _stationTransitionClip)
            expandedVisual.Clip = null;
        expandedVisual.Scale = Vector3.One;
        ExpandedContentLayer.Translation = Vector3.Zero;
    }

    private async Task LoadOrganizerPreviewAsync(OrganizerPreviewControl preview, Guid organizerId, object requestToken)
    {
        WidgetItem[] items = _host.GetOrganizerPreviewItems(organizerId).Take(CompactPreviewItemCount).ToArray();
        for (int index = 0; index < CompactPreviewItemCount; index++)
        {
            Image image = preview.Images[index];
            image.Tag = null;
            image.Source = null;
        }
        ApplyOrganizerPreviewLayout(preview, items.Length);
        for (int index = 0; index < items.Length; index++)
        {
            Image image = preview.Images[index];
            WidgetItem item = items[index];
            string iconKey = IsDocumentItem(item) ? item.RelativeName : item.FullPath;
            image.Tag = iconKey;
            if (item.Kind == WidgetItemKind.Organizer) image.Source = _organizerIcon;
            else if (IsDocumentItem(item)) image.Source = GetDocumentIcon(item);
            else await LoadIconAsync(image, item.FullPath);
            if (!ReferenceEquals(preview.RequestToken, requestToken)) return;
        }
    }

    private static void ApplyOrganizerPreviewLayout(OrganizerPreviewControl preview, int itemCount)
    {
        Image[] images = preview.Images.ToArray();
        double itemMargin = preview.IsCompactVisual ? .5 : 2;
        int visibleCount = Math.Clamp(itemCount, 0, images.Length);
        for (int index = 0; index < images.Length; index++)
        {
            Image image = images[index];
            Grid.SetRow(image, index / 2);
            Grid.SetColumn(image, index % 2);
            Grid.SetRowSpan(image, 1);
            Grid.SetColumnSpan(image, 1);
            image.Margin = new Thickness(itemMargin);
            image.HorizontalAlignment = HorizontalAlignment.Stretch;
            image.VerticalAlignment = VerticalAlignment.Stretch;
            image.Visibility = index < visibleCount ? Visibility.Visible : Visibility.Collapsed;
        }

        preview.EmptyStateIcon.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;

        switch (visibleCount)
        {
            case 1:
                Grid.SetRowSpan(images[0], 2);
                Grid.SetColumnSpan(images[0], 2);
                images[0].Margin = new Thickness(preview.IsCompactVisual ? 1 : 5);
                break;
            case 2:
                Grid.SetRowSpan(images[0], 2);
                Grid.SetRowSpan(images[1], 2);
                break;
            case 3:
                Grid.SetColumnSpan(images[2], 2);
                break;
        }
    }

    private static double MoveTowards(double current, double target, double maximumDelta) =>
        Math.Abs(target - current) <= maximumDelta ? target : current + Math.Sign(target - current) * maximumDelta;

    private void PrepareTransitionAnchor(
        NativeMethods.RECT expandedBounds,
        NativeMethods.RECT compactTarget,
        bool handoffAtCompactOrigin = false)
    {
        DisplayInfo display = DisplayPlacementService.ForBounds(expandedBounds) with
        {
            Scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d)
        };
        if (_definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            _transitionStartScale = Vector2.One;
            int anchorX = compactTarget.Left + compactTarget.Width / 2;
            int anchorY = compactTarget.Top + compactTarget.Height / 2;
            _transitionAnchorDip = new Vector2(
                (float)((anchorX - expandedBounds.Left) / display.Scale),
                (float)((anchorY - expandedBounds.Top) / display.Scale));
            _transitionCompactTranslation = Vector3.Zero;
            return;
        }
        double compactViewWidth = Math.Max(1, GetCompactWidthDip());
        double compactViewHeight = Math.Max(1, GetCompactHeightDip());
        double thumbnailSizeDip = Math.Max(1, 39 * _appliedCompactScale);
        var thumbnailCenterInCompact = new Point(compactViewWidth / 2, thumbnailSizeDip / 2);

        double thumbnailOffsetX = thumbnailCenterInCompact.X - compactViewWidth / 2;
        double thumbnailOffsetY = thumbnailCenterInCompact.Y - compactViewHeight / 2;
        int compactCenterX = compactTarget.Left + DipToPx(
            compactViewWidth / 2 + thumbnailOffsetX,
            display.Scale);
        int compactCenterY = compactTarget.Top + DipToPx(
            compactViewHeight / 2 + thumbnailOffsetY,
            display.Scale);
        double expandedWidthDip = expandedBounds.Width / display.Scale;
        double expandedHeightDip = expandedBounds.Height / display.Scale;
        _transitionStartScale = new Vector2(
            (float)Math.Clamp(thumbnailSizeDip / expandedWidthDip, .025, 1),
            (float)Math.Clamp(thumbnailSizeDip / expandedHeightDip, .025, 1));
        _transitionAnchorDip = handoffAtCompactOrigin
            ? new Vector2((float)thumbnailCenterInCompact.X, (float)thumbnailCenterInCompact.Y)
            : new Vector2(
                (float)((compactCenterX - expandedBounds.Left) / display.Scale),
                (float)((compactCenterY - expandedBounds.Top) / display.Scale));
        double compactCenterInExpandedX = expandedWidthDip / 2 + thumbnailOffsetX;
        double compactCenterInExpandedY = expandedHeightDip / 2 + thumbnailOffsetY;
        _transitionCompactTranslation = handoffAtCompactOrigin
            ? new Vector3(
                (float)(thumbnailCenterInCompact.X - compactCenterInExpandedX),
                (float)(thumbnailCenterInCompact.Y - compactCenterInExpandedY),
                0)
            : new Vector3(
                _transitionAnchorDip.X - (float)compactCenterInExpandedX,
                _transitionAnchorDip.Y - (float)compactCenterInExpandedY,
                0);
    }

    private CollapseTransitionGeometry CreateCollapseTransitionGeometry(
        NativeMethods.RECT expandedBounds,
        NativeMethods.RECT compactTarget)
    {
        return new CollapseTransitionGeometry(
            expandedBounds,
            compactTarget,
            compactTarget.Left,
            compactTarget.Top);
    }

    private void ApplyCollapseWindowPosition(double progress, bool reducedMotion)
    {
        if (reducedMotion || _collapseTransitionGeometry is not { } geometry) return;
        var position = new TransitionPositionPath(geometry.ExpandedBounds.Left, geometry.ExpandedBounds.Top,
            geometry.EndExpandedLeft, geometry.EndExpandedTop).PositionAt(progress);
        int left = (int)Math.Round(position.X);
        int top = (int)Math.Round(position.Y);
        if (left == _lastTransitionWindowLeft && top == _lastTransitionWindowTop) return;
        _lastTransitionWindowLeft = left;
        _lastTransitionWindowTop = top;
        _animationTrace?.Metrics.RecordWork(AnimationTraceWork.CollapseMove);
        _ = NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            left,
            top,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private async Task WaitForNextRenderAsync(CancellationToken cancellationToken)
    {
        AnimationTraceSession? trace = _animationTrace;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _windowLifetime.Token);
        EventHandler<object>? handler = null;
        bool completed = await UiEventAwaiter.WaitAsync(
            complete =>
            {
                handler = (_, _) => { trace?.Metrics.RecordRendering(Stopwatch.GetTimestamp()); complete(); };
                CompositionTarget.Rendering += handler;
            },
            _ => { if (handler is not null) CompositionTarget.Rendering -= handler; },
            TimeSpan.FromMilliseconds(100), linked.Token);
        trace?.Metrics.RecordWait(rendered: false, completed);
    }

    private async Task WaitForNextRenderedAsync(CancellationToken cancellationToken)
    {
        AnimationTraceSession? trace = _animationTrace;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _windowLifetime.Token);
        EventHandler<RenderedEventArgs>? handler = null;
        bool completed = await UiEventAwaiter.WaitAsync(
            complete => { handler = (_, _) => complete(); CompositionTarget.Rendered += handler; },
            _ => { if (handler is not null) CompositionTarget.Rendered -= handler; },
            TimeSpan.FromMilliseconds(100), linked.Token);
        trace?.Metrics.RecordWait(rendered: true, completed);
    }

    private NativeMethods.RECT CalculateExpandedBounds(NativeMethods.RECT compact, DisplayInfo? targetDisplay = null)
    {
        DisplayInfo display = targetDisplay ?? ((_definition.PlacementMode == OrganizerPlacementMode.Station
                ? DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice)
                : DisplayPlacementService.ForBounds(compact)) with
            {
                Scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d)
            });
        if (_definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            return DisplayPlacementService.CalculateStationBounds(
                display,
                _definition.DockEdge,
                _definition.Layout,
                _definition.CanvasScale,
                _definition.ItemScale,
                _definition.Position,
                _definition.ManualCanvasBaseWidthDip,
                _definition.ManualCanvasBaseHeightDip);
        }
        double? baseWidth = IsCompactList ? _definition.CompactListCanvasWidthDip : _definition.ManualCanvasBaseWidthDip;
        double? baseHeight = IsCompactList ? _definition.CompactListCanvasHeightDip : _definition.ManualCanvasBaseHeightDip;
        double canvas = IsCompactList ? 1 : _definition.CanvasScale;
        NativeMethods.RECT result = DisplayPlacementService.CalculateExpandedBounds(
            compact, display, _definition.Layout, canvas, baseWidth, baseHeight);
        _contentDisplayFit = baseWidth is double w && baseHeight is double h
            ? OrganizerContentScale.DisplayFit(w * canvas, h * canvas,
                result.Width / display.Scale, (result.Height - GetExpandedTitleBandPx(display.Scale)) / display.Scale)
            : 1;
        ApplyExpandedContentInset();
        return result;
    }

    private bool ShouldRememberExpandedPosition() =>
        OrganizerInteractionMath.ShouldRememberExpandedPosition(
            _host.State.GlobalSettings.RememberExpandedOrganizerPosition,
            _definition.PlacementMode,
            IsPermanentlyExpanded);

    private void CaptureExpandedPosition()
    {
        if (IsDock) { CaptureDockCenter(); return; }
        if (ShouldRememberExpandedPosition() && TryGetLogicalWindowRect(out NativeMethods.RECT bounds))
            _definition.ExpandedPosition = DisplayPlacementService.Capture(bounds, _hwnd);
    }

    private void CompactTile_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _externalHoverTimer.Stop();
        _hoverExpandScrollToEnd = false;
        if (_expanded || _animating || e.Pointer.PointerDeviceType == PointerDeviceType.Touch && !e.GetCurrentPoint(CompactTile).Properties.IsLeftButtonPressed)
        {
            return;
        }
        PointerPoint point = e.GetCurrentPoint(CompactTile);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse &&
            IsEligibleForDesktopMultiSelect &&
            (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0)
        {
            _host.ToggleOrganizerMultiSelection(OrganizerId);
            e.Handled = true;
            return;
        }

        BeginWidgetPress(CompactTile, e, point.Position, expanded: false);
    }

    private void ExpandedView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (CommitTitleOnOutsidePress(e)) return;
        if (e.Handled) return; // Hover proxies have already established the logical item press.
        if (_organizerTitleEdit.IsBusy || _itemReorderProbeRunning || !_expanded || _animating) return;
        PointerPoint point = e.GetCurrentPoint(ExpandedView);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && TryBeginCanvasResize())
        {
            e.Handled = true;
            return;
        }
        if (IsExpandedDragBlocked(e.OriginalSource as DependencyObject)) return;
        _compactClicks.Reset();

        // The hover visual can extend outside its unscaled XAML item host.
        if (IsDock && DockItemAtPoint(point.Position) is { } dockItem)
        {
            Item_PointerPressed(dockItem, e);
            e.Handled = true;
            return;
        }

        if (TryBeginItemMarqueePress(e)) return;

        BeginWidgetPress(ExpandedView, e, point.Position, expanded: true);
        _pressedOrganizerTitle = _pressActive && !IsDock && !_definition.HideName &&
            ReferenceEquals(e.OriginalSource, ExpandedNameText);
    }

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
        int centerX = bounds.Left + bounds.Width / 2;
        int centerY = bounds.Top + titleHeightPx + panelHeightPx / 2;
        double maximumWidth = Math.Max(1, 2d * Math.Min(centerX - work.Left, work.Right - centerX)) / display.Scale;
        double maximumHeight = Math.Max(1, 2d * Math.Min(centerY - work.Top - titleHeightPx, work.Bottom - centerY)) / display.Scale;
        (double minimumScale, double maximumScale) = OrganizerContentScale.FactorRange(
            baseWidth, baseHeight, minimumSize.Item1, minimumSize.Item2, maximumWidth, maximumHeight,
            ContentScale, compactList ? _definition.CompactListItemScale : _definition.ItemScale);

        _ = NativeMethods.SetCapture(_hwnd);
        if (NativeMethods.GetCapture() != _hwnd) return false;
        _dragCurrentBounds = bounds;
        _hasPendingCanvasResizeCursor = false;
        if (!_resizeGesture.TryStart()) return false;
        ClearWindowAlignment();
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
        SetCanvasResizeCursor(edge);
        return true;
    }

    private void PollCanvasResizeInput()
    {
        bool leftButtonDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        if (!_expanded || _animating || !NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
        {
            _canvasResizeLeftButtonDown = leftButtonDown;
            return;
        }

        CanvasResizeEdge edge = _canvasResize?.Edge ?? GetCanvasResizeEdge(cursor);
        if (_canvasResizeProbeRunning)
        {
            _canvasResizeLeftButtonDown = leftButtonDown;
            return;
        }
        if (_canvasResize is null)
        {
            if (leftButtonDown && !_canvasResizeLeftButtonDown && edge != CanvasResizeEdge.None)
                _ = TryBeginCanvasResize(edge);
        }
        else if (leftButtonDown)
        {
            CommitCanvasResize(cursor);
        }
        else
        {
            _ = FinishCanvasResizeAsync(cursor);
        }
        _canvasResizeLeftButtonDown = leftButtonDown;
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
        if (!show || IsDock || _definition.PlacementMode == OrganizerPlacementMode.Station)
        {
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
        if (_canvasResizeEdgeWindows.Count == 0)
        {
            for (int index = 0; index < 4; index++)
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
                if (edgeWindow == IntPtr.Zero) continue;
                _ = NativeMethods.SetLayeredWindowAttributes(edgeWindow, 0, 1, NativeMethods.LWA_ALPHA);
                AttachCanvasResizeWindowProc(edgeWindow);
                _canvasResizeEdgeWindows.Add(edgeWindow);
            }
        }

        if (_canvasResizeEdgeWindows.Count != 4 || !NativeMethods.GetClientRect(_hwnd, out NativeMethods.RECT client)) return;
        int width = Math.Max(1, client.Width);
        int height = Math.Max(1, client.Height);
        var rectangles = CanvasResizeHitZones.EdgeRectangles(
            width, height, NativeMethods.GetDpiForWindow(_hwnd) / 96d, IsCompactList);
        for (int index = 0; index < rectangles.Length; index++)
        {
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
        if (!_canvasResizeOriginalWindowProcs.Remove(window, out IntPtr previous) || !NativeMethods.IsWindow(window)) return;
        IntPtr current = NativeMethods.GetWindowLongPtr(window, NativeMethods.GWLP_WNDPROC);
        if (current == Marshal.GetFunctionPointerForDelegate(_canvasResizeWindowProc))
            _ = NativeMethods.SetWindowLongPtr(window, NativeMethods.GWLP_WNDPROC, previous);
    }

    private void BeginWidgetPress(UIElement captureTarget, PointerRoutedEventArgs e, Point point, bool expanded)
    {
        if (_organizerTitleEdit.IsBusy || _closing || _pressActive || _resizeGesture.IsCompleting || !_widgetGesture.TryStart()) return;
        _pressedPointerId = e.Pointer.PointerId;
        _widgetMousePress = e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && OrganizerKinds.IsRegular(_definition.PlacementMode);
        _draggingExpanded = expanded;
        _pressPointDip = point;
        _longPressGesture.Press(point.X, point.Y);
        _pressActive = true;
        ResetHoverWave();
        _widgetDragging = false;
        _pressStartedAt = Stopwatch.GetTimestamp();
        _ = NativeMethods.GetCursorPos(out _pressCursorPx);
        _ = TryGetLogicalWindowRect(out _pressWindowBounds);
        _dragAlignmentInsets = TryGetWindowAlignmentFrame(out NativeMethods.RECT alignmentFrame)
                ? WindowAlignmentInsets.From(_pressWindowBounds, alignmentFrame)
                : null;
        ClearWindowAlignment();
        _dragCurrentBounds = _pressWindowBounds;
        if (!expanded && _definition.PlacementMode == OrganizerPlacementMode.Positioned)
        {
            _positionedDragOriginBounds = _compactBounds;
        }
        _dragDisplay = DisplayPlacementService.ForBounds(_pressWindowBounds);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            _ = NativeMethods.SetCapture(_hwnd);
            _nativeMouseCapture = NativeMethods.GetCapture() == _hwnd;
        }
        else if (!captureTarget.CapturePointer(e.Pointer))
        {
            ResetCompactPress();
            return;
        }

        StartDragClock();
        if (!_widgetMousePress) _longPressTimer.Start();
        e.Handled = true;
    }

    private static bool IsExpandedDragBlocked(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button or TextBox || current is FrameworkElement { Name: "ItemHost" or "ItemContent" }) return true;
            if (current is Grid { Name: "ExpandedView" }) break;
        }
        return false;
    }

    private void ExpandedView_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_canvasResize is not null)
        {
            e.Handled = true;
            return;
        }
        if (!_pressActive || !_draggingExpanded || e.Pointer.PointerId != _pressedPointerId) return;
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse && !_widgetDragging)
        {
            Point current = e.GetCurrentPoint(ExpandedView).Position;
            _ = _longPressGesture.Move(current.X, current.Y);
        }
        else if (_widgetDragging)
        {
            _dragInputCount++;
            QueueWidgetDragFromCursor();
            e.Handled = true;
        }
    }

    private async void ExpandedView_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_canvasResize is not null)
        {
            await RunSafelyAsync(() => FinishCanvasResizeAsync(), "展开窗口拖动结束失败");
            e.Handled = true;
            return;
        }
        if (_pressActive && _draggingExpanded && e.Pointer.PointerId == _pressedPointerId)
        {
            await RunSafelyAsync(() => FinishCompactPressAsync(allowOpen: true), "展开窗口拖动取消失败");
            e.Handled = true;
        }
    }

    private async void ExpandedView_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_canvasResize is not null) await RunSafelyAsync(() => FinishCanvasResizeAsync(), "展开窗口缩放取消失败");
        else await RunSafelyAsync(() => FinishCompactPressAsync(allowOpen: false), "展开窗口捕获丢失处理失败");
    }

    private async void ExpandedView_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_canvasResize is not null && NativeMethods.GetCapture() != _hwnd)
            await RunSafelyAsync(() => FinishCanvasResizeAsync(), "展开窗口缩放捕获丢失处理失败");
        else if (_pressActive && _draggingExpanded && !_nativeMouseCapture)
            await RunSafelyAsync(() => FinishCompactPressAsync(allowOpen: false), "展开窗口拖动捕获丢失处理失败");
    }

    private void ExpandedView_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled) return; // A hover proxy also bubbles through ExpandedView's handled-events listener.
        _compactClicks.Reset();
        if (IsDock) { Dock_PointerWheelChanged(e); return; }
        Point pointerPosition = e.GetCurrentPoint(ItemsScrollView).Position;
        bool pointerInsideList = pointerPosition.X >= 0 && pointerPosition.Y >= 0 &&
            pointerPosition.X <= ItemsScrollView.ActualWidth &&
            pointerPosition.Y <= ItemsScrollView.ActualHeight || HoverItemAtViewportPoint(pointerPosition) is not null;
        bool controlPressed = OrganizerInteractionMath.IsControlPressed(
            e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control),
            NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL));
        OrganizerWheelAction action = OrganizerInteractionMath.ResolveWheelAction(
            _expanded,
            _animating,
            _canvasResize is not null,
            _itemReorderSession is not null,
            _shellDragActive,
            controlPressed,
            IsCompactList,
            pointerInsideList);
        if (action == OrganizerWheelAction.Ignore)
        {
            _wheelDeltaRemainder = 0;
            _gridScrollState = _gridScrollState with { Remainder = 0 };
            _compactListScrollState = _compactListScrollState with { Remainder = 0 };
            return;
        }

        PointerPoint point = e.GetCurrentPoint(ItemsScrollView);
        e.Handled = true;
        if (action is OrganizerWheelAction.ScrollCompactList or OrganizerWheelAction.ScrollGrid)
        {
            if (action == OrganizerWheelAction.ScrollCompactList) UpdateCompactScrollHover(pointerPosition);
            else ResetHoverWave();
            _wheelDeltaRemainder = 0;
            SyncSmoothScrollIdleOffset();
            double rowExtent;
            if (action == OrganizerWheelAction.ScrollCompactList)
            {
                rowExtent = CompactListItemHeightDip * _definition.CompactListItemScale * ContentScale;
                _compactListScrollState = OrganizerScrollMath.ConsumeWheelDelta(
                    _compactListScrollState,
                    point.Properties.MouseWheelDelta,
                    rowExtent,
                    ItemsScrollView.ScrollableHeight);
            }
            else
            {
                Size viewport = GetItemsViewportSize();
                (_, double cellHeight) = GetItemCellSizeDip(viewport.Width, viewport.Height);
                rowExtent = cellHeight + GetItemLayoutGapDip();
                _gridScrollState = OrganizerScrollMath.ConsumeWheelDelta(
                    _gridScrollState,
                    point.Properties.MouseWheelDelta,
                    rowExtent,
                    ItemsScrollView.ScrollableHeight);
            }
            RequestSmoothScroll();
            return;
        }

        _gridScrollState = _gridScrollState with { Remainder = 0 };
        _compactListScrollState = _compactListScrollState with { Remainder = 0 };
        _wheelDeltaRemainder += point.Properties.MouseWheelDelta;
        int steps = _wheelDeltaRemainder / 120;
        _wheelDeltaRemainder %= 120;
        if (steps == 0) return;

        if (action == OrganizerWheelAction.ScaleCompactList)
        {
            double nextCompactListScale = OrganizerInteractionMath.ApplyWheelSteps(
                _definition.CompactListItemScale,
                steps,
                .5,
                1.65);
            if (Math.Abs(nextCompactListScale - _definition.CompactListItemScale) < .0001) return;
            _definition.CompactListItemScale = nextCompactListScale;
            ApplyDefinition(OrganizerVisualChange.CompactListItemScale);
            _interactionSaveTimer.Stop();
            _interactionSaveTimer.Start();
            return;
        }

        double maximum = _definition.PlacementMode == OrganizerPlacementMode.Station
            ? DisplayPlacementService.CalculateMaximumStationItemScale(
                DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice),
                _definition.Layout)
            : DisplayPlacementService.CalculateMaximumItemScaleForExpandedSize(
                _definition.Layout,
                Math.Max(1, ExpandedPanel.ActualWidth / ContentScale),
                Math.Max(1, ExpandedPanel.ActualHeight / ContentScale));
        double next = OrganizerInteractionMath.ApplyWheelSteps(_definition.ItemScale, steps, .5, maximum);
        if (Math.Abs(next - _definition.ItemScale) < .0001) return;
        _definition.ItemScale = next;
        ApplyDefinition(OrganizerVisualChange.ItemScale);
        _interactionSaveTimer.Stop();
        _interactionSaveTimer.Start();
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
        double factor = OrganizerInteractionMath.CalculateResizeFactor(
            session.Edge,
            cursor.X - session.StartCursor.X,
            cursor.Y - session.StartCursor.Y,
            session.StartBounds.Width,
            startPanelHeightPx);
        double canvasScale = Math.Clamp(
            session.StartCanvasScale * factor,
            session.MinimumCanvasScale,
            session.MaximumCanvasScale);
        int centerX = session.StartBounds.Left + session.StartBounds.Width / 2;
        int centerY = session.StartBounds.Top + titleHeightPx + startPanelHeightPx / 2;
        (int left, int top, int width, int height) = OrganizerInteractionMath.CreateCenteredBounds(
            centerX,
            centerY,
            session.BaseWidthDip * canvasScale * session.DisplayScale,
            session.BaseHeightDip * canvasScale * session.DisplayScale);
        var bounds = new NativeMethods.RECT
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
        bool unchangedCanvas = RectsEqual(bounds, _dragCurrentBounds);
        if (unchangedCanvas) return;
        double previousFit = _contentDisplayFit;
        double previousStoredScale = session.CompactList ? _definition.CompactListContentScale : _definition.IconContentScale;
        _contentDisplayFit = 1;
        double nextScale = session.StartContentScale * canvasScale;
        if (session.CompactList) _definition.CompactListContentScale = nextScale;
        else _definition.IconContentScale = nextScale;
        // SizeChanged may run inside SetWindowPos: expose matching metrics first.
        ApplyExpandedContentInset();
        if (!ApplyCanvasResizeBounds(bounds, alignment))
        {
            if (session.CompactList) _definition.CompactListContentScale = previousStoredScale;
            else _definition.IconContentScale = previousStoredScale;
            _contentDisplayFit = previousFit;
            ApplyExpandedContentInset();
            return;
        }
        if (session.CompactList)
        {
            _definition.CompactListCanvasWidthDip = width / session.DisplayScale;
            _definition.CompactListCanvasHeightDip = height / session.DisplayScale;
        }
        else
        {
            _definition.ManualCanvasBaseWidthDip = width / session.DisplayScale / _definition.CanvasScale;
            _definition.ManualCanvasBaseHeightDip = height / session.DisplayScale / _definition.CanvasScale;
        }
        ConfigureItemsLayout();
        UpdateCanvasResizeEdgeWindows(show: true);
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
            ClearWindowAlignment();
            if (NativeMethods.GetCapture() == _hwnd) _ = NativeMethods.ReleaseCapture();
            RefreshPerformanceSettings();
        }
        ConfigureItemsLayout();
        UpdateCompactPreviewItemScale();
        UpdateSurfaceClips();
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
            Vector3.Distance(ExpandedContentLayer.Translation, Vector3.Zero) <= .001f;
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

    private void QueueCanvasResize(NativeMethods.POINT cursor)
    {
        _pendingCanvasResizeCursor = cursor;
        _hasPendingCanvasResizeCursor = true;
        if (_canvasResizeCommitQueued) return;
        _canvasResizeCommitQueued = true;
        long version = _resizeGesture.Version;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High,
                () => { if (!_closing && version == _resizeGesture.Version) ProcessQueuedCanvasResize(); }))
        {
            _canvasResizeCommitQueued = false;
        }
    }

    private void ProcessQueuedCanvasResize()
    {
        _canvasResizeCommitQueued = false;
        if (_canvasResize is null) return;
        if (!_hasPendingCanvasResizeCursor) return;
        NativeMethods.POINT cursor = _pendingCanvasResizeCursor;
        _hasPendingCanvasResizeCursor = false;
        CommitCanvasResize(cursor);
    }

    private void StopCanvasResizeRendering()
    {
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
        if (IsDock) return CanvasResizeEdge.None;
        if (HasHoverPresentation && TryScreenToHoverViewport(cursor, out Point hoverPoint) &&
            HoverItemAtViewportPoint(hoverPoint) is not null) return CanvasResizeEdge.None;
        if (_definition.PlacementMode == OrganizerPlacementMode.Station || !_expanded || _animating ||
            _itemReorderSession is not null || _shellDragActive) return CanvasResizeEdge.None;
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        Point point = new((cursor.X - bounds.Left) / scale, (cursor.Y - bounds.Top) / scale);
        return IsCompactList
            ? CanvasResizeHitZones.HitTest(point.X, point.Y, bounds.Width / scale, bounds.Height / scale, compactList: true)
            : GetCanvasResizeEdge(point, bounds.Width / scale, bounds.Height / scale);
    }

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

    // Hide only the visual subtree: the transparent button retains its original hit area.
    internal void RefreshCollapseIndicator() =>
        CollapseButtonSurface.Opacity = _host.State.GlobalSettings.HideCollapseIndicator ? 0 : 1;

    private async void CollapseButton_Click(object sender, RoutedEventArgs e) =>
        await RunSafelyAsync(CollapseAsync, "收起窗口失败");

    private void CollapseButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        CollapseButtonSurface.Background = _collapseHoverBrush;
        CollapseDash.Opacity = 1;
    }

    private void CollapseButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        CollapseDash.CenterPoint = new Vector3(6, 1, 0);
        CollapseDash.Scale = UseCustomAnimations ? new Vector3(.92f, .92f, 1) : Vector3.One;
        CollapseButtonSurface.Background = _collapsePressedBrush;
    }

    private void CollapseButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CollapseDash.Scale = Vector3.One;
        CollapseButtonSurface.Background = CollapseButton.IsPointerOver ? _collapseHoverBrush : _transparentItemBrush;
        CollapseDash.Opacity = CollapseButton.IsPointerOver ? 1 : .78;
    }

    private void CollapseButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        CollapseDash.Scale = Vector3.One;
        CollapseDash.Opacity = .78;
        CollapseButtonSurface.Background = _transparentItemBrush;
    }

    private void CompactTile_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressActive || e.Pointer.PointerId != _pressedPointerId)
        {
            return;
        }

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            return;
        }

        if (!_widgetDragging)
        {
            Point current = e.GetCurrentPoint(CompactTile).Position;
            _ = _longPressGesture.Move(current.X, current.Y);
            return;
        }

        _dragInputCount++;
        QueueWidgetDragFromCursor();
        e.Handled = true;
    }

    private IntPtr GestureWindowProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, IntPtr referenceData)
    {
        if (message is 0x02E0 or 0x007E) // WM_DPICHANGED / WM_DISPLAYCHANGE
            ResetHoverWave();
        if (IsDock && message is 0x02E0 or 0x007E) // WM_DPICHANGED / WM_DISPLAYCHANGE
            DispatcherQueue.TryEnqueue(() => { if (!_closing) { _dockRegionKey = null; ApplyDockLayout(); } });
        // The station resolves its display once and then keeps it in _stationDisplay. After a
        // topology change that cached rectangle can point at a monitor that no longer exists, so
        // the hot zone never matches again. Drop it and let the pointer timer resolve it afresh.
        if (!IsDock && _definition.PlacementMode == OrganizerPlacementMode.Station && message is 0x02E0 or 0x007E)
            _stationDisplay = null;
        if (!IsDock && _definition.PlacementMode != OrganizerPlacementMode.Station && message is 0x02E0 or 0x007E)
            DispatcherQueue.TryEnqueue(ScheduleDisplayChangeReconciliation);
        if (message == NativeMethods.WM_MOUSEMOVE && _shellDragActive)
        {
            DragMessageRelay.Complete(ref _itemDragOleMouseMovePending);
        }
        if (TryHandleCanvasResizeWindowMessage(hWnd, message, wParam, out IntPtr resizeResult)) return resizeResult;
        if (message == NativeMethods.WM_APP_START_ITEM_EXTERNAL_DRAG &&
            _itemReorderSession is { IsActive: true, NativeDragStarted: false } externalSession &&
            externalSession.SourceIndex >= 0 && externalSession.SourceIndex < _items.Count &&
            ShellDragService.RequiresNativeDrag(_items[externalSession.SourceIndex].Kind))
        {
            externalSession.StartNativeDrag();
            StartMouseNativeDrag(externalSession);
            return IntPtr.Zero;
        }
        if (_nativeMouseCapture && _pressActive && message == NativeMethods.WM_MOUSEMOVE)
        {
            if (NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
            {
                if (_widgetMousePress) TryStartMouseWidgetDrag();
                if (!_widgetDragging && !_widgetMousePress)
                {
                    double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
                    _ = _longPressGesture.Move(
                        _pressPointDip.X + (cursor.X - _pressCursorPx.X) / scale,
                        _pressPointDip.Y + (cursor.Y - _pressCursorPx.Y) / scale);
                }
                else
                {
                    _dragInputCount++;
                    if (!_dragClockBoosted)
                    {
                        QueueWidgetDragCursor(cursor);
                    }
                }
            }
        }
        else if (_nativeMouseCapture && _pressActive && message == NativeMethods.WM_LBUTTONUP)
        {
            QueueCompactPressCompletion(allowOpen: true);
        }
        else if (_nativeMouseCapture && _pressActive && message == NativeMethods.WM_CAPTURECHANGED)
        {
            QueueCompactPressCompletion(allowOpen: false);
        }
        return NativeMethods.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private IntPtr CanvasResizeWindowProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (TryHandleCanvasResizeWindowMessage(hWnd, message, wParam, out IntPtr result)) return result;
        return _canvasResizeOriginalWindowProcs.TryGetValue(hWnd, out IntPtr previous)
            ? NativeMethods.CallWindowProc(previous, hWnd, message, wParam, lParam)
            : IntPtr.Zero;
    }

    private bool TryHandleCanvasResizeWindowMessage(IntPtr hWnd, uint message, UIntPtr wParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (message == NativeMethods.WM_NCHITTEST && NativeMethods.GetCursorPos(out NativeMethods.POINT hitCursor))
        {
            if (hWnd != _hwnd)
            {
                result = new IntPtr(NativeMethods.HTCLIENT);
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

    private void UpdateWidgetDragFromCursor()
    {
        if (!_widgetDragging || !NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)) return;
        CommitWidgetDrag(cursor);
    }

    private void QueueWidgetDragFromCursor()
    {
        if (_widgetDragging && NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
            QueueWidgetDragCursor(cursor);
    }

    private void QueueWidgetDragCursor(NativeMethods.POINT cursor)
    {
        _pendingWidgetDragCursor = cursor;
        _hasPendingWidgetDragCursor = true;
    }

    private void DragRendering(object? sender, object args)
    {
        _dragRenderTickCount++;
        TryStartMouseWidgetDrag();
        if (!_widgetMousePress && _pressActive && !_widgetDragging &&
            Stopwatch.GetElapsedTime(_pressStartedAt).TotalMilliseconds >= LongPressMs)
        {
            TryStartWidgetDrag();
        }
        if (_widgetDragging)
        {
            NativeMethods.POINT cursor;
            if (_hasPendingWidgetDragCursor) cursor = _pendingWidgetDragCursor;
            else if (!NativeMethods.GetCursorPos(out cursor)) return;
            _hasPendingWidgetDragCursor = false;
            CommitWidgetDrag(cursor);
        }
    }

    private void CommitWidgetDrag(NativeMethods.POINT cursor)
    {
        if (_hasLastDragCursor && cursor.X == _lastDragCursor.X && cursor.Y == _lastDragCursor.Y)
        {
            return;
        }
        _lastDragCursor = cursor;
        _hasLastDragCursor = true;
        if (!_draggingExpanded) _host.UpdateOrganizerDragHover(this, cursor);
        var desired = new NativeMethods.RECT
        {
            Left = _pressWindowBounds.Left + cursor.X - _pressCursorPx.X,
            Top = _pressWindowBounds.Top + cursor.Y - _pressCursorPx.Y,
            Right = _pressWindowBounds.Right + cursor.X - _pressCursorPx.X,
            Bottom = _pressWindowBounds.Bottom + cursor.Y - _pressCursorPx.Y
        };
        DisplayInfo display = _dragDisplay ?? DisplayPlacementService.ForBounds(desired);
        bool overOrganizerDropTarget = !_draggingExpanded && (OrganizerInteractionMath.ShouldUseWindowAlignment(
                _host.State.GlobalSettings.WindowAlignmentEnabled,
                _draggingExpanded,
                _definition.PlacementMode,
                overOrganizerDropTarget: false) &&
            _host.IsOrganizerContainerDropTarget(this, cursor) ||
            _host.IsOrganizerDragHoverTarget(this, cursor));
        WindowAlignmentInsets? alignmentInsets = OrganizerInteractionMath.ShouldUseWindowAlignment(
            _host.State.GlobalSettings.WindowAlignmentEnabled,
            _draggingExpanded,
            _definition.PlacementMode,
            overOrganizerDropTarget,
            IsPermanentlyExpanded)
                ? _dragAlignmentInsets
                : null;
        NativeMethods.RECT nextBounds;
        if (_definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            nextBounds = DisplayPlacementService.CalculateStationDraggedBounds(
                _pressWindowBounds,
                _pressCursorPx,
                cursor,
                display,
                _definition.DockEdge);
        }
        else if (IsDock) nextBounds = desired;
        else
        {
            int centerX = desired.Left + desired.Width / 2;
            int centerY = desired.Top + desired.Height / 2;
            if (centerX < display.Monitor.Left || centerX >= display.Monitor.Right ||
                centerY < display.Monitor.Top || centerY >= display.Monitor.Bottom)
            {
                display = DisplayPlacementService.ForBounds(desired);
                _dragDisplay = display;
                ClearWindowAlignment();
            }
            nextBounds = alignmentInsets is WindowAlignmentInsets frameInsets
                ? WindowAlignmentMath.ClampFrame(desired, display.Work, frameInsets)
                : DisplayPlacementService.CalculateDraggedBounds(_pressWindowBounds, _pressCursorPx, cursor, display.Work);
        }

        if (alignmentInsets is WindowAlignmentInsets frameInsetsForAlignment)
        {
            uint dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(_hwnd));
            WindowAlignmentResult result = WindowAlignmentMath.Align(
                frameInsetsForAlignment.ToFrame(nextBounds),
                display.Work,
                _host.GetWindowAlignmentTargets(this, display),
                WindowAlignmentMath.DipToPx(WindowAlignmentMath.SnapDistanceDip, dpi),
                WindowAlignmentMath.DipToPx(WindowAlignmentMath.ReleaseDistanceDip, dpi),
                _windowAlignmentState);
            _windowAlignmentState = result.State;
            nextBounds = frameInsetsForAlignment.ToWindow(result.Bounds);
            _windowAlignmentGuide ??= new WindowAlignmentGuideOverlay(_hwnd);
            _windowAlignmentGuide.Show(result.XGuide, result.YGuide, dpi);
        }
        else
        {
            ClearWindowAlignment();
        }

        NativeMethods.RECT currentBounds = _draggingExpanded ? _dragCurrentBounds : _compactBounds;
        if (RectsEqual(nextBounds, currentBounds))
        {
            return;
        }
        if (!NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            nextBounds.Left,
            nextBounds.Top - (IsDock ? _dockOverflowPixels : 0),
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_SHOWWINDOW))
        {
            AppLogger.Error($"拖动收纳窗失败，Win32={Marshal.GetLastWin32Error()}。");
            ClearWindowAlignment();
            return;
        }
        if (_draggingExpanded)
        {
            _dragCurrentBounds = nextBounds;
        }
        else
        {
            _compactBounds = nextBounds;
            _host.UpdateMultiSelectedGroupDrag(this, nextBounds.Left - _pressWindowBounds.Left, nextBounds.Top - _pressWindowBounds.Top);
        }
        _dragCommitCount++;
    }

    private async void CompactTile_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressActive || e.Pointer.PointerId != _pressedPointerId)
        {
            return;
        }

        await RunSafelyAsync(() => FinishCompactPressAsync(allowOpen: true), "收起窗口拖动结束失败");
        e.Handled = true;
    }

    private async void CompactTile_PointerCanceled(object sender, PointerRoutedEventArgs e) =>
        await RunSafelyAsync(() => FinishCompactPressAsync(allowOpen: false), "收起窗口拖动取消失败");

    private async void CompactTile_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_pressActive && !_nativeMouseCapture)
        {
            await RunSafelyAsync(() => FinishCompactPressAsync(allowOpen: false), "收起窗口捕获丢失处理失败");
        }
    }

    private void LongPressTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        => TryStartWidgetDrag();

    private void TryStartWidgetDrag()
    {
        if (_pressActive && !_widgetDragging && _longPressGesture.Elapse() == LongPressResult.StartDrag)
        {
            _widgetDragging = true;
            _desktopCoordinator.PauseForWidgetDrag();
            if (!_draggingExpanded && !IsContained && _definition.PlacementMode != OrganizerPlacementMode.Station)
            {
                _desktopLayer?.SetExpanded(true, stayTopmost: true);
                _widgetDragTopmost = true;
                _host.BeginMultiSelectedGroupDrag(this);
            }
            _dragStartedAt = Stopwatch.GetTimestamp();
            _dragInputCount = 0;
            _dragCommitCount = 0;
            _dragRenderTickCount = 0;
            _hasLastDragCursor = false;
            UpdateWidgetDragFromCursor();
        }
    }

    private void QueueCompactPressCompletion(bool allowOpen)
    {
        long version = _widgetGesture.Version;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closing) _ = RunSafelyAsync(() => FinishCompactPressAsync(allowOpen, version), "拖动结束失败");
        });
    }

    private Task FinishCompactPressAsync(bool allowOpen, long? version = null) =>
        _widgetGesture.CompleteAsync(version ?? _widgetGesture.Version, async () =>
        {
            try { await FinishCompactPressCoreAsync(allowOpen); }
            finally
            {
                _host.EndOrganizerDragHover(this);
                _host.CancelMultiSelectedGroupDrag(this);
                if (!_closing && (_pressActive || _nativeMouseCapture)) ResetCompactPress();
            }
        });

    private async Task FinishCompactPressCoreAsync(bool allowOpen)
    {
        if (!_pressActive) return;
        if (allowOpen) TryStartMouseWidgetDrag();
        LongPressResult releaseResult = _longPressGesture.Release();
        bool wasDragging = releaseResult == LongPressResult.FinishDrag;
        bool wasExpandedDrag = _draggingExpanded;
        bool canOpen = allowOpen && releaseResult == LongPressResult.Open;
        bool editTitle = canOpen && _pressedOrganizerTitle && !wasDragging;
        int offeredFrames = 0;
        int committedFrames = 0;
        int renderTicks = 0;
        bool clockBoosted = false;
        double dragDurationMs = 0;
        if (wasDragging)
        {
            UpdateWidgetDragFromCursor();
            offeredFrames = _dragInputCount;
            committedFrames = _dragCommitCount;
            renderTicks = _dragRenderTickCount;
            clockBoosted = _dragClockBoosted;
            dragDurationMs = Stopwatch.GetElapsedTime(_dragStartedAt).TotalMilliseconds;
        }
        ResetCompactPress();
        if (editTitle) { BeginOrganizerTitleEdit(); return; }
        if (IsDock && !wasDragging) ApplyDockLayout();
        if (wasDragging)
        {
            await _host.WaitForOrganizerDragHoverAsync(this);
            if (_closing) return;
            AppLogger.Performance($"组件拖动 {dragDurationMs:0}ms，鼠标消息 {offeredFrames} 次，合成节拍 {renderTicks} 次，移动提交 {committedFrames} 次，时钟保活={clockBoosted}。");
            if (!wasExpandedDrag && NativeMethods.GetCursorPos(out NativeMethods.POINT dropPoint))
            {
                try
                {
                    if (await _host.TryContainDraggedOrganizerAsync(this, dropPoint))
                    {
                        _desktopLayer?.Reattach();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("收纳窗拖入容器失败。", ex);
                    _compactBounds = _definition.PlacementMode == OrganizerPlacementMode.Positioned
                        ? _positionedDragOriginBounds
                        : _pressWindowBounds;
                    ApplyBounds(_compactBounds, show: true);
                    ShowMessage(AppStrings.Format("OrganizerContainerDropError", ex.Message), InfoBarSeverity.Error);
                    _desktopLayer?.Reattach();
                    return;
                }
            }
            if (IsDock)
            {
                await SaveDockCenterAsync();
            }
            else if (wasExpandedDrag && ShouldRememberExpandedPosition())
            {
                CaptureExpandedPosition();
                await SaveStateAsync();
            }
            else if (_definition.PlacementMode == OrganizerPlacementMode.Station && wasExpandedDrag)
            {
                DisplayInfo display = DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
                _stationDisplay = display;
                _definition.Position = DisplayPlacementService.CaptureStationPosition(display, _definition.DockEdge, _dragCurrentBounds);
                _compactBounds = DisplayPlacementService.CalculateStationAnchor(display, _definition.DockEdge, _definition.Position);
                await SaveStateAsync();
                ResetStationPointerDelay();
            }
            else if (_definition.PlacementMode == OrganizerPlacementMode.Positioned)
            {
                if (!wasExpandedDrag)
                {
                    DesktopGridPlacement? placement = _host.FindNearestPositionedPlacement(_definition.Id, _compactBounds);
                    if (placement is null)
                    {
                        _compactBounds = _positionedDragOriginBounds;
                        ApplyBounds(_compactBounds, show: true);
                        ShowMessage(AppStrings.Get("DragReturnToGrid"), InfoBarSeverity.Warning);
                    }
                    else
                    {
                        MoveToPositionedPlacement(placement.Bounds, placement.CompactScale);
                        _definition.Position = DisplayPlacementService.Capture(_compactBounds, _hwnd);
                        await SaveStateAsync();
                    }
                }
            }
            else if (!wasExpandedDrag)
            {
                if (_definition.PlacementMode == OrganizerPlacementMode.Floating &&
                    _host.State.GlobalSettings.SnapFloatingToDesktopGrid)
                {
                    _compactBounds = _host.SnapSingleFloatingToDesktopGrid(this, _compactBounds);
                    ApplyBounds(_compactBounds, show: true);
                }
                _definition.Position = DisplayPlacementService.Capture(_compactBounds, _hwnd);
                await SaveStateAsync();
                await _host.CommitMultiSelectedGroupDragAsync(this);
            }
            _desktopLayer?.Reattach();
        }
        else if (canOpen)
        {
            try
            {
                await ExpandAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Expand from compact pointer failed", ex);
                _expanded = false;
                _animating = false;
                _host.NotifyDockOpenStateChanged();
            }
        }
    }

    private void ResetCompactPress()
    {
        _pressedOrganizerTitle = false;
        _widgetMousePress = false;
        _longPressTimer.Stop();
        _longPressGesture.Reset();
        _pressActive = false;
        StopDragClock();
        if (_pressedPointerId != 0)
        {
            _pressedPointerId = 0;
            if (_draggingExpanded) ExpandedView.ReleasePointerCaptures();
            else CompactTile.ReleasePointerCaptures();
        }
        if (_nativeMouseCapture)
        {
            _nativeMouseCapture = false;
            if (NativeMethods.GetCapture() == _hwnd) _ = NativeMethods.ReleaseCapture();
        }
        _pressedPointerId = 0;
        _widgetDragging = false;
        _hasPendingWidgetDragCursor = false;
        if (_widgetDragTopmost)
        {
            _desktopLayer?.SetExpanded(false, showWindow: false);
            _widgetDragTopmost = false;
        }
        _draggingExpanded = false;
        _dragDisplay = null;
        _dragAlignmentInsets = null;
        ClearWindowAlignment();
        _pressStartedAt = 0;
        _hasLastDragCursor = false;
        RefreshPerformanceSettings();
    }

    private void ClearWindowAlignment()
    {
        _windowAlignmentState = default;
        _windowAlignmentGuide?.Hide();
    }
}
