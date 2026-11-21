using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    private bool _itemBounceRenderingSubscribed;
    private long _itemBounceLastFrame;
    private IDisposable? _itemBounceClockBoost;
    private float _dockBounceViewportTop;
    private bool _dockBounceViewportTopValid;

    private void InvalidateHorizontalDockBounceViewportTop() => _dockBounceViewportTopValid = false;

    private float RefreshHorizontalDockBounceViewportTop()
    {
        double rasterScale = WindowRoot.XamlRoot?.RasterizationScale ?? 1;
        float viewportTop = -_dockOverflowPixels / (float)rasterScale;
        var screen = new NativeMethods.POINT();
        if (NativeMethods.ClientToScreen(_hwnd, ref screen))
        {
            var display = DisplayPlacementService.ForBounds(new NativeMethods.RECT
            {
                Left = screen.X,
                Top = screen.Y + _dockOverflowPixels,
                Right = screen.X + 1,
                Bottom = screen.Y + _dockOverflowPixels + 1
            });
            Point origin = !_hoverGeometryDirty && _hoverViewportWindowOriginValid
                ? _hoverViewportWindowOrigin
                : ItemsScrollView.TransformToVisual(WindowRoot).TransformPoint(new Point());
            viewportTop = Math.Max(viewportTop, (float)((display.Monitor.Top - screen.Y) / rasterScale - origin.Y));
        }
        _dockBounceViewportTop = viewportTop;
        _dockBounceViewportTopValid = true;
        return viewportTop;
    }

    private void StartItemBounce(WidgetItem item)
    {
        if (IsCompactList || !UseCustomAnimations || !HoverViewportVisible ||
            !TryGetRealizedItemHost(item.RelativeName, out var host) ||
            !_hoverItems.TryGetValue(host, out var visual)) return;
        if (_hoverGeometryDirty) UpdateHoverGeometry();
        // Sample the old envelope before restarting, including a second launch between frames.
        AdvanceItemBounces(fromRenderLoop: false);
        visual.Bounce.Restart((float)visual.Icon.ActualHeight, isDock: IsDock);
        // Bounce uses the overflow presentation even when hover magnification is disabled.
        // Refresh display headroom ONCE and set ONE union bounce HRGN at bounce start (never per-frame!).
        if (HorizontalHover)
        {
            RefreshHorizontalDockBounceViewportTop();
            RenderHoverPresentation();
            UpdateDockInputRegion();
        }
        if (_itemBounceRenderingSubscribed) return;
        _itemBounceLastFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += ItemBounce_Rendering;
        _itemBounceRenderingSubscribed = true;
        _itemBounceClockBoost ??= CompositorClockBoost.Acquire();
    }

    private void ItemBounce_Rendering(object? sender, object args)
    {
        if (IsCompactList || !UseCustomAnimations || !HoverViewportVisible ||
            _itemReorderSession is { IsActive: true } || _shellDragActive ||
            _widgetDragging || _canvasResize is not null)
        {
            ResetItemBounces();
            return;
        }
        if (!AdvanceItemBounces(fromRenderLoop: true)) StopItemBounceRendering();
    }

    private bool AdvanceItemBounces(bool fromRenderLoop)
    {
        if (!_itemBounceRenderingSubscribed) return false;
        // Icon grids do not run a hover wave. Scrolling can still move their
        // bounds while launch feedback is active, so this clock owns that refresh.
        if (_hoverGeometryDirty) UpdateHoverGeometry();
        long now = Stopwatch.GetTimestamp();
        double rawSeconds = Stopwatch.GetElapsedTime(_itemBounceLastFrame, now).TotalSeconds;
        double seconds = fromRenderLoop
            ? Math.Clamp(rawSeconds, 1d / 480d, 1d / 30d)
            : Math.Clamp(rawSeconds, 0d, 1d / 30d);
        _itemBounceLastFrame = now;
        bool horizontalHover = HorizontalHover;
        bool deferApplyToPresentation = horizontalHover && !_hoverLayoutPending;
        bool anyActive = false;
        foreach (var item in _hoverItems.Values)
        {
            if (!item.Bounce.IsActive) continue;
            bool settled = item.Bounce.Step(seconds);
            if (!settled) anyActive = true;
            // Active presented horizontal Dock icons receive a single ApplyItemBounce inside
            // RenderHoverPresentation() right below, avoiding duplicate per-frame work.
            if (!deferApplyToPresentation || settled || !item.Presented)
                ApplyItemBounce(item);
        }
        if (horizontalHover)
        {
            RenderHoverPresentation();
        }
        else UpdateDockToolTipPosition();
        return anyActive;
    }

    private void ApplyItemBounce(HoverItemVisual item)
    {
        float scale = item.Presented ? item.RenderScale : item.Content.Scale.Y;
        var iconBounds = HoverLayoutMath.Transform(item.IconBounds, item.BaseBounds, scale,
            item.Presented ? item.Translation : Vector2.Zero, false);
        float travel = item.Bounce.IsActive
            ? ItemBounceMotion.FitOffsetToBounds(item.Bounce.Offset, (float)item.Icon.ActualHeight,
                scale, iconBounds, _hoverViewportBounds, _hoverSurfaceBounds, _hoverSurfaceRadius,
                item.Bounce.AmplitudeMultiplier)
            : 0;
        if (HorizontalHover && item.Bounce.IsActive)
        {
            float viewportTop = _dockBounceViewportTopValid
                ? _dockBounceViewportTop
                : RefreshHorizontalDockBounceViewportTop();
            travel = DockVisualMath.BounceOffset(item.Bounce.Offset,
                (float)item.Icon.ActualHeight * .18f * item.Bounce.AmplitudeMultiplier,
                iconBounds, _hoverSurfaceBounds.Y, viewportTop, scale);
        }
        if (item.AppliedBounceY != travel)
        {
            Vector3 offset = new(0, travel, 0);
            item.Icon.Translation = offset;
            if (item.ProxyIcon is { } icon) icon.Translation = offset;
            item.AppliedBounceY = travel;
        }
        // The layer/ScrollView still clips to the viewport. Only the proxy's local
        // content clip must allow the icon to rise above its resting content bounds.
        if (item.Proxy is { } proxy)
        {
            bool clearClip = item.Bounce.IsActive;
            if (item.ProxyClipClearedForBounce != clearClip)
            {
                proxy.Clip = clearClip ? null : item.Clip;
                item.ProxyClipClearedForBounce = clearClip;
            }
        }
    }

    private void ResetItemBounces()
    {
        if (!_itemBounceRenderingSubscribed && _itemBounceClockBoost is null) return;
        foreach (var item in _hoverItems.Values) ResetItemBounce(item);
        StopItemBounceRendering();
    }

    private void ResetItemBounce(HoverItemVisual item)
    {
        item.Bounce.Reset();
        ApplyItemBounce(item);
    }

    private void StopItemBounceRendering()
    {
        if (_itemBounceRenderingSubscribed) CompositionTarget.Rendering -= ItemBounce_Rendering;
        _itemBounceRenderingSubscribed = false;
        _itemBounceClockBoost?.Dispose();
        _itemBounceClockBoost = null;
        _itemBounceLastFrame = 0;
        _dockBounceViewportTopValid = false;
        if (HorizontalHover && !_closing) UpdateDockInputRegion();
    }
}
