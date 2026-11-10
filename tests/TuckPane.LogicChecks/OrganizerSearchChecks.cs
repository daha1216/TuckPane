using System.Xml.Linq;
using TuckPane.Core;
using TuckPane.Models;

/// <summary>"Find in organizers": ranking, filtering and wiring. No window is created.</summary>
internal static class OrganizerSearchChecks
{
    internal static void Run()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        OrganizerSearchCandidate[] items =
        [
            new(a, "Work", 0, "Quarterly Report", "Quarterly Report.pdf", @"C:\w\Quarterly Report.pdf", WidgetItemKind.File),
            new(a, "Work", 0, "report", "report.txt", @"C:\w\report.txt", WidgetItemKind.File),
            new(b, "Home", 1, "Reports", "Reports", @"C:\h\Reports", WidgetItemKind.Folder),
            new(b, "Home", 1, "Café menu", "Café menu.docx", @"C:\h\Café menu.docx", WidgetItemKind.File),
            new(b, "Home", 1, "Visual Studio Code", "Visual Studio Code.lnk", @"C:\h\Visual Studio Code.lnk", WidgetItemKind.Shortcut),
            new(a, "Work", 0, "budget", "budget.xlsx", @"C:\w\budget.xlsx", WidgetItemKind.File),
        ];

        Require(OrganizerSearch.Search(items, "").Count == 0 && OrganizerSearch.Search(items, "   ").Count == 0,
            "An empty query lists nothing (the window shows a summary instead).");
        string[] report = Names(OrganizerSearch.Search(items, "report"));
        Require(report.Length == 3 && report[0] == "report" && report[1] == "Reports" && report[2] == "Quarterly Report",
            "Exact name beats prefix, prefix beats a later word start. Got: " + string.Join(", ", report));
        Require(Names(OrganizerSearch.Search(items, "cafe")).SequenceEqual(["Café menu"]), "Accents and case must be ignored.");
        Require(Names(OrganizerSearch.Search(items, "ｂｕｄｇｅｔ")).SequenceEqual(["budget"]), "Full-width input must match.");
        Require(Names(OrganizerSearch.Search(items, "vsc")).SequenceEqual(["Visual Studio Code"]), "Initials must match as a subsequence.");
        Require(Names(OrganizerSearch.Search(items, "report pdf")).SequenceEqual(["Quarterly Report"]),
            "All tokens must match; the extension is searchable through the file name.");
        Require(OrganizerSearch.Search(items, "zzz").Count == 0, "Unmatched queries must return nothing.");
        Require(OrganizerSearch.Search(Enumerable.Range(0, 200).Select(i =>
                new OrganizerSearchCandidate(a, "W", 0, $"file {i}", $"file {i}.txt", $@"C:\f{i}", WidgetItemKind.File)), "file").Count == OrganizerSearch.DefaultLimit,
            "Results must be capped so typing stays responsive.");
        Require(OrganizerSearch.ScoreToken("x", "x") > OrganizerSearch.ScoreToken("xy", "x") &&
                OrganizerSearch.ScoreToken("ab x", "x") > OrganizerSearch.ScoreToken("abx", "x"),
            "Score order must be exact > prefix > word start > substring.");

        Require(new GlobalSettings().SearchHotkey is null && QuickHideHotkey.FindSearch("ctrl+alt+f")?.Id == "Ctrl+Alt+F" &&
                QuickHideHotkey.SearchPresets.All(preset => preset.Id != "Ctrl+Shift+F"),
            "The search shortcut is opt-in and never steals Ctrl+Shift+F from editors.");

        string host = ReadSource("AppHost.cs");
        Require(host.Contains("case TrayCommand.Search:") && host.Contains("ShowSearch();") &&
                host.Contains("QuickHideHotkey.FindSearch(State.GlobalSettings.SearchHotkey) is { } searchKey && !_tray.SetSearchHotkey(searchKey)"),
            "Tray, shortcut and startup must all reach the search window.");
        int register = host.IndexOf("!_tray.SetSearchHotkey(QuickHideHotkey.FindSearch(next))", StringComparison.Ordinal);
        Require(register > 0 && host.IndexOf("State.GlobalSettings.SearchHotkey = next;", StringComparison.Ordinal) > register,
            "The search shortcut must be registered before it is saved.");
        Require(host.Contains("await Task.Run(() =>") && host.Contains("createIfMissing: false"),
            "Indexing must run off the UI thread and must never create missing storage directories.");
        string app = ReadSource("App.xaml.cs");
        Require(app.Contains("values.Contains(\"--search\", StringComparer.OrdinalIgnoreCase)") && app.Contains("_host!.ShowSearch();"),
            "TuckPane.exe --search must open the window, so external launchers can reach it.");
        string tray = ReadSource(Path.Combine("Services", "TrayIconService.cs"));
        Require(tray.Contains("wParam.ToUInt64() == SearchHotkeyId") && tray.Contains("_ = SetSearchHotkey(null);") &&
                tray.Contains("(UIntPtr)TrayCommand.Search"),
            "The tray host must dispatch, release and list the search command.");
        string window = ReadSource(Path.Combine("Services", "OrganizerSearchWindow.cs"));
        Require(window.Contains("WindowActivationState.Deactivated") && window.Contains("VirtualKey.Escape") &&
                window.Contains("reveal: false") && window.Contains("VirtualKey.Control"),
            "The search window closes on Esc or when switching away, and supports open and show-in-folder.");

        foreach (string language in new[] { "en-US", "zh-CN", "ja-JP" })
        {
            var names = XDocument.Parse(ReadSource(Path.Combine("Strings", language, "Resources.resw"))).Root!.Elements("data")
                .Select(element => (string)element.Attribute("name")!).ToHashSet();
            foreach (string key in new[] { "SearchHotkeyTitle", "TraySearch", "SearchWindowTitle", "SearchPlaceholder", "SearchLoading",
                         "SearchNoResults", "SearchSummaryFormat", "SearchHint", "SearchResultLocationFormat" })
                Require(names.Contains(key), $"{language} is missing {key}.");
        }

        Console.WriteLine("PASS --organizer-search: ranking (exact/prefix/word/substring/initials), accent/width-insensitive, multi-token, capped; " +
            "opt-in shortcut, tray/startup wiring, off-thread non-creating index, three languages. No window or global hotkey.");
    }

    private static string[] Names(IReadOnlyList<OrganizerSearchResult> results) => results.Select(result => result.Candidate.DisplayName).ToArray();

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
