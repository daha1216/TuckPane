using TuckPane.Core;
using TuckPane.Services;
using Microsoft.UI.Text;

internal static class LiquidGlassAndPerfChecks
{
    internal static Task RunAsync()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        CheckDesktopTypographyService(Require);
        CheckDedicatedHookThreadArchitecture(Require);
        CheckLiquidGlassAndNativeDesktopContract(Require);
        CheckZeroStutterReorderAndSelectionPerf(Require);
        CheckV432RetinaMenuAndSettingsContracts(Require);

        Console.WriteLine("LiquidGlassAndPerfChecks: all assertions passed.");
        return Task.CompletedTask;
    }

    private static void CheckDesktopTypographyService(Action<bool, string> require)
    {
        DesktopTypographyService.InvalidateCache();
        DesktopTypographyMetrics sys = DesktopTypographyService.ReadSystemDesktopTypography();
        require(!string.IsNullOrWhiteSpace(sys.FontFamilyName), "系统桌面字体名称读取为空。");
        require(sys.IconFontSizeDip is >= 11.5 and <= 14.0,
            $"系统桌面字号超出预期区间：{sys.IconFontSizeDip} DIP。");
        require(sys.FontWeight is >= 100 and <= 900,
            $"系统桌面字重超出合法范围：{sys.FontWeight}。");

        // Even at huge compact tile zoom (156% / 197%), native blend labels must stay authentic ~12 DIP Normal
        // and NEVER inflate into giant 20px billboard letters!
        var (native156Size, native156Weight) = DesktopTypographyService.ResolveCompactLabelTypography(
            CompactVisualStyle.NativeDesktop, compactScale: 1.56, compactNameScale: 1.0, positioned: false);
        require(native156Size is >= 11.5 and <= 14.0 && native156Weight.Weight == FontWeights.Normal.Weight,
            $"NativeDesktop 156% 字号/字重异常：{native156Size} / {native156Weight.Weight}。");

        var (native197Size, native197Weight) = DesktopTypographyService.ResolveCompactLabelTypography(
            CompactVisualStyle.NativeDesktop, compactScale: 1.97, compactNameScale: 1.0, positioned: false);
        require(Math.Abs(native197Size - native156Size) < 0.01 && native197Weight.Weight == FontWeights.Normal.Weight,
            $"NativeDesktop 随入口缩放膨胀：156%={native156Size}, 197%={native197Size}。");

        var (folderSize, folderWeight) = DesktopTypographyService.ResolveCompactLabelTypography(
            CompactVisualStyle.FluentFolder, compactScale: 1.56, compactNameScale: 1.0, positioned: false);
        require(folderSize is >= 11.5 and <= 14.0 && folderWeight.Weight == FontWeights.Normal.Weight,
            $"FluentFolder 字号/字重异常：{folderSize} / {folderWeight.Weight}。");

        var (glass156Size, glass156Weight) = DesktopTypographyService.ResolveCompactLabelTypography(
            CompactVisualStyle.GlassTile, compactScale: 1.56, compactNameScale: 1.0, positioned: false);
        require(glass156Size is >= 12.0 and <= 14.0 && glass156Weight.Weight == FontWeights.Normal.Weight,
            $"GlassTile 156% 字号/字重异常：{glass156Size} / {glass156Weight.Weight}。");

        var (glass200Size, _) = DesktopTypographyService.ResolveCompactLabelTypography(
            CompactVisualStyle.GlassTile, compactScale: 2.0, compactNameScale: 1.2, positioned: false);
        require(glass200Size <= 14.0, $"GlassTile 放大上限保护失效：{glass200Size} DIP。");

        // CompactNameScale slider (60%-100%) must smoothly shrink GlassTile & NativeDesktop labels instead of locking at 12 DIP.
        var (glass60Size, _) = DesktopTypographyService.ResolveCompactLabelTypography(
            CompactVisualStyle.GlassTile, compactScale: 1.0, compactNameScale: 0.6, positioned: false);
        require(glass60Size >= 8.0 && glass60Size < 10.0 && glass60Size < glass156Size - 2.0,
            $"GlassTile 收起名称缩放 60% 未生效：{glass60Size} DIP（100%={glass156Size} DIP）。");

        var (native60Size, _) = DesktopTypographyService.ResolveCompactLabelTypography(
            CompactVisualStyle.NativeDesktop, compactScale: 1.0, compactNameScale: 0.6, positioned: false);
        require(native60Size >= 7.5 && native60Size < native156Size - 2.0,
            $"NativeDesktop 收起名称缩放 60% 未生效：{native60Size} DIP（100%={native156Size} DIP）。");
    }

    private static void CheckDedicatedHookThreadArchitecture(Action<bool, string> require)
    {
        string srcRoot = Path.Combine(RepoSourceSnapshot.RepoRoot(), "src", "TuckPane");
        string marqueeHook = File.ReadAllText(Path.Combine(srcRoot, "Services", "DesktopMarqueeHookService.cs"));
        string outsideHook = File.ReadAllText(Path.Combine(srcRoot, "Services", "OutsideClickHook.cs"));

        foreach ((string name, string code) in new[]
        {
            ("DesktopMarqueeHookService", marqueeHook),
            ("OutsideClickHook", outsideHook)
        })
        {
            require(code.Contains("ThreadPriority.AboveNormal", StringComparison.Ordinal) &&
                    code.Contains("NativeMethods.GetMessage(", StringComparison.Ordinal) &&
                    code.Contains("NativeMethods.PeekMessage(", StringComparison.Ordinal) &&
                    code.Contains("NativeMethods.PostThreadMessage(", StringComparison.Ordinal) &&
                    code.Contains("WM_QUIT", StringComparison.Ordinal),
                $"{name} 必须在独立高优先级原生消息泵线程上运行 WH_MOUSE_LL，严禁挂载到 WinUI 主 UI 线程。");
        }

        require(marqueeHook.Contains("if (!_trackingDesktopPress)", StringComparison.Ordinal),
            "DesktopMarqueeHookService 缺少非拖拽状态下的极速短路分支。");
    }

    private static void CheckLiquidGlassAndNativeDesktopContract(Action<bool, string> require)
    {
        string srcRoot = Path.Combine(RepoSourceSnapshot.RepoRoot(), "src", "TuckPane");
        string mainXaml = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.xaml"));
        string mainCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.xaml.cs"));
        string styleCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.NativeDesktopStyle.cs"));
        string transCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.TransitionsAndResize.cs"));

        require(mainXaml.Contains("x:Name=\"CompactShadowHost\"", StringComparison.Ordinal) &&
                mainXaml.Contains("x:Name=\"CompactLiquidSheen\"", StringComparison.Ordinal) &&
                mainXaml.Contains("x:Name=\"CompactLiquidInnerRim\"", StringComparison.Ordinal) &&
                mainXaml.Contains("x:Name=\"CompactNativeBadge\"", StringComparison.Ordinal) &&
                mainXaml.Contains("x:Name=\"CompactNativeInnerRim\"", StringComparison.Ordinal) &&
                mainXaml.Contains("x:Name=\"CompactSpecularOverlay\"", StringComparison.Ordinal) &&
                mainXaml.Contains("x:Name=\"CompactNameShadowHost\"", StringComparison.Ordinal) &&
                mainXaml.Contains("x:Name=\"ExpandedLiquidSheen\"", StringComparison.Ordinal) &&
                mainXaml.Contains("x:Name=\"ExpandedLiquidInnerRim\"", StringComparison.Ordinal),
            "MainWindow.xaml 缺少 iOS 26 液态玻璃光学折射层、原生桌面胶囊或 GPU 高斯阴影宿主节点。");

        require(mainXaml.Contains("FontFamily=\"Microsoft YaHei UI, Segoe UI Variable Text\"", StringComparison.Ordinal),
            "MainWindow.xaml 紧凑/展开项目标签未对齐 Windows 11 原生中文字体栈。");

        require(styleCs.Contains("CompactSurfaceHost.Visibility = hideBackdrop ? Visibility.Collapsed : Visibility.Visible;", StringComparison.Ordinal),
            "NativeDesktop 模式未折叠隐藏 CompactSurfaceHost，会导致无用 DWM Acrylic 实时模糊持续消耗 GPU。");

        require(styleCs.Contains("CompactNameText.GetAlphaMask()", StringComparison.Ordinal) &&
                styleCs.Contains("_compactNameDropShadowAura", StringComparison.Ordinal) &&
                styleCs.Contains("CreateSpringVector3Animation()", StringComparison.Ordinal) &&
                styleCs.Contains("CreateRadialGradientBrush()", StringComparison.Ordinal) &&
                styleCs.Contains("UpdateCompactLabelSafeWidth()", StringComparison.Ordinal),
            "MainWindow.NativeDesktopStyle.cs 缺少双层 GPU 高斯文字阴影、DWM 弹簧动画、实时光斑或屏幕边缘防裁切逻辑。");

        // Ensure zero per-hover / per-style SolidColorBrush or AccessibilitySettings allocations in style code.
        int applyMethodStart = styleCs.IndexOf("internal void ApplyCompactVisualStyle()", StringComparison.Ordinal);
        require(applyMethodStart > 0, "找不到 ApplyCompactVisualStyle 方法。");
        string styleHotPaths = styleCs[applyMethodStart..];
        require(!styleHotPaths.Contains("new SolidColorBrush(", StringComparison.Ordinal),
            "MainWindow.NativeDesktopStyle.cs 热路径中存在 new SolidColorBrush 分配。");
        require(!styleHotPaths.Contains("new AccessibilitySettings()", StringComparison.Ordinal),
            "MainWindow.NativeDesktopStyle.cs 热路径中存在 new AccessibilitySettings 分配。");

        require(mainCs.Contains("CompactCornerRadiusDip = 9.5;", StringComparison.Ordinal) &&
                mainCs.Contains("DesktopTypographyService.IsHighContrast()", StringComparison.Ordinal) &&
                mainCs.Contains("DisposeCompactVisualResources();", StringComparison.Ordinal) &&
                !mainCs.Contains("new Windows.UI.ViewManagement.AccessibilitySettings()", StringComparison.Ordinal),
            "MainWindow.xaml.cs 未完成 24.4% iOS 曲率、缓存高对比度检测或 GPU 视觉资源清理接入。");

        require(transCs.Contains("UpdateCompactSpecularPointer(e);", StringComparison.Ordinal) &&
                transCs.Contains("SetCompactPressVisualState(true);", StringComparison.Ordinal) &&
                transCs.Contains("SetCompactPressVisualState(false);", StringComparison.Ordinal) &&
                transCs.Contains("DesktopTypographyService.InvalidateCache();", StringComparison.Ordinal),
            "MainWindow.TransitionsAndResize.cs 未完整接入实时光标高光、弹簧按压回弹或系统设置热更新。");
    }

    private static void CheckZeroStutterReorderAndSelectionPerf(Action<bool, string> require)
    {
        string srcRoot = Path.Combine(RepoSourceSnapshot.RepoRoot(), "src", "TuckPane");
        string reorderCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.FreeGridReorder.cs"));
        string selectionCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.ItemSelection.cs"));

        require(!reorderCs.Contains("new ThemeShadow()", StringComparison.Ordinal),
            "MainWindow.FreeGridReorder.cs 仍残留高开销 ThemeShadow，会导致 120Hz 拖拽掉帧。");
        require(reorderCs.Contains("_dragHostIndexLookup", StringComparison.Ordinal) &&
                reorderCs.Contains(".Rotation =", StringComparison.Ordinal) &&
                !reorderCs.Contains("GetElementVisual(", StringComparison.Ordinal) &&
                reorderCs.Contains("GuideCellStrokeBrush", StringComparison.Ordinal),
            "MainWindow.FreeGridReorder.cs 未启用 O(1) 索引查表、安全 UIElement.Rotation（严禁混用 GetElementVisual 引发 0x80070005）或静态预分配画刷。");

        int stepStart = reorderCs.IndexOf("private bool StepIosFluidItemSprings", StringComparison.Ordinal);
        int commitStart = reorderCs.IndexOf("private async Task CommitIosFluidReorderAsync", StringComparison.Ordinal);
        require(stepStart > 0 && commitStart > stepStart, "找不到 StepIosFluidItemSprings 方法。");
        string stepBody = reorderCs[stepStart..commitStart];
        require(!stepBody.Contains("IndexOfItem(", StringComparison.Ordinal),
            "StepIosFluidItemSprings 仍存在 O(N^2) IndexOfItem 扫描。");
        require(!stepBody.Contains("new HashSet<int>", StringComparison.Ordinal),
            "StepIosFluidItemSprings 每帧仍在堆上分配 HashSet<int>。");

        int buildGuideStart = reorderCs.IndexOf("private void BuildFreeGridGuideCells", StringComparison.Ordinal);
        require(buildGuideStart > 0, "找不到 BuildFreeGridGuideCells 方法。");
        string guideAndGhost = reorderCs[buildGuideStart..];
        require(!guideAndGhost.Contains("new SolidColorBrush(", StringComparison.Ordinal),
            "自由网格引导格/磁吸占位框仍存在每帧/每次拖拽 SolidColorBrush 分配。");

        int applySelStart = selectionCs.IndexOf("internal void ApplyItemSelectionVisual", StringComparison.Ordinal);
        int clearSelStart = selectionCs.IndexOf("private void SelectionClearButton_Click", StringComparison.Ordinal);
        require(applySelStart > 0 && clearSelStart > applySelStart, "找不到 ApplyItemSelectionVisual 方法。");
        require(!selectionCs[applySelStart..clearSelStart].Contains("new SolidColorBrush(", StringComparison.Ordinal),
            "MainWindow.ItemSelection.cs 的 ApplyItemSelectionVisual 仍存在逐项画刷分配。");

        // 7. Panel-aligned 8-direction resize hit zones (56 DIP title band + 36 DIP rounded corners)
        require(CanvasResizeHitZones.HitTestPanel(200, 10, 400, 456, compactList: false, panelTopDip: 56) == CanvasResizeEdge.None,
            "标题带上方透明空气不应触发顶部拉伸热区。");
        require(CanvasResizeHitZones.HitTestPanel(200, 56, 400, 456, compactList: false, panelTopDip: 56) == CanvasResizeEdge.Top,
            "玻璃面板可见顶边（y=56）必须命中 Top 拉伸热区。");
        require(CanvasResizeHitZones.HitTestPanel(200, 48, 400, 456, compactList: false, panelTopDip: 56) == CanvasResizeEdge.Top,
            "玻璃面板可见顶边外沿（y=48）应支持轻松命中 Top。");
        require(CanvasResizeHitZones.HitTestPanel(200, 450, 400, 456, compactList: false, panelTopDip: 56) == CanvasResizeEdge.Bottom,
            "玻璃面板底边必须命中 Bottom 拉伸热区。");
        require(CanvasResizeHitZones.HitTestPanel(24, 56 + 24, 400, 456, compactList: false, panelTopDip: 56) == (CanvasResizeEdge.Left | CanvasResizeEdge.Top),
            "左上 36 DIP 圆角弧段必须命中 Left|Top 双向拉伸。");
        require(CanvasResizeHitZones.HitTestPanel(376, 56 + 24, 400, 456, compactList: false, panelTopDip: 56) == (CanvasResizeEdge.Right | CanvasResizeEdge.Top),
            "右上 36 DIP 圆角弧段必须命中 Right|Top 双向拉伸。");
        require(CanvasResizeHitZones.HitTestPanel(24, 456 - 24, 400, 456, compactList: false, panelTopDip: 56) == (CanvasResizeEdge.Left | CanvasResizeEdge.Bottom),
            "左下 36 DIP 圆角弧段必须命中 Left|Bottom 双向拉伸。");
        require(CanvasResizeHitZones.HitTestPanel(376, 456 - 24, 400, 456, compactList: false, panelTopDip: 56) == (CanvasResizeEdge.Right | CanvasResizeEdge.Bottom),
            "右下 36 DIP 圆角弧段必须命中 Right|Bottom 双向拉伸。");
        require(CanvasResizeHitZones.PanelEdgeRectangles(800, 912, 1.5, compactList: false, panelTopPx: 84).Length == 8,
            "图标模式原生拉伸子窗口应覆盖四边 + 四圆角内弧（共 8 块）。");

        // 8. Opposite-edge anchored resize & lifted consistent full-work-area max scale
        var liftedRange = OrganizerContentScale.FactorRange(400, 400, 200, 200, 1200, 1200, 1.0, 1.0);
        require(liftedRange.Max >= 2.99,
            $"大显示器工作区下最大缩放系数不应被 1.65 锁死，实际 Max={liftedRange.Max}。");
        var workPanel = new TuckPane.Services.NativeMethods.RECT { Left = 0, Top = 56, Right = 2560, Bottom = 1400 };
        var startRightEdge = new TuckPane.Services.NativeMethods.RECT { Left = 1900, Top = 300, Right = 2500, Bottom = 900 };
        double leftPullFactor = OrganizerInteractionMath.CalculateAnchoredResizeFactor(CanvasResizeEdge.Left, -600, 0, 600, 600);
        require(Math.Abs(leftPullFactor - 2.0) < 0.001, "左拖拉宽 600px 应精确 1:1 跟踪为 2.0x。");
        var anchoredLeftPull = OrganizerInteractionMath.CreateAnchoredBounds(
            CanvasResizeEdge.Left, startRightEdge, workPanel, 1200, 1200);
        require(anchoredLeftPull.Right == startRightEdge.Right && anchoredLeftPull.Width == 1200,
            "右边缘收纳窗向左拉伸时，右边必须钉死不动且可跨越整屏自由放大。");

        // 9. Zero-flash atomic transition bridge & zero-twitch resize source invariants
        string resizeCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.CanvasResize.cs"));
        string alignCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.Alignment.cs"));
        string transitionsCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.TransitionsAndResize.cs"));
        require(alignCs.Contains("SWP_NOCOPYBITS", StringComparison.Ordinal),
            "ApplyCanvasResizeBounds 必须携带 SWP_NOCOPYBITS 防止左/上边缘移动时 GDI BitBlt 撕裂抽搐。");
        require(resizeCs.Contains("CompositionTarget.Rendering += OnCanvasResizeRendering", StringComparison.Ordinal) &&
                resizeCs.Contains("BeginCanvasResizeGpuScaling", StringComparison.Ordinal),
            "画布拉伸必须由单 VSync CompositionTarget.Rendering + GPU 缩放驱动。");
        require(transitionsCs.Contains("TransitionBridgeOverlay", StringComparison.Ordinal) &&
                transitionsCs.Contains("CloakForAtomicRelocation", StringComparison.Ordinal) &&
                transitionsCs.Contains("CommitCompactHandoffAsync", StringComparison.Ordinal),
            "展开/收起必须通过 TransitionBridgeOverlay + DWMWA_CLOAK 实现零瞬移、零闪烁原子交接。");
    }

    private static void CheckV432RetinaMenuAndSettingsContracts(Action<bool, string> require)
    {
        // 1. Retina supersampled compact mini-icons
        require(IconCacheService.NormalizeCompactPreviewPixelSize(0) == 12 &&
                IconCacheService.NormalizeCompactPreviewPixelSize(40) == 40 &&
                IconCacheService.NormalizeCompactPreviewPixelSize(512) == 128,
            "IconCacheService.NormalizeCompactPreviewPixelSize 物理像素夹紧区间异常。");

        string srcRoot = Path.Combine(RepoSourceSnapshot.RepoRoot(), "src", "TuckPane");
        string iconCacheCs = File.ReadAllText(Path.Combine(srcRoot, "Services", "IconCacheService.cs"));
        string mainCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.xaml.cs"));
        string transCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.TransitionsAndResize.cs"));
        string mainXaml = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.xaml"));
        string menuCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.OrganizerMenu.cs"));
        string catalogCs = File.ReadAllText(Path.Combine(srcRoot, "OrganizerCatalogController.cs"));
        string styleCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.NativeDesktopStyle.cs"));
        string hostDesktopCs = File.ReadAllText(Path.Combine(srcRoot, "AppHost.NativeDesktop.cs"));
        string consoleXaml = File.ReadAllText(Path.Combine(srcRoot, "ConsoleWindow.xaml"));
        string formXaml = File.ReadAllText(Path.Combine(srcRoot, "Controls", "OrganizerSettingsForm.xaml"));

        require(iconCacheCs.Contains("GetCompactPreviewIconAsync(", StringComparison.Ordinal) &&
                iconCacheCs.Contains("BitmapInterpolationMode.Fant", StringComparison.Ordinal) &&
                iconCacheCs.Contains("DecodePixelType.Physical", StringComparison.Ordinal) &&
                iconCacheCs.Contains("ApplyAlphaSafeMicroUnsharpMask(", StringComparison.Ordinal),
            "IconCacheService 缺少 Fant 面积超采样、Alpha 安全微锐化或 1:1 物理像素解码链路。");

        require(mainCs.Contains("EnsureCompactPreviewReadyAsync(", StringComparison.Ordinal) &&
                mainCs.Contains("IsCompactPreviewUpToDate(", StringComparison.Ordinal) &&
                mainCs.Contains("BuildCompactPreviewSignature(", StringComparison.Ordinal),
            "MainWindow.xaml.cs 缺少紧凑预览零重载守卫或签名比对，收起后可能异步覆盖导致闪烁。");

        // 2. Post-enlarge collapse zero-flash dead-zone & pre-cloak preview sync
        require(transCs.Contains("EnsureCompactPreviewReadyAsync(", StringComparison.Ordinal) &&
                transCs.Contains("GetPixelSnappedCompactTranslation(", StringComparison.Ordinal) &&
                transCs.Contains("0.06", StringComparison.Ordinal),
            "MainWindow.TransitionsAndResize.cs 缺少收起前预览同步、物理像素对齐或 0.06 收起静定死区。");

        // 3. Expanded context menu reliability, outside-click pause & floating Toast HUD
        require(mainXaml.Contains("x:Name=\"ExpandedToastPill\"", StringComparison.Ordinal) &&
                menuCs.Contains("PauseOutsideClickForOverlay(", StringComparison.Ordinal) &&
                menuCs.Contains("ResumeOutsideClickAfterOverlay(", StringComparison.Ordinal) &&
                menuCs.Contains("ShowExpandedToast(", StringComparison.Ordinal),
            "展开态右键菜单缺少外部点击钩子暂停保护或浮动 Toast 反馈胶囊。");

        require(catalogCs.Contains("YieldForMenuDismissAsync(", StringComparison.Ordinal) &&
                catalogCs.Contains("IsDialogActive", StringComparison.Ordinal),
            "OrganizerCatalogController 缺少菜单关闭后安全让步或模态框激活态保护。");

        int alignMethodStart = hostDesktopCs.IndexOf("AlignFloatingToDesktopGridAsync(", StringComparison.Ordinal);
        require(alignMethodStart > 0, "找不到 AlignFloatingToDesktopGridAsync 方法。");
        string alignBody = hostDesktopCs[alignMethodStart..Math.Min(hostDesktopCs.Length, alignMethodStart + 1400)];
        require(!alignBody.Contains("IsEligibleForDesktopMultiSelect", StringComparison.Ordinal),
            "AlignFloatingToDesktopGridAsync 不应再跳过展开中的悬浮窗。");

        // 4. Settings EdgeGlow wiring & Swiss-grid layout spine
        require(styleCs.Contains("EdgeGlowEnabled", StringComparison.Ordinal) &&
                styleCs.Contains("ExpandedLiquidSheen", StringComparison.Ordinal) &&
                consoleXaml.Contains("Width=\"228\"", StringComparison.Ordinal) &&
                formXaml.Contains("Width=\"208\"", StringComparison.Ordinal),
            "设置页缺少边缘弧光开关实时联动或 Swiss-Grid 右列对齐规格。");

        // 5. Zero-overlap submenu templates & Windows Explorer StrCmpLogicalW natural mixed sorting
        require(mainXaml.Contains("x:Key=\"OrganizerMenuItemStyle\"", StringComparison.Ordinal) &&
                mainXaml.Contains("x:Key=\"OrganizerMenuSubItemStyle\"", StringComparison.Ordinal),
            "MainWindow.xaml 缺少防止子菜单图标与文字重叠的显式列布局样式。");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var mixedGameShortcuts = new SortableItemMetadata[]
        {
            new("鸣潮.lnk", "鸣潮", TuckPane.Models.WidgetItemKind.Shortcut, ".lnk", now, 1551),
            new("三角洲行动.lnk", "三角洲行动", TuckPane.Models.WidgetItemKind.Shortcut, ".lnk", now, 848),
            new("NVIDIA App.lnk", "NVIDIA App", TuckPane.Models.WidgetItemKind.Shortcut, ".lnk", now, 1430),
            new("Steam.lnk", "Steam", TuckPane.Models.WidgetItemKind.Shortcut, ".lnk", now, 550),
            new("UU加速器.lnk", "UU加速器", TuckPane.Models.WidgetItemKind.Shortcut, ".lnk", now, 499),
            new("Wallpaper Engine.url", "Wallpaper Engine", TuckPane.Models.WidgetItemKind.InternetShortcut, ".url", now, 202)
        };
        IReadOnlyList<string> explorerSorted = OrganizerSortOrder.Sort(mixedGameShortcuts, OrganizerSortCriterion.NameAscending);
        require(explorerSorted[0] == "NVIDIA App.lnk" &&
                explorerSorted[1] == "Steam.lnk" &&
                explorerSorted[2] == "UU加速器.lnk" &&
                explorerSorted[3] == "Wallpaper Engine.url" &&
                explorerSorted[4] == "鸣潮.lnk" &&
                explorerSorted[5] == "三角洲行动.lnk",
            $"按名称（A→Z）未遵循 Windows 资源管理器 StrCmpLogicalW 中英自然顺序：{string.Join(" -> ", explorerSorted)}");

        // 6. Optical Liquid Glass Edge Rim & Pre-Uncloak DComp Commit & Element-Identity FreeGrid Slot Resolution
        string edgeSurfaceCs = File.ReadAllText(Path.Combine(srcRoot, "Services", "ThemeEdgeSurface.cs"));
        string freeGridLayoutCs = File.ReadAllText(Path.Combine(srcRoot, "Controls", "FreeSlotGridLayout.cs"));
        string freeGridReorderCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.FreeGridReorder.cs"));
        require(edgeSurfaceCs.Contains("UpdatePointerRim(", StringComparison.Ordinal) &&
                edgeSurfaceCs.Contains("ClearPointerRim(", StringComparison.Ordinal) &&
                edgeSurfaceCs.Contains("_counterArcShape", StringComparison.Ordinal) &&
                edgeSurfaceCs.Contains("_pointerRimVisual", StringComparison.Ordinal) &&
                edgeSurfaceCs.Contains("GlassEdgePureBaseStops", StringComparison.Ordinal),
            "ThemeEdgeSurface 缺少纯晶白零泛黑基底、双极菲涅尔圆角焦散弧或 120Hz 鼠标跟随流光边缘。");

        require(!mainXaml.Contains("#14000000", StringComparison.OrdinalIgnoreCase) &&
                !mainXaml.Contains("#22000000", StringComparison.OrdinalIgnoreCase),
            "MainWindow.xaml 的 CompactLiquidSheen 仍残留会导致边缘泛黑的黑色渐变色标。");

        require(freeGridLayoutCs.Contains("ElementSlotResolver", StringComparison.Ordinal) &&
                freeGridReorderCs.Contains("_resolvedSlotByRelativeName", StringComparison.Ordinal) &&
                freeGridReorderCs.Contains("RebuildResolvedSlotLookup(", StringComparison.Ordinal) &&
                freeGridReorderCs.Contains("ResolveFreeGridElementSlotForLayout", StringComparison.Ordinal),
            "FreeSlotGridLayout 缺少按 UIElement 真实身份（RelativeName）解析槽位的机制，排序或筛选后拖拽会导致错位乱跳。");

        require(mainCs.Contains("NativeMethods.SWP_NOCOPYBITS", StringComparison.Ordinal) &&
                transCs.Contains("NativeMethods.DwmFlush()", StringComparison.Ordinal) &&
                styleCs.Contains("ResetCompactHoverSpringToRestIfIdle(", StringComparison.Ordinal),
            "悬停展开缺少 SWP_NOCOPYBITS、DwmFlush 预提交护航或紧凑态悬停弹簧防抖锁。");

        // 7. End-to-end 46-item polish contracts (ACP .lnk resolution, straight-alpha icon fix,
        //    8-dir resize layout sync, zero-leak hover reset, clamped spring, Todo Enter commit, 2D arrow nav)
        string sortOrderCs = File.ReadAllText(Path.Combine(srcRoot, "Core", "OrganizerSortOrder.cs"));
        string canvasResizeCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.CanvasResize.cs"));
        string keyboardCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.KeyboardFilter.cs"));
        string dragDropCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.ItemDragAndDrop.cs"));
        string selectionCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.ItemSelection.cs"));
        string todoXaml = File.ReadAllText(Path.Combine(srcRoot, "TodoWindow.xaml"));
        string noteTitleCs = File.ReadAllText(Path.Combine(srcRoot, "MainWindow.OrganizerTitle.cs"));

        require(sortOrderCs.Contains("MultiByteToWideChar", StringComparison.Ordinal) &&
                !sortOrderCs.Contains("Encoding.Default.GetString", StringComparison.Ordinal),
            "OrganizerSortOrder 必须通过 Win32 CP_ACP 解析 ANSI .lnk 路径，避免 .NET 10 UTF-8 Encoding.Default 造成中文路径乱码。");
        require(iconCacheCs.Contains("PremultiplyAlpha(pixels)", StringComparison.Ordinal) &&
                iconCacheCs.Contains("StrictUtf8", StringComparison.Ordinal),
            "IconCacheService 必须对 IShellItemImageFactory 输出执行 PremultiplyAlpha 并支持 UTF-8 .url 路径解析，避免文件夹/文档泛白光晕或丢图。");
        require(canvasResizeCs.Contains("WindowRoot.UpdateLayout();", StringComparison.Ordinal) &&
                mainCs.Contains("ExpandedContentLayer.SizeChanged += SurfaceHost_SizeChanged;", StringComparison.Ordinal),
            "8 向画布缩放收尾缺少同步布局刷新或 ExpandedContentLayer.SizeChanged 裁剪订阅。");
        require(styleCs.Contains("ForceResetCompactHoverVisuals()", StringComparison.Ordinal) &&
                mainCs.Contains("ExpandedView_PointerExited", StringComparison.Ordinal) &&
                transCs.Contains("Clamp(props.Progress, 0.0, 1.0)", StringComparison.Ordinal),
            "缺少无条件悬停弹簧复位、展开态指针离窗流光熄灭或 DWM 表达式进度钳位。");
        require(keyboardCs.Contains("MoveKeyboardFocusDirectional(", StringComparison.Ordinal) &&
                dragDropCs.Contains("ClearPendingExternalDropSlot();", StringComparison.Ordinal) &&
                selectionCs.Contains("UpdateExpandedTopOverlayOffsets()", StringComparison.Ordinal) &&
                todoXaml.Contains("UpdateSourceTrigger=PropertyChanged", StringComparison.Ordinal) &&
                noteTitleCs.Contains("ExpandedNameEditorCapsule.Visibility = Visibility.Visible;", StringComparison.Ordinal),
            "缺少 2D 自由网格方向键导航、外部拖入 finally 落格清理、过滤栏/多选徽章避让、待办回车即时绑定更新或内联标题编辑高对比度磨砂胶囊。");
    }
}
