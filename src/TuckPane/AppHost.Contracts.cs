using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

internal interface IOrganizerHost
{
    AppStateV2 State { get; }
    TransferQueue TransferQueue { get; }
    ShellLaunchService ShellLauncher { get; }
    IconCacheService SharedIconCache { get; }
    ConsoleWindow Console { get; }
    event EventHandler? ThemeChanged;

    Task SaveStateAsync();
    void OpenConsole(Guid? organizerId = null);
    void LogStatus(string title, string message);
    void ReportOperationError(string title, string message, IntPtr owner = default, string? monitorDevice = null);
    void NotifyTransparencyFallback();

    IReadOnlyList<WindowAlignmentTarget> GetWindowAlignmentTargets(MainWindow source, DisplayInfo display);
    IReadOnlyList<WidgetItem> GetContainedOrganizerItems(Guid containerId);
    IReadOnlyList<WidgetItem> GetOrganizerPreviewItems(Guid organizerId);
    void NotifyOrganizerPreviewChanged(Guid organizerId);

    Task<OrganizerDefinition> DuplicateOrganizerAsync(Guid id);
    Task<TransferOutcome> DeleteOrganizerAsync(Guid id, OrganizerDeleteDisposition disposition);
    Task<string?> ToggleOrganizerModeAsync(Guid id);
    Task<string?> ToggleOrganizerExpandedContentModeAsync(Guid id);
    Task RenameOrganizerAsync(Guid organizerId, string name);
    Task SetOrganizerNameVisibilityAsync(Guid id, bool hideName);

    OrganizerExpansionMode GetRequestedExpansionMode(Guid id);
    Task<string?> SetOrganizerExpansionModeAsync(Guid id, OrganizerExpansionMode? requestedMode = null, Func<CancellationToken, Task<bool>>? prepare = null);
    void CloseExpansionModeCoordinator(Guid id);
    void NotifyPermanentExpanded(MainWindow window);

    DesktopGridPlacement? FindNearestPositionedPlacement(Guid organizerId, NativeMethods.RECT desiredBounds);
    DesktopGridPlacement? FindCurrentPositionedPlacement(Guid organizerId, NativeMethods.RECT currentBounds);

    Task PrepareToExpandAsync(MainWindow source, Func<bool>? isCurrent = null);
    Task CollapseContainedChildrenAsync(Guid containerId, Func<bool>? isCurrent = null);
    Task ReconcileExclusiveExpansionAsync(MainWindow? preferred = null);
    void NotifyCollapsed(MainWindow source);
    bool HasExpandedContainedChild(Guid containerId);
    bool ContainsExpandedContainedChildPoint(Guid containerId, NativeMethods.POINT point);

    void RaiseActiveCompactOrganizerDrags(MainWindow station);
    bool IsOrganizerContainerDropTarget(MainWindow source, NativeMethods.POINT dropPoint);
    bool IsOrganizerDragHoverTarget(MainWindow source, NativeMethods.POINT point);
    void UpdateOrganizerDragHover(MainWindow source, NativeMethods.POINT point);
    void UpdateOrganizerDragHover(MainWindow source, Guid draggedOrganizerId, NativeMethods.POINT point);
    Task WaitForOrganizerDragHoverAsync(MainWindow source);
    void EndOrganizerDragHover(MainWindow source);
    Task<bool> TryContainDraggedOrganizerAsync(MainWindow source, NativeMethods.POINT dropPoint);
    Task<string?> FinishContainedOrganizerDragAsync(Guid organizerId, NativeMethods.POINT dropPoint);
    Task OpenContainedOrganizerAsync(Guid organizerId, NativeMethods.RECT anchor);
    void UpdateContainedOrganizerDragPreview(Guid organizerId, NativeMethods.POINT cursor);
    void ReconcileContainedOrganizer(Guid organizerId);

    void RefreshDockRunningSubscription(bool invalidate = false);
    bool IsDockItemOpen(WidgetItem item);
    void NotifyDockOpenStateChanged();

