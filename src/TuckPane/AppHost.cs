using TuckPane.Models;
using TuckPane.Services;
using TuckPane.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace TuckPane;

public sealed partial class AppHost : IDisposable
{
    private sealed record OrganizerReleasePlacement(
        OrganizerDefinition Organizer,
        NativeMethods.RECT Bounds,
        double? RuntimeScale);

    private readonly StateStore _stateStore = new();
    private readonly FolderOrganizerCreation _shellOrganizerCreation = new();
    internal FolderContextMenuService FolderContextMenu { get; } = FolderContextMenuService.CreateForCurrentProcess();
    internal FolderContextMenuService DesktopContextMenu { get; } = FolderContextMenuService.CreateForCurrentProcess(desktop: true);
    private readonly DesktopGridService _desktopGrid = new();
    private readonly Dictionary<Guid, MainWindow> _windows = [];
    private readonly Dictionary<Guid, NoteWindow> _noteWindows = [];
    private readonly HashSet<Guid> _trayHiddenNotes = [];
    private readonly SemaphoreSlim _globalSettingsGate = new(1, 1);
    private readonly NoteStore _noteStore = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private TrayIconService? _tray;
    private MainWindow? _expandedWindow;
    private MainWindow? _organizerDragHoverSource;
    private MainWindow? _organizerDragHoverTarget;
    private Task? _organizerDragHoverTask;
    private IDisposable? _organizerDragHoverHold;
    private NativeMethods.RECT _organizerDragHoverBounds;
    private bool _transparencyNoticeShown;
    private bool _gridFallbackNoticeShown;
    private bool _disposed;
    private readonly ExitPreparation _exitPreparation = new();

    public AppStateV2 State { get; private set; } = new();
    public TransferQueue TransferQueue { get; } = new();
    internal ShellLaunchService ShellLauncher { get; } = new();
    public ConsoleWindow Console { get; private set; } = null!;
    internal AppUpdateService Updates { get; private set; } = null!;
    internal bool IsPreparingUpdate { get; private set; }
    public IReadOnlyCollection<MainWindow> Windows => _windows.Values;
    internal event EventHandler? ThemeChanged;
    internal ThemeTarget? LastChangedThemeTarget { get; private set; }

    public async Task SetOrganizerTextColorAsync(OrganizerTextColor color)
    {
        color = GlobalSettings.NormalizeOrganizerTextColor(color);
        if (!await MutateGlobalSettingAsync(s => s.OrganizerTextColor, (s, v) => s.OrganizerTextColor = v, color)) return;
        LastChangedThemeTarget = null;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    internal IReadOnlyList<WindowAlignmentTarget> GetWindowAlignmentTargets(MainWindow source, DisplayInfo display)
    {
        int capacity = Math.Max(0, _windows.Count - 1);
        if (capacity == 0) return [];
        var targets = new List<WindowAlignmentTarget>(capacity);
        foreach (MainWindow window in _windows.Values)
        {
            if (ReferenceEquals(window, source) || !window.TryGetWindowAlignmentFrame(out NativeMethods.RECT bounds)) continue;
            if (!string.Equals(DisplayPlacementService.ForBounds(bounds).Device, display.Device, StringComparison.OrdinalIgnoreCase)) continue;
            targets.Add(new WindowAlignmentTarget(window.OrganizerId, bounds));
        }
        if (targets.Count > 1)
            targets.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        return targets.Count == 0 ? [] : targets.ToArray();
    }

    internal IReadOnlyList<WidgetItem> GetContainedOrganizerItems(Guid containerId) =>
        State.Organizers
            .Where(organizer => organizer.ContainerOrganizerId == containerId)
            .Select(organizer => new WidgetItem(
                organizer.Name,
                AppPaths.ResolveStoragePath(organizer),
                OrganizerContainment.ItemKey(organizer.Id),
                WidgetItemKind.Organizer,
                organizerId: organizer.Id))
            .ToArray();

    internal IReadOnlyList<WidgetItem> GetOrganizerPreviewItems(Guid organizerId) =>
        _windows.TryGetValue(organizerId, out MainWindow? window)
            ? window.ItemSnapshot.Take(4).Select(item => item.CopyValue()).ToArray()
            : [];

    internal void NotifyOrganizerPreviewChanged(Guid organizerId)
    {
        Guid currentId = organizerId;
        while (State.Organizers.FirstOrDefault(candidate => candidate.Id == currentId) is
               { ContainerOrganizerId: Guid containerId })
        {
            if (_windows.TryGetValue(containerId, out MainWindow? container))
                container.RefreshOrganizerPreview(currentId);
            currentId = containerId;
        }
    }

    public async Task InitializeAsync()
    {
        long startupStarted = Environment.TickCount64;
        AppPaths.EnsureCreated();
        // Age out icon cache leftovers in the background; startup must not wait for disk cleanup.
        _ = IconCacheService.SweepAsync();
        State = await _stateStore.LoadAsync();
        AppStrings.SetLanguage(State.GlobalSettings.Language);
        try { FolderContextMenu.RepairIfOwned(AppStrings.Get("FolderMenuCommand")); }
        catch (Exception ex) { AppLogger.Error("无法修复当前安装副本的文件夹菜单。", ex); }
        try { DesktopContextMenu.InitializeDesktop(AppStrings.Get("DesktopMenuCommand"), FolderContextMenuService.IsInstalledCopy()); }
        catch (Exception ex) { AppLogger.Error("无法初始化桌面右键菜单。", ex); }
        RefreshFolderContextMenuLabel();
        await MigrateLegacyOrganizerNotesAsync();
        try { StartupService.Apply(State.GlobalSettings.StartWithWindows); }
        catch (Exception ex) { AppLogger.Error("无法同步开机自启注册表项。", ex); }

        Updates = new AppUpdateService();
        Console = new ConsoleWindow(this);
        Console.InitializeHostWindow();
        _tray = new TrayIconService(Console.Hwnd, () => State.GlobalSettings.StartWithWindows, () => TransferQueue.IsActive,
            HandleTrayCommand, LifecycleWindowCounts, () => IsPeekActive);
        TransferQueue.StateChanged += TransferQueue_StateChanged;

        if (NormalizePositionedPlacementsOnStartup() | NormalizeStationPlacementsOnStartup()) await SaveStateAsync();
        foreach (OrganizerDefinition organizer in State.Organizers) CreateWindow(organizer);
        InitializeProductivityServices();
        Console.RefreshAll();
        _ = Updates.CheckAsync(false);
        AppLogger.Performance($"startup organizers={State.Organizers.Count} elapsedMs={Environment.TickCount64 - startupStarted}");
    }

    public async Task<OrganizerDefinition> CreateOrganizerAsync(OrganizerDefinition draft, string? storagePath = null)
    {
        draft.ContainerOrganizerId = null;
        OrganizerExpansion.Normalize(draft);
        if (draft.PlacementMode == OrganizerPlacementMode.Station)
            draft.ExpandedContentMode = OrganizerExpandedContentMode.Icon;
        if (draft.PlacementMode == OrganizerPlacementMode.Station)
        {
            if (State.Organizers.Any(item => item.PlacementMode == OrganizerPlacementMode.Station && item.DockEdge == draft.DockEdge))
                throw new InvalidOperationException(AppStrings.Get("StationEdgeOccupiedError"));
        }
        else if (!OrganizerKinds.CanCreate(draft.PlacementMode, State.Organizers))
        {
            throw new ArgumentOutOfRangeException(nameof(draft.PlacementMode));
        }
        Guid id = draft.Id;
        if (id == Guid.Empty || State.Organizers.Any(item => item.Id == id))
            throw new InvalidOperationException(AppStrings.Get("OrganizerIdOccupied"));
        draft.Name = string.IsNullOrWhiteSpace(draft.Name) ? AppStrings.DefaultOrganizerName : draft.Name.Trim();
        draft.CreatedAtUtc = DateTimeOffset.UtcNow;
        string itemsPath;
        string? defaultStorageDirectory = null;
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            defaultStorageDirectory = AppPaths.ResolveDefaultStorageDirectory(State.GlobalSettings);
            itemsPath = AppPaths.CreateDefaultOrganizerStoragePath(defaultStorageDirectory, id);
            draft.StorageRelativePath = string.Empty;
            draft.StorageAbsolutePath = itemsPath;
            draft.StorageOwnedByApp = true;
        }
        else
        {
            itemsPath = FolderOrganizerCreation.BindExistingStorage(draft, storagePath, State.Organizers);
        }

        draft.CompactScale = State.GlobalSettings.ResolveCompactScale(draft.PlacementMode, draft.CompactScale);
        DisplayInfo primary = DisplayPlacementService.GetDisplay();
        DisplayInfo selectedDisplay = DisplayPlacementService.GetDisplay(draft.Position?.MonitorDevice);
        NativeMethods.RECT bounds;
        if (draft.PlacementMode == OrganizerPlacementMode.Station)
        {
            bounds = DisplayPlacementService.CalculateStationAnchor(selectedDisplay, draft.DockEdge);
        }
        else if (draft.PlacementMode == OrganizerPlacementMode.Positioned)
        {
            DesktopGridPlacement? placement = FindPositionedPlacement(
                primary,
                desiredCenter: null,
                excludeId: id,
                draft.CompactScale);
            if (placement is null) throw new InvalidOperationException(AppStrings.Get("NoPrimaryGridError"));
            bounds = placement.Bounds;
        }
        else
        {
            int width = Math.Max(1, (int)Math.Round(OrganizerLimits.CompactWindowWidthDip * draft.CompactScale * selectedDisplay.Scale));
            int height = Math.Max(1, (int)Math.Round(OrganizerLimits.CompactWindowHeightDip * draft.CompactScale * selectedDisplay.Scale));
            bounds = DisplayPlacementService.FindAvailable(
                selectedDisplay,
                _windows.Values.Select(window => window.CompactBounds).ToArray(),
                width,
                height);
        }
        draft.Position = DisplayPlacementService.Capture(bounds);

        bool createdStorage = false;
        bool stateAdded = false;
        try
        {
            if (defaultStorageDirectory is not null)
            {
                itemsPath = AppPaths.CreateDefaultOrganizerStorageDirectory(
                    defaultStorageDirectory,
                    id,
                    State.Organizers.Select(AppPaths.ResolveStoragePath));
                createdStorage = true;
            }
            State.Organizers.Add(draft);
            stateAdded = true;
            await SaveStateAsync();
            CreateWindow(draft);
            Console.RefreshAll();
            return draft;
        }
        catch
        {
            if (stateAdded)
            {
                State.Organizers.RemoveAll(item => item.Id == draft.Id);
                try { await SaveStateAsync(); }
                catch (Exception rollbackError) { AppLogger.Error("无法回滚创建收纳窗的状态。", rollbackError); }
            }
            if (createdStorage) TryDeleteEmptyCreatedStorage(itemsPath);
            throw;
        }
    }

    public Task CreateDesktopOrganizerAsync()
        => CreateShellOrganizerAsync(static () => null);

    public Task CreateFolderOrganizerAsync(string storagePath)
        => CreateShellOrganizerAsync(() => storagePath);

    internal Task CreateFolderOrganizerFromArgumentsAsync(string[] arguments)
        => CreateShellOrganizerAsync(() => FolderOrganizerCreation.ParseArguments(arguments));

    internal async Task CreateExternalNoteAsync(string directory)
    {
        if (IsPreparingUpdate) return;
        try
        {
            string targetDirectory = NoteStore.ValidatePortableDirectory(directory);
            NativeMethods.POINT anchor;
            if (!NativeMethods.GetCursorPos(out anchor))
            {
                DisplayInfo display = DisplayPlacementService.GetDisplay();
                anchor = new NativeMethods.POINT { X = display.Work.Left + 16, Y = display.Work.Top + 16 };
            }
            var definition = new NoteDefinition
            {
                Name = "新建便签",
                Theme = State.GlobalSettings.NoteTheme,
                FontSize = 14,
                ShowRuledLines = false,
                Placement = CreateNotePlacement(anchor)
            };
            string path = await _noteStore.CreatePortableAsync(
                targetDirectory,
                definition.Name,
                ToPortableDocument(definition, new NoteDocument()));
            await OpenExternalNoteAsync(path);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法在目标目录创建便签：{directory}", ex);
            ReportOperationError(AppStrings.Get("PortableNoteCreateErrorTitle"), ex.Message);
        }
    }

