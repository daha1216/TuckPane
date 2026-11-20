namespace TuckPane.Core;

internal static class CanvasResizeHitZones
{
    internal const double BorderDip = 28;
    internal const double PanelOuterTopMarginDip = 10;
    internal const double PanelEdgeThicknessDip = 16;
    internal const double PanelCompactLeftThicknessDip = 10;
    internal const double PanelOuterRimPriorityDip = 14;
    internal const double PanelCornerReachDip = 40;
    internal const double PanelCornerArcReachDip = 32;

    internal static double LeftBorderDip(bool compactList) => compactList ? 6 : BorderDip;

    internal static CanvasResizeEdge HitTest(double x, double y, double width, double height, bool compactList)
    {
        if (x < 0 || y < 0 || x > width || y > height) return CanvasResizeEdge.None;
        CanvasResizeEdge edge = CanvasResizeEdge.None;
        if (x <= LeftBorderDip(compactList)) edge |= CanvasResizeEdge.Left;
        else if (x >= width - BorderDip) edge |= CanvasResizeEdge.Right;
        if (y <= BorderDip) edge |= CanvasResizeEdge.Top;
        else if (y >= height - BorderDip) edge |= CanvasResizeEdge.Bottom;
        return edge;
    }

    // Panel-aware hit test anchored to the visible frosted-glass panel [0..width, panelTopDip..height]
    // rather than the transparent ExpandedTitleRow above it, with generous reach along the 36-DIP rounded corners.
    internal static CanvasResizeEdge HitTestPanel(
        double x, double y, double width, double height, bool compactList, double panelTopDip)
    {
        if (width <= 0 || height <= 0) return CanvasResizeEdge.None;
        double panelTop = Math.Clamp(panelTopDip, 0, height);
        double panelHeight = Math.Max(1, height - panelTop);
        double topMin = Math.Max(0, panelTop - PanelOuterTopMarginDip);
        if (x < 0 || x > width || y < topMin || y > height) return CanvasResizeEdge.None;

        double topMax = Math.Min(height, panelTop + PanelEdgeThicknessDip);
        double bottomMin = Math.Max(panelTop, height - PanelEdgeThicknessDip);
        double leftMax = compactList ? PanelCompactLeftThicknessDip : PanelEdgeThicknessDip;
        double rightMin = Math.Max(0, width - PanelEdgeThicknessDip);
        double corner = Math.Min(PanelCornerReachDip, Math.Min(width / 3d, panelHeight / 3d));
        double cornerArc = Math.Min(PanelCornerArcReachDip, Math.Min(width / 3d, panelHeight / 3d));

        if (y >= topMin && y <= topMax)
        {
            if (x <= corner) return CanvasResizeEdge.Left | CanvasResizeEdge.Top;
            if (x >= width - corner) return CanvasResizeEdge.Right | CanvasResizeEdge.Top;
            return CanvasResizeEdge.Top;
        }
        if (y >= bottomMin && y <= height)
        {
            if (x <= corner) return CanvasResizeEdge.Left | CanvasResizeEdge.Bottom;
            if (x >= width - corner) return CanvasResizeEdge.Right | CanvasResizeEdge.Bottom;
            return CanvasResizeEdge.Bottom;
        }
        if (x <= leftMax)
        {
            if (y <= panelTop + corner) return CanvasResizeEdge.Left | CanvasResizeEdge.Top;
            if (y >= height - corner) return CanvasResizeEdge.Left | CanvasResizeEdge.Bottom;
            return CanvasResizeEdge.Left;
        }
        if (x >= rightMin)
        {
            if (y <= panelTop + corner) return CanvasResizeEdge.Right | CanvasResizeEdge.Top;
            if (y >= height - corner) return CanvasResizeEdge.Right | CanvasResizeEdge.Bottom;
            return CanvasResizeEdge.Right;
        }

        if (!compactList)
        {
            if (y <= panelTop + cornerArc)
            {
                if (x <= cornerArc) return CanvasResizeEdge.Left | CanvasResizeEdge.Top;
                if (x >= width - cornerArc) return CanvasResizeEdge.Right | CanvasResizeEdge.Top;
            }
            else if (y >= height - cornerArc)
            {
                if (x <= cornerArc) return CanvasResizeEdge.Left | CanvasResizeEdge.Bottom;
                if (x >= width - cornerArc) return CanvasResizeEdge.Right | CanvasResizeEdge.Bottom;
            }
        }
        return CanvasResizeEdge.None;
    }

