using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

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

    private void ShowEnhancedFileContextMenu(FrameworkElement host, WidgetItem item)
    {
        var open = new MenuFlyoutItem { Text = AppStrings.Get("ContextItemOpen") };
        var reveal = new MenuFlyoutItem { Text = AppStrings.Get("ContextItemReveal") };
        var copyPath = new MenuFlyoutItem { Text = AppStrings.Get("ContextItemCopyPath") };
        var properties = new MenuFlyoutItem { Text = AppStrings.Get("ContextItemProperties") };
        var rename = new MenuFlyoutItem { Text = AppStrings.Get("ContextRenameFile") };
        var copy = new MenuFlyoutItem { Text = AppStrings.Get("ContextCopyFile") };
        var cut = new MenuFlyoutItem { Text = AppStrings.Get("Cut") };
        var delete = new MenuFlyoutItem { Text = AppStrings.Get("Delete") };

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
        copyPath.Click += (_, _) => RunShellItemCommand(item, () => ShellItemCommands.CopyPathToClipboard(item.FullPath));
        properties.Click += (_, _) => RunShellItemCommand(item, () => ShellItemCommands.ShowProperties(_hwnd, item.FullPath));
        rename.Click += async (_, _) => await _catalogController.ShowRenameFileDialogAsync(item);
        copy.Click += async (_, _) => await _catalogController.CopyFileItemAsync(item);
        cut.Click += async (_, _) => await _catalogController.CutFileItemAsync(item);
        delete.Click += async (_, _) => await _catalogController.DeleteFileItemAsync(item);

        MenuFlyout flyout = CreateOrganizerContextMenu();
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
        flyout.Closed += (_, _) => _shellContextMenuOpen = false;
        try
        {
            flyout.ShowAt(host);
        }
        catch (Exception ex)
        {
            _shellContextMenuOpen = false;
            RestoreContextMenuHost();
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
        await RunSafelyAsync(async () => _ = await _host.TidyFloatingEdgeStackAsync(OrganizerId), "边缘整齐排列失败");

    internal async Task SortOrganizerAsync(OrganizerSortCriterion criterion)
    {
        if (_closing) return;
        IReadOnlyList<WidgetItem> source = _allCatalogItems.Count > 0 ? _allCatalogItems : _items.ToArray();
        if (source.Count <= 1) return;

        SortableItemMetadata[] metadata = await Task.Run(() =>
            source.Select(OrganizerSortOrder.InspectItem).ToArray());
        if (_closing) return;

        IReadOnlyList<string> sortedOrder = OrganizerSortOrder.Sort(metadata, criterion);
        _definition.ItemOrder = sortedOrder.ToList();
        _definition.ItemSlots = FreeGridSlotMap.CompactToDense(sortedOrder);
        var lookup = source.ToDictionary(item => item.RelativeName, StringComparer.OrdinalIgnoreCase);
        WidgetItem[] reordered = sortedOrder
            .Where(lookup.ContainsKey)
            .Select(key => lookup[key])
            .Concat(source.Where(item => !sortedOrder.Contains(item.RelativeName, StringComparer.OrdinalIgnoreCase)))
            .ToArray();

        ApplyCatalogItemsAndRender(reordered, refreshIcons: false);
        await SaveStateAsync();
    }
}
