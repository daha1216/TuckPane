using System.Collections.Concurrent;
using System.Text.Json;
using TuckPane.Core;

namespace TuckPane.Services;

/// <summary>
/// Persists a bounded ledger of items relocated from User/Public Desktop into TuckPane organizers
/// and tracks short anti-fight suppression windows whenever the user deliberately moves or copies
/// items back out onto the Desktop.
/// </summary>
internal sealed class DesktopOriginLedger
{
    private const int MaxRecords = 500;
    private static readonly TimeSpan MoveOutCooldown = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CopyOutCooldown = TimeSpan.FromHours(12);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly List<DesktopOriginRecord> _records = [];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _suppressedDesktopPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    internal static DesktopOriginLedger Shared { get; } = new(Path.Combine(AppPaths.LocalRoot, "desktop-origins.json"));

    internal DesktopOriginLedger(string filePath)
    {
        _filePath = filePath;
    }

    internal IReadOnlyList<DesktopOriginRecord> Snapshot()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            return _records.ToArray();
        }
    }

    internal void RecordMovedFromDesktop(
        string originalDesktopPath,
        Guid organizerId,
        string relativeName,
        bool isDirectory,
        string? normalizedTargetPath = null)
    {
        if (string.IsNullOrWhiteSpace(originalDesktopPath) || string.IsNullOrWhiteSpace(relativeName))
            return;

        string normalizedOrigin = DesktopUpdateMatchRules.NormalizePath(originalDesktopPath);
        string normalizedTarget = DesktopUpdateMatchRules.NormalizePath(normalizedTargetPath);
        _suppressedDesktopPaths.TryRemove(normalizedOrigin, out _);

        lock (_gate)
        {
            EnsureLoadedLocked();
            _records.RemoveAll(r =>
                string.Equals(DesktopUpdateMatchRules.NormalizePath(r.OriginalDesktopPath), normalizedOrigin, StringComparison.OrdinalIgnoreCase) ||
                (r.OrganizerId == organizerId && string.Equals(r.RelativeName, relativeName, StringComparison.OrdinalIgnoreCase)));

            _records.Add(new DesktopOriginRecord(
                normalizedOrigin,
                organizerId,
                relativeName,
                isDirectory,
                normalizedTarget.Length > 0 ? normalizedTarget : null,
                DateTimeOffset.UtcNow));

            TrimLocked();
            SaveLocked();
        }
    }

    internal void RecordRemovedFromOrganizer(Guid organizerId, string relativeName)
    {
        if (string.IsNullOrWhiteSpace(relativeName)) return;
        lock (_gate)
        {
            EnsureLoadedLocked();
            int removed = _records.RemoveAll(r =>
                r.OrganizerId == organizerId &&
                string.Equals(r.RelativeName, relativeName, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) SaveLocked();
        }
    }

    internal void RecordMovedOrCopiedToDesktop(string desktopPath, bool isCopy, Guid? sourceOrganizerId = null, string? sourceRelativeName = null)
    {
        if (string.IsNullOrWhiteSpace(desktopPath)) return;
        string normalized = DesktopUpdateMatchRules.NormalizePath(desktopPath);
        DateTimeOffset until = DateTimeOffset.UtcNow + (isCopy ? CopyOutCooldown : MoveOutCooldown);
        _suppressedDesktopPaths[normalized] = until;

        if (!isCopy && sourceOrganizerId is Guid orgId && !string.IsNullOrWhiteSpace(sourceRelativeName))
            RecordRemovedFromOrganizer(orgId, sourceRelativeName);
        else if (!isCopy)
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                int removed = _records.RemoveAll(r =>
                    string.Equals(DesktopUpdateMatchRules.NormalizePath(r.OriginalDesktopPath), normalized, StringComparison.OrdinalIgnoreCase));
                if (removed > 0) SaveLocked();
            }
        }
    }

    internal void SuppressDesktopPath(string desktopPath, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(desktopPath)) return;
        string normalized = DesktopUpdateMatchRules.NormalizePath(desktopPath);
        _suppressedDesktopPaths[normalized] = DateTimeOffset.UtcNow + duration;
    }

    internal bool IsDesktopPathSuppressed(string desktopPath)
    {
        if (string.IsNullOrWhiteSpace(desktopPath)) return false;
        string normalized = DesktopUpdateMatchRules.NormalizePath(desktopPath);
        if (!_suppressedDesktopPaths.TryGetValue(normalized, out DateTimeOffset until))
            return false;
        if (DateTimeOffset.UtcNow <= until)
            return true;
        _suppressedDesktopPaths.TryRemove(normalized, out _);
        return false;
    }

    private void EnsureLoadedLocked()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_filePath)) return;
            string json = File.ReadAllText(_filePath);
            List<DesktopOriginRecord>? parsed = JsonSerializer.Deserialize<List<DesktopOriginRecord>>(json, SerializerOptions);
            if (parsed is { Count: > 0 })
            {
                _records.AddRange(parsed);
                TrimLocked();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"读取桌面来源账本失败：{_filePath}", ex);
        }
    }

    private void TrimLocked()
    {
        if (_records.Count <= MaxRecords) return;
        _records.Sort((a, b) => b.TrackedAtUtc.CompareTo(a.TrackedAtUtc));
        _records.RemoveRange(MaxRecords, _records.Count - MaxRecords);
    }

    private void SaveLocked()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            string temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_records, SerializerOptions));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"保存桌面来源账本失败：{_filePath}", ex);
        }
    }
}
