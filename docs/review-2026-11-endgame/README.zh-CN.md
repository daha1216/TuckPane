# TuckPane v4.2.7 终局评审与重构白皮书（2026-11）

> **评审基线**：`daha1216/TuckPane` `main@be22d62`（v4.2.7，`refactor/surgical-overhaul` 之后）  
> **评审组织**：项目终局评审与重构指挥部（首席架构师 / 首席产品官 / 首席体验官 / 首席技术官 / 数据与 AI 架构师 / 安全隐私与合规官 / SRE 与性能工程专家 / 质量与可靠性专家 / 增长与商业化负责人 / 平台工程与开发者体验专家 / 开源与生态战略专家）  
> **执行原则**：保持 `SchemaVersion = 16` 零破坏向后兼容、不引入额外 NuGet 依赖（锁文件 `packages.lock.json` 零改动）、三语（`zh-CN` / `en-US` / `ja-JP`）全量对等、所有改动均经 58 套无头回归测试与真实 Windows 11 Per-Monitor V2 DPI 实机隔离验证。

---

## 一、两大 P0 开机日常痛点的取证级根因分析与 A/B 实测

在用户真实高分屏环境（`2560×1600 @ 150% DPI`，工作区 `0,0,2560,1528`，6 个 `PlacementMode = Floating` 悬浮收纳窗紧贴屏幕右边缘 `XDip = 1617.3333`，`CompactScale = 1.56`）下，我们对 v4.2.7 进行了逐帧 Win32 / DWM 窗口句柄探针采样，定位了两条长期困扰贴边悬浮用户的 P0 缺陷。

### 1.1 P0-1：贴边悬浮收纳窗每次开机/重启后瞬移回屏幕中央

- **根因链路**：
  1. `MainWindow.InitializeCoreAsync()` 调用 `ApplyBounds(_compactBounds)` 后同步调用 `WindowRoot.UpdateLayout()` 和 `TryGetCompactAlignmentInsets(_compactBounds, out restoreInsets)`。
  2. WinUI 3 的 `DesktopWindowXamlSource` 布局尺寸同步滞后于 Win32 `SetWindowPos`，此时 `WindowRoot` 仍保持巨大的默认尺寸（约 `1280×720 DIP`）。
  3. 因为 `CompactView` 设置了居中对齐（`HorizontalAlignment="Center" VerticalAlignment="Center"`），`CompactThumbnailHost.TransformToVisual(WindowRoot)` 返回的偏移达到 `origin.X ≈ +914px`，算出严重失真的负边距 `insets = (Left: +914, Right: -827)`。
  4. `WindowAlignmentMath.ClampFrame` 将可见帧右边界夹紧到 `2560` 再反推窗口左边缘 `2560 - 914 - width`，把所有贴右边缘窗口强行瞬移至 **`Rect.Left = 1555px`（屏幕中央）**；而本来位于屏幕中部的窗口因未触发夹紧，正负偏移恰巧相互抵消，掩盖了此 Bug。
  5. 叠加放大因素：`ApplyCompactScale()` 在恢复位置前默认以 `repositionWindow: true` 运行；`DesktopRepairTimer_Tick` 在显示器休眠/切屏的瞬态缩屏期间把临时夹紧坐标写回 `definition.Position`；`WM_DISPLAYCHANGE`/`WM_DPICHANGED` 此前未对 `Floating` 模式做重算恢复。
- **修复方案（Phase 1）**：
  - 引入纯逻辑模块 `Core/CompactPlacementMath.cs`：确定性计算收缩态可见帧内边距 `CalculateCompactAlignmentInsets`（彻底消除启动期对异步 `TransformToVisual` 的依赖），并补充 `WindowAlignmentInsets.IsValidFor` 防御校验。
  - 引入边缘亲和恢复 `ResolveRestoredOriginPx` 与瞬态缩屏保护 `ShouldPersistClampedFloatingPosition`：当显示器分辨率或 DPI 瞬态收缩时不污染持久化的 `definition.Position`，仅在有效用户拖动或显式布局变更时落盘。
  - 统一 `WM_DISPLAYCHANGE` / `WM_DPICHANGED` 调度防抖重算 `ScheduleDisplayChangeReconciliation` -> `ReconcileDisplayChange`，同时覆盖 `Floating`、`Positioned`、`Dock` 与 `Station`。

