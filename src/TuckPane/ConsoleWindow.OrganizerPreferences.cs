using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TuckPane.Core;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private readonly Dictionary<string, ToggleSwitch> _organizerMenuToggles = new();

    private void UpdateOrganizerPreferenceControls()
    {
        using var _ = _settingBinder.Suppress();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(HideCollapseIndicatorToggle, AppStrings.Get("HideCollapseIndicatorTitle"));
        if (HideCollapseIndicatorToggle.IsEnabled) HideCollapseIndicatorToggle.IsOn = _host.State.GlobalSettings.HideCollapseIndicator;
        if (CompactHoverMagnificationToggle.IsEnabled) CompactHoverMagnificationToggle.IsOn = _host.State.GlobalSettings.CompactHoverMagnificationEnabled;
        if (DockHoverMagnificationToggle.IsEnabled) DockHoverMagnificationToggle.IsOn = _host.State.GlobalSettings.DockHoverMagnificationEnabled;
        UpdateHoverMagnificationScaleControls();
        foreach (string key in OrganizerMenuVisibility.Actions)
        {
            if (!_organizerMenuToggles.TryGetValue(key, out ToggleSwitch? toggle))
            {
                toggle = new ToggleSwitch { Tag = key, MinWidth = 44, MaxWidth = 46, Padding = new Thickness(0), Margin = new Thickness(0), OnContent = string.Empty, OffContent = string.Empty, HorizontalAlignment = HorizontalAlignment.Right };
                toggle.Toggled += OrganizerMenuToggle_Toggled;
                _organizerMenuToggles.Add(key, toggle);
                OrganizerMenuOptions.Children.Add(CreateMenuSettingRow(toggle, AppStrings.Get(key)));
            }
            if (toggle.Parent is Grid row)
            {
                var label = (TextBlock)row.Children[0];
                label.Text = AppStrings.Get(key);
                label.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(AppStrings.FontFamily);
                label.CharacterSpacing = AppStrings.CharacterSpacing;
            }
            toggle.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(AppStrings.FontFamily);
            toggle.CharacterSpacing = AppStrings.CharacterSpacing;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, AppStrings.Get(key));
            if (toggle.IsEnabled) toggle.IsOn = OrganizerMenuVisibility.IsEnabled(_host.State.GlobalSettings, key);
        }
        UpdateSettingGroupSeparators(OrganizerMenuOptions);
    }

    private async void HideCollapseIndicatorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _settingBinder.IsSuppressed) return;
        HideCollapseIndicatorToggle.IsEnabled = false;
        try { await _host.SetHideCollapseIndicatorAsync(HideCollapseIndicatorToggle.IsOn); }
        catch (Exception ex)
        {
            AppLogger.Error("无法保存收缩标志设置。", ex);
            ShowError(AppStrings.Get("SaveConfigurationError"), ex.Message);
        }
        finally
        {
            HideCollapseIndicatorToggle.IsEnabled = true;
            UpdateOrganizerPreferenceControls();
        }
    }

    private async void HoverMagnificationToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_componentReady || _settingBinder.IsSuppressed || sender is not ToggleSwitch toggle) return;
        toggle.IsEnabled = false;
        UpdateHoverMagnificationScaleControls();
        try { await _host.SetHoverMagnificationAsync(ReferenceEquals(toggle, DockHoverMagnificationToggle), toggle.IsOn); }
        catch (Exception ex)
        {
            AppLogger.Error("无法保存悬浮放大设置。", ex);
            ShowError(AppStrings.Get("SaveConfigurationError"), ex.Message);
        }
        finally
        {
            toggle.IsEnabled = true;
            UpdateOrganizerPreferenceControls();
        }
    }

    private async void OrganizerMenuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_settingBinder.IsSuppressed || sender is not ToggleSwitch { Tag: string key } toggle) return;
        toggle.IsEnabled = false;
        try { await _host.SetOrganizerMenuVisibilityAsync(key, toggle.IsOn); }
        catch (Exception ex)
        {
            AppLogger.Error("无法保存窗口菜单显示设置。", ex);
            ShowError(AppStrings.Get("SaveConfigurationError"), ex.Message);
        }
        finally
        {
            toggle.IsEnabled = true;
            UpdateOrganizerPreferenceControls();
        }
    }
}
