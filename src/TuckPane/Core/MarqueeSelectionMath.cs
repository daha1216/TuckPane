using TuckPane.Services;

namespace TuckPane.Core;

internal readonly record struct MarqueeTargetRect<TId>(TId Id, NativeMethods.RECT Bounds);

/// <summary>
/// Pure geometry for rubber-band marquee selection (both Windows Desktop -> Organizers
/// and inside expanded organizers -> Items) and lockstep multi-organizer group dragging.
/// </summary>
internal static class MarqueeSelectionMath
{
    internal const double ActivationThresholdDip = 6.0;

    internal static bool IsMarqueeActive(NativeMethods.POINT start, NativeMethods.POINT current, double scale = 1.0)
    {
        double threshold = Math.Max(4.0, ActivationThresholdDip * Math.Max(1.0, scale));
        double dx = current.X - start.X;
        double dy = current.Y - start.Y;
        return dx * dx + dy * dy >= threshold * threshold;
    }

    internal static NativeMethods.RECT NormalizeRect(NativeMethods.POINT a, NativeMethods.POINT b) =>
        new()
        {
            Left = Math.Min(a.X, b.X),
            Top = Math.Min(a.Y, b.Y),
            Right = Math.Max(a.X, b.X),
            Bottom = Math.Max(a.Y, b.Y)
        };

    internal static bool Intersects(NativeMethods.RECT a, NativeMethods.RECT b) =>
        a.Width > 0 && a.Height > 0 &&
        b.Width > 0 && b.Height > 0 &&
        a.Left < b.Right && a.Right > b.Left &&
        a.Top < b.Bottom && a.Bottom > b.Top;

    internal static IReadOnlyList<TId> FindIntersectingIds<TId>(
        NativeMethods.RECT marquee,
        IEnumerable<MarqueeTargetRect<TId>> targets)
    {
        if (marquee.Width <= 0 || marquee.Height <= 0) return [];
        var matched = new List<TId>();
        foreach (MarqueeTargetRect<TId> target in targets)
        {
            if (Intersects(marquee, target.Bounds))
                matched.Add(target.Id);
        }
        return matched;
    }

    /// <summary>
    /// Computes the clamped lockstep destination bounds for a peer organizer being
    /// dragged alongside a lead organizer by (deltaX, deltaY).
    /// </summary>
    internal static NativeMethods.RECT ComputeGroupPeerBounds(
        NativeMethods.RECT peerInitialBounds,
        int deltaX,
        int deltaY,
        NativeMethods.RECT work,
        WindowAlignmentInsets? insets = null)
    {
        var desired = new NativeMethods.RECT
        {
            Left = peerInitialBounds.Left + deltaX,
            Top = peerInitialBounds.Top + deltaY,
            Right = peerInitialBounds.Right + deltaX,
            Bottom = peerInitialBounds.Bottom + deltaY
        };
        if (insets is WindowAlignmentInsets frameInsets && frameInsets.IsValidFor(desired))
            return WindowAlignmentMath.ClampFrame(desired, work, frameInsets);
        return DisplayPlacementService.Clamp(desired, work);
    }
}