### 1.2 P0-2：每次开机启动后在桌面弹出大量巨型黑窗

- **根因链路**：
  1. `AppHost.CreateWindow` 在设置收缩尺寸、应用 `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`、建立 DWM Alpha 合成以及设置 DWM Cloak 之前，就对所有非 Station 收纳窗调用了 `window.Activate()`。
  2. `TransparentWindowBackdrop` 在处理 `WM_ERASEBKGND` 时以 `CreateSolidBrush(0)` 填充整个未缩放的默认客户区，导致开机高负载下每个收纳窗先以巨大实体黑框 + 任务栏图标闪烁在屏幕上弹出，数帧后才缩小透明化。
- **修复方案（Phase 1）**：
  - 在 `MainWindow.StartupPlacement.cs` 引入 `BeginStartupCloak` / `ReleaseStartupCloak`：在 `Activate()` 之前即调用 `DWMWA_CLOAK = 13` 隐身、`DWMWA_TRANSITIONS_FORCEDISABLED = 3` 禁用 DWM 弹窗过渡、提前应用 `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` 与透明合成，并按目标显示器与坐标 `PrePositionCompactHostWindow` 预定位。
  - `AppHost.CreateWindow` 显式调用 `_ = window.InitializeAsync()`，避免 `WS_EX_NOACTIVATE` 抑制 `Activated` 事件导致初始化停滞。
  - 等待 XAML 完成首个真实渲染帧并稳定贴靠桌面层后再解除 `DWMWA_CLOAK`，实现开机 **0 黑窗、0 任务栏闪烁、0 坐标漂移**。

### 1.3 真实 Windows 11（2560×1600 @ 150% DPI）隔离沙箱 A/B 实测对比

使用 Per-Monitor V2 DPI 感知探针（`SetProcessDpiAwarenessContext(-4)`）在隔离 `TUCKPANE_TEST_ROOT` 中连续以 5ms 周期采样 6 个贴右边缘悬浮收纳窗的启动全过程：

| 观测指标 | 修复前（v4.2.7 `be22d62`） | 修复后（Phase 1 `8412310`） |
| :--- | :---: | :---: |
| 未隐身巨型黑窗采样帧数（`HugeUncloakedSamples`） | **84 帧** | **0 帧** |
| 非 ToolWindow 任务栏闪现采样帧数（`NonToolWindowVisibleSamples`） | **84 帧** | **0 帧** |
| 6 个右贴边窗口最终物理坐标（`Rect.Left ~ Rect.Right`） | `1555 ~ 1733`（全部跳至屏幕中央） | **`2426 ~ 2604`**（可见右边界精确对齐 `2560`，0px 漂移） |

---

## 二、11 角色终局委员会全景诊断与竞品对标

### 2.1 行业成熟同类产品横向对标

我们对 Windows 桌面整理生态中的主流商业与开源项目进行了系统对标：

| 产品 / 项目 | 核心优势与用户强口碑特性 | TuckPane v4.2.7 原状短板 | 本次重构对策 |
| :--- | :--- | :--- | :--- |
| **Stardock Fences 5** | **Peek（一键置顶透视）**、**Folder Portals / 桌面自动分类规则**、多显示器布局快照、双击隐藏 | 缺少置顶透视、无跨窗搜索、无自动归类规则、多屏/插拔后坐标易丢 | 完整落地 **Peek 透视**、**拓扑感知布局快照（含自动/手动/导入导出）**、**桌面/下载自动收纳规则（预览+一键撤销）** |
| **DeskFrame / DesktopPossible** | 轻量原生、内置过滤栏、快速右键定位源文件与属性、排序整理 | 展开窗缺少即时筛选与右键一键排序；文件右键仅基础菜单、缺定位/复制路径/属性 | 落地 **展开态键入即筛 + 键盘方向导航**、**5 维智能排序（含自然数字排序）**、**增强文件右键菜单（打开/定位/复制路径/属性）** |
| **PecoFence / Fenceless / Racks / DeskBox** | 极速启动器（Spotlight 式检索）、边缘整齐吸附列队、全局快捷键可控 | 无跨收纳窗全局搜索；多悬浮窗贴边易参差不齐；全局快捷键缺失 | 落地 **Spotlight 全局跨窗搜索浮窗（多词+拼音/变音归一化+首字母缩写）**、**一键整齐贴边列队（`EdgeStackAlignMath`）**、**三组独立无冲突全局热键** |

