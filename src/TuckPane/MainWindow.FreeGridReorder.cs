using System.Diagnostics;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TuckPane.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;
using Windows.UI;

namespace TuckPane;

public sealed partial class MainWindow
{
    private readonly FreeSlotGridLayout _freeSlotGridLayout = new();
    private int[] _resolvedItemSlots = [];
    private FreeGridReorderEngine? _freeGridEngine;
    private readonly List<Border> _freeGridGuideCells = [];
    private Vector3 _dropGhostPosition;
    private Vector3 _dropGhostVelocity;
    private Vector3 _dropGhostTarget;
    private bool _dropGhostPositioned;
    private float _dragTiltAngleDeg;
    private float _dragTiltVelocity;
    private int? _pendingExternalDropSlot;

    private bool IsFreeGridLayoutActive =>
        !IsDock &&
        !IsCompactList &&
        _host.State.GlobalSettings.FreeGridPlacementEnabled &&
        !HasActiveFilter;

    private bool UsesIosFluidReorder => !IsDock && !IsCompactList;

    internal void RefreshFreeGridLayoutMode()
    {
        if (_closing) return;
        SyncResolvedItemSlots();
        ConfigureItemsLayout(updateItems: true);
    }

    private void SyncResolvedItemSlots()
    {
        if (_items.Count == 0)
        {
            _resolvedItemSlots = [];
            return;
        }

        string[] names = _items.Select(i => i.RelativeName).ToArray();

        // If external files were just dropped onto a specific free-grid cell, bind new items there first.
        if (IsFreeGridLayoutActive && _pendingExternalDropSlot is int preferredSlot)
        {
            _pendingExternalDropSlot = null;
            _definition.ItemSlots ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var existingOccupied = new HashSet<int>();
            foreach (string name in names)
            {
                if (_definition.ItemSlots.TryGetValue(name, out int slot) && slot >= 0)
                    existingOccupied.Add(slot);
            }

            int cols = Math.Max(1, GetItemLayoutColumnCount());
            int minRows = Math.Max(1, _definition.Layout.Rows);
            int maxSlotBound = Math.Max(cols * minRows, (existingOccupied.Count > 0 ? existingOccupied.Max() : 0) + cols * 2);

            bool assignedAny = false;
            int cursorSlot = preferredSlot;
            foreach (string name in names)
            {
                if (_definition.ItemSlots.ContainsKey(name)) continue;
                int chosen = FreeGridSlotMap.FindNearestFreeSlot(cursorSlot, existingOccupied, maxSlotBound);
                _definition.ItemSlots[name] = chosen;
                existingOccupied.Add(chosen);
                cursorSlot = chosen + 1;
                assignedAny = true;
            }
            if (assignedAny)
            {
                _resolvedItemSlots = FreeGridSlotMap.ResolveSlots(names, _definition.ItemSlots);
                _definition.ItemOrder = FreeGridSlotMap.BuildReadingOrder(names, _resolvedItemSlots);
                _ = SaveStateAsync();
                _freeSlotGridLayout.InvalidateSlots();
                return;
            }
        }

        _resolvedItemSlots = IsFreeGridLayoutActive
            ? FreeGridSlotMap.ResolveSlots(names, _definition.ItemSlots)
            : Enumerable.Range(0, names.Length).ToArray();
        _freeSlotGridLayout.InvalidateSlots();
    }

    private int ResolveFreeGridSlotForLayout(int itemIndex)
    {
        if (itemIndex >= 0 && itemIndex < _resolvedItemSlots.Length)
            return _resolvedItemSlots[itemIndex];
        return itemIndex;
    }

    private bool TryConfigureFreeSlotGridLayout(double width, double height, double cellWidth, double cellHeight)
    {
        if (!IsFreeGridLayoutActive) return false;
        SyncResolvedItemSlots();
        _freeSlotGridLayout.CellWidth = cellWidth;
        _freeSlotGridLayout.CellHeight = cellHeight;
        _freeSlotGridLayout.Gap = GetItemLayoutGapDip();
        _freeSlotGridLayout.Columns = GetItemLayoutColumnCount();
        _freeSlotGridLayout.MinimumVisibleRows = Math.Max(1, _definition.Layout.Rows);
        _freeSlotGridLayout.SlotResolver = ResolveFreeGridSlotForLayout;
        if (!ReferenceEquals(ItemsRepeater.Layout, _freeSlotGridLayout))
            ItemsRepeater.Layout = _freeSlotGridLayout;
        SyncFreeGridOverlayBounds(
            _freeSlotGridLayout.Columns,
            FreeGridSlotMap.TotalRowCount(
                _resolvedItemSlots,
                _freeSlotGridLayout.Columns,
                _freeSlotGridLayout.MinimumVisibleRows,
                _freeSlotGridLayout.IncludeDragBufferRow),
            cellWidth,
            cellHeight,
            _freeSlotGridLayout.Gap);
        _freeSlotGridLayout.InvalidateSlots();
        return true;
    }

