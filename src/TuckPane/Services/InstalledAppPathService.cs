using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using TuckPane.Core;
using TuckPane.Models;

namespace TuckPane.Services;

internal sealed record OrganizerFolderMapping(
    string OldDesktopPrefix,
    string NewOrganizerPrefix,
    Guid OrganizerId,
    string OrganizerName,
    int Depth);

internal sealed record ElevatedMergeDirective(
    string SourceDirectory,
    string TargetDirectory,
    IReadOnlyList<string> StopServiceNames,
    IReadOnlyList<int> StopProcessIds,
    IReadOnlyList<string> RelaunchTargetExecutables);

internal sealed record ElevatedRepairOutcome(
    bool Succeeded,
    bool UserCancelled,
    int RepairedRegistryCount,
    int RepairedServiceCount,
    int MergedFilesCount,
    string? BackupDirectory,
    string? ErrorMessage,
    IReadOnlyList<string> RelaunchedExecutables);

/// <summary>
/// Detects installed applications (Inno Setup, NSIS, MSI, App Paths, Run auto-start, Windows Services)
/// whose registered install/update/launch path still points to an old User Desktop directory after the
/// folder was moved into a TuckPane organizer, silently heals HKCU keys, and provides a safe, .reg-backed
/// UAC-elevated repair + locked-folder merge + Public Desktop cleanup workflow for HKLM keys and Windows Services.
/// </summary>
internal static class InstalledAppPathService
{
    internal const string DefaultRegistryValueName = "(Default)";

