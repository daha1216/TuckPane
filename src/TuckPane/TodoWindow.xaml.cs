using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace TuckPane;

public sealed partial class TodoWindow : Window, IPortableDocumentWindow
{
    private readonly AppHost _host;
    private readonly NoteStore _store;
    private readonly PortableTodoDocument _document;
    private readonly ObservableCollection<TodoRow> _rows;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _completionTimer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly TodoComposer _composer = new();
    private readonly PortableDocumentWindowChrome _windowChrome;
    private string _externalPath;
    private bool _syncingTaskCheckBox;
    private bool _isDraggingTask;

    internal TodoWindow(AppHost host, NoteStore store, string path, PortableTodoDocument document)
    {
        _host = host;
        _store = store;
        _externalPath = Path.GetFullPath(path);
        _document = document;
        bool removedExpired = TodoRules.RemoveExpired(_document, DateTimeOffset.UtcNow) > 0;
        _rows = new ObservableCollection<TodoRow>(_document.Tasks.Select(task =>
            new TodoRow(task, _document.FontSize, _host.State.GlobalSettings.TodoShowSeparators)));

        InitializeComponent();
        _windowChrome = new PortableDocumentWindowChrome(
            this,
            _host,
            WindowRoot,
            WindowFrame,
            HeaderBar,
            ErrorBar,
            () => Path.GetFileNameWithoutExtension(_externalPath),
            () => _document.Placement,
            placement =>
            {
                _document.Placement = placement;
                ScheduleSave();
            },
            HideAsync,
            CommitTitleRenameAsync,
            OnThemeSelected);

        HeaderBar.ColorButton.Click += ColorButton_Click;
        HeaderBar.OptionToggleButton.Click += SeparatorsButton_Click;

        TaskList.ItemsSource = _rows;
        UpdateComposerMetrics();
        WindowRoot.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(WindowRoot_PointerWheelChanged), handledEventsToo: true);
        WindowRoot.ActualThemeChanged += WindowRoot_ActualThemeChanged;

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await FlushAsync();

        _completionTimer = DispatcherQueue.CreateTimer();
        _completionTimer.Interval = TimeSpan.FromMilliseconds(50);
        _completionTimer.IsRepeating = true;
        _completionTimer.Tick += CompletionTimer_Tick;

