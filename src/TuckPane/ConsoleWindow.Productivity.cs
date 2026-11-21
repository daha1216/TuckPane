using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private bool _syncingProductivityControls;
    private long _layoutSnapshotRefreshVersion;
    private readonly List<LayoutSnapshot> _loadedSnapshots = [];
    private readonly Dictionary<Guid, AutoOrganizeFormDraft> _autoOrganizeDrafts = [];
    private Guid? _autoOrganizeDraftOrganizerId;
    private bool _autoOrganizeDraftDirty;
    private bool _autoOrganizeBusy;

    private sealed record AutoOrganizeFormDraft(AutoOrganizeSource Source, string Extensions, string Patterns);

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
            FreeGridPlacementToggle.IsOn = settings.FreeGridPlacementEnabled;
            FreeGridPreferDirectSwapToggle.IsOn = settings.FreeGridPreferDirectSwap;
            UndoAutoOrganizeButton.IsEnabled = !_autoOrganizeBusy && _host.AutoOrganize.CanUndo;

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
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AutoOrganizeExtensionsBox, AppStrings.Get("AutoOrganizeExtensionsLabel"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AutoOrganizePatternsBox, AppStrings.Get("AutoOrganizePatternsLabel"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(QuickHideHotkeyCombo, AppStrings.Get("QuickHideHotkeyTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchHotkeyCombo, AppStrings.Get("SearchHotkeyTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PeekHotkeyCombo, AppStrings.Get("PeekHotkeyTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AutoApplyTopologySnapshotsToggle, AppStrings.Get("LayoutSnapshotsAutoTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CompactVisualStyleCombo, AppStrings.Get("CompactVisualStyleTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SnapFloatingToDesktopGridToggle, AppStrings.Get("SnapFloatingToDesktopGridTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DesktopMarqueeSelectionToggle, AppStrings.Get("DesktopMarqueeSelectionTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FreeGridPlacementToggle, AppStrings.Get("FreeGridPlacementTitle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FreeGridPreferDirectSwapToggle, AppStrings.Get("FreeGridPreferDirectSwapTitle"));
        if (AutoOrganizeTargetCombo.Items.Count > 0)
        {
            bool wasSyncing = _syncingProductivityControls;
            _syncingProductivityControls = true;
            try { RefreshAutoOrganizeTargetList(); }
            finally { _syncingProductivityControls = wasSyncing; }
        }
    }

    private void WithSuppressedProductivitySync(Action action)
    {
        bool wasSyncing = _syncingProductivityControls;
        _syncingProductivityControls = true;
        try { action(); }
        finally { _syncingProductivityControls = wasSyncing; }
    }

    private void SyncHotkeyCombosFromSettings() => WithSuppressedProductivitySync(() =>
    {
        GlobalSettings settings = _host.State.GlobalSettings;
        QuickHideHotkeyCombo.SelectedIndex = Math.Clamp((int)settings.QuickHideHotkey, 0, QuickHideHotkeyCombo.Items.Count - 1);
        SearchHotkeyCombo.SelectedIndex = Math.Clamp((int)settings.SearchHotkey, 0, SearchHotkeyCombo.Items.Count - 1);
        PeekHotkeyCombo.SelectedIndex = Math.Clamp((int)settings.PeekHotkey, 0, PeekHotkeyCombo.Items.Count - 1);
    });

    private async void FreeGridPlacementToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingProductivityControls) return;
        try
        {
            await _host.SetFreeGridPlacementEnabledAsync(FreeGridPlacementToggle.IsOn);
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("SaveConfigurationError"), ex.Message);
            WithSuppressedProductivitySync(() => FreeGridPlacementToggle.IsOn = _host.State.GlobalSettings.FreeGridPlacementEnabled);
        }
    }

    private async void FreeGridPreferDirectSwapToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingProductivityControls) return;
        try
        {
            await _host.SetFreeGridPreferDirectSwapAsync(FreeGridPreferDirectSwapToggle.IsOn);
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("SaveConfigurationError"), ex.Message);
            WithSuppressedProductivitySync(() => FreeGridPreferDirectSwapToggle.IsOn = _host.State.GlobalSettings.FreeGridPreferDirectSwap);
        }
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
            WithSuppressedProductivitySync(() => CompactVisualStyleCombo.SelectedIndex =
                Math.Clamp((int)_host.State.GlobalSettings.CompactVisualStyle, 0, CompactVisualStyleCombo.Items.Count - 1));
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
            WithSuppressedProductivitySync(() => SnapFloatingToDesktopGridToggle.IsOn = _host.State.GlobalSettings.SnapFloatingToDesktopGrid);
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
            WithSuppressedProductivitySync(() => DesktopMarqueeSelectionToggle.IsOn = _host.State.GlobalSettings.DesktopMarqueeSelectionEnabled);
        }
    }

    private async void AlignDesktopGridButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int count = await _host.AlignFloatingToDesktopGridAsync();
            ShowDesktopGridAlignStatus(AppStrings.Format("DesktopGridAlignDoneFormat", count));
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("LayoutSnapshotsErrorTitle"), ex.Message);
        }
    }

    private void ShowDesktopGridAlignStatus(string message)
    {
        DesktopGridAlignStatusText.Text = message;
        DesktopGridAlignStatusText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
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
                SyncHotkeyCombosFromSettings();
            }
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("HotkeyErrorTitle"), ex.Message);
            SyncHotkeyCombosFromSettings();
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
                SyncHotkeyCombosFromSettings();
            }
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("HotkeyErrorTitle"), ex.Message);
            SyncHotkeyCombosFromSettings();
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
                SyncHotkeyCombosFromSettings();
            }
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("HotkeyErrorTitle"), ex.Message);
            SyncHotkeyCombosFromSettings();
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
            WithSuppressedProductivitySync(() => AutoApplyTopologySnapshotsToggle.IsOn = _host.State.GlobalSettings.AutoApplyTopologySnapshots);
        }
    }

    private async Task RefreshLayoutSnapshotListAsync()
    {
        long version = ++_layoutSnapshotRefreshVersion;
        try
        {
            LayoutSnapshotStoreData store = await _host.LayoutSnapshots.LoadAsync();
            if (_closingPermanently || version != _layoutSnapshotRefreshVersion) return;
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
        if (_appWindow is null) return;
        try
        {
            var picker = new FileSavePicker(_appWindow.Id)
            {
                SuggestedFileName = $"TuckPane-layout-snapshots-{DateTime.Now:yyyyMMdd}",
                DefaultFileExtension = ".json"
            };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            PickFileResult? file = await picker.PickSaveFileAsync();
            if (file is null || string.IsNullOrWhiteSpace(file.Path)) return;
            await _host.ExportLayoutSnapshotsAsync(file.Path);
            ShowLayoutSnapshotStatus(AppStrings.Format("LayoutSnapshotExportedFormat", Path.GetFileName(file.Path)));
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("LayoutSnapshotsErrorTitle"), ex.Message);
        }
    }

    private async void ImportLayoutSnapshotsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow is null) return;
        try
        {
            var picker = new FileOpenPicker(_appWindow.Id);
            picker.FileTypeFilter.Add(".json");
            PickFileResult? file = await picker.PickSingleFileAsync();
            if (file is null || string.IsNullOrWhiteSpace(file.Path)) return;
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
        CaptureCurrentAutoOrganizeDraftIfDirty();
        var liveIds = _host.State.Organizers.Select(o => o.Id).ToHashSet();
        foreach (Guid staleId in _autoOrganizeDrafts.Keys.Where(id => !liveIds.Contains(id)).ToArray())
            _autoOrganizeDrafts.Remove(staleId);

        Guid? previousId = AutoOrganizeTargetCombo.SelectedItem is ComboBoxItem { Tag: Guid id } ? id : _autoOrganizeDraftOrganizerId;
        AutoOrganizeTargetCombo.Items.Clear();
        int selectedIndex = -1;
        for (int index = 0; index < _host.State.Organizers.Count; index++)
        {
            OrganizerDefinition organizer = _host.State.Organizers[index];
            AutoOrganizeTargetCombo.Items.Add(new ComboBoxItem
            {
                Content = FormatOrganizerDisplayName(organizer),
                Tag = organizer.Id
            });
            if (previousId == organizer.Id) selectedIndex = index;
        }
        if (AutoOrganizeTargetCombo.Items.Count > 0)
            AutoOrganizeTargetCombo.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        LoadSelectedAutoOrganizeRule();
    }

    internal void RefreshAutoOrganizeTargetName(OrganizerDefinition definition)
    {
        if (AutoOrganizeTargetCombo is null) return;
        for (int index = 0; index < AutoOrganizeTargetCombo.Items.Count; index++)
        {
            if (AutoOrganizeTargetCombo.Items[index] is ComboBoxItem { Tag: Guid itemId } && itemId == definition.Id)
            {
                bool wasSelected = AutoOrganizeTargetCombo.SelectedIndex == index;
                string label = FormatOrganizerDisplayName(definition);
                WithSuppressedProductivitySync(() =>
                {
                    AutoOrganizeTargetCombo.Items[index] = new ComboBoxItem
                    {
                        Content = label,
                        Tag = definition.Id
                    };
                    if (wasSelected)
                        AutoOrganizeTargetCombo.SelectedIndex = index;
                });
                break;
            }
        }
    }

    private void AutoOrganizeTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProductivityControls) return;
        CaptureCurrentAutoOrganizeDraftIfDirty();
        ShowAutoOrganizeStatus(string.Empty);
        LoadSelectedAutoOrganizeRule();
    }

    private void AutoOrganizeSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProductivityControls) return;
        MarkAutoOrganizeDraftDirty();
    }

    private void AutoOrganizeDraft_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingProductivityControls) return;
        MarkAutoOrganizeDraftDirty();
    }

    private void MarkAutoOrganizeDraftDirty()
    {
        if (_autoOrganizeDraftOrganizerId is not Guid organizerId) return;
        _autoOrganizeDraftDirty = true;
        _autoOrganizeDrafts[organizerId] = ReadAutoOrganizeFormDraft();
    }

    private AutoOrganizeFormDraft ReadAutoOrganizeFormDraft()
    {
        var source = AutoOrganizeSourceCombo.SelectedIndex == 1
            ? AutoOrganizeSource.Downloads
            : AutoOrganizeSource.Desktop;
        string ext = (AutoOrganizeExtensionsBox.Text ?? string.Empty).Trim();
        string pat = (AutoOrganizePatternsBox.Text ?? string.Empty).Trim();
        return new AutoOrganizeFormDraft(source, ext, pat);
    }

    private void CaptureCurrentAutoOrganizeDraftIfDirty()
    {
        if (_autoOrganizeDraftDirty && _autoOrganizeDraftOrganizerId is Guid organizerId)
        {
            _autoOrganizeDrafts[organizerId] = ReadAutoOrganizeFormDraft();
            _autoOrganizeDraftDirty = false;
        }
    }

    private static List<AutoOrganizeRule> BuildRulesFromDraft(AutoOrganizeFormDraft draft) =>
        string.IsNullOrEmpty(draft.Extensions) && string.IsNullOrEmpty(draft.Patterns)
            ? []
            : [new AutoOrganizeRule { Enabled = true, Source = draft.Source, Extensions = draft.Extensions, NamePatterns = draft.Patterns }];

    private void LoadSelectedAutoOrganizeRule()
    {
        WithSuppressedProductivitySync(() =>
        {
            if (AutoOrganizeTargetCombo.SelectedItem is not ComboBoxItem { Tag: Guid organizerId } ||
                _host.State.Organizers.FirstOrDefault(o => o.Id == organizerId) is not OrganizerDefinition organizer)
            {
                _autoOrganizeDraftOrganizerId = null;
                _autoOrganizeDraftDirty = false;
                AutoOrganizeSourceCombo.SelectedIndex = 0;
                AutoOrganizeExtensionsBox.Text = string.Empty;
                AutoOrganizePatternsBox.Text = string.Empty;
                return;
            }

            _autoOrganizeDraftOrganizerId = organizer.Id;
            _autoOrganizeDraftDirty = false;
            if (_autoOrganizeDrafts.TryGetValue(organizer.Id, out AutoOrganizeFormDraft? draft))
            {
                AutoOrganizeSourceCombo.SelectedIndex = (int)draft.Source;
                AutoOrganizeExtensionsBox.Text = draft.Extensions;
                AutoOrganizePatternsBox.Text = draft.Patterns;
                return;
            }

            AutoOrganizeRule? rule = organizer.AutoOrganizeRules.FirstOrDefault();
            AutoOrganizeSourceCombo.SelectedIndex = rule is null ? 0 : (int)rule.Source;
            AutoOrganizeExtensionsBox.Text = rule?.Extensions ?? string.Empty;
            AutoOrganizePatternsBox.Text = rule?.NamePatterns ?? string.Empty;
        });
    }

    private void AutoOrganizePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_autoOrganizeBusy || sender is not Button { DataContext: string presetKey }) return;
        var source = AutoOrganizeSourceCombo.SelectedIndex == 1 ? AutoOrganizeSource.Downloads : AutoOrganizeSource.Desktop;
        AutoOrganizeRule preset = AutoOrganizeRules.CreatePreset(presetKey, source);
        WithSuppressedProductivitySync(() =>
        {
            AutoOrganizeExtensionsBox.Text = preset.Extensions;
            AutoOrganizePatternsBox.Text = preset.NamePatterns;
        });
        MarkAutoOrganizeDraftDirty();
    }

    private void SetAutoOrganizeBusy(bool busy)
    {
        _autoOrganizeBusy = busy;
        AutoOrganizeTargetCombo.IsEnabled = !busy;
        AutoOrganizeSourceCombo.IsEnabled = !busy;
        AutoOrganizeExtensionsBox.IsEnabled = !busy;
        AutoOrganizePatternsBox.IsEnabled = !busy;
        foreach (Button presetButton in AutoOrganizePresetsPanel.Children.OfType<Button>())
            presetButton.IsEnabled = !busy;
        AutoOrganizeSaveRuleButton.IsEnabled = !busy;
        PreviewAutoOrganizeButton.IsEnabled = !busy;
        RunAutoOrganizeButton.IsEnabled = !busy;
        UndoAutoOrganizeButton.IsEnabled = !busy && _host.AutoOrganize.CanUndo;
    }

    private AppStateV2 BuildAutoOrganizePreviewState()
    {
        CaptureCurrentAutoOrganizeDraftIfDirty();
        var organizers = new List<OrganizerDefinition>(_host.State.Organizers.Count);
        foreach (OrganizerDefinition source in _host.State.Organizers)
        {
            OrganizerDefinition clone = source.Clone();
            clone.Name = FormatOrganizerDisplayName(source);
            if (_autoOrganizeDrafts.TryGetValue(source.Id, out AutoOrganizeFormDraft? draft))
                clone.AutoOrganizeRules = BuildRulesFromDraft(draft);
            organizers.Add(clone);
        }
        return new AppStateV2
        {
            SchemaVersion = _host.State.SchemaVersion,
            GlobalSettings = _host.State.GlobalSettings,
            Organizers = organizers
        };
    }

    private async Task CommitPendingAutoOrganizeDraftsAsync()
    {
        CaptureCurrentAutoOrganizeDraftIfDirty();
        if (_autoOrganizeDrafts.Count == 0) return;
        foreach (KeyValuePair<Guid, AutoOrganizeFormDraft> entry in _autoOrganizeDrafts.ToArray())
        {
            if (!_host.State.Organizers.Any(o => o.Id == entry.Key))
            {
                _autoOrganizeDrafts.Remove(entry.Key);
                continue;
            }
            await _host.SaveOrganizerAutoOrganizeRulesAsync(entry.Key, BuildRulesFromDraft(entry.Value));
            _autoOrganizeDrafts.Remove(entry.Key);
        }
        _autoOrganizeDraftDirty = false;
    }

    private async void AutoOrganizeSaveRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoOrganizeBusy || AutoOrganizeTargetCombo.SelectedItem is not ComboBoxItem { Tag: Guid organizerId }) return;
        AutoOrganizeFormDraft draft = ReadAutoOrganizeFormDraft();
        List<AutoOrganizeRule> rules = BuildRulesFromDraft(draft);
        SetAutoOrganizeBusy(true);
        try
        {
            await _host.SaveOrganizerAutoOrganizeRulesAsync(organizerId, rules);
            _autoOrganizeDrafts.Remove(organizerId);
            _autoOrganizeDraftDirty = false;
            ShowAutoOrganizeStatus(rules.Count == 0
                ? AppStrings.Get("AutoOrganizeRuleCleared")
                : AppStrings.Get("AutoOrganizeRuleSaved"));
        }
        catch (Exception ex)
        {
            ShowError(AppStrings.Get("AutoOrganizeTitle"), ex.Message);
        }
        finally
        {
            if (!_closingPermanently) SetAutoOrganizeBusy(false);
        }
    }

    private async void PreviewAutoOrganizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoOrganizeBusy) return;
        SetAutoOrganizeBusy(true);
        try
        {
            AppStateV2 previewState = BuildAutoOrganizePreviewState();
            IReadOnlyList<AutoOrganizePlannedMove> plan = await Task.Run(() => _host.AutoOrganize.PreviewPlan(previewState));
            if (_closingPermanently) return;
            if (plan.Count == 0)
            {
                ShowAutoOrganizeStatus(AppStrings.Get("AutoOrganizePreviewEmpty"));
                return;
            }
            string sample = string.Join("、", plan.Take(5).Select(p => $"{Path.GetFileName(p.SourcePath)} → {p.TargetOrganizerName}"));
            string suffix = plan.Count > 5 ? " …" : string.Empty;
            ShowAutoOrganizeStatus(AppStrings.Format("AutoOrganizePreviewFormat", plan.Count, sample + suffix));
        }
        catch (Exception ex)
        {
            if (!_closingPermanently) ShowError(AppStrings.Get("AutoOrganizeTitle"), ex.Message);
        }
        finally
        {
            if (!_closingPermanently) SetAutoOrganizeBusy(false);
        }
    }

    private async void RunAutoOrganizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoOrganizeBusy) return;
        SetAutoOrganizeBusy(true);
        try
        {
            await CommitPendingAutoOrganizeDraftsAsync();
            AutoOrganizeRunSummary summary = await _host.RunAutoOrganizeAsync();
            if (_closingPermanently) return;
            ShowAutoOrganizeStatus(AppStrings.Format("AutoOrganizeRunResultFormat", summary.MovedCount, summary.ShortcutCount, summary.FailedCount));
        }
        catch (Exception ex)
        {
            if (!_closingPermanently) ShowError(AppStrings.Get("AutoOrganizeTitle"), ex.Message);
        }
        finally
        {
            if (!_closingPermanently) SetAutoOrganizeBusy(false);
        }
    }

    private async void UndoAutoOrganizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoOrganizeBusy) return;
        SetAutoOrganizeBusy(true);
        try
        {
            AutoOrganizeUndoSummary summary = await _host.UndoAutoOrganizeAsync();
            if (_closingPermanently) return;
            ShowAutoOrganizeStatus(AppStrings.Format("AutoOrganizeUndoResultFormat", summary.RestoredCount, summary.SkippedCount, summary.FailedCount));
        }
        catch (Exception ex)
        {
            if (!_closingPermanently) ShowError(AppStrings.Get("AutoOrganizeTitle"), ex.Message);
        }
        finally
        {
            if (!_closingPermanently) SetAutoOrganizeBusy(false);
        }
    }

    private void ShowAutoOrganizeStatus(string message)
    {
        AutoOrganizeStatusText.Text = message;
        AutoOrganizeStatusText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}

