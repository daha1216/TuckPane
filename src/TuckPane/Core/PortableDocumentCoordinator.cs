using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane.Core;

internal enum PortableDocumentKind
{
    Note,
    Todo
}

internal interface IPortableDocumentWindow
{
    PortableDocumentKind Kind { get; }
    object UnderlyingWindow { get; }
    string? ExternalPath { get; }
    bool IsVisible { get; }
    void ShowAndActivate();
    void HideForTray();
    void RestoreFromTray();
    Task<bool> FlushForExitAsync();
    Task<bool> FlushAndHideForDragAsync();
    void RestoreAfterDrag();
    Task BeginDirectoryRenameAsync();
    void EndDirectoryRename();
    void RebindExternalPath(string path);
    void ClosePermanentlyWithoutSave();
    Task ClosePermanentlyAsync();
    Task ApplyGlobalThemeAsync(NoteTheme theme);
    void ApplyLanguage();
    void ApplyAlwaysOnTopSetting();
}

internal sealed record PortableExportedWindow(
    PortableDocumentKind Kind,
    string OldPath,
    IPortableDocumentWindow Window,
    bool WasVisible,
    bool WasTrayHidden);

// Coordinates open .tucknote and .tucktodo windows, tray visibility, and serialized open/rename/export transitions.
internal sealed class PortableDocumentCoordinator
{
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly Dictionary<string, IPortableDocumentWindow> _noteWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPortableDocumentWindow> _todoWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _trayHiddenNotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _trayHiddenTodos = new(StringComparer.OrdinalIgnoreCase);

    internal int NoteCount => _noteWindows.Count;
    internal int TodoCount => _todoWindows.Count;
    internal IReadOnlyCollection<IPortableDocumentWindow> NoteWindows => _noteWindows.Values;
    internal IReadOnlyCollection<IPortableDocumentWindow> TodoWindows => _todoWindows.Values;
    internal IEnumerable<IPortableDocumentWindow> AllWindows => _noteWindows.Values.Concat(_todoWindows.Values);

    internal static string ExpectedExtension(PortableDocumentKind kind) =>
        kind == PortableDocumentKind.Note ? ".tucknote" : ".tucktodo";

    internal Task WaitOpenGateAsync() => _openGate.WaitAsync();
    internal void ReleaseOpenGate() => _openGate.Release();

    internal async Task DrainOpenGateAsync()
    {
        await _openGate.WaitAsync();
        _openGate.Release();
    }

    internal HashSet<string> GetOpenPaths(PortableDocumentKind kind) =>
        GetWindows(kind).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal bool IsOpenAndVisible(PortableDocumentKind kind, string path) =>
        GetWindows(kind).TryGetValue(path, out IPortableDocumentWindow? window) && window.IsVisible;

    internal bool TryGetWindow(PortableDocumentKind kind, string path, out IPortableDocumentWindow? window) =>
        GetWindows(kind).TryGetValue(Path.GetFullPath(path), out window);

    internal async Task OpenAsync(
        PortableDocumentKind kind,
        string path,
        Func<bool> isPreparingUpdate,
        Func<string, Task<IPortableDocumentWindow?>> loadAndCreateAsync,
        Action<string> reportInvalidExtension,
        Action<string, Exception> reportFailure)
    {
        if (isPreparingUpdate()) return;
        string fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(ExpectedExtension(kind), StringComparison.OrdinalIgnoreCase))
        {
            reportInvalidExtension(fullPath);
            return;
        }