### 2.2 委员会各维度关键诊断与重构落点

1. **首席架构师 & CTO**：
   - `MainWindow.xaml.cs` 超过 6,400 行，启动定位、资源释放、快捷键和筛选逻辑高度耦合。本次在不破坏既有 56 套契约测试的前提下，采用 **纯算法下沉 `Core/` + 局部职责分拆 partial（`MainWindow.StartupPlacement.cs`、`MainWindow.ItemActions.cs`、`MainWindow.KeyboardFilter.cs`、`AppHost.Productivity.cs`、`ConsoleWindow.Productivity.cs`）** 的演进式重构，并用 `WindowResourceScope` 实现 LIFO 异常隔离析构，杜绝窗口关闭期部分资源泄漏。
2. **首席产品官 & 首席体验官**：
   - 桌面收纳的本质不仅是“把图标藏起来”，而是 **“零摩擦找到并打开”** 与 **“零心智维护整洁度”**。
   - 因此我们构建了四位一体的高频交互闭环：**全局搜索（随时唤起，Enter 打开 / Ctrl+Enter 定位 / Shift+Enter 打开所在收纳窗 / Ctrl+Shift+C 复制路径）** + **Peek 透视（全屏工作时一键把所有收纳窗浮到最上层，点开文件自动退回桌面层）** + **窗内键入即筛 & 5 维排序** + **贴边一键整齐列队**。
3. **SRE 与性能工程专家**：
   - 全局搜索采用后台只读扫描索引，零 UI 线程磁盘 I/O；布局快照独立落盘 `layout-snapshots.json`（`WriteThrough + Flush(true)` 原子替换），绝不膨胀 `state.json`；`App.xaml.cs` 新增 `RecoverableUiErrorCount` 并在诊断页显式提示，消除“静默吞异常”黑盒。
4. **安全隐私与合规官**：
   - 自动收纳规则默认完全关闭（opt-in），严格跳过隐藏/系统文件、重解析点/符号链接、`desktop.ini`、staging 临时目录及同目录循环；对 `.exe`/`.msi` 可执行文件仅创建 `.lnk` 快捷方式而绝不移动本体；强制支持 **Dry-Run 预览** 与 **单次批次一键撤销**。
5. **质量与开发者体验专家**：
   - 完整保持 `SchemaVersion = 16` 向后兼容、零新增外部依赖、全项目 Release 0 警告编译；新增 `--startup-placement` 与 `--endgame-productivity` 两套回归测试（纳入 `--ci-headless`，共 58 套），自动校验三语 `.resw` 词条集合完全对等。

---

## 三、两阶段 PR 交付架构

为便于上游仓库清晰评审与按需合入，本次重构拆分为两条职责清晰的提交链路：

1. **PR #1（`fix/startup-cloak-and-edge-placement`）— P0 开机体验救火专修**
   - 解决开机巨型黑窗（DWM Cloak + 预定位 + 预样式化 + 首帧稳定后显现）。
   - 解决贴边悬浮窗重启跳回屏幕中央（确定性 `CompactPlacementMath` + 边缘亲和恢复 + 瞬态缩屏写回保护 + 显示拓扑变化防抖重算）。
   - 引入 `WindowResourceScope` 统一 LIFO 析构。
