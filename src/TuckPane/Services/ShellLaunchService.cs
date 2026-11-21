using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TuckPane.Services;

internal sealed class ShellLaunchService : IDisposable
{
    private readonly object _sync = new();
    private readonly BlockingCollection<(string Path, TaskCompletionSource Completion)> _requests = new(16);
    private readonly Dictionary<string, TaskCompletionSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _launch;
    private Thread? _worker;
    private bool _disposed;

    internal ShellLaunchService(Action<string>? launch = null) => _launch = launch ?? LaunchPathWithShell;

    internal Task OpenAsync(string path)
    {
        string key = Path.GetFullPath(path);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending.TryGetValue(key, out var existing)) return existing.Task;
            if (_pending.Count >= 16) throw new InvalidOperationException(AppStrings.Get("LaunchQueueFull"));
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(key, completion);
            _requests.Add((key, completion));
            if (_worker is null)
            {
                _worker = new Thread(Work) { IsBackground = true, Name = "TuckPane Shell launch" };
                _worker.SetApartmentState(ApartmentState.STA);
                _worker.Start();
            }
            return completion.Task;
        }
    }

    internal static void LaunchPathWithShell(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string extension = Path.GetExtension(fullPath);

        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            LaunchShortcut(fullPath);
            return;
        }

        if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            LaunchInternetShortcut(fullPath);
            return;
        }

        string workingDir = ResolveFallbackDirectory(fullPath);
        uint mask = NativeMethods.SEE_MASK_INVOKEIDLIST |
                    NativeMethods.SEE_MASK_FLAG_LOG_USAGE |
                    NativeMethods.SEE_MASK_NOASYNC;
        if (!TryShellExecute(fullPath, arguments: null, workingDir, verb: null, mask, NativeMethods.SW_SHOWNORMAL, out int error))
            ThrowUnlessCancelled(error);
    }

    private static void LaunchShortcut(string shortcutPath)
    {
        ShortcutInspection? inspection = ShortcutIntegrityService.InspectLink(shortcutPath);
        string workingDir = inspection?.EffectiveWorkingDirectory ?? ResolveFallbackDirectory(shortcutPath);
        int showCmd = inspection?.ShowCommand > 0 ? inspection.ShowCommand : NativeMethods.SW_SHOWNORMAL;

        if (inspection is null || inspection.IsVirtualShellTarget || inspection.IsMsiAdvertised)
        {
            // UWP / MSIX (shell:AppsFolder), Control Panel PIDLs, and MSI Advertised shortcuts
            // must be invoked via SEE_MASK_INVOKEIDLIST so Windows Shell activates them natively.
            uint virtualMask = NativeMethods.SEE_MASK_INVOKEIDLIST |
                               NativeMethods.SEE_MASK_FLAG_LOG_USAGE |
                               NativeMethods.SEE_MASK_NOASYNC;
            if (!TryShellExecute(shortcutPath, arguments: null, workingDir, verb: null, virtualMask, showCmd, out int virtualErr))
                ThrowUnlessCancelled(virtualErr);
            return;
        }

        // Standard filesystem .lnk: explicitly supply lpDirectory so the launched app NEVER
        // inherits System32 (at startup) or TuckPane's install directory!
        uint linkMask = NativeMethods.SEE_MASK_FLAG_LOG_USAGE | NativeMethods.SEE_MASK_NOASYNC;
        if (TryShellExecute(shortcutPath, arguments: null, workingDir, verb: null, linkMask, showCmd, out int primaryError))
            return;

        if (primaryError == NativeMethods.ERROR_CANCELLED)
            return;

        // Self-healing fallback: if a relocated/aged .lnk failed to launch directly,
        // invoke its resolved target directly with original arguments, working dir, and elevation verb.
        string target = inspection.ExpandedTargetPath;
        if (!string.IsNullOrWhiteSpace(target) && (File.Exists(target) || Directory.Exists(target)))
        {
            string? verb = inspection.RequiresElevation ? "runas" : null;
            string? args = string.IsNullOrWhiteSpace(inspection.Arguments) ? null : inspection.Arguments;
            if (TryShellExecute(target, args, workingDir, verb, linkMask, showCmd, out int fallbackError))
            {
                AppLogger.Info($"快捷方式直启回退成功：{shortcutPath} -> {target}");
                return;
            }
            ThrowUnlessCancelled(fallbackError);
            return;
        }

        ThrowUnlessCancelled(primaryError);
    }

    private static void LaunchInternetShortcut(string urlPath)
    {
        string workingDir = ResolveFallbackDirectory(urlPath);
        uint mask = NativeMethods.SEE_MASK_INVOKEIDLIST |
                    NativeMethods.SEE_MASK_FLAG_LOG_USAGE |
                    NativeMethods.SEE_MASK_NOASYNC;
        if (TryShellExecute(urlPath, arguments: null, workingDir, verb: null, mask, NativeMethods.SW_SHOWNORMAL, out int error))
            return;

        if (error == NativeMethods.ERROR_CANCELLED)
            return;

        // Fallback for machines with damaged .url shell associations (e.g., Steam / browser URLs).
        if (ShortcutIntegrityService.TryReadInternetShortcutUrl(urlPath, out string url, out string? urlWorkDir))
        {
            if (TryShellExecute(url, arguments: null, urlWorkDir ?? workingDir, verb: null, NativeMethods.SEE_MASK_FLAG_LOG_USAGE | NativeMethods.SEE_MASK_NOASYNC, NativeMethods.SW_SHOWNORMAL, out int urlErr))
                return;
            if (urlErr == NativeMethods.ERROR_CANCELLED)
                return;
        }

        ThrowUnlessCancelled(error);
    }

    private static bool TryShellExecute(
        string file,
        string? arguments,
        string? workingDirectory,
        string? verb,
        uint mask,
        int showCommand,
        out int win32Error)
    {
        var info = new NativeMethods.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.SHELLEXECUTEINFO>(),
            fMask = mask,
            lpVerb = verb,
            lpFile = file,
            lpParameters = arguments,
            lpDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
            nShow = showCommand > 0 ? showCommand : NativeMethods.SW_SHOWNORMAL
        };

        if (NativeMethods.ShellExecuteEx(ref info))
        {
            win32Error = 0;
            return true;
        }

        win32Error = Marshal.GetLastWin32Error();
        return false;
    }

    private static void ThrowUnlessCancelled(int win32Error)
    {
        if (win32Error is 0 or NativeMethods.ERROR_CANCELLED)
            return;
        throw new Win32Exception(win32Error);
    }

    private static string ResolveFallbackDirectory(string fullPath)
    {
        if (Directory.Exists(fullPath))
            return fullPath;
        string? parent = Path.GetDirectoryName(fullPath);
        return !string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent)
            ? parent
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private void Work()
    {
        foreach (var request in _requests.GetConsumingEnumerable())
        {
            lock (_sync) { if (_disposed) { request.Completion.TrySetCanceled(); continue; } }
            Exception? error = null;
            using var operation = AppLogger.Begin(DiagnosticArea.Shell);
            try { _launch(request.Path); operation.Complete(); }
            catch (Exception ex) { error = ex; operation.Fail(ex); }
            lock (_sync) _pending.Remove(request.Path);
            if (error is null) request.Completion.TrySetResult();
            else request.Completion.TrySetException(error);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _requests.CompleteAdding();
            while (_requests.TryTake(out var request)) request.Completion.TrySetCanceled();
            _pending.Clear();
        }
        // A native Shell call cannot safely be aborted. Never join a stuck STA from the UI.
    }
}
