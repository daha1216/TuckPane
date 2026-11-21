using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;
using Windows.UI;

namespace TuckPane;

public sealed partial class MainWindow
{
    private static readonly SolidColorBrush ItemSelectedBgBrush = new(Color.FromArgb(68, 0, 120, 212));
    private static readonly SolidColorBrush ItemSelectedHoverBgBrush = new(Color.FromArgb(92, 0, 120, 212));
    private static readonly SolidColorBrush ItemSelectedPressedBgBrush = new(Color.FromArgb(112, 0, 120, 212));
    private static readonly SolidColorBrush ItemSelectedBorderBrush = new(Color.FromArgb(215, 0, 120, 212));
    private readonly SolidColorBrush ItemHoverBorderBrush = new(Color.FromArgb(72, 255, 255, 255));
    private static readonly SolidColorBrush ItemTransparentBorderBrush = new(Colors.Transparent);

    private readonly ItemSelectionModel _itemSelection = new();
    private Border? _hoveredItemHost;
    private bool _marqueePressed;
    private bool _marqueeActive;
    private bool _marqueeAdditiveCtrl;
    private uint _marqueePointerId;
    private Pointer? _marqueeCapturedPointer;
    private Point _marqueeStartContent;
    private IReadOnlyCollection<string>? _marqueeBaseline;
    private List<MarqueeTargetRect<string>>? _marqueeSnapshotTargets;

    internal bool HasMultipleItemsSelected => _itemSelection.HasMultipleSelection;

    internal bool IsItemSelected(string relativeName) => _itemSelection.Contains(relativeName);

    internal IReadOnlyList<WidgetItem> GetSelectedItemsInOrder()
    {
        IReadOnlyList<WidgetItem> visualItems = GetItemsInVisualReadingOrder();
        string[] visibleNames = visualItems.Select(i => i.RelativeName).ToArray();
        IReadOnlyList<string> ordered = _itemSelection.OrderedSelection(visibleNames);
        if (ordered.Count == 0) return [];
        var byName = new Dictionary<string, WidgetItem>(visualItems.Count, StringComparer.OrdinalIgnoreCase);
        foreach (WidgetItem item in visualItems)
            byName.TryAdd(item.RelativeName, item);
        var result = new List<WidgetItem>(ordered.Count);
        foreach (string name in ordered)
        {
            if (byName.TryGetValue(name, out WidgetItem? item)) result.Add(item);
        }
        return result;
    }

    internal IReadOnlyList<WidgetItem> GetDragPayloadItems(WidgetItem primaryItem)
    {
        if (!_itemSelection.Contains(primaryItem.RelativeName) || !_itemSelection.HasMultipleSelection)
            return [primaryItem];

        // Multi-file drag applies to filesystem items (files, folders, shortcuts, portable docs).
        WidgetItem[] selected = GetSelectedItemsInOrder()
            .Where(item => item.Kind is not WidgetItemKind.Organizer and not WidgetItemKind.Note)
            .ToArray();
        return selected.Length > 1 ? selected : [primaryItem];
    }

    internal void ClearItemSelection()
    {
        if (_itemSelection.Clear())
            RefreshItemSelectionVisuals();
    }

    internal void SelectAllVisibleItems()
    {
        if (!_expanded || IsDock || _items.Count == 0) return;
        IReadOnlyList<WidgetItem> visualItems = GetItemsInVisualReadingOrder();
        _itemSelection.SelectAll(visualItems.Select(i => i.RelativeName).ToArray());
        if (_keyboardFocusedIndex < 0 && visualItems.Count > 0)
            SyncKeyboardFocusToItem(visualItems[0].RelativeName, updateVisuals: false);
        RefreshItemSelectionVisuals();
    }

    private void PruneItemSelection()
    {
        if (_itemSelection.Prune(_items.Select(i => i.RelativeName)))
            RefreshItemSelectionVisuals();
    }

