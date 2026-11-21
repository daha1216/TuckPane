using System.Runtime.InteropServices;
using System.Text;
using TuckPane.Models;
using TuckPane.Services;

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
                        if (!string.IsNullOrWhiteSpace(targetPath))
                        {
                            if (File.Exists(targetPath))
                            {
                                var targetFile = new FileInfo(targetPath);
                                size = targetFile.Length;
                                string targetExt = Path.GetExtension(targetPath).ToLowerInvariant();
                                extension = string.IsNullOrEmpty(targetExt) ? "lnk:1-file" : $"lnk:1-{targetExt}";
                            }
                            else if (Directory.Exists(targetPath))
                            {
                                extension = "lnk:0-folder";
                            }
                            else
                            {
                                string targetExt = Path.GetExtension(targetPath).ToLowerInvariant();
                                if (isDirectoryAttr && !IsExecutableExtension(targetExt))
                                    extension = "lnk:0-folder";
                                else
                                    extension = string.IsNullOrEmpty(targetExt) ? "lnk:2-shortcut" : $"lnk:1-{targetExt}";
                            }
                        }
                        else
                        {
                            extension = isDirectoryAttr ? "lnk:0-folder" : "lnk:2-shortcut";
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

    private const uint CpAcp = 0;
    private const uint ExpSzLinkSignature = 0xA0000001u;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int MultiByteToWideChar(
        uint codePage,
        uint dwFlags,
        in byte lpMultiByteStr,
        int cbMultiByte,
        [Out] char[]? lpWideCharStr,
        int cchWideChar);

    private static bool IsExecutableExtension(string lowerExtension) =>
        lowerExtension is ".exe" or ".com" or ".bat" or ".cmd" or ".msi" or ".ps1" or ".scr" or ".lnk";

    private static bool TryResolveShortcutTarget(string lnkPath, out string? targetPath, out bool isDirectoryAttribute)
    {
        targetPath = null;
        isDirectoryAttribute = false;
        string? linkInfoPath = null;
        string? expSzPath = null;

        try
        {
            byte[] bytes = File.ReadAllBytes(lnkPath);
            if (bytes.Length >= 76 && BitConverter.ToUInt32(bytes, 0) == 0x0000004C)
            {
                uint linkFlags = BitConverter.ToUInt32(bytes, 20);
                uint fileAttributes = BitConverter.ToUInt32(bytes, 24);
                isDirectoryAttribute = (fileAttributes & 0x10u) != 0;

                int pos = 76;
                bool binaryIntact = true;

                // Skip LinkTargetIDList if HasLinkTargetIDList (0x01) is set.
                if ((linkFlags & 0x01u) != 0)
                {
                    if (pos + 2 <= bytes.Length)
                    {
                        ushort idListSize = BitConverter.ToUInt16(bytes, pos);
                        pos += 2 + idListSize;
                        if (pos > bytes.Length) binaryIntact = false;
                    }
                    else
                    {
                        binaryIntact = false;
                    }
                }

                // Parse LinkInfo if HasLinkInfo (0x02) is set.
                if (binaryIntact && (linkFlags & 0x02u) != 0)
                {
                    if (pos + 4 <= bytes.Length)
                    {
                        int linkInfoStart = pos;
                        uint linkInfoSize = BitConverter.ToUInt32(bytes, linkInfoStart);
                        if (linkInfoSize >= 4 && (long)linkInfoStart + linkInfoSize <= bytes.Length)
                        {
                            int linkInfoEnd = linkInfoStart + (int)linkInfoSize;
                            if (linkInfoSize >= 28)
                            {
                                uint linkInfoHeaderSize = BitConverter.ToUInt32(bytes, linkInfoStart + 4);
                                uint linkInfoFlags = BitConverter.ToUInt32(bytes, linkInfoStart + 8);
                                if ((linkInfoFlags & 0x01u) != 0)
                                {
                                    string? basePath = null;
                                    string? commonSuffix = null;
                                    if (linkInfoHeaderSize >= 0x24 && linkInfoSize >= 36)
                                    {
                                        uint baseOffsetUni = BitConverter.ToUInt32(bytes, linkInfoStart + 28);
                                        uint suffixOffsetUni = BitConverter.ToUInt32(bytes, linkInfoStart + 32);
                                        if (baseOffsetUni > 0 && baseOffsetUni < linkInfoSize)
                                        {
                                            int start = linkInfoStart + (int)baseOffsetUni;
                                            basePath = ReadNullTerminatedUtf16(bytes, start, linkInfoEnd - start);
                                        }
                                        if (suffixOffsetUni > 0 && suffixOffsetUni < linkInfoSize)
                                        {
                                            int start = linkInfoStart + (int)suffixOffsetUni;
                                            commonSuffix = ReadNullTerminatedUtf16(bytes, start, linkInfoEnd - start);
                                        }
                                    }

                                    if (string.IsNullOrWhiteSpace(basePath))
                                    {
                                        uint baseOffset = BitConverter.ToUInt32(bytes, linkInfoStart + 16);
                                        uint suffixOffset = BitConverter.ToUInt32(bytes, linkInfoStart + 24);
                                        if (baseOffset > 0 && baseOffset < linkInfoSize)
                                        {
                                            int start = linkInfoStart + (int)baseOffset;
                                            basePath = ReadNullTerminatedAnsi(bytes, start, linkInfoEnd - start);
                                        }
                                        if (suffixOffset > 0 && suffixOffset < linkInfoSize)
                                        {
                                            int start = linkInfoStart + (int)suffixOffset;
                                            commonSuffix = ReadNullTerminatedAnsi(bytes, start, linkInfoEnd - start);
                                        }
                                    }

                                    if (!string.IsNullOrWhiteSpace(basePath))
                                    {
                                        string combined = string.IsNullOrEmpty(commonSuffix)
                                            ? basePath
                                            : $"{basePath.TrimEnd('\\')}\\{commonSuffix.TrimStart('\\')}";
                                        linkInfoPath = Environment.ExpandEnvironmentVariables(combined.Trim());
                                        if (File.Exists(linkInfoPath) || Directory.Exists(linkInfoPath))
                                        {
                                            targetPath = linkInfoPath;
                                            return true;
                                        }
                                    }
                                }
                            }
                            pos = linkInfoEnd;
                        }
                        else
                        {
                            binaryIntact = false;
                        }
                    }
                    else
                    {
                        binaryIntact = false;
                    }
                }

                // Skip StringData (Name, RelativePath, WorkingDir, Arguments, IconLocation) to reach ExtraData.
                if (binaryIntact)
                {
                    bool isUnicode = (linkFlags & 0x80u) != 0;
                    int charBytes = isUnicode ? 2 : 1;
                    ReadOnlySpan<uint> stringFlags = [0x04u, 0x08u, 0x10u, 0x20u, 0x40u];
                    foreach (uint flag in stringFlags)
                    {
                        if ((linkFlags & flag) == 0) continue;
                        if (pos + 2 > bytes.Length)
                        {
                            binaryIntact = false;
                            break;
                        }
                        ushort charCount = BitConverter.ToUInt16(bytes, pos);
                        pos += 2 + charCount * charBytes;
                        if (pos > bytes.Length)
                        {
                            binaryIntact = false;
                            break;
                        }
                    }
                }

                // Parse ExtraData blocks for EXP_SZ_LINK (0xA0000001 EnvironmentVariableDataBlock).
                if (binaryIntact)
                {
                    while (pos + 4 <= bytes.Length)
                    {
                        uint blockSize = BitConverter.ToUInt32(bytes, pos);
                        if (blockSize < 4 || (long)pos + blockSize > bytes.Length) break;
                        if (blockSize >= 788 && BitConverter.ToUInt32(bytes, pos + 4) == ExpSzLinkSignature)
                        {
                            string unicodeTarget = ReadNullTerminatedUtf16(bytes, pos + 268, 520);
                            string rawTarget = !string.IsNullOrWhiteSpace(unicodeTarget)
                                ? unicodeTarget
                                : ReadNullTerminatedAnsi(bytes, pos + 8, 260);
                            if (!string.IsNullOrWhiteSpace(rawTarget))
                            {
                                expSzPath = Environment.ExpandEnvironmentVariables(rawTarget.Trim());
                                if (File.Exists(expSzPath) || Directory.Exists(expSzPath))
                                {
                                    targetPath = expSzPath;
                                    return true;
                                }
                            }
                        }
                        pos += (int)blockSize;
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed binary shortcut payloads and fall back safely to COM IShellLinkW.
        }

        // Authoritative COM IShellLinkW fallback when binary LinkInfo + EXP_SZ_LINK do not yield an existing path.
        string? comTargetPath = null;
        try
        {
            ShortcutInspection? inspection = ShortcutIntegrityService.InspectLink(lnkPath);
            if (inspection is not null &&
                !inspection.IsVirtualShellTarget &&
                !string.IsNullOrWhiteSpace(inspection.ExpandedTargetPath))
            {
                comTargetPath = inspection.ExpandedTargetPath;
                if (File.Exists(comTargetPath) || Directory.Exists(comTargetPath))
                {
                    targetPath = comTargetPath;
                    return true;
                }
            }
        }
        catch
        {
            // Fall through to best non-existing parsed target below.
        }

        string? fallback = !string.IsNullOrWhiteSpace(comTargetPath)
            ? comTargetPath
            : !string.IsNullOrWhiteSpace(expSzPath)
                ? expSzPath
                : linkInfoPath;
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            targetPath = fallback;
            return true;
        }
        return false;
    }

    private static string ReadNullTerminatedUtf16(byte[] bytes, int offset, int maxBytes = int.MaxValue)
    {
        if (offset < 0 || offset >= bytes.Length) return string.Empty;
        int limit = maxBytes == int.MaxValue ? bytes.Length : Math.Min(bytes.Length, offset + maxBytes);
        int end = offset;
        while (end + 1 < limit && (bytes[end] != 0 || bytes[end + 1] != 0))
            end += 2;
        return end > offset ? Encoding.Unicode.GetString(bytes, offset, end - offset) : string.Empty;
    }

    private static string ReadNullTerminatedAnsi(byte[] bytes, int offset, int maxBytes = int.MaxValue)
    {
        if (offset < 0 || offset >= bytes.Length) return string.Empty;
        int limit = maxBytes == int.MaxValue ? bytes.Length : Math.Min(bytes.Length, offset + maxBytes);
        int end = offset;
        while (end < limit && bytes[end] != 0)
            end++;
        int byteCount = end - offset;
        if (byteCount <= 0) return string.Empty;

        int charCount = MultiByteToWideChar(CpAcp, 0, in bytes[offset], byteCount, null, 0);
        if (charCount <= 0) return string.Empty;
        char[] buffer = new char[charCount];
        int written = MultiByteToWideChar(CpAcp, 0, in bytes[offset], byteCount, buffer, charCount);
        return written > 0 ? new string(buffer, 0, written) : string.Empty;
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
