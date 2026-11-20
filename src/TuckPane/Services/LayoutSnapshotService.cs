using System.Text.Json;
using TuckPane.Core;
using TuckPane.Models;

namespace TuckPane.Services;

/// <summary>
/// Persists topology-aware and user-named layout snapshots to a dedicated atomic
/// JSON store so state.json stays lean and SchemaVersion 16 remains untouched.
/// </summary>
internal sealed class LayoutSnapshotService
{
    internal const long MaximumImportFileBytes = 5L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _storePath;

    internal LayoutSnapshotService(string? storePath = null)
    {
        _storePath = Path.GetFullPath(storePath ?? Path.Combine(AppPaths.LocalRoot, "layout-snapshots.json"));
    }

    internal async Task<LayoutSnapshotStoreData> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return await LoadUnlockedAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<LayoutSnapshot> SaveSnapshotAsync(
        string name,
        bool isAutoTopology,
        IReadOnlyList<DisplayInfo> displays,
        IEnumerable<OrganizerDefinition> organizers)
    {
        await _gate.WaitAsync();
        try
        {
            LayoutSnapshotStoreData data = await LoadUnlockedAsync();
            LayoutSnapshot snapshot = LayoutSnapshotMath.Capture(name, isAutoTopology, displays, organizers);
            LayoutSnapshotMath.UpsertSnapshot(data, snapshot);
            await WriteUnlockedAsync(data);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<bool> DeleteSnapshotAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            LayoutSnapshotStoreData data = await LoadUnlockedAsync();
            int removed = data.Snapshots.RemoveAll(snapshot => snapshot.Id == id);
            if (removed == 0) return false;
            await WriteUnlockedAsync(data);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<LayoutSnapshot?> FindAutoSnapshotAsync(IReadOnlyList<DisplayInfo> displays)
    {
        (string key, _) = LayoutSnapshotMath.BuildTopologyFingerprint(displays);
        LayoutSnapshotStoreData data = await LoadAsync();
        return LayoutSnapshotMath.FindAutoSnapshot(data, key);
    }

    internal async Task ExportToFileAsync(string destinationPath)
    {
        LayoutSnapshotStoreData data = await LoadAsync();
        await WriteFileAtomicallyAsync(Path.GetFullPath(destinationPath), data);
    }

    internal async Task<int> ImportFromFileAsync(string sourcePath)
    {
        string fullPath = Path.GetFullPath(sourcePath);
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists) throw new FileNotFoundException("布局快照文件不存在。", fullPath);
        if (fileInfo.Length > MaximumImportFileBytes)
            throw new InvalidDataException("布局快照文件超过 5 MB 上限。");

        string json = await File.ReadAllTextAsync(fullPath);
        LayoutSnapshotStoreData? imported = JsonSerializer.Deserialize<LayoutSnapshotStoreData>(json, JsonOptions);
        if (imported?.Snapshots is not { Count: > 0 }) return 0;

        await _gate.WaitAsync();
        try
        {
            LayoutSnapshotStoreData current = await LoadUnlockedAsync();
            int importedCount = 0;
            foreach (LayoutSnapshot snapshot in imported.Snapshots)
            {
                if (snapshot.Entries is null || snapshot.Entries.Count == 0) continue;
                if (snapshot.Id == Guid.Empty) snapshot.Id = Guid.NewGuid();
                LayoutSnapshotMath.UpsertSnapshot(current, snapshot);
                importedCount++;
            }
            if (importedCount > 0) await WriteUnlockedAsync(current);
            return importedCount;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LayoutSnapshotStoreData> LoadUnlockedAsync()
    {
        if (!File.Exists(_storePath)) return new LayoutSnapshotStoreData();
        try
        {
            string json = await File.ReadAllTextAsync(_storePath);
            LayoutSnapshotStoreData? parsed = JsonSerializer.Deserialize<LayoutSnapshotStoreData>(json, JsonOptions);
            if (parsed is null) return new LayoutSnapshotStoreData();
            parsed.Snapshots ??= [];
            return parsed;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法读取布局快照文件：{_storePath}", ex);
            return new LayoutSnapshotStoreData();
        }
    }

    private Task WriteUnlockedAsync(LayoutSnapshotStoreData data) =>
        WriteFileAtomicallyAsync(_storePath, data);

    private static async Task WriteFileAtomicallyAsync(string destination, LayoutSnapshotStoreData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".tmp";
        byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        try
        {
            await using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                PreallocationSize = utf8.Length
            }))
            {
                await stream.WriteAsync(utf8);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
