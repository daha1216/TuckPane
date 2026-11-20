using System.Runtime.InteropServices;

namespace TuckPane.Services;

/// <summary>
/// Provides Windows Shell IFileOperation move/delete fallback (enabling seamless UAC-brokered
/// relocation of protected Public Desktop shortcuts) and Win32 reparse-tag inspection for
/// Windows 11 OneDrive Cloud files and Store App Execution Aliases.
/// </summary>
internal static class ShellFileOperationService
{
    private const uint CloudReparseTagMask = 0xFFFF0FFF;
    private const uint CloudReparseTagBase = 0x9000001A;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    internal static bool IsDesktopOrigin(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string? parent = Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(parent)) return false;
            string normalizedParent = parent.TrimEnd(Path.DirectorySeparatorChar);

            string userDesktop = AppPaths.DesktopRoot.TrimEnd(Path.DirectorySeparatorChar);
            if (!string.IsNullOrWhiteSpace(userDesktop) &&
                normalizedParent.Equals(userDesktop, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                .TrimEnd(Path.DirectorySeparatorChar);
            return !string.IsNullOrWhiteSpace(publicDesktop) &&
                normalizedParent.Equals(publicDesktop, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsPublicDesktopPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                .TrimEnd(Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(publicDesktop)) return false;
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            return full.StartsWith(publicDesktop + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true ONLY for safe Microsoft Windows 11 reparse points (OneDrive cloud placeholders
    /// and Windows Store App Execution Aliases). Always returns false for symlinks and junctions.
    /// </summary>
    internal static bool IsSafeCloudOrAppAliasReparsePoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            IntPtr handle = NativeMethods.FindFirstFile(normalized, out NativeMethods.WIN32_FIND_DATAW data);
            if (handle == InvalidHandleValue || handle == IntPtr.Zero)
                return false;
            try
            {
                uint tag = data.dwReserved0;
                if (tag is NativeMethods.IO_REPARSE_TAG_SYMLINK or NativeMethods.IO_REPARSE_TAG_MOUNT_POINT)
                    return false;
                if (tag == NativeMethods.IO_REPARSE_TAG_APPEXECLINK)
                    return true;
                return (tag & CloudReparseTagMask) == CloudReparseTagBase;
            }
            finally
            {
                _ = NativeMethods.FindClose(handle);
            }
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryClearReadOnlyAttribute(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) == 0) return false;
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryShellMove(string sourcePath, string destinationPath, out bool userCancelled)
    {
        bool moved = false;
        bool cancelled = false;
        RunOnSta(() =>
        {
            moved = TryShellMoveCore(sourcePath, destinationPath, out cancelled);
        });
        userCancelled = cancelled;
        return moved;
    }

    internal static bool TryShellDelete(string sourcePath, out bool userCancelled)
    {
        bool deleted = false;
        bool cancelled = false;
        RunOnSta(() =>
        {
            deleted = TryShellDeleteCore(sourcePath, out cancelled);
        });
        userCancelled = cancelled;
        return deleted;
    }

    private static bool TryShellMoveCore(string sourcePath, string destinationPath, out bool userCancelled)
    {
        userCancelled = false;
        string? destinationFolder = Path.GetDirectoryName(destinationPath);
        string destinationName = Path.GetFileName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationFolder) || string.IsNullOrWhiteSpace(destinationName))
            return false;

        object? opObject = null;
        NativeMethods.IShellItem? sourceItem = null;
        NativeMethods.IShellItem? destFolderItem = null;
        try
        {
            Directory.CreateDirectory(destinationFolder);
            Guid shellItemIid = typeof(NativeMethods.IShellItem).GUID;
            if (NativeMethods.SHCreateItemFromParsingName(sourcePath, IntPtr.Zero, in shellItemIid, out sourceItem) < 0 ||
                sourceItem is null)
            {
                return false;
            }
            if (NativeMethods.SHCreateItemFromParsingName(destinationFolder, IntPtr.Zero, in shellItemIid, out destFolderItem) < 0 ||
                destFolderItem is null)
            {
                return false;
            }

            opObject = new NativeMethods.FileOperation();
            var op = (NativeMethods.IFileOperation)opObject;
            uint flags = NativeMethods.FOF_ALLOWUNDO |
                         NativeMethods.FOF_NOCONFIRMATION |
                         NativeMethods.FOF_NOERRORUI |
                         NativeMethods.FOFX_NOCOPYHOOKS |
                         NativeMethods.FOFX_ADDUNDORECORD;
            if (op.SetOperationFlags(flags) < 0) return false;
            if (op.MoveItem(sourceItem, destFolderItem, destinationName, IntPtr.Zero) < 0) return false;

            int hr = op.PerformOperations();
            _ = op.GetAnyOperationsAborted(out bool aborted);
            if (aborted || hr == unchecked((int)0x800704C7) || hr == unchecked((int)0x80270000))
            {
                userCancelled = true;
                return false;
            }

            return hr >= 0 && (File.Exists(destinationPath) || Directory.Exists(destinationPath)) &&
                !File.Exists(sourcePath) && !Directory.Exists(sourcePath);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"IFileOperation 移动失败：{sourcePath}", ex);
            return false;
        }
        finally
        {
            ReleaseCom(destFolderItem);
            ReleaseCom(sourceItem);
            ReleaseCom(opObject);
        }
    }

    private static bool TryShellDeleteCore(string sourcePath, out bool userCancelled)
    {
        userCancelled = false;
        object? opObject = null;
        NativeMethods.IShellItem? sourceItem = null;
        try
        {
            Guid shellItemIid = typeof(NativeMethods.IShellItem).GUID;
            if (NativeMethods.SHCreateItemFromParsingName(sourcePath, IntPtr.Zero, in shellItemIid, out sourceItem) < 0 ||
                sourceItem is null)
            {
                return false;
            }

            opObject = new NativeMethods.FileOperation();
            var op = (NativeMethods.IFileOperation)opObject;
            uint flags = NativeMethods.FOF_NOCONFIRMATION |
                         NativeMethods.FOF_NOERRORUI |
                         NativeMethods.FOFX_NOCOPYHOOKS;
            if (op.SetOperationFlags(flags) < 0) return false;
            if (op.DeleteItem(sourceItem, IntPtr.Zero) < 0) return false;

            int hr = op.PerformOperations();
            _ = op.GetAnyOperationsAborted(out bool aborted);
            if (aborted || hr == unchecked((int)0x800704C7) || hr == unchecked((int)0x80270000))
            {
                userCancelled = true;
                return false;
            }

            return hr >= 0 && !File.Exists(sourcePath) && !Directory.Exists(sourcePath);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"IFileOperation 清理受保护源项失败：{sourcePath}", ex);
            return false;
        }
        finally
        {
            ReleaseCom(sourceItem);
            ReleaseCom(opObject);
        }
    }

    private static void RunOnSta(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            action();
            return;
        }

        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        })
        {
            IsBackground = true,
            Name = "TuckPane Shell FileOperation"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured is not null) throw captured;
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            _ = Marshal.FinalReleaseComObject(value);
    }
}
