using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace TuckPane;

public sealed partial class MainWindow
{
    // Internal opt-in: keep the icon wave implementation available without a UI setting.
    internal const bool IconHoverWaveEnabled = false;
    private bool UseHoverWave => UseCustomAnimations && OrganizerHoverWave.IsEnabledFor(_definition.PlacementMode, IsCompactList, IconHoverWaveEnabled,
        _host.State.GlobalSettings.CompactHoverMagnificationEnabled, _host.State.GlobalSettings.DockHoverMagnificationEnabled);
    private float HoverMaximumScale => (float)GlobalSettings.NormalizeHoverMagnificationScale(IsDock
        ? _host.State.GlobalSettings.DockHoverMagnificationScale
        : IsCompactList ? _host.State.GlobalSettings.CompactHoverMagnificationScale : OrganizerHoverWave.MaximumScale);

    internal void RefreshHoverPreference()
    {
        if (HorizontalHover) ApplyDockLayout();
        if (!UseHoverWave) ResetHoverWave();
        RefreshHoverAvailability();
    }

    private readonly OrganizerHoverWave _hoverWave = new();
    private readonly Dictionary<Border, HoverItemVisual> _hoverItems = new();
    private Border? _hoverPressedHost;
    private bool _hoverRenderingSubscribed;
    private IDisposable? _hoverWaveClockBoost;
    private bool _hoverGeometryDirty = true;
    private bool _hoverLayoutPending;
    private bool _hoverTargetsDirty = true;
    private long _hoverLastFrame;
    private PointerEventHandler? _hoverPointerEnteredHandler;
    private PointerEventHandler? _hoverPointerMovedHandler;
    private PointerEventHandler? _hoverPointerExitedHandler;
    private readonly HoverAnchorMotion _hoverAnchor = new();
    private readonly DockHoverMotion _dockHoverMotion = new();
    private KeyValuePair<Border, HoverItemVisual>[] _hoverLayout = [];
    private float[] _hoverLayoutKeys = [];
    private int _hoverLayoutCount;
    private HoverLayoutItem[] _hoverLayoutInput = [], _hoverLayoutAltInput = [];
    private HoverLayoutPose[] _hoverLayoutPoses = [], _hoverLayoutAltPoses = [];
    private HoverHitRegion[] _hoverHitRegions = [];
    private float[] _hoverLayoutCenters = [], _hoverLayoutScales = [];
    private float[] _hoverLayoutAltCenters = [], _hoverLayoutAltScales = [];
    private Point _hoverViewportWindowOrigin;
    private bool _hoverViewportWindowOriginValid;
    private Rect _appliedHoverViewportClipRect;
    private bool _hasAppliedHoverViewportClipRect;
    private Rect _appliedCompactHighlightClipRect;
    private bool _hasAppliedCompactHighlightClipRect;
    private DispatcherQueueTimer? _hoverPointerGuard;

    private sealed class HoverItemVisual(StackPanel content, Grid icon)
    {
        internal readonly StackPanel Content = content;
        internal readonly Grid Icon = icon;
        internal readonly HoverWaveMotion Motion = new();
        internal readonly ItemBounceMotion Bounce = new();
        internal FrameworkElement? ProxyIcon;
        internal readonly RectangleGeometry Clip = new();
        internal Vector2 Center;
        internal Vector2 Pitch;
        internal bool NearViewport;
        internal bool Raised;
        internal HoverRect BaseBounds;
        internal HoverRect RowBounds;
        internal HoverRect IconBounds;
        internal HoverRect VisibleBaseBounds;
        internal HoverRect VisibleBounds;
        internal Vector2 Translation;
        internal Border? Proxy;
        internal Action? DetachProxyHandlers;
        internal string? DockToolTipText;
        internal bool Presented;
        internal float RenderScale = 1;
        internal float AppliedContentScale = 1;
        internal float AppliedProxyScale = float.NaN;
        internal Vector2 AppliedProxyTranslation = new(float.NaN, float.NaN);
        internal HoverRect AppliedProxyClip;
        internal bool HasAppliedProxyClip;
        internal int AppliedHostZIndex;
        internal int AppliedProxyZIndex = int.MinValue;
        internal float AppliedBounceY = float.NaN;
        internal bool ProxyClipClearedForBounce;
        internal double AppliedProxyLeft = double.NaN;
        internal double AppliedProxyTop = double.NaN;
        internal Vector3 AppliedProxyCenter = new(float.NaN, float.NaN, float.NaN);
        internal Vector3 AppliedContentCenter = new(float.NaN, float.NaN, float.NaN);
        internal object? AppliedToolTip;
    }

