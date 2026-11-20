using System.Diagnostics;

internal static class CiHeadlessRunner
{
    private static readonly TimeSpan PerSuiteTimeout = TimeSpan.FromSeconds(90);

    private static readonly string[][] Suites =
    [
        ["--stability", "storage"],
        ["--stability", "queue"],
        ["--stability", "lifecycle"],
        ["--stability", "drop"],
        ["--stability", "launch"],
        ["--stability", "watcher"],
        ["--stability", "diagnostics"],
        ["--stability", "diagnostics-limits"],
        ["--folder-context-menu"],
        ["--launch-environment"],
        ["--dock-mode", "state"],
        ["--dock-mode", "layout"],
        ["--dock-mode", "hover"],
        ["--dock-mode", "theme"],
        ["--dock-mode", "commands"],
        ["--dock-mode", "refinements"],
        ["--permanent-expanded"],
        ["--expansion-mode-switch"],
        ["--note-todo-42"],
        ["--note-save-failure"],
        ["--appearance-corners-defaults"],
        ["--organizer-hover-wave"],
        ["--organizer-alignment-menu-name"],
        ["--organizer-animation"],
        ["--vertical-dock-visuals"],
        ["--dock-bounce-amplitude"],
        ["--click-bounce-compact-hover"],
        ["--settings-dock-adjustments"],
        ["--settings-management"],
        ["--backdrop-lifetime"],
        ["--theme-opacity-blur-arc"],
        ["--compact-dock-input"],
        ["--dock-hover-tuning", "stability"],
        ["--dock-hover-tuning", "padding"],
        ["--dock-hover-tuning", "settings"],
        ["--dock-polish", "tooltip"],
        ["--dock-polish", "running"],
        ["--dock-stability", "resize"],
        ["--dock-stability", "hover"],
        ["--sep11-fixes", "all"],
        ["--seven-features", "all"],
        ["--settings-appearance", "all"],
        ["--settings-compact-followup", "all"],
        ["--sep13-interactions", "resize"],
        ["--sep13-interactions", "menu"],
        ["--delete-choice"],
        ["--station-startup-activation"],
        ["--interaction-recovery-wheel"],
        ["--default-storage-directory"],
        ["--performance-profile"],
        ["--unified-compact-scale"],
        ["--window-alignment"],
        ["--theme-material-removal"],
        ["--theme-targets"],
        ["--unified-theme"],
        ["--release-safety"],
        ["--startup-placement"],
        ["--endgame-productivity"],
        ["--native-desktop-fusion"],
        ["--ios-free-grid"],
        ["--desktop-move-8x8"]
    ];

    internal static async Task RunAsync()
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine LogicChecks executable path.");
        string repoRoot = RepoSourceSnapshot.RepoRoot();
        string tempParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var failures = new List<string>();
        var totalClock = Stopwatch.StartNew();

        foreach (string[] suiteArgs in Suites)
        {
            string label = string.Join(' ', suiteArgs);
            string isolatedRoot = Path.Combine(tempParent, "TuckPane-ci-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(isolatedRoot);
            try
            {
                var startInfo = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = repoRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                startInfo.Environment["TUCKPANE_TEST_ROOT"] = isolatedRoot;
                foreach (string arg in suiteArgs) startInfo.ArgumentList.Add(arg);

                var suiteClock = Stopwatch.StartNew();
                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"Failed to start child process for {label}.");
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                using var cts = new CancellationTokenSource(PerSuiteTimeout);
                bool exited = true;
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    exited = false;
                    try { process.Kill(entireProcessTree: true); } catch { }
                }

                string stdout = (await stdoutTask).Trim();
                string stderr = (await stderrTask).Trim();
                if (!exited)
                {
                    string message = $"TIMEOUT ({PerSuiteTimeout.TotalSeconds:F0}s): {label}";
                    Console.Error.WriteLine(message);
                    failures.Add(message);
                    continue;
                }

                if (process.ExitCode != 0)
                {
                    string details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    string message = $"FAIL (exit {process.ExitCode}): {label}\n{details}";
                    Console.Error.WriteLine(message);
                    failures.Add($"{label} (exit {process.ExitCode})");
                    continue;
                }

                Console.WriteLine($"[PASS {suiteClock.ElapsedMilliseconds,4} ms] {label}");
            }
            finally
            {
                string full = Path.GetFullPath(isolatedRoot);
                if (string.Equals(Path.GetDirectoryName(full), tempParent, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(full).StartsWith("TuckPane-ci-", StringComparison.Ordinal))
                {
                    try { if (Directory.Exists(full)) Directory.Delete(full, recursive: true); }
                    catch { }
                }
            }
        }

        Console.WriteLine(
            $"CI headless summary: {Suites.Length - failures.Count}/{Suites.Length} suites passed in {totalClock.Elapsed.TotalSeconds:F2}s.");
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"CI headless LogicChecks failed ({failures.Count}/{Suites.Length}): {string.Join(", ", failures)}");
        }
    }
}
