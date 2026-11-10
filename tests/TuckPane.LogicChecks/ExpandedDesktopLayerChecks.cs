/// <summary>
/// Known issue 1 in 4.2.4-4.2.6 release notes: after Win+D an expanded pane (always the case for
/// permanently expanded panes) stayed behind the wallpaper. The repair itself needs a live shell, so
/// it was verified by hand with Win+D on a desktop session; this guards the wiring at source level.
/// </summary>
internal static class ExpandedDesktopLayerChecks
{
    internal static void Run()
    {
        string layer = ReadSource(Path.Combine("Services", "DesktopLayerService.cs"));
        int repair = layer.IndexOf("internal bool RepairExpandedLayer()", StringComparison.Ordinal);
        string body = repair >= 0 ? layer[repair..layer.IndexOf("private void ReassertDesktopAttachment()", repair, StringComparison.Ordinal)] : "";
        Require(body.Contains("if (!_expanded || _stayTopmost") && body.Contains("if (!IsBehindDesktopLayer()) return false;") &&
                body.Contains("if (!TryGetDesktopInsertAnchor(out IntPtr anchor)) return false;") &&
                !body.Contains("HWND_TOP,") && !body.Contains("HWND_TOPMOST"),
            "Expanded repair must only act on a buried, non-topmost pane and re-insert it directly above the desktop host, never above user windows.");

        string window = ReadSource("MainWindow.xaml.cs");
        int tick = window.IndexOf("private void DesktopVisibilityTimer_Tick", StringComparison.Ordinal);
        string tickBody = tick >= 0 ? window[tick..window.IndexOf("private bool DesktopLayerBusy", tick, StringComparison.Ordinal)] : "";
        Require(!tickBody.Contains("_closing || _expanded ||") && tickBody.Contains("desktopLayer.RepairExpandedLayer()"),
            "The desktop visibility timer must keep watching expanded Floating/Positioned panes.");
        Require(tickBody.IndexOf("DesktopLayerBusy", StringComparison.Ordinal) < tickBody.IndexOf("RepairExpandedLayer", StringComparison.Ordinal),
            "Expanded repair must respect the shared interaction guard (menus, drags, overlays).");

        Console.WriteLine("PASS --expanded-desktop-layer: expanded panes are re-inserted above the desktop host after Win+D, guarded, never raised over user windows. Source-level; live Win+D verified manually.");
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
