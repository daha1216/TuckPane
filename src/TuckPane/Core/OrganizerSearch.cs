using System.Globalization;
using TuckPane.Models;

namespace TuckPane.Core;

/// <summary>One searchable entry: a top-level item of some organizer's storage directory.</summary>
internal sealed record OrganizerSearchCandidate(
    Guid OrganizerId,
    string OrganizerName,
    int OrganizerOrder,
    string DisplayName,
    string FileName,
    string FullPath,
    WidgetItemKind Kind);

internal readonly record struct OrganizerSearchResult(OrganizerSearchCandidate Candidate, int Score);

/// <summary>
/// Ranking for "Find in organizers". Every whitespace-separated token must match the item's
/// display name (or, failing that, its file name including the extension). Per token, earlier and
/// tighter matches score higher: whole name > prefix > word start > substring > initials/subsequence.
/// Matching ignores case, width and accents so "cafe" finds "Café" and full-width input works.
/// </summary>
internal static class OrganizerSearch
{
    internal const int DefaultLimit = 50;

    private const int Exact = 1000;
    private const int Prefix = 800;
    private const int WordStart = 600;
    private const int Substring = 400;
    private const int Subsequence = 150;

    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType |
        CompareOptions.IgnoreWidth | CompareOptions.IgnoreNonSpace;

    internal static IReadOnlyList<OrganizerSearchResult> Search(
        IEnumerable<OrganizerSearchCandidate> candidates, string? query, int limit = DefaultLimit)
    {
        string[] tokens = Tokenize(query);
        if (tokens.Length == 0) return [];
        var results = new List<OrganizerSearchResult>();
        foreach (OrganizerSearchCandidate candidate in candidates)
        {
            int total = 0;
            bool all = true;
            foreach (string token in tokens)
            {
                int score = Math.Max(ScoreToken(candidate.DisplayName, token), ScoreToken(candidate.FileName, token) - 50);
                if (score <= 0) { all = false; break; }
                total += score;
            }
            if (!all) continue;
            // Folders and shortcuts are what people jump to most; nudge them only to break ties.
            if (candidate.Kind is WidgetItemKind.Folder or WidgetItemKind.Shortcut or WidgetItemKind.InternetShortcut) total += 5;
            results.Add(new OrganizerSearchResult(candidate, total));
        }
        return results
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Candidate.DisplayName.Length)
            .ThenBy(result => result.Candidate.OrganizerOrder)
            .ThenBy(result => result.Candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(1, limit))
            .ToArray();
    }

    internal static string[] Tokenize(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static int ScoreToken(string text, string token)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)) return 0;
        if (Compare.Compare(text, token, Loose) == 0) return Exact;
        int index = Compare.IndexOf(text, token, Loose);
        if (index == 0) return Prefix;
        if (index > 0)
        {
            // Prefer the earliest occurrence that starts a word ("report" in "Q3 report.pdf").
            for (int at = index; at >= 0 && at < text.Length; at = Compare.IndexOf(text, token, at + 1, Loose))
            {
                if (IsWordStart(text, at)) return WordStart - Math.Min(at, 100);
                if (at + 1 >= text.Length) break;
            }
            return Substring - Math.Min(index, 100);
        }
        return IsSubsequence(text, token) ? Subsequence : 0;
    }

    private static bool IsWordStart(string text, int index)
    {
        if (index <= 0) return true;
        char previous = text[index - 1];
        char current = text[index];
        return !char.IsLetterOrDigit(previous) ||
            char.IsLower(previous) && char.IsUpper(current) ||
            char.IsLetter(previous) != char.IsLetter(current);
    }

    // "qr" finds "Quarterly Report"; also lets short queries match abbreviations.
    private static bool IsSubsequence(string text, string token)
    {
        if (token.Length < 2) return false;
        int position = 0;
        foreach (char wanted in token)
        {
            bool found = false;
            while (position < text.Length)
            {
                if (Compare.Compare(text, position++, 1, wanted.ToString(), 0, 1, Loose) == 0) { found = true; break; }
            }
            if (!found) return false;
        }
        return true;
    }
}
