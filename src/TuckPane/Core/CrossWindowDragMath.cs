namespace TuckPane.Core;

public readonly record struct DragRectDip(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public static class CrossWindowDragMath
{
    public static bool ShouldDetachVisualToFloatingGhost(
        DragRectDip cardVisualRectInWindow,
        DragRectDip clipViewportRectInWindow,
        double liftScale = 1.12,
        double marginDip = 6.0)
    {
        if (!double.IsFinite(cardVisualRectInWindow.Left) ||
            !double.IsFinite(cardVisualRectInWindow.Top) ||
            !double.IsFinite(cardVisualRectInWindow.Width) ||
            !double.IsFinite(cardVisualRectInWindow.Height) ||
            !double.IsFinite(clipViewportRectInWindow.Left) ||
            !double.IsFinite(clipViewportRectInWindow.Top) ||
            !double.IsFinite(clipViewportRectInWindow.Width) ||
            !double.IsFinite(clipViewportRectInWindow.Height) ||
            clipViewportRectInWindow.Width <= 0 ||
            clipViewportRectInWindow.Height <= 0 ||
            cardVisualRectInWindow.Width <= 0 ||
            cardVisualRectInWindow.Height <= 0)
        {
            return false;
        }

        double safeScale = double.IsFinite(liftScale) ? Math.Max(1.0, liftScale) : 1.0;
        double safeMargin = double.IsFinite(marginDip) ? Math.Max(0.0, marginDip) : 0.0;
        double width = Math.Max(0.0, cardVisualRectInWindow.Width);
        double height = Math.Max(0.0, cardVisualRectInWindow.Height);

        double centerX = cardVisualRectInWindow.Left + width * 0.5;
        double centerY = cardVisualRectInWindow.Top + height * 0.5;
        double halfWidth = width * 0.5 * safeScale + safeMargin;
        double halfHeight = height * 0.5 * safeScale + safeMargin;

        double expandedLeft = centerX - halfWidth;
        double expandedTop = centerY - halfHeight;
        double expandedRight = centerX + halfWidth;
        double expandedBottom = centerY + halfHeight;

        return expandedLeft <= clipViewportRectInWindow.Left ||
               expandedTop <= clipViewportRectInWindow.Top ||
               expandedRight >= clipViewportRectInWindow.Right ||
               expandedBottom >= clipViewportRectInWindow.Bottom;
    }

    public static float ComputeVelocityTiltTargetDeg(float velocityXPxPerSec, double dpiScale)
    {
        if (!float.IsFinite(velocityXPxPerSec)) return 0f;
        double safeDpi = double.IsFinite(dpiScale) ? Math.Max(1.0, dpiScale) : 1.0;
        float velocityDipPerSec = (float)(velocityXPxPerSec / safeDpi);
        if (!float.IsFinite(velocityDipPerSec)) return 0f;
        return Math.Clamp(velocityDipPerSec / 240f, -8.5f, 8.5f);
    }

    public static IReadOnlyList<(int X, int Y)> ComputeDesktopDropFanPositions(
        int anchorX,
        int anchorY,
        int itemCount,
        int cellWidthPx,
        int cellHeightPx,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom)
    {
        if (itemCount <= 0) return Array.Empty<(int X, int Y)>();

        int stepW = Math.Max(1, cellWidthPx);
        int stepH = Math.Max(1, cellHeightPx);

        int minX = Math.Min(workLeft, workRight);
        int areaRight = Math.Max(workLeft, workRight);
        int minY = Math.Min(workTop, workBottom);
        int areaBottom = Math.Max(workTop, workBottom);

        int maxX = Math.Max(minX, areaRight - stepW);
        int maxY = Math.Max(minY, areaBottom - stepH);

        int startX = Math.Clamp(anchorX, minX, maxX);
        int startY = Math.Clamp(anchorY, minY, maxY);

        var result = new List<(int X, int Y)>(itemCount);
        if (itemCount == 1)
        {
            result.Add((startX, startY));
            return result;
        }

        // Build column X order: start at anchor column, fan right to workRight,
        // then wrap left from anchor column down to workLeft.
        var columns = new List<int> { startX };
        for (int x = startX + stepW; x <= maxX; x += stepW)
        {
            columns.Add(x);
            if (x > int.MaxValue - stepW) break;
        }
        for (int x = startX - stepW; x >= minX; x -= stepW)
        {
            columns.Add(x);
            if (x < int.MinValue + stepW) break;
        }

        // Primary pass: for each column, step vertically down from startY inside [minY..maxY].
        for (int colIndex = 0; colIndex < columns.Count && result.Count < itemCount; colIndex++)
        {
            int colX = columns[colIndex];
            for (int y = startY; y <= maxY && result.Count < itemCount; y += stepH)
            {
                result.Add((colX, y));
                if (y > int.MaxValue - stepH) break;
            }
        }

        // Secondary pass: if dropped near bottom and horizontal columns filled up,
        // expand cleanly into available rows above startY inside [minY..maxY].
        for (int colIndex = 0; colIndex < columns.Count && result.Count < itemCount; colIndex++)
        {
            int colX = columns[colIndex];
            for (int y = startY - stepH; y >= minY && result.Count < itemCount; y -= stepH)
            {
                result.Add((colX, y));
                if (y < int.MinValue + stepH) break;
            }
        }

        // Overflow safety: if itemCount exceeds the total desktop cells in work area,
        // wrap deterministically across already-clamped positions so zero icons go off-screen.
        int filledCount = result.Count;
        int overflowIndex = 0;
        while (result.Count < itemCount)
        {
            (int X, int Y) basePos = filledCount > 0 ? result[overflowIndex % filledCount] : (startX, startY);
            result.Add((Math.Clamp(basePos.X, minX, maxX), Math.Clamp(basePos.Y, minY, maxY)));
            overflowIndex++;
        }

        return result;
    }

    public static (int WindowLeftPx, int WindowTopPx) ComputeGhostWindowOriginPx(
        int cursorX,
        int cursorY,
        int grabOffsetXPx,
        int grabOffsetYPx,
        int cardPadLeftPx,
        int cardPadTopPx) =>
        (cursorX - grabOffsetXPx - cardPadLeftPx,
         cursorY - grabOffsetYPx - cardPadTopPx);
}
