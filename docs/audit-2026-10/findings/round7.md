# 第七轮：总控台（ConsoleWindow）生命周期与设置持久化 + 进程级共享

审查对象：`daha1216/TuckPane` @ `7f58788`（v4.2.4）
审查方式：**静态阅读**。本机无 .NET SDK，本轮未编译、未运行。
本轮新增 6 条（P2 × 1、P3 × 5），编号域 I（总控台生命周期）、域 J（进程级共享）。

---

## 域 I：总控台生命周期与设置持久化

> 说明：退出时防抖计时器不 flush 的问题已由 **F2** 登记（`MainWindow.xaml.cs:358-361`、`ConsoleWindow.xaml.cs:147-150`），本轮不重复计数；域 I 只收 F2 未覆盖的总控台侧问题。

---

### I1 [P3] `ConsoleWindow_Closed` 不解除 `CompositionTarget.Rendering` 订阅

**位置**：`src/TuckPane/ConsoleWindow.xaml.cs:489-520`

`ConsoleWindow_Closed` 的解绑清单相当完整：解 `Loaded`/`SizeChanged`、`AttachConsoleXamlRoot(null)`、释放 `_consoleContentClip`、`_themeBackdrop.DetachAndClose()`、解 `ThemeChanged`、`_accessibilitySubscription?.Dispose()`、注销 6 个可见性 token、**Stop 六个 timer**、`_themeSurface.Dispose()`、`_settingsEdgeSurface.Dispose()`、解 `Activated`/`Closed`、`_chrome?.Dispose()`。

但**没有**解除这两处静态事件订阅：

```csharp
// :1979-1985  ScheduleRuntimeApply
CompositionTarget.Rendering += ApplyPendingRuntimeChanges;

// :1059-1070  UniformCompactScaleSlider_ValueChanged
CompositionTarget.Rendering += ApplyPendingUniformCompactScaleChanges;
```

两者都是"订阅后等下一帧自己退订"的模式（`ApplyPendingRuntimeChanges` 第一句就是 `-=`）。只要窗口在"已订阅、未到下一帧"的窗口期被关闭，订阅就永久留在 `CompositionTarget.Rendering` 上，持有整个 `ConsoleWindow` 及其可视树。由于 `CompositionTarget.Rendering` 是进程级静态事件，这不是"随窗口回收"的量级问题。

实际影响有限（每帧多一次 `ApplyPendingRuntimeChanges` 调用，且它立即 `-=`），但属于**静态事件订阅未配对**，与报告里"`CompositionTarget.Rendering` 12 处订阅全配对"的正面结论冲突——那 12 处是 `MainWindow` 侧，总控台这 2 处是遗漏。

**建议**：`ConsoleWindow_Closed` 里补

```csharp
CompositionTarget.Rendering -= ApplyPendingRuntimeChanges;
CompositionTarget.Rendering -= ApplyPendingUniformCompactScaleChanges;
```

（重复退订在 .NET 里是空操作，安全。）

---

### I2 [P3] 关闭按钮的指针热路径每次新建 `AccessibilitySettings`

**位置**：`src/TuckPane/ConsoleWindow.xaml.cs:385-398`（`SetCloseButtonState`）、`:373-383`（四个指针处理器）

```csharp
private void SetCloseButtonState(...)
{
    var accessibility = new Windows.UI.ViewManagement.AccessibilitySettings();
    bool highContrast = accessibility.HighContrast;
    ...
}
```

它被 `ConsoleCloseButton_PointerEntered` / `PointerPressed` / `PointerReleased` / `PointerExited` 四个处理器调用，即**鼠标每次进出/按下/抬起按钮都 new 一个 WinRT 对象**。

同文件里已有更省的写法可对照：

- `SetMinimizeButtonState`（`:364-371`）不新建任何对象；
- `UpdateManageListItemSurfaces`（`:1768-1788`）用的是缓存字段 `_accessibilitySettings.HighContrast`。

`AccessibilitySettings` 是 WinRT 可激活类，构造要走 COM 激活，且它自身会挂 `HighContrastChanged` 事件源。高频构造至少是明显的分配与激活开销，最坏情况是每次构造都在系统事件源上留一个未释放的订阅。

