using System.Text.RegularExpressions;

namespace TuckPane.Core;

public enum DesktopUpdateMatchKind
{
    ShortcutExactName = 0,
    ShortcutSameTarget = 1,
    ShortcutVersionedTarget = 2,
    ShortcutSameUrl = 3,
    DirectoryOverlap = 4,
    DirectoryLedgerTracked = 5,
    TrackedFileUpdate = 6
}

public sealed record DesktopItemSnapshot(
    string FullPath,
    string Name,
    string Extension,
    bool IsDirectory,
    bool IsPublicDesktop,
    bool IsHiddenOrSystem,
    bool IsReparsePoint,
    string? NormalizedTargetPath,
    string? NormalizedArguments,
    string? UrlTarget,
    IReadOnlyList<string> ChildEntryNames,
    bool ContainsAppArtifacts);

public sealed record OrganizerItemSnapshot(
    Guid OrganizerId,
    string OrganizerName,
    string StorageDirectory,
    string RelativeName,
    string FullPath,
    string Extension,
    bool IsDirectory,
    string? NormalizedTargetPath,
    string? NormalizedArguments,
    string? UrlTarget,
    IReadOnlyList<string> ChildEntryNames);

public sealed record DesktopOriginRecord(
    string OriginalDesktopPath,
    Guid OrganizerId,
    string RelativeName,
    bool IsDirectory,
    string? NormalizedTargetPath,
    DateTimeOffset TrackedAtUtc);

public sealed record DesktopUpdateMatch(
    DesktopItemSnapshot DesktopItem,
    Guid TargetOrganizerId,
    string TargetOrganizerName,
    string TargetStorageDirectory,
    string TargetRelativeName,
    string TargetFullPath,
    DesktopUpdateMatchKind Kind,
    int Confidence,
    bool RenameTargetToDesktopName);

public sealed record InstalledAppPathMismatch(
    string RegistryHive,
    string RegistrySubKeyPath,
    string DisplayName,
    string DisplayVersion,
    string ValueName,
    string OldValue,
    string NewValue,
    string OldDesktopPrefix,
    string NewOrganizerPrefix,
    bool IsService,
    string? ServiceName,
    Guid OrganizerId,
    string OrganizerName);

