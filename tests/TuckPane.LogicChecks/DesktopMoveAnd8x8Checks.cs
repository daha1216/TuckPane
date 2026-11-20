using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.ApplicationModel.DataTransfer;

internal static class DesktopMoveAnd8x8Checks
{
    internal static async Task RunAsync()
    {
        var failures = new List<string>();
        void Require(bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }

        await Check8x8GridExpansionAsync(Require);
        await CheckDesktopDropMoveAndReadOnlyShortcutAsync(Require);
        CheckShortcutIntegrityAndUrlSafety(Require);
        CheckIosBidirectionalAndMultiStackReorder(Require);
        CheckArchitectureDebloatContracts(Require);

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Desktop move / 8x8 / iOS stack checks failed ({failures.Count}): {string.Join("；", failures)}");

        Console.WriteLine("PASS: 8x8 grid capacity, true desktop move & shortcut integrity, iOS bidirectional/stack reorder, and MainWindow de-bloat");
    }

    private static async Task Check8x8GridExpansionAsync(Action<bool, string> require)
    {
        require(OrganizerLimits.MaximumLayoutDimension == 8,
            $"OrganizerLimits.MaximumLayoutDimension expected 8, got {OrganizerLimits.MaximumLayoutDimension}.");

        string formXaml = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/Controls/OrganizerSettingsForm.xaml");
        require(formXaml.Contains("x:Name=\"RowsSlider\" Grid.Column=\"1\" Minimum=\"2\" Maximum=\"8\"", StringComparison.Ordinal) &&
                formXaml.Contains("x:Name=\"ColumnsSlider\" Grid.Column=\"1\" Minimum=\"2\" Maximum=\"8\"", StringComparison.Ordinal),
            "OrganizerSettingsForm.xaml RowsSlider/ColumnsSlider must allow up to 8.");

        string tempRoot = Path.Combine(Path.GetTempPath(), $"TuckPane-8x8-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new StateStore(Path.Combine(tempRoot, "state.json"));
            var state = new AppStateV2
            {
                SchemaVersion = 16,
                Organizers =
                [
                    new OrganizerDefinition
                    {
                        Name = "Grid8x8",
                        PlacementMode = OrganizerPlacementMode.Floating,
                        Layout = new OrganizerLayout { Rows = 8, Columns = 8 },
                        ItemSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["first.lnk"] = 0,
                            ["corner.lnk"] = 63
                        }
                    }
                ]
            };
            await store.SaveAsync(state);
            AppStateV2 loaded = await store.LoadAsync();
            require(loaded.Organizers[0].Layout.Rows == 8 && loaded.Organizers[0].Layout.Columns == 8,
                "8x8 organizer layout did not round-trip through StateStore.");
            require(loaded.Organizers[0].ItemSlots.TryGetValue("corner.lnk", out int corner) && corner == 63,
                "64th slot (index 63) in 8x8 grid was not preserved.");
        }
        finally
        {
            TryDeleteDir(tempRoot);
        }

