using System.Runtime.InteropServices;

namespace TuckPane.Services;

internal static class FloatingDragNativeMethods
{
    internal const long WS_EX_TOPMOST = 0x00000008L;
    internal const uint PM_REMOVE = 0x0001;
    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int MDT_EFFECTIVE_DPI = 0;
    internal const int DWMWA_EXCLUDED_FROM_PEEK = 12;

    internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    internal const int SWC_DESKTOP = 8;
    internal const int SWFO_NEEDDISPATCH = 1;
    internal const uint SVGIO_ALLVIEW = 0x00000002;
    internal const uint FWF_AUTOARRANGE = 0x00000001;
    internal const uint SVSI_POSITIONITEM = 0x00000080;
    internal const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    internal const uint SIGDN_FILESYSPATH = 0x80058000;

    internal static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    internal static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    internal static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    internal static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    internal static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref NativeMethods.MSG lpMsg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static extern IntPtr DispatchMessage(ref NativeMethods.MSG lpMsg);

    [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateLayeredWindowPositionOnly(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref NativeMethods.POINT pptDst,
        IntPtr psize,
        IntPtr hdcSrc,
        IntPtr pptSrc,
        uint crKey,
        IntPtr pblend,
        uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateLayeredWindowPositionAndAlpha(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref NativeMethods.POINT pptDst,
        IntPtr psize,
        IntPtr hdcSrc,
        IntPtr pptSrc,
        uint crKey,
        ref NativeMethods.BLENDFUNCTION pblend,
        uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("shell32.dll")]
    internal static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll", PreserveSig = true)]
    internal static extern int SHCreateItemWithParent(
        IntPtr pidlParent,
        IntPtr psfParent,
        IntPtr pidl,
        ref Guid riid,
        out IntPtr ppvItem);

    [ComImport]
    [Guid("1AF3A467-214F-4298-908E-06B03E0B39F9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFolderView2
    {
        // IFolderView (slots 3..16)
        [PreserveSig] int GetCurrentViewMode(out uint viewMode);
        [PreserveSig] int SetCurrentViewMode(uint viewMode);
        [PreserveSig] int GetFolder(ref Guid interfaceId, out IntPtr folder);
        [PreserveSig] int Item(int index, out IntPtr itemIdList);
        [PreserveSig] int ItemCount(uint flags, out int count);
        [PreserveSig] int Items(uint flags, ref Guid interfaceId, out IntPtr items);
        [PreserveSig] int GetSelectionMarkedItem(out int index);
        [PreserveSig] int GetFocusedItem(out int index);
        [PreserveSig] int GetItemPosition(IntPtr itemIdList, out NativeMethods.POINT point);
        [PreserveSig] int GetSpacing(ref NativeMethods.POINT point);
        [PreserveSig] int GetDefaultSpacing(out NativeMethods.POINT point);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int index, uint flags);
        [PreserveSig] int SelectAndPositionItems(
            uint count,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] itemIdLists,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] NativeMethods.POINT[] points,
            uint flags);

        // IFolderView2 (slots 17..41)
        [PreserveSig] int SetGroupBy(IntPtr key, [MarshalAs(UnmanagedType.Bool)] bool ascending);
        [PreserveSig] int GetGroupBy(IntPtr key, [MarshalAs(UnmanagedType.Bool)] out bool ascending);
        [PreserveSig] int SetViewProperty(IntPtr pidl, IntPtr propKey, IntPtr propVar);
        [PreserveSig] int GetViewProperty(IntPtr pidl, IntPtr propKey, IntPtr propVar);
        [PreserveSig] int SetTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string propList);
        [PreserveSig] int SetExtendedTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string propList);
        [PreserveSig] int SetText(int type, [MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int SetCurrentFolderFlags(uint mask, uint flags);
        [PreserveSig] int GetCurrentFolderFlags(out uint flags);
        [PreserveSig] int GetSortColumnCount(out int columns);
        [PreserveSig] int SetSortColumns(IntPtr sortColumns, int columns);
        [PreserveSig] int GetSortColumns(IntPtr sortColumns, int columns);
        [PreserveSig] int GetItem(int itemIndex, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetVisibleItem(int start, [MarshalAs(UnmanagedType.Bool)] bool previous, out int item);
        [PreserveSig] int GetSelectedItem(int start, out int item);
        [PreserveSig] int GetSelection([MarshalAs(UnmanagedType.Bool)] bool noneImpliesFolder, out IntPtr itemArray);
        [PreserveSig] int GetSelectionState(IntPtr pidl, out uint flags);
        [PreserveSig] int InvokeVerbOnSelection([MarshalAs(UnmanagedType.LPStr)] string? verb);
        [PreserveSig] int SetViewModeAndIconSize(uint viewMode, int imageSize);
        [PreserveSig] int GetViewModeAndIconSize(out uint viewMode, out int imageSize);
        [PreserveSig] int SetGroupSubsetCount(uint visibleRows);
        [PreserveSig] int GetGroupSubsetCount(out uint visibleRows);
        [PreserveSig] int SetRedraw([MarshalAs(UnmanagedType.Bool)] bool redrawOn);
        [PreserveSig] int IsMoveInSameFolder();
        [PreserveSig] int DoRename();
    }
}
