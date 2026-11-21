using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TuckPane.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.UI;

namespace TuckPane;

public sealed partial class OrganizerSettingsForm : UserControl
{
    private readonly Dictionary<Border, long> _rowVisibilityTokens = new();

    public OrganizerSettingsForm()
    {
        InitializeComponent();
        RowsSlider.HorizontalAlignment = HorizontalAlignment.Stretch;
        ColumnsSlider.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    public bool IsCreationMode
    {
        get => _isCreationMode;
        set
        {
            _isCreationMode = value;
            CreationStorageCard.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            ManageStorageGroup.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            DockSizeLabelText.Tag = value ? "loc:DockIconSizeLabel" : "loc:DockSizeLabel";
            RowsSlider.Width = double.NaN;
            RowsSlider.HorizontalAlignment = HorizontalAlignment.Stretch;
            ColumnsSlider.Width = double.NaN;
            ColumnsSlider.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }
    private bool _isCreationMode;

    internal event EventHandler<object>? ControlValueChanged;
    internal event SelectionChangedEventHandler? ExpansionModeSelectionChanged;

    internal TextBox NameInput => NameBox;
    internal TextBlock NameError => NameErrorText;
    internal TextBox StoragePathInput => _isCreationMode ? CreationStoragePathBox : ManagePathBox;
    internal Button ChooseStorage => ChooseStorageButton;
    internal Button ResetStorage => ResetStorageButton;
    internal Button OpenManagedFolder => OpenManagedFolderButton;
    internal ComboBox PlacementCombo => PlacementModeCombo;
    internal ComboBoxItem FloatingItem => FloatingModeItem;
    internal ComboBoxItem PositionedItem => PositionedModeItem;
    internal ComboBox DockOrientation => DockOrientationCombo;
    internal ConsoleSlider DockIconSize => DockIconSizeSlider;
    internal TextBlock DockIconSizeLabel => DockIconSizeValue;
    internal ConsoleSlider DockSpacing => DockSpacingSlider;
    internal TextBlock DockSpacingLabel => DockSpacingValue;
    internal ComboBox ExpansionCombo => ExpansionModeCombo;
    internal ComboBox ExpandedContentCombo => ExpandedContentModeCombo;
    internal ComboBox DisplaySelector => DisplayCombo;
    internal ComboBox DockEdgeSelector => DockEdgeCombo;
    internal ConsoleSlider Rows => RowsSlider;
    internal ConsoleSlider Columns => ColumnsSlider;
    internal ConsoleSlider CompactScale => CompactScaleSlider;
    internal ConsoleSlider CanvasScale => CanvasScaleSlider;
    internal ConsoleSlider ItemScale => ItemScaleSlider;

    internal ConsoleSlider[] AllSliders =>
    [
        RowsSlider,
        ColumnsSlider,
        CompactScaleSlider,
        CanvasScaleSlider,
        ItemScaleSlider,
        DockIconSizeSlider,
        DockSpacingSlider
    ];

    internal void SubscribeSliderCompletion(EventHandler handler)
    {
        foreach (ConsoleSlider slider in AllSliders)
            slider.AdjustmentCompleted += handler;
    }

    internal void ApplySurfacePalette(
        Color card,
        Color rowBorder,
        Color primaryText,
        Color secondaryText,
        Color inputSurface,
        Color sliderActive,
        Color sliderInactive,
        Color sliderFocusSecondary)
    {
        SetBrush("FormCardSurfaceBrush", card);
        SetBrush("FormRowBorderBrush", rowBorder);
        SetBrush("FormPrimaryTextBrush", primaryText);
        SetBrush("FormSecondaryTextBrush", secondaryText);
        SetBrush("FormInputSurfaceBrush", inputSurface);
        SetBrush("FormSliderActiveBrush", sliderActive);
        SetBrush("FormSliderInactiveBrush", sliderInactive);
        SetBrush("FormSliderFocusPrimaryBrush", sliderActive);
        SetBrush("FormSliderFocusSecondaryBrush", sliderFocusSecondary);
    }

    internal void PopulateDisplays(IReadOnlyList<DisplayInfo> displays, string? preferredDevice = null)
    {
        string? target = preferredDevice ?? SelectedDisplayDevice;
        DisplayCombo.Items.Clear();
        for (int index = 0; index < displays.Count; index++)
        {
            DisplayInfo display = displays[index];
            DisplayCombo.Items.Add(new ComboBoxItem
            {
                Tag = display.Device,
                Content = AppStrings.Format("DisplayItemFormat", index + 1, display.Monitor.Width, display.Monitor.Height)
            });
        }
        SelectDisplay(target);
    }

    internal void SelectDisplay(string? device)
    {
        DisplayCombo.SelectedItem = DisplayCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, device, StringComparison.OrdinalIgnoreCase));
        if (DisplayCombo.SelectedIndex < 0 && DisplayCombo.Items.Count > 0) DisplayCombo.SelectedIndex = 0;
    }

