using Microsoft.UI.Xaml.Controls;
using TuckPane.Services;

namespace TuckPane;

internal sealed class ConsoleSettingBinder
{
    private int _suppressDepth;

    internal bool IsSuppressed => _suppressDepth > 0;

    internal Scope Suppress()
    {
        _suppressDepth++;
        return new Scope(this);
    }

    internal void SetToggle(ToggleSwitch? toggle, bool isOn)
    {
        if (toggle is null) return;
        using var _ = Suppress();
        toggle.IsOn = isOn;
    }

    internal void SetComboIndex(ComboBox? combo, int index)
    {
        if (combo is null) return;
        using var _ = Suppress();
        combo.SelectedIndex = index;
    }

    internal async Task RunToggleAsync(
        bool componentReady,
        Func<Task> persistAsync,
        Action revertUi,
        string logMessage,
        string errorTitleKey,
        Action<string, string> showError)
    {
        if (!componentReady || IsSuppressed) return;
        try
        {
            await persistAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error(logMessage, ex);
            revertUi();
            showError(AppStrings.Get(errorTitleKey), ex.Message);
        }
    }

    internal async Task RunComboAsync(
        bool componentReady,
        ComboBox? combo,
        Func<int, Task> persistAsync,
        Action revertUi,
        string? logMessage,
        string errorTitleKey,
        Action<string, string> showError)
    {
        if (!componentReady || IsSuppressed || combo is null || combo.SelectedIndex < 0) return;
        try
        {
            await persistAsync(combo.SelectedIndex);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(logMessage)) AppLogger.Error(logMessage, ex);
            revertUi();
            showError(AppStrings.Get(errorTitleKey), ex.Message);
        }
    }

    internal struct Scope : IDisposable
    {
        private ConsoleSettingBinder? _owner;
        internal Scope(ConsoleSettingBinder owner) => _owner = owner;
        public void Dispose()
        {
            if (_owner is null) return;
            _owner._suppressDepth--;
            _owner = null;
        }
    }
}
