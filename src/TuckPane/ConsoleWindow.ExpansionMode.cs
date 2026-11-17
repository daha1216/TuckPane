using Microsoft.UI.Xaml.Controls;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private long _manageExpansionVersion;

    internal void RefreshExpansionModeSelection(Guid id)
    {
        if (_selectedId != id) return;
        bool loading = _loadingEditor;
        _loadingEditor = true;
        try
        {
            ManageForm.ExpansionCombo.SelectedIndex = (int)_host.GetRequestedExpansionMode(id);
            UpdateManageControls();
        }
        finally { _loadingEditor = loading; }
    }

    private async void ManageExpansionMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingEditor || _adjustingManageControls ||
            _selectedId is not Guid id || ManageForm.ExpansionCombo.SelectedIndex < 0) return;
        var mode = (OrganizerExpansionMode)ManageForm.ExpansionCombo.SelectedIndex;
        long version = ++_manageExpansionVersion;
        try
        {
            string? error = await _host.SetOrganizerExpansionModeAsync(id, mode,
                token => FlushPendingManageChangesAsync().WaitAsync(token));
            if (error is not null && _selectedId == id && version == _manageExpansionVersion)
                ShowError(AppStrings.Get("OrganizerModeErrorTitle"), error);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法切换收纳窗展开模式。", ex);
            if (_selectedId == id && version == _manageExpansionVersion)
                ShowError(AppStrings.Get("SaveConfigurationError"), ex.Message);
        }
        finally
        {
            if (version == _manageExpansionVersion) RefreshExpansionModeSelection(id);
        }
    }
}