    internal string? SelectedDisplayDevice => (DisplayCombo.SelectedItem as ComboBoxItem)?.Tag as string;

    internal void SelectPlacementMode(OrganizerPlacementMode mode)
    {
        if (_isCreationMode)
        {
            PlacementModeCombo.SelectedIndex = mode == OrganizerPlacementMode.Positioned ? 1 : 0;
            return;
        }
        PlacementModeCombo.SelectedItem = mode switch
        {
            OrganizerPlacementMode.Floating => FloatingModeItem,
            OrganizerPlacementMode.Positioned => PositionedModeItem,
            _ => null
        };
    }

    internal OrganizerPlacementMode ResolvePlacementMode(ThemeTarget creationCategory, OrganizerPlacementMode? editingSourceMode)
    {
        if (_isCreationMode)
        {
            return creationCategory switch
            {
                ThemeTarget.Station => OrganizerPlacementMode.Station,
                ThemeTarget.Dock => OrganizerPlacementMode.Dock,
                _ => PlacementModeCombo.SelectedIndex == 1
                    ? OrganizerPlacementMode.Positioned
                    : OrganizerPlacementMode.Floating
            };
        }
        return editingSourceMode switch
        {
            OrganizerPlacementMode.Station => OrganizerPlacementMode.Station,
            OrganizerPlacementMode.Dock => OrganizerPlacementMode.Dock,
            _ => ReferenceEquals(PlacementModeCombo.SelectedItem, PositionedModeItem)
                ? OrganizerPlacementMode.Positioned
                : OrganizerPlacementMode.Floating
        };
    }

    internal void LoadFromDefinition(
        OrganizerDefinition value,
        OrganizerExpansionMode requestedExpansionMode,
        bool isStationCategory)
    {
        NameBox.Text = value.Name;
        SelectPlacementMode(value.PlacementMode);
        ExpansionModeCombo.SelectedIndex = (int)requestedExpansionMode;
        ExpandedContentModeCombo.SelectedIndex = (int)value.ExpandedContentMode;
        DockEdgeCombo.SelectedIndex = (int)value.DockEdge;
        SelectDisplay(value.Position?.MonitorDevice);
        ConfigureGridSliders(isStationCategory);
        RowsSlider.SetModelValue(value.Layout.Rows);
        ColumnsSlider.SetModelValue(value.Layout.Columns);
        UpdateGridDimensionLabels();
        if (_isCreationMode)
        {
            CompactScaleSlider.Maximum = value.PlacementMode == OrganizerPlacementMode.Positioned
                ? OrganizerLimits.MaximumPositionedCompactScale
                : OrganizerLimits.MaximumCompactScale;
            CanvasScaleSlider.Minimum = .1;
            ItemScaleSlider.Maximum = Math.Max(1.65, value.ItemScale);
        }
        CompactScaleSlider.SetModelValue(value.CompactScale);
        CanvasScaleSlider.SetModelValue(value.CanvasScale);
        ItemScaleSlider.SetModelValue(value.ItemScale);
        DockOrientationCombo.SelectedIndex = (int)value.DockOrientation;
        DockIconSizeSlider.SetModelValue(value.DockIconSizeDip);
        DockSpacingSlider.SetModelValue(value.DockSpacingFactor);
    }

