using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;
using TuckPane.Models;
using TuckPane.Core;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class MainWindow
{
    private SolidColorBrush? _toastSuccessBrush;
    private SolidColorBrush? _toastInfoBrush;
    private SolidColorBrush? _toastWarningBrush;
    private SolidColorBrush? _toastErrorBrush;

    private bool _organizerMenuHooksAttached;
    private long _expandedViewToastVisibilityToken;
    private DispatcherQueueTimer? _expandedToastTimer;
    private DispatcherQueueTimer? _expandedToastCollapseTimer;
    private DispatcherQueueTimer? _outsideClickResumeTimer;
    private int _expandedToastVersion;
    private string? _cachedToastFontFamilyName;
    private FontFamily? _cachedToastFontFamily;

    internal static string LocalizeToast(string zh, string en, string ja) => AppStrings.Language switch
    {
        AppLanguage.English => en,
        AppLanguage.Japanese => ja,
        _ => zh
    };

    private void EnsureOrganizerMenuHooksAndIcons()
    {
        if (AddItemMenuItem.Icon is null)
            AddItemMenuItem.Icon = new FontIcon { Glyph = "\uE710" };

        SetMenuIconGlyph(
            ToggleModeMenuItem,
            _definition.PlacementMode == OrganizerPlacementMode.Floating ? "\uE718" : "\uE77A");
        SetMenuIconGlyph(
            ToggleContentModeMenuItem,
            _definition.ExpandedContentMode == OrganizerExpandedContentMode.Icon ? "\uE8FD" : "\uF0E2");
        bool permanent = _host.GetRequestedExpansionMode(OrganizerId) == OrganizerExpansionMode.AlwaysExpanded;
        SetMenuIconGlyph(ToggleExpansionModeMenuItem, permanent ? "\uE73F" : "\uE740");

        if (_organizerMenuHooksAttached) return;
        _organizerMenuHooksAttached = true;
        OrganizerContextMenu.Opening += OrganizerMenu_PauseOutsideClick;
        OrganizerContextMenu.Closed += OrganizerMenu_ResumeOutsideClick;
        _expandedViewToastVisibilityToken = ExpandedView.RegisterPropertyChangedCallback(
            UIElement.VisibilityProperty,
            ExpandedView_ToastVisibilityChanged);
        Closed += OrganizerMenu_WindowClosed;
    }

    private static void SetMenuIconGlyph(MenuFlyoutItem item, string glyph)
    {
        if (item.Icon is FontIcon existing)
        {
            if (!string.Equals(existing.Glyph, glyph, StringComparison.Ordinal))
                existing.Glyph = glyph;
            return;
        }
        item.Icon = new FontIcon { Glyph = glyph };
    }

    internal void PauseOutsideClickForOverlay()
    {
        _outsideClickResumeTimer?.Stop();
        _outsideClickHook?.Stop();
    }

    internal void ResumeOutsideClickAfterOverlay()
    {
        if (_closing) return;
        if (_outsideClickResumeTimer is null)
        {
            _outsideClickResumeTimer = DispatcherQueue.CreateTimer();
            _outsideClickResumeTimer.Interval = TimeSpan.FromMilliseconds(60);
            _outsideClickResumeTimer.IsRepeating = false;
            _outsideClickResumeTimer.Tick += OutsideClickResumeTimer_Tick;
        }
        _outsideClickResumeTimer.Stop();
        _outsideClickResumeTimer.Start();
    }

    private void OutsideClickResumeTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_closing) return;
        if (_overlayOpenCount > 0 || _contextMenuActivated || _shellContextMenuOpen || _catalogController.IsAddingItem)
            return;
        ApplyOutsideClickSetting();
    }

    private void OrganizerMenu_PauseOutsideClick(object? sender, object e) => PauseOutsideClickForOverlay();

    private void OrganizerMenu_ResumeOutsideClick(object? sender, object e) => ResumeOutsideClickAfterOverlay();

    private void ExpandedView_ToastVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (ExpandedView.Visibility != Visibility.Visible)
            HideExpandedToast();
    }

    private void OrganizerMenu_WindowClosed(object sender, WindowEventArgs args)
    {
        Closed -= OrganizerMenu_WindowClosed;
        if (_organizerMenuHooksAttached)
        {
            _organizerMenuHooksAttached = false;
            OrganizerContextMenu.Opening -= OrganizerMenu_PauseOutsideClick;
            OrganizerContextMenu.Closed -= OrganizerMenu_ResumeOutsideClick;
            if (_expandedViewToastVisibilityToken != 0)
            {
                ExpandedView.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, _expandedViewToastVisibilityToken);
                _expandedViewToastVisibilityToken = 0;
            }
        }
        if (_expandedToastTimer is not null)
        {
            _expandedToastTimer.Stop();
            _expandedToastTimer.Tick -= ExpandedToastTimer_Tick;
            _expandedToastTimer = null;
        }
        if (_expandedToastCollapseTimer is not null)
        {
            _expandedToastCollapseTimer.Stop();
            _expandedToastCollapseTimer.Tick -= ExpandedToastCollapseTimer_Tick;
            _expandedToastCollapseTimer = null;
        }
        if (_outsideClickResumeTimer is not null)
        {
            _outsideClickResumeTimer.Stop();
            _outsideClickResumeTimer.Tick -= OutsideClickResumeTimer_Tick;
            _outsideClickResumeTimer = null;
        }
    }

    internal void ShowExpandedToast(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (_closing || string.IsNullOrWhiteSpace(message)) return;
        if (!_expanded || IsDock || ExpandedView.Visibility != Visibility.Visible) return;

        EnsureOrganizerMenuHooksAndIcons();
        if (_expandedToastTimer is null)
        {
            _expandedToastTimer = DispatcherQueue.CreateTimer();
            _expandedToastTimer.Interval = TimeSpan.FromMilliseconds(2200);
            _expandedToastTimer.IsRepeating = false;
            _expandedToastTimer.Tick += ExpandedToastTimer_Tick;
        }
        if (_expandedToastCollapseTimer is null)
        {
            _expandedToastCollapseTimer = DispatcherQueue.CreateTimer();
            _expandedToastCollapseTimer.Interval = TimeSpan.FromMilliseconds(200);
            _expandedToastCollapseTimer.IsRepeating = false;
            _expandedToastCollapseTimer.Tick += ExpandedToastCollapseTimer_Tick;
        }

        _expandedToastTimer.Stop();
        _expandedToastCollapseTimer.Stop();
        int version = ++_expandedToastVersion;

        (string glyph, SolidColorBrush iconBrush) = severity switch
        {
            InfoBarSeverity.Success => ("\uE73E", _toastSuccessBrush ??= new(Color.FromArgb(0xFF, 0x34, 0xC7, 0x59))),
            InfoBarSeverity.Warning => ("\uE7BA", _toastWarningBrush ??= new(Color.FromArgb(0xFF, 0xFB, 0xBF, 0x24))),
            InfoBarSeverity.Error => ("\uE783", _toastErrorBrush ??= new(Color.FromArgb(0xFF, 0xF8, 0x71, 0x71))),
            _ => ("\uE946", _toastInfoBrush ??= new(Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA)))
        };

        ExpandedToastIcon.Glyph = glyph;
        ExpandedToastIcon.Foreground = iconBrush;
        ExpandedToastText.Text = message;

        string fontName = AppStrings.FontFamily;
        if (!string.Equals(_cachedToastFontFamilyName, fontName, StringComparison.Ordinal))
        {
            _cachedToastFontFamilyName = fontName;
            _cachedToastFontFamily = new FontFamily(fontName);
        }
        if (_cachedToastFontFamily is not null)
            ExpandedToastText.FontFamily = _cachedToastFontFamily;

        bool wasCollapsed = ExpandedToastPill.Visibility != Visibility.Visible || ExpandedToastPill.Opacity < 0.05;
        if (UseCustomAnimations && wasCollapsed)
        {
            if (ExpandedToastPill.OpacityTransition is not null)
                ExpandedToastPill.OpacityTransition.Duration = TimeSpan.Zero;
            if (ExpandedToastPill.TranslationTransition is not null)
                ExpandedToastPill.TranslationTransition.Duration = TimeSpan.Zero;
            ExpandedToastPill.Opacity = 0;
            ExpandedToastPill.Translation = new Vector3(0f, 10f, 0f);
            ExpandedToastPill.Visibility = Visibility.Visible;

            if (!DispatcherQueue.TryEnqueue(() =>
            {
                if (_closing || version != _expandedToastVersion) return;
                if (ExpandedToastPill.OpacityTransition is not null)
                    ExpandedToastPill.OpacityTransition.Duration = TimeSpan.FromMilliseconds(180);
                if (ExpandedToastPill.TranslationTransition is not null)
                    ExpandedToastPill.TranslationTransition.Duration = TimeSpan.FromMilliseconds(220);
                ExpandedToastPill.Opacity = 1;
                ExpandedToastPill.Translation = Vector3.Zero;
            }))
            {
                ExpandedToastPill.Opacity = 1;
                ExpandedToastPill.Translation = Vector3.Zero;
            }
        }
        else
        {
            TimeSpan opacityDuration = UseCustomAnimations ? TimeSpan.FromMilliseconds(160) : TimeSpan.Zero;
            TimeSpan translationDuration = UseCustomAnimations ? TimeSpan.FromMilliseconds(200) : TimeSpan.Zero;
            if (ExpandedToastPill.OpacityTransition is not null)
                ExpandedToastPill.OpacityTransition.Duration = opacityDuration;
            if (ExpandedToastPill.TranslationTransition is not null)
                ExpandedToastPill.TranslationTransition.Duration = translationDuration;
            ExpandedToastPill.Visibility = Visibility.Visible;
            ExpandedToastPill.Opacity = 1;
            ExpandedToastPill.Translation = Vector3.Zero;
        }

        _expandedToastTimer.Start();
    }

    internal void HideExpandedToast()
    {
        ++_expandedToastVersion;
        _expandedToastTimer?.Stop();
        _expandedToastCollapseTimer?.Stop();
        if (ExpandedToastPill.OpacityTransition is not null)
            ExpandedToastPill.OpacityTransition.Duration = TimeSpan.Zero;
        if (ExpandedToastPill.TranslationTransition is not null)
            ExpandedToastPill.TranslationTransition.Duration = TimeSpan.Zero;
        ExpandedToastPill.Opacity = 0;
        ExpandedToastPill.Translation = Vector3.Zero;
        ExpandedToastPill.Visibility = Visibility.Collapsed;
    }

    private void ExpandedToastTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_closing) return;
        if (UseCustomAnimations)
        {
            if (ExpandedToastPill.OpacityTransition is not null)
                ExpandedToastPill.OpacityTransition.Duration = TimeSpan.FromMilliseconds(180);
            if (ExpandedToastPill.TranslationTransition is not null)
                ExpandedToastPill.TranslationTransition.Duration = TimeSpan.FromMilliseconds(180);
            ExpandedToastPill.Opacity = 0;
            ExpandedToastPill.Translation = new Vector3(0f, 8f, 0f);
            _expandedToastCollapseTimer?.Stop();
            _expandedToastCollapseTimer?.Start();
        }
        else
        {
            HideExpandedToast();
        }
    }

    private void ExpandedToastCollapseTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_closing) return;
        if (ExpandedToastPill.Opacity < 0.05)
        {
            ExpandedToastPill.Visibility = Visibility.Collapsed;
            ExpandedToastPill.Translation = Vector3.Zero;
        }
    }

    internal void RefreshOrganizerMenuVisibility()
    {
        EnsureOrganizerMenuHooksAndIcons();
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
        PauseOutsideClickForOverlay();
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
        var file = new MenuFlyoutItem
        {
            Text = AppStrings.Get("ContextAddFile"),
            Tag = "ContextAddFile",
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        var folder = new MenuFlyoutItem
        {
            Text = AppStrings.Get("ContextAddFolder"),
            Tag = "ContextAddFolder",
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        file.Click += AddFileMenuItem_Click;
        folder.Click += AddFolderMenuItem_Click;
        menu.Items.Add(file);
        menu.Items.Add(folder);
        LocalizeContextMenu(menu);
        menu.Opening += ContextMenu_Opening;
        menu.Opening += OrganizerMenu_PauseOutsideClick;
        menu.Opened += ContextMenu_Opened;
        menu.Closed += ContextMenu_Closed;
        menu.Closed += OrganizerMenu_ResumeOutsideClick;
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

    private async void HideNameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        bool hide = ((ToggleMenuFlyoutItem)sender).IsChecked;
        await RunSafelyAsync(async () =>
        {
            await _host.SetOrganizerNameVisibilityAsync(OrganizerId, hide);
            if (!_closing)
            {
                ShowExpandedToast(
                    hide
                        ? LocalizeToast("已隐藏收纳窗名称", "Organizer name hidden", "ボックス名を非表示にしました")
                        : LocalizeToast("已显示收纳窗名称", "Organizer name shown", "ボックス名を表示しました"),
                    InfoBarSeverity.Success);
            }
        }, "更新收纳窗名称显示失败");
    }
}
