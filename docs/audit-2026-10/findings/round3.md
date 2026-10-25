# 第三轮审查 — 未覆盖模块（TuckPane 4.2.4）

- 审查对象：`fork/` 分支 `audit424`，HEAD `7f58788 Release TuckPane 4.2.4`
- 触发：用户要求"我不相信只有这么点问题，绝对还有遗存的其他问题"
- 方法：按缺陷类型批量 grep 定位嫌疑点 → 定点读代码核验 → 判定成立/误报
- 结论：**新增 2 条 P2**（R1、R2），另有 15 个此前从未系统检查的模块判定防护到位（见 §3）
- 局限：无 .NET SDK（本机只有 .NET 8 运行时），全部结论来自静态阅读，未编译未运行

---

## 1. R1 [P2] 全局 UI 异常处理器无条件吞掉所有异常

**位置**：`src/TuckPane/App.xaml.cs:100-110`

```csharp
public App()
{
    InitializeComponent();
    UnhandledException += (_, args) =>
    {
        AppLogger.Error("Unhandled UI exception", args.Exception);
        AppLogger.Lifecycle("ui-exception", $"type={args.Exception.GetType().FullName} hresult=0x{args.Exception.HResult:X8} handled=true");
        // Keep recoverable async-void/UI callback failures from terminating
        // the process. Native fail-fast crashes are still handled by WER.
        args.Handled = true;
    };
}
```

**机制**：`args.Handled = true` 是**无条件**的，对所有异常类型一视同仁。

**为什么是问题**：

1. 全仓有 **86 处 `async void`**（`MainWindow.xaml.cs` 26、`ConsoleWindow.xaml.cs` 24、`NoteWindow` 8、`TodoWindow` 5 等）。这个处理器让它们不会崩进程——这是设计目的，没错。但代价是：**任何** UI 回调里的 `NullReferenceException`、`InvalidOperationException`、`ArgumentException` 都被静默记录后继续运行。
2. 应用会**带着可能已损坏的中间状态继续运行**。例如某个 `async void` 在"改了一半设置"时抛异常，后续逻辑基于半成品状态执行，用户看到的是行为异常而非错误提示。
3. 用户永远看不到错误。`AppLogger.Error` 写的是 `diagnostics/runtime.*.jsonl`，普通用户不会去看。
4. 开发期同样掩盖真实缺陷——这类"吞异常"的代码会让回归测试和手工测试都发现不了问题。
5. 次要问题：处理器内部调 `AppLogger.Error` → `DiagnosticRecord` 序列化 → 若此时日志写入器正在 Dispose（退出流程），二次异常会从这个处理器里逃出，`Handled` 已无意义，进程直接终止。

**建议**：改成白名单——只对明确可恢复的异常类型（如已知的 COM 环境差异 `COMException`、`OperationCanceledException`）置 `Handled = true`，其余记录后**不**置位，让进程按正常路径崩溃并由 WER 收集。或者在 `Handled = true` 的同时给用户一个可见提示（InfoBar / 托盘气泡），至少让用户知道"刚才那次操作失败了"。

**归属**：上游遗留（`git log -S "args.Handled = true"` 可查），非二开引入。

---

## 2. R2 [P2] 资源管理器重启后托盘图标不重建

**位置**：`src/TuckPane/Services/TrayIconService.cs`

```csharp
// :43  构造函数内，仅此一次
bool iconAdded = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _data);
// :45
bool versionSet = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref _data);

// :49-54  切换语言时只发 MODIFY
public void ApplyLanguage()
{
    _data.szTip = AppStrings.Get("AppTitle");
    _data.uFlags = NativeMethods.NIF_TIP | NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON;
    _ = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref _data);
}
```

**机制**：`Shell_NotifyIcon(NIM_ADD)` 注册的图标属于**当前 explorer.exe 实例**。资源管理器崩溃或被重启后，任务栏重建，所有注册全部丢失。标准做法是：用 `RegisterWindowMessage("TaskbarCreated")` 拿到广播消息，收到后重新 `NIM_ADD`。

