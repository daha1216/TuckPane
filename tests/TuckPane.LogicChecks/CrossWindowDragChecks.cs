using System.Xml.Linq;
using TuckPane.Core;
using TuckPane.Services;

internal static class CrossWindowDragChecks
{
    internal static async Task RunAsync()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        CheckPreEdgeDetachGeometry(Require);
        CheckVelocityInertiaTilt(Require);
        CheckDesktopDropFanPositions(Require);
        CheckGhostWindowOriginAlignment(Require);
        CheckFloatingGhostControllerLifecycle(Require);
        await CheckDesktopPositionServiceHeadlessGuardAsync(Require);
        CheckSourceContractsAndInvariants(Require);

        Console.WriteLine("CrossWindowDragChecks passed (7/7 groups).");
    }

    private static void CheckPreEdgeDetachGeometry(Action<bool, string> require)
    {
        var viewport = new DragRectDip(0, 0, 520, 420);

        // Card comfortably centered inside organizer -> keep in-window visual.
        var centered = new DragRectDip(180, 140, 100, 110);
        require(
            !CrossWindowDragMath.ShouldDetachVisualToFloatingGhost(centered, viewport, liftScale: 1.12, marginDip: 6.0),
            "Centered card must NOT prematurely detach to floating ghost.");

        // Card dragged toward left edge: card left = 8 DIP (cursor still well inside organizer at ~58 DIP!),
        // but 1.12x scale expands left edge by 6 DIP -> 2 DIP, within 6 DIP margin of clip.Left (0) -> MUST detach BEFORE clipping!
        var nearLeftEdge = new DragRectDip(8, 140, 100, 110);
        require(
            CrossWindowDragMath.ShouldDetachVisualToFloatingGhost(nearLeftEdge, viewport, liftScale: 1.12, marginDip: 6.0),
            "Card approaching left clip boundary must detach BEFORE a single pixel can be clipped.");

        // Approaching top, right, bottom edges must also detach early.
        var nearTop = new DragRectDip(180, 9, 100, 110);
        var nearRight = new DragRectDip(412, 140, 100, 110);
        var nearBottom = new DragRectDip(180, 301, 100, 110);
        require(CrossWindowDragMath.ShouldDetachVisualToFloatingGhost(nearTop, viewport), "Near top edge should detach.");
        require(CrossWindowDragMath.ShouldDetachVisualToFloatingGhost(nearRight, viewport), "Near right edge should detach.");
        require(CrossWindowDragMath.ShouldDetachVisualToFloatingGhost(nearBottom, viewport), "Near bottom edge should detach.");

        // Just outside the detach threshold (100px card scaled 1.12x expands 6px + 6px margin = 12px from 0 -> left=13 stays inside).
        var safelyInsideLeft = new DragRectDip(13.5, 140, 100, 110);
        require(
            !CrossWindowDragMath.ShouldDetachVisualToFloatingGhost(safelyInsideLeft, viewport, liftScale: 1.12, marginDip: 6.0),
            "Card just clear of scale+margin threshold should remain in-window.");

        // Empty or non-finite viewport must never throw or spuriously detach.
        require(!CrossWindowDragMath.ShouldDetachVisualToFloatingGhost(centered, new DragRectDip(0, 0, 0, 0)),
            "Zero-size viewport should return false safely.");
        require(!CrossWindowDragMath.ShouldDetachVisualToFloatingGhost(new DragRectDip(double.NaN, 0, 100, 100), viewport),
            "NaN card coordinates should return false safely.");
    }

    private static void CheckVelocityInertiaTilt(Action<bool, string> require)
    {
        require(MathF.Abs(CrossWindowDragMath.ComputeVelocityTiltTargetDeg(0f, 1.5)) < 0.001f,
            "Zero velocity must yield zero tilt.");

        float tilt100 = CrossWindowDragMath.ComputeVelocityTiltTargetDeg(720f, 1.0);
        float tilt200 = CrossWindowDragMath.ComputeVelocityTiltTargetDeg(1440f, 2.0);
        require(tilt100 > 2.5f && tilt100 < 3.5f, $"Moderate velocity should tilt gently (got {tilt100}).");
        require(MathF.Abs(tilt100 - tilt200) < 0.01f,
            $"Velocity tilt must be DPI-invariant (100%={tilt100}, 200%={tilt200}).");

        float maxRight = CrossWindowDragMath.ComputeVelocityTiltTargetDeg(50_000f, 1.0);
        float maxLeft = CrossWindowDragMath.ComputeVelocityTiltTargetDeg(-50_000f, 1.0);
        require(MathF.Abs(maxRight - 8.5f) < 0.01f && MathF.Abs(maxLeft + 8.5f) < 0.01f,
            $"Fast flick tilt must clamp strictly to [-8.5, +8.5] (got {maxLeft}, {maxRight}).");

        require(CrossWindowDragMath.ComputeVelocityTiltTargetDeg(float.NaN, 1.5) == 0f,
            "NaN velocity must yield 0 tilt.");
        require(CrossWindowDragMath.ComputeVelocityTiltTargetDeg(float.PositiveInfinity, 1.5) == 0f,
            "Infinite velocity must yield 0 tilt.");
    }

    private static void CheckDesktopDropFanPositions(Action<bool, string> require)
    {
        IReadOnlyList<(int X, int Y)> single = CrossWindowDragMath.ComputeDesktopDropFanPositions(
            anchorX: 600, anchorY: 400, itemCount: 1,
            cellWidthPx: 100, cellHeightPx: 110,
            workLeft: 0, workTop: 0, workRight: 1920, workBottom: 1040);
        require(single.Count == 1 && single[0] == (600, 400),
            $"Single in-bounds drop should keep exact anchor (got {single.Count}: {single.FirstOrDefault()}).");

        // Multi-item drop near bottom-right corner must wrap cleanly without escaping work area or colliding.
        IReadOnlyList<(int X, int Y)> fanned = CrossWindowDragMath.ComputeDesktopDropFanPositions(
            anchorX: 1850, anchorY: 960, itemCount: 5,
            cellWidthPx: 100, cellHeightPx: 110,
            workLeft: 0, workTop: 0, workRight: 1920, workBottom: 1040);
        require(fanned.Count == 5, $"Expected 5 fanned positions, got {fanned.Count}.");
        var unique = new HashSet<(int X, int Y)>(fanned);
        require(unique.Count == 5, "All 5 fanned desktop drop positions must be distinct.");
        foreach ((int x, int y) in fanned)
        {
            require(x >= 0 && x <= 1820 && y >= 0 && y <= 930,
                $"Fanned desktop position ({x},{y}) escaped work area bounds.");
        }

        // Zero count -> empty list.
        require(CrossWindowDragMath.ComputeDesktopDropFanPositions(100, 100, 0, 100, 100, 0, 0, 1920, 1080).Count == 0,
            "Zero itemCount should return empty list.");
    }

    private static void CheckGhostWindowOriginAlignment(Action<bool, string> require)
    {
        (int winLeft, int winTop) = CrossWindowDragMath.ComputeGhostWindowOriginPx(
            cursorX: 1240,
            cursorY: 680,
            grabOffsetXPx: 62,
            grabOffsetYPx: 48,
            cardPadLeftPx: 54,
            cardPadTopPx: 42);

        // Verify exact 0px handoff alignment: windowLeft + cardPadLeft + grabOffsetX == cursorX.
        require(winLeft + 54 + 62 == 1240, $"Horizontal handoff alignment mismatch: winLeft={winLeft}.");
        require(winTop + 42 + 48 == 680, $"Vertical handoff alignment mismatch: winTop={winTop}.");
    }

    private static void CheckFloatingGhostControllerLifecycle(Action<bool, string> require)
    {
        require(AppPaths.IsTestMode, "CrossWindowDragChecks must run in headless test mode.");
        FloatingDragGhostController controller = FloatingDragGhostController.Shared;
        controller.CancelImmediate();
        require(!controller.IsActive, "Controller should start inactive.");

        var desc = new FloatingDragGhostDescriptor(
            Items: [new FloatingDragGhostItemDescriptor(@"C:\Temp\Demo.lnk", "Demo")],
            CardWidthDip: 100,
            CardHeightDip: 110,
            IconSizeDip: 56,
            GrabOffsetXDip: 50,
            GrabOffsetYDip: 50,
            InitialDpiScale: 1.5,
            InitialCursorScreenPx: new NativeMethods.POINT { X = 800, Y = 500 },
            InitialScale: 1.12f,
            InitialTiltDeg: 2.5f,
            IsDarkTheme: false,
            ThemeAccentArgb: 0xFF007AFF);

        long token1 = controller.BeginSession(desc);
        require(token1 > 0 && controller.IsActive, "BeginSession must activate controller and return positive token.");
        controller.UpdateBadge(token1, FloatingDragBadgeKind.MoveToDesktop, "移到桌面");

        long token2 = controller.BeginSession(desc);
        require(token2 > token1 && controller.IsActive, "Second BeginSession must monotonically supersede first token.");

        // Stale token cancellation must NOT disrupt active token2.
        controller.CancelImmediate(token1);
        require(controller.IsActive, "Canceling stale token1 must not deactivate newer token2.");

        controller.CompleteWithLanding(token2, new NativeMethods.POINT { X = 400, Y = 400 });
        require(!controller.IsActive, "CompleteWithLanding in headless test mode must settle to inactive.");

        long token3 = controller.BeginSession(desc);
        controller.CompleteWithRebound(token3, new NativeMethods.POINT { X = 900, Y = 500 });
        require(!controller.IsActive, "CompleteWithRebound in headless test mode must settle to inactive.");
    }

    private static async Task CheckDesktopPositionServiceHeadlessGuardAsync(Action<bool, string> require)
    {
        int positioned = await DesktopIconPositionService.PositionDroppedDesktopItemsAsync(
            [@"C:\Users\Public\Desktop\NonExistent.lnk"],
            new NativeMethods.POINT { X = 500, Y = 400 });
        require(positioned == 0, "DesktopIconPositionService must safely no-op in headless test mode.");
    }

    private static void CheckSourceContractsAndInvariants(Action<bool, string> require)
    {
        string mainCs = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/MainWindow.xaml.cs");
        int mainLines = mainCs.Split('\n').Length;
        require(mainLines < 3800, $"MainWindow.xaml.cs must remain strictly < 3800 lines (currently {mainLines}).");
        require(
            !mainCs.Split('\n').Any(line => line.Contains("SetItemToolTip(", StringComparison.Ordinal) && line.Contains("FullPath", StringComparison.Ordinal)),
            "MainWindow.xaml.cs must not leak raw item.FullPath into item hover tooltips.");

        string dndCs = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/MainWindow.ItemDragAndDrop.cs");
        require(dndCs.Contains("SuppressItemHostToolTip", StringComparison.Ordinal),
            "MainWindow.ItemDragAndDrop.cs must suppress tooltips during drag.");
        require(dndCs.Contains("ShouldDetachVisualToFloatingGhost", StringComparison.Ordinal),
            "MainWindow.ItemDragAndDrop.cs must evaluate pre-edge floating ghost handoff.");
        require(!dndCs.Contains("GetElementVisual(", StringComparison.Ordinal),
            "MainWindow.ItemDragAndDrop.cs must never call GetElementVisual on item hosts.");

        string reorderCs = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/MainWindow.FreeGridReorder.cs");
        require(!reorderCs.Contains("GetElementVisual(", StringComparison.Ordinal),
            "MainWindow.FreeGridReorder.cs must have ZERO GetElementVisual calls.");
        require(!reorderCs.Contains("new ThemeShadow(", StringComparison.Ordinal),
            "MainWindow.FreeGridReorder.cs must have ZERO new ThemeShadow allocations.");

        string coordCs = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/ExternalItemDragCoordinator.cs");
        require(coordCs.Contains("item.Kind != WidgetItemKind.Note", StringComparison.Ordinal),
            "ExternalItemDragCoordinator must route all real filesystem items through native floating-ghost Shell drag.");
        require(coordCs.Contains("suppressSystemDragImage", StringComparison.Ordinal),
            "ExternalItemDragCoordinator must suppress duplicate 64px OLE bitmap when floating ghost is active.");
        require(coordCs.Contains("DesktopIconPositionService.PositionDroppedDesktopItemsAsync", StringComparison.Ordinal),
            "ExternalItemDragCoordinator must invoke DesktopIconPositionService on desktop drops.");

        string appHostCs = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/AppHost.cs");
        require(appHostCs.Contains("FloatingDragGhostController.ShutdownShared()", StringComparison.Ordinal),
            "AppHost must shut down FloatingDragGhostController on exit/dispose.");
        require(appHostCs.Contains("TryFindOrganizerUnderCursor", StringComparison.Ordinal),
            "AppHost must expose TryFindOrganizerUnderCursor for cross-organizer drag badge feedback.");

        // Verify 3-way .resw parity and presence of all 4 DragBadge* keys.
        string[] requiredKeys =
        [
            "DragBadgeMoveToDesktop",
            "DragBadgeCopyToDesktop",
            "DragBadgeMoveToOrganizerFormat",
            "DragBadgeCopyToOrganizerFormat"
        ];
        var keySets = new Dictionary<string, HashSet<string>>();
        foreach (string locale in new[] { "zh-CN", "en-US", "ja-JP" })
        {
            string xml = RepoSourceSnapshot.ReadRepoFile($"src/TuckPane/Strings/{locale}/Resources.resw");
            XDocument doc = XDocument.Parse(xml);
            var keys = doc.Descendants("data")
                .Select(el => el.Attribute("name")?.Value)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToHashSet(StringComparer.Ordinal);
            foreach (string key in requiredKeys)
                require(keys.Contains(key), $"Missing key '{key}' in {locale}/Resources.resw.");
            keySets[locale] = keys;
        }
        require(keySets["zh-CN"].SetEquals(keySets["en-US"]) && keySets["zh-CN"].SetEquals(keySets["ja-JP"]),
            "zh-CN, en-US, and ja-JP Resources.resw must maintain exact 3-way key parity.");
    }
}