    private void InitializeHoverWave()
    {
        _hoverPointerEnteredHandler = new PointerEventHandler(HoverViewport_PointerMoved);
        _hoverPointerMovedHandler = new PointerEventHandler(HoverViewport_PointerMoved);
        _hoverPointerExitedHandler = new PointerEventHandler(HoverViewport_PointerExited);
        WindowRoot.AddHandler(UIElement.PointerEnteredEvent, _hoverPointerEnteredHandler, handledEventsToo: true);
        WindowRoot.AddHandler(UIElement.PointerMovedEvent, _hoverPointerMovedHandler, handledEventsToo: true);
        WindowRoot.AddHandler(UIElement.PointerExitedEvent, _hoverPointerExitedHandler, handledEventsToo: true);
        ItemsScrollView.PointerExited += HoverViewport_PointerExited;
        ItemsScrollView.ViewChanged += HoverViewport_ViewChanged;
        ItemsScrollView.StateChanged += HoverViewport_StateChanged;
        ItemsRepeater.LayoutUpdated += HoverLayout_Updated;
        _uiSettings.AnimationsEnabledChanged += HoverAnimationsEnabledChanged;
        _hoverPointerGuard = DispatcherQueue.CreateTimer();
        _hoverPointerGuard.Tick += HoverPointerGuard_Tick;
    }

    private bool HoverViewportVisible => !_closing && _expanded && !_animating &&
        ExpandedView.Visibility == Visibility.Visible &&
        (_definition.PlacementMode == OrganizerPlacementMode.Station ? _stationVisible : _runtimeVisible || IsContained);

    private bool HoverInteractionBusy => CaptureInteractionState().BlocksHoverInteraction;

    private void RefreshHoverAvailability()
    {
        bool enabled = UseCustomAnimations;
        if (!enabled || !HoverViewportVisible || IsCompactList) ResetItemBounces();
        if (enabled && HoverViewportVisible && UseHoverPresentation &&
            _itemReorderSession is { State: ItemDragState.Pressed }) return;
        _hoverWave.SetAvailability(UseHoverWave, !HoverViewportVisible || HoverInteractionBusy);
        _hoverWave.SetScrolling(_smoothScrollRenderingSubscribed || ItemsScrollView.State != ScrollingInteractionState.Idle,
            preservePointer: IsCompactList);
        if (!enabled || !HoverViewportVisible || UseHoverPresentation && HoverInteractionBusy) ResetHoverWave();
        else
        {
            _hoverTargetsDirty = true;
            WakeHoverWave();
        }
        RefreshCompactRowHighlight();
    }

    private void HoverAnimationsEnabledChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (!_closing) RefreshHoverAvailability(); });

    private void RegisterHoverItem(Border host)
    {
        UnregisterHoverItem(host);
        StackPanel? content;
        Grid? icon;
        if (GetOrResolveItemHostParts(host) is { } parts)
        {
            content = parts.ItemContent;
            icon = parts.IconContainer;
        }
        else
        {
            content = FindItemPart<StackPanel>(host, "ItemContent");
            icon = FindItemPart<Grid>(host, "ItemIconContainer");
        }
        if (content is null || icon is null) return;
        content.ScaleTransition = null;
        content.TranslationTransition = null;
        content.Scale = Vector3.One;
        content.Translation = Vector3.Zero;
        // Compact feedback is rendered once, against the visible row geometry.
        host.BackgroundTransition = IsCompactList ? null : new BrushTransition { Duration = TimeSpan.FromMilliseconds(120) };
        host.Background = _transparentItemBrush;
        _hoverItems.Add(host, new HoverItemVisual(content, icon));
        host.SizeChanged += HoverElement_SizeChanged;
        content.SizeChanged += HoverElement_SizeChanged;
        InvalidateHoverGeometry();
    }

    private void UnregisterHoverItem(Border host)
    {
        if (!_hoverItems.Remove(host, out HoverItemVisual? item)) return;
        if (ReferenceEquals(host, _compactHighlightedHost)) ClearCompactRowHighlight();
        ResetItemBounce(item);
        ClearDockToolTip(host, item);
        host.SizeChanged -= HoverElement_SizeChanged;
        item.Content.SizeChanged -= HoverElement_SizeChanged;
        ResetHoverVisual(host, item);
        RemoveHoverProxy(item);
        _hoverGeometryDirty = true;
        if (ReferenceEquals(_hoverPressedHost, host)) _hoverPressedHost = null;
        if (_hoverItems.Count == 0) ResetHoverWave();
    }

    private void HoverElement_SizeChanged(object sender, SizeChangedEventArgs args) => InvalidateHoverGeometry();

    private void InvalidateHoverGeometry()
    {
        _hoverGeometryDirty = _hoverTargetsDirty = true;
        WakeHoverWave();
    }

    private void HoverLayout_Updated(object? sender, object args)
    {
        if (_applyingDockLayout) return;
        if (_hoverLayoutPending)
        {
            if (IsDock) return; // The queued current-layout commit owns this transition.
            _hoverLayoutPending = false;
            _hoverGeometryDirty = true;
        }
        if (!_hoverGeometryDirty || _closing || !_expanded || !UseHoverPresentation ||
            !UseCustomAnimations && !IsCompactList || HoverInteractionBusy || !WindowRoot.IsLoaded) return;
        UpdateHoverGeometry();
        if (UseCustomAnimations) RenderHoverPresentation();
        else RefreshCompactRowHighlight();
    }

    private void HoverViewport_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        Point pointer = args.GetCurrentPoint(ItemsScrollView).Position;
        UpdateCompactRowHighlight(pointer);
        if (UseHoverPresentation && _itemReorderSession is { State: ItemDragState.Pressed }) return;
        RefreshHoverAvailability();
        // A proxy becoming hit-testable can synthesize a routed pointer event.
        // Use the fixed viewport and physical cursor for Dock input, never a
        // transformed presentation as a new source of pointer coordinates.
        if (IsDock && NativeMethods.GetCursorPos(out var screen) && TryScreenToHoverViewport(screen, out Point fixedPoint))
        {
            pointer = fixedPoint;
            if (!IsPointerOverDockOwner(NativeMethods.WindowFromPoint(screen), screen))
            {
                CloseDockToolTip();
                ReturnHoverWave();
                return;
            }
        }
        UpdateDockToolTip(pointer);
        if (!CanActivateHover(pointer))
        {
            ReturnHoverWave();
            return;
        }
        if (!_hoverWave.MovePointer(new Vector2((float)pointer.X, (float)pointer.Y))) return;
        _hoverAnchor.Retarget(new((float)pointer.X, (float)pointer.Y), atRest: !HasHoverMotionToFinish());
        _hoverTargetsDirty = true;
        WakeHoverWave();
    }

    private void HoverViewport_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        Point pointer = args.GetCurrentPoint(ItemsScrollView).Position;
        if (IsDock && NativeMethods.GetCursorPos(out var screen) && TryScreenToHoverViewport(screen, out Point fixedPoint))
            pointer = fixedPoint;
        if (args.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
            UpdateCompactRowHighlight(pointer);
        UpdateDockToolTip(pointer);
        // Child exits also route here; crossing a cell must not erase the wave.
        if (!CanActivateHover(pointer)) ReturnHoverWave();
    }

    private void HoverViewport_ViewChanged(ScrollView sender, object args)
    {
        // Compact rows move beneath the same viewport pointer; keep spring progress and update targets.
        if (!IsCompactList) ResetHoverWave();
        _hoverGeometryDirty = true;
        RefreshHoverAvailability();
    }

    private void HoverViewport_StateChanged(ScrollView sender, object args)
    {
        if (!IsCompactList && sender.State != ScrollingInteractionState.Idle) ResetHoverWave();
        RefreshHoverAvailability();
    }

    private void UpdateCompactScrollHover(Point pointer)
    {
        UpdateCompactRowHighlight(pointer);
        RefreshHoverAvailability();
        if (!CanActivateHover(pointer))
        {
            ReturnHoverWave();
            return;
        }
        if (!_hoverWave.MovePointer(new((float)pointer.X, (float)pointer.Y))) return;
        _hoverAnchor.Retarget(new((float)pointer.X, (float)pointer.Y), atRest: !HasHoverMotionToFinish());
        _hoverTargetsDirty = true;
        WakeHoverWave();
    }

    private void ReturnHoverWave()
    {
        if (!_hoverWave.HasPointer && _hoverPressedHost is null && !HasHoverMotionToFinish()) return;
        _hoverWave.Leave();
        _hoverPressedHost = null;
        _hoverTargetsDirty = true;
        WakeHoverWave();
    }

    private void SetHoverPressed(Border host, bool pressed)
    {
        if (UseHoverPresentation)
        {
            // Preserve the clicked presentation until drag activation. The original host
            // owns capture, so quick taps/double taps do not chase a moving visual.
            if (pressed) _hoverPressedHost = host;
            else if (ReferenceEquals(_hoverPressedHost, host)) _hoverPressedHost = null;
            _hoverTargetsDirty = true;
            WakeHoverWave();
            return;
        }
        if (pressed)
        {
            _hoverWave.Leave();
            _hoverPressedHost = host;
        }
        else if (ReferenceEquals(_hoverPressedHost, host)) _hoverPressedHost = null;
        _hoverTargetsDirty = true;
        WakeHoverWave();
    }

    private void WakeHoverWave()
    {
        if (_closing || _hoverItems.Count == 0 || !UseCustomAnimations) return;
        // Icon mode still animates a press and its release, but ordinary pointer
        // movement must not subscribe a frame just to render an unchanged scale.
        if (!UseHoverWave && _hoverPressedHost is null && !HasHoverMotionToFinish()) return;
        if (!_hoverGeometryDirty && !_hoverWave.HasPointer && _hoverPressedHost is null && !HasHoverMotionToFinish()) return;
        if (!_hoverRenderingSubscribed)
        {
            _hoverLastFrame = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += HoverWave_Rendering;
            _hoverRenderingSubscribed = true;
            _hoverWaveClockBoost ??= CompositorClockBoost.Acquire();
        }
        UpdateHoverPointerGuard();
    }

    private bool HasHoverMotionToFinish()
    {
        if (IsDock) return _dockHoverMotion.IsActive || _dockHoverMotion.Scale != 1;
        foreach (HoverItemVisual item in _hoverItems.Values)
        {
            if (item.Motion.IsActive || item.Motion.Scale != 1) return true;
        }
        return false;
    }

    private void HoverWave_Rendering(object? sender, object args)
    {
        if (!HoverViewportVisible || !UseCustomAnimations)
        {
            ResetHoverWave();
            return;
        }
        if (UseHoverPresentation && _itemReorderSession is { State: ItemDragState.Pressed })
        {
            StopHoverRendering();
            return;
        }
        bool interactionBusy = HoverInteractionBusy;
        if (UseHoverPresentation && interactionBusy)
        {
            ResetHoverWave();
            return;
        }
        // Also guard against an interaction starting between input and this frame.
        _hoverWave.SetAvailability(UseHoverWave, interactionBusy);
        long now = Stopwatch.GetTimestamp();
        double seconds = Math.Clamp(Stopwatch.GetElapsedTime(_hoverLastFrame, now).TotalSeconds, 1d / 480d, 1d / 30d);
        _hoverLastFrame = now;
        if (_hoverGeometryDirty) UpdateHoverGeometry();
        bool active;
        if (IsDock)
        {
            _dockHoverMotion.Retarget(_hoverWave.Pointer, HoverMaximumScale);
            active = !_dockHoverMotion.Step(seconds);
        }
        else
        {
            active = !_hoverAnchor.Step(seconds);
            bool allowZIndex = _itemReorderSession is null && !_shellDragActive && !_nativeItemMotionRenderingSubscribed;
            bool usePresentation = UseHoverPresentation;
            bool compactList = IsCompactList;
            float maxScale = HoverMaximumScale;
            bool refreshTargets = _hoverTargetsDirty || interactionBusy;
            foreach ((Border host, HoverItemVisual item) in _hoverItems)
            {
                if (!item.NearViewport)
                {
                    if (item.Presented || item.Motion.IsActive || item.AppliedContentScale != 1 || item.Raised)
                        ResetHoverVisual(host, item);
                    continue;
                }
                if (refreshTargets)
                {
                    bool pressed = ReferenceEquals(host, _hoverPressedHost) && !_shellDragActive &&
                        _itemReorderSession is { State: ItemDragState.Pressed };
                    item.Motion.Retarget(pressed && !compactList ? .97f :
                        _hoverWave.GetTargetScale(item.Center, item.Pitch, compactList, maxScale));
                }
                active |= !item.Motion.Step(seconds);
                float scale = item.Motion.Scale;
                if (!usePresentation && item.AppliedContentScale != scale)
                {
                    item.Content.Scale = new Vector3(scale, scale, 1);
                    item.AppliedContentScale = scale;
                }
                if (allowZIndex)
                {
                    int layer = (int)Math.Round(Math.Max(0, scale - 1) * 1000);
                    if (item.AppliedHostZIndex != layer)
                    {
                        Canvas.SetZIndex(host, layer);
                        item.AppliedHostZIndex = layer;
                    }
                    item.Raised = layer > 0;
                }
            }
        }
        if (UseHoverPresentation) RenderHoverPresentation();
        _hoverTargetsDirty = false;
        if (!active) StopHoverRendering();
        UpdateHoverPointerGuard();
    }

    private void RenderHoverPresentation()
    {
        if (_hoverLayoutPending) return;
        int count = _hoverLayoutCount;
        if (count > 0)
        {
            for (int i = 0; i < count; i++)
                _hoverLayoutInput[i] = new(_hoverLayout[i].Value.BaseBounds, _hoverLayout[i].Value.Motion.Scale);
            if (IsDock)
                DockHoverLayout.CalculateInto(_hoverLayoutInput, _dockHoverMotion.Position, (float)_dockGeometry.Pitch,
                    _dockHoverMotion.Scale, HorizontalHover, _hoverLayoutPoses);
            else
                HoverLayoutMath.CalculateInto(_hoverLayoutInput,
                    HorizontalHover ? _hoverAnchor.Position.X : _hoverAnchor.Position.Y, HorizontalHover, IsCompactList,
                    _hoverLayoutPoses, _hoverLayoutCenters, _hoverLayoutScales);
            if (IsDock) HoverLayoutMath.ConstrainDockPoses(_hoverLayoutPoses, _hoverSurfaceBounds, _hoverSurfaceRadius, HorizontalHover);
            for (int i = 0; i < count; i++)
            {
                var (host, item) = _hoverLayout[i];
                if (item.Proxy is not Border proxy) continue;
                if (proxy.Child is not FrameworkElement child || !HoverLayoutMath.IsPresentationReady(
                    item.BaseBounds.Width, item.BaseBounds.Height, proxy.ActualWidth, proxy.ActualHeight,
                    child.ActualWidth, child.ActualHeight))
                {
                    HideHoverProxy(item);
                    _hoverGeometryDirty = true;
                    continue;
                }
                item.Translation = _hoverLayoutPoses[i].Translation;
                item.RenderScale = _hoverLayoutPoses[i].Scale;
                item.VisibleBounds = HoverLayoutMath.VisibleAfterTransform(item.BaseBounds,
                    item.RenderScale, item.Translation, IsCompactList, _hoverViewportBounds);
                if (IsDock && !_dockLayoutCommit.CanPresent(_hoverGeometryVersion,
                    dimensionsReady: true, item.VisibleBounds))
                {
                    HideHoverProxy(item);
                    continue;
                }
                if (item.AppliedProxyScale != item.RenderScale)
                {
                    proxy.Scale = new Vector3(item.RenderScale, item.RenderScale, 1);
                    item.AppliedProxyScale = item.RenderScale;
                }
                if (item.AppliedProxyTranslation != item.Translation)
                {
                    proxy.Translation = new Vector3(item.Translation, 0);
                    item.AppliedProxyTranslation = item.Translation;
                }
                HoverRect clip = HoverLayoutMath.LocalClip(item.VisibleBounds, item.BaseBounds,
                    item.RenderScale, item.Translation, IsCompactList);
                if (!item.HasAppliedProxyClip || item.AppliedProxyClip != clip)
                {
                    item.Clip.Rect = new Rect(clip.X, clip.Y, clip.Width, clip.Height);
                    item.AppliedProxyClip = clip;
                    item.HasAppliedProxyClip = true;
                }
                int zIndex = (int)Math.Round((item.RenderScale - 1) * 1000);
                if (item.AppliedProxyZIndex != zIndex)
                {
                    Canvas.SetZIndex(proxy, zIndex);
                    item.AppliedProxyZIndex = zIndex;
                }
                if (!item.Presented)
                {
                    Mirror(proxy, UIElement.OpacityProperty, host, nameof(UIElement.Opacity));
                    proxy.IsHitTestVisible = true;
                    item.Content.Opacity = 0;
                    SetHoverProxyPresented(item, true);
                }
                if (item.Bounce.IsActive) ApplyItemBounce(item);
            }
        }
        RefreshCompactRowHighlight();
        UpdateDockToolTipPosition();
        UpdateDockRunningVisuals();
    }

    private void UpdateHoverGeometry()
    {
        if (_hoverLayoutPending) return;
        // A reorder/FLIP temporarily transforms the host itself. Keep the cache
        // dirty until its layout is stable; hover must never retain that offset.
        if (_itemReorderSession is { State: not ItemDragState.Pressed } ||
            _nativeItemMotionRenderingSubscribed || _itemDragLanding || _shellDragActive ||
            _shellPromotionPending || _shellDropFinalizing) return;
        double rasterScale = WindowRoot.XamlRoot?.RasterizationScale ?? 1;
        _hoverViewportBounds = new(0, 0, (float)ItemsScrollView.ActualWidth, (float)ItemsScrollView.ActualHeight);
        _hoverViewportOrigin = ItemsScrollView.TransformToVisual(ExpandedContentLayer).TransformPoint(new Point());
        _hoverViewportWindowOrigin = ItemsScrollView.TransformToVisual(WindowRoot).TransformPoint(new Point());
        _hoverViewportWindowOriginValid = true;
        InvalidateHorizontalDockBounceViewportTop();
        _hoverSurfaceBounds = new(-(float)_hoverViewportOrigin.X, -(float)_hoverViewportOrigin.Y,
            (float)ExpandedContentLayer.ActualWidth, (float)ExpandedContentLayer.ActualHeight);
        _hoverSurfaceRadius = IsDock ? CurrentDockCornerRadius : (float)SnapDip(ExpandedCornerRadiusDip);
        Rect baseClipRect = new(_hoverViewportOrigin.X, _hoverViewportOrigin.Y,
            _hoverViewportBounds.Width, _hoverViewportBounds.Height);
        if (!_hasAppliedCompactHighlightClipRect || _appliedCompactHighlightClipRect != baseClipRect)
        {
            _compactHighlightClip.Rect = baseClipRect;
            _appliedCompactHighlightClipRect = baseClipRect;
            _hasAppliedCompactHighlightClipRect = true;
        }
        Rect viewportClipRect = baseClipRect;
        if (HorizontalHover)
        {
            double gutter = _dockOverflowPixels / rasterScale;
            viewportClipRect = new Rect(_hoverViewportOrigin.X, _hoverViewportOrigin.Y - gutter,
                _hoverViewportBounds.Width, _hoverViewportBounds.Height + gutter);
        }
        if (!_hasAppliedHoverViewportClipRect || _appliedHoverViewportClipRect != viewportClipRect)
        {
            _hoverViewportClip.Rect = viewportClipRect;
            _appliedHoverViewportClipRect = viewportClipRect;
            _hasAppliedHoverViewportClipRect = true;
        }
        _hoverButtonBounds = default;
        if (CollapseButton.Visibility == Visibility.Visible)
        {
            Point button = CollapseButton.TransformToVisual(ItemsScrollView).TransformPoint(new Point());
            _hoverButtonBounds = new((float)button.X, (float)button.Y,
                (float)CollapseButton.ActualWidth, (float)CollapseButton.ActualHeight);
        }
        _compactHoverBounds = default;
        double itemGap = GetItemLayoutGapDip();
        bool compactList = IsCompactList;
        bool isDock = IsDock;
        bool usePresentation = UseHoverPresentation;
        foreach ((Border host, HoverItemVisual item) in _hoverItems)
        {
            item.NearViewport = false;
            if (host.ActualWidth <= 0 || host.ActualHeight <= 0 || host.XamlRoot is null)
            {
                HideHoverProxy(item);
                continue;
            }
            Point origin;
            try { origin = host.TransformToVisual(ItemsScrollView).TransformPoint(new Point()); }
            catch (InvalidOperationException) { HideHoverProxy(item); continue; }
            double width = host.ActualWidth;
            double height = host.ActualHeight;
            HoverRect baseline = new((float)(origin.X + item.Content.ActualOffset.X),
                (float)(origin.Y + item.Content.ActualOffset.Y), (float)item.Content.ActualWidth, (float)item.Content.ActualHeight);
            item.BaseBounds = baseline;
            item.RowBounds = new((float)origin.X, (float)origin.Y, (float)width, (float)height);
            if (compactList)
            {
                HoverRect row = item.RowBounds.Intersect(_hoverViewportBounds);
                if (!row.IsEmpty)
                {
                    float left = _compactHoverBounds.IsEmpty ? row.X : Math.Min(_compactHoverBounds.X, row.X);
                    float top = _compactHoverBounds.IsEmpty ? row.Y : Math.Min(_compactHoverBounds.Y, row.Y);
                    float right = _compactHoverBounds.IsEmpty ? row.Right : Math.Max(_compactHoverBounds.Right, row.Right);
                    float bottom = _compactHoverBounds.IsEmpty ? row.Bottom : Math.Max(_compactHoverBounds.Bottom, row.Bottom);
                    _compactHoverBounds = new(left, top, right - left, bottom - top);
                }
            }
            item.IconBounds = new(baseline.X + item.Icon.ActualOffset.X, baseline.Y + item.Icon.ActualOffset.Y,
                (float)item.Icon.ActualWidth, (float)item.Icon.ActualHeight);
            item.VisibleBaseBounds = baseline.Intersect(_hoverViewportBounds);
            item.NearViewport = !item.VisibleBaseBounds.IsEmpty;
            if (usePresentation && item.NearViewport)
            {
                EnsureHoverProxy(host, item);
                Border proxy = item.Proxy!;
                if (!ReferenceEquals(proxy.DataContext, host.DataContext)) proxy.DataContext = host.DataContext;
                if (!ReferenceEquals(proxy.Tag, host.Tag)) proxy.Tag = host.Tag;
                if (!isDock)
                {
                    object? tip = ToolTipService.GetToolTip(host);
                    if (!ReferenceEquals(item.AppliedToolTip, tip))
                    {
                        ToolTipService.SetToolTip(proxy, tip);
                        item.AppliedToolTip = tip;
                    }
                }
                if (proxy.Width != baseline.Width) proxy.Width = baseline.Width;
                if (proxy.Height != baseline.Height) proxy.Height = baseline.Height;
                if (proxy.Child is FrameworkElement content)
                {
                    if (content.Width != baseline.Width) content.Width = baseline.Width;
                    if (content.Height != baseline.Height) content.Height = baseline.Height;
                }
                double proxyLeft = _hoverViewportOrigin.X + baseline.X;
                double proxyTop = _hoverViewportOrigin.Y + baseline.Y;
                if (item.AppliedProxyLeft != proxyLeft)
                {
                    Canvas.SetLeft(proxy, proxyLeft);
                    item.AppliedProxyLeft = proxyLeft;
                }
                if (item.AppliedProxyTop != proxyTop)
                {
                    Canvas.SetTop(proxy, proxyTop);
                    item.AppliedProxyTop = proxyTop;
                }
                Vector3 proxyCenter = new(compactList ? 0 : baseline.Width / 2, baseline.Height / 2, 0);
                if (item.AppliedProxyCenter != proxyCenter)
                {
                    proxy.CenterPoint = proxyCenter;
                    item.AppliedProxyCenter = proxyCenter;
                }
            }
            else HideHoverProxy(item);
            item.Center = new Vector2((float)(origin.X + width / 2), (float)(origin.Y + height / 2));
            item.Pitch = new Vector2((float)(width + itemGap), (float)(height + itemGap));
            Vector3 contentCenter = new(compactList ? 0 : (float)(item.Content.ActualWidth / 2),
                (float)(item.Content.ActualHeight / 2), 0);
            if (item.AppliedContentCenter != contentCenter)
            {
                item.Content.CenterPoint = contentCenter;
                item.AppliedContentCenter = contentCenter;
            }
        }
        int layoutCount = 0;
        if (_hoverLayout.Length < _hoverItems.Count)
        {
            int capacity = Math.Max(4, _hoverItems.Count);
            _hoverLayout = new KeyValuePair<Border, HoverItemVisual>[capacity];
            _hoverLayoutKeys = new float[capacity];
        }
        bool horizontal = HorizontalHover;
        foreach (KeyValuePair<Border, HoverItemVisual> pair in _hoverItems)
        {
            if (!pair.Value.NearViewport) continue;
            _hoverLayout[layoutCount] = pair;
            _hoverLayoutKeys[layoutCount] = horizontal ? pair.Value.BaseBounds.Center.X : pair.Value.BaseBounds.Center.Y;
            layoutCount++;
        }
        int clearEnd = Math.Min(_hoverLayoutCount, _hoverLayout.Length);
        if (layoutCount < clearEnd)
            Array.Clear(_hoverLayout, layoutCount, clearEnd - layoutCount);
        _hoverLayoutCount = layoutCount;
        // Stable in-place insertion sort: zero heap allocations, O(N) on nearly-ordered layouts.
        for (int i = 1; i < layoutCount; i++)
        {
            float key = _hoverLayoutKeys[i];
            KeyValuePair<Border, HoverItemVisual> entry = _hoverLayout[i];
            int j = i - 1;
            while (j >= 0 && _hoverLayoutKeys[j] > key)
            {
                _hoverLayoutKeys[j + 1] = _hoverLayoutKeys[j];
                _hoverLayout[j + 1] = _hoverLayout[j];
                j--;
            }
            _hoverLayoutKeys[j + 1] = key;
            _hoverLayout[j + 1] = entry;
        }
        if (_hoverHitRegions.Length < layoutCount)
            _hoverHitRegions = new HoverHitRegion[Math.Max(4, layoutCount)];
        if (_hoverLayoutInput.Length != layoutCount)
        {
            // Ping-pong previous length buffers so oscillating N <-> N+1 viewport counts during
            // 240Hz compact list smooth scroll incur zero steady-state allocations.
            if (_hoverLayoutAltInput.Length == layoutCount)
            {
                (_hoverLayoutInput, _hoverLayoutAltInput) = (_hoverLayoutAltInput, _hoverLayoutInput);
                (_hoverLayoutPoses, _hoverLayoutAltPoses) = (_hoverLayoutAltPoses, _hoverLayoutPoses);
                (_hoverLayoutCenters, _hoverLayoutAltCenters) = (_hoverLayoutAltCenters, _hoverLayoutCenters);
                (_hoverLayoutScales, _hoverLayoutAltScales) = (_hoverLayoutAltScales, _hoverLayoutScales);
            }
            else
            {
                _hoverLayoutAltInput = _hoverLayoutInput;
                _hoverLayoutAltPoses = _hoverLayoutPoses;
                _hoverLayoutAltCenters = _hoverLayoutCenters;
                _hoverLayoutAltScales = _hoverLayoutScales;
                _hoverLayoutInput = layoutCount == 0 ? [] : new HoverLayoutItem[layoutCount];
                _hoverLayoutPoses = layoutCount == 0 ? [] : new HoverLayoutPose[layoutCount];
                _hoverLayoutCenters = layoutCount == 0 ? [] : new float[layoutCount];
                _hoverLayoutScales = layoutCount == 0 ? [] : new float[layoutCount];
            }
        }
        _hoverGeometryDirty = false;
        _hoverGeometryVersion = _dockLayoutCommit.Version;
        _hoverTargetsDirty = true;
    }

    private void ResetHoverWave()
    {
        ClearCompactRowHighlight(forgetPointer: !HoverViewportVisible || !IsCompactList);
        if (!HoverViewportVisible || IsCompactList || !UseCustomAnimations ||
            _itemReorderSession is { IsActive: true } || _widgetDragging || _canvasResize is not null)
            ResetItemBounces();
        CloseDockToolTip();
        _hoverWave.Leave();
        _hoverAnchor.Reset();
        _dockHoverMotion.Reset();
        _hoverPressedHost = null;
        foreach ((Border host, HoverItemVisual item) in _hoverItems) ResetHoverVisual(host, item);
        _hoverGeometryDirty = _hoverTargetsDirty = true;
        _hoverViewportWindowOriginValid = false;
        InvalidateHorizontalDockBounceViewportTop();
        StopHoverRendering();
        _hoverPointerGuard?.Stop();
        UpdateHoverInputRegion();
    }

    private void ResetHoverVisual(Border host, HoverItemVisual item)
    {
        HideHoverProxy(item);
        item.Motion.Reset();
        if (item.AppliedContentScale != 1)
        {
            item.Content.Scale = Vector3.One;
            item.AppliedContentScale = 1;
        }
        item.Content.Translation = Vector3.Zero;
        if (item.Raised && Canvas.GetZIndex(host) < 1000)
        {
            Canvas.SetZIndex(host, 0);
            item.AppliedHostZIndex = 0;
        }
        else if (item.Raised)
        {
            item.AppliedHostZIndex = int.MinValue;
        }
        item.Raised = false;
    }

    private void StopHoverRendering()
    {
        if (_hoverRenderingSubscribed) CompositionTarget.Rendering -= HoverWave_Rendering;
        _hoverRenderingSubscribed = false;
        _hoverWaveClockBoost?.Dispose();
        _hoverWaveClockBoost = null;
        _hoverLastFrame = 0;
    }

    private void UpdateHoverPointerGuard()
    {
        if (_hoverPointerGuard is null) return;
        TimeSpan interval = TimeSpan.FromMilliseconds(_host.State.GlobalSettings.PerformanceTuning.PointerPollMilliseconds);
        if (_hoverPointerGuard.Interval != interval) _hoverPointerGuard.Interval = interval;
        if (UseHoverPresentation && (_hoverWave.HasPointer || HasHoverMotionToFinish() || _visibleDockToolTip is { IsOpen: true }))
        {
            if (!_hoverPointerGuard.IsRunning) _hoverPointerGuard.Start();
        }
        else _hoverPointerGuard.Stop();
    }

    private void HoverPointerGuard_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!HoverViewportVisible || (!UseHoverWave && _visibleDockToolTip is not { IsOpen: true }) || HoverInteractionBusy)
        {
            if (HoverViewportVisible && UseHoverWave && _itemReorderSession is { State: ItemDragState.Pressed }) return;
            ResetHoverWave();
            return;
        }
        bool hadPointer = _hoverWave.HasPointer;
        bool sampled = NativeMethods.GetCursorPos(out var screen);
        bool inside = sampled && TryScreenToHoverViewport(screen, out Point point) && CanActivateHover(point);
        IntPtr hit = sampled ? NativeMethods.WindowFromPoint(screen) : IntPtr.Zero;
        bool overOwner = IsPointerOverDockOwner(hit, screen);
        if (!overOwner || !inside) CloseDockToolTip();
        if (hadPointer && !_hoverWave.ObservePointerPresence(overOwner, inside)) ReturnHoverWave();
        UpdateHoverPointerGuard();
    }

    private bool IsInsideBaseItemContent(Border host, Point pointer)
    {
        if (!_hoverItems.TryGetValue(host, out HoverItemVisual? item)) return false;
        // ActualOffset/ActualSize describe layout, unaffected by the visual scale.
        Vector3 offset = item.Content.ActualOffset;
        return pointer.X >= offset.X && pointer.Y >= offset.Y &&
            pointer.X <= offset.X + item.Content.ActualWidth && pointer.Y <= offset.Y + item.Content.ActualHeight;
    }

    private void DisposeHoverWave()
    {
        _visibleDockToolTip?.Dispose();
        _visibleDockToolTip = null;
        ResetItemBounces();
        ResetHoverWave();
        if (_hoverPointerEnteredHandler is not null)
            WindowRoot.RemoveHandler(UIElement.PointerEnteredEvent, _hoverPointerEnteredHandler);
        if (_hoverPointerMovedHandler is not null)
            WindowRoot.RemoveHandler(UIElement.PointerMovedEvent, _hoverPointerMovedHandler);
        if (_hoverPointerExitedHandler is not null)
            WindowRoot.RemoveHandler(UIElement.PointerExitedEvent, _hoverPointerExitedHandler);
        _hoverPointerEnteredHandler = null;
        _hoverPointerMovedHandler = null;
        _hoverPointerExitedHandler = null;
        ItemsScrollView.PointerExited -= HoverViewport_PointerExited;
        ItemsScrollView.ViewChanged -= HoverViewport_ViewChanged;
        ItemsScrollView.StateChanged -= HoverViewport_StateChanged;
        ItemsRepeater.LayoutUpdated -= HoverLayout_Updated;
        _uiSettings.AnimationsEnabledChanged -= HoverAnimationsEnabledChanged;
        foreach ((Border host, HoverItemVisual item) in _hoverItems)
        {
            ClearDockToolTip(host, item);
            host.SizeChanged -= HoverElement_SizeChanged;
            item.Content.SizeChanged -= HoverElement_SizeChanged;
            RemoveHoverProxy(item);
        }
        _hoverItems.Clear();
        if (_hoverLayoutCount > 0) Array.Clear(_hoverLayout, 0, _hoverLayoutCount);
        _hoverLayoutCount = 0;
        _hoverViewportWindowOriginValid = false;
        _hasAppliedHoverViewportClipRect = false;
        _hasAppliedCompactHighlightClipRect = false;
        _presentedHoverProxyCount = 0;
        if (_hoverPointerGuard is not null) _hoverPointerGuard.Tick -= HoverPointerGuard_Tick;
        _hoverPointerGuard = null;
        if (_hoverPresentationLayer is not null) ExpandedContentLayer.Children.Remove(_hoverPresentationLayer);
        _hoverPresentationLayer = null;
        if (_compactHighlightLayer is not null) ExpandedContentLayer.Children.Remove(_compactHighlightLayer);
        _compactHighlightLayer = null;
        _compactHighlight = null;
        ResetCompactHighlightCache();
    }
}
