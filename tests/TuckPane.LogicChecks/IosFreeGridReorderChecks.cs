using System.Numerics;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;
using Point = Windows.Foundation.Point;

internal static class IosFreeGridReorderChecks
{
    internal static async Task RunAsync()
    {
        var failures = new List<string>();
        void Require(bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }

        CheckFreeGridSlotMap(Require);
        CheckFreeGridReorderModes(Require);
        CheckDwellAndHysteresis(Require);
        CheckIosAnalyticalSpringPhysics(Require);
        await CheckSchema16ItemSlotsPersistenceAsync(Require);
        CheckWiringContracts(Require);

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"iOS free grid reorder checks failed ({failures.Count}): {string.Join("；", failures)}");

        Console.WriteLine("PASS: iOS 18/26 free-placement grid and fluid spring reorder engine");
    }

    private static void CheckFreeGridSlotMap(Action<bool, string> require)
    {
        string[] names = ["Bandizip.lnk", "LocalSend.lnk", "Blender.lnk", "scrcpy.lnk", "NewApp.lnk"];
        var saved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bandizip.lnk"] = 0,
            ["LocalSend.lnk"] = 5,   // top-right on 6-col grid
            ["Blender.lnk"] = 24,    // bottom-left row 4
            ["scrcpy.lnk"] = 29      // bottom-right corner!
        };

        int[] resolved = FreeGridSlotMap.ResolveSlots(names, saved);
        require(resolved[0] == 0 && resolved[1] == 5 && resolved[2] == 24 && resolved[3] == 29,
            "稀疏自由槽位未能保留用户放在四角/空行后的精确位置。");
        require(resolved[4] == 1,
            "新增文件未自动落入首个空闲槽位（slot 1），或误移动了已有自由摆放项。");

        List<string> readingOrder = FreeGridSlotMap.BuildReadingOrder(names, resolved);
        require(readingOrder.SequenceEqual(["Bandizip.lnk", "NewApp.lnk", "LocalSend.lnk", "Blender.lnk", "scrcpy.lnk"]),
            "稀疏槽位对应的阅读顺序与 slot 升序不一致。");

        int totalRows = FreeGridSlotMap.TotalRowCount(resolved, columns: 6, minimumVisibleRows: 5, includeDragBufferRow: true);
        require(totalRows == 6, $"含拖拽缓冲行的总行数应为 6，实际={totalRows}。");

        int hitSlot = FreeGridSlotMap.PointToSlot(new Point(550, 440), cellWidth: 100, cellHeight: 100, gap: 10, columns: 6, totalRows: 5);
        require(hitSlot == 29, $"右下角坐标未精准映射到槽位 29，实际={hitSlot}。");

        Dictionary<string, int> compacted = FreeGridSlotMap.CompactToDense(readingOrder);
        require(compacted["Bandizip.lnk"] == 0 && compacted["scrcpy.lnk"] == 4,
            "一键紧凑排布空位未把所有项收紧到 0..N-1。");
    }

    private static void CheckFreeGridReorderModes(Action<bool, string> require)
    {
        // Sparse 6x5 grid: cluster A at [0, 1, 2], blank gap at [3..23], isolated cluster B at [24, 25], corner C at [29].
        int[] initial = [0, 1, 2, 24, 25, 29];

        // 1. Free-form drop into empty slot 17 -> ZERO other icons move!
        ReorderPreviewSnapshot emptyDrop = FreeGridReorderEngine.ComputeLayout(
            initial, sourceItemIndex: 0, targetSlot: 17, freeGridEnabled: true, preferDirectSwap: false, slotLimit: 30);
        require(emptyDrop.Kind == ReorderTargetKind.EmptySlot &&
                emptyDrop.VisualSlots.SequenceEqual([17, 1, 2, 24, 25, 29]),
            "拖入空槽位 17 时影响了其他图标，未能实现 iOS 18 零扰动自由落格。");

        // 2. Drop from isolated corner 29 onto occupied slot 0 in cluster A -> ripples ONLY cluster A [0,1,2 -> 1,2,3], leaving [24,25] untouched!
        ReorderPreviewSnapshot localRipple = FreeGridReorderEngine.ComputeLayout(
            initial, sourceItemIndex: 5, targetSlot: 0, freeGridEnabled: true, preferDirectSwap: false, slotLimit: 30);
        require(localRipple.Kind == ReorderTargetKind.PushAside &&
                localRipple.VisualSlots.SequenceEqual([1, 2, 3, 24, 25, 0]),
            "占用槽局部流式让位未在首个空隙处停止，或误扰动了远端分区 [24,25]。");

        // 3. Reorder within same contiguous cluster A (item 0 -> slot 2) -> smooth reading-order shift [0,1,2 -> 2,0,1], leaving [24,25,29] untouched!
        ReorderPreviewSnapshot intraCluster = FreeGridReorderEngine.ComputeLayout(
            initial, sourceItemIndex: 0, targetSlot: 2, freeGridEnabled: true, preferDirectSwap: false, slotLimit: 30);
        require(intraCluster.Kind == ReorderTargetKind.PushAside &&
                intraCluster.VisualSlots.SequenceEqual([2, 0, 1, 24, 25, 29]),
            "同连续集群内移动未按 iOS 阅读顺序平滑让位。");

        // 4. Direct 1-to-1 swap (item 0 at slot 0 <-> item 5 at slot 29) -> ONLY those two swap!
        ReorderPreviewSnapshot directSwap = FreeGridReorderEngine.ComputeLayout(
            initial, sourceItemIndex: 0, targetSlot: 29, freeGridEnabled: true, preferDirectSwap: true, slotLimit: 30);
        require(directSwap.Kind == ReorderTargetKind.DirectSwap &&
                directSwap.VisualSlots.SequenceEqual([29, 1, 2, 24, 25, 0]),
            "1 对 1 直接互换未精准交换源与目标，或误动了其他图标。");

        // 5. Dense (non-free) mode: clean deterministic reading-order insertion (NO 15-puzzle snake!).
        int[] dense = [0, 1, 2, 3, 4, 5];
        ReorderPreviewSnapshot denseMove = FreeGridReorderEngine.ComputeLayout(
            dense, sourceItemIndex: 5, targetSlot: 1, freeGridEnabled: false, preferDirectSwap: false, slotLimit: 6);
        require(denseMove.VisualSlots.SequenceEqual([0, 2, 3, 4, 5, 1]),
            "紧凑模式下重排未遵循确定性 iOS 阅读顺序插入。");
    }

    private static void CheckDwellAndHysteresis(Action<bool, string> require)
    {
        // Item 0 at slot 0, item 1 at slot 1, slot 2 is empty.
        int[] initial = [0, 1];
        var engine = new FreeGridReorderEngine(initial, sourceItemIndex: 0, freeGridEnabled: true, maxSlotCount: 12);

        // Quick 16ms sweep over occupied slot 1 should NOT immediately scatter item 1 (dwell protection).
        Point centerSlot1 = FreeGridSlotMap.SlotCenter(1, columns: 6, cellWidth: 100, cellHeight: 100, gap: 10);
        bool premature = engine.UpdateFromPointer(centerSlot1, 100, 100, 10, 6, 2, deltaMs: 16, preferDirectSwap: false);
        require(!premature && engine.TargetSlot == 0,
            "快速掠过占用槽（16ms）时缺少防误触驻留阈值（Dwell），会导致穿越乱序。");

        // Dwelling past OccupiedTransitDwellMs (~80ms) commits smoothly.
        bool settledAfterDwell = false;
        for (int frame = 0; frame < 6 && !settledAfterDwell; frame++)
            settledAfterDwell = engine.UpdateFromPointer(centerSlot1, 100, 100, 10, 6, 2, deltaMs: 16, preferDirectSwap: false);
        require(settledAfterDwell && engine.TargetSlot == 1,
            "持续悬停超过 Dwell 阈值后未能触发占用槽让位。");

        // Moving into an EMPTY slot 2 commits IMMEDIATELY (0ms lag, no dwell needed for empty cells!).
        Point centerSlot2 = FreeGridSlotMap.SlotCenter(2, columns: 6, cellWidth: 100, cellHeight: 100, gap: 10);
        bool instantEmpty = engine.UpdateFromPointer(centerSlot2, 100, 100, 10, 6, 2, deltaMs: 16, preferDirectSwap: false);
        require(instantEmpty && engine.TargetSlot == 2 && engine.CurrentKind == ReorderTargetKind.EmptySlot,
            "拖入空槽位时未立即响应落点预览。");

        // Slight jitter right near boundary between slot 2 and slot 1 must not oscillate (hysteresis).
        Point boundaryJitter = new(centerSlot2.X - 52, centerSlot2.Y);
        bool jittered = engine.UpdateFromPointer(boundaryJitter, 100, 100, 10, 6, 2, deltaMs: 100, preferDirectSwap: false);
        require(!jittered && engine.TargetSlot == 2,
            "槽位边界微小抖动触发了来回震荡，空间迟滞带（Hysteresis）未生效。");
    }

    private static void CheckIosAnalyticalSpringPhysics(Action<bool, string> require)
    {
        // Verify exact closed-form iOS spring stability and velocity preservation across 60Hz and 240Hz.
        foreach (double dt in new[] { 1.0 / 60.0, 1.0 / 120.0, 1.0 / 240.0 })
        {
            Vector3 value = Vector3.Zero;
            Vector3 velocity = Vector3.Zero;
            Vector3 target = new(240f, 120f, 0f);
            bool settled = false;
            float peakX = 0f;

            for (int step = 0; step < 300; step++)
            {
                settled = ItemMotionMath.StepIosSpring(ref value, ref velocity, target, dt, responseSeconds: 0.28, dampingFraction: 0.84);
                peakX = Math.Max(peakX, value.X);
                if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
                {
                    require(false, $"iOS 解析弹簧在 dt={dt:F4} 下产生了 NaN/Infinity。");
                    break;
                }
                if (settled) break;
            }

            require(settled && Vector3.Distance(value, target) < 0.01f,
                $"iOS 弹簧在 dt={dt:F4} 下未平稳收敛至目标点。");
            // Underdamped dampingFraction=0.84 should have subtle organic overshoot (~0.5%-3%), never chaotic (>10%).
            require(peakX >= target.X && peakX <= target.X * 1.06f,
                $"iOS 欠阻尼弹簧超调量异常（peakX={peakX:F2}, targetX={target.X:F2}）。");
        }

        // Mid-flight retargeting preserves velocity smoothly without snapping to zero.
        Vector3 pos = Vector3.Zero;
        Vector3 vel = Vector3.Zero;
        for (int i = 0; i < 6; i++)
            ItemMotionMath.StepIosSpring(ref pos, ref vel, new Vector3(200f, 0f, 0f), 1.0 / 120.0);
        float vxBeforeRetarget = vel.X;
        ItemMotionMath.StepIosSpring(ref pos, ref vel, new Vector3(200f, 200f, 0f), 1.0 / 120.0);
        require(vxBeforeRetarget > 50f && vel.X > 0f && vel.Y > 0f,
            "半途切换目标槽位时未能保持连续惯性动量（平滑弧线过渡）。");
    }

    private static async Task CheckSchema16ItemSlotsPersistenceAsync(Action<bool, string> require)
    {
        string root = Path.Combine(Path.GetTempPath(), $"TuckPane-freegrid-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string legacyPath = Path.Combine(root, "legacy16.json");
            await File.WriteAllTextAsync(legacyPath,
                """
                {
                  "SchemaVersion": 16,
                  "GlobalSettings": {},
                  "Organizers": [
                    {
                      "Id": "22222222-2222-2222-2222-222222222222",
                      "Name": "工具",
                      "PlacementMode": 0,
                      "ItemOrder": ["a.lnk", "b.lnk"]
                    }
                  ]
                }
                """);

            var store = new StateStore(legacyPath);
            AppStateV2 loaded = await store.LoadAsync();
            require(loaded.SchemaVersion == 16 &&
                    loaded.GlobalSettings.FreeGridPlacementEnabled &&
                    !loaded.GlobalSettings.FreeGridPreferDirectSwap &&
                    loaded.Organizers[0].ItemSlots is not null &&
                    loaded.Organizers[0].ItemSlots.Count == 0,
                "旧版 Schema 16 状态加载 Phase 4 自由网格默认值不符合预期。");

            loaded.GlobalSettings.FreeGridPreferDirectSwap = true;
            loaded.Organizers[0].ItemSlots["a.lnk"] = 0;
            loaded.Organizers[0].ItemSlots["b.lnk"] = 29;
            await store.SaveAsync(loaded);

            AppStateV2 reloaded = await store.LoadAsync();
            require(reloaded.GlobalSettings.FreeGridPreferDirectSwap &&
                    reloaded.Organizers[0].ItemSlots["a.lnk"] == 0 &&
                    reloaded.Organizers[0].ItemSlots["b.lnk"] == 29,
                "ItemSlots 稀疏自由方格坐标保存后未能无损往返加载。");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckWiringContracts(Action<bool, string> require)
    {
        string allCs = RepoSourceSnapshot.AllTuckPaneCSharp();
        string allXaml = RepoSourceSnapshot.AllTuckPaneXaml();

        require(allXaml.Contains("x:Name=\"FreeGridOverlayCanvas\"", StringComparison.Ordinal) &&
                allXaml.Contains("x:Name=\"FreeGridDropSlotGhost\"", StringComparison.Ordinal) &&
                allXaml.Contains("x:Name=\"CompactGridGapsMenuItem\"", StringComparison.Ordinal) &&
                allXaml.Contains("x:Name=\"FreeGridPlacementToggle\"", StringComparison.Ordinal) &&
                allXaml.Contains("x:Name=\"FreeGridPreferDirectSwapToggle\"", StringComparison.Ordinal),
            "XAML 缺少自由网格落点光晕、紧凑排布菜单项或控制台交互开关。");

        require(allCs.Contains("FreeSlotGridLayout", StringComparison.Ordinal) &&
                allCs.Contains("FreeGridReorderEngine", StringComparison.Ordinal) &&
                allCs.Contains("StepIosSpring(", StringComparison.Ordinal) &&
                allCs.Contains("RecordExternalDropSlotFromEvent(", StringComparison.Ordinal) &&
                allCs.Contains("CompactGridGapsAsync(", StringComparison.Ordinal),
            "自由网格布局、iOS 弹簧引擎、外部拖入精准落格或紧凑排布未完整接线。");
    }
}