    private static readonly string[] UninstallValueNames =
    [
        "InstallLocation",
        "Inno Setup: App Path",
        "InstallSource",
        "UninstallString",
        "QuietUninstallString",
        "ModifyPath",
        "DisplayIcon"
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    internal static IReadOnlyList<OrganizerFolderMapping> BuildFolderMappings(
        AppStateV2 state,
        string? desktopRootOverride = null)
    {
        string desktopRoot = DesktopUpdateMatchRules.NormalizePath(desktopRootOverride ?? AppPaths.DesktopRoot);
        if (string.IsNullOrWhiteSpace(desktopRoot)) return [];

        var mappings = new List<OrganizerFolderMapping>();
        var depth1Entries = new List<(string DirPath, Guid OrganizerId, string OrganizerName)>();
        var depth1DesktopPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDepth1Pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: map depth-1 organizer directories: Organizer\<folder> -> Desktop\<folder>.
        foreach (OrganizerDefinition organizer in state.Organizers)
        {
            string storageDir = DesktopUpdateMatchRules.NormalizePath(AppPaths.ResolveStoragePath(organizer));
            if (string.IsNullOrWhiteSpace(storageDir) || !Directory.Exists(storageDir)) continue;
            if (storageDir.Equals(desktopRoot, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                foreach (string childDir in Directory.EnumerateDirectories(storageDir, "*", SearchOption.TopDirectoryOnly))
                {
                    string folderName = Path.GetFileName(childDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (IsSkippedDirectory(childDir, folderName)) continue;

                    string oldDesktopPrefix = DesktopUpdateMatchRules.NormalizePath(Path.Combine(desktopRoot, folderName));
                    string newOrganizerPrefix = DesktopUpdateMatchRules.NormalizePath(childDir);
                    depth1Entries.Add((newOrganizerPrefix, organizer.Id, organizer.Name));
                    depth1DesktopPrefixes.Add(oldDesktopPrefix);

                    string pairKey = oldDesktopPrefix + "|" + newOrganizerPrefix;
                    if (seenDepth1Pairs.Add(pairKey))
                    {
                        mappings.Add(new OrganizerFolderMapping(
                            oldDesktopPrefix,
                            newOrganizerPrefix,
                            organizer.Id,
                            organizer.Name,
                            Depth: 1));
                    }
                }
            }
            catch
            {
                // Ignore inaccessible organizer storage directories.
            }
        }

        // Pass 2: map depth-2 nested app directories: Organizer\<group>\<app> -> Desktop\<app>.
        // Deduplicate so depth-1 matches always take priority over depth-2 when both exist.
        var seenDepth2DesktopPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string groupDir, Guid organizerId, string organizerName) in depth1Entries)
        {
            try
            {
                foreach (string nestedDir in Directory.EnumerateDirectories(groupDir, "*", SearchOption.TopDirectoryOnly))
                {
                    string appName = Path.GetFileName(nestedDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (IsSkippedDirectory(nestedDir, appName)) continue;

                    string oldDesktopPrefix = DesktopUpdateMatchRules.NormalizePath(Path.Combine(desktopRoot, appName));
                    if (depth1DesktopPrefixes.Contains(oldDesktopPrefix)) continue;
                    if (!seenDepth2DesktopPrefixes.Add(oldDesktopPrefix)) continue;

                    string newOrganizerPrefix = DesktopUpdateMatchRules.NormalizePath(nestedDir);
                    mappings.Add(new OrganizerFolderMapping(
                        oldDesktopPrefix,
                        newOrganizerPrefix,
                        organizerId,
                        organizerName,
                        Depth: 2));
                }
            }
            catch
            {
                // Ignore inaccessible group directories.
            }
        }

        return mappings;
    }

    internal static IReadOnlyList<InstalledAppPathMismatch> ScanMismatches(
        AppStateV2 state,
        string? desktopRootOverride = null)
    {
        IReadOnlyList<OrganizerFolderMapping> folderMappings = BuildFolderMappings(state, desktopRootOverride);
        if (folderMappings.Count == 0) return [];

        var mismatches = new List<InstalledAppPathMismatch>();
        ScanUninstallHive(Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Uninstall", folderMappings, mismatches);
        ScanUninstallHive(Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\Uninstall", folderMappings, mismatches);
        ScanUninstallHive(Registry.LocalMachine, "HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", folderMappings, mismatches);
        ScanAppPathsHive(Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\App Paths", folderMappings, mismatches);
        ScanAppPathsHive(Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\App Paths", folderMappings, mismatches);
        ScanRunHive(Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", folderMappings, mismatches);
        ScanRunHive(Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run", folderMappings, mismatches);
        ScanRunHive(Registry.LocalMachine, "HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", folderMappings, mismatches);
        ScanServicesHive(folderMappings, mismatches);
        return mismatches;
    }

    internal static int RepairCurrentUserMismatches(IReadOnlyList<InstalledAppPathMismatch> mismatches)
    {
        InstalledAppPathMismatch[] hkcuItems = mismatches
            .Where(m => !m.IsService && string.Equals(m.RegistryHive, "HKCU", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (hkcuItems.Length == 0) return 0;

        TryWriteBackupManifest("hkcu", hkcuItems);

        int repaired = 0;
        foreach (IGrouping<string, InstalledAppPathMismatch> group in hkcuItems.GroupBy(m => m.RegistrySubKeyPath, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(group.Key, writable: true);
                if (key is null) continue;
                foreach (InstalledAppPathMismatch item in group)
                {
                    string regValueName = IsDefaultRegistryValueName(item.ValueName) ? string.Empty : item.ValueName;
                    if (key.GetValue(regValueName) is string current &&
                        string.Equals(current, item.OldValue, StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue(regValueName, item.NewValue, key.GetValueKind(regValueName));
                        repaired++;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"修复 HKCU 安装路径失败：{group.Key}", ex);
            }
        }
        return repaired;
    }

    /// <summary>
    /// Executes a user-confirmed UAC-elevated repair plan covering HKLM Uninstall/App Paths/Run registry paths,
    /// Windows Service ImagePaths, optional locked Desktop->Organizer folder merges, and optional Public Desktop
    /// duplicate file cleanups. Passes all parameters via a structured JSON plan file (zero shell string
    /// interpolation) and automatically exports .reg backups before modifying any registry key.
    /// </summary>
    internal static async Task<ElevatedRepairOutcome> ApplyElevatedRepairAndMergeAsync(
        IReadOnlyList<InstalledAppPathMismatch> mismatches,
        IReadOnlyList<ElevatedMergeDirective> mergeDirectives,
        IEnumerable<string>? shortcutSearchRoots,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? cleanupFilePaths = null)
    {
        string[] cleanups = cleanupFilePaths is { Count: > 0 }
            ? cleanupFilePaths
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .Select(DesktopUpdateMatchRules.NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        if (mismatches.Count == 0 && mergeDirectives.Count == 0 && cleanups.Length == 0)
            return new(true, false, 0, 0, 0, null, null, []);

        string jobId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        string jobRoot = Path.Combine(AppPaths.LocalRoot, "updates", "repair-jobs", jobId);
        string backupDir = Path.Combine(AppPaths.LocalRoot, "updates", "registry-backups", jobId);
        Directory.CreateDirectory(jobRoot);
        Directory.CreateDirectory(backupDir);

        string planPath = Path.Combine(jobRoot, "plan.json");
        string resultPath = Path.Combine(jobRoot, "result.json");
        string scriptPath = Path.Combine(jobRoot, "apply-repair.ps1");

        var plan = new
        {
            BackupDirectory = backupDir,
            ResultPath = resultPath,
            RegistryEntries = mismatches.Where(m => !m.IsService).Select(m => new
            {
                m.RegistryHive,
                m.RegistrySubKeyPath,
                m.ValueName,
                m.OldValue,
                m.NewValue
            }).ToArray(),
            ServiceEntries = mismatches.Where(m => m.IsService && !string.IsNullOrWhiteSpace(m.ServiceName)).Select(m => new
            {
                ServiceName = m.ServiceName!,
                m.RegistrySubKeyPath,
                m.OldValue,
                m.NewValue
            }).ToArray(),
            MergeDirectives = mergeDirectives.Select(d => new
            {
                d.SourceDirectory,
                d.TargetDirectory,
                d.StopServiceNames,
                d.StopProcessIds
            }).ToArray(),
            CleanupFilePaths = cleanups
        };

        string scriptContent = BuildElevatedRepairScript().Replace("\r\n", "\n").Replace("\n", "\r\n");
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, SerializerOptions), new UTF8Encoding(true), cancellationToken);
        await File.WriteAllTextAsync(scriptPath, scriptContent, new UTF8Encoding(true), cancellationToken);

        string psExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(psExe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" -PlanPath \"{planPath}\""
        };

        Process? proc;
        try
        {
            proc = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED (user clicked No on UAC)
        {
            return new(false, UserCancelled: true, 0, 0, 0, backupDir, null, []);
        }

        if (proc is null)
            return new(false, false, 0, 0, 0, backupDir, "无法启动提权修复进程。", []);

        using (proc)
        {
            await proc.WaitForExitAsync(cancellationToken);
        }

        if (!File.Exists(resultPath))
            return new(false, false, 0, 0, 0, backupDir, "提权修复未返回结果文件。", []);

        string resultJson = await File.ReadAllTextAsync(resultPath, cancellationToken);
        using JsonDocument doc = JsonDocument.Parse(resultJson);
        JsonElement root = doc.RootElement;
        bool succeeded = root.TryGetProperty("Succeeded", out JsonElement sEl) && sEl.GetBoolean();
        int regCount = root.TryGetProperty("RepairedRegistryCount", out JsonElement rEl) ? rEl.GetInt32() : 0;
        int svcCount = root.TryGetProperty("RepairedServiceCount", out JsonElement svEl) ? svEl.GetInt32() : 0;
        int mergedCount = root.TryGetProperty("MergedFilesCount", out JsonElement mEl) ? mEl.GetInt32() : 0;
        string? errMsg = root.TryGetProperty("ErrorMessage", out JsonElement eEl) && eEl.ValueKind == JsonValueKind.String ? eEl.GetString() : null;

        // Rebind shortcuts and relaunch user GUI apps non-elevated from their new organizer location!
        var relaunched = new List<string>();
        if (succeeded)
        {
            foreach (ElevatedMergeDirective directive in mergeDirectives)
            {
                _ = DesktopUpdateMergeEngine.RebindShortcutsPointingToPrefix(
                    directive.SourceDirectory,
                    directive.TargetDirectory,
                    shortcutSearchRoots);
                ShellChangeNotificationService.NotifyMoved(directive.SourceDirectory, directive.TargetDirectory);

                foreach (string targetExe in directive.RelaunchTargetExecutables.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(targetExe)) continue;
                    try
                    {
                        var relaunchInfo = new ProcessStartInfo(targetExe)
                        {
                            UseShellExecute = true,
                            WorkingDirectory = Path.GetDirectoryName(targetExe) ?? directive.TargetDirectory
                        };
                        _ = Process.Start(relaunchInfo);
                        relaunched.Add(targetExe);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error($"合并后重新启动应用失败：{targetExe}", ex);
                    }
                }
            }
        }

        return new(succeeded, false, regCount, svcCount, mergedCount, backupDir, errMsg, relaunched);
    }

    private static bool IsDefaultRegistryValueName(string? valueName) =>
        string.IsNullOrEmpty(valueName) ||
        string.Equals(valueName, DefaultRegistryValueName, StringComparison.OrdinalIgnoreCase);

    private static bool IsSkippedDirectory(string dirPath, string dirName)
    {
        if (string.IsNullOrWhiteSpace(dirName) || DesktopUpdateMatchRules.IsIgnoredDesktopName(dirName))
            return true;
        try
        {
            FileAttributes attrs = File.GetAttributes(dirPath);
            if ((attrs & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                return true;
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void ScanUninstallHive(
        RegistryKey hiveRoot,
        string hiveName,
        string uninstallRootPath,
        IReadOnlyList<OrganizerFolderMapping> mappings,
        List<InstalledAppPathMismatch> output)
    {
        try
        {
            using RegistryKey? uninstallRoot = hiveRoot.OpenSubKey(uninstallRootPath);
            if (uninstallRoot is null) return;

            foreach (string subKeyName in uninstallRoot.GetSubKeyNames())
            {
                string subKeyPath = uninstallRootPath + "\\" + subKeyName;
                try
                {
                    using RegistryKey? appKey = uninstallRoot.OpenSubKey(subKeyName);
                    if (appKey is null) continue;

                    string displayName = appKey.GetValue("DisplayName") as string ?? subKeyName;
                    string displayVersion = appKey.GetValue("DisplayVersion") as string ?? string.Empty;

                    foreach (string valueName in UninstallValueNames)
                    {
                        if (appKey.GetValue(valueName) is not string rawValue || string.IsNullOrWhiteSpace(rawValue))
                            continue;

                        foreach (OrganizerFolderMapping map in mappings)
                        {
                            if (!DesktopUpdateMatchRules.TryRewritePathPrefix(rawValue, map.OldDesktopPrefix, map.NewOrganizerPrefix, out string rewritten))
                                continue;

                            // Confirm the relocated folder/binary (or at least its immediate app subfolder) exists in the organizer!
                            if (!TargetPathExistsInOrganizer(rewritten, map.NewOrganizerPrefix))
                                continue;

                            output.Add(new InstalledAppPathMismatch(
                                hiveName,
                                subKeyPath,
                                displayName,
                                displayVersion,
                                valueName,
                                rawValue,
                                rewritten,
                                map.OldDesktopPrefix,
                                map.NewOrganizerPrefix,
                                IsService: false,
                                ServiceName: null,
                                map.OrganizerId,
                                map.OrganizerName));
                            break;
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible uninstall key.
                }
            }
        }
        catch
        {
            // Ignore registry hive read failures.
        }
    }

    private static void ScanAppPathsHive(
        RegistryKey hiveRoot,
        string hiveName,
        string appPathsRootPath,
        IReadOnlyList<OrganizerFolderMapping> mappings,
        List<InstalledAppPathMismatch> output)
    {
        try
        {
            using RegistryKey? appPathsRoot = hiveRoot.OpenSubKey(appPathsRootPath);
            if (appPathsRoot is null) return;

            (string RegValueName, string ReportedValueName)[] targets =
            [
                (string.Empty, DefaultRegistryValueName),
                ("Path", "Path")
            ];

            foreach (string subKeyName in appPathsRoot.GetSubKeyNames())
            {
                string subKeyPath = appPathsRootPath + "\\" + subKeyName;
                try
                {
                    using RegistryKey? appKey = appPathsRoot.OpenSubKey(subKeyName);
                    if (appKey is null) continue;

                    string displayName = Path.GetFileNameWithoutExtension(subKeyName);
                    if (string.IsNullOrWhiteSpace(displayName)) displayName = subKeyName;

                    foreach ((string regValueName, string reportedValueName) in targets)
                    {
                        if (appKey.GetValue(regValueName) is not string rawValue || string.IsNullOrWhiteSpace(rawValue))
                            continue;

                        foreach (OrganizerFolderMapping map in mappings)
                        {
                            if (!DesktopUpdateMatchRules.TryRewritePathPrefix(rawValue, map.OldDesktopPrefix, map.NewOrganizerPrefix, out string rewritten))
                                continue;
                            if (!TargetPathExistsInOrganizer(rewritten, map.NewOrganizerPrefix))
                                continue;

                            output.Add(new InstalledAppPathMismatch(
                                hiveName,
                                subKeyPath,
                                displayName,
                                string.Empty,
                                reportedValueName,
                                rawValue,
                                rewritten,
                                map.OldDesktopPrefix,
                                map.NewOrganizerPrefix,
                                IsService: false,
                                ServiceName: null,
                                map.OrganizerId,
                                map.OrganizerName));
                            break;
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible App Paths subkey.
                }
            }
        }
        catch
        {
            // Ignore App Paths hive read failures.
        }
    }

    private static void ScanRunHive(
        RegistryKey hiveRoot,
        string hiveName,
        string runRootPath,
        IReadOnlyList<OrganizerFolderMapping> mappings,
        List<InstalledAppPathMismatch> output)
    {
        try
        {
            using RegistryKey? runKey = hiveRoot.OpenSubKey(runRootPath);
            if (runKey is null) return;

            foreach (string valueName in runKey.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName)) continue;
                try
                {
                    if (runKey.GetValue(valueName) is not string rawValue || string.IsNullOrWhiteSpace(rawValue))
                        continue;

                    foreach (OrganizerFolderMapping map in mappings)
                    {
                        if (!DesktopUpdateMatchRules.TryRewritePathPrefix(rawValue, map.OldDesktopPrefix, map.NewOrganizerPrefix, out string rewritten))
                            continue;
                        if (!TargetPathExistsInOrganizer(rewritten, map.NewOrganizerPrefix))
                            continue;

                        output.Add(new InstalledAppPathMismatch(
                            hiveName,
                            runRootPath,
                            valueName,
                            string.Empty,
                            valueName,
                            rawValue,
                            rewritten,
                            map.OldDesktopPrefix,
                            map.NewOrganizerPrefix,
                            IsService: false,
                            ServiceName: null,
                            map.OrganizerId,
                            map.OrganizerName));
                        break;
                    }
                }
                catch
                {
                    // Skip inaccessible Run value.
                }
            }
        }
        catch
        {
            // Ignore Run hive read failures.
        }
    }

    private static void ScanServicesHive(
        IReadOnlyList<OrganizerFolderMapping> mappings,
        List<InstalledAppPathMismatch> output)
    {
        const string servicesPath = @"SYSTEM\CurrentControlSet\Services";
        try
        {
            using RegistryKey? servicesRoot = Registry.LocalMachine.OpenSubKey(servicesPath);
            if (servicesRoot is null) return;

            foreach (string serviceName in servicesRoot.GetSubKeyNames())
            {
                string subKeyPath = servicesPath + "\\" + serviceName;
                try
                {
                    using RegistryKey? svcKey = servicesRoot.OpenSubKey(serviceName);
                    if (svcKey?.GetValue("ImagePath") is not string rawImagePath || string.IsNullOrWhiteSpace(rawImagePath))
                        continue;

                    string displayName = svcKey.GetValue("DisplayName") as string ?? serviceName;
                    foreach (OrganizerFolderMapping map in mappings)
                    {
                        if (!DesktopUpdateMatchRules.TryRewritePathPrefix(rawImagePath, map.OldDesktopPrefix, map.NewOrganizerPrefix, out string rewritten))
                            continue;
                        if (!TargetPathExistsInOrganizer(rewritten, map.NewOrganizerPrefix))
                            continue;

                        output.Add(new InstalledAppPathMismatch(
                            "HKLM",
                            subKeyPath,
                            displayName,
                            string.Empty,
                            "ImagePath",
                            rawImagePath,
                            rewritten,
                            map.OldDesktopPrefix,
                            map.NewOrganizerPrefix,
                            IsService: true,
                            ServiceName: serviceName,
                            map.OrganizerId,
                            map.OrganizerName));
                        break;
                    }
                }
                catch
                {
                    // Skip inaccessible service key.
                }
            }
        }
        catch
        {
            // Ignore services hive read failures.
        }
    }

    internal static bool TargetPathExistsInOrganizer(string rewrittenRawValue, string organizerFolderPrefix)
    {
        string normPrefix = DesktopUpdateMatchRules.NormalizePath(organizerFolderPrefix);
        if (string.IsNullOrWhiteSpace(normPrefix)) return false;

        string? candidate = ExtractPathAtPrefix(rewrittenRawValue, normPrefix);
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = DesktopUpdateMatchRules.ExtractLeadingPath(rewrittenRawValue);

        string leading = Environment.ExpandEnvironmentVariables(candidate);
        if (string.IsNullOrWhiteSpace(leading)) return false;

        string normalizedLeading = DesktopUpdateMatchRules.NormalizePath(leading);
        if (!normalizedLeading.Equals(normPrefix, StringComparison.OrdinalIgnoreCase) &&
            !normalizedLeading.StartsWith(normPrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (File.Exists(normalizedLeading) || Directory.Exists(normalizedLeading))
            return true;

        // If the specific executable is inside a subfolder (e.g. Organizer\vpn\Bettbox\unins000.exe) and
        // Bettbox folder already exists inside Organizer\vpn, accept it as a valid relocated app directory.
        string? parent = Path.GetDirectoryName(normalizedLeading);
        return !string.IsNullOrWhiteSpace(parent) &&
            (parent.Equals(normPrefix, StringComparison.OrdinalIgnoreCase) ||
             parent.StartsWith(normPrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) &&
            Directory.Exists(parent);
    }

    private static string? ExtractPathAtPrefix(string rewrittenRawValue, string organizerFolderPrefix)
    {
        int searchFrom = 0;
        while (searchFrom <= rewrittenRawValue.Length - organizerFolderPrefix.Length)
        {
            int idx = rewrittenRawValue.IndexOf(organizerFolderPrefix, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            bool leadingOk = idx == 0 ||
                rewrittenRawValue[idx - 1] is ('"' or '\'' or ';' or '=' or ' ' or '\t');
            int afterIdx = idx + organizerFolderPrefix.Length;
            bool trailingOk = afterIdx == rewrittenRawValue.Length ||
                rewrittenRawValue[afterIdx] is ('\\' or '/' or '"' or '\'' or ',' or ';' or ' ' or '\t');

            if (!leadingOk || !trailingOk)
            {
                searchFrom = idx + 1;
                continue;
            }

            if (idx > 0 && rewrittenRawValue[idx - 1] == '"')
            {
                int endQuote = rewrittenRawValue.IndexOf('"', idx);
                string segment = endQuote > idx
                    ? rewrittenRawValue[idx..endQuote]
                    : rewrittenRawValue[idx..];
                return DesktopUpdateMatchRules.ExtractLeadingPath(segment);
            }

            return DesktopUpdateMatchRules.ExtractLeadingPath(rewrittenRawValue[idx..]);
        }

        return null;
    }

    private static void TryWriteBackupManifest(string tag, IReadOnlyList<InstalledAppPathMismatch> items)
    {
        try
        {
            string dir = Path.Combine(AppPaths.LocalRoot, "updates", "registry-backups");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{tag}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(items, SerializerOptions));
        }
        catch
        {
            // Non-fatal backup write failure.
        }
    }

    internal static string BuildElevatedRepairScript() =>
@"param([Parameter(Mandatory)][string]$PlanPath)
$ErrorActionPreference = 'Stop'
$plan = Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
$repairedReg = 0
$repairedSvc = 0
$mergedFiles = 0
$stoppedServices = New-Object System.Collections.Generic.List[string]
$errMsg = $null

function Export-KeyBackup([string]$hive, [string]$subKey, [string]$backupDir, [int]$idx) {
    $regPath = ""$hive\$subKey""
    $outFile = Join-Path $backupDir (""backup-{0:D3}.reg"" -f $idx)
    & reg.exe export $regPath $outFile /y | Out-Null
}

try {
    New-Item -ItemType Directory -Path $plan.BackupDirectory -Force | Out-Null
    $bkIdx = 0
    $exportedKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    if ($null -ne $plan.RegistryEntries) {
        foreach ($e in $plan.RegistryEntries) {
            $k = ""$($e.RegistryHive)\$($e.RegistrySubKeyPath)""
            if ($exportedKeys.Add($k)) { Export-KeyBackup $e.RegistryHive $e.RegistrySubKeyPath $plan.BackupDirectory ($bkIdx++) }
        }
    }
    if ($null -ne $plan.ServiceEntries) {
        foreach ($s in $plan.ServiceEntries) {
            $k = ""HKLM\$($s.RegistrySubKeyPath)""
            if ($exportedKeys.Add($k)) { Export-KeyBackup 'HKLM' $s.RegistrySubKeyPath $plan.BackupDirectory ($bkIdx++) }
        }
    }

    # 1. Stop locking services & processes for locked folder merges
    if ($null -ne $plan.MergeDirectives) {
        foreach ($m in $plan.MergeDirectives) {
            if ($null -ne $m.StopServiceNames) {
                foreach ($svcName in $m.StopServiceNames) {
                    try {
                        $svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
                        if ($svc -and $svc.Status -ne 'Stopped') {
                            Stop-Service -Name $svcName -Force -ErrorAction SilentlyContinue
                            $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(10))
                            if (-not $stoppedServices.Contains($svcName)) { $stoppedServices.Add($svcName) }
                        }
                    } catch {}
                }
            }
            if ($null -ne $m.StopProcessIds) {
                foreach ($procId in $m.StopProcessIds) {
                    try {
                        $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
                        if ($p) {
                            if ($p.MainWindowHandle -ne [IntPtr]::Zero) { [void]$p.CloseMainWindow(); [void]$p.WaitForExit(2500) }
                            if (-not $p.HasExited) {
                                Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
                                [void]$p.WaitForExit(3000)
                            }
                        }
                    } catch {}
                }
            }
            Start-Sleep -Milliseconds 800

            # 2. Recursive file-by-file overwrite merge preserving unrelated sibling folders
            if (Test-Path -LiteralPath $m.SourceDirectory) {
                $srcRoot = [IO.Path]::GetFullPath($m.SourceDirectory).TrimEnd('\')
                $dstRoot = [IO.Path]::GetFullPath($m.TargetDirectory).TrimEnd('\')
                New-Item -ItemType Directory -Path $dstRoot -Force | Out-Null
                foreach ($f in (Get-ChildItem -LiteralPath $srcRoot -File -Recurse -Force)) {
                    $rel = $f.FullName.Substring($srcRoot.Length).TrimStart('\')
                    $dst = Join-Path $dstRoot $rel
                    $dstDir = Split-Path -Parent $dst
                    if (-not (Test-Path -LiteralPath $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
                    if (Test-Path -LiteralPath $dst) {
                        try { (Get-Item -LiteralPath $dst -Force).Attributes = 'Normal' } catch {}
                    }
                    Copy-Item -LiteralPath $f.FullName -Destination $dst -Force
                    $mergedFiles++
                    try { $f.Attributes = 'Normal' } catch {}
                    Remove-Item -LiteralPath $f.FullName -Force
                }
                # Remove empty directories bottom-up
                $subDirs = @(Get-ChildItem -LiteralPath $srcRoot -Directory -Recurse -Force | Sort-Object { $_.FullName.Length } -Descending)
                foreach ($d in $subDirs) {
                    if (@(Get-ChildItem -LiteralPath $d.FullName -Force).Count -eq 0) {
                        Remove-Item -LiteralPath $d.FullName -Force
                    }
                }
                if (@(Get-ChildItem -LiteralPath $srcRoot -Force).Count -eq 0) {
                    Remove-Item -LiteralPath $srcRoot -Force
                }
            }
        }
    }

    # 3. Clean up already-merged Public Desktop duplicate files awaiting UAC deletion
    if ($null -ne $plan.CleanupFilePaths) {
        foreach ($p in $plan.CleanupFilePaths) {
            if ([string]::IsNullOrWhiteSpace($p)) { continue }
            if (Test-Path -LiteralPath $p -PathType Leaf) {
                try { (Get-Item -LiteralPath $p -Force).Attributes = 'Normal' } catch {}
                Remove-Item -LiteralPath $p -Force
                $mergedFiles++
            }
        }
    }

    # 4. Rebind registry Uninstall / App Paths / Run entries
    if ($null -ne $plan.RegistryEntries) {
        foreach ($e in $plan.RegistryEntries) {
            $psPath = if ($e.RegistryHive -eq 'HKCU') { ""Registry::HKEY_CURRENT_USER\$($e.RegistrySubKeyPath)"" } else { ""Registry::HKEY_LOCAL_MACHINE\$($e.RegistrySubKeyPath)"" }
            if (Test-Path -LiteralPath $psPath) {
                if ([string]::IsNullOrEmpty($e.ValueName) -or $e.ValueName -eq '(Default)') {
                    Set-ItemProperty -LiteralPath $psPath -Name '(Default)' -Value $e.NewValue
                } else {
                    Set-ItemProperty -LiteralPath $psPath -Name $e.ValueName -Value $e.NewValue
                }
                $repairedReg++
            }
        }
    }

    # 5. Rebind Windows Service ImagePaths via SCM (sc.exe config takes effect immediately without reboot)
    if ($null -ne $plan.ServiceEntries) {
        foreach ($s in $plan.ServiceEntries) {
            $svc = Get-Service -Name $s.ServiceName -ErrorAction SilentlyContinue
            $wasRunning = $stoppedServices.Contains($s.ServiceName)
            if ($svc -and $svc.Status -ne 'Stopped') {
                Stop-Service -Name $s.ServiceName -Force -ErrorAction SilentlyContinue
                try { $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(10)) } catch {}
                $wasRunning = $true
            }
            $binVal = $s.NewValue
            if ($binVal -match '\s' -and -not $binVal.StartsWith('""')) { $binVal = '""' + $binVal + '""' }
            $scResult = & sc.exe config $s.ServiceName binPath= $binVal 2>&1
            if ($LASTEXITCODE -eq 0) {
                $repairedSvc++
            } else {
                $psPath = ""Registry::HKEY_LOCAL_MACHINE\$($s.RegistrySubKeyPath)""
                if (Test-Path -LiteralPath $psPath) {
                    Set-ItemProperty -LiteralPath $psPath -Name 'ImagePath' -Value $s.NewValue
                    $repairedSvc++
                }
            }
            if ($wasRunning) {
                try { Start-Service -Name $s.ServiceName -ErrorAction SilentlyContinue } catch {}
            }
        }
    }

    @{
        Succeeded = $true
        RepairedRegistryCount = $repairedReg
        RepairedServiceCount = $repairedSvc
        MergedFilesCount = $mergedFiles
        ErrorMessage = $null
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $plan.ResultPath -Encoding UTF8
}
catch {
    $errMsg = $_.Exception.Message
    @{
        Succeeded = $false
        RepairedRegistryCount = $repairedReg
        RepairedServiceCount = $repairedSvc
        MergedFilesCount = $mergedFiles
        ErrorMessage = $errMsg
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $plan.ResultPath -Encoding UTF8
    exit 1
}
";
}