    internal bool UpdateDockControls(
        OrganizerPlacementMode activeMode,
        OrganizerPlacementMode? sourceMode,
        ThemeTarget creationCategory)
    {
        bool dock = activeMode == OrganizerPlacementMode.Dock;
        DockSettingsCard.Visibility = dock ? Visibility.Visible : Visibility.Collapsed;
        if (_isCreationMode)
        {
            IdentityGroup.Visibility = Visibility.Visible;
            PlacementModeCard.Visibility = creationCategory == ThemeTarget.Organizer ? Visibility.Visible : Visibility.Collapsed;
            ExpandedContentModeCard.Visibility = dock ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            bool regular = sourceMode is { } mode && OrganizerKinds.IsRegular(mode);
            IdentityGroup.Visibility = sourceMode == OrganizerPlacementMode.Station
                ? Visibility.Collapsed
                : Visibility.Visible;
            PlacementModeCard.Visibility = regular ? Visibility.Visible : Visibility.Collapsed;
            ExpandedContentModeCard.Visibility = regular ? Visibility.Visible : Visibility.Collapsed;
            PlacementModeCombo.IsEnabled = regular;
        }

        if (!dock) return false;

        NameCard.Visibility = Visibility.Visible;
        foreach (FrameworkElement control in new FrameworkElement[]
        {
            ExpansionModeCard, DisplayCard, DockEdgeCard,
            CompactScaleCard, CanvasScaleCard, RowsCard, ColumnsCard
        })
        {
            control.Visibility = Visibility.Collapsed;
        }
        if (_isCreationMode)
        {
            ExpandedContentModeCombo.SelectedIndex = 0;
            ExpansionModeCombo.SelectedIndex = 0;
        }
        DockIconSizeValue.Text = $"{DockLayoutMath.NormalizeIconSize(DockIconSizeSlider.Value):0} DIP";
        DockSpacingValue.Text = $"{DockLayoutMath.NormalizeSpacing(DockSpacingSlider.Value):0.00}";
        RefreshSeparators();
        return true;
    }

