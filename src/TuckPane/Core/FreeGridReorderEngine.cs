using System.Numerics;
using Windows.Foundation;

namespace TuckPane.Core;

internal enum ReorderTargetKind
{
    Original,
    EmptySlot,
    PushAside,
    DirectSwap
}

internal readonly record struct ReorderPreviewSnapshot(
    int TargetSlot,
    ReorderTargetKind Kind,
    int[] VisualSlots);

/// <summary>
/// Pure iOS 18 / iOS 26 SpringBoard-inspired reorder engine supporting:
/// 1. Empty-slot direct placement with ZERO disturbance to other icons;
/// 2. Local contiguous ripple push-aside bounded by the nearest empty gap
///    (and deterministic reading-order insertion inside dense clusters — never
///    a confusing 15-puzzle snake trail);
/// 3. Direct 1-to-1 swap (A <-> B) via setting or holding Shift during drag;
/// 4. Spatial hysteresis + brief dwell threshold over occupied slots so fast
///    cursor sweeps across icons toward an empty area never scatter your layout.
/// </summary>
internal sealed class FreeGridReorderEngine
{
    internal const double HysteresisRatio = 0.18;
    internal const double OccupiedTransitDwellMs = 80.0;

    private readonly int[] _initialSlots;
    private readonly int _sourceItemIndex;
    private readonly int _sourceSlot;
    private readonly bool _freeGridEnabled;
    private readonly int _maxSlotCount;

    private int _committedTargetSlot;
    private ReorderTargetKind _committedKind = ReorderTargetKind.Original;
    private int[] _visualSlots;

    private int? _pendingOccupiedSlot;
    private double _pendingOccupiedElapsedMs;
    private bool _lastDirectSwap;

    internal FreeGridReorderEngine(
        IReadOnlyList<int> initialSlots,
        int sourceItemIndex,
        bool freeGridEnabled,
        int maxSlotCount)
    {
        ArgumentNullException.ThrowIfNull(initialSlots);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceItemIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sourceItemIndex, initialSlots.Count);

