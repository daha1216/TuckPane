using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class StartupPlacementChecks
{
    internal static void Run()
    {
        CheckExactRightEdgeFloatingRestore();
        CheckStaleNegativeInsetsRejected();
        CheckEdgeAffinityAcrossWorkAreaChanges();
        CheckTransientDisplayShrinkDoesNotPersist();
        CheckWindowResourceScope();
        CheckStartupAntiBlackWindowContracts();
        Console.WriteLine("PASS --startup-placement: edge-snapped floating restore, stale inset guard, edge affinity, transient shrink protection, WindowResourceScope, and cloaked zero-black-flash startup contracts.");
    }

    private static void CheckExactRightEdgeFloatingRestore()
    {
        // Reproduces 2560x1600 @ 150% DPI (Work 2560x1528 => 1706.6667 x 1018.6667 DIP),
        // CompactScale = 1.56, NameScale = 1.0, floating organizer snapped flush to right edge.
        var display = new DisplayInfo(
            @"\\.\DISPLAY1",
            Rect(0, 0, 2560, 1600),
            Rect(0, 0, 2560, 1528),
            1.5);
        int widthPx = (int)Math.Round(OrganizerLimits.CalculateCompactWindowWidthDip(1.56, 1.0) * display.Scale);
        int heightPx = (int)Math.Round(OrganizerLimits.CalculateCompactWindowHeightDip(1.56, 1.0) * display.Scale);
        Require(widthPx == 178 && heightPx == 154, $"Unexpected compact pixel size: {widthPx}x{heightPx}.");

        WindowAlignmentInsets insets = CompactPlacementMath.CalculateCompactAlignmentInsets(
            widthPx, heightPx, compactScale: 1.56, displayScale: display.Scale);
        Require(insets.IsValidFor(Rect(0, 0, widthPx, heightPx)) &&
                insets.Left == 43 && insets.Right == 44 && insets.Top == 0 && insets.Bottom == 63,
            $"Unexpected deterministic compact insets: {insets}.");

        var saved = new WidgetPosition
        {
            MonitorDevice = @"\\.\DISPLAY1",
            XDip = 1617.3333333333333,
            YDip = 364.6666666666667,
            SavedWorkAreaWidthDip = 1706.6666666666667,
            SavedWorkAreaHeightDip = 1018.6666666666666
        };

        Require(CompactPlacementMath.ShouldUseAlignmentFrameOnRestore(
            OrganizerPlacementMode.Floating, windowAlignmentEnabled: false, saved, widthPx, heightPx, display),
            "An edge-snapped floating window must still restore with alignment insets even when snapping is toggled off.");

        NativeMethods.RECT restored = DisplayPlacementService.RestoreToDisplay(
            saved, display, widthPx, heightPx, insets);
        NativeMethods.RECT visibleFrame = insets.ToFrame(restored);
        Require(restored.Left == 2426 && visibleFrame.Right == 2560,
            $"Right-edge floating organizer drifted from edge: window=({restored.Left},{restored.Top},{restored.Right},{restored.Bottom}) frameRight={visibleFrame.Right}.");
    }

    private static void CheckStaleNegativeInsetsRejected()
    {
        NativeMethods.RECT work = Rect(0, 0, 2560, 1528);
        NativeMethods.RECT window = Rect(2426, 547, 2604, 701);
        // Exact corrupted insets previously produced when WindowRoot was still at default ~1280x720 DIP.
        var staleBugInsets = new WindowAlignmentInsets(Left: 914, Top: 465, Right: -827, Bottom: -402);
        Require(!staleBugInsets.IsValidFor(window), "Negative/oversized insets must fail IsValidFor.");

        NativeMethods.RECT clamped = WindowAlignmentMath.ClampFrame(window, work, staleBugInsets);
        Require(clamped.Left == 2382 && clamped.Right == 2560,
            $"Invalid insets must fall back to standard work-area clamp instead of teleporting toward center: got Left={clamped.Left}.");

        var oversizedInsets = new WindowAlignmentInsets(Left: 100, Top: 0, Right: 100, Bottom: 0);
        Require(!oversizedInsets.IsValidFor(window), "Insets wider than the window must fail IsValidFor.");
    }

    private static void CheckEdgeAffinityAcrossWorkAreaChanges()
    {
        // Window saved flush against right edge of 1706.67 DIP display, restored onto wider 2560 DIP work area.
        var widerDisplay = new DisplayInfo(
            @"\\.\DISPLAY1",
            Rect(0, 0, 3840, 2160),
            Rect(0, 0, 3840, 2088),
            1.5);
        int widthPx = 178;
        int heightPx = 154;
        WindowAlignmentInsets insets = CompactPlacementMath.CalculateCompactAlignmentInsets(widthPx, heightPx, 1.56, 1.5);

        var rightPinned = new WidgetPosition
        {
            MonitorDevice = @"\\.\DISPLAY1",
            XDip = 1617.3333333333333,
            YDip = 300,
            SavedWorkAreaWidthDip = 1706.6666666666667,
            SavedWorkAreaHeightDip = 1018.6666666666666
        };

        NativeMethods.RECT restoredRight = DisplayPlacementService.RestoreToDisplay(
            rightPinned, widerDisplay, widthPx, heightPx, insets);
        Require(insets.ToFrame(restoredRight).Right == widerDisplay.Work.Right,
            $"Right-edge pinned window must stay flush with wider work area right edge: frameRight={insets.ToFrame(restoredRight).Right}, expected={widerDisplay.Work.Right}.");

        // Mid-screen window must keep its absolute XDip instead of jumping to the right edge.
        var midScreen = new WidgetPosition
        {
            MonitorDevice = @"\\.\DISPLAY1",
            XDip = 600,
            YDip = 300,
            SavedWorkAreaWidthDip = 1706.6666666666667,
            SavedWorkAreaHeightDip = 1018.6666666666666
        };
        NativeMethods.RECT restoredMid = DisplayPlacementService.RestoreToDisplay(
            midScreen, widerDisplay, widthPx, heightPx, insets);
        Require(restoredMid.Left == 900, $"Mid-screen window must retain XDip=600 (900px), got Left={restoredMid.Left}.");
    }

    private static void CheckTransientDisplayShrinkDoesNotPersist()
    {
        var saved = new WidgetPosition
        {
            MonitorDevice = @"\\.\DISPLAY1",
            XDip = 1617.3333,
            YDip = 364.6667,
            SavedWorkAreaWidthDip = 1706.6667,
            SavedWorkAreaHeightDip = 1018.6667
        };
        var normalDisplay = new DisplayInfo(@"\\.\DISPLAY1", Rect(0, 0, 2560, 1600), Rect(0, 0, 2560, 1528), 1.5);
        var shrunkBootDisplay = new DisplayInfo(@"\\.\DISPLAY1", Rect(0, 0, 1024, 768), Rect(0, 0, 1024, 728), 1.0);
        var otherDisplay = new DisplayInfo(@"\\.\DISPLAY2", Rect(0, 0, 2560, 1600), Rect(0, 0, 2560, 1528), 1.5);

        Require(CompactPlacementMath.ShouldPersistClampedFloatingPosition(saved, normalDisplay),
            "Normal display repair should be allowed to persist.");
        Require(!CompactPlacementMath.ShouldPersistClampedFloatingPosition(saved, shrunkBootDisplay),
            "Transient work-area shrink during boot/game/RDP must never overwrite saved floating coordinates.");
        Require(!CompactPlacementMath.ShouldPersistClampedFloatingPosition(saved, otherDisplay),
            "Fallback to a different monitor device must never overwrite saved floating coordinates.");
    }

    private static void CheckWindowResourceScope()
    {
        var order = new List<int>();
        var errors = new List<Exception>();
        var scope = new WindowResourceScope();
        scope.Track(() => order.Add(1));
        scope.Track(() => { order.Add(2); throw new InvalidOperationException("boom"); });
        scope.Track(() => order.Add(3));
        Require(scope.Count == 3 && !scope.IsDisposed, "Scope must track registered cleanups before disposal.");

        scope.DisposeAll(errors.Add);
        Require(scope.IsDisposed && scope.Count == 0, "Scope must clear cleanups on disposal.");
        Require(order.SequenceEqual([3, 2, 1]), "Scope must run cleanups in LIFO order even when one throws.");
        Require(errors.Count == 1, "Scope must report cleanup failure without aborting remaining cleanups.");

        scope.DisposeAll(errors.Add);
        Require(order.Count == 3, "Second disposal must be a no-op.");

        bool lateRan = false;
        scope.Track(() => lateRan = true);
        Require(lateRan, "Track after disposal must run cleanup immediately.");
    }

    private static void CheckStartupAntiBlackWindowContracts()
    {
        string mainWindowCs = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/MainWindow.xaml.cs");
        string initHost = RepoSourceSnapshot.ExtractMethodBody(mainWindowCs, "InitializeHostWindow()");
        Require(initHost.Contains("BeginStartupCloak()", StringComparison.Ordinal) &&
                initHost.Contains("DesktopLayerService.ApplyInitialToolWindowStyle(_hwnd)", StringComparison.Ordinal) &&
                initHost.Contains("TransparentWindowBackdrop.EnsureAlphaComposition(_hwnd)", StringComparison.Ordinal) &&
                initHost.Contains("PrePositionCompactHostWindow()", StringComparison.Ordinal),
            "InitializeHostWindow must cloak, style as toolwindow/no-activate, enable DWM alpha, and pre-position before Activate().");

        string initCore = RepoSourceSnapshot.ExtractMethodBody(mainWindowCs, "InitializeCoreAsync()");
        Require(initCore.Contains("ApplyCompactScale(repositionWindow: false)", StringComparison.Ordinal) &&
                initCore.Contains("WaitForNextRenderAsync(", StringComparison.Ordinal) &&
                initCore.Contains("ReleaseStartupCloak()", StringComparison.Ordinal) &&
                initCore.Contains("finally", StringComparison.Ordinal),
            "InitializeCoreAsync must avoid (0,0) jump, wait for first frame, and always release DWM startup cloak in finally.");

        string appHostCs = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/AppHost.cs");
        string createWindow = RepoSourceSnapshot.ExtractMethodBody(appHostCs, "CreateWindow(OrganizerDefinition organizer)");
        Require(createWindow.Contains("window.InitializeAsync()", StringComparison.Ordinal),
            "AppHost.CreateWindow must start InitializeAsync explicitly because WS_EX_NOACTIVATE suppresses WM_ACTIVATE.");
    }

    private static NativeMethods.RECT Rect(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
