using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using TuckPane.Models;
using TuckPane.Core;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class MainWindow
{
    internal void RefreshOrganizerMenuVisibility()
    {
        var settings = _host.State.GlobalSettings;
        string[] visible = OrganizerMenuVisibility.VisibleActions(settings, _definition.PlacementMode);
        // Side stations can be narrower than the menu; let the popup use the screen.
        OrganizerContextMenu.ShouldConstrainToRootBounds = false;
        foreach (MenuFlyoutItemBase item in OrganizerContextMenu.Items)
        {
            item.Visibility = item is MenuFlyoutSeparator
                ? OrganizerMenuVisibility.ShowSeparator(visible) ? Visibility.Visible : Visibility.Collapsed
                : visible.Contains(ReferenceEquals(item, ToggleExpansionModeMenuItem)
                    ? "ContextSwitchExpansion" : item.Tag as string ?? "") ? Visibility.Visible : Visibility.Collapsed;
        }
        if (visible.Length == 0) OrganizerContextMenu.Hide();
        CompactView.ContextFlyout = visible.Length == 0 ? null : OrganizerContextMenu;
        ExpandedView.ContextFlyout = visible.Length == 0 ? null : OrganizerContextMenu;
    }

    private void OrganizerWindow_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || _closing || _animating || _organizerTitleEdit.IsEditing) return;
        // Preserve item menus when the magnified icon extends beyond its original host.
        if (IsDock && e.TryGetPosition(ExpandedView, out Point dockPoint) && DockItemAtPoint(dockPoint) is { } host)
        {
            Item_ContextRequested(host, e);
            return;
        }
        RefreshOrganizerMenuVisibility();
        if (!OrganizerContextMenu.Items.Any(item => item.Visibility == Visibility.Visible)) return;
        FrameworkElement target = _expanded ? ExpandedView : CompactView;
        e.Handled = true;
        if (e.TryGetPosition(target, out Point point)) OrganizerContextMenu.ShowAt(target, new FlyoutShowOptions { Position = point });
        else OrganizerContextMenu.ShowAt(target);
    }

    // The empty Dock button shares the same two choices as the window menu.
    private void AddItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogController.IsAddingItem || _closing || sender is not FrameworkElement target) return;
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false,
            MenuFlyoutPresenterStyle = (Style)WindowRoot.Resources["OrganizerMenuPresenterStyle"] };
        var file = new MenuFlyoutItem { Text = AppStrings.Get("ContextAddFile"), Tag = "ContextAddFile" };
        var folder = new MenuFlyoutItem { Text = AppStrings.Get("ContextAddFolder"), Tag = "ContextAddFolder" };
        file.Click += AddFileMenuItem_Click;
        folder.Click += AddFolderMenuItem_Click;
        menu.Items.Add(file);
        menu.Items.Add(folder);
        LocalizeContextMenu(menu);
        menu.Opening += ContextMenu_Opening;
        menu.Opened += ContextMenu_Opened;
        menu.Closed += ContextMenu_Closed;
        menu.ShowAt(target);
    }

    private async void AddFileMenuItem_Click(object sender, RoutedEventArgs e) => await _catalogController.AddSelectedItemAsync(folder: false);
    private async void AddFolderMenuItem_Click(object sender, RoutedEventArgs e) => await _catalogController.AddSelectedItemAsync(folder: true);

    private void UpdatePlacementModeMenuItems()
    {
        bool station = !OrganizerKinds.IsRegular(_definition.PlacementMode);
        string key = _definition.PlacementMode == OrganizerPlacementMode.Floating
            ? "ContextSwitchToPositioned" : "ContextSwitchToFloating";
        ToggleModeMenuItem.Text = AppStrings.Get(key);
        ToggleModeMenuItem.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
        ToggleModeMenuItem.FontFamily = new FontFamily(AppStrings.FontFamily);
        ToggleModeMenuItem.CharacterSpacing = AppStrings.CharacterSpacing;
        UpdateHideNameMenuItems();
    }

    private void UpdateHideNameMenuItems()
    {
        HideNameMenuItem.Text = AppStrings.Get("ContextHideName");
        HideNameMenuItem.IsChecked = _definition.HideName;
        HideNameMenuItem.Visibility = !OrganizerKinds.IsRegular(_definition.PlacementMode) ? Visibility.Collapsed : Visibility.Visible;
        HideNameMenuItem.FontFamily = new FontFamily(AppStrings.FontFamily);
        HideNameMenuItem.CharacterSpacing = AppStrings.CharacterSpacing;
        RefreshOrganizerMenuVisibility();
    }

    private void ApplyOrganizerNameVisibility()
    {
        CompactNameText.Visibility = _definition.HideName ? Visibility.Collapsed : Visibility.Visible;
        SyncCompactNameShadow();
        ExpandedNameText.Visibility = _organizerTitleEdit.IsEditing ? Visibility.Collapsed : _definition.HideName || !OrganizerKinds.IsRegular(_definition.PlacementMode)
            ? Visibility.Collapsed : Visibility.Visible;
        // Row heights and bounds are deliberately unchanged when names are hidden.
        ToolTipService.SetToolTip(CompactTile, _storage.Exists
            ? _definition.HideName ? null : _definition.Name
            : AppStrings.Get("MissingStorage"));
        UpdateHideNameMenuItems();
    }

    private async void HideNameMenuItem_Click(object sender, RoutedEventArgs e) =>
        await RunSafelyAsync(() => _host.SetOrganizerNameVisibilityAsync(OrganizerId,
            ((ToggleMenuFlyoutItem)sender).IsChecked), "更新收纳窗名称显示失败");
}