**建议**：改用已有的 `_accessibilitySettings.HighContrast`（字段已在 `:1768` 使用），或缓存一个实例。

---

### I3 [P3] `RefreshAll()` 会静默丢弃管理页里未提交的名字

**位置**：`src/TuckPane/ConsoleWindow.xaml.cs:233-255`（`RefreshAll`）、`:1709-1749`（`PopulateManageList`）、`:1799-1828`（`LoadManageEditor`）

调用链：

```
RefreshAll()
  → PopulateManageList(selectId ?? _selectedId)     // preserveEditor 默认 false
    → LoadManageEditor(id)
      → _editing = Clone(source);
        _manageNameDirty = false;
        ManageNameBox.Text = source.Name;            // 覆盖用户输入
```

`RefreshAll` 的调用点里有多个会在用户正编辑管理页时发生：

- `AppHost.cs:2574`：托盘菜单 `TrayCommand.ToggleStartup` → `Console.RefreshAll()`；
- `AppHost.cs:1881` / `:1937`：收纳窗被拖回桌面 / 被移入容器后 `Console.RefreshAll(organizer.Id)`；
- `AppHost.cs:1497` / `:1504` / `:1572` / `:1604`：创建、删除收纳窗后的刷新。

对照同文件里另外两条切换路径，作者是知道要先落盘的：

- `ManageList_SelectionChanged`（`:1751-1777`）：先 `await FlushPendingManageChangesAsync()`，失败则回滚选中项；
- `RootNavigation_SelectionChanged`（`:715-739`）：同样先 flush，失败回滚。

**只有 `RefreshAll` 跳过了 flush。** 后果：用户在名字框里打完字（尚未触发 `CommitManageNameAsync`），此时点托盘菜单切一下"开机启动"，名字输入被还原成旧值，且没有任何提示。

**建议**：`RefreshAll` 改为 `PopulateManageList(selectId ?? _selectedId, preserveEditor: _editing is not null)`；或在 `RefreshAll` 开头判断 `_manageNameDirty || _manageChangeVersion > _savedManageChangeVersion` 时先 `await FlushPendingManageChangesAsync()`（需把 `RefreshAll` 改成 async 或加一个同步的 `FlushBeforeReload` 快路径）。

---

### I4 [P3] `CaptureManageDraft` / `LoadManageEditor` 的 `.First()` 无兜底，异常会抛进渲染帧

**位置**：`src/TuckPane/ConsoleWindow.xaml.cs:1951-1952`、`:1801`、`:1987-2002`

```csharp
// CaptureManageDraft :1951-1952
OrganizerDefinition live = _host.State.Organizers.First(item => item.Id == _editing.Id);
draft.DockIconSizeDip = live.DockIconSizeDip;
draft.DockSpacingFactor = live.DockSpacingFactor;

// LoadManageEditor :1801
OrganizerDefinition source = _host.State.Organizers.First(item => item.Id == id);
```

`CaptureManageDraft` 是在 `ApplyPendingRuntimeChanges`（`CompositionTarget.Rendering` 回调）里被调用的：

```csharp
private void ApplyPendingRuntimeChanges(object? sender, object e)
{
    ...
    OrganizerDefinition draft = CaptureManageDraft();   // ← 可能抛
    string? error = _host.ApplyOrganizerRuntime(draft, changes);
    if (error is not null) { ShowError(...); LoadManageEditor(draft.Id); }  // ← 又可能抛
}
```

`ApplyPendingRuntimeChanges` 外层没有 try/catch（只有 `ShowError` 分支）。一旦 `_editing.Id` 对应的收纳窗在"已排帧、未执行"的窗口期被删除（删除路径会 `State.Organizers.RemoveAll(...)`，见 `AppHost.cs:228`），`First()` 抛 `InvalidOperationException` → 冒泡出 `CompositionTarget.Rendering` → 落到 `App.UnhandledException`（`App.xaml.cs:102-109`）被 `args.Handled = true` 吞掉（F6），**下一帧再抛一次**，因为 `_runtimeApplyScheduled` 已被置回 false 但 `_pendingVisualChanges` 仍在。

即：删掉正在编辑的收纳窗，可能进入"每帧一次静默异常"的循环，直到用户切走管理页。

