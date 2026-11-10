using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TuckPane.Services;

/// <summary>Explorer verbs offered on a pane item: show it in its folder and open its Properties sheet.</summary>
internal static class ShellItemCommands
{
    private const uint SHOP_FILEPATH = 0x2;

    /// <summary>Opens an Explorer window on the item's parent folder with the item selected.</summary>
    internal static void Reveal(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException(AppStrings.Get("ShellDragMissing"), full);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = false,
            // explorer.exe parses its own command line; /select needs the quoted path in the same token.
            Arguments = "/select,\"" + full + "\""
        };
        using Process? _ = Process.Start(start);
    }

    /// <summary>Shows the standard Windows Properties sheet. The sheet is modeless and owned by the Shell.</summary>
    internal static void ShowProperties(IntPtr owner, string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException(AppStrings.Get("ShellDragMissing"), full);
        if (!SHObjectProperties(owner, SHOP_FILEPATH, full, null))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);
}