    private (double Width, double Height) ResolveDragCellSize()
    {
        double w = _itemDragHost is { } host && double.IsFinite(host.Width) && host.Width > 1
            ? host.Width
            : _itemDragHost is { } h2 && h2.ActualWidth > 1
                ? h2.ActualWidth
                : Math.Max(1, _freeSlotGridLayout.CellWidth);
        double h = _itemDragHost is { } hostH && double.IsFinite(hostH.Height) && hostH.Height > 1
            ? hostH.Height
            : _itemDragHost is { } h3 && h3.ActualHeight > 1
                ? h3.ActualHeight
                : Math.Max(1, _freeSlotGridLayout.CellHeight);
        return (w, h);
    }

    private void SyncFreeGridOverlayBounds(int columns, int totalRows, double cellWidth, double cellHeight, double gap)
    {
        FreeGridOverlayCanvas.Margin = ItemsRepeater.Margin;
        FreeGridOverlayCanvas.Width = Math.Max(1, columns * cellWidth + Math.Max(0, columns - 1) * gap);
        FreeGridOverlayCanvas.Height = Math.Max(1, totalRows * cellHeight + Math.Max(0, totalRows - 1) * gap);
    }

    private void BeginIosFluidReorder(ItemReorderSession session)
    {
        if (!UsesIosFluidReorder)
        {
            _freeGridEngine = null;
            return;
        }

        SyncResolvedItemSlots();
        int columns = Math.Max(1, GetItemLayoutColumnCount());
        int minRows = Math.Max(1, _definition.Layout.Rows);
        bool freeGrid = IsFreeGridLayoutActive;
        int totalRows = FreeGridSlotMap.TotalRowCount(_resolvedItemSlots, columns, minRows, includeDragBufferRow: freeGrid);
        int maxSlots = freeGrid ? columns * totalRows : _items.Count;

        _freeGridEngine = new FreeGridReorderEngine(_resolvedItemSlots, session.SourceIndex, freeGrid, maxSlots);
        _dragTiltAngleDeg = 0;
        _dragTiltVelocity = 0;
        _dropGhostPositioned = false;

        (double cellWidth, double cellHeight) = ResolveDragCellSize();
        SyncFreeGridOverlayBounds(columns, totalRows, cellWidth, cellHeight, GetItemLayoutGapDip());

        if (freeGrid)
        {
            _freeSlotGridLayout.IncludeDragBufferRow = true;
            BuildFreeGridGuideCells(columns, totalRows);
            ShowFreeGridDropGhost(_freeGridEngine.SourceSlot, ReorderTargetKind.Original, immediate: true);
        }

        if (_itemDragHost is { } draggedHost)
        {
            try
            {
                var shadow = new ThemeShadow();
                shadow.Receivers.Add(FreeGridOverlayCanvas);
                draggedHost.Shadow = shadow;
            }
            catch
            {
                draggedHost.Shadow = null;
            }
            ItemMotionState motion = GetItemMotion(draggedHost);
            motion.ScaleTarget = _itemReorderUsesAnimations ? new Vector3(1.12f, 1.12f, 1f) : Vector3.One;
            motion.RotationAngle = 0;
        }
    }

