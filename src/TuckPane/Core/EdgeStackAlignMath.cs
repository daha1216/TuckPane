using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane.Core;

internal readonly record struct EdgeStackCandidate(
    Guid OrganizerId,
    NativeMethods.RECT WindowBounds,
    WindowAlignmentInsets Insets);

internal readonly record struct EdgeStackPlacement(
    Guid OrganizerId,
    NativeMethods.RECT NewWindowBounds);

/// <summary>
/// Cleanly aligns and distributes edge-snapped floating organizers into a crisp,
/// non-overlapping vertical or horizontal edge column flush with the screen edge.
/// </summary>
internal static class EdgeStackAlignMath
{
    internal const double EdgeCorridorDip = 120.0;
    internal const double DefaultGapDip = 14.0;
    internal const double EdgeMarginDip = 12.0;

    internal static OrganizerDockEdge DetectNearestEdge(
        NativeMethods.RECT visibleFrame,
        NativeMethods.RECT work)
    {
        int distLeft = Math.Max(0, visibleFrame.Left - work.Left);
        int distRight = Math.Max(0, work.Right - visibleFrame.Right);
        int distTop = Math.Max(0, visibleFrame.Top - work.Top);
        int distBottom = Math.Max(0, work.Bottom - visibleFrame.Bottom);

        int min = Math.Min(Math.Min(distLeft, distRight), Math.Min(distTop, distBottom));
        if (min == distRight) return OrganizerDockEdge.Right;
        if (min == distLeft) return OrganizerDockEdge.Left;
        if (min == distTop) return OrganizerDockEdge.Top;
        return OrganizerDockEdge.Bottom;
    }

    internal static IReadOnlyList<EdgeStackPlacement> ComputeTidyStack(
        IReadOnlyList<EdgeStackCandidate> candidates,
        OrganizerDockEdge edge,
        NativeMethods.RECT work,
        double displayScale,
        Guid? requiredAnchorId = null)
    {
        if (candidates.Count == 0) return [];
        double scale = Math.Max(1.0, displayScale);
        int corridorPx = Math.Max(1, (int)Math.Round(EdgeCorridorDip * scale));
        int gapPx = Math.Max(2, (int)Math.Round(DefaultGapDip * scale));
        int marginPx = Math.Max(0, (int)Math.Round(EdgeMarginDip * scale));

        var selected = new List<EdgeStackCandidate>();
        foreach (EdgeStackCandidate candidate in candidates)
        {
            NativeMethods.RECT frame = candidate.Insets.IsValidFor(candidate.WindowBounds)
                ? candidate.Insets.ToFrame(candidate.WindowBounds)
                : candidate.WindowBounds;
            bool isAnchor = requiredAnchorId is Guid anchorId && candidate.OrganizerId == anchorId;
            if (isAnchor || DistanceToEdge(frame, work, edge) <= corridorPx)
                selected.Add(candidate);
        }

        if (selected.Count == 0) return [];

        bool verticalStack = edge is OrganizerDockEdge.Left or OrganizerDockEdge.Right;
        List<EdgeStackCandidate> ordered = verticalStack
            ? selected.OrderBy(c => c.WindowBounds.Top).ThenBy(c => c.OrganizerId).ToList()
            : selected.OrderBy(c => c.WindowBounds.Left).ThenBy(c => c.OrganizerId).ToList();

        int availableAxis = verticalStack
            ? Math.Max(1, work.Height - marginPx * 2)
            : Math.Max(1, work.Width - marginPx * 2);

        int totalItemExtent = 0;
        foreach (EdgeStackCandidate candidate in ordered)
        {
            NativeMethods.RECT frame = candidate.Insets.IsValidFor(candidate.WindowBounds)
                ? candidate.Insets.ToFrame(candidate.WindowBounds)
                : candidate.WindowBounds;
            totalItemExtent += verticalStack ? Math.Max(1, frame.Height) : Math.Max(1, frame.Width);
        }

        int effectiveGap = ordered.Count > 1
            ? Math.Clamp((availableAxis - totalItemExtent) / (ordered.Count - 1), 2, gapPx)
            : 0;
        if (ordered.Count > 1 && totalItemExtent + gapPx * (ordered.Count - 1) <= availableAxis)
            effectiveGap = gapPx;

        int totalStackExtent = totalItemExtent + effectiveGap * Math.Max(0, ordered.Count - 1);
        int cursor = verticalStack
            ? work.Top + Math.Max(marginPx, (work.Height - totalStackExtent) / 2)
            : work.Left + Math.Max(marginPx, (work.Width - totalStackExtent) / 2);

        var placements = new List<EdgeStackPlacement>(ordered.Count);
        foreach (EdgeStackCandidate candidate in ordered)
        {
            bool validInsets = candidate.Insets.IsValidFor(candidate.WindowBounds);
            NativeMethods.RECT frame = validInsets
                ? candidate.Insets.ToFrame(candidate.WindowBounds)
                : candidate.WindowBounds;
            int frameWidth = Math.Max(1, frame.Width);
            int frameHeight = Math.Max(1, frame.Height);

            int frameLeft;
            int frameTop;
            if (verticalStack)
            {
                frameLeft = edge == OrganizerDockEdge.Left ? work.Left : work.Right - frameWidth;
                frameTop = Math.Clamp(cursor, work.Top, Math.Max(work.Top, work.Bottom - frameHeight));
                cursor = frameTop + frameHeight + effectiveGap;
            }
            else
            {
                frameTop = edge == OrganizerDockEdge.Top ? work.Top : work.Bottom - frameHeight;
                frameLeft = Math.Clamp(cursor, work.Left, Math.Max(work.Left, work.Right - frameWidth));
                cursor = frameLeft + frameWidth + effectiveGap;
            }

            var targetFrame = new NativeMethods.RECT
            {
                Left = frameLeft,
                Top = frameTop,
                Right = frameLeft + frameWidth,
                Bottom = frameTop + frameHeight
            };
            NativeMethods.RECT targetWindow = validInsets
                ? candidate.Insets.ToWindow(targetFrame)
                : targetFrame;
            placements.Add(new EdgeStackPlacement(candidate.OrganizerId, targetWindow));
        }
        return placements;
    }

    private static int DistanceToEdge(NativeMethods.RECT frame, NativeMethods.RECT work, OrganizerDockEdge edge) =>
        edge switch
        {
            OrganizerDockEdge.Left => Math.Abs(frame.Left - work.Left),
            OrganizerDockEdge.Right => Math.Abs(work.Right - frame.Right),
            OrganizerDockEdge.Top => Math.Abs(frame.Top - work.Top),
            _ => Math.Abs(work.Bottom - frame.Bottom)
        };
}
