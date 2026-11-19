using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace TuckPane.Services;

/// <summary>
/// Standard Windows Shell item commands (Reveal in File Explorer, native Properties
/// dialog, and Copy Path) executed safely without spawning console windows.
/// </summary>
internal static class ShellItemCommands
{
    internal static void RevealInExplorer(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        string normalized = Path.GetFullPath(fullPath);
        if (!File.Exists(normalized) && !Directory.Exists(normalized))
            throw new FileNotFoundException(AppStrings.Get("ShellDragMissing"), normalized);

        var start = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"/select,\"{normalized}\""
        };
        using Process? _ = Process.Start(start);
    }

    internal static void ShowProperties(IntPtr ownerHwnd, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        string normalized = Path.GetFullPath(fullPath);
        if (!File.Exists(normalized) && !Directory.Exists(normalized))
            throw new FileNotFoundException(AppStrings.Get("ShellDragMissing"), normalized);

        var info = new NativeMethods.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.SHELLEXECUTEINFO>(),
            fMask = NativeMethods.SEE_MASK_INVOKEIDLIST,
            hwnd = ownerHwnd,
            lpVerb = "properties",
            lpFile = normalized,
            nShow = NativeMethods.SW_SHOWNORMAL
        };
        if (!NativeMethods.ShellExecuteEx(ref info))
        {
            int error = Marshal.GetLastWin32Error();
            throw new System.ComponentModel.Win32Exception(error);
        }
    }

    internal static void CopyPathToClipboard(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        var package = new DataPackage();
        package.SetText(Path.GetFullPath(fullPath));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
