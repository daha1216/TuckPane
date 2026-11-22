using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using TuckPane.Core;
using TuckPane.Models;

namespace TuckPane.Services;

internal sealed record LockedProcessInfo(int ProcessId, string ProcessName, string ExecutablePath, bool IsServiceHost);

internal sealed record DesktopUpdateLockInfo(
    IReadOnlyList<LockedProcessInfo> Processes,
    IReadOnlyList<string> RunningServices)
{
    internal bool HasLocks => Processes.Count > 0 || RunningServices.Count > 0;
}

internal enum DesktopUpdateMergeStatus
{
    Merged = 0,
    DeduplicatedIdentical = 1,
    SkippedBusyWriting = 2,
    BlockedByLock = 3,
    MergedPendingSourceCleanup = 4,
    Failed = 5
}

internal sealed record DesktopUpdateMergeResult(
    DesktopUpdateMatch Match,
    DesktopUpdateMergeStatus Status,
    int FilesMerged,
    DesktopUpdateLockInfo? LockInfo = null,
    string? ErrorMessage = null);

/// <summary>
/// Inspects desktop/organizer items, checks installer write quiescence and running process/service locks,
/// and performs atomic in-place shortcut replacement or recursive sibling-preserving folder overwrite-merges.
/// </summary>
internal static class DesktopUpdateMergeEngine
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceContinuePending = 0x00000005;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr hService, out SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    internal static DesktopItemSnapshot? InspectDesktopEntry(string fullPath, bool isPublicDesktop)
    {
        try
        {
            string normalized = DesktopUpdateMatchRules.NormalizePath(fullPath);
            if (string.IsNullOrWhiteSpace(normalized)) return null;
            string name = Path.GetFileName(normalized);
            if (DesktopUpdateMatchRules.IsIgnoredDesktopName(name)) return null;

            FileAttributes attrs = File.GetAttributes(normalized);
            bool isDir = (attrs & FileAttributes.Directory) != 0;
            bool isHiddenOrSystem = (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0;
            bool isReparse = (attrs & FileAttributes.ReparsePoint) != 0 &&
                !ShellFileOperationService.IsSafeCloudOrAppAliasReparsePoint(normalized);

            string ext = isDir ? string.Empty : Path.GetExtension(name);
            string? targetPath = null;
            string? args = null;
            string? urlTarget = null;
            List<string> children = [];
            bool containsAppArtifacts = false;

            if (isDir && !isHiddenOrSystem && !isReparse)
            {
                InspectDirectorySummary(normalized, children, out containsAppArtifacts);
            }
            else if (!isDir && ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                ShortcutInspection? link = ShortcutIntegrityService.InspectLink(normalized);
                if (link is not null && !link.IsVirtualShellTarget)
                {
                    targetPath = link.ExpandedTargetPath;
                    args = link.Arguments;
                }
            }
            else if (!isDir && ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                urlTarget = TryReadInternetShortcutUrl(normalized);
            }

            return new DesktopItemSnapshot(
                normalized,
                name,
                ext,
                isDir,
                isPublicDesktop,
                isHiddenOrSystem,
                isReparse,
                targetPath,
                args,
                urlTarget,
                children,
                containsAppArtifacts);
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyList<OrganizerItemSnapshot> BuildOrganizerInventory(AppStateV2 state)
    {
        var result = new List<OrganizerItemSnapshot>();
        foreach (OrganizerDefinition organizer in state.Organizers)
        {
            string storageDir = DesktopUpdateMatchRules.NormalizePath(AppPaths.ResolveStoragePath(organizer));
            if (string.IsNullOrWhiteSpace(storageDir) || !Directory.Exists(storageDir)) continue;

            try
            {
                foreach (string entryPath in Directory.EnumerateFileSystemEntries(storageDir, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        string name = Path.GetFileName(entryPath.TrimEnd(Path.DirectorySeparatorChar));
                        if (DesktopUpdateMatchRules.IsIgnoredDesktopName(name)) continue;

                        FileAttributes attrs = File.GetAttributes(entryPath);
                        bool isDir = (attrs & FileAttributes.Directory) != 0;
                        if ((attrs & FileAttributes.ReparsePoint) != 0 &&
                            !ShellFileOperationService.IsSafeCloudOrAppAliasReparsePoint(entryPath))
                        {
                            continue;
                        }

                        string ext = isDir ? string.Empty : Path.GetExtension(name);
                        string? targetPath = null;
                        string? args = null;
                        string? urlTarget = null;
                        List<string> children = [];

                        if (isDir)
                        {
                            InspectDirectorySummary(entryPath, children, out _);
                        }
                        else if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                        {
                            ShortcutInspection? link = ShortcutIntegrityService.InspectLink(entryPath);
                            if (link is not null && !link.IsVirtualShellTarget)
                            {
                                targetPath = link.ExpandedTargetPath;
                                args = link.Arguments;
                            }
                        }
                        else if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                        {
                            urlTarget = TryReadInternetShortcutUrl(entryPath);
                        }

                        result.Add(new OrganizerItemSnapshot(
                            organizer.Id,
                            organizer.Name,
                            storageDir,
                            name,
                            DesktopUpdateMatchRules.NormalizePath(entryPath),
                            ext,
                            isDir,
                            targetPath,
                            args,
                            urlTarget,
                            children));
                    }
                    catch
                    {
                        // Skip transient/unreadable entry safely.
                    }
                }
            }
            catch
            {
                // Skip offline/unreadable organizer directory safely.
            }
        }
        return result;
    }

    internal static async Task<bool> WaitForQuiescenceAsync(
        string path,
        bool isDirectory,
        int sampleIntervalMs = 1200,
        int maxChecks = 3,
        CancellationToken cancellationToken = default)
    {
        if (sampleIntervalMs <= 0)
            return isDirectory ? Directory.Exists(path) : File.Exists(path);

        if (!isDirectory)
        {
            for (int attempt = 0; attempt < maxChecks; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(path)) return false;
                if (TryReadFileFingerprint(path, out long len1, out long ticks1))
                {
                    await Task.Delay(Math.Min(sampleIntervalMs, 450), cancellationToken);
                    if (TryReadFileFingerprint(path, out long len2, out long ticks2) &&
                        len1 == len2 && ticks1 == ticks2)
                    {
                        return true;
                    }
                }
                else
                {
                    await Task.Delay(Math.Min(sampleIntervalMs, 600), cancellationToken);
                }
            }
            return false;
        }

        for (int attempt = 0; attempt < maxChecks; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(path)) return false;
            (int count1, long bytes1, long maxTicks1) = SnapshotDirectoryMetrics(path, cancellationToken);
            if (count1 == 0)
            {
                await Task.Delay(sampleIntervalMs, cancellationToken);
                continue;
            }
            await Task.Delay(sampleIntervalMs, cancellationToken);
            if (!Directory.Exists(path)) return false;
            (int count2, long bytes2, long maxTicks2) = SnapshotDirectoryMetrics(path, cancellationToken);
            if (count1 == count2 && bytes1 == bytes2 && maxTicks1 == maxTicks2)
                return true;
        }
        return false;
    }

    internal static DesktopUpdateLockInfo DetectLocks(string sourcePath, string targetPath, bool isDirectory)
    {
        string normSource = DesktopUpdateMatchRules.NormalizePath(sourcePath);
        string normTarget = DesktopUpdateMatchRules.NormalizePath(targetPath);

        var watchedPrefixes = new List<string> { normSource };
        if (isDirectory && Directory.Exists(normSource) && Directory.Exists(normTarget))
        {
            try
            {
                foreach (string childDir in Directory.EnumerateDirectories(normSource, "*", SearchOption.TopDirectoryOnly))
                {
                    string childName = Path.GetFileName(childDir.TrimEnd(Path.DirectorySeparatorChar));
                    string matchingTargetChild = Path.Combine(normTarget, childName);
                    if (Directory.Exists(matchingTargetChild))
                        watchedPrefixes.Add(DesktopUpdateMatchRules.NormalizePath(matchingTargetChild));
                }
            }
            catch
            {
                // Ignore transient enumeration errors.
            }
        }
        else if (!isDirectory)
        {
            watchedPrefixes.Add(normTarget);
        }

        // First discover Windows Services whose ImagePath lives under any watched prefix.
        var servicePathsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var runningServices = new List<string>();
        try
        {
            using RegistryKey? servicesRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (servicesRoot is not null)
            {
                foreach (string serviceName in servicesRoot.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? svcKey = servicesRoot.OpenSubKey(serviceName);
                        if (svcKey?.GetValue("ImagePath") is not string rawImagePath || string.IsNullOrWhiteSpace(rawImagePath))
                            continue;
                        string expanded = Environment.ExpandEnvironmentVariables(DesktopUpdateMatchRules.ExtractLeadingPath(rawImagePath));
                        string normImage = DesktopUpdateMatchRules.NormalizePath(expanded);
                        if (!MatchesAnyPrefix(normImage, watchedPrefixes)) continue;

                        servicePathsByName[serviceName] = normImage;
                        if (IsWindowsServiceRunning(serviceName))
                            runningServices.Add(serviceName);
                    }
                    catch
                    {
                        // Skip inaccessible service key.
                    }
                }
            }
        }
        catch
        {
            // Registry read failure should not crash lock detection.
        }

        var lockedProcesses = new List<LockedProcessInfo>();
        var serviceImageSet = new HashSet<string>(servicePathsByName.Values, StringComparer.OrdinalIgnoreCase);
        int currentPid = Environment.ProcessId;

        foreach (Process proc in Process.GetProcesses())
        {
            try
            {
                if (proc.Id == currentPid || proc.Id <= 4) continue;
                string? exePath = TryGetProcessExecutablePath(proc.Id);
                if (string.IsNullOrWhiteSpace(exePath)) continue;
                string normExe = DesktopUpdateMatchRules.NormalizePath(exePath);
                if (!MatchesAnyPrefix(normExe, watchedPrefixes)) continue;

                bool isSvcHost = serviceImageSet.Contains(normExe);
                lockedProcesses.Add(new LockedProcessInfo(proc.Id, proc.ProcessName, normExe, isSvcHost));
            }
            catch
            {
                // Access denied or process exited.
            }
            finally
            {
                proc.Dispose();
            }
        }

        return new DesktopUpdateLockInfo(lockedProcesses, runningServices);
    }

    internal static async Task<DesktopUpdateMergeResult> ReconcileMatchAsync(
        DesktopUpdateMatch match,
        bool allowElevationUi,
        int quiescenceMs = 1200,
        IEnumerable<string>? shortcutSearchRoots = null,
        CancellationToken cancellationToken = default)
    {
        if (match.DesktopItem.IsDirectory)
        {
            return await ReconcileDirectoryAsync(
                match,
                quiescenceMs,
                shortcutSearchRoots,
                cancellationToken);
        }

        return await ReconcileFileAsync(
            match,
            allowElevationUi,
            quiescenceMs,
            cancellationToken);
    }

    internal static async Task<DesktopUpdateMergeResult> ReconcileFileAsync(
        DesktopUpdateMatch match,
        bool allowElevationUi,
        int quiescenceMs = 450,
        CancellationToken cancellationToken = default)
    {
        string source = match.DesktopItem.FullPath;
        string target = match.TargetFullPath;
        if (!File.Exists(source))
            return new(match, DesktopUpdateMergeStatus.SkippedBusyWriting, 0);

        bool settled = await WaitForQuiescenceAsync(source, isDirectory: false, sampleIntervalMs: quiescenceMs, maxChecks: 3, cancellationToken);
        if (!settled)
            return new(match, DesktopUpdateMergeStatus.SkippedBusyWriting, 0);

        string? targetDir = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(targetDir))
            return new(match, DesktopUpdateMergeStatus.Failed, 0, ErrorMessage: "Invalid target directory.");

        string staging = Path.Combine(targetDir, $".tuckpane-merge-staging-{Guid.NewGuid():N}{Path.GetExtension(source)}");
        try
        {
            Directory.CreateDirectory(targetDir);

            bool identical = File.Exists(target) && AreFilesByteIdentical(source, target);
            if (!identical)
            {
                File.Copy(source, staging, overwrite: true);
                if (Path.GetExtension(target).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                    _ = ShortcutIntegrityService.RepairMovedShortcutIfNeeded(source, staging);

                _ = ShellFileOperationService.TryClearReadOnlyAttribute(target);
                File.Move(staging, target, overwrite: true);
            }

            bool sourceDeleted = TryDeleteDesktopFile(source, match.DesktopItem.IsPublicDesktop, allowElevationUi, out bool needsElevation);
            ShellChangeNotificationService.NotifyMoved(source, target);

            if (!sourceDeleted && needsElevation)
            {
                return new(match, DesktopUpdateMergeStatus.MergedPendingSourceCleanup, identical ? 0 : 1);
            }

            return new(
                match,
                identical ? DesktopUpdateMergeStatus.DeduplicatedIdentical : DesktopUpdateMergeStatus.Merged,
                identical ? 0 : 1);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            DesktopUpdateLockInfo locks = DetectLocks(source, target, isDirectory: false);
            return new(match, DesktopUpdateMergeStatus.BlockedByLock, 0, locks, ex.Message);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"同步桌面文件到收纳窗失败：{source} -> {target}", ex);
            return new(match, DesktopUpdateMergeStatus.Failed, 0, ErrorMessage: ex.Message);
        }
        finally
        {
            TryDeleteFileQuietly(staging);
        }
    }

    internal static async Task<DesktopUpdateMergeResult> ReconcileDirectoryAsync(
        DesktopUpdateMatch match,
        int quiescenceMs = 1200,
        IEnumerable<string>? shortcutSearchRoots = null,
        CancellationToken cancellationToken = default)
    {
        string sourceDir = match.DesktopItem.FullPath;
        string targetDir = match.TargetFullPath;

        if (!Directory.Exists(sourceDir) || !Directory.Exists(targetDir))
            return new(match, DesktopUpdateMergeStatus.SkippedBusyWriting, 0);

        bool settled = await WaitForQuiescenceAsync(sourceDir, isDirectory: true, sampleIntervalMs: quiescenceMs, maxChecks: 3, cancellationToken);
        if (!settled)
            return new(match, DesktopUpdateMergeStatus.SkippedBusyWriting, 0);

        DesktopUpdateLockInfo locks = DetectLocks(sourceDir, targetDir, isDirectory: true);
        if (locks.HasLocks)
            return new(match, DesktopUpdateMergeStatus.BlockedByLock, 0, locks);

        int mergedFiles = 0;
        try
        {
            // Verify every file inside sourceDir can be opened cleanly BEFORE mutating targetDir so a locked
            // installer or background executable never leaves a half-merged state.
            string[] allSourceFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            foreach (string srcFile in allSourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(sourceDir, srcFile);
                string dstFile = Path.Combine(targetDir, rel);
                if (!CanOpenForMerge(srcFile, writeRequired: true) ||
                    (File.Exists(dstFile) && !CanOpenForMerge(dstFile, writeRequired: true)))
                {
                    DesktopUpdateLockInfo freshLocks = DetectLocks(sourceDir, targetDir, isDirectory: true);
                    return new(match, DesktopUpdateMergeStatus.BlockedByLock, 0, freshLocks, $"文件正被占用：{rel}");
                }
            }

            foreach (string srcFile in allSourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(sourceDir, srcFile);
                string dstFile = Path.Combine(targetDir, rel);
                string? dstParent = Path.GetDirectoryName(dstFile);
                if (!string.IsNullOrWhiteSpace(dstParent))
                    Directory.CreateDirectory(dstParent);

                string staging = Path.Combine(dstParent ?? targetDir, $".tuckpane-merge-staging-{Guid.NewGuid():N}");
                try
                {
                    File.Copy(srcFile, staging, overwrite: true);
                    if (Path.GetExtension(dstFile).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                        _ = ShortcutIntegrityService.RepairMovedShortcutIfNeeded(srcFile, staging);
                    _ = ShellFileOperationService.TryClearReadOnlyAttribute(dstFile);
                    File.Move(staging, dstFile, overwrite: true);
                    mergedFiles++;

                    _ = ShellFileOperationService.TryClearReadOnlyAttribute(srcFile);
                    File.Delete(srcFile);
                }
                finally
                {
                    TryDeleteFileQuietly(staging);
                }
            }

            // Replicate any empty leaf directories that existed in sourceDir into targetDir.
            foreach (string srcSubDir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relDir = Path.GetRelativePath(sourceDir, srcSubDir);
                Directory.CreateDirectory(Path.Combine(targetDir, relDir));
            }

            PruneEmptyDirectoriesBottomUp(sourceDir);
            if (Directory.Exists(sourceDir) && !Directory.EnumerateFileSystemEntries(sourceDir).Any())
                Directory.Delete(sourceDir, recursive: false);

            // Rebind any .lnk shortcuts pointing into the old Desktop folder so they target the organizer copy.
            RebindShortcutsPointingToPrefix(sourceDir, targetDir, shortcutSearchRoots);
            ShellChangeNotificationService.NotifyMoved(sourceDir, targetDir);

            return new(match, DesktopUpdateMergeStatus.Merged, mergedFiles);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            DesktopUpdateLockInfo freshLocks = DetectLocks(sourceDir, targetDir, isDirectory: true);
            return new(match, DesktopUpdateMergeStatus.BlockedByLock, mergedFiles, freshLocks, ex.Message);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"合并覆盖桌面更新目录失败：{sourceDir} -> {targetDir}", ex);
            return new(match, DesktopUpdateMergeStatus.Failed, mergedFiles, ErrorMessage: ex.Message);
        }
    }

    internal static int RebindShortcutsPointingToPrefix(
        string oldDirectoryPrefix,
        string newDirectoryPrefix,
        IEnumerable<string>? searchRoots)
    {
        if (searchRoots is null) return 0;
        int rebound = 0;
        foreach (string root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (string lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.TopDirectoryOnly))
                {
                    if (ShortcutIntegrityService.RebindShortcutPrefix(lnk, oldDirectoryPrefix, newDirectoryPrefix))
                        rebound++;
                }
            }
            catch
            {
                // Ignore inaccessible search roots.
            }
        }
        return rebound;
    }

    private static void InspectDirectorySummary(string directoryPath, List<string> topChildren, out bool containsAppArtifacts)
    {
        containsAppArtifacts = false;
        try
        {
            int count = 0;
            foreach (string entry in Directory.EnumerateFileSystemEntries(directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                string childName = Path.GetFileName(entry.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(childName) || childName.StartsWith('.')) continue;
                if (count++ < 64) topChildren.Add(childName);

                if (!containsAppArtifacts)
                {
                    string ext = Path.GetExtension(childName);
                    if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".msi", StringComparison.OrdinalIgnoreCase))
                    {
                        containsAppArtifacts = true;
                    }
                    else if (Directory.Exists(entry))
                    {
                        try
                        {
                            foreach (string nested in Directory.EnumerateFiles(entry, "*.exe", SearchOption.TopDirectoryOnly))
                            {
                                _ = nested;
                                containsAppArtifacts = true;
                                break;
                            }
                        }
                        catch
                        {
                            // Ignore inaccessible nested directory.
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore unreadable directories.
        }
    }

    private static string? TryReadInternetShortcutUrl(string urlFilePath)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(urlFilePath);
            string text;
            try { text = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { text = Encoding.Default.GetString(bytes); }

            bool inSection = false;
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    inSection = line.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (inSection && line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    return line[4..].Trim();
            }
        }
        catch
        {
            // Ignore unreadable .url files.
        }
        return null;
    }

    private static bool TryReadFileFingerprint(string path, out long length, out long lastWriteTicks)
    {
        length = 0;
        lastWriteTicks = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            length = stream.Length;
            lastWriteTicks = File.GetLastWriteTimeUtc(path).Ticks;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (int Count, long TotalBytes, long MaxWriteTicks) SnapshotDirectoryMetrics(string directory, CancellationToken token)
    {
        int count = 0;
        long totalBytes = 0;
        long maxTicks = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    count++;
                    totalBytes += info.Length;
                    long ticks = info.LastWriteTimeUtc.Ticks;
                    if (ticks > maxTicks) maxTicks = ticks;
                }
                catch
                {
                    // File disappeared or locked during scan.
                }
            }
        }
        catch
        {
            // Transient scan error.
        }
        return (count, totalBytes, maxTicks);
    }

    private static bool CanOpenForMerge(string path, bool writeRequired)
    {
        try
        {
            if (writeRequired) _ = ShellFileOperationService.TryClearReadOnlyAttribute(path);
            FileAccess access = writeRequired ? FileAccess.ReadWrite : FileAccess.Read;
            FileShare share = writeRequired ? FileShare.None : FileShare.Read;
            using var fs = new FileStream(path, FileMode.Open, access, share);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool AreFilesByteIdentical(string pathA, string pathB)
    {
        try
        {
            var infoA = new FileInfo(pathA);
            var infoB = new FileInfo(pathB);
            if (!infoA.Exists || !infoB.Exists || infoA.Length != infoB.Length) return false;
            if (infoA.Length > 4 * 1024 * 1024) return false;
            return File.ReadAllBytes(pathA).AsSpan().SequenceEqual(File.ReadAllBytes(pathB));
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeleteDesktopFile(string source, bool isPublicDesktop, bool allowElevationUi, out bool needsElevation)
    {
        needsElevation = false;
        try
        {
            _ = ShellFileOperationService.TryClearReadOnlyAttribute(source);
            File.Delete(source);
            return !File.Exists(source);
        }
        catch (UnauthorizedAccessException) when (isPublicDesktop)
        {
            if (!allowElevationUi)
            {
                needsElevation = true;
                return false;
            }
            return ShellFileOperationService.TryShellDelete(source, out _);
        }
        catch
        {
            return false;
        }
    }

    private static void PruneEmptyDirectoriesBottomUp(string root)
    {
        if (!Directory.Exists(root)) return;
        string[] dirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
        Array.Sort(dirs, static (a, b) => b.Length.CompareTo(a.Length));
        foreach (string dir in dirs)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir, recursive: false);
            }
            catch
            {
                // Ignore if non-empty or busy.
            }
        }
    }

    private static string? TryGetProcessExecutablePath(int processId)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            int capacity = 1024;
            var buffer = new StringBuilder(capacity);
            return QueryFullProcessImageNameW(handle, 0, buffer, ref capacity)
                ? buffer.ToString(0, capacity)
                : null;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    private static bool IsWindowsServiceRunning(string serviceName)
    {
        IntPtr scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return false;
        try
        {
            IntPtr service = OpenServiceW(scm, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero) return false;
            try
            {
                return QueryServiceStatus(service, out SERVICE_STATUS status) &&
                    status.dwCurrentState is ServiceRunning or ServiceStartPending or ServiceContinuePending;
            }
            finally
            {
                _ = CloseServiceHandle(service);
            }
        }
        finally
        {
            _ = CloseServiceHandle(scm);
        }
    }

    private static bool MatchesAnyPrefix(string normalizedPath, IReadOnlyList<string> prefixes)
    {
        foreach (string prefix in prefixes)
        {
            if (normalizedPath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSharingViolation(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code is 32 or 33; // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
    }

    private static void TryDeleteFileQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
