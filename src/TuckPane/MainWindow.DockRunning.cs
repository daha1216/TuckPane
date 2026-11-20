using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TuckPane.Models;
using TuckPane.Core;
using TuckPane.Services;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    private Canvas? _dockDotsLayer;
    private readonly Dictionary<Border, Ellipse> _dockDots = [];
    private readonly List<Border> _dockDotsToRemove = [];
    private readonly SolidColorBrush _dockDotBrush = new(Microsoft.UI.Colors.White);
    internal bool WantsDockRunningState => IsDock && HoverViewportVisible && _appWindow is { IsVisible: true };
    internal bool IsOpenForDock => !_closing && IsExpanded && _appWindow is { IsVisible: true };

    internal void UpdateDockRunningVisuals()
    {
        if (!WantsDockRunningState)
        {
            if (_dockDotsLayer is not null) _dockDotsLayer.Visibility = Visibility.Collapsed;
            return;
        }
        _dockDotsToRemove.Clear();
        foreach (Border host in _dockDots.Keys)
        {
            if (!_hoverItems.ContainsKey(host)) _dockDotsToRemove.Add(host);
        }
        for (int i = 0; i < _dockDotsToRemove.Count; i++)
        {
            Border removed = _dockDotsToRemove[i];
            if (_dockDots.Remove(removed, out Ellipse? removedDot))
                _dockDotsLayer?.Children.Remove(removedDot);
        }
        _dockDotsToRemove.Clear();
        if (_dockDotsLayer is not null) _dockDotsLayer.Visibility = Visibility.Visible;
        ThemeValues theme = _host.State.GlobalSettings.GetTheme(OrganizerKinds.ThemeFor(_definition.PlacementMode));
        var color = ThemePalette.IsDark(theme) ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        if (_dockDotBrush.Color != color) _dockDotBrush.Color = color;
        Point origin = ItemsScrollView.TransformToVisual(ExpandedContentLayer).TransformPoint(new Point());
        foreach ((Border host, HoverItemVisual visual) in _hoverItems)
        {
            bool open = host.DataContext is WidgetItem item && _host.IsDockItemOpen(item);
            if (!_dockDots.TryGetValue(host, out Ellipse? dot))
            {
                if (!open) continue;
                if (_dockDotsLayer is null)
                {
                    _dockDotsLayer = new Canvas { IsHitTestVisible = false };
                    Canvas.SetZIndex(_dockDotsLayer, 2000);
                    ExpandedContentLayer.Children.Add(_dockDotsLayer);
                }
                dot = new Ellipse { Width = 4, Height = 4, Fill = _dockDotBrush, IsHitTestVisible = false, Opacity = .78 };
                _dockDots[host] = dot;
                _dockDotsLayer.Children.Add(dot);
            }
            dot.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (!open) continue;
            if (!ReferenceEquals(dot.Fill, _dockDotBrush)) dot.Fill = _dockDotBrush;
            if (HorizontalHover)
            {
                var icon = GetRenderedDockIcon(visual, includeBounce: false);
                double centerY = _hoverSurfaceBounds.Bottom - Math.Max(5, _dockGeometry.BottomInset / 2);
                dot.Translation = new Vector3((float)(origin.X + icon.Center.X - 2), (float)(origin.Y + centerY - 2), 0f);
            }
            else
            {
                var center = DockVisualMath.VerticalRunningDotCenter(_hoverSurfaceBounds.X,
                    (float)_dockGeometry.LeftInset, visual.IconBounds, visual.Presented ? visual.Translation.Y : 0);
                dot.Translation = new Vector3((float)(origin.X + center.X - 2), (float)(origin.Y + center.Y - 2), 0f);
            }
        }
    }

    private void DisposeDockRunningVisuals()
    {
        if (_dockDotsLayer is not null)
        {
            _dockDotsLayer.Children.Clear();
            ExpandedContentLayer.Children.Remove(_dockDotsLayer);
            _dockDotsLayer = null;
        }
        _dockDots.Clear();
        _dockDotsToRemove.Clear();
    }
}
