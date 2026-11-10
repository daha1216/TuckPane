using System.Xml.Linq;
using TuckPane.Core;
using TuckPane.Models;

/// <summary>
/// Quick-hide hotkey, item Open/Show in folder/Properties and one-shot pane sorting.
/// Pure ordering is checked behaviourally; window wiring is checked at source level.
/// </summary>
internal static class UxQuickActionsChecks
{
    internal static void Run()
    {
        CheckSortOrder();
        CheckMenuVisibility();
        CheckWiring();
        CheckResources();
        Console.WriteLine("PASS --ux-quick-actions: natural/type/date sorting with folders first, sort menu visibility, " +
            "quick-hide hotkey wiring and default, item shell commands, three languages; no GUI or global hotkey registered.");
    }

    private static void CheckSortOrder()
    {
        DateTime t = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        OrganizerSortEntry[] entries =
        [
            new("file10.txt", "file10", WidgetItemKind.File, ".txt", t.AddDays(1)),
            new("file2.txt", "file2", WidgetItemKind.File, ".txt", t.AddDays(3)),
            new("Alpha.pdf", "Alpha", WidgetItemKind.File, ".pdf", t),
            new("Zeta", "Zeta", WidgetItemKind.Folder, "", t.AddDays(9)),
            new("beta", "beta", WidgetItemKind.Folder, "", t),
            new("organizer:1", "Child pane", WidgetItemKind.Organizer, "", DateTime.MinValue),
            new("App.lnk", "App", WidgetItemKind.Shortcut, ".lnk", t.AddDays(2)),
        ];

        Require(OrganizerSortOrder.Sort(entries, OrganizerSortKey.Name).SequenceEqual(
                ["organizer:1", "beta", "Zeta", "Alpha.pdf", "App.lnk", "file2.txt", "file10.txt"]),
            "Name sort must put panes, then folders first and use natural (file2 < file10) case-insensitive order.");
        Require(OrganizerSortOrder.Sort(entries, OrganizerSortKey.Type).SequenceEqual(
                ["organizer:1", "beta", "Zeta", "App.lnk", "Alpha.pdf", "file2.txt", "file10.txt"]),
            "Type sort must group by extension after panes and folders.");
        Require(OrganizerSortOrder.Sort(entries, OrganizerSortKey.DateModified).SequenceEqual(
                ["organizer:1", "Zeta", "beta", "file2.txt", "App.lnk", "file10.txt", "Alpha.pdf"]),
            "Date sort must be newest first within each group.");
        Require(OrganizerSortOrder.Sort([], OrganizerSortKey.Name).Count == 0, "Sorting nothing must return nothing.");

        OrganizerSortEntry[] ties =
        [
            new("b.txt", "same", WidgetItemKind.File, ".txt", t),
            new("a.txt", "same", WidgetItemKind.File, ".txt", t),
        ];
        Require(OrganizerSortOrder.Sort(ties, OrganizerSortKey.Name).SequenceEqual(["a.txt", "b.txt"]) &&
                OrganizerSortOrder.Sort(ties.Reverse(), OrganizerSortKey.Name).SequenceEqual(["a.txt", "b.txt"]),
            "Equal display names must sort deterministically by file name.");
    }

    private static void CheckMenuVisibility()
    {
        var settings = new GlobalSettings();
        foreach (OrganizerPlacementMode mode in Enum.GetValues<OrganizerPlacementMode>())
            Require(OrganizerMenuVisibility.VisibleActions(settings, mode).Contains("ContextSortBy"),
                $"Sort must be available by default for {mode}.");
        settings.OrganizerMenuVisibility = OrganizerMenuVisibility.Actions.ToDictionary(key => key, key => key == "ContextSortBy");
        string[] onlySort = OrganizerMenuVisibility.VisibleActions(settings, OrganizerPlacementMode.Floating);
        Require(onlySort.SequenceEqual(["ContextSortBy"]) && !OrganizerMenuVisibility.ShowSeparator(onlySort),
            "Sort belongs to the item-command group and must not leave a dangling separator on its own.");
        settings.OrganizerMenuVisibility["ContextManage"] = true;
        Require(OrganizerMenuVisibility.ShowSeparator(OrganizerMenuVisibility.VisibleActions(settings, OrganizerPlacementMode.Floating)),
            "Sort plus a window command must keep the separator between groups.");
        Require(new GlobalSettings().QuickHideHotkey is null, "The global shortcut must stay opt-in for existing and new users.");
        Require(QuickHideHotkey.Find(null) is null && QuickHideHotkey.Find("") is null && QuickHideHotkey.Find("Win+L") is null &&
                QuickHideHotkey.Normalize("ctrl+alt+h") == "Ctrl+Alt+H",
            "Unknown or empty shortcut ids must mean off; known ids normalise case-insensitively.");
        Require(QuickHideHotkey.Presets.Select(preset => preset.Id).Distinct().Count() == QuickHideHotkey.Presets.Length &&
                QuickHideHotkey.Presets.All(preset => (preset.Modifiers & (QuickHideHotkey.ModControl | QuickHideHotkey.ModAlt)) != 0 && (preset.Modifiers & 0x0008) == 0),
            "Presets must be unique, need Ctrl or Alt, and never use the Windows key.");
    }

