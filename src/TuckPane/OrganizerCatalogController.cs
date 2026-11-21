using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualBasic.FileIO;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TuckPane;

/// <summary>
/// Owns directory watching, debounced catalog reconciliation, and non-rendering
/// file/note/clipboard/dialog/folder operations for an organizer window.
/// </summary>
internal sealed class OrganizerCatalogController
{
    private readonly MainWindow _window;
    private readonly DispatcherQueueTimer _watcherDebounceTimer;
    private readonly WatcherRecovery _watcherRecovery = new();
    private FileSystemWatcher? _watcher;
    private IReadOnlySet<string> _knownPortableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private long _ignoredPortableSaveEvents;
    private int _catalogScanActive;
    private bool _catalogRefreshPending;
    private int _watcherRefreshPosted;
    private bool _catalogRefreshIconsPending;
    private bool _catalogNotifyUnsupportedPending;
    private Task? _catalogRefreshTask;
    private CancellationTokenSource? _catalogRefreshCancellation;
    private long _catalogRefreshGeneration;
    private HashSet<string> _lastUnsupported = new(StringComparer.OrdinalIgnoreCase);
    private bool _addingItem;
    private int _activeDialogCount;

    public OrganizerCatalogController(MainWindow window)
    {
        _window = window;
        _watcherDebounceTimer = window.DispatcherQueue.CreateTimer();
        _watcherDebounceTimer.Interval = TimeSpan.FromMilliseconds(250);
        _watcherDebounceTimer.IsRepeating = false;
        _watcherDebounceTimer.Tick += WatcherDebounceTimer_Tick;
    }

    public bool IsDialogActive => _activeDialogCount > 0;
    public bool IsAddingItem => _addingItem || _activeDialogCount > 0;