/// <summary>
/// Pure, deterministic matching and path-rewriting rules for reconciling desktop
/// shortcuts, application folders, and registry install paths when an already-organized
/// application updates itself and recreates copies on User or Public Desktop.
/// </summary>
public static partial class DesktopUpdateMatchRules
{
    private static readonly HashSet<string> IgnoredExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini",
        "thumbs.db",
        "tuckpane.lnk"
    };

    private static readonly HashSet<string> IgnoredTempExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp",
        ".temp",
        ".crdownload",
        ".part",
        ".partial",
        ".download",
        ".opdownload",
        ".bak"
    };

    private static readonly HashSet<string> GenericShellLaunchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe",
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "wscript.exe",
        "cscript.exe",
        "mshta.exe",
        "rundll32.exe",
        "conhost.exe",
        "wt.exe",
        "msiexec.exe",
        "javaw.exe",
        "java.exe",
        "python.exe",
        "pythonw.exe",
        "node.exe",
        "dotnet.exe"
    };

    private static readonly HashSet<string> ExecutableOrInstallerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".msi",
        ".msix",
        ".bat",
        ".cmd",
        ".ps1",
        ".com",
        ".dll"
    };

    // Matches version directory segments like "app-1.2.3", "v1.19.9", "2.1.23.5_20261120205734", "128.0.6613.114".
    [GeneratedRegex(@"^(?:app[-_]?|v)?\d+\.\d+(?:\.\d+){0,2}(?:[-_+][0-9a-zA-Z._-]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionSegmentRegex();

    [GeneratedRegex(@"(?:--profile-directory|--user-data-dir|--app-id|--app|-p)\s*(?:=|\s)\s*(?:""([^""]+)""|(\S+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DistinctProfileArgRegex();

    public static bool IsIgnoredDesktopName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        string trimmed = name.Trim();
        if (IgnoredExactNames.Contains(trimmed)) return true;
        if (trimmed.StartsWith("~$", StringComparison.Ordinal) ||
            trimmed.StartsWith(".glassfolder-staging-", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith(".tuckpane-merge-staging-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string ext = Path.GetExtension(trimmed);
        return !string.IsNullOrEmpty(ext) && IgnoredTempExtensions.Contains(ext);
    }

    public static bool IsShortcutExtension(string? extension) =>
        string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase);

    public static bool IsGenericShellLauncher(string? normalizedTargetPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedTargetPath)) return true;
        string fileName = Path.GetFileName(normalizedTargetPath.TrimEnd('\\', '/'));
        return string.IsNullOrWhiteSpace(fileName) || GenericShellLaunchers.Contains(fileName);
    }

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0) return string.Empty;
        try
        {
            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    /// <summary>
    /// Normalizes versioned executable paths (e.g. Squirrel "app-1.2.0\App.exe" or Leidian "2.1.23.5_20261120\wujie.exe")
    /// into a stable parent-root + executable key so cross-version shortcut updates still match their organizer item.
    /// Returns null if the path does not contain a versioned directory segment.
    /// </summary>
    public static string? BuildVersionedExecutableKey(string? normalizedTargetPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedTargetPath)) return null;
        if (IsGenericShellLauncher(normalizedTargetPath)) return null;

        string normalized = NormalizePath(normalizedTargetPath);
        string exeName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(exeName) ||
            !exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? dir = Path.GetDirectoryName(normalized);
        if (string.IsNullOrWhiteSpace(dir)) return null;

        string[] parts = dir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        bool replacedAny = false;
        for (int i = 1; i < parts.Length; i++)
        {
            if (VersionSegmentRegex().IsMatch(parts[i]))
            {
                parts[i] = "<ver>";
                replacedAny = true;
            }
        }

        if (!replacedAny) return null;
        return string.Join(Path.DirectorySeparatorChar, parts) + "|" + exeName.ToLowerInvariant();
    }

    /// <summary>
    /// Detects browser/app profile isolation arguments (e.g. Chrome "--profile-directory=Profile 1")
    /// so distinct profile shortcuts targeting the same chrome.exe are never conflated.
    /// </summary>
    public static bool HasConflictingProfileArguments(string? argsA, string? argsB)
    {
        string sigA = ExtractProfileSignature(argsA);
        string sigB = ExtractProfileSignature(argsB);
        if (sigA.Length == 0 && sigB.Length == 0) return false;
        return !string.Equals(sigA, sigB, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractProfileSignature(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return string.Empty;
        MatchCollection matches = DistinctProfileArgRegex().Matches(args);
        if (matches.Count == 0) return string.Empty;
        var tokens = new List<string>(matches.Count);
        foreach (Match match in matches)
            tokens.Add(match.Value.Trim());
        tokens.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join('|', tokens);
    }

    public static DesktopUpdateMatch? FindBestMatch(
        DesktopItemSnapshot candidate,
        IReadOnlyList<OrganizerItemSnapshot> organizerItems,
        IReadOnlyList<DesktopOriginRecord> ledger)
    {
        if (candidate.IsHiddenOrSystem || candidate.IsReparsePoint || IsIgnoredDesktopName(candidate.Name))
            return null;
        if (organizerItems.Count == 0) return null;

        // Never treat an organizer's own storage directory as a desktop duplicate to merge into itself.
        string candidateNormPath = NormalizePath(candidate.FullPath);
        foreach (OrganizerItemSnapshot item in organizerItems)
        {
            if (candidateNormPath.Equals(NormalizePath(item.StorageDirectory), StringComparison.OrdinalIgnoreCase))
                return null;
        }

        if (candidate.IsDirectory)
            return FindBestDirectoryMatch(candidate, organizerItems, ledger);

        if (IsShortcutExtension(candidate.Extension))
            return FindBestShortcutMatch(candidate, organizerItems, ledger);

        return FindBestTrackedFileMatch(candidate, organizerItems, ledger);
    }

    private static DesktopUpdateMatch? FindBestShortcutMatch(
        DesktopItemSnapshot candidate,
        IReadOnlyList<OrganizerItemSnapshot> organizerItems,
        IReadOnlyList<DesktopOriginRecord> ledger)
    {
        string candTarget = NormalizePath(candidate.NormalizedTargetPath);
        string? candVersionKey = BuildVersionedExecutableKey(candTarget);
        string candUrl = (candidate.UrlTarget ?? string.Empty).Trim();

        DesktopUpdateMatch? best = null;
        foreach (OrganizerItemSnapshot item in organizerItems)
        {
            if (item.IsDirectory || !IsShortcutExtension(item.Extension)) continue;
            if (!string.Equals(item.Extension, candidate.Extension, StringComparison.OrdinalIgnoreCase)) continue;

            // Tier 1: Exact same shortcut filename (e.g. "Antigravity.lnk" == "Antigravity.lnk").
            if (string.Equals(item.RelativeName, candidate.Name, StringComparison.OrdinalIgnoreCase))
            {
                // If both specify distinct browser/app profile args, refuse to conflate.
                if (HasConflictingProfileArguments(candidate.NormalizedArguments, item.NormalizedArguments))
                    continue;

                var match = new DesktopUpdateMatch(
                    candidate, item.OrganizerId, item.OrganizerName, item.StorageDirectory,
                    item.RelativeName, item.FullPath, DesktopUpdateMatchKind.ShortcutExactName,
                    Confidence: 100, RenameTargetToDesktopName: false);
                best = PickHigher(best, match, ledger);
                continue;
            }

            // Tier 2 (.url): Exact same URL target (e.g. steam://rungameid/...).
            if (string.Equals(candidate.Extension, ".url", StringComparison.OrdinalIgnoreCase))
            {
                string itemUrl = (item.UrlTarget ?? string.Empty).Trim();
                if (candUrl.Length > 0 && itemUrl.Length > 0 &&
                    string.Equals(candUrl, itemUrl, StringComparison.OrdinalIgnoreCase))
                {
                    var match = new DesktopUpdateMatch(
                        candidate, item.OrganizerId, item.OrganizerName, item.StorageDirectory,
                        item.RelativeName, item.FullPath, DesktopUpdateMatchKind.ShortcutSameUrl,
                        Confidence: 94, RenameTargetToDesktopName: false);
                    best = PickHigher(best, match, ledger);
                }
                continue;
            }

            // Tier 2 & 3 (.lnk): Same target exe OR same versioned install-root + exe name.
            if (candTarget.Length == 0 || IsGenericShellLauncher(candTarget)) continue;
            string itemTarget = NormalizePath(item.NormalizedTargetPath);
            if (itemTarget.Length == 0 || IsGenericShellLauncher(itemTarget)) continue;
            if (HasConflictingProfileArguments(candidate.NormalizedArguments, item.NormalizedArguments)) continue;

            if (string.Equals(candTarget, itemTarget, StringComparison.OrdinalIgnoreCase))
            {
                var match = new DesktopUpdateMatch(
                    candidate, item.OrganizerId, item.OrganizerName, item.StorageDirectory,
                    item.RelativeName, item.FullPath, DesktopUpdateMatchKind.ShortcutSameTarget,
                    Confidence: 95, RenameTargetToDesktopName: false);
                best = PickHigher(best, match, ledger);
                continue;
            }

            if (candVersionKey is not null)
            {
                string? itemVersionKey = BuildVersionedExecutableKey(itemTarget);
                if (itemVersionKey is not null &&
                    string.Equals(candVersionKey, itemVersionKey, StringComparison.OrdinalIgnoreCase))
                {
                    var match = new DesktopUpdateMatch(
                        candidate, item.OrganizerId, item.OrganizerName, item.StorageDirectory,
                        item.RelativeName, item.FullPath, DesktopUpdateMatchKind.ShortcutVersionedTarget,
                        Confidence: 90, RenameTargetToDesktopName: false);
                    best = PickHigher(best, match, ledger);
                }
            }
        }

        return best;
    }

    private static DesktopUpdateMatch? FindBestDirectoryMatch(
        DesktopItemSnapshot candidate,
        IReadOnlyList<OrganizerItemSnapshot> organizerItems,
        IReadOnlyList<DesktopOriginRecord> ledger)
    {
        // Only merge directories that live on User Desktop (not Public Desktop).
        if (candidate.IsPublicDesktop) return null;
        if (candidate.ChildEntryNames.Count == 0) return null;

        var candidateChildren = new HashSet<string>(candidate.ChildEntryNames, StringComparer.OrdinalIgnoreCase);
        DesktopUpdateMatch? best = null;

        foreach (OrganizerItemSnapshot item in organizerItems)
        {
            if (!item.IsDirectory) continue;
            if (!string.Equals(item.RelativeName, candidate.Name, StringComparison.OrdinalIgnoreCase)) continue;

            bool hasStructuralOverlap = item.ChildEntryNames.Any(child => candidateChildren.Contains(child));
            bool hasLedgerRecord = HasMatchingLedgerEntry(candidate.FullPath, item.OrganizerId, item.RelativeName, isDirectory: true, ledger);

            if (hasStructuralOverlap)
            {
                var match = new DesktopUpdateMatch(
                    candidate, item.OrganizerId, item.OrganizerName, item.StorageDirectory,
                    item.RelativeName, item.FullPath, DesktopUpdateMatchKind.DirectoryOverlap,
                    Confidence: hasLedgerRecord ? 99 : 95, RenameTargetToDesktopName: false);
                best = PickHigher(best, match, ledger);
            }
            else if (hasLedgerRecord && candidate.ContainsAppArtifacts)
            {
                var match = new DesktopUpdateMatch(
                    candidate, item.OrganizerId, item.OrganizerName, item.StorageDirectory,
                    item.RelativeName, item.FullPath, DesktopUpdateMatchKind.DirectoryLedgerTracked,
                    Confidence: 92, RenameTargetToDesktopName: false);
                best = PickHigher(best, match, ledger);
            }
        }

        return best;
    }

    private static DesktopUpdateMatch? FindBestTrackedFileMatch(
        DesktopItemSnapshot candidate,
        IReadOnlyList<OrganizerItemSnapshot> organizerItems,
        IReadOnlyList<DesktopOriginRecord> ledger)
    {
        // For non-shortcut regular files, only reconcile executables/installers explicitly tracked in the Desktop Origin Ledger
        // so ordinary user documents with identical names are never silently overwritten.
        if (!ExecutableOrInstallerExtensions.Contains(candidate.Extension)) return null;

        DesktopUpdateMatch? best = null;
        foreach (OrganizerItemSnapshot item in organizerItems)
        {
            if (item.IsDirectory) continue;
            if (!string.Equals(item.RelativeName, candidate.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (!HasMatchingLedgerEntry(candidate.FullPath, item.OrganizerId, item.RelativeName, isDirectory: false, ledger))
                continue;

            var match = new DesktopUpdateMatch(
                candidate, item.OrganizerId, item.OrganizerName, item.StorageDirectory,
                item.RelativeName, item.FullPath, DesktopUpdateMatchKind.TrackedFileUpdate,
                Confidence: 88, RenameTargetToDesktopName: false);
            best = PickHigher(best, match, ledger);
        }

        return best;
    }

    private static bool HasMatchingLedgerEntry(
        string desktopPath,
        Guid organizerId,
        string relativeName,
        bool isDirectory,
        IReadOnlyList<DesktopOriginRecord> ledger)
    {
        string normalizedDesktop = NormalizePath(desktopPath);
        foreach (DesktopOriginRecord entry in ledger)
        {
            if (entry.OrganizerId != organizerId || entry.IsDirectory != isDirectory) continue;
            if (string.Equals(NormalizePath(entry.OriginalDesktopPath), normalizedDesktop, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.RelativeName, relativeName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static DesktopUpdateMatch PickHigher(
        DesktopUpdateMatch? current,
        DesktopUpdateMatch candidate,
        IReadOnlyList<DesktopOriginRecord> ledger)
    {
        if (current is null) return candidate;
        if (candidate.Confidence != current.Confidence)
            return candidate.Confidence > current.Confidence ? candidate : current;

        bool candTracked = HasMatchingLedgerEntry(
            candidate.DesktopItem.FullPath, candidate.TargetOrganizerId, candidate.TargetRelativeName,
            candidate.DesktopItem.IsDirectory, ledger);
        bool currTracked = HasMatchingLedgerEntry(
            current.DesktopItem.FullPath, current.TargetOrganizerId, current.TargetRelativeName,
            current.DesktopItem.IsDirectory, ledger);
        if (candTracked != currTracked)
            return candTracked ? candidate : current;

        return current;
    }

    private static readonly string[] CommandExecutableExtensions =
    [
        ".exe",
        ".bat",
        ".cmd",
        ".com",
        ".msi",
        ".ps1",
        ".vbs"
    ];

    /// <summary>
    /// Safely rewrites an old Desktop directory prefix inside registry values such as
    /// InstallLocation, Inno Setup: App Path, InstallSource, UninstallString, ModifyPath,
    /// DisplayIcon (with ",0"), App Paths (Default &amp; semicolon-delimited Path), Run, or Service ImagePath,
    /// preserving surrounding quotes, trailing slashes, icon indices, and command-line arguments.
    /// </summary>
    public static bool TryRewritePathPrefix(
        string? rawValue,
        string oldPrefix,
        string newPrefix,
        out string rewritten)
    {
        rewritten = string.Empty;
        if (string.IsNullOrWhiteSpace(rawValue) ||
            string.IsNullOrWhiteSpace(oldPrefix) ||
            string.IsNullOrWhiteSpace(newPrefix))
        {
            return false;
        }

        string normOld = NormalizePath(oldPrefix);
        string normNew = NormalizePath(newPrefix);
        if (normOld.Length == 0 || normNew.Length == 0 ||
            string.Equals(normOld, normNew, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int searchFrom = 0;
        bool replacedAny = false;
        System.Text.StringBuilder? sb = null;
        int copiedUntil = 0;

        while (searchFrom <= rawValue.Length - normOld.Length)
        {
            int matchIndex = rawValue.IndexOf(normOld, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0) break;

            bool leadingOk = matchIndex == 0 ||
                rawValue[matchIndex - 1] is ('"' or '\'' or ';' or '=' or ' ' or '\t');

            int afterIndex = matchIndex + normOld.Length;
            // Must end on a path boundary (slash, quote, comma for icon index, semicolon for PATH lists,
            // whitespace, or end-of-string) so "Desktop\vpn" never accidentally rewrites "Desktop\vpn2".
            bool trailingOk = afterIndex == rawValue.Length ||
                rawValue[afterIndex] is ('\\' or '/' or '"' or '\'' or ',' or ';' or ' ' or '\t');

            if (leadingOk && trailingOk)
            {
                sb ??= new System.Text.StringBuilder(rawValue.Length + Math.Max(0, normNew.Length - normOld.Length));
                sb.Append(rawValue, copiedUntil, matchIndex - copiedUntil);
                sb.Append(normNew);
                copiedUntil = afterIndex;
                searchFrom = afterIndex;
                replacedAny = true;
            }
            else
            {
                searchFrom = matchIndex + 1;
            }
        }

        if (!replacedAny || sb is null)
            return false;

        sb.Append(rawValue, copiedUntil, rawValue.Length - copiedUntil);
        rewritten = sb.ToString();
        return !string.Equals(rewritten, rawValue, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extracts the filesystem path portion from a registry value (stripping quotes,
    /// semicolon-delimited PATH suffixes, trailing arguments, or ",iconIndex").
    /// </summary>
    public static string ExtractLeadingPath(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return string.Empty;
        string s = rawValue.Trim();
        if (s.StartsWith('"'))
        {
            int endQuote = s.IndexOf('"', 1);
            s = endQuote > 1 ? s[1..endQuote].Trim() : s.Trim('"').Trim();
            return StripTrailingIconIndex(s);
        }

        // For semicolon-delimited PATH values (e.g. App Paths\Path), take the first non-empty entry.
        int semicolonIndex = s.IndexOf(';');
        if (semicolonIndex >= 0)
        {
            foreach (string part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmedPart = part.Trim().Trim('"');
                if (trimmedPart.Length > 0)
                {
                    s = trimmedPart;
                    break;
                }
            }
        }

        // Strip trailing ",0" or ",-101" icon index if present.
        s = StripTrailingIconIndex(s);

        // If unquoted command line with an executable extension followed by whitespace arguments,
        // truncate right after the executable extension.
        int bestCut = -1;
        foreach (string ext in CommandExecutableExtensions)
        {
            int searchFrom = 0;
            while (searchFrom < s.Length)
            {
                int idx = s.IndexOf(ext, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (idx <= 0) break;
                int endIdx = idx + ext.Length;
                if (endIdx < s.Length && s[endIdx] is ' ' or '\t')
                {
                    if (bestCut < 0 || endIdx < bestCut)
                        bestCut = endIdx;
                    break;
                }
                searchFrom = endIdx;
            }
        }

        if (bestCut > 0)
            s = s[..bestCut].Trim();

        return s;
    }

    private static string StripTrailingIconIndex(string value)
    {
        int commaIndex = value.LastIndexOf(',');
        if (commaIndex > 2 && int.TryParse(value[(commaIndex + 1)..].Trim(), out _))
            return value[..commaIndex].Trim();
        return value;
    }
}
