# TuckPane 架构评估与优化建议书

- 对象：`daha1216/TuckPane` @ `7f58788`（4.2.4）
- 规模：`src/TuckPane` 164 个 `.cs` / 34536 行；全仓 214 个 `.cs` / 47988 行（含测试 12985 行）
- 视角：不评价功能，只评价**结构能否支撑继续演进**

---

## §0 一句话结论

**这套代码的局部质量不差，差的是边界。** 更新链（`PortablePackage`、`UpdateFiles`）和日志隐私（`AppLogger` + `DiagnosticRecord`）这两块写得比多数商业软件都严谨；但 `MainWindow` 一个类 10355 行、~280 字段、~379 方法，`AppHost` 2606 行，两者之间 142 处反向调用——**架构的复杂度集中在两个点上，而这两个点恰好是所有功能都要经过的地方。**

结果就是 4.2.4 的那个 bug：桌面层附着逻辑明明已经写好了自愈（`RepairDesktopAttachment`），但因为"收起态"和"展开态"这两条路径分散在 `MainWindow` 的三个地方（计时器、Tick、`DesktopLayerService`），修的时候只覆盖了收起态，展开态被漏掉，而且**没有任何测试能发现这个遗漏**。

---

## §1 现状度量

### 1.1 体积分布

| 文件 | 行数 |
|---|---|
| `src/TuckPane/MainWindow.xaml.cs` | 7997 |
| `src/TuckPane/AppHost.cs` | 2606 |
| `src/TuckPane/ConsoleWindow.xaml.cs` | 2248 |
| `src/TuckPane/TodoWindow.xaml.cs` | 919 |
| `src/TuckPane/NoteWindow.xaml.cs` | 830 |
| `src/TuckPane/Services/NativeMethods.cs` | 799 |
| `src/TuckPane/Services/IconCacheService.cs` | 746 |
| `src/TuckPane/Services/ShellDragService.cs` | 733 |
| `src/TuckPane/MainWindow.HoverWave.cs` | 631 |
| `src/TuckPane/Services/StorageService.cs` | 618 |
| `src/TuckPane/Services/DisplayPlacementService.cs` | 611 |
| `src/TuckPane/Models/AppState.cs` | 589 |

### 1.2 `MainWindow` 分部文件全清单（18 个，合计 10355 行）

| 分部文件 | 行数 |
|---|---|
| `MainWindow.xaml.cs` | 7997 |
| `MainWindow.HoverWave.cs` | 631 |
| `MainWindow.HoverPresentation.cs` | 354 |
| `MainWindow.Dock.cs` | 208 |
| `MainWindow.ExpansionMode.cs` | 200 |
| `MainWindow.OrganizerMenu.cs` | 158 |
| `MainWindow.ItemBounce.cs` | 124 |
| `MainWindow.AnimationDiagnostics.cs` | 110 |
| `MainWindow.DockToolTips.cs` | 102 |
| `MainWindow.CompactHighlight.cs` | 84 |
| `MainWindow.OrganizerTitle.cs` | 84 |
| `MainWindow.DockRunning.cs` | 70 |
| `MainWindow.CompactInput.cs` | 61 |
| `MainWindow.CompactResize.cs` | 60 |
| `MainWindow.Alignment.cs` | 47 |
| `MainWindow.ContentScale.cs` | 19 |
| `MainWindow.WidgetDragActivation.cs` | 15 |
| `MainWindow.IncomingDrop.cs` | 13 |

**观察**：分部文件是**按"功能片段"切的，不是按"职责层"切的**。`MainWindow.xaml.cs` 独占 77%，剩下的 17 个文件加起来 2358 行。这意味着 `partial` 只是把文件变短了，**类的复杂度一点没降**——所有字段、所有方法仍在同一个类型里，任何一处改动都可能影响其余 379 个方法。

### 1.3 复杂度热点

| 指标 | 数值 | 说明 |
|---|---|---|
| `MainWindow` 字段声明 | ~280 | 一个类型持有 280 份状态，几乎不可能被完整理解 |
| `MainWindow` 方法 | ~379 | |
| `MainWindow` 调用 `_host.` | **142** | 双向耦合：`AppHost` 创建 `MainWindow`，`MainWindow` 又反过来驱动 `AppHost` |
| `async void` | **86** | 无法被 `await`、无法被测试、异常只能靠全局兜底 |
| 即发即忘 | **32** | 同上 |

