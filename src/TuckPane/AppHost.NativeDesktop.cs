using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class AppHost
{
    private DesktopMarqueeHookService? _desktopMarqueeHook;
    private readonly HashSet<Guid> _selectedOrganizerIds = [];
    private HashSet<Guid>? _marqueeBaselineSelection;
    private Guid? _groupDragLeadId;
    private IReadOnlyList<DisplayInfo>? _groupDragDisplays;
    private readonly Dictionary<Guid, (NativeMethods.RECT InitialBounds, WindowAlignmentInsets Insets, DisplayInfo Display)> _groupDragPeers = [];

    internal int MultiSelectedOrganizerCount => _selectedOrganizerIds.Count;

    internal bool IsOrganizerMultiSelected(Guid id) => _selectedOrganizerIds.Contains(id);

    private void InitializeNativeDesktopServices()
    {
        _desktopMarqueeHook = new DesktopMarqueeHookService(
            _dispatcher,
            HandleDesktopMarqueeUpdated,
            HandleDesktopMarqueeCompleted,
            HandleDesktopEmptyClick);
        SyncDesktopMarqueeHookState();
    }

    private void DisposeNativeDesktopServices()
    {
        _desktopMarqueeHook?.Dispose();
        _desktopMarqueeHook = null;
        _selectedOrganizerIds.Clear();
        _groupDragPeers.Clear();
        _groupDragDisplays = null;
        _groupDragLeadId = null;
    }

    private void SyncDesktopMarqueeHookState()
    {
        if (_desktopMarqueeHook is null) return;
        if (State.GlobalSettings.DesktopMarqueeSelectionEnabled && !IsPreparingUpdate)
            _desktopMarqueeHook.Start();
        else
            _desktopMarqueeHook.Stop();
    }

    internal async Task SetCompactVisualStyleAsync(CompactVisualStyle style)
    {
        CompactVisualStyle normalized = CompactVisualStyles.Normalize(style);
        await MutateGlobalSettingAsync(
            s => s.CompactVisualStyle,
            (s, v) => s.CompactVisualStyle = v,
            normalized);
        foreach (MainWindow window in _windows.Values)
            window.ApplyCompactVisualStyle();
    }

    internal async Task SetSnapFloatingToDesktopGridAsync(bool enabled)
    {
        await MutateGlobalSettingAsync(
            s => s.SnapFloatingToDesktopGrid,
            (s, v) => s.SnapFloatingToDesktopGrid = v,
            enabled);
    }

    internal async Task SetDesktopMarqueeSelectionEnabledAsync(bool enabled)
    {
        await MutateGlobalSettingAsync(
            s => s.DesktopMarqueeSelectionEnabled,
            (s, v) => s.DesktopMarqueeSelectionEnabled = v,
            enabled);
        if (!enabled) ClearOrganizerMultiSelection();
        SyncDesktopMarqueeHookState();
    }

    internal async Task SetFreeGridPlacementEnabledAsync(bool enabled)
    {
        await MutateGlobalSettingAsync(
            s => s.FreeGridPlacementEnabled,
            (s, v) => s.FreeGridPlacementEnabled = v,
            enabled);
        foreach (MainWindow window in _windows.Values)
            window.RefreshFreeGridLayoutMode();
    }

    internal async Task SetFreeGridPreferDirectSwapAsync(bool enabled)
    {
        await MutateGlobalSettingAsync(
            s => s.FreeGridPreferDirectSwap,
            (s, v) => s.FreeGridPreferDirectSwap = v,
            enabled);
    }

    internal void ToggleOrganizerMultiSelection(Guid id)
    {
        if (!_windows.TryGetValue(id, out MainWindow? window) || !window.IsEligibleForDesktopMultiSelect)
            return;
        if (!_selectedOrganizerIds.Remove(id))
            _selectedOrganizerIds.Add(id);
        RefreshAllMultiSelectionVisuals();
    }

    internal void ClearOrganizerMultiSelection()
    {
        _marqueeBaselineSelection = null;
        if (_selectedOrganizerIds.Count == 0) return;
        _selectedOrganizerIds.Clear();
        RefreshAllMultiSelectionVisuals();
    }

    internal void RemoveDeletedOrganizerFromSelection(Guid id)
    {
        bool removed = _selectedOrganizerIds.Remove(id);
        _marqueeBaselineSelection?.Remove(id);
        _groupDragPeers.Remove(id);
        if (_groupDragLeadId == id)
        {
            _groupDragLeadId = null;
            _groupDragDisplays = null;
        }
        if (removed)
            RefreshAllMultiSelectionVisuals();
    }

    private void HandleDesktopEmptyClick()
    {
        _marqueeBaselineSelection = null;
        ClearOrganizerMultiSelection();
    }

    private void HandleDesktopMarqueeUpdated(NativeMethods.RECT marqueeScreenRect, bool additiveCtrl)
    {
        if (IsPreparingUpdate || !State.GlobalSettings.DesktopMarqueeSelectionEnabled) return;
        _marqueeBaselineSelection ??= additiveCtrl ? [.. _selectedOrganizerIds] : [];

        IReadOnlyList<Guid> hits = FindOrganizersIntersectingMarquee(marqueeScreenRect);
        ApplyOrganizerMarqueeSelection(hits, additiveCtrl);
    }

    private void HandleDesktopMarqueeCompleted(NativeMethods.RECT marqueeScreenRect, bool additiveCtrl, bool wasDrag)
    {
        if (wasDrag && State.GlobalSettings.DesktopMarqueeSelectionEnabled)
        {
            _marqueeBaselineSelection ??= additiveCtrl ? [.. _selectedOrganizerIds] : [];
            IReadOnlyList<Guid> hits = FindOrganizersIntersectingMarquee(marqueeScreenRect);
            ApplyOrganizerMarqueeSelection(hits, additiveCtrl);
        }
        _marqueeBaselineSelection = null;
    }

    private IReadOnlyList<Guid> FindOrganizersIntersectingMarquee(NativeMethods.RECT marqueeScreenRect)
    {
        var targets = new List<MarqueeTargetRect<Guid>>();
        foreach (MainWindow window in _windows.Values)
        {
            if (!window.IsEligibleForDesktopMultiSelect) continue;
            targets.Add(new MarqueeTargetRect<Guid>(window.OrganizerId, window.GetVisibleCompactFrameRect()));
        }
        return MarqueeSelectionMath.FindIntersectingIds(marqueeScreenRect, targets);
    }

    private void ApplyOrganizerMarqueeSelection(IReadOnlyList<Guid> hits, bool additiveCtrl)
    {
        var next = new HashSet<Guid>();
        if (additiveCtrl && _marqueeBaselineSelection is not null)
        {
            foreach (Guid id in _marqueeBaselineSelection) next.Add(id);
            foreach (Guid hit in hits)
            {
                if (!next.Add(hit)) next.Remove(hit);
            }
        }
        else
        {
            foreach (Guid hit in hits) next.Add(hit);
        }

        if (next.SetEquals(_selectedOrganizerIds)) return;
        _selectedOrganizerIds.Clear();
        foreach (Guid id in next) _selectedOrganizerIds.Add(id);
        RefreshAllMultiSelectionVisuals();
    }

    private void RefreshAllMultiSelectionVisuals()
    {
        foreach (MainWindow window in _windows.Values)
            window.SetOrganizerMultiSelected(_selectedOrganizerIds.Contains(window.OrganizerId));
    }

    internal void BeginMultiSelectedGroupDrag(MainWindow lead)
    {
        _groupDragPeers.Clear();
        _groupDragDisplays = null;
        _groupDragLeadId = null;

        if (!_selectedOrganizerIds.Contains(lead.OrganizerId))
        {
            ClearOrganizerMultiSelection();
            return;
        }
        if (_selectedOrganizerIds.Count <= 1 || lead.IsExpanded) return;

        _groupDragLeadId = lead.OrganizerId;
        _groupDragDisplays = DisplayPlacementService.GetDisplays();
        foreach (Guid id in _selectedOrganizerIds)
        {
            if (id == lead.OrganizerId) continue;
            if (!_windows.TryGetValue(id, out MainWindow? peer) || !peer.IsEligibleForDesktopMultiSelect) continue;
            if (peer.Definition.PlacementMode != OrganizerPlacementMode.Floating) continue;

            NativeMethods.RECT bounds = peer.GetCurrentCompactBounds();
            WindowAlignmentInsets insets = peer.GetCurrentCompactAlignmentInsets();
            DisplayInfo display = ResolveCachedDisplay(bounds, _groupDragDisplays)
                ?? DisplayPlacementService.ForBounds(bounds);
            _groupDragPeers[id] = (bounds, insets, display);
            peer.PrepareForGroupPeerDrag(topmost: true);
        }
    }

    internal void UpdateMultiSelectedGroupDrag(MainWindow lead, int deltaX, int deltaY)
    {
        if (_groupDragLeadId != lead.OrganizerId || _groupDragPeers.Count == 0) return;

        IReadOnlyList<DisplayInfo>? cachedDisplays = _groupDragDisplays;
        foreach ((Guid peerId, var snapshot) in _groupDragPeers)
        {
            if (!_windows.TryGetValue(peerId, out MainWindow? peer)) continue;
            var desired = new NativeMethods.RECT
            {
                Left = snapshot.InitialBounds.Left + deltaX,
                Top = snapshot.InitialBounds.Top + deltaY,
                Right = snapshot.InitialBounds.Right + deltaX,
                Bottom = snapshot.InitialBounds.Bottom + deltaY
            };
            DisplayInfo targetDisplay = ContainsCenter(snapshot.Display.Monitor, desired)
                ? snapshot.Display
                : ResolveCachedDisplay(desired, cachedDisplays) ?? snapshot.Display;
            NativeMethods.RECT nextBounds = MarqueeSelectionMath.ComputeGroupPeerBounds(
                snapshot.InitialBounds,
                deltaX,
                deltaY,
                targetDisplay.Work,
                snapshot.Insets);
            peer.MoveAsGroupDragPeer(nextBounds);
        }
    }

    private static bool ContainsCenter(NativeMethods.RECT monitor, NativeMethods.RECT rect)
    {
        int centerX = rect.Left + rect.Width / 2;
        int centerY = rect.Top + rect.Height / 2;
        return centerX >= monitor.Left && centerX < monitor.Right &&
               centerY >= monitor.Top && centerY < monitor.Bottom;
    }

    private static DisplayInfo? ResolveCachedDisplay(NativeMethods.RECT rect, IReadOnlyList<DisplayInfo>? displays)
    {
        if (displays is null || displays.Count == 0) return null;
        if (displays.Count == 1) return displays[0];
        for (int i = 0; i < displays.Count; i++)
        {
            if (ContainsCenter(displays[i].Monitor, rect))
                return displays[i];
        }
        int centerX = rect.Left + rect.Width / 2;
        int centerY = rect.Top + rect.Height / 2;
        DisplayInfo nearest = displays[0];
        long bestDistance = long.MaxValue;
        for (int i = 0; i < displays.Count; i++)
        {
            NativeMethods.RECT mon = displays[i].Monitor;
            long dx = centerX < mon.Left ? mon.Left - centerX : centerX >= mon.Right ? centerX - (mon.Right - 1) : 0;
            long dy = centerY < mon.Top ? mon.Top - centerY : centerY >= mon.Bottom ? centerY - (mon.Bottom - 1) : 0;
            long dist = dx * dx + dy * dy;
            if (dist < bestDistance)
            {
                bestDistance = dist;
                nearest = displays[i];
            }
        }
        return nearest;
    }

    internal async Task CommitMultiSelectedGroupDragAsync(MainWindow lead)
    {
        if (_groupDragLeadId != lead.OrganizerId || _groupDragPeers.Count == 0)
        {
            _groupDragPeers.Clear();
            _groupDragDisplays = null;
            _groupDragLeadId = null;
            return;
        }

        Guid[] peerIds = _groupDragPeers.Keys.ToArray();
        _groupDragPeers.Clear();
        _groupDragDisplays = null;
        _groupDragLeadId = null;

        bool snapToGrid = State.GlobalSettings.SnapFloatingToDesktopGrid;
        foreach (Guid peerId in peerIds)
        {
            if (!_windows.TryGetValue(peerId, out MainWindow? peer)) continue;
            NativeMethods.RECT finalBounds = peer.GetCurrentCompactBounds();
            if (snapToGrid && peer.Definition.PlacementMode == OrganizerPlacementMode.Floating)
            {
                finalBounds = SnapSingleFloatingToDesktopGrid(peer, finalBounds);
                peer.MoveAsGroupDragPeer(finalBounds);
            }
            peer.FinishGroupPeerDrag(commitPosition: true);
        }

        await SaveStateAsync();
        await CaptureCurrentTopologySnapshotAsync();
    }

    internal void CancelMultiSelectedGroupDrag(MainWindow lead)
    {
        if (_groupDragLeadId != lead.OrganizerId) return;
        foreach ((Guid peerId, var snapshot) in _groupDragPeers)
        {
            if (!_windows.TryGetValue(peerId, out MainWindow? peer)) continue;
            peer.MoveAsGroupDragPeer(snapshot.InitialBounds);
            peer.FinishGroupPeerDrag(commitPosition: false);
        }
        _groupDragPeers.Clear();
        _groupDragDisplays = null;
        _groupDragLeadId = null;
    }

    internal NativeMethods.RECT SnapSingleFloatingToDesktopGrid(MainWindow source, NativeMethods.RECT desiredBounds)
    {
        DisplayInfo display = DisplayPlacementService.ForBounds(desiredBounds);
        DesktopGridSnapshot snapshot = _desktopGrid.ReadSnapshot(display);
        WindowAlignmentInsets insets = source.GetCurrentCompactAlignmentInsets();

        var siblingFrames = new List<NativeMethods.RECT>();
        foreach (MainWindow other in _windows.Values)
        {
            if (ReferenceEquals(other, source) || !other.IsEligibleForDesktopMultiSelect) continue;
            if (!string.Equals(
                DisplayPlacementService.ForBounds(other.GetCurrentCompactBounds()).Device,
                display.Device,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            siblingFrames.Add(other.GetVisibleCompactFrameRect());
        }

        return DesktopGridSnapMath.SnapSingleBounds(
            desiredBounds,
            insets,
            display.Work,
            snapshot.CellWidthPx,
            snapshot.CellHeightPx,
            snapshot.DesktopIconCenters,
            siblingFrames);
    }

    public async Task<int> AlignFloatingToDesktopGridAsync(Guid? anchorOrganizerId = null)
    {
        MainWindow? anchorWindow = anchorOrganizerId is Guid id && _windows.TryGetValue(id, out MainWindow? found)
            ? found
            : _windows.Values.FirstOrDefault(w => w.Definition.PlacementMode == OrganizerPlacementMode.Floating && w.Definition.ContainerOrganizerId is null);
        if (anchorWindow is null) return 0;

        DisplayInfo display = DisplayPlacementService.ForBounds(anchorWindow.GetCurrentCompactBounds());
        DesktopGridSnapshot snapshot = _desktopGrid.ReadSnapshot(display);

        // If user multi-selected organizers, align only the selected group; otherwise align all floating windows on this display.
        bool useMultiSelection = _selectedOrganizerIds.Count > 1 &&
            (anchorOrganizerId is null || _selectedOrganizerIds.Contains(anchorOrganizerId.Value));

        var targets = new List<DesktopGridSnapTarget>();
        var staticOccupied = new List<NativeMethods.RECT>();
        foreach (MainWindow window in _windows.Values)
        {
            if (window.IsClosing || window.IsContainedWindow || !window.IsRuntimeVisible ||
                window.Definition.PlacementMode is not (OrganizerPlacementMode.Floating or OrganizerPlacementMode.Positioned))
                continue;
            NativeMethods.RECT bounds = window.GetCurrentCompactBounds();
            if (!string.Equals(DisplayPlacementService.ForBounds(bounds).Device, display.Device, StringComparison.OrdinalIgnoreCase))
                continue;

            bool include = window.Definition.PlacementMode == OrganizerPlacementMode.Floating &&
                (!useMultiSelection || _selectedOrganizerIds.Contains(window.OrganizerId));
            if (include)
            {
                targets.Add(new DesktopGridSnapTarget(window.OrganizerId, bounds, window.GetCurrentCompactAlignmentInsets()));
            }
            else
            {
                staticOccupied.Add(window.GetVisibleCompactFrameRect());
            }
        }

        if (targets.Count == 0) return 0;

        IReadOnlyList<DesktopGridSnapResult> snapped = DesktopGridSnapMath.SnapGroupToGrid(
            targets,
            display.Work,
            snapshot.CellWidthPx,
            snapshot.CellHeightPx,
            snapshot.DesktopIconCenters,
            staticOccupied);

        int moved = 0;
        foreach (DesktopGridSnapResult placement in snapped)
        {
            if (!_windows.TryGetValue(placement.OrganizerId, out MainWindow? window)) continue;
            window.ApplyProgrammaticCompactBounds(placement.SnappedBounds);
            moved++;
        }

        if (moved > 0)
        {
            await SaveStateAsync();
            await CaptureCurrentTopologySnapshotAsync();
        }
        return moved;
    }

    internal async Task<(int ImportedCount, int ShortcutCount, int FailedCount)> ImportSelectedDesktopIconsAsync(Guid organizerId)
    {
        if (!_windows.TryGetValue(organizerId, out MainWindow? window))
            return (0, 0, 0);

        string[] selectedPaths = await RunOnStaThreadAsync(DesktopSelectionService.ReadSelectedDesktopPaths);
        if (selectedPaths.Length == 0)
            return (0, 0, 0);

        string targetDir = AppPaths.ResolveStoragePath(window.Definition);
        string normalizedTarget = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar);
        string[] safePaths = selectedPaths
            .Where(path =>
            {
                string full = Path.GetFullPath(path);
                string? dir = Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(dir, normalizedTarget, StringComparison.OrdinalIgnoreCase)) return false;
                if (Directory.Exists(full) &&
                    (full.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                     normalizedTarget.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
                return true;
            })
            .ToArray();

        if (safePaths.Length == 0) return (0, 0, 0);

        IReadOnlyList<TransferOutcome> outcomes = await TransferQueue.RunAsync(
            token => window.Storage.ImportBatchAsync(safePaths, progress: null, token));

        int moved = outcomes.Count(o => o.Status is TransferStatus.Moved or TransferStatus.Copied or TransferStatus.CopiedSourceRetained);
        int shortcuts = outcomes.Count(o => o.Status == TransferStatus.ShortcutCreated);
        int failed = outcomes.Count(o => o.Status == TransferStatus.Failed);

        if (moved > 0 || shortcuts > 0)
            await window.RefreshCatalogAsync(notifyUnsupported: false);

        return (moved, shortcuts, failed);
    }

    private static Task<T> RunOnStaThreadAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "TuckPane.DesktopSelectionSta"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
