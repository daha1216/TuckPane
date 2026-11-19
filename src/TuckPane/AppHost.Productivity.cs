using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class AppHost
{
    private GlobalHotkeyService? _hotkeys;
    private OrganizerSearchWindow? _searchWindow;
    private readonly LayoutSnapshotService _layoutSnapshots = new();
    private readonly AutoOrganizeService _autoOrganize = new();
    private bool _windowsExplicitlyHidden;
    private bool _peekActive;
    private bool _peekRevealedHiddenWindows;
    private string? _lastTopologyKey;

    internal bool IsPeekActive => _peekActive;
    internal LayoutSnapshotService LayoutSnapshots => _layoutSnapshots;
    internal AutoOrganizeService AutoOrganize => _autoOrganize;

    private void InitializeProductivityServices()
    {
        _hotkeys = new GlobalHotkeyService(Console.Hwnd, HandleGlobalHotkey);
        ApplyConfiguredHotkeys();
        (string key, _) = LayoutSnapshotMath.BuildTopologyFingerprint(DisplayPlacementService.GetDisplays());
        _lastTopologyKey = key;
        _ = EnsureInitialTopologySnapshotAsync();
    }

    private void DisposeProductivityServices()
    {
        ExitPeekMode();
        _hotkeys?.Dispose();
        _hotkeys = null;
        _searchWindow?.ClosePermanently();
        _searchWindow = null;
    }

    private void ApplyConfiguredHotkeys()
    {
        if (_hotkeys is null) return;
        GlobalSettings settings = State.GlobalSettings;
        HotkeyBinding? quickHide = GlobalHotkeyDefinitions.Resolve(settings.QuickHideHotkey);
        HotkeyBinding? search = GlobalHotkeyDefinitions.Resolve(settings.SearchHotkey);
        HotkeyBinding? peek = GlobalHotkeyDefinitions.Resolve(settings.PeekHotkey);

        if (!_hotkeys.TrySetHotkey(GlobalHotkeyDefinitions.QuickHideHotkeyId, quickHide))
            AppLogger.Error($"无法注册快速隐藏热键：{quickHide?.Label}");
        if (!GlobalHotkeyDefinitions.ConflictsWithAny(search, quickHide) &&
            !_hotkeys.TrySetHotkey(GlobalHotkeyDefinitions.SearchHotkeyId, search))
        {
            AppLogger.Error($"无法注册全局查找热键：{search?.Label}");
        }
        if (!GlobalHotkeyDefinitions.ConflictsWithAny(peek, quickHide, search) &&
            !_hotkeys.TrySetHotkey(GlobalHotkeyDefinitions.PeekHotkeyId, peek))
        {
            AppLogger.Error($"无法注册透视置顶热键：{peek?.Label}");
        }
    }

    private void HandleGlobalHotkey(int hotkeyId)
    {
        _ = _dispatcher.TryEnqueue(() =>
        {
            if (IsPreparingUpdate) return;
            switch (hotkeyId)
            {
                case GlobalHotkeyDefinitions.QuickHideHotkeyId:
                    ToggleQuickHide();
                    break;
                case GlobalHotkeyDefinitions.SearchHotkeyId:
                    OpenSearchWindow();
                    break;
                case GlobalHotkeyDefinitions.PeekHotkeyId:
                    TogglePeekMode();
                    break;
            }
        });
    }

    public async Task<string?> SetQuickHideHotkeyAsync(QuickHideHotkeyPreset preset)
    {
        preset = GlobalHotkeyDefinitions.Normalize(preset);
        HotkeyBinding? candidate = GlobalHotkeyDefinitions.Resolve(preset);
        HotkeyBinding? search = GlobalHotkeyDefinitions.Resolve(State.GlobalSettings.SearchHotkey);
        HotkeyBinding? peek = GlobalHotkeyDefinitions.Resolve(State.GlobalSettings.PeekHotkey);
        if (GlobalHotkeyDefinitions.ConflictsWithAny(candidate, search, peek))
            return AppStrings.Get("HotkeyDuplicateError");

        if (_hotkeys is not null && !_hotkeys.TrySetHotkey(GlobalHotkeyDefinitions.QuickHideHotkeyId, candidate))
            return AppStrings.Get("HotkeyConflictError");

        try
        {
            await MutateGlobalSettingAsync(s => s.QuickHideHotkey, (s, v) => s.QuickHideHotkey = v, preset);
            return null;
        }
        catch
        {
            _hotkeys?.TrySetHotkey(GlobalHotkeyDefinitions.QuickHideHotkeyId,
                GlobalHotkeyDefinitions.Resolve(State.GlobalSettings.QuickHideHotkey));
            throw;
        }
    }

    public async Task<string?> SetSearchHotkeyAsync(SearchHotkeyPreset preset)
    {
        preset = GlobalHotkeyDefinitions.Normalize(preset);
        HotkeyBinding? candidate = GlobalHotkeyDefinitions.Resolve(preset);
        HotkeyBinding? quickHide = GlobalHotkeyDefinitions.Resolve(State.GlobalSettings.QuickHideHotkey);
        HotkeyBinding? peek = GlobalHotkeyDefinitions.Resolve(State.GlobalSettings.PeekHotkey);
        if (GlobalHotkeyDefinitions.ConflictsWithAny(candidate, quickHide, peek))
            return AppStrings.Get("HotkeyDuplicateError");

        if (_hotkeys is not null && !_hotkeys.TrySetHotkey(GlobalHotkeyDefinitions.SearchHotkeyId, candidate))
            return AppStrings.Get("HotkeyConflictError");

        try
        {
            await MutateGlobalSettingAsync(s => s.SearchHotkey, (s, v) => s.SearchHotkey = v, preset);
            return null;
        }
        catch
        {
            _hotkeys?.TrySetHotkey(GlobalHotkeyDefinitions.SearchHotkeyId,
                GlobalHotkeyDefinitions.Resolve(State.GlobalSettings.SearchHotkey));
            throw;
        }
    }

    public async Task<string?> SetPeekHotkeyAsync(PeekHotkeyPreset preset)
    {
        preset = GlobalHotkeyDefinitions.Normalize(preset);
        HotkeyBinding? candidate = GlobalHotkeyDefinitions.Resolve(preset);
        HotkeyBinding? quickHide = GlobalHotkeyDefinitions.Resolve(State.GlobalSettings.QuickHideHotkey);
        HotkeyBinding? search = GlobalHotkeyDefinitions.Resolve(State.GlobalSettings.SearchHotkey);
        if (GlobalHotkeyDefinitions.ConflictsWithAny(candidate, quickHide, search))
            return AppStrings.Get("HotkeyDuplicateError");

        if (_hotkeys is not null && !_hotkeys.TrySetHotkey(GlobalHotkeyDefinitions.PeekHotkeyId, candidate))
            return AppStrings.Get("HotkeyConflictError");

        try
        {
            await MutateGlobalSettingAsync(s => s.PeekHotkey, (s, v) => s.PeekHotkey = v, preset);
            return null;
        }
        catch
        {
            _hotkeys?.TrySetHotkey(GlobalHotkeyDefinitions.PeekHotkeyId,
                GlobalHotkeyDefinitions.Resolve(State.GlobalSettings.PeekHotkey));
            throw;
        }
    }

    public void ShowAllWindows()
    {
        _windowsExplicitlyHidden = false;
        foreach (MainWindow window in _windows.Values) window.SetVisible(true);
        foreach (Guid noteId in _trayHiddenNotes.ToArray())
        {
            if (_noteWindows.TryGetValue(noteId, out NoteWindow? noteWindow)) noteWindow.RestoreFromTray();
        }
        _trayHiddenNotes.Clear();
        _portableDocuments.RestoreAllFromTray();
    }

    public void HideAllWindows()
    {
        ExitPeekMode();
        _windowsExplicitlyHidden = true;
        foreach (MainWindow window in _windows.Values) window.SetVisible(false);
        _trayHiddenNotes.Clear();
        foreach ((Guid noteId, NoteWindow noteWindow) in _noteWindows)
        {
            if (!noteWindow.IsVisible) continue;
            _trayHiddenNotes.Add(noteId);
            noteWindow.HideForTray();
        }
        _portableDocuments.HideAllForTray();
    }

    public void ToggleQuickHide()
    {
        if (_windowsExplicitlyHidden) ShowAllWindows();
        else HideAllWindows();
    }

    public void TogglePeekMode()
    {
        if (_peekActive)
        {
            ExitPeekMode();
            return;
        }

        _peekActive = true;
        _peekRevealedHiddenWindows = _windowsExplicitlyHidden;
        if (_windowsExplicitlyHidden)
        {
            foreach (MainWindow window in _windows.Values) window.SetVisible(true);
        }
        foreach (MainWindow window in _windows.Values)
            window.ApplyPeekState(active: true);
    }

    public void ExitPeekMode()
    {
        if (!_peekActive) return;
        _peekActive = false;
        foreach (MainWindow window in _windows.Values)
            window.ApplyPeekState(active: false);
        if (_peekRevealedHiddenWindows)
        {
            _peekRevealedHiddenWindows = false;
            foreach (MainWindow window in _windows.Values) window.SetVisible(false);
        }
    }

    public void OpenSearchWindow()
    {
        if (IsPreparingUpdate) return;
        _searchWindow ??= new OrganizerSearchWindow(this);
        _searchWindow.OpenAndFocus();
    }

    internal async Task JumpToOrganizerAsync(Guid organizerId)
    {
        if (State.Organizers.FirstOrDefault(o => o.Id == organizerId) is not OrganizerDefinition organizer) return;
        if (_windowsExplicitlyHidden) ShowAllWindows();

        if (organizer.ContainerOrganizerId is Guid parentId &&
            _windows.TryGetValue(parentId, out MainWindow? parentWindow) &&
            !parentWindow.IsExpanded)
        {
            await parentWindow.ExpandOrganizerAsync();
        }

        if (_windows.TryGetValue(organizerId, out MainWindow? window))
        {
            if (!window.IsExpanded) await window.ExpandOrganizerAsync();
            window.BringToForegroundForNavigation();
        }
    }

    public async Task<int> TidyFloatingEdgeStackAsync(Guid? anchorOrganizerId = null)
    {
        MainWindow? anchorWindow = anchorOrganizerId is Guid id && _windows.TryGetValue(id, out MainWindow? found)
            ? found
            : _windows.Values.FirstOrDefault(w =>
                w.DefinitionPlacementMode == OrganizerPlacementMode.Floating && !w.IsContainedWindow);
        if (anchorWindow is null) return 0;

        DisplayInfo display = DisplayPlacementService.ForBounds(anchorWindow.CompactBounds);
        WindowAlignmentInsets anchorInsets = anchorWindow.GetDeterministicCompactInsetsForTidy(anchorWindow.CompactBounds, display.Scale);
        NativeMethods.RECT anchorFrame = anchorInsets.IsValidFor(anchorWindow.CompactBounds)
            ? anchorInsets.ToFrame(anchorWindow.CompactBounds)
            : anchorWindow.CompactBounds;
        OrganizerDockEdge targetEdge = EdgeStackAlignMath.DetectNearestEdge(anchorFrame, display.Work);

        var candidates = new List<EdgeStackCandidate>();
        foreach (MainWindow candidate in _windows.Values)
        {
            if (candidate.DefinitionPlacementMode != OrganizerPlacementMode.Floating || candidate.IsContainedWindow)
                continue;
            DisplayInfo candidateDisplay = DisplayPlacementService.ForBounds(candidate.CompactBounds);
            if (!string.Equals(candidateDisplay.Device, display.Device, StringComparison.OrdinalIgnoreCase))
                continue;
            WindowAlignmentInsets insets = candidate.GetDeterministicCompactInsetsForTidy(candidate.CompactBounds, display.Scale);
            candidates.Add(new EdgeStackCandidate(candidate.OrganizerId, candidate.CompactBounds, insets));
        }

        IReadOnlyList<EdgeStackPlacement> plan = EdgeStackAlignMath.ComputeTidyStack(
            candidates,
            targetEdge,
            display.Work,
            display.Scale,
            requiredAnchorId: anchorWindow.OrganizerId);
        if (plan.Count == 0) return 0;

        foreach (EdgeStackPlacement placement in plan)
        {
            if (_windows.TryGetValue(placement.OrganizerId, out MainWindow? window))
                window.ApplyTidyEdgePlacement(placement.NewWindowBounds);
        }

        await SaveStateAsync();
        _ = CaptureCurrentTopologySnapshotAsync();
        return plan.Count;
    }

    public Task SetAutoApplyTopologySnapshotsAsync(bool enabled) =>
        MutateGlobalSettingAsync(s => s.AutoApplyTopologySnapshots, (s, v) => s.AutoApplyTopologySnapshots = v, enabled);

    internal async Task<LayoutSnapshot> SaveNamedLayoutSnapshotAsync(string name)
    {
        IReadOnlyList<DisplayInfo> displays = DisplayPlacementService.GetDisplays();
        return await _layoutSnapshots.SaveSnapshotAsync(name, isAutoTopology: false, displays, State.Organizers);
    }

    internal async Task<int> RestoreLayoutSnapshotAsync(Guid snapshotId)
    {
        LayoutSnapshotStoreData store = await _layoutSnapshots.LoadAsync();
        LayoutSnapshot? snapshot = store.Snapshots.FirstOrDefault(s => s.Id == snapshotId);
        if (snapshot is null) return 0;
        int restored = LayoutSnapshotMath.ApplySnapshot(snapshot, State.Organizers);
        if (restored == 0) return 0;
        await SaveStateAsync();
        foreach (MainWindow window in _windows.Values)
            window.ReconcileDisplayChange();
        return restored;
    }

    internal Task<bool> DeleteLayoutSnapshotAsync(Guid snapshotId) =>
        _layoutSnapshots.DeleteSnapshotAsync(snapshotId);

    internal Task ExportLayoutSnapshotsAsync(string path) =>
        _layoutSnapshots.ExportToFileAsync(path);

    internal Task<int> ImportLayoutSnapshotsAsync(string path) =>
        _layoutSnapshots.ImportFromFileAsync(path);

    internal async Task CaptureCurrentTopologySnapshotAsync()
    {
        try
        {
            IReadOnlyList<DisplayInfo> displays = DisplayPlacementService.GetDisplays();
            if (displays.Count == 0) return;
            await _layoutSnapshots.SaveSnapshotAsync(string.Empty, isAutoTopology: true, displays, State.Organizers);
        }
        catch (Exception ex)
        {
            AppLogger.Error("自动保存显示器拓扑布局快照失败。", ex);
        }
    }

    internal async Task NotifyDisplayTopologyChangedAsync()
    {
        if (!State.GlobalSettings.AutoApplyTopologySnapshots) return;
        IReadOnlyList<DisplayInfo> displays = DisplayPlacementService.GetDisplays();
        (string key, _) = LayoutSnapshotMath.BuildTopologyFingerprint(displays);
        if (string.Equals(key, _lastTopologyKey, StringComparison.OrdinalIgnoreCase)) return;
        _lastTopologyKey = key;

        LayoutSnapshot? matching = await _layoutSnapshots.FindAutoSnapshotAsync(displays);
        if (matching is null) return;
        if (LayoutSnapshotMath.ApplySnapshot(matching, State.Organizers) > 0)
        {
            foreach (MainWindow window in _windows.Values)
                window.ReconcileDisplayChange();
        }
    }

    private async Task EnsureInitialTopologySnapshotAsync()
    {
        try
        {
            IReadOnlyList<DisplayInfo> displays = DisplayPlacementService.GetDisplays();
            if (displays.Count == 0) return;
            LayoutSnapshot? existing = await _layoutSnapshots.FindAutoSnapshotAsync(displays);
            if (existing is null)
                await _layoutSnapshots.SaveSnapshotAsync(string.Empty, isAutoTopology: true, displays, State.Organizers);
        }
        catch (Exception ex)
        {
            AppLogger.Error("初始化显示器拓扑布局快照失败。", ex);
        }
    }

    internal IReadOnlyList<AutoOrganizePlannedMove> PreviewAutoOrganize() =>
        _autoOrganize.PreviewPlan(State);

    internal async Task<AutoOrganizeRunSummary> RunAutoOrganizeAsync()
    {
        IReadOnlyList<AutoOrganizePlannedMove> plan = _autoOrganize.PreviewPlan(State);
        AutoOrganizeRunSummary summary = await _autoOrganize.ExecutePlanAsync(plan, TransferQueue);
        foreach (Guid targetId in plan.Select(item => item.TargetOrganizerId).Distinct())
        {
            if (_windows.TryGetValue(targetId, out MainWindow? window))
                _ = window.RefreshCatalogAsync(notifyUnsupported: false, refreshIcons: true);
        }
        return summary;
    }

    internal async Task<AutoOrganizeUndoSummary> UndoAutoOrganizeAsync()
    {
        AutoOrganizeUndoSummary summary = await _autoOrganize.UndoLastBatchAsync();
        foreach (MainWindow window in _windows.Values)
            _ = window.RefreshCatalogAsync(notifyUnsupported: false, refreshIcons: false);
        return summary;
    }

    internal async Task SaveOrganizerAutoOrganizeRulesAsync(Guid organizerId, IReadOnlyList<AutoOrganizeRule> rules)
    {
        if (State.Organizers.FirstOrDefault(item => item.Id == organizerId) is not OrganizerDefinition organizer)
            return;
        List<AutoOrganizeRule> previous = organizer.AutoOrganizeRules.Select(r => r.Clone()).ToList();
        organizer.AutoOrganizeRules = rules.Select(r => r.Clone()).ToList();
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            organizer.AutoOrganizeRules = previous;
            throw;
        }
    }
}