**建议**：三处 `.First(` 改 `FirstOrDefault(` 并在 null 时早退（`CaptureManageDraft` 返回 null → `ApplyPendingRuntimeChanges` 直接 `return`；`LoadManageEditor` null 时 `ManageDetailCard.Visibility = Collapsed` 并清空 `_editing`）。

---

### I5 [P3] 一次滑条调整触发 5 次全量状态落盘

**位置**：`src/TuckPane/ConsoleWindow.SliderPersistence.cs`（`FlushPendingSliderSavesAsync`）、`src/TuckPane/Services/StateStore.cs:34-51`

`FlushPendingSliderSavesAsync()` 依次调用：

```
SaveHoverMagnificationSlidersAsync()   → _host.SaveStateAsync()
SaveUniformSlidersAsync()              → _host.SaveStateAsync()
SaveNameSlidersAsync()                 → _host.SaveStateAsync()
SaveDelaySlidersAsync()                → _host.SaveStateAsync()
SaveThemeAsync()                       → _host.SaveStateAsync()
```

每一次 `SaveStateAsync` 都会走完整的 `StateStore.SaveAsync`：`Normalize(state)` → 全量 `JsonSerializer.Serialize` → `File.WriteAllTextAsync(tmp)` → `File.Copy(state.json → state.json.bak)` → `File.Move(tmp → state.json)`，全程持 `_saveGate`，`MoveIntoPlaceAsync` 还带 10×50ms 重试。

也就是说，**用户松开一个滑条**（`Slider_AdjustmentCompleted`）→ 5 次全量序列化 + 5 次备份复制 + 5 次原子替换。滑条越多、`state.json` 越大（收纳窗条目多时可达数百 KB），这条路径越贵。

**建议**：`FlushPendingSliderSavesAsync` 改为"先把 5 组待提交值全部写进 `State`，再调用一次 `SaveStateAsync`"。各组现有的 `SliderSaveSequence` 已经能在 `save(capture)` 回调里捕获快照，把 5 个 `SaveXxxAsync` 的"赋值"部分抽出来、把"落盘"合并到最后一次即可，语义不变。

---

## 域 J：进程级共享（二开特有）

### J1 [P2] 二开与上游共享单实例键与状态目录：双版本互斥、互写同一份 `state.json`

**位置**：`src/TuckPane/App.xaml.cs:70`、`:241-247`、`src/TuckPane/Services/AppPaths.cs:7`、`:20`

三处硬编码的标识**完全沿用上游，没有随二开改名**：

```csharp
// App.xaml.cs:70 / :241
const string name = "TuckPane-019d2f2d-0bfb-7ff0-98f5-d93093bb0b5d";

// AppPaths.cs:7
private const string ProductDirectoryName = "TuckPane";
// AppPaths.cs:20
public static string StatePath { get; } = Path.Combine(LocalRoot, "state.json");
```

并且 `IsActivationExecutable`（`App.xaml.cs:177-195`）把任何名为 `TuckPane.exe` / `00-启动 TuckPane.exe` 的完整路径都认作"自己"：

```csharp
return Path.IsPathFullyQualified(candidate) &&
    (name.Equals("TuckPane.exe", StringComparison.OrdinalIgnoreCase) ||
     name.Equals("00-启动 TuckPane.exe", StringComparison.OrdinalIgnoreCase));
```

后果（本用户正是"先装原版、再装二开"的处境）：

1. **运行时互斥**：原版与二开同时只能有一个在跑。先启动原版、再双击二开 → 二开发现 `FindOrRegisterForKey` 不是 current → `RedirectActivation` 把激活转给原版 → 自己 `return 0` 退出。用户看到的是"点了二开没反应，反而是原版弹出来了"。
2. **状态互写**：两者都读写 `%LOCALAPPDATA%\TuckPane\state.json`（以及 `Items/`、`Windows/`、`icon-cache/`）。二开已改过 `AppStateV2` 的语义（新增 `ContainerStationId`、`ExpandedPosition` 等），原版读到后会按自己的 `Normalize` 处理，再写回时**丢掉不认识的字段**——两个版本轮流启动一次，二开的容器关系与展开位置就没了。
3. **单实例键带 `TUCKPANE_TEST_ROOT` 后缀的机制**说明作者考虑过"多实例并存"，但只用于测试，没有用于"两个发行渠道并存"。