### 1.4 最大的方法（>100 行）

| 方法 | 行数 | 位置 |
|---|---|---|
| `BeginXamlShellDrag` | 175 | `MainWindow.xaml.cs:5552` |
| `ExpandTransitionAsync` | 172 | `MainWindow.xaml.cs:1665` |
| `CommitItemReorderAsync` | 136 | `MainWindow.xaml.cs:4914` |
| `PrepareItemElement` | 127 | `MainWindow.xaml.cs:1318` |
| `BeginNativeShellDrag` | 126 | `MainWindow.xaml.cs:5426` |
| `InitializeCoreAsync` | 122 | `MainWindow.xaml.cs:511` |
| `ImportFromDragAsync` | 113 | `MainWindow.xaml.cs:6350` |
| `FinishCompactPressCoreAsync` | 113 | `MainWindow.xaml.cs:3430` |
| `StationPointerTimer_Tick` | 112 | `MainWindow.xaml.cs:6162`（`async void`） |
| `PasteMenuItem_Click` | 109 | `MainWindow.xaml.cs:6611`（`async void`） |

---

## §2 五个结构性问题

### 问题 1：`MainWindow` 是上帝对象

**证据**：10355 行 / ~280 字段 / ~379 方法。

**具体后果**（不是理论担忧）：
- 桌面层自愈要同时改 `_desktopVisibilityTimer`（字段）、`DesktopVisibilityTimer_Tick`（方法）、`RefreshPerformanceSettings`（启停）、`MainWindow_Closed`（清理）四个地方。4.2.4 的 bug 就出在第四个地方漏了一行——而前三个地方在同一个文件里，第四个在 700 行之外。
- 280 个字段意味着构造函数/初始化顺序是隐式的、无法验证的。`InitializeCoreAsync` 122 行里任何一个 `await` 之后，都可能有另一条路径已经改了字段状态。
- 任何"这个窗口现在是什么状态"的问题，答案只能靠通读 10355 行。

### 问题 2：`MainWindow` ↔ `AppHost` 双向耦合

**证据**：`MainWindow` 里 142 处 `_host.` 调用。

正常的层次是：`AppHost`（应用级）→ 创建/持有 → `MainWindow`（窗口级）。单向依赖。
实际是双向：`MainWindow` 直接调用 `_host.CreateTodoAsync(...)`、`_host.CloseExpansionModeCoordinator(...)`、`_host.EndOrganizerDragHover(this)`……

**后果**：无法单独构造一个 `MainWindow` 做测试（必须造一个完整的 `AppHost`，而 `AppHost` 又要文件系统、要 Shell 交互、要消息循环）。这就是为什么 `DesktopLayerService` 这种**纯逻辑 + 少量 Win32** 的类，至今没有一行测试。

### 问题 3：静态可变全局状态

**证据**：
- `AppPaths.cs:9` `private static readonly string? TestRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");` —— 静态只读但**在类型初始化时求值**，此后所有路径（`DesktopRoot`、`UserProfile`、`LocalAppData`）都依赖它。
- `AppStrings.Language` 静态可变。

**后果**：任何依赖 `AppPaths` 的类型都无法在测试里被隔离到临时目录（除非设环境变量并重启进程——这正是 `TUCKPANE_TEST_ROOT` 存在的原因，也是它必须留在正式版里的原因：**测试基础设施的缺陷，被一个运行时开关补上了**）。

### 问题 4：`async void` 与即发即忘

**证据**：86 处 `async void`、32 处即发即忘。

**公平地说**：这不是灾难。`App.xaml.cs` 的三层全局兜底（`AppDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException`、`UnhandledException`）确实接住了。所以**当前不会静默崩溃**。

**但代价是**：
- 异常路径变成"记录 + 可能退出"，无法在调用点做有意义的恢复。
- `async void` 方法无法被 `await`，因此无法在测试里等待它完成——`StationPointerTimer_Tick`（112 行）和 `PasteMenuItem_Click`（109 行）这两个最复杂的方法，测试上完全不可达。
- 时序问题只能靠人肉推理。

### 问题 5：服务没有生命周期管理