    private Task YieldForMenuDismissAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => completion.TrySetResult()))
            return Task.CompletedTask;
        return completion.Task;
    }

    public void Stop()
    {
        _watcherDebounceTimer.Stop();
        _catalogRefreshCancellation?.Cancel();
        _catalogRefreshCancellation?.Dispose();
        _catalogRefreshCancellation = null;
        _watcherRecovery.Dispose();
        _watcher?.Dispose();
        _watcher = null;
    }

    public void StartWatcher()
    {
        if (_window.IsClosing || !_window.Storage.Exists || _watcher is not null) return;
        try { CreateWatcher(); }
        catch (Exception ex)
        {
            AppLogger.Error("Directory watcher start failed", ex);
            RecoverWatcher();
        }
    }

    private void CreateWatcher()
    {
        var watcher = new FileSystemWatcher(_window.Storage.ItemsRoot)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Changed += Watcher_Changed;
        watcher.Created += Watcher_Changed;
        watcher.Deleted += Watcher_Changed;
        watcher.Renamed += Watcher_Changed;
        watcher.Error += Watcher_Error;
        _watcher = watcher;
        try { watcher.EnableRaisingEvents = true; }
        catch { _watcher = null; watcher.Dispose(); throw; }
    }

    private void Watcher_Error(object sender, ErrorEventArgs args)
    {
        if (_window.IsClosing || !ReferenceEquals(sender, _watcher)) return;
        AppLogger.Error("Directory watcher lost notifications", args.GetException());
        _ = _window.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_window.IsClosing && ReferenceEquals(sender, _watcher)) RecoverWatcher();
        });
    }

    private void RecoverWatcher()
    {
        _ = _watcherRecovery.RequestAsync(() =>
        {
            if (_window.IsClosing || !_window.Storage.Exists) return false;
            _watcher?.Dispose();
            _watcher = null;
            CreateWatcher();
            return true;
        }, () =>
        {
            if (!_window.IsClosing)
                _ = _window.RunWindowSafelyAsync(() => RefreshCatalogAsync(false, true), "目录监听恢复刷新失败");
        });
    }

    private void Watcher_Changed(object sender, FileSystemEventArgs args)
    {
        if (_window.IsClosing || !ReferenceEquals(sender, _watcher)) return;
        if (!PortableDocumentCatalogChanges.ShouldRefresh(args.ChangeType, args.Name,
                (args as RenamedEventArgs)?.OldName, Volatile.Read(ref _knownPortableNames),
                Volatile.Read(ref _catalogScanActive) != 0))
        {
            if (AppLogger.PerformanceTraceEnabled && Interlocked.Increment(ref _ignoredPortableSaveEvents) % 32 == 1)
                AppLogger.Performance($"note-catalog ignored-save-events={_ignoredPortableSaveEvents} organizer={_window.OrganizerId}");
            return;
        }
        Interlocked.Increment(ref _catalogRefreshGeneration);
        if (Interlocked.Exchange(ref _watcherRefreshPosted, 1) == 0)
        {
            if (!_window.DispatcherQueue.TryEnqueue(() =>
            {
                if (_window.IsClosing) { Volatile.Write(ref _watcherRefreshPosted, 0); return; }
                // The posted refresh represents all accumulated generations, including a new watcher.
                // An obsolete sender must not discard changes coalesced behind its callback.
                if (ReferenceEquals(sender, _watcher)) _watcherRecovery.HealthyChange();
                _watcherDebounceTimer.Stop();
                _watcherDebounceTimer.Start();
            }))
            {
                Volatile.Write(ref _watcherRefreshPosted, 0);
                AppLogger.Performance("catalog-watcher enqueue failed");
            }
        }
    }

    private async void WatcherDebounceTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        Volatile.Write(ref _watcherRefreshPosted, 0);
        try
        {
            await RefreshCatalogAsync(notifyUnsupported: true, refreshIcons: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法刷新收纳目录：{_window.Storage.ItemsRoot}", ex);
            if (!_window.IsClosing)
                _window.ShowOrganizerMessage(
                    AppStrings.Format("DirectoryReadErrorFormat", Path.GetFileName(_window.Storage.ItemsRoot), ex.Message),
                    InfoBarSeverity.Warning);
        }
    }

    public Task RefreshCatalogAsync(bool notifyUnsupported, bool refreshIcons = false)
    {
        _catalogRefreshPending = true;
        Interlocked.Increment(ref _catalogRefreshGeneration);
        _catalogNotifyUnsupportedPending |= notifyUnsupported;
        _catalogRefreshIconsPending |= refreshIcons;
        if (_catalogRefreshTask is { IsCompleted: false })
            _catalogRefreshCancellation?.Cancel();
        return StartCatalogRefreshIfReady();
    }

    public Task StartCatalogRefreshIfReady()
    {
        if (_window.IsClosing || _window.CaptureInteractionState().BlocksCatalogRefresh) return Task.CompletedTask;
        if (_catalogRefreshTask is { IsCompleted: false }) return _catalogRefreshTask;
        _catalogRefreshTask = FlushCatalogRefreshesAsync();
        return _catalogRefreshTask;
    }

    private async Task FlushCatalogRefreshesAsync()
    {
        Volatile.Write(ref _catalogScanActive, 1);
        _catalogRefreshCancellation?.Cancel();
        _catalogRefreshCancellation?.Dispose();
        CancellationTokenSource refreshCancellation = new();
        _catalogRefreshCancellation = refreshCancellation;
        CancellationToken cancellationToken = refreshCancellation.Token;
        try
        {
            while (_catalogRefreshPending && !_window.IsClosing && !_window.CaptureInteractionState().BlocksCatalogRefresh)
            {
                bool notifyUnsupported = _catalogNotifyUnsupportedPending;
                bool refreshIcons = _catalogRefreshIconsPending;
                _catalogRefreshPending = false;
                _catalogNotifyUnsupportedPending = false;
                _catalogRefreshIconsPending = false;
                long generation = Volatile.Read(ref _catalogRefreshGeneration);
                await RefreshCatalogCoreAsync(notifyUnsupported, refreshIcons, generation, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _window.IsClosing)
        {
            AppLogger.Performance("catalog-refresh cancelled");
        }
        finally
        {
            Volatile.Write(ref _catalogScanActive, 0);
            if (ReferenceEquals(_catalogRefreshCancellation, refreshCancellation))
            {
                refreshCancellation.Dispose();
                _catalogRefreshCancellation = null;
            }
            else
            {
                refreshCancellation.Dispose();
            }
            if (_catalogRefreshPending && !_window.IsClosing && !_window.CaptureInteractionState().BlocksCatalogRefresh)
            {
                _ = _window.DispatcherQueue.TryEnqueue(() => _ = StartCatalogRefreshIfReady());
            }
        }
    }

    private async Task RefreshCatalogCoreAsync(
        bool notifyUnsupported,
        bool refreshIcons,
        long generation,
        CancellationToken cancellationToken)
    {
        long traceStart = AppLogger.PerformanceTraceEnabled ? Stopwatch.GetTimestamp() : 0;
        OrganizerDefinition definition = _window.Definition;
        WidgetItem[] containedOrganizers = _window.Host.GetContainedOrganizerItems(definition.Id).ToArray();
        IReadOnlyList<WidgetItem> diskItems = await Task.Run(_window.Storage.ReadItems, cancellationToken);
        IReadOnlyList<string> unsupportedNames = _window.Storage.ReadUnsupportedNames();
        cancellationToken.ThrowIfCancellationRequested();
        if (_window.IsClosing || generation != Volatile.Read(ref _catalogRefreshGeneration)) return;
        long traceUiStart = traceStart == 0 ? 0 : Stopwatch.GetTimestamp();
        Volatile.Write(ref _knownPortableNames, (IReadOnlySet<string>)diskItems
            .Where(item => item.Kind is WidgetItemKind.PortableNote or WidgetItemKind.PortableTodo)
            .Select(item => item.RelativeName).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var byName = new Dictionary<string, WidgetItem>(StringComparer.OrdinalIgnoreCase);
        var duplicateItems = new List<WidgetItem>();
        foreach (WidgetItem candidate in diskItems
            .Concat(definition.Notes.Select(OrganizerNoteRules.CreateItem))
            .Concat(containedOrganizers))
        {
            if (!byName.TryAdd(candidate.RelativeName, candidate))
                duplicateItems.Add(candidate);
        }
        var ordered = new List<WidgetItem>();
        foreach (string relativeName in definition.ItemOrder)
        {
            if (byName.Remove(relativeName, out WidgetItem? item))
            {
                ordered.Add(item);
            }
        }

        ordered.AddRange(byName.Values.OrderBy(item => item.RelativeName, StringComparer.CurrentCultureIgnoreCase));
        if (duplicateItems.Count > 0)
            ordered.AddRange(duplicateItems.OrderBy(item => item.RelativeName, StringComparer.CurrentCultureIgnoreCase));
        bool orderChanged = !definition.ItemOrder.SequenceEqual(ordered.Select(item => item.RelativeName), StringComparer.OrdinalIgnoreCase);
        _window.ApplyCatalogItemsAndRender(ordered, refreshIcons);
        definition.ItemOrder = ordered.Select(item => item.RelativeName).ToList();
        if (orderChanged)
        {
            await _window.SaveOrganizerStateAsync();
        }

        _window.Host.NotifyOrganizerPreviewChanged(definition.Id);

        if (traceStart != 0)
            AppLogger.Performance($"note-catalog organizer={definition.Id} readMs={Stopwatch.GetElapsedTime(traceStart, traceUiStart).TotalMilliseconds:F2} uiMs={Stopwatch.GetElapsedTime(traceUiStart).TotalMilliseconds:F2} items={ordered.Count}");

        var unsupported = unsupportedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] addedUnsupported = unsupported.Except(_lastUnsupported, StringComparer.OrdinalIgnoreCase).ToArray();
        _lastUnsupported = unsupported;
        if (notifyUnsupported && addedUnsupported.Length > 0)
        {
            string message = AppStrings.Format("UnsupportedItemsFormat", string.Join(", ", addedUnsupported.Take(3)));
            _window.ShowOrganizerMessage(message, InfoBarSeverity.Warning);
        }
    }

    public async Task AddSelectedItemAsync(bool folder)
    {
        if (_addingItem || _activeDialogCount > 0 || _window.IsClosing) return;
        _addingItem = true;
        _window.AdjustOverlayCount(+1);
        _window.PauseOutsideClickForOverlay();
        try
        {
            await YieldForMenuDismissAsync();
            if (_window.IsClosing) return;
            _window.DesktopLayer?.SetInputActivation(true);
            _window.ResetStationDelay();
            string? source = folder ? FileSelectionService.PickSingleFolder(_window.Hwnd) : FileSelectionService.PickSingleFile(_window.Hwnd);
            if (_window.IsClosing || string.IsNullOrWhiteSpace(source)) return;
            TransferOutcome outcome = _window.Storage.AddItemShortcut(source);
            if (outcome.Status != TransferStatus.ShortcutCreated)
            {
                _window.ShowOrganizerMessage(outcome.Message, InfoBarSeverity.Error);
                return;
            }
            StartWatcher();
            await RefreshCatalogAsync(notifyUnsupported: false);
            string addedName = !string.IsNullOrWhiteSpace(outcome.DestinationPath)
                ? Path.GetFileNameWithoutExtension(outcome.DestinationPath)
                : Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string toast = string.IsNullOrWhiteSpace(addedName)
                ? AppStrings.Get("ShortcutCreated")
                : MainWindow.LocalizeToast($"已添加“{addedName}”", $"Added \"{addedName}\"", $"「{addedName}」を追加しました");
            _window.ShowOrganizerMessage(AppStrings.Get("ShortcutCreated"), InfoBarSeverity.Success);
            _window.ShowExpandedToast(toast, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLogger.Error("添加项目失败。", ex);
            if (!_window.IsClosing) _window.ShowOrganizerMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _addingItem = false;
            if (!_window.IsClosing) _window.DesktopLayer?.SetInputActivation(_window.IsContextMenuActivated || IsAddingItem);
            _window.AdjustOverlayCount(-1);
            _window.ResetStationDelay();
            _window.ResumeOutsideClickAfterOverlay();
        }
    }

    public async Task CopyFileItemAsync(WidgetItem item)
    {
        try
        {
            if (MainWindow.IsDocumentWidgetItem(item) && !await _window.Host.FlushPortableWindowAsync(item.FullPath))
                throw new IOException(AppStrings.Get(item.Kind == WidgetItemKind.PortableTodo
                    ? "TodoDragSaveFailed"
                    : "NoteDragSaveFailed"));
            ShellDragService.SetCopyClipboard(item.FullPath);
            if (!_window.IsClosing)
            {
                _window.ShowExpandedToast(
                    MainWindow.LocalizeToast($"已复制“{item.Name}”", $"Copied \"{item.Name}\"", $"「{item.Name}」をコピーしました"),
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法复制文件项目：{item.FullPath}", ex);
            _window.ShowOrganizerMessage(AppStrings.Format("CopyItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
    }

    public async Task CutFileItemAsync(WidgetItem item)
    {
        try
        {
            if (MainWindow.IsDocumentWidgetItem(item) && !await _window.Host.FlushPortableWindowAsync(item.FullPath))
                throw new IOException(AppStrings.Get(item.Kind == WidgetItemKind.PortableTodo
                    ? "TodoDragSaveFailed"
                    : "NoteDragSaveFailed"));
            ShellDragService.SetCutClipboard(item.FullPath);
            if (!_window.IsClosing)
            {
                _window.ShowExpandedToast(
                    MainWindow.LocalizeToast($"已剪切“{item.Name}”", $"Cut \"{item.Name}\"", $"「{item.Name}」を切り取りました"),
                    InfoBarSeverity.Informational);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法剪切文件项目：{item.FullPath}", ex);
            _window.ShowOrganizerMessage(AppStrings.Format("CutItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
    }

    public async Task ShowRenameFileDialogAsync(WidgetItem item)
    {
        if (_window.IsClosing || _activeDialogCount > 0) return;
        bool directory = Directory.Exists(item.FullPath);
        string initialName = directory
            ? Path.GetFileName(item.FullPath)
            : Path.GetFileNameWithoutExtension(item.FullPath);
        string acceptedName = initialName;
        _activeDialogCount++;
        _window.AdjustOverlayCount(+1);
        _window.PauseOutsideClickForOverlay();
        try
        {
            await YieldForMenuDismissAsync();
            if (_window.IsClosing) return;
            _window.DesktopLayer?.SetInputActivation(true);
            _window.ResetStationDelay();
            DisplayInfo display = _window.IsDockWindow ? _window.ResolveRenameDialogDisplay()
                : _window.GetOrResolveStationDisplay();
            bool accepted = await OwnedDialogWindow.ShowTextInputAsync(
                _window.Hwnd,
                display,
                _window.Host,
                AppStrings.Get("RenameItemTitle"),
                initialName,
                AppStrings.Get("Rename"),
                AppStrings.Get("Cancel"),
                candidate =>
                {
                    acceptedName = candidate.Trim();
                    return TryGetRenameTarget(item, acceptedName, out _);
                });
            if (accepted)
            {
                await RenameFileItemAsync(item, acceptedName);
                if (!_window.IsClosing)
                {
                    _window.ShowExpandedToast(
                        MainWindow.LocalizeToast($"已重命名为“{acceptedName}”", $"Renamed to \"{acceptedName}\"", $"「{acceptedName}」に名前を変更しました"),
                        InfoBarSeverity.Success);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法重命名文件项目：{item.FullPath}", ex);
            _window.ShowOrganizerMessage(AppStrings.Format("RenameItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _activeDialogCount = Math.Max(0, _activeDialogCount - 1);
            _window.AdjustOverlayCount(-1);
            if (!_window.IsClosing)
            {
                _window.DesktopLayer?.SetInputActivation(_window.IsContextMenuActivated || IsAddingItem);
                _window.ResetStationDelay();
                _window.ResumeOutsideClickAfterOverlay();
            }
        }
    }

    private string? TryGetRenameTarget(WidgetItem item, string candidate, out string targetPath)
    {
        targetPath = item.FullPath;
        bool directory = Directory.Exists(item.FullPath);
        string extension = directory ? string.Empty : Path.GetExtension(item.FullPath);
        if (candidate.Length == 0) return AppStrings.Get("ItemNameRequired");
        if (candidate.Length + extension.Length > 255 ||
            candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            candidate.EndsWith('.') || candidate.EndsWith(' '))
        {
            return AppStrings.Get("FolderNameInvalidError");
        }
        if (NoteStore.IsReservedDeviceName(candidate.Split('.', 2)[0]))
            return AppStrings.Get("FolderNameReservedError");
        string directoryPath = Path.GetDirectoryName(item.FullPath) ?? _window.Storage.ItemsRoot;
        targetPath = Path.Combine(directoryPath, candidate + extension);
        if (!targetPath.Equals(item.FullPath, StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(targetPath) || Directory.Exists(targetPath)))
        {
            return AppStrings.Get("ItemNameDuplicate");
        }
        return null;
    }

    private async Task RenameFileItemAsync(WidgetItem item, string candidate)
    {
        string? error = TryGetRenameTarget(item, candidate, out string targetPath);
        if (error is not null) throw new InvalidOperationException(error);
        if (targetPath.Equals(item.FullPath, StringComparison.Ordinal)) return;
        if (item.Kind == WidgetItemKind.PortableNote)
        {
            await _window.Host.RenameExternalNoteAsync(item.FullPath, candidate);
            return;
        }
        if (item.Kind == WidgetItemKind.PortableTodo)
        {
            await _window.Host.RenameExternalTodoAsync(item.FullPath, candidate);
            return;
        }

        OrganizerDefinition definition = _window.Definition;
        bool directory = Directory.Exists(item.FullPath);
        string oldRelativeName = item.RelativeName;
        string newRelativeName = Path.GetFileName(targetPath);
        int orderIndex = definition.ItemOrder.FindIndex(value =>
            value.Equals(oldRelativeName, StringComparison.OrdinalIgnoreCase));
        int oldSlot = 0;
        bool hadSlot = definition.ItemSlots is not null &&
            definition.ItemSlots.TryGetValue(oldRelativeName, out oldSlot);
        int overwrittenSlot = 0;
        bool hadTargetSlotCollision = definition.ItemSlots is not null &&
            !oldRelativeName.Equals(newRelativeName, StringComparison.OrdinalIgnoreCase) &&
            definition.ItemSlots.TryGetValue(newRelativeName, out overwrittenSlot);
        if (directory) Directory.Move(item.FullPath, targetPath);
        else File.Move(item.FullPath, targetPath);
        if (orderIndex >= 0) definition.ItemOrder[orderIndex] = newRelativeName;
        if (hadSlot && definition.ItemSlots is not null)
        {
            definition.ItemSlots.Remove(oldRelativeName);
            definition.ItemSlots[newRelativeName] = oldSlot;
        }
        try
        {
            if (orderIndex >= 0 || hadSlot) await _window.Host.SaveStateAsync();
        }
        catch
        {
            try
            {
                if (directory) Directory.Move(targetPath, item.FullPath);
                else File.Move(targetPath, item.FullPath);
                if (orderIndex >= 0) definition.ItemOrder[orderIndex] = oldRelativeName;
                if (hadSlot && definition.ItemSlots is not null)
                {
                    definition.ItemSlots.Remove(newRelativeName);
                    if (hadTargetSlotCollision) definition.ItemSlots[newRelativeName] = overwrittenSlot;
                    definition.ItemSlots[oldRelativeName] = oldSlot;
                }
            }
            catch (Exception rollbackError)
            {
                AppLogger.Error($"无法回滚文件项目重命名：{targetPath}", rollbackError);
            }
            throw;
        }
        await RefreshCatalogAsync(notifyUnsupported: false);
    }

    public async Task DeleteFileItemAsync(WidgetItem item)
    {
        try
        {
            if (MainWindow.IsDocumentWidgetItem(item) && !await _window.Host.FlushPortableWindowAsync(item.FullPath))
                throw new IOException(AppStrings.Get(item.Kind == WidgetItemKind.PortableTodo
                    ? "TodoDragSaveFailed"
                    : "NoteDragSaveFailed"));
            await Task.Run(() =>
            {
                if (Directory.Exists(item.FullPath))
                {
                    FileSystem.DeleteDirectory(
                        item.FullPath,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin,
                        UICancelOption.ThrowException);
                }
                else if (File.Exists(item.FullPath))
                {
                    FileSystem.DeleteFile(
                        item.FullPath,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin,
                        UICancelOption.ThrowException);
                }
            });
            if (MainWindow.IsDocumentWidgetItem(item)) _window.Host.ClosePortableWindowWithoutSave(item.FullPath);
            StartWatcher();
            await RefreshCatalogAsync(notifyUnsupported: false);
            if (!_window.IsClosing)
            {
                _window.ShowExpandedToast(
                    MainWindow.LocalizeToast($"已将“{item.Name}”移入回收站", $"Moved \"{item.Name}\" to Recycle Bin", $"「{item.Name}」をごみ箱に移動しました"),
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法将文件项目移入回收站：{item.FullPath}", ex);
            _window.ShowOrganizerMessage(AppStrings.Format("DeleteItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
    }

    public async Task ShowRenameNoteDialogAsync(Guid noteId)
    {
        if (_window.IsClosing || _activeDialogCount > 0) return;
        OrganizerDefinition definition = _window.Definition;
        NoteDefinition? note = definition.Notes.FirstOrDefault(item => item.Id == noteId);
        if (note is null) return;
        _activeDialogCount++;
        _window.AdjustOverlayCount(+1);
        _window.PauseOutsideClickForOverlay();
        string acceptedName = note.Name;
        try
        {
            await YieldForMenuDismissAsync();
            if (_window.IsClosing) return;
            _window.DesktopLayer?.SetInputActivation(true);
            _window.ResetStationDelay();
            DisplayInfo display = _window.IsDockWindow ? _window.ResolveRenameDialogDisplay()
                : DisplayPlacementService.GetDisplay(definition.Position?.MonitorDevice);
            bool accepted = await OwnedDialogWindow.ShowTextInputAsync(
                _window.Hwnd,
                display,
                _window.Host,
                AppStrings.Get("NoteRenameTitle"),
                note.Name,
                AppStrings.Get("Rename"),
                AppStrings.Get("Cancel"),
                candidate =>
                {
                    acceptedName = candidate.Trim();
                    if (acceptedName.Length == 0) return AppStrings.Get("NoteNameRequired");
                    return OrganizerNoteRules.IsNameAvailable(
                        definition.Notes.Where(item => item.Id != noteId).Select(item => item.Name),
                        acceptedName) ? null : AppStrings.Get("NoteNameDuplicate");
                });
            if (accepted)
            {
                await _window.Host.RenameNoteAsync(definition.Id, noteId, acceptedName);
                if (!_window.IsClosing)
                {
                    _window.ShowExpandedToast(
                        MainWindow.LocalizeToast($"已重命名为“{acceptedName}”", $"Renamed to \"{acceptedName}\"", $"「{acceptedName}」に名前を変更しました"),
                        InfoBarSeverity.Success);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法重命名便签。", ex);
            _window.ShowOrganizerMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _activeDialogCount = Math.Max(0, _activeDialogCount - 1);
            _window.AdjustOverlayCount(-1);
            if (!_window.IsClosing)
            {
                _window.DesktopLayer?.SetInputActivation(_window.IsContextMenuActivated || IsAddingItem);
                _window.ResetStationDelay();
                _window.ResumeOutsideClickAfterOverlay();
            }
        }
    }

    public async Task ShowDeleteNoteDialogAsync(Guid noteId)
    {
        if (_window.IsClosing || _activeDialogCount > 0) return;
        OrganizerDefinition definition = _window.Definition;
        NoteDefinition? note = definition.Notes.FirstOrDefault(item => item.Id == noteId);
        if (note is null) return;
        _activeDialogCount++;
        _window.AdjustOverlayCount(+1);
        _window.PauseOutsideClickForOverlay();
        try
        {
            await YieldForMenuDismissAsync();
            if (_window.IsClosing) return;
            _window.DesktopLayer?.SetInputActivation(true);
            _window.ResetStationDelay();
            DisplayInfo display = DisplayPlacementService.GetDisplay(definition.Position?.MonitorDevice);
            bool accepted = await OwnedDialogWindow.ShowConfirmationAsync(
                _window.Hwnd,
                display,
                _window.Host,
                AppStrings.Get("NoteDeleteTitle"),
                AppStrings.Format("NoteDeleteMessageFormat", note.Name),
                AppStrings.Get("Delete"),
                AppStrings.Get("Cancel"));
            if (accepted)
            {
                await _window.Host.DeleteNoteAsync(definition.Id, noteId);
                if (!_window.IsClosing)
                {
                    _window.ShowExpandedToast(
                        MainWindow.LocalizeToast($"已删除便签“{note.Name}”", $"Deleted note \"{note.Name}\"", $"付箋「{note.Name}」を削除しました"),
                        InfoBarSeverity.Success);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法删除便签。", ex);
            _window.ShowOrganizerMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _activeDialogCount = Math.Max(0, _activeDialogCount - 1);
            _window.AdjustOverlayCount(-1);
            if (!_window.IsClosing)
            {
                _window.DesktopLayer?.SetInputActivation(_window.IsContextMenuActivated || IsAddingItem);
                _window.ResetStationDelay();
                _window.ResumeOutsideClickAfterOverlay();
            }
        }
    }

    public async Task ImportFromDragAsync(DragEventArgs e)
    {
        using var diagnostic = AppLogger.Begin(DiagnosticArea.Drop);
        e.AcceptedOperation = DataPackageOperation.None;
        if (!_window.HasActiveLocalFileDrop) return;
        if (e.AllowedOperations == DataPackageOperation.None) return;

        string[] paths = [];
        Exception? storageReadError = null;
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            try
            {
                IReadOnlyList<IStorageItem> storageItems = await e.DataView.GetStorageItemsAsync().AsTask(_window.LifetimeToken);
                paths = storageItems.Select(item => item.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (OperationCanceledException) when (_window.IsClosing) { throw; }
            catch (Exception ex)
            {
                storageReadError = ex;
            }
        }

        if (paths.Length == 0)
        {
            _ = ShellDragService.TryGetFileDropPaths(e.DataView, out paths);
        }

        if (paths.Length == 0)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            diagnostic.Fail(storageReadError ?? new InvalidOperationException());
            string reason = storageReadError is not null && !string.IsNullOrWhiteSpace(storageReadError.Message)
                ? storageReadError.Message
                : AppStrings.Get("NoImportableItems");
            _window.ShowOrganizerMessage(AppStrings.Format("ReadDropErrorFormat", reason), InfoBarSeverity.Error);
            return;
        }

        bool controlPressed = (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool fromDesktop = paths.All(ShellFileOperationService.IsDesktopOrigin);
        DataPackageOperation operation = OrganizerInteractionMath.SelectIncomingDropOperation(
            e.AllowedOperations,
            fromDesktop,
            controlPressed);
        if (operation == DataPackageOperation.None) return;
        bool move = operation == DataPackageOperation.Move;

        if (!_window.IsExpandedCore)
        {
            await _window.ExpandOrganizerAsync(scrollToEnd: true);
        }

        _window.Host.LogStatus("TuckPane", AppStrings.Format(
            move ? "MovingItemsFormat" : "CopyingItemsFormat",
            AppStrings.FormatItemCount(paths.Length)));
        IProgress<TransferProgress>? progress = null;
        AppLogger.Record(DiagnosticArea.Drop, DiagnosticStage.DataRead, diagnostic.Id, count: paths.Length);

        try
        {
            IReadOnlyList<TransferOutcome> outcomes = await _window.Host.TransferQueue.RunAsync(token => move
                ? _window.Storage.ImportBatchAsync(paths, progress, token)
                : _window.Storage.CopyBatchAsync(paths, progress, token), _window.LifetimeToken);
            e.AcceptedOperation = IncomingDropOperation.ResultOperation(operation, e.AllowedOperations, outcomes);
            if (outcomes.All(outcome => outcome.Status is TransferStatus.Moved or TransferStatus.Copied or TransferStatus.ShortcutCreated))
                diagnostic.Complete(outcomes.Count);
            else diagnostic.Fail(new IOException());
            RebindMovedPortableWindows(outcomes);
            if (_window.IsClosing) return;
            StartWatcher();
            await RefreshCatalogAsync(notifyUnsupported: false);
            if (!_window.ConsumeDropPositionedInPlace())
                await _window.ScrollToEndAfterRenderAsync();

            TransferOutcome[] warnings = outcomes.Where(outcome => move
                ? outcome.Status is not (TransferStatus.Moved or TransferStatus.ShortcutCreated or TransferStatus.Cancelled)
                : outcome.Status is not (TransferStatus.Copied or TransferStatus.ShortcutCreated or TransferStatus.Cancelled)).ToArray();
            if (warnings.Length == 0)
            {
                if (outcomes.Any(outcome => outcome.Status == TransferStatus.Cancelled))
                    _window.Host.LogStatus("TuckPane", AppStrings.Get(move ? "MoveCancelled" : "CopyCancelled"));
                else
                    _window.Host.LogStatus("TuckPane", AppStrings.Format(
                        move ? "MovedItemsFormat" : "CopiedItemsFormat",
                        AppStrings.FormatItemCount(outcomes.Count)));
            }
            else
            {
                _window.ShowOrganizerMessage(string.Join(" ", warnings.Select(outcome => $"{Path.GetFileName(outcome.SourcePath)}：{outcome.Message}")), InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            diagnostic.Fail(new OperationCanceledException());
            e.AcceptedOperation = DataPackageOperation.None;
            if (!_window.IsClosing) _window.ShowOrganizerMessage(AppStrings.Get(move ? "MoveCancelled" : "CopyCancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppLogger.Error("拖入失败。", ex);
            diagnostic.Fail(ex);
            e.AcceptedOperation = DataPackageOperation.None;
            if (!_window.IsClosing) _window.ShowOrganizerMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    public async Task PasteClipboardAsync(NativeMethods.POINT contextMenuScreenPoint)
    {
        _window.AdjustOverlayCount(+1);
        _window.PauseOutsideClickForOverlay();
        _window.ResetStationDelay();
        bool catalogRefreshed = false;
        try
        {
            DataPackageView data = Clipboard.GetContent();
            bool hasNativeFiles = ShellDragService.TryGetClipboardPaths(out string[] nativePaths, out bool nativeMove);
            if (data.Contains(StandardDataFormats.StorageItems) || hasNativeFiles)
            {
                string[] paths;
                bool move;
                try
                {
                    IReadOnlyList<IStorageItem> items = await data.GetStorageItemsAsync();
                    paths = items.Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
                    move = data.RequestedOperation.HasFlag(DataPackageOperation.Move);
                }
                catch when (hasNativeFiles)
                {
                    paths = nativePaths;
                    move = nativeMove;
                }
                if (paths.Length == 0 && hasNativeFiles)
                {
                    paths = nativePaths;
                    move = nativeMove;
                }
                if (paths.Length == 0)
                {
                    _window.ShowExpandedToast(
                        MainWindow.LocalizeToast("剪贴板暂无可粘贴内容", "Clipboard is empty", "クリップボードに貼り付け可能な内容がありません"),
                        InfoBarSeverity.Informational);
                    return;
                }

                IProgress<TransferProgress>? progress = null;
                IReadOnlyList<TransferOutcome> outcomes = await _window.Host.TransferQueue.RunAsync(token => move
                    ? _window.Storage.ImportBatchAsync(paths, progress, token)
                    : _window.Storage.CopyBatchAsync(paths, progress, token));
                RebindMovedPortableWindows(outcomes);
                StartWatcher();
                await RefreshCatalogAsync(notifyUnsupported: false);
                await _window.ScrollToEndAfterRenderAsync();
                catalogRefreshed = true;
                TransferOutcome[] warnings = outcomes.Where(outcome => outcome.Status is not (
                    TransferStatus.Moved or TransferStatus.Copied or TransferStatus.ShortcutCreated or TransferStatus.Cancelled)).ToArray();
                if (warnings.Length > 0)
                {
                    _window.ShowOrganizerMessage(string.Join(" ", warnings.Select(outcome => $"{Path.GetFileName(outcome.SourcePath)}：{outcome.Message}")), InfoBarSeverity.Warning);
                    return;
                }

                if (outcomes.Any(outcome => outcome.Status == TransferStatus.Cancelled))
                {
                    string cancelled = AppStrings.Get("PasteCancelled");
                    _window.Host.LogStatus("TuckPane", cancelled);
                    _window.ShowExpandedToast(cancelled, InfoBarSeverity.Informational);
                    return;
                }

                DataPackageOperation completed = move && outcomes.All(outcome => outcome.Status == TransferStatus.Moved)
                    ? DataPackageOperation.Move
                    : DataPackageOperation.Copy;
                data.ReportOperationCompleted(completed);
                string message = AppStrings.Format(move ? "MovedItemsFormat" : "CopiedItemsFormat", AppStrings.FormatItemCount(outcomes.Count));
                _window.ShowOrganizerMessage(message, InfoBarSeverity.Success);
                _window.ShowExpandedToast(message, InfoBarSeverity.Success);
            }
            else if (data.Contains(StandardDataFormats.Bitmap))
            {
                string path = await SaveClipboardBitmapAsync(data);
                data.ReportOperationCompleted(DataPackageOperation.Copy);
                string message = AppStrings.Format("PastedImageFormat", Path.GetFileName(path));
                _window.ShowOrganizerMessage(message, InfoBarSeverity.Success);
                _window.ShowExpandedToast(message, InfoBarSeverity.Success);
            }
            else if (data.Contains(StandardDataFormats.Text))
            {
                string text = await data.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    _window.ShowExpandedToast(
                        MainWindow.LocalizeToast("剪贴板暂无可粘贴内容", "Clipboard is empty", "クリップボードに貼り付け可能な内容がありません"),
                        InfoBarSeverity.Informational);
                    return;
                }
                string notePath = await _window.Host.CreateNoteAsync(_window.OrganizerId, text, contextMenuScreenPoint);
                await _window.Host.OpenExternalNoteAsync(notePath);
                data.ReportOperationCompleted(DataPackageOperation.Copy);
                await _window.ScrollToEndAfterRenderAsync();
                _window.ShowExpandedToast(
                    MainWindow.LocalizeToast("已从剪贴板新建便签", "Created note from clipboard", "クリップボードから付箋を作成しました"),
                    InfoBarSeverity.Success);
                return;
            }
            else
            {
                _window.ShowExpandedToast(
                    MainWindow.LocalizeToast("剪贴板暂无可粘贴内容", "Clipboard is empty", "クリップボードに貼り付け可能な内容がありません"),
                    InfoBarSeverity.Informational);
                return;
            }

            if (!catalogRefreshed)
            {
                StartWatcher();
                await RefreshCatalogAsync(notifyUnsupported: false);
                await _window.ScrollToEndAfterRenderAsync();
            }
        }
        catch (OperationCanceledException)
        {
            string cancelled = AppStrings.Get("PasteCancelled");
            _window.ShowOrganizerMessage(cancelled, InfoBarSeverity.Informational);
            _window.ShowExpandedToast(cancelled, InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppLogger.Error("从剪贴板粘贴失败。", ex);
            _window.ShowOrganizerMessage(AppStrings.Format("PasteFailedFormat", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _window.AdjustOverlayCount(-1);
            if (!_window.IsClosing)
            {
                _window.ResetStationDelay();
                _window.ResumeOutsideClickAfterOverlay();
            }
        }
    }

    private async Task<string> SaveClipboardBitmapAsync(DataPackageView data)
    {
        RandomAccessStreamReference reference = await data.GetBitmapAsync();
        _window.Storage.EnsureCreated();
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string requestedDestination = Path.Combine(
            _window.Storage.ItemsRoot,
            AppStrings.Format("PastedImageNameFormat", stamp));
        string staging = Path.Combine(_window.Storage.ItemsRoot, $".glassfolder-staging-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(staging)) { }
            using IRandomAccessStreamWithContentType input = await reference.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);
            StorageFile stagingFile = await StorageFile.GetFileFromPathAsync(staging);
            using (IRandomAccessStream output = await stagingFile.OpenAsync(FileAccessMode.ReadWrite))
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }
            for (int attempt = 0; attempt < 32; attempt++)
            {
                string destination = StorageService.GetUniquePath(requestedDestination);
                try
                {
                    File.Move(staging, destination, overwrite: false);
                    return destination;
                }
                catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
                {
                    // Another writer claimed the name; pick a fresh unique path.
                }
            }
            throw new IOException(AppStrings.Get("AddItemNameConflict"));
        }
        finally
        {
            try
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"粘贴图片临时文件清理失败：{staging}", ex);
            }
        }
    }

    public async Task CreateNewFolderWithDialogAsync()
    {
        if (_window.IsClosing || _activeDialogCount > 0) return;
        OrganizerDefinition definition = _window.Definition;
        _activeDialogCount++;
        _window.AdjustOverlayCount(+1);
        _window.PauseOutsideClickForOverlay();
        string defaultName = AppStrings.Get("NewFolderDefaultName");
        string? createdPath = null;
        try
        {
            await YieldForMenuDismissAsync();
            if (_window.IsClosing) return;
            _window.DesktopLayer?.SetInputActivation(true);
            _window.ResetStationDelay();
            DisplayInfo display = DisplayPlacementService.GetDisplay(definition.Position?.MonitorDevice);
            bool accepted = await OwnedDialogWindow.ShowTextInputAsync(
                _window.Hwnd,
                display,
                _window.Host,
                AppStrings.Get("NewFolderTitle"),
                defaultName,
                AppStrings.Get("Create"),
                AppStrings.Get("Cancel"),
                name =>
                {
                    try
                    {
                        createdPath = _window.Storage.CreateUniqueFolder(name);
                        return null;
                    }
                    catch (Exception ex)
                    {
                        return ex.Message;
                    }
                });
            if (!accepted || createdPath is null) return;
            StartWatcher();
            await RefreshCatalogAsync(notifyUnsupported: false);
            await _window.ScrollToEndAfterRenderAsync();
            string message = AppStrings.Format("FolderCreatedFormat", Path.GetFileName(createdPath));
            _window.ShowOrganizerMessage(message, InfoBarSeverity.Success);
            _window.ShowExpandedToast(message, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLogger.Error("新建文件夹失败。", ex);
            _window.ShowOrganizerMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _activeDialogCount = Math.Max(0, _activeDialogCount - 1);
            if (!_window.IsClosing)
            {
                _window.DesktopLayer?.SetInputActivation(_window.IsContextMenuActivated || IsAddingItem);
                _window.ResetStationDelay();
            }
            _window.AdjustOverlayCount(-1);
            if (!_window.IsClosing) _window.ResumeOutsideClickAfterOverlay();
        }
    }

    public async Task ShowDeleteOrganizerDialogAsync()
    {
        if (_window.IsClosing || _activeDialogCount > 0) return;
        _activeDialogCount++;
        _window.AdjustOverlayCount(+1);
        _window.PauseOutsideClickForOverlay();
        try
        {
            if (!_window.IsExpandedCore) await _window.ExpandOrganizerAsync();
            if (_window.IsClosing) return;
            if (_window.Host.TransferQueue.IsActive)
            {
                _window.ShowOrganizerMessage(AppStrings.Get("TransferBeforeDelete"), InfoBarSeverity.Warning);
                return;
            }

            await YieldForMenuDismissAsync();
            if (_window.IsClosing) return;
            OrganizerDefinition definition = _window.Definition;
            _window.DesktopLayer?.SetInputActivation(true);
            _window.ResetStationDelay();
            string storagePath = AppPaths.ResolveStoragePath(definition);
            DisplayInfo display = _window.TryGetWindowRect(out NativeMethods.RECT dialogBounds)
                ? DisplayPlacementService.ForBounds(dialogBounds)
                : DisplayPlacementService.GetDisplay(definition.Position?.MonitorDevice);
            OrganizerDeleteDisposition? choice = await OwnedDialogWindow.ShowDeleteChoiceAsync(
                _window.Hwnd, display, _window.Host, definition.Name, storagePath);
            if (_window.IsClosing || choice is not OrganizerDeleteDisposition disposition) return;
            TransferOutcome outcome = await _window.Host.DeleteOrganizerAsync(definition.Id, disposition);
            if (!_window.IsClosing && outcome.Status is not (TransferStatus.Moved or TransferStatus.Retained))
            {
                _window.ShowOrganizerMessage(outcome.Message, outcome.Status == TransferStatus.CopiedSourceRetained ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法删除收纳窗。", ex);
            if (!_window.IsClosing) _window.ShowOrganizerMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _activeDialogCount = Math.Max(0, _activeDialogCount - 1);
            if (!_window.IsClosing)
            {
                _window.DesktopLayer?.SetInputActivation(_window.IsContextMenuActivated || IsAddingItem);
                _window.ResetStationDelay();
            }
            _window.AdjustOverlayCount(-1);
            if (!_window.IsClosing) _window.ResumeOutsideClickAfterOverlay();
        }
    }

    public async Task ShowRenameOrganizerDialogAsync()
    {
        if (_window.IsClosing || _activeDialogCount > 0) return;
        _activeDialogCount++;
        _window.AdjustOverlayCount(+1);
        _window.PauseOutsideClickForOverlay();
        try
        {
            if (!_window.IsExpandedCore)
            {
                await _window.ExpandOrganizerAsync();
            }
            if (_window.IsClosing) return;
            await YieldForMenuDismissAsync();
            if (_window.IsClosing) return;
            OrganizerDefinition definition = _window.Definition;
            _window.DesktopLayer?.SetInputActivation(true);
            _window.ResetStationDelay();
            string acceptedName = definition.Name;
            DisplayInfo display = _window.ResolveRenameDialogDisplay();
            bool accepted = await OwnedDialogWindow.ShowTextInputAsync(
                _window.Hwnd,
                display,
                _window.Host,
                AppStrings.Get("RenameTitle"),
                definition.Name,
                AppStrings.Get("Save"),
                AppStrings.Get("Cancel"),
                candidate =>
                {
                    try { acceptedName = OrganizerNameChange.NormalizeName(candidate); return null; }
                    catch (Exception ex) { return ex.Message; }
                },
                maxLength: 40,
                placeholderText: AppStrings.Get("RenamePlaceholder"));
            if (!_window.IsClosing && accepted && acceptedName.Length > 0)
            {
                await _window.Host.RenameOrganizerAsync(definition.Id, acceptedName);
                if (!_window.IsClosing)
                {
                    _window.ShowExpandedToast(
                        MainWindow.LocalizeToast($"已重命名为“{acceptedName}”", $"Renamed to \"{acceptedName}\"", $"「{acceptedName}」に名前を変更しました"),
                        InfoBarSeverity.Success);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法重命名收纳窗。", ex);
            if (!_window.IsClosing) _window.ShowOrganizerMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _activeDialogCount = Math.Max(0, _activeDialogCount - 1);
            if (!_window.IsClosing)
            {
                _window.DesktopLayer?.SetInputActivation(_window.IsContextMenuActivated || IsAddingItem);
                _window.ResetStationDelay();
            }
            _window.AdjustOverlayCount(-1);
            if (!_window.IsClosing) _window.ResumeOutsideClickAfterOverlay();
        }
    }

    public void OpenStorageDirectory()
    {
        try
        {
            _window.Storage.EnsureCreated();
            if (_window.Host.IsPeekActive) _window.Host.ExitPeekMode();
            Process.Start(new ProcessStartInfo(_window.Storage.ItemsRoot) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法打开收纳目录：{_window.Storage.ItemsRoot}", ex);
            _window.ShowOrganizerMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    public async Task<bool> ImportFromPeerAsync(string path)
    {
        IReadOnlyList<TransferOutcome> outcomes = await _window.Host.TransferQueue.RunAsync(
            token => _window.Storage.ImportBatchAsync([path], progress: null, token));
        RebindMovedPortableWindows(outcomes);
        bool moved = outcomes.Any(outcome => outcome.Status is TransferStatus.Moved or TransferStatus.ShortcutCreated);
        if (moved)
        {
            StartWatcher();
            await RefreshCatalogAsync(notifyUnsupported: false);
        }
        return moved;
    }

    private void RebindMovedPortableWindows(IEnumerable<TransferOutcome> outcomes)
    {
        foreach (TransferOutcome outcome in outcomes)
        {
            if (outcome.Status == TransferStatus.Moved && outcome.DestinationPath is { } destination)
                _window.Host.RebindPortableWindowAfterMove(outcome.SourcePath, destination);
        }
    }
}
