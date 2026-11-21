using System.Diagnostics;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TuckPane.Core;
using TuckPane.Models;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace TuckPane.Services;

/// <summary>
/// Spotlight/Raycast-style instant search window that searches across all organizers'
/// top-level items on a background thread without mutating storage directories.
/// </summary>
internal sealed class OrganizerSearchWindow : Window
{
    private const double WindowWidthDip = 660;
    private const double WindowHeightDip = 456;

    private readonly AppHost _host;
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly TextBox _queryBox;
    private readonly Button _clearButton;
    private readonly Border _countBadge;
    private readonly TextBlock _countBadgeText;
    private readonly ListView _resultsList;
    private readonly StackPanel _emptyStatePanel;
    private readonly FontIcon _emptyStateIcon;
    private readonly TextBlock _statusText;
    private readonly HintWrapPanel _footerPanel;
    private readonly Border _cardBorder;
    private readonly List<OrganizerSearchEntry> _index = [];

    // Pre-allocated mutable brushes updated in-place on theme refresh (zero per-keystroke allocation).
    private readonly SolidColorBrush _transparentBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _cardBackgroundBrush = new(Color.FromArgb(248, 248, 249, 251));
    private readonly SolidColorBrush _cardBorderBrush = new(Color.FromArgb(40, 0, 0, 0));
    private readonly SolidColorBrush _dividerBrush = new(Color.FromArgb(24, 0, 0, 0));
    private readonly SolidColorBrush _primaryTextBrush = new(Color.FromArgb(255, 24, 26, 30));
    private readonly SolidColorBrush _secondaryTextBrush = new(Color.FromArgb(255, 104, 108, 118));
    private readonly SolidColorBrush _accentBrush = new(Color.FromArgb(255, 0, 103, 192));
    private readonly SolidColorBrush _badgeBackgroundBrush = new(Color.FromArgb(24, 0, 120, 212));
    private readonly SolidColorBrush _badgeTextBrush = new(Color.FromArgb(255, 0, 90, 170));
    private readonly SolidColorBrush _countBadgeBackgroundBrush = new(Color.FromArgb(14, 0, 0, 0));
    private readonly SolidColorBrush _kbdBackgroundBrush = new(Color.FromArgb(230, 255, 255, 255));
    private readonly SolidColorBrush _kbdBorderBrush = new(Color.FromArgb(36, 0, 0, 0));
    private readonly SolidColorBrush _kbdTextBrush = new(Color.FromArgb(255, 64, 68, 78));
    private readonly SolidColorBrush _clearForegroundBrush = new(Color.FromArgb(255, 104, 108, 118));
    private readonly SolidColorBrush _clearHoverForegroundBrush = new(Color.FromArgb(255, 24, 26, 30));
    private readonly SolidColorBrush _clearPressedForegroundBrush = new(Color.FromArgb(255, 24, 26, 30));
    private readonly SolidColorBrush _clearBorderBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _clearHoverBrush = new(Color.FromArgb(18, 0, 0, 0));
    private readonly SolidColorBrush _clearPressedBrush = new(Color.FromArgb(32, 0, 0, 0));
    private readonly SolidColorBrush _listItemHoverBrush = new(Color.FromArgb(14, 0, 0, 0));
    private readonly SolidColorBrush _listItemSelectedBrush = new(Color.FromArgb(24, 0, 103, 192));
    private readonly SolidColorBrush _listItemSelectedHoverBrush = new(Color.FromArgb(34, 0, 103, 192));
    private readonly SolidColorBrush _listItemPressedBrush = new(Color.FromArgb(42, 0, 103, 192));

    private BitmapImage? _noteAssetIcon;
    private BitmapImage? _todoAssetIcon;
    private BitmapImage? _organizerAssetIcon;

    private IReadOnlyList<OrganizerSearchHit> _currentHits = [];
    private IntPtr _hwnd;
    private AppWindow? _appWindow;
    private bool _usesBackdrop;
    private bool _initialized;
    private bool _closed;
    private bool _isIndexing;
    private bool _queryBoxLoaded;
    private bool _focusPendingAfterLoad;
    private bool _focusPendingOnActivate;
    private int _indexVersion;
    private int _renderVersion;
    private long _lastOpenedTimestamp;
    private bool _hasBeenActivatedSinceOpen;
    private IDisposable? _entranceBoost;
    private CompositionScopedBatch? _entranceBatch;

