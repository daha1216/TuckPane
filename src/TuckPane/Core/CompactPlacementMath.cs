using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane.Core;

/// <summary>
/// Pure geometry and persistence rules for restoring compact organizer windows
/// without relying on asynchronous XAML visual-tree layout.
/// </summary>
internal static class CompactPlacementMath
{
    internal const double EdgeSnapToleranceDip = 4.0;
    internal const double TransientWorkAreaShrinkToleranceDip = 16.0;
    internal const double CompactThumbnailExtentDip = 39.0;

    /// <summary>
    /// Computes deterministic visible-frame insets for the compact rounded-square
    /// tile (<c>CompactThumbnailHost</c>) inside a compact window of size
    /// <paramref name="windowWidthPx"/> × <paramref name="windowHeightPx"/>.
    /// </summary>
    internal static WindowAlignmentInsets CalculateCompactAlignmentInsets(
        int windowWidthPx,
        int windowHeightPx,
        double compactScale,
        double displayScale)
    {
        int safeWidth = Math.Max(1, windowWidthPx);
        int safeHeight = Math.Max(1, windowHeightPx);
        double safeCompactScale = double.IsFinite(compactScale) && compactScale > 0
            ? compactScale
            : OrganizerLimits.DefaultCompactScale;
        double safeDisplayScale = double.IsFinite(displayScale) && displayScale > 0
            ? displayScale
            : 1.0;

        int thumbnailPx = Math.Clamp(
            (int)Math.Round(CompactThumbnailExtentDip * safeCompactScale * safeDisplayScale),
            1,
            Math.Min(safeWidth, safeHeight));

        int left = Math.Max(0, (safeWidth - thumbnailPx) / 2);
        int right = Math.Max(0, safeWidth - left - thumbnailPx);
        int top = 0;
        int bottom = Math.Max(0, safeHeight - thumbnailPx);
        return new WindowAlignmentInsets(left, top, right, bottom);
    }

    /// <summary>
    /// Determines whether restoring a floating organizer should clamp its visible
    /// rounded-square frame instead of the outer transparent name-box bounds.
    /// </summary>
    internal static bool ShouldUseAlignmentFrameOnRestore(
        OrganizerPlacementMode placementMode,
        bool windowAlignmentEnabled,
        WidgetPosition? savedPosition,
        int compactWidthPx,
        int compactHeightPx,
        DisplayInfo display)
    {
        if (placementMode != OrganizerPlacementMode.Floating) return false;
        if (windowAlignmentEnabled) return true;
        if (savedPosition is null || string.IsNullOrWhiteSpace(savedPosition.MonitorDevice)) return false;

        double leftPx = savedPosition.XDip * display.Scale;
        double topPx = savedPosition.YDip * display.Scale;
        return leftPx < 0 || topPx < 0 ||
               leftPx + compactWidthPx > display.Work.Width ||
               topPx + compactHeightPx > display.Work.Height;
    }

    /// <summary>
    /// Adjusts the target X/Y pixel coordinates when the current monitor work area
    /// dimensions differ from the saved work area, keeping edge-pinned floating
    /// windows flush with their original screen edges.
    /// </summary>
    internal static (int X, int Y) ResolveRestoredOriginPx(
        WidgetPosition saved,
        DisplayInfo display,
        int widthPx,
        int heightPx,
        WindowAlignmentInsets? alignmentInsets)
    {
        double scale = Math.Max(1.0, display.Scale);
        double currentWorkWidthDip = display.Work.Width / scale;
        double currentWorkHeightDip = display.Work.Height / scale;

        double xDip = saved.XDip;
        double yDip = saved.YDip;

        double insetLeftDip = 0;
        double insetRightDip = 0;
        double insetTopDip = 0;
        double insetBottomDip = 0;
        if (alignmentInsets is WindowAlignmentInsets insets &&
            insets.IsValidFor(new NativeMethods.RECT { Left = 0, Top = 0, Right = widthPx, Bottom = heightPx }))
        {
            insetLeftDip = insets.Left / scale;
            insetRightDip = insets.Right / scale;
            insetTopDip = insets.Top / scale;
            insetBottomDip = insets.Bottom / scale;
        }

        double windowWidthDip = widthPx / scale;
        double windowHeightDip = heightPx / scale;

        if (saved.SavedWorkAreaWidthDip > 0 &&
            Math.Abs(saved.SavedWorkAreaWidthDip - currentWorkWidthDip) > 1.0)
        {
            double savedFrameLeftDip = xDip + insetLeftDip;
            double savedFrameRightDip = xDip + windowWidthDip - insetRightDip;
            double distanceFromRightDip = saved.SavedWorkAreaWidthDip - savedFrameRightDip;

            if (Math.Abs(distanceFromRightDip) <= EdgeSnapToleranceDip &&
                Math.Abs(savedFrameLeftDip) > EdgeSnapToleranceDip)
            {
                xDip = currentWorkWidthDip - distanceFromRightDip - windowWidthDip + insetRightDip;
            }
        }

        if (saved.SavedWorkAreaHeightDip > 0 &&
            Math.Abs(saved.SavedWorkAreaHeightDip - currentWorkHeightDip) > 1.0)
        {
            double savedFrameTopDip = yDip + insetTopDip;
            double savedFrameBottomDip = yDip + windowHeightDip - insetBottomDip;
            double distanceFromBottomDip = saved.SavedWorkAreaHeightDip - savedFrameBottomDip;

            if (Math.Abs(distanceFromBottomDip) <= EdgeSnapToleranceDip &&
                Math.Abs(savedFrameTopDip) > EdgeSnapToleranceDip)
            {
                yDip = currentWorkHeightDip - distanceFromBottomDip - windowHeightDip + insetBottomDip;
            }
        }

        int x = display.Work.Left + (int)Math.Round(xDip * scale);
        int y = display.Work.Top + (int)Math.Round(yDip * scale);
        return (x, y);
    }

    /// <summary>
    /// Prevents periodic desktop-repair clamping from permanently overwriting a
    /// user's saved floating coordinates during transient boot, RDP, sleep/wake,
    /// fullscreen-game, or disconnected-monitor states.
    /// </summary>
    internal static bool ShouldPersistClampedFloatingPosition(
        WidgetPosition? saved,
        DisplayInfo currentDisplay)
    {
        if (saved is null || string.IsNullOrWhiteSpace(saved.MonitorDevice)) return true;

        if (!string.Equals(saved.MonitorDevice, currentDisplay.Device, StringComparison.OrdinalIgnoreCase))
            return false;

        double scale = Math.Max(1.0, currentDisplay.Scale);
        double currentWorkWidthDip = currentDisplay.Work.Width / scale;
        double currentWorkHeightDip = currentDisplay.Work.Height / scale;

        if (saved.SavedWorkAreaWidthDip > 0 &&
            currentWorkWidthDip + TransientWorkAreaShrinkToleranceDip < saved.SavedWorkAreaWidthDip)
        {
            return false;
        }

        if (saved.SavedWorkAreaHeightDip > 0 &&
            currentWorkHeightDip + TransientWorkAreaShrinkToleranceDip < saved.SavedWorkAreaHeightDip)
        {
            return false;
        }

        return true;
    }
}
