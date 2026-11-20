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
/// 2. Bidirectional nearest-gap ripple push-aside with pointer approach-vector tie-breaking
///    (always chooses the shortest contiguous shift to either side — never a 15-puzzle snake);
/// 3. Direct 1-to-1 swap (A &lt;-&gt; B) via setting or holding Shift during drag;
/// 4. Spatial hysteresis + brief dwell threshold over occupied slots;
/// 5. iOS 18 multi-icon stack drop (ComputeStackLayout) placing a fanned selection deck cleanly.
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
    private readonly int[] _draggedStackIndices;

    private int _committedTargetSlot;
    private ReorderTargetKind _committedKind = ReorderTargetKind.Original;
    private int[] _visualSlots;

    private int? _pendingOccupiedSlot;
    private double _pendingOccupiedElapsedMs;
    private bool _lastDirectSwap;
    private Point? _lastPointerCenter;
    private double _approachDeltaX;

    internal FreeGridReorderEngine(
        IReadOnlyList<int> initialSlots,
        int sourceItemIndex,
        bool freeGridEnabled,
        int maxSlotCount,
        IReadOnlyList<int>? draggedStackIndices = null)
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

        if (draggedStackIndices is { Count: > 1 })
        {
            var stack = new List<int>(draggedStackIndices.Count) { sourceItemIndex };
            foreach (int idx in draggedStackIndices)
            {
                if (idx >= 0 && idx < _initialSlots.Length && idx != sourceItemIndex && !stack.Contains(idx))
                    stack.Add(idx);
            }
            _draggedStackIndices = stack.ToArray();
        }
        else
        {
            _draggedStackIndices = [sourceItemIndex];
        }
    }

    internal int SourceItemIndex => _sourceItemIndex;
    internal int SourceSlot => _sourceSlot;
    internal int TargetSlot => _committedTargetSlot;
    internal ReorderTargetKind CurrentKind => _committedKind;
    internal int Revision { get; private set; }
    internal bool IsMultiStackDrag => _draggedStackIndices.Length > 1;
    internal IReadOnlyList<int> DraggedStackIndices => _draggedStackIndices;

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

    internal int GetSlotHopDistanceFromTarget(int itemIndex, int columns)
    {
        int cols = Math.Max(1, columns);
        int vis = GetVisualSlot(itemIndex);
        int dr = Math.Abs(vis / cols - _committedTargetSlot / cols);
        int dc = Math.Abs(vis % cols - _committedTargetSlot % cols);
        return dr + dc;
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
        if (_lastPointerCenter is Point prev)
        {
            double dx = draggedCenter.X - prev.X;
            if (Math.Abs(dx) > 0.5)
                _approachDeltaX = _approachDeltaX * 0.65 + dx * 0.35;
        }
        _lastPointerCenter = draggedCenter;

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

        if (rawSlot == _committedTargetSlot && preferDirectSwap == _lastDirectSwap)
        {
            _pendingOccupiedSlot = null;
            _pendingOccupiedElapsedMs = 0;
            return false;
        }

        bool isTargetOccupiedByOther = FindOriginalOccupant(rawSlot) >= 0;

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
        return CommitTargetSlot(rawSlot, preferDirectSwap, slotLimit, _approachDeltaX);
    }

    internal bool CommitTargetSlot(
        int targetSlot,
        bool preferDirectSwap,
        int? slotLimitOverride = null,
        double approachDeltaX = 0)
    {
        int slotLimit = slotLimitOverride ?? _maxSlotCount;
        int clamped = _freeGridEnabled
            ? Math.Clamp(targetSlot, 0, Math.Max(0, slotLimit - 1))
            : Math.Clamp(targetSlot, 0, Math.Max(0, _initialSlots.Length - 1));

        ReorderPreviewSnapshot next = IsMultiStackDrag
            ? ComputeStackLayout(_initialSlots, _draggedStackIndices, clamped, _freeGridEnabled, slotLimit)
            : ComputeLayout(_initialSlots, _sourceItemIndex, clamped, _freeGridEnabled, preferDirectSwap, slotLimit, approachDeltaX);

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
        int slotLimit,
        double approachDeltaX = 0)
    {
        int count = initialSlots.Count;
        int[] result = initialSlots.ToArray();
        int sourceSlot = initialSlots[sourceItemIndex];

        if (targetSlot == sourceSlot)
            return new(sourceSlot, ReorderTargetKind.Original, result);

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

        // Case 4a: Contiguous chain between targetSlot and sourceSlot -> slide directly into sourceSlot.
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

        // Case 4b: Bidirectional nearest-gap ripple (true iOS 18/26 minimal-churn wave).
        // Compare distance to the nearest empty gap forward vs backward and pick the shorter chain,
        // using pointer approach vector as the tie-breaker so icons part naturally away from approach.
        int forwardGap = targetSlot + 1;
        while (forwardGap < slotLimit && occupantBySlot.ContainsKey(forwardGap))
            forwardGap++;
        bool forwardValid = forwardGap < slotLimit;
        int forwardDist = forwardValid ? forwardGap - targetSlot : int.MaxValue;

        int backwardGap = targetSlot - 1;
        while (backwardGap >= 0 && occupantBySlot.ContainsKey(backwardGap))
            backwardGap--;
        bool backwardValid = backwardGap >= 0;
        int backwardDist = backwardValid ? targetSlot - backwardGap : int.MaxValue;

        if (forwardValid || backwardValid)
        {
            bool chooseBackward = backwardValid && (
                !forwardValid ||
                backwardDist < forwardDist ||
                (backwardDist == forwardDist && approachDeltaX < 0));

            if (chooseBackward)
            {
                for (int slot = backwardGap + 1; slot <= targetSlot; slot++)
                {
                    if (occupantBySlot.TryGetValue(slot, out int itemIdx))
                        result[itemIdx] = slot - 1;
                }
            }
            else
            {
                for (int slot = forwardGap - 1; slot >= targetSlot; slot--)
                {
                    if (occupantBySlot.TryGetValue(slot, out int itemIdx))
                        result[itemIdx] = slot + 1;
                }
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

    /// <summary>
    /// Computes an iOS 18 multi-icon stack drop layout: keeps all non-selected items stable
    /// whenever the target run is free, and smoothly pushes only colliding items to the nearest
    /// open slots when dropping a stack onto occupied cells.
    /// </summary>
    internal static ReorderPreviewSnapshot ComputeStackLayout(
        IReadOnlyList<int> initialSlots,
        IReadOnlyList<int> draggedIndices,
        int anchorTargetSlot,
        bool freeGridEnabled,
        int slotLimit)
    {
        int count = initialSlots.Count;
        int[] result = initialSlots.ToArray();
        if (draggedIndices.Count == 0)
            return new(anchorTargetSlot, ReorderTargetKind.Original, result);

        int primaryIndex = draggedIndices[0];
        if (anchorTargetSlot == initialSlots[primaryIndex])
            return new(anchorTargetSlot, ReorderTargetKind.Original, result);

        var draggedSet = new HashSet<int>(draggedIndices);
        int effectiveLimit = Math.Max(slotLimit, count + draggedIndices.Count);

        if (!freeGridEnabled)
        {
            // Dense mode: extract remaining items in reading order and splice the dragged deck at anchorTargetSlot.
            var remaining = Enumerable.Range(0, count)
                .Where(i => !draggedSet.Contains(i))
                .OrderBy(i => initialSlots[i])
                .ToList();
            int insertAt = Math.Clamp(anchorTargetSlot, 0, remaining.Count);
            remaining.InsertRange(insertAt, draggedIndices);
            for (int slot = 0; slot < remaining.Count; slot++)
                result[remaining[slot]] = slot;
            return new(insertAt, ReorderTargetKind.PushAside, result);
        }

        // Free-Grid mode: reserve consecutive slots starting at anchorTargetSlot for the stack,
        // then relocate only directly displaced non-selected items to the nearest open slots.
        int maxStart = Math.Max(0, effectiveLimit - draggedIndices.Count);
        int clampedStart = Math.Clamp(anchorTargetSlot, 0, maxStart);

        var occupied = new HashSet<int>();
        var reservedByStack = new HashSet<int>();
        for (int k = 0; k < draggedIndices.Count; k++)
        {
            int target = clampedStart + k;
            result[draggedIndices[k]] = target;
            reservedByStack.Add(target);
            occupied.Add(target);
        }

        var displaced = new List<int>();
        for (int i = 0; i < count; i++)
        {
            if (draggedSet.Contains(i)) continue;
            int slot = initialSlots[i];
            if (reservedByStack.Contains(slot))
                displaced.Add(i);
            else
                occupied.Add(slot);
        }

        bool anyDisplaced = displaced.Count > 0;
        // Relocate displaced items in reading order to nearest free cells without disturbing unrelated clusters.
        displaced.Sort((a, b) => initialSlots[a].CompareTo(initialSlots[b]));
        int expandedBound = Math.Max(effectiveLimit, (occupied.Count > 0 ? occupied.Max() : 0) + displaced.Count + 8);
        foreach (int itemIdx in displaced)
        {
            int nextSlot = FreeGridSlotMap.FindNearestFreeSlot(clampedStart + draggedIndices.Count, occupied, expandedBound);
            result[itemIdx] = nextSlot;
            occupied.Add(nextSlot);
        }

        return new(clampedStart, anyDisplaced ? ReorderTargetKind.PushAside : ReorderTargetKind.EmptySlot, result);
    }

    private int FindOriginalOccupant(int slot)
    {
        var dragged = new HashSet<int>(_draggedStackIndices);
        for (int i = 0; i < _initialSlots.Length; i++)
        {
            if (!dragged.Contains(i) && _initialSlots[i] == slot)
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