    private void UpdateIosFluidReorderFrame(
        ItemReorderSession session,
        Point pointerContent,
        double seconds)
    {
        if (_freeGridEngine is null || _itemDragHost is null) return;

        int columns = Math.Max(1, GetItemLayoutColumnCount());
        (double cellWidth, double cellHeight) = ResolveDragCellSize();
        double gap = GetItemLayoutGapDip();
        bool freeGrid = IsFreeGridLayoutActive;
        int totalRows = FreeGridSlotMap.TotalRowCount(
            _resolvedItemSlots,
            columns,
            Math.Max(1, _definition.Layout.Rows),
            includeDragBufferRow: freeGrid);

        Point draggedCenter = new(
            pointerContent.X - session.GrabOffset.X + cellWidth / 2.0,
            pointerContent.Y - session.GrabOffset.Y + cellHeight / 2.0);

        bool shiftHeld = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
        bool preferDirectSwap = _host.State.GlobalSettings.FreeGridPreferDirectSwap ^ shiftHeld;

        bool changed = _freeGridEngine.UpdateFromPointer(
            draggedCenter,
            cellWidth,
            cellHeight,
            gap,
            columns,
            totalRows,
            seconds * 1000.0,
            preferDirectSwap);

        if (changed)
        {
            ApplyIosFluidTargets(columns, cellWidth, cellHeight, gap);
            ShowFreeGridDropGhost(_freeGridEngine.TargetSlot, _freeGridEngine.CurrentKind, immediate: !_itemReorderUsesAnimations);
        }

        // Continuously integrate all displaced icons with velocity-preserving iOS underdamped springs!
        StepIosFluidItemSprings(seconds, directDragHost: _itemDragHost);

        // Animate lifted dragged host scale + subtle velocity-coupled iOS tilt.
        ItemMotionState draggedMotion = GetItemMotion(_itemDragHost);
        Vector3 prevPos = draggedMotion.Translation;
        Vector3 nextTranslation = new(
            (float)(pointerContent.X - session.PressPointerContent.X) + _hoverDragOriginOffset.X,
            (float)(pointerContent.Y - session.PressPointerContent.Y) + _hoverDragOriginOffset.Y,
            24f);
        if (seconds > 0)
            draggedMotion.TranslationVelocity = (nextTranslation - prevPos) / (float)seconds;
        draggedMotion.Translation = nextTranslation;
        draggedMotion.TranslationTarget = nextTranslation;
        _itemDragHost.Translation = nextTranslation;

        if (_itemReorderUsesAnimations)
        {
            ItemMotionMath.StepIosSpring(
                ref draggedMotion.Scale,
                ref draggedMotion.ScaleVelocity,
                draggedMotion.ScaleTarget,
                seconds,
                responseSeconds: 0.24,
                dampingFraction: 0.78);
            _itemDragHost.Scale = draggedMotion.Scale;

            float targetTilt = Math.Clamp(draggedMotion.TranslationVelocity.X / 360f, -4.5f, 4.5f);
            _dragTiltAngleDeg = ItemMotionMath.StepScalarSpring(
                ref _dragTiltAngleDeg,
                ref _dragTiltVelocity,
                targetTilt,
                seconds,
                responseSeconds: 0.22,
                dampingFraction: 0.76);
            _itemDragHost.Rotation = _dragTiltAngleDeg;
        }

        StepFreeGridDropGhostSpring(seconds);
    }

    private void ApplyIosFluidTargets(int columns, double cellWidth, double cellHeight, double gap)
    {
        if (_freeGridEngine is null) return;
        for (int index = 0; index < _items.Count; index++)
        {
            if (index == _freeGridEngine.SourceItemIndex || !TryGetRealizedItemHost(index, out Border host)) continue;
            Vector2 delta = _freeGridEngine.GetSlotTranslation(index, columns, cellWidth, cellHeight, gap);
            ItemMotionState motion = GetItemMotion(host);
            motion.TranslationTarget = new Vector3(delta, 0);
            motion.ScaleTarget = delta.LengthSquared() > 1f ? new Vector3(0.97f, 0.97f, 1f) : Vector3.One;
            if (!_itemReorderUsesAnimations)
            {
                motion.Translation = motion.TranslationTarget;
                motion.TranslationVelocity = Vector3.Zero;
                motion.Scale = Vector3.One;
                motion.ScaleVelocity = Vector3.Zero;
                host.Translation = motion.Translation;
                host.Scale = Vector3.One;
            }
        }
    }

