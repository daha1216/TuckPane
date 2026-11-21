using TuckPane.Core;
using TuckPane.Models;

namespace TuckPane.Services;

internal sealed record AutoOrganizeRunSummary(
    int MovedCount,
    int ShortcutCount,
    int FailedCount,
    AutoOrganizeUndoBatch? UndoBatch);

internal sealed record AutoOrganizeUndoSummary(
    int RestoredCount,
    int SkippedCount,
    int FailedCount);

/// <summary>
/// Executes opt-in Auto-Organize Rules with safe Desktop/Downloads scanning,
/// dry-run preview, atomic TransferQueue/StorageService moves, and 1-click Undo.
/// </summary>
internal sealed class AutoOrganizeService
{
    private readonly SemaphoreSlim _undoGate = new(1, 1);
    private volatile AutoOrganizeUndoBatch? _lastBatch;

    internal bool CanUndo => _lastBatch is { Entries.Count: > 0 };
    internal int LastUndoCount => _lastBatch?.Entries.Count ?? 0;

    internal IReadOnlyList<AutoOrganizePlannedMove> PreviewPlan(
        AppStateV2 state,
        string? desktopOverride = null,
        string? downloadsOverride = null)
    {
        var targets = new List<(Guid, string, string, IReadOnlyList<AutoOrganizeRule>)>();
        foreach (OrganizerDefinition organizer in state.Organizers)
        {
            if (organizer.AutoOrganizeRules is not { Count: > 0 }) continue;
            if (!organizer.AutoOrganizeRules.Any(rule => rule.Enabled)) continue;
            string storagePath = AppPaths.ResolveStoragePath(organizer);
            targets.Add((organizer.Id, organizer.Name, storagePath, organizer.AutoOrganizeRules));
        }

        if (targets.Count == 0) return [];

        bool scanDesktop = targets.Any(t => t.Item4.Any(r => r.Enabled && r.Source == AutoOrganizeSource.Desktop));
        bool scanDownloads = targets.Any(t => t.Item4.Any(r => r.Enabled && r.Source == AutoOrganizeSource.Downloads));

        var candidates = new List<AutoOrganizeCandidate>();
        if (scanDesktop)
            ScanDirectory(desktopOverride ?? AppPaths.DesktopRoot, AutoOrganizeSource.Desktop, candidates);
        if (scanDownloads)
            ScanDirectory(downloadsOverride ?? ResolveDownloadsRoot(), AutoOrganizeSource.Downloads, candidates);

        return AutoOrganizeRules.EvaluatePlan(candidates, targets, DateTimeOffset.UtcNow);
    }

    internal async Task<AutoOrganizeRunSummary> ExecutePlanAsync(
        IReadOnlyList<AutoOrganizePlannedMove> plan,
        TransferQueue? transferQueue = null,
        CancellationToken cancellationToken = default)
    {
        if (plan.Count == 0) return new(0, 0, 0, null);

        var undoEntries = new List<AutoOrganizeUndoEntry>();
        int moved = 0;
        int shortcuts = 0;
        int failed = 0;

        foreach (IGrouping<string, AutoOrganizePlannedMove> group in plan.GroupBy(item => item.TargetDirectory, StringComparer.OrdinalIgnoreCase))
        {
            string targetDir = group.Key;
            string[] sourcePaths = group.Select(item => item.SourcePath).ToArray();
            var storage = new StorageService(targetDir, createIfMissing: true);

            IReadOnlyList<TransferOutcome> outcomes = transferQueue is not null
                ? await transferQueue.RunAsync(
                    token => storage.ImportBatchAsync(sourcePaths, progress: null, token),
                    cancellationToken)
                : await storage.ImportBatchAsync(sourcePaths, progress: null, cancellationToken);

            foreach (TransferOutcome outcome in outcomes)
            {
                switch (outcome.Status)
                {
                    case TransferStatus.Moved when !string.IsNullOrWhiteSpace(outcome.DestinationPath):
                        moved++;
                        undoEntries.Add(new AutoOrganizeUndoEntry(outcome.SourcePath, outcome.DestinationPath, CreatedShortcutOnly: false));
                        break;
                    case TransferStatus.ShortcutCreated when !string.IsNullOrWhiteSpace(outcome.DestinationPath):
                        shortcuts++;
                        undoEntries.Add(new AutoOrganizeUndoEntry(outcome.SourcePath, outcome.DestinationPath, CreatedShortcutOnly: true));
                        break;
                    case TransferStatus.Failed:
                    case TransferStatus.CopiedSourceRetained:
                    case TransferStatus.Cancelled:
                        failed++;
                        break;
                }
            }
        }

        AutoOrganizeUndoBatch? batch = undoEntries.Count > 0
            ? new AutoOrganizeUndoBatch(DateTimeOffset.UtcNow, undoEntries)
            : null;
        if (batch is not null)
        {
            await _undoGate.WaitAsync(cancellationToken);
            try { _lastBatch = batch; }
            finally { _undoGate.Release(); }
        }
        return new(moved, shortcuts, failed, batch);
    }

