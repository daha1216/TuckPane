using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class NativeDesktopFusionChecks
{
    internal static async Task RunAsync()
    {
        var failures = new List<string>();
        void Require(bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }

        CheckCompactVisualStyles(Require);
        CheckDesktopGridSnap(Require);
        CheckMarqueeSelectionAndGroupDrag(Require);
        CheckItemSelectionModel(Require);
        await CheckRealMultiFileShellDataObjectAsync(Require);
        await CheckSchema16PersistenceAsync(Require);
        CheckArchitectureWiring(Require);

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Native desktop fusion checks failed ({failures.Count}): {string.Join("；", failures)}");

        Console.WriteLine("PASS: Native desktop fusion, grid snap, and marquee multi-selection");
    }

    private static void CheckCompactVisualStyles(Action<bool, string> require)
    {
        require(CompactVisualStyles.Normalize((CompactVisualStyle)999) == CompactVisualStyle.GlassTile,
            "非法 CompactVisualStyle 未安全回退到 GlassTile。");
        require(CompactVisualStyles.BackdropOpacity(CompactVisualStyle.GlassTile) == 1.0 &&
                CompactVisualStyles.BackdropOpacity(CompactVisualStyle.NativeDesktop) == 0.0 &&
                CompactVisualStyles.BackdropOpacity(CompactVisualStyle.FluentFolder) is > 0.2 and < 0.7,
            "三种收缩视觉风格的背景不透明度映射不符合设计契约。");
        require(CompactVisualStyles.LabelShadowOpacity(CompactVisualStyle.NativeDesktop, highContrast: false) >= 0.8 &&
                CompactVisualStyles.LabelShadowOpacity(CompactVisualStyle.NativeDesktop, highContrast: true) == 0.0,
            "原生桌面融合文字投影未在高对比度模式下自动关闭，或常规模式下对比度不足。");
        require(CompactVisualStyles.LabelMaxLines(CompactVisualStyle.NativeDesktop) == 2 &&
                CompactVisualStyles.LabelMaxLines(CompactVisualStyle.FluentFolder) == 2 &&
                CompactVisualStyles.LabelMaxLines(CompactVisualStyle.GlassTile) == 1,
            "原生桌面/立体文件夹模式未启用 2 行标题换行。");
    }

    private static void CheckDesktopGridSnap(Action<bool, string> require)
    {
        var work = new NativeMethods.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1528 };
        var insets = new WindowAlignmentInsets(Left: 20, Top: 10, Right: 20, Bottom: 10);
        int cellW = 114;
        int cellH = 128;

        // Native desktop icon sits at column 0, row 0 (center ~(65, 72)) -> grid origin (8, 8).
        var nativeIcons = new NativeMethods.POINT[]
        {
            new() { X = 8 + cellW / 2, Y = 8 + cellH / 2 },
            new() { X = 8 + cellW / 2, Y = 8 + cellH + cellH / 2 }
        };

        // Two floating organizers near top-left; one collides with native icon at (0,0) and must avoid it.
        Guid id1 = Guid.NewGuid();
        Guid id2 = Guid.NewGuid();
        var targets = new DesktopGridSnapTarget[]
        {
            new(id1, new NativeMethods.RECT { Left = 10, Top = 10, Right = 140, Bottom = 120 }, insets),
            new(id2, new NativeMethods.RECT { Left = 12, Top = 14, Right = 142, Bottom = 124 }, insets)
        };

        IReadOnlyList<DesktopGridSnapResult> snapped = DesktopGridSnapMath.SnapGroupToGrid(
            targets, work, cellW, cellH, nativeIcons);

        require(snapped.Count == 2, "批量桌面网格吸附返回数量不匹配。");
        NativeMethods.RECT f1 = insets.ToFrame(snapped[0].SnappedBounds);
        NativeMethods.RECT f2 = insets.ToFrame(snapped[1].SnappedBounds);

        // Neither snapped organizer may overlap native icon (0,0) or each other.
        int c1x = f1.Left + f1.Width / 2;
        int c1y = f1.Top + f1.Height / 2;
        require(!(Math.Abs(c1x - nativeIcons[0].X) < cellW / 2 && Math.Abs(c1y - nativeIcons[0].Y) < cellH / 2),
            "悬浮收纳窗网格吸附未避让已占用的原生 Windows 桌面图标槽位。");
        require(!MarqueeSelectionMath.Intersects(f1, f2),
            "多个悬浮收纳窗批量对齐桌面网格后发生相互重叠。");
        require(f1.Left >= work.Left && f1.Right <= work.Right && f1.Top >= work.Top && f1.Bottom <= work.Bottom,
            "对齐桌面网格后的收纳窗超出了工作区边界。");
    }

    private static void CheckMarqueeSelectionAndGroupDrag(Action<bool, string> require)
    {
        var p1 = new NativeMethods.POINT { X = 400, Y = 300 };
        var pSmall = new NativeMethods.POINT { X = 402, Y = 301 };
        var pBig = new NativeMethods.POINT { X = 120, Y = 80 };
        require(!MarqueeSelectionMath.IsMarqueeActive(p1, pSmall, 1.5),
            "微小抖动误触发了框选激活阈值。");
        require(MarqueeSelectionMath.IsMarqueeActive(p1, pBig, 1.5),
            "有效框选拖动未激活框选矩形。");

        NativeMethods.RECT normalized = MarqueeSelectionMath.NormalizeRect(p1, pBig);
        require(normalized.Left == 120 && normalized.Top == 80 && normalized.Right == 400 && normalized.Bottom == 300,
            "反向拉框（右下到左上）矩形归一化结果错误。");

        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        Guid c = Guid.NewGuid();
        var targets = new MarqueeTargetRect<Guid>[]
        {
            new(a, new NativeMethods.RECT { Left = 100, Top = 70, Right = 200, Bottom = 160 }),
            new(b, new NativeMethods.RECT { Left = 350, Top = 260, Right = 450, Bottom = 350 }),
            new(c, new NativeMethods.RECT { Left = 900, Top = 600, Right = 1000, Bottom = 700 })
        };
        IReadOnlyList<Guid> hits = MarqueeSelectionMath.FindIntersectingIds(normalized, targets);
        require(hits.Count == 2 && hits.Contains(a) && hits.Contains(b) && !hits.Contains(c),
            "框选矩形相交命中集合错误。");

        // Lockstep peer group drag clamping.
        var work = new NativeMethods.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1528 };
        var peerStart = new NativeMethods.RECT { Left = 2200, Top = 200, Right = 2360, Bottom = 350 };
        NativeMethods.RECT peerNext = MarqueeSelectionMath.ComputeGroupPeerBounds(peerStart, deltaX: 500, deltaY: -300, work);
        require(peerNext.Right == 2560 && peerNext.Top == 0 && peerNext.Width == 160 && peerNext.Height == 150,
            "多收纳窗同步组拖动在工作区边界未保持尺寸并正确夹紧。");
    }

    private static void CheckItemSelectionModel(Action<bool, string> require)
    {
        var model = new ItemSelectionModel();
        string[] visible = ["a.lnk", "b.lnk", "c.pdf", "d.txt", "e.png"];

        model.HandleClick(visible, 1, ctrl: false, shift: false);
        require(model.Count == 1 && model.Contains("b.lnk"), "单击未选中单项。");

        model.HandleClick(visible, 3, ctrl: false, shift: true);
        require(model.Count == 3 && model.OrderedSelection(visible).SequenceEqual(["b.lnk", "c.pdf", "d.txt"]),
            "Shift 连选范围错误。");

        model.HandleClick(visible, 2, ctrl: true, shift: false);
        require(model.Count == 2 && !model.Contains("c.pdf") && model.Contains("b.lnk") && model.Contains("d.txt"),
            "Ctrl 反选未正确取消已选项。");

        IReadOnlyCollection<string> baseline = model.Snapshot();
        model.ApplyMarquee(["a.lnk", "d.txt"], additiveCtrl: true, baselineBeforeDrag: baseline);
        require(model.OrderedSelection(visible).SequenceEqual(["a.lnk", "b.lnk"]),
            "Ctrl+框选增量反选行为错误。");

        model.SelectAll(visible);
        require(model.Count == 5, "Ctrl+A 全选数量不匹配。");

        require(model.Prune(["a.lnk", "c.pdf"]) && model.Count == 2,
            "目录刷新后 Prune 未剔除已消失的项目。");

        require(model.Clear() && !model.HasSelection, "Clear 未清空选择状态。");
    }

    private static async Task CheckRealMultiFileShellDataObjectAsync(Action<bool, string> require)
    {
        string root = Path.Combine(Path.GetTempPath(), $"TuckPane-multidrag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string f1 = Path.Combine(root, "one.txt");
            string f2 = Path.Combine(root, "two.txt");
            string f3 = Path.Combine(root, "three.txt");
            await File.WriteAllTextAsync(f1, "1");
            await File.WriteAllTextAsync(f2, "2");
            await File.WriteAllTextAsync(f3, "3");

            using ShellDragService.ShellDataObject single = ShellDragService.CreateDataObject(f1);
            require(single.Pointer != IntPtr.Zero, "单文件 ShellDataObject 构造失败。");

            using ShellDragService.ShellDataObject multi = ShellDragService.CreateDataObject([f1, f2, f3]);
            require(multi.Pointer != IntPtr.Zero, "多文件 IShellItemArray ShellDataObject 构造失败。");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckSchema16PersistenceAsync(Action<bool, string> require)
    {
        string root = Path.Combine(Path.GetTempPath(), $"TuckPane-fusion-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string legacyPath = Path.Combine(root, "legacy16.json");
            await File.WriteAllTextAsync(legacyPath,
                """
                {
                  "SchemaVersion": 16,
                  "GlobalSettings": {},
                  "Organizers": []
                }
                """);

            var store = new StateStore(legacyPath);
            AppStateV2 loaded = await store.LoadAsync();
            require(loaded.SchemaVersion == 16 &&
                    loaded.GlobalSettings.CompactVisualStyle == CompactVisualStyle.GlassTile &&
                    !loaded.GlobalSettings.SnapFloatingToDesktopGrid &&
                    loaded.GlobalSettings.DesktopMarqueeSelectionEnabled,
                "旧 Schema 16 配置加载 Phase 3 新字段默认值不符合预期。");

            loaded.GlobalSettings.CompactVisualStyle = CompactVisualStyle.NativeDesktop;
            loaded.GlobalSettings.SnapFloatingToDesktopGrid = true;
            loaded.GlobalSettings.DesktopMarqueeSelectionEnabled = false;
            await store.SaveAsync(loaded);

            AppStateV2 reloaded = await store.LoadAsync();
            require(reloaded.GlobalSettings.CompactVisualStyle == CompactVisualStyle.NativeDesktop &&
                    reloaded.GlobalSettings.SnapFloatingToDesktopGrid &&
                    !reloaded.GlobalSettings.DesktopMarqueeSelectionEnabled,
                "Phase 3 视觉融合与框选设置保存后未能无损往返加载。");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckArchitectureWiring(Action<bool, string> require)
    {
        string allCs = RepoSourceSnapshot.AllTuckPaneCSharp();
        string allXaml = RepoSourceSnapshot.AllTuckPaneXaml();

        require(allXaml.Contains("x:Name=\"CompactNativeSelectionPlate\"", StringComparison.Ordinal) &&
                allXaml.Contains("x:Name=\"CompactFolderTab\"", StringComparison.Ordinal) &&
                allXaml.Contains("x:Name=\"CompactNameShadow1\"", StringComparison.Ordinal) &&
                allXaml.Contains("x:Name=\"MarqueeSelectionRect\"", StringComparison.Ordinal) &&
                allXaml.Contains("x:Name=\"SelectionCountBadge\"", StringComparison.Ordinal) &&
                allXaml.Contains("x:Name=\"CompactVisualStyleCombo\"", StringComparison.Ordinal),
            "MainWindow/ConsoleWindow XAML 缺少原生融合外观或橡皮筋框选视觉元素。");

        require(allCs.Contains("DesktopMarqueeHookService", StringComparison.Ordinal) &&
                allCs.Contains("DesktopSelectionService", StringComparison.Ordinal) &&
                allCs.Contains("SHCreateShellItemArrayFromIDLists", StringComparison.Ordinal) &&
                allCs.Contains("UpdateMultiSelectedGroupDrag(", StringComparison.Ordinal) &&
                allCs.Contains("TryBeginItemMarqueePress(", StringComparison.Ordinal) &&
                allCs.Contains("AlignFloatingToDesktopGridAsync(", StringComparison.Ordinal),
            "桌面框选联动、成组拖动、窗内橡皮筋选择或多文件 Shell 拖出未完整接线。");
    }
}
