using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    private Point? _compactHighlightPointer;
    private Border? _compactHighlightedHost;
    private Canvas? _compactHighlightLayer;
    private Border? _compactHighlight;
    // A XAML Geometry has one owner. Share Rect values with the proxy layer, never its Clip instance.
    private readonly RectangleGeometry _compactHighlightClip = new();
    private Vector3 _compactHighlightTranslation = new(float.NaN, float.NaN, float.NaN);
    private double _compactHighlightWidth = double.NaN;
    private double _compactHighlightHeight = double.NaN;
    private CornerRadius _compactHighlightCornerRadius;
    private bool _compactHighlightHasCornerRadius;
    private Brush? _compactHighlightBrush;
    private bool _compactHighlightVisible;
    private bool _compactHighlightTransitionActive;

    private void UpdateCompactRowHighlight(Point pointer)
    {
        if (!IsCompactList) return;
        _compactHighlightPointer = pointer;
        RefreshCompactRowHighlight();
    }

    private void RefreshCompactRowHighlight()
    {
        bool pressed = _itemReorderSession is { State: ItemDragState.Pressed };
        if (!IsCompactList || !HoverViewportVisible || _hoverLayoutPending ||
            HoverInteractionBusy && !pressed || _compactHighlightPointer is not { } pointer)
        {
            ClearCompactRowHighlight(forgetPointer: !IsCompactList || !HoverViewportVisible);
            return;
        }
        // This is independent of the wave's enabled flag and smoothed pointer.
        Border? host = pressed && _hoverPressedHost is { } captured
            ? captured : HoverItemAtViewportPoint(pointer);
        if (host is null || !_hoverItems.TryGetValue(host, out var item))
        {
            ClearCompactRowHighlight();
            return;
        }
        _compactHighlightedHost = host;
        if (!ReferenceEquals(host.Background, _transparentItemBrush))
            host.Background = _transparentItemBrush;
        if (_compactHighlight is null)
        {
            var layer = new Canvas { IsHitTestVisible = false, Clip = _compactHighlightClip };
            var highlight = new Border { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            try
            {
                Canvas.SetZIndex(layer, -2);
                layer.Children.Add(highlight);
                ExpandedContentLayer.Children.Add(layer);
            }
            catch
            {
                // Failed attachment must not leave the reusable clip owned by an abandoned Canvas.
                layer.Clip = null;
                throw;
            }
            _compactHighlightLayer = layer;
            _compactHighlight = highlight;
            ResetCompactHighlightCache();
        }
        bool wantTransition = UseCustomAnimations;
        if (_compactHighlightTransitionActive != wantTransition)
        {
            _compactHighlight.BackgroundTransition = wantTransition
                ? new BrushTransition { Duration = TimeSpan.FromMilliseconds(90) }
                : null;
            _compactHighlightTransitionActive = wantTransition;
        }
        HoverRect row = item.Presented
            ? HoverLayoutMath.Transform(item.RowBounds, item.BaseBounds, item.RenderScale, item.Translation, true)
            : item.RowBounds;
        row = row.Intersect(_hoverViewportBounds);
        if (row.IsEmpty)
        {
            if (_compactHighlightVisible)
            {
                _compactHighlight.Visibility = Visibility.Collapsed;
                _compactHighlightVisible = false;
            }
            return;
        }
        if (double.IsNaN(_compactHighlightWidth) || Math.Abs(_compactHighlightWidth - row.Width) > 0.05)
        {
            _compactHighlight.Width = row.Width;
            _compactHighlightWidth = row.Width;
        }
        if (double.IsNaN(_compactHighlightHeight) || Math.Abs(_compactHighlightHeight - row.Height) > 0.05)
        {
            _compactHighlight.Height = row.Height;
            _compactHighlightHeight = row.Height;
        }
        Vector3 translation = new((float)_hoverViewportOrigin.X + row.X, (float)_hoverViewportOrigin.Y + row.Y, 0);
        if (_compactHighlightTranslation != translation)
        {
            _compactHighlight.Translation = translation;
            _compactHighlightTranslation = translation;
        }
        CornerRadius cornerRadius = host.CornerRadius;
        if (!_compactHighlightHasCornerRadius || _compactHighlightCornerRadius != cornerRadius)
        {
            _compactHighlight.CornerRadius = cornerRadius;
            _compactHighlightCornerRadius = cornerRadius;
            _compactHighlightHasCornerRadius = true;
        }
        Brush targetBrush = pressed && ReferenceEquals(host, _hoverPressedHost)
            ? _pressedItemBrush : _hoveredItemBrush;
        if (!ReferenceEquals(_compactHighlightBrush, targetBrush))
        {
            _compactHighlight.Background = targetBrush;
            _compactHighlightBrush = targetBrush;
        }
        if (!_compactHighlightVisible)
        {
            _compactHighlight.Visibility = Visibility.Visible;
            _compactHighlightVisible = true;
        }
    }

    private void ClearCompactRowHighlight(bool forgetPointer = false)
    {
        _compactHighlightedHost = null;
        if (_compactHighlight is not null && _compactHighlightVisible)
        {
            _compactHighlight.Visibility = Visibility.Collapsed;
            _compactHighlightVisible = false;
        }
        if (forgetPointer) _compactHighlightPointer = null;
    }

    private void ResetCompactHighlightCache()
    {
        _compactHighlightTranslation = new(float.NaN, float.NaN, float.NaN);
        _compactHighlightWidth = double.NaN;
        _compactHighlightHeight = double.NaN;
        _compactHighlightCornerRadius = default;
        _compactHighlightHasCornerRadius = false;
        _compactHighlightBrush = null;
        _compactHighlightVisible = false;
        _compactHighlightTransitionActive = false;
    }
}