**证据**：`DesktopLayerService`、`IconCacheService`、`ShellDragService`、`TrayIconService` 等，全部由 `AppHost` / `MainWindow` 手工 `new` 并在特定位置手工 `Dispose`。

**后果**：4.2.3 的 F3（`_desktopVisibilityTimer` 漏 `Stop()`）和 F11（`RepairDesktopAttachment` 返回旧状态）本质上是同一个问题：**"谁负责在什么时机清理/更新这个服务"没有唯一答案**，只能靠每个调用点自己记得。

---

## §3 重构路线（分四阶段，每阶段独立可交付）

> 原则：**不做"大重写"**。这个项目在活跃开发，任何需要长期停摆的重构都不会被执行。每阶段都应该是"能在一周内合入、合入后立刻减少一类 bug"的改动。

### 阶段 0：先建安全网（1 天）

**目标**：让后续每一步重构都有回归保护。

1. 新增 `.github/workflows/ci.yml`（`pull_request` + `push: main`），跑 `dotnet build -c Release` + `LogicChecks --stability` + `UpdateChecks --updates`。
2. 给 `DesktopLayerService` 补第一批单元测试——**不需要 Win32**，把 `ProbeDesktopAttachment` 的判定逻辑抽成纯函数（输入：`expanded`/`windowValid`/`cloaked`/`ownerMatches`/`visible`/`behindDesktop`；输出：`DesktopAttachmentState`），这样 5 个分支全可测。

**收益**：4.2.4 那个 bug 在阶段 0 之后就不会发生——因为"展开态应该返回什么"会变成一个被测试钉死的行为。

### 阶段 1：收敛桌面层附着（2-3 天）

**目标**：把散在 `MainWindow` 四处的桌面层逻辑收进一个类。

新建 `src/TuckPane/Services/DesktopAttachmentCoordinator.cs`：

```csharp
internal sealed class DesktopAttachmentCoordinator : IDisposable
{
    private readonly DesktopLayerService _layer;
    private readonly Func<bool> _shouldWatch;      // 由 MainWindow 注入，替代 4 处守卫
    private readonly DispatcherQueueTimer _timer;

    public DesktopAttachmentState Evaluate(WindowStateSnapshot snapshot);  // 纯判定
    public void Apply(DesktopAttachmentState state, WindowStateSnapshot snapshot);  // 修复动作
    public void Start(); public void Stop(); public void Dispose();          // 生命周期唯一入口
}
```

`MainWindow` 只保留一个字段 `_desktopAttachment` 和两处调用（`Start`/`Stop`）。计时器启停、`Closed` 清理、Tick 守卫全部收敛到 `Coordinator` 内部。

**收益**：消除 F3/F4/F11/N2 这一整类问题。新增一个窗口状态（比如"展开中"）时，只需要改 `WindowStateSnapshot` 和 `Evaluate`，不会漏掉某个 `Stop()`。

### 阶段 2：拆 `MainWindow`（1-2 周，可分批）

**目标**：把 10355 行按"职责"而不是"功能片段"切开。

建议的切分（按依赖度从低到高，可逐个独立合入）：

| 新类型 | 从 `MainWindow` 迁出 | 预估行数 |
|---|---|---|
| `CompactCardPresenter` | 紧凑态视觉：`CompactHighlight`、`ItemBounce`、`HoverWave`、`HoverPresentation`、`ContentScale` | ~1200 |
| `DockPresenter` | `Dock`、`DockRunning`、`DockToolTips` | ~380 |
| `ExpansionController` | `ExpansionMode`、`ExpandTransitionAsync` | ~400 |
| `ShellDragCoordinator` | `BeginXamlShellDrag`、`BeginNativeShellDrag`、`IncomingDrop`、`WidgetDragActivation` | ~500 |
| `OrganizerChromeController` | `OrganizerTitle`、`OrganizerMenu`、`Alignment` | ~300 |

每个 `Presenter` 通过一个窄接口与 `MainWindow` 交互（只暴露它真正需要的几个方法），而不是拿整个 `MainWindow` 引用。

**注意**：这一步风险最高，必须**先有阶段 0 的测试**，且一次只迁一个。

### 阶段 3：把 `_host.` 反向调用改成接口（3-5 天）

**目标**：解除 `MainWindow` → `AppHost` 的双向耦合。