2. **PR #2（`feat/endgame-ux-and-productivity-overhaul`，基于 PR #1）— 终局体验与生产力跃升**
   - **三组可配置全局快捷键 + CLI 入口**：快速隐藏/恢复、Spotlight 全局搜索（`--search`）、Peek 置顶透视（`--peek`），内置跨快捷键冲突与系统占用检测。
   - **Spotlight 跨收纳窗全局搜索浮窗**：支持多词 AND、变音/全角归一化、驼峰/分词首字母缩写（如 `vsc` -> `Visual Studio Code`）、收纳窗名与扩展名过滤，全键盘操作。
   - **Peek 透视模式**：一键将所有收纳窗临时提升至最前端，点击任意文件/便签/待办打开后自动恢复桌面层，兼容 `DesktopLayerService` 安全探针。
   - **展开态即时筛选 + 键盘导航 + 5 维智能排序 + 增强文件右键菜单**（在资源管理器中显示、复制完整路径、系统属性）。
   - **悬浮窗一键整齐贴边列队（`EdgeStackAlignMath`）**：自动识别贴边方向，按原始上下顺序等距无重叠紧贴屏幕边缘，不影响屏幕中央自由摆放的窗口。
   - **显示器拓扑感知布局快照（`LayoutSnapshotService`）**：按显示器设备/工作区/DPI 生成 SHA-256 拓扑指纹，切换外接屏/扩展坞自动记忆与恢复，支持手动命名快照及 JSON 导入导出。
   - **桌面 / 下载目录自动收纳规则引擎（`AutoOrganizeService`）**：支持扩展名、通配符、最小驻留天数、一键预设、安全保护、预览与一键撤销，并自动适配重定向后的系统 Downloads 目录。
3. **PR #13（`feat/native-desktop-fusion-and-marquee-multiselect`，基于 PR #12）— 原生桌面视觉融合与全链路框选多选**
   - **三种收缩外观融合风格（`CompactVisualStyle`）**：除保留默认 `经典毛玻璃卡片` 外，新增 **`原生桌面融合（无底框）`**（静止时零灰框直融壁纸、悬停/多选浮现 Windows 11 原生 Fluent 选框、双层高对比投影文字 + 2 行自动换行，彻底解决白底/高反差壁纸上标题消失与圆角灰框割裂感）与 **`Fluent 立体文件夹`**（带文件夹耳片与半透明玻璃袋轮廓）。
   - **悬浮窗对齐真实 Windows 桌面图标网格（`DesktopGridSnapMath`）**：基于 `SysListView32` 的 `LVM_GETITEMSPACING` 与真实原生图标原点/占位避让，支持右键一键对齐桌面网格或拖动结束自动吸附。
   - **桌面级蓝色选框联动收纳窗 + 成组批量拖动（`DesktopMarqueeHookService` + `AppHost.NativeDesktop.cs`）**：超轻量无阻塞 `WH_MOUSE_LL` 监听桌面空区拉框，被 Windows 蓝色选框扫过的悬浮收纳窗实时同步高亮（并支持 `Ctrl+点击` 增删），拖动任意选中收纳窗即可**整组锁定相对偏移平滑同步移动并原子落盘**；另支持右键一键“收入桌面已选项目”（通过 `IShellWindows` / `IFolderView` / `IShellItemArray` 读取原生桌面已选文件批量收纳入箱）。
   - **展开窗内部橡皮筋拉框多选 + 原生多文件批量拖出（`ItemSelectionModel` + `MainWindow.ItemSelection.cs` + `ShellDragService` 多文件 `IShellItemArray`）**：在展开收纳窗内容区拉框、`Ctrl+点击`、`Shift+连选`、`Ctrl+A` 多选任意数量文件/快捷方式，支持一次拖动批量拖出到桌面/资源管理器/其他收纳窗，并支持批量 `Ctrl+C`、`Ctrl+X`、回收站批量 `Delete` 与多选右键菜单。

---

## 四、后续演进建议（v5.0 Roadmap）

1. **热键自定义录制器**：在现有安全预设之外，支持用户自定义任意组合键录制（带冲突实时检测与回退）。
2. **规则定时/事件轻量触发**：在自动收纳规则稳定运行一两个版本后，提供可选的“托盘静默建议 / 每日一次确认整理”模式（保持非侵入与可撤销原则）。
3. **`MainWindow` 状态机进一步解耦**：将拖拽协调、动画节拍与目录渲染继续抽离为独立控制器，把单文件体量压降至 2,000 行以内，提升社区贡献者的阅读与维护效率。