        Closed += TodoWindow_Closed;
        ApplyLanguage();
        ApplyTheme();
        ApplySeparatorSetting();
        RefreshCompletionTimer();
        if (removedExpired) ScheduleSave();
    }

    internal bool IsVisible => _windowChrome.IsVisible;
    internal string ExternalPath => _externalPath;

    PortableDocumentKind IPortableDocumentWindow.Kind => PortableDocumentKind.Todo;
    object IPortableDocumentWindow.UnderlyingWindow => this;
    string? IPortableDocumentWindow.ExternalPath => ExternalPath;
    bool IPortableDocumentWindow.IsVisible => IsVisible;
    void IPortableDocumentWindow.ShowAndActivate() => ShowAndActivate();
    void IPortableDocumentWindow.HideForTray() => HideForTray();
    void IPortableDocumentWindow.RestoreFromTray() => RestoreFromTray();
    Task<bool> IPortableDocumentWindow.FlushForExitAsync() => FlushForExitAsync();
    Task<bool> IPortableDocumentWindow.FlushAndHideForDragAsync() => FlushAndHideForDragAsync();
    void IPortableDocumentWindow.RestoreAfterDrag() => RestoreAfterDrag();
    Task IPortableDocumentWindow.BeginDirectoryRenameAsync() => BeginDirectoryRenameAsync();
    void IPortableDocumentWindow.EndDirectoryRename() => EndDirectoryRename();
    void IPortableDocumentWindow.RebindExternalPath(string path) => RebindExternalPath(path);
    void IPortableDocumentWindow.ClosePermanentlyWithoutSave() => ClosePermanentlyWithoutSave();
    Task IPortableDocumentWindow.ClosePermanentlyAsync() => ClosePermanentlyAsync();
    Task IPortableDocumentWindow.ApplyGlobalThemeAsync(NoteTheme theme) => ApplyGlobalThemeAsync(theme);
    void IPortableDocumentWindow.ApplyLanguage() => ApplyLanguage();
    void IPortableDocumentWindow.ApplyAlwaysOnTopSetting() => ApplyAlwaysOnTopSetting();

    internal void InitializeHostWindow() => _windowChrome.InitializeHostWindow();
    internal void ShowAndActivate() => _windowChrome.ShowAndActivate();
    internal void ApplyAlwaysOnTopSetting() => _windowChrome.ApplyAlwaysOnTopSetting();

    internal async Task ApplyGlobalThemeAsync(NoteTheme theme)
    {
        _document.Theme = theme;
        try { ApplyTheme(); }
        catch (Exception ex)
        {
            AppLogger.Error("待办主题外观应用失败", ex);
            throw;
        }
        if (!await FlushAsync())
            throw new IOException(AppStrings.Get("NoteThemeDocumentSaveFailed"));
    }

    internal void RebindExternalPath(string path)
    {
        _externalPath = Path.GetFullPath(path);
        UpdateTitle();
    }

    internal void HideForTray() => _windowChrome.HideForTray();
    internal void RestoreFromTray() => _windowChrome.RestoreFromTray();
    internal Task HideAsync() => _windowChrome.HideAsync(FlushAsync);

    internal Task<bool> FlushAndHideForDragAsync() =>
        _windowChrome.FlushAndHideForDragAsync(FlushAsync, "TodoDragSaveFailed");

    internal void RestoreAfterDrag() => _windowChrome.RestoreAfterDrag();

    internal Task<bool> FlushForExitAsync() =>
        _windowChrome.IsPermanentClose ? Task.FromResult(true) : FlushAsync();

    internal Task ClosePermanentlyAsync() => _windowChrome.ClosePermanentlyAsync(FlushAsync);

    internal void ClosePermanentlyWithoutSave() =>
        _windowChrome.ClosePermanentlyWithoutSave(() =>
        {
            _saveTimer.Stop();
            _completionTimer.Stop();
        });

    internal void ApplyLanguage()
    {
        string idSuffix = GetAutomationSuffix();
        _windowChrome.ApplyHeaderLanguage(
            idPrefix: "Todo",
            idSuffix: idSuffix,
            renameHintKey: "TodoRenameHint",
            colorKey: "TodoColor",
            optionKey: "TodoSeparators",
            closeKey: "CloseTodo");
        AutomationProperties.SetAutomationId(HeaderBar.OptionToggleButton, $"TodoSeparators-{idSuffix}");
        AutomationProperties.SetName(NewTaskBox, AppStrings.Get("TodoAddPlaceholder"));
        AutomationProperties.SetAutomationId(NewTaskBox, $"TodoNewTaskBox-{idSuffix}");
        AutomationProperties.SetName(AddTaskButton, AppStrings.Get("TodoAddPlaceholder"));
        AutomationProperties.SetAutomationId(AddTaskButton, $"TodoAddTask-{idSuffix}");
        foreach (TodoRow row in _rows) row.ApplyLanguage();
    }

    private void UpdateTitle()
    {
        string idSuffix = GetAutomationSuffix();
        string name = Path.GetFileNameWithoutExtension(_externalPath);
        Title = name;
        HeaderBar.TitleText.Text = name;
        AutomationProperties.SetName(HeaderBar.TitleText, name);
        AutomationProperties.SetAutomationId(HeaderBar.TitleText, $"TodoTitle-{idSuffix}");
        AutomationProperties.SetHelpText(HeaderBar.TitleText, AppStrings.Get("TodoRenameHint"));
        AutomationProperties.SetName(HeaderBar.TitleEditor, AppStrings.Get("ContextRename"));
        AutomationProperties.SetAutomationId(HeaderBar.TitleEditor, $"TodoTitleEditor-{idSuffix}");
    }

    private string GetAutomationSuffix() =>
        ((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(_externalPath)).ToString("X8");

    private async Task<bool> FlushAsync()
    {
        _saveTimer.Stop();
        await _saveGate.WaitAsync();
        try { return await FlushCoreAsync(); }
        finally { _saveGate.Release(); }
    }

    internal async Task BeginDirectoryRenameAsync()
    {
        _saveTimer.Stop();
        await _saveGate.WaitAsync();
        if (await FlushCoreAsync())
        {
            WindowRoot.IsHitTestVisible = false;
            return;
        }
        _saveGate.Release();
        throw new IOException(AppStrings.Get("TodoDragSaveFailed"));
    }

    internal void EndDirectoryRename()
    {
        WindowRoot.IsHitTestVisible = true;
        _saveGate.Release();
    }

    private async Task<bool> FlushCoreAsync()
    {
        try
        {
            _document.Tasks = _rows.Select(row => row.Task).ToList();
            await _store.SaveTodoAsync(_externalPath, _document);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法保存待办：{_externalPath}", ex);
            ShowError(AppStrings.Format("TodoSaveErrorFormat", ex.Message));
            return false;
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void NewTaskBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            _composer.Cancel();
            RefreshComposer(focusAdd: true);
            return;
        }
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        if (_composer.Submit(_document) is not PortableTodoTask task) return;
        _rows.Add(new TodoRow(task, _document.FontSize, _host.State.GlobalSettings.TodoShowSeparators));
        RefreshComposer(focusAdd: true);
        ScheduleSave();
        _ = DispatcherQueue.TryEnqueue(() => NewTaskEntry.StartBringIntoView());
    }

    private void AddTaskButton_Click(object sender, RoutedEventArgs e)
    {
        _composer.Begin();
        RefreshComposer();
        NewTaskBox.Focus(FocusState.Programmatic);
    }

    private void NewTaskBox_TextChanged(object sender, TextChangedEventArgs e) =>
        _composer.Text = NewTaskBox.Text;

    private void NewTaskBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _composer.Blur();
        RefreshComposer();
    }

    private void RefreshComposer(bool focusAdd = false)
    {
        NewTaskBox.Text = _composer.Text;
        AddTaskButton.Visibility = _composer.IsEditing ? Visibility.Collapsed : Visibility.Visible;
        NewTaskBox.Visibility = _composer.IsEditing ? Visibility.Visible : Visibility.Collapsed;
        if (focusAdd) AddTaskButton.Focus(FocusState.Programmatic);
    }

    private void UpdateComposerMetrics()
    {
        NewTaskBox.FontSize = _document.FontSize;
        AddTaskButton.FontSize = _document.FontSize;
        var line = new TextBlock
        {
            Text = "Ag中", FontSize = _document.FontSize,
            FontFamily = NewTaskBox.FontFamily
        };
        line.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        NewTaskEntry.Height = Math.Max(40, Math.Max(21, line.DesiredSize.Height) + 12 +
            (_host.State.GlobalSettings.TodoShowSeparators ? 1 : 0));
    }

    private void TaskCheckTarget_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Grid target || !ReferenceEquals(e.OriginalSource, target) ||
            FindDescendant<CheckBox>(target, candidate => candidate.Tag is TodoRow) is not { IsEnabled: true } box) return;
        _ = box.Focus(FocusState.Pointer);
        box.IsChecked = box.IsChecked != true;
        TaskCheckBox_Click(box, e);
        e.Handled = true;
    }

    private void TaskCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingTaskCheckBox || sender is not CheckBox { Tag: TodoRow row } box) return;
        TodoRules.SetDone(row.Task, box.IsChecked == true, DateTimeOffset.UtcNow);
        row.RefreshVisual(DateTimeOffset.UtcNow);
        RefreshCompletionTimer();
        ScheduleSave();
    }

    private void UndoTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TodoRow row }) return;
        TodoRules.SetDone(row.Task, done: false, DateTimeOffset.UtcNow);
        row.RefreshVisual(DateTimeOffset.UtcNow);
        SyncTaskCheckBox(row);
        RefreshCompletionTimer();
        ScheduleSave();
    }

    private void TaskCheckBox_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is CheckBox box && args.NewValue is TodoRow row) SetTaskCheckBoxState(box, row.Done);
    }

    private void SyncTaskCheckBox(TodoRow row)
    {
        if (TaskList.ContainerFromItem(row) is not ListViewItem container) return;
        CheckBox? box = FindDescendant<CheckBox>(container, candidate => ReferenceEquals(candidate.Tag, row));
        if (box is not null) SetTaskCheckBoxState(box, row.Done);
    }

    private void SetTaskCheckBoxState(CheckBox box, bool isChecked)
    {
        _syncingTaskCheckBox = true;
        try { box.IsChecked = isChecked; }
        finally { _syncingTaskCheckBox = false; }
    }

    private void TaskText_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoRow row }) BeginTaskEdit(row);
        e.Handled = true;
    }

    private void EditTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: TodoRow row }) BeginTaskEdit(row);
    }

    private void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: TodoRow row }) return;
        _rows.Remove(row);
        _document.Tasks.Remove(row.Task);
        RefreshCompletionTimer();
        ScheduleSave();
    }

    private void BeginTaskEdit(TodoRow row)
    {
        row.BeginEdit();
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (TaskList.ContainerFromItem(row) is not ListViewItem container) return;
            TextBox? editor = FindDescendant<TextBox>(container, candidate => ReferenceEquals(candidate.Tag, row));
            editor?.Focus(FocusState.Programmatic);
            editor?.SelectAll();
        });
    }

    private void TaskEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: TodoRow row }) return;
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            row.CancelEdit();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitTaskEdit(row);
            e.Handled = true;
        }
    }

    private void TaskEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: TodoRow row }) CommitTaskEdit(row);
    }

    private void CommitTaskEdit(TodoRow row)
    {
        if (!row.IsEditing) return;
        if (TodoRules.UpdateText(row.Task, row.EditText)) ScheduleSave();
        row.EndEdit();
    }

    private void TaskList_DragItemsStarting(object sender, DragItemsStartingEventArgs e) =>
        _isDraggingTask = true;

    private void TaskList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _isDraggingTask = false;
        _document.Tasks = _rows.Select(row => row.Task).ToList();
        ScheduleSave();
    }

    private void CompletionTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool editingAny = _isDraggingTask;
        foreach (TodoRow row in _rows)
        {
            if (row.IsEditing) editingAny = true;
            if (row.Done || row.HasPendingVisualSync) row.RefreshVisual(now);
        }
        if (!editingAny)
        {
            TodoRow[] expired = _rows.Where(row =>
                row.Task.Done &&
                row.Task.CompletedAtUtc is DateTimeOffset completed &&
                now.ToUniversalTime() - completed >= TodoRules.CompletionDelay).ToArray();
            foreach (TodoRow row in expired)
            {
                _rows.Remove(row);
                _document.Tasks.Remove(row.Task);
            }
            if (expired.Length > 0) ScheduleSave();
        }
        RefreshCompletionTimer();
    }

    private void RefreshCompletionTimer()
    {
        if (_rows.Any(row => row.Done)) _completionTimer.Start();
        else _completionTimer.Stop();
    }

    private void WindowRoot_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if ((NativeMethods.GetKeyState(NativeMethods.VK_CONTROL) & 0x8000) == 0) return;
        int delta = e.GetCurrentPoint(WindowRoot).Properties.MouseWheelDelta;
        if (delta == 0) return;
        double next = Math.Clamp(
            _document.FontSize + Math.Sign(delta),
            OrganizerNoteRules.MinimumFontSize,
            OrganizerNoteRules.MaximumFontSize);
        if (next == _document.FontSize) return;
        _document.FontSize = next;
        foreach (TodoRow row in _rows) row.FontSize = next;
        UpdateComposerMetrics();
        ScheduleSave();
        e.Handled = true;
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e) =>
        _windowChrome.ShowThemeFlyout(_document.Theme, "Todo");

    private async void OnThemeSelected(NoteTheme theme)
    {
        try { await _host.SetNoteThemeAsync(theme); }
        catch (Exception ex)
        {
            AppLogger.Error("无法同步全局待办主题", ex);
            ShowError(AppStrings.Format("NoteThemeChangeErrorFormat", ex.Message));
        }
    }

    internal void ApplySeparatorSetting()
    {
        bool enabled = _host.State.GlobalSettings.TodoShowSeparators;
        HeaderBar.OptionToggleButton.IsChecked = enabled;
        foreach (TodoRow row in _rows) row.SetSeparators(enabled);
        UpdateComposerMetrics();
    }

    private async void SeparatorsButton_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = HeaderBar.OptionToggleButton.IsChecked == true;
        HeaderBar.OptionToggleButton.IsEnabled = false;
        try { await _host.SetTodoShowSeparatorsAsync(enabled); }
        catch (Exception ex)
        {
            AppLogger.Error("无法保存待办分隔线设置", ex);
            ShowError(AppStrings.Format("TodoSaveErrorFormat", ex.Message));
        }
        finally
        {
            ApplySeparatorSetting();
            HeaderBar.OptionToggleButton.IsEnabled = true;
        }
    }

    private void WindowRoot_ActualThemeChanged(FrameworkElement sender, object args) => ApplyTheme();

    private void ApplyTheme()
    {
        PortableDocumentResolvedTheme resolved = _windowChrome.ApplyChromeTheme(_document.Theme, showFrameBorder: true);
        WindowRoot.RequestedTheme = resolved.HighContrast
            ? ElementTheme.Default
            : resolved.Text.R > 128 ? ElementTheme.Dark : ElementTheme.Light;

        var text = (SolidColorBrush)WindowRoot.Resources["TodoTextBrush"];
        var input = (SolidColorBrush)WindowRoot.Resources["TodoInputBrush"];
        var border = (SolidColorBrush)WindowRoot.Resources["TodoBorderBrush"];
        var separator = (SolidColorBrush)WindowRoot.Resources["TodoSeparatorBrush"];
        text.Color = resolved.Text;
        input.Color = resolved.HighContrast
            ? resolved.Editor
            : ColorHelper.FromArgb(24, resolved.Text.R, resolved.Text.G, resolved.Text.B);
        border.Color = resolved.Border;
        separator.Color = resolved.Border;
        separator.Opacity = resolved.HighContrast ? 1 : 0.5;

        WindowThemeBrushes themeBrushes = _windowChrome.ThemeBrushes;
        themeBrushes.Set(WindowRoot.Resources, "TextControlBackgroundPointerOver", input.Color);
        themeBrushes.Set(WindowRoot.Resources, "TextControlBackgroundFocused", input.Color);
        themeBrushes.Set(WindowRoot.Resources, "TextControlForegroundPointerOver", text.Color);
        themeBrushes.Set(WindowRoot.Resources, "TextControlForegroundFocused", text.Color);
        themeBrushes.Set(WindowRoot.Resources, "TextControlBorderBrushPointerOver", border.Color);
        themeBrushes.Set(WindowRoot.Resources, "TextControlBorderBrushFocused", resolved.Accent);

        Color addColor = resolved.HighContrast
            ? resolved.Editor
            : ColorHelper.FromArgb(12, resolved.Text.R, resolved.Text.G, resolved.Text.B);
        Color addBorder = resolved.HighContrast
            ? resolved.Border
            : ColorHelper.FromArgb(80, resolved.Border.R, resolved.Border.G, resolved.Border.B);
        ((SolidColorBrush)WindowRoot.Resources["TodoAddBrush"]).Color = addColor;
        ((SolidColorBrush)WindowRoot.Resources["TodoAddBorderBrush"]).Color = addBorder;
        themeBrushes.Set(NewTaskBox.Resources, "TextControlBackgroundPointerOver", addColor);
        themeBrushes.Set(NewTaskBox.Resources, "TextControlBackgroundFocused", addColor);
        themeBrushes.Set(NewTaskBox.Resources, "TextControlBorderBrushPointerOver", addBorder);
        themeBrushes.Set(NewTaskBox.Resources, "TextControlBorderBrushFocused", resolved.Accent);
        themeBrushes.Set(AddTaskButton.Resources, "ButtonBackgroundPointerOver",
            resolved.HighContrast ? resolved.Accent : ColorHelper.FromArgb(24, resolved.Text.R, resolved.Text.G, resolved.Text.B));
        themeBrushes.Set(AddTaskButton.Resources, "ButtonBackgroundPressed",
            resolved.HighContrast ? resolved.Accent : ColorHelper.FromArgb(36, resolved.Text.R, resolved.Text.G, resolved.Text.B));
        themeBrushes.Set(AddTaskButton.Resources, "ButtonForegroundPointerOver",
            resolved.HighContrast ? resolved.Surface : resolved.Text);
        themeBrushes.Set(AddTaskButton.Resources, "ButtonForegroundPressed",
            resolved.HighContrast ? resolved.Surface : resolved.Text);
        themeBrushes.Set(AddTaskButton.Resources, "ButtonBorderBrushPointerOver", addBorder);
        themeBrushes.Set(AddTaskButton.Resources, "ButtonBorderBrushPressed", resolved.Accent);
        TaskList.Foreground = text;
    }

    private async Task CommitTitleRenameAsync(string candidate)
    {
        try
        {
            if (!await FlushAsync())
                throw new IOException(AppStrings.Get("TodoDragSaveFailed"));
            _externalPath = await _host.RenameExternalTodoAsync(_externalPath, candidate);
            UpdateTitle();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法重命名待办：{_externalPath}", ex);
            throw;
        }
    }

    private void TodoWindow_Closed(object sender, WindowEventArgs args)
    {
        WindowRoot.ActualThemeChanged -= WindowRoot_ActualThemeChanged;
        _saveTimer.Stop();
        _completionTimer.Stop();
        _windowChrome.Dispose();
    }

    private void ShowError(string message) => _windowChrome.ShowError(message);

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match)) return match;
            if (FindDescendant(child, predicate) is T nested) return nested;
        }
        return null;
    }

    public sealed class TodoRow : INotifyPropertyChanged
    {
        private double _fontSize;
        private bool _isEditing;
        private string _editText;
        private double _opacity = 1;
        private bool _showSeparators;
        private bool _lastRenderedDone;

        internal TodoRow(PortableTodoTask task, double fontSize, bool showSeparators = false)
        {
            Task = task;
            _fontSize = fontSize;
            _showSeparators = showSeparators;
            _editText = task.Text;
            _lastRenderedDone = !task.Done;
            RefreshVisual(DateTimeOffset.UtcNow);
        }

        internal PortableTodoTask Task { get; }
        public string Text => Task.Text;
        public bool Done => Task.Done;
        internal bool HasPendingVisualSync => _lastRenderedDone != Task.Done || (!Task.Done && Math.Abs(_opacity - 1) > .001);
        public TextDecorations TextDecorations => Done
            ? Windows.UI.Text.TextDecorations.Strikethrough
            : Windows.UI.Text.TextDecorations.None;
        public Visibility UndoVisibility => Done ? Visibility.Visible : Visibility.Collapsed;
        public string UndoLabel => AppStrings.Get("TodoUndo");
        public string EditLabel => AppStrings.Get("TodoEdit");
        public string DeleteLabel => AppStrings.Get("TodoDelete");
        public Visibility TextVisibility => _isEditing ? Visibility.Collapsed : Visibility.Visible;
        public Visibility EditorVisibility => _isEditing ? Visibility.Visible : Visibility.Collapsed;
        public bool IsEditing => _isEditing;
        public double CheckBoxSize => _fontSize * .8;
        public Thickness SeparatorThickness => new(0, 0, 0, _showSeparators ? 1 : 0);

        internal void SetSeparators(bool enabled)
        {
            if (_showSeparators == enabled) return;
            _showSeparators = enabled;
            OnPropertyChanged(nameof(SeparatorThickness));
        }

        public string EditText
        {
            get => _editText;
            set => _editText = value;
        }

        public double FontSize
        {
            get => _fontSize;
            set
            {
                if (_fontSize == value) return;
                _fontSize = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CheckBoxSize));
            }
        }

        public double Opacity
        {
            get => _opacity;
            private set { if (Math.Abs(_opacity - value) > .001) { _opacity = value; OnPropertyChanged(); } }
        }

        internal void BeginEdit()
        {
            _editText = Task.Text;
            _isEditing = true;
            OnPropertyChanged(nameof(EditText));
            OnPropertyChanged(nameof(TextVisibility));
            OnPropertyChanged(nameof(EditorVisibility));
        }

        internal void CancelEdit()
        {
            _editText = Task.Text;
            EndEdit();
        }

        internal void EndEdit()
        {
            _isEditing = false;
            _editText = Task.Text;
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(EditText));
            OnPropertyChanged(nameof(TextVisibility));
            OnPropertyChanged(nameof(EditorVisibility));
        }

        internal void RefreshVisual(DateTimeOffset now)
        {
            Opacity = TodoRules.GetOpacity(Task, now);
            bool done = Done;
            if (_lastRenderedDone == done) return;
            _lastRenderedDone = done;
            OnPropertyChanged(nameof(Done));
            OnPropertyChanged(nameof(TextDecorations));
            OnPropertyChanged(nameof(UndoVisibility));
        }

        internal void ApplyLanguage()
        {
            OnPropertyChanged(nameof(UndoLabel));
            OnPropertyChanged(nameof(EditLabel));
            OnPropertyChanged(nameof(DeleteLabel));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
