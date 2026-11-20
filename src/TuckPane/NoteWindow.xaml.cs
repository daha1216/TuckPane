using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.ApplicationModel.DataTransfer;

namespace TuckPane;

public sealed partial class NoteWindow : Window, IPortableDocumentWindow
{
    private readonly AppHost _host;
    private readonly NoteDefinition _definition;
    private readonly NoteStore _store;
    private readonly Guid? _organizerId;
    private string? _externalPath;
    private readonly PortableNoteDocument? _portableDocument;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private readonly NoteSaveSequence _saveSequence;
    private readonly PortableDocumentWindowChrome _windowChrome;
    private bool _shellInitialized;
    private bool _editorReady;
    private bool _editorInitializing;
    private NoteDocument _document = new();
    private Task<NoteDocument>? _documentLoadTask;
    private Uri? _editorUri;
    private Microsoft.Web.WebView2.Core.CoreWebView2? _coreWebView;

    internal NoteWindow(
        AppHost host,
        NoteDefinition definition,
        NoteStore store,
        string? externalPath = null,
        PortableNoteDocument? portableDocument = null,
        Guid? organizerId = null)
    {
        _host = host;
        _definition = definition;
        _store = store;
        _organizerId = organizerId;
        _externalPath = externalPath is null ? null : Path.GetFullPath(externalPath);
        _portableDocument = portableDocument;
        _saveSequence = new NoteSaveSequence(async readEditor =>
        {
            await EnsureDocumentLoadedAsync();
            if (readEditor) await ReadEditorStateAsync();
        }, CaptureWrite);
        if (_externalPath is not null)
        {
            ArgumentNullException.ThrowIfNull(portableDocument);
            _document = new NoteDocument { Html = portableDocument.Html };
            _documentLoadTask = Task.FromResult(_document);
        }

        InitializeComponent();
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += SaveTimer_Tick;

        _windowChrome = new PortableDocumentWindowChrome(
            this,
            _host,
            WindowRoot,
            WindowFrame,
            HeaderBar,
            ErrorBar,
            () => _definition.Name,
            () => PortableDocumentWindowChrome.ToPortablePlacement(_definition.Placement),
            placement =>
            {
                _definition.Placement = PortableDocumentWindowChrome.ToNotePlacement(placement);
                _saveTimer.Stop();
                _saveTimer.Start();
            },
            HideAsync,
            CommitTitleRenameAsync,
            OnThemeSelected);

        HeaderBar.OptionToggleButton.IsChecked = definition.ShowRuledLines;
        HeaderBar.ColorButton.Click += ColorButton_Click;
        HeaderBar.OptionToggleButton.Click += RuledLinesButton_Click;

        Activated += NoteWindow_Activated;
        Closed += NoteWindow_Closed;
        WindowRoot.ActualThemeChanged += WindowRoot_ActualThemeChanged;
        ApplyLanguage();
        ApplyTheme();
    }

    internal Guid Id => _definition.Id;
    internal bool IsVisible => _windowChrome.IsVisible;
    internal string? ExternalPath => _externalPath;

    PortableDocumentKind IPortableDocumentWindow.Kind => PortableDocumentKind.Note;
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