public sealed class FlowWrapPanel : Panel
{
    private double _horizontalSpacing = 8;
    private double _verticalSpacing = 8;

    public double HorizontalSpacing
    {
        get => _horizontalSpacing;
        set
        {
            double next = Math.Max(0, value);
            if (Math.Abs(_horizontalSpacing - next) < 0.01) return;
            _horizontalSpacing = next;
            InvalidateMeasure();
        }
    }

    public double VerticalSpacing
    {
        get => _verticalSpacing;
        set
        {
            double next = Math.Max(0, value);
            if (Math.Abs(_verticalSpacing - next) < 0.01) return;
            _verticalSpacing = next;
            InvalidateMeasure();
        }
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        double maxWidth = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width);
        double lineWidth = 0;
        double lineHeight = 0;
        double totalWidth = 0;
        double totalHeight = 0;
        bool hasVisible = false;

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Windows.Foundation.Size(maxWidth, double.PositiveInfinity));
            Windows.Foundation.Size desired = child.DesiredSize;
            double itemWidth = Math.Ceiling(desired.Width);
            double itemHeight = Math.Ceiling(desired.Height);

            if (lineWidth > 0 && lineWidth + _horizontalSpacing + itemWidth > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight + _verticalSpacing;
                lineWidth = itemWidth;
                lineHeight = itemHeight;
            }
            else
            {
                if (lineWidth > 0) lineWidth += _horizontalSpacing;
                lineWidth += itemWidth;
                lineHeight = Math.Max(lineHeight, itemHeight);
            }
            hasVisible = true;
        }

        if (hasVisible)
        {
            totalWidth = Math.Max(totalWidth, lineWidth);
            totalHeight += lineHeight;
        }

        return new Windows.Foundation.Size(totalWidth, totalHeight);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        double maxWidth = Math.Max(0, finalSize.Width);
        double x = 0;
        double y = 0;
        double lineHeight = 0;

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            Windows.Foundation.Size desired = child.DesiredSize;
            double desiredWidth = Math.Ceiling(desired.Width);
            double itemWidth = maxWidth > 0 ? Math.Min(desiredWidth, maxWidth) : desiredWidth;
            double itemHeight = Math.Ceiling(desired.Height);

            if (x > 0 && x + _horizontalSpacing + itemWidth > maxWidth)
            {
                x = 0;
                y += lineHeight + _verticalSpacing;
                lineHeight = 0;
            }
            else if (x > 0)
            {
                x += _horizontalSpacing;
            }

            child.Arrange(new Windows.Foundation.Rect(x, y, itemWidth, itemHeight));
            x += itemWidth;
            lineHeight = Math.Max(lineHeight, itemHeight);
        }

        return new Windows.Foundation.Size(finalSize.Width, Math.Max(finalSize.Height, y + lineHeight));
    }
}