    internal OrganizerSearchWindow(AppHost host)
    {
        _host = host;
        Title = AppStrings.Get("SearchWindowTitle");

        try
        {
            if (DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
                _usesBackdrop = true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("搜索窗口启用系统半透明背景失败，将回退至纯色表面。", ex);
            _usesBackdrop = false;
        }

        _queryBox = new TextBox
        {
            PlaceholderText = AppStrings.Get("SearchPlaceholder"),
            FontSize = 16.5,
            BorderThickness = new Thickness(0),
            Background = _transparentBrush,
            Foreground = _primaryTextBrush,
            PlaceholderForeground = _secondaryTextBrush,
            Padding = new Thickness(6, 12, 6, 12),
            VerticalAlignment = VerticalAlignment.Center
        };
        ConfigureBorderlessQueryBoxResources(_queryBox);
        _queryBox.Loaded += QueryBox_Loaded;
        _queryBox.TextChanged += (_, _) =>
        {
            UpdateClearButtonVisibility();
            ApplyFilter();
        };
        _queryBox.PreviewKeyDown += QueryBox_PreviewKeyDown;

        _clearButton = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(13),
            Background = _transparentBrush,
            Foreground = _clearForegroundBrush,
            BorderThickness = new Thickness(1),
            BorderBrush = _clearBorderBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Content = new FontIcon
            {
                Glyph = "\uE711",
                FontSize = 10
            }
        };
        _clearButton.Resources["ButtonBackground"] = _transparentBrush;
        _clearButton.Resources["ButtonBackgroundPointerOver"] = _clearHoverBrush;
        _clearButton.Resources["ButtonBackgroundPressed"] = _clearPressedBrush;
        _clearButton.Resources["ButtonForeground"] = _clearForegroundBrush;
        _clearButton.Resources["ButtonForegroundPointerOver"] = _clearHoverForegroundBrush;
        _clearButton.Resources["ButtonForegroundPressed"] = _clearPressedForegroundBrush;
        _clearButton.Resources["ButtonBorderBrush"] = _clearBorderBrush;
        _clearButton.Resources["ButtonBorderBrushPointerOver"] = _clearBorderBrush;
        _clearButton.Resources["ButtonBorderBrushPressed"] = _clearBorderBrush;
        _clearButton.Click += (_, _) =>
        {
            _queryBox.Text = string.Empty;
            RequestQueryBoxFocus();
        };

        _countBadgeText = new TextBlock
        {
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = _secondaryTextBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        _countBadge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(6, 0, 14, 0),
            Background = _countBadgeBackgroundBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = _countBadgeText
        };

        var searchGlyph = new FontIcon
        {
            Glyph = "\uE721",
            FontSize = 16,
            Foreground = _secondaryTextBrush,
            Margin = new Thickness(16, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var headerGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        Grid.SetColumn(searchGlyph, 0);
        Grid.SetColumn(_queryBox, 1);
        Grid.SetColumn(_clearButton, 2);
        Grid.SetColumn(_countBadge, 3);
        headerGrid.Children.Add(searchGlyph);
        headerGrid.Children.Add(_queryBox);
        headerGrid.Children.Add(_clearButton);
        headerGrid.Children.Add(_countBadge);

        var headerBorder = new Border
        {
            Padding = new Thickness(0, 4, 0, 4),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _dividerBrush,
            Child = headerGrid
        };

        var itemContainerStyle = new Style(typeof(ListViewItem));
        itemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemContainerStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        itemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        itemContainerStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2, 2, 2, 2)));
        itemContainerStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 38.0));
        itemContainerStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));

        _resultsList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            Padding = new Thickness(8, 6, 8, 6),
            ItemContainerStyle = itemContainerStyle
        };
        _resultsList.Resources["ListViewItemCornerRadius"] = new CornerRadius(8);
        _resultsList.Resources["ListViewItemBackgroundPointerOver"] = _listItemHoverBrush;
        _resultsList.Resources["ListViewItemBackgroundPressed"] = _listItemPressedBrush;
        _resultsList.Resources["ListViewItemBackgroundSelected"] = _listItemSelectedBrush;
        _resultsList.Resources["ListViewItemBackgroundSelectedPointerOver"] = _listItemSelectedHoverBrush;
        _resultsList.Resources["ListViewItemBackgroundSelectedPressed"] = _listItemPressedBrush;
        _resultsList.Resources["ListViewItemSelectionIndicatorBrush"] = _accentBrush;
        _resultsList.Resources["ListViewItemSelectionIndicatorPointerOverBrush"] = _accentBrush;
        _resultsList.Resources["ListViewItemSelectionIndicatorPressedBrush"] = _accentBrush;
        _resultsList.ItemClick += async (_, e) =>
        {
            if (e.ClickedItem is FrameworkElement { Tag: OrganizerSearchHit hit })
                await ExecuteSelectedAsync(hit, revealInExplorer: false, jumpToOrganizer: false);
        };
        _resultsList.KeyDown += ResultsList_KeyDown;

        _emptyStateIcon = new FontIcon
        {
            Glyph = "\uE721",
            FontSize = 26,
            Foreground = _secondaryTextBrush,
            Opacity = 0.55,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _statusText = new TextBlock
        {
            FontSize = 13,
            Foreground = _secondaryTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Text = AppStrings.Get("SearchIndexing")
        };
        _emptyStatePanel = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Visible,
            Children = { _emptyStateIcon, _statusText }
        };

        var resultsHost = new Grid
        {
            Children = { _resultsList, _emptyStatePanel }
        };

        _footerPanel = new HintWrapPanel
        {
            HorizontalSpacing = 10,
            VerticalSpacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        var footerBorder = new Border
        {
            Padding = new Thickness(14, 8, 14, 9),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = _dividerBrush,
            Child = _footerPanel
        };

        var rootGrid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };
        Grid.SetRow(headerBorder, 0);
        Grid.SetRow(resultsHost, 1);
        Grid.SetRow(footerBorder, 2);
        rootGrid.Children.Add(headerBorder);
        rootGrid.Children.Add(resultsHost);
        rootGrid.Children.Add(footerBorder);

        bool nativeRounded = NativeMethods.SupportsWindows11DwmAttributes;
        _cardBorder = new Border
        {
            CornerRadius = new CornerRadius(nativeRounded ? 0 : 12),
            BorderThickness = new Thickness(nativeRounded ? 0 : 1),
            BorderBrush = _cardBorderBrush,
            Background = _cardBackgroundBrush,
            Child = rootGrid
        };
        _cardBorder.PreviewKeyDown += CardBorder_PreviewKeyDown;

        Content = _cardBorder;
        Activated += OrganizerSearchWindow_Activated;
        Closed += (_, _) =>
        {
            _closed = true;
            _focusPendingAfterLoad = false;
            _focusPendingOnActivate = false;
            _queryBox.Loaded -= QueryBox_Loaded;
            _cardBorder.PreviewKeyDown -= CardBorder_PreviewKeyDown;
            ++_renderVersion;
            CancelEntranceAnimation();
        };
    }

    internal void OpenAndFocus()
    {
        if (_closed) return;
        EnsureInitialized();
        ApplyLanguageAndTheme();
        PositionOnCursorMonitor();
        _queryBox.Text = string.Empty;
        UpdateClearButtonVisibility();
        _lastOpenedTimestamp = Stopwatch.GetTimestamp();
        _hasBeenActivatedSinceOpen = false;
        _focusPendingOnActivate = true;

        bool shouldAnimate = PrepareEntranceAnimationState();
        _appWindow?.Show(activateWindow: true);
        Activate();
        _ = NativeMethods.SetForegroundWindow(_hwnd);
        if (shouldAnimate)
            StartEntranceAnimation();
        RequestQueryBoxFocus();
        _ = RefreshIndexAsync(++_indexVersion);
    }

    internal void Dismiss()
    {
        if (_closed) return;
        _focusPendingAfterLoad = false;
        _focusPendingOnActivate = false;
        ++_renderVersion;
        CancelEntranceAnimation();
        _appWindow?.Hide();
    }

    internal void ClosePermanently()
    {
        if (_closed) return;
        _closed = true;
        ++_renderVersion;
        CancelEntranceAnimation();
        Close();
    }

    private void ConfigureBorderlessQueryBoxResources(TextBox box)
    {
        box.Resources["TextControlBackground"] = _transparentBrush;
        box.Resources["TextControlBackgroundPointerOver"] = _transparentBrush;
        box.Resources["TextControlBackgroundFocused"] = _transparentBrush;
        box.Resources["TextControlBackgroundDisabled"] = _transparentBrush;
        box.Resources["TextControlBorderBrush"] = _transparentBrush;
        box.Resources["TextControlBorderBrushPointerOver"] = _transparentBrush;
        box.Resources["TextControlBorderBrushFocused"] = _transparentBrush;
        box.Resources["TextControlBorderBrushDisabled"] = _transparentBrush;
        box.Resources["TextControlForeground"] = _primaryTextBrush;
        box.Resources["TextControlForegroundPointerOver"] = _primaryTextBrush;
        box.Resources["TextControlForegroundFocused"] = _primaryTextBrush;
        box.Resources["TextControlPlaceholderForeground"] = _secondaryTextBrush;
        box.Resources["TextControlPlaceholderForegroundPointerOver"] = _secondaryTextBrush;
        box.Resources["TextControlPlaceholderForegroundFocused"] = _secondaryTextBrush;
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        try { _appWindow.IsShownInSwitchers = false; }
        catch { /* Fallback to WS_EX_TOOLWINDOW below on builds where SetShownInSwitchers is unsupported. */ }
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            // Keep native Windows 11 thin border + drop shadow while removing the titlebar.
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        if (NativeMethods.SupportsWindows11DwmAttributes)
        {
            int cornerPref = NativeMethods.DWMWCP_ROUND;
            _ = NativeMethods.DwmSetWindowAttribute(
                _hwnd,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPref,
                sizeof(int));
        }
        long exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE,
            new IntPtr((exStyle | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW));
        _appWindow.Closing += (_, args) =>
        {
            if (_closed) return;
            args.Cancel = true;
            Dismiss();
        };
    }

    private void ApplyLanguageAndTheme()
    {
        Title = AppStrings.Get("SearchWindowTitle");
        _queryBox.PlaceholderText = AppStrings.Get("SearchPlaceholder");

        bool highContrast = _accessibilitySettings.HighContrast;
        Color systemBg = _uiSettings.GetColorValue(UIColorType.Background);
        bool dark = (0.299 * systemBg.R + 0.587 * systemBg.G + 0.114 * systemBg.B) < 128.0;
        _cardBorder.RequestedTheme = highContrast
            ? ElementTheme.Default
            : dark ? ElementTheme.Dark : ElementTheme.Light;

        if (highContrast)
        {
            Color windowBg = _uiSettings.UIElementColor(UIElementType.Window);
            Color windowText = _uiSettings.UIElementColor(UIElementType.WindowText);
            Color grayText = _uiSettings.UIElementColor(UIElementType.GrayText);
            Color accent = _uiSettings.UIElementColor(UIElementType.Highlight);
            Color highlightText = _uiSettings.UIElementColor(UIElementType.HighlightText);

            _cardBackgroundBrush.Color = windowBg;
            _cardBorderBrush.Color = windowText;
            _dividerBrush.Color = windowText;
            _primaryTextBrush.Color = windowText;
            _secondaryTextBrush.Color = grayText;
            _accentBrush.Color = accent;
            _badgeBackgroundBrush.Color = accent;
            _badgeTextBrush.Color = highlightText;
            _countBadgeBackgroundBrush.Color = windowBg;
            _kbdBackgroundBrush.Color = windowBg;
            _kbdBorderBrush.Color = windowText;
            _kbdTextBrush.Color = windowText;
            _clearForegroundBrush.Color = windowText;
            _clearHoverForegroundBrush.Color = highlightText;
            _clearPressedForegroundBrush.Color = highlightText;
            _clearBorderBrush.Color = windowText;
            _clearHoverBrush.Color = accent;
            _clearPressedBrush.Color = accent;
            _listItemHoverBrush.Color = Color.FromArgb(36, accent.R, accent.G, accent.B);
            _listItemSelectedBrush.Color = Color.FromArgb(56, accent.R, accent.G, accent.B);
            _listItemSelectedHoverBrush.Color = Color.FromArgb(72, accent.R, accent.G, accent.B);
            _listItemPressedBrush.Color = Color.FromArgb(88, accent.R, accent.G, accent.B);
        }
        else if (dark)
        {
            _cardBackgroundBrush.Color = _usesBackdrop
                ? Color.FromArgb(166, 28, 30, 36)
                : Color.FromArgb(250, 28, 30, 36);
            _cardBorderBrush.Color = Color.FromArgb(52, 255, 255, 255);
            _dividerBrush.Color = Color.FromArgb(28, 255, 255, 255);
            _primaryTextBrush.Color = Color.FromArgb(255, 244, 246, 250);
            _secondaryTextBrush.Color = Color.FromArgb(255, 166, 173, 186);
            _accentBrush.Color = Color.FromArgb(255, 96, 178, 255);
            _badgeBackgroundBrush.Color = Color.FromArgb(38, 96, 178, 255);
            _badgeTextBrush.Color = Color.FromArgb(255, 178, 218, 255);
            _countBadgeBackgroundBrush.Color = Color.FromArgb(24, 255, 255, 255);
            _kbdBackgroundBrush.Color = Color.FromArgb(26, 255, 255, 255);
            _kbdBorderBrush.Color = Color.FromArgb(48, 255, 255, 255);
            _kbdTextBrush.Color = Color.FromArgb(255, 212, 217, 226);
            _clearForegroundBrush.Color = _secondaryTextBrush.Color;
            _clearHoverForegroundBrush.Color = _primaryTextBrush.Color;
            _clearPressedForegroundBrush.Color = _primaryTextBrush.Color;
            _clearBorderBrush.Color = Colors.Transparent;
            _clearHoverBrush.Color = Color.FromArgb(28, 255, 255, 255);
            _clearPressedBrush.Color = Color.FromArgb(46, 255, 255, 255);
            _listItemHoverBrush.Color = Color.FromArgb(20, 255, 255, 255);
            _listItemSelectedBrush.Color = Color.FromArgb(36, 96, 178, 255);
            _listItemSelectedHoverBrush.Color = Color.FromArgb(48, 96, 178, 255);
            _listItemPressedBrush.Color = Color.FromArgb(56, 96, 178, 255);
        }
        else
        {
            _cardBackgroundBrush.Color = _usesBackdrop
                ? Color.FromArgb(182, 248, 249, 252)
                : Color.FromArgb(252, 248, 249, 252);
            _cardBorderBrush.Color = Color.FromArgb(34, 0, 0, 0);
            _dividerBrush.Color = Color.FromArgb(20, 0, 0, 0);
            _primaryTextBrush.Color = Color.FromArgb(255, 24, 26, 30);
            _secondaryTextBrush.Color = Color.FromArgb(255, 102, 108, 118);
            _accentBrush.Color = Color.FromArgb(255, 0, 103, 192);
            _badgeBackgroundBrush.Color = Color.FromArgb(22, 0, 120, 212);
            _badgeTextBrush.Color = Color.FromArgb(255, 0, 90, 170);
            _countBadgeBackgroundBrush.Color = Color.FromArgb(14, 0, 0, 0);
            _kbdBackgroundBrush.Color = Color.FromArgb(236, 255, 255, 255);
            _kbdBorderBrush.Color = Color.FromArgb(34, 0, 0, 0);
            _kbdTextBrush.Color = Color.FromArgb(255, 64, 68, 78);
            _clearForegroundBrush.Color = _secondaryTextBrush.Color;
            _clearHoverForegroundBrush.Color = _primaryTextBrush.Color;
            _clearPressedForegroundBrush.Color = _primaryTextBrush.Color;
            _clearBorderBrush.Color = Colors.Transparent;
            _clearHoverBrush.Color = Color.FromArgb(16, 0, 0, 0);
            _clearPressedBrush.Color = Color.FromArgb(30, 0, 0, 0);
            _listItemHoverBrush.Color = Color.FromArgb(14, 0, 0, 0);
            _listItemSelectedBrush.Color = Color.FromArgb(24, 0, 103, 192);
            _listItemSelectedHoverBrush.Color = Color.FromArgb(34, 0, 103, 192);
            _listItemPressedBrush.Color = Color.FromArgb(42, 0, 103, 192);
        }

        RebuildFooterKeycaps();
    }

    private void RebuildFooterKeycaps()
    {
        _footerPanel.Children.Clear();
        string hint = AppStrings.Get("SearchFooterHint");
        string[] segments = hint.Split(['·', '•'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string segment in segments)
        {
            int splitIndex = segment.IndexOf(' ');
            if (splitIndex <= 0 || splitIndex >= segment.Length - 1)
            {
                _footerPanel.Children.Add(new TextBlock
                {
                    Text = segment,
                    FontSize = 11,
                    Foreground = _secondaryTextBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
                continue;
            }

            string keyCap = segment[..splitIndex];
            string actionLabel = segment[(splitIndex + 1)..].Trim();

            var kbd = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = _kbdBorderBrush,
                Background = _kbdBackgroundBrush,
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = keyCap,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _kbdTextBrush,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            var label = new TextBlock
            {
                Text = actionLabel,
                FontSize = 11,
                Foreground = _secondaryTextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { kbd, label }
            };
            _footerPanel.Children.Add(item);
        }
    }

    private bool PrepareEntranceAnimationState()
    {
        CancelEntranceAnimation();
        Visual visual = ElementCompositionPreview.GetElementVisual(_cardBorder);
        visual.StopAnimation("Scale");
        visual.StopAnimation("Opacity");

        if (!_host.State.GlobalSettings.ShouldUseCustomAnimations(_uiSettings.AnimationsEnabled))
        {
            visual.Scale = Vector3.One;
            visual.Opacity = 1f;
            return false;
        }

        float width = (float)(_cardBorder.ActualWidth > 0 ? _cardBorder.ActualWidth : WindowWidthDip);
        float height = (float)(_cardBorder.ActualHeight > 0 ? _cardBorder.ActualHeight : WindowHeightDip);
        visual.CenterPoint = new Vector3(width * 0.5f, height * 0.35f, 0f);
        visual.Scale = new Vector3(0.96f, 0.96f, 1f);
        visual.Opacity = 0f;
        return true;
    }

    private void StartEntranceAnimation()
    {
        CancelEntranceAnimation();
        Visual visual = ElementCompositionPreview.GetElementVisual(_cardBorder);
        Compositor compositor = visual.Compositor;
        _entranceBoost = CompositorClockBoost.Acquire();
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _entranceBatch = batch;
        batch.Completed += EntranceBatch_Completed;

        SpringVector3NaturalMotionAnimation scaleSpring = compositor.CreateSpringVector3Animation();
        scaleSpring.InitialValue = new Vector3(0.96f, 0.96f, 1f);
        scaleSpring.FinalValue = Vector3.One;
        scaleSpring.DampingRatio = 0.86f;
        scaleSpring.Period = TimeSpan.FromMilliseconds(54);
        visual.StartAnimation("Scale", scaleSpring);

        CubicBezierEasingFunction easeOut = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1.0f), new Vector2(0.30f, 1.0f));
        ScalarKeyFrameAnimation opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(0f, 0f);
        opacityAnim.InsertKeyFrame(1f, 1f, easeOut);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(150);
        visual.StartAnimation("Opacity", opacityAnim);

        batch.End();
    }

    private void QueryBox_Loaded(object sender, RoutedEventArgs e)
    {
        _queryBoxLoaded = true;
        if (!_focusPendingAfterLoad || _closed) return;
        _focusPendingAfterLoad = false;
        _ = DispatcherQueue.TryEnqueue(FocusQueryBoxNow);
    }

    private void RequestQueryBoxFocus()
    {
        if (_closed) return;
        if (!_queryBoxLoaded && !_queryBox.IsLoaded)
        {
            _focusPendingAfterLoad = true;
            return;
        }
        _queryBoxLoaded = true;
        _focusPendingAfterLoad = false;
        if (!_queryBox.Focus(FocusState.Programmatic))
            _ = DispatcherQueue.TryEnqueue(FocusQueryBoxNow);
        else
            _queryBox.SelectionStart = _queryBox.Text.Length;
    }

    private void FocusQueryBoxNow()
    {
        if (_closed || _appWindow is null || !_appWindow.IsVisible) return;
        _ = _queryBox.Focus(FocusState.Programmatic);
        _queryBox.SelectionStart = _queryBox.Text.Length;
    }

    private void EntranceBatch_Completed(object sender, CompositionBatchCompletedEventArgs args)
    {
        if (ReferenceEquals(_entranceBatch, sender))
            CancelEntranceAnimation();
    }

    private void CancelEntranceAnimation()
    {
        if (_entranceBatch is not null)
        {
            _entranceBatch.Completed -= EntranceBatch_Completed;
            _entranceBatch.Dispose();
            _entranceBatch = null;
        }
        _entranceBoost?.Dispose();
        _entranceBoost = null;
    }

    private void PositionOnCursorMonitor()
    {
        DisplayInfo display = NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)
            ? DisplayPlacementService.ForBounds(new NativeMethods.RECT
            {
                Left = cursor.X, Top = cursor.Y, Right = cursor.X + 1, Bottom = cursor.Y + 1
            })
            : DisplayPlacementService.GetDisplay();

        double scale = Math.Max(1.0, display.Scale);
        int width = Math.Min(display.Work.Width - 32, Math.Max(360, (int)Math.Round(WindowWidthDip * scale)));
        int height = Math.Min(display.Work.Height - 48, Math.Max(280, (int)Math.Round(WindowHeightDip * scale)));
        int left = display.Work.Left + (display.Work.Width - width) / 2;
        int top = display.Work.Top + Math.Max(24, (display.Work.Height - height) / 4);

        _ = NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            left,
            top,
            width,
            height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    private void UpdateClearButtonVisibility()
    {
        _clearButton.Visibility = string.IsNullOrEmpty(_queryBox.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async Task RefreshIndexAsync(int version)
    {
        _isIndexing = true;
        if (_index.Count == 0)
        {
            _statusText.Text = AppStrings.Get("SearchIndexing");
            _emptyStatePanel.Visibility = Visibility.Visible;
            _countBadge.Visibility = Visibility.Collapsed;
        }
        (Guid Id, string Name, string Path, IReadOnlyList<NoteDefinition> Notes)[] snapshot = _host.State.Organizers
            .Select(o => (o.Id, o.Name, AppPaths.ResolveStoragePath(o), (IReadOnlyList<NoteDefinition>)o.Notes.ToArray()))
            .ToArray();

        List<OrganizerSearchEntry> entries = await Task.Run(() =>
        {
            var collected = new List<OrganizerSearchEntry>(256);
            foreach (var organizer in snapshot)
            {
                try
                {
                    if (Directory.Exists(organizer.Path))
                    {
                        var storage = new StorageService(organizer.Path, createIfMissing: false);
                        foreach (WidgetItem item in storage.ReadItems())
                        {
                            collected.Add(new OrganizerSearchEntry(
                                organizer.Id,
                                organizer.Name,
                                item.Name,
                                item.RelativeName,
                                item.FullPath,
                                item.Kind,
                                item.NoteId));
                        }
                    }
                    foreach (NoteDefinition note in organizer.Notes)
                    {
                        collected.Add(new OrganizerSearchEntry(
                            organizer.Id,
                            organizer.Name,
                            note.Name,
                            note.Id.ToString("N"),
                            organizer.Path,
                            WidgetItemKind.Note,
                            note.Id));
                    }
                }
                catch
                {
                    // Skip unreadable or missing organizer folders gracefully.
                }
            }
            return collected;
        });

        if (_closed || version != _indexVersion) return;
        _isIndexing = false;
        _index.Clear();
        _index.AddRange(entries);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        int renderVersion = ++_renderVersion;
        string query = _queryBox.Text ?? string.Empty;
        string[] queryTokens = OrganizerSearch.Tokenize(query);
        _currentHits = OrganizerSearch.Search(_index, query, OrganizerSearch.DefaultMaxResults);
        _resultsList.Items.Clear();

        foreach (OrganizerSearchHit hit in _currentHits)
        {
            var row = new Grid
            {
                Padding = new Thickness(10, 7, 10, 7),
                ColumnSpacing = 12,
                Tag = hit,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(26) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };

            FrameworkElement iconHost = BuildRowIcon(hit.Entry, renderVersion);
            TextBlock title = BuildHighlightedTitle(hit.Entry.ItemName, queryTokens);

            var badge = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 2, 8, 2),
                Background = _badgeBackgroundBrush,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 160,
                Child = new TextBlock
                {
                    Text = hit.Entry.OrganizerName,
                    FontSize = 11,
                    FontWeight = FontWeights.Medium,
                    Foreground = _badgeTextBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1
                }
            };
            if (hit.Entry.OrganizerName.Length > 12)
                ToolTipService.SetToolTip(badge, hit.Entry.OrganizerName);

            Grid.SetColumn(iconHost, 0);
            Grid.SetColumn(title, 1);
            Grid.SetColumn(badge, 2);
            row.Children.Add(iconHost);
            row.Children.Add(title);
            row.Children.Add(badge);
            _resultsList.Items.Add(row);
        }

        if (_resultsList.Items.Count > 0)
            _resultsList.SelectedIndex = 0;

        if (_currentHits.Count == 0)
        {
            _statusText.Text = _isIndexing ? AppStrings.Get("SearchIndexing") : AppStrings.Get("SearchNoResults");
            _emptyStatePanel.Visibility = Visibility.Visible;
            _countBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            _emptyStatePanel.Visibility = Visibility.Collapsed;
            _countBadgeText.Text = AppStrings.Format("SearchResultCountFormat", _currentHits.Count, _index.Count);
            _countBadge.Visibility = Visibility.Visible;
        }
    }

    private FrameworkElement BuildRowIcon(OrganizerSearchEntry entry, int renderVersion)
    {
        var slot = new Grid
        {
            Width = 26,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        BitmapImage? builtIn = GetBuiltInAssetIcon(entry.Kind);
        if (builtIn is not null)
        {
            slot.Children.Add(new Image
            {
                Source = builtIn,
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform
            });
            return slot;
        }

        var fallback = new FontIcon
        {
            Glyph = GlyphForKind(entry.Kind),
            FontSize = 15,
            Foreground = _secondaryTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var image = new Image
        {
            Width = 26,
            Height = 26,
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed
        };
        slot.Children.Add(fallback);
        slot.Children.Add(image);

        if (!string.IsNullOrWhiteSpace(entry.FullPath))
            _ = LoadRowShellIconAsync(entry.FullPath, image, fallback, renderVersion);

        return slot;
    }

    private BitmapImage? GetBuiltInAssetIcon(WidgetItemKind kind) => kind switch
    {
        WidgetItemKind.Note or WidgetItemKind.PortableNote =>
            _noteAssetIcon ??= new BitmapImage(new Uri("ms-appx:///Assets/Note.png")),
        WidgetItemKind.PortableTodo =>
            _todoAssetIcon ??= new BitmapImage(new Uri("ms-appx:///Assets/Todo.png")),
        WidgetItemKind.Organizer =>
            _organizerAssetIcon ??= new BitmapImage(new Uri("ms-appx:///Assets/TuckPane.png")),
        _ => null
    };

    private async Task LoadRowShellIconAsync(string path, Image targetImage, FontIcon fallbackIcon, int renderVersion)
    {
        try
        {
            BitmapImage? bitmap = await _host.SharedIconCache.GetIconAsync(path);
            if (_closed || renderVersion != _renderVersion || bitmap is null) return;
            targetImage.Source = bitmap;
            targetImage.Visibility = Visibility.Visible;
            fallbackIcon.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // Keep clean FontIcon fallback if shell icon extraction fails.
        }
    }

    private TextBlock BuildHighlightedTitle(string itemName, string[] tokens)
    {
        var title = new TextBlock
        {
            FontSize = 14,
            Foreground = _primaryTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (tokens.Length == 0 || string.IsNullOrEmpty(itemName))
        {
            title.Text = itemName;
            return title;
        }

        List<(int Start, int End)>? ranges = null;
        foreach (string token in tokens)
        {
            if (token.Length == 0) continue;
            int searchFrom = 0;
            while (searchFrom < itemName.Length)
            {
                int found = itemName.IndexOf(token, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                ranges ??= [];
                ranges.Add((found, found + token.Length));
                searchFrom = found + token.Length;
            }
        }

        if (ranges is null || ranges.Count == 0)
        {
            title.Text = itemName;
            return title;
        }

        ranges.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        int cursor = 0;
        foreach (var (start, end) in ranges)
        {
            int clampedStart = Math.Max(start, cursor);
            if (clampedStart >= end) continue;
            if (clampedStart > cursor)
            {
                title.Inlines.Add(new Run { Text = itemName[cursor..clampedStart] });
            }
            title.Inlines.Add(new Run
            {
                Text = itemName[clampedStart..end],
                FontWeight = FontWeights.SemiBold,
                Foreground = _accentBrush
            });
            cursor = end;
        }
        if (cursor < itemName.Length)
        {
            title.Inlines.Add(new Run { Text = itemName[cursor..] });
        }
        return title;
    }

    private void CardBorder_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (IsDescendantOrSelf(e.OriginalSource as DependencyObject, _queryBox) &&
                !string.IsNullOrEmpty(_queryBox.Text))
            {
                _queryBox.Text = string.Empty;
                return;
            }
            Dismiss();
            return;
        }
        if (e.Key == VirtualKey.C &&
            (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0 &&
            (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0)
        {
            if (TryCopySelectedPath())
            {
                e.Handled = true;
            }
        }
    }

    private bool TryCopySelectedPath()
    {
        int index = _resultsList.SelectedIndex;
        if (index < 0 || index >= _currentHits.Count || _currentHits[index].Entry.Kind == WidgetItemKind.Note)
            return false;
        try
        {
            ShellItemCommands.CopyPathToClipboard(_currentHits[index].Entry.FullPath);
            Dismiss();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"全局搜索复制路径到剪贴板失败：{_currentHits[index].Entry.FullPath}", ex);
            _host.ReportOperationError(AppStrings.Get("SearchWindowTitle"), ex.Message);
        }
        return true;
    }

    private static bool IsDescendantOrSelf(DependencyObject? element, DependencyObject ancestor)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, ancestor)) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private async void QueryBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down && _resultsList.Items.Count > 0)
        {
            e.Handled = true;
            int next = Math.Min(_resultsList.Items.Count - 1, _resultsList.SelectedIndex + 1);
            _resultsList.SelectedIndex = next;
            _resultsList.ScrollIntoView(_resultsList.Items[next]);
            return;
        }
        if (e.Key == VirtualKey.Up && _resultsList.Items.Count > 0)
        {
            e.Handled = true;
            int prev = Math.Max(0, _resultsList.SelectedIndex - 1);
            _resultsList.SelectedIndex = prev;
            _resultsList.ScrollIntoView(_resultsList.Items[prev]);
            return;
        }
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await LaunchCurrentSelectionAsync();
        }
    }

    private async void ResultsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await LaunchCurrentSelectionAsync();
        }
    }

    private Task LaunchCurrentSelectionAsync()
    {
        int index = _resultsList.SelectedIndex;
        if (index < 0 || index >= _currentHits.Count) return Task.CompletedTask;
        bool ctrl = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
        bool shift = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0; // VK_SHIFT
        return ExecuteSelectedAsync(_currentHits[index], revealInExplorer: ctrl, jumpToOrganizer: shift);
    }

    private async Task ExecuteSelectedAsync(OrganizerSearchHit hit, bool revealInExplorer, bool jumpToOrganizer)
    {
        Dismiss();
        try
        {
            if (jumpToOrganizer)
            {
                await _host.JumpToOrganizerAsync(hit.Entry.OrganizerId);
                return;
            }
            if (revealInExplorer && hit.Entry.Kind != WidgetItemKind.Note)
            {
                ShellItemCommands.RevealInExplorer(hit.Entry.FullPath);
                return;
            }
            if (hit.Entry.Kind == WidgetItemKind.PortableNote)
            {
                await _host.OpenExternalNoteAsync(hit.Entry.FullPath);
                return;
            }
            if (hit.Entry.Kind == WidgetItemKind.PortableTodo)
            {
                await _host.OpenExternalTodoAsync(hit.Entry.FullPath);
                return;
            }
            if (hit.Entry.Kind == WidgetItemKind.Note && hit.Entry.NoteId is Guid noteId)
            {
                _host.OpenNote(hit.Entry.OrganizerId, noteId);
                return;
            }
            await _host.ShellLauncher.OpenAsync(hit.Entry.FullPath);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"全局搜索执行项目失败：{hit.Entry.FullPath}", ex);
            _host.ReportOperationError(AppStrings.Get("SearchWindowTitle"), ex.Message);
        }
    }

    private void OrganizerSearchWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _hasBeenActivatedSinceOpen = true;
            if (_focusPendingOnActivate)
            {
                _focusPendingOnActivate = false;
                RequestQueryBoxFocus();
            }
            return;
        }
        if (!_hasBeenActivatedSinceOpen ||
            Stopwatch.GetElapsedTime(_lastOpenedTimestamp) < TimeSpan.FromMilliseconds(350))
        {
            return;
        }
        Dismiss();
    }

    private static string GlyphForKind(WidgetItemKind kind) => kind switch
    {
        WidgetItemKind.Folder => "\uE8B7",
        WidgetItemKind.PortableNote or WidgetItemKind.Note => "\uE70B",
        WidgetItemKind.PortableTodo => "\uE73A",
        WidgetItemKind.Shortcut or WidgetItemKind.InternetShortcut => "\uE71B",
        WidgetItemKind.Organizer => "\uE8F1",
        _ => "\uE8A5"
    };

    private sealed class HintWrapPanel : Panel
    {
        public double HorizontalSpacing { get; set; } = 10;
        public double VerticalSpacing { get; set; } = 6;

        protected override Size MeasureOverride(Size availableSize)
        {
            double maxWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
            double currentX = 0;
            double currentY = 0;
            double lineHeight = 0;
            double totalWidth = 0;

            foreach (UIElement child in Children)
            {
                child.Measure(new Size(maxWidth, double.PositiveInfinity));
                Size desired = child.DesiredSize;
                if (currentX > 0 && currentX + HorizontalSpacing + desired.Width > maxWidth)
                {
                    totalWidth = Math.Max(totalWidth, currentX);
                    currentY += lineHeight + VerticalSpacing;
                    currentX = 0;
                    lineHeight = 0;
                }
                if (currentX > 0) currentX += HorizontalSpacing;
                currentX += desired.Width;
                lineHeight = Math.Max(lineHeight, desired.Height);
            }

            totalWidth = Math.Max(totalWidth, currentX);
            double totalHeight = currentY + lineHeight;
            return new Size(
                double.IsInfinity(availableSize.Width) ? totalWidth : Math.Min(totalWidth, availableSize.Width),
                double.IsInfinity(availableSize.Height) ? totalHeight : Math.Min(totalHeight, availableSize.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double maxWidth = finalSize.Width;
            double currentX = 0;
            double currentY = 0;
            double lineHeight = 0;

            foreach (UIElement child in Children)
            {
                Size desired = child.DesiredSize;
                if (currentX > 0 && currentX + HorizontalSpacing + desired.Width > maxWidth)
                {
                    currentY += lineHeight + VerticalSpacing;
                    currentX = 0;
                    lineHeight = 0;
                }
                if (currentX > 0) currentX += HorizontalSpacing;
                double arrangeWidth = Math.Max(0, Math.Min(desired.Width, maxWidth - currentX));
                child.Arrange(new Rect(currentX, currentY, arrangeWidth, desired.Height));
                currentX += arrangeWidth;
                lineHeight = Math.Max(lineHeight, desired.Height);
            }

            return new Size(finalSize.Width, Math.Max(finalSize.Height, currentY + lineHeight));
        }
    }
}
