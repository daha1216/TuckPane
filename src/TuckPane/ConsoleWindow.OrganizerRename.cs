using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TuckPane.Models;
using TuckPane.Services;
using Windows.System;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private bool _manageNameDirty;
    private Task<bool>? _manageRenameTask;

    private void ManageNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingEditor || _editing is null) return;
        _manageNameDirty = true;
        ManageForm.NameError.Visibility = string.IsNullOrWhiteSpace(ManageForm.NameInput.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void ManageNameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitManageNameAsync();
        }
        else if (e.Key == VirtualKey.Escape && _selectedId is Guid id)
        {
            e.Handled = true;
            _manageNameDirty = false;
            RefreshOrganizerName(id);
        }
    }

    private async void ManageNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_loadingEditor) await CommitManageNameAsync();
    }

    private Task<bool> CommitManageNameAsync()
    {
        if (_manageRenameTask is not null) return _manageRenameTask;
        if (!_manageNameDirty || _selectedId is not Guid id) return Task.FromResult(true);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manageRenameTask = completion.Task;
        _manageNameDirty = false;
        string requested = ManageForm.NameInput.Text;
        _ = CommitManageNameCoreAsync(id, requested, completion);
        return completion.Task;
    }

    private async Task CommitManageNameCoreAsync(Guid id, string requested, TaskCompletionSource<bool> completion)
    {
        bool saved = false;
        ManageForm.NameInput.IsEnabled = false;
        try
        {
            OrganizerDefinition? definition = _host.State.Organizers.FirstOrDefault(item => item.Id == id);
            if (definition is not null && definition.Name != requested.Trim())
                await _host.RenameOrganizerAsync(id, requested);
            saved = true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法重命名收纳窗。", ex);
            ShowError(AppStrings.Get("SaveConfigurationError"), ex.Message);
        }
        finally
        {
            RefreshOrganizerName(id);
            ManageForm.NameInput.IsEnabled = true;
            _manageRenameTask = null;
            completion.SetResult(saved);
        }
    }

    internal void RefreshOrganizerName(Guid id)
    {
        OrganizerDefinition? definition = _host.State.Organizers.FirstOrDefault(item => item.Id == id);
        if (definition is null) return;
        RefreshManageListItemMetadata(definition);
        RefreshAutoOrganizeTargetName(definition);
        if (_selectedId != id || _editing is null) return;
        _editing.Name = definition.Name;
        _editing.StorageAbsolutePath = definition.StorageAbsolutePath;
        _editing.StorageRelativePath = definition.StorageRelativePath;
        bool wasLoading = _loadingEditor;
        _loadingEditor = true;
        if (!_manageNameDirty) ManageForm.NameInput.Text = definition.Name;
        ManageForm.StoragePathInput.Text = AppPaths.ResolveStoragePath(definition);
        ManageForm.NameError.Visibility = Visibility.Collapsed;
        _loadingEditor = wasLoading;
    }
}
