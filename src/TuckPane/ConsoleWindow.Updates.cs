using Microsoft.UI.Xaml;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private void InitializeUpdates()
    {
        _host.Updates.Changed += UpdatesChanged;
        RefreshUpdates();
    }

    private void UpdatesChanged()
    {
        if (!_closingPermanently) DispatcherQueue.TryEnqueue(() => { if (!_closingPermanently) RefreshUpdates(); });
    }

    private void RefreshUpdates()
    {
        AppUpdateService update = _host.Updates;
        UpdateNavItem.Content = AppStrings.Get("NavUpdate");
        UpdateVersionText.Text = AppStrings.Format("UpdateCurrentVersion", update.CurrentVersion,
            AppStrings.Get(update.Installed ? "UpdateInstalledEdition" : "UpdatePortableEdition"));
        double progress = Math.Clamp(update.Progress, 0, 100);
        string statusBase = AppStrings.Get(update.StatusKey);
        if (update.Downloading)
        {
            UpdateStatusText.Text = update.Available is { } downloadingRelease
                ? $"{statusBase}  {Math.Round(progress):0}%  ({downloadingRelease.Version})"
                : $"{statusBase}  {Math.Round(progress):0}%";
        }
        else
        {
            UpdateStatusText.Text = statusBase + (update.Available is { } release ? "  " + release.Version : "");
        }
        UpdateLastCheckText.Text = AppStrings.Format("UpdateLastCheck", update.LastCheck is { } last ? AppStrings.FormatDate(last) : AppStrings.Get("UpdateNeverChecked"));
        UpdateBadge.Visibility = update.Available is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateCheckButton.IsEnabled = !update.Busy;
        bool canInstall = !update.Busy && update.Available is not null;
        UpdateInstallButton.IsEnabled = canInstall;
        if (canInstall && Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? accentResource) && accentResource is Style accentStyle)
        {
            UpdateInstallButton.Style = accentStyle;
        }
        else
        {
            UpdateInstallButton.ClearValue(FrameworkElement.StyleProperty);
        }
        UpdateCancelButton.Visibility = update.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressRow.Visibility = update.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Visibility = update.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Value = progress;
        UpdateProgressText.Text = update.Downloading ? $"{Math.Round(progress):0}%" : string.Empty;
        UpdateErrorText.Text = update.Error ?? "";
        UpdateErrorText.Visibility = string.IsNullOrEmpty(update.Error) ? Visibility.Collapsed : Visibility.Visible;
        UpdateNotesText.Text = update.Available?.Notes ?? AppStrings.Get("UpdateNoNotes");
        string recovery = string.Join(Environment.NewLine, new[]
        {
            update.PreviousResult,
            string.IsNullOrEmpty(update.BackupPath) ? null : AppStrings.Format("UpdateBackupLocation", update.BackupPath)
        }.Where(s => !string.IsNullOrEmpty(s)));
        UpdateRecoveryText.Text = recovery;
        Visibility recoveryVisibility = string.IsNullOrWhiteSpace(recovery) ? Visibility.Collapsed : Visibility.Visible;
        UpdateRecoveryText.Visibility = recoveryVisibility;
        UpdateRecoveryRow.Visibility = recoveryVisibility;
    }

    private async void UpdateCheck_Click(object sender, RoutedEventArgs e) => await _host.Updates.CheckAsync(true);
    private async void UpdateInstall_Click(object sender, RoutedEventArgs e) =>
        await _host.Updates.DownloadAndInstallAsync(_host.ExitForUpdateAsync, () =>
            _host.State.Organizers.Select(AppPaths.ResolveStoragePath).Concat(new[] { AppPaths.UserRoot, AppPaths.LocalRoot }).ToArray());
    private void UpdateCancel_Click(object sender, RoutedEventArgs e) => _host.Updates.CancelDownload();
}
