using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace TuckPane;

/// <summary>
/// Coordinates note staging preparation/cleanup and external modal drag handoffs
/// (contained organizer drag, native Shell IDataObject drag, and WinUI StartDragAsync)
/// out of an organizer window.
/// </summary>
internal sealed class ExternalItemDragCoordinator
{
    private const int NoteDragShellCompletionGraceMs = 750;

    private readonly MainWindow _window;
    private Guid? _noteDragPreparationId;
    private Task<(string Path, bool RestoreWindow, IStorageItem StorageItem)>? _noteDragPreparationTask;
    private Task _noteDragCleanupTask = Task.CompletedTask;
    private CancellationTokenSource? _shellPreparationCancellation;
    private bool _hasContainedDragPreview;
    private NativeMethods.POINT _lastContainedDragPreviewCursor;
    private NativeMethods.RECT _lastContainedDragPreviewBounds;
    private IntPtr _containedDragPreviewOwner;

    public ExternalItemDragCoordinator(MainWindow window)
    {
        _window = window;
    }

    public void CancelShellPreparation() => _shellPreparationCancellation?.Cancel();

    public void ResetContainedDragPreview()
    {
        _hasContainedDragPreview = false;
        _containedDragPreviewOwner = IntPtr.Zero;
    }

    public bool IsSameContainedDragPreview(IntPtr ownerHwnd, NativeMethods.POINT cursor) =>
        _hasContainedDragPreview &&
        _containedDragPreviewOwner == ownerHwnd &&
        cursor.X == _lastContainedDragPreviewCursor.X &&
        cursor.Y == _lastContainedDragPreviewCursor.Y;

    public void RecordContainedDragPreview(IntPtr ownerHwnd, NativeMethods.POINT cursor, NativeMethods.RECT bounds)
    {
        _hasContainedDragPreview = true;
        _lastContainedDragPreviewCursor = cursor;
        _lastContainedDragPreviewBounds = bounds;
        _containedDragPreviewOwner = ownerHwnd;
    }

    public void BeginNoteDragPreparation(ItemReorderSession session, uint pointerId)
    {
        if (pointerId == 0 || session.SourceIndex < 0 || session.SourceIndex >= _window.ItemSnapshot.Count ||
            _window.ItemSnapshot[session.SourceIndex] is not { Kind: WidgetItemKind.Note, NoteId: Guid noteId }) return;
        _noteDragPreparationId = noteId;
        _noteDragPreparationTask = PrepareNoteDragDataAsync(noteId, _noteDragCleanupTask);
    }

    private async Task<(string Path, bool RestoreWindow, IStorageItem StorageItem)> PrepareNoteDragDataAsync(
        Guid noteId,
        Task previousCleanup)
    {
        await previousCleanup;
        (string path, bool restoreWindow) = await _window.Host.PrepareNoteDragAsync(_window.OrganizerId, noteId);
        try
        {
            StorageFile storageFile = await StorageFile.GetFileFromPathAsync(path);
            return (path, restoreWindow, storageFile);
        }
        catch
        {
            await _window.Host.CompleteNoteDragAsync(_window.OrganizerId, noteId, path, restoreWindow, moved: false);
            throw;
        }
    }

    public void DiscardNoteDragPreparation()
    {
        Guid? noteId = _noteDragPreparationId;
        Task<(string Path, bool RestoreWindow, IStorageItem StorageItem)>? preparation = _noteDragPreparationTask;
        _noteDragPreparationId = null;
        _noteDragPreparationTask = null;
        if (noteId is Guid id && preparation is not null)
            _noteDragCleanupTask = DiscardNoteDragPreparationAsync(id, preparation);
    }

    private async Task DiscardNoteDragPreparationAsync(
        Guid noteId,
        Task<(string Path, bool RestoreWindow, IStorageItem StorageItem)> preparation)
    {
        try
        {
            (string path, bool restoreWindow, _) = await preparation;
            await _window.Host.CompleteNoteDragAsync(_window.OrganizerId, noteId, path, restoreWindow, moved: false);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法清理未使用的便签拖放预热：{noteId}", ex);
        }
    }