    Task<string> CreateNoteAsync(Guid organizerId, string? text, NativeMethods.POINT anchor);
    Task<string> CreateTodoAsync(Guid organizerId, NativeMethods.POINT anchor);
    void OpenNote(Guid organizerId, Guid noteId);
    Task OpenExternalNoteAsync(string path);
    Task OpenExternalTodoAsync(string path);
    Task<(string Path, bool RestoreWindow)> PrepareNoteDragAsync(Guid organizerId, Guid noteId);
    Task CompleteNoteDragAsync(Guid organizerId, Guid noteId, string stagingPath, bool restoreWindow, bool moved);
    Task RenameNoteAsync(Guid organizerId, Guid noteId, string name);
    Task<string> RenameExternalNoteAsync(string path, string name);
    Task<string> RenameExternalTodoAsync(string path, string name);
    Task DeleteNoteAsync(Guid organizerId, Guid noteId);
    void RebindPortableWindowAfterMove(string oldPath, string newPath);
    Task<bool> FlushPortableWindowAsync(string path);
    void ClosePortableWindowWithoutSave(string path);

    bool IsPeekActive { get; }
    void ExitPeekMode();
    Task<int> TidyFloatingEdgeStackAsync(Guid? anchorOrganizerId = null);
    Task NotifyDisplayTopologyChangedAsync();

    bool IsOrganizerMultiSelected(Guid id);
    int MultiSelectedOrganizerCount { get; }
    void ToggleOrganizerMultiSelection(Guid id);
    void ClearOrganizerMultiSelection();
    void BeginMultiSelectedGroupDrag(MainWindow lead);
    void UpdateMultiSelectedGroupDrag(MainWindow lead, int deltaX, int deltaY);
    Task CommitMultiSelectedGroupDragAsync(MainWindow lead);
    void CancelMultiSelectedGroupDrag(MainWindow lead);
    NativeMethods.RECT SnapSingleFloatingToDesktopGrid(MainWindow source, NativeMethods.RECT desiredBounds);
    Task<int> AlignFloatingToDesktopGridAsync(Guid? anchorOrganizerId = null);
    Task<(int ImportedCount, int ShortcutCount, int FailedCount)> ImportSelectedDesktopIconsAsync(Guid organizerId);
}

internal interface IConsoleHost
{
    AppStateV2 State { get; }
    TransferQueue TransferQueue { get; }
    IReadOnlyCollection<MainWindow> Windows { get; }
    FolderContextMenuService FolderContextMenu { get; }
    FolderContextMenuService DesktopContextMenu { get; }
    DockSettingsChanges DockSettings { get; }
    AppUpdateService Updates { get; }
    event EventHandler? ThemeChanged;

    Task SaveStateAsync();
    Task SaveStateAsync(Action? snapshotCaptured);
    Task<bool> ExitForUpdateAsync(Func<Task> handoff);
    string ValidateStoragePath(string path);
    void RecreateStorage(Guid id);

    Task<OrganizerDefinition> CreateOrganizerAsync(OrganizerDefinition draft, string? storagePath = null);
    Task<TransferOutcome> DeleteOrganizerAsync(Guid id, OrganizerDeleteDisposition disposition);
    Task RenameOrganizerAsync(Guid organizerId, string name);
    string? ApplyOrganizerRuntime(OrganizerDefinition edited, OrganizerVisualChange changes);

    OrganizerExpansionMode GetRequestedExpansionMode(Guid id);
    Task<string?> SetOrganizerExpansionModeAsync(Guid id, OrganizerExpansionMode? requestedMode = null, Func<CancellationToken, Task<bool>>? prepare = null);

