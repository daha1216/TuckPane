using Windows.Foundation;

namespace TuckPane.Core;

/// <summary>
/// Pure sparse free-placement grid slot mapping inspired by iOS 18 / iOS 26 Home Screen.
/// Maps each item's RelativeName to a non-negative 0-based grid slot index
/// (slot = row * columns + col) while allowing arbitrary empty gaps, blank rows,
/// and collision-free adaptation when items arrive or column counts change.
/// </summary>
internal static class FreeGridSlotMap
{
    internal static int[] ResolveSlots(
        IReadOnlyList<string> itemRelativeNames,
        IReadOnlyDictionary<string, int>? savedSlots)
    {
        int count = itemRelativeNames.Count;
        if (count == 0) return [];

        var resolved = new int[count];
        Array.Fill(resolved, -1);
        var used = new HashSet<int>();

        if (savedSlots is { Count: > 0 })
        {
            for (int i = 0; i < count; i++)
            {
                if (savedSlots.TryGetValue(itemRelativeNames[i], out int slot) &&
                    slot >= 0 &&
                    used.Add(slot))
                {
                    resolved[i] = slot;
                }
            }
        }

        // Any new or colliding item settles cleanly into the first unoccupied slot
        // without disturbing any existing user-placed icon.
        int nextFree = 0;
        for (int i = 0; i < count; i++)
        {
            if (resolved[i] >= 0) continue;
            while (used.Contains(nextFree)) nextFree++;
            resolved[i] = nextFree;
            used.Add(nextFree);
            nextFree++;
        }

        return resolved;
    }

    internal static Dictionary<string, int> BuildDictionary(
        IReadOnlyList<string> itemRelativeNames,
        IReadOnlyList<int> slots)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int count = Math.Min(itemRelativeNames.Count, slots.Count);
        for (int i = 0; i < count; i++)
        {
            if (slots[i] >= 0)
                map[itemRelativeNames[i]] = slots[i];
        }
        return map;
    }

    internal static List<string> BuildReadingOrder(
        IReadOnlyList<string> itemRelativeNames,
        IReadOnlyList<int> slots)
    {
        return Enumerable.Range(0, Math.Min(itemRelativeNames.Count, slots.Count))
            .OrderBy(i => slots[i])
            .ThenBy(i => i)
            .Select(i => itemRelativeNames[i])
            .ToList();
    }

    internal static Dictionary<string, int> CompactToDense(IReadOnlyList<string> orderedRelativeNames)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < orderedRelativeNames.Count; i++)
            map[orderedRelativeNames[i]] = i;
        return map;
    }

    internal static int TotalRowCount(IReadOnlyList<int> slots, int columns, int minimumVisibleRows, bool includeDragBufferRow = false)
    {
        int cols = Math.Max(1, columns);
        int maxRow = Math.Max(1, minimumVisibleRows) - 1;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i] >= 0)
                maxRow = Math.Max(maxRow, slots[i] / cols);
        }
        return maxRow + 1 + (includeDragBufferRow ? 1 : 0);
    }

    internal static int PointToSlot(
        Point contentPoint,
        double cellWidth,
        double cellHeight,
        double gap,
        int columns,
        int totalRows)
    {
        int cols = Math.Max(1, columns);
        int rows = Math.Max(1, totalRows);
        double stepX = Math.Max(1, cellWidth + gap);
        double stepY = Math.Max(1, cellHeight + gap);
        int col = Math.Clamp((int)Math.Floor(contentPoint.X / stepX), 0, cols - 1);
        int row = Math.Clamp((int)Math.Floor(contentPoint.Y / stepY), 0, rows - 1);
        return row * cols + col;
    }

    internal static Point SlotOrigin(int slot, int columns, double cellWidth, double cellHeight, double gap)
    {
        int cols = Math.Max(1, columns);
        double stepX = Math.Max(1, cellWidth + gap);
        double stepY = Math.Max(1, cellHeight + gap);
        return new Point((slot % cols) * stepX, (slot / cols) * stepY);
    }

    internal static Point SlotCenter(int slot, int columns, double cellWidth, double cellHeight, double gap)
    {
        Point origin = SlotOrigin(slot, columns, cellWidth, cellHeight, gap);
        return new Point(origin.X + cellWidth / 2.0, origin.Y + cellHeight / 2.0);
    }

    internal static int FindNearestFreeSlot(int preferredSlot, IReadOnlyCollection<int> occupiedSlots, int maxSlotExclusive)
    {
        int limit = Math.Max(1, maxSlotExclusive);
        int clamped = Math.Clamp(preferredSlot, 0, limit - 1);
        if (!occupiedSlots.Contains(clamped)) return clamped;

        for (int delta = 1; delta < limit; delta++)
        {
            int forward = clamped + delta;
            if (forward < limit && !occupiedSlots.Contains(forward)) return forward;
            int backward = clamped - delta;
            if (backward >= 0 && !occupiedSlots.Contains(backward)) return backward;
        }
        return limit;
    }
}