    private bool StepIosFluidItemSprings(double seconds, Border? directDragHost)
    {
        if (!_itemReorderUsesAnimations)
        {
            foreach ((Border host, ItemMotionState motion) in _itemMotionStates)
            {
                if (ReferenceEquals(host, directDragHost)) continue;
                motion.Translation = motion.TranslationTarget;
                motion.TranslationVelocity = Vector3.Zero;
                motion.Scale = motion.ScaleTarget;
                motion.ScaleVelocity = Vector3.Zero;
                host.Translation = motion.Translation;
                host.Scale = motion.Scale;
                host.Rotation = 0;
            }
            return true;
        }

        bool settled = true;
        foreach ((Border host, ItemMotionState motion) in _itemMotionStates)
        {
            if (!IsFinite(motion.Translation) || !IsFinite(motion.TranslationTarget) || !IsFinite(motion.TranslationVelocity))
            {
                motion.Translation = Vector3.Zero;
                motion.TranslationTarget = Vector3.Zero;
                motion.TranslationVelocity = Vector3.Zero;
            }
            if (!IsFinite(motion.Scale) || !IsFinite(motion.ScaleTarget) || !IsFinite(motion.ScaleVelocity))
            {
                motion.Scale = Vector3.One;
                motion.ScaleTarget = Vector3.One;
                motion.ScaleVelocity = Vector3.Zero;
            }
            if (!ReferenceEquals(host, directDragHost))
            {
                settled &= ItemMotionMath.StepIosSpring(
                    ref motion.Translation,
                    ref motion.TranslationVelocity,
                    motion.TranslationTarget,
                    seconds,
                    responseSeconds: 0.28,
                    dampingFraction: 0.84);
                host.Translation = motion.Translation;

                // Displaced siblings return gracefully to scale 1.0 as they approach their destination.
                Vector3 targetScale = Vector3.DistanceSquared(motion.Translation, motion.TranslationTarget) < 64f
                    ? Vector3.One
                    : motion.ScaleTarget;
                settled &= ItemMotionMath.StepIosSpring(
                    ref motion.Scale,
                    ref motion.ScaleVelocity,
                    targetScale,
                    seconds,
                    responseSeconds: 0.26,
                    dampingFraction: 0.85);
                host.Scale = motion.Scale;
            }
        }

        if (directDragHost is null && Math.Abs(_dragTiltAngleDeg) > 0.02f)
        {
            _dragTiltAngleDeg = ItemMotionMath.StepScalarSpring(
                ref _dragTiltAngleDeg,
                ref _dragTiltVelocity,
                0f,
                seconds,
                responseSeconds: 0.20,
                dampingFraction: 0.82);
            if (_itemDragHost is { } landingHost)
                landingHost.Rotation = _dragTiltAngleDeg;
            if (Math.Abs(_dragTiltAngleDeg) > 0.05f) settled = false;
        }

        return settled;
    }

