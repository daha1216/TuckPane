using System.Numerics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TuckPane.Controls;
using TuckPane.Core;
using TuckPane.Services;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    private Canvas? _hoverPresentationLayer;
    private readonly RectangleGeometry _hoverViewportClip = new();
    private HoverRect _hoverViewportBounds, _hoverSurfaceBounds, _hoverButtonBounds;
    private HoverRect _compactHoverBounds;
    private float _hoverSurfaceRadius;
    private Point _hoverViewportOrigin;
    private Point? _hoverPressOffset;
    private Vector3 _hoverDragOriginOffset;
    private UIElement? _itemPointerCaptureOwner;
    private Microsoft.UI.Xaml.Input.Pointer? _itemCapturedPointer;
    private readonly DockLayoutCommit _dockLayoutCommit = new();
    private readonly List<NativeMethods.RECT> _dockBounceRegionRects = new();
    private readonly StringBuilder _dockRegionKeyBuilder = new(64);
    private long _hoverGeometryVersion;
    private int _presentedHoverProxyCount;
    private bool UseHoverPresentation => IsDock || IsCompactList;
    private bool HorizontalHover => IsDock && _definition.DockOrientation == Models.DockOrientation.Horizontal;
    private bool HasHoverPresentation => _presentedHoverProxyCount > 0;

    private void SetHoverProxyPresented(HoverItemVisual item, bool presented)
    {
        if (item.Presented == presented) return;
        item.Presented = presented;
        if (presented) _presentedHoverProxyCount++;
        else _presentedHoverProxyCount = Math.Max(0, _presentedHoverProxyCount - 1);
    }

    private void BeginHoverLayoutChange()
    {
        ResetItemBounces();
        _hoverLayoutPending = true;
        ResetHoverWave();
        if (!IsDock) return;
        long version = _dockLayoutCommit.Request();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_closing || version != _dockLayoutCommit.Version) return;
            // Finish source and viewport layout before measuring the replacement tree.
            // SizeChanged may request a newer layout while this call drains layout.
            WindowRoot.UpdateLayout();
            if (!_dockLayoutCommit.Complete(version)) return;
            _hoverLayoutPending = false;
            InvalidateHoverGeometry();
            UpdateDockInputRegion();
            if (!HoverViewportVisible || !UseCustomAnimations || HoverInteractionBusy) return;
            UpdateHoverGeometry();
            RenderHoverPresentation();
        });
    }

    private static void Mirror(FrameworkElement target, DependencyProperty property, FrameworkElement source, string path) =>
        target.SetBinding(property, new Binding { Source = source, Path = new PropertyPath(path), Mode = BindingMode.OneWay });

    // This small presentation tree shares the existing image sources. No catalog/icon requests or item ownership.
    private static FrameworkElement CloneHoverContent(FrameworkElement source, HoverItemVisual item)
    {
        FrameworkElement copy;
        switch (source)
        {
            case OrganizerPreviewControl preview:
                var previewCopy = new OrganizerPreviewControl();
                previewCopy.SetCompactVisual(preview.IsCompactVisual);
                for (int i = 0; i < preview.Images.Count; i++)
                    Mirror(previewCopy.Images[i], Image.SourceProperty, preview.Images[i], nameof(Image.Source));
                Mirror(previewCopy.EmptyStateIcon, UIElement.VisibilityProperty, preview.EmptyStateIcon, nameof(Visibility));
                Mirror(previewCopy.EmptyStateIcon, FontIcon.FontSizeProperty, preview.EmptyStateIcon, nameof(FontIcon.FontSize));
                copy = previewCopy;
                break;
            case StackPanel stack:
                var stackCopy = new StackPanel { Orientation = stack.Orientation, Spacing = stack.Spacing };
                Mirror(stackCopy, StackPanel.OrientationProperty, stack, nameof(StackPanel.Orientation));
                Mirror(stackCopy, StackPanel.SpacingProperty, stack, nameof(StackPanel.Spacing));
                for (int i = 0; i < stack.Children.Count; i++)
                {
                    if (stack.Children[i] is FrameworkElement child)
                        stackCopy.Children.Add(CloneHoverContent(child, item));
                }
                copy = stackCopy;
                break;
            case Grid grid:
                var gridCopy = new Grid();
                for (int i = 0; i < grid.Children.Count; i++)
                {
                    if (grid.Children[i] is not FrameworkElement child || child.Name == "ItemNameShadowHost") continue;
                    gridCopy.Children.Add(CloneHoverContent(child, item));
                }
                copy = gridCopy;
                break;
            case Image image:
                var imageCopy = new Image { Stretch = image.Stretch };
                Mirror(imageCopy, Image.SourceProperty, image, nameof(Image.Source));
                copy = imageCopy;
                break;
            case FontIcon icon:
                var iconCopy = new FontIcon { FontFamily = icon.FontFamily, FontSize = icon.FontSize };
                Mirror(iconCopy, FontIcon.FontSizeProperty, icon, nameof(FontIcon.FontSize));
                Mirror(iconCopy, FontIcon.GlyphProperty, icon, nameof(FontIcon.Glyph));
                Mirror(iconCopy, FontIcon.ForegroundProperty, icon, nameof(FontIcon.Foreground));
                copy = iconCopy;
                break;
            case TextBlock text:
                var textCopy = new TextBlock
                {
                    FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight,
                    TextAlignment = text.TextAlignment, TextTrimming = text.TextTrimming,
                    TextWrapping = text.TextWrapping, MaxWidth = text.MaxWidth
                };
                Mirror(textCopy, TextBlock.TextProperty, text, nameof(TextBlock.Text));
                Mirror(textCopy, TextBlock.ForegroundProperty, text, nameof(TextBlock.Foreground));
                Mirror(textCopy, TextBlock.FontSizeProperty, text, nameof(TextBlock.FontSize));
                Mirror(textCopy, TextBlock.MaxWidthProperty, text, nameof(TextBlock.MaxWidth));
                Mirror(textCopy, TextBlock.TextAlignmentProperty, text, nameof(TextBlock.TextAlignment));
                copy = textCopy;
                break;
            default:
                throw new InvalidOperationException($"Unsupported hover content: {source.GetType().Name}");
        }
        Mirror(copy, FrameworkElement.WidthProperty, source, nameof(FrameworkElement.Width));
        Mirror(copy, FrameworkElement.HeightProperty, source, nameof(FrameworkElement.Height));
        Mirror(copy, FrameworkElement.MarginProperty, source, nameof(FrameworkElement.Margin));
        Mirror(copy, FrameworkElement.HorizontalAlignmentProperty, source, nameof(FrameworkElement.HorizontalAlignment));
        Mirror(copy, FrameworkElement.VerticalAlignmentProperty, source, nameof(FrameworkElement.VerticalAlignment));
        copy.IsHitTestVisible = false;
        Mirror(copy, UIElement.VisibilityProperty, source, nameof(Visibility));
        if (ReferenceEquals(source, item.Icon))
        {
            item.ProxyIcon = copy;
            copy.Translation = item.Icon.Translation;
        }
        return copy;
    }

    private void EnsureHoverProxy(Border host, HoverItemVisual item)
    {
        if (item.Proxy is not null) return;
        if (_hoverPresentationLayer is null)
        {
            _hoverPresentationLayer = new Canvas { Clip = _hoverViewportClip };
            Canvas.SetZIndex(_hoverPresentationLayer, 1);
            ExpandedContentLayer.Children.Add(_hoverPresentationLayer);
        }
        var content = CloneHoverContent(item.Content, item);
        content.Width = item.BaseBounds.Width;
        content.Height = item.BaseBounds.Height;
        content.HorizontalAlignment = HorizontalAlignment.Left;
        content.VerticalAlignment = VerticalAlignment.Top;
        var proxy = new Border
        {
            Child = content, Width = item.BaseBounds.Width, Height = item.BaseBounds.Height,
            Background = _transparentItemBrush,
            DataContext = host.DataContext, Tag = host.Tag, Clip = item.Clip,
            Opacity = 0, IsHitTestVisible = false
        };
        // Prepare and measure once before replacing the original. Subsequent hover
        // enter/leave frames keep this same tree, including the resting frame.
        SizeChangedEventHandler onSizeChanged = (_, _) =>
        {
            if (_hoverItems.TryGetValue(host, out var current) && ReferenceEquals(current, item))
                InvalidateHoverGeometry();
        };
        if (!IsDock)
        {
            object? tip = ToolTipService.GetToolTip(host);
            ToolTipService.SetToolTip(proxy, tip);
            item.AppliedToolTip = tip;
        }
        PointerEventHandler onPointerPressed = (_, e) => ForwardHoverPress(host, item, e);
        PointerEventHandler onPointerMoved = (_, e) => Item_PointerMoved(host, e);
        PointerEventHandler onPointerReleased = (_, e) => Item_PointerReleased(host, e);
        PointerEventHandler onPointerCanceled = (_, e) => Item_PointerCanceled(host, e);
        PointerEventHandler onPointerCaptureLost = (_, e) =>
        {
            if (ReferenceEquals(_itemPointerCaptureOwner, proxy)) Item_PointerCaptureLost(host, e);
        };
        DoubleTappedEventHandler onDoubleTapped = (_, e) => ItemHost_DoubleTapped(host, e);
        TypedEventHandler<UIElement, ContextRequestedEventArgs> onContextRequested = (_, e) =>
        {
            if (e.Handled || !_hoverItems.ContainsKey(host)) return;
            if (e.TryGetPosition(ItemsScrollView, out Point point) && HoverItemAtViewportPoint(point) != host) return;
            ResetHoverWave();
            Item_ContextRequested(host, e);
        };
        proxy.SizeChanged += onSizeChanged;
        proxy.PointerPressed += onPointerPressed;
        proxy.PointerMoved += onPointerMoved;
        proxy.PointerReleased += onPointerReleased;
        proxy.PointerCanceled += onPointerCanceled;
        proxy.PointerCaptureLost += onPointerCaptureLost;
        proxy.PointerWheelChanged += ExpandedView_PointerWheelChanged;
        proxy.DoubleTapped += onDoubleTapped;
        proxy.ContextRequested += onContextRequested;
        item.DetachProxyHandlers = () =>
        {
            proxy.SizeChanged -= onSizeChanged;
            proxy.PointerPressed -= onPointerPressed;
            proxy.PointerMoved -= onPointerMoved;
            proxy.PointerReleased -= onPointerReleased;
            proxy.PointerCanceled -= onPointerCanceled;
            proxy.PointerCaptureLost -= onPointerCaptureLost;
            proxy.PointerWheelChanged -= ExpandedView_PointerWheelChanged;
            proxy.DoubleTapped -= onDoubleTapped;
            proxy.ContextRequested -= onContextRequested;
        };
        item.Proxy = proxy;
        item.AppliedProxyScale = float.NaN;
        item.AppliedProxyTranslation = new(float.NaN, float.NaN);
        item.HasAppliedProxyClip = false;
        item.AppliedProxyZIndex = int.MinValue;
        item.AppliedBounceY = float.NaN;
        item.ProxyClipClearedForBounce = false;
        item.AppliedProxyLeft = double.NaN;
        item.AppliedProxyTop = double.NaN;
        item.AppliedProxyCenter = new(float.NaN, float.NaN, float.NaN);
        ApplyItemBounce(item);
        _hoverPresentationLayer.Children.Add(proxy);
    }

    private void ForwardHoverPress(Border host, HoverItemVisual item, PointerRoutedEventArgs args)
    {
        if (args.Handled || !args.GetCurrentPoint(WindowRoot).Properties.IsLeftButtonPressed) return;
        Point point = args.GetCurrentPoint(ItemsScrollView).Position;
        if (HoverItemAtViewportPoint(point) != host) return;
        Vector2 pivot = IsCompactList ? new(item.BaseBounds.X, item.BaseBounds.Center.Y) : item.BaseBounds.Center;
        Vector2 baseline = pivot + (new Vector2((float)point.X, (float)point.Y) - item.Translation - pivot) / item.RenderScale;
        _hoverPressOffset = new Point(baseline.X - item.BaseBounds.X + item.Content.ActualOffset.X,
            baseline.Y - item.BaseBounds.Y + item.Content.ActualOffset.Y);
        try { BeginItemPointerPress(host, args, item.Proxy!); }
        finally { _hoverPressOffset = null; }
        args.Handled = true;
    }

    private void RemoveHoverProxy(HoverItemVisual item)
    {
        HideHoverProxy(item);
        item.DetachProxyHandlers?.Invoke();
        item.DetachProxyHandlers = null;
        if (item.Proxy is { } proxy)
        {
            proxy.ClearValue(UIElement.OpacityProperty);
            ToolTipService.SetToolTip(proxy, null);
            if (proxy.Child is FrameworkElement child)
                ClearHoverContentBindings(child);
            proxy.Child = null;
            proxy.DataContext = null;
            proxy.Tag = null;
            proxy.Clip = null;
            _hoverPresentationLayer?.Children.Remove(proxy);
        }
        item.Proxy = null;
        item.ProxyIcon = null;
        item.AppliedProxyScale = float.NaN;
        item.AppliedProxyTranslation = new(float.NaN, float.NaN);
        item.HasAppliedProxyClip = false;
        item.AppliedProxyZIndex = int.MinValue;
        item.ProxyClipClearedForBounce = false;
        item.AppliedProxyLeft = double.NaN;
        item.AppliedProxyTop = double.NaN;
        item.AppliedProxyCenter = new(float.NaN, float.NaN, float.NaN);
        item.AppliedToolTip = null;
    }

    private static void ClearHoverContentBindings(FrameworkElement element)
    {
        element.ClearValue(FrameworkElement.WidthProperty);
        element.ClearValue(FrameworkElement.HeightProperty);
        element.ClearValue(FrameworkElement.MarginProperty);
        element.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
        element.ClearValue(FrameworkElement.VerticalAlignmentProperty);
        element.ClearValue(UIElement.VisibilityProperty);
        switch (element)
        {
            case OrganizerPreviewControl preview:
                for (int i = 0; i < preview.Images.Count; i++)
                    preview.Images[i].ClearValue(Image.SourceProperty);
                preview.EmptyStateIcon.ClearValue(UIElement.VisibilityProperty);
                preview.EmptyStateIcon.ClearValue(FontIcon.FontSizeProperty);
                break;
            case StackPanel stack:
                stack.ClearValue(StackPanel.OrientationProperty);
                stack.ClearValue(StackPanel.SpacingProperty);
                for (int i = 0; i < stack.Children.Count; i++)
                {
                    if (stack.Children[i] is FrameworkElement child)
                        ClearHoverContentBindings(child);
                }
                break;
            case Grid grid:
                for (int i = 0; i < grid.Children.Count; i++)
                {
                    if (grid.Children[i] is FrameworkElement child)
                        ClearHoverContentBindings(child);
                }
                break;
            case Image image:
                image.ClearValue(Image.SourceProperty);
                break;
            case FontIcon icon:
                icon.ClearValue(FontIcon.FontSizeProperty);
                icon.ClearValue(FontIcon.GlyphProperty);
                icon.ClearValue(FontIcon.ForegroundProperty);
                break;
            case TextBlock text:
                text.ClearValue(TextBlock.TextProperty);
                text.ClearValue(TextBlock.ForegroundProperty);
                text.ClearValue(TextBlock.FontSizeProperty);
                text.ClearValue(TextBlock.MaxWidthProperty);
                text.ClearValue(TextBlock.TextAlignmentProperty);
                break;
        }
    }

    private void HideHoverProxy(HoverItemVisual item)
    {
        if (item.Proxy is { } proxy)
        {
            proxy.Opacity = 0;
            proxy.IsHitTestVisible = false;
        }
        SetHoverProxyPresented(item, false);
        item.Content.Opacity = 1;
        item.VisibleBounds = default;
        item.Translation = Vector2.Zero;
        item.RenderScale = 1;
        item.AppliedProxyScale = float.NaN;
        item.AppliedProxyTranslation = new(float.NaN, float.NaN);
        item.HasAppliedProxyClip = false;
        item.AppliedProxyZIndex = int.MinValue;
    }

    // Exclude the fixed bounce gutter from persisted position and drag geometry.
    private bool TryGetLogicalWindowRect(out NativeMethods.RECT bounds)
    {
        if (!NativeMethods.GetWindowRect(_hwnd, out bounds)) return false;
        if (IsDock) bounds.Top += _dockOverflowPixels;
        return true;
    }

    private Border? HoverItemAtViewportPoint(Point point)
    {
        if (_hoverGeometryDirty) UpdateHoverGeometry();
        Vector2 local = new((float)point.X, (float)point.Y);
        if (!InsideHoverRegion(point)) return null;
        int count = _hoverLayoutCount;
        for (int i = 0; i < count; i++)
        {
            // A sibling's ready proxy must not make an original item unclickable.
            // Hit testing follows the representation that is actually visible.
            HoverItemVisual item = _hoverLayout[i].Value;
            _hoverHitRegions[i] = new(item.RowBounds, item.BaseBounds, item.VisibleBounds,
                item.Translation, item.RenderScale, item.Presented);
        }
        int index = HoverLayoutMath.HitTestItems(_hoverHitRegions.AsSpan(0, count), IsCompactList,
            _hoverViewportBounds, _hoverButtonBounds, local);
        return index < 0 ? null : _hoverLayout[index].Key;
    }

    private bool InsideHoverRegion(Point point)
    {
        Vector2 local = new((float)point.X, (float)point.Y);
        return _hoverViewportBounds.Contains(local) && !_hoverButtonBounds.Contains(local) &&
            HoverLayoutMath.ContainsRounded(_hoverSurfaceBounds, _hoverSurfaceRadius, local);
    }

    private bool CanActivateHover(Point point)
    {
        if (_hoverGeometryDirty) UpdateHoverGeometry();
        if (!InsideHoverRegion(point)) return false;
        if (!IsCompactList) return true;
        // RowBounds are aggregated after layout, including only the gaps between
        // actual rows. Pointer X does not change the vertical wave's strength.
        return HoverLayoutMath.CanActivateCompact(_compactHoverBounds, _hoverViewportBounds,
            _hoverButtonBounds, new((float)point.X, (float)point.Y));
    }

    private float CurrentDockCornerRadius => RoundedSurfaceGeometry.Create(_dockGeometry.Width, _dockGeometry.Height,
        _dockGeometry.CornerRadius, WindowRoot.XamlRoot?.RasterizationScale ?? 1).Radius;

    private bool TryScreenToHoverViewport(NativeMethods.POINT screen, out Point point)
    {
        point = default;
        if (!NativeMethods.ScreenToClient(_hwnd, ref screen)) return false;
        double scale = WindowRoot.XamlRoot?.RasterizationScale ?? 1;
        Point origin = !_hoverGeometryDirty && _hoverViewportWindowOriginValid
            ? _hoverViewportWindowOrigin
            : ItemsScrollView.TransformToVisual(WindowRoot).TransformPoint(new Point());
        point = new Point(screen.X / scale - origin.X, screen.Y / scale - origin.Y);
        return true;
    }

    private void UpdateHoverInputRegion()
    {
        if (_hwnd == IntPtr.Zero || !WindowRoot.IsLoaded || _updatingDockInputRegion) return;
        _updatingDockInputRegion = true;
        try { ApplyHoverInputRegion(); }
        finally { _updatingDockInputRegion = false; }
    }

    private void ApplyHoverInputRegion()
    {
        if (!IsDock)
        {
            if (_dockRegionKey is not null) DockNative.SetWindowRgn(_hwnd, IntPtr.Zero, true);
            _dockRegionKey = null;
            return;
        }
        if (!_expanded) return;
        if (_itemReorderSession is { IsActive: true } || _shellDragActive)
        {
            if (_dockRegionKey != "drag") DockNative.SetWindowRgn(_hwnd, IntPtr.Zero, true);
            _dockRegionKey = "drag";
            return;
        }
        double scale = WindowRoot.XamlRoot?.RasterizationScale ?? 1;
        var clientOrigin = new NativeMethods.POINT();
        if (!NativeMethods.GetWindowRect(_hwnd, out var windowBounds) ||
            !NativeMethods.ClientToScreen(_hwnd, ref clientOrigin)) return;
        int offsetX = clientOrigin.X - windowBounds.Left, offsetY = clientOrigin.Y - windowBounds.Top;
        NativeMethods.RECT WindowRegionPixels(HoverRect rect, Vector2 origin)
        {
            // XAML coordinates are client-relative; HRGN coordinates are HWND-relative.
            var pixels = HoverLayoutMath.ToPixels(rect, scale, origin, fringe: 1);
            pixels.Left += offsetX; pixels.Right += offsetX;
            pixels.Top += offsetY; pixels.Bottom += offsetY;
            return pixels;
        }
        Point surfaceOrigin = ExpandedPanel.TransformToVisual(WindowRoot).TransformPoint(new Point());
        RoundedSurfaceGeometry body = RoundedSurfaceGeometry.Create(ExpandedPanel.ActualWidth,
            ExpandedPanel.ActualHeight, CurrentDockCornerRadius, scale);
        HoverRect surface = new((float)surfaceOrigin.X, (float)surfaceOrigin.Y,
            body.Width, body.Height);
        var frame = WindowRegionPixels(surface, Vector2.Zero);
        int diameter = (int)Math.Round(2 * body.Radius * scale) + 2;
        _dockRegionKeyBuilder.Clear();
        _dockRegionKeyBuilder.Append(frame.Left).Append(',').Append(frame.Top).Append(',')
            .Append(frame.Right).Append(',').Append(frame.Bottom).Append(',').Append(diameter);
        _dockBounceRegionRects.Clear();
        if (HorizontalHover)
        {
            Point origin = !_hoverGeometryDirty && _hoverViewportWindowOriginValid
                ? _hoverViewportWindowOrigin
                : ItemsScrollView.TransformToVisual(WindowRoot).TransformPoint(new Point());
            Vector2 originVec = new((float)origin.X, (float)origin.Y);
            float overflowTopDip = -(float)(_dockOverflowPixels / scale);
            foreach (HoverItemVisual item in _hoverItems.Values)
            {
                if (!item.Bounce.IsActive) continue;
                // Union static envelope up through the Dock bounce gutter so ONE SetWindowRgn covers the entire bounce!
                HoverRect baseIcon = GetRenderedDockIcon(item, includeBounce: false);
                float envelopeTop = Math.Min(baseIcon.Y, overflowTopDip);
                HoverRect envelope = new(baseIcon.X, envelopeTop, baseIcon.Width, Math.Max(baseIcon.Height, baseIcon.Bottom - envelopeTop));
                var pixels = WindowRegionPixels(envelope, originVec);
                _dockBounceRegionRects.Add(pixels);
                _dockRegionKeyBuilder.Append('/').Append(pixels.Left).Append(',').Append(pixels.Top)
                    .Append(',').Append(pixels.Right).Append(',').Append(pixels.Bottom);
            }
        }
        if (_dockRegionKey is not null && _dockRegionKeyBuilder.Equals(_dockRegionKey.AsSpan())) return;
        IntPtr region = DockNative.CreateRoundRectRgn(frame.Left, frame.Top, frame.Right, frame.Bottom, diameter, diameter);
        if (region == IntPtr.Zero) return;
        for (int i = 0; i < _dockBounceRegionRects.Count; i++)
        {
            NativeMethods.RECT icon = _dockBounceRegionRects[i];
            IntPtr iconRegion = DockNative.CreateRectRgn(icon.Left, icon.Top, icon.Right, icon.Bottom);
            if (iconRegion == IntPtr.Zero) continue;
            DockNative.CombineRgn(region, region, iconRegion, 2);
            DockNative.DeleteObject(iconRegion);
        }
        // A visible region change must redraw the newly exposed client pixels.
        // TransparentWindowBackdrop handles that erase; skipping it leaves stale
        // native backing pixels visible under the animated transparent content.
        if (DockNative.SetWindowRgn(_hwnd, region, true) == 0) DockNative.DeleteObject(region);
        else
        {
            _dockRegionKey = _dockRegionKeyBuilder.ToString();
            // Region changes send native window-position notifications. Apply the
            // border suppression last, without another frame/size change.
            _desktopLayer?.ApplyTransparentChrome();
        }
    }
}