    void UpdateGlobalTheme(
        ThemeTarget target,
        uint colorArgb,
        double transparency,
        double blurStrength,
        bool solidColorMode = false,
        double? solidOpacity = null,
        bool? fullyTransparent = null);
    void ApplyNameScales(double compactScale, double expandedScale);
    Task SetUniformCompactScaleEnabledAsync(OrganizerPlacementMode mode, bool enabled);
    string? ApplyUniformCompactScale(OrganizerPlacementMode mode, double scale);
    List<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> CaptureUniformCompactScaleSnapshots(OrganizerPlacementMode mode);
    void RestoreUniformCompactScale(
        OrganizerPlacementMode mode,
        double scale,
        IReadOnlyList<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> snapshots);

    Task SetStartupAsync(bool enabled);
    Task SetDefaultStorageDirectoryAsync(string? path);
    Task SetLanguageAsync(AppLanguage language);
    Task SetCollapseOnOutsideClickAsync(bool enabled);
    Task SetNoteAlwaysOnTopAsync(bool enabled);
    Task SetEdgeGlowEnabledAsync(bool enabled);
    Task SetExclusiveExpansionAsync(bool enabled);
    Task SetExpandOnHoverAsync(bool enabled);
    Task SetCollapseOnPointerLeaveAsync(bool enabled);
    Task SetPerformanceProfileAsync(PerformanceProfile profile);
    Task SetWindowAlignmentEnabledAsync(bool enabled);
    void SetHoverDelays(int hoverExpandDelayMs, int pointerLeaveCollapseDelayMs, int stationPointerLeaveCollapseDelayMs);
    void SetStationActivation(int distanceDip, int hoverExpandDelayMs);

    Task SetHideCollapseIndicatorAsync(bool hidden);
    Task SetOrganizerMenuVisibilityAsync(string key, bool enabled);
    void SetHoverMagnificationScales(double compactScale, double dockScale);
    Task SetHoverMagnificationAsync(bool dock, bool enabled);

    Task<string?> SetQuickHideHotkeyAsync(QuickHideHotkeyPreset preset);
    Task<string?> SetSearchHotkeyAsync(SearchHotkeyPreset preset);
    Task<string?> SetPeekHotkeyAsync(PeekHotkeyPreset preset);
    Task SetAutoApplyTopologySnapshotsAsync(bool enabled);
    LayoutSnapshotService LayoutSnapshots { get; }
    AutoOrganizeService AutoOrganize { get; }
    Task<LayoutSnapshot> SaveNamedLayoutSnapshotAsync(string name);
    Task<int> RestoreLayoutSnapshotAsync(Guid snapshotId);
    Task<bool> DeleteLayoutSnapshotAsync(Guid snapshotId);
    Task ExportLayoutSnapshotsAsync(string path);
    Task<int> ImportLayoutSnapshotsAsync(string path);
    Task<int> TidyFloatingEdgeStackAsync(Guid? anchorOrganizerId = null);
    IReadOnlyList<AutoOrganizePlannedMove> PreviewAutoOrganize();
    Task<AutoOrganizeRunSummary> RunAutoOrganizeAsync();
    Task<AutoOrganizeUndoSummary> UndoAutoOrganizeAsync();
    Task SaveOrganizerAutoOrganizeRulesAsync(Guid organizerId, IReadOnlyList<AutoOrganizeRule> rules);

    Task SetCompactVisualStyleAsync(CompactVisualStyle style);
    Task SetSnapFloatingToDesktopGridAsync(bool enabled);
    Task SetDesktopMarqueeSelectionEnabledAsync(bool enabled);
    Task<int> AlignFloatingToDesktopGridAsync(Guid? anchorOrganizerId = null);
}

public sealed partial class AppHost : IOrganizerHost, IConsoleHost
{
    internal IconCacheService SharedIconCache { get; } = new();

    ShellLaunchService IOrganizerHost.ShellLauncher => ShellLauncher;
    IconCacheService IOrganizerHost.SharedIconCache => SharedIconCache;
    event EventHandler? IOrganizerHost.ThemeChanged
    {
        add => ThemeChanged += value;
        remove => ThemeChanged -= value;
    }

