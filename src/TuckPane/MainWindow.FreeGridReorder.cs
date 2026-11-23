using System.Diagnostics;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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
    private static readonly SolidColorBrush GuideCellStrokeBrush = new(Color.FromArgb(30, 255, 255, 255));
    private static readonly SolidColorBrush GuideCellFillBrush = new(Color.FromArgb(10, 255, 255, 255));
    private static readonly SolidColorBrush DropGhostSwapBgBrush = new(Color.FromArgb(62, 0, 120, 212));
    private static readonly SolidColorBrush DropGhostSwapBorderBrush = new(Color.FromArgb(230, 0, 120, 212));
    private static readonly SolidColorBrush DropGhostNormalBgBrush = new(Color.FromArgb(40, 255, 255, 255));
    private static readonly SolidColorBrush DropGhostNormalBorderBrush = new(Color.FromArgb(155, 255, 255, 255));
    private static readonly SolidColorBrush SwapPeerBorderBrush = new(Color.FromArgb(200, 0, 120, 212));
    private static readonly SolidColorBrush DraggedElevatedBgBrush = new(Color.FromArgb(36, 255, 255, 255));
    private static readonly SolidColorBrush DraggedElevatedBorderBrush = new(Color.FromArgb(135, 255, 255, 255));

    private readonly FreeSlotGridLayout _freeSlotGridLayout = new();
    private int[] _resolvedItemSlots = [];
    private readonly Dictionary<string, int> _resolvedSlotByRelativeName = new(StringComparer.OrdinalIgnoreCase);
    private FreeGridReorderEngine? _freeGridEngine;
    private readonly List<Border> _freeGridGuideCells = [];
    private readonly Dictionary<Border, int> _dragHostIndexLookup = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> _dragStackIndexSet = [];
    private Vector3 _dropGhostPosition;
    private Vector3 _dropGhostVelocity;
    private Vector3 _dropGhostTarget;
    private Vector3 _dropGhostScale = Vector3.One;
    private Vector3 _dropGhostScaleVelocity;
    private bool _dropGhostPositioned;
    private float _dragTiltAngleDeg;
    private float _dragTiltVelocity;
    private double _dragWiggleClockSeconds;
    private int? _pendingExternalDropSlot;
    private HashSet<string>? _pendingExternalDropExistingNames;
    private bool _pendingSlotMapSave;
    private bool _dropPositionedInPlace;
    private int[]? _pendingInternalOleFreeGridSlots;
    private int[] _dragStackIndices = [];
    private Border? _swapHighlightHost;

    private bool IsFreeGridLayoutActive =>
        !IsDock &&
        !IsCompactList &&
        _host.State.GlobalSettings.FreeGridPlacementEnabled &&
        !HasActiveFilter;

    private bool UsesIosFluidReorder => !IsDock && !IsCompactList;

    internal bool ConsumeDropPositionedInPlace()
    {
        bool positioned = _dropPositionedInPlace;
        _dropPositionedInPlace = false;
        return positioned;
    }

    internal bool ConsumePendingSlotMapSave()
    {
        bool dirty = _pendingSlotMapSave;
        _pendingSlotMapSave = false;
        return dirty;
    }

    internal void ClearPendingExternalDropSlot()
    {
        _pendingExternalDropSlot = null;
        _pendingExternalDropExistingNames = null;
    }

    internal IReadOnlyList<WidgetItem> GetItemsInVisualReadingOrder()
    {
        int count = _items.Count;
        if (count == 0) return [];
        if (IsDock || IsCompactList || !_host.State.GlobalSettings.FreeGridPlacementEnabled)
            return _items;

        int[] slots = IsFreeGridLayoutActive && _resolvedItemSlots.Length == count
            ? _resolvedItemSlots
            : _definition.ItemSlots is { Count: > 0 }
                ? FreeGridSlotMap.ResolveSlots(_items.Select(i => i.RelativeName).ToArray(), _definition.ItemSlots)
                : [];
        if (slots.Length != count) return _items;

        bool alreadyOrdered = true;
        for (int i = 1; i < count; i++)
        {
            if (slots[i] < slots[i - 1])
            {
                alreadyOrdered = false;
                break;
            }
        }
        if (alreadyOrdered) return _items;

        var indices = new int[count];
        for (int i = 0; i < count; i++) indices[i] = i;
        Array.Sort(indices, (a, b) =>
        {
            int cmp = slots[a].CompareTo(slots[b]);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });
        var ordered = new WidgetItem[count];
        for (int i = 0; i < count; i++) ordered[i] = _items[indices[i]];
        return ordered;
    }

    internal int IndexOfItemInVisualOrder(string relativeName)
    {
        IReadOnlyList<WidgetItem> visualItems = GetItemsInVisualReadingOrder();
        for (int i = 0; i < visualItems.Count; i++)
        {
            if (visualItems[i].RelativeName.Equals(relativeName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    internal void RefreshFreeGridLayoutMode()
    {
        if (_closing) return;
        SyncResolvedItemSlots();
        ConfigureItemsLayout(updateItems: true);
    }

    private void RebuildResolvedSlotLookup()
    {
        _resolvedSlotByRelativeName.Clear();
        int count = Math.Min(_items.Count, _resolvedItemSlots.Length);
        for (int i = 0; i < count; i++)
        {
            string rel = _items[i].RelativeName;
            if (!string.IsNullOrEmpty(rel))
                _resolvedSlotByRelativeName[rel] = _resolvedItemSlots[i];
        }
    }

    private void SyncResolvedItemSlots()
    {
        if (_items.Count == 0)
        {
            _resolvedItemSlots = [];
            _resolvedSlotByRelativeName.Clear();
            return;
        }

        string[] names = _items.Select(i => i.RelativeName).ToArray();

        // If external files were just dropped onto a specific free-grid cell, bind ONLY newly added items there
        // while preserving all pre-existing items' resolved slots so nothing ever teleports.
        if (IsFreeGridLayoutActive && _pendingExternalDropSlot is int preferredSlot)
        {
            _pendingExternalDropSlot = null;
            HashSet<string>? existingBeforeDrop = _pendingExternalDropExistingNames;
            _pendingExternalDropExistingNames = null;
            _definition.ItemSlots ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var existingOccupied = new HashSet<int>();
            foreach (string name in names)
            {
                bool isExisting = existingBeforeDrop is null || existingBeforeDrop.Contains(name);
                if (!isExisting) continue;
                if (_definition.ItemSlots.TryGetValue(name, out int savedSlot) && savedSlot >= 0)
                {
                    existingOccupied.Add(savedSlot);
                }
                else if (_resolvedSlotByRelativeName.TryGetValue(name, out int resolvedSlot) && resolvedSlot >= 0)
                {
                    _definition.ItemSlots[name] = resolvedSlot;
                    existingOccupied.Add(resolvedSlot);
                }
            }

            // Seed any remaining pre-existing unslotted items into their natural first-free cells BEFORE placing drops.
            if (existingBeforeDrop is not null)
            {
                int nextNatural = 0;
                foreach (string name in names)
                {
                    if (!existingBeforeDrop.Contains(name) || _definition.ItemSlots.ContainsKey(name)) continue;
                    while (existingOccupied.Contains(nextNatural)) nextNatural++;
                    _definition.ItemSlots[name] = nextNatural;
                    existingOccupied.Add(nextNatural);
                    nextNatural++;
                }
            }

            int cols = Math.Max(1, GetItemLayoutColumnCount());
            int minRows = Math.Max(1, _definition.Layout.Rows);
            int maxSlotBound = Math.Max(cols * minRows, (existingOccupied.Count > 0 ? existingOccupied.Max() : 0) + cols * 2);

            bool assignedAny = false;
            int cursorSlot = preferredSlot;
            foreach (string name in names)
            {
                bool isNewItem = existingBeforeDrop is not null
                    ? !existingBeforeDrop.Contains(name)
                    : !_definition.ItemSlots.ContainsKey(name);
                if (!isNewItem) continue;
                maxSlotBound = Math.Max(maxSlotBound, cursorSlot + cols + 1);
                int chosen = FreeGridSlotMap.FindNearestFreeSlot(cursorSlot, existingOccupied, maxSlotBound);
                _definition.ItemSlots[name] = chosen;
                existingOccupied.Add(chosen);
                cursorSlot = chosen + 1;
                assignedAny = true;
            }
            if (assignedAny)
            {
                _dropPositionedInPlace = true;
                _pendingSlotMapSave = true;
                _resolvedItemSlots = FreeGridSlotMap.ResolveSlots(names, _definition.ItemSlots);
                RebuildResolvedSlotLookup();
                _definition.ItemOrder = FreeGridSlotMap.BuildReadingOrder(names, _resolvedItemSlots);
                _freeSlotGridLayout.InvalidateSlots();
                return;
            }
        }

        _resolvedItemSlots = IsFreeGridLayoutActive
            ? FreeGridSlotMap.ResolveSlots(names, _definition.ItemSlots)
            : Enumerable.Range(0, names.Length).ToArray();
        RebuildResolvedSlotLookup();
        _freeSlotGridLayout.InvalidateSlots();
    }

    private int ResolveFreeGridSlotForLayout(int itemIndex)
    {
        if (itemIndex >= 0 && itemIndex < _resolvedItemSlots.Length)
            return _resolvedItemSlots[itemIndex];
        return itemIndex;
    }

    private int ResolveFreeGridElementSlotForLayout(UIElement element, int fallbackIndex)
    {
        if (element is FrameworkElement fe)
        {
            if (fe.Tag is string tag && _resolvedSlotByRelativeName.TryGetValue(tag, out int slotByTag))
                return slotByTag;
            if (fe.DataContext is WidgetItem item && _resolvedSlotByRelativeName.TryGetValue(item.RelativeName, out int slotByItem))
                return slotByItem;
        }
        int repeaterIndex = ItemsRepeater.GetElementIndex(element);
        if (repeaterIndex >= 0 && repeaterIndex < _resolvedItemSlots.Length)
            return _resolvedItemSlots[repeaterIndex];
        return ResolveFreeGridSlotForLayout(fallbackIndex);
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
        _freeSlotGridLayout.ElementSlotResolver = ResolveFreeGridElementSlotForLayout;
        if (!ReferenceEquals(ItemsRepeater.Layout, _freeSlotGridLayout))
            ItemsRepeater.Layout = _freeSlotGridLayout;
        if (_freeGridEngine is not null)
        {
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
        }
        else if (double.IsFinite(FreeGridOverlayCanvas.Width) || double.IsFinite(FreeGridOverlayCanvas.Height))
        {
            FreeGridOverlayCanvas.Margin = ItemsRepeater.Margin;
            FreeGridOverlayCanvas.Width = double.NaN;
            FreeGridOverlayCanvas.Height = double.NaN;
        }
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
            _dragStackIndices = [];
            return;
        }

        SyncResolvedItemSlots();
        int columns = Math.Max(1, GetItemLayoutColumnCount());
        int minRows = Math.Max(1, _definition.Layout.Rows);
        bool freeGrid = IsFreeGridLayoutActive;
        int totalRows = FreeGridSlotMap.TotalRowCount(_resolvedItemSlots, columns, minRows, includeDragBufferRow: freeGrid);
        int maxSlots = freeGrid ? columns * totalRows : _items.Count;

        _dragStackIndices = ResolveActiveDragStackIndices(session.SourceIndex);
        _dragStackIndexSet.Clear();
        if (_dragStackIndices.Length > 1)
        {
            foreach (int stackIndex in _dragStackIndices)
                _dragStackIndexSet.Add(stackIndex);
        }

        _freeGridEngine = new FreeGridReorderEngine(_resolvedItemSlots, session.SourceIndex, freeGrid, maxSlots, _dragStackIndices);
        _dragTiltAngleDeg = 0;
        _dragTiltVelocity = 0;
        _dragWiggleClockSeconds = 0;
        _dropGhostPositioned = false;
        _dropGhostScale = Vector3.One;
        _dropGhostScaleVelocity = Vector3.Zero;
        ClearSwapHighlight();

        (double cellWidth, double cellHeight) = ResolveDragCellSize();
        SyncFreeGridOverlayBounds(columns, totalRows, cellWidth, cellHeight, GetItemLayoutGapDip());

        // Pre-build O(1) host->index lookup and centered UIElement pivots ONCE per drag (0 allocations per frame!).
        // Uses UIElement.Rotation instead of ElementCompositionPreview.GetElementVisual so UIElement.Scale/Translation never hit 0x80070005.
        _dragHostIndexLookup.Clear();
        Vector3 pivot = new((float)(cellWidth / 2.0), (float)(cellHeight / 2.0), 0f);
        for (int index = 0; index < _items.Count; index++)
        {
            if (TryGetRealizedItemHost(index, out Border siblingHost))
            {
                _dragHostIndexLookup[siblingHost] = index;
                siblingHost.CenterPoint = pivot;
                siblingHost.Rotation = 0f;
            }
        }

        if (freeGrid)
        {
            _freeSlotGridLayout.IncludeDragBufferRow = true;
            BuildFreeGridGuideCells(columns, totalRows);
            ShowFreeGridDropGhost(_freeGridEngine.SourceSlot, ReorderTargetKind.Original, immediate: true);
        }

        if (_itemDragHost is { } draggedHost)
        {
            // Lightweight liquid glass elevation border instead of heavy per-frame ThemeShadow rasterization.
            draggedHost.Background = DraggedElevatedBgBrush;
            draggedHost.BorderBrush = DraggedElevatedBorderBrush;
            draggedHost.BorderThickness = new Thickness(1);
            ItemMotionState motion = GetItemMotion(draggedHost);
            motion.ScaleTarget = _itemReorderUsesAnimations ? new Vector3(1.12f, 1.12f, 1f) : Vector3.One;
            motion.RotationAngle = 0;
        }

        if (_dragStackIndices.Length > 1)
        {
            FreeGridDragStackBadgeText.Text = _dragStackIndices.Length.ToString();
            Point primaryOrigin = FreeGridSlotMap.SlotOrigin(
                _freeGridEngine.SourceSlot,
                columns,
                cellWidth,
                cellHeight,
                GetItemLayoutGapDip());
            float initialDx = (float)(session.LatestPointerContent.X - session.PressPointerContent.X) + _hoverDragOriginOffset.X;
            float initialDy = (float)(session.LatestPointerContent.Y - session.PressPointerContent.Y) + _hoverDragOriginOffset.Y;
            FreeGridDragStackBadge.Translation = new Vector3(
                (float)(primaryOrigin.X + initialDx + cellWidth - 18),
                (float)(primaryOrigin.Y + initialDy - 6),
                0f);
            FreeGridDragStackBadge.Visibility = Visibility.Visible;
            for (int k = 1; k < _dragStackIndices.Length; k++)
            {
                if (TryGetRealizedItemHost(_dragStackIndices[k], out Border follower))
                {
                    Canvas.SetZIndex(follower, 999 - Math.Min(k, 10));
                }
            }
        }
    }

    private int[] ResolveActiveDragStackIndices(int sourceIndex)
    {
        if (sourceIndex < 0 || sourceIndex >= _items.Count) return [sourceIndex];
        string sourceName = _items[sourceIndex].RelativeName;
        if (_itemSelection.Count <= 1 || !_itemSelection.Contains(sourceName))
            return [sourceIndex];

        var list = new List<int>(_itemSelection.Count) { sourceIndex };
        for (int i = 0; i < _items.Count; i++)
        {
            if (i != sourceIndex && _itemSelection.Contains(_items[i].RelativeName))
                list.Add(i);
        }
        return list.ToArray();
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
        bool preferDirectSwap = !_freeGridEngine.IsMultiStackDrag &&
            (_host.State.GlobalSettings.FreeGridPreferDirectSwap ^ shiftHeld);

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

        // Advance living iOS edit-mode wiggle clock.
        _dragWiggleClockSeconds += seconds;

        // Animate lifted primary dragged host + velocity-coupled inertia tilt.
        ItemMotionState draggedMotion = GetItemMotion(_itemDragHost);
        Vector3 prevPos = draggedMotion.Translation;
        Vector3 nextTranslation = new(
            (float)(pointerContent.X - session.PressPointerContent.X) + _hoverDragOriginOffset.X,
            (float)(pointerContent.Y - session.PressPointerContent.Y) + _hoverDragOriginOffset.Y,
            26f);
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
                responseSeconds: 0.22,
                dampingFraction: 0.78);
            _itemDragHost.Scale = draggedMotion.Scale;

            float targetTilt = Math.Clamp(draggedMotion.TranslationVelocity.X / 320f, -5.0f, 5.0f);
            _dragTiltAngleDeg = ItemMotionMath.StepScalarSpring(
                ref _dragTiltAngleDeg,
                ref _dragTiltVelocity,
                targetTilt,
                seconds,
                responseSeconds: 0.20,
                dampingFraction: 0.76);
            _itemDragHost.Rotation = _dragTiltAngleDeg;
        }

        // Flock multi-selected icons behind the primary dragged icon in a fanned iOS 18 stack!
        if (_dragStackIndices.Length > 1)
        {
            Point primaryOrigin = FreeGridSlotMap.SlotOrigin(_freeGridEngine.SourceSlot, columns, cellWidth, cellHeight, gap);
            Point livePrimaryTopLeft = new(primaryOrigin.X + nextTranslation.X, primaryOrigin.Y + nextTranslation.Y);
            UpdateDragStackFollowers(livePrimaryTopLeft, columns, cellWidth, cellHeight, gap);
            if (_floatingDragGhostDetached)
            {
                if (FreeGridDragStackBadge.Visibility != Visibility.Collapsed)
                    FreeGridDragStackBadge.Visibility = Visibility.Collapsed;
            }
            else
            {
                FreeGridDragStackBadge.Translation = new Vector3(
                    (float)(livePrimaryTopLeft.X + cellWidth - 18),
                    (float)(livePrimaryTopLeft.Y - 6),
                    0f);
            }
        }

        if (_floatingDragGhostDetached && _itemDragHost.Opacity != 0)
            _itemDragHost.Opacity = 0;

        // Continuously integrate all sibling icons with velocity-preserving iOS underdamped springs!
        StepIosFluidItemSprings(seconds, directDragHost: _itemDragHost);
        StepFreeGridDropGhostSpring(seconds);
    }

    private void UpdateDragStackFollowers(
        Point livePrimaryTopLeft,
        int columns,
        double cellWidth,
        double cellHeight,
        double gap)
    {
        for (int k = 1; k < _dragStackIndices.Length; k++)
        {
            int itemIdx = _dragStackIndices[k];
            if (!TryGetRealizedItemHost(itemIdx, out Border follower)) continue;
            Point orig = FreeGridSlotMap.SlotOrigin(_resolvedItemSlots[itemIdx], columns, cellWidth, cellHeight, gap);
            int fanLayer = Math.Min(k, 3);
            float fanDx = fanLayer * 5f;
            float fanDy = fanLayer * 4f;
            float fanZ = Math.Max(8f, 22f - fanLayer * 3f);

            ItemMotionState motion = GetItemMotion(follower);
            motion.TranslationTarget = new Vector3(
                (float)(livePrimaryTopLeft.X - orig.X) + fanDx,
                (float)(livePrimaryTopLeft.Y - orig.Y) + fanDy,
                fanZ);
            motion.ScaleTarget = new Vector3(1.06f - fanLayer * 0.02f, 1.06f - fanLayer * 0.02f, 1f);
            follower.Opacity = _floatingDragGhostDetached ? 0.0 : (k <= 3 ? 0.94 - fanLayer * 0.08 : 0.0);
            float fanAngle = fanLayer % 2 == 1 ? -3.5f : 3.5f;
            motion.RotationAngle = fanAngle;
            follower.Rotation = fanAngle;
        }
    }

    private void ApplyIosFluidTargets(int columns, double cellWidth, double cellHeight, double gap)
    {
        if (_freeGridEngine is null) return;
        ClearSwapHighlight();
        bool hasStack = _dragStackIndexSet.Count > 1;

        for (int index = 0; index < _items.Count; index++)
        {
            if (index == _freeGridEngine.SourceItemIndex ||
                (hasStack && _dragStackIndexSet.Contains(index)) ||
                !TryGetRealizedItemHost(index, out Border host))
            {
                continue;
            }

            Vector2 delta = _freeGridEngine.GetSlotTranslation(index, columns, cellWidth, cellHeight, gap);
            ItemMotionState motion = GetItemMotion(host);
            motion.TranslationTarget = new Vector3(delta, 0);

            bool isSwapPeer = _freeGridEngine.CurrentKind == ReorderTargetKind.DirectSwap && delta.LengthSquared() > 1f;
            if (isSwapPeer)
            {
                motion.ScaleTarget = new Vector3(0.93f, 0.93f, 1f);
                _swapHighlightHost = host;
                host.BorderBrush = SwapPeerBorderBrush;
            }
            else
            {
                motion.ScaleTarget = delta.LengthSquared() > 1f ? new Vector3(0.96f, 0.96f, 1f) : Vector3.One;
            }

            if (!_itemReorderUsesAnimations)
            {
                motion.Translation = motion.TranslationTarget;
                motion.TranslationVelocity = Vector3.Zero;
                motion.Scale = Vector3.One;
                motion.ScaleVelocity = Vector3.Zero;
                motion.RotationAngle = 0;
                host.Translation = motion.Translation;
                host.Scale = Vector3.One;
                host.Rotation = 0f;
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
                motion.RotationAngle = 0;
                host.Translation = motion.Translation;
                host.Scale = motion.Scale;
                host.Rotation = 0f;
            }
            return true;
        }

        int columns = Math.Max(1, GetItemLayoutColumnCount());
        bool isActivelyDragging = directDragHost is not null && !_itemDragLanding;
        bool hasStack = _dragStackIndexSet.Count > 1;
        bool settled = true;
        int hostOrdinal = 0;

        foreach ((Border host, ItemMotionState motion) in _itemMotionStates)
        {
            hostOrdinal++;
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
            if (ReferenceEquals(host, directDragHost)) continue;

            // Distance-staggered iOS ripple spring response for organic cascading motion (O(1) zero-alloc lookup!).
            double response = _itemDragLanding ? 0.19 : 0.24;
            double damping = _itemDragLanding ? 0.88 : 0.83;
            bool isStackFollower = false;

            if (_freeGridEngine is not null && _dragHostIndexLookup.TryGetValue(host, out int itemIndex))
            {
                if (hasStack && _dragStackIndexSet.Contains(itemIndex) && itemIndex != _freeGridEngine.SourceItemIndex)
                {
                    isStackFollower = true;
                    response = _itemDragLanding ? 0.21 : 0.16;
                    damping = 0.82;
                }
                else if (!_itemDragLanding)
                {
                    int hops = Math.Min(4, _freeGridEngine.GetSlotHopDistanceFromTarget(itemIndex, columns));
                    response = 0.23 + hops * 0.018;
                }
            }

            settled &= ItemMotionMath.StepIosSpring(
                ref motion.Translation,
                ref motion.TranslationVelocity,
                motion.TranslationTarget,
                seconds,
                responseSeconds: response,
                dampingFraction: damping);
            host.Translation = motion.Translation;

            Vector3 targetScale = isStackFollower && isActivelyDragging
                ? motion.ScaleTarget
                : Vector3.DistanceSquared(motion.Translation, motion.TranslationTarget) < 64f
                    ? Vector3.One
                    : motion.ScaleTarget;
            settled &= ItemMotionMath.StepIosSpring(
                ref motion.Scale,
                ref motion.ScaleVelocity,
                targetScale,
                seconds,
                responseSeconds: 0.24,
                dampingFraction: 0.85);
            host.Scale = motion.Scale;

            // Subtle living iOS edit-mode micro-jiggle driven directly on UIElement.Rotation (GPU-composited, zero GetElementVisual conflict!).
            if (!isStackFollower)
            {
                if (isActivelyDragging && Vector3.DistanceSquared(motion.Translation, motion.TranslationTarget) < 4f)
                {
                    double phase = hostOrdinal * 1.61803398875;
                    float targetWiggle = (float)(Math.Sin(_dragWiggleClockSeconds * 11.5 + phase) * 1.05);
                    motion.RotationAngle = targetWiggle;
                    host.Rotation = targetWiggle;
                }
                else if (Math.Abs(motion.RotationAngle) > 0.02f)
                {
                    motion.RotationAngle *= 0.72f;
                    if (Math.Abs(motion.RotationAngle) <= 0.05f) motion.RotationAngle = 0f;
                    else settled = false;
                    host.Rotation = motion.RotationAngle;
                }
            }
            else if (!isActivelyDragging && Math.Abs(motion.RotationAngle) > 0.02f)
            {
                motion.RotationAngle *= 0.70f;
                if (Math.Abs(motion.RotationAngle) <= 0.05f) motion.RotationAngle = 0f;
                else settled = false;
                host.Rotation = motion.RotationAngle;
            }
        }

        if (directDragHost is null && Math.Abs(_dragTiltAngleDeg) > 0.02f)
        {
            _dragTiltAngleDeg = ItemMotionMath.StepScalarSpring(
                ref _dragTiltAngleDeg,
                ref _dragTiltVelocity,
                0f,
                seconds,
                responseSeconds: 0.18,
                dampingFraction: 0.85);
            if (_itemDragHost is { } landingHost)
            {
                landingHost.Rotation = _dragTiltAngleDeg;
            }
            if (Math.Abs(_dragTiltAngleDeg) > 0.05f) settled = false;
        }

        return settled;
    }

    private async Task CommitIosFluidReorderAsync(ItemReorderSession session)
    {
        FreeGridReorderEngine? engine = _freeGridEngine;
        if (engine is null || HasActiveFilter)
        {
            if (ReferenceEquals(_itemReorderSession, session)) CancelItemReorder();
            return;
        }

        try
        {
            StopItemDragInput(keepRendering: true);
            FreeGridDragStackBadge.Visibility = Visibility.Collapsed;
            ClearSwapHighlight();

            int columns = Math.Max(1, GetItemLayoutColumnCount());
            int[] finalSlots = engine.SnapshotVisualSlots();
            _itemDragLanding = true;

            (double landCellW, double landCellH) = ResolveDragCellSize();
            double gap = GetItemLayoutGapDip();

            if (_itemDragHost is { } draggedHost)
            {
                Vector2 landingDelta = engine.GetSlotTranslation(
                    engine.SourceItemIndex,
                    columns,
                    landCellW,
                    landCellH,
                    gap);

                if (_floatingDragGhostDetached)
                {
                    NativeMethods.POINT? landingCenter = TryGetFreeGridSlotScreenCenter(
                        finalSlots[engine.SourceItemIndex],
                        columns,
                        landCellW,
                        landCellH,
                        gap,
                        out NativeMethods.POINT slotCenterPx)
                        ? slotCenterPx
                        : null;
                    CompleteFloatingDragGhostLanding(landingCenter);
                }

                if (_itemReorderUsesAnimations)
                {
                    ItemMotionState motion = GetItemMotion(draggedHost);
                    motion.TranslationTarget = new Vector3(landingDelta, 0);
                    motion.ScaleTarget = Vector3.One;

                    // Unpack multi-selected stack followers directly to their final target slots!
                    for (int k = 1; k < _dragStackIndices.Length; k++)
                    {
                        int followerIdx = _dragStackIndices[k];
                        if (TryGetRealizedItemHost(followerIdx, out Border followerHost))
                        {
                            followerHost.Opacity = 1.0;
                            Vector2 followerDelta = engine.GetSlotTranslation(followerIdx, columns, landCellW, landCellH, gap);
                            ItemMotionState fMotion = GetItemMotion(followerHost);
                            fMotion.TranslationTarget = new Vector3(followerDelta, 0);
                            fMotion.ScaleTarget = Vector3.One;
                        }
                    }

                    _itemMotionSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await Task.WhenAny(_itemMotionSettled.Task, Task.Delay(290));
                }
            }

            if (!ReferenceEquals(_itemReorderSession, session) || HasActiveFilter) return;
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
                    RebuildResolvedSlotLookup();
                    _freeSlotGridLayout.InvalidateSlots();
                    ItemsRepeater.InvalidateMeasure();
                    ItemsRepeater.InvalidateArrange();
                    ItemsRepeater.UpdateLayout();
                    ReconcileSmoothScrollBounds();
                }
                else
                {
                    var lookup = new Dictionary<string, WidgetItem>(_items.Count, StringComparer.OrdinalIgnoreCase);
                    var duplicates = new List<WidgetItem>();
                    foreach (WidgetItem entry in _items)
                    {
                        if (!lookup.TryAdd(entry.RelativeName, entry))
                            duplicates.Add(entry);
                    }
                    var desiredList = new List<WidgetItem>(_items.Count);
                    var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string name in nextReadingOrder)
                    {
                        if (lookup.TryGetValue(name, out WidgetItem? matched) && emitted.Add(name))
                            desiredList.Add(matched);
                    }
                    foreach (WidgetItem entry in _items)
                    {
                        if (!emitted.Contains(entry.RelativeName))
                            desiredList.Add(entry);
                    }
                    desiredList.AddRange(duplicates);
                    ApplyCatalogItemsAndRender(desiredList, refreshIcons: false);
                    SyncResolvedItemSlots();
                    ItemsRepeater.UpdateLayout();
                    ReconcileSmoothScrollBounds();
                }

                InvalidateActiveSortCriterion();
                if (visualPositions is not null) ApplyFlipFromCapturedPositions(visualPositions);
                RenderCompactPreview();
                _host.NotifyOrganizerPreviewChanged(_definition.Id);
            }

            session.MarkOutcome(ItemDragState.Committed);
            FinishItemReorderSession();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_itemReorderSession, session))
                session.MarkOutcome(ItemDragState.Cancelled);
            AppLogger.Error("保存自由网格项目换序失败。", ex);
            if (!_closing) ShowMessage(AppStrings.Get("SaveConfigurationError"), InfoBarSeverity.Warning);
        }
        finally
        {
            _itemDragLanding = false;
            StopItemDragInput(keepRendering: false);
            CleanupIosFreeGridOverlays();
            _freeGridEngine = null;
            if (ReferenceEquals(_itemReorderSession, session))
            {
                ResetItemDragVisuals();
                FinishItemReorderSession();
            }
        }
    }

    private void UpdateInternalOleFreeGridTarget(DragEventArgs e, ItemReorderSession session)
    {
        if (!IsFreeGridLayoutActive || session.SourceIndex < 0 || session.SourceIndex >= _items.Count) return;

        int columns = Math.Max(1, GetItemLayoutColumnCount());
        int minRows = Math.Max(1, _definition.Layout.Rows);
        double cellWidth = Math.Max(1, _freeSlotGridLayout.CellWidth);
        double cellHeight = Math.Max(1, _freeSlotGridLayout.CellHeight);
        double gap = _freeSlotGridLayout.Gap;

        if (_freeGridEngine is null)
        {
            SyncResolvedItemSlots();
            int initRows = FreeGridSlotMap.TotalRowCount(_resolvedItemSlots, columns, minRows, includeDragBufferRow: true);
            int maxSlots = columns * initRows;
            _freeGridEngine = new FreeGridReorderEngine(_resolvedItemSlots, session.SourceIndex, freeGridEnabled: true, maxSlots);
            _dragHostIndexLookup.Clear();
            Vector3 pivot = new((float)(cellWidth / 2.0), (float)(cellHeight / 2.0), 0f);
            for (int index = 0; index < _items.Count; index++)
            {
                if (TryGetRealizedItemHost(index, out Border siblingHost))
                {
                    _dragHostIndexLookup[siblingHost] = index;
                    siblingHost.CenterPoint = pivot;
                    siblingHost.Rotation = 0f;
                }
            }
            _freeSlotGridLayout.IncludeDragBufferRow = true;
            SyncFreeGridOverlayBounds(columns, initRows, cellWidth, cellHeight, gap);
            BuildFreeGridGuideCells(columns, initRows);
            _dropGhostPositioned = false;
        }

        int totalRows = FreeGridSlotMap.TotalRowCount(_resolvedItemSlots, columns, minRows, includeDragBufferRow: true);
        int slotLimit = Math.Max(_items.Count, columns * totalRows);
        Point pointerContent = e.GetPosition(ItemsRepeater);
        int targetSlot = FreeGridSlotMap.PointToSlot(pointerContent, cellWidth, cellHeight, gap, columns, totalRows);
        bool shiftHeld = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
        bool preferDirectSwap = _host.State.GlobalSettings.FreeGridPreferDirectSwap ^ shiftHeld;

        session.MarkInternalPreview();
        bool changed = _freeGridEngine.CommitTargetSlot(targetSlot, preferDirectSwap, slotLimit);
        if (changed || FreeGridDropSlotGhost.Visibility != Visibility.Visible)
        {
            ApplyIosFluidTargets(columns, cellWidth, cellHeight, gap);
            ShowFreeGridDropGhost(_freeGridEngine.TargetSlot, _freeGridEngine.CurrentKind, immediate: !UseCustomAnimations);
        }
        StartNativeItemMotionRendering();
    }

    private void LeaveInternalOleFreeGridPreview()
    {
        if (_freeGridEngine is null) return;
        _freeGridEngine.ResetToOriginal();
        int columns = Math.Max(1, GetItemLayoutColumnCount());
        ApplyIosFluidTargets(columns, Math.Max(1, _freeSlotGridLayout.CellWidth), Math.Max(1, _freeSlotGridLayout.CellHeight), _freeSlotGridLayout.Gap);
        FreeGridDropSlotGhost.Visibility = Visibility.Collapsed;
        StartNativeItemMotionRendering();
    }

    private void CaptureInternalOleFreeGridDropSlots(DragEventArgs e, ItemReorderSession session)
    {
        UpdateInternalOleFreeGridTarget(e, session);
        _pendingInternalOleFreeGridSlots = _freeGridEngine?.SnapshotVisualSlots();
    }

    private int[]? ConsumePendingInternalOleFreeGridSlots()
    {
        int[]? slots = _pendingInternalOleFreeGridSlots;
        _pendingInternalOleFreeGridSlots = null;
        return slots;
    }

    private async Task CommitInternalOleFreeGridReorderAsync(ItemReorderSession session, int[] finalSlots)
    {
        if (HasActiveFilter || finalSlots.Length != _items.Count)
        {
            if (ReferenceEquals(_itemReorderSession, session)) CancelItemReorder();
            return;
        }

        try
        {
            StopItemDragInput(keepRendering: false);
            session.BeginCommit();
            Dictionary<string, Point>? visualPositions = UseCustomAnimations ? CaptureRealizedItemPositions() : null;
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

                _resolvedItemSlots = finalSlots;
                RebuildResolvedSlotLookup();
                _freeSlotGridLayout.InvalidateSlots();
                ItemsRepeater.InvalidateMeasure();
                ItemsRepeater.InvalidateArrange();
                ItemsRepeater.UpdateLayout();
                ReconcileSmoothScrollBounds();

                InvalidateActiveSortCriterion();
                if (visualPositions is not null) ApplyFlipFromCapturedPositions(visualPositions);
                RenderCompactPreview();
                _host.NotifyOrganizerPreviewChanged(_definition.Id);
            }

            session.MarkOutcome(ItemDragState.Committed);
            FinishItemReorderSession();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_itemReorderSession, session))
                session.MarkOutcome(ItemDragState.Cancelled);
            AppLogger.Error("保存 OLE 回拖自由网格项目换序失败。", ex);
            if (!_closing) ShowMessage(AppStrings.Get("SaveConfigurationError"), InfoBarSeverity.Warning);
        }
        finally
        {
            _itemDragLanding = false;
            StopItemDragInput(keepRendering: false);
            CleanupIosFreeGridOverlays();
            _freeGridEngine = null;
            _pendingInternalOleFreeGridSlots = null;
            if (ReferenceEquals(_itemReorderSession, session))
            {
                ResetItemDragVisuals();
                FinishItemReorderSession();
            }
        }
    }

    private void ClearSwapHighlight()
    {
        if (_swapHighlightHost is not null)
        {
            string? tag = _swapHighlightHost.Tag as string;
            _swapHighlightHost.BorderBrush = ItemTransparentBorderBrush;
            if (!string.IsNullOrEmpty(tag))
                ApplyItemSelectionVisual(_swapHighlightHost, tag);
            _swapHighlightHost = null;
        }
    }

    private void CleanupIosFreeGridOverlays()
    {
        bool hadBufferRow = _freeSlotGridLayout.IncludeDragBufferRow;
        _freeSlotGridLayout.IncludeDragBufferRow = false;
        FreeGridDropSlotGhost.Visibility = Visibility.Collapsed;
        FreeGridDragStackBadge.Visibility = Visibility.Collapsed;
        ClearSwapHighlight();
        _dropGhostPositioned = false;
        _dropGhostScale = Vector3.One;
        _dropGhostScaleVelocity = Vector3.Zero;
        _dragTiltAngleDeg = 0;
        _dragTiltVelocity = 0;
        _dragWiggleClockSeconds = 0;
        if (_itemDragHost is { } host)
        {
            host.Rotation = 0f;
            host.BorderBrush = ItemTransparentBorderBrush;
            if (host.Tag is string dragTag)
                ApplyItemSelectionVisual(host, dragTag);
        }
        foreach (int idx in _dragStackIndices)
        {
            if (TryGetRealizedItemHost(idx, out Border member))
            {
                member.Opacity = 1.0;
                member.Rotation = 0f;
                Canvas.SetZIndex(member, 0);
            }
        }
        _dragStackIndices = [];
        _dragStackIndexSet.Clear();
        _dragHostIndexLookup.Clear();
        foreach ((Border motionHost, ItemMotionState motion) in _itemMotionStates)
        {
            motion.RotationAngle = 0f;
            motionHost.Rotation = 0f;
        }
        // Pool guide cells (collapse instead of destroying/rebuilding XAML tree on every drag!).
        foreach (Border cell in _freeGridGuideCells)
            cell.Visibility = Visibility.Collapsed;

        // Release any explicit drag-buffer height on FreeGridOverlayCanvas so no phantom blank row or scrollbar lingers.
        if (double.IsFinite(FreeGridOverlayCanvas.Height) || double.IsFinite(FreeGridOverlayCanvas.Width) || hadBufferRow)
        {
            FreeGridOverlayCanvas.Width = double.NaN;
            FreeGridOverlayCanvas.Height = double.NaN;
            _freeSlotGridLayout.InvalidateSlots();
            ItemsRepeater.InvalidateMeasure();
            ItemsRepeater.InvalidateArrange();
            ItemsScrollHost.InvalidateMeasure();
            ItemsScrollView.InvalidateMeasure();
            ReconcileSmoothScrollBounds();
        }
    }

    private void BuildFreeGridGuideCells(int columns, int totalRows)
    {
        double cellWidth = _freeSlotGridLayout.CellWidth;
        double cellHeight = _freeSlotGridLayout.CellHeight;
        double gap = _freeSlotGridLayout.Gap;

        int totalCells = Math.Min(columns * totalRows, 96);
        while (_freeGridGuideCells.Count < totalCells)
        {
            var cell = new Border
            {
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                BorderBrush = GuideCellStrokeBrush,
                Background = GuideCellFillBrush,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            Canvas.SetZIndex(cell, 0);
            FreeGridOverlayCanvas.Children.Insert(0, cell);
            _freeGridGuideCells.Add(cell);
        }

        for (int slot = 0; slot < _freeGridGuideCells.Count; slot++)
        {
            Border cell = _freeGridGuideCells[slot];
            if (slot < totalCells)
            {
                Point origin = FreeGridSlotMap.SlotOrigin(slot, columns, cellWidth, cellHeight, gap);
                cell.Width = cellWidth;
                cell.Height = cellHeight;
                Canvas.SetLeft(cell, origin.X);
                Canvas.SetTop(cell, origin.Y);
                cell.Visibility = Visibility.Visible;
            }
            else
            {
                cell.Visibility = Visibility.Collapsed;
            }
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
        FreeGridDropSlotGhost.CenterPoint = new Vector3((float)(cellWidth / 2.0), (float)(cellHeight / 2.0), 0);
        bool isSwap = kind == ReorderTargetKind.DirectSwap;
        FreeGridDropSlotGhost.Background = isSwap ? DropGhostSwapBgBrush : DropGhostNormalBgBrush;
        FreeGridDropSlotGhost.BorderBrush = isSwap ? DropGhostSwapBorderBrush : DropGhostNormalBorderBrush;
        FreeGridDropSlotGhost.Visibility = Visibility.Visible;

        _dropGhostTarget = new Vector3((float)origin.X, (float)origin.Y, 0);
        if (immediate || !_dropGhostPositioned)
        {
            _dropGhostPosition = _dropGhostTarget;
            _dropGhostVelocity = Vector3.Zero;
            _dropGhostScale = Vector3.One;
            _dropGhostScaleVelocity = Vector3.Zero;
            _dropGhostPositioned = true;
            FreeGridDropSlotGhost.Translation = _dropGhostPosition;
            FreeGridDropSlotGhost.Scale = Vector3.One;
        }
        else
        {
            // Tactile magnetic catch pulse whenever target slot changes!
            _dropGhostScale = new Vector3(0.93f, 0.93f, 1f);
            _dropGhostScaleVelocity = new Vector3(0.6f, 0.6f, 0f);
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
            responseSeconds: 0.20,
            dampingFraction: 0.85);
        ItemMotionMath.StepIosSpring(
            ref _dropGhostScale,
            ref _dropGhostScaleVelocity,
            Vector3.One,
            seconds,
            responseSeconds: 0.22,
            dampingFraction: 0.72);
        FreeGridDropSlotGhost.Translation = _dropGhostPosition;
        FreeGridDropSlotGhost.Scale = _dropGhostScale;
    }

    private void RecordExternalDropSlotFromEvent(DragEventArgs e)
    {
        _dropPositionedInPlace = false;
        if (!IsFreeGridLayoutActive)
        {
            ClearPendingExternalDropSlot();
            return;
        }
        IReadOnlyList<WidgetItem> existingSource = _allCatalogItems.Count > 0 ? _allCatalogItems : _items;
        _pendingExternalDropExistingNames = new HashSet<string>(
            existingSource.Select(i => i.RelativeName),
            StringComparer.OrdinalIgnoreCase);

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
        if (_closing) return;
        if (IsCompactList || !_host.State.GlobalSettings.FreeGridPlacementEnabled)
        {
            ShowExpandedToast(
                LocalizeToast("当前视图无需整理方格空位", "Grid compaction is only available in free-grid icon view", "空き詰めはフリーグリッドアイコン表示時のみ利用できます"),
                InfoBarSeverity.Informational);
            return;
        }

        IReadOnlyList<WidgetItem> source = _allCatalogItems.Count > 0 ? _allCatalogItems : _items.ToArray();
        if (source.Count == 0)
        {
            ShowExpandedToast(
                LocalizeToast("当前收纳窗暂无项目", "No items in this organizer", "このボックスには項目がありません"),
                InfoBarSeverity.Informational);
            return;
        }

        string[] names = source.Select(i => i.RelativeName).ToArray();
        int[] priorSlots = FreeGridSlotMap.ResolveSlots(names, _definition.ItemSlots);
        List<string> readingOrder = FreeGridSlotMap.BuildReadingOrder(names, priorSlots);
        Dictionary<string, int> denseSlots = FreeGridSlotMap.CompactToDense(readingOrder);
        bool slotsChanged = false;
        for (int i = 0; i < names.Length; i++)
        {
            if (!denseSlots.TryGetValue(names[i], out int denseSlot) || priorSlots[i] != denseSlot)
            {
                slotsChanged = true;
                break;
            }
        }
        if (!slotsChanged)
        {
            for (int i = 0; i < names.Length && i < readingOrder.Count; i++)
            {
                if (!string.Equals(names[i], readingOrder[i], StringComparison.OrdinalIgnoreCase))
                {
                    slotsChanged = true;
                    break;
                }
            }
        }

        if (!slotsChanged)
        {
            ShowExpandedToast(
                LocalizeToast("当前已是紧凑排布，无空位可整理", "Grid is already compact", "すでに隙間なく整列されています"),
                InfoBarSeverity.Informational);
            return;
        }

        Dictionary<string, Point>? before = UseCustomAnimations && _expanded ? CaptureRealizedItemPositions() : null;

        List<string> previousOrder = _definition.ItemOrder.ToList();
        var previousSlots = new Dictionary<string, int>(_definition.ItemSlots ?? [], StringComparer.OrdinalIgnoreCase);
        _definition.ItemOrder = readingOrder;
        _definition.ItemSlots = denseSlots;
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

        var lookup = new Dictionary<string, WidgetItem>(source.Count, StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<WidgetItem>();
        foreach (WidgetItem entry in source)
        {
            if (!lookup.TryAdd(entry.RelativeName, entry))
                duplicates.Add(entry);
        }
        var orderedList = new List<WidgetItem>(source.Count);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string n in readingOrder)
        {
            if (lookup.TryGetValue(n, out WidgetItem? matched) && emitted.Add(n))
                orderedList.Add(matched);
        }
        foreach (WidgetItem entry in source)
        {
            if (!emitted.Contains(entry.RelativeName))
                orderedList.Add(entry);
        }
        orderedList.AddRange(duplicates);
        NormalizeItemMotionsToLayout("CompactGridGaps");
        ApplyCatalogItemsAndRender(orderedList, refreshIcons: false);
        SyncResolvedItemSlots();
        _freeSlotGridLayout.InvalidateSlots();
        ItemsRepeater.InvalidateMeasure();
        ItemsRepeater.InvalidateArrange();
        ItemsRepeater.UpdateLayout();
        ReconcileSmoothScrollBounds();
        if (before is not null) ApplyFlipFromCapturedPositions(before);
        RenderCompactPreview();
        _host.NotifyOrganizerPreviewChanged(_definition.Id);
        ShowExpandedToast(
            LocalizeToast("已紧凑排布方格空位", "Compacted grid gaps", "グリッドの空きを詰めました"),
            InfoBarSeverity.Success);
    }
}