    public async Task CompleteMouseNativeDragAsync(ItemReorderSession session)
    {
        if (!ReferenceEquals(_window.ActiveReorderSession, session)) return;
        if (session.SourceIndex < 0 || session.SourceIndex >= _window.ItemSnapshot.Count)
        {
            _window.CancelActiveItemReorder();
            return;
        }
        if (!ShellDragService.RequiresNativeDrag(_window.ItemSnapshot[session.SourceIndex].Kind))
        {
            _window.CancelActiveItemReorder();
            return;
        }

        WidgetItem item = _window.ItemSnapshot[session.SourceIndex];
        (string Path, bool RestoreWindow, IStorageItem StorageItem)? preparedNote = null;
        if (item is { Kind: WidgetItemKind.Note, NoteId: Guid noteId })
        {
            Task<(string Path, bool RestoreWindow, IStorageItem StorageItem)>? preparation =
                _noteDragPreparationId == noteId ? _noteDragPreparationTask : null;
            if (preparation is null)
            {
                _window.CancelActiveItemReorder();
                return;
            }
            try
            {
                preparedNote = await preparation;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"无法预热便签拖出：{item.Name}", ex);
                _window.ShowOrganizerMessage(AppStrings.Format("DragItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
                if (ReferenceEquals(_window.ActiveReorderSession, session)) _window.CancelActiveItemReorder();
                return;
            }
            if (!ReferenceEquals(_window.ActiveReorderSession, session))
            {
                await _noteDragCleanupTask;
                return;
            }
            if (_noteDragPreparationId != noteId || !ReferenceEquals(_noteDragPreparationTask, preparation))
            {
                _window.CancelActiveItemReorder();
                return;
            }
            _noteDragPreparationId = null;
            _noteDragPreparationTask = null;
        }

        _window.CaptureNativeDragCellDimensions();
        if (_window.CurrentItemDragHost is not { } host || _window.CurrentItemDragPointerPoint is not { } pointerPoint)
        {
            if (item.NoteId is Guid abandonedNoteId && preparedNote is { } abandoned)
            {
                _noteDragCleanupTask = DiscardNoteDragPreparationAsync(abandonedNoteId, Task.FromResult(abandoned));
                await _noteDragCleanupTask;
            }
            _window.CancelActiveItemReorder();
            return;
        }
        if (item is { Kind: WidgetItemKind.Organizer, OrganizerId: not null })
        {
            await _window.RunWindowSafelyAsync(
                () => BeginContainedOrganizerDragAsync(item, host, session),
                "收纳窗元素拖出启动失败");
            return;
        }
        IReadOnlyList<WidgetItem> dragItems = _window.GetDragPayloadItems(item);
        bool useNativeShellDrag = item.Kind != WidgetItemKind.Note;
        if (useNativeShellDrag)
        {
            await _window.RunWindowSafelyAsync(
                () => BeginNativeShellDragAsync(item, host, session, dragItems),
                "Shell 文件拖出启动失败");
            return;
        }
        await _window.RunWindowSafelyAsync(
            () => BeginXamlShellDragAsync(host, pointerPoint, session, preparedNote),
            "WinUI 文件拖出启动失败");
    }

    private async Task BeginContainedOrganizerDragAsync(WidgetItem item, Border host, ItemReorderSession session)
    {
        using var modeInteraction = _window.HoldModeInteraction();
        bool completed = false;
        bool timedOut = false;
        long dragStarted = Stopwatch.GetTimestamp();
        ResetContainedDragPreview();
        _window.BeginExternalDragHandoff(host, item.RelativeName, keepBoundaryHook: true, releasePointerCapture: true, startMotionRendering: false);
        using CancellationTokenSource dragCancellation = new(TimeSpan.FromSeconds(30));
        CancellationToken cancellationToken = dragCancellation.Token;
        try
        {
            bool cancelled = false;
            while (!_window.IsClosing &&
                   !cancellationToken.IsCancellationRequested &&
                   (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0)
            {
                if ((NativeMethods.GetAsyncKeyState(0x1B) & 0x8000) != 0)
                {
                    cancelled = true;
                    break;
                }
                if (NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
                {
                    _window.Host.UpdateOrganizerDragHover(_window, item.OrganizerId!.Value, cursor);
                    _window.Host.UpdateContainedOrganizerDragPreview(item.OrganizerId!.Value, cursor);
                }
                await Task.Delay(16, cancellationToken);
            }

            timedOut = !_window.IsClosing && cancellationToken.IsCancellationRequested;

            if (!cancelled && !timedOut && !_window.IsClosing && NativeMethods.GetCursorPos(out NativeMethods.POINT dropPoint))
            {
                await _window.Host.WaitForOrganizerDragHoverAsync(_window);
                string? error = await _window.Host.FinishContainedOrganizerDragAsync(item.OrganizerId!.Value, dropPoint);
                completed = error is null;
                if (error is not null) _window.ShowOrganizerMessage(error, InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            timedOut = !_window.IsClosing;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"收纳窗元素拖出失败：{item.Name}", ex);
            _window.ShowOrganizerMessage(AppStrings.Format("OrganizerContainerDropError", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _window.Host.EndOrganizerDragHover(_window);
            try
            {
                _window.Host.ReconcileContainedOrganizer(item.OrganizerId!.Value);
                _window.CompleteExternalDragVisualState(session);
                _window.ResetExternalDragTiming();
                await _window.Host.ReconcileExclusiveExpansionAsync(completed ? null : _window);
                await _window.StartCatalogRefreshIfReady();
            }
            catch (Exception cleanupError)
            {
                AppLogger.Error("收纳窗元素拖出清理失败。", cleanupError);
                _window.TryCompleteExternalDragVisualState(session);
            }
            ResetContainedDragPreview();
            AppLogger.Performance($"nested-drag durationMs={Stopwatch.GetElapsedTime(dragStarted).TotalMilliseconds:0} completed={completed} timedOut={timedOut}");
        }
    }

    private async Task BeginNativeShellDragAsync(
        WidgetItem item,
        Border host,
        ItemReorderSession session,
        IReadOnlyList<WidgetItem>? batchItems = null)
    {
        using var modeInteraction = _window.HoldModeInteraction();
        IReadOnlyList<WidgetItem> dragWidgets = batchItems is { Count: > 1 } ? batchItems : [item];
        // Flush ALL open portable .tucknote / .tucktodo windows in the drag payload before Shell handoff!
        foreach (WidgetItem dragWidget in dragWidgets)
        {
            if (MainWindow.IsDocumentWidgetItem(dragWidget) &&
                !await _window.Host.FlushPortableWindowAsync(dragWidget.FullPath))
            {
                _window.ShowOrganizerMessage(AppStrings.Get(dragWidget.Kind == WidgetItemKind.PortableTodo
                    ? "TodoDragSaveFailed"
                    : "NoteDragSaveFailed"), InfoBarSeverity.Error);
                _window.CancelActiveItemReorder();
                return;
            }
        }

        ShellDragService.ShellDragSession? shellSession = null;
        string[] dragPaths = dragWidgets.Select(x => x.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string dragPath = dragPaths[0];
        bool internalDropAccepted = false;
        bool dragCompleted = false;
        bool visualStateFinished = false;
        long ghostToken = _window.EnsureFloatingDragGhostActive(item, host, session, dragWidgets);
        _window.BeginExternalDragHandoff(host, item.RelativeName, keepBoundaryHook: true, releasePointerCapture: true, startMotionRendering: true);
        using CancellationTokenSource preparationCancellation = new(TimeSpan.FromSeconds(10));
        _shellPreparationCancellation = preparationCancellation;
        try
        {
            (double cellWidth, double cellHeight) = _window.NativeDragCellDimensions;
            bool preferMove = OrganizerInteractionMath.ExternalDragRequestedOperation(item.Kind) == DataPackageOperation.Move;
            bool allowLink = OrganizerInteractionMath.ExternalDragAllowedOperations(item.Kind).HasFlag(DataPackageOperation.Link);
            shellSession = await ShellDragService.PrepareAsync(
                dragPaths,
                session.GrabOffset.X / cellWidth,
                session.GrabOffset.Y / cellHeight,
                preferMove: preferMove,
                cancellationToken: preparationCancellation.Token,
                suppressSystemDragImage: ghostToken != 0);

            FloatingDragBadgeKind lastBadgeKind = FloatingDragBadgeKind.None;
            string? lastBadgeText = null;
            void HandleShellDragFeedback(ShellDragFeedbackSnapshot fb)
            {
                if (ghostToken == 0) return;
                FloatingDragBadgeKind nextKind = FloatingDragBadgeKind.None;
                string? nextText = null;
                if (_window.ContainsScreenPoint(fb.Cursor) && _window.IsPointerOverWindow(fb.Cursor))
                {
                    nextKind = FloatingDragBadgeKind.None;
                    nextText = null;
                }
                else if (_window.Host.TryFindOrganizerUnderCursor(fb.Cursor, _window.OrganizerId, out string targetOrgName))
                {
                    if (fb.ControlHeld)
                    {
                        nextKind = FloatingDragBadgeKind.CopyToOrganizer;
                        nextText = AppStrings.Format("DragBadgeCopyToOrganizerFormat", targetOrgName);
                    }
                    else
                    {
                        nextKind = FloatingDragBadgeKind.MoveToOrganizer;
                        nextText = AppStrings.Format("DragBadgeMoveToOrganizerFormat", targetOrgName);
                    }
                }
                else if (fb.OverDesktop)
                {
                    if (fb.ControlHeld)
                    {
                        nextKind = FloatingDragBadgeKind.CopyToDesktop;
                        nextText = AppStrings.Get("DragBadgeCopyToDesktop");
                    }
                    else
                    {
                        nextKind = FloatingDragBadgeKind.MoveToDesktop;
                        nextText = AppStrings.Get("DragBadgeMoveToDesktop");
                    }
                }

                if (nextKind != lastBadgeKind || !string.Equals(nextText, lastBadgeText, StringComparison.Ordinal))
                {
                    lastBadgeKind = nextKind;
                    lastBadgeText = nextText;
                    FloatingDragGhostController.Shared.UpdateBadge(ghostToken, nextKind, nextText);
                }
            }

            ShellDragResult result = ShellDragService.Move(
                _window.Hwnd,
                shellSession,
                default,
                () => _window.IsClosing,
                allowLink: allowLink,
                onFeedback: ghostToken != 0 ? HandleShellDragFeedback : null);
            dragCompleted = result.Outcome != ShellDragOutcome.Cancelled;
            internalDropAccepted = _window.WasInternalOleDropAccepted;
            AppLogger.Info($"Shell拖出交接：结果={result.Outcome}，桌面复制={result.DesktopCopyRequested}，项目数={dragPaths.Length}，路径={dragPath}。");

            NativeMethods.POINT? desktopDropPoint = null;
            if (internalDropAccepted)
            {
                _window.CompleteFloatingDragGhostLanding(null);
            }
            else if (result.Outcome == ShellDragOutcome.DesktopRequested)
            {
                NativeMethods.POINT dropPoint = result.DesktopDropPoint
                    ?? result.LastCursorPoint
                    ?? (NativeMethods.GetCursorPos(out NativeMethods.POINT currentCursor) ? currentCursor : default);
                desktopDropPoint = dropPoint;
                _window.CompleteFloatingDragGhostLanding(dropPoint);
            }
            else if (result.Outcome == ShellDragOutcome.Cancelled)
            {
                _window.CompleteFloatingDragGhostRebound();
            }
            else
            {
                NativeMethods.POINT? landingPoint = result.LastCursorPoint
                    ?? (NativeMethods.GetCursorPos(out NativeMethods.POINT dropPt) ? dropPt : null);
                _window.CompleteFloatingDragGhostLanding(landingPoint);
            }

            if (internalDropAccepted && ReferenceEquals(_window.ActiveReorderSession, session))
            {
                await _window.CommitNativeItemReorderAsync(session);
            }
            else if (ReferenceEquals(_window.ActiveReorderSession, session))
            {
                bool desktopMoved = result.Outcome == ShellDragOutcome.DesktopRequested && !result.DesktopCopyRequested;
                session.MarkOutcome(result.Outcome == ShellDragOutcome.ExternalMoved || desktopMoved
                    ? ItemDragState.ExternalMoved
                    : ItemDragState.Cancelled);
                _window.AbortReorderVisualsAfterExternalDrag();
            }

            _window.SetShellDropFinalizing(true);
            _window.CompleteExternalDragVisualState(session);
            visualStateFinished = true;
            shellSession.Dispose();
            shellSession = null;

            if (result.Outcome == ShellDragOutcome.DesktopRequested)
            {
                var positionedDesktopPaths = new List<string>(dragPaths.Length);
                if (result.DesktopCopyRequested)
                {
                    bool anyCopied = false;
                    foreach (string currentPath in dragPaths)
                    {
                        TransferOutcome copied = await _window.Host.TransferQueue.RunAsync(async token =>
                        {
                            try { return await _window.Storage.CopyItemToDesktopAsync(currentPath, progress: null, token); }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex) { return new TransferOutcome(currentPath, null, TransferStatus.Failed, ex.Message); }
                        });
                        if (copied.Status is not (TransferStatus.Copied or TransferStatus.ShortcutCreated))
                        {
                            dragCompleted = false;
                            _window.ShowOrganizerMessage(copied.Message, copied.Status == TransferStatus.Cancelled
                                ? InfoBarSeverity.Informational
                                : InfoBarSeverity.Error);
                        }
                        else if (copied.DestinationPath is { } copiedDestination)
                        {
                            anyCopied = true;
                            positionedDesktopPaths.Add(copiedDestination);
                            await Task.Run(() => ShellChangeNotificationService.NotifyCreated(copiedDestination));
                        }
                    }
                    if (anyCopied && dragPaths.Length > 1) _window.ClearItemSelection();
                }
                else
                {
                    bool anyChanged = false;
                    foreach (string currentPath in dragPaths)
                    {
                        TransferOutcome moved = await _window.Host.TransferQueue.RunAsync(
                            token => _window.Storage.MoveItemToDesktopAsync(currentPath, progress: null, token));
                        if (moved.Status != TransferStatus.Moved)
                        {
                            dragCompleted = false;
                            _window.ShowOrganizerMessage(moved.Message, moved.Status == TransferStatus.Cancelled
                                ? InfoBarSeverity.Informational
                                : moved.Status == TransferStatus.CopiedSourceRetained ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
                            if (moved.Status == TransferStatus.CopiedSourceRetained)
                            {
                                if (moved.DestinationPath is { } retained && (File.Exists(retained) || Directory.Exists(retained)))
                                {
                                    positionedDesktopPaths.Add(retained);
                                    if (!File.Exists(currentPath))
                                        _window.Host.RebindPortableWindowAfterMove(currentPath, retained);
                                }
                                anyChanged = true;
                            }
                        }
                        else
                        {
                            if (moved.DestinationPath is { } destination)
                            {
                                positionedDesktopPaths.Add(destination);
                                _window.Host.RebindPortableWindowAfterMove(currentPath, destination);
                                await Task.Run(() => ShellChangeNotificationService.NotifyMoved(currentPath, destination));
                            }
                            anyChanged = true;
                        }
                    }
                    if (anyChanged)
                    {
                        if (dragPaths.Length > 1) _window.ClearItemSelection();
                        await _window.RefreshCatalogAsync(notifyUnsupported: false);
                    }
                }

                if (positionedDesktopPaths.Count > 0 && desktopDropPoint is NativeMethods.POINT dropPointPx)
                {
                    _ = PositionDroppedDesktopItemsSafelyAsync(positionedDesktopPaths, dropPointPx);
                }
            }
            else if (result.Outcome == ShellDragOutcome.ExternalMoved)
            {
                foreach (WidgetItem dragWidget in dragWidgets)
                {
                    if (MainWindow.IsDocumentWidgetItem(dragWidget) && !File.Exists(dragWidget.FullPath))
                        _window.Host.ClosePortableWindowWithoutSave(dragWidget.FullPath);
                }
                if (dragPaths.Length > 1) _window.ClearItemSelection();
                await Task.Delay(120);
                await _window.RefreshCatalogAsync(notifyUnsupported: false);
            }
        }
        catch (OperationCanceledException) when (preparationCancellation.IsCancellationRequested || _window.IsClosing)
        {
            AppLogger.Performance("shell-drag preparation cancelled");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Shell文件拖出失败：{item.FullPath}", ex);
            _window.ShowOrganizerMessage(AppStrings.Format("DragItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            try
            {
                _window.CancelFloatingDragGhostIfStillActive();
                _window.StopBoundaryHookForExternalDrag();
                if (!visualStateFinished) _window.CompleteExternalDragVisualState(session);
                shellSession?.Dispose();
                _window.ClearExternalDragFlags();
                _window.ResetExternalDragTiming();
                await _window.Host.ReconcileExclusiveExpansionAsync(dragCompleted ? null : _window);
                await _window.StartCatalogRefreshIfReady();
            }
            catch (Exception cleanupError)
            {
                AppLogger.Error("Shell 文件拖出清理失败。", cleanupError);
                _window.CancelFloatingDragGhostIfStillActive();
                _window.StopBoundaryHookForExternalDrag();
                shellSession?.Dispose();
                _window.ClearExternalDragFlags();
                _window.TryCompleteExternalDragVisualState(session);
            }
            if (ReferenceEquals(_shellPreparationCancellation, preparationCancellation))
                _shellPreparationCancellation = null;
        }
    }

    private static async Task PositionDroppedDesktopItemsSafelyAsync(
        IReadOnlyList<string> destinationPaths,
        NativeMethods.POINT dropPointPx)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            _ = await DesktopIconPositionService.PositionDroppedDesktopItemsAsync(
                destinationPaths,
                dropPointPx,
                cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Error("无法将拖出至桌面的图标定位到释放坐标。", ex);
        }
    }

    private async Task BeginXamlShellDragAsync(
        Border host,
        PointerPoint pointerPoint,
        ItemReorderSession session,
        (string Path, bool RestoreWindow, IStorageItem StorageItem)? preparedNote)
    {
        using var modeInteraction = _window.HoldModeInteraction();
        if (!ReferenceEquals(_window.ActiveReorderSession, session) ||
            session.SourceIndex < 0 || session.SourceIndex >= _window.ItemSnapshot.Count) return;

        WidgetItem item = _window.ItemSnapshot[session.SourceIndex];
        if (MainWindow.IsDocumentWidgetItem(item) && !await _window.Host.FlushPortableWindowAsync(item.FullPath))
        {
            _window.ShowOrganizerMessage(AppStrings.Get(item.Kind == WidgetItemKind.PortableTodo
                ? "TodoDragSaveFailed"
                : "NoteDragSaveFailed"), InfoBarSeverity.Error);
            _window.CancelActiveItemReorder();
            return;
        }
        Guid? draggedNoteId = item.Kind == WidgetItemKind.Note ? item.NoteId : null;
        string dragPath = preparedNote?.Path ?? item.FullPath;
        string? noteStagingPath = preparedNote?.Path;
        bool restoreNoteWindow = preparedNote?.RestoreWindow ?? false;
        bool noteDragCompleted = false;
        bool internalDropAccepted = false;
        bool dragCompleted = false;
        bool visualStateFinished = false;
        _window.BeginExternalDragHandoff(host, item.RelativeName, keepBoundaryHook: false, releasePointerCapture: false, startMotionRendering: true, hideHostImmediately: false);

        async void PopulateDragData(UIElement _, DragStartingEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                DataPackageOperation allowed = OrganizerInteractionMath.ExternalDragAllowedOperations(item.Kind);
                args.Data.RequestedOperation = OrganizerInteractionMath.ExternalDragRequestedOperation(item.Kind);
                IStorageItem storageItem;
                if (draggedNoteId is not null)
                {
                    storageItem = preparedNote?.StorageItem ??
                        throw new InvalidOperationException("The internal note drag was not prepared before shell handoff.");
                }
                else
                {
                    storageItem = Directory.Exists(dragPath)
                        ? await StorageFolder.GetFolderFromPathAsync(dragPath)
                        : await StorageFile.GetFileFromPathAsync(dragPath);
                }
                args.Data.SetStorageItems([storageItem], readOnly: false);
                args.AllowedOperations = allowed;
                _window.ApplyExternalDragPlaceholder(host);
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                AppLogger.Error($"无法准备 WinUI 文件拖出：{dragPath}", ex);
            }
            finally
            {
                deferral.Complete();
            }
        }

        host.DragStarting += PopulateDragData;
        try
        {
            DataPackageOperation operation = await host.StartDragAsync(pointerPoint);
            internalDropAccepted = _window.WasInternalOleDropAccepted;
            if (draggedNoteId is not null && operation == DataPackageOperation.None && !internalDropAccepted)
                await Task.Delay(NoteDragShellCompletionGraceMs);
            bool sourceItemExists = File.Exists(dragPath) || Directory.Exists(dragPath);
            bool externalSourceMoved = OrganizerInteractionMath.ExternalDragMovedSource(
                operation,
                internalDropAccepted,
                sourceItemExists);
            dragCompleted = operation != DataPackageOperation.None || externalSourceMoved;
            AppLogger.Info($"WinUI拖出交接：结果={operation}，路径={dragPath}。");
            if (_window.IsFloatingDragGhostActive)
            {
                if (internalDropAccepted)
                    _window.CompleteFloatingDragGhostLanding(null);
                else if (dragCompleted)
                    _window.CompleteFloatingDragGhostLanding(NativeMethods.GetCursorPos(out NativeMethods.POINT dropPt) ? dropPt : null);
                else
                    _window.CompleteFloatingDragGhostRebound();
            }
            if (internalDropAccepted && ReferenceEquals(_window.ActiveReorderSession, session))
            {
                await _window.CommitNativeItemReorderAsync(session);
            }
            else if (ReferenceEquals(_window.ActiveReorderSession, session))
            {
                session.MarkOutcome(externalSourceMoved
                    ? ItemDragState.ExternalMoved
                    : ItemDragState.Cancelled);
                _window.AbortReorderVisualsAfterExternalDrag();
            }

            _window.SetShellDropFinalizing(true);
            _window.CompleteExternalDragVisualState(session);
            visualStateFinished = true;
            if (draggedNoteId is Guid completedNoteId && noteStagingPath is not null)
            {
                try
                {
                    await _window.Host.CompleteNoteDragAsync(
                        _window.OrganizerId,
                        completedNoteId,
                        noteStagingPath,
                        restoreNoteWindow,
                        moved: externalSourceMoved);
                }
                finally
                {
                    noteDragCompleted = true;
                }
            }
            else if (externalSourceMoved)
            {
                if (MainWindow.IsDocumentWidgetItem(item)) _window.Host.ClosePortableWindowWithoutSave(item.FullPath);
                await Task.Delay(120);
                await _window.RefreshCatalogAsync(notifyUnsupported: false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"WinUI 文件拖出失败：{dragPath}", ex);
            _window.ShowOrganizerMessage(AppStrings.Format("DragItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            try
            {
                if (!noteDragCompleted && draggedNoteId is Guid noteId && noteStagingPath is not null)
                {
                    try
                    {
                        await _window.Host.CompleteNoteDragAsync(
                            _window.OrganizerId,
                            noteId,
                            noteStagingPath,
                            restoreNoteWindow,
                            moved: false);
                    }
                    catch (Exception cleanupError)
                    {
                        AppLogger.Error($"无法恢复取消的便签拖出：{noteId}", cleanupError);
                    }
                }
                _window.CancelFloatingDragGhostIfStillActive();
                host.DragStarting -= PopulateDragData;
                host.ReleasePointerCaptures();
                _ = NativeMethods.ReleaseCapture();
                if (!visualStateFinished) _window.CompleteExternalDragVisualState(session);
                _window.ClearExternalDragFlags();
                _window.ResetExternalDragTiming();
                await _window.Host.ReconcileExclusiveExpansionAsync(dragCompleted ? null : _window);
                await _window.StartCatalogRefreshIfReady();
            }
            catch (Exception cleanupError)
            {
                AppLogger.Error("WinUI 文件拖出清理失败。", cleanupError);
                _window.CancelFloatingDragGhostIfStillActive();
                host.DragStarting -= PopulateDragData;
                host.ReleasePointerCaptures();
                _ = NativeMethods.ReleaseCapture();
                if (!visualStateFinished) _window.TryCompleteExternalDragVisualState(session);
                _window.ClearExternalDragFlags();
            }
        }
    }
}
