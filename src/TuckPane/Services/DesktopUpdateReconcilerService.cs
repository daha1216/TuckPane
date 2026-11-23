using System.Collections.Concurrent;
using TuckPane.Core;
using TuckPane.Models;

namespace TuckPane.Services;

internal sealed record PendingDesktopUpdateItem(
    DesktopUpdateMatch Match,
    DesktopUpdateMergeStatus Status,
    DesktopUpdateLockInfo? LockInfo,
    string? Detail);

internal sealed record DesktopUpdateReconcileSummary(
    int MergedCount,
    int DeduplicatedCount,
    int PendingLockedCount,
    int PendingCleanupCount,
    int RepairedHkcuCount,
    int RemainingRegistryMismatchCount,
    IReadOnlyList<PendingDesktopUpdateItem> PendingItems,
    IReadOnlyList<InstalledAppPathMismatch> RemainingMismatches);

/// <summary>
/// Watches User Desktop and Public Desktop in real time for recreated shortcuts or reinstalled
/// app folders that already live inside a TuckPane organizer, automatically overwrites/merges
/// unlocked updates back into their organizer in place (preserving FreeGrid slots), and tracks
/// locked items or HKLM/Service install-path mismatches for safe 1-click user resolution.
/// </summary>
internal sealed class DesktopUpdateReconcilerService : IDisposable
{
    private const int WatcherDebounceMs = 1800;
    private const int PendingRetryIntervalMs = 20_000;

    private readonly Func<AppStateV2> _getState;
    private readonly TransferQueue _transferQueue;
    private readonly Action<Guid, string> _onOrganizerUpdated;
    private readonly DesktopOriginLedger _ledger;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _dirtyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateSync = new();

    private FileSystemWatcher? _userDesktopWatcher;
    private FileSystemWatcher? _publicDesktopWatcher;
    private Timer? _debounceTimer;
    private Timer? _pendingRetryTimer;
    private List<PendingDesktopUpdateItem> _pendingItems = [];
    private List<InstalledAppPathMismatch> _remainingMismatches = [];
    private bool _enabled;
    private bool _disposed;

    internal event Action? StateChanged;

    internal DesktopUpdateReconcilerService(
        Func<AppStateV2> getState,
        TransferQueue transferQueue,
        Action<Guid, string> onOrganizerUpdated,
        DesktopOriginLedger? ledger = null)
    {
        _getState = getState;
        _transferQueue = transferQueue;
        _onOrganizerUpdated = onOrganizerUpdated;
        _ledger = ledger ?? DesktopOriginLedger.Shared;
    }

    internal IReadOnlyList<PendingDesktopUpdateItem> PendingItems
    {
        get { lock (_stateSync) return _pendingItems.ToArray(); }
    }

    internal IReadOnlyList<InstalledAppPathMismatch> RemainingMismatches
    {
        get { lock (_stateSync) return _remainingMismatches.ToArray(); }
    }

    internal int LockedPendingCount
    {
        get
        {
            lock (_stateSync)
                return _pendingItems.Count(p => p.Status == DesktopUpdateMergeStatus.BlockedByLock);
        }
    }

    internal int CleanupPendingCount
    {
        get
        {
            lock (_stateSync)
                return _pendingItems.Count(p => p.Status == DesktopUpdateMergeStatus.MergedPendingSourceCleanup);
        }
    }

