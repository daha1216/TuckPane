using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;

namespace TuckPane;

public sealed partial class MainWindow
{
    internal void ApplyPeekState(bool active)
    {
        if (_closing || _definition.PlacementMode == OrganizerPlacementMode.Station || IsContained) return;
        _desktopLayer?.SetPeekTopmost(active);
    }

    internal void BringToForegroundForNavigation()
    {
        if (_closing || _hwnd == IntPtr.Zero) return;
        _desktopLayer?.SetInputActivation(true);
        WindowRoot.Focus(FocusState.Programmatic);
    }

    internal WindowAlignmentInsets GetDeterministicCompactInsetsForTidy(NativeMethods.RECT windowBounds, double displayScale) =>
        CalculateDeterministicCompactAlignmentInsets(windowBounds, displayScale);

    internal void ApplyTidyEdgePlacement(NativeMethods.RECT newWindowBounds)
    {
        if (_closing || _definition.PlacementMode != OrganizerPlacementMode.Floating || IsContained) return;
        _compactBounds = newWindowBounds;
        _definition.Position = DisplayPlacementService.Capture(_compactBounds, _hwnd);
        if (!_expanded && !_animating)
        {
            ApplyBounds(_compactBounds, show: _runtimeVisible, preserveZOrder: true);
            UpdateSurfaceClips();
        }
    }

    internal void InvalidateActiveSortCriterion()
    {
        if (_activeSortCriterion is null) return;
        _activeSortCriterion = null;
        UpdateSortMenuIndicators();
    }