这条与 C2（安装器共用 `AppId` 与发布者身份，`TuckPane.iss:19,24-26,48`）是同一根因在运行期的表现，但**比 C2 更早触发**：C2 需要一次覆盖安装才生效，J1 只要两个版本都装着、交替启动就会发生。

**建议**（按侵入性从小到大）：

1. 最小改动：`CreateInstanceKey` 与 `CreateSingleInstanceGuard` 的 `name` 加渠道后缀（如 `-daha`），`AppPaths.ProductDirectoryName` 同步改成 `TuckPane-Daha`，并保留一段"首次启动从 `TuckPane` 目录迁移"的逻辑；
2. 若作者希望继续与上游共享配置（比如用户从原版平滑迁移过来），至少要**禁止原版与二开同时运行**并给出明确提示，而不是静默重定向；
3. 长期方案见 C2：二开应当有独立的 `AppId`、独立的发布者身份、独立的安装目录。

---

## 本轮"防护到位"复核（正面结论）

| # | 模块 | 结论 |
|---|------|------|
| 1 | 语言资源完整性 | `en-US` / `ja-JP` / `zh-CN` 各 **429** 个键，三方**零缺失、零多余**；代码里 `AppStrings.Get/Format` 引用的 **255** 个键**全部存在**，无一个未定义键 |
| 2 | 格式化占位符 | 逐条比对 `AppStrings.Format(key, ...)` 的实参个数与三种语言里 `{n}` 的最大下标，**零越界**（不会出现 `FormatException`） |
| 3 | 管理页切换 | `ManageList_SelectionChanged` / `RootNavigation_SelectionChanged` 均先 `FlushPendingManageChangesAsync()`，失败回滚选中项，不丢改动 |
| 4 | 滑条并发 | `SliderSaveSequence` 用 `SemaphoreSlim(1,1)` + `_revision`/`_completedRevision` 循环，快照在真正序列化的回调里捕获，失败才回滚 → 快速拖动不会写出撕裂值 |
| 5 | 开关处理器 | `:897-1002` 的 5 个 Toggle 全部 `async void` 但都有 `_componentReady`/`_loadingXxx` 守卫 + try/catch + 回滚 + `ShowError` |
| 6 | 异常捕获纪律 | 全仓 28 处裸 `catch {`，逐条核对：均为"回滚后 rethrow"、"Dispose 后 rethrow"、"诊断/日志不得影响主流程"三类，**无一处静默吞掉业务错误** |
| 7 | 同步阻塞 | 全仓 `.Result` / `.Wait(` 仅 4 处，均为有超时的日志 flush（`Wait(300ms)`）或已判定完成的 `Task.Result`，**无 UI 线程同步等待** |
| 8 | 删除/重入守卫 | `DeleteOrganizerButton_Click` 有 `_deleteDialogOpen` 重入守卫 + `TransferQueue.IsActive` 守卫 + try/catch/finally |
| 9 | 编辑器字段完整性 | `ConsoleWindow.Clone` 虽少复制 5 个字段（`HideName`/`ExpandedPosition`/`ContainerOrganizerId`/`StorageOwnedByApp`/`Notes`），但 `AppHost.ApplyOrganizerRuntime` 是**逐字段赋值**、从不整体替换对象，草稿只是"设置载体"，**不是缺陷** |
| 10 | 事件订阅总账 | 全仓 `+=` / `-=` 逐事件比对，除 I1 指出的 2 处 `CompositionTarget.Rendering` 外，其余不配对项均为"随对象一起销毁"的局部订阅（timer.Tick、局部 lambda、`AppWindow` 生命周期事件），**无静态事件泄漏** |

---

## 本轮未覆盖

- `TodoWindow.xaml.cs`（919 行）与 `NoteWindow.xaml.cs`（830 行）只做了事件订阅与 `async void` 计数，未逐段精读；
- `Services/StorageService.cs`（618 行）、`Services/ShellDragService.cs`（733 行）、`Services/IconCacheService.cs`（746 行）的并发与错误路径；
- XAML 层的 Tab 顺序、`AutomationProperties`、键盘可达性；
- 全部结论仍未经过编译与运行验证（本机无 .NET SDK）。
