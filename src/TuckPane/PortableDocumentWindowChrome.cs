using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;
using WinUIEx;

namespace TuckPane;

internal readonly record struct PortableDocumentResolvedTheme(
    bool HighContrast,
    Color Surface,
    Color Editor,
    Color Accent,
    Color Text,
    Color Border);

internal sealed class PortableDocumentWindowChrome : IDisposable
{
    internal const double MinimumWidthDip = 280;
    internal const double MinimumHeightDip = 340;
    internal const double MaximumWidthDip = 1600;
    internal const double MaximumHeightDip = 1200;
    private const double DefaultWidthDip = 360;
    private const double DefaultHeightDip = 480;

    private readonly Window _window;
    private readonly AppHost _host;
    private readonly FrameworkElement _windowRoot;
    private readonly Border _windowFrame;
    private readonly PortableDocumentTitleBar _titleBar;
    private readonly InfoBar _errorBar;
    private readonly WindowThemeBrushes _themeBrushes = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly UISettings _uiSettings = new();
    private readonly Func<string> _getTitle;
    private readonly Func<PortableNotePlacement?> _getPlacement;
    private readonly Action<PortableNotePlacement> _setPlacement;
    private readonly Func<Task> _hideWindowAsync;
    private readonly Func<string, Task> _commitRenameAsync;
    private readonly Action<NoteTheme> _onThemeSelected;

    private AppWindow _appWindow = null!;
    private InputNonClientPointerSource? _titleInput;
    private NativeWindowChromeController? _chrome;
    private IntPtr _hwnd;
    private bool _initialized;
    private bool _permanentClose;
    private bool _visibleValue;
    private bool _restoringPlacement;
    private bool _renaming;
    private bool _renameCommitInProgress;

    internal PortableDocumentWindowChrome(
        Window window,
        AppHost host,
        FrameworkElement windowRoot,
        Border windowFrame,
        PortableDocumentTitleBar titleBar,
        InfoBar errorBar,
        Func<string> getTitle,
        Func<PortableNotePlacement?> getPlacement,
        Action<PortableNotePlacement> setPlacement,
        Func<Task> hideWindowAsync,
        Func<string, Task> commitRenameAsync,
        Action<NoteTheme> onThemeSelected)
    {
        _window = window;
        _host = host;
        _windowRoot = windowRoot;
        _windowFrame = windowFrame;
        _titleBar = titleBar;
        _errorBar = errorBar;
        _getTitle = getTitle;
        _getPlacement = getPlacement;
        _setPlacement = setPlacement;
        _hideWindowAsync = hideWindowAsync;
        _commitRenameAsync = commitRenameAsync;
        _onThemeSelected = onThemeSelected;

        _window.ExtendsContentIntoTitleBar = true;
        _window.SetTitleBar(_titleBar.TitleDragSurface);
        _window.SystemBackdrop = new TransparentTintBackdrop(Colors.Transparent);

        _titleBar.TitleDragSurface.SizeChanged += TitleDragSurface_SizeChanged;
        _titleBar.TitleText.SizeChanged += TitleDragSurface_SizeChanged;
        _titleBar.TitleText.Tapped += TitleText_Tapped;
        _titleBar.TitleEditor.KeyDown += TitleEditor_KeyDown;
        _titleBar.TitleEditor.LostFocus += TitleEditor_LostFocus;
        _titleBar.CloseButton.Click += CloseButton_Click;
    }

    internal IntPtr Hwnd => _hwnd;
    internal bool IsPermanentClose => _permanentClose;
    internal WindowThemeBrushes ThemeBrushes => _themeBrushes;
    internal bool HighContrast => _accessibility.HighContrast;
    internal UISettings UiSettings => _uiSettings;

    internal bool IsVisible
    {
        get => _visibleValue;
        private set
        {
            if (_visibleValue == value) return;
            _visibleValue = value;
            _host.NotifyDockOpenStateChanged();
        }
    }

