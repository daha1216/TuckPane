using System.Globalization;
using System.Text;
using TuckPane.Models;

namespace TuckPane.Core;

internal sealed record OrganizerSearchEntry(
    Guid OrganizerId,
    string OrganizerName,
    string ItemName,
    string RelativeName,
    string FullPath,
    WidgetItemKind Kind,
    Guid? NoteId = null);

internal sealed record OrganizerSearchHit(
    OrganizerSearchEntry Entry,
    int Score);

/// <summary>
/// Pure multi-token, diacritic/fullwidth-normalized fuzzy search and ranking engine
/// shared by Global Spotlight Search and In-Organizer Instant Filter.
/// </summary>
internal static class OrganizerSearch
{
    internal const int DefaultMaxResults = 50;

    internal static IReadOnlyList<OrganizerSearchHit> Search(
        IEnumerable<OrganizerSearchEntry> entries,
        string? query,
        int maxResults = DefaultMaxResults)
    {
        ArgumentNullException.ThrowIfNull(entries);
        int limit = Math.Max(1, maxResults);
        string[] tokens = Tokenize(query);
        if (tokens.Length == 0)
        {
            return entries
                .OrderBy(entry => entry.OrganizerName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.ItemName, StringComparer.CurrentCultureIgnoreCase)
                .Take(limit)
                .Select(entry => new OrganizerSearchHit(entry, 0))
                .ToArray();
        }

        var hits = new List<OrganizerSearchHit>();
        foreach (OrganizerSearchEntry entry in entries)
        {
            int score = ScoreEntry(entry, tokens);
            if (score > 0) hits.Add(new OrganizerSearchHit(entry, score));
        }

        return hits
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Entry.ItemName.Length)
            .ThenBy(hit => hit.Entry.ItemName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(hit => hit.Entry.OrganizerName, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    internal static bool MatchesItemQuery(WidgetItem item, string? query)
    {
        string[] tokens = Tokenize(query);
        if (tokens.Length == 0) return true;
        var entry = new OrganizerSearchEntry(Guid.Empty, string.Empty, item.Name, item.RelativeName, item.FullPath, item.Kind, item.NoteId);
        return ScoreEntry(entry, tokens) > 0;
    }

    internal static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string compatibility = value.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(compatibility.Length);
        foreach (char ch in compatibility)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString().Trim();
    }

    internal static string[] Tokenize(string? query)
    {
        string normalized = NormalizeText(query);
        if (normalized.Length == 0) return [];
        return normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int ScoreEntry(OrganizerSearchEntry entry, string[] tokens)
    {
        string name = NormalizeText(entry.ItemName);
        string relative = NormalizeText(entry.RelativeName);
        string extension = NormalizeText(Path.GetExtension(entry.RelativeName));
        string organizer = NormalizeText(entry.OrganizerName);
        string acronym = BuildAcronym(entry.ItemName);

        int totalScore = 0;
        foreach (string token in tokens)
        {
            int tokenScore = ScoreSingleToken(token, name, relative, extension, organizer, acronym);
            if (tokenScore <= 0) return 0; // Multi-token AND semantics.
            totalScore += tokenScore;
        }
        return totalScore;
    }

    private static int ScoreSingleToken(
        string token,
        string name,
        string relative,
        string extension,
        string organizer,
        string acronym)
    {
        if (name.Equals(token, StringComparison.Ordinal) ||
            relative.Equals(token, StringComparison.Ordinal))
        {
            return 1000;
        }

        if (name.StartsWith(token, StringComparison.Ordinal) ||
            relative.StartsWith(token, StringComparison.Ordinal))
        {
            return 800;
        }

        if (HasWordBoundaryMatch(name, token))
            return 600;

        int nameIndex = name.IndexOf(token, StringComparison.Ordinal);
        if (nameIndex >= 0)
            return Math.Max(350, 450 - nameIndex);

        int relativeIndex = relative.IndexOf(token, StringComparison.Ordinal);
        if (relativeIndex >= 0)
            return Math.Max(300, 400 - relativeIndex);

        if (token.Length >= 2 && acronym.Length >= token.Length &&
            acronym.StartsWith(token, StringComparison.Ordinal))
        {
            return 280;
        }

        if (token.Length >= 2 && acronym.Contains(token, StringComparison.Ordinal))
            return 240;

        if (!string.IsNullOrEmpty(extension))
        {
            string bareExt = extension.TrimStart('.');
            string bareToken = token.TrimStart('.');
            if (bareToken.Length > 0 &&
                (extension.Equals(token, StringComparison.Ordinal) || bareExt.Equals(bareToken, StringComparison.Ordinal)))
            {
                return 200;
            }
        }

        if (!string.IsNullOrEmpty(organizer))
        {
            if (organizer.Equals(token, StringComparison.Ordinal)) return 160;
            if (organizer.StartsWith(token, StringComparison.Ordinal)) return 130;
            if (organizer.Contains(token, StringComparison.Ordinal)) return 100;
        }

        return 0;
    }

    private static bool HasWordBoundaryMatch(string text, string token)
    {
        if (text.Length == 0 || token.Length == 0) return false;
        int index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            if (index == 0 || IsWordSeparator(text[index - 1]))
                return true;
            index++;
        }
        return false;
    }

    private static string BuildAcronym(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string compatibility = raw.Normalize(NormalizationForm.FormKD);
        var acronym = new StringBuilder();
        bool startWord = true;
        char previous = '\0';
        foreach (char ch in compatibility)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (IsWordSeparator(ch))
            {
                startWord = true;
                previous = ch;
                continue;
            }
            bool camelBoundary = char.IsUpper(ch) && char.IsLower(previous);
            if (startWord || camelBoundary)
            {
                acronym.Append(char.ToLowerInvariant(ch));
                startWord = false;
            }
            previous = ch;
        }
        return acronym.ToString();
    }

    private static bool IsWordSeparator(char ch) =>
        char.IsWhiteSpace(ch) || ch is '-' or '_' or '.' or '/' or '\\' or '(' or ')' or '[' or ']' or '+' or ':';
}