    IReadOnlyList<WindowAlignmentTarget> IOrganizerHost.GetWindowAlignmentTargets(MainWindow source, DisplayInfo display) =>
        GetWindowAlignmentTargets(source, display);
    IReadOnlyList<WidgetItem> IOrganizerHost.GetContainedOrganizerItems(Guid containerId) =>
        GetContainedOrganizerItems(containerId);
    IReadOnlyList<WidgetItem> IOrganizerHost.GetOrganizerPreviewItems(Guid organizerId) =>
        GetOrganizerPreviewItems(organizerId);
    void IOrganizerHost.NotifyOrganizerPreviewChanged(Guid organizerId) =>
        NotifyOrganizerPreviewChanged(organizerId);
    Task<string?> IOrganizerHost.ToggleOrganizerExpandedContentModeAsync(Guid id) =>
        ToggleOrganizerExpandedContentModeAsync(id);
    Task IOrganizerHost.RenameOrganizerAsync(Guid organizerId, string name) =>
        RenameOrganizerAsync(organizerId, name);
    Task IOrganizerHost.SetOrganizerNameVisibilityAsync(Guid id, bool hideName) =>
        SetOrganizerNameVisibilityAsync(id, hideName);
    OrganizerExpansionMode IOrganizerHost.GetRequestedExpansionMode(Guid id) =>
        GetRequestedExpansionMode(id);
    Task<string?> IOrganizerHost.SetOrganizerExpansionModeAsync(Guid id, OrganizerExpansionMode? requestedMode, Func<CancellationToken, Task<bool>>? prepare) =>
        SetOrganizerExpansionModeAsync(id, requestedMode, prepare);
    void IOrganizerHost.CloseExpansionModeCoordinator(Guid id) =>
        CloseExpansionModeCoordinator(id);
    void IOrganizerHost.NotifyPermanentExpanded(MainWindow window) =>
        NotifyPermanentExpanded(window);
    DesktopGridPlacement? IOrganizerHost.FindNearestPositionedPlacement(Guid organizerId, NativeMethods.RECT desiredBounds) =>
        FindNearestPositionedPlacement(organizerId, desiredBounds);
    DesktopGridPlacement? IOrganizerHost.FindCurrentPositionedPlacement(Guid organizerId, NativeMethods.RECT currentBounds) =>
        FindCurrentPositionedPlacement(organizerId, currentBounds);
    Task IOrganizerHost.CollapseContainedChildrenAsync(Guid containerId, Func<bool>? isCurrent) =>
        CollapseContainedChildrenAsync(containerId, isCurrent);
    Task IOrganizerHost.ReconcileExclusiveExpansionAsync(MainWindow? preferred) =>
        ReconcileExclusiveExpansionAsync(preferred);
    bool IOrganizerHost.HasExpandedContainedChild(Guid containerId) =>
        HasExpandedContainedChild(containerId);
    bool IOrganizerHost.ContainsExpandedContainedChildPoint(Guid containerId, NativeMethods.POINT point) =>
        ContainsExpandedContainedChildPoint(containerId, point);
    void IOrganizerHost.RaiseActiveCompactOrganizerDrags(MainWindow station) =>
        RaiseActiveCompactOrganizerDrags(station);
    bool IOrganizerHost.IsOrganizerContainerDropTarget(MainWindow source, NativeMethods.POINT dropPoint) =>
        IsOrganizerContainerDropTarget(source, dropPoint);
    bool IOrganizerHost.IsOrganizerDragHoverTarget(MainWindow source, NativeMethods.POINT point) =>
        IsOrganizerDragHoverTarget(source, point);
    void IOrganizerHost.UpdateOrganizerDragHover(MainWindow source, NativeMethods.POINT point) =>
        UpdateOrganizerDragHover(source, point);
    void IOrganizerHost.UpdateOrganizerDragHover(MainWindow source, Guid draggedOrganizerId, NativeMethods.POINT point) =>
        UpdateOrganizerDragHover(source, draggedOrganizerId, point);
    Task IOrganizerHost.WaitForOrganizerDragHoverAsync(MainWindow source) =>
        WaitForOrganizerDragHoverAsync(source);
    void IOrganizerHost.EndOrganizerDragHover(MainWindow source) =>
        EndOrganizerDragHover(source);
    Task<bool> IOrganizerHost.TryContainDraggedOrganizerAsync(MainWindow source, NativeMethods.POINT dropPoint) =>
        TryContainDraggedOrganizerAsync(source, dropPoint);
    Task<string?> IOrganizerHost.FinishContainedOrganizerDragAsync(Guid organizerId, NativeMethods.POINT dropPoint) =>
        FinishContainedOrganizerDragAsync(organizerId, dropPoint);
    Task IOrganizerHost.OpenContainedOrganizerAsync(Guid organizerId, NativeMethods.RECT anchor) =>
        OpenContainedOrganizerAsync(organizerId, anchor);
    void IOrganizerHost.UpdateContainedOrganizerDragPreview(Guid organizerId, NativeMethods.POINT cursor) =>
        UpdateContainedOrganizerDragPreview(organizerId, cursor);
    void IOrganizerHost.ReconcileContainedOrganizer(Guid organizerId) =>
        ReconcileContainedOrganizer(organizerId);
    void IOrganizerHost.RefreshDockRunningSubscription(bool invalidate) =>
        RefreshDockRunningSubscription(invalidate);
    bool IOrganizerHost.IsDockItemOpen(WidgetItem item) =>
        IsDockItemOpen(item);
    void IOrganizerHost.NotifyDockOpenStateChanged() =>
        NotifyDockOpenStateChanged();
    Task<string> IOrganizerHost.CreateNoteAsync(Guid organizerId, string? text, NativeMethods.POINT anchor) =>
        CreateNoteAsync(organizerId, text, anchor);
    Task<string> IOrganizerHost.CreateTodoAsync(Guid organizerId, NativeMethods.POINT anchor) =>
        CreateTodoAsync(organizerId, anchor);
    void IOrganizerHost.OpenNote(Guid organizerId, Guid noteId) =>
        OpenNote(organizerId, noteId);
    Task IOrganizerHost.OpenExternalNoteAsync(string path) =>
        OpenExternalNoteAsync(path);
    Task IOrganizerHost.OpenExternalTodoAsync(string path) =>
        OpenExternalTodoAsync(path);
    Task<(string Path, bool RestoreWindow)> IOrganizerHost.PrepareNoteDragAsync(Guid organizerId, Guid noteId) =>
        PrepareNoteDragAsync(organizerId, noteId);
    Task IOrganizerHost.CompleteNoteDragAsync(Guid organizerId, Guid noteId, string stagingPath, bool restoreWindow, bool moved) =>
        CompleteNoteDragAsync(organizerId, noteId, stagingPath, restoreWindow, moved);
    Task IOrganizerHost.RenameNoteAsync(Guid organizerId, Guid noteId, string name) =>
        RenameNoteAsync(organizerId, noteId, name);
    Task<string> IOrganizerHost.RenameExternalNoteAsync(string path, string name) =>
        RenameExternalNoteAsync(path, name);
    Task<string> IOrganizerHost.RenameExternalTodoAsync(string path, string name) =>
        RenameExternalTodoAsync(path, name);
    Task IOrganizerHost.DeleteNoteAsync(Guid organizerId, Guid noteId) =>
        DeleteNoteAsync(organizerId, noteId);
    void IOrganizerHost.RebindPortableWindowAfterMove(string oldPath, string newPath) =>
        RebindPortableWindowAfterMove(oldPath, newPath);
    Task<bool> IOrganizerHost.FlushPortableWindowAsync(string path) =>
        FlushPortableWindowAsync(path);
    void IOrganizerHost.ClosePortableWindowWithoutSave(string path) =>
        ClosePortableWindowWithoutSave(path);
    bool IOrganizerHost.IsPeekActive => IsPeekActive;
    void IOrganizerHost.ExitPeekMode() => ExitPeekMode();
    Task<int> IOrganizerHost.TidyFloatingEdgeStackAsync(Guid? anchorOrganizerId) =>
        TidyFloatingEdgeStackAsync(anchorOrganizerId);
    Task IOrganizerHost.NotifyDisplayTopologyChangedAsync() =>
        NotifyDisplayTopologyChangedAsync();
    bool IOrganizerHost.IsOrganizerMultiSelected(Guid id) => IsOrganizerMultiSelected(id);
    int IOrganizerHost.MultiSelectedOrganizerCount => MultiSelectedOrganizerCount;
    void IOrganizerHost.ToggleOrganizerMultiSelection(Guid id) => ToggleOrganizerMultiSelection(id);
    void IOrganizerHost.ClearOrganizerMultiSelection() => ClearOrganizerMultiSelection();
    void IOrganizerHost.BeginMultiSelectedGroupDrag(MainWindow lead) => BeginMultiSelectedGroupDrag(lead);
    void IOrganizerHost.UpdateMultiSelectedGroupDrag(MainWindow lead, int deltaX, int deltaY) =>
        UpdateMultiSelectedGroupDrag(lead, deltaX, deltaY);
    Task IOrganizerHost.CommitMultiSelectedGroupDragAsync(MainWindow lead) => CommitMultiSelectedGroupDragAsync(lead);
    void IOrganizerHost.CancelMultiSelectedGroupDrag(MainWindow lead) => CancelMultiSelectedGroupDrag(lead);
    NativeMethods.RECT IOrganizerHost.SnapSingleFloatingToDesktopGrid(MainWindow source, NativeMethods.RECT desiredBounds) =>
        SnapSingleFloatingToDesktopGrid(source, desiredBounds);
    Task<int> IOrganizerHost.AlignFloatingToDesktopGridAsync(Guid? anchorOrganizerId) =>
        AlignFloatingToDesktopGridAsync(anchorOrganizerId);
    Task<(int ImportedCount, int ShortcutCount, int FailedCount)> IOrganizerHost.ImportSelectedDesktopIconsAsync(Guid organizerId) =>
        ImportSelectedDesktopIconsAsync(organizerId);

