using TuckPane.Models;
using TuckPane.Services;

/// <summary>
/// Focused checks for the 2026-11 reliability batch (audit items F2, D1, M3, K1, K3, K5, K8, L2, I1).
/// Behavioural checks use isolated temporary directories; wiring that needs a live window is
/// verified at source level, consistent with the rest of this suite.
/// </summary>
internal static class AuditReliabilityChecks
{
    internal static async Task RunAsync()
    {
        await CheckDurableStateSaveAsync();
        await CheckExecutableShortcutAsync();
        await CheckEmptyExportDeleteFailureAsync();
        CheckSourceWiring();
        Console.WriteLine("PASS --audit-reliability: durable state save, native executable shortcut, empty-export delete failure, " +
            "exit flush / startup guard / Ctrl-copy to desktop / render unsubscribe wiring; isolated files, no GUI.");
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
            Require(File.Exists(statePath + ".bak"), "Second save must roll the previous state into .bak.");
            Require(!File.Exists(statePath + ".tmp"), "Durable save must not leave its temporary file behind.");
            byte[] head = File.ReadAllBytes(statePath)[..3];
            Require(!(head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF), "State JSON must stay BOM-less UTF-8.");
            AppStateV2 reloaded = await new StateStore(statePath).LoadAsync();
            Require(reloaded.Organizers.Count == state.Organizers.Count, "Durable save must round-trip.");
        }
        finally { TryDelete(root); }
    }

    private static async Task CheckExecutableShortcutAsync()
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
            Require(outcomes is [{ Status: TransferStatus.ShortcutCreated, DestinationPath: string link }] && File.Exists(link) &&
                    link.EndsWith("Tool.lnk", StringComparison.OrdinalIgnoreCase),
                "Dropping an executable must create a native .lnk shortcut. " + string.Join("; ", outcomes.Select(o => $"{o.Status}:{o.Message}")));
            Require(File.Exists(executable), "Creating a shortcut must never move the executable.");
        }
        finally { TryDelete(root); }
    }

    private static async Task CheckEmptyExportDeleteFailureAsync()
    {
        string root = CreateScratch();
        string pane = Path.Combine(root, "pane");
        Directory.CreateDirectory(pane);
        // Read-only directories cannot be deleted on Windows without clearing the attribute first.
        var info = new DirectoryInfo(pane);
        info.Attributes |= FileAttributes.ReadOnly;
        try
        {
            TransferOutcome outcome = await new StorageService(pane)
                .ExportToDesktopAsync("Pane", progress: null, CancellationToken.None);
            Require(outcome.Status == TransferStatus.Failed || !Directory.Exists(pane),
                "A failed empty-directory delete must surface as a Failed outcome instead of an exception.");
        }
        finally
        {
            if (Directory.Exists(pane)) info.Attributes &= ~FileAttributes.ReadOnly;
            TryDelete(root);
        }
    }

    private static void CheckSourceWiring()
    {
        string host = ReadSource("AppHost.cs");
        Require(host.Contains("try { StartupService.Apply(State.GlobalSettings.StartWithWindows); }"),
            "Startup Run-key synchronisation must not be able to abort initialisation (M3).");
        int commit = host.IndexOf("AppLogger.Lifecycle(\"exit-commit\"", StringComparison.Ordinal);
        int flush = host.LastIndexOf("try { await SaveStateAsync(); }", commit, StringComparison.Ordinal);
        Require(commit > 0 && flush > 0 && commit - flush < 400,
            "Ordinary exit must flush debounced state before committing (F2).");

        string window = ReadSource("MainWindow.xaml.cs");
        Require(window.Contains("result.DesktopCopyRequested") && window.Contains("_storage.CopyItemToDesktopAsync(dragPath"),
            "Ctrl+drag to the desktop must copy instead of move (K1).");
        int closed = window.IndexOf("private void MainWindow_Closed", StringComparison.Ordinal);
        string closedBody = closed >= 0 ? window[closed..Math.Min(window.Length, closed + 6000)] : string.Empty;
        Require(closedBody.Contains("StopHoverRendering();") && closedBody.Contains("StopItemBounceRendering();"),
            "Closing a pane must unsubscribe every CompositionTarget.Rendering handler (I1).");

        string storage = ReadSource(Path.Combine("Services", "StorageService.cs"));
        Require(!storage.Contains("GetTypeFromProgID(\"WScript.Shell\")") && !storage.Contains("Environment.SpecialFolder.DesktopDirectory"),
            "Storage must use native shell links (K3) and the sandbox-aware desktop root (K8).");

        string todo = ReadSource("TodoWindow.xaml.cs");
        Require(!todo.Contains("_saveGate.Dispose();"), "A pending to-do save must not hit a disposed gate (L2).");
    }

    private static string CreateScratch()
    {
        string root = Path.Combine(Path.GetTempPath(), "TuckPane-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string ReadSource(string relative)
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory != null)
        {
            string path = Path.Combine(directory.FullName, "src", "TuckPane", relative);
            if (File.Exists(path)) return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Cannot locate production source {relative}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
