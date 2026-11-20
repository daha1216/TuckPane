using TuckPane.Services;

namespace TuckPane.Core;

internal readonly record struct DesktopGridSnapTarget(
    Guid OrganizerId,
    NativeMethods.RECT CurrentBounds,
    WindowAlignmentInsets Insets);

internal readonly record struct DesktopGridSnapResult(
    Guid OrganizerId,
    NativeMethods.RECT SnappedBounds,
    int Column,
    int Row);

/// <summary>
/// Pure math for snapping floating organizers cleanly onto the real Windows desktop
/// SysListView32 icon grid while preserving each organizer's size and avoiding
/// collisions with existing native desktop icons or sibling organizers.
/// </summary>
internal static class DesktopGridSnapMath
{
    internal static NativeMethods.RECT SnapSingleBounds(
        NativeMethods.RECT currentBounds,
        WindowAlignmentInsets insets,
        NativeMethods.RECT work,
        int cellWidthPx,
        int cellHeightPx,
        IReadOnlyList<NativeMethods.POINT>? desktopIconCenters = null,
        IReadOnlyList<NativeMethods.RECT>? occupiedSiblingFrames = null)
    {
        int cellW = Math.Max(48, cellWidthPx);
        int cellH = Math.Max(48, cellHeightPx);
        int columns = Math.Max(1, work.Width / cellW);
        int rows = Math.Max(1, work.Height / cellH);

        bool validInsets = insets.IsValidFor(currentBounds);
        NativeMethods.RECT visible = validInsets ? insets.ToFrame(currentBounds) : currentBounds;
        int centerX = visible.Left + visible.Width / 2;
        int centerY = visible.Top + visible.Height / 2;

        // Derive grid origin from existing native desktop icons when available so our
        // grid phase matches Windows Explorer's exact top-left margin.
        (int originX, int originY) = ResolveGridOrigin(work, cellW, cellH, desktopIconCenters);

        int preferredCol = Math.Clamp((int)Math.Round((centerX - originX - cellW / 2.0) / cellW), 0, columns - 1);
        int preferredRow = Math.Clamp((int)Math.Round((centerY - originY - cellH / 2.0) / cellH), 0, rows - 1);

        int bestCol = preferredCol;
        int bestRow = preferredRow;
        long bestDistSq = long.MaxValue;
        bool foundFree = false;

        for (int col = 0; col < columns; col++)
        {
            for (int row = 0; row < rows; row++)
            {
                NativeMethods.POINT slotCenter = SlotCenter(originX, originY, col, row, cellW, cellH);
                if (IsOccupiedByDesktopIcon(slotCenter, cellW, cellH, desktopIconCenters))
                    continue;

                NativeMethods.RECT candidateFrame = CenterFrameInWork(visible.Width, visible.Height, slotCenter, work);
                if (IsOccupiedBySibling(candidateFrame, occupiedSiblingFrames))
                    continue;

                long dx = slotCenter.X - centerX;
                long dy = slotCenter.Y - centerY;
                long distSq = dx * dx + dy * dy;
                if (!foundFree || distSq < bestDistSq)
                {
                    foundFree = true;
                    bestDistSq = distSq;
                    bestCol = col;
                    bestRow = row;
                }
            }
        }

        NativeMethods.POINT chosenCenter = SlotCenter(originX, originY, bestCol, bestRow, cellW, cellH);
        NativeMethods.RECT snappedVisible = CenterFrameInWork(visible.Width, visible.Height, chosenCenter, work);
        return validInsets ? insets.ToWindow(snappedVisible) : snappedVisible;
    }