    FolderContextMenuService IConsoleHost.FolderContextMenu => FolderContextMenu;
    FolderContextMenuService IConsoleHost.DesktopContextMenu => DesktopContextMenu;
    LayoutSnapshotService IConsoleHost.LayoutSnapshots => LayoutSnapshots;
    AutoOrganizeService IConsoleHost.AutoOrganize => AutoOrganize;
    Task<LayoutSnapshot> IConsoleHost.SaveNamedLayoutSnapshotAsync(string name) => SaveNamedLayoutSnapshotAsync(name);
    Task<int> IConsoleHost.RestoreLayoutSnapshotAsync(Guid snapshotId) => RestoreLayoutSnapshotAsync(snapshotId);
    Task<bool> IConsoleHost.DeleteLayoutSnapshotAsync(Guid snapshotId) => DeleteLayoutSnapshotAsync(snapshotId);
    Task IConsoleHost.ExportLayoutSnapshotsAsync(string path) => ExportLayoutSnapshotsAsync(path);
    Task<int> IConsoleHost.ImportLayoutSnapshotsAsync(string path) => ImportLayoutSnapshotsAsync(path);
    IReadOnlyList<AutoOrganizePlannedMove> IConsoleHost.PreviewAutoOrganize() => PreviewAutoOrganize();
    Task<AutoOrganizeRunSummary> IConsoleHost.RunAutoOrganizeAsync() => RunAutoOrganizeAsync();
    Task<AutoOrganizeUndoSummary> IConsoleHost.UndoAutoOrganizeAsync() => UndoAutoOrganizeAsync();
    Task IConsoleHost.SaveOrganizerAutoOrganizeRulesAsync(Guid organizerId, IReadOnlyList<AutoOrganizeRule> rules) =>
        SaveOrganizerAutoOrganizeRulesAsync(organizerId, rules);
    Task IConsoleHost.SetCompactVisualStyleAsync(CompactVisualStyle style) => SetCompactVisualStyleAsync(style);
    Task IConsoleHost.SetSnapFloatingToDesktopGridAsync(bool enabled) => SetSnapFloatingToDesktopGridAsync(enabled);
    Task IConsoleHost.SetDesktopMarqueeSelectionEnabledAsync(bool enabled) => SetDesktopMarqueeSelectionEnabledAsync(enabled);
    Task<int> IConsoleHost.AlignFloatingToDesktopGridAsync(Guid? anchorOrganizerId) =>
        AlignFloatingToDesktopGridAsync(anchorOrganizerId);
    DockSettingsChanges IConsoleHost.DockSettings => DockSettings;
    AppUpdateService IConsoleHost.Updates => Updates;
    event EventHandler? IConsoleHost.ThemeChanged
    {
        add => ThemeChanged += value;
        remove => ThemeChanged -= value;
    }

