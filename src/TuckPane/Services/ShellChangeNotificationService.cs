using System.Runtime.InteropServices;

namespace TuckPane.Services;

internal enum ShellMoveNotificationKind
{
    Rename,
    DeleteCreate
}

internal static class ShellChangeNotificationService
{
    private const uint RenameItem = 0x00000001;
    private const uint CreateItem = 0x00000002;
    private const uint DeleteItem = 0x00000004;
    private const uint MakeDirectory = 0x00000008;
    private const uint RemoveDirectory = 0x00000010;
    private const uint RenameFolder = 0x00020000;
    private const uint PathUnicode = 0x0005;
    private const uint Flush = 0x1000;

    internal static void NotifyMoved(string sourcePath, string destinationPath)
    {
        bool isDirectory = Directory.Exists(destinationPath);
        if (ClassifyMove(sourcePath, destinationPath) == ShellMoveNotificationKind.DeleteCreate)
        {
            SHChangeNotify(
                isDirectory ? RemoveDirectory : DeleteItem,
                PathUnicode | Flush,
                sourcePath,
                null);
            SHChangeNotify(
                isDirectory ? MakeDirectory : CreateItem,
                PathUnicode | Flush,
                destinationPath,
                null);
            return;
        }

        SHChangeNotify(
            isDirectory ? RenameFolder : RenameItem,
            PathUnicode | Flush,
            sourcePath,
            destinationPath);
    }

    internal static void NotifyCreated(string path)
    {
        SHChangeNotify(
            Directory.Exists(path) ? MakeDirectory : CreateItem,
            PathUnicode | Flush,
            path,
            null);
    }

    internal static ShellMoveNotificationKind ClassifyMove(string sourcePath, string destinationPath)
    {
        string? sourceParent = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        string? destinationParent = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        return string.Equals(sourceParent, destinationParent, StringComparison.OrdinalIgnoreCase)
            ? ShellMoveNotificationKind.Rename
            : ShellMoveNotificationKind.DeleteCreate;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        string item1,
        string? item2);
}