        _initialSlots = initialSlots.ToArray();
        _sourceItemIndex = sourceItemIndex;
        _sourceSlot = _initialSlots[sourceItemIndex];
        _freeGridEnabled = freeGridEnabled;
        _maxSlotCount = Math.Max(initialSlots.Count, maxSlotCount);
        _committedTargetSlot = _sourceSlot;
        _visualSlots = (int[])_initialSlots.Clone();
    }

    internal int SourceItemIndex => _sourceItemIndex;
    internal int SourceSlot => _sourceSlot;
    internal int TargetSlot => _committedTargetSlot;
    internal ReorderTargetKind CurrentKind => _committedKind;
    internal int Revision { get; private set; }

    internal int GetVisualSlot(int itemIndex) =>
        itemIndex >= 0 && itemIndex < _visualSlots.Length ? _visualSlots[itemIndex] : itemIndex;

    internal int[] SnapshotVisualSlots() => (int[])_visualSlots.Clone();

    internal Vector2 GetSlotTranslation(int itemIndex, int columns, double cellWidth, double cellHeight, double gap)
    {
        int orig = _initialSlots[itemIndex];
        int vis = _visualSlots[itemIndex];
        if (orig == vis) return Vector2.Zero;
        Point from = FreeGridSlotMap.SlotOrigin(orig, columns, cellWidth, cellHeight, gap);
        Point to = FreeGridSlotMap.SlotOrigin(vis, columns, cellWidth, cellHeight, gap);
        return new Vector2((float)(to.X - from.X), (float)(to.Y - from.Y));
    }

    internal bool UpdateFromPointer(
        Point draggedCenter,
        double cellWidth,
        double cellHeight,
        double gap,
        int columns,
        int totalRows,
        double deltaMs,
        bool preferDirectSwap)
    {
        int cols = Math.Max(1, columns);
        int rows = Math.Max(1, totalRows);
        int slotLimit = _freeGridEnabled
            ? Math.Max(_maxSlotCount, cols * rows)
            : _initialSlots.Length;

        int rawSlot = FreeGridSlotMap.PointToSlot(draggedCenter, cellWidth, cellHeight, gap, cols, rows);
        if (!_freeGridEnabled)
            rawSlot = Math.Clamp(rawSlot, 0, Math.Max(0, _initialSlots.Length - 1));
        else
            rawSlot = Math.Clamp(rawSlot, 0, Math.Max(0, slotLimit - 1));

        // Spatial hysteresis: require meaningful penetration into a different slot
        // before shifting targets to prevent boundary jitter.
        if (rawSlot != _committedTargetSlot)
        {
            Point currentCenter = FreeGridSlotMap.SlotCenter(_committedTargetSlot, cols, cellWidth, cellHeight, gap);
            Point candidateCenter = FreeGridSlotMap.SlotCenter(rawSlot, cols, cellWidth, cellHeight, gap);
            double hysteresisPx = Math.Max(6.0, Math.Min(cellWidth, cellHeight) * HysteresisRatio);
            double distCurrent = Distance(draggedCenter, currentCenter);
            double distCandidate = Distance(draggedCenter, candidateCenter);
            if (distCandidate + hysteresisPx > distCurrent)
            {
                _pendingOccupiedSlot = null;
                _pendingOccupiedElapsedMs = 0;
                return false;
            }
        }

        // If swap preference flipped (e.g. user pressed/released Shift while hovering same slot), recompute immediately.
        if (rawSlot == _committedTargetSlot && preferDirectSwap == _lastDirectSwap)
        {
            _pendingOccupiedSlot = null;
            _pendingOccupiedElapsedMs = 0;
            return false;
        }

        bool isTargetOccupiedByOther = FindOriginalOccupant(rawSlot) >= 0;

        // iOS 18+ transit protection in Free-Grid mode: moving into an EMPTY slot or returning
        // to the original slot commits immediately with zero lag; hovering over an OCCUPIED slot
        // requires a brief dwell (~80ms) so dragging across icons to reach an empty cell doesn't
        // accidentally scatter intermediate rows.
        if (_freeGridEnabled &&
            isTargetOccupiedByOther &&
            rawSlot != _committedTargetSlot &&
            deltaMs > 0)
        {
            if (_pendingOccupiedSlot != rawSlot)
            {
                _pendingOccupiedSlot = rawSlot;
                _pendingOccupiedElapsedMs = deltaMs;
                return false;
            }
            _pendingOccupiedElapsedMs += deltaMs;
            if (_pendingOccupiedElapsedMs < OccupiedTransitDwellMs)
                return false;
        }

        _pendingOccupiedSlot = null;
        _pendingOccupiedElapsedMs = 0;
        return CommitTargetSlot(rawSlot, preferDirectSwap, slotLimit);
    }

    internal bool CommitTargetSlot(int targetSlot, bool preferDirectSwap, int? slotLimitOverride = null)
    {
        int slotLimit = slotLimitOverride ?? _maxSlotCount;
        int clamped = _freeGridEnabled
            ? Math.Clamp(targetSlot, 0, Math.Max(0, slotLimit - 1))
            : Math.Clamp(targetSlot, 0, Math.Max(0, _initialSlots.Length - 1));

        ReorderPreviewSnapshot next = ComputeLayout(
            _initialSlots,
            _sourceItemIndex,
            clamped,
            _freeGridEnabled,
            preferDirectSwap,
            slotLimit);

        _lastDirectSwap = preferDirectSwap;
        if (next.TargetSlot == _committedTargetSlot &&
            next.Kind == _committedKind &&
            next.VisualSlots.AsSpan().SequenceEqual(_visualSlots))
        {
            return false;
        }

        _committedTargetSlot = next.TargetSlot;
        _committedKind = next.Kind;
        _visualSlots = next.VisualSlots;
        Revision++;
        return true;
    }

    internal void ResetToOriginal()
    {
        _pendingOccupiedSlot = null;
        _pendingOccupiedElapsedMs = 0;
        if (_committedTargetSlot == _sourceSlot && _committedKind == ReorderTargetKind.Original) return;
        _committedTargetSlot = _sourceSlot;
        _committedKind = ReorderTargetKind.Original;
        _visualSlots = (int[])_initialSlots.Clone();
        Revision++;
    }

    internal static ReorderPreviewSnapshot ComputeLayout(
        IReadOnlyList<int> initialSlots,
        int sourceItemIndex,
        int targetSlot,
        bool freeGridEnabled,
        bool preferDirectSwap,
        int slotLimit)
    {
        int count = initialSlots.Count;
        int[] result = initialSlots.ToArray();
        int sourceSlot = initialSlots[sourceItemIndex];

        if (targetSlot == sourceSlot)
            return new(sourceSlot, ReorderTargetKind.Original, result);

        // Build occupancy map of all non-dragged items.
        var occupantBySlot = new Dictionary<int, int>(count);
        for (int i = 0; i < count; i++)
        {
            if (i == sourceItemIndex) continue;
            occupantBySlot[initialSlots[i]] = i;
        }

        // Case 1: Empty slot in Free-Grid mode -> direct zero-disturbance placement!
        if (freeGridEnabled && !occupantBySlot.ContainsKey(targetSlot))
        {
            result[sourceItemIndex] = targetSlot;
            return new(targetSlot, ReorderTargetKind.EmptySlot, result);
        }

        // Case 2: Direct 1-to-1 Swap (A <-> B) — zero collateral shifting!
        if (preferDirectSwap && occupantBySlot.TryGetValue(targetSlot, out int swapPeer))
        {
            result[sourceItemIndex] = targetSlot;
            result[swapPeer] = sourceSlot;
            return new(targetSlot, ReorderTargetKind.DirectSwap, result);
        }

        // Case 3: Classic dense iOS reading-order insertion (non-free mode).
        if (!freeGridEnabled)
        {
            if (targetSlot < sourceSlot)
            {
                for (int i = 0; i < count; i++)
                {
                    if (i == sourceItemIndex) continue;
                    if (initialSlots[i] >= targetSlot && initialSlots[i] < sourceSlot)
                        result[i] = initialSlots[i] + 1;
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    if (i == sourceItemIndex) continue;
                    if (initialSlots[i] > sourceSlot && initialSlots[i] <= targetSlot)
                        result[i] = initialSlots[i] - 1;
                }
            }
            result[sourceItemIndex] = targetSlot;
            return new(targetSlot, ReorderTargetKind.PushAside, result);
        }

        // Case 4: iOS 18+ Free-Grid local contiguous ripple push-aside.
        // Step 4a: If sourceSlot and targetSlot are connected by a contiguous occupied chain
        // (no blank gap separating them), slide ONLY the icons between targetSlot and sourceSlot
        // cleanly into sourceSlot (exact iOS flow inside a group without disturbing distant groups!).
        if (IsContiguousChainBetween(targetSlot, sourceSlot, occupantBySlot))
        {
            int step = targetSlot < sourceSlot ? 1 : -1;
            int cursor = targetSlot;
            while (cursor != sourceSlot)
            {
                if (occupantBySlot.TryGetValue(cursor, out int itemIdx))
                    result[itemIdx] = cursor + step;
                cursor += step;
            }
            result[sourceItemIndex] = targetSlot;
            return new(targetSlot, ReorderTargetKind.PushAside, result);
        }

        // Step 4b: Otherwise, ripple forward ONLY up to the very first empty slot at/after targetSlot
        // (or backward if forward hits the hard limit). Icons beyond the first empty gap NEVER move!
        int forwardGap = targetSlot + 1;
        while (forwardGap < slotLimit && occupantBySlot.ContainsKey(forwardGap))
            forwardGap++;

        if (forwardGap < slotLimit)
        {
            for (int slot = forwardGap - 1; slot >= targetSlot; slot--)
            {
                if (occupantBySlot.TryGetValue(slot, out int itemIdx))
                    result[itemIdx] = slot + 1;
            }
            result[sourceItemIndex] = targetSlot;
            return new(targetSlot, ReorderTargetKind.PushAside, result);
        }

        int backwardGap = targetSlot - 1;
        while (backwardGap >= 0 && occupantBySlot.ContainsKey(backwardGap))
            backwardGap--;

        if (backwardGap >= 0)
        {
            for (int slot = backwardGap + 1; slot <= targetSlot; slot++)
            {
                if (occupantBySlot.TryGetValue(slot, out int itemIdx))
                    result[itemIdx] = slot - 1;
            }
            result[sourceItemIndex] = targetSlot;
            return new(targetSlot, ReorderTargetKind.PushAside, result);
        }

        // Grid completely saturated -> swap cleanly into sourceSlot.
        if (occupantBySlot.TryGetValue(targetSlot, out int fallbackPeer))
        {
            result[sourceItemIndex] = targetSlot;
            result[fallbackPeer] = sourceSlot;
            return new(targetSlot, ReorderTargetKind.DirectSwap, result);
        }

        result[sourceItemIndex] = targetSlot;
        return new(targetSlot, ReorderTargetKind.EmptySlot, result);
    }

    private int FindOriginalOccupant(int slot)
    {
        for (int i = 0; i < _initialSlots.Length; i++)
        {
            if (i != _sourceItemIndex && _initialSlots[i] == slot)
                return i;
        }
        return -1;
    }

    private static bool IsContiguousChainBetween(int targetSlot, int sourceSlot, Dictionary<int, int> occupantBySlot)
    {
        int step = targetSlot < sourceSlot ? 1 : -1;
        for (int slot = targetSlot; slot != sourceSlot; slot += step)
        {
            if (!occupantBySlot.ContainsKey(slot))
                return false;
        }
        return true;
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