        Dictionary<string, IPortableDocumentWindow> windows = GetWindows(kind);
        HashSet<string> trayHidden = GetTrayHidden(kind);
        await _openGate.WaitAsync();
        try
        {
            if (isPreparingUpdate()) return;
            if (windows.TryGetValue(fullPath, out IPortableDocumentWindow? existing))
            {
                trayHidden.Remove(fullPath);
                existing.ShowAndActivate();
                return;
            }

            IPortableDocumentWindow? created = await loadAndCreateAsync(fullPath);
            if (created is null) return;
            if (isPreparingUpdate())
            {
                created.ClosePermanentlyWithoutSave();
                return;
            }
            windows[fullPath] = created;
            created.ShowAndActivate();
        }
        catch (Exception ex)
        {
            reportFailure(fullPath, ex);
        }
        finally
        {
            _openGate.Release();
        }
    }

    internal async Task<string> RenameAsync(
        PortableDocumentKind kind,
        string path,
        string name,
        IReadOnlyList<OrganizerDefinition> organizers,
        Func<Task> saveStateAsync,
        Func<Guid, string, Task> refreshOwnerAsync)
    {
        string fullPath = Path.GetFullPath(path);
        string candidate = name.Trim();
        if (candidate.Length == 0)
            throw new InvalidOperationException(AppStrings.Get(kind == PortableDocumentKind.Note ? "NoteNameRequired" : "TodoNameRequired"));
        if (candidate.Length > 120 || candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || candidate.EndsWith('.'))
            throw new InvalidOperationException(AppStrings.Get("FolderNameInvalidError"));
        if (NoteStore.IsReservedDeviceName(candidate.Split('.', 2)[0]))
            throw new InvalidOperationException(AppStrings.Get("FolderNameReservedError"));

        string directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException(AppStrings.Get("FolderNameInvalidError"));
        string targetPath = Path.Combine(directory, candidate + ExpectedExtension(kind));
        if (targetPath.Equals(fullPath, StringComparison.Ordinal)) return fullPath;
        if (!targetPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(targetPath) || Directory.Exists(targetPath)))
            throw new InvalidOperationException(AppStrings.Get(kind == PortableDocumentKind.Note ? "NoteNameDuplicate" : "TodoNameDuplicate"));

        OrganizerDefinition? organizer = organizers.FirstOrDefault(item =>
            Path.GetFullPath(AppPaths.ResolveStoragePath(item)).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        string oldFileName = Path.GetFileName(fullPath);
        int orderIndex = organizer?.ItemOrder.FindIndex(item =>
            item.Equals(oldFileName, StringComparison.OrdinalIgnoreCase)) ?? -1;

        Dictionary<string, IPortableDocumentWindow> windows = GetWindows(kind);
        HashSet<string> trayHidden = GetTrayHidden(kind);
        bool wasHidden = trayHidden.Contains(fullPath);
        windows.TryGetValue(fullPath, out IPortableDocumentWindow? window);

        try
        {
            await NoteRenameTransaction.RenameAsync(
                fullPath,
                targetPath,
                () => window?.BeginDirectoryRenameAsync() ?? Task.CompletedTask,
                () => window?.EndDirectoryRename(),
                (oldPath, newPath) =>
                {
                    windows.Remove(oldPath);
                    trayHidden.Remove(oldPath);
                    if (window is not null) windows[newPath] = window;
                    if (wasHidden) trayHidden.Add(newPath);
                    if (orderIndex >= 0) organizer!.ItemOrder[orderIndex] = Path.GetFileName(newPath);
                },
                newPath => window?.RebindExternalPath(newPath),
                () => orderIndex >= 0 ? saveStateAsync() : Task.CompletedTask);
        }
        finally
        {
            // Refresh only after the save gate is released, including a failed rollback that kept the new name.
            if (organizer is not null) await refreshOwnerAsync(organizer.Id, targetPath);
        }
        return targetPath;
    }

    internal void MarkHiddenForTray(PortableDocumentKind kind, string path) =>
        GetTrayHidden(kind).Add(Path.GetFullPath(path));

    internal void RebindAfterMove(string oldPath, string newPath)
    {
        string source = Path.GetFullPath(oldPath);
        string destination = Path.GetFullPath(newPath);
        foreach (PortableDocumentKind kind in new[] { PortableDocumentKind.Note, PortableDocumentKind.Todo })
        {
            Dictionary<string, IPortableDocumentWindow> windows = GetWindows(kind);
            HashSet<string> trayHidden = GetTrayHidden(kind);
            if (!windows.Remove(source, out IPortableDocumentWindow? window)) continue;
            bool hidden = trayHidden.Remove(source);
            windows[destination] = window;
            if (hidden) trayHidden.Add(destination);
            window.RebindExternalPath(destination);
        }
    }

    internal async Task<bool> FlushWindowAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (_noteWindows.TryGetValue(fullPath, out IPortableDocumentWindow? noteWindow))
            return await noteWindow.FlushForExitAsync();
        if (_todoWindows.TryGetValue(fullPath, out IPortableDocumentWindow? todoWindow))
            return await todoWindow.FlushForExitAsync();
        return true;
    }

    internal void CloseWithoutSave(string path)
    {
        string fullPath = Path.GetFullPath(path);
        foreach (PortableDocumentKind kind in new[] { PortableDocumentKind.Note, PortableDocumentKind.Todo })
        {
            if (!GetWindows(kind).Remove(fullPath, out IPortableDocumentWindow? window)) continue;
            GetTrayHidden(kind).Remove(fullPath);
            window.ClosePermanentlyWithoutSave();
        }
    }

    internal async Task<List<PortableExportedWindow>> FlushAndHideTopLevelAsync(string sourceRoot)
    {
        var moved = new List<PortableExportedWindow>();
        try
        {
            foreach (PortableDocumentKind kind in new[] { PortableDocumentKind.Note, PortableDocumentKind.Todo })
            {
                Dictionary<string, IPortableDocumentWindow> windows = GetWindows(kind);
                HashSet<string> trayHidden = GetTrayHidden(kind);
                foreach ((string path, IPortableDocumentWindow window) in windows
                             .Where(pair => OrganizerNoteRules.RebaseTopLevelPortablePath(sourceRoot, sourceRoot, pair.Key) is not null)
                             .ToArray())
                {
                    bool wasVisible = await window.FlushAndHideForDragAsync();
                    moved.Add(new(kind, path, window, wasVisible, trayHidden.Contains(path)));
                }
            }
            return moved;
        }
        catch
        {
            foreach (PortableExportedWindow item in moved.Where(item => item.WasVisible))
                item.Window.RestoreAfterDrag();
            throw;
        }
    }

    internal void CommitTopLevelExport(string sourceRoot, string destinationRoot, IReadOnlyList<PortableExportedWindow> moved)
    {
        foreach (PortableExportedWindow item in moved)
        {
            string newPath = OrganizerNoteRules.ResolvePortablePathAfterMove(
                sourceRoot,
                destinationRoot,
                item.OldPath,
                moveSucceeded: true);
            Dictionary<string, IPortableDocumentWindow> windows = GetWindows(item.Kind);
            HashSet<string> trayHidden = GetTrayHidden(item.Kind);
            windows.Remove(item.OldPath);
            windows[newPath] = item.Window;
            trayHidden.Remove(item.OldPath);
            if (item.WasTrayHidden) trayHidden.Add(newPath);
            item.Window.RebindExternalPath(newPath);
            if (item.WasVisible) item.Window.RestoreAfterDrag();
        }
    }

    internal void RestoreAllFromTray()
    {
        foreach (PortableDocumentKind kind in new[] { PortableDocumentKind.Note, PortableDocumentKind.Todo })
        {
            Dictionary<string, IPortableDocumentWindow> windows = GetWindows(kind);
            HashSet<string> trayHidden = GetTrayHidden(kind);
            foreach (string path in trayHidden.ToArray())
            {
                if (windows.TryGetValue(path, out IPortableDocumentWindow? window))
                    window.RestoreFromTray();
            }
            trayHidden.Clear();
        }
    }

    internal void HideAllForTray()
    {
        foreach (PortableDocumentKind kind in new[] { PortableDocumentKind.Note, PortableDocumentKind.Todo })
        {
            Dictionary<string, IPortableDocumentWindow> windows = GetWindows(kind);
            HashSet<string> trayHidden = GetTrayHidden(kind);
            trayHidden.Clear();
            foreach ((string path, IPortableDocumentWindow window) in windows)
            {
                if (!window.IsVisible) continue;
                trayHidden.Add(path);
                window.HideForTray();
            }
        }
    }

    internal void ApplyLanguage()
    {
        foreach (IPortableDocumentWindow window in AllWindows.ToArray())
            window.ApplyLanguage();
    }

    internal void ApplyAlwaysOnTopSetting()
    {
        foreach (IPortableDocumentWindow window in AllWindows.ToArray())
            window.ApplyAlwaysOnTopSetting();
    }

    internal void Clear()
    {
        _noteWindows.Clear();
        _todoWindows.Clear();
        _trayHiddenNotes.Clear();
        _trayHiddenTodos.Clear();
    }

    internal void CloseAllOnDispose()
    {
        foreach (IPortableDocumentWindow window in AllWindows.ToArray())
            _ = window.ClosePermanentlyAsync();
    }

    private Dictionary<string, IPortableDocumentWindow> GetWindows(PortableDocumentKind kind) =>
        kind == PortableDocumentKind.Note ? _noteWindows : _todoWindows;

    private HashSet<string> GetTrayHidden(PortableDocumentKind kind) =>
        kind == PortableDocumentKind.Note ? _trayHiddenNotes : _trayHiddenTodos;
}
