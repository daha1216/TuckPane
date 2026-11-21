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
    private readonly SolidColorBrush _dockDotStrokeBrush = new(Windows.UI.Color.FromArgb(0x88, 0x00, 0x00, 0x00));
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
        // Dual-contrast luminous dot: crisp white core with translucent dark halo stays legible on
        // BOTH pure white and pure black wallpapers (including FullyTransparent Dock mode!).
        bool lightOpaqueDock = !ThemePalette.IsDark(theme) && !theme.FullyTransparent;
        var fillColor = lightOpaqueDock ? Windows.UI.Color.FromArgb(0xE8, 0x1D, 0x1D, 0x1F) : Microsoft.UI.Colors.White;
        var strokeColor = lightOpaqueDock ? Windows.UI.Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x88, 0x00, 0x00, 0x00);
        if (_dockDotBrush.Color != fillColor) _dockDotBrush.Color = fillColor;
        if (_dockDotStrokeBrush.Color != strokeColor) _dockDotStrokeBrush.Color = strokeColor;
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
                dot = new Ellipse
                {
                    Width = 5,
                    Height = 5,
                    Fill = _dockDotBrush,
                    Stroke = _dockDotStrokeBrush,
                    StrokeThickness = 0.85,
                    IsHitTestVisible = false,
                    Opacity = .92
                };
                _dockDots[host] = dot;
                _dockDotsLayer.Children.Add(dot);
            }
            dot.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (!open) continue;
            if (!ReferenceEquals(dot.Fill, _dockDotBrush)) dot.Fill = _dockDotBrush;
            if (!ReferenceEquals(dot.Stroke, _dockDotStrokeBrush)) dot.Stroke = _dockDotStrokeBrush;
            if (HorizontalHover)
            {
                var icon = GetRenderedDockIcon(visual, includeBounce: false);
                double centerY = _hoverSurfaceBounds.Bottom - Math.Max(4.0, _dockGeometry.BottomInset * 0.34);
                dot.Translation = new Vector3((float)(origin.X + icon.Center.X - 2.5), (float)(origin.Y + centerY - 2.5), 0f);
            }
            else
            {
                var center = DockVisualMath.VerticalRunningDotCenter(_hoverSurfaceBounds.X,
                    (float)_dockGeometry.LeftInset, visual.IconBounds, visual.Presented ? visual.Translation.Y : 0);
                dot.Translation = new Vector3((float)(origin.X + center.X - 2.5), (float)(origin.Y + center.Y - 2.5), 0f);
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
