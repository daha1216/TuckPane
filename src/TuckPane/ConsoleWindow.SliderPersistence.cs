using TuckPane.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private readonly SliderSaveSequence _uniformSliderSave = new();
    private readonly SliderSaveSequence _nameSliderSave = new();
    private readonly SliderSaveSequence _delaySliderSave = new();
    private readonly SliderSaveSequence _themeSliderSave = new();

    private void InitializeSliderCompletion()
    {
        WireSliderGroup(
            [UniformFloatingCompactScaleSlider, UniformPositionedCompactScaleSlider],
            async () =>
            {
                _uniformCompactScaleSaveTimer.Stop();
                if (_uniformCompactScaleApplyScheduled) ApplyPendingUniformCompactScaleChanges(null, EventArgs.Empty);
                _ = await SaveUniformSlidersAsync();
                UpdateUniformCompactScaleControls();
            });

        WireSliderGroup(
            [CompactHoverMagnificationSlider, DockHoverMagnificationSlider],
            async () =>
            {
                _hoverMagnificationSaveTimer?.Stop();
                _ = await SaveHoverMagnificationSlidersAsync();
                UpdateHoverMagnificationScaleControls();
            });

        WireSliderGroup(
            [CompactNameScaleSlider, ExpandedNameScaleSlider],
            async () =>
            {
                _nameScaleSaveTimer.Stop();
                _ = await SaveNameSlidersAsync();
                UpdateNameScaleControls();
            });

        WireSliderGroup(
            [
                HoverExpandDelaySlider,
                PointerLeaveCollapseDelaySlider,
                StationPointerLeaveCollapseDelaySlider,
                StationActivationDistanceSlider,
                StationHoverExpandDelaySlider
            ],
            async () =>
            {
                _hoverDelaySaveTimer.Stop();
                _ = await SaveDelaySlidersAsync();
                UpdateHoverDelayControls();
            });

        WireSliderGroup(
            [ThemeTransparencySlider, ThemeBlurStrengthSlider],
            async () =>
            {
                _themeSaveTimer.Stop();
                _ = await SaveThemeAsync();
                UpdateThemeControls();
            });

        ManageForm.SubscribeSliderCompletion(async (_, _) =>
        {
            if (!_componentReady || _closingPermanently) return;
            _ = await FlushPendingManageChangesAsync(commitName: false);
            UpdateManageControls();
        });

        AddForm.SubscribeSliderCompletion((_, _) =>
        {
            if (!_componentReady || _closingPermanently) return;
            UpdateAddControls();
        });
    }

    private void WireSliderGroup(ConsoleSlider[] sliders, Func<Task> onCompletedAsync)
    {
        EventHandler handler = async (_, _) =>
        {
            if (!_componentReady || _closingPermanently) return;
            await onCompletedAsync();
        };
        foreach (ConsoleSlider slider in sliders)
            slider.AdjustmentCompleted += handler;
    }

    internal async Task<bool> FlushPendingSliderSavesAsync()
    {
        _uniformCompactScaleSaveTimer.Stop();
        _nameScaleSaveTimer.Stop();
        _hoverDelaySaveTimer.Stop();
        _themeSaveTimer.Stop();
        _hoverMagnificationSaveTimer?.Stop();
        if (_uniformCompactScaleApplyScheduled) ApplyPendingUniformCompactScaleChanges(null, EventArgs.Empty);
        bool saved = await SaveHoverMagnificationSlidersAsync();
        saved = await SaveUniformSlidersAsync() && saved;
        saved = await SaveNameSlidersAsync() && saved;
        saved = await SaveDelaySlidersAsync() && saved;
        return await SaveThemeAsync() && saved;
    }

    private Task<bool> SaveUniformSlidersAsync() => _uniformSliderSave.SaveAsync(
        () =>
        {
            if (_uniformCompactScaleApplyScheduled) ApplyPendingUniformCompactScaleChanges(null, EventArgs.Empty);
            return (_host.State.GlobalSettings.UniformFloatingCompactScale,
                _host.State.GlobalSettings.UniformPositionedCompactScale,
                _host.CaptureUniformCompactScaleSnapshots(OrganizerPlacementMode.Floating),
                _host.CaptureUniformCompactScaleSnapshots(OrganizerPlacementMode.Positioned), _uniformSliderSave.Revision);
        },
        _host.SaveStateAsync,
        saved =>
        {
            _savedUniformFloatingCompactScale = saved.Item1;
            _savedUniformPositionedCompactScale = saved.Item2;
            bool newerEdit = _uniformSliderSave.Revision != saved.Item5;
            _savedUniformFloatingSnapshots = newerEdit ? saved.Item3 : null;
            _savedUniformPositionedSnapshots = newerEdit ? saved.Item4 : null;
        },
        () => { RestoreSavedUniformCompactScales(); UpdateUniformCompactScaleControls(); },
        ex => ShowSliderSaveError("UniformCompactScaleErrorTitle", ex));

    private Task<bool> SaveNameSlidersAsync() => _nameSliderSave.SaveAsync(
        () => (_host.State.GlobalSettings.UniformFloatingCompactNameScale, _host.State.GlobalSettings.ExpandedNameScale),
        _host.SaveStateAsync,
        saved => { _savedCompactNameScale = saved.Item1; _savedExpandedNameScale = saved.Item2; },
        () => { RestoreSavedNameScales(); UpdateNameScaleControls(); },
        ex => ShowSliderSaveError("UniformCompactNameScaleErrorTitle", ex));

    private Task<bool> SaveDelaySlidersAsync() => _delaySliderSave.SaveAsync(
        () => (_host.State.GlobalSettings.HoverExpandDelayMs, _host.State.GlobalSettings.PointerLeaveCollapseDelayMs,
            _host.State.GlobalSettings.StationPointerLeaveCollapseDelayMs, _host.State.GlobalSettings.StationActivationDistanceDip,
            _host.State.GlobalSettings.StationHoverExpandDelayMs),
        _host.SaveStateAsync,
        saved =>
        {
            (_savedHoverExpandDelayMs, _savedPointerLeaveCollapseDelayMs, _savedStationPointerLeaveCollapseDelayMs,
                _savedStationActivationDistanceDip, _savedStationHoverExpandDelayMs) = saved;
        },
        () =>
        {
            _host.SetHoverDelays(_savedHoverExpandDelayMs, _savedPointerLeaveCollapseDelayMs, _savedStationPointerLeaveCollapseDelayMs);
            _host.SetStationActivation(_savedStationActivationDistanceDip, _savedStationHoverExpandDelayMs);
            UpdateHoverDelayControls();
        },
        ex => ShowSliderSaveError("HoverDelayErrorTitle", ex));

    private Task<bool> SaveThemeSlidersAsync() => _themeSliderSave.SaveAsync(
        () => (_host.State.GlobalSettings.GetTheme(ThemeTarget.Organizer),
            _host.State.GlobalSettings.GetTheme(ThemeTarget.Station), _host.State.GlobalSettings.GetTheme(ThemeTarget.Dock)),
        _host.SaveStateAsync,
        saved => (_savedOrganizerTheme, _savedStationTheme, _savedDockTheme) = saved,
        () =>
        {
            foreach (var (target, previous) in new[] { (ThemeTarget.Organizer, _savedOrganizerTheme), (ThemeTarget.Station, _savedStationTheme), (ThemeTarget.Dock, _savedDockTheme) })
                _host.UpdateGlobalTheme(target, previous.ColorArgb, previous.Transparency, previous.BlurStrength,
                    previous.SolidColorMode, previous.SolidOpacity, previous.FullyTransparent);
        },
        ex => ShowSliderSaveError("ThemeSaveErrorTitle", ex));

    private void ShowSliderSaveError(string key, Exception ex)
    {
        AppLogger.Error("无法保存滑条设置。", ex);
        ShowError(AppStrings.Get(key), ex.Message);
    }
}
