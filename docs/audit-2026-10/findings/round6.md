# 第六轮审查：Station 状态机 / 触摸输入 / 资源生命周期对称性

- 审查对象：`fork/` 分支 `audit424`，HEAD `7f58788 Release TuckPane 4.2.4`
- 方式：纯静态代码审查（本机无 .NET 10 SDK，未编译未运行）
- 结果：**新增 2 条 P3**（A15、C10），另为既有 F2 补充一条更强的证据链；其余 12 项抽样核查判定为防护到位

---

## 新发现

### A15 [P3] 中转站的显示器信息只解析一次，显示器拔掉/换位后热区永久错位

`src/TuckPane/MainWindow.xaml.cs:6196`（`StationPointerTimer_Tick` 的收起态分支）：

```csharp
DisplayInfo display = _stationDisplay ??= DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
```

`??=` 的语义是"只在 `_stationDisplay` 为 null 时解析一次"。而 `_stationDisplay` 一旦在初始化时被赋值（`src/TuckPane/MainWindow.xaml.cs:527`，`InitializeCoreAsync` 的 Station 分支），此后只有三处会重写：

- `:3495`（拖拽落点重算）
- `:6964`（模式切换）
- `MoveToStationPlacement`（`:7655-7669`，`_stationDisplay = DisplayPlacementService.ForBounds(anchor)`）

也就是说：**只要用户没有主动拖动或切换中转站，显示器拓扑变化不会刷新这个缓存**。

后果链条：

1. `IsStationHotZone(cursor, display, _definition.DockEdge, StationActivationDistanceDip)` 用 `display.Work` 判定光标是否进入边缘热区。
2. 显示器被拔掉后，`display.Work` 指向一块已经不存在的屏幕区域，光标永远不会落进去 → **中转站再也无法悬停展开**。
3. 显示器换了位置或改了缩放/分辨率后，热区落在错误的位置 → 表现为"边缘悬停不灵"或"在错误的地方弹出"。
4. 唯一恢复手段是拖动中转站或重启应用，用户无从得知。

对照组：Dock 走的是另一条路径 —— `src/TuckPane/MainWindow.xaml.cs:3109` 在 `WM_DISPLAYCHANGE`（`0x007E`）里对 `IsDock` 窗口重算 `_dockRegionKey` 并 `ApplyDockLayout()`。中转站没有对称处理，这与 A14（DPI/拓扑变化只处理 Dock）同源，但 A14 讲的是窗口尺寸/坐标，这条讲的是**热区判定数据**，代码路径独立。

建议修法：

```csharp
// StationPointerTimer_Tick 里，改成每次按需校验
DisplayInfo display = _stationDisplay is { } cached && DisplayPlacementService.IsStillValid(cached)
    ? cached
    : (_stationDisplay = DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice));
```

或在 `GestureWindowProc` 的 `0x007E` 分支里对 Station 窗口执行 `_stationDisplay = null;`（下次 tick 自然重解析），与 Dock 的 `_dockRegionKey = null` 完全对称 —— 后者只需一行，风险最低。

---

### C10 [P3] 文件夹右键菜单用 `%1`，驱动器根目录的尾反斜杠会吃掉收尾引号

`src/TuckPane/Services/FolderContextMenuService.cs:48-50`：

```csharp
internal static string BuildCommand(string executable, bool desktop = false) => desktop
    ? $"\"{Path.GetFullPath(executable)}\" --create-organizer"
    : $"\"{Path.GetFullPath(executable)}\" --create-organizer-in \"%1\"";
```

这段字符串被写进 `HKCU\Software\Classes\Directory\shell\...\command`（`installer/TuckPane.iss:111-138` 的 `RegisterFolderMenu` 也写同一形式）。Explorer 展开 `%1` 时**不会**帮你去掉尾反斜杠：右键驱动器根目录，`%1` = `D:\`，拼出来就是：

```
"C:\Users\...\TuckPane.exe" --create-organizer-in "D:\"
```

按 `CommandLineToArgvW` 的规则，`\` 后紧跟 `"` 表示**字面引号**，于是第三个参数变成 `D:"`（带一个引号字符）。

`src/TuckPane/Core/FolderOrganizerCreation.cs:14-20` 的 `ParseArguments`：

```csharp
if (arguments.Length != 2 || !string.Equals(arguments[0], Argument, StringComparison.OrdinalIgnoreCase) ||
    string.IsNullOrWhiteSpace(arguments[1]) || !Path.IsPathFullyQualified(arguments[1]))
    throw new ArgumentException(AppStrings.Get("FolderMenuInvalidArguments"));