    private bool TryHandleItemSelectionClick(string relativeName)
    {
        if (IsDock) return false;
        bool ctrl = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
        bool shift = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0; // VK_SHIFT
        if (!ctrl && !shift) return false;

        IReadOnlyList<WidgetItem> visualItems = GetItemsInVisualReadingOrder();
        int visualIndex = -1;
        for (int i = 0; i < visualItems.Count; i++)
        {
            if (visualItems[i].RelativeName.Equals(relativeName, StringComparison.OrdinalIgnoreCase))
            {
                visualIndex = i;
                break;
            }
        }
        if (visualIndex < 0) return false;
        string[] visualOrder = visualItems.Select(i => i.RelativeName).ToArray();
        _itemSelection.HandleClick(visualOrder, visualIndex, ctrl, shift);
        SyncKeyboardFocusToItem(relativeName, updateVisuals: false);
        RefreshItemSelectionVisuals();
        return true;
    }

    internal bool TryBeginItemMarqueePress(PointerRoutedEventArgs e)
    {
        if (!_expanded || IsDock || _animating || _shellDragActive || _itemReorderSession is not null ||
            e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
        {
            return false;
        }

        // Only start rubber-band selection when clicking empty space inside the items content
        // area (below the header title row and not on a header/filter/collapse button).
        PointerPoint contentPoint = e.GetCurrentPoint(ExpandedContentLayer);
        if (!contentPoint.Properties.IsLeftButtonPressed) return false;
        if (contentPoint.Position.Y < 32) return false;

        FocusExpandedItemsForKeyboardInput();
        _marqueePressed = true;
        _marqueeActive = false;
        _marqueePointerId = e.Pointer.PointerId;
        _marqueeStartContent = contentPoint.Position;
        _marqueeAdditiveCtrl = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
        _marqueeBaseline = _itemSelection.Snapshot();
        _marqueeSnapshotTargets = CaptureMarqueeTargets();
        _marqueeCapturedPointer = e.Pointer;

        if (!ExpandedContentLayer.CapturePointer(e.Pointer))
        {
            CancelItemMarquee();
            return false;
        }

        ExpandedContentLayer.PointerMoved += ExpandedContentLayer_MarqueePointerMoved;
        ExpandedContentLayer.PointerReleased += ExpandedContentLayer_MarqueePointerReleased;
        ExpandedContentLayer.PointerCanceled += ExpandedContentLayer_MarqueePointerCanceled;
        ExpandedContentLayer.PointerCaptureLost += ExpandedContentLayer_MarqueePointerCaptureLost;
        e.Handled = true;
        return true;
    }

    private void ExpandedContentLayer_MarqueePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueePressed || e.Pointer.PointerId != _marqueePointerId) return;
        Point current = e.GetCurrentPoint(ExpandedContentLayer).Position;
        var startPx = new NativeMethods.POINT { X = (int)Math.Round(_marqueeStartContent.X), Y = (int)Math.Round(_marqueeStartContent.Y) };
        var currPx = new NativeMethods.POINT { X = (int)Math.Round(current.X), Y = (int)Math.Round(current.Y) };

        if (!_marqueeActive)
        {
            if (!MarqueeSelectionMath.IsMarqueeActive(startPx, currPx, 1.0)) return;
            _marqueeActive = true;
            MarqueeSelectionRect.Visibility = Visibility.Visible;
        }

        double left = Math.Max(0, Math.Min(_marqueeStartContent.X, current.X));
        double top = Math.Max(0, Math.Min(_marqueeStartContent.Y, current.Y));
        double right = Math.Min(ExpandedContentLayer.ActualWidth, Math.Max(_marqueeStartContent.X, current.X));
        double bottom = Math.Min(ExpandedContentLayer.ActualHeight, Math.Max(_marqueeStartContent.Y, current.Y));

        Canvas.SetLeft(MarqueeSelectionRect, left);
        Canvas.SetTop(MarqueeSelectionRect, top);
        MarqueeSelectionRect.Width = Math.Max(1, right - left);
        MarqueeSelectionRect.Height = Math.Max(1, bottom - top);

