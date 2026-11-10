using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class MainWindow
{
    private bool _sortingItems;

    private async void SortByNameMenuItem_Click(object sender, RoutedEventArgs e) => await SortItemsAsync(OrganizerSortKey.Name);
    private async void SortByTypeMenuItem_Click(object sender, RoutedEventArgs e) => await SortItemsAsync(OrganizerSortKey.Type);
    private async void SortByDateMenuItem_Click(object sender, RoutedEventArgs e) => await SortItemsAsync(OrganizerSortKey.DateModified);

    /// <summary>
    /// Rewrites this pane's persisted order once. The pane keeps its manual order afterwards, so
    /// drag reordering still works and new items continue to append at the end.
    /// </summary>
    private async Task SortItemsAsync(OrganizerSortKey key)
    {
        if (_sortingItems || _closing || _shellDragActive || _shellDropFinalizing || _itemReorderSession is not null) return;
        _sortingItems = true;
        try
        {
            WidgetItem[] items = _items.ToArray();
            if (items.Length < 2) return;
            OrganizerSortEntry[] entries = await Task.Run(() => items.Select(DescribeForSort).ToArray());
            if (_closing) return;
            List<string> sorted = OrganizerSortOrder.Sort(entries, key).ToList();
            // Keep keys of items that are ordered but not currently listed (e.g. a note still
            // migrating) at the end rather than silently dropping them.
            var listed = new HashSet<string>(sorted, StringComparer.OrdinalIgnoreCase);
            sorted.AddRange(_definition.ItemOrder.Where(name => !listed.Contains(name)));
            if (sorted.SequenceEqual(_definition.ItemOrder, StringComparer.OrdinalIgnoreCase)) return;

            List<string> previous = _definition.ItemOrder.ToList();
            _definition.ItemOrder = sorted;
            try
            {
                await _host.SaveStateAsync();
            }
            catch (Exception ex)
            {
                _definition.ItemOrder = previous;
                AppLogger.Error("保存排序失败。", ex);
                if (!_closing) ShowMessage(AppStrings.Get("SaveConfigurationError"), InfoBarSeverity.Warning);
                return;
            }
            await RefreshCatalogAsync(notifyUnsupported: false);
        }
        catch (Exception ex)
        {
            AppLogger.Error("排序收纳窗项目失败。", ex);
            if (!_closing) ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _sortingItems = false;
        }
    }

    private static OrganizerSortEntry DescribeForSort(WidgetItem item)
    {
        string extension = item.Kind is WidgetItemKind.Folder or WidgetItemKind.Organizer or WidgetItemKind.Note
            ? string.Empty
            : Path.GetExtension(item.RelativeName);
        DateTime modified = DateTime.MinValue;
        if (item.Kind is not (WidgetItemKind.Organizer or WidgetItemKind.Note))
        {
            try
            {
                modified = item.Kind == WidgetItemKind.Folder
                    ? Directory.GetLastWriteTimeUtc(item.FullPath)
                    : File.GetLastWriteTimeUtc(item.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return new OrganizerSortEntry(item.RelativeName, item.Name, item.Kind, extension, modified);
    }

    /// <summary>Adds Open / Show in folder / Properties ahead of the existing file commands.</summary>
    private void AddFileShellCommands(MenuFlyout flyout, WidgetItem item)
    {
        var open = new MenuFlyoutItem { Text = AppStrings.Get("ContextOpenItem") };
        var reveal = new MenuFlyoutItem { Text = AppStrings.Get("ContextRevealItem") };
        var properties = new MenuFlyoutItem { Text = AppStrings.Get("ContextItemProperties") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(open, "OpenFileMenuItem");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(reveal, "RevealFileMenuItem");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(properties, "FilePropertiesMenuItem");
        open.Click += async (_, _) => await RunSafelyAsync(() => OpenItemAsync(item, doubleTap: false), "打开项目失败");
        reveal.Click += (_, _) => RunShellItemCommand(item, ShellItemCommands.Reveal);
        properties.Click += (_, _) => RunShellItemCommand(item, path => ShellItemCommands.ShowProperties(_hwnd, path));
        flyout.Items.Add(open);
        flyout.Items.Add(reveal);
        flyout.Items.Add(properties);
        flyout.Items.Add(new MenuFlyoutSeparator());
    }

    private void RunShellItemCommand(WidgetItem item, Action<string> command)
    {
        try
        {
            command(item.FullPath);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Shell 项目命令失败：{item.FullPath}", ex);
            if (!_closing) ShowMessage(AppStrings.Format("FileMenuErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
    }
}
