using System.Text.RegularExpressions;
using System.Xml.Linq;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class EndgameProductivityChecks
{
    internal static async Task RunAsync()
    {
        var failures = new List<string>();
        void Require(bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }

        CheckGlobalHotkeys(Require);
        CheckSearchEngine(Require);
        CheckSortOrder(Require);
        CheckEdgeStackAlign(Require);
        await CheckLayoutSnapshotsAsync(Require);
        await CheckAutoOrganizeAsync(Require);
        await CheckStateSchema16BackwardCompatibilityAsync(Require);
        CheckLocalizationParityAndWiring(Require);

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Endgame productivity checks failed ({failures.Count}): {string.Join("；", failures)}");

        Console.WriteLine("PASS: Endgame productivity and UX overhaul");
    }

    private static void CheckGlobalHotkeys(Action<bool, string> require)
    {
        require(GlobalHotkeyDefinitions.Normalize((QuickHideHotkeyPreset)999) == QuickHideHotkeyPreset.Off,
            "无效 QuickHideHotkeyPreset 未回退到 Off。");
        require(GlobalHotkeyDefinitions.Normalize((SearchHotkeyPreset)999) == SearchHotkeyPreset.Off,
            "无效 SearchHotkeyPreset 未回退到 Off。");
        require(GlobalHotkeyDefinitions.Normalize((PeekHotkeyPreset)999) == PeekHotkeyPreset.Off,
            "无效 PeekHotkeyPreset 未回退到 Off。");

        HotkeyBinding? hideSpace = GlobalHotkeyDefinitions.Resolve(QuickHideHotkeyPreset.CtrlAltSpace);
        HotkeyBinding? searchSpace = GlobalHotkeyDefinitions.Resolve(SearchHotkeyPreset.CtrlAltSpace);
        HotkeyBinding? peekP = GlobalHotkeyDefinitions.Resolve(PeekHotkeyPreset.CtrlAltP);

        require(hideSpace is not null && searchSpace is not null && peekP is not null,
            "预设全局快捷键解析为空。");
        require(GlobalHotkeyDefinitions.ConflictsWithAny(searchSpace, hideSpace, peekP),
            "相同组合键 Ctrl+Alt+Space 未触发冲突拦截。");
        require(!GlobalHotkeyDefinitions.ConflictsWithAny(peekP, hideSpace, searchSpace),
            "不同组合键 Ctrl+Alt+P 误报冲突。");
        require(!GlobalHotkeyDefinitions.ConflictsWithAny(null, hideSpace, searchSpace, peekP),
            "关闭快捷键时不应报冲突。");
    }

    private static void CheckSearchEngine(Action<bool, string> require)
    {
        Guid orgTools = Guid.NewGuid();
        Guid orgDocs = Guid.NewGuid();
        var entries = new OrganizerSearchEntry[]
        {
            new(orgTools, "开发工具", "Visual Studio Code", "Visual Studio Code.lnk", @"C:\TuckPane\Visual Studio Code.lnk", WidgetItemKind.Shortcut),
            new(orgTools, "开发工具", "Code", "Code.lnk", @"C:\TuckPane\Code.lnk", WidgetItemKind.Shortcut),
            new(orgDocs, "项目文档", "DeepSeek Handbook 2026", "DeepSeek Handbook 2026.pdf", @"C:\TuckPane\DeepSeek Handbook 2026.pdf", WidgetItemKind.File),
            new(orgDocs, "项目文档", "Café Resume", "Café Resume.docx", @"C:\TuckPane\Café Resume.docx", WidgetItemKind.File),
            new(orgDocs, "项目文档", "quarterly_report_q4", "quarterly_report_q4.xlsx", @"C:\TuckPane\quarterly_report_q4.xlsx", WidgetItemKind.File)
        };

        // Exact > prefix > word boundary ranking.
        IReadOnlyList<OrganizerSearchHit> codeHits = OrganizerSearch.Search(entries, "code");
        require(codeHits.Count == 2 && codeHits[0].Entry.ItemName == "Code" && codeHits[0].Score > codeHits[1].Score,
            "精确匹配未优先于词边界匹配。");

        // Acronym matching ("vsc" -> Visual Studio Code, "dsh" -> DeepSeek Handbook).
        IReadOnlyList<OrganizerSearchHit> vscHits = OrganizerSearch.Search(entries, "vsc");
        require(vscHits.Count == 1 && vscHits[0].Entry.ItemName == "Visual Studio Code",
            "首字母缩写 vsc 未命中 Visual Studio Code。");

        IReadOnlyList<OrganizerSearchHit> dshHits = OrganizerSearch.Search(entries, "dsh");
        require(dshHits.Count == 1 && dshHits[0].Entry.ItemName == "DeepSeek Handbook 2026",
            "驼峰首字母缩写 dsh 未命中 DeepSeek Handbook 2026。");

        // Multi-token AND + organizer name + extension matching.
        IReadOnlyList<OrganizerSearchHit> multiHits = OrganizerSearch.Search(entries, "文档 pdf deepseek");
        require(multiHits.Count == 1 && multiHits[0].Entry.RelativeName == "DeepSeek Handbook 2026.pdf",
            "多词 AND（收纳窗名+扩展名+关键词）未精准命中。");

        // Diacritics & fullwidth normalization.
        IReadOnlyList<OrganizerSearchHit> diacriticHits = OrganizerSearch.Search(entries, "ｃａｆｅ");
        require(diacriticHits.Count == 1 && diacriticHits[0].Entry.ItemName == "Café Resume",
            "全角/变音符号归一化搜索未命中 Café Resume。");

        // Inline WidgetItem filter helper.
        var widgetItem = new WidgetItem(
            "Visual Studio Code",
            "Visual Studio Code.lnk",
            @"C:\TuckPane\Visual Studio Code.lnk",
            WidgetItemKind.Shortcut);
        require(OrganizerSearch.MatchesItemQuery(widgetItem, "vsc") &&
                !OrganizerSearch.MatchesItemQuery(widgetItem, "excel"),
            "收纳窗内联筛选 MatchesItemQuery 行为异常。");

        // Result cap enforcement.
        var many = Enumerable.Range(0, 80)
            .Select(i => new OrganizerSearchEntry(orgDocs, "Docs", $"report_{i}", $"report_{i}.txt", $@"C:\r\{i}.txt", WidgetItemKind.File));
        IReadOnlyList<OrganizerSearchHit> capped = OrganizerSearch.Search(many, "report", maxResults: 50);
        require(capped.Count == 50, "搜索结果上限 maxResults=50 未生效。");
    }

    private static void CheckSortOrder(Action<bool, string> require)
    {
        DateTimeOffset t1 = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset t2 = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset t3 = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

        var items = new SortableItemMetadata[]
        {
            new("file10.txt", "file10", WidgetItemKind.File, ".txt", t1, 500),
            new("file2.txt", "file2", WidgetItemKind.File, ".txt", t3, 100),
            new("alpha.pdf", "alpha", WidgetItemKind.File, ".pdf", t2, 4096),
            new("WorkFolder", "WorkFolder", WidgetItemKind.Folder, "", t1, 0),
            new("App.lnk", "App", WidgetItemKind.Shortcut, ".lnk", t2, 2048)
        };

        IReadOnlyList<string> nameAsc = OrganizerSortOrder.Sort(items, OrganizerSortCriterion.NameAscending);
        require(nameAsc[0] == "WorkFolder" &&
                nameAsc.ToList().IndexOf("file2.txt") < nameAsc.ToList().IndexOf("file10.txt"),
            "按名称升序未保持文件夹优先或自然数字排序（file2 < file10）。");

        IReadOnlyList<string> nameDesc = OrganizerSortOrder.Sort(items, OrganizerSortCriterion.NameDescending);
        require(nameDesc[0] == "WorkFolder" &&
                nameDesc.ToList().IndexOf("file10.txt") < nameDesc.ToList().IndexOf("file2.txt"),
            "按名称降序未保持文件夹优先或自然数字倒序。");

        IReadOnlyList<string> byType = OrganizerSortOrder.Sort(items, OrganizerSortCriterion.TypeAscending);
        require(byType[0] == "WorkFolder" && byType[1] == "App.lnk" && byType[2] == "alpha.pdf",
            "按类型排序分桶顺序（文件夹 -> 快捷方式 -> 扩展名）异常。");

        IReadOnlyList<string> byDate = OrganizerSortOrder.Sort(items, OrganizerSortCriterion.DateModifiedDescending);
        require(byDate[0] == "file2.txt", "按修改日期倒序未把最新文件排在首位。");

        IReadOnlyList<string> bySize = OrganizerSortOrder.Sort(items, OrganizerSortCriterion.SizeDescending);
        require(bySize[0] == "alpha.pdf" && bySize[1] == "App.lnk" && bySize[^1] == "WorkFolder",
            "按大小倒序未把大文件排前、容器排后。");
    }

    private static void CheckEdgeStackAlign(Action<bool, string> require)
    {
        var work = new NativeMethods.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1528 };
        var insets = new WindowAlignmentInsets(Left: 32, Top: 24, Right: 44, Bottom: 24);

        var rightFrame = new NativeMethods.RECT { Left = 2460, Top = 120, Right = 2560, Bottom = 220 };
        require(EdgeStackAlignMath.DetectNearestEdge(rightFrame, work) == OrganizerDockEdge.Right,
            "贴右边缘窗口最近边识别错误。");
        var leftFrame = new NativeMethods.RECT { Left = 8, Top = 300, Right = 108, Bottom = 400 };
        require(EdgeStackAlignMath.DetectNearestEdge(leftFrame, work) == OrganizerDockEdge.Left,
            "贴左边缘窗口最近边识别错误。");

        Guid id1 = Guid.NewGuid();
        Guid id2 = Guid.NewGuid();
        Guid id3 = Guid.NewGuid();
        Guid centerId = Guid.NewGuid();

        var candidates = new EdgeStackCandidate[]
        {
            // Three right-edge windows with messy X/Y offsets.
            new(id2, new NativeMethods.RECT { Left = 2410, Top = 500, Right = 2588, Bottom = 678 }, insets),
            new(id1, new NativeMethods.RECT { Left = 2420, Top = 140, Right = 2598, Bottom = 318 }, insets),
            new(id3, new NativeMethods.RECT { Left = 2400, Top = 900, Right = 2578, Bottom = 1078 }, insets),
            // One center window that should NOT be pulled into the right edge stack.
            new(centerId, new NativeMethods.RECT { Left = 1100, Top = 400, Right = 1278, Bottom = 578 }, insets)
        };

        IReadOnlyList<EdgeStackPlacement> placements = EdgeStackAlignMath.ComputeTidyStack(
            candidates, OrganizerDockEdge.Right, work, displayScale: 1.5, requiredAnchorId: id1);

        require(placements.Count == 3 && placements.All(p => p.OrganizerId != centerId),
            "贴边整列误把屏幕中央窗口也拉到了右边，或遗漏了贴边窗口。");
        require(placements[0].OrganizerId == id1 && placements[1].OrganizerId == id2 && placements[2].OrganizerId == id3,
            "贴边整列未保持原有上下视觉顺序。");

        for (int i = 0; i < placements.Count; i++)
        {
            NativeMethods.RECT visible = insets.ToFrame(placements[i].NewWindowBounds);
            require(visible.Right == work.Right,
                $"第 {i} 个窗口贴边整列后可见右边缘 {visible.Right} 未紧贴 {work.Right}。");
            if (i > 0)
            {
                NativeMethods.RECT prevVisible = insets.ToFrame(placements[i - 1].NewWindowBounds);
                require(visible.Top > prevVisible.Bottom,
                    $"贴边整列后第 {i - 1} 与 {i} 个窗口发生纵向重叠。");
            }
        }
    }

    private static async Task CheckLayoutSnapshotsAsync(Action<bool, string> require)
    {
        var single = new DisplayInfo[]
        {
            new(@"\\.\DISPLAY1",
                new NativeMethods.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1600 },
                new NativeMethods.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1528 },
                1.5)
        };
        var dual = new DisplayInfo[]
        {
            single[0],
            new(@"\\.\DISPLAY2",
                new NativeMethods.RECT { Left = 2560, Top = 0, Right = 4480, Bottom = 1080 },
                new NativeMethods.RECT { Left = 2560, Top = 0, Right = 4480, Bottom = 1040 },
                1.0)
        };

        (string keyA, _) = LayoutSnapshotMath.BuildTopologyFingerprint(single);
        (string keyB, _) = LayoutSnapshotMath.BuildTopologyFingerprint(single);
        (string keyDual, _) = LayoutSnapshotMath.BuildTopologyFingerprint(dual);
        require(keyA == keyB && keyA != keyDual,
            "显示器拓扑指纹不稳定或未能区分单屏/双屏配置。");

        string root = Path.Combine(Path.GetTempPath(), $"TuckPane-snapshot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string storePath = Path.Combine(root, "layout-snapshots.json");
            string exportPath = Path.Combine(root, "exported-snapshots.json");
            var service = new LayoutSnapshotService(storePath);

            var org = new OrganizerDefinition
            {
                Id = Guid.NewGuid(),
                Name = "右侧收纳",
                PlacementMode = OrganizerPlacementMode.Floating,
                Position = new WidgetPosition { MonitorDevice = @"\\.\DISPLAY1", XDip = 1617.3333, YDip = 120 }
            };

            LayoutSnapshot saved = await service.SaveSnapshotAsync("办公单屏", isAutoTopology: true, single, [org]);
            require(saved.Entries.Count == 1 && File.Exists(storePath), "布局快照未能原子落盘。");

            // Simulate accidental drift and restore via auto topology lookup.
            org.Position = new WidgetPosition { MonitorDevice = @"\\.\DISPLAY1", XDip = 800, YDip = 400 };
            LayoutSnapshot? found = await service.FindAutoSnapshotAsync(single);
            require(found is not null, "未按拓扑指纹查找到自动快照。");
            int restored = LayoutSnapshotMath.ApplySnapshot(found!, [org]);
            require(restored == 1 && Math.Abs(org.Position.XDip - 1617.3333) < 0.01,
                "应用布局快照后未能恢复原始贴边坐标。");

            // Export & import round-trip into a fresh store.
            await service.ExportToFileAsync(exportPath);
            var secondService = new LayoutSnapshotService(Path.Combine(root, "second.json"));
            int importedCount = await secondService.ImportFromFileAsync(exportPath);
            require(importedCount == 1, "布局快照导出/导入往返数量不匹配。");

            bool deleted = await service.DeleteSnapshotAsync(saved.Id);
            require(deleted && (await service.LoadAsync()).Snapshots.Count == 0,
                "删除布局快照失败。");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckAutoOrganizeAsync(Action<bool, string> require)
    {
        // Rule safety checks (pure logic).
        DateTimeOffset now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var docRule = AutoOrganizeRules.CreatePreset("documents", AutoOrganizeSource.Desktop);
        docRule.MinAgeDays = 2;

        var oldPdf = new AutoOrganizeCandidate(@"C:\Desktop\Spec.pdf", "Spec.pdf", ".pdf", AutoOrganizeSource.Desktop, now.AddDays(-5), false, false, false);
        var freshPdf = new AutoOrganizeCandidate(@"C:\Desktop\New.pdf", "New.pdf", ".pdf", AutoOrganizeSource.Desktop, now.AddHours(-3), false, false, false);
        var hiddenPdf = new AutoOrganizeCandidate(@"C:\Desktop\Secret.pdf", "Secret.pdf", ".pdf", AutoOrganizeSource.Desktop, now.AddDays(-5), false, false, true);
        var reparsePdf = new AutoOrganizeCandidate(@"C:\Desktop\Link.pdf", "Link.pdf", ".pdf", AutoOrganizeSource.Desktop, now.AddDays(-5), false, true, false);
        var desktopIni = new AutoOrganizeCandidate(@"C:\Desktop\desktop.ini", "desktop.ini", ".ini", AutoOrganizeSource.Desktop, now.AddDays(-5), false, false, false);

        require(AutoOrganizeRules.MatchesRule(docRule, oldPdf, now), "满足 MinAgeDays 的 .pdf 未匹配规则。");
        require(!AutoOrganizeRules.MatchesRule(docRule, freshPdf, now), "未满 MinAgeDays 的文件被误匹配。");
        require(!AutoOrganizeRules.MatchesRule(docRule, hiddenPdf, now), "隐藏/系统文件未被安全跳过。");
        require(!AutoOrganizeRules.MatchesRule(docRule, reparsePdf, now), "符号链接/重解析点未被安全跳过。");
        require(!AutoOrganizeRules.MatchesRule(new AutoOrganizeRule { Extensions = ".ini" }, desktopIni, now),
            "desktop.ini 未被强制保护。");

        // End-to-end isolated disk execution: Preview -> Execute (.pdf move + .exe shortcut) -> Undo.
        string root = Path.Combine(Path.GetTempPath(), $"TuckPane-autoorg-test-{Guid.NewGuid():N}");
        string desktopDir = Path.Combine(root, "Desktop");
        string docsTarget = Path.Combine(root, "Organizers", "Docs");
        string appsTarget = Path.Combine(root, "Organizers", "Apps");
        Directory.CreateDirectory(desktopDir);
        Directory.CreateDirectory(docsTarget);
        Directory.CreateDirectory(appsTarget);

        try
        {
            string pdfPath = Path.Combine(desktopDir, "Proposal.pdf");
            string exePath = Path.Combine(desktopDir, "Installer.exe");
            string hiddenPath = Path.Combine(desktopDir, "HiddenNote.pdf");
            await File.WriteAllTextAsync(pdfPath, "proposal-bytes");
            await File.WriteAllTextAsync(exePath, "fake-exe-payload");
            await File.WriteAllTextAsync(hiddenPath, "hidden-payload");
            File.SetAttributes(hiddenPath, FileAttributes.Hidden);

            var state = new AppStateV2
            {
                Organizers =
                [
                    new OrganizerDefinition
                    {
                        Id = Guid.NewGuid(),
                        Name = "文档箱",
                        StorageAbsolutePath = docsTarget,
                        AutoOrganizeRules = [AutoOrganizeRules.CreatePreset("documents", AutoOrganizeSource.Desktop)]
                    },
                    new OrganizerDefinition
                    {
                        Id = Guid.NewGuid(),
                        Name = "安装器箱",
                        StorageAbsolutePath = appsTarget,
                        AutoOrganizeRules = [AutoOrganizeRules.CreatePreset("installers", AutoOrganizeSource.Desktop)]
                    }
                ]
            };

            var service = new AutoOrganizeService();
            IReadOnlyList<AutoOrganizePlannedMove> plan = service.PreviewPlan(state, desktopOverride: desktopDir);
            require(plan.Count == 2 &&
                    plan.Any(p => p.SourcePath.Equals(pdfPath, StringComparison.OrdinalIgnoreCase) && !p.CreatesShortcut) &&
                    plan.Any(p => p.SourcePath.Equals(exePath, StringComparison.OrdinalIgnoreCase) && p.CreatesShortcut),
                "自动归类 PreviewPlan 未正确生成 1 项移动 + 1 项快捷方式计划（并跳过隐藏文件）。");

            var queue = new TransferQueue();
            AutoOrganizeRunSummary run = await service.ExecutePlanAsync(plan, queue);
            require(run.MovedCount == 1 && run.ShortcutCount == 1 && run.FailedCount == 0 && service.CanUndo,
                $"自动归类执行结果异常：moved={run.MovedCount}, shortcuts={run.ShortcutCount}, failed={run.FailedCount}。");
            require(!File.Exists(pdfPath) && File.Exists(Path.Combine(docsTarget, "Proposal.pdf")),
                "文档文件未成功移动到目标收纳窗目录。");
            require(File.Exists(exePath) && Directory.EnumerateFiles(appsTarget, "*.lnk").Any(),
                "可执行文件应保留原地并在收纳窗内创建 .lnk 快捷方式。");
            require(File.Exists(hiddenPath), "隐藏文件不应被移动。");

            AutoOrganizeUndoSummary undo = await service.UndoLastBatchAsync();
            require(undo.RestoredCount == 2 && undo.FailedCount == 0 && !service.CanUndo,
                $"自动归类撤销结果异常：restored={undo.RestoredCount}, skipped={undo.SkippedCount}, failed={undo.FailedCount}。");
            require(File.Exists(pdfPath) && !File.Exists(Path.Combine(docsTarget, "Proposal.pdf")),
                "撤销后文档文件未恢复回原桌面路径。");
            require(!Directory.EnumerateFiles(appsTarget, "*.lnk").Any() && File.Exists(exePath),
                "撤销后未清理自动创建的 .lnk 快捷方式或误删原 .exe。");
        }
        finally
        {
            try
            {
                string hiddenPath = Path.Combine(desktopDir, "HiddenNote.pdf");
                if (File.Exists(hiddenPath)) File.SetAttributes(hiddenPath, FileAttributes.Normal);
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch { }
        }
    }

    private static async Task CheckStateSchema16BackwardCompatibilityAsync(Action<bool, string> require)
    {
        string root = Path.Combine(Path.GetTempPath(), $"TuckPane-schema16-compat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // Minimal v4.2.7 SchemaVersion 16 state with none of the new Phase 2 fields.
            string legacyPath = Path.Combine(root, "legacy-state.json");
            await File.WriteAllTextAsync(legacyPath,
                """
                {
                  "SchemaVersion": 16,
                  "GlobalSettings": {},
                  "Organizers": [
                    {
                      "Id": "11111111-1111-1111-1111-111111111111",
                      "Name": "Legacy",
                      "PlacementMode": 0
                    }
                  ]
                }
                """);

            var store = new StateStore(legacyPath);
            AppStateV2 loaded = await store.LoadAsync();
            require(loaded.SchemaVersion == AppStateV2.CurrentSchemaVersion &&
                    loaded.GlobalSettings.QuickHideHotkey == QuickHideHotkeyPreset.Off &&
                    loaded.GlobalSettings.SearchHotkey == SearchHotkeyPreset.Off &&
                    loaded.GlobalSettings.PeekHotkey == PeekHotkeyPreset.Off &&
                    loaded.GlobalSettings.AutoApplyTopologySnapshots &&
                    loaded.Organizers.Count == 1 &&
                    loaded.Organizers[0].AutoOrganizeRules is not null &&
                    loaded.Organizers[0].AutoOrganizeRules.Count == 0,
                "旧版 SchemaVersion 16 状态加载时未能安全赋予 Phase 2 默认值。");

            loaded.GlobalSettings.QuickHideHotkey = QuickHideHotkeyPreset.CtrlAltH;
            loaded.GlobalSettings.SearchHotkey = SearchHotkeyPreset.CtrlAltF;
            loaded.GlobalSettings.PeekHotkey = PeekHotkeyPreset.CtrlAltP;
            loaded.GlobalSettings.AutoApplyTopologySnapshots = false;
            loaded.Organizers[0].AutoOrganizeRules.Add(AutoOrganizeRules.CreatePreset("images", AutoOrganizeSource.Downloads));
            await store.SaveAsync(loaded);

            AppStateV2 reloaded = await store.LoadAsync();
            require(reloaded.SchemaVersion == 16 &&
                    reloaded.GlobalSettings.QuickHideHotkey == QuickHideHotkeyPreset.CtrlAltH &&
                    reloaded.GlobalSettings.SearchHotkey == SearchHotkeyPreset.CtrlAltF &&
                    reloaded.GlobalSettings.PeekHotkey == PeekHotkeyPreset.CtrlAltP &&
                    !reloaded.GlobalSettings.AutoApplyTopologySnapshots &&
                    reloaded.Organizers[0].AutoOrganizeRules.Count == 1 &&
                    reloaded.Organizers[0].AutoOrganizeRules[0].Source == AutoOrganizeSource.Downloads,
                "Phase 2 配置与自动归类规则保存后未能无损往返加载。");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckLocalizationParityAndWiring(Action<bool, string> require)
    {
        string repoRoot = RepoSourceSnapshot.RepoRoot();
        string srcRoot = Path.Combine(repoRoot, "src", "TuckPane");

        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(srcRoot, "*.*", SearchOption.AllDirectories)
            .Where(path =>
                (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"AppStrings\.(?:Get|Format)\(\s*""([^""]+)"""))
                usedKeys.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"loc:([A-Za-z0-9_]+)"))
                usedKeys.Add(m.Groups[1].Value);
            if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match m in Regex.Matches(text, @"Tag=""(Context[A-Za-z0-9_]+)"""))
                    usedKeys.Add(m.Groups[1].Value);
            }
        }

        string[] languages = ["zh-CN", "en-US", "ja-JP"];
        var languageKeys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string lang in languages)
        {
            string reswPath = Path.Combine(srcRoot, "Strings", lang, "Resources.resw");
            XDocument doc = XDocument.Load(reswPath);
            var keys = doc.Root!
                .Elements("data")
                .Select(e => (string?)e.Attribute("name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToHashSet(StringComparer.Ordinal);
            languageKeys[lang] = keys;

            string[] missing = usedKeys.Where(k => !keys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            require(missing.Length == 0, $"{lang} 缺少 {missing.Length} 个本地化键：{string.Join(", ", missing.Take(5))}");
        }

        require(languageKeys["zh-CN"].SetEquals(languageKeys["en-US"]) &&
                languageKeys["zh-CN"].SetEquals(languageKeys["ja-JP"]),
            "zh-CN / en-US / ja-JP 三个 .resw 文件的词条集合不完全对等。");

        // Architectural wiring checks across MainWindow, AppHost, App, DesktopLayerService, ConsoleWindow.
        string allCs = RepoSourceSnapshot.AllTuckPaneCSharp();
        require(allCs.Contains("GlobalHotkeyService", StringComparison.Ordinal) &&
                allCs.Contains("OrganizerSearchWindow", StringComparison.Ordinal) &&
                allCs.Contains("SetPeekTopmost(", StringComparison.Ordinal) &&
                allCs.Contains("TidyFloatingEdgeStackAsync(", StringComparison.Ordinal) &&
                allCs.Contains("RecoverableUiErrorCount", StringComparison.Ordinal) &&
                allCs.Contains("--search", StringComparison.Ordinal) &&
                allCs.Contains("--peek", StringComparison.Ordinal),
            "Phase 2 关键生产力链路（全局快捷键/Spotlight 搜索/Peek 透视/贴边整列/CLI 入口/异常计数）接入不完整。");
    }
}
