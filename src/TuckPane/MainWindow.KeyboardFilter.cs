using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.System;

namespace TuckPane;

public sealed partial class MainWindow
{
    private IReadOnlyList<WidgetItem> _allCatalogItems = [];
    private string _activeFilterQuery = string.Empty;
    private int _keyboardFocusedIndex = -1;
    private bool _filterBoxInputActive;

    private bool HasActiveFilter => !string.IsNullOrWhiteSpace(_activeFilterQuery);

    private void ApplyFilteredCatalogItems(IReadOnlyList<WidgetItem> ordered, bool refreshIcons)
    {
        _allCatalogItems = ordered.ToArray();
        IReadOnlyList<WidgetItem> visible = HasActiveFilter
            ? _allCatalogItems.Where(item => OrganizerSearch.MatchesItemQuery(item, _activeFilterQuery)).ToArray()
            : _allCatalogItems;
        ReconcileCatalogItems(visible);
        PruneItemSelection();
        ClampKeyboardFocusIndex();
        RenderAll();
        if (refreshIcons) RefreshRealizedIcons();
        RefreshItemSelectionVisuals();
        UpdateExpandedFilterStatus();
    }

    private void ShowExpandedFilterBar(string? initialText = null)
    {
        if (_closing || !_expanded || IsDock || _definition.PlacementMode == OrganizerPlacementMode.Station) return;
        ExpandedFilterBar.Visibility = Visibility.Visible;
        ExpandedFilterBox.PlaceholderText = AppStrings.Get("OrganizerFilterPlaceholder");
        if (!_filterBoxInputActive)
        {
            _filterBoxInputActive = true;
            _desktopLayer?.SetInputActivation(true);
        }
        if (initialText is not null)
        {
            ExpandedFilterBox.Text = initialText;
            ExpandedFilterBox.SelectionStart = initialText.Length;
        }
        ExpandedFilterBox.Focus(FocusState.Programmatic);
        UpdateExpandedFilterStatus();
    }

    private void ClearExpandedFilter(bool releaseInputActivation = true)
    {
        bool hadFilter = HasActiveFilter || ExpandedFilterBar.Visibility == Visibility.Visible;
        _activeFilterQuery = string.Empty;
        if (ExpandedFilterBox.Text.Length > 0) ExpandedFilterBox.Text = string.Empty;
        ExpandedFilterBar.Visibility = Visibility.Collapsed;
        if (releaseInputActivation && _filterBoxInputActive)
        {
            _filterBoxInputActive = false;
            if (!_organizerTitleEdit.IsEditing)
                _desktopLayer?.SetInputActivation(false);
        }
        if (hadFilter && _allCatalogItems.Count > 0)
        {
            ReconcileCatalogItems(_allCatalogItems);
            ClampKeyboardFocusIndex();
            RenderItems();
        }
        UpdateExpandedFilterStatus();
    }

    private void UpdateExpandedFilterStatus()
    {
        if (ExpandedFilterBar.Visibility != Visibility.Visible) return;
        ExpandedFilterCountText.Text = HasActiveFilter
            ? $"{_items.Count}/{_allCatalogItems.Count}"
            : string.Empty;
    }

