using System.Runtime.InteropServices;

namespace TuckPane.Services;

/// <summary>
/// Reads the real filesystem paths of icons currently marquee-selected on the
/// native Windows Desktop (SysListView32) via documented IShellWindows / IFolderView
/// COM interfaces so users can pack selected desktop icons into an organizer in 1 click.
/// </summary>
internal static class DesktopSelectionService
{
    private const int ShellWindowClassDesktop = 8; // SWC_DESKTOP
    private const int ShellWindowOptionNeedDispatch = 1; // SWFO_NEEDDISPATCH
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserInterface = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid ShellItemArrayInterface = new("B63EA76D-1F85-456F-A19C-48159EFA858B");

    internal static string[] ReadSelectedDesktopPaths()
    {
        if (AppPaths.IsTestMode) return [];

        object? shellWindowsObj = null;
        object? desktopWindowObj = null;
        IntPtr browserPtr = IntPtr.Zero;
        object? browserObj = null;
        NativeMethods.IShellView? shellView = null;
        IntPtr itemArrayPtr = IntPtr.Zero;
        object? itemArrayObj = null;

        try
        {
            Type? type = Type.GetTypeFromCLSID(ShellWindowsClass);
            if (type is null) return [];
            shellWindowsObj = Activator.CreateInstance(type);
            if (shellWindowsObj is not NativeMethods.IShellWindows shellWindows) return [];

            object empty1 = 0;
            object empty2 = 0;
            desktopWindowObj = shellWindows.FindWindowSW(
                ref empty1,
                ref empty2,
                ShellWindowClassDesktop,
                out _,
                ShellWindowOptionNeedDispatch);
            if (desktopWindowObj is not NativeMethods.IServiceProvider serviceProvider) return [];

            Guid sid = TopLevelBrowserService;
            Guid iid = ShellBrowserInterface;
            if (serviceProvider.QueryService(ref sid, ref iid, out browserPtr) < 0 || browserPtr == IntPtr.Zero)
                return [];

            browserObj = Marshal.GetObjectForIUnknown(browserPtr);
            if (browserObj is not NativeMethods.IShellBrowser browser) return [];
            if (browser.QueryActiveShellView(out shellView) < 0 || shellView is null) return [];
            if (shellView is not NativeMethods.IFolderView folderView) return [];

            Guid arrayIid = ShellItemArrayInterface;
            if (folderView.Items(NativeMethods.SVGIO_SELECTION, ref arrayIid, out itemArrayPtr) < 0 ||
                itemArrayPtr == IntPtr.Zero)
            {
                return [];
            }

            itemArrayObj = Marshal.GetObjectForIUnknown(itemArrayPtr);
            if (itemArrayObj is not NativeMethods.IShellItemArray array) return [];
            if (array.GetCount(out uint count) < 0 || count == 0) return [];

            var paths = new List<string>((int)Math.Min(count, 512));
            for (uint i = 0; i < count; i++)
            {
                if (array.GetItemAt(i, out NativeMethods.IShellItem item) < 0 || item is null) continue;
                IntPtr pathPtr = IntPtr.Zero;
                try
                {
                    // Virtual shell icons (This PC, Recycle Bin, Control Panel) return failure here
                    // and are skipped automatically so only real files/folders/shortcuts are imported.
                    if (item.GetDisplayName(NativeMethods.SIGDN_FILESYSPATH, out pathPtr) >= 0 &&
                        pathPtr != IntPtr.Zero)
                    {
                        string? path = Marshal.PtrToStringUni(pathPtr);
                        if (!string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
                            paths.Add(path);
                    }
                }
                finally
                {
                    if (pathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPtr);
                    if (Marshal.IsComObject(item)) _ = Marshal.FinalReleaseComObject(item);
                }
            }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return [];
        }
        finally
        {
            if (itemArrayObj is not null && Marshal.IsComObject(itemArrayObj)) _ = Marshal.ReleaseComObject(itemArrayObj);
            if (itemArrayPtr != IntPtr.Zero) _ = Marshal.Release(itemArrayPtr);
            if (shellView is not null && Marshal.IsComObject(shellView)) _ = Marshal.ReleaseComObject(shellView);
            if (browserObj is not null && Marshal.IsComObject(browserObj)) _ = Marshal.ReleaseComObject(browserObj);
            if (browserPtr != IntPtr.Zero) _ = Marshal.Release(browserPtr);
            if (desktopWindowObj is not null && Marshal.IsComObject(desktopWindowObj)) _ = Marshal.ReleaseComObject(desktopWindowObj);
            if (shellWindowsObj is not null && Marshal.IsComObject(shellWindowsObj)) _ = Marshal.ReleaseComObject(shellWindowsObj);
        }
    }
}