    private async Task CommitIosFluidReorderAsync(ItemReorderSession session)
    {
        FreeGridReorderEngine? engine = _freeGridEngine;
        if (engine is null) return;

        try
        {
            StopItemDragInput(keepRendering: true);
            int columns = Math.Max(1, GetItemLayoutColumnCount());
            int[] finalSlots = engine.SnapshotVisualSlots();
            _itemDragLanding = true;

            if (_itemDragHost is { } draggedHost)
            {
                (double landCellW, double landCellH) = ResolveDragCellSize();
                Vector2 landingDelta = engine.GetSlotTranslation(
                    engine.SourceItemIndex,
                    columns,
                    landCellW,
                    landCellH,
                    GetItemLayoutGapDip());
                if (_itemReorderUsesAnimations)
                {
                    ItemMotionState motion = GetItemMotion(draggedHost);
                    motion.TranslationTarget = new Vector3(landingDelta, 0);
                    motion.ScaleTarget = Vector3.One;
                    _itemMotionSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await Task.WhenAny(_itemMotionSettled.Task, Task.Delay(320));
                }
            }

            if (!ReferenceEquals(_itemReorderSession, session)) return;
            StopItemDragInput(keepRendering: false);

            Dictionary<string, Point>? visualPositions = _itemReorderUsesAnimations ? CaptureRealizedItemPositions() : null;
            CleanupIosFreeGridOverlays();
            ResetItemDragVisuals();

            bool changed = !finalSlots.AsSpan().SequenceEqual(_resolvedItemSlots);
            if (changed)
            {
                string[] currentNames = _items.Select(i => i.RelativeName).ToArray();
                List<string> previousOrder = _definition.ItemOrder.ToList();
                var previousSlots = new Dictionary<string, int>(_definition.ItemSlots ?? [], StringComparer.OrdinalIgnoreCase);

                Dictionary<string, int> nextSlotMap = FreeGridSlotMap.BuildDictionary(currentNames, finalSlots);
                List<string> nextReadingOrder = FreeGridSlotMap.BuildReadingOrder(currentNames, finalSlots);

                _definition.ItemSlots = nextSlotMap;
                _definition.ItemOrder = nextReadingOrder;
                try
                {
                    await _host.SaveStateAsync();
                }
                catch
                {
                    _definition.ItemOrder = previousOrder;
                    _definition.ItemSlots = previousSlots;
                    throw;
                }

                if (IsFreeGridLayoutActive)
                {
                    _resolvedItemSlots = finalSlots;
                    _freeSlotGridLayout.InvalidateSlots();
                    ItemsRepeater.UpdateLayout();
                }
                else
                {
                    // Dense mode: reorder the observable collection in place to match reading order.
                    var lookup = _items.ToDictionary(i => i.RelativeName, StringComparer.OrdinalIgnoreCase);
                    WidgetItem[] desired = nextReadingOrder
                        .Where(lookup.ContainsKey)
                        .Select(name => lookup[name])
                        .ToArray();
                    _itemCollectionMoveInProgress = true;
                    try
                    {
                        _ = CatalogCollectionSync.ApplyReorderInPlace(_items, desired);
                        _resolvedItemSlots = Enumerable.Range(0, _items.Count).ToArray();
                        ItemsRepeater.UpdateLayout();
                    }
                    finally
                    {
                        _itemCollectionMoveInProgress = false;
                    }
                }

                if (visualPositions is not null) ApplyFlipFromCapturedPositions(visualPositions);
                _host.NotifyOrganizerPreviewChanged(_definition.Id);
            }

            session.MarkOutcome(ItemDragState.Committed);
            FinishItemReorderSession();
        }
        finally
        {
            CleanupIosFreeGridOverlays();
            _freeGridEngine = null;
        }
    }

    private void CleanupIosFreeGridOverlays()
    {
        _freeSlotGridLayout.IncludeDragBufferRow = false;
        FreeGridDropSlotGhost.Visibility = Visibility.Collapsed;
        _dropGhostPositioned = false;
        _dragTiltAngleDeg = 0;
        _dragTiltVelocity = 0;
        if (_itemDragHost is { } host)
        {
            host.Rotation = 0;
            host.Shadow = null;
        }
        foreach (Border cell in _freeGridGuideCells)
            FreeGridOverlayCanvas.Children.Remove(cell);
        _freeGridGuideCells.Clear();
    }

