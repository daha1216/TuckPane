using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private bool _syncingProductivityControls;
    private readonly List<LayoutSnapshot> _loadedSnapshots = [];

    private void RefreshProductivityControls()
    {
        _syncingProductivityControls = true;
        try
        {
            GlobalSettings settings = _host.State.GlobalSettings;
            QuickHideHotkeyCombo.SelectedIndex = Math.Clamp((int)settings.QuickHideHotkey, 0, QuickHideHotkeyCombo.Items.Count - 1);
            SearchHotkeyCombo.SelectedIndex = Math.Clamp((int)settings.SearchHotkey, 0, SearchHotkeyCombo.Items.Count - 1);
            PeekHotkeyCombo.SelectedIndex = Math.Clamp((int)settings.PeekHotkey, 0, PeekHotkeyCombo.Items.Count - 1);
            AutoApplyTopologySnapshotsToggle.IsOn = settings.AutoApplyTopologySnapshots;
            CompactVisualStyleCombo.SelectedIndex = Math.Clamp((int)settings.CompactVisualStyle, 0, CompactVisualStyleCombo.Items.Count - 1);
            SnapFloatingToDesktopGridToggle.IsOn = settings.SnapFloatingToDesktopGrid;
            DesktopMarqueeSelectionToggle.IsOn = settings.DesktopMarqueeSelectionEnabled;
            UndoAutoOrganizeButton.IsEnabled = _host.AutoOrganize.CanUndo;

            int uiErrors = App.RecoverableUiErrorCount;
            if (uiErrors > 0)
            {
                InternalErrorNoticeText.Text = AppStrings.Format("DiagnosticsInternalErrorNoticeFormat", uiErrors);
                InternalErrorNoticeText.Visibility = Visibility.Visible;
            }
            else
            {
                InternalErrorNoticeText.Visibility = Visibility.Collapsed;
            }

            RefreshAutoOrganizeTargetList();
        }
        finally
        {
            _syncingProductivityControls = false;
        }
        _ = RefreshLayoutSnapshotListAsync();
    }

    private void ApplyProductivityLanguage()
    {
        AutoOrganizeExtensionsBox.PlaceholderText = AppStrings.Get("AutoOrganizeExtensionsPlaceholder");
        AutoOrganizePatternsBox.PlaceholderText = AppStrings.Get("AutoOrganizePatternsPlaceholder");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(QuickHideHotkeyCombo, AppStrings.Get("QuickHideHotkeyTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchHotkeyCombo, AppStrings.Get("SearchHotkeyTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PeekHotkeyCombo, AppStrings.Get("PeekHotkeyTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AutoApplyTopologySnapshotsToggle, AppStrings.Get("LayoutSnapshotsAutoTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CompactVisualStyleCombo, AppStrings.Get("CompactVisualStyleTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SnapFloatingToDesktopGridToggle, AppStrings.Get("SnapFloatingToDesktopGridTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DesktopMarqueeSelectionToggle, AppStrings.Get("DesktopMarqueeSelectionTitle"));
    }

    private async void CompactVisualStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProductivityControls || CompactVisualStyleCombo.SelectedIndex < 0) return;
        var style = (CompactVisualStyle)CompactVisualStyleCombo.SelectedIndex;
        try
        {
            await _host.SetCompactVisualStyleAsync(style);
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("SaveConfigurationError"), ex.Message);
            RefreshProductivityControls();
        }
    }

    private async void SnapFloatingToDesktopGridToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingProductivityControls) return;
        try
        {
            await _host.SetSnapFloatingToDesktopGridAsync(SnapFloatingToDesktopGridToggle.IsOn);
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("SaveConfigurationError"), ex.Message);
            RefreshProductivityControls();
        }
    }

    private async void DesktopMarqueeSelectionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingProductivityControls) return;
        try
        {
            await _host.SetDesktopMarqueeSelectionEnabledAsync(DesktopMarqueeSelectionToggle.IsOn);
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("SaveConfigurationError"), ex.Message);
            RefreshProductivityControls();
        }
    }

    private async void AlignDesktopGridButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int count = await _host.AlignFloatingToDesktopGridAsync();
            ShowLayoutSnapshotStatus(AppStrings.Format("DesktopGridAlignDoneFormat", count));
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("LayoutSnapshotsErrorTitle"), ex.Message);
        }
    }

    private async void QuickHideHotkeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProductivityControls || QuickHideHotkeyCombo.SelectedIndex < 0) return;
        var preset = (QuickHideHotkeyPreset)QuickHideHotkeyCombo.SelectedIndex;
        try
        {
            string? error = await _host.SetQuickHideHotkeyAsync(preset);
            if (error is not null)
            {
                ShowError(AppStrings.Get("HotkeyErrorTitle"), error);
                RefreshProductivityControls();
            }
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("HotkeyErrorTitle"), ex.Message);
            RefreshProductivityControls();
        }
    }

    private async void SearchHotkeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProductivityControls || SearchHotkeyCombo.SelectedIndex < 0) return;
        var preset = (SearchHotkeyPreset)SearchHotkeyCombo.SelectedIndex;
        try
        {
            string? error = await _host.SetSearchHotkeyAsync(preset);
            if (error is not null)
            {
                ShowError(AppStrings.Get("HotkeyErrorTitle"), error);
                RefreshProductivityControls();
            }
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("HotkeyErrorTitle"), ex.Message);
            RefreshProductivityControls();
        }
    }

    private async void PeekHotkeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProductivityControls || PeekHotkeyCombo.SelectedIndex < 0) return;
        var preset = (PeekHotkeyPreset)PeekHotkeyCombo.SelectedIndex;
        try
        {
            string? error = await _host.SetPeekHotkeyAsync(preset);
            if (error is not null)
            {
                ShowError(AppStrings.Get("HotkeyErrorTitle"), error);
                RefreshProductivityControls();
            }
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("HotkeyErrorTitle"), ex.Message);
            RefreshProductivityControls();
        }
    }

    private async void AutoApplyTopologySnapshotsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingProductivityControls) return;
        try
        {
            await _host.SetAutoApplyTopologySnapshotsAsync(AutoApplyTopologySnapshotsToggle.IsOn);
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("SaveConfigurationError"), ex.Message);
            RefreshProductivityControls();
        }
    }

    private async Task RefreshLayoutSnapshotListAsync()
    {
        try
        {
            LayoutSnapshotStoreData store = await _host.LayoutSnapshots.LoadAsync();
            _loadedSnapshots.Clear();
            _loadedSnapshots.AddRange(store.Snapshots);
            LayoutSnapshotCombo.Items.Clear();
            foreach (LayoutSnapshot snapshot in _loadedSnapshots)
            {
                string tag = snapshot.IsAutoTopology ? AppStrings.Get("LayoutSnapshotAutoTag") : AppStrings.Get("LayoutSnapshotManualTag");
                string time = snapshot.SavedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
                LayoutSnapshotCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"[{tag}] {snapshot.Name} ({time})",
                    Tag = snapshot.Id
                });
            }
            if (LayoutSnapshotCombo.Items.Count > 0) LayoutSnapshotCombo.SelectedIndex = 0;
            bool hasSelection = LayoutSnapshotCombo.Items.Count > 0;
            RestoreLayoutSnapshotButton.IsEnabled = hasSelection;
            DeleteLayoutSnapshotButton.IsEnabled = hasSelection;
        }
        catch (Exception ex)
        {
            AppLogger.Error("刷新布局快照列表失败。", ex);
        }
    }

    private async void SaveLayoutSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string label = $"{AppStrings.Get("LayoutSnapshotDefaultPrefix")} {DateTime.Now:yyyy-MM-dd HH:mm}";
            LayoutSnapshot saved = await _host.SaveNamedLayoutSnapshotAsync(label);
            await RefreshLayoutSnapshotListAsync();
            ShowLayoutSnapshotStatus(AppStrings.Format("LayoutSnapshotSavedFormat", saved.Name, saved.Entries.Count));
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("LayoutSnapshotsErrorTitle"), ex.Message);
        }
    }

    private async void RestoreLayoutSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (LayoutSnapshotCombo.SelectedItem is not ComboBoxItem { Tag: Guid snapshotId }) return;
        try
        {
            int count = await _host.RestoreLayoutSnapshotAsync(snapshotId);
            ShowLayoutSnapshotStatus(AppStrings.Format("LayoutSnapshotRestoredFormat", count));
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("LayoutSnapshotsErrorTitle"), ex.Message);
        }
    }

    private async void DeleteLayoutSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (LayoutSnapshotCombo.SelectedItem is not ComboBoxItem { Tag: Guid snapshotId }) return;
        try
        {
            if (await _host.DeleteLayoutSnapshotAsync(snapshotId))
            {
                await RefreshLayoutSnapshotListAsync();
                ShowLayoutSnapshotStatus(AppStrings.Get("LayoutSnapshotDeleted"));
            }
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("LayoutSnapshotsErrorTitle"), ex.Message);
        }
    }

    private async void TidyEdgeStackButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int aligned = await _host.TidyFloatingEdgeStackAsync();
            ShowLayoutSnapshotStatus(AppStrings.Format("TidyEdgeStackDoneFormat", aligned));
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("LayoutSnapshotsErrorTitle"), ex.Message);
        }
    }

    private async void ExportLayoutSnapshotsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = $"TuckPane-layout-snapshots-{DateTime.Now:yyyyMMdd}"
            };
            picker.FileTypeChoices.Add("JSON", [".json"]);
            InitializeWithWindow.Initialize(picker, Hwnd);
            Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await _host.ExportLayoutSnapshotsAsync(file.Path);
            ShowLayoutSnapshotStatus(AppStrings.Format("LayoutSnapshotExportedFormat", file.Name));
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("LayoutSnapshotsErrorTitle"), ex.Message);
        }
    }

    private async void ImportLayoutSnapshotsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            InitializeWithWindow.Initialize(picker, Hwnd);
            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;
            int count = await _host.ImportLayoutSnapshotsAsync(file.Path);
            await RefreshLayoutSnapshotListAsync();
            ShowLayoutSnapshotStatus(AppStrings.Format("LayoutSnapshotImportedFormat", count));
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("LayoutSnapshotsErrorTitle"), ex.Message);
        }
    }

    private void ShowLayoutSnapshotStatus(string message)
    {
        LayoutSnapshotStatusText.Text = message;
        LayoutSnapshotStatusText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshAutoOrganizeTargetList()
    {
        Guid? previousId = AutoOrganizeTargetCombo.SelectedItem is ComboBoxItem { Tag: Guid id } ? id : null;
        AutoOrganizeTargetCombo.Items.Clear();
        int selectedIndex = -1;
        for (int index = 0; index < _host.State.Organizers.Count; index++)
        {
            OrganizerDefinition organizer = _host.State.Organizers[index];
            AutoOrganizeTargetCombo.Items.Add(new ComboBoxItem { Content = organizer.Name, Tag = organizer.Id });
            if (previousId == organizer.Id) selectedIndex = index;
        }
        if (AutoOrganizeTargetCombo.Items.Count > 0)
            AutoOrganizeTargetCombo.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        LoadSelectedAutoOrganizeRule();
    }

    private void AutoOrganizeTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProductivityControls) return;
        LoadSelectedAutoOrganizeRule();
    }

    private void LoadSelectedAutoOrganizeRule()
    {
        if (AutoOrganizeTargetCombo.SelectedItem is not ComboBoxItem { Tag: Guid organizerId } ||
            _host.State.Organizers.FirstOrDefault(o => o.Id == organizerId) is not OrganizerDefinition organizer)
        {
            AutoOrganizeSourceCombo.SelectedIndex = 0;
            AutoOrganizeExtensionsBox.Text = string.Empty;
            AutoOrganizePatternsBox.Text = string.Empty;
            return;
        }

        AutoOrganizeRule? rule = organizer.AutoOrganizeRules.FirstOrDefault();
        AutoOrganizeSourceCombo.SelectedIndex = rule is null ? 0 : (int)rule.Source;
        AutoOrganizeExtensionsBox.Text = rule?.Extensions ?? string.Empty;
        AutoOrganizePatternsBox.Text = rule?.NamePatterns ?? string.Empty;
    }

    private void AutoOrganizePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: string presetKey }) return;
        var source = AutoOrganizeSourceCombo.SelectedIndex == 1 ? AutoOrganizeSource.Downloads : AutoOrganizeSource.Desktop;
        AutoOrganizeRule preset = AutoOrganizeRules.CreatePreset(presetKey, source);
        AutoOrganizeExtensionsBox.Text = preset.Extensions;
        AutoOrganizePatternsBox.Text = preset.NamePatterns;
    }

    private async void AutoOrganizeSaveRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (AutoOrganizeTargetCombo.SelectedItem is not ComboBoxItem { Tag: Guid organizerId }) return;
        string ext = (AutoOrganizeExtensionsBox.Text ?? string.Empty).Trim();
        string pat = (AutoOrganizePatternsBox.Text ?? string.Empty).Trim();
        var source = AutoOrganizeSourceCombo.SelectedIndex == 1 ? AutoOrganizeSource.Downloads : AutoOrganizeSource.Desktop;
        List<AutoOrganizeRule> rules = string.IsNullOrEmpty(ext) && string.IsNullOrEmpty(pat)
            ? []
            : [new AutoOrganizeRule { Enabled = true, Source = source, Extensions = ext, NamePatterns = pat }];
        try
        {
            await _host.SaveOrganizerAutoOrganizeRulesAsync(organizerId, rules);
            ShowAutoOrganizeStatus(rules.Count == 0
                ? AppStrings.Get("AutoOrganizeRuleCleared")
                : AppStrings.Get("AutoOrganizeRuleSaved"));
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("AutoOrganizeTitle"), ex.Message);
        }
    }

    private void PreviewAutoOrganizeButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<AutoOrganizePlannedMove> plan = _host.PreviewAutoOrganize();
        if (plan.Count == 0)
        {
            ShowAutoOrganizeStatus(AppStrings.Get("AutoOrganizePreviewEmpty"));
            return;
        }
        string sample = string.Join("、", plan.Take(5).Select(p => $"{Path.GetFileName(p.SourcePath)} → {p.TargetOrganizerName}"));
        string suffix = plan.Count > 5 ? " …" : string.Empty;
        ShowAutoOrganizeStatus(AppStrings.Format("AutoOrganizePreviewFormat", plan.Count, sample + suffix));
    }

    private async void RunAutoOrganizeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AutoOrganizeRunSummary summary = await _host.RunAutoOrganizeAsync();
            UndoAutoOrganizeButton.IsEnabled = _host.AutoOrganize.CanUndo;
            ShowAutoOrganizeStatus(AppStrings.Format("AutoOrganizeRunResultFormat", summary.MovedCount, summary.ShortcutCount, summary.FailedCount));
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("AutoOrganizeTitle"), ex.Message);
        }
    }

    private async void UndoAutoOrganizeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AutoOrganizeUndoSummary summary = await _host.UndoAutoOrganizeAsync();
            UndoAutoOrganizeButton.IsEnabled = _host.AutoOrganize.CanUndo;
            ShowAutoOrganizeStatus(AppStrings.Format("AutoOrganizeUndoResultFormat", summary.RestoredCount, summary.SkippedCount, summary.FailedCount));
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("AutoOrganizeTitle"), ex.Message);
        }
    }

    private void ShowAutoOrganizeStatus(string message)
    {
        AutoOrganizeStatusText.Text = message;
        AutoOrganizeStatusText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
