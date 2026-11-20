using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using TuckPane.Core;
using TuckPane.Controls;
using TuckPane.Models;
using TuckPane.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.VisualBasic.FileIO;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using WinUIEx;
using WinRT.Interop;

namespace TuckPane;

public sealed partial class MainWindow : Window
{
    private const double CompactWidthDip = OrganizerLimits.CompactWindowWidthDip;
    private const double CompactHeightDip = OrganizerLimits.CompactWindowHeightDip;
    private const int CompactPreviewRows = 2;
    private const int CompactPreviewColumns = 2;
    private const int CompactPreviewItemCount = CompactPreviewRows * CompactPreviewColumns;
    private const double CompactCornerRadiusDip = 9.5;
    private const double ExpandedCornerRadiusDip = 36;
    private const double ExpandTransitionResponseSeconds = .27;
    private const double CollapseTransitionResponseSeconds = .22;
    private const double StationTransitionResponseSeconds = .24;
    private const int ReducedMotionDurationMs = 120;
    private const int LongPressMs = 350;
    private const double LongPressMoveLimitDip = 8;
    private const double ItemGapDip = DisplayPlacementService.ItemGapDip;
    private const double CompactListItemHeightDip = 36;
    private const double CompactListIconSizeDip = 20;
    private const double CompactListFontSizeDip = 14;
    private static readonly long OleMouseMoveMinimumTicks = Math.Max(1, Stopwatch.Frequency / 120);
    private static readonly ScrollingScrollOptions InstantScrollOptions = new(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore);

    private readonly AppHost _host;
    private StorageService _storage;
    private readonly DesktopAttachmentCoordinator _desktopCoordinator;
    private readonly OrganizerCatalogController _catalogController;
    private readonly ExternalItemDragCoordinator _externalDragCoordinator;
    private readonly IconCacheService _iconCache;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _longPressTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _externalHoverTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _interactionSaveTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _canvasResizeInputTimer;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly LongPressGesture _longPressGesture = new(LongPressMoveLimitDip);
    private readonly UniformGridLayout _gridLayout = new() { Orientation = Orientation.Horizontal };
    private readonly SolidColorBrush _transparentItemBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _hoveredItemBrush = new(ColorHelper.FromArgb(20, 24, 38, 56));
    private readonly SolidColorBrush _pressedItemBrush = new(ColorHelper.FromArgb(36, 24, 38, 56));
    private readonly SolidColorBrush _draggedItemBrush = new(ColorHelper.FromArgb(28, 24, 38, 56));
    private readonly SolidColorBrush _collapseHoverBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _collapsePressedBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _folderFallbackBrush = new(ColorHelper.FromArgb(255, 106, 210, 255));
    private readonly SolidColorBrush _fileFallbackBrush = new(ColorHelper.FromArgb(255, 236, 239, 245));
    private readonly SolidColorBrush _organizerTextBrush = new(Colors.White);
    private readonly SolidColorBrush _dockEmptyAddBrush = new(Colors.White);
    private readonly SolidColorBrush _warningBadgeBgBrush = new(ColorHelper.FromArgb(184, 29, 35, 43));
    private readonly SolidColorBrush _warningIconFgBrush = new(ColorHelper.FromArgb(255, 255, 200, 87));
    private readonly BitmapImage _noteIcon = new(new Uri("ms-appx:///Assets/Note.png"))
    {
        DecodePixelWidth = 128
    };
    private readonly BitmapImage _todoIcon = new(new Uri("ms-appx:///Assets/Todo.png"))
    {
        DecodePixelWidth = 128
    };
    private readonly BitmapImage _organizerIcon = new(new Uri("ms-appx:///Assets/TuckPane.png"))
    {
        DecodePixelWidth = 128
    };
    private readonly NativeMethods.SubclassProc _gestureWindowProc;
    private readonly NativeMethods.WindowProc _canvasResizeWindowProc;
    private readonly ThemeBackdrop _compactBackdrop = new(ThemeTarget.Organizer, "compact", useRoundedMask: true);
    private readonly ThemeBackdrop _expandedBackdrop;
    private readonly ThemeSurface _compactSurface;
    private readonly ThemeSurface _expandedSurface;
    private readonly ThemeEdgeSurface _compactEdgeSurface;
    private readonly ThemeEdgeSurface _expandedEdgeSurface;
    private readonly Dictionary<Image, string> _pendingIconLoads = [];
    private readonly SemaphoreSlim _iconLoadGate = new(3, 3);
    private static readonly UIntPtr GestureSubclassId = new(0x47464452UL);

    private OrganizerDefinition _definition;
    private readonly ObservableCollection<WidgetItem> _items = [];
    private IntPtr _hwnd;
    private AppWindow? _appWindow;
    private bool _hostWindowInitialized;
    private readonly List<IntPtr> _canvasResizeEdgeWindows = [];
    private readonly Dictionary<IntPtr, IntPtr> _canvasResizeOriginalWindowProcs = [];
    private DesktopLayerService? _desktopLayer;
    private OutsideClickHook? _outsideClickHook;
    private NativeMethods.RECT _compactBounds;
    private NativeMethods.RECT _containedAnchorBounds;
    private DisplayInfo? _stationDisplay;
    private bool _expanded;
    private bool _animating;
    private bool _closing;
    private bool _stationVisible = true;
    private bool _stationReady;
    private readonly InteractionSession _widgetGesture = new();
    private readonly InteractionSession _resizeGesture = new();
    private Task? _initializationTask;
    private Task? _stationContentTask;
    private readonly CancellationTokenSource _windowLifetime = new();
    private bool _runtimeVisible = true;
    private int _overlayOpenCount;
    private CancellationTokenSource? _transitionCancellation;
    private RectangleClip? _compactClip;
    private RectangleClip? _expandedClip;
    private RectangleClip? _stationTransitionClip;
    private Visual? _expandedCompositionVisual;
    private int _compactPreviewVersion;
    private double _transitionProgress;
    private double _transitionVelocity;
    private Vector2 _transitionAnchorDip;
    private Vector2 _transitionStartScale = Vector2.One;
    private Vector3 _transitionCompactTranslation;
    private CollapseTransitionGeometry? _collapseTransitionGeometry;
    private int _lastTransitionWindowLeft = int.MinValue;
    private int _lastTransitionWindowTop = int.MinValue;
    private double _lastExpandedClipRadius = -1;
    private XamlRoot? _subscribedXamlRoot;
    private readonly PointerEventHandler _expandedPointerPressedHandler;
    private readonly PointerEventHandler _expandedPointerMovedHandler;
    private readonly PointerEventHandler _expandedPointerReleasedHandler;
    private readonly PointerEventHandler _expandedPointerCanceledHandler;
    private readonly PointerEventHandler _expandedPointerCaptureLostHandler;
    private readonly PointerEventHandler _expandedPointerWheelChangedHandler;

    private uint _pressedPointerId;
    private Point _pressPointDip;
    private NativeMethods.POINT _pressCursorPx;
    private NativeMethods.RECT _pressWindowBounds;
    private NativeMethods.RECT _positionedDragOriginBounds;
    private NativeMethods.RECT _dragCurrentBounds;
    private bool _pressActive;
    private bool _widgetDragging;
    private bool _widgetDragTopmost;
    private bool _groupDragHasPeers;
    private bool _draggingExpanded;
    private bool _nativeMouseCapture;
    private bool _dragRenderingSubscribed;
    private bool _dragClockBoosted;
    private bool _itemDragClockBoosted;
    private IDisposable? _smoothScrollClockBoost;
    private IDisposable? _canvasResizeClockBoost;
    private DisplayInfo? _dragDisplay;
    private WindowAlignmentInsets? _dragAlignmentInsets;
    private WindowAlignmentState _windowAlignmentState;
    private WindowAlignmentGuideOverlay? _windowAlignmentGuide;
    private long _dragStartedAt;
    private long _pressStartedAt;
    private int _dragInputCount;
    private int _dragCommitCount;
    private int _dragRenderTickCount;
    private bool _hasLastDragCursor;
    private NativeMethods.POINT _lastDragCursor;
    private bool _hasPendingWidgetDragCursor;
    private NativeMethods.POINT _pendingWidgetDragCursor;
    private CanvasResizeSession? _canvasResize;
    private bool _canvasResizeCommitQueued;
    private bool _hasPendingCanvasResizeCursor;
    private NativeMethods.POINT _pendingCanvasResizeCursor;
    private int _wheelDeltaRemainder;
    private SmoothScrollState _gridScrollState = SmoothScrollState.Empty;
    private SmoothScrollState _compactListScrollState = SmoothScrollState.Empty;
    private bool _smoothScrollRenderingSubscribed;
    private long _smoothScrollLastFrame;
#if DEBUG
    private bool _canvasResizeProbeRunning;
    private bool _itemReorderProbeRunning;
#else
    private static bool _canvasResizeProbeRunning => false;
    private static bool _itemReorderProbeRunning => false;
#endif
    private bool _shellContextMenuOpen;
    private bool _contextMenuActivated;
    private bool _contextMenuCounted;
    private NativeMethods.POINT _contextMenuScreenPoint;
    private bool _hoverExpandScrollToEnd;

    private string? _draggedRelativeName;
    private bool _shellDragActive;
    private ItemReorderSession? _itemReorderSession;
    private Border? _itemDragHost;
    private uint _itemDragPointerId;
    private PointerPoint? _itemDragLastPointerPoint;
    private PointerDeviceType _itemDragPointerType;
    private readonly NativeMethods.HookProc _itemDragBoundaryHookProc;
    private IntPtr _itemDragBoundaryHook;
    private Thread? _itemDragBoundaryHookThread;
    private readonly ManualResetEventSlim _itemDragBoundaryHookReady = new(false);
    private uint _itemDragBoundaryHookThreadId;
    private int _itemDragBoundaryHookStopRequested;
    private int _itemDragBoundaryArmed;
    private int _itemDragBoundaryPromotionPosted;
    private long _itemDragBoundaryDetectedAt;
    private NativeMethods.RECT _itemDragBoundaryBounds;
    private int _itemDragOleMouseMovePending;
    private long _itemDragLastOleMouseMoveForwardedAt;
    private bool _itemDragRenderingSubscribed;
    private bool _itemDragLanding;
    private bool _itemCollectionMoveInProgress;
    private bool _shellPromotionPending;
    private bool _shellDropFinalizing;
    private bool _internalOleDropAccepted;
    private bool _dragHasLocalFile;
    private long _shellPromotionRequestedAt;
    private long _itemDragPressedAt;
    private double _nativeDragCellWidth;
    private double _nativeDragCellHeight;
    private bool _nativeItemMotionRenderingSubscribed;
    private long _nativeItemMotionRenderingStartedAt;
    private readonly bool _itemDragTraceEnabled = Environment.GetEnvironmentVariable("GLASSFOLDER_TEST_ITEM_DRAG_TRACE") == "1";
    private long _itemDragLastFrame;
    private bool _itemReorderUsesAnimations;
    private TaskCompletionSource? _itemMotionSettled;
    private int[]? _gapTransitionFrom;
    private int[]? _gapTransitionTo;
    private long _gapTransitionStartedAt;
    private int _gapTransitionRevision;
    private int _gapMaterializedRevision;
    private bool _gapTransitionCompleting;
    private TaskCompletionSource? _gapTransitionSettled;
    private readonly RealizedItemRegistry<Border> _realizedItemHosts = new();
    private readonly Dictionary<Border, ItemHostParts> _itemHostParts = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _itemDragIdentityWarnings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Border, ItemMotionState> _itemMotionStates = new(ReferenceEqualityComparer.Instance);
    private int _itemElementsPrepared;
    private int _itemElementsCleared;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _itemTouchHoldTimer;
    private double _appliedCompactScale = 1;
    private double _positionedCompactWidthDip;
    private double _positionedCompactHeightDip;

    private sealed class ItemHostParts(
        Grid iconContainer,
        StackPanel itemContent,
        FontIcon fallbackIcon,
        Image itemImage,
        OrganizerPreviewControl organizerPreview,
        Grid nameContainer,
        Grid nameShadowHost,
        TextBlock nameText)
    {
        internal readonly Grid IconContainer = iconContainer;
        internal readonly StackPanel ItemContent = itemContent;
        internal readonly FontIcon FallbackIcon = fallbackIcon;
        internal readonly Image ItemImage = itemImage;
        internal readonly OrganizerPreviewControl OrganizerPreview = organizerPreview;
        internal readonly Grid NameContainer = nameContainer;
        internal readonly Grid NameShadowHost = nameShadowHost;
        internal readonly TextBlock NameText = nameText;
        internal SpriteVisual? NameShadowVisual;
        internal DropShadow? NameDropShadow;
        internal CompositionBrush? NameAlphaMask;
        internal SizeChangedEventHandler? NameSizeChangedHandler;
    }

    private sealed class ItemMotionState
    {
        internal Visual? Visual;
        internal Vector3 Translation;
        internal Vector3 TranslationTarget;
        internal Vector3 TranslationVelocity;
        internal Vector3 Scale = Vector3.One;
        internal Vector3 ScaleTarget = Vector3.One;
        internal Vector3 ScaleVelocity;
        internal float RotationAngle;
    }

    private sealed record CanvasResizeSession(
        CanvasResizeEdge Edge,
        NativeMethods.POINT StartCursor,
        NativeMethods.RECT StartBounds,
        bool CompactList,
        double StartCanvasScale,
        double StartContentScale,
        double BaseWidthDip,
        double BaseHeightDip,
        double MinimumCanvasScale,
        double MaximumCanvasScale,
        double DisplayScale,
        NativeMethods.RECT WorkArea,
        WindowAlignmentInsets? AlignmentInsets,
        DisplayInfo Display);

    public MainWindow(AppHost host, OrganizerDefinition definition)
    {
        _host = host;
        _iconCache = host.SharedIconCache;
        _definition = definition;
        _expandedBackdrop = new(ThemeTarget.Organizer, "expanded", useRoundedMask: IsDock);
        _storage = new StorageService(AppPaths.ResolveStoragePath(definition), createIfMissing: false);
        _gestureWindowProc = GestureWindowProc;
        _canvasResizeWindowProc = CanvasResizeWindowProc;
        _itemDragBoundaryHookProc = ItemDragBoundaryHookProc;
        InitializeComponent();
        SystemBackdrop = new TransparentWindowBackdrop();
        _compactBackdrop.Attach(CompactSurfaceHost);
        _expandedBackdrop.Attach(ExpandedSurfaceHost);
        ApplyExpandedContentInset();
        ItemsRepeater.ItemsSource = _items;
        _compactSurface = new ThemeSurface(CompactSurfaceHost, CompactThumbnailHost);
        _expandedSurface = new ThemeSurface(ExpandedSurfaceHost, IsDock ? ExpandedPanel : null);
        _compactEdgeSurface = new ThemeEdgeSurface(CompactEdgeOverlay, CompactThumbnailHost);
        _expandedEdgeSurface = new ThemeEdgeSurface(ExpandedEdgeOverlay, IsDock ? ExpandedPanel : null);
        CompactThumbnailHost.SizeChanged += SurfaceHost_SizeChanged;
        ExpandedPanel.SizeChanged += SurfaceHost_SizeChanged;
        WindowRoot.SizeChanged += SurfaceHost_SizeChanged;
        WindowRoot.Loaded += WindowRoot_Loaded;
        _expandedPointerPressedHandler = new PointerEventHandler(ExpandedView_PointerPressed);
        _expandedPointerMovedHandler = new PointerEventHandler(ExpandedView_PointerMoved);
        _expandedPointerReleasedHandler = new PointerEventHandler(ExpandedView_PointerReleased);
        _expandedPointerCanceledHandler = new PointerEventHandler(ExpandedView_PointerCanceled);
        _expandedPointerCaptureLostHandler = new PointerEventHandler(ExpandedView_PointerCaptureLost);
        _expandedPointerWheelChangedHandler = new PointerEventHandler(ExpandedView_PointerWheelChanged);
        ExpandedView.AddHandler(UIElement.PointerPressedEvent, _expandedPointerPressedHandler, handledEventsToo: true);
        ExpandedView.AddHandler(UIElement.PointerMovedEvent, _expandedPointerMovedHandler, handledEventsToo: true);
        ExpandedView.AddHandler(UIElement.PointerReleasedEvent, _expandedPointerReleasedHandler, handledEventsToo: true);
        ExpandedView.AddHandler(UIElement.PointerCanceledEvent, _expandedPointerCanceledHandler, handledEventsToo: true);
        ExpandedView.AddHandler(UIElement.PointerCaptureLostEvent, _expandedPointerCaptureLostHandler, handledEventsToo: true);
        ExpandedView.AddHandler(UIElement.PointerWheelChangedEvent, _expandedPointerWheelChangedHandler, handledEventsToo: true);
        WindowRoot.PointerEntered += WindowRoot_PointerEntered;
        WindowRoot.PointerExited += WindowRoot_PointerExited;
        Title = "TuckPane";
        _desktopCoordinator = new DesktopAttachmentCoordinator(this);
        _catalogController = new OrganizerCatalogController(this);
        _externalDragCoordinator = new ExternalItemDragCoordinator(this);
        _host.ThemeChanged += Host_ThemeChanged;
        ApplyTheme();
        ApplyLanguage();

        _longPressTimer = DispatcherQueue.CreateTimer();
        _longPressTimer.Interval = TimeSpan.FromMilliseconds(LongPressMs);
        _longPressTimer.IsRepeating = false;
        _longPressTimer.Tick += LongPressTimer_Tick;

        _externalHoverTimer = DispatcherQueue.CreateTimer();
        _externalHoverTimer.Interval = TimeSpan.FromMilliseconds(_host.State.GlobalSettings.HoverExpandDelayMs);
        _externalHoverTimer.IsRepeating = false;
        _externalHoverTimer.Tick += ExternalHoverTimer_Tick;

        _interactionSaveTimer = DispatcherQueue.CreateTimer();
        _interactionSaveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _interactionSaveTimer.IsRepeating = false;
        _interactionSaveTimer.Tick += InteractionSaveTimer_Tick;

        _canvasResizeInputTimer = DispatcherQueue.CreateTimer();
        _canvasResizeInputTimer.Interval = TimeSpan.FromMilliseconds(16);
        _canvasResizeInputTimer.IsRepeating = true;
        _canvasResizeInputTimer.Tick += CanvasResizeInputTimer_Tick;

        _itemTouchHoldTimer = DispatcherQueue.CreateTimer();
        _itemTouchHoldTimer.Interval = TimeSpan.FromMilliseconds(LongPressMs);
        _itemTouchHoldTimer.IsRepeating = false;
        _itemTouchHoldTimer.Tick += ItemTouchHoldTimer_Tick;

        Activated += MainWindow_Activated;
        Activated += OrganizerTitleWindow_Activated;
        Closed += MainWindow_Closed;
        InitializeHoverWave();
    }

