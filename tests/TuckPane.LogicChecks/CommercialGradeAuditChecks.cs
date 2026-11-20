using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class CommercialGradeAuditChecks
{
    internal static Task RunAsync()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        CheckBehaviorContracts(Require);
        CheckTrackAReliabilityAndAsyncIo(Require);
        CheckTrackBConsoleThemeAndSearch(Require);
        CheckTrackCMainWindow240HzAndLiquidGlass(Require);

        Console.WriteLine("CommercialGradeAuditChecks: all assertions passed.");
        return Task.CompletedTask;
    }

    private static void CheckBehaviorContracts(Action<bool, string> require)
    {
        // 1. Folder names containing dots must be preserved verbatim even when extensions are hidden (P0-7).
        require(StorageService.GetDisplayName("MyProject.Core", WidgetItemKind.Folder, showExtensions: false) == "MyProject.Core",
            "含小数点的文件夹名被错误截断（MyProject.Core）。");
        require(StorageService.GetDisplayName("v2.1.0", WidgetItemKind.Folder, showExtensions: false) == "v2.1.0",
            "含版本号的文件夹名被错误截断（v2.1.0）。");
        require(StorageService.GetDisplayName(".git", WidgetItemKind.Folder, showExtensions: false) == ".git",
            "以点开头的文件夹名被截断为空（.git）。");
        require(StorageService.GetDisplayName("App.lnk", WidgetItemKind.Shortcut, showExtensions: true) == "App",
            "快捷方式应继续隐藏 .lnk 扩展名。");

        // 2. DisplayPlacementService.Clamp must never throw on degenerate zero/inverted work areas (P0-6).
        var bigWindow = new NativeMethods.RECT { Left = 100, Top = 100, Right = 600, Bottom = 500 };
        var zeroWork = new NativeMethods.RECT { Left = 0, Top = 0, Right = 0, Bottom = 0 };
        NativeMethods.RECT clamped = DisplayPlacementService.Clamp(bigWindow, zeroWork);
        require(clamped.Width == bigWindow.Width && clamped.Height == bigWindow.Height,
            "零尺寸工作区下 Clamp 破坏了窗口尺寸或抛出异常。");

        // 3. Process-wide ref-counted CompositorClockBoost acquire/release must be safe & balanced.
        using (CompositorClockBoost.Acquire())
        using (CompositorClockBoost.Acquire())
        {
            // Nested leases must not throw and must release cleanly in LIFO order.
        }
        CompositorClockBoost.Release(); // Extra defensive release must be a safe no-op.
    }

    private static void CheckTrackAReliabilityAndAsyncIo(Action<bool, string> require)
    {
        string srcRoot = Path.Combine(RepoSourceSnapshot.RepoRoot(), "src", "TuckPane");

        // Dead code files must remain removed.
        require(!File.Exists(Path.Combine(srcRoot, "Services", "DesktopIconPlacementService.cs")),
            "僵尸文件 DesktopIconPlacementService.cs 不应残留。");
        require(!File.Exists(Path.Combine(srcRoot, "Services", "DesktopIconPlacementMath.cs")),
            "僵尸文件 DesktopIconPlacementMath.cs 不应残留。");

        // P0-1: NoteStore releases _gate in inner finally even if temp delete throws.
        string noteStore = File.ReadAllText(Path.Combine(srcRoot, "Services", "NoteStore.cs"));
        require(noteStore.Contains("catch (IOException)", StringComparison.Ordinal) &&
                noteStore.Contains("_gate.Release();", StringComparison.Ordinal),
            "NoteStore 缺少临时文件删除保护与 _gate 必释放守卫。");

        // P0-2: Hook services use try/catch inside OnMouse and persistent warm thread in OutsideClickHook.
        string marqueeHook = File.ReadAllText(Path.Combine(srcRoot, "Services", "DesktopMarqueeHookService.cs"));
        string outsideHook = File.ReadAllText(Path.Combine(srcRoot, "Services", "OutsideClickHook.cs"));
        require(marqueeHook.Contains("HookSession", StringComparison.Ordinal) &&
                outsideHook.Contains("_armed", StringComparison.Ordinal) &&
                outsideHook.Contains("HookSession", StringComparison.Ordinal),
            "鼠标钩子服务缺少安全会话生命周期或常驻热线程 _armed 控制。");

        // P0-3 & P0-5: IconCacheService uses ReleaseComObject on shared IImageList (never FinalReleaseComObject!) & offloads disk I/O.
        string iconCache = File.ReadAllText(Path.Combine(srcRoot, "Services", "IconCacheService.cs"));
        require(!iconCache.Contains("FinalReleaseComObject(imageList)", StringComparison.Ordinal) &&
                iconCache.Contains("Marshal.ReleaseComObject(imageList)", StringComparison.Ordinal) &&
                iconCache.Contains("Task.Run(", StringComparison.Ordinal),
            "IconCacheService 仍存在共享 IImageList FinalReleaseComObject 或未异步离屏化磁盘/PNG I/O。");

        // P0-5: StateStore offloads disk persistence via Task.Run while keeping WriteThrough durability.
        string stateStore = File.ReadAllText(Path.Combine(srcRoot, "Services", "StateStore.cs"));
        require(stateStore.Contains("Task.Run(", StringComparison.Ordinal) &&
                stateStore.Contains("FileOptions.WriteThrough", StringComparison.Ordinal),
            "StateStore 未在保留 WriteThrough 持久性的同时将磁盘落盘异步离屏化。");

        // P0-4: Multi-path clipboard helper exists.
        string shellCmds = File.ReadAllText(Path.Combine(srcRoot, "Services", "ShellItemCommands.cs"));
        require(shellCmds.Contains("CopyPathsToClipboard(", StringComparison.Ordinal),
            "ShellItemCommands 缺少安全的批量路径复制方法 CopyPathsToClipboard。");

        // P1-8: IShellLinkW uses explicit LPWStr marshaling, TrayIconService posts WM_NULL, DesktopGridService passes SMTO_ABORTIFHUNG.
        string shellCom = File.ReadAllText(Path.Combine(srcRoot, "Services", "NativeMethods.ShellCom.cs"));
        string tray = File.ReadAllText(Path.Combine(srcRoot, "Services", "TrayIconService.cs"));
        string grid = File.ReadAllText(Path.Combine(srcRoot, "Services", "DesktopGridService.cs"));
        require(shellCom.Contains("[MarshalAs(UnmanagedType.LPWStr)]", StringComparison.Ordinal),
            "IShellLinkW COM 接口缺少显式 LPWStr 封送标注。");
        require(tray.Contains("WM_NULL", StringComparison.Ordinal),
            "TrayIconService.ShowMenu 缺少必需的 PostMessage(WM_NULL)。");
        require(grid.Contains("SMTO_ABORTIFHUNG", StringComparison.Ordinal),
            "DesktopGridService.SendMessageTimeout 缺少 SMTO_ABORTIFHUNG 防卡死标志。");
    }

    private static void CheckTrackBConsoleThemeAndSearch(Action<bool, string> require)
    {
        string srcRoot = Path.Combine(RepoSourceSnapshot.RepoRoot(), "src", "TuckPane");
        string backdrop = File.ReadAllText(Path.Combine(srcRoot, "Services", "ThemeBackdrop.cs"));
        string consoleCs = File.ReadAllText(Path.Combine(srcRoot, "ConsoleWindow.xaml.cs"));
        string consoleXaml = File.ReadAllText(Path.Combine(srcRoot, "ConsoleWindow.xaml"));
        string sliderCs = File.ReadAllText(Path.Combine(srcRoot, "Controls", "ConsoleSlider.cs"));
        string searchCs = File.ReadAllText(Path.Combine(srcRoot, "Services", "OrganizerSearchWindow.cs"));
        string appHostCs = File.ReadAllText(Path.Combine(srcRoot, "AppHost.cs"));

        // P0-1: ThemeBackdrop caches animatable effect factory and updates shader properties in-place without recompiling.
        require(backdrop.Contains("Blur.BlurAmount", StringComparison.Ordinal) &&
                backdrop.Contains("TintOpacity.Opacity", StringComparison.Ordinal) &&
                backdrop.Contains("TintColor.Color", StringComparison.Ordinal),
            "ThemeBackdrop 未启用 animatableProperties 就地更新着色器参数。");

        // P0-3: ConsoleWindow replaces Task.Delay(16) page animation with GPU Compositor spring + removes ViewportWidth bindings.
        int showPageStart = consoleCs.IndexOf("private Task ShowPageAsync", StringComparison.Ordinal);
        if (showPageStart < 0) showPageStart = consoleCs.IndexOf("private async Task ShowPageAsync", StringComparison.Ordinal);
        require(showPageStart > 0, "ConsoleWindow.xaml.cs 中找不到 ShowPageAsync。");
        string showPageWindow = consoleCs.Substring(showPageStart, Math.Min(2500, consoleCs.Length - showPageStart));
        require(!showPageWindow.Contains("Task.Delay(16", StringComparison.Ordinal) &&
                showPageWindow.Contains("CreateSpringVector3Animation", StringComparison.Ordinal) &&
                showPageWindow.Contains("CompositorClockBoost", StringComparison.Ordinal),
            "ConsoleWindow.ShowPageAsync 仍残留 Task.Delay(16) 循环或未接入 GPU 弹簧与高刷提频。");

        require(!consoleXaml.Contains("Binding ViewportWidth", StringComparison.Ordinal),
            "ConsoleWindow.xaml 仍残留导致滚动重排的 {Binding ViewportWidth}。");

        require(appHostCs.Contains("LastChangedThemeTarget", StringComparison.Ordinal) &&
                consoleCs.Contains("LastChangedThemeTarget", StringComparison.Ordinal),
            "AppHost / ConsoleWindow 缺少目标隔离的主题广播机制。");

        int thumbPaletteStart = sliderCs.IndexOf("SetThumbPalette(", StringComparison.Ordinal);
        require(thumbPaletteStart > 0 &&
                !sliderCs[thumbPaletteStart..].Contains("new SolidColorBrush(", StringComparison.Ordinal),
            "ConsoleSlider.SetThumbPalette 仍在每次主题遍历时分配 SolidColorBrush。");

        // P0.4: Raycast-grade OrganizerSearchWindow.
        require(searchCs.Contains("DesktopAcrylicBackdrop", StringComparison.Ordinal) &&
                searchCs.Contains("DWMWCP_ROUND", StringComparison.Ordinal) &&
                searchCs.Contains("SharedIconCache.GetIconAsync", StringComparison.Ordinal),
            "OrganizerSearchWindow 未完成亚克力圆角与真实缓存 Shell 图标升级。");
    }

    private static void CheckTrackCMainWindow240HzAndLiquidGlass(Action<bool, string> require)
    {
        string srcRoot = Path.Combine(RepoSourceSnapshot.RepoRoot(), "src", "TuckPane");
        string transCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.TransitionsAndResize.cs"));
        string mainXaml = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.xaml"));
        string mainCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.xaml.cs"));
        string styleCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.NativeDesktopStyle.cs"));
        string reorderCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.FreeGridReorder.cs"));
        string selectionCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.ItemSelection.cs"));

        // P0-2: Single-VSync persistent CompositionTarget.Rendering + analytical iOS spring + ref-counted CompositorClockBoost.
        int runVisStart = transCs.IndexOf("private async Task RunVisualTransitionAsync", StringComparison.Ordinal);
        if (runVisStart < 0) runVisStart = transCs.IndexOf("private Task RunVisualTransitionAsync", StringComparison.Ordinal);
        int applyFrameStart = transCs.IndexOf("private void ApplyTransitionFrame", StringComparison.Ordinal);
        require(runVisStart > 0 && applyFrameStart > runVisStart, "找不到 RunVisualTransitionAsync。");
        string runVisBody = transCs[runVisStart..applyFrameStart];
        require(!runVisBody.Contains("while (true)", StringComparison.Ordinal) &&
                runVisBody.Contains("CompositionTarget.Rendering += OnRendering;", StringComparison.Ordinal) &&
                runVisBody.Contains("StepScalarSpring", StringComparison.Ordinal) &&
                runVisBody.Contains("CompositorClockBoost.Acquire()", StringComparison.Ordinal),
            "RunVisualTransitionAsync 未迁移为单订阅同步 VSync + 解析 iOS 弹簧 + 引用计数高刷提频。");

        // P0.1 & P0.2: GPU shadows on expanded title & item names + rounded tile drop shadow mask.
        require(mainXaml.Contains("x:Name=\"ExpandedNameShadowHost\"", StringComparison.Ordinal) &&
                mainXaml.Contains("x:Name=\"ItemNameShadowHost\"", StringComparison.Ordinal) &&
                mainXaml.Contains("x:Name=\"CompactEmptyGlyph\"", StringComparison.Ordinal),
            "MainWindow.xaml 缺少展开标题阴影、项目标签阴影或空收纳窗引导图标。");
        require(styleCs.Contains("_compactTileShadowMaskBrush", StringComparison.Ordinal) ||
                styleCs.Contains("CreateVisualSurface()", StringComparison.Ordinal),
            "MainWindow.NativeDesktopStyle.cs 紧凑卡片阴影缺少圆角 Alpha 遮罩。");
        require(styleCs.Contains("CreateImplicitAnimationCollection()", StringComparison.Ordinal) &&
                styleCs.Contains("_tileScaleSpring", StringComparison.Ordinal),
            "MainWindow.NativeDesktopStyle.cs 未启用 GPU 隐式光晕动画或弹簧实例复用。");

        // P0-4 & P1-1: FreeGridDropSlotGhost uses GPU Translation (no per-frame Canvas.SetLeft/Top), batch CopyPaths fixed.
        int ghostStepStart = reorderCs.IndexOf("private void StepFreeGridDropGhostSpring", StringComparison.Ordinal);
        int recordDropStart = reorderCs.IndexOf("private void RecordExternalDropSlotFromEvent", StringComparison.Ordinal);
        require(ghostStepStart > 0 && recordDropStart > ghostStepStart, "找不到 StepFreeGridDropGhostSpring。");
        string ghostStepBody = reorderCs[ghostStepStart..recordDropStart];
        require(!ghostStepBody.Contains("Canvas.SetLeft(", StringComparison.Ordinal) &&
                !ghostStepBody.Contains("Canvas.SetTop(", StringComparison.Ordinal) &&
                ghostStepBody.Contains(".Translation", StringComparison.Ordinal),
            "StepFreeGridDropGhostSpring 仍在 120Hz 热路径调用 Canvas.SetLeft/Top。");

        require(selectionCs.Contains("CopyPathsToClipboard(", StringComparison.Ordinal),
            "MainWindow.ItemSelection.cs 批量复制路径未改用 CopyPathsToClipboard。");

        // P1-3: MainWindow eliminates VisitTextBlocks tree walk and checks LastChangedThemeTarget.
        require(!mainCs.Contains("VisitTextBlocks(", StringComparison.Ordinal) &&
                mainCs.Contains("LastChangedThemeTarget", StringComparison.Ordinal) &&
                mainCs.Contains("ItemHostParts", StringComparison.Ordinal),
            "MainWindow.xaml.cs 未移除多余 VisitTextBlocks 遍历、未缓存 ItemHostParts 或未接入目标隔离主题广播。");
    }
}
