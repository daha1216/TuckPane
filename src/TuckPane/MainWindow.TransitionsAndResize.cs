using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
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
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Hosting;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;

namespace TuckPane;

public sealed partial class MainWindow
{
    private const float ExpandSpringDampingRatio = 0.80f;
    private const int ExpandSpringPeriodMs = 44;
    private const float CollapseSpringDampingRatio = 0.94f;
    private const int CollapseSpringPeriodMs = 30;
    private const float StationSpringDampingRatio = 0.94f;
    private const int StationSpringPeriodMs = 40;
    private const int ReducedMotionTransitionDurationMs = 140;
    private const int ExpandBatchSafetyTimeoutMs = 480;
    private const int CollapseBatchSafetyTimeoutMs = 340;
    private const int StationBatchSafetyTimeoutMs = 380;
    private const int PreWarmFrameTimeoutMs = 50;
    private const int AtomicRelocationFrameTimeoutMs = 48;

    // 100% DWM GPU Compositor transition resources (reused across every expand/collapse, zero per-frame allocations).
    private CompositionPropertySet? _transitionProps;
    private bool _transitionVisualTranslationEnabled;
    private Visual? _compactCompositionVisual;
    private Visual? _expandedSurfaceCompositionVisual;
    private Visual? _expandedSheenCompositionVisual;
    private Visual? _expandedEdgeCompositionVisual;
    private Visual? _expandedContentCompositionVisual;
    private Visual? _expandedNameCompositionVisual;
    private Visual? _expandedNameShadowCompositionVisual;

    private ExpressionAnimation? _expandedScaleExpr;
    private ExpressionAnimation? _expandedTranslationExpr;
    private ExpressionAnimation? _expandedOpacityExpr;
    private ExpressionAnimation? _surfaceOpacityExpr;
    private ExpressionAnimation? _contentOpacityExpr;
    private ExpressionAnimation? _contentTranslationExpr;
    private ExpressionAnimation? _compactOpacityExpr;
    private ExpressionAnimation? _compactScaleExpr;
    private ExpressionAnimation? _stationOpacityExpr;
    private ExpressionAnimation? _stationClipEdgeExpr;
    private ExpressionAnimation? _stationContentTranslationExpr;

    private SpringScalarNaturalMotionAnimation? _expandProgressSpring;
    private SpringScalarNaturalMotionAnimation? _collapseProgressSpring;
    private SpringScalarNaturalMotionAnimation? _stationProgressSpring;
    private ScalarKeyFrameAnimation? _reducedMotionProgressAnim;
    private CubicBezierEasingFunction? _reducedMotionEaseOut;