    private void ExpandedFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string next = ExpandedFilterBox.Text ?? string.Empty;
        if (string.Equals(next, _activeFilterQuery, StringComparison.Ordinal)) return;
        _activeFilterQuery = next;
        if (_allCatalogItems.Count == 0 && _items.Count > 0)
            _allCatalogItems = _items.ToArray();
        IReadOnlyList<WidgetItem> visible = HasActiveFilter
            ? _allCatalogItems.Where(item => OrganizerSearch.MatchesItemQuery(item, _activeFilterQuery)).ToArray()
            : _allCatalogItems;
        ReconcileCatalogItems(visible);
        _keyboardFocusedIndex = _items.Count > 0 ? 0 : -1;
        RenderItems();
        UpdateKeyboardSelectionVisuals();
        UpdateExpandedFilterStatus();
    }

    private async void ExpandedFilterBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ClearExpandedFilter(releaseInputActivation: true);
            return;
        }
        if (e.Key == VirtualKey.Enter && _items.Count > 0)
        {
            e.Handled = true;
            int index = _keyboardFocusedIndex is >= 0 && _keyboardFocusedIndex < _items.Count ? _keyboardFocusedIndex : 0;
            WidgetItem target = _items[index];
            ClearExpandedFilter(releaseInputActivation: true);
            await RunSafelyAsync(() => OpenItemDirectlyAsync(target), "打开筛选项目失败");
            return;
        }
        if (e.Key is VirtualKey.Down or VirtualKey.Up)
        {
            e.Handled = true;
            MoveKeyboardFocus(e.Key == VirtualKey.Down ? 1 : -1);
        }
    }

    private void ExpandedFilterBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_filterBoxInputActive)
        {
            _filterBoxInputActive = false;
            if (!_organizerTitleEdit.IsEditing)
                _desktopLayer?.SetInputActivation(false);
        }
        if (!HasActiveFilter)
            ExpandedFilterBar.Visibility = Visibility.Collapsed;
    }

    private void ExpandedFilterClearButton_Click(object sender, RoutedEventArgs e) =>
        ClearExpandedFilter(releaseInputActivation: true);

    private async Task HandleOrganizerKeyDownAsync(KeyRoutedEventArgs e)
    {
        if (_organizerTitleEdit.IsBusy) return;

        bool ctrl = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (_itemReorderSession is not null)
            {
                CancelItemReorder();
                return;
            }
            if (_itemSelection.HasSelection)
            {
                ClearItemSelection();
                return;
            }
            if (_host.MultiSelectedOrganizerCount > 0)
            {
                _host.ClearOrganizerMultiSelection();
                return;
            }
            if (HasActiveFilter || ExpandedFilterBar.Visibility == Visibility.Visible)
            {
                ClearExpandedFilter(releaseInputActivation: true);
                return;
            }
            if (_host.IsPeekActive)
            {
                _host.ExitPeekMode();
                return;
            }
            if (_expanded && !StaysExpanded)
            {
                await RunSafelyAsync(CollapseAsync, "Escape 收起窗口失败");
            }
            return;
        }

        if (!_expanded || IsDock || _definition.PlacementMode == OrganizerPlacementMode.Station) return;
        if (ExpandedFilterBox.FocusState != FocusState.Unfocused) return;

        if (ctrl && e.Key == VirtualKey.A)
        {
            e.Handled = true;
            SelectAllVisibleItems();
            return;
        }

        if (ctrl && e.Key == VirtualKey.F)
        {
            e.Handled = true;
            ShowExpandedFilterBar();
            return;
        }

        if (ctrl && e.Key == VirtualKey.V)
        {
            e.Handled = true;
            await _catalogController.PasteClipboardAsync(default);
            return;
        }

        int columns = IsCompactList ? 1 : Math.Max(1, GetItemLayoutColumnCount());
        switch (e.Key)
        {
            case VirtualKey.Right:
                e.Handled = true;
                MoveKeyboardFocus(1);
                return;
            case VirtualKey.Left:
                e.Handled = true;
                MoveKeyboardFocus(-1);
                return;
            case VirtualKey.Down:
                e.Handled = true;
                MoveKeyboardFocus(columns);
                return;
            case VirtualKey.Up:
                e.Handled = true;
                MoveKeyboardFocus(-columns);
                return;
            case VirtualKey.Enter:
                if (TryGetKeyboardFocusedItem(out WidgetItem? enterItem))
                {
                    e.Handled = true;
                    await RunSafelyAsync(() => OpenItemDirectlyAsync(enterItem!), "键盘打开项目失败");
                }
                return;
            case VirtualKey.F2:
                if (TryGetKeyboardFocusedItem(out WidgetItem? renameItem) && renameItem!.Kind != WidgetItemKind.Organizer)
                {
                    e.Handled = true;
                    if (renameItem.Kind == WidgetItemKind.Note && renameItem.NoteId is Guid noteId)
                        await _catalogController.ShowRenameNoteDialogAsync(noteId);
                    else
                        await _catalogController.ShowRenameFileDialogAsync(renameItem);
                }
                return;
            case VirtualKey.Delete:
                if (_itemSelection.HasMultipleSelection)
                {
                    e.Handled = true;
                    await RunSafelyAsync(DeleteSelectedItemsAsync, "批量删除项目失败");
                }
                else if (TryGetKeyboardFocusedItem(out WidgetItem? deleteItem) && deleteItem!.Kind != WidgetItemKind.Organizer)
                {
                    e.Handled = true;
                    if (deleteItem.Kind == WidgetItemKind.Note && deleteItem.NoteId is Guid noteId)
                        await _catalogController.ShowDeleteNoteDialogAsync(noteId);
                    else
                        await _catalogController.DeleteFileItemAsync(deleteItem);
                }
                return;
            case VirtualKey.C when ctrl:
                if (_itemSelection.HasMultipleSelection)
                {
                    e.Handled = true;
                    await RunSafelyAsync(() => { CopySelectedItemsToClipboard(); return Task.CompletedTask; }, "批量复制项目失败");
                }
                else if (TryGetKeyboardFocusedItem(out WidgetItem? copyItem) && copyItem!.Kind is not (WidgetItemKind.Organizer or WidgetItemKind.Note))
                {
                    e.Handled = true;
                    await _catalogController.CopyFileItemAsync(copyItem);
                }
                return;
            case VirtualKey.X when ctrl:
                if (_itemSelection.HasMultipleSelection)
                {
                    e.Handled = true;
                    await RunSafelyAsync(() => { CutSelectedItemsToClipboard(); return Task.CompletedTask; }, "批量剪切项目失败");
                }
                else if (TryGetKeyboardFocusedItem(out WidgetItem? cutItem) && cutItem!.Kind is not (WidgetItemKind.Organizer or WidgetItemKind.Note))
                {
                    e.Handled = true;
                    await _catalogController.CutFileItemAsync(cutItem);
                }
                return;
        }

        // Direct type-to-filter: alphanumeric keys immediately open the inline filter bar.
        if (!ctrl && TryMapTypeToFilterChar(e.Key, out char typedChar))
        {
            e.Handled = true;
            ShowExpandedFilterBar(typedChar.ToString());
        }
    }

    private void MoveKeyboardFocus(int delta)
    {
        if (_items.Count == 0)
        {
            _keyboardFocusedIndex = -1;
            return;
        }
        if (_keyboardFocusedIndex < 0)
            _keyboardFocusedIndex = delta >= 0 ? 0 : _items.Count - 1;
        else
            _keyboardFocusedIndex = Math.Clamp(_keyboardFocusedIndex + delta, 0, _items.Count - 1);

        UpdateKeyboardSelectionVisuals();
    }

    private void ClampKeyboardFocusIndex()
    {
        if (_items.Count == 0) _keyboardFocusedIndex = -1;
        else if (_keyboardFocusedIndex >= _items.Count) _keyboardFocusedIndex = _items.Count - 1;
    }

    private bool TryGetKeyboardFocusedItem(out WidgetItem? item)
    {
        if (_keyboardFocusedIndex >= 0 && _keyboardFocusedIndex < _items.Count)
        {
            item = _items[_keyboardFocusedIndex];
            return true;
        }
        item = null;
        return false;
    }

    private void UpdateKeyboardSelectionVisuals()
    {
        for (int index = 0; index < _items.Count; index++)
        {
            if (!TryGetRealizedItemHost(index, out Border host)) continue;
            if (index == _keyboardFocusedIndex)
                SetItemInteractionVisual(host, pressed: false);
            else if (!ReferenceEquals(host, _itemDragHost))
                ResetItemInteractionVisual(host);
        }
    }

    private static bool TryMapTypeToFilterChar(VirtualKey key, out char ch)
    {
        if (key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            ch = (char)('a' + (key - VirtualKey.A));
            return true;
        }
        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            ch = (char)('0' + (key - VirtualKey.Number0));
            return true;
        }
        ch = '\0';
        return false;
    }
}