```

`Path.IsPathFullyQualified("D:\"")` 为 false → 抛 `FolderMenuInvalidArguments` → 用户看到"参数无效"的错误提示，而不是建出收纳窗。UNC 根（`\\server\share\`）同理。

影响面：只影响"右键驱动器根目录 / UNC 根 → 使用 TuckPane 创建收纳窗"，普通文件夹（不以 `\` 结尾）不受影响。因此定 P3。

建议修法（两种，任选其一）：

1. **应用侧容错**（改动最小，且对已安装的注册表无需迁移）：
   ```csharp
   string candidate = arguments[1];
   if (candidate.Length > 1 && candidate.EndsWith('"')) candidate = candidate[..^1];  // "D:\" 被解析成 D:"
   ```
   再走原来的 `IsPathFullyQualified` 校验。
2. **注册侧改用不带外层引号的 `%V` + 原始命令行解析**：改动大，且要注意 `ReadCommandExecutable`（`:52-61`）是拿存储的命令串与 `BuildCommand(...)` 做**全等比较**来判定"这个菜单是不是本程序注册的" —— 一旦改了 `BuildCommand` 的输出格式，老版本注册的 `%1` 形式会被判成"别人的菜单"，卸载时 `UnregisterOwnedFolderMenu`（`installer/TuckPane.iss:140-156`）就不会清理它，留下一个指向已删除 exe 的死菜单。**若采用方案 2，必须让 `ReadCommandExecutable` 同时接受新旧两种格式。**

---

## 对既有 F2 的证据补强

F2 原文是"退出不 flush 位置防抖计时器"。本轮读到 `src/TuckPane/AppHost.cs:2398-2499` 的 `ExitCoreAsync` 全文，可以把话说得更准确：

```csharp
if (handoff is not null)
{
    await SaveStateAsync();
    await handoff();
}
```

**只有更新交接（`handoff != null`）这一条路径会在退出前落盘 `State`。** 普通退出（托盘退出、控制台关闭）全程没有 `SaveStateAsync()`：它只 flush 了主题（`Console.FlushPendingThemeSaveAsync`）、管理器改动（`Console.FlushPendingManageChangesAsync`）、便签（`NoteWindow.FlushForExitAsync`）和待办（`TodoWindow.FlushForExitAsync`）。

而两个位置防抖计时器在退出时只被 `Stop()`、不 flush：

- `_interactionSaveTimer`：`src/TuckPane/MainWindow.xaml.cs:358-361`，350 ms 一次性
- `_placementTimer`：`src/TuckPane/ConsoleWindow.xaml.cs:147-150`，450 ms 一次性

→ 用户拖动收纳窗后 350 ms 内点托盘退出，新位置丢失。窗口越小概率越低，但这是**可复现的数据丢失**，不是理论问题。修法：`ExitCoreAsync` 在 `attempt.Commit()` 之前无条件 `await SaveStateAsync()`（放在 `if (handoff is not null)` 外面），或让 `MainWindow_Closed` 在 `Stop()` 前先同步触发一次保存。

---

## 判定为防护到位的项（含证据）

| # | 项 | 证据 |
|---|---|---|
| 1 | `_shellDropFinalizing` 置位/复位配对 | 置 true `:5474`/`:5650`；复位 `:5529`/`:5542`（外层 `finally`）/`:5705`/`:5720`/`:5864`（兜底 catch）；`:5514-5549` 的 `finally` 还做了 `StopItemDragBoundaryHook()`、`FinishShellDragVisualState`、`shellSession?.Dispose()`、`ReconcileExclusiveExpansionAsync` 等收尾 |
| 2 | `_nativeMouseCapture` 不粘 | `:2576` 赋值；`:3559` 复位；`WM_LBUTTONUP`/`WM_CAPTURECHANGED`（`:3147`/`:3151`）也复位 |
| 3 | `_canvasResize` 不粘 | `MainWindow.Alignment.cs:12-14`：`if (_canvasResize is null) return; ... _canvasResize = null;` |
| 4 | `_addingItem` 不粘 | `MainWindow.OrganizerMenu.cs:79` 置 true，`:110` 在 `finally` 复位（注释说明 Shell 模态循环期间必须保持 true） |
| 5 | `CompositionTarget.Rendering`（**静态事件**）全部配对 | 12 处订阅，全部有标志位 + 配对退订：`ConsoleWindow.xaml.cs:1069/:1074`、`:1984/:1989`、`MainWindow.HoverWave.cs:294/:558`、`MainWindow.ItemBounce.cs:30/:120`、`MainWindow.xaml.cs:2262`、`:3637/:3650`、`:4365/:5756/:5803`、`:5110/:5139`、`:6541/:6590` |
| 6 | `HoverPresentation.cs:154-170` 的 9 个 lambda 订阅 | 全部挂在**每项新建的 proxy 元素**上，随条目回收，不构成静态事件泄漏 |
| 7 | 便签/待办窗口字典增删配对 | `AppHost.cs:882`/`:1333`/`:692`/`:741`/`:764`/`:813`/`:818` 均有 Remove |
| 8 | 无遗留标记 | 全仓 `TODO`/`FIXME`/`HACK`/`XXX`/`NotImplementedException`/`#warning` 零命中 |
| 9 | 空 catch 均有理由 | 16 处，绝大多数带注释（如 `AppLogger.cs:48`、`:94`、`DockRunningService.cs:153`、`TrayIconService.cs:83`/`:92`） |
| 10 | 重命名不搬目录 | `Core/OrganizerNameChange.cs` 只改 `Name` 并回滚；`AppPaths.CreateOwnedContainerName`（`:195-201`）只在**创建时**生成目录名，重命名不动 `StorageRelativePath` |
| 11 | 便携包替换不删用户文件 | `TuckPane.Updates/PortablePackage.cs:87-148`：只遍历两版清单里出现过的路径；新版本要占用的路径若已有用户文件则 `throw new IOException("An extra user file occupies a new program path: " + name)`；失败按 `touched` 逆序回滚 |
| 12 | 更新器结果文件顺序 | `TuckPane.Updater/Program.cs:55-88`：先 `Report("launched", ...)` 再 `Process.Start`，启动失败时外层 `catch` 用 `Report("failed", ex.Message)` 覆盖，最终状态正确 |

---

## 未覆盖

- 触摸/笔手势的真实行为（`PointerDeviceType.Touch` 分支只做了静态阅读，无法验证长按、双指、笔压感在真机上的表现）
- Station 在多显示器不同 DPI 下的实际热区（需要真机）
- 第六轮未再深挖 `ConsoleWindow` 的设置持久化细节（前几轮已覆盖保存/回滚路径）
