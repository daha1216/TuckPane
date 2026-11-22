using System.Xml.Linq;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

internal static class DesktopUpdateReconcilerChecks
{
    internal static async Task RunAsync()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        CheckMultiSignalMatching(Require);
        CheckRegistryPathPrefixRewriting(Require);
        CheckOriginLedgerAndSuppression(Require);
        await CheckAtomicFileAndDirectoryMergesAsync(Require);
        await CheckSettingsRoundTripAndUiContractsAsync(Require);

        Console.WriteLine("DesktopUpdateReconcilerChecks: all assertions passed.");
    }

    private static void CheckMultiSignalMatching(Action<bool, string> require)
    {
        Guid aiOrg = Guid.NewGuid();
        Guid coreOrg = Guid.NewGuid();
        Guid gameOrg = Guid.NewGuid();

        OrganizerItemSnapshot[] inventory =
        [
            new(aiOrg, "AI agent", @"C:\TuckPane\Windows\AI", "Antigravity.lnk", @"C:\TuckPane\Windows\AI\Antigravity.lnk", ".lnk", false,
                @"C:\Apps\Antigravity\Antigravity.exe", "", null, []),
            new(aiOrg, "AI agent", @"C:\TuckPane\Windows\AI", "App 1.0.lnk", @"C:\TuckPane\Windows\AI\App 1.0.lnk", ".lnk", false,
                @"D:\Tools\MyApp\MyApp.exe", "", null, []),
            new(aiOrg, "AI agent", @"C:\TuckPane\Windows\AI", "无界趣连2.0.lnk", @"C:\TuckPane\Windows\AI\无界趣连2.0.lnk", ".lnk", false,
                @"C:\leidian\wujie\2.1.23.5_20261120\wujie.exe", "", null, []),
            new(aiOrg, "AI agent", @"C:\TuckPane\Windows\AI", "SquirrelApp.lnk", @"C:\TuckPane\Windows\AI\SquirrelApp.lnk", ".lnk", false,
                @"C:\Users\mjq\AppData\Local\SquirrelApp\app-1.2.0\SquirrelApp.exe", "", null, []),
            new(aiOrg, "AI agent", @"C:\TuckPane\Windows\AI", "Chrome - jq.lnk", @"C:\TuckPane\Windows\AI\Chrome - jq.lnk", ".lnk", false,
                @"C:\Program Files\Google\Chrome\Application\chrome.exe", "--profile-directory=\"Profile 1\"", null, []),
            new(aiOrg, "AI agent", @"C:\TuckPane\Windows\AI", "打开脚本.lnk", @"C:\TuckPane\Windows\AI\打开脚本.lnk", ".lnk", false,
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-File start.ps1", null, []),
            new(gameOrg, "游戏娱乐", @"C:\TuckPane\Windows\Games", "Wallpaper Engine.url", @"C:\TuckPane\Windows\Games\Wallpaper Engine.url", ".url", false,
                null, null, "steam://rungameid/431960", []),
            new(coreOrg, "核心文件", @"C:\TuckPane\Windows\Core", "vpn", @"C:\TuckPane\Windows\Core\vpn", "", true,
                null, null, null, ["Bettbox", "FlClash", "Clash Verge", "Nano"])
        ];

        // 1. Exact .lnk filename match
        var exactLnk = new DesktopItemSnapshot(@"C:\Desktop\Antigravity.lnk", "Antigravity.lnk", ".lnk", false, false, false, false,
            @"C:\Apps\Antigravity\Antigravity.exe", "", null, [], false);
        DesktopUpdateMatch? m1 = DesktopUpdateMatchRules.FindBestMatch(exactLnk, inventory, []);
        require(m1 is { Kind: DesktopUpdateMatchKind.ShortcutExactName, TargetOrganizerId: var id1 } && id1 == aiOrg,
            "同名快捷方式未匹配到原收纳窗。");

        // 2. Renamed .lnk targeting same executable
        var renamedLnk = new DesktopItemSnapshot(@"C:\Desktop\App 2.0.lnk", "App 2.0.lnk", ".lnk", false, false, false, false,
            @"D:\Tools\MyApp\MyApp.exe", "", null, [], false);
        DesktopUpdateMatch? m2 = DesktopUpdateMatchRules.FindBestMatch(renamedLnk, inventory, []);
        require(m2 is { Kind: DesktopUpdateMatchKind.ShortcutSameTarget, TargetRelativeName: "App 1.0.lnk" },
            "同目标改名快捷方式未正确匹配。");

        // 3. Versioned install-folder path normalization (Leidian + Squirrel)
        var versionedWujie = new DesktopItemSnapshot(@"C:\PublicDesktop\无界趣连2.1.lnk", "无界趣连2.1.lnk", ".lnk", false, true, false, false,
            @"C:\leidian\wujie\2.1.24.0_20261123\wujie.exe", "", null, [], false);
        DesktopUpdateMatch? m3 = DesktopUpdateMatchRules.FindBestMatch(versionedWujie, inventory, []);
        require(m3 is { Kind: DesktopUpdateMatchKind.ShortcutVersionedTarget, TargetRelativeName: "无界趣连2.0.lnk" },
            "版本号子目录变更的快捷方式未通过版本号归一化匹配。");

        var versionedSquirrel = new DesktopItemSnapshot(@"C:\Desktop\SquirrelApp New.lnk", "SquirrelApp New.lnk", ".lnk", false, false, false, false,
            @"C:\Users\mjq\AppData\Local\SquirrelApp\app-1.3.0\SquirrelApp.exe", "", null, [], false);
        DesktopUpdateMatch? m3b = DesktopUpdateMatchRules.FindBestMatch(versionedSquirrel, inventory, []);
        require(m3b is { Kind: DesktopUpdateMatchKind.ShortcutVersionedTarget, TargetRelativeName: "SquirrelApp.lnk" },
            "Squirrel app-X.Y.Z 版本目录快捷方式未正确匹配。");

        // 4. .url target URL match
        var urlDup = new DesktopItemSnapshot(@"C:\Desktop\Wallpaper Engine 2.url", "Wallpaper Engine 2.url", ".url", false, false, false, false,
            null, null, "steam://rungameid/431960", [], false);
        DesktopUpdateMatch? m4 = DesktopUpdateMatchRules.FindBestMatch(urlDup, inventory, []);
        require(m4 is { Kind: DesktopUpdateMatchKind.ShortcutSameUrl, TargetOrganizerId: var id4 } && id4 == gameOrg,
            "相同 URL 目标的 .url 快捷方式未正确匹配。");

        // 5. Distinct Chrome profile arguments MUST NOT match
        var chromeOtherProfile = new DesktopItemSnapshot(@"C:\Desktop\Chrome - qiao.lnk", "Chrome - qiao.lnk", ".lnk", false, false, false, false,
            @"C:\Program Files\Google\Chrome\Application\chrome.exe", "--profile-directory=\"Profile 2\"", null, [], false);
        require(DesktopUpdateMatchRules.FindBestMatch(chromeOtherProfile, inventory, []) is null,
            "不同浏览器配置文件参数的快捷方式不应被误判为重复。");

        // 6. Generic shell launchers (powershell.exe) with different names MUST NOT match
        var otherScript = new DesktopItemSnapshot(@"C:\Desktop\关闭脚本.lnk", "关闭脚本.lnk", ".lnk", false, false, false, false,
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-File stop.ps1", null, [], false);
        require(DesktopUpdateMatchRules.FindBestMatch(otherScript, inventory, []) is null,
            "通用外壳启动器（powershell.exe）不同脚本不应被误匹配。");

        // 7. Folder structural overlap (exact Desktop\vpn\Bettbox scenario!)
        var vpnUpdateDir = new DesktopItemSnapshot(@"C:\Desktop\vpn", "vpn", "", true, false, false, false,
            null, null, null, ["Bettbox"], ContainsAppArtifacts: true);
        DesktopUpdateMatch? m7 = DesktopUpdateMatchRules.FindBestMatch(vpnUpdateDir, inventory, []);
        require(m7 is { Kind: DesktopUpdateMatchKind.DirectoryOverlap, TargetOrganizerId: var id7, TargetRelativeName: "vpn" } && id7 == coreOrg,
            "存在子项结构重叠的桌面更新目录（如 Desktop\\vpn\\Bettbox）未匹配到对应收纳窗。");

        // 8. Unrelated non-overlapping folder without ledger MUST NOT match
        var unrelatedDir = new DesktopItemSnapshot(@"C:\Desktop\vpn", "vpn", "", true, false, false, false,
            null, null, null, ["RandomNotes.txt"], ContainsAppArtifacts: false);
        require(DesktopUpdateMatchRules.FindBestMatch(unrelatedDir, inventory, []) is null,
            "无子项重叠且无账本记录的无关同名目录不应自动合并。");

        // 9. Ignored names & TuckPane.lnk MUST never match
        var selfLnk = new DesktopItemSnapshot(@"C:\Desktop\TuckPane.lnk", "TuckPane.lnk", ".lnk", false, false, false, false,
            @"D:\TuckPane\TuckPane.exe", "", null, [], false);
        require(DesktopUpdateMatchRules.IsIgnoredDesktopName(selfLnk.Name) &&
                DesktopUpdateMatchRules.FindBestMatch(selfLnk, inventory, []) is null,
            "TuckPane.lnk 或系统临时文件必须被安全忽略。");
    }

    private static void CheckRegistryPathPrefixRewriting(Action<bool, string> require)
    {
        const string oldPrefix = @"C:\Users\mjq\Desktop\vpn";
        const string newPrefix = @"C:\Users\mjq\TuckPane\Windows\Core\vpn";

        require(DesktopUpdateMatchRules.TryRewritePathPrefix(@"C:\Users\mjq\Desktop\vpn\Bettbox\", oldPrefix, newPrefix, out string r1) &&
                r1 == @"C:\Users\mjq\TuckPane\Windows\Core\vpn\Bettbox\",
            $"带尾斜杠安装路径重写失败：{r1}");

        require(DesktopUpdateMatchRules.TryRewritePathPrefix("\"C:\\Users\\mjq\\Desktop\\vpn\\Clash Verge\"", oldPrefix, newPrefix, out string r2) &&
                r2 == "\"C:\\Users\\mjq\\TuckPane\\Windows\\Core\\vpn\\Clash Verge\"",
            $"带引号路径重写失败：{r2}");

        require(DesktopUpdateMatchRules.TryRewritePathPrefix("\"C:\\Users\\mjq\\Desktop\\vpn\\Bettbox\\unins000.exe\" /SILENT", oldPrefix, newPrefix, out string r3) &&
                r3 == "\"C:\\Users\\mjq\\TuckPane\\Windows\\Core\\vpn\\Bettbox\\unins000.exe\" /SILENT",
            $"带卸载参数路径重写失败：{r3}");

        require(DesktopUpdateMatchRules.TryRewritePathPrefix(@"C:\Users\mjq\Desktop\vpn\Bettbox\Bettbox.exe,0", oldPrefix, newPrefix, out string r4) &&
                r4 == @"C:\Users\mjq\TuckPane\Windows\Core\vpn\Bettbox\Bettbox.exe,0",
            $"带图标索引路径重写失败：{r4}");

        require(!DesktopUpdateMatchRules.TryRewritePathPrefix(@"C:\Users\mjq\Desktop\vpn2\Bettbox\", oldPrefix, newPrefix, out _),
            "路径前缀边界校验失效：Desktop\\vpn 错误匹配到了 Desktop\\vpn2。");
    }

    private static void CheckOriginLedgerAndSuppression(Action<bool, string> require)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "TuckPane-LedgerCheck-" + Guid.NewGuid().ToString("N"));
        try
        {
            string ledgerFile = Path.Combine(tempRoot, "desktop-origins.json");
            Guid orgId = Guid.NewGuid();
            var ledger = new DesktopOriginLedger(ledgerFile);
            ledger.RecordMovedFromDesktop(@"C:\Users\mjq\Desktop\vpn", orgId, "vpn", isDirectory: true);

            // Reload from disk to verify persistence.
            var reloaded = new DesktopOriginLedger(ledgerFile);
            IReadOnlyList<DesktopOriginRecord> records = reloaded.Snapshot();
            require(records.Count == 1 && records[0].OrganizerId == orgId && records[0].RelativeName == "vpn",
                "桌面来源账本持久化往返失败。");

            // User deliberately moves or copies item back out to Desktop -> must suppress & remove from ledger on move-out.
            reloaded.RecordMovedOrCopiedToDesktop(@"C:\Users\mjq\Desktop\vpn", isCopy: false, orgId, "vpn");
            require(reloaded.IsDesktopPathSuppressed(@"C:\Users\mjq\Desktop\vpn"),
                "用户主动拖出到桌面的路径未进入防打架冷却期。");
            require(reloaded.Snapshot().Count == 0,
                "用户主动移回桌面后未清理原账本条目。");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task CheckAtomicFileAndDirectoryMergesAsync(Action<bool, string> require)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "TuckPane-MergeCheck-" + Guid.NewGuid().ToString("N"));
        try
        {
            string desktopDir = Path.Combine(tempRoot, "Desktop");
            string orgDir = Path.Combine(tempRoot, "Organizer");
            Directory.CreateDirectory(desktopDir);
            Directory.CreateDirectory(orgDir);
            Guid orgId = Guid.NewGuid();

            // A. Shortcut in-place atomic overwrite (zero "(2)" duplicate!)
            string orgUrl = Path.Combine(orgDir, "Game.url");
            string deskUrl = Path.Combine(desktopDir, "Game.url");
            await File.WriteAllTextAsync(orgUrl, "[InternetShortcut]\r\nURL=steam://rungameid/100\r\nIconIndex=0\r\n");
            await File.WriteAllTextAsync(deskUrl, "[InternetShortcut]\r\nURL=steam://rungameid/100\r\nIconIndex=1\r\n");

            DesktopItemSnapshot deskSnap = DesktopUpdateMergeEngine.InspectDesktopEntry(deskUrl, isPublicDesktop: false)!;
            var matchUrl = new DesktopUpdateMatch(deskSnap, orgId, "游戏", orgDir, "Game.url", orgUrl,
                DesktopUpdateMatchKind.ShortcutExactName, 100, false);
            DesktopUpdateMergeResult urlResult = await DesktopUpdateMergeEngine.ReconcileFileAsync(matchUrl, allowElevationUi: false, quiescenceMs: 0);
            require(urlResult.Status == DesktopUpdateMergeStatus.Merged &&
                    !File.Exists(deskUrl) &&
                    File.ReadAllText(orgUrl).Contains("IconIndex=1", StringComparison.Ordinal) &&
                    Directory.GetFiles(orgDir).Length == 1,
                "快捷方式原地覆盖合并失败或产生了多余副本。");

            // B. Recursive folder smart merge preserving unrelated sibling apps!
            string orgVpn = Path.Combine(orgDir, "vpn");
            string orgBettbox = Path.Combine(orgVpn, "Bettbox");
            string orgFlClash = Path.Combine(orgVpn, "FlClash");
            Directory.CreateDirectory(orgBettbox);
            Directory.CreateDirectory(orgFlClash);
            await File.WriteAllTextAsync(Path.Combine(orgBettbox, "version.txt"), "1.19.8-old");
            await File.WriteAllTextAsync(Path.Combine(orgFlClash, "FlClash.bin"), "untouched-sibling");

            string deskVpn = Path.Combine(desktopDir, "vpn");
            string deskBettbox = Path.Combine(deskVpn, "Bettbox");
            Directory.CreateDirectory(deskBettbox);
            await File.WriteAllTextAsync(Path.Combine(deskBettbox, "version.txt"), "1.19.9-new");
            await File.WriteAllTextAsync(Path.Combine(deskBettbox, "added.dat"), "new-asset");

            DesktopItemSnapshot deskDirSnap = DesktopUpdateMergeEngine.InspectDesktopEntry(deskVpn, isPublicDesktop: false)!;
            var matchDir = new DesktopUpdateMatch(deskDirSnap, orgId, "核心文件", orgDir, "vpn", orgVpn,
                DesktopUpdateMatchKind.DirectoryOverlap, 95, false);

            // B1. First simulate an active file lock -> MUST cleanly return BlockedByLock without modifying target!
            using (var lockStream = new FileStream(Path.Combine(deskBettbox, "version.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                DesktopUpdateMergeResult lockedRes = await DesktopUpdateMergeEngine.ReconcileDirectoryAsync(matchDir, quiescenceMs: 0);
                require(lockedRes.Status == DesktopUpdateMergeStatus.BlockedByLock &&
                        File.ReadAllText(Path.Combine(orgBettbox, "version.txt")) == "1.19.8-old",
                    "文件被独占锁定时未安全中止合并。");
            }

            // B2. Once unlocked -> MUST merge Bettbox v1.19.9 into Org\vpn\Bettbox, preserve FlClash untouched, and remove Desktop\vpn!
            DesktopUpdateMergeResult mergedRes = await DesktopUpdateMergeEngine.ReconcileDirectoryAsync(matchDir, quiescenceMs: 0);
            require(mergedRes.Status == DesktopUpdateMergeStatus.Merged &&
                    mergedRes.FilesMerged == 2 &&
                    !Directory.Exists(deskVpn) &&
                    File.ReadAllText(Path.Combine(orgBettbox, "version.txt")) == "1.19.9-new" &&
                    File.Exists(Path.Combine(orgBettbox, "added.dat")) &&
                    File.ReadAllText(Path.Combine(orgFlClash, "FlClash.bin")) == "untouched-sibling",
                "目录递归覆盖合并失败或误伤了同目录兄弟软件。");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task CheckSettingsRoundTripAndUiContractsAsync(Action<bool, string> require)
    {
        // 1. GlobalSettings.AutoSyncDesktopUpdatesEnabled defaults to true and preserves SchemaVersion == 16.
        string tempRoot = Path.Combine(Path.GetTempPath(), "TuckPane-SyncState-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            string statePath = Path.Combine(tempRoot, "state.json");
            await File.WriteAllTextAsync(statePath, """{"SchemaVersion":16,"GlobalSettings":{},"Organizers":[]}""");
            var store = new StateStore(statePath);
            AppStateV2 loaded = await store.LoadAsync();
            require(loaded.SchemaVersion == 16 && loaded.GlobalSettings.AutoSyncDesktopUpdatesEnabled,
                "旧 SchemaVersion=16 配置加载后 AutoSyncDesktopUpdatesEnabled 应默认开启。");
            loaded.GlobalSettings.AutoSyncDesktopUpdatesEnabled = false;
            await store.SaveAsync(loaded);
            AppStateV2 reloaded = await store.LoadAsync();
            require(reloaded.SchemaVersion == 16 && !reloaded.GlobalSettings.AutoSyncDesktopUpdatesEnabled,
                "AutoSyncDesktopUpdatesEnabled 开关持久化往返失败或改变了 SchemaVersion。");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }

        // 2. Verify SystemPage XAML controls & 3-language .resw key parity.
        string repoRoot = RepoSourceSnapshot.RepoRoot();
        string consoleXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "TuckPane", "ConsoleWindow.xaml"));
        foreach (string controlName in new[]
        {
            "AutoSyncDesktopUpdatesToggle",
            "ScanDesktopUpdatesButton",
            "ResolveLockedUpdatesButton",
            "RepairAppInstallPathsButton",
            "DesktopUpdateSyncStatusText"
        })
        {
            require(consoleXaml.Contains($"x:Name=\"{controlName}\"", StringComparison.Ordinal),
                $"ConsoleWindow.xaml 缺少桌面更新同步控件：{controlName}");
        }

        string[] langs = ["zh-CN", "en-US", "ja-JP"];
        var sets = new Dictionary<string, HashSet<string>>();
        foreach (string lang in langs)
        {
            XDocument doc = XDocument.Load(Path.Combine(repoRoot, "src", "TuckPane", "Strings", lang, "Resources.resw"));
            sets[lang] = doc.Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet(StringComparer.Ordinal);
        }
        require(sets["zh-CN"].SetEquals(sets["en-US"]) && sets["zh-CN"].SetEquals(sets["ja-JP"]),
            "三语 Resources.resw 键集合不对等。");
    }
}
