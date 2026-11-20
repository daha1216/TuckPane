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
    }
}
