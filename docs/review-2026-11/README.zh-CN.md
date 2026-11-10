# TuckPane 4.2.6 架构与体验评审（2026-11）

> 范围：`daha1216/TuckPane` main（4.2.6，`601948b`）全部源码、测试、脚本与文档。
> 方法：通读核心模块，对照 `docs/audit-2026-10` 的 77 条结论逐条回归，与同类产品逐项比对功能，并在隔离环境（`TUCKPANE_TEST_ROOT`）中实际运行自包含构建。
> 配套 PR：[#6 可靠性修复](https://github.com/daha1216/TuckPane/pull/6)，[#7 快速隐藏 / 项目菜单 / 排序](https://github.com/daha1216/TuckPane/pull/7)。本文档本身是第三个 PR，不含代码。

---

## 0. 结论先行

TuckPane 的“底子”在同类开源项目里属于上游水平：真实目录模型、原子状态存储、带回滚的便携更新、白名单诊断、单实例激活收件箱、STA Shell 启动队列，这些都是 Fences 类开源替代品（BirdyFences、Desktop Fences+、DeskFrame、PecoFence）普遍没有的工程质量。

真正的短板不在“能不能用”，而在两处：

1. **可演进性**。`MainWindow` 由 18 个分部文件组成，共约 10,400 行，一个类同时承担四种放置模式、两种内容模式、动画、拖放、桌面层自愈和 Shell 交互。上一轮审计里反复出现的同类缺陷（A1、A13、I1、本轮 L2）根子都在这里：守卫条件散落在几十个 `_xxxActive` 标志上，每加一个计时器或渲染订阅，就要记得在关闭路径里手动对称清理一次。
2. **日常效率功能的缺口**。“收纳”做得很精致，但“找到”和“整理”还停在手工阶段：没有搜索、没有键盘操作、没有自动归类规则、没有一键清空桌面（#7 补上了最后一项和排序）。

所以建议的方向是**渐进式重构**，而不是推倒重来。一个 4 万行、直接操作用户真实文件、用户基础已经在用的桌面程序，重写的风险远大于收益。正确的路线是先用测试把行为钉住，再按职责把 `MainWindow` 一块块拆出去。

---

## 1. 架构评审

### 1.1 做得好、应当保留的设计

| 模块 | 评价 |
|---|---|
| `StateStore` | 临时文件、`.bak` 加原子改名，`Normalize` 集中收敛非法状态，字段缺失时有默认值，因此不必频繁提升 Schema。（#6 补上了 fsync。） |
| `TuckPane.Updates` + Updater | zip-slip 全维度拒绝、逐文件哈希、journal 回滚、父进程身份三重校验、asInvoker。这是全仓库最严谨的一条链。 |
| 诊断体系 | `DiagnosticRecord` 白名单、有界队列、轮转文件、导出时二次过滤，不收集任何隐私数据。 |
| `ActivationInbox` / `ShellLaunchService` / `TransferQueue` | 把跨线程、跨进程的竞态收敛到少数几个串行化点。 |
| `Core/*Math` | 几何、动画、对齐等纯函数已经独立出来，可以测试。这正是后续拆分 `MainWindow` 的抓手。 |

### 1.2 结构性问题

**A. `MainWindow` 过于臃肿**（约 10.4k 行，18 个 partial 文件）

- 职责混杂：窗口放置（Floating、Positioned、Station、Dock）、展开与收起状态机、项目渲染、拖放（OLE 拖入、Shell 拖出、内部重排）、桌面层自愈、悬浮放大、对齐和设置投影，全都在一个类里。
- 生命周期靠手工对称维护：`MainWindow_Closed` 要逐个停掉十几个计时器和渲染订阅。#6 的 I1 就是又漏了两个。
- partial 文件只是“按文件切开”，并不是“按职责隔离”：每个 partial 都能直接读写全部私有字段。

**建议的拆分顺序**（每一步都能单独合并，行为保持不变）：

1. `WindowResources`：统一登记计时器、`CompositionTarget.Rendering` 订阅、钩子和子窗口，关闭时一次性释放。改动最小，能直接消灭“漏停”这一整类缺陷。
2. `DesktopLayerSupervisor`：把桌面层自愈（`DesktopRepairTimer`、`ProbeDesktopAttachment`、可见性计时器）连同它的守卫一起抽出来，对外只暴露 `IsBusy` 这一个查询。这也就是审计里的 H1，同时是修复 A8（展开态按 Win+D 后被壁纸盖住）的前置条件。
3. `ExpansionStateMachine`：把 `_expanded`、`_animating`、`_transitionRequestVersion` 等状态收敛成显式状态和迁移规则，迁移逻辑可以纯测试。
4. `ItemsPresenter`：项目目录、排序、重排会话和渲染，以及本次新增的 `MainWindow.ItemActions.cs`。
5. 放置策略按模式分出 `IPlacementStrategy`（Floating、Positioned、Station、Dock），消掉遍布全类的 `if (IsDock)` 和 `PlacementMode == Station` 分支。

**B. `AppHost` 的上帝协调者倾向**（约 3k 行）

它同时管状态、窗口注册表、便签和待办的迁移、托盘、更新交接和全部 `SetXxxAsync` 设置写入。建议至少把“设置写入加回滚”抽成一个 `SettingsWriter`：现在约 20 个 `SetXxxAsync` 方法都是同一个模板（赋值 → 保存 → 失败回滚 → 广播），非常适合泛化。

**C. 全局 UI 异常被无条件吞掉**（审计 F6，仍未决策）

`App.UnhandledException` 一律设 `Handled = true`。好处是不崩，代价是缺陷被静默掩盖，用户看到的只是“某个按钮没反应”。建议：已知可恢复的异常类型（`COMException` 中与窗口已关闭相关的 HRESULT、`ObjectDisposedException`）继续吞掉；其余异常记录诊断，同时在总控台的更新/诊断页亮一个“发生过 N 次内部错误，导出诊断”的入口。

**D. 测试体系**

- `TuckPane.LogicChecks` 是一个手写的参数分发器，`Program.cs` 约 5k 行，包含 71 个 `if (args …)` 入口，套件按日期命名（`--sep08-fixes`、`--aug31-…`）。除了作者本人，没人知道发版前该跑哪些。
- 建议：引入 xUnit 或 MSTest。纯逻辑测试（`Core/*`、`StateStore`、`StorageService`、`OrganizerSortOrder` 等）迁过去，按模块而不是按日期命名，`dotnet test` 一条命令跑完。需要 WinAppRuntime 的部分单独放进一个 `[Trait("needs-desktop")]` 类别。
- CI 现在只跑 `UpdateChecks`。纯逻辑测试一旦迁出，大部分可以在托管 runner 上运行（它们本来就不需要 Windows App Runtime 的 bootstrap）。

**E. 文档散落**

根目录有 `PLAN.md`（停在 4.2.1）和 `WINDOW_ALIGNMENT_PLAN.md`，`docs/` 下有 `PROJECT_ANALYSIS`（75 KB，混杂了大量版本流水）、四份诊断/手测文档，外加 `audit-2026-10`。建议整理为四类：`docs/architecture.md`（只写当前架构）、`docs/testing.md`（发版前必须跑什么）、`docs/manual-checks/`、`RELEASE_NOTES.md`；过期计划移进 `docs/archive/`。

### 1.3 安全与信任（沿用审计，需维护者决策）

- **C1 更新包缺少独立信任锚**。包没有签名，哈希和包来自同一个来源。最低成本的做法是在 `update-files.json` 里加 Ed25519 签名，程序内置公钥。
- **C2 / J1 fork 与上游共用 AppId、单实例键和状态目录**。两个版本不能同时安装，至少应当写进 README。

---

## 2. 体验评审（按用户旅程）

| 旅程 | 现状 | 问题 | 建议 |
|---|---|---|---|
| 首次启动 | 托盘加总控台 | 新用户默认没有收纳窗，桌面上什么都看不到，不知道下一步做什么 | 首启时创建一个示例收纳窗，并在总控台显示 3 步引导：拖文件进来、Ctrl+滚轮缩放、右键看更多 |
| 收纳文件 | 拖入，可选移动或复制 | ✅ 成熟 | — |
| 找文件 | 展开后靠眼睛扫 | 文件一多就找不到，没有搜索和过滤 | 展开态直接打字即可过滤（type-to-filter），参考 DeskFrame 的 instant search |
| 整理 | 只能手动拖 | 没有排序，没有规则 | #7 已加入一键排序；下一步做**规则**（见 3.1） |
| 键盘操作 | 只有 Esc | Enter、Delete、F2、Ctrl+C/X/V、方向键都不响应 | 为展开态引入焦点项模型（见 3.2） |
| 项目菜单 | 只有重命名、复制、剪切、删除 | 没有“在文件夹中显示”“属性”“打开方式”，也看不到 Windows 原生菜单 | #7 已加入打开、在文件夹中显示、属性；下一步托管 `IContextMenu`，提供“显示更多选项” |
| 清空桌面 | 托盘 → 隐藏全部 | 路径太深 | #7 已加入快捷键；下一步支持“双击桌面空白处切换”（Fences 的核心体验，需要桌面鼠标钩子，选做） |
| 多显示器 / 分辨率变化 | 有 Normalize | 换显示器后布局被挤压，无法恢复 | **布局快照**：按显示器拓扑保存，拓扑变化时自动切换（Fences 也有） |
| 已知缺陷 | 4.2.6 自列 3 条 | A8：展开态按 Win+D 后被壁纸盖住（最影响观感） | 随“DesktopLayerSupervisor”的拆分一起修 |

---

## 3. 新功能设计稿（按投入产出排序）

### 3.1 自动归类规则（Rules）—— 最高价值

**痛点**：下载和截图不断落到桌面上，用户需要反复手动拖进收纳窗。

**设计**：
- 每个收纳窗可配置 0 到 N 条规则，条件包括扩展名集合、文件名通配符、来源目录（桌面 / 下载）和“超过 N 天未修改”，动作为移入本窗。
- 监视对象只限 `Desktop` 和 `Downloads` 两个目录（已有 `WatcherRecovery` 可以复用）。新文件稳定 2 秒后再判定，避免移动正在写入的下载。
- 所有移动走现有的 `TransferQueue` 和 `StorageService.ImportBatchAsync`，失败保留源文件，同时进入“最近自动归类”列表，支持一键撤销。
- 默认关闭，并提供“试运行”：只列出将会移动哪些文件，不实际执行。
- 数据模型：`OrganizerDefinition.Rules: List<OrganizerRule>`，缺失时为空，不需要提升 Schema。

### 3.2 键盘与焦点模型

- 展开时，项目区获得逻辑焦点项（Station 是 `WS_EX_NOACTIVATE`，保持现状，不处理键盘）。
- 按键映射：方向键移动焦点；Enter 打开；F2 重命名（复用 `ShowRenameFileDialogAsync`）；Delete 删除到回收站（复用 `DeleteFileItemAsync`）；Ctrl+C/X/V 复制、剪切、粘贴；直接打字进入过滤（与 3.3 合并）。
- 焦点环沿用主题边缘高光，不另外引入新的视觉元素。

### 3.3 即时搜索 / 过滤

- 展开态打字后，在标题下方出现一个过滤条；过滤只作用于渲染，不改变 `ItemOrder`。Esc 先清空过滤，再按一次才收起窗口。
- 进阶版本：总控台提供全局搜索，跨所有收纳窗匹配文件名，回车即展开对应的窗口并高亮该项。

### 3.4 布局快照

- 以“显示器设备 ID + 分辨率 + DPI”的集合作为键，保存所有收纳窗的 `Position`、`ExpandedPosition` 和 `DockCenter`。
- `WM_DISPLAYCHANGE` 时如果有匹配的快照就自动套用；没有匹配时保持现状，并提示“为当前显示器布局保存快照”。

### 3.5 原生 Shell 菜单（显示更多选项）

在项目菜单末尾加一项“显示更多选项”，托管 `IContextMenu` / `IContextMenu3`，提供 7-Zip、Git 等第三方扩展。需要在 STA 线程上处理菜单消息的转发，工作量中等，风险可控。

---

## 4. 建议路线图

| 阶段 | 内容 | 产出 |
|---|---|---|
| **立即** | 合并 #6、#7；手测 K1（Ctrl+拖到桌面）、排序和项目菜单 | 4.3.0 |
| **一** | `WindowResources` 统一释放；修 A8；F6 定策略；纯逻辑测试迁到 xUnit 并接入 CI | 稳定性基线 |
| **二** | 键盘与焦点模型、即时过滤、原生 Shell 菜单 | 效率体验 |
| **三** | 自动归类规则（含试运行和撤销）、布局快照 | 差异化功能 |
| **四** | `DesktopLayerSupervisor`、`ExpansionStateMachine` 和放置策略的拆分；C1 签名 | 可演进性与信任 |

---

## 5. 同类产品对照

| 能力 | Fences 5（商业） | DeskFrame | Desktop Fences+ | TuckPane 4.2.6 | 本轮之后 |
|---|---|---|---|---|---|
| 真实目录收纳（Folder Portal） | ✅ | ✅ | 部分 | ✅ | ✅ |
| 收起 / 展开 | ✅ Roll-up | ✅ | ✅ | ✅（动画更好） | ✅ |
| 一键清空桌面 | ✅ 双击桌面 | ? | ? | 托盘菜单 | ✅ 快捷键 |
| 自动归类规则 | ✅ | ? | ? | — | 设计稿 3.1 |
| 搜索 | ? | ✅ | ? | — | 设计稿 3.3 |
| 排序 | ✅ | ? | ? | — | ✅ |
| 便签 / 待办 | — | — | — | ✅ 独有 | ✅ |
| 边缘中转站 / Dock | — | — | — | ✅ 独有 | ✅ |
| 应用内更新 | ✅ | — | — | ✅ | ✅ |

“?” 表示本轮没有核实。参考资料：Stardock Fences 5 的功能介绍与更新日志（quick-hide、Rules、Folder Portals、Peek），以及 GitHub 上的 PinchToDebug/DeskFrame、limbo666/DesktopFences、PecoFence、DesktopPossible。

---

## 6. 本轮没有做的事，以及原因

- **没有重写，也没有拆分 `MainWindow`**：在没有测试覆盖实际交互的前提下大规模移动代码，几乎必然引入回归，而回归的代价落在用户的真实文件上。拆分应在第一阶段先把测试补齐之后再做。
- **没有修 A8（展开态按 Win+D 后被壁纸盖住）**：它依赖桌面层自愈逻辑的重新划界（H1），单独打补丁很可能重演 A1 / A13 那样守卫漂移的问题。
- **没有替维护者做产品决策**：F6（异常策略）、C1（签名方案）、C2 / J1（与上游的身份分家）和 L5（待办勾选即删除）都需要作者拍板。