    private static void CheckWiring()
    {
        string tray = ReadSource(Path.Combine("Services", "TrayIconService.cs"));
        Require(tray.Contains("hotkey.Modifiers | MOD_NOREPEAT, hotkey.VirtualKey") &&
                tray.Contains("previous.Modifiers | MOD_NOREPEAT, previous.VirtualKey") &&
                tray.Contains("message == WM_HOTKEY && wParam.ToUInt64() == QuickHideHotkeyId") &&
                tray.Contains("_ = SetQuickHideHotkey(null);"),
            "The tray host must register without repeat, restore the previous shortcut on conflict, dispatch WM_HOTKEY and release on dispose.");

        string host = ReadSource("AppHost.cs");
        Require(host.Contains("case TrayCommand.ToggleAll:") && host.Contains("if (_allHiddenByUser) ShowAllWindows();"),
            "The hotkey must toggle relative to the last explicit show/hide.");
        Require(host.Contains("QuickHideHotkey.Find(State.GlobalSettings.QuickHideHotkey) is { } quickHide && !_tray.SetQuickHideHotkey(quickHide)"),
            "A saved shortcut must be re-registered at startup.");
        int register = host.IndexOf("!_tray.SetQuickHideHotkey(QuickHideHotkey.Find(next))", StringComparison.Ordinal);
        int save = host.IndexOf("State.GlobalSettings.QuickHideHotkey = next;", StringComparison.Ordinal);
        Require(register > 0 && save > register, "A shortcut must be registered before it is saved, so a taken one is refused.");

        string console = ReadSource("ConsoleWindow.xaml.cs");
        int handler = console.IndexOf("private async void QuickHideHotkeyCombo_SelectionChanged", StringComparison.Ordinal);
        int handlerEnd = console.IndexOf("private async void WindowAlignmentToggle_Toggled", handler, StringComparison.Ordinal);
        string handlerBody = handler >= 0 && handlerEnd > handler ? console[handler..handlerEnd] : string.Empty;
        Require(handlerBody.Contains("RevertQuickHideHotkeyCombo();") && !handlerBody.Contains("UpdateQuickHideHotkeyCombo();") &&
                console.Contains("DispatcherQueue.TryEnqueue(() =>"),
            "A refused shortcut must be reverted after SelectionChanged returns; rebuilding the items inside it crashed WinUI.");

        string window = ReadSource("MainWindow.xaml.cs");
        Require(window.Contains("AddFileShellCommands(flyout, item);"), "File items must offer Open, Show in folder and Properties.");
        string xaml = ReadSource("MainWindow.xaml");
        Require(xaml.Contains("x:Name=\"CompactSortByMenuItem\"") && xaml.Contains("x:Name=\"ExpandedSortByMenuItem\""),
            "Both pane menus must carry the Sort by submenu.");
    }

    private static void CheckResources()
    {
        string[] keys =
        [
            "QuickHideHotkeyTitle", "QuickHideHotkeyErrorTitle", "QuickHideHotkeyOff", "QuickHideHotkeyInUseFormat",
            "ContextSortBy", "ContextSortByName", "ContextSortByType", "ContextSortByDate",
            "ContextOpenItem", "ContextRevealItem", "ContextItemProperties"
        ];
        foreach (string language in new[] { "en-US", "zh-CN", "ja-JP" })
        {
            XDocument document = XDocument.Parse(ReadSource(Path.Combine("Strings", language, "Resources.resw")));
            // Some legacy keys appear twice in the .resw files; the first definition wins at runtime.
            var names = document.Root!.Elements("data")
                .GroupBy(element => (string)element.Attribute("name")!)
                .ToDictionary(group => group.Key, group => (string?)group.First().Element("value"));
            foreach (string key in keys)
                Require(names.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value),
                    $"{language} is missing {key}.");
        }
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