    internal static bool IsOnOuterPanelRim(
        double x, double y, double width, double height, bool compactList, double panelTopDip)
    {
        if (width <= 0 || height <= 0) return false;
        double panelTop = Math.Clamp(panelTopDip, 0, height);
        double topMin = Math.Max(0, panelTop - PanelOuterTopMarginDip);
        if (x < 0 || x > width || y < topMin || y > height) return false;
        double leftRim = compactList ? PanelCompactLeftThicknessDip : PanelOuterRimPriorityDip;
        return x <= leftRim ||
            x >= width - PanelOuterRimPriorityDip ||
            y <= panelTop + PanelOuterRimPriorityDip ||
            y >= height - PanelOuterRimPriorityDip;
    }

    // These native child windows intercept input before XAML; their left width must
    // match the logical hit zone or the freed compact-list gutter still cannot drag.
    internal static (int X, int Y, int Width, int Height)[] EdgeRectangles(
        int width, int height, double scale, bool compactList)
    {
        int band = Math.Max(1, (int)Math.Ceiling(BorderDip * scale));
        int leftBand = Math.Max(1, (int)Math.Ceiling(LeftBorderDip(compactList) * scale));
        return
        [
            (0, 0, width, Math.Min(band, height)),
            (0, Math.Max(0, height - band), width, Math.Min(band, height)),
            (0, band, Math.Min(leftBand, width), Math.Max(1, height - 2 * band)),
            (Math.Max(0, width - band), band, Math.Min(band, width), Math.Max(1, height - 2 * band))
        ];
    }

    // Align native resize interceptor strips directly onto the visible glass panel perimeter
    // (starting at panelTopPx - PanelOuterTopMarginDip) plus 4 inner corner-arc patches when
    // !compactList so Top/Bottom/Left/Right and all 4 36-DIP rounded corners reliably receive
    // native resize cursors and drag input without blocking ExpandedNameText or CollapseButton.
    internal static (int X, int Y, int Width, int Height)[] PanelEdgeRectangles(
        int width, int height, double scale, bool compactList, int panelTopPx)
    {
        double safeScale = Math.Max(1, scale);
        int safeWidth = Math.Max(1, width);
        int safeHeight = Math.Max(1, height);
        int panelTop = Math.Clamp(panelTopPx, 0, safeHeight);
        int outerTop = Math.Max(1, (int)Math.Ceiling(PanelOuterTopMarginDip * safeScale));
        int edge = Math.Max(1, (int)Math.Ceiling(PanelEdgeThicknessDip * safeScale));
        int leftEdge = Math.Max(1, (int)Math.Ceiling((compactList ? PanelCompactLeftThicknessDip : PanelEdgeThicknessDip) * safeScale));

        int topY = Math.Max(0, panelTop - outerTop);
        int topBottom = Math.Min(safeHeight, panelTop + edge);
        int topHeight = Math.Max(1, topBottom - topY);

        int bottomY = Math.Max(topBottom, safeHeight - edge);
        int bottomHeight = Math.Max(1, safeHeight - bottomY);

        int sideHeight = Math.Max(1, bottomY - topBottom);
        int leftWidth = Math.Min(safeWidth, leftEdge);
        int rightWidth = Math.Min(safeWidth, edge);

        if (compactList)
        {
            return
            [
                (0, topY, safeWidth, topHeight),
                (0, bottomY, safeWidth, bottomHeight),
                (0, topBottom, leftWidth, sideHeight),
                (Math.Max(0, safeWidth - rightWidth), topBottom, rightWidth, sideHeight)
            ];
        }

        int cornerArc = Math.Max(edge, (int)Math.Ceiling(PanelCornerArcReachDip * safeScale));
        int innerArc = Math.Max(1, Math.Min(cornerArc - edge, Math.Min(safeWidth / 3, Math.Max(1, safeHeight - panelTop) / 3)));
        int topArcY = Math.Min(bottomY, topBottom);
        int topArcHeight = Math.Max(1, Math.Min(innerArc, bottomY - topArcY));
        int bottomArcY = Math.Max(topBottom, bottomY - innerArc);
        int bottomArcHeight = Math.Max(1, bottomY - bottomArcY);
        int rightArcX = Math.Max(leftWidth, safeWidth - rightWidth - innerArc);
        int arcWidth = Math.Max(1, Math.Min(innerArc, safeWidth - leftWidth - rightWidth));

        return
        [
            (0, topY, safeWidth, topHeight),
            (0, bottomY, safeWidth, bottomHeight),
            (0, topBottom, leftWidth, sideHeight),
            (Math.Max(0, safeWidth - rightWidth), topBottom, rightWidth, sideHeight),
            (leftWidth, topArcY, arcWidth, topArcHeight),
            (rightArcX, topArcY, arcWidth, topArcHeight),
            (leftWidth, bottomArcY, arcWidth, bottomArcHeight),
            (rightArcX, bottomArcY, arcWidth, bottomArcHeight)
        ];
    }
}
