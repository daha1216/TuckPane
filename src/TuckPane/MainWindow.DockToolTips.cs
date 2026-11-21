using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using TuckPane.Core;
using TuckPane.Services;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    private DockTipWindow? _visibleDockToolTip;
    private HoverItemVisual? _dockTipItem;
    private string? _lastDockTipText;
    private HoverRect _lastDockTipPixels;
    private double _lastDockTipScale;
    private bool _lastDockTipHorizontal;

    private void SetItemToolTip(Border host, string text)
    {
        if (!IsDock || !_hoverItems.TryGetValue(host, out HoverItemVisual? item))
        {
            ToolTipService.SetToolTip(host, text);
            return;
        }
        ToolTipService.SetToolTip(host, null);
        ToolTipService.SetToolTip(item.Content, null);
        item.DockToolTipText = text;
    }

    private void UpdateDockToolTip(Point point)
    {
        if (!IsDock) return;
        if (!HoverViewportVisible || HoverInteractionBusy || _hoverLayoutPending ||
            HoverItemAtViewportPoint(point) is not Border host ||
            !_hoverItems.TryGetValue(host, out HoverItemVisual? item) || string.IsNullOrEmpty(item.DockToolTipText))
        {
            CloseDockToolTip();
            return;
        }
        _dockTipItem = item;
        UpdateDockToolTipPosition();
        UpdateHoverPointerGuard();
    }

    private HoverRect GetRenderedDockIcon(HoverItemVisual item, bool includeBounce)
    {
        float scale = item.Presented ? item.RenderScale : item.Content.Scale.Y;
        HoverRect bounds = HoverLayoutMath.Transform(item.IconBounds, item.BaseBounds, scale,
            item.Presented ? item.Translation : Vector2.Zero, false);
        return includeBounce ? bounds with { Y = bounds.Y + item.Icon.Translation.Y * scale } : bounds;
    }

    private void UpdateDockToolTipPosition()
    {
        if (_dockTipItem is not { } item || !HoverViewportVisible || HoverInteractionBusy || _hoverLayoutPending) return;
        HoverRect icon = GetRenderedDockIcon(item, includeBounce: true);
        double scale = WindowRoot.XamlRoot?.RasterizationScale ?? 1;
        Point origin = ItemsScrollView.TransformToVisual(WindowRoot).TransformPoint(new Point());
        var screen = new NativeMethods.POINT();
        if (!NativeMethods.ClientToScreen(_hwnd, ref screen)) return;
        var pixels = new HoverRect((float)(screen.X + (origin.X + icon.X) * scale),
            (float)(screen.Y + (origin.Y + icon.Y) * scale), (float)(icon.Width * scale), (float)(icon.Height * scale));
        string tipText = item.DockToolTipText!;
        bool horizontal = HorizontalHover;
        if (_visibleDockToolTip is { IsOpen: true } &&
            string.Equals(_lastDockTipText, tipText, StringComparison.Ordinal) &&
            _lastDockTipHorizontal == horizontal &&
            Math.Abs(_lastDockTipScale - scale) < 0.001 &&
            Math.Abs(_lastDockTipPixels.X - pixels.X) < 0.5f &&
            Math.Abs(_lastDockTipPixels.Y - pixels.Y) < 0.5f &&
            Math.Abs(_lastDockTipPixels.Width - pixels.Width) < 0.5f &&
            Math.Abs(_lastDockTipPixels.Height - pixels.Height) < 0.5f)
        {
            return;
        }
        DisplayInfo display = DisplayPlacementService.ForBounds(new NativeMethods.RECT
        {
            Left = (int)pixels.X, Top = (int)pixels.Y, Right = (int)pixels.Right, Bottom = (int)pixels.Bottom
        });
        _visibleDockToolTip ??= new DockTipWindow(_hwnd);
        _visibleDockToolTip.Present(tipText, pixels, display.Work, horizontal, scale);
        _lastDockTipText = tipText;
        _lastDockTipPixels = pixels;
        _lastDockTipScale = scale;
        _lastDockTipHorizontal = horizontal;
    }

    private void CloseDockToolTip()
    {
        _visibleDockToolTip?.HideTip();
        _dockTipItem = null;
        _lastDockTipText = null;
        _lastDockTipPixels = default;
    }

    private void ClearDockToolTip(Border host, HoverItemVisual item)
    {
        if (ReferenceEquals(_dockTipItem, item)) CloseDockToolTip();
        ToolTipService.SetToolTip(item.Content, null);
        item.DockToolTipText = null;
    }

    private bool IsPointerOverDockOwner(IntPtr hit, NativeMethods.POINT screen)
    {
        if (hit == _hwnd || hit != IntPtr.Zero && NativeMethods.IsChild(_hwnd, hit)) return true;
        if (!IsDock || hit == IntPtr.Zero) return false;
        IntPtr root = DockToolTipNative.GetAncestor(hit, 2);
        bool ownTip = _visibleDockToolTip is { IsOpen: true } tip && root == tip.Hwnd;
        return DockHoverOwnership.IsOverDock(root, _hwnd, ownTip,
            window => DockToolTipNative.GetWindow(window, 2),
            window => DockToolTipNative.IsWindowVisible(window) &&
                NativeMethods.GetWindowRect(window, out var rect) &&
                screen.X >= rect.Left && screen.X < rect.Right && screen.Y >= rect.Top && screen.Y < rect.Bottom);
    }

    private static class DockToolTipNative
    {
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hwnd);
    }
}