    internal async Task<AutoOrganizeUndoSummary> UndoLastBatchAsync(CancellationToken cancellationToken = default)
    {
        await _undoGate.WaitAsync(cancellationToken);
        try
        {
            AutoOrganizeUndoBatch? batch = _lastBatch;
            if (batch is null || batch.Entries.Count == 0) return new(0, 0, 0);

            int restored = 0;
            int skipped = 0;
            int failed = 0;
            var remainingReversed = new List<AutoOrganizeUndoEntry>();
            IReadOnlyList<AutoOrganizeUndoEntry> entries = batch.Entries;

            for (int index = entries.Count - 1; index >= 0; index--)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    for (int preserve = index; preserve >= 0; preserve--)
                        remainingReversed.Add(entries[preserve]);
                    PersistRemainingBatch(batch.ExecutedAtUtc, remainingReversed);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                AutoOrganizeUndoEntry entry = entries[index];
                try
                {
                    if (entry.CreatedShortcutOnly)
                    {
                        if (File.Exists(entry.MovedPath))
                        {
                            File.Delete(entry.MovedPath);
                            restored++;
                        }
                        else skipped++;
                        continue;
                    }

                    bool existsFile = File.Exists(entry.MovedPath);
                    bool existsDir = !existsFile && Directory.Exists(entry.MovedPath);
                    if (!existsFile && !existsDir)
                    {
                        skipped++;
                        continue;
                    }

                    string? originalDir = Path.GetDirectoryName(entry.OriginalPath);
                    if (string.IsNullOrWhiteSpace(originalDir))
                    {
                        failed++;
                        remainingReversed.Add(entry);
                        continue;
                    }

                    TransferOutcome outcome = await StorageService.MoveItemToDirectoryAsync(
                        entry.MovedPath,
                        originalDir,
                        progress: null,
                        cancellationToken);
                    if (outcome.Status == TransferStatus.Moved)
                    {
                        restored++;
                    }
                    else if (outcome.Status == TransferStatus.Cancelled)
                    {
                        failed++;
                        for (int preserve = index; preserve >= 0; preserve--)
                            remainingReversed.Add(entries[preserve]);
                        break;
                    }
                    else
                    {
                        // Includes Failed and CopiedSourceRetained so failed items remain retryable.
                        failed++;
                        remainingReversed.Add(entry);
                    }
                }
                catch (OperationCanceledException)
                {
                    for (int preserve = index; preserve >= 0; preserve--)
                        remainingReversed.Add(entries[preserve]);
                    PersistRemainingBatch(batch.ExecutedAtUtc, remainingReversed);
                    throw;
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"自动归类撤销失败：{entry.MovedPath}", ex);
                    failed++;
                    remainingReversed.Add(entry);
                }
            }

            PersistRemainingBatch(batch.ExecutedAtUtc, remainingReversed);
            return new(restored, skipped, failed);
        }
        finally
        {
            _undoGate.Release();
        }
    }

    private void PersistRemainingBatch(DateTimeOffset executedAtUtc, List<AutoOrganizeUndoEntry> remainingReversed)
    {
        if (remainingReversed.Count == 0)
        {
            _lastBatch = null;
            return;
        }
        remainingReversed.Reverse();
        _lastBatch = new AutoOrganizeUndoBatch(executedAtUtc, remainingReversed);
    }

    internal static string ResolveDownloadsRoot()
    {
        if (AppPaths.IsTestMode)
            return Path.Combine(Path.GetDirectoryName(AppPaths.DesktopRoot)!, "Downloads");
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string raw &&
                !string.IsNullOrWhiteSpace(raw))
            {
                string expanded = Environment.ExpandEnvironmentVariables(raw);
                if (Directory.Exists(expanded)) return expanded;
            }
        }
        catch
        {
            // Fall back to standard profile Downloads directory.
        }
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "Downloads");
    }

    private static void ScanDirectory(string? directory, AutoOrganizeSource source, List<AutoOrganizeCandidate> output)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        try
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    FileAttributes attrs = File.GetAttributes(path);
                    bool isDir = (attrs & FileAttributes.Directory) != 0;
                    bool isReparse = (attrs & FileAttributes.ReparsePoint) != 0;
                    bool isHiddenOrSystem = (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                    string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
                    string ext = isDir ? string.Empty : Path.GetExtension(name);
                    DateTime writeUtc = isDir ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
                    output.Add(new AutoOrganizeCandidate(
                        Path.GetFullPath(path),
                        name,
                        ext,
                        source,
                        new DateTimeOffset(writeUtc, TimeSpan.Zero),
                        isDir,
                        isReparse,
                        isHiddenOrSystem));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip transient or locked entries safely.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Error($"扫描自动归类目录失败：{directory}", ex);
        }
    }
}
