using System.Runtime.InteropServices;
using System.Text;
using TuckPane.Models;

namespace TuckPane.Core;

public enum OrganizerSortCriterion
{
    NameAscending,
    NameDescending,
    TypeAscending,
    DateModifiedDescending,
    SizeDescending
}

internal sealed record SortableItemMetadata(
    string RelativeName,
    string DisplayName,
    WidgetItemKind Kind,
    string Extension,
    DateTimeOffset LastWriteUtc,
    long SizeBytes);

/// <summary>
/// Deterministic organizer item sorting rules that preserve folders/organizers first
/// where appropriate and break ties cleanly by Windows Explorer logical display name.
/// </summary>
internal static class OrganizerSortOrder
{
    internal static IReadOnlyList<string> Sort(
        IEnumerable<SortableItemMetadata> items,
        OrganizerSortCriterion criterion)
    {
        ArgumentNullException.ThrowIfNull(items);
        IEnumerable<SortableItemMetadata> ordered = criterion switch
        {
            OrganizerSortCriterion.NameAscending => items
                .OrderBy(item => IsContainerKind(item.Kind) ? 0 : 1)
                .ThenBy(item => item.DisplayName, NaturalNameComparer.Instance)
                .ThenBy(item => item.RelativeName, StringComparer.OrdinalIgnoreCase),

            OrganizerSortCriterion.NameDescending => items
                .OrderBy(item => IsContainerKind(item.Kind) ? 0 : 1)
                .ThenByDescending(item => item.DisplayName, NaturalNameComparer.Instance)
                .ThenByDescending(item => item.RelativeName, StringComparer.OrdinalIgnoreCase),

            OrganizerSortCriterion.TypeAscending => items
                .OrderBy(item => KindBucket(item.Kind))
                .ThenBy(item => item.Extension, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.DisplayName, NaturalNameComparer.Instance)
                .ThenBy(item => item.RelativeName, StringComparer.OrdinalIgnoreCase),

            OrganizerSortCriterion.DateModifiedDescending => items
                .OrderByDescending(item => item.LastWriteUtc)
                .ThenBy(item => item.DisplayName, NaturalNameComparer.Instance)
                .ThenBy(item => item.RelativeName, StringComparer.OrdinalIgnoreCase),

            OrganizerSortCriterion.SizeDescending => items
                .OrderBy(item => IsContainerKind(item.Kind) ? 1 : 0)
                .ThenByDescending(item => item.SizeBytes)
                .ThenBy(item => item.DisplayName, NaturalNameComparer.Instance)
                .ThenBy(item => item.RelativeName, StringComparer.OrdinalIgnoreCase),

            _ => items
        };
        return ordered.Select(item => item.RelativeName).ToArray();
    }