        UpdateMarqueeHitSelection(new NativeMethods.RECT
        {
            Left = (int)Math.Round(left),
            Top = (int)Math.Round(top),
            Right = (int)Math.Round(right),
            Bottom = (int)Math.Round(bottom)
        });
        e.Handled = true;
    }

    private void ExpandedContentLayer_MarqueePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueePressed || e.Pointer.PointerId != _marqueePointerId) return;
        bool wasActive = _marqueeActive;
        bool additive = _marqueeAdditiveCtrl;
        EndItemMarquee();
        if (!wasActive && !additive)
            ClearItemSelection();
        e.Handled = true;
    }

    private void ExpandedContentLayer_MarqueePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueePressed) CancelItemMarquee();
    }

    private void ExpandedContentLayer_MarqueePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueePressed) EndItemMarquee();
    }

    private void CancelItemMarquee()
    {
        bool restore = _marqueePressed && _marqueeActive && _marqueeBaseline is not null;
        IReadOnlyCollection<string>? baseline = _marqueeBaseline;
        EndItemMarquee();
        if (restore && baseline is not null)
        {
            _itemSelection.ApplyMarquee(baseline, additiveCtrl: false);
            RefreshItemSelectionVisuals();
        }
    }

    private void EndItemMarquee()
    {
        ExpandedContentLayer.PointerMoved -= ExpandedContentLayer_MarqueePointerMoved;
        ExpandedContentLayer.PointerReleased -= ExpandedContentLayer_MarqueePointerReleased;
        ExpandedContentLayer.PointerCanceled -= ExpandedContentLayer_MarqueePointerCanceled;
        ExpandedContentLayer.PointerCaptureLost -= ExpandedContentLayer_MarqueePointerCaptureLost;
        if (_marqueeCapturedPointer is not null)
        {
            try { ExpandedContentLayer.ReleasePointerCapture(_marqueeCapturedPointer); }
            catch { }
        }
        _marqueeCapturedPointer = null;
        _marqueePressed = false;
        _marqueeActive = false;
        _marqueePointerId = 0;
        _marqueeBaseline = null;
        _marqueeSnapshotTargets = null;
        MarqueeSelectionRect.Visibility = Visibility.Collapsed;
    }

    private List<MarqueeTargetRect<string>> CaptureMarqueeTargets()
    {
        var targets = new List<MarqueeTargetRect<string>>(_items.Count);
        double vpLeft = 0, vpTop = 0, vpRight = ExpandedContentLayer.ActualWidth, vpBottom = ExpandedContentLayer.ActualHeight;
        try
        {
            Point vpOrigin = ItemsScrollView.TransformToVisual(ExpandedContentLayer).TransformPoint(new Point(0, 0));
            double vpWidth = ItemsScrollView.ViewportWidth > 0 ? ItemsScrollView.ViewportWidth : ItemsScrollView.ActualWidth;
            double vpHeight = ItemsScrollView.ViewportHeight > 0 ? ItemsScrollView.ViewportHeight : ItemsScrollView.ActualHeight;
            if (vpWidth > 0 && vpHeight > 0)
            {
                vpLeft = vpOrigin.X;
                vpTop = vpOrigin.Y;
                vpRight = vpOrigin.X + vpWidth;
                vpBottom = vpOrigin.Y + vpHeight;
            }
        }
        catch
        {
            // Fall back to ExpandedContentLayer bounds if ScrollView transform is unavailable.
        }

        for (int index = 0; index < _items.Count; index++)
        {
            if (!TryGetRealizedItemHost(index, out Border host)) continue;
            if (host.ActualWidth <= 0 || host.ActualHeight <= 0) continue;
            try
            {
                Point origin = host.TransformToVisual(ExpandedContentLayer).TransformPoint(new Point(0, 0));
                double left = Math.Max(origin.X, vpLeft);
                double top = Math.Max(origin.Y, vpTop);
                double right = Math.Min(origin.X + host.ActualWidth, vpRight);
                double bottom = Math.Min(origin.Y + host.ActualHeight, vpBottom);
                if (right <= left || bottom <= top) continue;

                var bounds = new NativeMethods.RECT
                {
                    Left = (int)Math.Round(left),
                    Top = (int)Math.Round(top),
                    Right = (int)Math.Round(right),
                    Bottom = (int)Math.Round(bottom)
                };
                targets.Add(new MarqueeTargetRect<string>(_items[index].RelativeName, bounds));
            }
            catch
            {
                // Skip unrealized/detached elements safely.
            }
        }
        return targets;
    }

    private void UpdateMarqueeHitSelection(NativeMethods.RECT marqueeRect)
    {
        List<MarqueeTargetRect<string>> targets = _marqueeSnapshotTargets ?? CaptureMarqueeTargets();
        IReadOnlyList<string> hits = MarqueeSelectionMath.FindIntersectingIds(marqueeRect, targets);
        _itemSelection.ApplyMarquee(hits, _marqueeAdditiveCtrl, _marqueeBaseline);
        if (hits.Count > 0)
            SyncKeyboardFocusToItem(hits[^1], updateVisuals: false);
        else if (_itemSelection.Count == 0)
            _keyboardFocusedIndex = -1;
        RefreshItemSelectionVisuals();
    }

    internal void RefreshItemSelectionVisuals()
    {
        for (int index = 0; index < _items.Count; index++)
        {
            if (TryGetRealizedItemHost(index, out Border host))
                ApplyItemSelectionVisual(host, _items[index].RelativeName);
        }

        if (_itemSelection.HasMultipleSelection && _expanded && !IsDock)
        {
            SelectionCountBadgeText.Text = AppStrings.Format("SelectionCountBadgeFormat", _itemSelection.Count);
            SelectionCountBadge.Visibility = Visibility.Visible;
        }
        else
        {
            SelectionCountBadge.Visibility = Visibility.Collapsed;
        }
    }

    internal void ApplyItemSelectionVisual(Border host, string relativeName)
    {
        bool selected = _itemSelection.Contains(relativeName);
        bool hovered = ReferenceEquals(host, _hoveredItemHost);
        bool pressedDrag = ReferenceEquals(host, _itemDragHost) && _itemReorderSession is { State: ItemDragState.Pressed };
        if (selected && !IsDock)
        {
            host.Background = IsCompactList
                ? _transparentItemBrush
                : pressedDrag
                    ? ItemSelectedPressedBgBrush
                    : hovered
                        ? ItemSelectedHoverBgBrush
                        : ItemSelectedBgBrush;
            host.BorderBrush = ItemSelectedBorderBrush;
        }
        else if (hovered && !IsDock && !IsCompactList && !ReferenceEquals(host, _itemDragHost))
        {
            host.Background = _hoveredItemBrush;
            host.BorderBrush = ItemHoverBorderBrush;
        }
        else
        {
            host.BorderBrush = ItemTransparentBorderBrush;
            if (!ReferenceEquals(host, _itemDragHost))
                host.Background = _transparentItemBrush;
        }
    }

    private void SelectionClearButton_Click(object sender, RoutedEventArgs e) => ClearItemSelection();

    internal void CopySelectedItemsToClipboard()
    {
        string[] paths = GetSelectedItemsInOrder()
            .Where(item => item.Kind is not WidgetItemKind.Organizer and not WidgetItemKind.Note)
            .Select(item => item.FullPath)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToArray();
        if (paths.Length == 0) return;
        ShellDragService.SetCopyClipboard(paths);
    }

    internal void CutSelectedItemsToClipboard()
    {
        string[] paths = GetSelectedItemsInOrder()
            .Where(item => item.Kind is not WidgetItemKind.Organizer and not WidgetItemKind.Note)
            .Select(item => item.FullPath)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToArray();
        if (paths.Length == 0) return;
        ShellDragService.SetCutClipboard(paths);
    }

    internal async Task DeleteSelectedItemsAsync()
    {
        WidgetItem[] targets = GetSelectedItemsInOrder()
            .Where(item => item.Kind is not WidgetItemKind.Organizer and not WidgetItemKind.Note)
            .ToArray();
        if (targets.Length == 0) return;

        ClearItemSelection();
        await _catalogController.DeleteFileItemsBatchAsync(targets);
    }

    internal bool TryShowBatchItemContextMenu(FrameworkElement anchor, WidgetItem clickedItem, Point? pointerPoint = null)
    {
        if (!_itemSelection.Contains(clickedItem.RelativeName) || !_itemSelection.HasMultipleSelection)
            return false;

        WidgetItem[] selected = GetSelectedItemsInOrder()
            .Where(item => item.Kind is not WidgetItemKind.Organizer and not WidgetItemKind.Note)
            .ToArray();
        if (selected.Length <= 1) return false;

        var copyAll = new MenuFlyoutItem { Text = AppStrings.Format("ContextBatchCopyFormat", selected.Length), Icon = new FontIcon { Glyph = "\uE8C8" } };
        var cutAll = new MenuFlyoutItem { Text = AppStrings.Format("ContextBatchCutFormat", selected.Length), Icon = new FontIcon { Glyph = "\uE8C6" } };
        var copyPaths = new MenuFlyoutItem { Text = AppStrings.Get("ContextBatchCopyPaths"), Icon = new FontIcon { Glyph = "\uE71B" } };
        var deleteAll = new MenuFlyoutItem { Text = AppStrings.Format("ContextBatchDeleteFormat", selected.Length), Icon = new FontIcon { Glyph = "\uE74D" } };

        if (WindowRoot.Resources.TryGetValue("OrganizerMenuItemStyle", out object? styleObj) && styleObj is Style itemStyle)
        {
            copyAll.Style = itemStyle;
            cutAll.Style = itemStyle;
            copyPaths.Style = itemStyle;
            deleteAll.Style = itemStyle;
        }

        copyAll.Click += (_, _) => _ = RunSafelyAsync(() =>
        {
            CopySelectedItemsToClipboard();
            ShowExpandedToast(
                LocalizeToast($"已复制 {selected.Length} 项", $"Copied {selected.Length} items", $"{selected.Length} 件をコピーしました"),
                InfoBarSeverity.Success);
            return Task.CompletedTask;
        }, "批量复制失败");
        cutAll.Click += (_, _) => _ = RunSafelyAsync(() =>
        {
            CutSelectedItemsToClipboard();
            ShowExpandedToast(
                LocalizeToast($"已剪切 {selected.Length} 项", $"Cut {selected.Length} items", $"{selected.Length} 件を切り取りました"),
                InfoBarSeverity.Success);
            return Task.CompletedTask;
        }, "批量剪切失败");
        copyPaths.Click += (_, _) => _ = RunSafelyAsync(() =>
        {
            ShellItemCommands.CopyPathsToClipboard(selected.Select(i => i.FullPath));
            ShowExpandedToast(
                LocalizeToast($"已复制 {selected.Length} 条路径", $"Copied {selected.Length} paths", $"{selected.Length} 件のパスをコピーしました"),
                InfoBarSeverity.Success);
            return Task.CompletedTask;
        }, "批量复制路径失败");
        deleteAll.Click += async (_, _) => await RunSafelyAsync(DeleteSelectedItemsAsync, "批量删除失败");

        MenuFlyout menu = CreateOrganizerContextMenu();
        menu.ShouldConstrainToRootBounds = false;
        menu.Items.Add(copyAll);
        menu.Items.Add(cutAll);
        menu.Items.Add(copyPaths);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(deleteAll);

        LocalizeContextMenu(menu);
        _shellContextMenuOpen = true;
        PauseOutsideClickForOverlay();
        menu.Opening += OrganizerMenu_PauseOutsideClick;
        menu.Closed += (_, _) =>
        {
            _shellContextMenuOpen = false;
            ResumeOutsideClickAfterOverlay();
        };
        try
        {
            if (pointerPoint is Point pt)
                menu.ShowAt(anchor, pt);
            else
                menu.ShowAt(anchor);
        }
        catch (Exception ex)
        {
            _shellContextMenuOpen = false;
            RestoreContextMenuHost();
            ResumeOutsideClickAfterOverlay();
            AppLogger.Error("无法打开批量操作菜单。", ex);
        }
        return true;
    }
}
