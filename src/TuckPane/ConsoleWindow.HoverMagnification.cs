using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using TuckPane.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private readonly SliderSaveSequence _hoverMagnificationSave = new();
    private DispatcherQueueTimer? _hoverMagnificationSaveTimer;
    private bool _loadingHoverMagnificationScales;
    private bool _hoverMagnificationApplyScheduled;
    private double _pendingCompactHoverScale, _pendingDockHoverScale;
    private double _savedCompactHoverScale, _savedDockHoverScale;

    private void InitializeHoverMagnificationSliders()
    {
        _savedCompactHoverScale = _host.State.GlobalSettings.CompactHoverMagnificationScale;
        _savedDockHoverScale = _host.State.GlobalSettings.DockHoverMagnificationScale;
        _pendingCompactHoverScale = _savedCompactHoverScale;
        _pendingDockHoverScale = _savedDockHoverScale;
        _hoverMagnificationSaveTimer = DispatcherQueue.CreateTimer();
        _hoverMagnificationSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _hoverMagnificationSaveTimer.IsRepeating = false;
        _hoverMagnificationSaveTimer.Tick += async (_, _) => await SaveHoverMagnificationSlidersAsync();
        UpdateHoverMagnificationScaleControls();
    }

    private void UpdateHoverMagnificationScaleControls()
    {
        _loadingHoverMagnificationScales = true;
        try
        {
            GlobalSettings settings = _host.State.GlobalSettings;
            CompactHoverMagnificationSlider.SetModelValue(settings.CompactHoverMagnificationScale);
            DockHoverMagnificationSlider.SetModelValue(settings.DockHoverMagnificationScale);
            CompactHoverMagnificationSlider.IsEnabled = CompactHoverMagnificationToggle.IsEnabled
                ? settings.CompactHoverMagnificationEnabled : CompactHoverMagnificationToggle.IsOn;
            DockHoverMagnificationSlider.IsEnabled = DockHoverMagnificationToggle.IsEnabled
                ? settings.DockHoverMagnificationEnabled : DockHoverMagnificationToggle.IsOn;
            CompactHoverMagnificationValue.Text = $"{settings.CompactHoverMagnificationScale * 100:0}%";
            DockHoverMagnificationValue.Text = $"{settings.DockHoverMagnificationScale * 100:0}%";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CompactHoverMagnificationSlider,
                AppStrings.Get("CompactHoverMagnificationTitle") + " — " + AppStrings.Get("HoverMagnificationScaleLabel"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DockHoverMagnificationSlider,
                AppStrings.Get("DockHoverMagnificationTitle") + " — " + AppStrings.Get("HoverMagnificationScaleLabel"));
        }
        finally { _loadingHoverMagnificationScales = false; }
    }

    private void HoverMagnificationSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_componentReady || _loadingHoverMagnificationScales || _hoverMagnificationSaveTimer is null ||
            sender is not ConsoleSlider { IsEnabled: true } slider) return;
        GlobalSettings settings = _host.State.GlobalSettings;
        double value = GlobalSettings.NormalizeHoverMagnificationScale(slider.Value);
        bool dock = ReferenceEquals(slider, DockHoverMagnificationSlider);
        double currentCompact = _hoverMagnificationApplyScheduled ? _pendingCompactHoverScale : settings.CompactHoverMagnificationScale;
        double currentDock = _hoverMagnificationApplyScheduled ? _pendingDockHoverScale : settings.DockHoverMagnificationScale;
        if (value == (dock ? currentDock : currentCompact)) return;

        _pendingCompactHoverScale = dock ? currentCompact : value;
        _pendingDockHoverScale = dock ? value : currentDock;
        if (dock)
            DockHoverMagnificationValue.Text = $"{value * 100:0}%";
        else
            CompactHoverMagnificationValue.Text = $"{value * 100:0}%";

        _hoverMagnificationSave.Changed();
        _hoverMagnificationSaveTimer.Stop();
        _hoverMagnificationSaveTimer.Start();
        if (!_hoverMagnificationApplyScheduled)
        {
            _hoverMagnificationApplyScheduled = true;
            CompositionTarget.Rendering += ApplyPendingHoverMagnificationChanges;
        }
    }

    private void ApplyPendingHoverMagnificationChanges(object? sender, object args)
    {
        if (_hoverMagnificationApplyScheduled)
            CompositionTarget.Rendering -= ApplyPendingHoverMagnificationChanges;
        _hoverMagnificationApplyScheduled = false;
        if (_closingPermanently) return;
        _host.SetHoverMagnificationScales(_pendingCompactHoverScale, _pendingDockHoverScale);
    }

    private void FlushPendingHoverMagnificationApply()
    {
        if (_hoverMagnificationApplyScheduled)
            ApplyPendingHoverMagnificationChanges(null, EventArgs.Empty);
    }

    private void DisposeHoverMagnificationCoalescing()
    {
        if (_hoverMagnificationApplyScheduled)
        {
            CompositionTarget.Rendering -= ApplyPendingHoverMagnificationChanges;
            _hoverMagnificationApplyScheduled = false;
        }
    }

    private Task<bool> SaveHoverMagnificationSlidersAsync()
    {
        FlushPendingHoverMagnificationApply();
        return _hoverMagnificationSave.SaveAsync(
            () => (_host.State.GlobalSettings.CompactHoverMagnificationScale, _host.State.GlobalSettings.DockHoverMagnificationScale),
            _host.SaveStateAsync,
            saved => (_savedCompactHoverScale, _savedDockHoverScale) = saved,
            () =>
            {
                _host.SetHoverMagnificationScales(_savedCompactHoverScale, _savedDockHoverScale);
                UpdateHoverMagnificationScaleControls();
            },
            ex => ShowSliderSaveError("SaveConfigurationError", ex));
    }
}