    internal void InitializeHostWindow()
    {
        if (_initialized) return;
        _initialized = true;
        _hwnd = WindowNative.GetWindowHandle(_window);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _titleInput = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
        _appWindow.IsShownInSwitchers = true;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsAlwaysOnTop = _host.State.GlobalSettings.NoteAlwaysOnTop;
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }
        long extendedStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        extendedStyle = (extendedStyle | NativeMethods.WS_EX_APPWINDOW) & ~NativeMethods.WS_EX_TOOLWINDOW;
        _ = NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(extendedStyle));
        _ = NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        _chrome = new NativeWindowChromeController(_hwnd, _window.DispatcherQueue);
        RestorePlacement();
        _appWindow.Changed += AppWindow_Changed;
        _appWindow.Closing += AppWindow_Closing;
    }

    internal void ApplyAlwaysOnTopSetting()
    {
        if (_initialized && _appWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = _host.State.GlobalSettings.NoteAlwaysOnTop;
    }

    internal void ShowAndActivate(Action? afterShowEnqueued = null)
    {
        InitializeHostWindow();
        IsVisible = true;
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore(activateWindow: true);
        _appWindow.Show(activateWindow: true);
        _window.Activate();
        _ = NativeMethods.SetWindowPos(
            _hwnd,
            _host.State.GlobalSettings.NoteAlwaysOnTop ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_TOP,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW |
            NativeMethods.SWP_NOOWNERZORDER);
        _ = NativeMethods.SetForegroundWindow(_hwnd);
        _ = _window.DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTitlePassthrough();
            afterShowEnqueued?.Invoke();
        });
    }

    internal void HideForTray()
    {
        if (!IsVisible) return;
        IsVisible = false;
        _appWindow.Hide();
    }

    internal void RestoreFromTray()
    {
        if (_permanentClose) return;
        IsVisible = true;
        _appWindow.Show(activateWindow: false);
    }

    internal async Task HideAsync(Func<Task> flushBeforeHideAsync)
    {
        if (_permanentClose) return;
        await flushBeforeHideAsync();
        IsVisible = false;
        _appWindow.Hide();
    }

    internal async Task<bool> FlushAndHideForDragAsync(Func<Task<bool>> flushAsync, string errorResourceKey)
    {
        bool wasVisible = IsVisible;
        if (!await flushAsync())
            throw new IOException(AppStrings.Get(errorResourceKey));
        if (wasVisible)
        {
            IsVisible = false;
            _appWindow.Hide();
        }
        return wasVisible;
    }

    internal void RestoreAfterDrag()
    {
        if (_permanentClose) return;
        IsVisible = true;
        _appWindow.Show(activateWindow: false);
    }

    internal async Task ClosePermanentlyAsync(Func<Task> flushBeforeCloseAsync)
    {
        if (_permanentClose) return;
        await flushBeforeCloseAsync();
        _permanentClose = true;
        _window.Close();
    }

    internal void ClosePermanentlyWithoutSave(Action stopTimers)
    {
        if (_permanentClose) return;
        stopTimers();
        _permanentClose = true;
        _window.Close();
    }

    internal void UpdateTitle(string idSuffix, string renameHintKey)
    {
        string title = _getTitle();
        _window.Title = title;
        _titleBar.TitleText.Text = title;
        AutomationProperties.SetName(_titleBar.TitleText, title);
        AutomationProperties.SetAutomationId(_titleBar.TitleText, $"Title-{idSuffix}");
        AutomationProperties.SetHelpText(_titleBar.TitleText, AppStrings.Get(renameHintKey));
        AutomationProperties.SetName(_titleBar.TitleEditor, AppStrings.Get("ContextRename"));
        AutomationProperties.SetAutomationId(_titleBar.TitleEditor, $"TitleEditor-{idSuffix}");
    }

    internal void ApplyHeaderLanguage(
        string idPrefix,
        string idSuffix,
        string renameHintKey,
        string colorKey,
        string optionKey,
        string closeKey)
    {
        string title = _getTitle();
        _window.Title = title;
        _titleBar.TitleText.Text = title;
        AutomationProperties.SetName(_titleBar.TitleText, title);
        AutomationProperties.SetAutomationId(_titleBar.TitleText, $"{idPrefix}Title-{idSuffix}");
        AutomationProperties.SetHelpText(_titleBar.TitleText, AppStrings.Get(renameHintKey));
        AutomationProperties.SetName(_titleBar.TitleEditor, AppStrings.Get("ContextRename"));
        AutomationProperties.SetAutomationId(_titleBar.TitleEditor, $"{idPrefix}TitleEditor-{idSuffix}");

        string colorLabel = AppStrings.Get(colorKey);
        AutomationProperties.SetName(_titleBar.ColorButton, colorLabel);
        AutomationProperties.SetAutomationId(_titleBar.ColorButton, $"{idPrefix}Color-{idSuffix}");
        ToolTipService.SetToolTip(_titleBar.ColorButton, colorLabel);

        string optionLabel = AppStrings.Get(optionKey);
        AutomationProperties.SetName(_titleBar.OptionToggleButton, optionLabel);
        AutomationProperties.SetAutomationId(_titleBar.OptionToggleButton, $"{idPrefix}Option-{idSuffix}");
        ToolTipService.SetToolTip(_titleBar.OptionToggleButton, optionLabel);

        string closeLabel = AppStrings.Get(closeKey);
        AutomationProperties.SetName(_titleBar.CloseButton, closeLabel);
        AutomationProperties.SetAutomationId(_titleBar.CloseButton, $"Close{idPrefix}-{idSuffix}");
        ToolTipService.SetToolTip(_titleBar.CloseButton, closeLabel);
    }

    internal void ShowThemeFlyout(NoteTheme currentTheme, string idPrefix)
    {
        var flyout = new MenuFlyout();
        foreach (NoteThemeColors colors in NoteThemePalette.All)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = AppStrings.Get(colors.NameKey),
                Tag = colors.Theme,
                IsChecked = colors.Theme == currentTheme
            };
            AutomationProperties.SetAutomationId(item, $"{idPrefix}Theme-{colors.Theme}");
            item.Click += (sender, _) =>
            {
                if (sender is ToggleMenuFlyoutItem { Tag: NoteTheme selected })
                    _onThemeSelected(selected);
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(_titleBar.ColorButton);
    }

    internal PortableDocumentResolvedTheme ApplyChromeTheme(NoteTheme theme, bool showFrameBorder)
    {
        bool highContrast = _accessibility.HighContrast;
        Color surface;
        Color editor;
        Color accent;
        Color text;
        Color border;
        if (highContrast)
        {
            surface = _uiSettings.GetColorValue(UIColorType.Background);
            editor = surface;
            accent = _uiSettings.GetColorValue(UIColorType.Accent);
            text = _uiSettings.GetColorValue(UIColorType.Foreground);
            border = text;
        }
        else
        {
            NoteThemeColors palette = NoteThemePalette.Get(theme);
            surface = palette.SurfaceColor;
            editor = palette.EditorColor;
            accent = palette.AccentColor;
            text = palette.TextColor;
            border = palette.BorderColor;
        }

        _windowFrame.Background = new SolidColorBrush(editor);
        if (showFrameBorder) _windowFrame.BorderBrush = new SolidColorBrush(border);
        _titleBar.DragTitleBar.Background = new SolidColorBrush(surface);
        _titleBar.TitleText.Foreground = new SolidColorBrush(text);
        _titleBar.TitleEditor.Foreground = new SolidColorBrush(text);
        _titleBar.ColorButton.Foreground = new SolidColorBrush(accent);
        _themeBrushes.Set(_titleBar.ColorButton.Resources, "ButtonBackgroundPointerOver",
            ColorHelper.FromArgb(30, accent.R, accent.G, accent.B));
        _themeBrushes.Set(_titleBar.ColorButton.Resources, "ButtonBackgroundPressed",
            ColorHelper.FromArgb(52, accent.R, accent.G, accent.B));
        _themeBrushes.Set(_titleBar.ColorButton.Resources, "ButtonForegroundPointerOver", accent);
        _themeBrushes.Set(_titleBar.ColorButton.Resources, "ButtonForegroundPressed", accent);

        _titleBar.OptionToggleButton.Foreground = new SolidColorBrush(text);
        _themeBrushes.Set(_titleBar.OptionToggleButton.Resources, "ToggleButtonBackgroundPointerOver",
            ColorHelper.FromArgb(24, accent.R, accent.G, accent.B));
        _themeBrushes.Set(_titleBar.OptionToggleButton.Resources, "ToggleButtonBackgroundPressed",
            ColorHelper.FromArgb(42, accent.R, accent.G, accent.B));
        _themeBrushes.Set(_titleBar.OptionToggleButton.Resources, "ToggleButtonBackgroundChecked",
            ColorHelper.FromArgb(68, accent.R, accent.G, accent.B));
        _themeBrushes.Set(_titleBar.OptionToggleButton.Resources, "ToggleButtonBackgroundCheckedPointerOver",
            ColorHelper.FromArgb(88, accent.R, accent.G, accent.B));
        _themeBrushes.Set(_titleBar.OptionToggleButton.Resources, "ToggleButtonBackgroundCheckedPressed",
            ColorHelper.FromArgb(108, accent.R, accent.G, accent.B));
        _themeBrushes.Set(_titleBar.OptionToggleButton.Resources, "ToggleButtonBackgroundCheckedDisabled",
            ColorHelper.FromArgb(34, accent.R, accent.G, accent.B));
        _themeBrushes.Set(_titleBar.OptionToggleButton.Resources, "ToggleButtonForegroundChecked", text);
        _themeBrushes.Set(_titleBar.OptionToggleButton.Resources, "ToggleButtonForegroundCheckedPointerOver", text);
        _themeBrushes.Set(_titleBar.OptionToggleButton.Resources, "ToggleButtonForegroundCheckedPressed", text);
        _themeBrushes.Set(_titleBar.OptionToggleButton.Resources, "ToggleButtonForegroundCheckedDisabled", text);

        _titleBar.CloseButton.Foreground = new SolidColorBrush(text);
        Color closeHover = highContrast ? accent : ColorHelper.FromArgb(255, 196, 43, 28);
        Color closePressed = highContrast ? accent : ColorHelper.FromArgb(255, 164, 38, 25);
        Color closeForeground = highContrast ? surface : Colors.White;
        _themeBrushes.Set(_titleBar.CloseButton.Resources, "ButtonBackgroundPointerOver", closeHover);
        _themeBrushes.Set(_titleBar.CloseButton.Resources, "ButtonBackgroundPressed", closePressed);
        _themeBrushes.Set(_titleBar.CloseButton.Resources, "ButtonForegroundPointerOver", closeForeground);
        _themeBrushes.Set(_titleBar.CloseButton.Resources, "ButtonForegroundPressed", closeForeground);

        return new PortableDocumentResolvedTheme(highContrast, surface, editor, accent, text, border);
    }

    internal IReadOnlyDictionary<string, string> BuildEditorCssPalette(NoteTheme theme)
    {
        if (!_accessibility.HighContrast)
        {
            IReadOnlyDictionary<string, string> css = NoteThemePalette.Get(theme).Css;
            Color editor = ParseHexColor(css["editor"]);
            Color rule = ParseHexColor(css["border"]);
            return new Dictionary<string, string>(css)
            {
                ["rule"] = $"rgba({rule.R}, {rule.G}, {rule.B}, 0.5)",
                ["caret"] = IsDark(editor) ? "#FFFFFF" : "#000000",
                ["scroll-thumb"] = Darken(editor, .22),
                ["scroll-thumb-hover"] = Darken(editor, .30)
            };
        }

        string foreground = ToHex(_uiSettings.GetColorValue(UIColorType.Foreground));
        string background = ToHex(_uiSettings.GetColorValue(UIColorType.Background));
        string accent = ToHex(_uiSettings.GetColorValue(UIColorType.Accent));
        return new Dictionary<string, string>
        {
            ["surface"] = background,
            ["editor"] = background,
            ["accent"] = accent,
            ["border"] = foreground,
            ["text"] = foreground,
            ["muted"] = foreground,
            ["rule"] = foreground,
            ["caret"] = foreground,
            ["scroll-thumb"] = foreground,
            ["scroll-thumb-hover"] = foreground
        };
    }

    internal void ShowError(string message)
    {
        _errorBar.Message = message;
        _errorBar.IsOpen = true;
    }

    internal void UpdateTitlePassthrough()
    {
        FrameworkElement target = _renaming ? _titleBar.TitleDragSurface : _titleBar.TitleText;
        if (_titleInput is null || _hwnd == IntPtr.Zero || target.ActualWidth <= 0 || target.ActualHeight <= 0) return;
        Rect bounds = target.TransformToVisual(_windowRoot)
            .TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        _titleInput.SetRegionRects(
            NonClientRegionKind.Passthrough,
            [new RectInt32(
                (int)Math.Floor(bounds.X * scale),
                (int)Math.Floor(bounds.Y * scale),
                Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(bounds.Height * scale)))]);
    }

    internal static PortableNotePlacement? ToPortablePlacement(NoteWindowPlacement? placement) => placement is null
        ? null
        : new PortableNotePlacement
        {
            MonitorDevice = placement.MonitorDevice,
            XDip = placement.XDip,
            YDip = placement.YDip,
            WidthDip = placement.WidthDip,
            HeightDip = placement.HeightDip
        };

    internal static NoteWindowPlacement ToNotePlacement(PortableNotePlacement placement) => new()
    {
        MonitorDevice = placement.MonitorDevice,
        XDip = placement.XDip,
        YDip = placement.YDip,
        WidthDip = placement.WidthDip,
        HeightDip = placement.HeightDip
    };

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _hideWindowAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("关闭便携文档窗口失败。", ex);
            ShowError(ex.Message);
        }
    }

    private void TitleDragSurface_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitlePassthrough();

    private void TitleText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        BeginRename();
        e.Handled = true;
    }

    private void BeginRename()
    {
        if (_renaming) return;
        _renaming = true;
        _errorBar.IsOpen = false;
        _titleBar.TitleEditor.Text = _getTitle();
        _titleBar.TitleText.Visibility = Visibility.Collapsed;
        _titleBar.TitleEditor.Visibility = Visibility.Visible;
        UpdateTitlePassthrough();
        _titleBar.TitleEditor.Focus(FocusState.Programmatic);
        _titleBar.TitleEditor.SelectAll();
    }

    private async void TitleEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelRename();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitRenameCoreAsync();
        }
    }

    private async void TitleEditor_LostFocus(object sender, RoutedEventArgs e) => await CommitRenameCoreAsync();

    private async Task CommitRenameCoreAsync()
    {
        if (!_renaming || _renameCommitInProgress) return;
        _renameCommitInProgress = true;
        try
        {
            await _commitRenameAsync(_titleBar.TitleEditor.Text);
            EndRename();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _renameCommitInProgress = false;
            if (_renaming)
            {
                _titleBar.TitleEditor.Focus(FocusState.Programmatic);
                _titleBar.TitleEditor.SelectAll();
            }
        }
    }

    private void CancelRename()
    {
        if (!_renaming) return;
        _errorBar.IsOpen = false;
        _titleBar.TitleEditor.Text = _getTitle();
        EndRename();
    }

    private void EndRename()
    {
        _renaming = false;
        _titleBar.TitleEditor.Visibility = Visibility.Collapsed;
        _titleBar.TitleText.Visibility = Visibility.Visible;
        UpdateTitlePassthrough();
    }

    private void RestorePlacement()
    {
        PortableNotePlacement placement = _getPlacement() ?? new PortableNotePlacement
        {
            WidthDip = DefaultWidthDip,
            HeightDip = DefaultHeightDip
        };
        DisplayInfo display = DisplayPlacementService.GetDisplay(placement.MonitorDevice);
        double clampedWidthDip = Math.Clamp(placement.WidthDip, MinimumWidthDip, MaximumWidthDip);
        double clampedHeightDip = Math.Clamp(placement.HeightDip, MinimumHeightDip, MaximumHeightDip);
        int width = Math.Max(1, (int)Math.Round(clampedWidthDip * display.Scale));
        int height = Math.Max(1, (int)Math.Round(clampedHeightDip * display.Scale));
        int left = string.IsNullOrWhiteSpace(placement.MonitorDevice)
            ? display.Work.Left + (display.Work.Width - width) / 2
            : display.Work.Left + (int)Math.Round(placement.XDip * display.Scale);
        int top = string.IsNullOrWhiteSpace(placement.MonitorDevice)
            ? display.Work.Top + (display.Work.Height - height) / 2
            : display.Work.Top + (int)Math.Round(placement.YDip * display.Scale);
        NativeMethods.RECT bounds = DisplayPlacementService.Clamp(new NativeMethods.RECT
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height
        }, display.Work);
        _restoringPlacement = true;
        try
        {
            _appWindow.MoveAndResize(new RectInt32(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
        }
        finally
        {
            _restoringPlacement = false;
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_restoringPlacement || (!args.DidPositionChange && !args.DidSizeChange)) return;
        if (args.DidSizeChange)
        {
            DisplayInfo currentDisplay = DisplayPlacementService.ForBounds(new NativeMethods.RECT
            {
                Left = sender.Position.X,
                Top = sender.Position.Y,
                Right = sender.Position.X + sender.Size.Width,
                Bottom = sender.Position.Y + sender.Size.Height
            });
            int width = Math.Clamp(sender.Size.Width,
                (int)Math.Round(MinimumWidthDip * currentDisplay.Scale),
                (int)Math.Round(MaximumWidthDip * currentDisplay.Scale));
            int height = Math.Clamp(sender.Size.Height,
                (int)Math.Round(MinimumHeightDip * currentDisplay.Scale),
                (int)Math.Round(MaximumHeightDip * currentDisplay.Scale));
            if (width != sender.Size.Width || height != sender.Size.Height)
            {
                _restoringPlacement = true;
                try
                {
                    sender.Resize(new SizeInt32(width, height));
                }
                finally
                {
                    _restoringPlacement = false;
                }
            }
        }
        if (!NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT bounds)) return;
        DisplayInfo display = DisplayPlacementService.ForBounds(bounds);
        _setPlacement(new PortableNotePlacement
        {
            MonitorDevice = display.Device,
            XDip = (bounds.Left - display.Work.Left) / display.Scale,
            YDip = (bounds.Top - display.Work.Top) / display.Scale,
            WidthDip = bounds.Width / display.Scale,
            HeightDip = bounds.Height / display.Scale
        });
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_permanentClose) return;
        args.Cancel = true;
        _ = _hideWindowAsync();
    }

    public void Dispose()
    {
        _titleBar.TitleDragSurface.SizeChanged -= TitleDragSurface_SizeChanged;
        _titleBar.TitleText.SizeChanged -= TitleDragSurface_SizeChanged;
        _titleBar.TitleText.Tapped -= TitleText_Tapped;
        _titleBar.TitleEditor.KeyDown -= TitleEditor_KeyDown;
        _titleBar.TitleEditor.LostFocus -= TitleEditor_LostFocus;
        _titleBar.CloseButton.Click -= CloseButton_Click;
        if (_initialized)
        {
            _appWindow.Changed -= AppWindow_Changed;
            _appWindow.Closing -= AppWindow_Closing;
        }
        _chrome?.Dispose();
        _chrome = null;
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private static bool IsDark(Color color) => color.R * 299 + color.G * 587 + color.B * 114 < 128000;
    private static string Darken(Color color, double amount) => ToHex(ColorHelper.FromArgb(255,
        (byte)Math.Round(color.R * (1 - amount)),
        (byte)Math.Round(color.G * (1 - amount)),
        (byte)Math.Round(color.B * (1 - amount))));
    private static Color ParseHexColor(string value) => ColorHelper.FromArgb(255,
        Convert.ToByte(value.Substring(1, 2), 16),
        Convert.ToByte(value.Substring(3, 2), 16),
        Convert.ToByte(value.Substring(5, 2), 16));
}