    private TransitionCanvasPlan? _activeTransitionCanvas;
    private TransitionBridgeOverlay? _transitionBridge;
    private bool _transitionWindowCloaked;
    private bool _dwmTransitionRunning;
    private bool _dwmTransitionExpressionsBound;
    private bool _dwmTransitionStationBound;
    private bool _dwmTransitionUsesExplicitExpandedLayout;
    private bool _transitionClosingHooked;
    private long _lastHoverIconPreWarmAt;
    private bool _hoverIconPreWarmRunning;

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
        bool isStation = _definition.PlacementMode == OrganizerPlacementMode.Station;
        if (isStation)
        {
            if (!_stationReady) { ReleaseTransitionLock(requestVersion); return; }
            try { await (_stationContentTask ??= PrepareStationContentAsync()); }
            catch { _stationContentTask = null; throw; }
            if (!IsCurrent() || !_stationVisible) { ReleaseTransitionLock(requestVersion); return; }
        }
        await _host.PrepareToExpandAsync(this, IsCurrent);
        if (!IsCurrent() || isStation && !_stationVisible) { ReleaseTransitionLock(requestVersion); return; }
        bool contained = IsContained;
        NativeMethods.RECT compactOrigin = contained ? _containedAnchorBounds : _compactBounds;
        WidgetPosition? rememberedPosition = ShouldRememberExpandedPosition()
            ? _definition.ExpandedPosition
            : null;
        DisplayInfo targetDisplay = rememberedPosition is not null
            ? DisplayPlacementService.GetDisplay(rememberedPosition.MonitorDevice)
            : (isStation
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
        bool reducedMotion = !UseCustomAnimations;
        bool continuingCanvas = !reducedMotion && !isStation &&
            _activeTransitionCanvas is { } existingCanvas &&
            RectsEqual(existingCanvas.CompactOrigin, compactOrigin) &&
            _lastTransitionWindowLeft == existingCanvas.CanvasBounds.Left &&
            _lastTransitionWindowTop == existingCanvas.CanvasBounds.Top;
        bool continuingDwmFlight = continuingCanvas && _dwmTransitionRunning;
        bool continuingStationFlight = !reducedMotion && isStation && _dwmTransitionRunning && _dwmTransitionStationBound;
        CancellationTokenSource transition = StartTransition(modeCancellation);
        _expanded = true;
        _animating = true;
        try
        {
            RefreshPerformanceSettings();
            if (!isStation && !contained) _desktopLayer?.SetExpanded(true);

            NativeMethods.RECT currentBounds = compactOrigin;
            if (!isStation && !contained && !continuingCanvas &&
                !TryGetLogicalWindowRect(out currentBounds))
            {
                currentBounds = compactOrigin;
            }
            if (!isStation && !contained && !continuingCanvas &&
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

            if (continuingCanvas) expandedBounds = _activeTransitionCanvas!.Value.ExpandedBounds;

            if (isStation)
            {
                PrepareTransitionAnchor(expandedBounds, compactOrigin);
                _collapseTransitionGeometry = null;
                _activeTransitionCanvas = null;
                RestoreExpandedViewDefaultLayout();
                trace?.Advance(AnimationTraceStage.Layout);
                ExpandedView.Visibility = Visibility.Visible;
                CompactView.Visibility = Visibility.Collapsed;
                if (!continuingStationFlight)
                {
                    ExpandedView.Opacity = 1;
                    ResetExpandedTransitionOpacities();
                    Visual expandedVisual = GetExpandedCompositionVisual();
                    expandedVisual.IsVisible = false;
                    _desktopLayer?.SetExpanded(true, stayTopmost: true);
                    _host.RaiseActiveCompactOrganizerDrags(this);
                    ApplyBounds(expandedBounds, show: true);
                    _lastTransitionWindowLeft = expandedBounds.Left;
                    _lastTransitionWindowTop = expandedBounds.Top;
                    ConfigureItemsLayout();
                    WindowRoot.UpdateLayout();
                    UpdateSurfaceClips();
                    await WaitForNextRenderedAsync(transition.Token, PreWarmFrameTimeoutMs);
                    if (!IsCurrent() || transition.IsCancellationRequested) return;
                }
                else
                {
                    _desktopLayer?.SetExpanded(true, stayTopmost: true);
                    _host.RaiseActiveCompactOrganizerDrags(this);
                }
            }
            else
            {
                TransitionCanvasPlan plan = continuingCanvas
                    ? _activeTransitionCanvas!.Value
                    : BuildTransitionCanvasPlan(expandedBounds, compactOrigin, contained, reducedMotion);
                _activeTransitionCanvas = plan;
                _collapseTransitionGeometry = plan.IsUnionNeeded
                    ? CreateCollapseTransitionGeometry(expandedBounds, compactOrigin)
                    : null;
                PrepareTransitionAnchor(expandedBounds, compactOrigin, handoffAtCompactOrigin: plan.IsUnionNeeded);
                trace?.Advance(AnimationTraceStage.Layout);

                if (!continuingDwmFlight)
                {
                    EnsureTransitionVisualTranslationEnabled();
                    Visual expandedVisual = GetExpandedCompositionVisual();
                    Visual compactVisual = GetCompactCompositionVisual();
                    bool showCompactDuringTransition = !contained && (!reducedMotion || ContainsRect(expandedBounds, compactOrigin));
                    // Cloak the HWND under a 1:1 frozen screen snapshot before relocating/resizing to
                    // CanvasBounds so DWM never flashes CompactView at the canvas top-left for 1 frame.
                    bool shouldCloakForCanvasRelocation = !contained && _runtimeVisible && !RectsEqual(compactOrigin, plan.CanvasBounds);
                    if (shouldCloakForCanvasRelocation)
                    {
                        CloakForAtomicRelocation(compactOrigin, topmost: false);
                    }

                    ExpandedView.Visibility = Visibility.Visible;
                    ExpandedView.Opacity = 1;
                    ResetExpandedTransitionOpacities();
                    // Keep invisible during the 1-frame pre-warm while XAML realizes items, warms
                    // the Acrylic swapchain, and flushes dirty UIElement.Opacity = 1 flags.
                    expandedVisual.IsVisible = false;

                    CompactView.Visibility = showCompactDuringTransition ? Visibility.Visible : Visibility.Collapsed;
                    CompactView.Opacity = 1;
                    compactVisual.StopAnimation("Opacity");
                    compactVisual.StopAnimation("Scale");
                    compactVisual.Opacity = showCompactDuringTransition ? 1f : 0f;
                    compactVisual.Scale = Vector3.One;
                    compactVisual.CenterPoint = plan.CompactCenterPointDip;
                    compactVisual.IsVisible = true;
                    SetCompactVisualTranslation(showCompactDuringTransition ? GetPixelSnappedCompactTranslation(plan) : Vector3.Zero);

                    if (plan.IsUnionNeeded)
                    {
                        ApplyExpandedViewCanvasLayout(plan);
                    }
                    else
                    {
                        RestoreExpandedViewDefaultLayout();
                    }

                    if (contained)
                    {
                        _desktopLayer?.SetExpanded(true, stayTopmost: true);
                        ApplyBounds(plan.CanvasBounds, show: true);
                    }
                    else
                    {
                        ApplyBounds(plan.CanvasBounds, show: _runtimeVisible, preserveZOrder: shouldCloakForCanvasRelocation);
                    }
                    _lastTransitionWindowLeft = plan.CanvasBounds.Left;
                    _lastTransitionWindowTop = plan.CanvasBounds.Top;

                    ConfigureItemsLayout();
                    WindowRoot.UpdateLayout();
                    UpdateSurfaceClips();
                    await WaitForNextRenderedAsync(transition.Token, PreWarmFrameTimeoutMs);
                    if (!IsCurrent() || transition.IsCancellationRequested) return;
                }
            }

            trace?.Advance(AnimationTraceStage.Animation);
            await RunVisualTransitionAsync(1, transition.Token);
            if (!IsCurrent() || transition.IsCancellationRequested) return;

            if (_activeTransitionCanvas is { IsUnionNeeded: true } expandedUnionPlan &&
                expandedUnionPlan.ExpandedOffsetDip.LengthSquared() > 0.25f)
            {
                trace?.Advance(AnimationTraceStage.Handoff);
                bool canCloak = _runtimeVisible || contained;
                if (canCloak)
                {
                    CloakForAtomicRelocation(expandedUnionPlan.ExpandedBounds, topmost: contained);
                }

                _dwmTransitionRunning = false;
                _activeTransitionCanvas = null;
                _collapseTransitionGeometry = null;
                StopTransitionCompositionAnimations();
                ClearExpandedTransitionSweep();
                CompactView.Visibility = Visibility.Collapsed;
                CompactView.Opacity = 1;
                ResetCompactVisualToRest();
                RestoreExpandedViewDefaultLayout();
                ExpandedView.Opacity = 1;
                ResetExpandedTransitionOpacities();
                ResetExpandedVisualToRest(opacity: 1f);
                ApplyBounds(expandedUnionPlan.ExpandedBounds, show: _runtimeVisible || contained, preserveZOrder: true);
                _lastTransitionWindowLeft = expandedUnionPlan.ExpandedBounds.Left;
                _lastTransitionWindowTop = expandedUnionPlan.ExpandedBounds.Top;
                _animationTrace?.Metrics.RecordWork(AnimationTraceWork.UpdateLayout);
                WindowRoot.UpdateLayout();
                UpdateSurfaceClips();

                if (canCloak)
                {
                    await WaitForNextRenderedAsync(transition.Token, AtomicRelocationFrameTimeoutMs);
                    if (!IsCurrent() || transition.IsCancellationRequested) return;
                    UncloakAfterAtomicRelocation(flushDwm: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (trace is not null) trace.Outcome = "cancelled";
            if (requestVersion == _transitionRequestVersion && !_closing)
            {
                CleanupAbortedTransitionVisuals();
            }
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
                TransitionCanvasPlan? settledPlan = _activeTransitionCanvas;
                _animating = false;
                _dwmTransitionRunning = false;
                _activeTransitionCanvas = null;
                _collapseTransitionGeometry = null;
                StopTransitionCompositionAnimations();
                ClearExpandedTransitionSweep();
                _host.NotifyDockOpenStateChanged();
                CompactView.Visibility = Visibility.Collapsed;
                CompactView.Opacity = 1;
                ResetCompactVisualToRest();
                RestoreExpandedViewDefaultLayout();
                ExpandedView.Opacity = 1;
                ResetExpandedTransitionOpacities();
                ResetExpandedVisualToRest(opacity: 1f);
                if (_stationTransitionClip is not null) ClearStationTransitionVisuals();
                if (settledPlan is { IsUnionNeeded: true } unionPlan)
                {
                    ApplyBounds(unionPlan.ExpandedBounds, show: _runtimeVisible || contained, preserveZOrder: true);
                    _lastTransitionWindowLeft = unionPlan.ExpandedBounds.Left;
                    _lastTransitionWindowTop = unionPlan.ExpandedBounds.Top;
                    _animationTrace?.Metrics.RecordWork(AnimationTraceWork.UpdateLayout);
                    WindowRoot.UpdateLayout();
                    UpdateSurfaceClips();
                }
                UpdateExpandedLiquidSheenGeometry(IsDock ? CurrentDockCornerRadius : SnapDip(ExpandedCornerRadiusDip));
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
                if (trace is not null) trace.Outcome = "completed";
            }
            if (requestVersion == _transitionRequestVersion &&
                (_transitionWindowCloaked || _transitionBridge is { IsVisible: true }))
            {
                ForceRestoreUncloakedState();
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

        bool isStation = _definition.PlacementMode == OrganizerPlacementMode.Station;
        bool contained = IsContained;
        bool reducedMotion = !UseCustomAnimations;
        NativeMethods.RECT compactTarget = contained ? _containedAnchorBounds : _compactBounds;
        bool continuingCanvas = !reducedMotion && !isStation &&
            _activeTransitionCanvas is { } existingCanvas &&
            RectsEqual(existingCanvas.CompactOrigin, compactTarget) &&
            _lastTransitionWindowLeft == existingCanvas.CanvasBounds.Left &&
            _lastTransitionWindowTop == existingCanvas.CanvasBounds.Top;
        bool continuingDwmFlight = continuingCanvas && _dwmTransitionRunning;
        bool continuingStationFlight = !reducedMotion && isStation && _dwmTransitionRunning && _dwmTransitionStationBound;
        CancellationTokenSource transition = StartTransition(modeCancellation);
        _expanded = false;
        _animating = true;
        bool compactHandoffCompleted = false;
        try
        {
            RefreshPerformanceSettings();
            _canvasResizeInputTimer.Stop();
            UpdateCanvasResizeEdgeWindows(show: false);
            _externalHoverTimer.Stop();

            if (isStation)
            {
                if (!continuingStationFlight)
                {
                    _activeTransitionCanvas = null;
                    _collapseTransitionGeometry = null;
                    CompactView.Visibility = Visibility.Collapsed;
                    ExpandedView.Visibility = Visibility.Visible;
                    ExpandedView.Opacity = 1;
                    ResetExpandedTransitionOpacities();
                }
            }
            else
            {
                TransitionCanvasPlan plan;
                if (continuingCanvas)
                {
                    plan = _activeTransitionCanvas!.Value;
                }
                else
                {
                    if (!TryGetLogicalWindowRect(out NativeMethods.RECT expandedBounds))
                        expandedBounds = CalculateExpandedBounds(compactTarget);
                    plan = BuildTransitionCanvasPlan(expandedBounds, compactTarget, contained, reducedMotion);
                    _activeTransitionCanvas = plan;
                    _collapseTransitionGeometry = plan.IsUnionNeeded
                        ? CreateCollapseTransitionGeometry(expandedBounds, compactTarget)
                        : null;
                    PrepareTransitionAnchor(expandedBounds, compactTarget, handoffAtCompactOrigin: plan.IsUnionNeeded);
                }

                if (!continuingDwmFlight)
                {
                    EnsureTransitionVisualTranslationEnabled();
                    Visual compactVisual = GetCompactCompositionVisual();
                    Visual expandedVisual = GetExpandedCompositionVisual();
                    bool showCompactDuringTransition = !contained && (!reducedMotion || ContainsRect(plan.ExpandedBounds, compactTarget));
                    if (showCompactDuringTransition && !IsCompactPreviewUpToDate())
                    {
                        await EnsureCompactPreviewReadyAsync(transition.Token);
                        if (!IsCurrent() || transition.IsCancellationRequested) return;
                    }
                    // Cloak left/top-offset union expansions before moving the HWND origin so
                    // ExpandedView never jumps up/left for 1 frame before Margin commits.
                    bool shouldCloakForCanvasRelocation = plan.IsUnionNeeded &&
                        plan.ExpandedOffsetDip.LengthSquared() > 0.25f &&
                        (_runtimeVisible || contained);
                    if (shouldCloakForCanvasRelocation)
                    {
                        CloakForAtomicRelocation(plan.ExpandedBounds, topmost: contained);
                    }
                    expandedVisual.IsVisible = true;

                    if (showCompactDuringTransition)
                    {
                        CompactView.Visibility = Visibility.Visible;
                        CompactView.Opacity = 1;
                        // Keep hidden for 1 frame while XAML commits Visibility=Visible so CompactView
                        // never flashes on top of ExpandedView before ExpressionAnimations take over.
                        compactVisual.IsVisible = false;
                        compactVisual.CenterPoint = plan.CompactCenterPointDip;
                        SetCompactVisualTranslation(GetPixelSnappedCompactTranslation(plan));
                    }
                    else
                    {
                        CompactView.Visibility = Visibility.Collapsed;
                    }

                    if (plan.IsUnionNeeded)
                    {
                        ApplyExpandedViewCanvasLayout(plan);
                        ApplyBounds(plan.CanvasBounds, show: true, preserveZOrder: true);
                        _lastTransitionWindowLeft = plan.CanvasBounds.Left;
                        _lastTransitionWindowTop = plan.CanvasBounds.Top;
                        WindowRoot.UpdateLayout();
                        UpdateSurfaceClips();
                    }
                    else
                    {
                        _lastTransitionWindowLeft = plan.CanvasBounds.Left;
                        _lastTransitionWindowTop = plan.CanvasBounds.Top;
                    }

                    if (showCompactDuringTransition || plan.IsUnionNeeded)
                    {
                        await WaitForNextRenderedAsync(transition.Token, PreWarmFrameTimeoutMs);
                        if (!IsCurrent() || transition.IsCancellationRequested) return;
                    }
                }
            }

            trace?.Advance(AnimationTraceStage.Animation);
            await RunVisualTransitionAsync(0, transition.Token);
            if (!IsCurrent() || transition.IsCancellationRequested) return;

            if (!isStation && !contained)
            {
                trace?.Advance(AnimationTraceStage.Handoff);
                bool needsAtomicHandoff = ShouldUseAtomicCompactHandoff(_activeTransitionCanvas, compactTarget);
                await CommitCompactHandoffAsync(compactTarget, needsAtomicHandoff, transition.Token);
                if (!IsCurrent() || transition.IsCancellationRequested) return;
                compactHandoffCompleted = true;
            }
        }
        catch (OperationCanceledException)
        {
            if (trace is not null) trace.Outcome = "cancelled";
            if (requestVersion == _transitionRequestVersion && !_closing)
            {
                CleanupAbortedTransitionVisuals();
            }
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
                _dwmTransitionRunning = false;
                _activeTransitionCanvas = null;
                _collapseTransitionGeometry = null;
                _host.NotifyDockOpenStateChanged();
                if (!compactHandoffCompleted)
                {
                    StopTransitionCompositionAnimations();
                    ClearExpandedTransitionSweep();
                    CompactView.Opacity = 1;
                    if (isStation)
                    {
                        RestoreExpandedViewDefaultLayout();
                        ExpandedView.Visibility = Visibility.Collapsed;
                        ExpandedView.Opacity = 0;
                        ResetExpandedTransitionOpacities();
                        ResetExpandedVisualToRest(opacity: 0f);
                        ClearStationTransitionVisuals();
                    }
                    else if (contained)
                    {
                        ResetCompactVisualToRest();
                        CompactView.Visibility = Visibility.Collapsed;
                        RestoreExpandedViewDefaultLayout();
                        ExpandedView.Visibility = Visibility.Collapsed;
                        ExpandedView.Opacity = 0;
                        ResetExpandedTransitionOpacities();
                        ResetExpandedVisualToRest(opacity: 0f);
                        _appWindow?.Hide();
                    }
                    else
                    {
                        CommitCompactHandoff(compactTarget);
                    }
                    ClearExpandedFilter(releaseInputActivation: true);
                    if (isStation || contained) _appWindow?.Hide();
                    _desktopLayer?.SetExpanded(false, showWindow: false);
                }
                CancelItemMarquee();
                ClearItemSelection();
                _keyboardFocusedIndex = -1;
                RefreshPerformanceSettings();
                _host.NotifyCollapsed(this);
                if (trace is not null) trace.Outcome = "completed";
            }
            if (requestVersion == _transitionRequestVersion &&
                (_transitionWindowCloaked || _transitionBridge is { IsVisible: true }))
            {
                ForceRestoreUncloakedState();
            }
        }
    }

    private void CloakForAtomicRelocation(NativeMethods.RECT? bridgeRect, bool topmost = false)
    {
        if (_closing || _hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd)) return;
        bool bridgeShown = false;
        if (bridgeRect is { Width: > 0, Height: > 0 } rect)
        {
            _transitionBridge ??= new TransitionBridgeOverlay();
            bridgeShown = _transitionBridge.CaptureAndShow(rect, _hwnd, topmost);
        }
        if (!_transitionWindowCloaked)
        {
            if (NativeMethods.SetWindowCloaked(_hwnd, true))
            {
                _transitionWindowCloaked = true;
            }
            else if (bridgeShown)
            {
                _transitionBridge?.Hide();
            }
        }
    }

    private void UncloakAfterAtomicRelocation(bool flushDwm = true)
    {
        bool wasCloaked = _transitionWindowCloaked;
        _transitionWindowCloaked = false;
        if (wasCloaked && !_startupCloaked && _hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
        {
            _ = NativeMethods.SetWindowCloaked(_hwnd, false);
            if (flushDwm) _ = NativeMethods.DwmFlush();
        }
        _transitionBridge?.Hide();
    }

    private void ForceRestoreUncloakedState() => UncloakAfterAtomicRelocation(flushDwm: false);

    private bool ShouldUseAtomicCompactHandoff(TransitionCanvasPlan? settledPlan, NativeMethods.RECT compactTarget)
    {
        if (!_runtimeVisible || _hwnd == IntPtr.Zero) return false;
        if (settledPlan is { } plan)
            return !RectsEqual(plan.CanvasBounds, compactTarget);
        return TryGetLogicalWindowRect(out NativeMethods.RECT currentRect) && !RectsEqual(currentRect, compactTarget);
    }

    private void RecoverFailedTransition(CancellationTokenSource transition)
    {
        if (_closing || !ReferenceEquals(_transitionCancellation, transition)) return;
        // Clear logical locks before any native/XAML cleanup, which can itself fail or reenter.
        _transitionCancellation = null;
        transition.Cancel();
        transition.Dispose();
        _animating = false;
        _dwmTransitionRunning = false;
        _activeTransitionCanvas = null;
        _host.NotifyDockOpenStateChanged();
        _expanded = false;
        _transitionProgress = 0;
        _transitionVelocity = 0;
        _collapseTransitionGeometry = null;
        StopTransitionCompositionAnimations();
        ClearExpandedTransitionSweep();
        _canvasResizeInputTimer.Stop();
        _outsideClickHook?.Stop();
        UpdateCanvasResizeEdgeWindows(show: false);
        RestoreExpandedViewDefaultLayout();
        ExpandedView.Visibility = Visibility.Collapsed;
        ExpandedView.Opacity = 0;
        ResetExpandedTransitionOpacities();
        ResetExpandedVisualToRest(opacity: 0f);
        if (_stationTransitionClip is not null) ClearStationTransitionVisuals();
        bool hidden = IsContained || _definition.PlacementMode == OrganizerPlacementMode.Station;
        CompactView.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        CompactView.Opacity = 1;
        ResetCompactVisualToRest();
        if (hidden) _appWindow?.Hide();
        else
        {
            ApplyBounds(_compactBounds, show: _runtimeVisible, preserveZOrder: true);
            WindowRoot.UpdateLayout();
            UpdateSurfaceClips();
        }
        _desktopLayer?.SetExpanded(false, showWindow: false);
        ForceRestoreUncloakedState();
        RefreshPerformanceSettings();
        _host.NotifyCollapsed(this);
    }

    private async Task CommitCompactHandoffAsync(
        NativeMethods.RECT compactTarget,
        bool useAtomicCloak,
        CancellationToken cancellationToken)
    {
        bool cloakedHere = false;
        try
        {
            if (useAtomicCloak)
            {
                CloakForAtomicRelocation(compactTarget, topmost: false);
                cloakedHere = _transitionWindowCloaked;
            }

            _dwmTransitionRunning = false;
            _activeTransitionCanvas = null;
            _collapseTransitionGeometry = null;
            CommitCompactHandoff(compactTarget);
            ClearExpandedFilter(releaseInputActivation: true);
            _desktopLayer?.SetExpanded(false, showWindow: false);

            if (cloakedHere)
            {
                await WaitForNextRenderedAsync(cancellationToken, AtomicRelocationFrameTimeoutMs);
                if (_closing || cancellationToken.IsCancellationRequested) return;
                UncloakAfterAtomicRelocation(flushDwm: true);
                cloakedHere = false;
            }
        }
        finally
        {
            if (cloakedHere && !cancellationToken.IsCancellationRequested)
            {
                ForceRestoreUncloakedState();
            }
        }
    }

    private void CommitCompactHandoff(NativeMethods.RECT compactTarget)
    {
        StopTransitionCompositionAnimations();
        ClearExpandedTransitionSweep();
        _compactBounds = compactTarget;
        RestoreExpandedViewDefaultLayout();
        ExpandedView.Visibility = Visibility.Collapsed;
        ExpandedView.Opacity = 0;
        ResetExpandedTransitionOpacities();
        ResetExpandedVisualToRest(opacity: 0f);
        CompactView.Visibility = Visibility.Visible;
        CompactView.Opacity = 1;
        ResetCompactVisualToRest();
        ApplyBounds(compactTarget, show: true, preserveZOrder: true);
        _lastTransitionWindowLeft = compactTarget.Left;
        _lastTransitionWindowTop = compactTarget.Top;
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
        bool hadActiveTransition = _dwmTransitionRunning || _activeTransitionCanvas is not null || _dwmTransitionExpressionsBound;
        _animating = false;
        _transitionVelocity = 0;
        if (hadActiveTransition)
        {
            TransitionCanvasPlan? abandonedPlan = _activeTransitionCanvas;
            CleanupAbortedTransitionVisuals();
            if (!IsContained && _definition.PlacementMode != OrganizerPlacementMode.Station && _hwnd != IntPtr.Zero)
            {
                NativeMethods.RECT targetBounds = _expanded
                    ? abandonedPlan?.ExpandedBounds ?? CalculateExpandedBounds(_compactBounds)
                    : _compactBounds;
                _transitionProgress = _expanded ? 1 : 0;
                CompactView.Visibility = _expanded ? Visibility.Collapsed : Visibility.Visible;
                ExpandedView.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
                ExpandedView.Opacity = _expanded ? 1 : 0;
                ResetExpandedVisualToRest(opacity: _expanded ? 1f : 0f);
                ApplyBounds(targetBounds, show: _runtimeVisible, preserveZOrder: true);
                WindowRoot.UpdateLayout();
                UpdateSurfaceClips();
            }
        }
        ForceRestoreUncloakedState();
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

    private static bool ContainsRect(NativeMethods.RECT outer, NativeMethods.RECT inner) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top &&
        inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private TransitionCanvasPlan BuildTransitionCanvasPlan(
        NativeMethods.RECT expandedBounds,
        NativeMethods.RECT compactOrigin,
        bool contained,
        bool reducedMotion)
    {
        double scale = Math.Max(1.0, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        double compactWidthDip = contained
            ? Math.Max(1.0, compactOrigin.Width / scale)
            : Math.Max(1.0, GetCompactWidthDip());
        double compactHeightDip = contained
            ? Math.Max(1.0, compactOrigin.Height / scale)
            : Math.Max(1.0, GetCompactHeightDip());
        double compactTileSizeDip = contained
            ? Math.Max(1.0, Math.Min(compactWidthDip, compactHeightDip))
            : Math.Max(1.0, 39.0 * _appliedCompactScale);
        return reducedMotion
            ? TransitionCanvasMath.CreateReducedMotion(
                expandedBounds,
                compactOrigin,
                scale,
                compactTileSizeDip,
                compactWidthDip,
                compactHeightDip)
            : TransitionCanvasMath.Create(
                expandedBounds,
                compactOrigin,
                scale,
                compactTileSizeDip,
                compactWidthDip,
                compactHeightDip);
    }

    private void EnsureTransitionVisualTranslationEnabled()
    {
        if (_transitionVisualTranslationEnabled) return;
        ElementCompositionPreview.SetIsTranslationEnabled(ExpandedView, true);
        ElementCompositionPreview.SetIsTranslationEnabled(CompactView, true);
        ElementCompositionPreview.SetIsTranslationEnabled(ExpandedContentLayer, true);
        _transitionVisualTranslationEnabled = true;
    }

    private Visual GetCompactCompositionVisual() =>
        _compactCompositionVisual ??= ElementCompositionPreview.GetElementVisual(CompactView);

    private Visual GetExpandedSurfaceCompositionVisual() =>
        _expandedSurfaceCompositionVisual ??= ElementCompositionPreview.GetElementVisual(ExpandedSurfaceHost);

    private Visual GetExpandedSheenCompositionVisual() =>
        _expandedSheenCompositionVisual ??= ElementCompositionPreview.GetElementVisual(ExpandedLiquidSheen);

    private Visual GetExpandedEdgeCompositionVisual() =>
        _expandedEdgeCompositionVisual ??= ElementCompositionPreview.GetElementVisual(ExpandedEdgeOverlay);

    private Visual GetExpandedContentCompositionVisual() =>
        _expandedContentCompositionVisual ??= ElementCompositionPreview.GetElementVisual(ExpandedContentLayer);

    private Visual GetExpandedNameCompositionVisual() =>
        _expandedNameCompositionVisual ??= ElementCompositionPreview.GetElementVisual(ExpandedNameText);

    private Visual GetExpandedNameShadowCompositionVisual() =>
        _expandedNameShadowCompositionVisual ??= ElementCompositionPreview.GetElementVisual(ExpandedNameShadowHost);

    private Vector3 GetPixelSnappedCompactTranslation(TransitionCanvasPlan plan)
    {
        double scale = GetCompactDpiScale();
        int canvasW = plan.CanvasBounds.Width;
        int canvasH = plan.CanvasBounds.Height;
        int compactW = plan.CompactOrigin.Width;
        int compactH = plan.CompactOrigin.Height;
        // WinUI UseLayoutRounding centers CompactView at Floor((canvas - compact) / 2 + 0.5) physical px.
        int centeredX = (int)Math.Floor((canvasW - compactW) * 0.5 + 0.5);
        int centeredY = (int)Math.Floor((canvasH - compactH) * 0.5 + 0.5);
        int targetX = plan.CompactOrigin.Left - plan.CanvasBounds.Left;
        int targetY = plan.CompactOrigin.Top - plan.CanvasBounds.Top;
        return new Vector3(
            (float)((targetX - centeredX) / scale),
            (float)((targetY - centeredY) / scale),
            0f);
    }

    private void SetCompactVisualTranslation(Vector3 translation)
    {
        EnsureTransitionVisualTranslationEnabled();
        Visual visual = GetCompactCompositionVisual();
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", translation);
    }

    private void SetExpandedContentVisualTranslation(Vector3 translation)
    {
        EnsureTransitionVisualTranslationEnabled();
        Visual visual = GetExpandedContentCompositionVisual();
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", translation);
    }

    private Vector3 GetExpandedContentVisualTranslation()
    {
        if (!_transitionVisualTranslationEnabled || _expandedContentCompositionVisual is null)
            return Vector3.Zero;
        return _expandedContentCompositionVisual.Properties.TryGetVector3("Translation", out Vector3 value) == CompositionGetValueStatus.Succeeded
            ? value
            : Vector3.Zero;
    }

    private void ApplyExpandedViewCanvasLayout(TransitionCanvasPlan plan)
    {
        if (IsDock) return;
        ExpandedView.Width = plan.ExpandedSizeDip.X;
        ExpandedView.Height = plan.ExpandedSizeDip.Y;
        ExpandedView.HorizontalAlignment = HorizontalAlignment.Left;
        ExpandedView.VerticalAlignment = VerticalAlignment.Top;
        ExpandedView.Margin = new Thickness(plan.ExpandedOffsetDip.X, plan.ExpandedOffsetDip.Y, 0, 0);
        _dwmTransitionUsesExplicitExpandedLayout = true;
    }

    private void RestoreExpandedViewDefaultLayout()
    {
        if (IsDock || !_dwmTransitionUsesExplicitExpandedLayout) return;
        ExpandedView.Width = double.NaN;
        ExpandedView.Height = double.NaN;
        ExpandedView.HorizontalAlignment = HorizontalAlignment.Stretch;
        ExpandedView.VerticalAlignment = VerticalAlignment.Stretch;
        ExpandedView.Margin = new Thickness(0);
        _dwmTransitionUsesExplicitExpandedLayout = false;
    }

    private void ResetExpandedVisualToRest(float opacity = 1f)
    {
        Visual expandedVisual = GetExpandedCompositionVisual();
        expandedVisual.StopAnimation("Scale");
        expandedVisual.StopAnimation("Translation");
        expandedVisual.StopAnimation("Opacity");
        expandedVisual.Scale = Vector3.One;
        expandedVisual.Opacity = opacity;
        expandedVisual.IsVisible = true;
        if (_transitionVisualTranslationEnabled)
            expandedVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        if (_expandedContentCompositionVisual is { } contentVisual)
        {
            contentVisual.StopAnimation("Translation");
            contentVisual.StopAnimation("Opacity");
            contentVisual.Opacity = 1f;
            if (_transitionVisualTranslationEnabled)
                contentVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
    }

    private void ResetCompactVisualToRest()
    {
        Visual compactVisual = GetCompactCompositionVisual();
        compactVisual.StopAnimation("Scale");
        compactVisual.StopAnimation("Translation");
        compactVisual.StopAnimation("Opacity");
        compactVisual.Scale = Vector3.One;
        compactVisual.Opacity = 1f;
        compactVisual.IsVisible = true;
        if (_transitionVisualTranslationEnabled)
            compactVisual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    private void CleanupAbortedTransitionVisuals()
    {
        StopTransitionCompositionAnimations();
        ClearExpandedTransitionSweep();
        _dwmTransitionRunning = false;
        _activeTransitionCanvas = null;
        _collapseTransitionGeometry = null;
        RestoreExpandedViewDefaultLayout();
        ResetExpandedTransitionOpacities();
        ResetExpandedVisualToRest(opacity: _expanded ? 1f : 0f);
        ResetCompactVisualToRest();
        if (_stationTransitionClip is not null) ClearStationTransitionVisuals();
        ForceRestoreUncloakedState();
    }

    private void EnsureTransitionCompositionResources()
    {
        if (_transitionProps is not null) return;
        Compositor compositor = GetExpandedCompositionVisual().Compositor;

        _transitionProps = compositor.CreatePropertySet();
        _transitionProps.InsertScalar("Progress", 0f);

        _expandedScaleExpr = compositor.CreateExpressionAnimation(
            "Vector3(Max(0.02, Lerp(sx, 1.0, props.Progress)), Max(0.02, Lerp(sy, 1.0, props.Progress)), 1.0)");
        _expandedScaleExpr.SetReferenceParameter("props", _transitionProps);

        _expandedTranslationExpr = compositor.CreateExpressionAnimation(
            "Vector3(Lerp(tx, 0.0, props.Progress), Lerp(ty, 0.0, props.Progress), 0.0)");
        _expandedTranslationExpr.SetReferenceParameter("props", _transitionProps);

        _expandedOpacityExpr = compositor.CreateExpressionAnimation(
            "Clamp(Max(0.0, props.Progress - 0.04) / 0.08, 0.0, 1.0)");
        _expandedOpacityExpr.SetReferenceParameter("props", _transitionProps);

        _surfaceOpacityExpr = compositor.CreateExpressionAnimation(
            "Clamp(props.Progress / 0.28, 0.0, 1.0)");
        _surfaceOpacityExpr.SetReferenceParameter("props", _transitionProps);

        _contentOpacityExpr = compositor.CreateExpressionAnimation(
            "Clamp((props.Progress - delay) / span, 0.0, 1.0)");
        _contentOpacityExpr.SetReferenceParameter("props", _transitionProps);

        _contentTranslationExpr = compositor.CreateExpressionAnimation(
            "Vector3(0.0, (1.0 - Clamp((props.Progress - 0.10) / 0.65, 0.0, 1.0)) * rise, 0.0)");
        _contentTranslationExpr.SetReferenceParameter("props", _transitionProps);

        _compactOpacityExpr = compositor.CreateExpressionAnimation(
            "Clamp(1.0 - Max(0.0, props.Progress - 0.06) / 0.24, 0.0, 1.0)");
        _compactOpacityExpr.SetReferenceParameter("props", _transitionProps);

        _compactScaleExpr = compositor.CreateExpressionAnimation(
            "Vector3(1.0 + Clamp(Max(0.0, props.Progress - 0.06) / 0.24, 0.0, 1.0) * push, 1.0 + Clamp(Max(0.0, props.Progress - 0.06) / 0.24, 0.0, 1.0) * push, 1.0)");
        _compactScaleExpr.SetReferenceParameter("props", _transitionProps);

        _stationOpacityExpr = compositor.CreateExpressionAnimation(
            "Clamp(props.Progress, 0.0, 1.0)");
        _stationOpacityExpr.SetReferenceParameter("props", _transitionProps);

        _stationContentTranslationExpr = compositor.CreateExpressionAnimation(
            "Vector3(tx * (1.0 - Clamp(props.Progress, 0.0, 1.0)), ty * (1.0 - Clamp(props.Progress, 0.0, 1.0)), 0.0)");
        _stationContentTranslationExpr.SetReferenceParameter("props", _transitionProps);

        // Never assign InitialVelocity on spring instances so dwm.exe preserves live GPU velocity on mid-flight reversals!
        _expandProgressSpring = compositor.CreateSpringScalarAnimation();
        _expandProgressSpring.FinalValue = 1.0f;
        _expandProgressSpring.DampingRatio = ExpandSpringDampingRatio;
        _expandProgressSpring.Period = TimeSpan.FromMilliseconds(ExpandSpringPeriodMs);

        _collapseProgressSpring = compositor.CreateSpringScalarAnimation();
        _collapseProgressSpring.FinalValue = 0.0f;
        _collapseProgressSpring.DampingRatio = CollapseSpringDampingRatio;
        _collapseProgressSpring.Period = TimeSpan.FromMilliseconds(CollapseSpringPeriodMs);

        _stationProgressSpring = compositor.CreateSpringScalarAnimation();
        _stationProgressSpring.DampingRatio = StationSpringDampingRatio;
        _stationProgressSpring.Period = TimeSpan.FromMilliseconds(StationSpringPeriodMs);

        _reducedMotionEaseOut = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.22f, 1.0f),
            new Vector2(0.36f, 1.0f));
        _reducedMotionProgressAnim = compositor.CreateScalarKeyFrameAnimation();
        _reducedMotionProgressAnim.Duration = TimeSpan.FromMilliseconds(ReducedMotionTransitionDurationMs);

        if (!_transitionClosingHooked)
        {
            _transitionClosingHooked = true;
            Closed += TransitionEngine_WindowClosed;
        }
    }

    private void TransitionEngine_WindowClosed(object sender, WindowEventArgs args)
    {
        if (_transitionClosingHooked)
        {
            Closed -= TransitionEngine_WindowClosed;
            _transitionClosingHooked = false;
        }
        DisposeTransitionCompositionResources();
    }

    internal void DisposeTransitionCompositionResources()
    {
        if (_transitionClosingHooked)
        {
            Closed -= TransitionEngine_WindowClosed;
            _transitionClosingHooked = false;
        }
        try { StopTransitionCompositionAnimations(); } catch { }
        try { ClearExpandedTransitionSweep(); } catch { }
        ForceRestoreUncloakedState();
        _transitionBridge?.Dispose();
        _transitionBridge = null;
        _dwmTransitionRunning = false;
        _activeTransitionCanvas = null;

        _expandedScaleExpr?.Dispose();
        _expandedScaleExpr = null;
        _expandedTranslationExpr?.Dispose();
        _expandedTranslationExpr = null;
        _expandedOpacityExpr?.Dispose();
        _expandedOpacityExpr = null;
        _surfaceOpacityExpr?.Dispose();
        _surfaceOpacityExpr = null;
        _contentOpacityExpr?.Dispose();
        _contentOpacityExpr = null;
        _contentTranslationExpr?.Dispose();
        _contentTranslationExpr = null;
        _compactOpacityExpr?.Dispose();
        _compactOpacityExpr = null;
        _compactScaleExpr?.Dispose();
        _compactScaleExpr = null;
        _stationOpacityExpr?.Dispose();
        _stationOpacityExpr = null;
        _stationClipEdgeExpr?.Dispose();
        _stationClipEdgeExpr = null;
        _stationContentTranslationExpr?.Dispose();
        _stationContentTranslationExpr = null;

        _expandProgressSpring?.Dispose();
        _expandProgressSpring = null;
        _collapseProgressSpring?.Dispose();
        _collapseProgressSpring = null;
        _stationProgressSpring?.Dispose();
        _stationProgressSpring = null;
        _reducedMotionProgressAnim?.Dispose();
        _reducedMotionProgressAnim = null;
        _reducedMotionEaseOut?.Dispose();
        _reducedMotionEaseOut = null;

        _transitionProps?.Dispose();
        _transitionProps = null;

        _compactCompositionVisual = null;
        _expandedSurfaceCompositionVisual = null;
        _expandedSheenCompositionVisual = null;
        _expandedEdgeCompositionVisual = null;
        _expandedContentCompositionVisual = null;
        _expandedNameCompositionVisual = null;
        _expandedNameShadowCompositionVisual = null;
    }

    private void StopTransitionCompositionAnimations()
    {
        _transitionProps?.StopAnimation("Progress");
        if (_expandedCompositionVisual is { } expandedVisual)
        {
            expandedVisual.StopAnimation("Scale");
            expandedVisual.StopAnimation("Translation");
            expandedVisual.StopAnimation("Opacity");
        }
        if (_compactCompositionVisual is { } compactVisual)
        {
            compactVisual.StopAnimation("Scale");
            compactVisual.StopAnimation("Translation");
            compactVisual.StopAnimation("Opacity");
        }
        StopInnerExpandedTransitionAnimations();
        if (_stationTransitionClip is not null)
        {
            _stationTransitionClip.StopAnimation("Left");
            _stationTransitionClip.StopAnimation("Top");
            _stationTransitionClip.StopAnimation("Right");
            _stationTransitionClip.StopAnimation("Bottom");
        }
        _dwmTransitionExpressionsBound = false;
        _dwmTransitionStationBound = false;
    }

    private void StopInnerExpandedTransitionAnimations()
    {
        _expandedSurfaceCompositionVisual?.StopAnimation("Opacity");
        _expandedSheenCompositionVisual?.StopAnimation("Opacity");
        _expandedEdgeCompositionVisual?.StopAnimation("Opacity");
        if (_expandedContentCompositionVisual is { } contentVisual)
        {
            contentVisual.StopAnimation("Opacity");
            contentVisual.StopAnimation("Translation");
        }
        _expandedNameCompositionVisual?.StopAnimation("Opacity");
        _expandedNameShadowCompositionVisual?.StopAnimation("Opacity");
    }

    private void BindRegularTransitionExpressions(
        TransitionCanvasPlan plan,
        bool reducedMotion,
        bool contained,
        float initialProgress,
        bool continuingDwmFlight)
    {
        EnsureTransitionCompositionResources();
        EnsureTransitionVisualTranslationEnabled();
        CompositionPropertySet props = _transitionProps!;

        if (!continuingDwmFlight)
        {
            props.StopAnimation("Progress");
            props.InsertScalar("Progress", initialProgress);
        }

        // Preserve live GPU expressions untouched during mid-flight reversals so dwm.exe simply reverses props.Progress.
        if (continuingDwmFlight && _dwmTransitionExpressionsBound && !_dwmTransitionStationBound)
        {
            return;
        }

        Visual expandedVisual = GetExpandedCompositionVisual();
        Visual surfaceVisual = GetExpandedSurfaceCompositionVisual();
        Visual sheenVisual = GetExpandedSheenCompositionVisual();
        Visual edgeVisual = GetExpandedEdgeCompositionVisual();
        Visual contentVisual = GetExpandedContentCompositionVisual();
        Visual nameVisual = GetExpandedNameCompositionVisual();
        Visual nameShadowVisual = GetExpandedNameShadowCompositionVisual();
        Visual compactVisual = GetCompactCompositionVisual();

        if (_stationTransitionClip is not null && expandedVisual.Clip == _stationTransitionClip)
            expandedVisual.Clip = null;

        expandedVisual.CenterPoint = plan.ExpandedCenterPointDip;
        expandedVisual.IsVisible = true;

        _expandedScaleExpr!.SetScalarParameter("sx", reducedMotion ? 1.0f : plan.StartScale.X);
        _expandedScaleExpr.SetScalarParameter("sy", reducedMotion ? 1.0f : plan.StartScale.Y);
        expandedVisual.StartAnimation("Scale", _expandedScaleExpr);

        _expandedTranslationExpr!.SetScalarParameter("tx", reducedMotion ? 0.0f : plan.StartTranslationDip.X);
        _expandedTranslationExpr.SetScalarParameter("ty", reducedMotion ? 0.0f : plan.StartTranslationDip.Y);
        expandedVisual.StartAnimation("Translation", _expandedTranslationExpr);

        expandedVisual.StartAnimation("Opacity", _expandedOpacityExpr!);

        surfaceVisual.StartAnimation("Opacity", _surfaceOpacityExpr!);
        sheenVisual.StartAnimation("Opacity", _surfaceOpacityExpr!);
        edgeVisual.StartAnimation("Opacity", _surfaceOpacityExpr!);

        _contentOpacityExpr!.SetScalarParameter("delay", reducedMotion ? 0.0f : 0.14f);
        _contentOpacityExpr.SetScalarParameter("span", reducedMotion ? 0.28f : 0.44f);
        contentVisual.StartAnimation("Opacity", _contentOpacityExpr);
        nameVisual.StartAnimation("Opacity", _contentOpacityExpr);
        nameShadowVisual.StartAnimation("Opacity", _contentOpacityExpr);

        _contentTranslationExpr!.SetScalarParameter("rise", reducedMotion ? 0.0f : 12.0f);
        contentVisual.StartAnimation("Translation", _contentTranslationExpr);

        bool showCompact = !contained && CompactView.Visibility == Visibility.Visible;
        if (showCompact)
        {
            compactVisual.CenterPoint = plan.CompactCenterPointDip;
            SetCompactVisualTranslation(GetPixelSnappedCompactTranslation(plan));
            compactVisual.IsVisible = true;
            compactVisual.StartAnimation("Opacity", _compactOpacityExpr!);
            _compactScaleExpr!.SetScalarParameter("push", reducedMotion ? 0.0f : 0.10f);
            compactVisual.StartAnimation("Scale", _compactScaleExpr);
        }
        else
        {
            compactVisual.StopAnimation("Opacity");
            compactVisual.StopAnimation("Scale");
            compactVisual.Opacity = 0f;
            compactVisual.IsVisible = true;
        }

        if (!reducedMotion)
            BindExpandedTransitionSweep(props);
        else
            ClearExpandedTransitionSweep();

        _dwmTransitionExpressionsBound = true;
        _dwmTransitionStationBound = false;
    }

    private void BindStationTransitionExpressions(
        bool reducedMotion,
        float initialProgress,
        bool continuingStationFlight)
    {
        EnsureTransitionCompositionResources();
        EnsureTransitionVisualTranslationEnabled();
        CompositionPropertySet props = _transitionProps!;

        if (!continuingStationFlight)
        {
            props.StopAnimation("Progress");
            props.InsertScalar("Progress", initialProgress);
        }

        if (continuingStationFlight && _dwmTransitionExpressionsBound && _dwmTransitionStationBound)
        {
            return;
        }

        Visual expandedVisual = GetExpandedCompositionVisual();
        Visual contentVisual = GetExpandedContentCompositionVisual();
        Compositor compositor = expandedVisual.Compositor;

        expandedVisual.StopAnimation("Scale");
        expandedVisual.StopAnimation("Translation");
        expandedVisual.Scale = Vector3.One;
        expandedVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        expandedVisual.IsVisible = true;

        StopInnerExpandedTransitionAnimations();
        GetExpandedSurfaceCompositionVisual().Opacity = 1f;
        GetExpandedSheenCompositionVisual().Opacity = 1f;
        GetExpandedEdgeCompositionVisual().Opacity = 1f;
        contentVisual.Opacity = 1f;
        GetExpandedNameCompositionVisual().Opacity = 1f;
        GetExpandedNameShadowCompositionVisual().Opacity = 1f;

        double width = Math.Max(1.0, ExpandedView.ActualWidth);
        double height = Math.Max(1.0, ExpandedView.ActualHeight);
        double scale = Math.Max(1.0, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        OrganizerDockEdge edge = _definition.DockEdge;

        _stationTransitionClip ??= compositor.CreateRectangleClip();
        _stationTransitionClip.StopAnimation("Left");
        _stationTransitionClip.StopAnimation("Top");
        _stationTransitionClip.StopAnimation("Right");
        _stationTransitionClip.StopAnimation("Bottom");
        _stationTransitionClip.Left = 0f;
        _stationTransitionClip.Top = 0f;
        _stationTransitionClip.Right = (float)width;
        _stationTransitionClip.Bottom = (float)height;
        expandedVisual.Clip = _stationTransitionClip;

        expandedVisual.StartAnimation("Opacity", _stationOpacityExpr!);

        if (reducedMotion)
        {
            SetExpandedContentVisualTranslation(Vector3.Zero);
            ClearExpandedTransitionSweep();
        }
        else
        {
            bool horizontal = edge is OrganizerDockEdge.Left or OrganizerDockEdge.Right;
            double extent = horizontal ? width : height;
            float minReveal = (float)Math.Clamp(1.0 / scale, 0.0, extent);

            _stationClipEdgeExpr ??= compositor.CreateExpressionAnimation();
            _stationClipEdgeExpr.SetReferenceParameter("props", props);
            _stationClipEdgeExpr.SetScalarParameter("minReveal", minReveal);
            _stationClipEdgeExpr.SetScalarParameter("extent", (float)extent);

            string clipProperty;
            float travelX = 0f;
            float travelY = 0f;
            float travel = (float)StationTransitionMath.ContentTravelDip;

            switch (edge)
            {
                case OrganizerDockEdge.Left:
                    _stationClipEdgeExpr.Expression = "Lerp(minReveal, extent, Clamp(props.Progress, 0.0, 1.0))";
                    clipProperty = "Right";
                    travelX = -travel;
                    break;
                case OrganizerDockEdge.Top:
                    _stationClipEdgeExpr.Expression = "Lerp(minReveal, extent, Clamp(props.Progress, 0.0, 1.0))";
                    clipProperty = "Bottom";
                    travelY = -travel;
                    break;
                case OrganizerDockEdge.Right:
                    _stationClipEdgeExpr.Expression = "extent - Lerp(minReveal, extent, Clamp(props.Progress, 0.0, 1.0))";
                    clipProperty = "Left";
                    travelX = travel;
                    break;
                default:
                    _stationClipEdgeExpr.Expression = "extent - Lerp(minReveal, extent, Clamp(props.Progress, 0.0, 1.0))";
                    clipProperty = "Top";
                    travelY = travel;
                    break;
            }

            _stationTransitionClip.StartAnimation(clipProperty, _stationClipEdgeExpr);

            _stationContentTranslationExpr!.SetScalarParameter("tx", travelX);
            _stationContentTranslationExpr.SetScalarParameter("ty", travelY);
            contentVisual.StartAnimation("Translation", _stationContentTranslationExpr);

            BindExpandedTransitionSweep(props);
        }

        _dwmTransitionExpressionsBound = true;
        _dwmTransitionStationBound = true;
    }

    private async Task<bool> TryRunDwmCompositorTransitionAsync(
        double target,
        bool isStation,
        bool reducedMotion,
        CancellationToken cancellationToken)
    {
        bool expanding = target >= 0.5;
        bool contained = IsContained;
        bool continuingDwmFlight = _dwmTransitionRunning && _dwmTransitionExpressionsBound &&
            (_dwmTransitionStationBound == isStation);
        float initialProgress = (float)Math.Clamp(_transitionProgress, 0.0, 1.0);

        if (!isStation && _activeTransitionCanvas is null)
            return false;

        try
        {
            if (isStation)
            {
                BindStationTransitionExpressions(reducedMotion, initialProgress, continuingDwmFlight);
            }
            else
            {
                BindRegularTransitionExpressions(
                    _activeTransitionCanvas!.Value,
                    reducedMotion,
                    contained,
                    initialProgress,
                    continuingDwmFlight);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("DWM 合成过渡表达式绑定失败，回退至同步帧循环。", ex);
            StopTransitionCompositionAnimations();
            ClearExpandedTransitionSweep();
            _dwmTransitionRunning = false;
            return false;
        }

        if (!continuingDwmFlight && Math.Abs(target - initialProgress) <= 0.0005)
        {
            _transitionProps!.StopAnimation("Progress");
            _transitionProps.InsertScalar("Progress", (float)target);
            _dwmTransitionRunning = false;
            _transitionProgress = target;
            _transitionVelocity = 0;
            if (_transitionWindowCloaked)
                UncloakAfterAtomicRelocation(flushDwm: true);
            return true;
        }

        CompositionPropertySet props = _transitionProps!;
        Compositor compositor = props.Compositor;

        CompositionAnimation animation;
        if (reducedMotion)
        {
            _reducedMotionProgressAnim!.InsertKeyFrame(1.0f, (float)target, _reducedMotionEaseOut);
            animation = _reducedMotionProgressAnim;
        }
        else if (isStation)
        {
            SpringScalarNaturalMotionAnimation stationSpring = _stationProgressSpring!;
            stationSpring.FinalValue = (float)target;
            // Omit InitialValue on mid-flight reversal so dwm.exe inherits live GPU position & velocity!
            stationSpring.InitialValue = continuingDwmFlight ? null : initialProgress;
            animation = stationSpring;
        }
        else
        {
            SpringScalarNaturalMotionAnimation spring = expanding
                ? _expandProgressSpring!
                : _collapseProgressSpring!;
            spring.FinalValue = (float)target;
            // Omit InitialValue on mid-flight reversal so dwm.exe inherits live GPU position & velocity!
            spring.InitialValue = continuingDwmFlight ? null : initialProgress;
            animation = spring;
        }

        int safetyTimeoutMs = reducedMotion
            ? ReducedMotionTransitionDurationMs + 80
            : isStation
                ? StationBatchSafetyTimeoutMs
                : expanding
                    ? ExpandBatchSafetyTimeoutMs
                    : CollapseBatchSafetyTimeoutMs;

        using IDisposable clockBoost = CompositorClockBoost.Acquire();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        CompositionScopedBatch? batch = null;
        TypedEventHandler<object, CompositionBatchCompletedEventArgs>? completedHandler = null;
        var safetyTimer = DispatcherQueue.CreateTimer();
        safetyTimer.Interval = TimeSpan.FromMilliseconds(safetyTimeoutMs);
        safetyTimer.IsRepeating = false;
        TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>? timeoutHandler =
            (_, _) => tcs.TrySetResult(false);
        safetyTimer.Tick += timeoutHandler;

        using CancellationTokenRegistration cancelReg = cancellationToken.Register(() =>
            tcs.TrySetCanceled(cancellationToken));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            completedHandler = (_, _) => tcs.TrySetResult(true);
            batch.Completed += completedHandler;

            _dwmTransitionRunning = true;
            _transitionProgress = 0.5;
            _transitionVelocity = 0;
            _animationTrace?.Metrics.RecordWork(AnimationTraceWork.VisualFrame);

            long dwmStartedAt = Stopwatch.GetTimestamp();
            props.StartAnimation("Progress", animation);
            batch.End();
            if (_transitionWindowCloaked)
            {
                UncloakAfterAtomicRelocation(flushDwm: true);
            }
            safetyTimer.Start();

            bool completedByBatch = await tcs.Task;
            double elapsedMs = Stopwatch.GetElapsedTime(dwmStartedAt).TotalMilliseconds;
            AppLogger.Performance($"dwm-transition dir={(expanding ? "expand" : "collapse")} elapsed={elapsedMs:0}ms batch={completedByBatch}");
            _animationTrace?.Metrics.RecordWait(rendered: true, completed: completedByBatch);

            if (!cancellationToken.IsCancellationRequested)
            {
                _dwmTransitionRunning = false;
                _transitionProgress = target;
                _transitionVelocity = 0;
            }
            return true;
        }
        finally
        {
            safetyTimer.Stop();
            safetyTimer.Tick -= timeoutHandler;
            if (batch is not null)
            {
                if (completedHandler is not null) batch.Completed -= completedHandler;
                batch.Dispose();
            }
        }
    }

    internal void PreWarmCompactHoverIcons()
    {
        if (_closing || _expanded || _animating || _hoverIconPreWarmRunning ||
            !_host.State.GlobalSettings.ExpandOnHover || _items.Count == 0)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_lastHoverIconPreWarmAt != 0 &&
            Stopwatch.GetElapsedTime(_lastHoverIconPreWarmAt, now).TotalSeconds < 4.0)
        {
            return;
        }
        _lastHoverIconPreWarmAt = now;

        int limit = Math.Min(_items.Count, 24);
        List<string>? paths = null;
        for (int index = 0; index < limit; index++)
        {
            WidgetItem item = _items[index];
            if (item.Kind == WidgetItemKind.Organizer || IsDocumentItem(item) || string.IsNullOrWhiteSpace(item.FullPath))
                continue;
            paths ??= new List<string>(limit);
            paths.Add(item.FullPath);
        }
        if (paths is null || paths.Count == 0) return;

        _ = PreWarmCompactHoverIconsCoreAsync(paths);
    }

    private async Task PreWarmCompactHoverIconsCoreAsync(List<string> paths)
    {
        _hoverIconPreWarmRunning = true;
        try
        {
            foreach (string path in paths)
            {
                if (_closing || _expanded || _animating) return;
                // IconCacheService offloads Shell extraction & disk reads to ThreadPool automatically
                // and populates the shared RAM LRU cache before the 350ms hover delay fires.
                _ = await _host.SharedIconCache.GetIconAsync(path);
            }
        }
        catch
        {
            // Hover icon pre-warming is best-effort and must never surface UI errors.
        }
        finally
        {
            _hoverIconPreWarmRunning = false;
        }
    }

    private void ResetExpandedTransitionOpacities()
    {
        StopInnerExpandedTransitionAnimations();
        ExpandedSurfaceHost.Opacity = 1;
        ExpandedLiquidSheen.Opacity = 1;
        ExpandedEdgeOverlay.Opacity = 1;
        ExpandedContentLayer.Opacity = 1;
        ExpandedNameText.Opacity = 1;
        ExpandedNameShadowHost.Opacity = 1;
        if (_expandedSurfaceCompositionVisual is not null) _expandedSurfaceCompositionVisual.Opacity = 1f;
        if (_expandedSheenCompositionVisual is not null) _expandedSheenCompositionVisual.Opacity = 1f;
        if (_expandedEdgeCompositionVisual is not null) _expandedEdgeCompositionVisual.Opacity = 1f;
        if (_expandedContentCompositionVisual is not null) _expandedContentCompositionVisual.Opacity = 1f;
        if (_expandedNameCompositionVisual is not null) _expandedNameCompositionVisual.Opacity = 1f;
        if (_expandedNameShadowCompositionVisual is not null) _expandedNameShadowCompositionVisual.Opacity = 1f;
    }

    private async Task RunVisualTransitionAsync(double target, CancellationToken cancellationToken)
    {
        bool reducedMotion = !UseCustomAnimations;
        bool isStation = _definition.PlacementMode == OrganizerPlacementMode.Station;
        if (WindowRoot.IsLoaded && await TryRunDwmCompositorTransitionAsync(target, isStation, reducedMotion, cancellationToken))
        {
            return;
        }

        // Synchronized single-VSync fallback if WindowRoot is not loaded or DWM batch could not start.
        bool expanding = target >= 0.5;
        AnimationTraceSession? trace = _animationTrace;
        long previous = Stopwatch.GetTimestamp();
        long lastRenderingTick = previous;
        bool settled = false;

        using IDisposable clockBoost = CompositorClockBoost.Acquire();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void StepFrame(long now, bool fromRendering)
        {
            if (tcs.Task.IsCompleted) return;
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }
            try
            {
                if (fromRendering)
                {
                    lastRenderingTick = now;
                    trace?.Metrics.RecordRendering(now);
                    trace?.Metrics.RecordWait(rendered: false, completed: true);
                }
                else
                {
                    trace?.Metrics.RecordWait(rendered: false, completed: false);
                }

                double measuredMilliseconds = Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
                double delta = Math.Clamp(measuredMilliseconds / 1000d, 1d / 480d, 1d / 30d);
                previous = now;
                if (reducedMotion)
                {
                    double step = delta / (ReducedMotionDurationMs / 1000d);
                    _transitionProgress = MoveTowards(_transitionProgress, target, step);
                    _transitionVelocity = 0;
                }
                else
                {
                    double response = isStation
                        ? StationTransitionResponseSeconds
                        : expanding ? ExpandTransitionResponseSeconds : CollapseTransitionResponseSeconds;
                    double damping = isStation
                        ? 0.98
                        : expanding ? 0.86 : 0.96;
                    // Scale by 100 so ItemMotionMath.StepScalarSpring's snap threshold equals 0.0002 normalized precision.
                    float scaledPos = (float)(_transitionProgress * 100.0);
                    float scaledVel = (float)(_transitionVelocity * 100.0);
                    _ = ItemMotionMath.StepScalarSpring(
                        ref scaledPos,
                        ref scaledVel,
                        (float)(target * 100.0),
                        delta,
                        responseSeconds: response,
                        dampingFraction: damping);
                    _transitionProgress = isStation
                        ? Math.Clamp(scaledPos / 100.0, 0.0, 1.0)
                        : Math.Clamp(scaledPos / 100.0, 0.0, 1.06);
                    _transitionVelocity = scaledVel / 100.0;
                }
                ApplyTransitionFrame(_transitionProgress, reducedMotion);
                if (_transitionWindowCloaked)
                {
                    UncloakAfterAtomicRelocation(flushDwm: true);
                }
                if (Math.Abs(target - _transitionProgress) <= 0.001 && Math.Abs(_transitionVelocity) <= 0.01)
                {
                    _transitionProgress = target;
                    _transitionVelocity = 0;
                    ApplyTransitionFrame(target, reducedMotion);
                    settled = true;
                    tcs.TrySetResult();
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        // Single persistent CompositionTarget.Rendering subscription eliminates per-frame VSync halving & CTS/timer churn!
        void OnRendering(object? sender, object args) => StepFrame(Stopwatch.GetTimestamp(), fromRendering: true);

        // Fallback watchdog timer ensures hidden/occluded windows still complete deterministically if Rendering pauses.
        var fallbackTimer = DispatcherQueue.CreateTimer();
        fallbackTimer.Interval = TimeSpan.FromMilliseconds(100);
        fallbackTimer.IsRepeating = true;
        void OnFallbackTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(lastRenderingTick, now).TotalMilliseconds >= 95)
                StepFrame(now, fromRendering: false);
        }
        fallbackTimer.Tick += OnFallbackTick;

        using CancellationTokenRegistration cancelReg = cancellationToken.Register(() =>
            tcs.TrySetCanceled(cancellationToken));

        CompositionTarget.Rendering += OnRendering;
        fallbackTimer.Start();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await tcs.Task;
        }
        finally
        {
            fallbackTimer.Stop();
            fallbackTimer.Tick -= OnFallbackTick;
            CompositionTarget.Rendering -= OnRendering;
        }

        if (settled && !cancellationToken.IsCancellationRequested)
        {
            if (target == 0 && !isStation)
                await WaitForNextRenderedAsync(cancellationToken);
            else
                await WaitForNextRenderAsync(cancellationToken);
        }
    }

    private void ApplyTransitionFrame(double progress, bool reducedMotion)
    {
        _animationTrace?.Metrics.RecordWork(AnimationTraceWork.VisualFrame);
        if (_definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            ApplyStationTransitionFrame(Math.Clamp(progress, 0, 1), reducedMotion);
            return;
        }

        if (_stationTransitionClip is not null)
            ClearStationTransitionVisuals();

        double clamped = Math.Clamp(progress, 0, 1);
        Visual expandedVisual = GetExpandedCompositionVisual();
        expandedVisual.IsVisible = true;
        if (_activeTransitionCanvas is { } plan)
        {
            expandedVisual.CenterPoint = plan.ExpandedCenterPointDip;
            expandedVisual.Scale = reducedMotion
                ? Vector3.One
                : new Vector3(
                    MathF.Max(0.02f, plan.StartScale.X + (1f - plan.StartScale.X) * (float)progress),
                    MathF.Max(0.02f, plan.StartScale.Y + (1f - plan.StartScale.Y) * (float)progress),
                    1f);
            EnsureTransitionVisualTranslationEnabled();
            expandedVisual.Properties.InsertVector3(
                "Translation",
                reducedMotion
                    ? Vector3.Zero
                    : new Vector3(
                        plan.StartTranslationDip.X * (1f - (float)progress),
                        plan.StartTranslationDip.Y * (1f - (float)progress),
                        0f));
            SetCompactVisualTranslation(GetPixelSnappedCompactTranslation(plan));
        }
        else
        {
            expandedVisual.CenterPoint = new Vector3(_transitionAnchorDip, 0);
            SetCompactVisualTranslation(_transitionCompactTranslation);
            expandedVisual.Scale = reducedMotion
                ? Vector3.One
                : new Vector3(
                    MathF.Max(0.02f, _transitionStartScale.X + (1f - _transitionStartScale.X) * (float)progress),
                    MathF.Max(0.02f, _transitionStartScale.Y + (1f - _transitionStartScale.Y) * (float)progress),
                    1f);
        }

        float surfaceOpacity = (float)Math.Clamp(clamped / 0.28, 0, 1);
        float contentOpacity = reducedMotion
            ? surfaceOpacity
            : (float)Math.Clamp((clamped - 0.14) / 0.44, 0, 1);
        double compactPhase = Math.Clamp(Math.Max(0.0, clamped - 0.06) / 0.24, 0, 1);
        float compactOpacity = (float)(1.0 - compactPhase);
        float compactPush = reducedMotion ? 0f : (float)compactPhase * 0.10f;
        Visual compactVisual = GetCompactCompositionVisual();
        compactVisual.IsVisible = true;
        compactVisual.Opacity = compactOpacity;
        compactVisual.Scale = new Vector3(1f + compactPush, 1f + compactPush, 1f);
        expandedVisual.Opacity = (float)Math.Clamp(Math.Max(0.0, clamped - 0.04) / 0.08, 0, 1);
        GetExpandedSurfaceCompositionVisual().Opacity = surfaceOpacity;
        GetExpandedSheenCompositionVisual().Opacity = surfaceOpacity;
        GetExpandedEdgeCompositionVisual().Opacity = surfaceOpacity;
        GetExpandedContentCompositionVisual().Opacity = contentOpacity;
        GetExpandedNameCompositionVisual().Opacity = contentOpacity;
        GetExpandedNameShadowCompositionVisual().Opacity = contentOpacity;
        float contentRise = reducedMotion ? 0f : (1f - (float)Math.Clamp((clamped - 0.10) / 0.65, 0, 1)) * 12f;
        SetExpandedContentVisualTranslation(new Vector3(0f, contentRise, 0f));
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
        expandedVisual.IsVisible = true;
        _stationTransitionClip ??= expandedVisual.Compositor.CreateRectangleClip();
        _stationTransitionClip.Left = (float)frame.ClipLeft;
        _stationTransitionClip.Top = (float)frame.ClipTop;
        _stationTransitionClip.Right = (float)frame.ClipRight;
        _stationTransitionClip.Bottom = (float)frame.ClipBottom;
        expandedVisual.Clip = _stationTransitionClip;

        SetExpandedContentVisualTranslation(new Vector3((float)frame.TranslationX, (float)frame.TranslationY, 0));
        ResetExpandedTransitionOpacities();
        expandedVisual.Opacity = (float)frame.Opacity;
        GetCompactCompositionVisual().Opacity = 0f;
        SetCompactVisualTranslation(Vector3.Zero);
    }

    private void ClearStationTransitionVisuals()
    {
        if (!_animating && (_dwmTransitionRunning || _dwmTransitionExpressionsBound || _dwmTransitionUsesExplicitExpandedLayout))
        {
            StopTransitionCompositionAnimations();
            ClearExpandedTransitionSweep();
            _dwmTransitionRunning = false;
            _activeTransitionCanvas = null;
            RestoreExpandedViewDefaultLayout();
        }
        Visual expandedVisual = GetExpandedCompositionVisual();
        if (_stationTransitionClip is not null)
        {
            _stationTransitionClip.StopAnimation("Left");
            _stationTransitionClip.StopAnimation("Top");
            _stationTransitionClip.StopAnimation("Right");
            _stationTransitionClip.StopAnimation("Bottom");
            if (expandedVisual.Clip == _stationTransitionClip)
                expandedVisual.Clip = null;
        }
        expandedVisual.Scale = Vector3.One;
        expandedVisual.IsVisible = true;
        if (_transitionVisualTranslationEnabled)
            expandedVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        SetExpandedContentVisualTranslation(Vector3.Zero);
    }

    private int GetNestedOrganizerPreviewPhysicalPx(OrganizerPreviewControl preview, int itemCount)
    {
        double dpiScale = GetCompactDpiScale();
        double previewDip = double.IsFinite(preview.Width) && preview.Width > 0
            ? preview.Width
            : (preview.ActualWidth > 0 ? preview.ActualWidth : 20);
        double innerDip = Math.Max(6, previewDip - (preview.IsCompactVisual ? 4 : 12));
        double subSlotDip = itemCount <= 1 ? innerDip : innerDip * 0.5;
        return IconCacheService.NormalizeCompactPreviewPixelSize((int)Math.Ceiling(subSlotDip * dpiScale));
    }

    private static string BuildOrganizerPreviewSubIconKey(WidgetItem item, bool compactVisual, int compactPhysicalPx)
    {
        string baseKey = item.Kind == WidgetItemKind.Organizer
            ? $"organizer:{item.OrganizerId:N}"
            : IsDocumentItem(item)
                ? $"doc:{item.Kind}:{item.RelativeName}"
                : item.FullPath;
        return compactVisual ? $"{baseKey}|cpx={compactPhysicalPx}" : baseKey;
    }

    private bool IsOrganizerPreviewContentCurrent(OrganizerPreviewControl preview, Guid organizerId)
    {
        WidgetItem[] items = _host.GetOrganizerPreviewItems(organizerId).Take(CompactPreviewItemCount).ToArray();
        int compactPhysicalPx = preview.IsCompactVisual ? GetNestedOrganizerPreviewPhysicalPx(preview, items.Length) : 0;
        for (int index = 0; index < CompactPreviewItemCount; index++)
        {
            Image image = preview.Images[index];
            if (index < items.Length)
            {
                string expectedKey = BuildOrganizerPreviewSubIconKey(items[index], preview.IsCompactVisual, compactPhysicalPx);
                if (image.Visibility != Visibility.Visible ||
                    image.Source is null ||
                    !string.Equals(image.Tag as string, expectedKey, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else if (image.Visibility != Visibility.Collapsed)
            {
                return false;
            }
        }
        return true;
    }

    private async Task LoadOrganizerPreviewAsync(OrganizerPreviewControl preview, Guid organizerId, object requestToken)
    {
        WidgetItem[] items = _host.GetOrganizerPreviewItems(organizerId).Take(CompactPreviewItemCount).ToArray();
        bool compactVisual = preview.IsCompactVisual;
        int compactPhysicalPx = compactVisual ? GetNestedOrganizerPreviewPhysicalPx(preview, items.Length) : 0;

        for (int index = 0; index < CompactPreviewItemCount; index++)
        {
            Image image = preview.Images[index];
            if (index >= items.Length)
            {
                image.Tag = null;
                if (image.Source is not null) image.Source = null;
                continue;
            }

            string expectedKey = BuildOrganizerPreviewSubIconKey(items[index], compactVisual, compactPhysicalPx);
            if (!string.Equals(image.Tag as string, expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                image.Tag = expectedKey;
                if (image.Source is not null) image.Source = null;
            }
        }

        ApplyOrganizerPreviewLayout(preview, items.Length);

        for (int index = 0; index < items.Length; index++)
        {
            Image image = preview.Images[index];
            WidgetItem item = items[index];
            string iconKey = BuildOrganizerPreviewSubIconKey(item, compactVisual, compactPhysicalPx);
            if (string.Equals(image.Tag as string, iconKey, StringComparison.OrdinalIgnoreCase) &&
                image.Source is not null)
            {
                continue;
            }

            image.Tag = iconKey;
            if (item.Kind == WidgetItemKind.Organizer)
            {
                BitmapImage orgIcon = compactVisual ? GetCompactOrganizerFallbackIcon(compactPhysicalPx) : _organizerIcon;
                if (!ReferenceEquals(image.Source, orgIcon)) image.Source = orgIcon;
            }
            else if (IsDocumentItem(item))
            {
                BitmapImage docIcon = compactVisual ? GetCompactDocumentIcon(item, compactPhysicalPx) : GetDocumentIcon(item);
                if (!ReferenceEquals(image.Source, docIcon)) image.Source = docIcon;
            }
            else if (compactVisual)
            {
                BitmapImage? bitmap = await GetCompactPreviewIconThrottledAsync(item.FullPath, compactPhysicalPx);
                if (_closing || !ReferenceEquals(preview.RequestToken, requestToken)) return;
                if (string.Equals(image.Tag as string, iconKey, StringComparison.OrdinalIgnoreCase))
                {
                    if (bitmap is not null)
                    {
                        if (!ReferenceEquals(image.Source, bitmap)) image.Source = bitmap;
                    }
                    else
                    {
                        image.ClearValue(Image.SourceProperty);
                    }
                }
            }
            else
            {
                await LoadIconAsync(image, item.FullPath);
            }
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

    private async Task WaitForNextRenderedAsync(CancellationToken cancellationToken, int timeoutMs = 100)
    {
        AnimationTraceSession? trace = _animationTrace;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _windowLifetime.Token);
        EventHandler<RenderedEventArgs>? handler = null;
        bool completed = await UiEventAwaiter.WaitAsync(
            complete => { handler = (_, _) => complete(); CompositionTarget.Rendered += handler; },
            _ => { if (handler is not null) CompositionTarget.Rendered -= handler; },
            TimeSpan.FromMilliseconds(timeoutMs), linked.Token);
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

        SetCompactPressVisualState(true);
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
        UpdateCompactSpecularPointer(e);
        PreWarmCompactHoverIcons();
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
        try
        {
            if (message is NativeMethods.WM_SETTINGCHANGE or NativeMethods.WM_THEMECHANGED)
            {
                DesktopTypographyService.InvalidateCache();
                DispatcherQueue.TryEnqueue(() => { if (!_closing) ApplyCompactVisualStyle(); });
            }
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
        }
        catch (Exception ex)
        {
            AppLogger.Error("窗口子类消息处理异常。", ex);
        }
        return NativeMethods.DefSubclassProc(hWnd, message, wParam, lParam);
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
                _groupDragHasPeers = _organizerMultiSelected &&
                    _host.MultiSelectedOrganizerCount > 1 &&
                    _host.Windows.Any(w => !ReferenceEquals(w, this) && w.IsOrganizerMultiSelected &&
                        w.IsEligibleForDesktopMultiSelect && w.DefinitionPlacementMode == OrganizerPlacementMode.Floating);
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
        bool hadGroupDragPeers = _groupDragHasPeers;
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
                UpdateCompactLabelSafeWidth();
                if (hadGroupDragPeers)
                {
                    await _host.CommitMultiSelectedGroupDragAsync(this);
                }
                else
                {
                    await SaveStateAsync();
                    await _host.CommitMultiSelectedGroupDragAsync(this);
                }
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
        SetCompactPressVisualState(false);
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
        _groupDragHasPeers = false;
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