    private void SurfaceHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSurfaceClips();

    private void WindowRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedXamlRoot is not null)
            _subscribedXamlRoot.Changed -= XamlRoot_Changed;
        _subscribedXamlRoot = WindowRoot.XamlRoot;
        if (_subscribedXamlRoot is not null)
            _subscribedXamlRoot.Changed += XamlRoot_Changed;
        ApplyCompactVisualStyle();
        UpdateSurfaceClips();
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateSurfaceClips();

    private async void InteractionSaveTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!_closing) await SaveStateAsync();
    }

    private void CanvasResizeInputTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) =>
        PollCanvasResizeInput();

    public void ApplyLanguage()
    {
        LocalizeContextMenu(OrganizerContextMenu);
        UpdateContentModeMenuItems();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CollapseButton, AppStrings.Get("Collapse"));
    }

    private void LocalizeContextMenu(MenuFlyout flyout)
    {
        LocalizeContextMenuItems(flyout.Items);
    }

    private void LocalizeContextMenuItems(IEnumerable<MenuFlyoutItemBase> items)
    {
        FontFamily family = new(AppStrings.FontFamily);
        bool station = _definition.PlacementMode == OrganizerPlacementMode.Station;
        foreach (MenuFlyoutItemBase entry in items)
        {
            if (entry is MenuFlyoutSubItem sub)
            {
                if (sub.Tag is string subKey) sub.Text = AppStrings.Get(subKey);
                sub.FontFamily = family;
                sub.CharacterSpacing = AppStrings.CharacterSpacing;
                LocalizeContextMenuItems(sub.Items);
                continue;
            }
            if (entry is not MenuFlyoutItem item) continue;
            // These two organizer menus have text-only commands. A toggle elsewhere
            // in the same menu must not reserve a check column to their left.
            if (item is not ToggleMenuFlyoutItem)
                item.Resources["MenuFlyoutItemPlaceholderThemeThickness"] = new Thickness(0);
            if (item.Tag is not string key) continue;
            item.Text = AppStrings.Get(key);
            item.FontFamily = family;
            item.CharacterSpacing = AppStrings.CharacterSpacing;
            item.Visibility = key switch
            {
                "ContextDuplicate" or "ContextRename" => station ? Visibility.Collapsed : Visibility.Visible,
                _ => Visibility.Visible
            };
        }
    }

    private void UpdateContentModeMenuItems()
    {
        UpdatePlacementModeMenuItems();
        UpdateExpansionModeControls();
        ToggleContentModeMenuItem.Visibility = !OrganizerKinds.IsRegular(_definition.PlacementMode)
            ? Visibility.Collapsed
            : Visibility.Visible;
        ToggleContentModeMenuItem.Text = AppStrings.Get(_definition.ExpandedContentMode == OrganizerExpandedContentMode.Icon
            ? "ContextSwitchToCompactList"
            : "ContextSwitchToIcon");
        ToggleContentModeMenuItem.FontFamily = new FontFamily(AppStrings.FontFamily);
        ToggleContentModeMenuItem.CharacterSpacing = AppStrings.CharacterSpacing;
        RefreshOrganizerMenuVisibility();
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        try
        {
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("初始化失败。", ex);
            _host.ReportOperationError(AppStrings.Get("AppStartupErrorTitle"), ex.Message, _hwnd, _definition.Position?.MonitorDevice);
        }
    }

    internal void InitializeHostWindow()
    {
        if (_hostWindowInitialized) return;
        _hostWindowInitialized = true;
        _hwnd = WindowNative.GetWindowHandle(this);
        _outsideClickHook = new OutsideClickHook(_hwnd, DispatcherQueue, HandleOutsideClick);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Changed += (_, change) =>
        {
            if (change.DidVisibilityChange) _host.RefreshDockRunningSubscription();
        };
        _appWindow.Closing += (_, args) =>
        {
            try
            {
                _compactBackdrop.DetachAndClose();
                _expandedBackdrop.DetachAndClose();
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                AppLogger.Error("Cannot close organizer backdrops safely.", ex);
            }
        };
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        // Cloak, apply tool-window/no-activate styles, enable DWM alpha composition,
        // and pre-position BEFORE Activate() so startup never flashes black default
        // windows, never flashes taskbar icons, and never steals foreground focus.
        BeginStartupCloak();
        DesktopLayerService.ApplyInitialToolWindowStyle(_hwnd);
        DesktopLayerService.ApplyTransparentChrome(_hwnd);
        TransparentWindowBackdrop.EnsureAlphaComposition(_hwnd);
        if (_definition.PlacementMode == OrganizerPlacementMode.Station || IsContained)
        {
            _appWindow.Hide();
        }
        else
        {
            PrePositionCompactHostWindow();
        }
        IntPtr desktopIconView = DesktopLayerService.FindDesktopIconView();
        IntPtr initialOwner = desktopIconView != IntPtr.Zero ? desktopIconView : _host.Console.Hwnd;
        _ = NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, initialOwner);
    }

    internal Task InitializeAsync() => _initializationTask ??= InitializeAndReportAsync();

    private async Task InitializeAndReportAsync()
    {
        try { await InitializeCoreAsync(); }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            _stationReady = false;
            RefreshPerformanceSettings();
            AppLogger.Error("初始化失败。", ex);
            if (!_closing) _host.ReportOperationError(AppStrings.Get("AppStartupErrorTitle"), ex.Message, _hwnd, _definition.Position?.MonitorDevice);
        }
    }

    private async Task InitializeCoreAsync()
    {
        AppPaths.EnsureCreated();
        InitializeHostWindow();
        _ = NativeMethods.SetWindowSubclass(_hwnd, _gestureWindowProc, GestureSubclassId, IntPtr.Zero);

        try
        {
            if (IsDock)
            {
                await InitializeDockAsync();
                return;
            }

            // A Station starts hidden. Native readiness must not wait for its first XAML load.
            if (_definition.PlacementMode == OrganizerPlacementMode.Station)
            {
                DisplayInfo stationDisplay = DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
                _stationDisplay = stationDisplay;
                bool stationScalesNormalized = NormalizeVisualScales(stationDisplay);
                _compactBounds = DisplayPlacementService.CalculateStationAnchor(stationDisplay, _definition.DockEdge, _definition.Position);
                WidgetPosition stationPosition = DisplayPlacementService.Capture(_compactBounds);
                bool correctedDisplay = !PositionsEqual(_definition.Position, stationPosition);
                _definition.Position = stationPosition;
                _desktopLayer = new DesktopLayerService(_hwnd, IntPtr.Zero);
                _appWindow?.Hide();
                CompactView.Visibility = Visibility.Collapsed;
                ExpandedView.Visibility = Visibility.Collapsed;
                _stationReady = true;
                RefreshPerformanceSettings();
                AppLogger.Performance($"station-ready id={OrganizerId} display={stationDisplay.Device}");
                if (correctedDisplay || stationScalesNormalized) await SaveStateAsync();
                return;
            }

            await WaitForLoadedAsync();
            if (_closing) return;
            UpdateOrganizerName();
            // Never reposition during startup scale setup: bounds are restored deterministically below.
            ApplyCompactScale(repositionWindow: false);

            DisplayInfo display = DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
            double windowScale = Math.Max(1, WindowRoot.XamlRoot?.RasterizationScale ?? 1);
            if (_hwnd != IntPtr.Zero)
            {
                windowScale = Math.Max(windowScale, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
            }
            if (display.Monitor.Left == 0 && display.Monitor.Top == 0)
            {
                windowScale = Math.Max(windowScale, NativeMethods.GetDpiForSystem() / 96d);
            }
            display = display with { Scale = Math.Max(display.Scale, windowScale) };
            bool normalizedVisualScales = NormalizeVisualScales(display);
            _compactBounds = RestoreCompactBoundsForDisplay(display, _appliedCompactScale);
            if (!IsContained && _definition.PlacementMode == OrganizerPlacementMode.Positioned &&
                _host.FindCurrentPositionedPlacement(_definition.Id, _compactBounds) is { } positionedPlacement)
            {
                MoveToPositionedPlacement(positionedPlacement.Bounds, positionedPlacement.CompactScale);
                _definition.Position = DisplayPlacementService.Capture(_compactBounds);
                normalizedVisualScales = true;
            }
            AppLogger.Info($"初始化 DPI={display.Scale:0.##}，收起窗口={_compactBounds.Width}x{_compactBounds.Height}px。");

            _desktopLayer = new DesktopLayerService(_hwnd, IntPtr.Zero);
            _runtimeVisible = !IsContained;
            if (IsContained)
            {
                _appWindow?.Hide();
            }
            else
            {
                ApplyBounds(_compactBounds, show: true, preserveZOrder: true);
                _desktopLayer.Reattach(showWindow: true);
                WindowRoot.UpdateLayout();
                UpdateSurfaceClips();
                // Wait for WinUI DirectComposition to flush the first real transparent
                // frame before removing the DWM startup cloak.
                await WaitForNextRenderAsync(_windowLifetime.Token);
            }
            ReleaseStartupCloak();
            if (normalizedVisualScales) await SaveStateAsync();

            if (!_uiSettings.AdvancedEffectsEnabled)
            {
                _host.NotifyTransparencyFallback();
            }
            RefreshPerformanceSettings();
            if (_storage.Exists) StartWatcher();
            await RefreshCatalogAsync(notifyUnsupported: true, refreshIcons: true);
            if (IsPermanentlyExpanded || Environment.GetEnvironmentVariable("GLASSFOLDER_TEST_EXPANDED") == "1")
            {
                await ExpandAsync();
            }
#if DEBUG
            if (Environment.GetEnvironmentVariable("TUCKPANE_TEST_RESIZE_AUTORUN") == "1")
            {
                await RunCanvasResizeProbeAsync();
            }
            if (int.TryParse(Environment.GetEnvironmentVariable("GLASSFOLDER_TEST_ITEM_REORDER_CYCLES"), out int reorderCycles))
            {
                if (!_expanded) await ExpandAsync();
                await RunItemReorderProbeAsync(Math.Clamp(reorderCycles, 0, 30));
            }
            if (Environment.GetEnvironmentVariable("GLASSFOLDER_TEST_ITEM_REORDER_MATRIX") == "1")
            {
                if (!_expanded) await ExpandAsync();
                await RunItemReorderMatrixProbeAsync();
            }
            if (int.TryParse(Environment.GetEnvironmentVariable("GLASSFOLDER_TEST_TRANSITION_CYCLES"), out int testCycles))
            {
                for (int cycle = 0; cycle < Math.Clamp(testCycles, 0, 60); cycle++)
                {
                    if (!_expanded) await ExpandAsync();
                    await CollapseAsync();
                }
            }
#endif
        }
        finally
        {
            if (IsContained || _definition.PlacementMode == OrganizerPlacementMode.Station)
                _appWindow?.Hide();
            ReleaseStartupCloak();
        }
    }

    private async Task WaitForLoadedAsync()
    {
        if (WindowRoot.IsLoaded) return;
        RoutedEventHandler? handler = null;
        bool loaded = await UiEventAwaiter.WaitAsync(
            complete => { handler = (_, _) => complete(); WindowRoot.Loaded += handler; },
            _ => { if (handler is not null) WindowRoot.Loaded -= handler; },
            TimeSpan.FromSeconds(5), _windowLifetime.Token);
        if (!loaded) throw new TimeoutException("The organizer visual tree did not load within 5 seconds.");
    }

    private async Task PrepareStationContentAsync()
    {
        try
        {
            // Show without activation before waiting for Loaded; initialization remains hidden.
            _desktopLayer?.SetExpanded(true, stayTopmost: true);
            ApplyBounds(CalculateExpandedBounds(_compactBounds), show: true);
            await WaitForLoadedAsync();
            _windowLifetime.Token.ThrowIfCancellationRequested();
            UpdateOrganizerName();
            ApplyCompactScale(repositionWindow: false);
            // Slow directory enumeration must not hold the station's expand/collapse state.
            _ = RunSafelyAsync(async () =>
            {
                if (_storage.Exists) StartWatcher();
                await RefreshCatalogAsync(notifyUnsupported: true, refreshIcons: true);
            }, "中转站目录刷新失败");
        }
        catch
        {
            if (!_closing)
            {
                _appWindow?.Hide();
                _desktopLayer?.SetExpanded(false, showWindow: false);
            }
            throw;
        }
    }

#if DEBUG
    private async Task RunItemReorderProbeAsync(int cycles)
    {
        _itemReorderProbeRunning = true;
        try
        {
            for (int cycle = 0; cycle < cycles && _items.Count > 1; cycle++)
            {
                ConfigureItemsLayout();
                Border? host = null;
                for (int attempt = 0; attempt < 12 && host is null; attempt++)
                {
                    _animationTrace?.Metrics.RecordWork(AnimationTraceWork.UpdateLayout);
                    ItemsRepeater.UpdateLayout();
                    host = ItemsRepeater.TryGetElement(0) as Border ?? ItemsRepeater.GetOrCreateElement(0) as Border;
                    if (host is null) await WaitForNextRenderAsync(CancellationToken.None);
                }
                if (host?.DataContext is not WidgetItem item)
                    throw new InvalidOperationException("The reorder verification item was not realized.");

                double width = Math.Max(1, host.Width);
                double height = Math.Max(1, host.Height);
                Point press = new(width / 2, height / 2);
                var session = new ItemReorderSession(item.RelativeName, 0, press, press);
                session.Activate(press);
                _itemReorderSession = session;
                _itemDragHost = host;
                _itemDragPointerType = PointerDeviceType.Mouse;
                StartItemReorder();
                session.UpdateTarget(
                    new Point(press.X + width + GetItemLayoutGapDip(), press.Y),
                    width,
                    height,
                    GetItemLayoutGapDip(),
                    GetItemLayoutColumnCount(),
                    _items.Count);
                session.TryBeginPreviewTransition(_items.Count, out _, out _);
                ApplyAllProvisionalItemVisuals(animate: true);
                await CommitItemReorderAsync(session);
                AppLogger.Info($"内部换序探针完成 {cycle + 1}/{cycles}。");
            }
        }
        finally
        {
            _itemReorderProbeRunning = false;
        }
    }

    private async Task RunItemReorderMatrixProbeAsync()
    {
        int rows = Math.Max(1, _definition.Layout.Rows);
        int columns = GetItemLayoutColumnCount();
        int expectedCount = rows * columns;
        if (_items.Count != expectedCount)
            throw new InvalidOperationException($"The {columns}x{rows} reorder matrix requires exactly {expectedCount} items.");

        _itemReorderProbeRunning = true;
        try
        {
            ConfigureItemsLayout();
            _animationTrace?.Metrics.RecordWork(AnimationTraceWork.UpdateLayout);
            ItemsRepeater.UpdateLayout();
            await WaitForNextRenderAsync(CancellationToken.None);
            var slotOrigins = new Point[expectedCount];
            for (int index = 0; index < expectedCount; index++)
            {
                if (ItemsRepeater.TryGetElement(index) is not Border host)
                    throw new InvalidOperationException($"The {columns}x{rows} reorder element {index} was not realized.");
                slotOrigins[index] = host.TransformToVisual(ItemsRepeater).TransformPoint(new Point());
            }
            for (int source = 0; source < expectedCount; source++)
            {
                for (int target = 0; target < expectedCount; target++)
                {
                    if (source == target) continue;
                    if (ItemsRepeater.TryGetElement(source) is not Border sourceHost || sourceHost.DataContext is not WidgetItem item ||
                        ItemsRepeater.TryGetElement(target) is not Border targetHost)
                    {
                        throw new InvalidOperationException($"The {columns}x{rows} reorder element {source}->{target} was not realized.");
                    }

                    Point sourceOrigin = sourceHost.TransformToVisual(ItemsRepeater).TransformPoint(new Point());
                    Point targetOrigin = targetHost.TransformToVisual(ItemsRepeater).TransformPoint(new Point());
                    Point press = new(sourceOrigin.X + sourceHost.Width / 2, sourceOrigin.Y + sourceHost.Height / 2);
                    Point destination = new(targetOrigin.X + targetHost.Width / 2, targetOrigin.Y + targetHost.Height / 2);
                    var session = new ItemReorderSession(item.RelativeName, source, press, new Point(sourceHost.Width / 2, sourceHost.Height / 2));
                    session.Activate(press);
                    _itemReorderSession = session;
                    _itemDragHost = sourceHost;
                    session.UpdateTarget(destination, sourceHost.Width, sourceHost.Height, GetItemLayoutGapDip(), columns, _items.Count);
                    if (session.TargetIndex != target)
                        throw new InvalidOperationException($"The {columns}x{rows} target resolved as {session.TargetIndex}, expected {target}.");

                    session.TryBeginPreviewTransition(_items.Count, out _, out _);
                    ApplyAllProvisionalItemVisuals(animate: false);
                    int[] mapping = session.GetVisualIndices(_items.Count);
                    if (mapping.Distinct().Count() != _items.Count)
                        throw new InvalidOperationException($"The {columns}x{rows} mapping {source}->{target} contains duplicate slots.");
                    for (int index = 0; index < _items.Count; index++)
                    {
                        if (index == source || ItemsRepeater.TryGetElement(index) is not Border host) continue;
                        int visualIndex = mapping[index];
                        Vector2 expected = new(
                            (float)(slotOrigins[visualIndex].X - slotOrigins[index].X),
                            (float)(slotOrigins[visualIndex].Y - slotOrigins[index].Y));
                        Vector3 actual = GetItemMotion(host).TranslationTarget;
                        if (Vector2.DistanceSquared(expected, new Vector2(actual.X, actual.Y)) > 1f)
                        {
                            throw new InvalidOperationException(
                                $"The {columns}x{rows} visual target {source}->{target} is incorrect for item {index}: " +
                                $"expected ({expected.X:0.##},{expected.Y:0.##}), actual ({actual.X:0.##},{actual.Y:0.##}).");
                        }
                    }
                    CancelItemReorder(runPendingRefresh: false);
                }
            }
            AppLogger.Info($"满{columns}x{rows}换序矩阵完成{expectedCount * (expectedCount - 1)}/{expectedCount * (expectedCount - 1)}。");
        }
        finally
        {
            CancelItemReorder(runPendingRefresh: false);
            _itemReorderProbeRunning = false;
        }
    }
#endif

    internal void StartWatcher() => _catalogController.StartWatcher();

    internal Task RefreshCatalogAsync(bool notifyUnsupported, bool refreshIcons = false) =>
        _catalogController.RefreshCatalogAsync(notifyUnsupported, refreshIcons);

    internal Task StartCatalogRefreshIfReady() => _catalogController.StartCatalogRefreshIfReady();

    internal void ApplyCatalogItemsAndRender(IReadOnlyList<WidgetItem> ordered, bool refreshIcons) =>
        ApplyFilteredCatalogItems(ordered, refreshIcons);

    private void ReconcileCatalogItems(IReadOnlyList<WidgetItem> desired)
    {
        _itemCollectionMoveInProgress = true;
        try
        {
            CatalogCollectionSync.Apply(_items, desired);
            if (IsDock) _host.RefreshDockRunningSubscription(invalidate: true);
        }
        finally
        {
            _itemCollectionMoveInProgress = false;
        }
    }

    private void RenderAll()
    {
        UpdateOrganizerName();
        RenderCompactPreview();
        RenderItems();
    }

    private void UpdateOrganizerName()
    {
        CompactNameText.Text = _definition.Name;
        ExpandedNameText.Text = _definition.Name;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExpandedNameEditor, AppStrings.Get("ContextRename"));
        ExpandedNameText.FontSize = 42 * _host.State.GlobalSettings.ResolveExpandedNameScale(_definition.PlacementMode);
        ApplyOrganizerNameVisibility();
        SyncCompactNameShadow();
        SyncExpandedNameShadow();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExpandedNameText, _definition.Name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CompactTile, _definition.Name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(CompactTile, $"OrganizerCompact-{_definition.Id:N}");
    }

    private void RenderCompactPreview()
    {
        bool storageExists = _storage.Exists;
        CompactWarningBadge.Visibility = storageExists ? Visibility.Collapsed : Visibility.Visible;
        CompactEmptyGlyph.Visibility = _items.Count == 0 && storageExists ? Visibility.Visible : Visibility.Collapsed;
        string compactHelp = storageExists ? _definition.Name : AppStrings.Get("MissingStorage");
        ToolTipService.SetToolTip(CompactTile, storageExists && _definition.HideName ? null : compactHelp);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(CompactTile, storageExists ? string.Empty : compactHelp);
        UpdateCompactThumbnailMetrics();
        UpdateCompactPreviewItemScale();
        _ = RenderCompactPreviewAsync(++_compactPreviewVersion);
    }

    private async Task RenderCompactPreviewAsync(int version)
    {
        IReadOnlyList<WidgetItem> preview = _items.Take(CompactPreviewItemCount).ToArray();
        var bitmaps = new BitmapImage?[preview.Count];
        for (int index = 0; index < preview.Count; index++)
        {
            if (preview[index].Kind == WidgetItemKind.Organizer) continue;
            bitmaps[index] = IsDocumentItem(preview[index])
                ? GetDocumentIcon(preview[index])
                : await GetIconThrottledAsync(preview[index].FullPath);
        }
        if (version != _compactPreviewVersion || _closing) return;

        CompactEmptyGlyph.Visibility = preview.Count == 0 && _storage.Exists ? Visibility.Visible : Visibility.Collapsed;
        Image[] images = [CompactPreviewImage0, CompactPreviewImage1, CompactPreviewImage2, CompactPreviewImage3];
        OrganizerPreviewControl[] organizerPreviews =
            [CompactOrganizerPreview0, CompactOrganizerPreview1, CompactOrganizerPreview2, CompactOrganizerPreview3];
        for (int index = 0; index < images.Length; index++)
        {
            bool occupied = index < preview.Count;
            bool organizerItem = occupied && preview[index] is { Kind: WidgetItemKind.Organizer, OrganizerId: not null };
            images[index].Source = occupied && !organizerItem ? bitmaps[index] : null;
            images[index].Visibility = occupied && !organizerItem ? Visibility.Visible : Visibility.Collapsed;
            organizerPreviews[index].Visibility = organizerItem ? Visibility.Visible : Visibility.Collapsed;
            organizerPreviews[index].RequestToken = null;
            if (organizerItem)
            {
                object requestToken = new();
                organizerPreviews[index].RequestToken = requestToken;
                _ = LoadOrganizerPreviewAsync(
                    organizerPreviews[index],
                    preview[index].OrganizerId!.Value,
                    requestToken);
            }
        }
    }

    private void RefreshRealizedIcons()
    {
        for (int index = 0; index < _items.Count; index++)
        {
            if (!TryGetRealizedItemHost(index, out Border host) || host.DataContext is not WidgetItem item ||
                IsDocumentItem(item) || item.Kind == WidgetItemKind.Organizer) continue;
            if (GetOrResolveItemHostParts(host) is { } parts)
            {
                parts.ItemImage.ClearValue(Image.SourceProperty);
                _ = LoadIconAsync(parts.ItemImage, item.FullPath, parts.FallbackIcon, refresh: true, dockPresentation: IsDock);
            }
        }
    }

    private void UpdateCompactPreviewItemScale()
    {
        Grid grid = CompactPreviewGrid;
        grid.Margin = new Thickness(Math.Max(2.5, 4.4 * _appliedCompactScale));
        grid.ColumnSpacing = Math.Max(1.2, 1.8 * _appliedCompactScale);
        grid.RowSpacing = Math.Max(1.2, 1.8 * _appliedCompactScale);
        CompactEmptyGlyph.FontSize = Math.Max(12, Math.Round(16 * _appliedCompactScale));
        double iconSize = CalculateCompactPreviewIconSize(
            grid,
            Math.Max(1, grid.RowDefinitions.Count),
            Math.Max(1, grid.ColumnDefinitions.Count));
        Image[] images = [CompactPreviewImage0, CompactPreviewImage1, CompactPreviewImage2, CompactPreviewImage3];
        OrganizerPreviewControl[] organizerPreviews =
            [CompactOrganizerPreview0, CompactOrganizerPreview1, CompactOrganizerPreview2, CompactOrganizerPreview3];
        foreach (Image image in images)
        {
            image.Width = iconSize;
            image.Height = iconSize;
            image.HorizontalAlignment = HorizontalAlignment.Center;
            image.VerticalAlignment = VerticalAlignment.Center;
        }
        foreach (OrganizerPreviewControl preview in organizerPreviews)
        {
            preview.Width = iconSize;
            preview.Height = iconSize;
            preview.HorizontalAlignment = HorizontalAlignment.Center;
            preview.VerticalAlignment = VerticalAlignment.Center;
            preview.SetCompactVisual(true);
        }
    }

    private double CalculateCompactPreviewIconSize(Grid grid, int rows, int columns)
    {
        double width = CompactThumbnailHost.Width - grid.Margin.Left - grid.Margin.Right - grid.ColumnSpacing * Math.Max(0, columns - 1);
        double height = CompactThumbnailHost.Height - grid.Margin.Top - grid.Margin.Bottom - grid.RowSpacing * Math.Max(0, rows - 1);
        double slot = Math.Min(width / columns, height / rows);
        return Math.Max(2, slot * OrganizerLimits.CalculateCompactPreviewIconFraction(_definition.ItemScale));
    }

    private void RenderItems()
    {
        if (IsDock) { ApplyDockLayout(); return; }
        ConfigureItemsLayout();
    }

    private void ConfigureItemsLayout(bool updateItems = true)
    {
        if (IsDock)
        {
            ApplyDockContentLayout();
            if (updateItems) UpdateRealizedItems();
            InvalidateHoverGeometry();
            return;
        }
        _animationTrace?.Metrics.RecordWork(AnimationTraceWork.ConfigureLayout);
        Size viewport = GetItemsViewportSize();
        double width = viewport.Width;
        double height = viewport.Height;
        if (width <= 1 || height <= 1) return;
        ItemsScrollView.HorizontalScrollMode = ScrollingScrollMode.Disabled;
        ItemsScrollView.VerticalScrollMode = ScrollingScrollMode.Enabled;
        ItemsScrollView.IgnoredInputKinds = ScrollingInputKinds.MouseWheel;
        ItemsRepeater.Width = double.NaN;
        ItemsRepeater.Height = double.NaN;
        (double cellWidth, double cellHeight) = GetItemCellSizeDip(width, height);
        ItemsRepeater.Width = width;
        if (!TryConfigureFreeSlotGridLayout(width, height, cellWidth, cellHeight))
        {
            _gridLayout.MinItemWidth = cellWidth;
            _gridLayout.MinItemHeight = cellHeight;
            _gridLayout.MinColumnSpacing = GetItemLayoutGapDip();
            _gridLayout.MinRowSpacing = GetItemLayoutGapDip();
            _gridLayout.MaximumRowsOrColumns = GetItemLayoutColumnCount();
            if (!ReferenceEquals(ItemsRepeater.Layout, _gridLayout)) ItemsRepeater.Layout = _gridLayout;
        }
        if (updateItems) UpdateRealizedItems();
        ReconcileSmoothScrollBounds();
        InvalidateHoverGeometry();
    }

    private void ApplyExpandedContentInset()
    {
        if (IsDock) { ApplyDockContentLayout(); return; }
        bool station = _definition.PlacementMode == OrganizerPlacementMode.Station;
        ExpandedTitleRow.Height = new GridLength(
            station ? 0 : DisplayPlacementService.ExpandedTitleBandDip);
        ApplyOrganizerNameVisibility();
        CollapseButton.Visibility = station || IsPermanentlyExpanded ? Visibility.Collapsed : Visibility.Visible;
        RefreshCollapseIndicator();
        double side = DisplayPlacementService.ResolveExpandedSideInset(
            _definition.PlacementMode,
            _definition.ExpandedContentMode);
        double scale = ContentScale;
        var inset = new Thickness(
            (IsCompactList ? OrganizerContentScale.CompactLeftInset(side, _definition.CompactListItemScale) : side) * scale,
            (station ? DisplayPlacementService.StationTopInsetDip : DisplayPlacementService.ExpandedTopInsetDip) * scale,
            side * scale,
            (station ? DisplayPlacementService.StationBottomInsetDip : DisplayPlacementService.ExpandedBottomInsetDip) * scale);
        // Icon rows must be clipped at the content edge, not at the panel edge.
        // A margin inside the scroller lets the next row occupy its bottom inset.
        ItemsScrollView.Margin = IsCompactList ? new Thickness(0) : inset;
        ItemsRepeater.Margin = IsCompactList ? inset : new Thickness(0);
    }

    private Size GetItemsViewportSize()
    {
        Thickness margin = ItemsRepeater.Margin;
        return new Size(
            Math.Max(0, ItemsScrollView.ActualWidth - margin.Left - margin.Right),
            Math.Max(0, ItemsScrollView.ActualHeight - margin.Top - margin.Bottom));
    }

    private bool IsCompactList =>
        OrganizerKinds.IsRegular(_definition.PlacementMode) &&
        _definition.ExpandedContentMode == OrganizerExpandedContentMode.CompactList;

    private int GetItemLayoutColumnCount() => IsDock
        ? (_definition.DockOrientation == DockOrientation.Horizontal ? Math.Max(1, _items.Count) : 1)
        : IsCompactList ? 1 : Math.Max(1, _definition.Layout.Columns);

    private double GetItemLayoutGapDip() => IsDock ? _dockGeometry.Pitch - _dockGeometry.IconSize : IsCompactList ? 0 : ItemGapDip * ContentScale;

    private (double Width, double Height) GetItemCellSizeDip(double width, double height) => IsDock
        ? (_dockGeometry.IconSize, _dockGeometry.IconSize) : IsCompactList
        ? (width, CompactListItemHeightDip * _definition.CompactListItemScale * ContentScale)
        : GetScaledGridCellSize(width, height);

    private void ItemsRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Border host || host.DataContext is not WidgetItem item) return;
        _itemElementsPrepared++;
        _realizedItemHosts.Register(item.RelativeName, host);
        RegisterHoverItem(host);
        PrepareItemElement(host, item, loadIcon: true);
        ResetItemMotion(host);
        ApplyItemSelectionVisual(host, item.RelativeName);
        int itemIndex = IndexOfItem(item.RelativeName);
        if (!_itemCollectionMoveInProgress && itemIndex >= 0) ApplyProvisionalItemVisual(host, itemIndex, animate: false);
        if (_shellDragActive && _itemReorderSession is { } session &&
            item.RelativeName.Equals(session.RelativeName, StringComparison.OrdinalIgnoreCase))
        {
            ApplyNativeSourcePlaceholder(host);
        }
    }

    private void ItemsRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is not Border host) return;
        _itemElementsCleared++;
        _realizedItemHosts.Unregister(host);
        UnregisterHoverItem(host);
        if (!_itemCollectionMoveInProgress && ReferenceEquals(host, _itemDragHost))
        {
            if (_shellDragActive) _itemDragHost = null;
            else CancelItemReorder();
        }
        _itemMotionStates.Remove(host);
        DisposeItemHostParts(host);
        host.Translation = Vector3.Zero;
        host.Scale = Vector3.One;
        host.Opacity = 1;
        host.Shadow = null;
        host.Background = _transparentItemBrush;
        host.BorderBrush = ItemTransparentBorderBrush;
        host.BorderThickness = new Thickness(1);
        ResetItemContentTransform(host);
        Canvas.SetZIndex(host, 0);
    }

    private void UpdateRealizedItems()
    {
        for (int index = 0; index < _items.Count; index++)
        {
            if (TryGetRealizedItemHost(index, out Border host) && host.DataContext is WidgetItem item)
            {
                PrepareItemElement(host, item, loadIcon: false);
            }
        }
    }

    private bool TryGetRealizedItemHost(int itemIndex, out Border host)
    {
        if (itemIndex >= 0 && itemIndex < _items.Count &&
            TryGetRealizedItemHost(_items[itemIndex].RelativeName, out host)) return true;

        host = null!;
        return false;
    }

    private bool TryGetRealizedItemHost(string relativeName, out Border host)
    {
        if (_realizedItemHosts.TryGet(relativeName, out Border candidate) &&
            candidate.DataContext is WidgetItem item &&
            item.RelativeName.Equals(relativeName, StringComparison.OrdinalIgnoreCase) &&
            candidate.Tag is string tag &&
            tag.Equals(relativeName, StringComparison.OrdinalIgnoreCase))
        {
            host = candidate;
            return true;
        }

        if (candidate is not null) _realizedItemHosts.Unregister(candidate);
        if (_itemDragTraceEnabled && _itemReorderSession is not null && _itemDragIdentityWarnings.Add(relativeName))
        {
            AppLogger.Error($"换序跟踪：宿主身份解析失败 identity={relativeName} revision={_itemReorderSession.PreviewRevision}。");
        }
        host = null!;
        return false;
    }

    private void PrepareItemElement(Border host, WidgetItem item, bool loadIcon)
    {
        host.Tag = item.RelativeName;
        SetItemToolTip(host, IsDock || IsDocumentItem(item) || item.Kind == WidgetItemKind.Organizer ? item.Name : item.FullPath);
        if (item.NoteId is Guid noteId)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, $"NoteItem-{noteId:N}");
        }
        else if (item.Kind == WidgetItemKind.PortableNote)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, $"PortableNoteItem-{item.RelativeName}");
        }
        else if (item.Kind == WidgetItemKind.PortableTodo)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, $"PortableTodoItem-{item.RelativeName}");
        }
        else if (item is { Kind: WidgetItemKind.Organizer, OrganizerId: Guid organizerId })
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, $"OrganizerItem-{organizerId:N}");
        }
        else
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            host.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty);
        }
        Size viewport = GetItemsViewportSize();
        SizePreparedElement(host, viewport.Width, viewport.Height);

        if (GetOrResolveItemHostParts(host) is { } parts)
        {
            Grid iconContainer = parts.IconContainer;
            StackPanel itemContent = parts.ItemContent;
            FontIcon fallback = parts.FallbackIcon;
            Image image = parts.ItemImage;
            OrganizerPreviewControl organizerPreview = parts.OrganizerPreview;
            TextBlock name = parts.NameText;

            bool compactList = IsCompactList;
            double contentScale = ContentScale;
            double compactListScale = _definition.CompactListItemScale * contentScale;
            bool organizerItem = item is { Kind: WidgetItemKind.Organizer, OrganizerId: not null };
            double iconMaximum = _definition.PlacementMode == OrganizerPlacementMode.Station
                ? Math.Max(18, Math.Min(host.Width * .82, host.Height * .68))
                : Math.Max(18 * contentScale, Math.Min(host.Width, host.Height) * .68);
            double iconSize = IsDock ? _dockGeometry.IconSize : compactList
                ? CompactListIconSizeDip * compactListScale
                : Math.Clamp(72 * _definition.ItemScale, 18, iconMaximum / contentScale) * contentScale;
            if (_definition.PlacementMode == OrganizerPlacementMode.Station) iconSize = SnapDip(iconSize);
            iconContainer.Width = iconSize;
            iconContainer.Height = iconSize;
            image.Width = iconSize;
            image.Height = iconSize;
            organizerPreview.Width = iconSize;
            organizerPreview.Height = iconSize;
            organizerPreview.SetCompactVisual(false);
            organizerPreview.EmptyStateIcon.FontSize = Math.Max(10, iconSize * .42);
            fallback.FontSize = iconSize * .58;
            fallback.Glyph = item.Kind switch
            {
                WidgetItemKind.Folder => "\uE8B7",
                WidgetItemKind.Note or WidgetItemKind.PortableNote or WidgetItemKind.PortableTodo => "\uE70F",
                WidgetItemKind.Organizer => "\uE8B7",
                _ => "\uE7C3"
            };
            fallback.Foreground = item.Kind == WidgetItemKind.Folder ? _folderFallbackBrush : _fileFallbackBrush;
            host.CornerRadius = new CornerRadius(IsDock ? 0 : compactList ? 4 * compactListScale : 16 * contentScale);
            host.Padding = IsDock ? new Thickness(0) : compactList
                ? new Thickness(4 * compactListScale, 0, 4 * compactListScale, 0)
                : new Thickness(Math.Max(2, 5 * _definition.ItemScale) * contentScale);
            itemContent.Orientation = compactList ? Orientation.Horizontal : Orientation.Vertical;
            itemContent.HorizontalAlignment = compactList ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            itemContent.Spacing = compactList ? 8 * compactListScale : Math.Max(2, 6 * _definition.ItemScale) * contentScale;
            name.FontSize = compactList
                ? CompactListFontSizeDip * compactListScale
                : Math.Min(Math.Max(8, 13 * _definition.ItemScale), Math.Max(8, host.Height / contentScale * .15)) * contentScale;
            name.Width = compactList ? Math.Max(24 * contentScale, host.Width - iconSize - 20 * compactListScale) : double.NaN;
            name.MaxWidth = compactList ? name.Width : Math.Max(24 * contentScale, host.Width - 10 * contentScale);
            name.HorizontalAlignment = compactList ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            name.VerticalAlignment = VerticalAlignment.Center;
            name.TextAlignment = compactList ? TextAlignment.Left : TextAlignment.Center;
            name.Foreground = _organizerTextBrush;
            name.Visibility = IsDock ? Visibility.Collapsed : Visibility.Visible;
            parts.NameContainer.HorizontalAlignment = compactList ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            parts.NameContainer.Visibility = IsDock ? Visibility.Collapsed : Visibility.Visible;
            parts.NameShadowHost.HorizontalAlignment = compactList ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            parts.NameShadowHost.VerticalAlignment = VerticalAlignment.Center;
            SyncItemNameShadow(parts, DesktopTypographyService.IsHighContrast());
            bool showOrganizerPreview = organizerItem && !compactList;
            organizerPreview.Visibility = showOrganizerPreview ? Visibility.Visible : Visibility.Collapsed;
            image.Visibility = showOrganizerPreview ? Visibility.Collapsed : Visibility.Visible;
            string iconKey = IsDocumentItem(item) || organizerItem ? item.RelativeName : item.FullPath;
            bool itemChanged = !string.Equals(image.Tag as string, iconKey, StringComparison.OrdinalIgnoreCase);
            if (itemChanged)
            {
                image.Tag = iconKey;
                image.ClearValue(Image.SourceProperty);
                fallback.Visibility = Visibility.Collapsed;
            }
            if (showOrganizerPreview)
            {
                object requestToken = new();
                organizerPreview.RequestToken = requestToken;
                _ = LoadOrganizerPreviewAsync(organizerPreview, item.OrganizerId!.Value, requestToken);
            }
            else if (organizerItem)
            {
                organizerPreview.RequestToken = null;
                image.Source = _organizerIcon;
            }
            else
            {
                organizerPreview.RequestToken = null;
                if (IsDocumentItem(item)) image.Source = GetDocumentIcon(item);
                else if (loadIcon && (itemChanged || image.Source is null)) _ = LoadIconAsync(image, item.FullPath, fallback, dockPresentation: IsDock);
            }
            bool iconLoadPending = _pendingIconLoads.TryGetValue(image, out string? pendingPath) &&
                pendingPath.Equals(iconKey, StringComparison.OrdinalIgnoreCase);
            bool preserveKnownFailure = !loadIcon && !itemChanged && image.Source is null && !showOrganizerPreview;
            if (!preserveKnownFailure)
            {
                fallback.Visibility = OrganizerInteractionMath.ShouldShowItemFallback(
                    image.Source is not null,
                    iconLoadPending,
                    showOrganizerPreview)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private void SizePreparedElement(FrameworkElement element, double width, double height)
    {
        (element.Width, element.Height) = GetItemCellSizeDip(width, height);
    }

    private void PrepareReorderedItemIdentity(Border host, WidgetItem item)
    {
        if (item.Kind == WidgetItemKind.Organizer)
        {
            PrepareItemElement(host, item, loadIcon: true);
            return;
        }
        host.Tag = item.RelativeName;
        SetItemToolTip(host, IsDock || IsDocumentItem(item) ? item.Name : item.FullPath);
        if (item.NoteId is Guid noteId)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, $"NoteItem-{noteId:N}");
        }
        else if (item.Kind == WidgetItemKind.PortableNote)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, $"PortableNoteItem-{item.RelativeName}");
        }
        else if (item.Kind == WidgetItemKind.PortableTodo)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(host, $"PortableTodoItem-{item.RelativeName}");
        }
        else
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, item.Name);
            host.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty);
        }
        if (GetOrResolveItemHostParts(host) is { } parts)
        {
            FontIcon fallback = parts.FallbackIcon;
            Image image = parts.ItemImage;
            fallback.Glyph = item.Kind switch
            {
                WidgetItemKind.Folder => "\uE8B7",
                WidgetItemKind.Note or WidgetItemKind.PortableNote or WidgetItemKind.PortableTodo => "\uE70F",
                _ => "\uE7C3"
            };
            fallback.Foreground = item.Kind == WidgetItemKind.Folder ? _folderFallbackBrush : _fileFallbackBrush;
            string iconKey = IsDocumentItem(item) ? item.RelativeName : item.FullPath;
            if (!string.Equals(image.Tag as string, iconKey, StringComparison.OrdinalIgnoreCase))
            {
                image.Tag = iconKey;
                image.ClearValue(Image.SourceProperty);
            }
            if (IsDocumentItem(item)) image.Source = GetDocumentIcon(item);
            else _ = LoadIconAsync(image, item.FullPath, fallback, dockPresentation: IsDock);
            bool iconLoadPending = _pendingIconLoads.TryGetValue(image, out string? pendingPath) &&
                pendingPath.Equals(iconKey, StringComparison.OrdinalIgnoreCase);
            fallback.Visibility = OrganizerInteractionMath.ShouldShowItemFallback(
                image.Source is not null,
                iconLoadPending,
                organizerPreviewVisible: false)
                ? Visibility.Visible
                : Visibility.Collapsed;
            SyncItemNameShadow(parts, DesktopTypographyService.IsHighContrast());
        }
    }

    private ItemHostParts? GetOrResolveItemHostParts(Border host)
    {
        if (_itemHostParts.TryGetValue(host, out ItemHostParts? cached))
            return cached;
        if (FindItemPart<Grid>(host, "ItemIconContainer") is not Grid iconContainer ||
            FindItemPart<StackPanel>(host, "ItemContent") is not StackPanel itemContent ||
            FindItemPart<FontIcon>(host, "ItemFallbackIcon") is not FontIcon fallback ||
            FindItemPart<Image>(host, "ItemImage") is not Image image ||
            FindItemPart<OrganizerPreviewControl>(host, "OrganizerPreview") is not OrganizerPreviewControl organizerPreview ||
            FindItemPart<Grid>(host, "ItemNameContainer") is not Grid nameContainer ||
            FindItemPart<Grid>(host, "ItemNameShadowHost") is not Grid nameShadowHost ||
            FindItemPart<TextBlock>(host, "ItemNameText") is not TextBlock nameText)
        {
            return null;
        }
        var parts = new ItemHostParts(
            iconContainer, itemContent, fallback, image, organizerPreview, nameContainer, nameShadowHost, nameText);
        parts.NameSizeChangedHandler = (_, _) =>
        {
            if (!_closing) SyncItemNameShadow(parts, DesktopTypographyService.IsHighContrast());
        };
        nameText.SizeChanged += parts.NameSizeChangedHandler;
        _itemHostParts[host] = parts;
        return parts;
    }

    private void SyncItemNameShadow(ItemHostParts parts, bool highContrast)
    {
        if (_closing || !WindowRoot.IsLoaded) return;
        bool visible = !IsDock &&
                       !highContrast &&
                       parts.NameText.Visibility == Visibility.Visible &&
                       !string.IsNullOrEmpty(parts.NameText.Text);
        if (!visible)
        {
            if (parts.NameShadowVisual is not null)
                parts.NameShadowVisual.IsVisible = false;
            return;
        }

        double width = parts.NameText.ActualWidth > 0 ? parts.NameText.ActualWidth : parts.NameText.DesiredSize.Width;
        double height = parts.NameText.ActualHeight > 0 ? parts.NameText.ActualHeight : parts.NameText.DesiredSize.Height;
        if (width <= 1 || height <= 1)
        {
            if (parts.NameShadowVisual is not null)
                parts.NameShadowVisual.IsVisible = false;
            return;
        }

        if (parts.NameShadowVisual is null)
        {
            Visual hostVisual = ElementCompositionPreview.GetElementVisual(parts.NameShadowHost);
            Compositor compositor = hostVisual.Compositor;
            parts.NameDropShadow = compositor.CreateDropShadow();
            parts.NameDropShadow.Color = ColorHelper.FromArgb(204, 0, 0, 0);
            parts.NameDropShadow.BlurRadius = 3.4f;
            parts.NameDropShadow.Offset = new Vector3(0f, 0.8f, 0f);
            parts.NameShadowVisual = compositor.CreateSpriteVisual();
            parts.NameShadowVisual.Shadow = parts.NameDropShadow;
            ElementCompositionPreview.SetElementChildVisual(parts.NameShadowHost, parts.NameShadowVisual);
        }

        Vector2 size = new((float)width, (float)height);
        parts.NameShadowHost.Width = size.X;
        parts.NameShadowHost.Height = size.Y;
        parts.NameShadowVisual.Size = size;

        CompositionBrush mask = parts.NameText.GetAlphaMask();
        CompositionBrush? previousMask = parts.NameAlphaMask;
        parts.NameAlphaMask = mask;
        parts.NameDropShadow!.Mask = mask;
        if (previousMask is not null && !ReferenceEquals(previousMask, mask))
            previousMask.Dispose();
        parts.NameShadowVisual.IsVisible = true;
    }

    private void UpdateAllRealizedItemNameShadows(bool highContrast)
    {
        foreach (ItemHostParts parts in _itemHostParts.Values)
            SyncItemNameShadow(parts, highContrast);
    }

    private void DisposeItemHostParts(Border host)
    {
        if (!_itemHostParts.Remove(host, out ItemHostParts? parts)) return;
        DisposeItemHostPartsCore(parts);
    }

    private void DisposeAllItemNameShadows()
    {
        foreach (ItemHostParts parts in _itemHostParts.Values)
            DisposeItemHostPartsCore(parts);
        _itemHostParts.Clear();
    }

    private static void DisposeItemHostPartsCore(ItemHostParts parts)
    {
        if (parts.NameSizeChangedHandler is not null)
        {
            parts.NameText.SizeChanged -= parts.NameSizeChangedHandler;
            parts.NameSizeChangedHandler = null;
        }
        if (parts.NameShadowVisual is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(parts.NameShadowHost, null);
            parts.NameShadowVisual.Dispose();
            parts.NameShadowVisual = null;
        }
        if (parts.NameDropShadow is not null)
        {
            parts.NameDropShadow.Mask = null;
            parts.NameDropShadow.Dispose();
            parts.NameDropShadow = null;
        }
        parts.NameAlphaMask?.Dispose();
        parts.NameAlphaMask = null;
    }

    private static T? FindItemPart<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T match && match.Name == name) return match;
        int children = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < children; index++)
        {
            if (VisualTreeHelper.GetChild(root, index) is DependencyObject child &&
                FindItemPart<T>(child, name) is { } descendant) return descendant;
        }
        return null;
    }

    private void SetItemInteractionVisual(Border host, bool pressed)
    {
        bool selected = host.Tag is string name && _itemSelection.Contains(name);
        if (IsDock || IsCompactList)
        {
            host.Background = _transparentItemBrush;
            host.BorderBrush = selected && !IsDock ? ItemSelectedBorderBrush : ItemTransparentBorderBrush;
        }
        else if (selected)
        {
            host.Background = pressed ? ItemSelectedPressedBgBrush : ItemSelectedHoverBgBrush;
            host.BorderBrush = ItemSelectedBorderBrush;
        }
        else
        {
            host.Background = pressed ? _pressedItemBrush : _hoveredItemBrush;
            host.BorderBrush = ItemHoverBorderBrush;
        }
        SetHoverPressed(host, pressed);
        if (IsCompactList) RefreshCompactRowHighlight();
    }

    private void ResetItemInteractionVisual(Border host)
    {
        bool selected = host.Tag is string name && _itemSelection.Contains(name);
        if (selected && !IsDock)
        {
            host.Background = ItemSelectedBgBrush;
            host.BorderBrush = ItemSelectedBorderBrush;
        }
        else
        {
            host.Background = _transparentItemBrush;
            host.BorderBrush = ItemTransparentBorderBrush;
        }
        SetHoverPressed(host, pressed: false);
        if (IsCompactList) RefreshCompactRowHighlight();
    }

    private void ResetItemContentTransform(Border host)
    {
        if (_hoverItems.TryGetValue(host, out HoverItemVisual? item)) ResetHoverVisual(host, item);
    }

    private async Task LoadIconAsync(
        Image image,
        string path,
        FontIcon? fallback = null,
        bool refresh = false,
        bool dockPresentation = false)
    {
        _pendingIconLoads[image] = path;
        if (fallback is not null)
        {
            fallback.Visibility = OrganizerInteractionMath.ShouldShowItemFallback(
                image.Source is not null,
                iconLoadPending: true,
                organizerPreviewVisible: false)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        BitmapImage? bitmap = await GetIconThrottledAsync(path, refresh, dockPresentation);
        if (_closing) return;
        bool requestIsCurrent = _pendingIconLoads.TryGetValue(image, out string? pendingPath) &&
            pendingPath.Equals(path, StringComparison.OrdinalIgnoreCase);
        if (requestIsCurrent) _pendingIconLoads.Remove(image);
        if (string.Equals(image.Tag as string, path, StringComparison.OrdinalIgnoreCase))
        {
            if (bitmap is not null) image.Source = bitmap;
            else image.ClearValue(Image.SourceProperty);
            if (fallback is not null)
            {
                fallback.Visibility = OrganizerInteractionMath.ShouldShowItemFallback(
                    image.Source is not null,
                    iconLoadPending: false,
                    organizerPreviewVisible: false)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private async Task<BitmapImage?> GetIconThrottledAsync(string path, bool refresh = false, bool dockPresentation = false)
    {
        if (_closing) return null;
        BitmapImage? bitmap = null;
        bool entered = false;
        try
        {
            await _iconLoadGate.WaitAsync(_windowLifetime.Token);
            entered = true;
            if (_closing) return null;
            try
            {
                // IconCacheService keeps BitmapImage creation on the current
                // WinUI apartment; its pure Shell pixel fallback already
                // moves CPU-bound extraction to a worker.
                bitmap = dockPresentation
                    ? await _iconCache.GetDockIconAsync(path, refresh)
                    : await _iconCache.GetIconAsync(path, refresh);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"加载图标失败：{path}", ex);
            }
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (ObjectDisposedException) when (_closing) { }
        finally
        {
            if (entered)
            {
                try { _iconLoadGate.Release(); }
                catch (ObjectDisposedException) { }
            }
        }
        return bitmap;
    }


    private bool TryGetCompactAlignmentInsets(
        NativeMethods.RECT windowBounds,
        out WindowAlignmentInsets insets)
    {
        if (windowBounds.Width > 0 && windowBounds.Height > 0)
        {
            WindowAlignmentInsets deterministic = CalculateDeterministicCompactAlignmentInsets(windowBounds);
            if (deterministic.IsValidFor(windowBounds))
            {
                insets = deterministic;
                return true;
            }
        }
        if (TryGetCompactAlignmentFrame(out NativeMethods.RECT frame))
        {
            WindowAlignmentInsets measured = WindowAlignmentInsets.From(windowBounds, frame);
            if (measured.IsValidFor(windowBounds))
            {
                insets = measured;
                return true;
            }
        }
        insets = default;
        return false;
    }

    private bool TryGetCompactAlignmentFrame(out NativeMethods.RECT bounds)
    {
        if (TryGetLogicalWindowRect(out NativeMethods.RECT windowBounds) &&
            windowBounds.Width > 0 && windowBounds.Height > 0)
        {
            WindowAlignmentInsets insets = CalculateDeterministicCompactAlignmentInsets(windowBounds);
            if (insets.IsValidFor(windowBounds))
            {
                bounds = insets.ToFrame(windowBounds);
                return bounds.Width > 0 && bounds.Height > 0;
            }
        }
        return TryGetVisibleAlignmentFrame(CompactThumbnailHost, out bounds);
    }

    private bool TryGetVisibleAlignmentFrame(FrameworkElement surface, out NativeMethods.RECT bounds)
    {
        bounds = default;
        double width = surface.ActualWidth;
        double height = surface.ActualHeight;
        double scale = WindowRoot.XamlRoot?.RasterizationScale ?? 0;
        if (_hwnd == IntPtr.Zero || !surface.IsLoaded ||
            !double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(scale) ||
            width <= 0 || height <= 0 || scale <= 0) return false;

        Point origin;
        try
        {
            origin = surface.TransformToVisual(WindowRoot).TransformPoint(new Point());
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var clientOrigin = new NativeMethods.POINT();
        if (!NativeMethods.ClientToScreen(_hwnd, ref clientOrigin)) return false;
        bounds = new NativeMethods.RECT
        {
            Left = clientOrigin.X + (int)Math.Round(origin.X * scale),
            Top = clientOrigin.Y + (int)Math.Round(origin.Y * scale),
            Right = clientOrigin.X + (int)Math.Round((origin.X + width) * scale),
            Bottom = clientOrigin.Y + (int)Math.Round((origin.Y + height) * scale)
        };
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private void StartDragClock()
    {
        if (!_dragRenderingSubscribed)
        {
            CompositionTarget.Rendering += DragRendering;
            _dragRenderingSubscribed = true;
        }
        if (_nativeMouseCapture && !_dragClockBoosted && CompositorClockBoost.TryAcquire())
        {
            _dragClockBoosted = true;
        }
    }

    private void StopDragClock()
    {
        if (_dragRenderingSubscribed)
        {
            CompositionTarget.Rendering -= DragRendering;
            _dragRenderingSubscribed = false;
        }
        if (_dragClockBoosted)
        {
            CompositorClockBoost.Release();
            _dragClockBoosted = false;
        }
    }

    private async void ItemHost_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsCompactList && e.PointerDeviceType == PointerDeviceType.Mouse)
        {
            e.Handled = true; // Logical mouse sequence owns activation, including child organizers.
            return;
        }
        if (e.Handled || sender is not Border host) return;
        if (_shellDragActive || _shellDropFinalizing || _itemReorderSession is { IsActive: true }) return;
        if (UseHoverPresentation)
        {
            if (HoverItemAtViewportPoint(e.GetPosition(ItemsScrollView)) is not { } visualHost) return;
            host = visualHost;
        }
        else if (!IsCompactList && !IsInsideBaseItemContent(host, e.GetPosition(host))) return;
        e.Handled = true;
        await RunSafelyAsync(() => OpenTaggedItemAsync(host, doubleTap: true), "打开项目失败");
    }

    private Task OpenTaggedItemAsync(FrameworkElement host, bool doubleTap = false)
    {
        if (_shellDropFinalizing || host.Tag is not string relativeName || _draggedRelativeName is not null) return Task.CompletedTask;
        WidgetItem? item = _items.FirstOrDefault(candidate => candidate.RelativeName.Equals(relativeName, StringComparison.OrdinalIgnoreCase));
        if (item is null) return Task.CompletedTask;
        NativeMethods.RECT? anchor = item.Kind == WidgetItemKind.Organizer &&
            TryGetElementScreenBounds(host, out NativeMethods.RECT bounds) ? bounds : null;
        return OpenItemAsync(item, doubleTap, anchor);
    }

    private async Task OpenItemAsync(WidgetItem item, bool doubleTap, NativeMethods.RECT? organizerAnchor = null)
    {
        if (_shellDropFinalizing || _draggedRelativeName is not null) return;

        if (item is { Kind: WidgetItemKind.Note, NoteId: Guid noteId })
        {
            StartItemBounce(item);
            if (_host.IsPeekActive) _host.ExitPeekMode();
            _host.OpenNote(_definition.Id, noteId);
            return;
        }

        if (item.Kind == WidgetItemKind.PortableNote)
        {
            if (doubleTap && !IsCompactList) return;
            StartItemBounce(item);
            if (_host.IsPeekActive) _host.ExitPeekMode();
            await _host.OpenExternalNoteAsync(item.FullPath);
            return;
        }

        if (item.Kind == WidgetItemKind.PortableTodo)
        {
            if (doubleTap && !IsCompactList) return;
            StartItemBounce(item);
            if (_host.IsPeekActive) _host.ExitPeekMode();
            await _host.OpenExternalTodoAsync(item.FullPath);
            return;
        }

        if (item is { Kind: WidgetItemKind.Organizer, OrganizerId: Guid organizerId })
        {
            NativeMethods.RECT anchor = organizerAnchor ?? default;
            if (organizerAnchor is null &&
                NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
            {
                anchor = new NativeMethods.RECT
                {
                    Left = cursor.X,
                    Top = cursor.Y,
                    Right = cursor.X + 1,
                    Bottom = cursor.Y + 1
                };
            }
            StartItemBounce(item);
            await _host.OpenContainedOrganizerAsync(organizerId, anchor);
            return;
        }

        try
        {
            StartItemBounce(item);
            if (_host.IsPeekActive) _host.ExitPeekMode();
            Task launch = _host.ShellLauncher.OpenAsync(item.FullPath);
            if (await Task.WhenAny(launch, Task.Delay(TimeSpan.FromSeconds(5), _windowLifetime.Token)) != launch && !_closing)
                ShowMessage(AppStrings.Get("LaunchPending"), InfoBarSeverity.Informational);
            await launch.WaitAsync(_windowLifetime.Token);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            AppLogger.Error($"无法打开：{item.FullPath}", ex);
            if (!_closing) ShowMessage(AppStrings.Format("OpenItemErrorFormat", item.Name, ex.Message), InfoBarSeverity.Error);
        }
    }

    private void Item_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        _compactClicks.Reset();
        ResetHoverWave();
        e.Handled = true;
        if (!_expanded || _animating || _shellDragActive || _shellDropFinalizing ||
            _shellContextMenuOpen ||
            sender is not Border { Tag: string relativeName } host)
        {
            return;
        }

        WidgetItem? item = _items.FirstOrDefault(candidate =>
            candidate.RelativeName.Equals(relativeName, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        if (item.Kind == WidgetItemKind.Organizer) return;
        if (item is { Kind: WidgetItemKind.Note, NoteId: Guid noteId })
        {
            ShowNoteContextMenu(host, noteId);
            return;
        }
        if (TryShowBatchItemContextMenu(host, item)) return;
        ShowFileContextMenu(host, item);
    }

    private bool TryGetElementScreenBounds(FrameworkElement element, out NativeMethods.RECT bounds)
    {
        bounds = default;
        if (_hwnd == IntPtr.Zero || element.XamlRoot is null || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        Point origin;
        try
        {
            origin = element.TransformToVisual(WindowRoot).TransformPoint(new Point());
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        double scale = Math.Max(1, element.XamlRoot.RasterizationScale);
        var clientOrigin = new NativeMethods.POINT();
        if (!NativeMethods.ClientToScreen(_hwnd, ref clientOrigin)) return false;
        bounds = new NativeMethods.RECT
        {
            Left = clientOrigin.X + (int)Math.Round(origin.X * scale),
            Top = clientOrigin.Y + (int)Math.Round(origin.Y * scale),
            Right = clientOrigin.X + (int)Math.Round((origin.X + element.ActualWidth) * scale),
            Bottom = clientOrigin.Y + (int)Math.Round((origin.Y + element.ActualHeight) * scale)
        };
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private void ShowFileContextMenu(FrameworkElement host, WidgetItem item) =>
        ShowEnhancedFileContextMenu(host, item);

    private MenuFlyout CreateOrganizerContextMenu()
    {
        var flyout = new MenuFlyout
        {
            MenuFlyoutPresenterStyle = (Style)WindowRoot.Resources["OrganizerMenuPresenterStyle"],
            ShouldConstrainToRootBounds = !IsDock
        };
        flyout.Opening += ContextMenu_Opening;
        flyout.Opened += ContextMenu_Opened;
        flyout.Closed += ContextMenu_Closed;
        return flyout;
    }

    private void ShowNoteContextMenu(FrameworkElement host, Guid noteId)
    {
        var rename = new MenuFlyoutItem { Text = AppStrings.Get("ContextRenameNote") };
        var delete = new MenuFlyoutItem { Text = AppStrings.Get("ContextDeleteNote") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(rename, "RenameNoteMenuItem");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(delete, "DeleteNoteMenuItem");
        rename.Click += async (_, _) => await _catalogController.ShowRenameNoteDialogAsync(noteId);
        delete.Click += async (_, _) => await _catalogController.ShowDeleteNoteDialogAsync(noteId);
        MenuFlyout flyout = CreateOrganizerContextMenu();
        flyout.Items.Add(rename);
        flyout.Items.Add(delete);
        _shellContextMenuOpen = true;
        flyout.Closed += (_, _) =>
        {
            _shellContextMenuOpen = false;
        };
        try
        {
            flyout.ShowAt(host);
        }
        catch
        {
            _shellContextMenuOpen = false;
            RestoreContextMenuHost();
            throw;
        }
    }

    private void Item_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (IsCompactList) return; // The viewport owns logical row highlighting, including proxy/blank areas.
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse &&
            sender is Border host && !ReferenceEquals(host, _itemDragHost))
        {
            SetItemInteractionVisual(host, pressed: false);
        }
    }

    private void Item_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (IsCompactList) return;
        if (sender is Border host && !ReferenceEquals(host, _itemDragHost))
        {
            ResetItemInteractionVisual(host);
        }
    }


    private async void ExternalHoverTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        try
        {
            bool scrollToEnd = _hoverExpandScrollToEnd;
            _hoverExpandScrollToEnd = false;
            if (_closing || _expanded || _animating || _definition.PlacementMode == OrganizerPlacementMode.Station) return;
            if (!scrollToEnd && !ShouldStartIdleHoverExpand()) return;
            await ExpandAsync(scrollToEnd);
        }
        catch (Exception ex)
        {
            AppLogger.Error("悬停展开失败。", ex);
        }
    }

    private void WindowRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && ShouldStartIdleHoverExpand())
        {
            StartHoverExpand(scrollToEnd: false);
        }
    }

    private void WindowRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!CanActivateHover(e.GetCurrentPoint(ItemsScrollView).Position)) ReturnHoverWave();
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT cursor) &&
            TryGetLogicalWindowRect(out NativeMethods.RECT bounds))
        {
            if (DragBoundaryMath.Contains(bounds, cursor)) return;
            if (!_expanded && !_animating && !_changingExpansionMode) _modeHoverGuard.ObservePointer(insideEntry: false);
        }
        _externalHoverTimer.Stop();
        _hoverExpandScrollToEnd = false;
    }

    private void StartHoverExpand(bool scrollToEnd)
    {
        _hoverExpandScrollToEnd = scrollToEnd;
        _externalHoverTimer.Stop();
        _externalHoverTimer.Interval = TimeSpan.FromMilliseconds(_host.State.GlobalSettings.HoverExpandDelayMs);
        _externalHoverTimer.Start();
    }

    internal void RefreshHoverDelays()
    {
        bool restartExpandTimer = _externalHoverTimer.IsRunning;
        _externalHoverTimer.Stop();
        _externalHoverTimer.Interval = TimeSpan.FromMilliseconds(_host.State.GlobalSettings.HoverExpandDelayMs);
        _desktopCoordinator.ResetAllPointerDelays();
        if (restartExpandTimer) _externalHoverTimer.Start();
    }

    internal void RefreshPerformanceSettings()
    {
        PerformanceTuning tuning = _host.State.GlobalSettings.PerformanceTuning;
        bool visible = _definition.PlacementMode == OrganizerPlacementMode.Station
            ? _stationVisible && _stationReady
            : _runtimeVisible || IsContained && _expanded;
        bool interactionDragActive = CaptureInteractionState().IsDragGestureActive;
        bool pollPointer = !_closing && !StaysExpanded && !_changingExpansionMode && !interactionDragActive && OrganizerInteractionMath.ShouldPollPointer(
            _definition.PlacementMode,
            visible,
            IsContained,
            _expanded,
            _host.State.GlobalSettings.ExpandOnHover,
            _host.State.GlobalSettings.CollapseOnPointerLeave);
        bool repairDesktop = !_closing && !interactionDragActive && _desktopLayer is not null && !IsContained &&
            (_definition.PlacementMode == OrganizerPlacementMode.Station ? _stationVisible : _runtimeVisible);
        bool watchDesktop = repairDesktop &&
            (_definition.PlacementMode is OrganizerPlacementMode.Floating or OrganizerPlacementMode.Positioned or OrganizerPlacementMode.Dock);
        _desktopCoordinator.RefreshSchedule(tuning, pollPointer, repairDesktop, watchDesktop);

        TimeSpan iconTransitionDuration = UseCustomAnimations ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero;
        if (CollapseDash.ScaleTransition is not null) CollapseDash.ScaleTransition.Duration = iconTransitionDuration;
        RefreshHoverAvailability();
        if (!UseCustomAnimations)
        {
            CollapseDash.Scale = Vector3.One;
        }
    }

    private bool UseCustomAnimations =>
        _host.State.GlobalSettings.ShouldUseCustomAnimations(_uiSettings.AnimationsEnabled);

    private bool ShouldStartIdleHoverExpand() => ShouldStartIdleHoverExpand(CaptureInteractionState());

    internal bool ShouldStartIdleHoverExpand(OrganizerInteractionSnapshot interaction)
    {
        if (_changingExpansionMode || StaysExpanded || !_runtimeVisible || !_modeHoverGuard.CanHover) return false;
        bool interactionActive = interaction.BlocksIdleHoverExpand ||
            (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        return OrganizerInteractionMath.ShouldStartHoverExpand(
            _host.State.GlobalSettings.ExpandOnHover,
            _definition.PlacementMode == OrganizerPlacementMode.Station,
            _expanded,
            _animating,
            interactionActive);
    }

    private void ResetStationPointerDelay() => _desktopCoordinator.ResetStationPointerDelay();

    private bool IsPointerOverThisWindow(NativeMethods.POINT pointer)
    {
        IntPtr hit = NativeMethods.WindowFromPoint(pointer);
        if (hit == _hwnd || NativeMethods.IsChild(_hwnd, hit)) return true;
        return _canvasResizeEdgeWindows.Any(edge => hit == edge || NativeMethods.IsChild(edge, hit));
    }

    private void ContextMenu_Opening(object? sender, object e)
    {
        if (!_contextMenuCounted)
        {
            _contextMenuCounted = true;
            _overlayOpenCount++;
        }
        ResetHoverWave();
        UpdateExpansionModeControls();
        UpdateContentModeMenuItems();
        if (_contextMenuActivated) return;
        _contextMenuActivated = true;
        _desktopLayer?.SetInputActivation(true);
        ResetStationPointerDelay();
    }

    private void ContextMenu_Opened(object? sender, object e)
    {
        if (ReferenceEquals(sender, OrganizerContextMenu))
        {
            _ = NativeMethods.GetCursorPos(out _contextMenuScreenPoint);
            try
            {
                DataPackageView data = Clipboard.GetContent();
                PasteMenuItem.IsEnabled = data.Contains(StandardDataFormats.StorageItems) ||
                    ShellDragService.TryGetClipboardPaths(out _, out _) ||
                    data.Contains(StandardDataFormats.Bitmap) ||
                    data.Contains(StandardDataFormats.Text);
            }
            catch
            {
                PasteMenuItem.IsEnabled = false;
            }
        }
        if (!_contextMenuCounted)
        {
            _contextMenuCounted = true;
            _overlayOpenCount++;
        }
    }

    private void ContextMenu_Closed(object? sender, object e)
    {
        RestoreContextMenuHost();
    }

    private void RestoreContextMenuHost()
    {
        if (_contextMenuCounted)
        {
            _contextMenuCounted = false;
            _overlayOpenCount = Math.Max(0, _overlayOpenCount - 1);
        }
        if (_contextMenuActivated)
        {
            _contextMenuActivated = false;
            // Closed can arrive inside the file picker's nested modal message loop.
            // The add-item operation owns activation until its finally block.
            _desktopLayer?.SetInputActivation(_catalogController.IsAddingItem);
        }
        ResetStationPointerDelay();
    }

    private static bool HasLocalFileDrop(DataPackageView dataView) =>
        dataView.Contains(StandardDataFormats.StorageItems) || ShellDragService.HasFileDrop(dataView);

    private async void WindowRoot_KeyDown(object sender, KeyRoutedEventArgs e) =>
        await HandleOrganizerKeyDownAsync(e);

    private void ItemsScrollView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_expanded) return;
        ConfigureItemsLayout(updateItems: _canvasResize is null);
        ReconcileSmoothScrollBounds();
    }

    private void ScrollToEnd(bool animated)
    {
        double max = Math.Max(0, ItemsScrollView.ScrollableHeight);
        if (IsCompactList)
            _compactListScrollState = new(max, max, 0, 0);
        else
            _gridScrollState = new(max, max, 0, 0);
        if (animated && UseCustomAnimations)
        {
            if (IsCompactList)
                _compactListScrollState = _compactListScrollState with { CurrentOffset = ItemsScrollView.VerticalOffset };
            else
                _gridScrollState = _gridScrollState with { CurrentOffset = ItemsScrollView.VerticalOffset };
            RequestSmoothScroll();
        }
        else
        {
            ItemsScrollView.ScrollTo(0, max, InstantScrollOptions);
        }
    }

    private void ReconcileSmoothScrollBounds()
    {
        double max = Math.Max(0, ItemsScrollView.ScrollableHeight);
        _gridScrollState = OrganizerScrollMath.ClampState(_gridScrollState, max);
        _compactListScrollState = OrganizerScrollMath.ClampState(_compactListScrollState, max);
        if (max <= 0 || !_expanded)
        {
            StopSmoothScrollRendering();
            return;
        }
        SmoothScrollState state = IsCompactList ? _compactListScrollState : _gridScrollState;
        if (Math.Abs(state.CurrentOffset - state.TargetOffset) < .01)
            StopSmoothScrollRendering();
    }

    private void RequestSmoothScroll()
    {
        double max = Math.Max(0, ItemsScrollView.ScrollableHeight);
        if (max <= 0) return;
        ReconcileSmoothScrollBounds();
        if (!UseCustomAnimations)
        {
            if (IsCompactList)
                _compactListScrollState = _compactListScrollState with { CurrentOffset = _compactListScrollState.TargetOffset, Velocity = 0 };
            else
                _gridScrollState = _gridScrollState with { CurrentOffset = _gridScrollState.TargetOffset, Velocity = 0 };
            double offset = IsCompactList ? _compactListScrollState.CurrentOffset : _gridScrollState.CurrentOffset;
            ItemsScrollView.ScrollTo(0, offset, InstantScrollOptions);
            return;
        }
        _smoothScrollLastFrame = Stopwatch.GetTimestamp();
        if (!_smoothScrollRenderingSubscribed)
        {
            CompositionTarget.Rendering += SmoothScrollRendering;
            _smoothScrollRenderingSubscribed = true;
            _smoothScrollClockBoost ??= CompositorClockBoost.Acquire();
        }
    }

    private void SyncSmoothScrollIdleOffset()
    {
        if (_smoothScrollRenderingSubscribed) return;
        double max = Math.Max(0, ItemsScrollView.ScrollableHeight);
        double offset = Math.Clamp(
            double.IsFinite(ItemsScrollView.VerticalOffset) ? ItemsScrollView.VerticalOffset : 0,
            0,
            max);
        if (IsCompactList)
            _compactListScrollState = _compactListScrollState with { CurrentOffset = offset, TargetOffset = offset };
        else
            _gridScrollState = _gridScrollState with { CurrentOffset = offset, TargetOffset = offset };
    }

    private void SmoothScrollRendering(object? sender, object args)
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = Math.Clamp(
            Stopwatch.GetElapsedTime(_smoothScrollLastFrame, now).TotalSeconds,
            1d / 480,
            1d / 20);
        _smoothScrollLastFrame = now;
        double max = Math.Max(0, ItemsScrollView.ScrollableHeight);
        if (max <= 0 || !_expanded)
        {
            StopSmoothScrollRendering();
            return;
        }
        bool reduceMotion = !UseCustomAnimations;
        if (IsCompactList)
            _compactListScrollState = OrganizerScrollMath.Step(_compactListScrollState, seconds, max, reduceMotion);
        else
            _gridScrollState = OrganizerScrollMath.Step(_gridScrollState, seconds, max, reduceMotion);
        SmoothScrollState state = IsCompactList ? _compactListScrollState : _gridScrollState;
        ItemsScrollView.ScrollTo(0, state.CurrentOffset, InstantScrollOptions);
        if (Math.Abs(state.CurrentOffset - state.TargetOffset) < .01 && Math.Abs(state.Velocity) < .01)
            StopSmoothScrollRendering();
    }

    private void StopSmoothScrollRendering()
    {
        if (_smoothScrollRenderingSubscribed)
        {
            CompositionTarget.Rendering -= SmoothScrollRendering;
            _smoothScrollRenderingSubscribed = false;
        }
        _smoothScrollClockBoost?.Dispose();
        _smoothScrollClockBoost = null;
        _smoothScrollLastFrame = 0;
    }

    private async void RenameMenuItem_Click(object sender, RoutedEventArgs e) =>
        await _catalogController.ShowRenameOrganizerDialogAsync();

    private async void DuplicateWindowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _host.DuplicateOrganizerAsync(_definition.Id);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法复制收纳窗。", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void PasteMenuItem_Click(object sender, RoutedEventArgs e) =>
        await _catalogController.PasteClipboardAsync(_contextMenuScreenPoint);

    private async void NewNoteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            NativeMethods.POINT anchor = _contextMenuScreenPoint;
            if (anchor.X == 0 && anchor.Y == 0 && TryGetLogicalWindowRect(out NativeMethods.RECT bounds))
            {
                anchor = new NativeMethods.POINT
                {
                    X = bounds.Left + bounds.Width / 2,
                    Y = bounds.Top + bounds.Height / 2
                };
            }
            await _host.CreateNoteAsync(_definition.Id, null, anchor);
            await WaitForNextRenderAsync(CancellationToken.None);
            ScrollToEnd(animated: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法创建便签。", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void NewFolderMenuItem_Click(object sender, RoutedEventArgs e) =>
        await _catalogController.CreateNewFolderWithDialogAsync();

    private async void ToggleModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? error = await _host.ToggleOrganizerModeAsync(_definition.Id);
            if (error is not null) ShowMessage(error, InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法切换收纳窗模式。", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OpenStorageMenuItem_Click(object sender, RoutedEventArgs e) =>
        _catalogController.OpenStorageDirectory();

    private async void DeleteWindowMenuItem_Click(object sender, RoutedEventArgs e) =>
        await _catalogController.ShowDeleteOrganizerDialogAsync();

    private DisplayInfo GetRenameDialogDisplay() => RenameDialogPlacement.Resolve(
        TryGetLogicalWindowRect(out NativeMethods.RECT bounds) ? bounds : null,
        IsDock ? _definition.DockCenter?.MonitorDevice : _definition.Position?.MonitorDevice,
        DisplayPlacementService.ForBounds, DisplayPlacementService.GetDisplay);

    private void ManageMenuItem_Click(object sender, RoutedEventArgs e) => _host.OpenConsole(_definition.Id);

    private bool DesktopLayerBusy => CaptureInteractionState().BlocksDesktopLayerRepair;

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _host.EndOrganizerDragHover(this);
        Activated -= MainWindow_Activated;
        Activated -= OrganizerTitleWindow_Activated;
        CompactThumbnailHost.SizeChanged -= SurfaceHost_SizeChanged;
        ExpandedPanel.SizeChanged -= SurfaceHost_SizeChanged;
        WindowRoot.SizeChanged -= SurfaceHost_SizeChanged;
        WindowRoot.Loaded -= WindowRoot_Loaded;
        if (_subscribedXamlRoot is not null)
        {
            _subscribedXamlRoot.Changed -= XamlRoot_Changed;
            _subscribedXamlRoot = null;
        }
        ExpandedView.RemoveHandler(UIElement.PointerPressedEvent, _expandedPointerPressedHandler);
        ExpandedView.RemoveHandler(UIElement.PointerMovedEvent, _expandedPointerMovedHandler);
        ExpandedView.RemoveHandler(UIElement.PointerReleasedEvent, _expandedPointerReleasedHandler);
        ExpandedView.RemoveHandler(UIElement.PointerCanceledEvent, _expandedPointerCanceledHandler);
        ExpandedView.RemoveHandler(UIElement.PointerCaptureLostEvent, _expandedPointerCaptureLostHandler);
        ExpandedView.RemoveHandler(UIElement.PointerWheelChangedEvent, _expandedPointerWheelChangedHandler);
        WindowRoot.PointerEntered -= WindowRoot_PointerEntered;
        WindowRoot.PointerExited -= WindowRoot_PointerExited;
        _compactClicks.Reset();
        EndItemMarquee();
        DisposeHoverWave();
        DisposeDockRunningVisuals();
        try { _compactBackdrop.DetachAndClose(); }
        catch (Exception ex) { AppLogger.Error("Compact organizer backdrop close failed.", ex); }
        try { _expandedBackdrop.DetachAndClose(); }
        catch (Exception ex) { AppLogger.Error("Expanded organizer backdrop close failed.", ex); }
        _animationTrace?.Finish("window-closed");
        _closing = true;
        _host.RefreshDockRunningSubscription();
        ++_transitionRequestVersion;
        _host.CloseExpansionModeCoordinator(OrganizerId);
        ClearWindowAlignment();
        _windowLifetime.Cancel();
        _widgetGesture.Invalidate();
        _resizeGesture.Invalidate();
        _pressActive = false;
        _nativeMouseCapture = false;
        _canvasResize = null;
        _canvasResizeClockBoost?.Dispose();
        _canvasResizeClockBoost = null;
        _animating = false;
        _host.NotifyDockOpenStateChanged();
        _stationReady = false;
        _host.ThemeChanged -= Host_ThemeChanged;
        if (_itemReorderSession is not null) CancelItemReorder(runPendingRefresh: false);
        ShutdownItemDragBoundaryHook();
        StopNativeItemMotionRendering(snapToTargets: true);
        if (_itemDragClockBoosted)
        {
            CompositorClockBoost.Release();
            _itemDragClockBoosted = false;
        }
        _desktopCoordinator.Stop();
        _catalogController.Stop();
        _externalDragCoordinator.CancelShellPreparation();
        _resourceScope.DisposeAll(ex => AppLogger.Error("窗口附加资源释放失败。", ex));
        ReleaseStartupCloak();
        _interactionSaveTimer.Stop();
        _interactionSaveTimer.Tick -= InteractionSaveTimer_Tick;
        _canvasResizeInputTimer.Stop();
        _canvasResizeInputTimer.Tick -= CanvasResizeInputTimer_Tick;
        StopSmoothScrollRendering();
        _externalHoverTimer.Stop();
        _externalHoverTimer.Tick -= ExternalHoverTimer_Tick;
        _longPressTimer.Stop();
        _longPressTimer.Tick -= LongPressTimer_Tick;
        _itemTouchHoldTimer.Stop();
        _itemTouchHoldTimer.Tick -= ItemTouchHoldTimer_Tick;
        StopCanvasResizeRendering();
        StopDragClock();
        CancellationTokenSource? closingTransition = _transitionCancellation;
        _transitionCancellation = null;
        closingTransition?.Cancel();
        closingTransition?.Dispose();
        _compactClip?.Dispose();
        _compactClip = null;
        _expandedClip?.Dispose();
        _expandedClip = null;
        _stationTransitionClip?.Dispose();
        _stationTransitionClip = null;
        _compactSurface.Dispose();
        _expandedSurface.Dispose();
        _compactEdgeSurface.Dispose();
        _expandedEdgeSurface.Dispose();
        DisposeCompactVisualResources();
        DisposeAllItemNameShadows();
        _iconLoadGate.Dispose();
        _itemDragBoundaryHookReady.Dispose();
        _windowAlignmentGuide?.Dispose();
        _windowAlignmentGuide = null;
        _outsideClickHook?.Dispose();
        foreach (IntPtr edgeWindow in _canvasResizeEdgeWindows)
        {
            RestoreCanvasResizeWindowProc(edgeWindow);
            if (NativeMethods.IsWindow(edgeWindow)) _ = NativeMethods.DestroyWindow(edgeWindow);
        }
        _canvasResizeEdgeWindows.Clear();
        foreach (IntPtr window in _canvasResizeOriginalWindowProcs.Keys.ToArray())
            RestoreCanvasResizeWindowProc(window);
        if (_hwnd != IntPtr.Zero)
        {
            _ = NativeMethods.RemoveWindowSubclass(_hwnd, _gestureWindowProc, GestureSubclassId);
        }
        _desktopLayer?.Dispose();
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        if (severity is InfoBarSeverity.Warning or InfoBarSeverity.Error)
            _host.ReportOperationError("TuckPane", message, _hwnd, _definition.Position?.MonitorDevice);
        else
            _host.LogStatus("TuckPane", message);
    }

    private async Task SaveStateAsync()
    {
        try
        {
            await _host.SaveStateAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("保存配置失败。", ex);
            if (!_closing) ShowMessage(AppStrings.Get("SaveConfigurationError"), InfoBarSeverity.Warning);
        }
    }

    internal Guid OrganizerId => _definition.Id;
    internal Guid? ContainerOrganizerId => _definition.ContainerOrganizerId;
    internal OrganizerPlacementMode DefinitionPlacementMode => _definition.PlacementMode;
    internal bool IsAnimating => _animating;
    private bool IsContained => _definition.ContainerOrganizerId is not null;
    internal bool IsExpanded => _expanded || _animating;
    internal bool IsShellDragActive => Volatile.Read(ref _shellDragActive);
    internal void RefreshOrganizerName()
    {
        UpdateOrganizerName();
    }

    internal void RefreshContainedOrganizerName(Guid organizerId, string name)
    {
        WidgetItem? item = _items.FirstOrDefault(candidate => candidate.OrganizerId == organizerId);
        if (item is null) return;
        item.ApplyValue(new WidgetItem(name, item.FullPath, item.RelativeName, item.Kind, item.NoteId, item.OrganizerId));
        RefreshOrganizerPreview(organizerId);
    }
    private void HandleOutsideClick(NativeMethods.POINT clickPoint)
    {
        if (_host.ContainsExpandedContainedChildPoint(_definition.Id, clickPoint)) return;
        _ = RunSafelyAsync(CollapseAsync, "外部点击收起失败");
    }

    private async Task RunSafelyAsync(Func<Task> action, string context)
    {
        try
        {
            if (_closing) return;
            await action();
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            AppLogger.Error(context, ex);
            if (!_closing) ShowMessage("操作失败，请稍后重试。", InfoBarSeverity.Warning);
        }
    }

    internal void ApplyOutsideClickSetting()
    {
        if (_definition.PlacementMode != OrganizerPlacementMode.Station &&
            !StaysExpanded && !_changingExpansionMode &&
            _expanded && !_animating && _host.State.GlobalSettings.CollapseOnOutsideClick)
            _outsideClickHook?.Start();
        else
            _outsideClickHook?.Stop();
    }
    internal int FileCount => _items.Count(item => item.Kind is not (WidgetItemKind.Note or WidgetItemKind.Organizer));
    internal Task RefreshNotesAsync() => RefreshCatalogAsync(notifyUnsupported: false);
    internal Task RefreshContainedOrganizerItemsAsync() => RefreshCatalogAsync(notifyUnsupported: false);
    internal IReadOnlyList<WidgetItem> ItemSnapshot => _items;
    internal bool StorageExists => _storage.Exists;
    internal NativeMethods.RECT CompactBounds => _compactBounds;
    internal double AppliedCompactScale => _appliedCompactScale;

    internal bool TryGetCompactDropBounds(out NativeMethods.RECT bounds)
    {
        bounds = default;
        if (_closing || _appWindow is not { IsVisible: true } ||
            _animating || _definition.PlacementMode == OrganizerPlacementMode.Station)
            return false;
        if (IsContained)
        {
            return TryGetLogicalWindowRect(out bounds) && bounds.Width > 0 && bounds.Height > 0;
        }
        if (_expanded) return false;
        return TryGetCompactAlignmentFrame(out bounds);
    }

    internal Task ExpandForOrganizerDragAsync() =>
        _closing || _definition.PlacementMode == OrganizerPlacementMode.Station
            ? Task.CompletedTask
            : ExpandAsync();

    internal bool TryGetWindowAlignmentFrame(out NativeMethods.RECT bounds)
    {
        bounds = default;
        if (_closing || _appWindow is not { IsVisible: true } ||
            IsContained || _animating || !OrganizerInteractionMath.ShouldUseWindowAlignment(
                true, _expanded, _definition.PlacementMode, false, IsPermanentlyExpanded)) return false;
        return _expanded
            ? TryGetVisibleAlignmentFrame(ExpandedPanel, out bounds)
            : TryGetCompactAlignmentFrame(out bounds);
    }

    internal void RefreshWindowAlignmentSetting()
    {
        if (!_host.State.GlobalSettings.WindowAlignmentEnabled) ClearWindowAlignment();
    }

    internal bool ContainsScreenPoint(NativeMethods.POINT point) =>
        !_closing && _expanded && TryGetLogicalWindowRect(out NativeMethods.RECT bounds) &&
        (IsDock ? DockContainsScreenPoint(point) : DragBoundaryMath.Contains(bounds, point) ||
            HasHoverPresentation && TryScreenToHoverViewport(point, out Point hoverPoint) && HoverItemAtViewportPoint(hoverPoint) is not null);

    internal bool TryGetOrganizerDropIndex(NativeMethods.POINT screenPoint, out int insertionIndex)
    {
        insertionIndex = 0;
        if (_animating || !ContainsScreenPoint(screenPoint)) return false;
        NativeMethods.POINT clientPoint = screenPoint;
        if (!NativeMethods.ScreenToClient(_hwnd, ref clientPoint)) return false;
        double scale = Math.Max(1, WindowRoot.XamlRoot?.RasterizationScale ?? 1);
        if (IsDock)
        {
            Point dockOrigin = ExpandedView.TransformToVisual(WindowRoot).TransformPoint(new Point());
            insertionIndex = _dockGeometry.InsertionIndex(new Vector2((float)(clientPoint.X / scale - dockOrigin.X), (float)(clientPoint.Y / scale - dockOrigin.Y)));
            return true;
        }
        Point scrollOrigin;
        Point origin;
        try
        {
            scrollOrigin = ItemsScrollView.TransformToVisual(WindowRoot).TransformPoint(new Point());
            origin = ItemsRepeater.TransformToVisual(WindowRoot).TransformPoint(new Point());
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        double clientXDip = clientPoint.X / scale;
        double clientYDip = clientPoint.Y / scale;
        if (clientXDip < scrollOrigin.X || clientYDip < scrollOrigin.Y ||
            clientXDip > scrollOrigin.X + ItemsScrollView.ActualWidth ||
            clientYDip > scrollOrigin.Y + ItemsScrollView.ActualHeight) return false;
        double x = clientXDip - origin.X;
        double y = clientYDip - origin.Y;
        Size viewport = GetItemsViewportSize();
        (double width, double height) = GetItemCellSizeDip(viewport.Width, viewport.Height);
        double gap = GetItemLayoutGapDip();
        if (_definition.ExpandedContentMode == OrganizerExpandedContentMode.CompactList)
        {
            int compactRow = Math.Max(0, (int)Math.Floor(Math.Max(0, y) / Math.Max(1, height + gap)));
            int compactCandidate = compactRow;
            if (y - compactRow * (height + gap) > height / 2) compactCandidate++;
            insertionIndex = Math.Clamp(compactCandidate, 0, _items.Count);
            return true;
        }

        int columns = GetItemLayoutColumnCount();
        int column = Math.Clamp((int)Math.Floor(Math.Max(0, x) / Math.Max(1, width + gap)), 0, columns - 1);
        int row = Math.Max(0, (int)Math.Floor(Math.Max(0, y) / Math.Max(1, height + gap)));
        int candidate = row * columns + column;
        if (x - column * (width + gap) > width / 2) candidate++;
        insertionIndex = Math.Clamp(candidate, 0, _items.Count);
        return true;
    }

    internal void RefreshOrganizerPreview(Guid organizerId)
    {
        RenderCompactPreview();
        string key = OrganizerContainment.ItemKey(organizerId);
        if (TryGetRealizedItemHost(key, out Border host) &&
            _items.FirstOrDefault(item => item.RelativeName.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } item)
        {
            PrepareItemElement(host, item, loadIcon: true);
        }
    }

    private async void NewTodoMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            NativeMethods.POINT anchor = _contextMenuScreenPoint;
            if (anchor.X == 0 && anchor.Y == 0 && TryGetLogicalWindowRect(out NativeMethods.RECT bounds))
            {
                anchor = new NativeMethods.POINT
                {
                    X = bounds.Left + bounds.Width / 2,
                    Y = bounds.Top + bounds.Height / 2
                };
            }
            await _host.CreateTodoAsync(_definition.Id, anchor);
            await WaitForNextRenderAsync(CancellationToken.None);
            ScrollToEnd(animated: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法创建待办。", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void ToggleContentModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? error = await _host.ToggleOrganizerExpandedContentModeAsync(_definition.Id);
            if (error is not null) ShowMessage(error, InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            AppLogger.Error("无法切换收纳窗内容模式。", ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private static bool IsDocumentItem(WidgetItem item) =>
        item.Kind is WidgetItemKind.Note or WidgetItemKind.PortableNote or WidgetItemKind.PortableTodo;

    internal static bool IsDocumentWidgetItem(WidgetItem item) => IsDocumentItem(item);

    private BitmapImage GetDocumentIcon(WidgetItem item) =>
        item.Kind == WidgetItemKind.PortableTodo ? _todoIcon : _noteIcon;

    internal Task<bool> ImportFromPeerAsync(string path) => _catalogController.ImportFromPeerAsync(path);

    internal Task CollapseForPeerAsync() => CollapseAsync();

    internal void SetVisible(bool visible)
    {
        if (!visible) _compactClicks.Reset();
        if (IsDock && _appWindow is not null)
        {
            _runtimeVisible = visible;
            if (visible) { _desktopLayer?.BringAboveDesktopPeers(); ApplyDockLayout(); }
            else { ResetHoverWave(); _appWindow.Hide(); }
            RefreshPerformanceSettings();
            return;
        }
        ResetHoverWave();
        if (_appWindow is null) return;
        if (!visible)
        {
            StopCanvasResizeForHide();
            _canvasResizeInputTimer.Stop();
            _canvasResizeClockBoost?.Dispose();
            _canvasResizeClockBoost = null;
        }
        if (IsContained)
        {
            if (visible) _appWindow.Hide();
            else SetContained(true);
            RefreshPerformanceSettings();
            return;
        }
        if (_definition.PlacementMode == OrganizerPlacementMode.Station)
        {
            _stationVisible = visible;
            if (visible && _expanded)
            {
                _desktopLayer?.SetExpanded(true, stayTopmost: true);
                ApplyBounds(CalculateExpandedBounds(_compactBounds), show: true);
            }
            else _appWindow.Hide();
            RefreshPerformanceSettings();
            return;
        }
        _runtimeVisible = visible;
        if (!visible)
        {
            ++_modeVisibilityVersion;
            _pendingExpansionModeApply |= _animating || _changingExpansionMode;
            ++_transitionRequestVersion;
            _transitionCancellation?.Cancel();
            _animating = false;
            _host.NotifyDockOpenStateChanged();
        }
        if (visible)
        {
            _desktopLayer?.BringAboveDesktopPeers();
            if (_pendingExpansionModeApply)
            {
                _ = RunSafelyAsync(ApplyExpansionModeAsync, "恢复窗口展开模式失败");
                return;
            }
            if (IsPermanentlyExpanded && _expanded && TryGetLogicalWindowRect(out NativeMethods.RECT current))
                ApplyBounds(current, show: true);
            else ApplyBounds(_expanded ? CalculateExpandedBounds(_compactBounds) : _compactBounds, show: true);
            if (IsPermanentlyExpanded && !_expanded) _ = RunSafelyAsync(ExpandAsyncForMode, "恢复展开模式失败");
        }
        else
        {
            _appWindow.Hide();
        }
        RefreshPerformanceSettings();
    }

    internal Task ExpandContainedAsync(NativeMethods.RECT anchor)
    {
        if (!IsContained || anchor.Width <= 0 || anchor.Height <= 0) return Task.CompletedTask;
        _containedAnchorBounds = anchor;
        return ExpandAsync();
    }

    internal void MoveContainedDragPreview(NativeMethods.POINT cursor, MainWindow container)
    {
        ResetHoverWave();
        if (!IsContained || _appWindow is null) return;
        if (_externalDragCoordinator.IsSameContainedDragPreview(container._hwnd, cursor)) return;
        DisplayInfo display = DisplayPlacementService.ForBounds(new NativeMethods.RECT
        {
            Left = cursor.X,
            Top = cursor.Y,
            Right = cursor.X + 1,
            Bottom = cursor.Y + 1
        });
        int width = Math.Max(1, _compactBounds.Width);
        int height = Math.Max(1, _compactBounds.Height);
        var desired = new NativeMethods.RECT
        {
            Left = cursor.X - width / 2,
            Top = cursor.Y - height / 2,
            Right = cursor.X - width / 2 + width,
            Bottom = cursor.Y - height / 2 + height
        };
        NativeMethods.RECT bounds = DisplayPlacementService.CalculateDraggedBounds(desired, cursor, cursor, display.Work);
        _externalDragCoordinator.RecordContainedDragPreview(container._hwnd, cursor, bounds);
        ExpandedView.Visibility = Visibility.Collapsed;
        CompactView.Visibility = Visibility.Visible;
        UpdateSurfaceClips();
        _desktopLayer?.SetExpanded(true, stayTopmost: true);
        _desktopLayer?.SetTransientOwner(container._hwnd);
        ApplyBounds(bounds, show: true);
    }

    internal void SetContained(bool contained)
    {
        if (_appWindow is null) return;
        if (contained)
        {
            ++_transitionRequestVersion;
            _transitionCancellation?.Cancel();
            _expanded = false;
            _animating = false;
            _host.NotifyDockOpenStateChanged();
            CompactView.Visibility = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Collapsed;
            _desktopLayer?.SetExpanded(false, showWindow: false);
            _appWindow.Hide();
            RefreshPerformanceSettings();
            _host.NotifyCollapsed(this);
            return;
        }
        _runtimeVisible = true;
        ShowCompactPlacement(_compactBounds);
    }

    internal void ClosePermanently()
    {
        _host.EndOrganizerDragHover(this);
        _compactClicks.Reset();
        try { _compactBackdrop.DetachAndClose(); }
        catch (Exception ex) { AppLogger.Error("Compact organizer backdrop close failed.", ex); }
        try { _expandedBackdrop.DetachAndClose(); }
        catch (Exception ex) { AppLogger.Error("Expanded organizer backdrop close failed.", ex); }
        _closing = true;
        ++_transitionRequestVersion;
        _host.CloseExpansionModeCoordinator(OrganizerId);
        Close();
    }

    internal void ApplyDefinition(OrganizerVisualChange changes)
    {
        _compactClicks.Reset();
        if (IsDock)
        {
            ResetHoverWave();
            UpdateOrganizerName();
            ApplyDockLayout();
            ApplyTheme();
            return;
        }
        ResetHoverWave();
        ApplyExpandedContentInset();
        if (_itemReorderSession is not null) CancelItemReorder();
        if ((changes & (OrganizerVisualChange.ExpandedContentMode | OrganizerVisualChange.PlacementMode)) != 0)
        {
            _gridScrollState = _gridScrollState with { Remainder = 0 };
            _compactListScrollState = _compactListScrollState with { Remainder = 0 };
        }
        if ((changes & (OrganizerVisualChange.PlacementMode | OrganizerVisualChange.ExpandedContentMode)) != 0)
        {
            ApplyExpandedContentInset();
            if ((changes & OrganizerVisualChange.PlacementMode) != 0) ApplyLanguage();
        }
        if ((changes & (OrganizerVisualChange.Name | OrganizerVisualChange.NameScale)) != 0) UpdateOrganizerName();
        if ((changes & (OrganizerVisualChange.CompactScale | OrganizerVisualChange.NameScale | OrganizerVisualChange.PlacementMode)) != 0)
        {
            ApplyCompactScale(repositionWindow: !_expanded && !_animating);
        }

        if ((changes & OrganizerVisualChange.ItemScale) != 0) UpdateCompactPreviewItemScale();
        bool changesExpandedGeometry = (changes & (OrganizerVisualChange.Layout | OrganizerVisualChange.CanvasScale | OrganizerVisualChange.ExpandedContentMode)) != 0 ||
            _definition.PlacementMode == OrganizerPlacementMode.Station && (changes & OrganizerVisualChange.ItemScale) != 0;
        if ((_expanded || _animating) && changesExpandedGeometry)
        {
            _transitionCancellation?.Cancel();
            _transitionCancellation?.Dispose();
            _transitionCancellation = null;
            if (!TryGetLogicalWindowRect(out NativeMethods.RECT current)) current = CalculateExpandedBounds(_compactBounds);
            NativeMethods.RECT target = _definition.PlacementMode == OrganizerPlacementMode.Station
                ? CalculateExpandedBounds(_compactBounds)
                : CalculateExpandedBoundsAroundCenter(current);
            _expanded = true;
            _animating = false;
            _host.NotifyDockOpenStateChanged();
            _transitionProgress = 1;
            _transitionVelocity = 0;
            ClearStationTransitionVisuals();
            ApplyBounds(target, show: true);
            CompactView.Visibility = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Visible;
            ExpandedView.Opacity = 1;
            ResetExpandedTransitionOpacities();
            GetExpandedCompositionVisual().Scale = Vector3.One;
            ConfigureItemsLayout();
            UpdateSurfaceClips();
        }
        else if (_expanded && (changes & (OrganizerVisualChange.ItemScale | OrganizerVisualChange.CompactListItemScale)) != 0)
        {
            ClearStationTransitionVisuals();
            ConfigureItemsLayout();
        }
        if ((changes & OrganizerVisualChange.PlacementMode) != 0) RefreshPerformanceSettings();
        if (IsPermanentlyExpanded && _expanded && changesExpandedGeometry) CaptureExpandedPosition();
    }

    internal void RecreateStorage()
    {
        _storage.EnsureCreated();
        StartWatcher();
        _ = RefreshCatalogAsync(notifyUnsupported: false, refreshIcons: true);
    }

    private void ApplyTheme()
    {
        _animationTrace?.Metrics.RecordWork(AnimationTraceWork.ThemeApplication);
        ThemeValues theme = _host.State.GlobalSettings.GetTheme(OrganizerKinds.ThemeFor(_definition.PlacementMode));
        WindowRoot.RequestedTheme = ThemePalette.IsDark(theme) ? ElementTheme.Dark : ElementTheme.Light;
        bool useEffects = _uiSettings.AdvancedEffectsEnabled;
        _compactBackdrop.SetTheme(theme, useEffects);
        _expandedBackdrop.SetTheme(theme, useEffects);
        _compactSurface.SetTheme(theme, useEffects);
        _expandedSurface.SetTheme(theme, useEffects);
        _compactEdgeSurface.SetTheme(theme, useEffects);
        _expandedEdgeSurface.SetTheme(theme, useEffects);
        _compactEdgeSurface.SetEnabled(_host.State.GlobalSettings.EdgeGlowEnabled);
        _expandedEdgeSurface.SetEnabled(_host.State.GlobalSettings.EdgeGlowEnabled);
        var foregroundColor = ThemePalette.ForegroundColor(theme);
        _dockEmptyAddBrush.Color = foregroundColor;
        DockEmptyAddIcon.Foreground = _dockEmptyAddBrush;
        _organizerTextBrush.Color = ThemePalette.ResolveOrganizerTextColor(
            _host.State.GlobalSettings.OrganizerTextColor,
            theme);
        CompactNameText.Foreground = _organizerTextBrush;
        ExpandedNameText.Foreground = _organizerTextBrush;
        _hoveredItemBrush.Color = ColorHelper.FromArgb(22, foregroundColor.R, foregroundColor.G, foregroundColor.B);
        _pressedItemBrush.Color = ColorHelper.FromArgb(38, foregroundColor.R, foregroundColor.G, foregroundColor.B);
        _collapseHoverBrush.Color = ColorHelper.FromArgb(26, foregroundColor.R, foregroundColor.G, foregroundColor.B);
        _collapsePressedBrush.Color = ColorHelper.FromArgb(46, foregroundColor.R, foregroundColor.G, foregroundColor.B);
        bool highContrast = DesktopTypographyService.IsHighContrast();
        _warningBadgeBgBrush.Color = highContrast
            ? _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent)
            : ColorHelper.FromArgb(184, 29, 35, 43);
        _warningIconFgBrush.Color = highContrast
            ? _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background)
            : ColorHelper.FromArgb(255, 255, 200, 87);
        CompactWarningBadge.Background = _warningBadgeBgBrush;
        CompactWarningIcon.Foreground = _warningIconFgBrush;
        CollapseDash.Background = _dockEmptyAddBrush;
        RefreshCollapseIndicator();
        CollapseButtonSurface.Background = CollapseButton.IsPointerOver ? _collapseHoverBrush : _transparentItemBrush;
        UpdateRealizedItems();
        ApplyCompactVisualStyle();
    }

    private void Host_ThemeChanged(object? sender, EventArgs e)
    {
        if (_host.LastChangedThemeTarget is ThemeTarget target &&
            OrganizerKinds.ThemeFor(_definition.PlacementMode) != target)
        {
            return;
        }
        ApplyTheme();
    }

    internal void MoveToPositionedPlacement(NativeMethods.RECT bounds, double runtimeScale)
    {
        _runtimeVisible = true;
        DisplayInfo display = DisplayPlacementService.ForBounds(bounds);
        _positionedCompactWidthDip = bounds.Width / display.Scale;
        _positionedCompactHeightDip = bounds.Height / display.Scale;
        ApplyCompactScale(repositionWindow: false, runtimeScale: runtimeScale);
        ShowCompactPlacement(bounds);
    }

    internal void MoveToFloatingPlacement(NativeMethods.RECT bounds)
    {
        _runtimeVisible = true;
        ApplyCompactScale(repositionWindow: false);
        ShowCompactPlacement(bounds);
    }

    internal void MoveToStationPlacement(NativeMethods.RECT anchor)
    {
        ApplyExpandedContentInset();
        UpdateOrganizerName();
        _transitionCancellation?.Cancel();
        _outsideClickHook?.Stop();
        _canvasResizeInputTimer.Stop();
        UpdateCanvasResizeEdgeWindows(show: false);
        _expanded = false;
        _animating = false;
        _host.NotifyDockOpenStateChanged();
        _transitionProgress = 0;
        _transitionVelocity = 0;
        _compactBounds = anchor;
        _stationDisplay = DisplayPlacementService.ForBounds(anchor);
        CompactView.Visibility = Visibility.Collapsed;
        ExpandedView.Visibility = Visibility.Collapsed;
        ExpandedView.Opacity = 0;
        _desktopLayer?.SetExpanded(false, showWindow: false);
        _appWindow?.Hide();
        _host.NotifyCollapsed(this);
        ResetStationPointerDelay();
        RefreshPerformanceSettings();
        ApplyLanguage();
    }

    private void ShowCompactPlacement(NativeMethods.RECT bounds)
    {
        if (IsPermanentlyExpanded && _expanded)
        {
            _compactBounds = bounds;
            ApplyExpandedContentInset();
            UpdateOrganizerName();
            ApplyLanguage();
            ClearWindowAlignment();
            return;
        }
        ApplyExpandedContentInset();
        UpdateOrganizerName();
        _transitionCancellation?.Cancel();
        _outsideClickHook?.Stop();
        _canvasResizeInputTimer.Stop();
        UpdateCanvasResizeEdgeWindows(show: false);
        _expanded = false;
        _animating = false;
        _host.NotifyDockOpenStateChanged();
        _transitionProgress = 0;
        _transitionVelocity = 0;
        _compactBounds = bounds;
        ExpandedView.Visibility = Visibility.Collapsed;
        ExpandedView.Opacity = 0;
        CompactView.Visibility = Visibility.Visible;
        CompactView.Opacity = 1;
        UpdateCompactLabelSafeWidth();
        UpdateSurfaceClips();
        _desktopLayer?.SetExpanded(false, showWindow: false);
        ApplyBounds(_compactBounds, show: true);
        _host.NotifyCollapsed(this);
        RefreshPerformanceSettings();
        ApplyLanguage();
    }

    internal bool IsCompactOrganizerDragActive => _widgetDragging && _widgetDragTopmost && !_draggingExpanded;

    internal void RaiseActiveCompactOrganizerDrag() => _desktopLayer?.SetExpanded(true, stayTopmost: true);

    private void ApplyCompactScale(bool repositionWindow = true, double? runtimeScale = null)
    {
        bool positioned = _definition.PlacementMode == OrganizerPlacementMode.Positioned;
        if (!positioned)
        {
            _positionedCompactWidthDip = 0;
            _positionedCompactHeightDip = 0;
        }
        double scale = runtimeScale ?? (_definition.PlacementMode == OrganizerPlacementMode.Positioned
            ? _appliedCompactScale
            : _definition.CompactScale);
        double previousScale = _appliedCompactScale;
        _appliedCompactScale = scale;
        CompactView.Width = GetCompactWidthDip();
        CompactView.Height = GetCompactHeightDip();
        CompactTile.Width = 39 * scale;
        CompactTile.Height = 39 * scale;
        UpdateCompactThumbnailMetrics();
        UpdateCompactPreviewItemScale();
        CompactView.RowDefinitions[0].Height = new GridLength(39 * scale);
        CompactView.RowDefinitions[1].Height = new GridLength(positioned ? 4 : Math.Clamp(4 * scale, 3, 6));
        CompactView.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);

        if (_hwnd != IntPtr.Zero)
        {
            if (_definition.PlacementMode == OrganizerPlacementMode.Station)
            {
                DisplayInfo stationDisplay = DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
                _compactBounds = DisplayPlacementService.CalculateStationAnchor(stationDisplay, _definition.DockEdge, _definition.Position);
                if (repositionWindow) _appWindow?.Hide();
                UpdateSurfaceClips();
                return;
            }
            DisplayInfo display = DisplayPlacementService.ForBounds(_compactBounds);
            int width = DipToPx(GetCompactWidthDip(), display.Scale);
            int height = DipToPx(GetCompactHeightDip(), display.Scale);
            int iconCenterX = _compactBounds.Left + _compactBounds.Width / 2;
            int iconCenterY = _compactBounds.Top + DipToPx(19.5 * previousScale, display.Scale);
            _compactBounds.Left = iconCenterX - width / 2;
            _compactBounds.Top = iconCenterY - DipToPx(19.5 * scale, display.Scale);
            _compactBounds.Right = _compactBounds.Left + width;
            _compactBounds.Bottom = _compactBounds.Top + height;
            bool clampVisibleFrame = _definition.PlacementMode == OrganizerPlacementMode.Floating &&
                (_host.State.GlobalSettings.WindowAlignmentEnabled ||
                 _compactBounds.Left < display.Work.Left || _compactBounds.Top < display.Work.Top ||
                 _compactBounds.Right > display.Work.Right || _compactBounds.Bottom > display.Work.Bottom);
            _compactBounds = clampVisibleFrame
                ? WindowAlignmentMath.ClampFrame(
                    _compactBounds,
                    display.Work,
                    CompactPlacementMath.CalculateCompactAlignmentInsets(width, height, scale, display.Scale))
                : DisplayPlacementService.Clamp(_compactBounds, display.Work);
            if (repositionWindow && !IsContained) ApplyBounds(_compactBounds, show: true);
        }
        ApplyCompactVisualStyle();
        UpdateSurfaceClips();
    }

    private double GetCompactWidthDip() =>
        _definition.PlacementMode == OrganizerPlacementMode.Positioned && _positionedCompactWidthDip > 0
            ? _positionedCompactWidthDip
            : OrganizerLimits.CalculateCompactWindowWidthDip(
                _appliedCompactScale,
                _host.State.GlobalSettings.ResolveCompactNameScale(_definition.PlacementMode));

    private double GetCompactHeightDip() =>
        _definition.PlacementMode == OrganizerPlacementMode.Positioned && _positionedCompactHeightDip > 0
            ? _positionedCompactHeightDip
            : OrganizerLimits.CalculateCompactWindowHeightDip(
                _appliedCompactScale,
                _host.State.GlobalSettings.ResolveCompactNameScale(_definition.PlacementMode));

    private int GetExpandedTitleBandPx(double displayScale) =>
        _definition.PlacementMode == OrganizerPlacementMode.Station
            ? 0
            : (int)Math.Round(DisplayPlacementService.ExpandedTitleBandDip * displayScale);

    private NativeMethods.RECT CalculateExpandedBoundsAroundCenter(NativeMethods.RECT current)
    {
        var compactAtCenter = _compactBounds;
        int centerX = current.Left + current.Width / 2;
        DisplayInfo display = DisplayPlacementService.ForBounds(current) with
        {
            Scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d)
        };
        int titleHeightPx = GetExpandedTitleBandPx(display.Scale);
        int panelHeightPx = Math.Max(1, current.Height - titleHeightPx);
        int centerY = current.Top + titleHeightPx + panelHeightPx / 2;
        compactAtCenter.Left = centerX - compactAtCenter.Width / 2;
        compactAtCenter.Right = compactAtCenter.Left + _compactBounds.Width;
        compactAtCenter.Top = centerY - DipToPx(19.5 * _appliedCompactScale, display.Scale);
        compactAtCenter.Bottom = compactAtCenter.Top + _compactBounds.Height;
        return CalculateExpandedBounds(compactAtCenter);
    }

    private bool NormalizeVisualScales(DisplayInfo display)
    {
        if (IsDock) return false;
        bool station = _definition.PlacementMode == OrganizerPlacementMode.Station;
        double canvas;
        double maximumItem;
        if (station)
        {
            canvas = Math.Clamp(_definition.CanvasScale, .1, 1.2);
            maximumItem = DisplayPlacementService.CalculateMaximumStationItemScale(display, _definition.Layout);
        }
        else
        {
            double minimumCanvas;
            if (_definition.ManualCanvasBaseWidthDip is double baseWidth &&
                _definition.ManualCanvasBaseHeightDip is double baseHeight)
            {
                (double minimumWidth, double minimumHeight) =
                    DisplayPlacementService.CalculateMinimumExpandedSizeDip(_definition.Layout, .5);
                minimumCanvas = Math.Min(1.2,
                    Math.Max(.1, Math.Max(minimumWidth / baseWidth, minimumHeight / baseHeight)));
            }
            else
            {
                minimumCanvas = DisplayPlacementService.CalculateMinimumCanvasScale(display, _definition.Layout);
            }
            canvas = Math.Clamp(_definition.CanvasScale, minimumCanvas, 1.2);
            if (_definition.ManualCanvasBaseWidthDip is double manualWidth &&
                _definition.ManualCanvasBaseHeightDip is double manualHeight)
            {
                NativeMethods.RECT work = DisplayPlacementService.GetExpandedWorkArea(display);
                double availablePanelHeightDip = Math.Max(
                    1,
                    work.Height / display.Scale - DisplayPlacementService.ExpandedTitleBandDip);
                double fit = Math.Min(1, Math.Min(
                    work.Width / display.Scale / (manualWidth * canvas),
                    availablePanelHeightDip / (manualHeight * canvas)));
                double contentScale = OrganizerContentScale.Normalize(_definition.IconContentScale);
                maximumItem = DisplayPlacementService.CalculateMaximumItemScaleForExpandedSize(
                    _definition.Layout,
                    manualWidth * canvas * fit / contentScale,
                    manualHeight * canvas * fit / contentScale);
            }
            else
            {
                maximumItem = DisplayPlacementService.CalculateMaximumItemScale(display, _definition.Layout, canvas);
            }
        }
        double item = Math.Clamp(_definition.ItemScale, .5, maximumItem);
        bool changed = Math.Abs(canvas - _definition.CanvasScale) > .0001 || Math.Abs(item - _definition.ItemScale) > .0001;
        _definition.CanvasScale = canvas;
        _definition.ItemScale = item;
        return changed;
    }

    private void ApplyBounds(NativeMethods.RECT bounds, bool show, bool preserveZOrder = false)
    {
        if (HasHoverPresentation) ResetHoverWave();
        bounds = DockPhysicalBounds(bounds);
        _animationTrace?.Metrics.RecordWork(AnimationTraceWork.ApplyBounds);
        uint flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER |
            (preserveZOrder ? NativeMethods.SWP_NOZORDER : 0) |
            (show ? NativeMethods.SWP_SHOWWINDOW : 0);
        IntPtr insertAfter = preserveZOrder
            ? IntPtr.Zero
            : _definition.PlacementMode == OrganizerPlacementMode.Station && _expanded
                ? NativeMethods.HWND_TOPMOST
                : NativeMethods.HWND_TOP;
        _ = NativeMethods.SetWindowPos(_hwnd, insertAfter,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, flags);
        if (_expanded && !_animating && _canvasResizeEdgeWindows.Count > 0)
            UpdateCanvasResizeEdgeWindows(show);
    }

    private static int DipToPx(double dip, double scale) => Math.Max(1, (int)Math.Round(dip * scale));

    private static bool RectsEqual(NativeMethods.RECT first, NativeMethods.RECT second) =>
        first.Left == second.Left && first.Top == second.Top && first.Right == second.Right && first.Bottom == second.Bottom;

    private static bool PositionsEqual(WidgetPosition? first, WidgetPosition second) =>
        first is not null && string.Equals(first.MonitorDevice, second.MonitorDevice, StringComparison.OrdinalIgnoreCase) &&
        Math.Abs(first.XDip - second.XDip) < .01 && Math.Abs(first.YDip - second.YDip) < .01 &&
        Math.Abs(first.SavedWorkAreaWidthDip - second.SavedWorkAreaWidthDip) < .01 &&
        Math.Abs(first.SavedWorkAreaHeightDip - second.SavedWorkAreaHeightDip) < .01;

    private sealed record CollapseTransitionGeometry(
        NativeMethods.RECT ExpandedBounds,
        NativeMethods.RECT CompactBounds,
        int EndExpandedLeft,
        int EndExpandedTop);

    private void UpdateCompactThumbnailMetrics()
    {
        double tileSize = 39 * _appliedCompactScale;
        CompactThumbnailHost.Width = tileSize;
        bool folderStyle = CompactVisualStyles.ShowsFolderSilhouette(
            CompactVisualStyles.Normalize(_host.State.GlobalSettings.CompactVisualStyle));
        if (folderStyle)
        {
            double tabOverlapOffset = Math.Max(4, Math.Round(tileSize * 0.11));
            CompactThumbnailHost.Height = Math.Max(20, tileSize - tabOverlapOffset);
            CompactThumbnailHost.VerticalAlignment = VerticalAlignment.Bottom;
            CompactShadowHost.VerticalAlignment = VerticalAlignment.Bottom;
        }
        else
        {
            CompactThumbnailHost.Height = tileSize;
            CompactThumbnailHost.VerticalAlignment = VerticalAlignment.Center;
            CompactShadowHost.VerticalAlignment = VerticalAlignment.Center;
        }
    }

    private void UpdateSurfaceClips()
    {
        if (!WindowRoot.IsLoaded) return;
        _hoverGeometryDirty = true;
        _animationTrace?.Metrics.RecordWork(AnimationTraceWork.SurfaceGeometry);
        RoundedSurfaceGeometry compactGeometry = RoundedSurfaceGeometry.Create(
            CompactThumbnailHost.ActualWidth, CompactThumbnailHost.ActualHeight,
            CompactCornerRadiusDip * _appliedCompactScale, WindowRoot.XamlRoot?.RasterizationScale ?? 1);
        double compactRadius = compactGeometry.Radius;
        double expandedRadius = IsDock ? CurrentDockCornerRadius : SnapDip(ExpandedCornerRadiusDip);
        _compactBackdrop.SetRoundedGeometry(compactGeometry);
        if (IsDock)
        {
            // A single arranged body owns every visible edge; the animation gutter
            // and independently arranged overlay must not change the material outline.
            RoundedSurfaceGeometry dockSurface = RoundedSurfaceGeometry.Create(ExpandedPanel.ActualWidth,
                ExpandedPanel.ActualHeight, expandedRadius, WindowRoot.XamlRoot?.RasterizationScale ?? 1);
            expandedRadius = dockSurface.Radius;
            _expandedBackdrop.SetRoundedGeometry(dockSurface);
            _expandedSurface.SetRoundedGeometry(dockSurface);
            _expandedEdgeSurface.SetRoundedGeometry(dockSurface);
        }
        else
        {
            ExpandedSurfaceHost.CornerRadius = new CornerRadius(expandedRadius);
            _expandedSurface.SetCornerRadius(expandedRadius);
            _expandedEdgeSurface.SetCornerRadius(expandedRadius);
        }
        _lastExpandedClipRadius = expandedRadius;
        _compactSurface.SetCornerRadius(compactRadius);
        _compactEdgeSurface.SetCornerRadius(compactRadius);
        UpdateCompactShadowGeometry(compactRadius);
        UpdateExpandedLiquidSheenGeometry(expandedRadius);
        Visual compactBackdropVisual = ElementCompositionPreview.GetElementVisual(CompactSurfaceHost);
        Visual expandedBackdropVisual = ElementCompositionPreview.GetElementVisual(ExpandedSurfaceHost);
        Visual compactContentVisual = ElementCompositionPreview.GetElementVisual(CompactIconPresenter);
        Visual expandedContentVisual = ElementCompositionPreview.GetElementVisual(ExpandedContentLayer);
        compactBackdropVisual.BorderMode = CompositionBorderMode.Soft;
        expandedBackdropVisual.BorderMode = CompositionBorderMode.Soft;
        compactContentVisual.BorderMode = CompositionBorderMode.Soft;
        expandedContentVisual.BorderMode = CompositionBorderMode.Soft;
        _compactClip ??= compactContentVisual.Compositor.CreateRectangleClip();
        _expandedClip ??= expandedContentVisual.Compositor.CreateRectangleClip();
        ApplyPixelAlignedClip(_compactClip, CompactThumbnailHost, compactRadius);
        ApplyPixelAlignedClip(_expandedClip, ExpandedContentLayer, expandedRadius);
        compactContentVisual.Clip = _compactClip;
        expandedContentVisual.Clip = IsDock ? null : _expandedClip;
        if (IsDock) UpdateDockInputRegion();
    }

    private Visual GetExpandedCompositionVisual() =>
        _expandedCompositionVisual ??= ElementCompositionPreview.GetElementVisual(ExpandedView);

    private void UpdateExpandedClipRadius(double progress)
    {
        if (_expandedClip is null) UpdateSurfaceClips();
        if (_expandedClip is null) return;
        double clamped = Math.Clamp(progress, 0, 1);
        double radius = CompactCornerRadiusDip * _appliedCompactScale +
            (ExpandedCornerRadiusDip - CompactCornerRadiusDip * _appliedCompactScale) * clamped;
        double snappedRadius = SnapDip(radius);
        if (Math.Abs(snappedRadius - _lastExpandedClipRadius) < 0.001) return;
        _lastExpandedClipRadius = snappedRadius;
        _animationTrace?.Metrics.RecordWork(AnimationTraceWork.AnimatedCorner);
        SetClipRadius(_expandedClip, snappedRadius);
        ExpandedSurfaceHost.CornerRadius = new CornerRadius(snappedRadius);
        _expandedSurface.SetCornerRadius(snappedRadius);
        _expandedEdgeSurface.SetCornerRadius(snappedRadius);
        if (!_animating)
            UpdateExpandedLiquidSheenGeometry(snappedRadius);
    }

    private void ApplyPixelAlignedClip(RectangleClip clip, FrameworkElement element, double radius)
    {
        RoundedSurfaceGeometry surface = RoundedSurfaceGeometry.Create(
            element.ActualWidth, element.ActualHeight, radius, WindowRoot.XamlRoot?.RasterizationScale ?? 1);
        clip.Left = 0;
        clip.Top = 0;
        clip.Right = surface.Width;
        clip.Bottom = surface.Height;
        SetClipRadius(clip, surface.Radius);
    }

    private static void SetClipRadius(RectangleClip clip, double radius)
    {
        var value = new Vector2((float)Math.Max(0, radius));
        clip.TopLeftRadius = value;
        clip.TopRightRadius = value;
        clip.BottomLeftRadius = value;
        clip.BottomRightRadius = value;
    }

    private double SnapDip(double value)
    {
        double scale = Math.Max(1, WindowRoot.XamlRoot?.RasterizationScale ?? 1);
        return Math.Round(value * scale) / scale;
    }

    internal OrganizerInteractionSnapshot CaptureInteractionState()
    {
        OrganizerBusyFlags flags = OrganizerBusyFlags.None;
        if (_changingExpansionMode) flags |= OrganizerBusyFlags.ChangingExpansionMode;
        if (_animating) flags |= OrganizerBusyFlags.Animating;
        if (_pressActive) flags |= OrganizerBusyFlags.PressActive;
        if (_widgetDragging) flags |= OrganizerBusyFlags.WidgetDragging;
        if (_nativeMouseCapture) flags |= OrganizerBusyFlags.NativeMouseCapture;
        if (_canvasResize is not null) flags |= OrganizerBusyFlags.CanvasResizing;
        if (_widgetGesture.IsCompleting) flags |= OrganizerBusyFlags.WidgetGestureCompleting;
        if (_resizeGesture.IsCompleting) flags |= OrganizerBusyFlags.ResizeGestureCompleting;
        if (_itemReorderSession is not null) flags |= OrganizerBusyFlags.ItemReordering;
        if (Volatile.Read(ref _shellDragActive)) flags |= OrganizerBusyFlags.ShellDragActive;
        if (_shellPromotionPending) flags |= OrganizerBusyFlags.ShellPromotionPending;
        if (_shellDropFinalizing) flags |= OrganizerBusyFlags.ShellDropFinalizing;
        if (_itemDragLanding) flags |= OrganizerBusyFlags.ItemDragLanding;
        if (_itemCollectionMoveInProgress) flags |= OrganizerBusyFlags.ItemCollectionMoving;
        if (_nativeItemMotionRenderingSubscribed) flags |= OrganizerBusyFlags.NativeItemMotionRendering;
        if (_dragHasLocalFile) flags |= OrganizerBusyFlags.DragHasLocalFile;
        if (ReceivingDrop) flags |= OrganizerBusyFlags.ReceivingDrop;
        if (_shellContextMenuOpen) flags |= OrganizerBusyFlags.ShellContextMenuOpen;
        if (_contextMenuActivated) flags |= OrganizerBusyFlags.ContextMenuActivated;
        if (_overlayOpenCount > 0) flags |= OrganizerBusyFlags.OverlayOpen;
        if (_catalogController.IsAddingItem) flags |= OrganizerBusyFlags.AddingItem;
        if (_organizerTitleEdit.IsBusy) flags |= OrganizerBusyFlags.TitleEditing;
        if (_host.TransferQueue.IsActive) flags |= OrganizerBusyFlags.TransferQueueActive;
        if (_modeInteractionOperations > 0) flags |= OrganizerBusyFlags.ModeInteractionHeld;
        if (_desktopLayer is null) flags |= OrganizerBusyFlags.DesktopLayerUnavailable;
        else if (_desktopLayer.IsInputActivationEnabled) flags |= OrganizerBusyFlags.DesktopInputActivated;
        return new OrganizerInteractionSnapshot(flags);
    }

    internal AppHost Host => _host;
    internal OrganizerDefinition Definition => _definition;
    internal StorageService Storage => _storage;
    internal IntPtr Hwnd => _hwnd;
    internal DesktopLayerService? DesktopLayer => _desktopLayer;
    internal CancellationToken LifetimeToken => _windowLifetime.Token;
    internal bool IsClosing => _closing;
    internal bool IsExpandedCore => _expanded;
    internal bool IsPressActive => _pressActive;
    internal bool IsContainedWindow => IsContained;
    internal bool IsDockWindow => IsDock;
    internal bool IsRuntimeVisible => _runtimeVisible;
    internal bool IsStationVisible => _stationVisible;
    internal bool IsStationReady => _stationReady;
    internal bool IsChangingExpansionMode => _changingExpansionMode;
    internal bool IsContextMenuActivated => _contextMenuActivated;
    internal bool HasActiveLocalFileDrop => _dragHasLocalFile;
    internal bool IsExternalHoverTimerRunning => _externalHoverTimer.IsRunning;
    internal bool IsHoverExpandScrollToEnd => _hoverExpandScrollToEnd;
    internal ItemReorderSession? ActiveReorderSession => _itemReorderSession;
    internal Border? CurrentItemDragHost => _itemDragHost;
    internal PointerPoint? CurrentItemDragPointerPoint => _itemDragLastPointerPoint;
    internal (double Width, double Height) NativeDragCellDimensions => (_nativeDragCellWidth, _nativeDragCellHeight);
    internal bool WasInternalOleDropAccepted => _internalOleDropAccepted;

    internal static bool AreRectsEqual(NativeMethods.RECT first, NativeMethods.RECT second) => RectsEqual(first, second);
    internal static bool ArePositionsEqual(WidgetPosition? first, WidgetPosition second) => PositionsEqual(first, second);

    internal void ObserveHoverGuardPointer(bool inside) => _modeHoverGuard.ObservePointer(inside);
    internal void StopExternalHoverTimer() => _externalHoverTimer.Stop();
    internal void StartHoverExpandCore(bool scrollToEnd) => StartHoverExpand(scrollToEnd);
    internal bool IsPointerOverWindow(NativeMethods.POINT pointer) => IsPointerOverThisWindow(pointer);
    internal bool TryGetWindowRect(out NativeMethods.RECT bounds) => TryGetLogicalWindowRect(out bounds);
    internal DisplayInfo GetOrResolveStationDisplay() =>
        _stationDisplay ??= DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
    internal void SetStationDisplay(DisplayInfo display) => _stationDisplay = display;
    internal void SetCompactBounds(NativeMethods.RECT bounds, bool applyAndShow)
    {
        _compactBounds = bounds;
        if (applyAndShow) ApplyBounds(_compactBounds, show: true);
    }
    internal bool TryGetCompactAlignmentInsetsForRepair(NativeMethods.RECT bounds, out WindowAlignmentInsets insets) =>
        TryGetCompactAlignmentInsets(bounds, out insets);
    internal void HideAppWindow() => _appWindow?.Hide();
    internal Task SaveOrganizerStateAsync() => SaveStateAsync();
    internal Task ExpandOrganizerAsync(bool scrollToEnd = false) => ExpandAsync(scrollToEnd);
    internal Task CollapseOrganizerAsync() => CollapseAsync();
    internal Task RunWindowSafelyAsync(Func<Task> action, string context) => RunSafelyAsync(action, context);
    internal void ShowOrganizerMessage(string message, InfoBarSeverity severity) => ShowMessage(message, severity);
    internal DisplayInfo ResolveRenameDialogDisplay() => GetRenameDialogDisplay();
    internal void ResetStationDelay() => ResetStationPointerDelay();
    internal void AdjustOverlayCount(int delta) => _overlayOpenCount = Math.Max(0, _overlayOpenCount + delta);
    internal async Task ScrollToEndAfterRenderAsync()
    {
        await WaitForNextRenderAsync(CancellationToken.None);
        ScrollToEnd(animated: true);
    }
    internal IDisposable HoldModeInteraction() => HoldExpansionModeInteraction();
    internal void CancelActiveItemReorder() => CancelItemReorder();
    internal void CaptureNativeDragCellDimensions()
    {
        Size viewport = GetItemsViewportSize();
        (double fallbackWidth, double fallbackHeight) = GetItemCellSizeDip(viewport.Width, viewport.Height);
        _nativeDragCellWidth = Math.Max(1, _itemDragHost?.Width ?? fallbackWidth);
        _nativeDragCellHeight = Math.Max(1, _itemDragHost?.Height ?? fallbackHeight);
    }
    internal void BeginExternalDragHandoff(
        Border host,
        string relativeName,
        bool keepBoundaryHook,
        bool releasePointerCapture,
        bool startMotionRendering,
        bool hideHostImmediately = true)
    {
        _shellPromotionPending = true;
        if (!releasePointerCapture) _shellPromotionRequestedAt = Stopwatch.GetTimestamp();
        StopItemDragInput(keepRendering: false, keepBoundaryHook: keepBoundaryHook, releasePointerCapture: releasePointerCapture);
        CleanupIosFreeGridOverlays();
        _freeGridEngine = null;
        if (hideHostImmediately)
        {
            ResetItemDragVisuals();
            ApplyNativeSourcePlaceholder(host);
        }
        _draggedRelativeName = relativeName;
        Volatile.Write(ref _shellDragActive, true);
        _internalOleDropAccepted = false;
        _shellPromotionPending = false;
        if (startMotionRendering) StartNativeItemMotionRendering();
    }
    internal void ApplyExternalDragPlaceholder(Border host)
    {
        ResetItemDragVisuals();
        ApplyNativeSourcePlaceholder(host);
    }
    internal Task CommitNativeItemReorderAsync(ItemReorderSession session) =>
        CommitItemReorderAsync(session, nativeDrop: true);
    internal void AbortReorderVisualsAfterExternalDrag()
    {
        ResetItemDragVisuals();
        FinishItemReorderSession(runPendingRefresh: false);
    }
    internal void SetShellDropFinalizing(bool finalizing) => _shellDropFinalizing = finalizing;
    internal void CompleteExternalDragVisualState(ItemReorderSession session) => FinishShellDragVisualState(session);
    internal void TryCompleteExternalDragVisualState(ItemReorderSession session) => TryFinishShellDragVisualState(session);
    internal void StopBoundaryHookForExternalDrag() => StopItemDragBoundaryHook();
    internal void ClearExternalDragFlags()
    {
        _shellDropFinalizing = false;
        _internalOleDropAccepted = false;
        _shellPromotionRequestedAt = 0;
    }
    internal void ResetExternalDragTiming()
    {
        _shellPromotionRequestedAt = 0;
        Interlocked.Exchange(ref _itemDragBoundaryDetectedAt, 0);
        _itemDragPressedAt = 0;
    }
}