    internal async Task ApplyGlobalThemeAsync(NoteTheme theme)
    {
        _definition.Theme = theme;
        if (_portableDocument is not null) _portableDocument.Theme = theme;
        try { ApplyTheme(); }
        catch (Exception ex)
        {
            AppLogger.Error("便签主题外观应用失败", ex);
            throw;
        }
        Exception? editorFailure = null;
        if (_editorReady && Editor.CoreWebView2 is not null)
        {
            try
            {
                await Editor.CoreWebView2.ExecuteScriptAsync(
                    $"window.__tuckpane?.setTheme({JsonSerializer.Serialize(_windowChrome.BuildEditorCssPalette(_definition.Theme))})");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"无法同步便签主题：{Id}", ex);
                ShowError(AppStrings.Format("NoteEditorErrorFormat", ex.Message));
                editorFailure = ex;
            }
        }
        if (_externalPath is not null && !await FlushAsync(readEditor: true))
            throw new IOException(AppStrings.Get("NoteThemeDocumentSaveFailed"));
        if (editorFailure is not null) throw new InvalidOperationException("Note editor theme synchronization failed.", editorFailure);
    }

    internal void RebindExternalPath(string path)
    {
        if (_externalPath is null) throw new InvalidOperationException("Only portable notes can be rebound.");
        _externalPath = Path.GetFullPath(path);
        _definition.Name = Path.GetFileNameWithoutExtension(_externalPath);
        UpdateTitle();
    }

    internal void InitializeHostWindow() => _windowChrome.InitializeHostWindow();
    internal void ApplyAlwaysOnTopSetting() => _windowChrome.ApplyAlwaysOnTopSetting();
    internal void ShowAndActivate() => _windowChrome.ShowAndActivate(() => _ = FocusEditorAsync());
    internal void HideForTray() => _windowChrome.HideForTray();
    internal void RestoreFromTray() => _windowChrome.RestoreFromTray();
    internal Task HideAsync() => _windowChrome.HideAsync(() => FlushAsync(readEditor: true));

    internal Task<bool> FlushAndHideForDragAsync() =>
        _windowChrome.FlushAndHideForDragAsync(() => FlushAsync(readEditor: true), "NoteDragSaveFailed");

    internal void RestoreAfterDrag() => _windowChrome.RestoreAfterDrag();
    internal Task ClosePermanentlyAsync() => _windowChrome.ClosePermanentlyAsync(() => FlushAsync(readEditor: true));

    internal Task<bool> FlushForExitAsync() =>
        _windowChrome.IsPermanentClose ? Task.FromResult(true) : FlushAsync(readEditor: true);

    internal void ClosePermanentlyWithoutSave() =>
        _windowChrome.ClosePermanentlyWithoutSave(() => _saveTimer.Stop());

    internal void UpdateTitle()
    {
        string idSuffix = Id.ToString("N");
        Title = _definition.Name;
        HeaderBar.TitleText.Text = _definition.Name;
        AutomationProperties.SetName(HeaderBar.TitleText, _definition.Name);
        AutomationProperties.SetAutomationId(HeaderBar.TitleText, $"NoteTitle-{idSuffix}");
        AutomationProperties.SetHelpText(HeaderBar.TitleText, AppStrings.Get("NoteRenameHint"));
        AutomationProperties.SetName(HeaderBar.TitleEditor, AppStrings.Get("ContextRename"));
        AutomationProperties.SetAutomationId(HeaderBar.TitleEditor, $"NoteTitleEditor-{idSuffix}");
    }

    internal void ApplyLanguage()
    {
        _windowChrome.ApplyHeaderLanguage(
            idPrefix: "Note",
            idSuffix: Id.ToString("N"),
            renameHintKey: "NoteRenameHint",
            colorKey: "NoteColor",
            optionKey: "NoteRuledLines",
            closeKey: "CloseNote");
        AutomationProperties.SetAutomationId(HeaderBar.OptionToggleButton, $"NoteRuledLines-{Id:N}");
        if (_editorReady) _ = RefreshEditorLanguageAsync();
    }

    private async Task RefreshEditorLanguageAsync()
    {
        await ReadEditorStateAsync();
        await SendEditorLoadAsync();
    }

    private async void NoteWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_shellInitialized || _editorInitializing) return;
        _editorInitializing = true;
        try
        {
            await EnsureDocumentLoadedAsync();
            await Editor.EnsureCoreWebView2Async();
            if (Editor.CoreWebView2 is not { } core) throw new InvalidOperationException("WebView2 did not initialize.");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            _coreWebView = core;
            core.NewWindowRequested += CoreWebView2_NewWindowRequested;
            core.NavigationStarting += CoreWebView2_NavigationStarting;
            core.WebMessageReceived += CoreWebView2_WebMessageReceived;
            string htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "NoteEditor.html");
            _editorUri = new Uri(htmlPath);
            _editorReady = false;
            Editor.Source = _editorUri;
            _shellInitialized = true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法初始化便签编辑器：{Id}", ex);
            ShowError(AppStrings.Format("NoteEditorErrorFormat", ex.Message));
        }
        finally
        {
            _editorInitializing = false;
        }
    }

    private async void CoreWebView2_WebMessageReceived(
        Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            if (_editorUri is null || !Uri.TryCreate(args.Source, UriKind.Absolute, out Uri? source) ||
                !source.IsFile || !Path.GetFullPath(source.LocalPath)
                    .Equals(Path.GetFullPath(_editorUri.LocalPath), StringComparison.OrdinalIgnoreCase)) return;
            using JsonDocument message = JsonDocument.Parse(args.WebMessageAsJson);
            string type = message.RootElement.GetProperty("type").GetString() ?? string.Empty;
            if (await HandleImageMessageAsync(type, message.RootElement)) return;
            if (type == "shellReady")
            {
                await SendEditorLoadAsync();
                return;
            }
            if (type == "ready")
            {
                _editorReady = true;
                return;
            }
            if (type == "editorTiming" && AppLogger.PerformanceTraceEnabled)
            {
                JsonElement metrics = message.RootElement;
                AppLogger.Performance($"note-input id={Id} eventMs={metrics.GetProperty("eventMs").GetDouble():F2} frameOpportunityMs={metrics.GetProperty("frameMs").GetDouble():F2} utcMs={metrics.GetProperty("utcMs").GetDouble():F0}");
                return;
            }
            if (type == "copyText")
            {
                if (!message.RootElement.TryGetProperty("text", out JsonElement textElement) ||
                    textElement.GetString() is not { Length: > 0 } text ||
                    text.Length > NoteStore.MaximumHtmlLength) return;
                var content = new DataPackage();
                content.SetText(text);
                bool copied = false;
                int[] retryDelaysMs = [0, 50, 100, 200, 400];
                foreach (int delayMs in retryDelaysMs)
                {
                    if (delayMs > 0) await Task.Delay(delayMs);
                    copied = Clipboard.SetContentWithOptions(
                        content,
                        new ClipboardContentOptions { IsAllowedInHistory = true });
                    if (copied) break;
                }
                if (!copied)
                {
                    AppLogger.Error($"便签文字在 {retryDelaysMs.Length} 次尝试后仍无法写入剪贴板。");
                    ShowError(AppStrings.Get("NoteClipboardWriteError"));
                }
                return;
            }
            if (type is not ("changed" or "fontSize")) return;
            if (message.RootElement.TryGetProperty("fontSize", out JsonElement size) && size.TryGetDouble(out double fontSize))
                _definition.FontSize = Math.Clamp(fontSize, OrganizerNoteRules.MinimumFontSize, OrganizerNoteRules.MaximumFontSize);
            if (type == "changed" && message.RootElement.TryGetProperty("html", out JsonElement html))
            {
                string value = html.GetString() ?? string.Empty;
                if (value.Length <= NoteStore.MaximumHtmlLength) _document.Html = value;
                else ShowError(AppStrings.Get("NoteTooLarge"));
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法处理便签编辑消息：{Id}", ex);
        }
    }

    private async Task SendEditorLoadAsync()
    {
        if (!_shellInitialized || Editor.CoreWebView2 is null) return;
        _ = Editor.Focus(FocusState.Programmatic);
        string script = $"window.__tuckpane?.load({JsonSerializer.Serialize(_document.Html)}," +
            $"{_definition.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
            $"{JsonSerializer.Serialize(_windowChrome.BuildEditorCssPalette(_definition.Theme))},{JsonSerializer.Serialize(_definition.ShowRuledLines)}," +
            $"{JsonSerializer.Serialize(AppStrings.Get("NotePlaceholder"))}," +
            $"{JsonSerializer.Serialize(AppStrings.GetLanguageTag(AppStrings.Language))}," +
            $"{JsonSerializer.Serialize(AppStrings.Get("NotePastedImage"))}," +
            $"{JsonSerializer.Serialize(AppStrings.Get("NoteResizeImage"))}," +
            $"{JsonSerializer.Serialize(AppLogger.PerformanceTraceEnabled)})";
        await Editor.CoreWebView2.ExecuteScriptAsync(script);
    }

    private async Task FocusEditorAsync()
    {
        if (!_editorReady || Editor.CoreWebView2 is not { } core) return;
        _ = Editor.Focus(FocusState.Programmatic);
        await core.ExecuteScriptAsync("document.getElementById('editor')?.focus()");
    }

    private async Task ReadEditorStateAsync()
    {
        if (!_editorReady || Editor.CoreWebView2 is null) return;
        string json = await Editor.CoreWebView2.ExecuteScriptAsync("window.__tuckpane?.getState()");
        if (string.IsNullOrWhiteSpace(json) || json == "null") return;
        using JsonDocument state = JsonDocument.Parse(json);
        if (state.RootElement.ValueKind != JsonValueKind.Object) return;
        if (state.RootElement.TryGetProperty("html", out JsonElement html))
        {
            string value = html.GetString() ?? string.Empty;
            if (value.Length <= NoteStore.MaximumHtmlLength) _document.Html = value;
        }
        if (state.RootElement.TryGetProperty("fontSize", out JsonElement size) && size.TryGetDouble(out double fontSize))
            _definition.FontSize = Math.Clamp(fontSize, OrganizerNoteRules.MinimumFontSize, OrganizerNoteRules.MaximumFontSize);
    }

    private async Task<bool> FlushAsync(bool readEditor)
    {
        _saveTimer.Stop();
        return await FlushCoreAsync(readEditor);
    }

    internal async Task BeginDirectoryRenameAsync()
    {
        _saveTimer.Stop();
        if (await FlushCoreAsync(readEditor: true, holdForDirectoryRename: true))
        {
            WindowRoot.IsHitTestVisible = false;
            return;
        }
        throw new IOException(AppStrings.Get("NoteDragSaveFailed"));
    }

    internal void EndDirectoryRename()
    {
        WindowRoot.IsHitTestVisible = true;
        _saveSequence.EndDirectoryRename();
    }

    private async Task<bool> FlushCoreAsync(bool readEditor, bool holdForDirectoryRename = false)
    {
        long traceStart = AppLogger.PerformanceTraceEnabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            await _saveSequence.SaveAsync(readEditor, holdForDirectoryRename);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法保存便签：{Id}", ex);
            ShowError(AppStrings.Format("NoteSaveErrorFormat", ex.Message));
            return false;
        }
        finally
        {
            if (traceStart != 0) AppLogger.Performance($"note-save id={Id} totalMs={Stopwatch.GetElapsedTime(traceStart).TotalMilliseconds:F2}");
        }
    }

    private Func<Task> CaptureWrite()
    {
        if (_externalPath is null)
        {
            var snapshot = new NoteDocument { Html = _document.Html };
            return async () =>
            {
                await _store.SaveAsync(Id, snapshot);
                await _host.SaveStateAsync();
            };
        }
        var portable = new PortableNoteDocument
        {
            Theme = _definition.Theme,
            FontSize = _definition.FontSize,
            ShowRuledLines = _definition.ShowRuledLines,
            Placement = PortableDocumentWindowChrome.ToPortablePlacement(_definition.Placement),
            Html = _document.Html
        };
        string path = _externalPath;
        return () => Task.Run(() => _store.SavePortableAsync(path, portable));
    }

    private async Task EnsureDocumentLoadedAsync()
    {
        _documentLoadTask ??= _store.LoadAsync(Id);
        _document = await _documentLoadTask;
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e) =>
        _windowChrome.ShowThemeFlyout(_definition.Theme, "Note");

    private async void OnThemeSelected(NoteTheme theme)
    {
        try
        {
            await _host.SetNoteThemeAsync(theme);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法保存全局便签主题：{theme}", ex);
            ShowError(AppStrings.Format("NoteThemeChangeErrorFormat", ex.Message));
        }
    }

    private static void CoreWebView2_NewWindowRequested(
        Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs eventArgs) =>
        eventArgs.Handled = true;

    private void CoreWebView2_NavigationStarting(
        Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        if (_editorUri is null || !Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out Uri? target) ||
            !target.IsFile || !Path.GetFullPath(target.LocalPath)
                .Equals(Path.GetFullPath(_editorUri.LocalPath), StringComparison.OrdinalIgnoreCase))
            eventArgs.Cancel = true;
    }

    private async void SaveTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        try { _ = await FlushAsync(readEditor: false); }
        catch (Exception ex) { AppLogger.Error($"定时保存便签失败：{Id}", ex); }
    }

    private async void RuledLinesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _definition.ShowRuledLines = HeaderBar.OptionToggleButton.IsChecked == true;
            if (_editorReady && Editor.CoreWebView2 is not null)
                await Editor.CoreWebView2.ExecuteScriptAsync(
                    $"window.__tuckpane?.setRuledLines({JsonSerializer.Serialize(_definition.ShowRuledLines)})");
            await PersistMetadataAsync();
            await FocusEditorAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"切换便签横线失败：{Id}", ex);
            ShowError(AppStrings.Format("NoteEditorErrorFormat", ex.Message));
        }
    }

    private async Task PersistMetadataAsync()
    {
        if (_externalPath is null) await _host.SaveStateAsync();
        else await FlushAsync(readEditor: false);
    }

    private void ApplyTheme() => _ = _windowChrome.ApplyChromeTheme(_definition.Theme, showFrameBorder: false);

    private async void WindowRoot_ActualThemeChanged(FrameworkElement sender, object args)
    {
        try
        {
            ApplyTheme();
            if (_editorReady && Editor.CoreWebView2 is not null)
                await Editor.CoreWebView2.ExecuteScriptAsync(
                    $"window.__tuckpane?.setTheme({JsonSerializer.Serialize(_windowChrome.BuildEditorCssPalette(_definition.Theme))})");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"同步便签系统主题失败：{Id}", ex);
        }
    }

    private async Task CommitTitleRenameAsync(string candidate)
    {
        try
        {
            if (_externalPath is null)
            {
                if (_organizerId is not Guid organizerId) throw new InvalidOperationException(AppStrings.Get("NoteSaveErrorFormat"));
                await _host.RenameNoteAsync(organizerId, Id, candidate);
            }
            else
            {
                await _host.RenameExternalNoteAsync(_externalPath, candidate);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法重命名便签：{Id}", ex);
            throw;
        }
    }

    private void NoteWindow_Closed(object sender, WindowEventArgs args)
    {
        Closed -= NoteWindow_Closed;
        Activated -= NoteWindow_Activated;
        WindowRoot.ActualThemeChanged -= WindowRoot_ActualThemeChanged;
        HeaderBar.ColorButton.Click -= ColorButton_Click;
        HeaderBar.OptionToggleButton.Click -= RuledLinesButton_Click;
        if (_coreWebView is not null)
        {
            _coreWebView.NewWindowRequested -= CoreWebView2_NewWindowRequested;
            _coreWebView.NavigationStarting -= CoreWebView2_NavigationStarting;
            _coreWebView.WebMessageReceived -= CoreWebView2_WebMessageReceived;
            _coreWebView = null;
        }
        _saveTimer.Stop();
        _saveTimer.Tick -= SaveTimer_Tick;
        _editorReady = false;
        _windowChrome.Dispose();
        Editor.Close();
    }

    private void ShowError(string message) => _windowChrome.ShowError(message);
}