    private void ShowEnhancedFileContextMenu(FrameworkElement host, WidgetItem item, Point? pointerPoint = null)
    {
        var open = new MenuFlyoutItem { Text = AppStrings.Get("ContextItemOpen"), Icon = new FontIcon { Glyph = "\uE8E5" } };
        var reveal = new MenuFlyoutItem { Text = AppStrings.Get("ContextItemReveal"), Icon = new FontIcon { Glyph = "\uE8DA" } };
        var copyPath = new MenuFlyoutItem { Text = AppStrings.Get("ContextItemCopyPath"), Icon = new FontIcon { Glyph = "\uE71B" } };
        var properties = new MenuFlyoutItem { Text = AppStrings.Get("ContextItemProperties"), Icon = new FontIcon { Glyph = "\uE946" } };
        var rename = new MenuFlyoutItem { Text = AppStrings.Get("ContextRenameFile"), Icon = new FontIcon { Glyph = "\uE8AC" } };
        var copy = new MenuFlyoutItem { Text = AppStrings.Get("ContextCopyFile"), Icon = new FontIcon { Glyph = "\uE8C8" } };
        var cut = new MenuFlyoutItem { Text = AppStrings.Get("Cut"), Icon = new FontIcon { Glyph = "\uE8C6" } };
        var delete = new MenuFlyoutItem { Text = AppStrings.Get("Delete"), Icon = new FontIcon { Glyph = "\uE74D" } };

        if (WindowRoot.Resources.TryGetValue("OrganizerMenuItemStyle", out object? styleObj) && styleObj is Style itemStyle)
        {
            open.Style = itemStyle;
            reveal.Style = itemStyle;
            copyPath.Style = itemStyle;
            properties.Style = itemStyle;
            rename.Style = itemStyle;
            copy.Style = itemStyle;
            cut.Style = itemStyle;
            delete.Style = itemStyle;
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(open, "OpenFileMenuItem");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(reveal, "RevealFileMenuItem");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(copyPath, "CopyPathMenuItem");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(properties, "PropertiesFileMenuItem");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(rename, "RenameFileMenuItem");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(copy, "CopyFileMenuItem");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(cut, "CutFileMenuItem");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(delete, "DeleteFileMenuItem");

        open.Click += async (_, _) => await RunSafelyAsync(() => OpenItemDirectlyAsync(item), "打开项目失败");
        reveal.Click += (_, _) => RunShellItemCommand(item, () => ShellItemCommands.RevealInExplorer(item.FullPath));
        copyPath.Click += (_, _) => RunShellItemCommand(item, () =>
        {
            ShellItemCommands.CopyPathToClipboard(item.FullPath);
            ShowExpandedToast(
                LocalizeToast("已复制完整路径", "Full path copied", "フルパスをコピーしました"),
                InfoBarSeverity.Success);
        });
        properties.Click += (_, _) => RunShellItemCommand(item, () => ShellItemCommands.ShowProperties(_hwnd, item.FullPath));
        rename.Click += async (_, _) => await _catalogController.ShowRenameFileDialogAsync(item);
        copy.Click += async (_, _) => await _catalogController.CopyFileItemAsync(item);
        cut.Click += async (_, _) => await _catalogController.CutFileItemAsync(item);
        delete.Click += async (_, _) => await _catalogController.DeleteFileItemAsync(item);

        MenuFlyout flyout = CreateOrganizerContextMenu();
        flyout.ShouldConstrainToRootBounds = false;
        flyout.Items.Add(open);
        flyout.Items.Add(reveal);
        flyout.Items.Add(copyPath);
        flyout.Items.Add(properties);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(rename);
        flyout.Items.Add(copy);
        flyout.Items.Add(cut);
        flyout.Items.Add(delete);

        _shellContextMenuOpen = true;
        PauseOutsideClickForOverlay();
        flyout.Opening += OrganizerMenu_PauseOutsideClick;
        flyout.Closed += (_, _) =>
        {
            _shellContextMenuOpen = false;
            ResumeOutsideClickAfterOverlay();
        };
        try
        {
            if (pointerPoint is Point pt)
                flyout.ShowAt(host, pt);
            else
                flyout.ShowAt(host);
        }
        catch (Exception ex)
        {
            _shellContextMenuOpen = false;
            RestoreContextMenuHost();
            ResumeOutsideClickAfterOverlay();
            AppLogger.Error($"无法打开文件项目菜单：{item.FullPath}", ex);
            ShowMessage(AppStrings.Format("FileMenuErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
    }

    private void RunShellItemCommand(WidgetItem item, Action command)
    {
        try
        {
            command();
            if (_host.IsPeekActive) _host.ExitPeekMode();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"项目快捷命令失败：{item.FullPath}", ex);
            if (!_closing) ShowMessage(AppStrings.Format("OpenItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
    }

    private async Task OpenItemDirectlyAsync(WidgetItem item)
    {
        if (item.Kind == WidgetItemKind.Organizer && item.OrganizerId is Guid childId)
        {
            await _host.OpenContainedOrganizerAsync(childId, _compactBounds);
            return;
        }
        if (item.Kind == WidgetItemKind.Note && item.NoteId is Guid noteId)
        {
            _host.OpenNote(OrganizerId, noteId);
            if (_host.IsPeekActive) _host.ExitPeekMode();
            return;
        }
        if (item.Kind == WidgetItemKind.PortableNote)
        {
            await _host.OpenExternalNoteAsync(item.FullPath);
            if (_host.IsPeekActive) _host.ExitPeekMode();
            return;
        }
        if (item.Kind == WidgetItemKind.PortableTodo)
        {
            await _host.OpenExternalTodoAsync(item.FullPath);
            if (_host.IsPeekActive) _host.ExitPeekMode();
            return;
        }
        await _host.ShellLauncher.OpenAsync(item.FullPath);
        if (_host.IsPeekActive) _host.ExitPeekMode();
    }

    private async void SortByNameAscMenuItem_Click(object sender, RoutedEventArgs e) =>
        await RunSafelyAsync(() => SortOrganizerAsync(OrganizerSortCriterion.NameAscending), "按名称升序排序失败");

    private async void SortByNameDescMenuItem_Click(object sender, RoutedEventArgs e) =>
        await RunSafelyAsync(() => SortOrganizerAsync(OrganizerSortCriterion.NameDescending), "按名称降序排序失败");

    private async void SortByTypeMenuItem_Click(object sender, RoutedEventArgs e) =>
        await RunSafelyAsync(() => SortOrganizerAsync(OrganizerSortCriterion.TypeAscending), "按类型排序失败");

    private async void SortByDateModifiedMenuItem_Click(object sender, RoutedEventArgs e) =>
        await RunSafelyAsync(() => SortOrganizerAsync(OrganizerSortCriterion.DateModifiedDescending), "按修改日期排序失败");

    private async void SortBySizeMenuItem_Click(object sender, RoutedEventArgs e) =>
        await RunSafelyAsync(() => SortOrganizerAsync(OrganizerSortCriterion.SizeDescending), "按大小排序失败");

    private async void TidyEdgeStackMenuItem_Click(object sender, RoutedEventArgs e) =>
        await RunSafelyAsync(async () =>
        {
            int moved = await _host.TidyFloatingEdgeStackAsync(OrganizerId);
            if (_closing) return;
            if (moved <= 0)
            {
                ShowExpandedToast(
                    LocalizeToast("当前悬浮收纳窗已整齐贴边", "Floating organizers are already tidied", "フローティングボックスは整列済みです"),
                    InfoBarSeverity.Informational);
            }
            else if (_expanded)
            {
                string message = moved == 1
                    ? LocalizeToast("已更新贴边排列位置（收起后生效）", "Edge alignment updated (applies on collapse)", "端への整列位置を更新しました（折りたたみ後に反映）")
                    : LocalizeToast($"已整齐排列 {moved} 个悬浮窗（收起后生效）", $"Tidied {moved} floating organizers (applies on collapse)", $"{moved} 個のボックスを整列しました（折りたたみ後に反映）");
                ShowExpandedToast(message, InfoBarSeverity.Success);
            }
            else
            {
                ShowMessage(AppStrings.Format("TidyEdgeStackDoneFormat", moved), InfoBarSeverity.Success);
            }
        }, "边缘整齐排列失败");

    internal async Task SortOrganizerAsync(OrganizerSortCriterion criterion)
    {
        if (_closing) return;
        IReadOnlyList<WidgetItem> source = _allCatalogItems.Count > 0 ? _allCatalogItems : _items.ToArray();
        if (source.Count <= 1)
        {
            ShowExpandedToast(
                LocalizeToast("项目不足 2 项，无需排序", "Need at least 2 items to sort", "並び替えには 2 件以上の項目が必要です"),
                InfoBarSeverity.Informational);
            return;
        }

        string[] previousFullOrder = source.Select(i => i.RelativeName).ToArray();
        int[] previousFullSlots = FreeGridSlotMap.ResolveSlots(previousFullOrder, _definition.ItemSlots);
        var previousSlotByItem = new Dictionary<string, int>(previousFullOrder.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < previousFullOrder.Length; i++)
        {
            string rel = previousFullOrder[i];
            if (!string.IsNullOrEmpty(rel))
                previousSlotByItem[rel] = i < previousFullSlots.Length ? previousFullSlots[i] : i;
        }
        Dictionary<string, Point>? before = UseCustomAnimations && _expanded ? CaptureRealizedItemPositions() : null;

        SortableItemMetadata[] metadata = await Task.Run(() =>
            source.Select(OrganizerSortOrder.InspectItem).ToArray());
        if (_closing) return;

        IReadOnlyList<string> sortedOrder = OrganizerSortOrder.Sort(metadata, criterion);
        List<string> previousOrder = _definition.ItemOrder.ToList();
        var previousSlots = new Dictionary<string, int>(_definition.ItemSlots ?? [], StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> nextDenseSlots = FreeGridSlotMap.CompactToDense(sortedOrder);
        _definition.ItemOrder = sortedOrder.ToList();
        _definition.ItemSlots = nextDenseSlots;
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
        var reordered = new List<WidgetItem>(source.Count);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in sortedOrder)
        {
            if (lookup.TryGetValue(key, out WidgetItem? matched) && emitted.Add(key))
                reordered.Add(matched);
        }
        foreach (WidgetItem entry in source)
        {
            if (!emitted.Contains(entry.RelativeName))
                reordered.Add(entry);
        }
        reordered.AddRange(duplicates);

        NormalizeItemMotionsToLayout("SortOrganizer");
        ApplyCatalogItemsAndRender(reordered, refreshIcons: false);
        SyncResolvedItemSlots();
        _freeSlotGridLayout.InvalidateSlots();
        ItemsRepeater.InvalidateMeasure();
        ItemsRepeater.InvalidateArrange();
        ItemsRepeater.UpdateLayout();
        ReconcileSmoothScrollBounds();
        if (before is not null) ApplyFlipFromCapturedPositions(before);
        RenderCompactPreview();
        _host.NotifyOrganizerPreviewChanged(_definition.Id);

        _activeSortCriterion = criterion;
        UpdateSortMenuIndicators();

        int changedCount = 0;
        int compareLength = Math.Max(previousFullOrder.Length, reordered.Count);
        for (int index = 0; index < compareLength; index++)
        {
            string? oldName = index < previousFullOrder.Length ? previousFullOrder[index] : null;
            string? newName = index < reordered.Count ? reordered[index].RelativeName : null;
            int newSlot = newName is not null && nextDenseSlots.TryGetValue(newName, out int assignedSlot)
                ? assignedSlot
                : index;
            bool slotChanged = newName is not null &&
                previousSlotByItem.TryGetValue(newName, out int oldSlot) &&
                oldSlot != newSlot;
            if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase) || slotChanged)
                changedCount++;
        }

        if (changedCount == 0)
        {
            string unchangedToast = criterion switch
            {
                OrganizerSortCriterion.NameAscending => LocalizeToast("当前已是按名称（A→Z）顺序", "Already sorted by name (A→Z)", "すでに名前順（A→Z）です"),
                OrganizerSortCriterion.NameDescending => LocalizeToast("当前已是按名称（Z→A）顺序", "Already sorted by name (Z→A)", "すでに名前順（Z→A）です"),
                OrganizerSortCriterion.TypeAscending => LocalizeToast("当前已是按类型顺序", "Already sorted by type", "すでに種類順です"),
                OrganizerSortCriterion.DateModifiedDescending => LocalizeToast("当前已是按修改日期顺序", "Already sorted by date modified", "すでに更新日時順です"),
                OrganizerSortCriterion.SizeDescending => LocalizeToast("当前已是按大小顺序", "Already sorted by size", "すでにサイズ順です"),
                _ => LocalizeToast("当前已是该排序顺序", "Already in this sort order", "すでにこの並び順です")
            };
            ShowExpandedToast(unchangedToast, InfoBarSeverity.Informational);
            return;
        }

        string toastMessage = criterion switch
        {
            OrganizerSortCriterion.NameAscending => LocalizeToast("已按名称（A→Z）排序", "Sorted by name (A→Z)", "名前順（A→Z）で並び替えました"),
            OrganizerSortCriterion.NameDescending => LocalizeToast("已按名称（Z→A）排序", "Sorted by name (Z→A)", "名前順（Z→A）で並び替えました"),
            OrganizerSortCriterion.TypeAscending => LocalizeToast("已按类型排序", "Sorted by type", "種類順で並び替えました"),
            OrganizerSortCriterion.DateModifiedDescending => LocalizeToast("已按修改日期排序", "Sorted by date modified", "更新日時順で並び替えました"),
            OrganizerSortCriterion.SizeDescending => LocalizeToast("已按大小排序", "Sorted by size", "サイズ順で並び替えました"),
            _ => LocalizeToast("已完成排序", "Items sorted", "並び替えました")
        };
        ShowExpandedToast(toastMessage, InfoBarSeverity.Success);
    }
}