**实测**：全仓 `TaskbarCreated`、`RegisterWindowMessage` **零命中**（`grep -rn "TaskbarCreated\|RegisterWindowMessage" src/ --include=*.cs` 无输出）。`NIM_ADD` 全仓也只有 `TrayIconService.cs:43` 一处（构造函数）。

**后果**：资源管理器重启后，托盘图标**永久消失**，只能重启 TuckPane 恢复。用户失去：托盘菜单、显示/隐藏收纳窗、退出入口。而 `ApplyLanguage()` 走的是 `NIM_MODIFY`，图标已不存在时该调用静默失败（`_ =` 丢弃返回值），不会自愈。

**对比**：同一个项目对"资源管理器重启"在**桌面层**是有恢复逻辑的——`DesktopLayerService` 在 `_desktopIconView` 失效时会 `FindDesktopIconView()` 重新枚举窗口（`src/TuckPane/Services/DesktopLayerService.cs:60-64`）。托盘侧缺了对称的恢复路径，属于遗漏而非有意取舍。

**建议**：

```csharp
// 构造函数里
_taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");

// WindowProc 里（已有子类化，只需加一个分支）
if (message == _taskbarCreated)
{
    _ = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _data);
    _ = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref _data);
}
```

`WindowProc` 已经是子类化回调（`TrayIconService.cs:71` 起），加分支成本极低。

**归属**：上游遗留，非二开引入。

---

## 3. 判定"防护到位"的模块（本轮首次系统检查）

以下每条都回代码核验过，**不是"没看"，是"看了确认没问题"**。列出来是为了让审查覆盖面可复核。

| # | 模块 | 结论 | 关键证据 |
|---|------|------|----------|
| 1 | 便签 WebView2 编辑器 | 存储型 XSS 链断裂 | `NoteWindow.xaml.cs:303-319` 禁右键/DevTools/新窗口，`NavigationStarting` 只放行 `_editorUri`；`WebMessageReceived` 校验 `args.Source`（`:333-340`）；渲染走白名单消毒器 `cleanHtml`（`Assets/NoteEditor.html:128-163`），只保留 `img`（data URL 正则）/`br`/`div`/`p`，属性重建 |
| 2 | 拖放递归防护 | 三向守卫齐全 | `Core/DropValidator.cs:71-77` 同时拒绝"源=根""源是根祖先""源在根内"；`:58` 与 `:138-143` 逐项拒绝 ReparsePoint；`StorageService.ImportBatchAsync:42-51` 先整体校验再移动 |
| 3 | 诊断日志有界 | 有硬上限 | `Services/DiagnosticLogWriter.cs:8-9` 5MB × 4 文件；`:30-36` `Channel.CreateBounded(2048)`；`:47-52` 写满即丢弃并计数 |
| 4 | 退出时便签保存 | 失败会取消退出 | `AppHost.cs:2436-2455` 逐个 `FlushForExitAsync()`，失败则 `Lifecycle("exit-cancelled")` + `ShowAndActivate()` + `return false` |
| 5 | 便签关窗即隐藏 | 设计正确，非泄漏 | `NoteWindow.xaml.cs:809-814` 非永久关闭时 `args.Cancel = true` + `HideAsync()` |
| 6 | 事件订阅配对 | 全部成对 | `ConsoleWindow.xaml.cs:121`/`:502`、`MainWindow.xaml.cs:322`/`:7086`、`OwnedDialogWindow.cs:122`/`:305`；无静态事件 |
| 7 | culture 与解析纪律 | 全仓干净 | 无未指定比较方式的 `StartsWith/EndsWith/IndexOf/ToLower/ToUpper`；无裸 `int.Parse`/`double.Parse`/`Convert.ToInt32`；`DateTime.Now` 仅 2 处且均用于文件名 |
| 8 | 设置界面初始化守卫 | 5 个处理器全带守卫 | `ConsoleWindow.xaml.cs:54-69` 15 个 `_loading*`；`:1061`/`:1143`/`:1266`/`:1437`/`:1447` 逐一核对 |
| 9 | 滑块防抖保存 | 退出前冲刷 | `ConsoleWindow.SliderPersistence.cs:43-57` 停 6 个计时器并逐个保存 |
| 10 | 跨进程内存 | try/finally 配对 | `DesktopGridService.cs:296-376`，所有早退都走 `finally` 的 `VirtualFreeEx` + `CloseHandle` |
| 11 | 显示器回退 | 有回退与重映射 | `DisplayPlacementService.cs:66-68`、`:346-351` |
| 12 | 进程启动参数 | 无注入面 | `WindowsCrashEvents.cs:33-44` 用 `ArgumentList` 逐项传参；`FolderContextMenuService.cs:48-61` 覆写前校验命令指向本程序 |
| 13 | 数值边界 | 双向 Clamp | `Models/OrganizerLimits.cs` 全套常量；`ConsoleWindow.xaml.cs:2188-2205` |
| 14 | HICON 释放 | 正确（含所有权转移点） | `IconCacheService.cs:35/288/303/348-351/429-433/558`、`TrayIconService.cs:63`；`GetFallbackIcon:574-585` 属所有权转移 |
| 15 | OLE 拖放 COM 释放 | try/finally 全覆盖 | `ShellDragService.cs:293`/`:547`（dataObject）、`:404`（DragDropHelper）、`:426`/`:626`（item）、`:341`+`:372`（`Marshal.Release(dropSourcePointer)`）+ `OleUninitialize()` + `GC.KeepAlive` |
| 16 | 托盘窗口生命周期 | 句柄不会失效 | `ConsoleWindow.xaml.cs:449` `args.Cancel = true` + `:342` `Hide()`，窗口不销毁 |
| 17 | 运行中应用探测 | COM 全部 finally 释放 | `DockRunningService.cs:51/108/182/186/191/225` |
| 18 | `ValidationUpdateHandler` 泄漏面 | 生产构建不包含 | `AppUpdateService.cs:33-38` 整块在 `#if TUCKPANE_UPDATE_VALIDATION` 内 |