    internal (int Rows, int Columns) UpdateNonDockConstraints(
        OrganizerPlacementMode placementMode,
        OrganizerDefinition? editingSource,
        GlobalSettings globalSettings,
        bool canChangeExpansionMode,
        Func<DisplayInfo> resolveNonStationDisplay)
    {
        bool positioned = placementMode == OrganizerPlacementMode.Positioned;
        bool station = placementMode == OrganizerPlacementMode.Station;
        bool stationSource = editingSource?.PlacementMode == OrganizerPlacementMode.Station;

        if (station) ExpansionModeCombo.SelectedIndex = 0;
        ExpansionModeCard.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
        ExpansionModeCombo.IsEnabled = _isCreationMode ? !station : (!station && canChangeExpansionMode);

        if (!_isCreationMode)
        {
            FloatingModeItem.IsEnabled = !stationSource;
            PositionedModeItem.IsEnabled = !stationSource;
            NameErrorText.Visibility = Visibility.Collapsed;
        }

        if (station) ExpandedContentModeCombo.SelectedIndex = (int)OrganizerExpandedContentMode.Icon;
        ExpandedContentModeCombo.IsEnabled = !station;
        if (_isCreationMode) ExpandedContentModeCard.Visibility = station ? Visibility.Collapsed : Visibility.Visible;

        bool compactList = !station &&
            ExpandedContentModeCombo.SelectedIndex == (int)OrganizerExpandedContentMode.CompactList;
        NameCard.Visibility = station ? Visibility.Collapsed : Visibility.Visible;
        DisplayCard.Visibility = station ? Visibility.Visible : Visibility.Collapsed;
        DockEdgeCard.Visibility = station ? Visibility.Visible : Visibility.Collapsed;
        RowsCard.Visibility = compactList ? Visibility.Collapsed : Visibility.Visible;
        ColumnsCard.Visibility = compactList ? Visibility.Collapsed : Visibility.Visible;

        CompactScaleSlider.Maximum = positioned
            ? OrganizerLimits.MaximumPositionedCompactScale
            : OrganizerLimits.MaximumCompactScale;
        bool compactScaleConstrained = globalSettings.UsesUniformCompactScale(placementMode);
        CompactScaleCard.Visibility = _isCreationMode
            ? (station || compactScaleConstrained ? Visibility.Collapsed : Visibility.Visible)
            : (station ? Visibility.Collapsed : Visibility.Visible);
        if (compactScaleConstrained)
            CompactScaleSlider.SetModelValue(globalSettings.ResolveCompactScale(placementMode, CompactScaleSlider.Value));
        CompactScaleSlider.IsEnabled = !compactScaleConstrained;
        CompactScaleContentGrid.Opacity = compactScaleConstrained ? 0.48 : 1.0;
        ToolTipService.SetToolTip(CompactScaleCard, compactScaleConstrained ? AppStrings.Get("UniformEntrySizes") : null);
        CompactScaleSlider.SetModelValue(Math.Clamp(
            CompactScaleSlider.Value,
            OrganizerLimits.MinimumCompactScale,
            CompactScaleSlider.Maximum));

        ConfigureGridSliders(station);
        (int rows, int columns) = ReadGridDimensions(station);
        var layout = new OrganizerLayout { Mode = OrganizerLayoutMode.Grid, Rows = rows, Columns = columns };
        DisplayInfo display = station
            ? DisplayPlacementService.GetDisplay(SelectedDisplayDevice)
            : resolveNonStationDisplay();

        double maximumItemScale;
        if (station)
        {
            maximumItemScale = DisplayPlacementService.CalculateMaximumStationItemScale(display, layout);
        }
        else if (!_isCreationMode &&
                 editingSource?.ManualCanvasBaseWidthDip is double baseWidth &&
                 editingSource.ManualCanvasBaseHeightDip is double baseHeight)
        {
            (double minimumWidth, double minimumHeight) =
                DisplayPlacementService.CalculateMinimumExpandedSizeDip(layout, .5);
            CanvasScaleSlider.Minimum = Math.Min(1.2,
                Math.Max(.1, Math.Max(minimumWidth / baseWidth, minimumHeight / baseHeight)));
            if (CanvasScaleSlider.Value < CanvasScaleSlider.Minimum)
                CanvasScaleSlider.SetModelValue(CanvasScaleSlider.Minimum);
            double canvas = CanvasScaleSlider.Value;
            NativeMethods.RECT work = DisplayPlacementService.GetExpandedWorkArea(display);
            double availablePanelHeightDip = Math.Max(
                1,
                work.Height / display.Scale - DisplayPlacementService.ExpandedTitleBandDip);
            double fit = Math.Min(1, Math.Min(
                work.Width / display.Scale / (baseWidth * canvas),
                availablePanelHeightDip / (baseHeight * canvas)));
            maximumItemScale = DisplayPlacementService.CalculateMaximumItemScaleForExpandedSize(
                layout,
                baseWidth * canvas * fit,
                baseHeight * canvas * fit);
        }
        else
        {
            CanvasScaleSlider.Minimum = DisplayPlacementService.CalculateMinimumCanvasScale(display, layout);
            if (CanvasScaleSlider.Value < CanvasScaleSlider.Minimum)
                CanvasScaleSlider.SetModelValue(CanvasScaleSlider.Minimum);
            maximumItemScale = DisplayPlacementService.CalculateMaximumItemScale(display, layout, CanvasScaleSlider.Value);
        }

        ItemScaleSlider.Maximum = maximumItemScale;
        if (ItemScaleSlider.Value > ItemScaleSlider.Maximum)
            ItemScaleSlider.SetModelValue(ItemScaleSlider.Maximum);

        UpdateGridDimensionLabels();
        SetPercent(CompactPercent, CompactScaleSlider.Value);
        SetPercent(CanvasPercent, CanvasScaleSlider.Value);
        SetPercent(ItemPercent, ItemScaleSlider.Value);
        RefreshSeparators();
        return (rows, columns);
    }

    internal (int Rows, int Columns) ReadGridDimensions(bool station) => (
        Math.Clamp((int)Math.Round(RowsSlider.Value),
            station ? OrganizerLimits.MinimumStationRows : OrganizerLimits.MinimumGridDimension,
            station ? OrganizerLimits.MaximumStationRows : OrganizerLimits.MaximumLayoutDimension),
        Math.Clamp((int)Math.Round(ColumnsSlider.Value),
            station ? OrganizerLimits.MinimumStationColumns : OrganizerLimits.MinimumGridDimension,
            station ? OrganizerLimits.MaximumStationColumns : OrganizerLimits.MaximumLayoutDimension));