    internal int DistinctMismatchedAppCount
    {
        get
        {
            lock (_stateSync)
            {
                return _remainingMismatches
                    .Select(m => m.IsService
                        ? $"svc:{m.ServiceName}"
                        : m.RegistrySubKeyPath.EndsWith(@"\Run", StringComparison.OrdinalIgnoreCase)
                            ? $"{m.RegistryHive}:{m.RegistrySubKeyPath}:{m.ValueName}"
                            : $"{m.RegistryHive}:{m.RegistrySubKeyPath}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
            }
        }
    }

    internal void SetEnabled(bool enabled)
    {
        if (_disposed) return;
        _enabled = enabled;
        if (enabled)
        {
            EnsureWatchersStarted();
            // Run a gentle deferred startup reconciliation pass so offline updates are caught automatically.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(3500, _lifetime.Token);
                    if (_enabled && !_disposed)
                        _ = await ReconcileAllAsync(allowElevationUi: false, _lifetime.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AppLogger.Error("桌面更新自动启动扫描失败。", ex); }
            });
        }
        else
        {
            StopWatchers();
        }
    }

    internal Task<DesktopUpdateReconcileSummary> ReconcileAllAsync(
        bool allowElevationUi,
        CancellationToken cancellationToken = default) =>
        RunReconcileCoreAsync(specificPaths: null, allowElevationUi, cancellationToken);

    internal void NotifyFolderImportedFromDesktop(string oldDesktopPath, string newOrganizerPath, Guid organizerId)
    {
        if (!_enabled || _disposed) return;
        if (string.IsNullOrWhiteSpace(oldDesktopPath) || string.IsNullOrWhiteSpace(newOrganizerPath)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await NotifyFolderImportedFromDesktopAsync(oldDesktopPath, newOrganizerPath, organizerId, _lifetime.Token);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                AppLogger.Error($"桌面目录入窗源头重绑失败：{oldDesktopPath} -> {newOrganizerPath}", ex);
            }
        });
    }

    internal async Task NotifyFolderImportedFromDesktopAsync(
        string oldDesktopPath,
        string newOrganizerPath,
        Guid organizerId,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled || _disposed) return;

        string normOld = DesktopUpdateMatchRules.NormalizePath(oldDesktopPath);
        string normNew = DesktopUpdateMatchRules.NormalizePath(newOrganizerPath);
        if (string.IsNullOrWhiteSpace(normOld) ||
            string.IsNullOrWhiteSpace(normNew) ||
            string.Equals(normOld, normNew, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CancellationTokenSource? linked = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            CancellationToken ct = linked.Token;

            await _reconcileGate.WaitAsync(ct);
            try
            {
                if (!_enabled || _disposed) return;

                AppStateV2 state = _getState();
                List<string> shortcutRoots = BuildShortcutSearchRoots(state);

                // Also search the parent directory of oldDesktopPath (e.g. a custom/temp Desktop root)
                // unless it is the read-only Public Desktop directory.
                string? oldParent = Path.GetDirectoryName(normOld);
                string publicDesktop = DesktopUpdateMatchRules.NormalizePath(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
                if (!string.IsNullOrWhiteSpace(oldParent) &&
                    Directory.Exists(oldParent) &&
                    !string.Equals(DesktopUpdateMatchRules.NormalizePath(oldParent), publicDesktop, StringComparison.OrdinalIgnoreCase))
                {
                    shortcutRoots.Add(oldParent);
                }

                // 1. Immediately rebind all .lnk shortcuts across User Desktop, Start Menu, and all organizers.
                int reboundCount = await Task.Run(
                    () => DesktopUpdateMergeEngine.RebindShortcutsPointingToPrefix(normOld, normNew, shortcutRoots),
                    ct);

                // 2. Immediately scan installed app paths and silently repair all HKCU entries without UAC.
                IReadOnlyList<InstalledAppPathMismatch> mismatches = await Task.Run(
                    () => InstalledAppPathService.ScanMismatches(state),
                    ct);
                int hkcuRepaired = InstalledAppPathService.RepairCurrentUserMismatches(mismatches);
                if (hkcuRepaired > 0)
                {
                    mismatches = await Task.Run(
                        () => InstalledAppPathService.ScanMismatches(state),
                        ct);
                }

                // 3. Refresh remaining mismatches so ConsoleWindow immediately surfaces any HKLM / Service
                //    sync notice BEFORE the app ever attempts an update.
                lock (_stateSync)
                {
                    _remainingMismatches = mismatches.ToList();
                }

                if (reboundCount > 0 || hkcuRepaired > 0 || mismatches.Count > 0)
                {
                    AppLogger.Info(
                        $"收纳目录入窗源头重绑完成（Organizer={organizerId}）：{Path.GetFileName(normNew)}（快捷方式重绑 {reboundCount}，HKCU 修复 {hkcuRepaired}，待提权同步 {mismatches.Count}）");
                }

                StateChanged?.Invoke();
            }
            finally
            {
                _reconcileGate.Release();
            }
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Service disposed concurrently during shutdown.
        }
        finally
        {
            linked?.Dispose();
        }
    }

    internal async Task<ElevatedRepairOutcome> ResolveLockedAndRepairAllAsync(
        bool includeLockedMerges,
        bool includeRegistryRepairs,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PendingDesktopUpdateItem> lockedItems;
        IReadOnlyList<PendingDesktopUpdateItem> cleanupItems;
        IReadOnlyList<InstalledAppPathMismatch> mismatches;
        lock (_stateSync)
        {
            lockedItems = _pendingItems
                .Where(p => p.Status == DesktopUpdateMergeStatus.BlockedByLock && p.Match.DesktopItem.IsDirectory)
                .ToArray();
            cleanupItems = _pendingItems
                .Where(p => p.Status == DesktopUpdateMergeStatus.MergedPendingSourceCleanup && !p.Match.DesktopItem.IsDirectory)
                .ToArray();
            mismatches = _remainingMismatches.ToArray();
        }

        // First heal any HKCU keys directly without UAC.
        int hkcuHealed = includeRegistryRepairs
            ? InstalledAppPathService.RepairCurrentUserMismatches(mismatches)
            : 0;

        InstalledAppPathMismatch[] elevatedMismatches = includeRegistryRepairs
            ? mismatches.Where(m => m.IsService || !string.Equals(m.RegistryHive, "HKCU", StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];

        var directives = new List<ElevatedMergeDirective>();
        if (includeLockedMerges)
        {
            foreach (PendingDesktopUpdateItem pending in lockedItems)
            {
                string sourceDir = pending.Match.DesktopItem.FullPath;
                string targetDir = pending.Match.TargetFullPath;
                if (!Directory.Exists(sourceDir) || !Directory.Exists(targetDir)) continue;

                DesktopUpdateLockInfo freshLocks = DesktopUpdateMergeEngine.DetectLocks(sourceDir, targetDir, isDirectory: true);
                string[] servicesToStop = freshLocks.RunningServices.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                int[] pidsToStop = freshLocks.Processes.Select(p => p.ProcessId).Distinct().ToArray();

                // Record user GUI apps (non-service helpers) so we relaunch them cleanly as normal user from the organizer folder!
                var relaunchExes = new List<string>();
                foreach (LockedProcessInfo lp in freshLocks.Processes)
                {
                    if (lp.IsServiceHost) continue;
                    string procName = lp.ProcessName;
                    // Skip headless child core/helper daemons; relaunch the main GUI app executable.
                    if (procName.EndsWith("Core", StringComparison.OrdinalIgnoreCase) ||
                        procName.EndsWith("Helper", StringComparison.OrdinalIgnoreCase) ||
                        procName.EndsWith("Service", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (DesktopUpdateMatchRules.TryRewritePathPrefix(lp.ExecutablePath, sourceDir, targetDir, out string targetExe))
                        relaunchExes.Add(targetExe);
                    else if (lp.ExecutablePath.StartsWith(targetDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        relaunchExes.Add(lp.ExecutablePath);
                }

                directives.Add(new ElevatedMergeDirective(
                    sourceDir,
                    targetDir,
                    servicesToStop,
                    pidsToStop,
                    relaunchExes));
            }
        }

        // Collect Public Desktop duplicate files whose updated organizer target is verified present on disk,
        // so both locked folder merges and Public Desktop cleanups finish in a single UAC click.
        var cleanupFilePaths = new List<string>();
        var verifiedCleanupItems = new List<PendingDesktopUpdateItem>();
        if (includeLockedMerges || includeRegistryRepairs)
        {
            foreach (PendingDesktopUpdateItem pending in cleanupItems)
            {
                string sourcePath = DesktopUpdateMatchRules.NormalizePath(pending.Match.DesktopItem.FullPath);
                string targetPath = DesktopUpdateMatchRules.NormalizePath(pending.Match.TargetFullPath);
                string storageDir = DesktopUpdateMatchRules.NormalizePath(pending.Match.TargetStorageDirectory);
                if (string.IsNullOrWhiteSpace(sourcePath) ||
                    string.IsNullOrWhiteSpace(targetPath) ||
                    string.IsNullOrWhiteSpace(storageDir))
                {
                    continue;
                }

                if (!targetPath.StartsWith(storageDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!File.Exists(targetPath) || !File.Exists(sourcePath))
                    continue;

                cleanupFilePaths.Add(sourcePath);
                verifiedCleanupItems.Add(pending);
            }
        }

        if (elevatedMismatches.Length == 0 && directives.Count == 0 && cleanupFilePaths.Count == 0)
        {
            _ = await ReconcileAllAsync(allowElevationUi: false, cancellationToken);
            return new(true, false, hkcuHealed, 0, 0, null, null, []);
        }

        AppStateV2 state = _getState();
        List<string> shortcutRoots = BuildShortcutSearchRoots(state);

        ElevatedRepairOutcome outcome = await _transferQueue.RunAsync(
            token => InstalledAppPathService.ApplyElevatedRepairAndMergeAsync(
                elevatedMismatches,
                directives,
                shortcutRoots,
                token,
                cleanupFilePaths),
            cancellationToken);

        // Refresh affected organizers and re-scan state cleanly.
        if (outcome.Succeeded)
        {
            foreach (PendingDesktopUpdateItem item in verifiedCleanupItems)
                ShellChangeNotificationService.NotifyMoved(item.Match.DesktopItem.FullPath, item.Match.TargetFullPath);
            foreach (PendingDesktopUpdateItem item in lockedItems)
                _onOrganizerUpdated(item.Match.TargetOrganizerId, item.Match.TargetRelativeName);
        }
        _ = await ReconcileAllAsync(allowElevationUi: false, cancellationToken);

        return outcome with { RepairedRegistryCount = outcome.RepairedRegistryCount + hkcuHealed };
    }

    private async Task<DesktopUpdateReconcileSummary> RunReconcileCoreAsync(
        IReadOnlyCollection<string>? specificPaths,
        bool allowElevationUi,
        CancellationToken cancellationToken)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            AppStateV2 state = _getState();
            IReadOnlyList<OrganizerItemSnapshot> inventory = await Task.Run(
                () => DesktopUpdateMergeEngine.BuildOrganizerInventory(state),
                cancellationToken);
            IReadOnlyList<DesktopOriginRecord> ledgerSnapshot = _ledger.Snapshot();

            List<(string Path, bool IsPublic)> desktopEntries = CollectDesktopEntries(specificPaths);
            List<string> shortcutSearchRoots = BuildShortcutSearchRoots(state);

            int merged = 0;
            int deduplicated = 0;
            var pending = new List<PendingDesktopUpdateItem>();
            var touchedOrganizers = new List<(Guid OrganizerId, string RelativeName)>();

            foreach ((string entryPath, bool isPublic) in desktopEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_ledger.IsDesktopPathSuppressed(entryPath)) continue;

                DesktopItemSnapshot? candidate = await Task.Run(
                    () => DesktopUpdateMergeEngine.InspectDesktopEntry(entryPath, isPublic),
                    cancellationToken);
                if (candidate is null) continue;

                DesktopUpdateMatch? match = DesktopUpdateMatchRules.FindBestMatch(candidate, inventory, ledgerSnapshot);
                if (match is null) continue;

                DesktopUpdateMergeResult result = await _transferQueue.RunAsync(
                    token => DesktopUpdateMergeEngine.ReconcileMatchAsync(
                        match,
                        allowElevationUi,
                        quiescenceMs: specificPaths is null ? 450 : 1200,
                        shortcutSearchRoots,
                        token),
                    cancellationToken);

                switch (result.Status)
                {
                    case DesktopUpdateMergeStatus.Merged:
                        merged++;
                        touchedOrganizers.Add((match.TargetOrganizerId, match.TargetRelativeName));
                        AppLogger.Info($"桌面更新已自动覆盖同步：{match.DesktopItem.Name} -> [{match.TargetOrganizerName}]");
                        break;
                    case DesktopUpdateMergeStatus.DeduplicatedIdentical:
                        deduplicated++;
                        touchedOrganizers.Add((match.TargetOrganizerId, match.TargetRelativeName));
                        AppLogger.Info($"桌面重复副本已清理：{match.DesktopItem.Name} -> [{match.TargetOrganizerName}]");
                        break;
                    case DesktopUpdateMergeStatus.MergedPendingSourceCleanup:
                        merged++;
                        touchedOrganizers.Add((match.TargetOrganizerId, match.TargetRelativeName));
                        pending.Add(new PendingDesktopUpdateItem(match, result.Status, null, "公共桌面旧副本需要管理员授权清理"));
                        break;
                    case DesktopUpdateMergeStatus.BlockedByLock:
                        pending.Add(new PendingDesktopUpdateItem(match, result.Status, result.LockInfo, result.ErrorMessage));
                        break;
                    case DesktopUpdateMergeStatus.SkippedBusyWriting:
                        // Installer still writing; re-queue dirty path so the next debounce pass picks it up.
                        QueueDirtyPath(entryPath);
                        break;
                }
            }

            // Scan installed app registry/service mismatches and auto-heal HKCU silently.
            IReadOnlyList<InstalledAppPathMismatch> mismatches = await Task.Run(
                () => InstalledAppPathService.ScanMismatches(state),
                cancellationToken);
            int hkcuRepaired = InstalledAppPathService.RepairCurrentUserMismatches(mismatches);
            if (hkcuRepaired > 0)
            {
                mismatches = await Task.Run(() => InstalledAppPathService.ScanMismatches(state), cancellationToken);
            }

            // When running an incremental dirty-path pass, retain previously known pending items that still exist.
            List<PendingDesktopUpdateItem> mergedPending;
            lock (_stateSync)
            {
                if (specificPaths is null)
                {
                    mergedPending = pending;
                }
                else
                {
                    var touchedSet = new HashSet<string>(specificPaths.Select(DesktopUpdateMatchRules.NormalizePath), StringComparer.OrdinalIgnoreCase);
                    mergedPending = _pendingItems
                        .Where(existing =>
                            !touchedSet.Contains(DesktopUpdateMatchRules.NormalizePath(existing.Match.DesktopItem.FullPath)) &&
                            (existing.Match.DesktopItem.IsDirectory
                                ? Directory.Exists(existing.Match.DesktopItem.FullPath)
                                : File.Exists(existing.Match.DesktopItem.FullPath)))
                        .Concat(pending)
                        .ToList();
                }

                _pendingItems = mergedPending;
                _remainingMismatches = mismatches.ToList();
            }

            UpdatePendingRetryTimer(mergedPending.Any(p => p.Status == DesktopUpdateMergeStatus.BlockedByLock));

            foreach ((Guid orgId, string relName) in touchedOrganizers.Distinct())
                _onOrganizerUpdated(orgId, relName);

            StateChanged?.Invoke();

            return new DesktopUpdateReconcileSummary(
                merged,
                deduplicated,
                mergedPending.Count(p => p.Status == DesktopUpdateMergeStatus.BlockedByLock),
                mergedPending.Count(p => p.Status == DesktopUpdateMergeStatus.MergedPendingSourceCleanup),
                hkcuRepaired,
                mismatches.Count,
                mergedPending,
                mismatches);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private void EnsureWatchersStarted()
    {
        if (AppPaths.IsTestMode) return;
        _userDesktopWatcher ??= CreateDesktopWatcher(AppPaths.DesktopRoot);
        string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrWhiteSpace(publicDesktop) &&
            !publicDesktop.Equals(AppPaths.DesktopRoot, StringComparison.OrdinalIgnoreCase))
        {
            _publicDesktopWatcher ??= CreateDesktopWatcher(publicDesktop);
        }
        _debounceTimer ??= new Timer(OnDebounceTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    private FileSystemWatcher? CreateDesktopWatcher(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                InternalBufferSize = 32768,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
            };
            watcher.Created += OnDesktopFsEvent;
            watcher.Changed += OnDesktopFsEvent;
            watcher.Renamed += OnDesktopFsRenamed;
            watcher.Error += OnDesktopWatcherError;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"启动桌面更新监听失败：{directory}", ex);
            return null;
        }
    }

    private void OnDesktopFsEvent(object sender, FileSystemEventArgs e)
    {
        if (!_enabled || _disposed || string.IsNullOrWhiteSpace(e.FullPath)) return;
        if (DesktopUpdateMatchRules.IsIgnoredDesktopName(e.Name)) return;
        if (_ledger.IsDesktopPathSuppressed(e.FullPath)) return;
        QueueDirtyPath(e.FullPath);
    }

    private void OnDesktopFsRenamed(object sender, RenamedEventArgs e)
    {
        if (!_enabled || _disposed || string.IsNullOrWhiteSpace(e.FullPath)) return;
        if (DesktopUpdateMatchRules.IsIgnoredDesktopName(e.Name)) return;
        if (_ledger.IsDesktopPathSuppressed(e.FullPath)) return;
        QueueDirtyPath(e.FullPath);
    }

    private void OnDesktopWatcherError(object sender, ErrorEventArgs e)
    {
        AppLogger.Error("桌面更新监听缓冲区溢出或异常，执行全量补扫。", e.GetException());
        if (_enabled && !_disposed)
            _ = Task.Run(() => ReconcileAllAsync(allowElevationUi: false, _lifetime.Token));
    }

    private void QueueDirtyPath(string fullPath)
    {
        _dirtyPaths[DesktopUpdateMatchRules.NormalizePath(fullPath)] = 0;
        try { _debounceTimer?.Change(WatcherDebounceMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void OnDebounceTick(object? state)
    {
        if (!_enabled || _disposed) return;
        string[] batch = _dirtyPaths.Keys.ToArray();
        foreach (string key in batch) _dirtyPaths.TryRemove(key, out _);
        if (batch.Length == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunReconcileCoreAsync(batch, allowElevationUi: false, _lifetime.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Error("桌面更新实时同步处理失败。", ex);
            }
        });
    }

    private void UpdatePendingRetryTimer(bool hasLockedItems)
    {
        if (_disposed) return;
        if (hasLockedItems && _enabled)
        {
            _pendingRetryTimer ??= new Timer(OnPendingRetryTick, null, PendingRetryIntervalMs, PendingRetryIntervalMs);
            try { _pendingRetryTimer.Change(PendingRetryIntervalMs, PendingRetryIntervalMs); }
            catch (ObjectDisposedException) { }
        }
        else
        {
            try { _pendingRetryTimer?.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }
    }

    private void OnPendingRetryTick(object? state)
    {
        if (!_enabled || _disposed || LockedPendingCount == 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await ReconcileAllAsync(allowElevationUi: false, _lifetime.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Error("重试同步被占用桌面更新项失败。", ex);
            }
        });
    }

    private static List<(string Path, bool IsPublic)> CollectDesktopEntries(IReadOnlyCollection<string>? specificPaths)
    {
        var entries = new List<(string Path, bool IsPublic)>();
        string userDesktop = DesktopUpdateMatchRules.NormalizePath(AppPaths.DesktopRoot);
        string publicDesktop = DesktopUpdateMatchRules.NormalizePath(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

        if (specificPaths is not null)
        {
            foreach (string raw in specificPaths)
            {
                string norm = DesktopUpdateMatchRules.NormalizePath(raw);
                if (!File.Exists(norm) && !Directory.Exists(norm)) continue;
                bool isPublic = !string.IsNullOrWhiteSpace(publicDesktop) &&
                    norm.StartsWith(publicDesktop + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                entries.Add((norm, isPublic));
            }
            return entries;
        }

        EnumerateRoot(userDesktop, isPublic: false, entries);
        if (!string.IsNullOrWhiteSpace(publicDesktop) &&
            !publicDesktop.Equals(userDesktop, StringComparison.OrdinalIgnoreCase))
        {
            EnumerateRoot(publicDesktop, isPublic: true, entries);
        }
        return entries;
    }

    private static void EnumerateRoot(string root, bool isPublic, List<(string Path, bool IsPublic)> output)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(entry.TrimEnd(Path.DirectorySeparatorChar));
                if (DesktopUpdateMatchRules.IsIgnoredDesktopName(name)) continue;
                output.Add((DesktopUpdateMatchRules.NormalizePath(entry), isPublic));
            }
        }
        catch
        {
            // Ignore unreadable desktop root.
        }
    }

    internal static List<string> BuildShortcutSearchRoots(AppStateV2 state)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(AppPaths.DesktopRoot) && Directory.Exists(AppPaths.DesktopRoot))
            roots.Add(AppPaths.DesktopRoot);
        string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        if (!string.IsNullOrWhiteSpace(startMenu) && Directory.Exists(startMenu))
            roots.Add(startMenu);
        foreach (OrganizerDefinition organizer in state.Organizers)
        {
            string storageDir = AppPaths.ResolveStoragePath(organizer);
            if (!string.IsNullOrWhiteSpace(storageDir) && Directory.Exists(storageDir))
                roots.Add(storageDir);
        }
        return roots;
    }

    private void StopWatchers()
    {
        if (_userDesktopWatcher is not null)
        {
            _userDesktopWatcher.EnableRaisingEvents = false;
            _userDesktopWatcher.Dispose();
            _userDesktopWatcher = null;
        }
        if (_publicDesktopWatcher is not null)
        {
            _publicDesktopWatcher.EnableRaisingEvents = false;
            _publicDesktopWatcher.Dispose();
            _publicDesktopWatcher = null;
        }
        try { _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        try { _pendingRetryTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        _lifetime.Cancel();
        StopWatchers();
        _debounceTimer?.Dispose();
        _pendingRetryTimer?.Dispose();
        _reconcileGate.Dispose();
        _lifetime.Dispose();
    }
}
