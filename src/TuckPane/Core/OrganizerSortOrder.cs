using System.Runtime.InteropServices;
using TuckPane.Models;

namespace TuckPane.Core;

internal enum OrganizerSortKey
{
    Name,
    Type,
    DateModified
}

/// <summary>One item as seen by a one-shot sort; captured off the UI thread.</summary>
internal readonly record struct OrganizerSortEntry(
    string RelativeName,
    string DisplayName,
    WidgetItemKind Kind,
    string Extension,
    DateTime LastWriteUtc);

/// <summary>
/// Computes a new persisted <c>ItemOrder</c> for a one-shot "Sort by" command. Sorting is a
/// command, not a mode: the result is written once and manual drag reordering keeps working.
/// Contained panes and folders always lead, mirroring Explorer's "folders first" convention.
/// </summary>
internal static class OrganizerSortOrder
{
    internal static IReadOnlyList<string> Sort(IEnumerable<OrganizerSortEntry> entries, OrganizerSortKey key)
    {
        IOrderedEnumerable<OrganizerSortEntry> grouped = entries.OrderBy(entry => GroupRank(entry.Kind));
        IOrderedEnumerable<OrganizerSortEntry> ordered = key switch
        {
            OrganizerSortKey.Type => grouped
                .ThenBy(entry => entry.Extension, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.DisplayName, NaturalComparer.Instance),
            OrganizerSortKey.DateModified => grouped
                .ThenByDescending(entry => entry.LastWriteUtc)
                .ThenBy(entry => entry.DisplayName, NaturalComparer.Instance),
            _ => grouped.ThenBy(entry => entry.DisplayName, NaturalComparer.Instance)
        };
        // A stable final key keeps the result deterministic when display names collide.
        return ordered
            .ThenBy(entry => entry.RelativeName, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.RelativeName)
            .ToArray();
    }

    internal static int GroupRank(WidgetItemKind kind) => kind switch
    {
        WidgetItemKind.Organizer => 0,
        WidgetItemKind.Folder => 1,
        _ => 2
    };

    /// <summary>Explorer's ordering: "file2" before "file10", case-insensitive, culture-aware.</summary>
    internal sealed class NaturalComparer : IComparer<string>
    {
        internal static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            try { return StrCmpLogicalW(x, y); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return StringComparer.CurrentCultureIgnoreCase.Compare(x, y);
            }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string first, string second);
    }
}