    internal static SortableItemMetadata InspectItem(WidgetItem item)
    {
        DateTimeOffset modified = DateTimeOffset.MinValue;
        long size = 0;
        string extension = Path.GetExtension(item.RelativeName);
        try
        {
            if (item.Kind == WidgetItemKind.Folder)
            {
                var dir = new DirectoryInfo(item.FullPath);
                if (dir.Exists) modified = new DateTimeOffset(dir.LastWriteTimeUtc, TimeSpan.Zero);
                extension = string.Empty;
            }
            else if (item.Kind != WidgetItemKind.Organizer && item.Kind != WidgetItemKind.Note)
            {
                var file = new FileInfo(item.FullPath);
                if (file.Exists)
                {
                    modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                    size = file.Length;
                }

                if (item.Kind == WidgetItemKind.Shortcut && file.Exists)
                {
                    if (TryResolveShortcutTarget(item.FullPath, out string? targetPath, out bool isDirectoryAttr))
                    {
                        if (!string.IsNullOrWhiteSpace(targetPath) &&
                            (Directory.Exists(targetPath) || (isDirectoryAttr && !File.Exists(targetPath))))
                        {
                            extension = "lnk:0-folder";
                        }
                        else if (!string.IsNullOrWhiteSpace(targetPath) && File.Exists(targetPath))
                        {
                            var targetFile = new FileInfo(targetPath);
                            size = targetFile.Length;
                            string targetExt = Path.GetExtension(targetPath).ToLowerInvariant();
                            extension = string.IsNullOrEmpty(targetExt) ? "lnk:1-file" : $"lnk:1-{targetExt}";
                        }
                        else if (!string.IsNullOrWhiteSpace(targetPath))
                        {
                            string targetExt = Path.GetExtension(targetPath).ToLowerInvariant();
                            extension = string.IsNullOrEmpty(targetExt) ? "lnk:2-shortcut" : $"lnk:1-{targetExt}";
                        }
                        else
                        {
                            extension = "lnk:2-shortcut";
                        }
                    }
                    else
                    {
                        extension = "lnk:2-shortcut";
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Keep safe defaults when an item disappears or is inaccessible.
        }
        return new SortableItemMetadata(item.RelativeName, item.Name, item.Kind, extension, modified, size);
    }

    private static bool TryResolveShortcutTarget(string lnkPath, out string? targetPath, out bool isDirectoryAttribute)
    {
        targetPath = null;
        isDirectoryAttribute = false;
        try
        {
            byte[] bytes = File.ReadAllBytes(lnkPath);
            if (bytes.Length < 76) return false;
            uint headerSize = BitConverter.ToUInt32(bytes, 0);
            if (headerSize != 0x0000004C) return false;

            uint linkFlags = BitConverter.ToUInt32(bytes, 20);
            uint fileAttributes = BitConverter.ToUInt32(bytes, 24);
            isDirectoryAttribute = (fileAttributes & 0x10u) != 0;

            int pos = 76;
            // Skip LinkTargetIDList if HasLinkTargetIDList (0x01) is set.
            if ((linkFlags & 0x01u) != 0)
            {
                if (pos + 2 > bytes.Length) return false;
                ushort idListSize = BitConverter.ToUInt16(bytes, pos);
                pos += 2 + idListSize;
            }

            // Parse LinkInfo if HasLinkInfo (0x02) is set.
            if ((linkFlags & 0x02u) != 0 && pos + 28 <= bytes.Length)
            {
                int linkInfoStart = pos;
                uint linkInfoSize = BitConverter.ToUInt32(bytes, linkInfoStart);
                uint linkInfoHeaderSize = BitConverter.ToUInt32(bytes, linkInfoStart + 4);
                uint linkInfoFlags = BitConverter.ToUInt32(bytes, linkInfoStart + 8);
                if (linkInfoSize >= 28 && linkInfoStart + linkInfoSize <= bytes.Length && (linkInfoFlags & 0x01u) != 0)
                {
                    string? basePath = null;
                    string? commonSuffix = null;
                    if (linkInfoHeaderSize >= 0x24 && linkInfoStart + 36 <= bytes.Length)
                    {
                        uint baseOffsetUni = BitConverter.ToUInt32(bytes, linkInfoStart + 28);
                        uint suffixOffsetUni = BitConverter.ToUInt32(bytes, linkInfoStart + 32);
                        if (baseOffsetUni > 0 && linkInfoStart + baseOffsetUni < bytes.Length)
                            basePath = ReadNullTerminatedUtf16(bytes, (int)(linkInfoStart + baseOffsetUni));
                        if (suffixOffsetUni > 0 && linkInfoStart + suffixOffsetUni < bytes.Length)
                            commonSuffix = ReadNullTerminatedUtf16(bytes, (int)(linkInfoStart + suffixOffsetUni));
                    }

                    if (string.IsNullOrWhiteSpace(basePath))
                    {
                        uint baseOffset = BitConverter.ToUInt32(bytes, linkInfoStart + 16);
                        uint suffixOffset = BitConverter.ToUInt32(bytes, linkInfoStart + 24);
                        if (baseOffset > 0 && linkInfoStart + baseOffset < bytes.Length)
                            basePath = ReadNullTerminatedAnsi(bytes, (int)(linkInfoStart + baseOffset));
                        if (suffixOffset > 0 && linkInfoStart + suffixOffset < bytes.Length)
                            commonSuffix = ReadNullTerminatedAnsi(bytes, (int)(linkInfoStart + suffixOffset));
                    }

                    if (!string.IsNullOrWhiteSpace(basePath))
                    {
                        string combined = string.IsNullOrEmpty(commonSuffix)
                            ? basePath
                            : $"{basePath.TrimEnd('\\')}\\{commonSuffix.TrimStart('\\')}";
                        targetPath = Environment.ExpandEnvironmentVariables(combined);
                        return true;
                    }
                }
            }

            // Fallback: scan UTF-16LE strings inside the .lnk payload for a drive-rooted path.
            string? scanned = ScanForDrivePathUtf16(bytes);
            if (!string.IsNullOrWhiteSpace(scanned))
            {
                targetPath = Environment.ExpandEnvironmentVariables(scanned);
                return true;
            }
        }
        catch
        {
            // Ignore malformed shortcut payloads and fall back safely.
        }
        return false;
    }

    private static string ReadNullTerminatedUtf16(byte[] bytes, int offset)
    {
        int end = offset;
        while (end + 1 < bytes.Length && (bytes[end] != 0 || bytes[end + 1] != 0))
            end += 2;
        return end > offset ? Encoding.Unicode.GetString(bytes, offset, end - offset) : string.Empty;
    }

    private static string ReadNullTerminatedAnsi(byte[] bytes, int offset)
    {
        int end = offset;
        while (end < bytes.Length && bytes[end] != 0)
            end++;
        return end > offset ? Encoding.Default.GetString(bytes, offset, end - offset) : string.Empty;
    }

    private static string? ScanForDrivePathUtf16(byte[] bytes)
    {
        string? bestCandidate = null;
        for (int i = 76; i + 8 < bytes.Length; i++)
        {
            char c0 = (char)(bytes[i] | (bytes[i + 1] << 8));
            char c1 = (char)(bytes[i + 2] | (bytes[i + 3] << 8));
            char c2 = (char)(bytes[i + 4] | (bytes[i + 5] << 8));
            if (!char.IsAsciiLetter(c0) || c1 != ':' || c2 != '\\')
                continue;

            int end = i;
            while (end + 1 < bytes.Length)
            {
                char ch = (char)(bytes[end] | (bytes[end + 1] << 8));
                if (ch < 32 || ch is '"' or '<' or '>' or '|' or '*' or '?')
                    break;
                end += 2;
                if (end - i >= 520) break;
            }

            string candidate = Encoding.Unicode.GetString(bytes, i, end - i).Trim();
            if (candidate.Length >= 4)
            {
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    return candidate;
                bestCandidate ??= candidate;
            }
        }
        return bestCandidate;
    }

    private static bool IsContainerKind(WidgetItemKind kind) =>
        kind is WidgetItemKind.Organizer or WidgetItemKind.Folder;

    private static int KindBucket(WidgetItemKind kind) => kind switch
    {
        WidgetItemKind.Organizer => 0,
        WidgetItemKind.Folder => 1,
        WidgetItemKind.PortableNote or WidgetItemKind.Note => 2,
        WidgetItemKind.PortableTodo => 3,
        WidgetItemKind.Shortcut => 4,
        WidgetItemKind.InternetShortcut => 5,
        _ => 6
    };

    private sealed class NaturalNameComparer : IComparer<string>
    {
        internal static NaturalNameComparer Instance { get; } = new();

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string x, string y);

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            x ??= string.Empty;
            y ??= string.Empty;
            return StrCmpLogicalW(x, y);
        }
    }
}
