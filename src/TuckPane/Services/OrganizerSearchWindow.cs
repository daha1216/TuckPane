using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Models;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace TuckPane.Services;

/// <summary>
/// "Find in organizers": a launcher-style window that searches the top-level items of every
/// organizer at once. Enter opens the selected item, Ctrl+Enter shows it in its folder, Esc or
/// switching away closes the window. Organizer panes themselves never take keyboard focus, so
/// this is the keyboard entry point to their contents.
/// </summary>
internal sealed class OrganizerSearchWindow : Window
{
    private readonly AppHost _host;
    private readonly Grid _root;
    private readonly ThemeBackdrop _backdrop = new();
    private readonly ThemeSurface _surface;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly TextBox _query;
    private readonly ListView _results;
    private readonly TextBlock _status;
    private readonly TextBlock _hint;
    private IReadOnlyList<OrganizerSearchCandidate> _candidates = [];
    private bool _loaded;
    private bool _closed;
    private bool _launching;

    internal OrganizerSearchWindow(AppHost host, DisplayInfo display)
    {
        _host = host;
        Title = AppStrings.Get("SearchWindowTitle");
        _root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        var surfaceHost = new SystemBackdropElement { IsHitTestVisible = false };
        var content = new Grid { Padding = new Thickness(16, 14, 16, 12), RowSpacing = 10 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _query = new TextBox
        {
            PlaceholderText = AppStrings.Get("SearchPlaceholder"),
            FontSize = 16,
            IsSpellCheckEnabled = false
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_query, "OrganizerSearchBox");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_query, AppStrings.Get("SearchPlaceholder"));
        _query.TextChanged += (_, _) => Refresh();
        _query.PreviewKeyDown += Query_PreviewKeyDown;

        _results = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_results, "OrganizerSearchResults");
        _results.ItemClick += async (_, args) =>
        {
            if (args.ClickedItem is FrameworkElement { Tag: OrganizerSearchCandidate candidate })
                await LaunchAsync(candidate, reveal: false);
        };

        _status = new TextBlock
        {
            Opacity = .7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Text = AppStrings.Get("SearchLoading")
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_status, "OrganizerSearchStatus");
        _hint = new TextBlock { Opacity = .6, FontSize = 12, Text = AppStrings.Get("SearchHint"), TextTrimming = TextTrimming.CharacterEllipsis };

        Grid.SetRow(_query, 0);
        Grid.SetRow(_results, 1);
        Grid.SetRow(_status, 1);
        Grid.SetRow(_hint, 2);
        content.Children.Add(_query);
        content.Children.Add(_results);
        content.Children.Add(_status);
        content.Children.Add(_hint);
        _root.Children.Add(surfaceHost);
        _root.Children.Add(content);
        _root.KeyDown += Root_KeyDown;
        Content = _root;

        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        SystemBackdrop = new TransparentWindowBackdrop();
        _backdrop.Attach(surfaceHost);
        _surface = new ThemeSurface(surfaceHost);

        AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.IsShownInSwitchers = false;
        appWindow.Closing += (_, args) =>
        {
            try { _backdrop.DetachAndClose(); }
            catch (Exception ex)
            {
                args.Cancel = true;
                AppLogger.Error("Cannot close search backdrop safely.", ex);
            }
        };
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        NativeMethods.RECT bounds = DisplayPlacementService.CalculateCenteredDialogBounds(display, 560, 440);
        // Launchers sit in the upper third, where the eye already is, not dead centre.
        int lift = Math.Max(0, (bounds.Top - display.Work.Top) / 2);
        appWindow.MoveAndResize(new RectInt32(bounds.Left, bounds.Top - lift, bounds.Width, bounds.Height));
        long extendedStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        extendedStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        extendedStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        _ = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(extendedStyle));

        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated && _loaded && !_launching) CloseWindow();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            try { _backdrop.DetachAndClose(); }
            catch (Exception ex) { AppLogger.Error("Search backdrop close failed.", ex); }
            _host.ThemeChanged -= Host_ThemeChanged;
            _surface.Dispose();
        };
        _host.ThemeChanged += Host_ThemeChanged;
        ApplyTheme();
        _root.Loaded += async (_, _) =>
        {
            _ = _query.Focus(FocusState.Programmatic);
            await LoadAsync();
        };
    }

    internal bool IsClosed => _closed;

    internal void ShowAndFocus()
    {
        Activate();
        _ = NativeMethods.SetForegroundWindow(WindowNative.GetWindowHandle(this));
        _query.SelectAll();
        _ = _query.Focus(FocusState.Programmatic);
    }

    private async Task LoadAsync()
    {
        try
        {
            _candidates = await _host.CollectSearchCandidatesAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法读取收纳窗搜索索引。", ex);
            _candidates = [];
        }
        _loaded = true;
        if (!_closed) Refresh();
    }

    private void Refresh()
    {
        if (!_loaded) return;
        IReadOnlyList<OrganizerSearchResult> results = OrganizerSearch.Search(_candidates, _query.Text);
        _results.Items.Clear();
        foreach (OrganizerSearchResult result in results) _results.Items.Add(CreateRow(result.Candidate));
        if (_results.Items.Count > 0) _results.SelectedIndex = 0;
        if (string.IsNullOrWhiteSpace(_query.Text))
        {
            int organizers = _candidates.Select(candidate => candidate.OrganizerId).Distinct().Count();
            _status.Text = AppStrings.Format("SearchSummaryFormat", _candidates.Count, organizers);
        }
        else
        {
            _status.Text = AppStrings.Get("SearchNoResults");
        }
        _status.Visibility = _results.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static FrameworkElement CreateRow(OrganizerSearchCandidate candidate)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(2, 6, 2, 6), Tag = candidate };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new FontIcon { Glyph = Glyph(candidate.Kind), FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock { Text = candidate.DisplayName, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = AppStrings.Format("SearchResultLocationFormat", candidate.OrganizerName, candidate.FileName),
            FontSize = 12,
            Opacity = .65,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(icon);
        row.Children.Add(text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, candidate.DisplayName);
        return row;
    }

    private static string Glyph(WidgetItemKind kind) => kind switch
    {
        WidgetItemKind.Folder => "",
        WidgetItemKind.Shortcut or WidgetItemKind.InternetShortcut => "",
        WidgetItemKind.PortableNote => "",
        WidgetItemKind.PortableTodo => "",
        _ => ""
    };

    private async void Query_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                e.Handled = true;
                Move(1);
                break;
            case VirtualKey.Up:
                e.Handled = true;
                Move(-1);
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                if (Selected() is { } candidate)
                {
                    bool reveal = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                    await LaunchAsync(candidate, reveal);
                }
                break;
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        CloseWindow();
    }

    private void Move(int delta)
    {
        int count = _results.Items.Count;
        if (count == 0) return;
        int next = Math.Clamp(_results.SelectedIndex + delta, 0, count - 1);
        _results.SelectedIndex = next;
        _results.ScrollIntoView(_results.Items[next]);
    }

    private OrganizerSearchCandidate? Selected() =>
        (_results.SelectedItem ?? (_results.Items.Count > 0 ? _results.Items[0] : null)) is FrameworkElement { Tag: OrganizerSearchCandidate candidate }
            ? candidate
            : null;

    private async Task LaunchAsync(OrganizerSearchCandidate candidate, bool reveal)
    {
        if (_launching) return;
        _launching = true;
        try
        {
            await _host.OpenSearchResultAsync(candidate, reveal);
            CloseWindow();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法打开搜索结果：{candidate.FullPath}", ex);
            _status.Text = AppStrings.Format("FileMenuErrorFormat", candidate.DisplayName, ex.Message);
            _status.Visibility = Visibility.Visible;
        }
        finally
        {
            _launching = false;
        }
    }

    private void CloseWindow()
    {
        if (_closed) return;
        _closed = true;
        try { _backdrop.DetachAndClose(); }
        catch (Exception ex) { AppLogger.Error("Search backdrop close failed.", ex); }
        Close();
    }

    private void ApplyTheme()
    {
        ThemeValues theme = _host.State.GlobalSettings.GetTheme(ThemeTarget.Settings);
        _root.RequestedTheme = ThemePalette.IsDark(theme) ? ElementTheme.Dark : ElementTheme.Light;
        bool useEffects = _uiSettings.AdvancedEffectsEnabled;
        _backdrop.SetTheme(theme, useEffects);
        _surface.SetTheme(theme, useEffects);
    }

    private void Host_ThemeChanged(object? sender, EventArgs e) => ApplyTheme();
}