    internal OrganizerVisualChange GetVisualChange(object sender)
    {
        if (ReferenceEquals(sender, NameBox)) return OrganizerVisualChange.Name;
        if (ReferenceEquals(sender, PlacementModeCombo))
            return OrganizerVisualChange.PlacementMode | OrganizerVisualChange.CompactScale |
                   OrganizerVisualChange.Docking | OrganizerVisualChange.ExpandedContentMode;
        if (ReferenceEquals(sender, ExpandedContentModeCombo)) return OrganizerVisualChange.ExpandedContentMode;
        if (ReferenceEquals(sender, DisplayCombo) || ReferenceEquals(sender, DockEdgeCombo))
            return OrganizerVisualChange.Docking;
        if (ReferenceEquals(sender, CompactScaleSlider)) return OrganizerVisualChange.CompactScale;
        if (ReferenceEquals(sender, CanvasScaleSlider)) return OrganizerVisualChange.CanvasScale | OrganizerVisualChange.ItemScale;
        if (ReferenceEquals(sender, ItemScaleSlider)) return OrganizerVisualChange.ItemScale;
        return OrganizerVisualChange.Layout | OrganizerVisualChange.ItemScale | OrganizerVisualChange.CanvasScale;
    }

    internal void UnregisterCallbacks()
    {
        foreach (var (row, token) in _rowVisibilityTokens)
            row.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, token);
        _rowVisibilityTokens.Clear();
    }

    private void ConfigureGridSliders(bool station)
    {
        RowsSlider.Minimum = station ? OrganizerLimits.MinimumStationRows : OrganizerLimits.MinimumGridDimension;
        RowsSlider.Maximum = station ? OrganizerLimits.MaximumStationRows : OrganizerLimits.MaximumLayoutDimension;
        ColumnsSlider.Minimum = station ? OrganizerLimits.MinimumStationColumns : OrganizerLimits.MinimumGridDimension;
        ColumnsSlider.Maximum = station ? OrganizerLimits.MaximumStationColumns : OrganizerLimits.MaximumLayoutDimension;
        RowsSlider.SetModelValue(Math.Clamp(RowsSlider.Value, RowsSlider.Minimum, RowsSlider.Maximum));
        ColumnsSlider.SetModelValue(Math.Clamp(ColumnsSlider.Value, ColumnsSlider.Minimum, ColumnsSlider.Maximum));
    }

    private void RefreshSeparators()
    {
        UpdateGroupSeparators(IdentityPanel);
        UpdateGroupSeparators(ParametersPanel);
    }

    private void SettingsGroup_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border { Child: StackPanel panel }) return;
        foreach (Border row in panel.Children.OfType<Border>())
        {
            if (_rowVisibilityTokens.ContainsKey(row)) continue;
            _rowVisibilityTokens[row] = row.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty, (_, _) => UpdateGroupSeparators(panel));
        }
        UpdateGroupSeparators(panel);
    }

    private static void UpdateGroupSeparators(StackPanel panel)
    {
        Border[] visibleRows = panel.Children.OfType<Border>()
            .Where(row => row.Visibility == Visibility.Visible).ToArray();
        for (int index = 0; index < visibleRows.Length; index++)
            visibleRows[index].BorderThickness = index == visibleRows.Length - 1
                ? new Thickness(0)
                : new Thickness(0, 0, 0, 1);
    }

    private void FormControl_Changed(object sender, object e)
    {
        if (ReferenceEquals(sender, RowsSlider) || ReferenceEquals(sender, ColumnsSlider))
            UpdateGridDimensionLabels();
        ControlValueChanged?.Invoke(sender, e);
    }

    private void UpdateGridDimensionLabels()
    {
        if (RowsValueText is not null && RowsSlider is not null)
            RowsValueText.Text = $"{Math.Round(RowsSlider.Value):0}";
        if (ColumnsValueText is not null && ColumnsSlider is not null)
            ColumnsValueText.Text = $"{Math.Round(ColumnsSlider.Value):0}";
    }

    private void ExpansionModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isCreationMode) ControlValueChanged?.Invoke(sender, e);
        else ExpansionModeSelectionChanged?.Invoke(sender, e);
    }

    private void SetBrush(string key, Color color)
    {
        if (Resources[key] is SolidColorBrush brush) brush.Color = color;
    }

    private static void SetPercent(TextBlock target, double value) =>
        target.Text = $"{Math.Round(value * 100):0}%";
}
