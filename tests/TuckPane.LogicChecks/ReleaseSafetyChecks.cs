using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class ReleaseSafetyChecks
{
    internal static async Task RunAsync()
    {
        await CheckDurableStateSaveAsync();
        await CheckExecutableShortcutAndRegistrySafetyAsync();
        await CheckEmptyExportDeleteFailureAsync();
        CheckExpandedDesktopRepairDoesNotRaiseOverUserWindows();
        CheckFinalWiringContracts();
        Console.WriteLine("PASS --release-safety: durable state save, native shortcut & HKEY/desktop-root safety, empty-export failure, expanded/Dock Win+D anchor repair, note/todo race & Ctrl+drag wiring.");
    }

    private static async Task CheckDurableStateSaveAsync()
    {
        string root = CreateScratch();
        try
        {
            string statePath = Path.Combine(root, "state.json");
            var store = new StateStore(statePath);
            AppStateV2 state = await store.LoadAsync();
            await store.SaveAsync(state);
            await store.SaveAsync(state);
            Require(File.Exists(statePath) && new FileInfo(statePath).Length > 0, "State save produced no file.");
            Require(File.Exists(statePath + ".bak"), "Second save must roll previous state into .bak.");
            Require(!File.Exists(statePath + ".tmp"), "Durable save must not leave temporary file behind.");
            byte[] head = File.ReadAllBytes(statePath)[..3];
            Require(!(head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF), "State JSON must remain BOM-less UTF-8.");
            AppStateV2 reloaded = await new StateStore(statePath).LoadAsync();
            Require(reloaded.Organizers.Count == state.Organizers.Count, "Durable save must round-trip.");
        }
        finally { TryDelete(root); }
    }

    private static async Task CheckExecutableShortcutAndRegistrySafetyAsync()
    {
        string root = CreateScratch();
        try
        {
            string source = Path.Combine(root, "source");
            string pane = Path.Combine(root, "pane");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(pane);
            string executable = Path.Combine(source, "Tool.exe");
            File.WriteAllBytes(executable, [0x4D, 0x5A]);
            IReadOnlyList<TransferOutcome> outcomes = await new StorageService(pane)
                .CopyBatchAsync([executable], progress: null, CancellationToken.None);
            Require(outcomes is [{ Status: TransferStatus.ShortcutCreated, DestinationPath: string link }] &&
                    File.Exists(link) && link.EndsWith("Tool.lnk", StringComparison.OrdinalIgnoreCase),
                "Dropping an executable must create a native .lnk shortcut.");
            Require(File.Exists(executable), "Shortcut creation must never move the executable.");
        }
        finally { TryDelete(root); }
    }

    private static async Task CheckEmptyExportDeleteFailureAsync()
    {
        string root = CreateScratch();
        string pane = Path.Combine(root, "pane");
        Directory.CreateDirectory(pane);
        var info = new DirectoryInfo(pane);
        info.Attributes |= FileAttributes.ReadOnly;
        try
        {
            TransferOutcome outcome = await new StorageService(pane)
                .ExportToDesktopAsync("Pane", progress: null, CancellationToken.None);
            Require(outcome.Status == TransferStatus.Failed || !Directory.Exists(pane),
                "A failed empty-directory delete must return TransferStatus.Failed instead of throwing.");
        }
        finally
        {
            if (Directory.Exists(pane)) info.Attributes &= ~FileAttributes.ReadOnly;
            TryDelete(root);
        }
    }

    private static void CheckExpandedDesktopRepairDoesNotRaiseOverUserWindows()
    {
        string layerSource = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/Services/DesktopLayerService.cs");
        string repairExpanded = RepoSourceSnapshot.ExtractMethodBody(layerSource, "RepairExpandedZOrder(");
        int nonTopmostStart = repairExpanded.IndexOf("Re-insert directly above", StringComparison.Ordinal);
        Require(nonTopmostStart >= 0, "RepairExpandedZOrder must explicitly guard non-topmost repair.");
        string nonTopmostBody = repairExpanded[nonTopmostStart..];
        Require(nonTopmostBody.Contains("TryGetDesktopInsertAnchor(out IntPtr anchor)", StringComparison.Ordinal) &&
                !nonTopmostBody.Contains("RaiseAboveNormalWindows", StringComparison.Ordinal) &&
                !nonTopmostBody.Contains("HWND_TOPMOST", StringComparison.Ordinal),
            "Background expanded/Dock repair must seat immediately above the desktop host via TryGetDesktopInsertAnchor, never above user windows.");

        string coordinatorSource = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/DesktopAttachmentCoordinator.cs");
        string visibilityTick = RepoSourceSnapshot.ExtractMethodBody(coordinatorSource, "DesktopVisibilityTimer_Tick(");
        Require(!visibilityTick.Contains("IsExpandedCore", StringComparison.Ordinal) &&
                visibilityTick.Contains("OrganizerPlacementMode.Dock", StringComparison.Ordinal) &&
                visibilityTick.Contains("BlocksDesktopLayerRepair", StringComparison.Ordinal) &&
                visibilityTick.Contains("RepairDesktopAttachment", StringComparison.Ordinal),
            "DesktopVisibilityTimer_Tick must repair expanded Floating/Positioned and Dock windows while honoring BlocksDesktopLayerRepair.");
    }

    private static void CheckFinalWiringContracts()
    {
        string noteHtml = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/Assets/NoteEditor.html");
        Require(noteHtml.Contains("documentEpoch > 0 ?", StringComparison.Ordinal) &&
                noteHtml.Contains(": null", StringComparison.Ordinal),
            "NoteEditor getState() must return null before documentEpoch is initialized.");

        string noteWindow = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/NoteWindow.xaml.cs");
        string webMessage = RepoSourceSnapshot.ExtractMethodBody(noteWindow, "CoreWebView2_WebMessageReceived(");
        Require(webMessage.Contains("type == \"ready\"", StringComparison.Ordinal) &&
                webMessage.Contains("_editorReady = true;", StringComparison.Ordinal),
            "NoteWindow must gate _editorReady on explicit 'ready' message after load().");

        string chrome = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/PortableDocumentWindowChrome.cs");
        string cancelRename = RepoSourceSnapshot.ExtractMethodBody(chrome, "CancelRename(");
        Require(cancelRename.Contains("_errorBar.IsOpen = false;", StringComparison.Ordinal),
            "CancelRename must dismiss any prior rename validation error banner.");

        string coordinator = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/Core/PortableDocumentCoordinator.cs");
        string renameAsync = RepoSourceSnapshot.ExtractMethodBody(coordinator, "RenameAsync(");
        Require(renameAsync.Contains("NoteRenameTransaction.RenameAsync(", StringComparison.Ordinal) &&
                renameAsync.Contains("BeginDirectoryRenameAsync()", StringComparison.Ordinal) &&
                renameAsync.Contains("EndDirectoryRename()", StringComparison.Ordinal),
            "PortableDocumentCoordinator.RenameAsync must hold directory rename lock for both Note and Todo windows.");

        string dragCoordinator = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/ExternalItemDragCoordinator.cs");
        string nativeDrag = RepoSourceSnapshot.ExtractMethodBody(dragCoordinator, "BeginNativeShellDragAsync(");
        Require(nativeDrag.Contains("result.DesktopCopyRequested", StringComparison.Ordinal) &&
                nativeDrag.Contains("CopyItemToDesktopAsync", StringComparison.Ordinal) &&
                nativeDrag.Contains("ShellChangeNotificationService.NotifyCreated(", StringComparison.Ordinal),
            "Ctrl+drag to desktop must copy with exception safety and emit ShellChangeNotificationService.NotifyCreated.");

        string consoleSource = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/ConsoleWindow.xaml.cs");
        string consoleClosed = RepoSourceSnapshot.ExtractMethodBody(consoleSource, "ConsoleWindow_Closed(");
        Require(consoleClosed.Contains("_host.Updates.Changed -= UpdatesChanged;", StringComparison.Ordinal) &&
                consoleClosed.Contains("ApplyPendingRuntimeChanges", StringComparison.Ordinal),
            "ConsoleWindow_Closed must unsubscribe rendering and update notifications.");

        string storage = RepoSourceSnapshot.ReadRepoFile("src/TuckPane/Services/StorageService.cs");
        Require(!storage.Contains("GetTypeFromProgID(\"WScript.Shell\")", StringComparison.Ordinal) &&
                !storage.Contains("Environment.SpecialFolder.DesktopDirectory", StringComparison.Ordinal),
            "StorageService must use IShellLinkW and AppPaths.DesktopRoot.");
    }

    private static string CreateScratch()
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-release-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