        // Verify DisplayPlacementService handles 8x8 cleanly across 1080p, 1440p@150%, and 4K@200%.
        DisplayInfo[] displays =
        [
            new(@"\\.\DISPLAY1", new NativeMethods.RECT { Right = 1920, Bottom = 1080 }, new NativeMethods.RECT { Right = 1920, Bottom = 1040 }, 1.0),
            new(@"\\.\DISPLAY2", new NativeMethods.RECT { Right = 2560, Bottom = 1440 }, new NativeMethods.RECT { Right = 2560, Bottom = 1392 }, 1.5),
            new(@"\\.\DISPLAY3", new NativeMethods.RECT { Right = 3840, Bottom = 2160 }, new NativeMethods.RECT { Right = 3840, Bottom = 2080 }, 2.0)
        ];
        var layout8x8 = new OrganizerLayout { Rows = 8, Columns = 8 };
        foreach (DisplayInfo display in displays)
        {
            double minCanvas = DisplayPlacementService.CalculateMinimumCanvasScale(display, layout8x8);
            double maxItem = DisplayPlacementService.CalculateMaximumItemScale(display, layout8x8, canvasScale: 1.0);
            (double minW, double minH) = DisplayPlacementService.CalculateMinimumExpandedSizeDip(layout8x8, itemScale: 0.75);
            require(double.IsFinite(minCanvas) && minCanvas is > 0 and <= 1.2 &&
                    double.IsFinite(maxItem) && maxItem is >= 0.5 and <= 1.65 &&
                    minW > 100 && minH > 100,
                $"DisplayPlacementService produced invalid 8x8 metrics on {display.Device}.");
        }
    }

    private static async Task CheckDesktopDropMoveAndReadOnlyShortcutAsync(Action<bool, string> require)
    {
        var copy = DataPackageOperation.Copy;
        var move = DataPackageOperation.Move;
        var link = DataPackageOperation.Link;

        require(OrganizerInteractionMath.SelectIncomingDropOperation(copy | link, fromDesktop: true, controlPressed: false) == move,
            "Dragging from Desktop without Ctrl must resolve to Move even if AllowedOperations lacks Move.");
        require(OrganizerInteractionMath.SelectIncomingDropOperation(copy | move | link, fromDesktop: true, controlPressed: true) == copy,
            "Ctrl+dragging from Desktop must explicitly resolve to Copy.");
        require(OrganizerInteractionMath.SelectIncomingDropOperation(copy | link, fromDesktop: false, controlPressed: false) == copy,
            "Non-desktop drop with Copy|Link must still resolve to Copy.");

        // Verify ReadOnly .lnk files move cleanly without failing or turning into CopiedSourceRetained!
        string tempRoot = Path.Combine(Path.GetTempPath(), $"TuckPane-move-lnk-{Guid.NewGuid():N}");
        string fakeDesktop = Path.Combine(tempRoot, "Desktop");
        string targetDir = Path.Combine(tempRoot, "TargetApp");
        string organizerItems = Path.Combine(tempRoot, "TuckPane", "Org-12345678", "Items");
        Directory.CreateDirectory(fakeDesktop);
        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(organizerItems);
        try
        {
            string fakeExe = Path.Combine(targetDir, "MyTool.exe");
            File.WriteAllBytes(fakeExe, [0x4D, 0x5A, 0x90, 0x00]);

            string sourceLnk = Path.Combine(fakeDesktop, "MyTool.lnk");
            CreateTestShortcut(
                sourceLnk,
                targetPath: fakeExe,
                workingDir: string.Empty,
                arguments: "--profile=work",
                relativePath: @"..\TargetApp\MyTool.exe",
                runAsAdmin: true);
            File.SetAttributes(sourceLnk, File.GetAttributes(sourceLnk) | FileAttributes.ReadOnly);

            var storage = new StorageService(organizerItems);
            IReadOnlyList<TransferOutcome> outcomes = await storage.ImportBatchAsync([sourceLnk], progress: null, CancellationToken.None);
            require(outcomes.Count == 1 && outcomes[0].Status == TransferStatus.Moved,
                $"ReadOnly .lnk import must result in Moved (got {outcomes.FirstOrDefault()?.Status}).");
            require(!File.Exists(sourceLnk),
                "Original .lnk must be removed from source after Move.");
            string movedLnk = outcomes[0].DestinationPath!;
            require(File.Exists(movedLnk),
                "Moved .lnk must exist in organizer storage.");

            ShortcutInspection? inspected = ShortcutIntegrityService.InspectLink(movedLnk);
            require(inspected is not null, "Moved .lnk must remain inspectable.");
            if (inspected is not null)
            {
                require(string.Equals(inspected.ExpandedTargetPath, fakeExe, StringComparison.OrdinalIgnoreCase),
                    $"Relocated .lnk target must resolve cleanly to '{fakeExe}', got '{inspected.ExpandedTargetPath}'.");
                require(string.Equals(inspected.RawWorkingDirectory, targetDir, StringComparison.OrdinalIgnoreCase),
                    $"Empty WorkingDirectory should be repaired to target folder '{targetDir}', got '{inspected.RawWorkingDirectory}'.");
                require(inspected.Arguments == "--profile=work",
                    $"Shortcut arguments must be preserved verbatim (got '{inspected.Arguments}').");
                require(inspected.RequiresElevation,
                    "SLDF_RUNAS_USER elevation flag must be preserved after repair.");
                require(!ShortcutIntegrityService.RepairMovedShortcutIfNeeded(movedLnk, movedLnk),
                    "RepairMovedShortcutIfNeeded must be idempotent once target and working directory are valid.");
            }
        }
        finally
        {
            TryDeleteDir(tempRoot);
        }
    }

    private static void CheckShortcutIntegrityAndUrlSafety(Action<bool, string> require)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"TuckPane-url-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string steamUrl = Path.Combine(tempRoot, "Game.url");
            File.WriteAllText(steamUrl, "[InternetShortcut]\r\nURL=steam://rungameid/730\r\n");
            require(ShortcutIntegrityService.TryReadInternetShortcutUrl(steamUrl, out string parsedSteam, out _) &&
                    parsedSteam == "steam://rungameid/730",
                "Safe steam:// .url shortcut must be parsed for fallback launching.");

            string webUrl = Path.Combine(tempRoot, "Docs.url");
            File.WriteAllText(webUrl, "[InternetShortcut]\r\nURL=https://github.com/daha1216/TuckPane\r\n");
            require(ShortcutIntegrityService.TryReadInternetShortcutUrl(webUrl, out string parsedWeb, out _) &&
                    parsedWeb == "https://github.com/daha1216/TuckPane",
                "Safe https:// .url shortcut must be parsed.");

            string badUrl = Path.Combine(tempRoot, "Unsafe.url");
            File.WriteAllText(badUrl, "[InternetShortcut]\r\nURL=javascript:alert(1)\r\n");
            require(!ShortcutIntegrityService.TryReadInternetShortcutUrl(badUrl, out _, out _),
                "Unsafe javascript: scheme in .url must be rejected.");
        }
        finally
        {
            TryDeleteDir(tempRoot);
        }
    }

    private static void CheckIosBidirectionalAndMultiStackReorder(Action<bool, string> require)
    {
        // 1. Bidirectional nearest-gap ripple:
        // Row slots: [0:empty] [1:A] [2:B] [3:C] [4:D] [5:E] [6:F] [7:G] [8:empty], dragged item H at slot 20.
        // Dropping H onto slot 2: backward gap is 2 steps left (slot 0), forward gap is 6 steps right (slot 8).
        // Must shift ONLY {1, 2} left to {0, 1} instead of pushing 6 items right!
        int[] initial = [1, 2, 3, 4, 5, 6, 7, 20]; // idx 0..6 at slots 1..7, idx 7 (H) at slot 20
        ReorderPreviewSnapshot bidir = FreeGridReorderEngine.ComputeLayout(
            initial,
            sourceItemIndex: 7,
            targetSlot: 2,
            freeGridEnabled: true,
            preferDirectSwap: false,
            slotLimit: 64);
        require(bidir.Kind == ReorderTargetKind.PushAside &&
                bidir.VisualSlots[7] == 2 &&
                bidir.VisualSlots[0] == 0 &&
                bidir.VisualSlots[1] == 1 &&
                bidir.VisualSlots[2] == 3 &&
                bidir.VisualSlots[6] == 7,
            "Bidirectional ripple must shift the shorter left chain {1,2}->{0,1} and leave {3..7} untouched.");

        // 2. Approach-vector tie-breaker: [0:empty] [1:A] [2:empty], dragged B at slot 10 onto slot 1.
        // Backward dist == forward dist == 1. Approaching from right (approachDeltaX < 0) pushes A left to 0;
        // approaching from left (approachDeltaX > 0) pushes A right to 2.
        int[] tieSlots = [1, 10];
        ReorderPreviewSnapshot pushLeft = FreeGridReorderEngine.ComputeLayout(
            tieSlots, sourceItemIndex: 1, targetSlot: 1, freeGridEnabled: true, preferDirectSwap: false, slotLimit: 64, approachDeltaX: -12);
        ReorderPreviewSnapshot pushRight = FreeGridReorderEngine.ComputeLayout(
            tieSlots, sourceItemIndex: 1, targetSlot: 1, freeGridEnabled: true, preferDirectSwap: false, slotLimit: 64, approachDeltaX: +12);
        require(pushLeft.VisualSlots[0] == 0 && pushRight.VisualSlots[0] == 2,
            "Approach-vector tie-breaker must push left when approaching from right and right when approaching from left.");

        // 3. iOS 18 Multi-Icon Stack Drop onto empty area:
        int[] stackInitial = [0, 1, 2, 3, 5, 10];
        ReorderPreviewSnapshot stackEmpty = FreeGridReorderEngine.ComputeStackLayout(
            stackInitial,
            draggedIndices: [0, 2, 4],
            anchorTargetSlot: 24,
            freeGridEnabled: true,
            slotLimit: 64);
        require(stackEmpty.Kind == ReorderTargetKind.EmptySlot &&
                stackEmpty.VisualSlots[0] == 24 &&
                stackEmpty.VisualSlots[2] == 25 &&
                stackEmpty.VisualSlots[4] == 26 &&
                stackEmpty.VisualSlots[1] == 1 &&
                stackEmpty.VisualSlots[3] == 3 &&
                stackEmpty.VisualSlots[5] == 10,
            "Multi-icon stack drop onto empty region must unpack consecutively at 24..26 with zero sibling movement.");

        // 4. Multi-Icon Stack Drop onto occupied cells preserves bijection (all unique slots):
        ReorderPreviewSnapshot stackCollide = FreeGridReorderEngine.ComputeStackLayout(
            stackInitial,
            draggedIndices: [0, 2, 4],
            anchorTargetSlot: 1,
            freeGridEnabled: true,
            slotLimit: 64);
        require(stackCollide.VisualSlots.Distinct().Count() == stackInitial.Length &&
                stackCollide.VisualSlots[0] == 1 &&
                stackCollide.VisualSlots[2] == 2 &&
                stackCollide.VisualSlots[4] == 3 &&
                stackCollide.VisualSlots[5] == 10,
            "Multi-icon stack drop onto occupied cells must unpack cleanly with zero slot collisions and leave distant slot 10 untouched.");
    }

    private static void CheckArchitectureDebloatContracts(Action<bool, string> require)
    {
        string mainWindowCs = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/MainWindow.xaml.cs");
        int mainLines = mainWindowCs.Split('\n').Length;
        require(mainLines < 3800,
            $"MainWindow.xaml.cs should remain de-bloated below 3,800 lines (currently {mainLines}).");

        string transitionsCs = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/MainWindow.TransitionsAndResize.cs");
        string dragDropCs = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/MainWindow.ItemDragAndDrop.cs");
        require(transitionsCs.Contains("ExpandTransitionAsync", StringComparison.Ordinal) &&
                dragDropCs.Contains("ItemsGrid_DragOver", StringComparison.Ordinal),
            "Extracted MainWindow partial modules must house transitions/resize and item drag/drop logic.");

        string launchCs = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/Services/ShellLaunchService.cs");
        require(launchCs.Contains("ShellExecuteEx", StringComparison.Ordinal) &&
                launchCs.Contains("ShortcutIntegrityService.InspectLink", StringComparison.Ordinal) &&
                launchCs.Contains("ERROR_CANCELLED", StringComparison.Ordinal),
            "ShellLaunchService must use ShellExecuteEx with shortcut integrity inspection and UAC cancel handling.");
    }

    private static void CreateTestShortcut(
        string lnkPath,
        string targetPath,
        string workingDir,
        string arguments,
        string relativePath,
        bool runAsAdmin)
    {
        object instance = new NativeMethods.ShellLink();
        try
        {
            var link = (NativeMethods.IShellLinkW)instance;
            Marshal.ThrowExceptionForHR(link.SetPath(targetPath));
            Marshal.ThrowExceptionForHR(link.SetWorkingDirectory(workingDir));
            Marshal.ThrowExceptionForHR(link.SetArguments(arguments));
            Marshal.ThrowExceptionForHR(link.SetRelativePath(relativePath, 0));
            if (runAsAdmin && instance is NativeMethods.IShellLinkDataList dataList && dataList.GetFlags(out uint flags) >= 0)
            {
                _ = dataList.SetFlags(flags | NativeMethods.SLDF_RUNAS_USER | NativeMethods.SLDF_HAS_RELPATH);
            }
            ((IPersistFile)instance).Save(lnkPath, true);
        }
        finally
        {
            if (Marshal.IsComObject(instance))
                _ = Marshal.FinalReleaseComObject(instance);
        }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(path, recursive: true);
        }
        catch { }
    }
}