---

## 4. 未覆盖部分（诚实声明）

- **动态行为**：所有 Win32/COM 行为均为静态推演，未在真机上验证（无 .NET SDK，无法编译）。
- **`TodoWindow`（919 行）内部逻辑**：只核了与便签对称的关闭/保存路径，未逐行读。
- **`MainWindow.HoverWave`（631 行）动画状态机**：只核了计时器生命周期，未核动画状态转移。
- **安装器 `[Code]` 段**（`installer/TuckPane.iss:97` 起）：只核了注册表写入内容，未核 Pascal 脚本分支。
- **更新包 `PortablePackage.Apply` 的回滚路径**：前两轮读过，未构造失败场景验证。

---

## 5. 本轮的判断

三轮下来，**代码质量本身不是这个项目的主要风险**。这一轮我按缺陷类型扫了 18 个从未系统检查过的模块，只找出 2 条 P2，而且都是上游遗留、与二开无关。

真正的风险仍然是那两条，且都不是"代码写得对不对"能解决的：

1. **更新源是一个个人 GitHub 账号**（`daha1216/TuckPane`），安装包没有数字签名。任何能拿到该账号的人（或 GitHub 账号被盗）都能推一个"更新"到你机器上，而你现有的防护（`SHA256SUMS.txt`）和安装包放在同一个 release 里，同源，防不了这个。
2. **安装器身份仍是上游的**（`installer/TuckPane.iss:24-26,48` 全是 `ch998244353`，`AppId` 与上游相同）。这不是漏洞，但意味着"控制面板里看到的是谁"和"实际在跑谁的代码"对不上，且两条渠道可以互相静默覆盖。

所以如果问"还能不能放心用"——能，前提是你接受"信任朋友这个账号"。如果哪天要降低这个依赖，最小动作是：给安装包加代码签名（哪怕自签名 + 固定指纹校验），或者干脆只在需要时手动装、关掉应用内自动更新。
