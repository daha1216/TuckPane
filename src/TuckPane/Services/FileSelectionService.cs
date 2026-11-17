using System.Runtime.InteropServices;

namespace TuckPane.Services;

internal static class FileSelectionService
{
    private const uint ForceFileSystem = 0x40;
    private const uint PickFolders = 0x20;
    private const uint AllowMultiSelect = 0x200;
    private const uint PathMustExist = 0x800;
    private const uint FileMustExist = 0x1000;
    private const uint NoDereferenceLinks = 0x100000;
    private const int Cancelled = unchecked((int)0x800704C7);

    // Called on the owner window's STA. Show runs the standard Windows modal loop.
    internal static string? PickSingleFile(IntPtr owner) => PickSingleItem(owner, folder: false);
    internal static string? PickSingleFolder(IntPtr owner) => PickSingleItem(owner, folder: true);

    private static string? PickSingleItem(IntPtr owner, bool folder)
    {
        object instance = new NativeMethods.FileOpenDialog();
        NativeMethods.IShellItem? result = null;
        try
        {
            var dialog = (NativeMethods.IFileOpenDialog)instance;
            dialog.GetOptions(out uint options);
            dialog.SetOptions((options & ~(AllowMultiSelect | PickFolders | FileMustExist)) | ForceFileSystem |
                PathMustExist | NoDereferenceLinks | (folder ? PickFolders : FileMustExist));
            dialog.SetTitle(AppStrings.Get("ContextAddItem"));
            dialog.SetOkButtonLabel(AppStrings.Get("ContextAddItem"));
            int hr = dialog.Show(owner);
            if (hr == Cancelled) return null;
            Marshal.ThrowExceptionForHR(hr);
            dialog.GetResult(out result);
            Marshal.ThrowExceptionForHR(result.GetDisplayName(0x80058000, out IntPtr path)); // SIGDN_FILESYSPATH
            try { return Marshal.PtrToStringUni(path); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        finally
        {
            if (result is not null) Marshal.FinalReleaseComObject(result);
            Marshal.FinalReleaseComObject(instance);
        }
    }
}