把 `MainWindow` 实际用到的 `AppHost` 能力提取成一个接口（按 142 处调用的实际用途聚类，估计 5-8 个方法组）：

```csharp
internal interface IOrganizerHost
{
    Task SaveStateAsync();
    Task<string> CreateTodoAsync(Guid organizerId, POINT anchor);
    void EndOrganizerDragHover(MainWindow window);
    void CloseExpansionModeCoordinator(Guid organizerId);
    // ...
}
```

`MainWindow` 构造参数从 `AppHost` 变成 `IOrganizerHost`。这样测试里可以传一个假实现。

**收益**：`MainWindow` 第一次变得**可构造**——这是所有 UI 层测试的前提。

---

## §4 具体的、可以现在就做的 10 个改动

按"性价比"排序（收益 / 风险）：

| # | 改动 | 位置 | 预估 | 收益 |
|---|---|---|---|---|
| 1 | 加 `ci.yml` | 新增 | 30 行 | 从此 PR 有检查 |
| 2 | `StateStore` 加 fsync | `Services/StateStore.cs:36-52` | 15 行 | 消除断电丢配置 |
| 3 | 展开态纳入探针 | `Services/DesktopLayerService.cs:195`、`MainWindow.xaml.cs:7026` | 20 行 | 消掉已承认的已知问题 |
| 4 | 参数层统一 `IsLocalPath` | `App.xaml.cs:197-237` | 15 行 | 消除 UNC 凭据泄露 |
| 5 | `TUCKPANE_TEST_ROOT` 用 `#if DEBUG` 隔离 | `Services/AppPaths.cs:9` | 4 行 | 去掉正式版隐藏开关 |
| 6 | `ProbeDesktopAttachment` 抽成纯函数 | `Services/DesktopLayerService.cs:195-227` | 40 行 | 5 个分支可测 |
| 7 | 计时器走 `PerformanceTuning` 档位 | `MainWindow.xaml.cs:342-345` + `Models/AppState.cs:207-212` | 10 行 | 省电档真正省电 |
| 8 | `CleanupSessions` 顺带清 `config-backups` | `Services/AppUpdateService.cs:157-183` | 15 行 | 停止无限增长 |
| 9 | 保存失败加一次 UI 提示 | `AppHost.Dock.cs:26` | 20 行 | 用户知道发生了什么 |
| 10 | `.iss` 身份改成本 fork | `installer/TuckPane.iss:24-26,48` | 6 行 | 安装器显示正确的发布者 |

第 10 项有一个**必须先做的决策**：`AppId` 要不要跟着改？改了 = 系统视为新软件，老用户无法平滑升级、卸载残留；不改 = 卸载时仍会去动上游的键。建议**只改显示信息（Publisher/URL/Company），不动 `AppId`**，把共存问题留到以后单独处理。

---

## §5 不建议做的事

1. **不要整体重写成 MVVM 框架**。这套代码的交互（Win32 子类化、Shell 拖拽、DWM 合成、桌面层 z-order）根本不是 MVVM 能表达的，强行套只会增加中间层。
2. **不要为了"减少 async void"而机械改写**。86 处里大部分是事件处理器，`async void` 是正确的签名。真正该改的是那两个 100+ 行的（`StationPointerTimer_Tick`、`PasteMenuItem_Click`）——它们该拆成"事件处理器 + 可 await 的核心方法"两段。
3. **不要先动 `AppHost`**。它是第二大的，但它的问题主要是"方法太多"，而不是"职责混乱"。等 `MainWindow` 拆完，`AppHost` 自然瘦下来。
4. **不要在阶段 0 之前做任何重构**。没有 CI 和测试的重构，等于把已知 bug 换成未知 bug。

---

## §6 如果只做一件事

**加 `ci.yml`。**

理由：现在这个仓库的改进完全依赖"某个人记得在本地跑测试"。PR #1 之所以质量不错，是因为我在提交信息里写了验证声明、朋友自己也跑了一遍——但这是**两个人碰巧都很谨慎**，不是系统的保证。有了 CI，`build-release.ps1` 里那些检查（版本号一致、必需文件齐全、无私有文件泄漏）才会在**每次改动**时执行，而不是等到打 tag。

这一个文件的价值，超过其余九项改动的总和。
