using System.Text.RegularExpressions;

namespace TuckPane.Core;

public enum AutoOrganizeSource
{
    Desktop = 0,
    Downloads = 1
}

public sealed class AutoOrganizeRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public AutoOrganizeSource Source { get; set; } = AutoOrganizeSource.Desktop;
    public string Extensions { get; set; } = string.Empty;
    public string NamePatterns { get; set; } = string.Empty;
    public int MinAgeDays { get; set; }

    public AutoOrganizeRule Clone() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Source = Source,
        Extensions = Extensions,
        NamePatterns = NamePatterns,
        MinAgeDays = MinAgeDays
    };
}

internal sealed record AutoOrganizeCandidate(
    string FullPath,
    string FileName,
    string Extension,
    AutoOrganizeSource Source,
    DateTimeOffset LastWriteUtc,
    bool IsDirectory,
    bool IsReparsePoint,
    bool IsHiddenOrSystem);

internal sealed record AutoOrganizePlannedMove(
    string SourcePath,
    Guid TargetOrganizerId,
    string TargetOrganizerName,
    string TargetDirectory,
    bool CreatesShortcut);

internal sealed record AutoOrganizeUndoEntry(
    string OriginalPath,
    string MovedPath,
    bool CreatedShortcutOnly);

internal sealed record AutoOrganizeUndoBatch(
    DateTimeOffset ExecutedAtUtc,
    IReadOnlyList<AutoOrganizeUndoEntry> Entries);

/// <summary>
/// Pure rule-matching and dry-run planning engine for Auto-Organize Rules.
/// Guarantees safety: skips hidden/system entries, reparse points, directories
/// overlapping target storage, and never overwrites.
/// </summary>
internal static class AutoOrganizeRules
{
    internal static IReadOnlyList<string> ParseExtensions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.StartsWith('.') ? token.ToLowerInvariant() : "." + token.ToLowerInvariant())
            .Where(token => token.Length > 1 && !token.Contains('*') && !token.Contains('?'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ParsePatterns(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool MatchesWildcard(string fileName, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static bool MatchesRule(AutoOrganizeRule rule, AutoOrganizeCandidate candidate, DateTimeOffset nowUtc)
    {
        if (!rule.Enabled || candidate.Source != rule.Source) return false;
        if (candidate.IsReparsePoint || candidate.IsHiddenOrSystem) return false;
        if (candidate.FileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return false;
        if (candidate.FileName.StartsWith(".glassfolder-staging-", StringComparison.OrdinalIgnoreCase)) return false;

        int minDays = Math.Max(0, rule.MinAgeDays);
        if (minDays > 0 && (nowUtc - candidate.LastWriteUtc).TotalDays < minDays)
            return false;

        IReadOnlyList<string> extensions = ParseExtensions(rule.Extensions);
        IReadOnlyList<string> patterns = ParsePatterns(rule.NamePatterns);
        if (extensions.Count == 0 && patterns.Count == 0) return false;

        bool extMatches = extensions.Count > 0 && !candidate.IsDirectory &&
            extensions.Any(ext => candidate.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase));
        bool patternMatches = patterns.Count > 0 &&
            patterns.Any(pattern => MatchesWildcard(candidate.FileName, pattern));

        if (extensions.Count > 0 && patterns.Count > 0)
            return extMatches || patternMatches;
        return extMatches || patternMatches;
    }

    internal static IReadOnlyList<AutoOrganizePlannedMove> EvaluatePlan(
        IEnumerable<AutoOrganizeCandidate> candidates,
        IReadOnlyList<(Guid OrganizerId, string OrganizerName, string TargetDirectory, IReadOnlyList<AutoOrganizeRule> Rules)> targets,
        DateTimeOffset nowUtc)
    {
        var claimedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<AutoOrganizePlannedMove>();
        foreach (AutoOrganizeCandidate candidate in candidates)
        {
            string fullPath = Path.GetFullPath(candidate.FullPath);
            if (!claimedSources.Add(fullPath)) continue;

            foreach (var target in targets)
            {
                if (string.IsNullOrWhiteSpace(target.TargetDirectory)) continue;
                string normalizedTarget = Path.GetFullPath(target.TargetDirectory).TrimEnd(Path.DirectorySeparatorChar);
                string? sourceDir = Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(sourceDir, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (candidate.IsDirectory &&
                    (fullPath.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                     normalizedTarget.StartsWith(fullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (target.Rules.Any(rule => MatchesRule(rule, candidate, nowUtc)))
                {
                    bool createsShortcut = !candidate.IsDirectory && DropValidator.IsExecutable(fullPath);
                    plan.Add(new AutoOrganizePlannedMove(
                        fullPath,
                        target.OrganizerId,
                        target.OrganizerName,
                        normalizedTarget,
                        createsShortcut));
                    break;
                }
            }
        }
        return plan;
    }

    internal static AutoOrganizeRule CreatePreset(string presetKey, AutoOrganizeSource source = AutoOrganizeSource.Desktop) =>
        presetKey switch
        {
            "documents" => new AutoOrganizeRule { Source = source, Extensions = ".pdf, .doc, .docx, .xls, .xlsx, .ppt, .pptx, .txt, .md" },
            "images" => new AutoOrganizeRule { Source = source, Extensions = ".png, .jpg, .jpeg, .webp, .gif, .bmp, .svg" },
            "archives" => new AutoOrganizeRule { Source = source, Extensions = ".zip, .7z, .rar, .tar, .gz" },
            "shortcuts" => new AutoOrganizeRule { Source = source, Extensions = ".lnk, .url" },
            "installers" => new AutoOrganizeRule { Source = source, Extensions = ".exe, .msi, .msix" },
            _ => new AutoOrganizeRule { Source = source, Extensions = ".txt, .md" }
        };
}
