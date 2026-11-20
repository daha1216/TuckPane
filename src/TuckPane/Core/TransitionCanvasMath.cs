using System.Numerics;
using TuckPane.Services;

namespace TuckPane.Core;

internal readonly record struct TransitionCanvasPlan(
    NativeMethods.RECT ExpandedBounds,
    NativeMethods.RECT CompactOrigin,
    NativeMethods.RECT CanvasBounds,
    bool IsUnionNeeded,
    Vector2 ExpandedOffsetDip,
    Vector2 ExpandedSizeDip,
    Vector2 CompactOffsetDip,
    Vector2 StartScale,
    Vector3 StartTranslationDip,
    Vector3 CompactFinalTranslationDip,
    Vector3 ExpandedCenterPointDip,
    Vector3 CompactCenterPointDip);

/// <summary>
/// Pure-math geometry planner for single-canvas DWM GPU expand/collapse transitions.
/// Unifies stationary organizers (where expanded bounds contain compact origin) and
/// remembered-position organizers (where expanded bounds are displaced from compact origin)
/// into one transparent union HWND with zero per-frame SetWindowPos calls.
/// </summary>
internal static class TransitionCanvasMath
{
    internal const double MinimumStartScale = 0.04;
    internal const double MaximumStartScale = 1.0;

    internal static NativeMethods.RECT Union(NativeMethods.RECT first, NativeMethods.RECT second) => new()
    {
        Left = Math.Min(first.Left, second.Left),
        Top = Math.Min(first.Top, second.Top),
        Right = Math.Max(first.Right, second.Right),
        Bottom = Math.Max(first.Bottom, second.Bottom)
    };

    internal static TransitionCanvasPlan Create(
        NativeMethods.RECT expandedBounds,
        NativeMethods.RECT compactOrigin,
        double displayScale,
        double compactTileSizeDip,
        double compactViewWidthDip,
        double compactViewHeightDip)
    {
        double scale = double.IsFinite(displayScale) && displayScale > 0 ? displayScale : 1.0;
        NativeMethods.RECT canvasBounds = Union(expandedBounds, compactOrigin);
        bool isUnionNeeded =
            canvasBounds.Left != expandedBounds.Left ||
            canvasBounds.Top != expandedBounds.Top ||
            canvasBounds.Right != expandedBounds.Right ||
            canvasBounds.Bottom != expandedBounds.Bottom;

        double expandedWidthDip = Math.Max(1.0, expandedBounds.Width / scale);
        double expandedHeightDip = Math.Max(1.0, expandedBounds.Height / scale);
        double canvasWidthDip = Math.Max(1.0, canvasBounds.Width / scale);
        double canvasHeightDip = Math.Max(1.0, canvasBounds.Height / scale);
        double safeTileSize = Math.Max(1.0, compactTileSizeDip);
        double safeCompactWidth = Math.Max(1.0, compactViewWidthDip);
        double safeCompactHeight = Math.Max(1.0, compactViewHeightDip);

        var expandedOffsetDip = new Vector2(
            (float)((expandedBounds.Left - canvasBounds.Left) / scale),
            (float)((expandedBounds.Top - canvasBounds.Top) / scale));
        var expandedSizeDip = new Vector2((float)expandedWidthDip, (float)expandedHeightDip);
        var compactOffsetDip = new Vector2(
            (float)((compactOrigin.Left - canvasBounds.Left) / scale),
            (float)((compactOrigin.Top - canvasBounds.Top) / scale));

        var startScale = new Vector2(
            (float)Math.Clamp(safeTileSize / expandedWidthDip, MinimumStartScale, MaximumStartScale),
            (float)Math.Clamp(safeTileSize / expandedHeightDip, MinimumStartScale, MaximumStartScale));

        double expandedCenterX = expandedOffsetDip.X + expandedWidthDip * 0.5;
        double expandedCenterY = expandedOffsetDip.Y + expandedHeightDip * 0.5;
        double compactTileCenterX = compactOffsetDip.X + safeCompactWidth * 0.5;
        double compactTileCenterY = compactOffsetDip.Y + safeTileSize * 0.5;

        var startTranslationDip = new Vector3(
            (float)(compactTileCenterX - expandedCenterX),
            (float)(compactTileCenterY - expandedCenterY),
            0f);

        double compactViewCenterX = compactOffsetDip.X + safeCompactWidth * 0.5;
        double compactViewCenterY = compactOffsetDip.Y + safeCompactHeight * 0.5;
        var compactFinalTranslationDip = new Vector3(
            (float)(compactViewCenterX - canvasWidthDip * 0.5),
            (float)(compactViewCenterY - canvasHeightDip * 0.5),
            0f);

        var expandedCenterPointDip = new Vector3(
            (float)(expandedWidthDip * 0.5),
            (float)(expandedHeightDip * 0.5),
            0f);
        var compactCenterPointDip = new Vector3(
            (float)(safeCompactWidth * 0.5),
            (float)(safeTileSize * 0.5),
            0f);

        return new TransitionCanvasPlan(
            expandedBounds,
            compactOrigin,
            canvasBounds,
            isUnionNeeded,
            expandedOffsetDip,
            expandedSizeDip,
            compactOffsetDip,
            startScale,
            startTranslationDip,
            compactFinalTranslationDip,
            expandedCenterPointDip,
            compactCenterPointDip);
    }

    internal static TransitionCanvasPlan CreateReducedMotion(
        NativeMethods.RECT expandedBounds,
        NativeMethods.RECT compactOrigin,
        double displayScale,
        double compactTileSizeDip,
        double compactViewWidthDip,
        double compactViewHeightDip)
    {
        TransitionCanvasPlan full = Create(
            expandedBounds,
            compactOrigin,
            displayScale,
            compactTileSizeDip,
            compactViewWidthDip,
            compactViewHeightDip);

        if (!full.IsUnionNeeded)
        {
            return full with
            {
                StartScale = Vector2.One,
                StartTranslationDip = Vector3.Zero
            };
        }

        return new TransitionCanvasPlan(
            expandedBounds,
            compactOrigin,
            CanvasBounds: expandedBounds,
            IsUnionNeeded: false,
            ExpandedOffsetDip: Vector2.Zero,
            ExpandedSizeDip: full.ExpandedSizeDip,
            CompactOffsetDip: Vector2.Zero,
            StartScale: Vector2.One,
            StartTranslationDip: Vector3.Zero,
            CompactFinalTranslationDip: Vector3.Zero,
            ExpandedCenterPointDip: full.ExpandedCenterPointDip,
            CompactCenterPointDip: full.CompactCenterPointDip);
    }
}