    private void BuildFreeGridGuideCells(int columns, int totalRows)
    {
        foreach (Border existing in _freeGridGuideCells)
            FreeGridOverlayCanvas.Children.Remove(existing);
        _freeGridGuideCells.Clear();

        double cellWidth = _freeSlotGridLayout.CellWidth;
        double cellHeight = _freeSlotGridLayout.CellHeight;
        double gap = _freeSlotGridLayout.Gap;
        var strokeBrush = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));
        var fillBrush = new SolidColorBrush(Color.FromArgb(8, 255, 255, 255));

        int totalCells = Math.Min(columns * totalRows, 96);
        for (int slot = 0; slot < totalCells; slot++)
        {
            Point origin = FreeGridSlotMap.SlotOrigin(slot, columns, cellWidth, cellHeight, gap);
            var cell = new Border
            {
                Width = cellWidth,
                Height = cellHeight,
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                BorderBrush = strokeBrush,
                Background = fillBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(cell, origin.X);
            Canvas.SetTop(cell, origin.Y);
            Canvas.SetZIndex(cell, 0);
            FreeGridOverlayCanvas.Children.Insert(0, cell);
            _freeGridGuideCells.Add(cell);
        }
    }

    private void ShowFreeGridDropGhost(int slot, ReorderTargetKind kind, bool immediate)
    {
        if (!IsFreeGridLayoutActive && kind == ReorderTargetKind.Original)
        {
            FreeGridDropSlotGhost.Visibility = Visibility.Collapsed;
            return;
        }

        int columns = Math.Max(1, GetItemLayoutColumnCount());
        (double cellWidth, double cellHeight) = ResolveDragCellSize();
        double gap = GetItemLayoutGapDip();
        FreeGridOverlayCanvas.Margin = ItemsRepeater.Margin;
        Point origin = FreeGridSlotMap.SlotOrigin(slot, columns, cellWidth, cellHeight, gap);

        FreeGridDropSlotGhost.Width = cellWidth;
        FreeGridDropSlotGhost.Height = cellHeight;
        bool isSwap = kind == ReorderTargetKind.DirectSwap;
        FreeGridDropSlotGhost.Background = new SolidColorBrush(isSwap
            ? Color.FromArgb(56, 0, 120, 212)
            : Color.FromArgb(38, 255, 255, 255));
        FreeGridDropSlotGhost.BorderBrush = new SolidColorBrush(isSwap
            ? Color.FromArgb(220, 0, 120, 212)
            : Color.FromArgb(145, 255, 255, 255));
        FreeGridDropSlotGhost.Visibility = Visibility.Visible;

        _dropGhostTarget = new Vector3((float)origin.X, (float)origin.Y, 0);
        if (immediate || !_dropGhostPositioned)
        {
            _dropGhostPosition = _dropGhostTarget;
            _dropGhostVelocity = Vector3.Zero;
            _dropGhostPositioned = true;
            Canvas.SetLeft(FreeGridDropSlotGhost, _dropGhostPosition.X);
            Canvas.SetTop(FreeGridDropSlotGhost, _dropGhostPosition.Y);
        }
    }

    private void StepFreeGridDropGhostSpring(double seconds)
    {
        if (!_dropGhostPositioned || FreeGridDropSlotGhost.Visibility != Visibility.Visible) return;
        ItemMotionMath.StepIosSpring(
            ref _dropGhostPosition,
            ref _dropGhostVelocity,
            _dropGhostTarget,
            seconds,
            responseSeconds: 0.22,
            dampingFraction: 0.86);
        Canvas.SetLeft(FreeGridDropSlotGhost, _dropGhostPosition.X);
        Canvas.SetTop(FreeGridDropSlotGhost, _dropGhostPosition.Y);
    }

    private void RecordExternalDropSlotFromEvent(DragEventArgs e)
    {
        if (!IsFreeGridLayoutActive)
        {
            _pendingExternalDropSlot = null;
            return;
        }
        Point dropPoint = e.GetPosition(ItemsRepeater);
        int columns = Math.Max(1, GetItemLayoutColumnCount());
        int totalRows = FreeGridSlotMap.TotalRowCount(
            _resolvedItemSlots,
            columns,
            Math.Max(1, _definition.Layout.Rows),
            includeDragBufferRow: true);
        _pendingExternalDropSlot = FreeGridSlotMap.PointToSlot(
            dropPoint,
            _freeSlotGridLayout.CellWidth,
            _freeSlotGridLayout.CellHeight,
            _freeSlotGridLayout.Gap,
            columns,
            totalRows);
    }

    private async void CompactGridGapsMenuItem_Click(object sender, RoutedEventArgs e) =>
        await RunSafelyAsync(CompactGridGapsAsync, "紧凑排布方格空位失败");

    internal async Task CompactGridGapsAsync()
    {
        if (_closing || _items.Count == 0) return;
        Dictionary<string, Point>? before = UseCustomAnimations && _expanded ? CaptureRealizedItemPositions() : null;

        string[] names = _items.Select(i => i.RelativeName).ToArray();
        List<string> readingOrder = FreeGridSlotMap.BuildReadingOrder(names, _resolvedItemSlots);
        _definition.ItemOrder = readingOrder;
        _definition.ItemSlots = FreeGridSlotMap.CompactToDense(readingOrder);
        await SaveStateAsync();

        var lookup = _items.ToDictionary(i => i.RelativeName, StringComparer.OrdinalIgnoreCase);
        WidgetItem[] ordered = readingOrder
            .Where(lookup.ContainsKey)
            .Select(n => lookup[n])
            .ToArray();
        ApplyCatalogItemsAndRender(ordered, refreshIcons: false);
        SyncResolvedItemSlots();
        ItemsRepeater.UpdateLayout();
        if (before is not null) ApplyFlipFromCapturedPositions(before);
        _host.NotifyOrganizerPreviewChanged(_definition.Id);
    }
}
