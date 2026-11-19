using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Models;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace TuckPane.Services;

/// <summary>
/// Spotlight-style instant search window that searches across all organizers'
/// top-level items on a background thread without mutating storage directories.
/// </summary>
internal sealed class OrganizerSearchWindow : Window
{
    private const double WindowWidthDip = 620;
    private const double WindowHeightDip = 440;

    private readonly AppHost _host;
    private readonly TextBox _queryBox;
    private readonly ListView _resultsList;
    private readonly TextBlock _statusText;
    private readonly TextBlock _hintText;
    private readonly Border _cardBorder;
    private readonly List<OrganizerSearchEntry> _index = [];
    private IReadOnlyList<OrganizerSearchHit> _currentHits = [];
    private IntPtr _hwnd;
    private AppWindow? _appWindow;
    private bool _initialized;
    private bool _closed;
    private int _indexVersion;
    private long _lastOpenedTimestamp;
    private bool _hasBeenActivatedSinceOpen;

    internal OrganizerSearchWindow(AppHost host)
    {
        _host = host;
        Title = AppStrings.Get("SearchWindowTitle");

        _queryBox = new TextBox
        {
            PlaceholderText = AppStrings.Get("SearchPlaceholder"),
            FontSize = 16,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(8, 10, 8, 10)
        };
        _queryBox.TextChanged += (_, _) => ApplyFilter();
        _queryBox.PreviewKeyDown += QueryBox_PreviewKeyDown;

        _statusText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(12, 0, 12, 6),
            Text = AppStrings.Get("SearchIndexing")
        };

        _resultsList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            Padding = new Thickness(6, 0, 6, 6)
        };
        _resultsList.ItemClick += async (_, e) =>
        {
            if (e.ClickedItem is FrameworkElement { Tag: OrganizerSearchHit hit })
                await ExecuteSelectedAsync(hit, revealInExplorer: false, jumpToOrganizer: false);
        };
        _resultsList.KeyDown += ResultsList_KeyDown;

        _hintText = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.65,
            Margin = new Thickness(14, 6, 14, 10),
            Text = AppStrings.Get("SearchFooterHint"),
            TextWrapping = TextWrapping.Wrap
        };

        var rootGrid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };

        var headerBorder = new Border
        {
            Padding = new Thickness(10, 6, 10, 4),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
            Child = _queryBox
        };
        Grid.SetRow(headerBorder, 0);
        Grid.SetRow(_statusText, 1);
        Grid.SetRow(_resultsList, 3);
        Grid.SetRow(_hintText, 4);

        rootGrid.Children.Add(headerBorder);
        rootGrid.Children.Add(_statusText);
        rootGrid.Children.Add(_resultsList);
        rootGrid.Children.Add(_hintText);

        _cardBorder = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(45, 0, 0, 0)),
            Background = new SolidColorBrush(Color.FromArgb(248, 248, 249, 251)),
            Child = rootGrid
        };

        Content = _cardBorder;
        Activated += OrganizerSearchWindow_Activated;
        Closed += (_, _) => _closed = true;
    }

    internal void OpenAndFocus()
    {
        if (_closed) return;
        EnsureInitialized();
        ApplyLanguageAndTheme();
        PositionOnCursorMonitor();
        _queryBox.Text = string.Empty;
        _lastOpenedTimestamp = Stopwatch.GetTimestamp();
        _hasBeenActivatedSinceOpen = false;
        _appWindow?.Show();
        Activate();
        _ = NativeMethods.SetForegroundWindow(_hwnd);
        _queryBox.Focus(FocusState.Programmatic);
        _ = RefreshIndexAsync(++_indexVersion);
    }

    internal void Dismiss()
    {
        if (_closed) return;
        _appWindow?.Hide();
    }

    internal void ClosePermanently()
    {
        if (_closed) return;
        _closed = true;
        Close();
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
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
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
        _hintText.Text = AppStrings.Get("SearchFooterHint");
        ThemeValues theme = _host.State.GlobalSettings.GetTheme(ThemeTarget.Settings);
        bool dark = ThemePalette.IsDark(theme);
        _cardBorder.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        _cardBorder.Background = new SolidColorBrush(dark
            ? Color.FromArgb(250, 32, 34, 38)
            : Color.FromArgb(252, 248, 249, 251));
        _cardBorder.BorderBrush = new SolidColorBrush(dark
            ? Color.FromArgb(64, 255, 255, 255)
            : Color.FromArgb(40, 0, 0, 0));
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
        int width = Math.Min(display.Work.Width - 32, Math.Max(320, (int)Math.Round(WindowWidthDip * scale)));
        int height = Math.Min(display.Work.Height - 48, Math.Max(260, (int)Math.Round(WindowHeightDip * scale)));
        int left = display.Work.Left + (display.Work.Width - width) / 2;
        int top = display.Work.Top + Math.Max(24, (display.Work.Height - height) / 4);

        _ = NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            left,
            top,
            width,
            height,
            NativeMethods.SWP_SHOWWINDOW);
    }

    private async Task RefreshIndexAsync(int version)
    {
        _statusText.Text = AppStrings.Get("SearchIndexing");
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
        _index.Clear();
        _index.AddRange(entries);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = _queryBox.Text ?? string.Empty;
        _currentHits = OrganizerSearch.Search(_index, query, OrganizerSearch.DefaultMaxResults);
        _resultsList.Items.Clear();

        foreach (OrganizerSearchHit hit in _currentHits)
        {
            var row = new Grid
            {
                Padding = new Thickness(8, 6, 8, 6),
                ColumnSpacing = 10,
                Tag = hit,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };
            var icon = new FontIcon
            {
                Glyph = GlyphForKind(hit.Entry.Kind),
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center
            };
            var title = new TextBlock
            {
                Text = hit.Entry.ItemName,
                FontSize = 14,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var badge = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 2, 8, 2),
                Background = new SolidColorBrush(Color.FromArgb(26, 0, 120, 212)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = hit.Entry.OrganizerName,
                    FontSize = 11,
                    Opacity = 0.85
                }
            };
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(title, 1);
            Grid.SetColumn(badge, 2);
            row.Children.Add(icon);
            row.Children.Add(title);
            row.Children.Add(badge);
            _resultsList.Items.Add(row);
        }

        if (_resultsList.Items.Count > 0)
            _resultsList.SelectedIndex = 0;

        _statusText.Text = _currentHits.Count == 0
            ? AppStrings.Get("SearchNoResults")
            : AppStrings.Format("SearchResultCountFormat", _currentHits.Count, _index.Count);
    }

    private async void QueryBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Dismiss();
            return;
        }
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
            return;
        }
        if (e.Key == VirtualKey.C &&
            (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0 &&
            (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0)
        {
            int index = _resultsList.SelectedIndex;
            if (index >= 0 && index < _currentHits.Count && _currentHits[index].Entry.Kind != WidgetItemKind.Note)
            {
                e.Handled = true;
                ShellItemCommands.CopyPathToClipboard(_currentHits[index].Entry.FullPath);
                Dismiss();
            }
        }
    }

    private async void ResultsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Dismiss();
        }
        else if (e.Key == VirtualKey.Enter)
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
}