    internal static IReadOnlyList<DesktopGridSnapResult> SnapGroupToGrid(
        IReadOnlyList<DesktopGridSnapTarget> targets,
        NativeMethods.RECT work,
        int cellWidthPx,
        int cellHeightPx,
        IReadOnlyList<NativeMethods.POINT>? desktopIconCenters = null,
        IReadOnlyList<NativeMethods.RECT>? staticOccupiedFrames = null)
    {
        if (targets.Count == 0) return [];
        int cellW = Math.Max(48, cellWidthPx);
        int cellH = Math.Max(48, cellHeightPx);
        (int originX, int originY) = ResolveGridOrigin(work, cellW, cellH, desktopIconCenters);

        var occupied = new List<NativeMethods.RECT>();
        if (staticOccupiedFrames is not null) occupied.AddRange(staticOccupiedFrames);

        // Process in reading order (top-to-bottom, left-to-right) for deterministic stability.
        var ordered = targets
            .OrderBy(t => t.CurrentBounds.Top)
            .ThenBy(t => t.CurrentBounds.Left)
            .ThenBy(t => t.OrganizerId)
            .ToArray();

        var results = new List<DesktopGridSnapResult>(ordered.Length);
        foreach (DesktopGridSnapTarget target in ordered)
        {
            NativeMethods.RECT snappedWindow = SnapSingleBounds(
                target.CurrentBounds,
                target.Insets,
                work,
                cellW,
                cellH,
                desktopIconCenters,
                occupied);
            NativeMethods.RECT snappedFrame = target.Insets.IsValidFor(snappedWindow)
                ? target.Insets.ToFrame(snappedWindow)
                : snappedWindow;
            occupied.Add(snappedFrame);

            int cx = snappedFrame.Left + snappedFrame.Width / 2;
            int cy = snappedFrame.Top + snappedFrame.Height / 2;
            int col = Math.Max(0, (int)Math.Round((cx - originX - cellW / 2.0) / cellW));
            int row = Math.Max(0, (int)Math.Round((cy - originY - cellH / 2.0) / cellH));
            results.Add(new DesktopGridSnapResult(target.OrganizerId, snappedWindow, col, row));
        }
        return results;
    }

    internal static (int OriginX, int OriginY) ResolveGridOrigin(
        NativeMethods.RECT work,
        int cellW,
        int cellH,
        IReadOnlyList<NativeMethods.POINT>? desktopIconCenters)
    {
        if (desktopIconCenters is not { Count: > 0 })
            return (work.Left, work.Top);

        int? minRemX = null;
        int? minRemY = null;
        foreach (NativeMethods.POINT pt in desktopIconCenters)
        {
            if (pt.X < work.Left || pt.X >= work.Right || pt.Y < work.Top || pt.Y >= work.Bottom) continue;
            int remX = ((pt.X - work.Left - cellW / 2) % cellW + cellW) % cellW;
            int remY = ((pt.Y - work.Top - cellH / 2) % cellH + cellH) % cellH;
            minRemX = minRemX is null ? remX : Math.Min(minRemX.Value, remX);
            minRemY = minRemY is null ? remY : Math.Min(minRemY.Value, remY);
        }

        int ox = work.Left + (minRemX is int rx && rx <= cellW / 2 ? rx : 0);
        int oy = work.Top + (minRemY is int ry && ry <= cellH / 2 ? ry : 0);
        return (ox, oy);
    }

    private static NativeMethods.POINT SlotCenter(int originX, int originY, int col, int row, int cellW, int cellH) =>
        new() { X = originX + col * cellW + cellW / 2, Y = originY + row * cellH + cellH / 2 };

    private static NativeMethods.RECT CenterFrameInWork(int width, int height, NativeMethods.POINT center, NativeMethods.RECT work)
    {
        int w = Math.Max(1, width);
        int h = Math.Max(1, height);
        int left = Math.Clamp(center.X - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
        int top = Math.Clamp(center.Y - h / 2, work.Top, Math.Max(work.Top, work.Bottom - h));
        return new NativeMethods.RECT { Left = left, Top = top, Right = left + w, Bottom = top + h };
    }

    private static bool IsOccupiedByDesktopIcon(
        NativeMethods.POINT slotCenter,
        int cellW,
        int cellH,
        IReadOnlyList<NativeMethods.POINT>? desktopIconCenters)
    {
        if (desktopIconCenters is not { Count: > 0 }) return false;
        int halfW = Math.Max(16, cellW / 2);
        int halfH = Math.Max(16, cellH / 2);
        foreach (NativeMethods.POINT icon in desktopIconCenters)
        {
            if (Math.Abs(icon.X - slotCenter.X) < halfW && Math.Abs(icon.Y - slotCenter.Y) < halfH)
                return true;
        }
        return false;
    }

    private static bool IsOccupiedBySibling(NativeMethods.RECT candidate, IReadOnlyList<NativeMethods.RECT>? siblings)
    {
        if (siblings is not { Count: > 0 }) return false;
        foreach (NativeMethods.RECT sibling in siblings)
        {
            if (candidate.Left < sibling.Right && candidate.Right > sibling.Left &&
                candidate.Top < sibling.Bottom && candidate.Bottom > sibling.Top)
            {
                return true;
            }
        }
        return false;
    }
}
