using Microsoft.Win32;

namespace TuckPane.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TuckPane";
    private const string LegacyValueName = "GlassFolder";

    public static void Apply(bool enabled)
    {
        if (AppPaths.IsTestMode) return;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            string executable = Environment.ProcessPath ?? throw new InvalidOperationException(AppStrings.Get("StartupPathUnavailable"));
            key.SetValue(ValueName, $"\"{executable}\" --startup", RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法更新开机自启注册表项。", ex);
        }
    }
}