    Task IConsoleHost.SaveStateAsync(Action? snapshotCaptured) =>
        SaveStateAsync(snapshotCaptured);
    Task<bool> IConsoleHost.ExitForUpdateAsync(Func<Task> handoff) =>
        ExitForUpdateAsync(handoff);
    Task IConsoleHost.RenameOrganizerAsync(Guid organizerId, string name) =>
        RenameOrganizerAsync(organizerId, name);
    string? IConsoleHost.ApplyOrganizerRuntime(OrganizerDefinition edited, OrganizerVisualChange changes) =>
        ApplyOrganizerRuntime(edited, changes);
    OrganizerExpansionMode IConsoleHost.GetRequestedExpansionMode(Guid id) =>
        GetRequestedExpansionMode(id);
    Task<string?> IConsoleHost.SetOrganizerExpansionModeAsync(Guid id, OrganizerExpansionMode? requestedMode, Func<CancellationToken, Task<bool>>? prepare) =>
        SetOrganizerExpansionModeAsync(id, requestedMode, prepare);
    void IConsoleHost.UpdateGlobalTheme(
        ThemeTarget target,
        uint colorArgb,
        double transparency,
        double blurStrength,
        bool solidColorMode,
        double? solidOpacity,
        bool? fullyTransparent) =>
        UpdateGlobalTheme(target, colorArgb, transparency, blurStrength, solidColorMode, solidOpacity, fullyTransparent);
    void IConsoleHost.ApplyNameScales(double compactScale, double expandedScale) =>
        ApplyNameScales(compactScale, expandedScale);
    string? IConsoleHost.ApplyUniformCompactScale(OrganizerPlacementMode mode, double scale) =>
        ApplyUniformCompactScale(mode, scale);
    List<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> IConsoleHost.CaptureUniformCompactScaleSnapshots(OrganizerPlacementMode mode) =>
        CaptureUniformCompactScaleSnapshots(mode);
    void IConsoleHost.RestoreUniformCompactScale(
        OrganizerPlacementMode mode,
        double scale,
        IReadOnlyList<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> snapshots) =>
        RestoreUniformCompactScale(mode, scale, snapshots);
    Task IConsoleHost.SetHideCollapseIndicatorAsync(bool hidden) =>
        SetHideCollapseIndicatorAsync(hidden);
    Task IConsoleHost.SetOrganizerMenuVisibilityAsync(string key, bool enabled) =>
        SetOrganizerMenuVisibilityAsync(key, enabled);
    void IConsoleHost.SetHoverMagnificationScales(double compactScale, double dockScale) =>
        SetHoverMagnificationScales(compactScale, dockScale);
    Task IConsoleHost.SetHoverMagnificationAsync(bool dock, bool enabled) =>
        SetHoverMagnificationAsync(dock, enabled);
}