    private Task CreateShellOrganizerAsync(Func<string?> resolveStoragePath)
    {
        return FolderOrganizerCreation.RunWithFeedbackAsync(
            () => _shellOrganizerCreation.CreateAsync(resolveStoragePath(), State.GlobalSettings, State.Organizers, async (draft, path) =>
            {
                DisplayInfo display = NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)
                    ? DisplayPlacementService.ForBounds(new NativeMethods.RECT
                    {
                        Left = cursor.X,
                        Top = cursor.Y,
                        Right = cursor.X + 1,
                        Bottom = cursor.Y + 1
                    })
                    : DisplayPlacementService.GetDisplay();
                draft.Position = new WidgetPosition { MonitorDevice = display.Device };
                await CreateOrganizerAsync(draft, path);
            }),
            ShowOrganizerCreationErrorAsync,
            ex => AppLogger.Error("Shell 收纳窗创建或错误提示失败。", ex),
            message => ReportOperationError(AppStrings.Get("CreateErrorTitle"), message));
    }

    private Task ShowOrganizerCreationErrorAsync(string message)
    {
        DisplayInfo display = NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)
            ? DisplayPlacementService.ForBounds(new NativeMethods.RECT
            {
                Left = cursor.X, Top = cursor.Y, Right = cursor.X + 1, Bottom = cursor.Y + 1
            })
            : DisplayPlacementService.GetDisplay();
        return ShowOperationErrorAsync(AppStrings.Get("CreateErrorTitle"), message, monitorDevice: display.Device);
    }

    public async Task<OrganizerDefinition> DuplicateOrganizerAsync(Guid id)
    {
        OrganizerDefinition source = State.Organizers.FirstOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException(AppStrings.Get("OrganizerContainmentMissingError"));
        if (source.PlacementMode == OrganizerPlacementMode.Station)
            throw new InvalidOperationException(AppStrings.Get("StationDuplicateError"));
        string name = OrganizerInteractionMath.CreateCopyName(
            source.Name,
            State.Organizers.Select(item => item.Name),
            AppStrings.Get("CopyNameSuffix"));
        var draft = OrganizerInteractionMath.CopySettings(source, name);
        draft.Notes.Clear();
        draft.ItemOrder.Clear();
        draft.ContainerOrganizerId = null;
        return await CreateOrganizerAsync(draft);
    }

    internal async Task<string> CreateNoteAsync(Guid organizerId, string? text, NativeMethods.POINT anchor)
    {
        OrganizerDefinition organizer = State.Organizers.FirstOrDefault(item => item.Id == organizerId)
            ?? throw new InvalidOperationException(AppStrings.Get("OrganizerContainmentMissingError"));
        var note = new NoteDefinition
        {
            Name = OrganizerNoteRules.CreateDefaultName(organizer.Notes.Select(item => item.Name)),
            Theme = State.GlobalSettings.NoteTheme,
            Placement = CreateNotePlacement(anchor)
        };
        string path = await CreateOrganizerPortableNoteAsync(
            organizer,
            _noteStore,
            note.Name,
            ToPortableDocument(note, new NoteDocument { Html = OrganizerNoteRules.PlainTextToHtml(text) }),
            SaveStateAsync);
        if (_windows.TryGetValue(organizerId, out MainWindow? window))
        {
            try { await window.RefreshNotesAsync(); }
            catch (Exception ex) { AppLogger.Error($"新建便签后刷新失败：{path}", ex); }
        }
        return path;
    }

    internal async Task<string> CreateTodoAsync(Guid organizerId, NativeMethods.POINT anchor)
    {
        OrganizerDefinition organizer = State.Organizers.FirstOrDefault(item => item.Id == organizerId)
            ?? throw new InvalidOperationException(AppStrings.Get("OrganizerContainmentMissingError"));
        string storagePath = AppPaths.ResolveStoragePath(organizer);
        Directory.CreateDirectory(storagePath);
        var document = new PortableTodoDocument
        {
            Theme = State.GlobalSettings.NoteTheme,
            FontSize = 14,
            Placement = CreateTodoPlacement(anchor)
        };
        string path = await _noteStore.CreateTodoAsync(storagePath, "新建待办", document);
        string relativeName = Path.GetFileName(path);
        organizer.ItemOrder.Add(relativeName);
        try { await SaveStateAsync(); }
        catch
        {
            organizer.ItemOrder.RemoveAll(key => key.Equals(relativeName, StringComparison.OrdinalIgnoreCase));
            try { File.Delete(path); }
            catch (Exception cleanupError) { AppLogger.Error($"无法回滚新建待办：{path}", cleanupError); }
            throw;
        }
        if (_windows.TryGetValue(organizerId, out MainWindow? window))
        {
            try { await window.RefreshNotesAsync(); }
            catch (Exception ex) { AppLogger.Error($"新建待办后刷新失败：{path}", ex); }
        }
        await OpenExternalTodoAsync(path);
        return path;
    }

    internal static async Task<string> CreateOrganizerPortableNoteAsync(
        OrganizerDefinition organizer,
        NoteStore noteStore,
        string name,
        PortableNoteDocument document,
        Func<Task> saveStateAsync)
    {
        string storagePath = AppPaths.ResolveStoragePath(organizer);
        Directory.CreateDirectory(storagePath);
        string path = await noteStore.CreatePortableAsync(storagePath, name, document);
        string relativeName = Path.GetFileName(path);
        organizer.ItemOrder.Add(relativeName);
        try { await saveStateAsync(); }
        catch
        {
            organizer.ItemOrder.RemoveAll(key => key.Equals(relativeName, StringComparison.OrdinalIgnoreCase));
            try { File.Delete(path); }
            catch (Exception cleanupError) { AppLogger.Error($"无法回滚新建便签：{path}", cleanupError); }
            throw;
        }
        return path;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> MigrateLegacyOrganizerNotesAsync(OrganizerDefinition? only = null)
    {
        var migrated = new Dictionary<Guid, string>();
        IEnumerable<OrganizerDefinition> organizers = only is null ? State.Organizers : [only!];
        foreach (OrganizerDefinition organizer in organizers)
            foreach ((Guid id, string path) in await MigrateLegacyOrganizerNotesAsync(organizer, _noteStore, SaveStateAsync))
                migrated[id] = path;
        return migrated;
    }

    internal static async Task<IReadOnlyDictionary<Guid, string>> MigrateLegacyOrganizerNotesAsync(
        OrganizerDefinition organizer,
        NoteStore noteStore,
        Func<Task> saveStateAsync)
    {
        var migrated = new Dictionary<Guid, string>();
        if (organizer.Notes.Count == 0) return migrated;
        string storagePath;
        try { storagePath = AppPaths.ResolveStoragePath(organizer); }
        catch (Exception ex)
        {
            AppLogger.Error($"无法解析旧便签迁移目录：{organizer.Id}", ex);
            return migrated;
        }
        if (!Directory.Exists(storagePath)) return migrated;

        foreach (NoteDefinition note in organizer.Notes.ToArray())
        {
            string? createdPath = null;
            int noteIndex = organizer.Notes.IndexOf(note);
            string legacyKey = OrganizerNoteRules.ItemKey(note.Id);
            int orderIndex = organizer.ItemOrder.FindIndex(key => key.Equals(legacyKey, StringComparison.OrdinalIgnoreCase));
            try
            {
                NoteDocument document = await noteStore.LoadAsync(note.Id);
                createdPath = await noteStore.CreatePortableAsync(storagePath, note.Name, ToPortableDocument(note, document));
                _ = await noteStore.LoadPortableAsync(createdPath);
                string relativeName = Path.GetFileName(createdPath);
                organizer.Notes.Remove(note);
                organizer.ItemOrder.RemoveAll(key => key.Equals(legacyKey, StringComparison.OrdinalIgnoreCase));
                if (orderIndex >= 0) organizer.ItemOrder.Insert(Math.Min(orderIndex, organizer.ItemOrder.Count), relativeName);
                else organizer.ItemOrder.Add(relativeName);
                try
                {
                    await saveStateAsync();
                }
                catch
                {
                    organizer.Notes.Insert(Math.Min(noteIndex, organizer.Notes.Count), note);
                    organizer.ItemOrder.RemoveAll(key => key.Equals(relativeName, StringComparison.OrdinalIgnoreCase));
                    if (orderIndex >= 0) organizer.ItemOrder.Insert(Math.Min(orderIndex, organizer.ItemOrder.Count), legacyKey);
                    throw;
                }
                migrated[note.Id] = createdPath;
                try { await noteStore.DeleteAsync(note.Id); }
                catch (Exception cleanupError) { AppLogger.Error($"无法清理已迁移旧便签：{note.Id}", cleanupError); }
            }
            catch (Exception ex)
            {
                if (createdPath is not null)
                {
                    try { File.Delete(createdPath); }
                    catch (Exception cleanupError) { AppLogger.Error($"无法回滚旧便签迁移文件：{createdPath}", cleanupError); }
                }
                AppLogger.Error($"旧便签迁移失败，将在下次启动重试：{note.Id}", ex);
            }
        }
        return migrated;
    }

    internal void OpenNote(Guid organizerId, Guid noteId)
    {
        if (IsPreparingUpdate) return;
        if (State.Organizers.FirstOrDefault(item => item.Id == organizerId) is not OrganizerDefinition organizer) return;
        if (organizer.Notes.FirstOrDefault(item => item.Id == noteId) is not NoteDefinition note) return;
        if (!_noteWindows.TryGetValue(noteId, out NoteWindow? window))
        {
            window = new NoteWindow(this, note, _noteStore, organizerId: organizerId);
            window.InitializeHostWindow();
            _noteWindows[noteId] = window;
        }
        window.ShowAndActivate();
    }

    internal async Task<(string Path, bool RestoreWindow)> PrepareNoteDragAsync(Guid organizerId, Guid noteId)
    {
        OrganizerDefinition organizer = State.Organizers.FirstOrDefault(item => item.Id == organizerId)
            ?? throw new InvalidOperationException(AppStrings.Get("OrganizerContainmentMissingError"));
        NoteDefinition note = organizer.Notes.FirstOrDefault(item => item.Id == noteId)
            ?? throw new InvalidOperationException(AppStrings.Get("OrganizerContainmentMissingError"));
        bool restoreWindow = false;
        try
        {
            if (_noteWindows.TryGetValue(noteId, out NoteWindow? window))
                restoreWindow = await window.FlushAndHideForDragAsync();
            NoteDocument document = await _noteStore.LoadAsync(noteId);
            string path = await _noteStore.CreatePortableStagingAsync(note.Name, ToPortableDocument(note, document));
            return (path, restoreWindow);
        }
        catch
        {
            if (restoreWindow && _noteWindows.TryGetValue(noteId, out NoteWindow? window)) window.RestoreAfterDrag();
            throw;
        }
    }

    internal async Task CompleteNoteDragAsync(
        Guid organizerId,
        Guid noteId,
        string stagingPath,
        bool restoreWindow,
        bool moved)
    {
        try
        {
            if (moved)
            {
                try { await DeleteNoteAsync(organizerId, noteId); }
                catch
                {
                    if (State.Organizers.FirstOrDefault(item => item.Id == organizerId)?.Notes.Any(item => item.Id == noteId) == true)
                        OpenNote(organizerId, noteId);
                    throw;
                }
            }
            else if (restoreWindow && _noteWindows.TryGetValue(noteId, out NoteWindow? window))
            {
                window.RestoreAfterDrag();
            }
        }
        finally
        {
            CleanupNoteDragStaging(stagingPath);
        }
    }

    internal async Task RenameNoteAsync(Guid organizerId, Guid noteId, string name)
    {
        OrganizerDefinition organizer = State.Organizers.FirstOrDefault(item => item.Id == organizerId)
            ?? throw new InvalidOperationException(AppStrings.Get("OrganizerContainmentMissingError"));
        NoteDefinition note = organizer.Notes.FirstOrDefault(item => item.Id == noteId)
            ?? throw new InvalidOperationException(AppStrings.Get("OrganizerContainmentMissingError"));
        string candidate = name.Trim();
        if (candidate.Length == 0) throw new InvalidOperationException(AppStrings.Get("NoteNameRequired"));
        if (!OrganizerNoteRules.IsNameAvailable(
                organizer.Notes.Where(item => item.Id != noteId).Select(item => item.Name),
                candidate))
            throw new InvalidOperationException(AppStrings.Get("NoteNameDuplicate"));
        string previous = note.Name;
        note.Name = candidate;
        try { await SaveStateAsync(); }
        catch
        {
            note.Name = previous;
            throw;
        }
        if (_noteWindows.TryGetValue(noteId, out NoteWindow? noteWindow)) noteWindow.UpdateTitle();
        if (_windows.TryGetValue(organizerId, out MainWindow? window))
        {
            try { await window.RefreshNotesAsync(); }
            catch (Exception ex) { AppLogger.Error($"便签改名后刷新失败：{noteId}", ex); }
        }
    }

    internal async Task SetNoteThemeAsync(NoteTheme theme)
    {
        if (!_dispatcher.HasThreadAccess)
            throw new InvalidOperationException("Note themes must be selected on the UI dispatcher.");
        IReadOnlyList<NoteThemeFailure> failures = await _noteThemeSynchronization.SetAsync(
            State.GlobalSettings, () => State.Organizers.SelectMany(organizer => organizer.Notes).ToArray(),
            theme, SaveStateAsync, () => CaptureNoteThemeTargets(theme));
        foreach (NoteThemeFailure failure in failures)
            AppLogger.Error($"主题同步失败：{failure.Target}", failure.Error);
        if (failures.Count > 0)
            throw new InvalidOperationException(AppStrings.Format("NoteThemeSyncErrorFormat", failures.Count),
                new AggregateException(failures.Select(failure => failure.Error)));
    }

    private readonly NoteThemeSynchronization _noteThemeSynchronization = new();

    private IReadOnlyList<NoteThemeTarget> CaptureNoteThemeTargets(NoteTheme theme)
    {
        var targets = new List<NoteThemeTarget>();
        foreach (NoteWindow window in _noteWindows.Values.Concat(OpenExternalNoteWindows).ToArray())
            targets.Add(new($"note:{window.Id}", () => window.ApplyGlobalThemeAsync(theme)));
        foreach (TodoWindow window in OpenExternalTodoWindows.ToArray())
            targets.Add(new($"todo:{window.ExternalPath}", () => window.ApplyGlobalThemeAsync(theme)));
        HashSet<string> openNotePaths = _portableDocuments.GetOpenPaths(PortableDocumentKind.Note);
        HashSet<string> openTodoPaths = _portableDocuments.GetOpenPaths(PortableDocumentKind.Todo);
        var visitedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (OrganizerDefinition organizer in State.Organizers.ToArray())
        {
            string root;
            try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.ResolveStoragePath(organizer))); }
            catch (Exception ex)
            {
                targets.Add(new($"directory:{organizer.Id}", () => Task.FromException(ex)));
                continue;
            }
            if (!visitedRoots.Add(root)) continue;
            targets.Add(new($"note-files:{root}", async () =>
            {
                IReadOnlyList<string> failed = await _noteStore.ApplyThemeToTopLevelPortableFilesAsync(root, theme, openNotePaths);
                if (failed.Count > 0) throw new IOException(string.Join(Environment.NewLine, failed));
            }));
            targets.Add(new($"todo-files:{root}", async () =>
            {
                IReadOnlyList<string> failed = await _noteStore.ApplyThemeToTopLevelTodoFilesAsync(root, theme, openTodoPaths);
                if (failed.Count > 0) throw new IOException(string.Join(Environment.NewLine, failed));
            }));
        }
        return targets;
    }

    internal async Task DeleteNoteAsync(Guid organizerId, Guid noteId)
    {
        if (State.Organizers.FirstOrDefault(item => item.Id == organizerId) is not OrganizerDefinition organizer) return;
        if (organizer.Notes.FirstOrDefault(item => item.Id == noteId) is not NoteDefinition note) return;
        int index = organizer.Notes.IndexOf(note);
        int orderIndex = organizer.ItemOrder.FindIndex(key =>
            key.Equals(OrganizerNoteRules.ItemKey(noteId), StringComparison.OrdinalIgnoreCase));
        if (_noteWindows.Remove(noteId, out NoteWindow? noteWindow)) await noteWindow.ClosePermanentlyAsync();
        _trayHiddenNotes.Remove(noteId);
        organizer.Notes.RemoveAt(index);
        organizer.ItemOrder.RemoveAll(key => key.Equals(OrganizerNoteRules.ItemKey(noteId), StringComparison.OrdinalIgnoreCase));
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            organizer.Notes.Insert(Math.Min(index, organizer.Notes.Count), note);
            organizer.ItemOrder.Insert(orderIndex < 0 ? organizer.ItemOrder.Count : Math.Min(orderIndex, organizer.ItemOrder.Count), OrganizerNoteRules.ItemKey(noteId));
            throw;
        }
        try { await _noteStore.DeleteAsync(noteId); }
        catch (Exception ex) { AppLogger.Error($"无法删除便签文件：{noteId}", ex); }
        if (_windows.TryGetValue(organizerId, out MainWindow? window)) await window.RefreshNotesAsync();
    }

    private static NoteWindowPlacement CreateNotePlacement(NativeMethods.POINT anchor)
    {
        DisplayInfo display = DisplayPlacementService.ForBounds(new NativeMethods.RECT
        {
            Left = anchor.X,
            Top = anchor.Y,
            Right = anchor.X + 1,
            Bottom = anchor.Y + 1
        });
        int width = (int)Math.Round(360 * display.Scale);
        int height = (int)Math.Round(300 * display.Scale);
        int offset = (int)Math.Round(16 * display.Scale);
        NativeMethods.RECT bounds = DisplayPlacementService.Clamp(new NativeMethods.RECT
        {
            Left = anchor.X + offset,
            Top = anchor.Y + offset,
            Right = anchor.X + offset + width,
            Bottom = anchor.Y + offset + height
        }, display.Work);
        return new NoteWindowPlacement
        {
            MonitorDevice = display.Device,
            XDip = (bounds.Left - display.Work.Left) / display.Scale,
            YDip = (bounds.Top - display.Work.Top) / display.Scale,
            WidthDip = bounds.Width / display.Scale,
            HeightDip = bounds.Height / display.Scale
        };
    }

    private static PortableNotePlacement CreateTodoPlacement(NativeMethods.POINT anchor)
    {
        DisplayInfo display = DisplayPlacementService.ForBounds(new NativeMethods.RECT
        {
            Left = anchor.X,
            Top = anchor.Y,
            Right = anchor.X + 1,
            Bottom = anchor.Y + 1
        });
        int width = (int)Math.Round(360 * display.Scale);
        int height = (int)Math.Round(480 * display.Scale);
        int offset = (int)Math.Round(16 * display.Scale);
        NativeMethods.RECT bounds = DisplayPlacementService.Clamp(new NativeMethods.RECT
        {
            Left = anchor.X + offset,
            Top = anchor.Y + offset,
            Right = anchor.X + offset + width,
            Bottom = anchor.Y + offset + height
        }, display.Work);
        return new PortableNotePlacement
        {
            MonitorDevice = display.Device,
            XDip = (bounds.Left - display.Work.Left) / display.Scale,
            YDip = (bounds.Top - display.Work.Top) / display.Scale,
            WidthDip = bounds.Width / display.Scale,
            HeightDip = bounds.Height / display.Scale
        };
    }

    private static PortableNoteDocument ToPortableDocument(NoteDefinition note, NoteDocument document) => new()
    {
        Theme = note.Theme,
        FontSize = note.FontSize,
        ShowRuledLines = note.ShowRuledLines,
        Placement = note.Placement is null ? null : new PortableNotePlacement
        {
            MonitorDevice = note.Placement.MonitorDevice,
            XDip = note.Placement.XDip,
            YDip = note.Placement.YDip,
            WidthDip = note.Placement.WidthDip,
            HeightDip = note.Placement.HeightDip
        },
        Html = document.Html
    };

    private static NoteWindowPlacement? FromPortablePlacement(PortableNotePlacement? placement) => placement is null
        ? null
        : new NoteWindowPlacement
        {
            MonitorDevice = placement.MonitorDevice,
            XDip = placement.XDip,
            YDip = placement.YDip,
            WidthDip = placement.WidthDip,
            HeightDip = placement.HeightDip
        };

    private static void CleanupNoteDragStaging(string path)
    {
        try
        {
            string root = Path.GetFullPath(AppPaths.NoteStagingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(fullPath)) File.Delete(fullPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法清理便签拖放暂存文件：{path}", ex);
        }
    }

    public string ValidateStoragePath(string path)
    {
        string normalized = AppPaths.ValidateCustomStoragePath(path);
        foreach (OrganizerDefinition organizer in State.Organizers)
        {
            if (AppPaths.PathsOverlap(normalized, AppPaths.ResolveStoragePath(organizer)))
                throw new InvalidOperationException(AppStrings.Get("StoragePathOverlap"));
        }
        return normalized;
    }

    public async Task<string?> ToggleOrganizerModeAsync(Guid id)
    {
        if (State.Organizers.FirstOrDefault(item => item.Id == id) is not OrganizerDefinition initial) return null;
        if (!OrganizerKinds.IsRegular(initial.PlacementMode)) return AppStrings.Get("StationManageModeError");
        if (_windows.TryGetValue(id, out MainWindow? window) && window.IsExpanded)
        {
            await window.CollapseForPeerAsync();
        }
        if (State.Organizers.FirstOrDefault(item => item.Id == id) is not OrganizerDefinition current) return null;

        OrganizerDefinition edited = OrganizerInteractionMath.CopySettings(current, current.Name);
        edited.Id = current.Id;
        edited.PlacementMode = current.PlacementMode == OrganizerPlacementMode.Floating
            ? OrganizerPlacementMode.Positioned
            : OrganizerPlacementMode.Floating;
        string? error = ApplyOrganizerRuntime(
            edited,
            OrganizerVisualChange.PlacementMode | OrganizerVisualChange.CompactScale);
        if (error is not null) return error;
        await SaveStateAsync();
        Console.RefreshAll(id);
        return null;
    }

    internal async Task<string?> ToggleOrganizerExpandedContentModeAsync(Guid id)
    {
        if (State.Organizers.FirstOrDefault(item => item.Id == id) is not OrganizerDefinition current) return null;
        OrganizerDefinition edited = OrganizerInteractionMath.CopySettings(current, current.Name);
        edited.Id = current.Id;
        if (!OrganizerInteractionMath.TryToggleExpandedContentMode(edited))
            return AppStrings.Get("StationManageModeError");

        string? error = ApplyOrganizerRuntime(edited, OrganizerVisualChange.ExpandedContentMode);
        if (error is not null) return error;
        await SaveStateAsync();
        Console.RefreshAll(id);
        return null;
    }

    internal string? ApplyOrganizerRuntime(OrganizerDefinition edited, OrganizerVisualChange changes)
    {
        if (State.Organizers.FirstOrDefault(item => item.Id == edited.Id) is not OrganizerDefinition current) return null;
        if (!OrganizerInteractionMath.CanChangePlacementMode(current.PlacementMode, edited.PlacementMode))
            return AppStrings.Get("StationManageModeError");
        if (edited.PlacementMode == OrganizerPlacementMode.Station)
            edited.ExpandedContentMode = OrganizerExpandedContentMode.Icon;
        if (edited.PlacementMode == OrganizerPlacementMode.Station &&
            State.Organizers.Any(item => item.Id != edited.Id &&
                item.PlacementMode == OrganizerPlacementMode.Station && item.DockEdge == edited.DockEdge))
        {
            return AppStrings.Get("StationEdgeOccupiedError");
        }
        bool layoutChanged = current.Layout.Mode != edited.Layout.Mode ||
            current.Layout.Rows != edited.Layout.Rows ||
            current.Layout.Columns != edited.Layout.Columns;
        OrganizerPlacementMode previousMode = current.PlacementMode;
        double previousCompactScale = current.CompactScale;
        WidgetPosition? previousPosition = current.Position;
        Guid[] previousContainmentChain = OrganizerContainment.GetAncestorIds(State.Organizers, current.Id).ToArray();
        // Display names are persisted only by RenameOrganizerAsync.
        current.CopyEditableSettingsFrom(edited, copyPreferencesAndDock: false, copyManualCanvasBase: false);
        if (current.PlacementMode == OrganizerPlacementMode.Dock)
            DockSettings.Apply(current, edited.DockOrientation, edited.DockIconSizeDip, edited.DockSpacingFactor);
        if (edited.PlacementMode == OrganizerPlacementMode.Station && edited.Position is not null)
            current.Position = edited.Position;
        if (layoutChanged)
        {
            current.ManualCanvasBaseWidthDip = null;
            current.ManualCanvasBaseHeightDip = null;
        }
        StateStore.Normalize(State);
        IEnumerable<Guid> affectedParentIds = previousContainmentChain;
        if (current.ContainerOrganizerId is Guid currentParent)
            affectedParentIds = affectedParentIds.Append(currentParent);
        affectedParentIds = affectedParentIds
            .Distinct()
            .ToArray();
        foreach (Guid parentId in affectedParentIds)
        {
            if (_windows.TryGetValue(parentId, out MainWindow? parentOrganizer))
                _ = parentOrganizer.RefreshContainedOrganizerItemsAsync();
        }
        if (_windows.TryGetValue(current.Id, out MainWindow? window))
        {
            bool enteringStation = previousMode != OrganizerPlacementMode.Station &&
                current.PlacementMode == OrganizerPlacementMode.Station;
            bool movingStation = current.PlacementMode == OrganizerPlacementMode.Station &&
                ((changes & (OrganizerVisualChange.Docking | OrganizerVisualChange.PlacementMode)) != 0);
            if (enteringStation || movingStation)
            {
                DisplayInfo stationDisplay = DisplayPlacementService.GetDisplay(current.Position?.MonitorDevice);
                NativeMethods.RECT anchor = DisplayPlacementService.CalculateStationAnchor(stationDisplay, current.DockEdge);
                current.Position = DisplayPlacementService.Capture(anchor);
                window.ApplyDefinition(changes & ~(OrganizerVisualChange.CompactScale | OrganizerVisualChange.NameScale | OrganizerVisualChange.PlacementMode | OrganizerVisualChange.Docking));
                window.MoveToStationPlacement(anchor);
                return null;
            }

            bool enteringPositioned = previousMode != OrganizerPlacementMode.Positioned &&
                current.PlacementMode == OrganizerPlacementMode.Positioned;
            bool resizingPositioned = current.PlacementMode == OrganizerPlacementMode.Positioned &&
                (changes & OrganizerVisualChange.CompactScale) != 0;
            if (enteringPositioned || resizingPositioned)
            {
                NativeMethods.RECT currentBounds = window.CompactBounds;
                DisplayInfo display = previousMode == OrganizerPlacementMode.Station
                    ? DisplayPlacementService.GetDisplay(previousPosition?.MonitorDevice)
                    : DisplayPlacementService.ForBounds(currentBounds);
                NativeMethods.RECT stationAnchor = DisplayPlacementService.CalculateStationAnchor(display, current.DockEdge, previousPosition);
                var center = new NativeMethods.POINT
                {
                    X = previousMode == OrganizerPlacementMode.Station ? stationAnchor.Left : currentBounds.Left + currentBounds.Width / 2,
                    Y = previousMode == OrganizerPlacementMode.Station ? stationAnchor.Top : currentBounds.Top + currentBounds.Height / 2
                };
                DesktopGridPlacement? placement = FindPositionedPlacement(
                    display,
                    center,
                    current.Id,
                    current.CompactScale);
                if (placement is null)
                {
                    current.PlacementMode = previousMode;
                    current.CompactScale = previousCompactScale;
                    current.Position = previousPosition;
                    return AppStrings.Get("PositionedRollbackError");
                }
                current.Position = DisplayPlacementService.Capture(placement.Bounds);
                window.ApplyDefinition(changes & ~(OrganizerVisualChange.CompactScale | OrganizerVisualChange.PlacementMode));
                window.MoveToPositionedPlacement(placement.Bounds, placement.CompactScale);
                if (current.ContainerOrganizerId is not null) window.SetContained(true);
                return null;
            }

            if (previousMode == OrganizerPlacementMode.Station && current.PlacementMode == OrganizerPlacementMode.Floating)
            {
                DisplayInfo display = DisplayPlacementService.GetDisplay(previousPosition?.MonitorDevice);
                int width = Math.Max(1, (int)Math.Round(OrganizerLimits.CompactWindowWidthDip * current.CompactScale * display.Scale));
                int height = Math.Max(1, (int)Math.Round(OrganizerLimits.CompactWindowHeightDip * current.CompactScale * display.Scale));
                NativeMethods.RECT[] occupied = _windows.Values
                    .Where(candidate => candidate.OrganizerId != current.Id)
                    .Select(candidate => candidate.CompactBounds)
                    .ToArray();
                NativeMethods.RECT bounds = DisplayPlacementService.FindAvailable(display, occupied, width, height);
                current.Position = DisplayPlacementService.Capture(bounds);
                window.ApplyDefinition(changes & ~(OrganizerVisualChange.CompactScale | OrganizerVisualChange.NameScale | OrganizerVisualChange.PlacementMode));
                window.MoveToFloatingPlacement(bounds);
                if (current.ContainerOrganizerId is not null) window.SetContained(true);
                return null;
            }

            window.ApplyDefinition(changes);
            if (current.ContainerOrganizerId is not null) window.SetContained(true);
        }
        return null;
    }

    internal DesktopGridPlacement? FindNearestPositionedPlacement(Guid organizerId, NativeMethods.RECT desiredBounds)
    {
        if (State.Organizers.FirstOrDefault(item => item.Id == organizerId) is not OrganizerDefinition organizer) return null;
        DisplayInfo display = DisplayPlacementService.ForBounds(desiredBounds);
        var center = new NativeMethods.POINT
        {
            X = desiredBounds.Left + desiredBounds.Width / 2,
            Y = desiredBounds.Top + desiredBounds.Height / 2
        };
        return FindPositionedPlacement(display, center, organizerId, organizer.CompactScale);
    }

    internal DesktopGridPlacement? FindCurrentPositionedPlacement(Guid organizerId, NativeMethods.RECT currentBounds)
    {
        if (State.Organizers.FirstOrDefault(item => item.Id == organizerId) is not OrganizerDefinition organizer) return null;
        DisplayInfo display = DisplayPlacementService.ForBounds(currentBounds);
        var center = new NativeMethods.POINT
        {
            X = currentBounds.Left + currentBounds.Width / 2,
            Y = currentBounds.Top + currentBounds.Height / 2
        };
        return FindPositionedPlacement(display, center, organizerId, organizer.CompactScale);
    }

    private bool TryCreateOrganizerReleasePlan(
        OrganizerDefinition container,
        out IReadOnlyList<OrganizerReleasePlacement> releasePlan,
        out string? error)
    {
        IReadOnlyList<OrganizerDefinition> children = OrganizerContainment.GetDirectChildren(State.Organizers, container.Id);
        if (children.Count == 0)
        {
            releasePlan = [];
            error = null;
            return true;
        }

        NativeMethods.RECT parentBounds = _windows.TryGetValue(container.Id, out MainWindow? parentWindow)
            ? parentWindow.CompactBounds
            : DisplayPlacementService.RestoreToDisplay(
                container.Position,
                DisplayPlacementService.GetDisplay(container.Position?.MonitorDevice),
                1,
                1);
        DisplayInfo display = DisplayPlacementService.ForBounds(parentBounds);
        var planned = new List<OrganizerReleasePlacement>(children.Count);
        OrganizerReleaseItem[] floatingItems = children
            .Where(child => child.PlacementMode == OrganizerPlacementMode.Floating)
            .Select(child =>
            {
                double nameScale = State.GlobalSettings.ResolveCompactNameScale(child.PlacementMode);
                int width = Math.Max(1, (int)Math.Round(
                    OrganizerLimits.CalculateCompactWindowWidthDip(child.CompactScale, nameScale) * display.Scale));
                int height = Math.Max(1, (int)Math.Round(
                    OrganizerLimits.CalculateCompactWindowHeightDip(child.CompactScale, nameScale) * display.Scale));
                return new OrganizerReleaseItem(child.Id, width, height);
            })
            .ToArray();
        IReadOnlyDictionary<Guid, NativeMethods.RECT> floatingBounds = OrganizerReleasePlanner.PlanFloating(
            parentBounds,
            display.Work,
            display.Scale,
            floatingItems);

        foreach (OrganizerDefinition child in children.Where(child => child.PlacementMode == OrganizerPlacementMode.Floating))
            planned.Add(new(child, floatingBounds[child.Id], null));

        OrganizerDefinition[] positionedChildren = children
            .Where(child => child.PlacementMode == OrganizerPlacementMode.Positioned)
            .ToArray();
        if (positionedChildren.Length == 0)
        {
            var floatingById = planned.ToDictionary(item => item.Organizer.Id);
            releasePlan = children.Select(child => floatingById[child.Id]).ToArray();
            error = null;
            return true;
        }

        DesktopGridSnapshot grid = ReadGridSnapshot(display);
        var occupied = _windows.Values
            .Where(window => window.OrganizerId != container.Id)
            .Where(window => State.Organizers.FirstOrDefault(candidate => candidate.Id == window.OrganizerId) is
                { PlacementMode: OrganizerPlacementMode.Positioned, ContainerOrganizerId: null })
            .Select(window => window.CompactBounds)
            .ToList();
        var parentCenter = new NativeMethods.POINT
        {
            X = parentBounds.Left + parentBounds.Width / 2,
            Y = parentBounds.Top + parentBounds.Height / 2
        };
        foreach (OrganizerDefinition child in positionedChildren)
        {
            DesktopGridPlacement? placement = DesktopGridService.Find(
                grid,
                occupied,
                parentCenter,
                child.CompactScale);
            if (placement is null)
            {
                releasePlan = [];
                error = AppStrings.Get("OrganizerReleaseNoGridError");
                return false;
            }
            occupied.Add(placement.Bounds);
            planned.Add(new(child, placement.Bounds, placement.CompactScale));
        }

        var byId = planned.ToDictionary(item => item.Organizer.Id);
        releasePlan = children.Select(child => byId[child.Id]).ToArray();
        error = null;
        return true;
    }

    public async Task<TransferOutcome> DeleteOrganizerAsync(Guid id, OrganizerDeleteDisposition disposition)
    {
        if (!Enum.IsDefined(disposition)) throw new ArgumentOutOfRangeException(nameof(disposition));
        if (TransferQueue.IsActive) return new(string.Empty, null, TransferStatus.Failed, AppStrings.Get("TransferBeforeDelete"));
        if (State.Organizers.FirstOrDefault(item => item.Id == id) is not OrganizerDefinition definition)
            return new(string.Empty, null, TransferStatus.Retained, string.Empty);
        if (!TryCreateOrganizerReleasePlan(definition, out IReadOnlyList<OrganizerReleasePlacement> releasePlan, out string? releaseError))
        {
            return new(
                AppPaths.ResolveStoragePath(definition),
                null,
                TransferStatus.Failed,
                releaseError ?? AppStrings.Get("OrganizerReleaseNoGridError"));
        }
        await CollapseContainedChildrenAsync(definition.Id);
        if (!State.Organizers.Contains(definition))
            return new(string.Empty, null, TransferStatus.Retained, string.Empty);
        var legacyWindows = new List<(Guid Id, NoteWindow Window, bool WasVisible, bool WasTrayHidden)>();
        try
        {
            foreach (NoteDefinition note in definition.Notes)
            {
                if (!_noteWindows.TryGetValue(note.Id, out NoteWindow? noteWindow)) continue;
                bool wasVisible = await noteWindow.FlushAndHideForDragAsync();
                legacyWindows.Add((note.Id, noteWindow, wasVisible, _trayHiddenNotes.Contains(note.Id)));
            }
        }
        catch (Exception ex)
        {
            foreach (var item in legacyWindows.Where(item => item.WasVisible)) item.Window.RestoreAfterDrag();
            return new(AppPaths.ResolveStoragePath(definition), null, TransferStatus.Failed, ex.Message);
        }

        IReadOnlyDictionary<Guid, string> migratedPaths = await MigrateLegacyOrganizerNotesAsync(definition);
        foreach (var item in legacyWindows)
        {
            if (!migratedPaths.TryGetValue(item.Id, out string? path))
            {
                if (item.WasVisible) item.Window.RestoreAfterDrag();
                continue;
            }
            _noteWindows.Remove(item.Id);
            _trayHiddenNotes.Remove(item.Id);
            item.Window.ClosePermanentlyWithoutSave();
            await OpenExternalNoteAsync(path);
            if (!item.WasVisible && _portableDocuments.TryGetWindow(PortableDocumentKind.Note, path, out IPortableDocumentWindow? migratedWindow) && migratedWindow is not null)
            {
                migratedWindow.HideForTray();
                if (item.WasTrayHidden) _portableDocuments.MarkHiddenForTray(PortableDocumentKind.Note, path);
            }
        }
        if (definition.Notes.Count > 0)
            return new(AppPaths.ResolveStoragePath(definition), null, TransferStatus.Failed, AppStrings.Get("DeleteLegacyNoteMigrationFailed"));

        string sourceRoot = Path.GetFullPath(AppPaths.ResolveStoragePath(definition));
        TransferOutcome outcome;
        if (disposition == OrganizerDeleteDisposition.MoveFolderToDesktop)
        {
            await _portableDocuments.WaitOpenGateAsync();
            List<PortableExportedWindow> movedPortable = [];
            try
            {
                try
                {
                    movedPortable = await _portableDocuments.FlushAndHideTopLevelAsync(sourceRoot);
                }
                catch (Exception ex)
                {
                    return new(sourceRoot, null, TransferStatus.Failed, ex.Message);
                }

                var storage = new StorageService(
                    sourceRoot,
                    createIfMissing: false,
                    ownedContainerPath: AppPaths.GetOwnedStorageContainer(definition),
                    exportEmptyDirectory: !definition.StorageOwnedByApp);
                outcome = await TransferQueue.RunAsync(token => storage.ExportToDesktopAsync(definition.Name, null, token));
                if (outcome.Status != TransferStatus.Moved)
                {
                    if (outcome is { Status: TransferStatus.CopiedSourceRetained, DestinationPath: not null })
                    {
                        foreach (string oldPath in movedPortable.Select(item => item.OldPath))
                        {
                            string? recovered = OrganizerNoteRules.RebaseTopLevelPortablePath(sourceRoot, outcome.DestinationPath, oldPath);
                            if (!File.Exists(oldPath) && recovered is not null && File.Exists(recovered))
                                RebindPortableWindowAfterMove(oldPath, recovered);
                        }
                    }
                    return outcome;
                }
                if (outcome.DestinationPath is not null)
                {
                    string destinationRoot = Path.GetFullPath(outcome.DestinationPath);
                    _portableDocuments.CommitTopLevelExport(sourceRoot, destinationRoot, movedPortable);

                    definition.StorageRelativePath = string.Empty;
                    definition.StorageAbsolutePath = destinationRoot;
                    definition.StorageOwnedByApp = false;
                }
            }
            finally
            {
                foreach (PortableExportedWindow item in movedPortable.Where(item => item.WasVisible))
                    try { item.Window.RestoreAfterDrag(); } catch (Exception ex) { AppLogger.Error("Restore exported portable document failed", ex); }
                _portableDocuments.ReleaseOpenGate();
            }
        }
        else
        {
            outcome = new(sourceRoot, sourceRoot, TransferStatus.Retained, AppStrings.Format("OrganizerDeletedFilesRetainedFormat", sourceRoot));
        }

        int stateIndex = State.Organizers.IndexOf(definition);
        OrganizerContainmentSnapshot containmentSnapshot = OrganizerContainment.Capture(State.Organizers);
        var positionSnapshot = releasePlan.ToDictionary(item => item.Organizer.Id, item => item.Organizer.Position);
        Guid? previousContainerId = definition.ContainerOrganizerId;
        OrganizerContainment.ReleaseDirectChildren(State.Organizers, definition.Id);
        foreach (OrganizerReleasePlacement placement in releasePlan)
            placement.Organizer.Position = DisplayPlacementService.Capture(placement.Bounds);
        OrganizerContainment.Detach(State.Organizers, definition.Id);
        State.Organizers.Remove(definition);
        try
        {
            await SaveStateAsync();
        }
        catch (Exception ex)
        {
            State.Organizers.Insert(Math.Max(0, stateIndex), definition);
            containmentSnapshot.Restore(State.Organizers);
            foreach (OrganizerReleasePlacement placement in releasePlan)
                placement.Organizer.Position = positionSnapshot[placement.Organizer.Id];
            if (outcome.Status == TransferStatus.Moved && outcome.DestinationPath is not null)
            {
                if (_windows.Remove(id, out MainWindow? staleWindow)) staleWindow.ClosePermanently();
                CreateWindow(definition);
            }
            return new(sourceRoot, outcome.DestinationPath, TransferStatus.Failed, ex.Message);
        }
        RemoveDeletedOrganizerFromSelection(id);
        CloseExpansionModeCoordinator(id);
        if (_windows.Remove(id, out MainWindow? window))
        {
            if (ReferenceEquals(_expandedWindow, window)) _expandedWindow = null;
            window.ClosePermanently();
        }
        foreach (OrganizerReleasePlacement placement in releasePlan)
        {
            if (!_windows.TryGetValue(placement.Organizer.Id, out MainWindow? childWindow)) continue;
            try
            {
                if (placement.RuntimeScale is double runtimeScale)
                    childWindow.MoveToPositionedPlacement(placement.Bounds, runtimeScale);
                else
                    childWindow.MoveToFloatingPlacement(placement.Bounds);
            }
            catch (Exception ex)
            {
                AppLogger.Error("父容器已删除，但子收纳窗位置刷新失败。", ex);
            }
        }
        if (previousContainerId is Guid previousStationId && _windows.TryGetValue(previousStationId, out MainWindow? previousStation))
            await previousStation.RefreshContainedOrganizerItemsAsync();
        Console.RefreshAll();
        return outcome;
    }

    public void RecreateStorage(Guid id)
    {
        if (_windows.TryGetValue(id, out MainWindow? window)) window.RecreateStorage();
        Console.RefreshAll(id);
    }

    internal void UpdateGlobalTheme(
        ThemeTarget target,
        uint colorArgb,
        double transparency,
        double blurStrength,
        bool solidColorMode = false,
        double? solidOpacity = null,
        bool? fullyTransparent = null)
    {
        GlobalSettings settings = State.GlobalSettings;
        ThemeValues previous = settings.GetTheme(target);
        ThemeValues theme = GlobalSettings.ResolveThemeUpdate(
            previous,
            colorArgb,
            transparency,
            blurStrength,
            solidColorMode,
            solidOpacity,
            fullyTransparent);
        if (settings.GetTheme(target) == theme) return;
        settings.SetTheme(target, theme);
        LastChangedThemeTarget = target;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> MutateGlobalSettingAsync<T>(
        Func<GlobalSettings, T> read,
        Action<GlobalSettings, T> write,
        T value,
        IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        await _globalSettingsGate.WaitAsync();
        try
        {
            GlobalSettings settings = State.GlobalSettings;
            T previous = read(settings);
            if (comparer.Equals(previous, value)) return false;
            write(settings, value);
            try
            {
                await SaveStateAsync();
                return true;
            }
            catch
            {
                write(settings, previous);
                throw;
            }
        }
        finally
        {
            _globalSettingsGate.Release();
        }
    }

    public async Task SetStartupAsync(bool enabled)
    {
        await _globalSettingsGate.WaitAsync();
        try
        {
            if (State.GlobalSettings.StartWithWindows == enabled) return;
            bool previous = State.GlobalSettings.StartWithWindows;
            StartupService.Apply(enabled);
            State.GlobalSettings.StartWithWindows = enabled;
            try
            {
                await SaveStateAsync();
            }
            catch
            {
                State.GlobalSettings.StartWithWindows = previous;
                StartupService.Apply(previous);
                throw;
            }
        }
        finally
        {
            _globalSettingsGate.Release();
        }
    }

    private void RefreshFolderContextMenuLabel()
    {
        try { DesktopContextMenu.UpdateLabelIfOwned(AppStrings.Get("DesktopMenuCommand")); }
        catch (Exception ex) { AppLogger.Error("无法更新桌面菜单文字。", ex); }
        try { FolderContextMenu.UpdateLabelIfOwned(AppStrings.Get("FolderMenuCommand")); }
        catch (Exception ex) { AppLogger.Error("无法更新文件夹右键菜单文字；可在系统设置中修复关联。", ex); }
    }

    public async Task SetDefaultStorageDirectoryAsync(string? path)
    {
        string? normalized = string.IsNullOrWhiteSpace(path) ? null : AppPaths.ValidateCustomStoragePath(path);
        if (!await MutateGlobalSettingAsync(
                s => s.DefaultStorageDirectory,
                (s, v) => s.DefaultStorageDirectory = v,
                normalized,
                StringComparer.OrdinalIgnoreCase))
            return;
        Console.RefreshAll();
    }

    public async Task SetLanguageAsync(AppLanguage language)
    {
        if (!Enum.IsDefined(language)) language = AppLanguage.ChineseSimplified;
        bool changed = false;
        await _globalSettingsGate.WaitAsync();
        try
        {
            if (State.GlobalSettings.Language != language)
            {
                AppLanguage previous = State.GlobalSettings.Language;
                State.GlobalSettings.Language = language;
                AppStrings.SetLanguage(language);
                try
                {
                    await SaveStateAsync();
                    changed = true;
                }
                catch
                {
                    State.GlobalSettings.Language = previous;
                    AppStrings.SetLanguage(previous);
                    throw;
                }
            }
        }
        finally
        {
            _globalSettingsGate.Release();
        }

        if (!changed)
        {
            Console.ApplyLanguage();
            return;
        }
        RefreshFolderContextMenuLabel();
        Console.ApplyLanguage();
        foreach (MainWindow window in _windows.Values) window.ApplyLanguage();
        foreach (NoteWindow window in _noteWindows.Values) window.ApplyLanguage();
        _portableDocuments.ApplyLanguage();
        _tray?.ApplyLanguage();
        Console.RefreshAll();
    }

    public async Task PrepareToExpandAsync(MainWindow source, Func<bool>? isCurrent = null)
    {
        if (isCurrent?.Invoke() == false) return;
        if (source.StaysExpanded) return;
        if (State.GlobalSettings.ExclusiveExpansion)
        {
            MainWindow[] unrelated = _windows.Values
                .Where(window => !ReferenceEquals(window, source) && window.IsExpanded && !window.StaysExpanded &&
                    !IsContainedParentChildPair(window, source) && !window.IsShellDragActive)
                .OrderBy(window => window.ContainerOrganizerId is null ? 1 : 0)
                .ToArray();
            foreach (MainWindow window in unrelated)
            {
                if (isCurrent?.Invoke() == false) return;
                await window.CollapseForPeerAsync();
            }
        }
        if (isCurrent?.Invoke() == false) return;
        _expandedWindow = source;
    }

    internal void RaiseActiveCompactOrganizerDrags(MainWindow station)
    {
        foreach (MainWindow source in _windows.Values.Where(window =>
                     !ReferenceEquals(window, station) && window.IsCompactOrganizerDragActive))
            source.RaiseActiveCompactOrganizerDrag();
    }

    internal async Task CollapseContainedChildrenAsync(Guid containerId, Func<bool>? isCurrent = null)
    {
        foreach (MainWindow child in _windows.Values.Where(window =>
                     window.OrganizerId != containerId &&
                     OrganizerContainment.IsAncestor(State.Organizers, containerId, window.OrganizerId) &&
                     window.IsExpanded).ToArray())
        {
            if (isCurrent?.Invoke() == false) return;
            await child.CollapseForPeerAsync();
        }
    }

    private IEnumerable<Guid> GetContainmentRefreshIds(IEnumerable<Guid?> seeds)
    {
        var result = new HashSet<Guid>();
        foreach (Guid? seed in seeds)
        {
            if (seed is not Guid id) continue;
            result.Add(id);
            foreach (Guid ancestorId in OrganizerContainment.GetAncestorIds(State.Organizers, id))
                result.Add(ancestorId);
        }
        return result;
    }

    private async Task RefreshContainmentParentsAsync(IEnumerable<Guid?> seeds)
    {
        foreach (Guid id in GetContainmentRefreshIds(seeds))
        {
            if (_windows.TryGetValue(id, out MainWindow? window))
                await window.RefreshContainedOrganizerItemsAsync();
        }
    }

    internal async Task ReconcileExclusiveExpansionAsync(MainWindow? preferred = null)
    {
        if (!State.GlobalSettings.ExclusiveExpansion) return;
        MainWindow? keep = preferred is { IsExpanded: true, StaysExpanded: false } ? preferred :
            _expandedWindow is { IsExpanded: true, StaysExpanded: false } ? _expandedWindow :
            _windows.Values.FirstOrDefault(window => window.IsExpanded && !window.StaysExpanded);
        _expandedWindow = keep;
        foreach (MainWindow window in _windows.Values.ToArray())
        {
            if (!ReferenceEquals(window, keep) && window.IsExpanded && !window.StaysExpanded && !window.IsShellDragActive &&
                (keep is null || !IsContainedParentChildPair(window, keep)))
                await window.CollapseForPeerAsync();
        }
    }

    public void NotifyCollapsed(MainWindow source)
    {
        if (!ReferenceEquals(_expandedWindow, source)) return;
        _expandedWindow = OrganizerContainment.GetAncestorIds(State.Organizers, source.OrganizerId)
            .Select(id => _windows.TryGetValue(id, out MainWindow? ancestor) && ancestor.IsExpanded && !ancestor.StaysExpanded ? ancestor : null)
            .FirstOrDefault(ancestor => ancestor is not null);
    }

    internal bool HasExpandedContainedChild(Guid containerId) =>
        _windows.Values.Any(window =>
            window.OrganizerId != containerId &&
            OrganizerContainment.IsAncestor(State.Organizers, containerId, window.OrganizerId) &&
            window.IsExpanded);

    internal bool ContainsExpandedContainedChildPoint(Guid containerId, NativeMethods.POINT point) =>
        _windows.Values.Any(window =>
            window.OrganizerId != containerId &&
            OrganizerContainment.IsAncestor(State.Organizers, containerId, window.OrganizerId) &&
            window.IsExpanded && window.ContainsScreenPoint(point));

    private bool IsContainedParentChildPair(MainWindow first, MainWindow second) =>
        OrganizerContainment.IsAncestor(State.Organizers, first.OrganizerId, second.OrganizerId) ||
        OrganizerContainment.IsAncestor(State.Organizers, second.OrganizerId, first.OrganizerId);

    internal bool IsOrganizerContainerDropTarget(MainWindow source, NativeMethods.POINT dropPoint) =>
        _windows.Values.Any(target =>
            !ReferenceEquals(target, source) &&
            !OrganizerContainment.IsAncestor(State.Organizers, source.OrganizerId, target.OrganizerId) &&
            target.TryGetOrganizerDropIndex(dropPoint, out _));

    internal bool IsOrganizerDragHoverTarget(MainWindow source, NativeMethods.POINT point) =>
        _organizerDragHoverSource == source && _organizerDragHoverTarget is not null &&
        (DragBoundaryMath.Contains(_organizerDragHoverBounds, point) ||
         _organizerDragHoverTarget.ContainsScreenPoint(point));

    internal void UpdateOrganizerDragHover(MainWindow source, NativeMethods.POINT point)
        => UpdateOrganizerDragHover(source, source.OrganizerId, point);

    internal void UpdateOrganizerDragHover(MainWindow source, Guid draggedOrganizerId, NativeMethods.POINT point)
    {
        if (ReferenceEquals(_organizerDragHoverSource, source) && _organizerDragHoverTarget is not null &&
            (DragBoundaryMath.Contains(_organizerDragHoverBounds, point) ||
             _organizerDragHoverTarget.ContainsScreenPoint(point)))
            return;

        MainWindow? target = _windows.Values.FirstOrDefault(candidate =>
            !ReferenceEquals(candidate, source) &&
            candidate.OrganizerId != draggedOrganizerId &&
            OrganizerKinds.IsRegular(candidate.DefinitionPlacementMode) &&
            candidate.ContainerOrganizerId is null &&
            !OrganizerContainment.IsAncestor(State.Organizers, draggedOrganizerId, candidate.OrganizerId) &&
            (candidate.IsExpanded && candidate.ContainsScreenPoint(point) ||
             candidate.TryGetCompactDropBounds(out NativeMethods.RECT bounds) && DragBoundaryMath.Contains(bounds, point)));


        if (target is null)
        {
            EndOrganizerDragHover(source);
            return;
        }

        if (ReferenceEquals(_organizerDragHoverSource, source) &&
            ReferenceEquals(_organizerDragHoverTarget, target)) return;

        _organizerDragHoverHold?.Dispose();
        _organizerDragHoverHold = target.HoldIncomingDrop();
        _organizerDragHoverSource = source;
        _organizerDragHoverTarget = target;
        _ = target.TryGetCompactDropBounds(out _organizerDragHoverBounds);
        _organizerDragHoverTask = target.ExpandForOrganizerDragAsync();
        _ = _organizerDragHoverTask.ContinueWith(
            _ => { }, TaskScheduler.Default);
    }

    internal async Task WaitForOrganizerDragHoverAsync(MainWindow source)
    {
        if (!ReferenceEquals(_organizerDragHoverSource, source)) return;
        Task? task = _organizerDragHoverTask;
        if (task is not null)
        {
            try { await task; }
            catch (Exception ex) { AppLogger.Error("收纳窗拖动悬停展开失败。", ex); }
        }
    }

    internal void EndOrganizerDragHover(MainWindow source)
    {
        if (!ReferenceEquals(_organizerDragHoverSource, source)) return;
        _organizerDragHoverHold?.Dispose();
        _organizerDragHoverHold = null;
        _organizerDragHoverSource = null;
        _organizerDragHoverTarget = null;
        _organizerDragHoverTask = null;
        _organizerDragHoverBounds = default;
    }

    internal async Task<bool> TryContainDraggedOrganizerAsync(MainWindow source, NativeMethods.POINT dropPoint)
    {
        if (State.Organizers.FirstOrDefault(item => item.Id == source.OrganizerId) is not OrganizerDefinition organizer) return false;
        if (source.IsExpanded ||
            organizer.PlacementMode is not (OrganizerPlacementMode.Floating or OrganizerPlacementMode.Positioned)) return false;
        foreach (MainWindow target in _windows.Values)
        {
            if (target.OrganizerId == organizer.Id ||
                OrganizerContainment.IsAncestor(State.Organizers, organizer.Id, target.OrganizerId)) continue;
            if (!target.TryGetOrganizerDropIndex(dropPoint, out int insertionIndex)) continue;
            using var receivingDrop = target.HoldIncomingDrop();
            await MoveOrganizerToContainerAsync(organizer, target.OrganizerId, insertionIndex);
            return true;
        }
        return false;
    }

    internal async Task<string?> FinishContainedOrganizerDragAsync(Guid organizerId, NativeMethods.POINT dropPoint)
    {
        if (State.Organizers.FirstOrDefault(item => item.Id == organizerId) is not OrganizerDefinition organizer) return null;
        _windows.TryGetValue(organizer.Id, out MainWindow? window);
        foreach (MainWindow target in _windows.Values)
        {
            if (target.OrganizerId == organizer.Id ||
                OrganizerContainment.IsAncestor(State.Organizers, organizer.Id, target.OrganizerId)) continue;
            if (!target.TryGetOrganizerDropIndex(dropPoint, out int insertionIndex)) continue;
            using var receivingDrop = target.HoldIncomingDrop();
            await MoveOrganizerToContainerAsync(organizer, target.OrganizerId, insertionIndex);
            return null;
        }

        Guid? previousContainerId = organizer.ContainerOrganizerId;
        if (previousContainerId is null) return null;
        DisplayInfo display = DisplayPlacementService.ForBounds(new NativeMethods.RECT
        {
            Left = dropPoint.X,
            Top = dropPoint.Y,
            Right = dropPoint.X + 1,
            Bottom = dropPoint.Y + 1
        });
        NativeMethods.RECT bounds;
        DesktopGridPlacement? positionedPlacement = null;
        if (organizer.PlacementMode == OrganizerPlacementMode.Positioned)
        {
            positionedPlacement = FindPositionedPlacement(display, dropPoint, organizer.Id, organizer.CompactScale);
            if (positionedPlacement is null) return AppStrings.Get("ContainedOrganizerNoGridError");
            bounds = positionedPlacement.Bounds;
        }
        else
        {
            double nameScale = State.GlobalSettings.ResolveCompactNameScale(organizer.PlacementMode);
            int width = Math.Max(1, (int)Math.Round(
                OrganizerLimits.CalculateCompactWindowWidthDip(organizer.CompactScale, nameScale) * display.Scale));
            int height = Math.Max(1, (int)Math.Round(
                OrganizerLimits.CalculateCompactWindowHeightDip(organizer.CompactScale, nameScale) * display.Scale));
            var desired = new NativeMethods.RECT
            {
                Left = dropPoint.X - width / 2,
                Top = dropPoint.Y - height / 2,
                Right = dropPoint.X - width / 2 + width,
                Bottom = dropPoint.Y - height / 2 + height
            };
            bounds = DisplayPlacementService.CalculateDraggedBounds(desired, dropPoint, dropPoint, display.Work);
        }

        OrganizerDefinition? parent = State.Organizers.FirstOrDefault(item => item.Id == previousContainerId);
        OrganizerContainmentSnapshot containmentSnapshot = OrganizerContainment.Capture(State.Organizers);
        WidgetPosition? previousPosition = organizer.Position;
        OrganizerContainment.Detach(State.Organizers, organizer.Id);
        organizer.Position = DisplayPlacementService.Capture(bounds);
        try
        {
            await SaveStateAsync();
        }
        catch (Exception ex)
        {
            containmentSnapshot.Restore(State.Organizers);
            organizer.Position = previousPosition;
            return AppStrings.Format("OrganizerContainerDropError", ex.Message);
        }

        if (window is not null)
        {
            try
            {
                if (positionedPlacement is not null) window.MoveToPositionedPlacement(bounds, positionedPlacement.CompactScale);
                else window.MoveToFloatingPlacement(bounds);
            }
            catch (Exception ex)
            {
                AppLogger.Error("收纳窗已拖回桌面，但窗口位置刷新失败。", ex);
            }
            finally
            {
                window.SetContained(false);
            }
        }
        try
        {
            if (parent is not null) await RefreshContainmentParentsAsync([parent.Id]);
            Console.RefreshAll(organizer.Id);
        }
        catch (Exception ex)
        {
            AppLogger.Error("收纳窗已拖回桌面，但界面刷新失败。", ex);
        }
        return null;
    }

    internal Task OpenContainedOrganizerAsync(Guid organizerId, NativeMethods.RECT anchor) =>
        _windows.TryGetValue(organizerId, out MainWindow? window)
            ? window.ExpandContainedAsync(anchor)
            : Task.CompletedTask;

    internal void UpdateContainedOrganizerDragPreview(Guid organizerId, NativeMethods.POINT cursor)
    {
        OrganizerDefinition? organizer = State.Organizers.FirstOrDefault(item => item.Id == organizerId);
        if (organizer?.ContainerOrganizerId is not Guid containerId ||
            !_windows.TryGetValue(containerId, out MainWindow? container) ||
            !_windows.TryGetValue(organizerId, out MainWindow? window)) return;
        window.MoveContainedDragPreview(cursor, container);
    }

    internal void ReconcileContainedOrganizer(Guid organizerId)
    {
        OrganizerDefinition? organizer = State.Organizers.FirstOrDefault(item => item.Id == organizerId);
        if (organizer is not null && _windows.TryGetValue(organizerId, out MainWindow? window))
            window.SetContained(organizer.ContainerOrganizerId is not null);
    }

    private async Task MoveOrganizerToContainerAsync(
        OrganizerDefinition organizer,
        Guid containerId,
        int insertionIndex)
    {
        OrganizerContainmentSnapshot snapshot = OrganizerContainment.Capture(State.Organizers);
        OrganizerContainmentMoveResult move = OrganizerContainment.TryMove(
            State.Organizers,
            organizer.Id,
            containerId,
            insertionIndex);
        if (!move.Succeeded) throw new InvalidOperationException(GetContainmentFailureMessage(move.Failure));
        try
        {
            await SaveStateAsync();
        }
        catch
        {
            snapshot.Restore(State.Organizers);
            throw;
        }

        if (_windows.TryGetValue(organizer.Id, out MainWindow? sourceWindow)) sourceWindow.SetContained(true);
        try
        {
            await RefreshContainmentParentsAsync([move.PreviousContainerId, containerId]);
            Console.RefreshAll(organizer.Id);
        }
        catch (Exception ex)
        {
            AppLogger.Error("收纳窗已移入容器，但界面刷新失败。", ex);
        }
    }

    private static string GetContainmentFailureMessage(OrganizerContainmentFailure failure) => failure switch
    {
        OrganizerContainmentFailure.SameOrganizer => AppStrings.Get("OrganizerContainmentSelfError"),
        OrganizerContainmentFailure.StationCannotBeContained => AppStrings.Get("OrganizerContainmentStationSourceError"),
        OrganizerContainmentFailure.DockCannotBeContained => AppStrings.Get("DockCannotBeContained"),
        OrganizerContainmentFailure.TargetIsDescendant => AppStrings.Get("OrganizerContainmentTargetContainedError"),
        _ => AppStrings.Get("OrganizerContainmentMissingError")
    };

    public void OpenConsole(Guid? organizerId = null)
    {
        _ = _dispatcher.TryEnqueue(() => Console.ShowAndActivate(organizerId));
    }

    public void NotifyTransparencyFallback()
    {
        if (_transparencyNoticeShown) return;
        _transparencyNoticeShown = true;
        LogStatus("TuckPane", AppStrings.Get("TransparencyNotification"));
    }

    private DesktopGridPlacement? FindPositionedPlacement(
        DisplayInfo display,
        NativeMethods.POINT? desiredCenter,
        Guid excludeId,
        double compactScale)
    {
        DesktopGridSnapshot snapshot = ReadGridSnapshot(display);
        NativeMethods.RECT[] occupied = _windows.Values
            .Where(window => window.OrganizerId != excludeId)
            .Where(window => State.Organizers.FirstOrDefault(item => item.Id == window.OrganizerId) is
                { PlacementMode: OrganizerPlacementMode.Positioned, ContainerOrganizerId: null })
            .Select(window => window.CompactBounds)
            .ToArray();
        return DesktopGridService.Find(snapshot, occupied, desiredCenter, compactScale);
    }

    private bool NormalizePositionedPlacementsOnStartup()
    {
        IReadOnlyList<DisplayInfo> displays = DisplayPlacementService.GetDisplays();
        DisplayInfo primary = displays.FirstOrDefault(display => display.Monitor.Left == 0 && display.Monitor.Top == 0) ?? displays.First();
        var occupied = new List<NativeMethods.RECT>();
        bool changed = false;
        foreach (OrganizerDefinition organizer in State.Organizers.Where(item =>
                     item.PlacementMode == OrganizerPlacementMode.Positioned && item.ContainerOrganizerId is null))
        {
            DisplayInfo display = displays.FirstOrDefault(item => string.Equals(item.Device, organizer.Position?.MonitorDevice, StringComparison.OrdinalIgnoreCase)) ?? primary;
            DesktopGridSnapshot snapshot = ReadGridSnapshot(display);
            double scale = Math.Min(
                organizer.CompactScale,
                DesktopGridService.CalculatePositionedCompactScale(snapshot));
            (int width, int height, _) = DesktopGridService.CalculatePositionedWindowSize(snapshot, scale);
            NativeMethods.RECT desired = DisplayPlacementService.RestoreToDisplay(organizer.Position, display, width, height);
            var center = new NativeMethods.POINT
            {
                X = desired.Left + desired.Width / 2,
                Y = desired.Top + desired.Height / 2
            };
            DesktopGridPlacement? placement = DesktopGridService.Find(
                snapshot,
                occupied,
                center,
                organizer.CompactScale);
            if (placement is null)
            {
                occupied.Add(desired);
                continue;
            }
            occupied.Add(placement.Bounds);
            if (!RectsEqual(desired, placement.Bounds)) changed = true;
            organizer.Position = DisplayPlacementService.Capture(placement.Bounds);
        }
        return changed;
    }

    private bool NormalizeStationPlacementsOnStartup()
    {
        bool changed = false;
        foreach (OrganizerDefinition organizer in State.Organizers.Where(item => item.PlacementMode == OrganizerPlacementMode.Station))
        {
            DisplayInfo display = DisplayPlacementService.GetDisplay(organizer.Position?.MonitorDevice);
            WidgetPosition normalized = DisplayPlacementService.Capture(
                DisplayPlacementService.CalculateStationAnchor(display, organizer.DockEdge, organizer.Position));
            if (!PositionsEqual(organizer.Position, normalized)) changed = true;
            organizer.Position = normalized;
        }
        return changed;
    }

    private static bool PositionsEqual(WidgetPosition? first, WidgetPosition second) =>
        first is not null && string.Equals(first.MonitorDevice, second.MonitorDevice, StringComparison.OrdinalIgnoreCase) &&
        Math.Abs(first.XDip - second.XDip) < .01 && Math.Abs(first.YDip - second.YDip) < .01;

    private DesktopGridSnapshot ReadGridSnapshot(DisplayInfo display)
    {
        DesktopGridSnapshot snapshot = _desktopGrid.ReadSnapshot(display);
        if (!snapshot.ExplorerPositionsAvailable && !_gridFallbackNoticeShown)
        {
            _gridFallbackNoticeShown = true;
            LogStatus("TuckPane", AppStrings.Get("GridFallbackMessage"));
        }
        return snapshot;
    }

    private static bool RectsEqual(NativeMethods.RECT first, NativeMethods.RECT second) =>
        first.Left == second.Left && first.Top == second.Top && first.Right == second.Right && first.Bottom == second.Bottom;

    private static void TryDeleteEmptyCreatedStorage(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"无法回滚空的收纳目录：{path}", ex);
        }
    }

    public async Task SetUniformCompactScaleEnabledAsync(OrganizerPlacementMode mode, bool enabled)
    {
        await _globalSettingsGate.WaitAsync();
        try
        {
            (bool previousEnabled, double previousScale) = GetUniformCompactScaleSetting(mode);
            if (previousEnabled == enabled) return;
            List<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> snapshots =
                CaptureCompactScaleSnapshots(mode);
            string? error = ApplyUniformCompactScaleSetting(mode, enabled, previousScale, snapshots);
            if (error is not null) throw new InvalidOperationException(error);
            try
            {
                await SaveStateAsync();
            }
            catch
            {
                RestoreUniformCompactScaleSetting(mode, previousEnabled, previousScale, snapshots);
                throw;
            }
        }
        finally
        {
            _globalSettingsGate.Release();
        }
    }

    internal string? ApplyUniformCompactScale(OrganizerPlacementMode mode, double scale)
    {
        (bool enabled, _) = GetUniformCompactScaleSetting(mode);
        return ApplyUniformCompactScaleSetting(mode, enabled, scale, CaptureCompactScaleSnapshots(mode));
    }

    internal List<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> CaptureUniformCompactScaleSnapshots(
        OrganizerPlacementMode mode) =>
        CaptureCompactScaleSnapshots(mode);

    internal void RestoreUniformCompactScale(
        OrganizerPlacementMode mode,
        double scale,
        IReadOnlyList<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> snapshots)
    {
        (bool enabled, _) = GetUniformCompactScaleSetting(mode);
        RestoreUniformCompactScaleSetting(mode, enabled, scale, snapshots);
    }

    private string? ApplyUniformCompactScaleSetting(
        OrganizerPlacementMode mode,
        bool enabled,
        double scale,
        IReadOnlyList<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> snapshots)
    {
        (bool previousEnabled, double previousScale) = GetUniformCompactScaleSetting(mode);
        SetUniformCompactScaleSetting(mode, enabled, scale);
        if (!enabled) return null;

        try
        {
            foreach (var snapshot in snapshots)
            {
                if (State.Organizers.FirstOrDefault(item => item.Id == snapshot.Definition.Id) is not OrganizerDefinition current)
                    continue;
                OrganizerDefinition edited = OrganizerInteractionMath.CopySettings(current, current.Name);
                edited.Id = current.Id;
                edited.CompactScale = State.GlobalSettings.ResolveCompactScale(mode, current.CompactScale);
                string? error = ApplyOrganizerRuntime(edited, OrganizerVisualChange.CompactScale);
                if (error is null) continue;
                RestoreUniformCompactScaleSetting(mode, previousEnabled, previousScale, snapshots);
                return error;
            }
        }
        catch (Exception ex)
        {
            RestoreUniformCompactScaleSetting(mode, previousEnabled, previousScale, snapshots);
            AppLogger.Error("无法批量应用统一入口大小。", ex);
            return ex.Message;
        }
        return null;
    }

    private void RestoreUniformCompactScaleSetting(
        OrganizerPlacementMode mode,
        bool enabled,
        double scale,
        IReadOnlyList<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> snapshots)
    {
        SetUniformCompactScaleSetting(mode, enabled, scale);
        foreach (var snapshot in snapshots)
        {
            if (State.Organizers.FirstOrDefault(item => item.Id == snapshot.Definition.Id) is not OrganizerDefinition current)
                continue;
            current.CompactScale = snapshot.Definition.CompactScale;
            current.Position = snapshot.Definition.Position;
            if (!_windows.TryGetValue(current.Id, out MainWindow? window)) continue;
            try
            {
                if (mode == OrganizerPlacementMode.Positioned)
                {
                    window.MoveToPositionedPlacement(snapshot.Bounds, snapshot.RuntimeScale);
                }
                else
                {
                    window.MoveToFloatingPlacement(snapshot.Bounds);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("无法回滚统一入口大小。", ex);
            }
        }
    }

    private (bool Enabled, double Scale) GetUniformCompactScaleSetting(OrganizerPlacementMode mode) => mode switch
    {
        OrganizerPlacementMode.Floating => (
            State.GlobalSettings.UseUniformFloatingCompactScale,
            State.GlobalSettings.UniformFloatingCompactScale),
        OrganizerPlacementMode.Positioned => (
            State.GlobalSettings.UseUniformPositionedCompactScale,
            State.GlobalSettings.UniformPositionedCompactScale),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private void SetUniformCompactScaleSetting(OrganizerPlacementMode mode, bool enabled, double scale)
    {
        scale = GlobalSettings.NormalizeCompactScale(mode, scale);
        switch (mode)
        {
            case OrganizerPlacementMode.Floating:
                State.GlobalSettings.UseUniformFloatingCompactScale = enabled;
                State.GlobalSettings.UniformFloatingCompactScale = scale;
                break;
            case OrganizerPlacementMode.Positioned:
                State.GlobalSettings.UseUniformPositionedCompactScale = enabled;
                State.GlobalSettings.UniformPositionedCompactScale = scale;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    internal void ApplyNameScales(double compactScale, double expandedScale)
    {
        compactScale = GlobalSettings.NormalizeCompactNameScale(compactScale);
        expandedScale = GlobalSettings.NormalizeCompactNameScale(expandedScale);
        State.GlobalSettings.UniformFloatingCompactNameScale = compactScale;
        State.GlobalSettings.ExpandedNameScale = expandedScale;
        foreach (OrganizerDefinition organizer in State.Organizers.Where(item =>
                     item.PlacementMode is OrganizerPlacementMode.Floating or OrganizerPlacementMode.Positioned))
            if (_windows.TryGetValue(organizer.Id, out MainWindow? window)) window.ApplyDefinition(OrganizerVisualChange.NameScale);
    }

    private List<(OrganizerDefinition Definition, NativeMethods.RECT Bounds, double RuntimeScale)> CaptureCompactScaleSnapshots(
        OrganizerPlacementMode mode) => State.Organizers
        .Where(item => item.PlacementMode == mode)
        .Select(item =>
        {
            OrganizerDefinition snapshot = item.Clone();
            NativeMethods.RECT bounds = _windows.TryGetValue(item.Id, out MainWindow? window)
                ? window.CompactBounds
                : default;
            return (snapshot, bounds, window?.AppliedCompactScale ?? item.CompactScale);
        })
        .ToList();

    public async Task SetCollapseOnOutsideClickAsync(bool enabled)
    {
        if (!await MutateGlobalSettingAsync(s => s.CollapseOnOutsideClick, (s, v) => s.CollapseOnOutsideClick = v, enabled)) return;
        foreach (MainWindow window in _windows.Values) window.ApplyOutsideClickSetting();
    }

    public async Task SetNoteAlwaysOnTopAsync(bool enabled)
    {
        if (!await MutateGlobalSettingAsync(s => s.NoteAlwaysOnTop, (s, v) => s.NoteAlwaysOnTop = v, enabled)) return;
        foreach (NoteWindow window in _noteWindows.Values) window.ApplyAlwaysOnTopSetting();
        _portableDocuments.ApplyAlwaysOnTopSetting();
    }

    public async Task SetEdgeGlowEnabledAsync(bool enabled)
    {
        if (!await MutateGlobalSettingAsync(s => s.EdgeGlowEnabled, (s, v) => s.EdgeGlowEnabled = v, enabled)) return;
        LastChangedThemeTarget = null;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetExclusiveExpansionAsync(bool enabled)
    {
        await MutateGlobalSettingAsync(s => s.ExclusiveExpansion, (s, v) => s.ExclusiveExpansion = v, enabled);
        if (enabled) await ReconcileExclusiveExpansionAsync();
    }

    public async Task SetExpandOnHoverAsync(bool enabled)
    {
        if (!await MutateGlobalSettingAsync(s => s.ExpandOnHover, (s, v) => s.ExpandOnHover = v, enabled)) return;
        foreach (MainWindow window in _windows.Values) window.RefreshPerformanceSettings();
    }

    public async Task SetCollapseOnPointerLeaveAsync(bool enabled)
    {
        if (!await MutateGlobalSettingAsync(s => s.CollapseOnPointerLeave, (s, v) => s.CollapseOnPointerLeave = v, enabled)) return;
        foreach (MainWindow window in _windows.Values) window.RefreshPerformanceSettings();
    }

    public async Task SetPerformanceProfileAsync(PerformanceProfile profile)
    {
        if (!Enum.IsDefined(profile)) profile = PerformanceProfile.Balanced;
        if (!await MutateGlobalSettingAsync(s => s.PerformanceProfile, (s, v) => s.PerformanceProfile = v, profile)) return;
        foreach (MainWindow window in _windows.Values) window.RefreshPerformanceSettings();
    }

    public async Task SetWindowAlignmentEnabledAsync(bool enabled)
    {
        if (!await MutateGlobalSettingAsync(s => s.WindowAlignmentEnabled, (s, v) => s.WindowAlignmentEnabled = v, enabled)) return;
        foreach (MainWindow window in _windows.Values) window.RefreshWindowAlignmentSetting();
    }

    public void SetHoverDelays(
        int hoverExpandDelayMs,
        int pointerLeaveCollapseDelayMs,
        int stationPointerLeaveCollapseDelayMs)
    {
        State.GlobalSettings.HoverExpandDelayMs = GlobalSettings.NormalizeHoverDelayMs(hoverExpandDelayMs);
        State.GlobalSettings.PointerLeaveCollapseDelayMs = GlobalSettings.NormalizeHoverDelayMs(pointerLeaveCollapseDelayMs);
        State.GlobalSettings.StationPointerLeaveCollapseDelayMs = GlobalSettings.NormalizeHoverDelayMs(
            stationPointerLeaveCollapseDelayMs);
        foreach (MainWindow window in _windows.Values) window.RefreshHoverDelays();
    }

    public void SetStationActivation(int distanceDip, int hoverExpandDelayMs)
    {
        State.GlobalSettings.StationActivationDistanceDip =
            GlobalSettings.NormalizeStationActivationDistanceDip(distanceDip);
        State.GlobalSettings.StationHoverExpandDelayMs =
            GlobalSettings.NormalizeStationHoverExpandDelayMs(hoverExpandDelayMs);
        foreach (MainWindow window in _windows.Values) window.RefreshHoverDelays();
    }

    private string LifecycleWindowCounts() =>
        $"organizers={_windows.Count} notes={_noteWindows.Count + _portableDocuments.NoteCount} " +
        $"todos={_portableDocuments.TodoCount} exiting={_exitPreparation.IsActive}";

    public Task ExitAsync(string source = "app") => ExitCoreAsync(source, null);
    internal Task<bool> ExitForUpdateAsync(Func<Task> handoff) => ExitCoreAsync("update", handoff);
    private async Task<bool> ExitCoreAsync(string source, Func<Task>? handoff)
    {
        using ExitPreparation.Attempt? attempt = _exitPreparation.TryBegin(TransferQueue);
        if (attempt is null) return false;
        IsPreparingUpdate = handoff is not null;
        UpdateWindowInput? frozen = null;
        try
        {
        AppLogger.Lifecycle("exit-begin", $"source={source} {LifecycleWindowCounts()}");
        if (TransferQueue.IsActive && !await Console.ConfirmCancelTransferAndExitAsync())
        {
            AppLogger.Lifecycle("exit-cancelled", $"source={source} reason=transfer-confirmation");
            return false;
        }
        if (!await attempt.WaitForTransfersAsync(TimeSpan.FromSeconds(5)))
        {
            AppLogger.Record(DiagnosticArea.Lifecycle, DiagnosticStage.Failed, exception: new TimeoutException());
            Console.ShowExitPending();
            return false;
        }
        if (handoff is not null)
        {
            frozen = new UpdateWindowInput(_windows.Values.Cast<Window>().Concat(_noteWindows.Values).Concat(OpenPortableUiWindows).Append(Console).Distinct());
            await _portableDocuments.DrainOpenGateAsync();
        }
        if (!await Console.FlushPendingThemeSaveAsync())
        {
            AppLogger.Lifecycle("exit-cancelled", $"source={source} reason=theme-save");
            return false;
        }
        if (!await Console.FlushPendingManageChangesAsync())
        {
            AppLogger.Lifecycle("exit-cancelled", $"source={source} reason=manage-save");
            Console.ShowAndActivate();
            return false;
        }

        NoteWindow[] noteWindows = _noteWindows.Values
            .Concat(OpenExternalNoteWindows)
            .Distinct()
            .ToArray();
        foreach (NoteWindow window in noteWindows)
        {
            if (await window.FlushForExitAsync()) continue;
            AppLogger.Lifecycle("exit-cancelled", $"source={source} reason=note-save");
            window.ShowAndActivate();
            return false;
        }
        TodoWindow[] todoWindows = OpenExternalTodoWindows.Distinct().ToArray();
        foreach (TodoWindow window in todoWindows)
        {
            if (await window.FlushForExitAsync()) continue;
            AppLogger.Lifecycle("exit-cancelled", $"source={source} reason=todo-save");
            window.ShowAndActivate();
            return false;
        }

        await SaveStateAsync();
        if (handoff is not null)
        {
            await handoff();
        }
        AppLogger.Lifecycle("exit-commit", $"source={source} {LifecycleWindowCounts()}");
        attempt.Commit();
        Updates?.Dispose();
        DisposeProductivityServices();
        DisposeDockRunningState();
        ShellLauncher.Dispose();
        _organizerDragHoverHold?.Dispose();
        _organizerDragHoverHold = null;
        foreach (ExpansionModeCoordinator coordinator in _expansionModes.Values.ToArray()) coordinator.Close();
        _expansionModes.Clear();
        foreach (NoteWindow window in noteWindows) window.ClosePermanentlyWithoutSave();
        foreach (TodoWindow window in todoWindows) window.ClosePermanentlyWithoutSave();
        _noteWindows.Clear();
        _portableDocuments.Clear();
        foreach (MainWindow window in _windows.Values.ToArray()) window.ClosePermanently();
        _windows.Clear();
        _tray?.Dispose();
        _tray = null;
        Console.ClosePermanently();
        try { await AppLogger.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
        AppLogger.Lifecycle("exit-application", $"source={source}");
        Application.Current.Exit();
        return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Exit preparation failed", ex);
            if (attempt.IsCommitted) Application.Current.Exit();
            else if (handoff is not null) throw;
            else Console.ShowExitPending();
            return attempt.IsCommitted;
        }
        finally
        {
            if (!attempt.IsCommitted)
            {
                IsPreparingUpdate = false;
                frozen?.Dispose();
            }
        }
    }

    private void CreateWindow(OrganizerDefinition organizer)
    {
        if (IsPreparingUpdate) return;
        var window = new MainWindow(this, organizer);
        window.InitializeHostWindow();
        _windows.Add(organizer.Id, window);
        if (organizer.PlacementMode == OrganizerPlacementMode.Station)
        {
            _ = window.InitializeAsync();
            window.SetVisible(true);
        }
        else
        {
            window.Activate();
            // Ensure initialization always starts even when WS_EX_NOACTIVATE suppresses WM_ACTIVATE.
            _ = window.InitializeAsync();
            if (organizer.ContainerOrganizerId is not null) window.SetContained(true);
        }
    }

    private void HandleTrayCommand(TrayCommand command)
    {
        _ = _dispatcher.TryEnqueue(async () =>
        {
            if (IsPreparingUpdate) return;
            try
            {
                switch (command)
                {
                    case TrayCommand.OpenConsole:
                        OpenConsole();
                        break;
                    case TrayCommand.SearchOrganizers:
                        OpenSearchWindow();
                        break;
                    case TrayCommand.TogglePeek:
                        TogglePeekMode();
                        break;
                    case TrayCommand.TidyEdgeStack:
                        _ = await TidyFloatingEdgeStackAsync();
                        break;
                    case TrayCommand.ShowAll:
                        ShowAllWindows();
                        break;
                    case TrayCommand.HideAll:
                        HideAllWindows();
                        break;
                    case TrayCommand.ToggleStartup:
                        await SetStartupAsync(!State.GlobalSettings.StartWithWindows);
                        Console.RefreshAll();
                        break;
                    case TrayCommand.CancelTransfer:
                        TransferQueue.CancelCurrent();
                        break;
                    case TrayCommand.Exit:
                        await ExitAsync("tray");
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"托盘命令处理失败：{command}", ex);
                if (command == TrayCommand.Exit)
                    AppLogger.Lifecycle("exit-failed", $"source=tray type={ex.GetType().FullName} hresult=0x{ex.HResult:X8}");
            }
        });
    }

    private void TransferQueue_StateChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed) Console?.UpdateTransferState();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _feedbackDisposed = true;
        TransferQueue.StateChanged -= TransferQueue_StateChanged;
        _organizerDragHoverHold?.Dispose();
        _organizerDragHoverHold = null;
        foreach (ExpansionModeCoordinator coordinator in _expansionModes.Values.ToArray())
            coordinator.Close();
        _expansionModes.Clear();
        DisposeProductivityServices();
        DisposeDockRunningState();
        Updates?.Dispose();
        ShellLauncher.Dispose();
        AppLogger.Lifecycle("host-dispose", LifecycleWindowCounts());
        _tray?.Dispose();
        _tray = null;
        foreach (NoteWindow window in _noteWindows.Values.ToArray()) _ = window.ClosePermanentlyAsync();
        _noteWindows.Clear();
        _portableDocuments.CloseAllOnDispose();
        foreach (MainWindow window in _windows.Values.ToArray()) window.ClosePermanently();
        _windows.Clear();
        TryDisposeGate(_globalSettingsGate);
        TryDisposeGate(_expansionModeSaveGate);
        TryDisposeGate(_nameVisibilityGate);
        TryDisposeGate(_organizerPreferenceGate);
        TryDisposeGate(_organizerNameGate);
        TryDisposeGate(_topologyChangeGate);
    }

    private static void TryDisposeGate(SemaphoreSlim gate)
    {
        try { gate.Dispose(); }
        catch (ObjectDisposedException) { }
    }
}


