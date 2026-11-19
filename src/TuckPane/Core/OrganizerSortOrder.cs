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
/// where appropriate and break ties cleanly by natural display name.
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
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep safe defaults when an item disappears or is inaccessible.
        }
        return new SortableItemMetadata(item.RelativeName, item.Name, item.Kind, extension, modified, size);
    }

    private static bool IsContainerKind(WidgetItemKind kind) =>
        kind is WidgetItemKind.Organizer or WidgetItemKind.Folder;

    private static int KindBucket(WidgetItemKind kind) => kind switch
    {
        WidgetItemKind.Organizer => 0,
        WidgetItemKind.Folder => 1,
        WidgetItemKind.PortableNote or WidgetItemKind.Note => 2,
        WidgetItemKind.PortableTodo => 3,
        WidgetItemKind.Shortcut or WidgetItemKind.InternetShortcut => 4,
        _ => 5
    };

    private sealed class NaturalNameComparer : IComparer<string>
    {
        internal static NaturalNameComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            x ??= string.Empty;
            y ??= string.Empty;
            int ix = 0, iy = 0;
            while (ix < x.Length && iy < y.Length)
            {
                if (char.IsAsciiDigit(x[ix]) && char.IsAsciiDigit(y[iy]))
                {
                    int startX = ix;
                    while (ix < x.Length && char.IsAsciiDigit(x[ix])) ix++;
                    int startY = iy;
                    while (iy < y.Length && char.IsAsciiDigit(y[iy])) iy++;
                    ReadOnlySpan<char> numX = x.AsSpan(startX, ix - startX).TrimStart('0');
                    ReadOnlySpan<char> numY = y.AsSpan(startY, iy - startY).TrimStart('0');
                    int lengthCompare = numX.Length.CompareTo(numY.Length);
                    if (lengthCompare != 0) return lengthCompare;
                    int digitCompare = numX.CompareTo(numY, StringComparison.Ordinal);
                    if (digitCompare != 0) return digitCompare;
                    continue;
                }

                int charCompare = string.Compare(x, ix, y, iy, 1, StringComparison.CurrentCultureIgnoreCase);
                if (charCompare != 0) return charCompare;
                ix++;
                iy++;
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
