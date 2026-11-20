## 4.3.1

本版聚焦 **极致丝滑性能抢救、Windows 11 原生桌面字体对齐与屏幕边缘防裁切、以及超越 Apple iOS 26 的液态玻璃光学与动效重构**，全面解决 `4.3.0` 实机反馈的卡顿掉帧、紧凑标签字体违和/边缘裁切及浅色壁纸下毛玻璃发灰问题（完全保持 `SchemaVersion = 16` 配置向后兼容，应用内更新只检查本 fork）。

- **P0 流畅度彻底根治（>98% 主 UI 线程 CPU 下降，120–240Hz ProMotion 级丝滑）**：
  - **独立高优先级原生消息泵线程运行鼠标钩子**：将 `DesktopMarqueeHookService` 与 `OutsideClickHook` 的 `WH_MOUSE_LL` 钩子从 WinUI 3 主 UI 线程彻底剥离至独立 `ThreadPriority.AboveNormal` 原生 `GetMessage` 线程，并增加非桌面框选态 ~15ns 极速短路路径；彻底消除设置界面（`ConsoleWindow`）滚动、切换、拖滑块与展开/收起动效卡顿。
  - **关停隐藏态 DWM Acrylic 实时模糊**：原生桌面融合模式隐藏背景时将 `CompactSurfaceHost.Visibility` 置为 `Collapsed`（替代仅改 `Opacity = 0`），彻底关停不可见的实时 GPU 模糊管线。
  - **120Hz 零分配自由网格拖拽引擎**：移除拖拽宿主每帧全图重绘的 `ThemeShadow`，改为轻量液态高亮描边；用拖拽期预建 `O(1)` 字典查表取代 `StepIosFluidItemSprings` 内每帧 `O(N²)` `IndexOfItem` 扫描与 `HashSet` 分配；将所有微震与惯性倾斜迁移至 GPU `Composition Visual.RotationAngleInDegrees`；池化复用网格引导格，并将所有选区/悬停/拖拽 `SolidColorBrush` 静态预分配。
- **Windows 11 原生桌面字体与双层 GPU 高斯文字阴影**：
  - 新增 `DesktopTypographyService`，通过 `SystemParametersInfoForDpi(SPI_GETICONMETRICS, 96 DPI)` 读取系统真实桌面图标字体（中文 Windows 11 为 `Microsoft YaHei UI`、`12 DIP`、`Normal`）并缓存高对比度状态；彻底取代随入口缩放膨胀至 20px 的巨型粗体标题，同步统一展开窗项目标签字体。
  - 废除硬边双影克隆，改由 `CompactNameText.GetAlphaMask()` 驱动**双层 GPU 高斯 `DropShadow`**（外层柔光晕 `BlurRadius = 5.6` + 内层紧凑核心 `BlurRadius = 2.1`，精准复刻 Windows 资源管理器 `DrawShadowText`），在纯白壁纸、纯黑壁纸与高反差线条壁纸上均清晰锐利、零空心双影。
  - 新增 `UpdateCompactLabelSafeWidth()` 屏幕边缘对称防裁切算法：贴边悬浮窗自动按显示器边界余量收紧安全行宽并优雅换行/省略，彻底根除靠右贴边时文字被屏幕边缘切半的缺陷。
- **超越 Apple iOS 26 液态玻璃光学层 & 原生桌面融合重构**：
  - **iOS 26 液态玻璃（收起态 + 展开态全套升级）**：采用 24.4% Apple 连续超椭圆曲率（`CompactCornerRadiusDip = 9.5`）、双层光学边缘（外层 `#24000000` 轮廓线在纯白壁纸上立现通透轮廓 + 内层 `#D4FFFFFF` 液态折射高光边）、斜向焦散折射光泽、悬浮环境 GPU 阴影、实时追踪光标的 GPU `CompositionRadialGradientBrush` 流动高光斑，以及原生 DWM 合成器线程 `SpringVector3NaturalMotionAnimation` 悬停抬升与按压回弹物理动效；展开窗面板同步加入角半径随动变形的液态光学边缘。
  - **原生桌面融合（Windows 11）**：升级为晶透一体图标胶囊 + 轻盈环境阴影 + 真实 Windows 11 桌面字体与双层高斯阴影，与左侧系统桌面图标浑然一体，告别四个小图标悬空漂浮的突兀感。
- **验证**：Release x64 0 警告 0 错误构建；全套无头 `TuckPane.LogicChecks` **62/62**（含新增 `--liquid-glass-perf`）、`UpdateChecks` **4/4**、Node 便签 **3/3** 全部通过；Windows 11 隔离沙箱实测 6 个真实右贴边收纳窗在 250 次高频鼠标移动下独立钩子线程 `0.00ms`、主 UI 线程仅 `15.62ms`（较 `4.3.0` 下降 >98%），三种风格全量零报错。

## 4.3.0

本版合入五项里程碑级重构与体验升级 PR（[#11](https://github.com/daha1216/TuckPane/pull/11)、[#12](https://github.com/daha1216/TuckPane/pull/12)、[#13](https://github.com/daha1216/TuckPane/pull/13)、[#14](https://github.com/daha1216/TuckPane/pull/14)、[#15](https://github.com/daha1216/TuckPane/pull/15)），彻底根治开机黑窗、贴边回中、桌面拖入变复制与软件快捷方式启动失败等核心痛点，全面引入原生桌面视觉融合、全链路框选多选、iOS 18/26 自由方格与多选叠卡弹簧重排，以及 `8×8` 大容量网格与主窗口深度瘦身（完全保持 `SchemaVersion = 16` 配置向后兼容，应用内更新只检查本 fork）。

- **P0 启动与贴边位置根治（[#11](https://github.com/daha1216/TuckPane/pull/11)）**：
  - **开机零巨型黑窗**：在窗口首帧显示前启用 `DWMWA_CLOAK` 隐身、预设 `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` 无边框样式并预定位至目标紧凑尺寸，待首帧渲染稳定后再解隐，彻底告别启动期满屏闪黑窗。
  - **贴边悬浮窗零漂移**：新增 `CompactPlacementMath` 确定性边距几何与边缘亲和恢复、瞬态缩屏写回保护及显示拓扑变化防抖重算，贴边悬浮窗重启不再跳回屏幕中央；同步引入 `WindowResourceScope` 统一 LIFO 析构。
- **桌面真移动与快捷方式无损启动（[#15](https://github.com/daha1216/TuckPane/pull/15)）**：
  - **桌面拖入默认真移动（含公共桌面提权）**：来自用户桌面或 `C:\Users\Public\Desktop` 公共桌面的拖入默认按移动处理（仅按住 `Ctrl` 显式复制）；移/删前自动清除只读属性；受权保护的公共桌面快捷方式遇权限受限时自动桥接 COM `IFileOperation` 触发系统标准 UAC 真实移走，移动后立即广播 `SHChangeNotify` 刷新桌面，不再残留重复副本。
  - **迁入快捷方式自愈与原生 `ShellExecuteExW` 启动**：新增 `ShortcutIntegrityService`，迁入标准 `.lnk` 时自动锚定空白/失效 `WorkingDirectory` 并通过 `SetRelativePath` 重锚相对路径，同时零破坏跳过 MSI 广告快捷方式（`SLDF_HAS_DARWINID`）与 UWP / `shell:AppsFolder` 虚拟 PIDL，完整保留参数、`SLDF_RUNAS_USER` 与 `AppUserModelID`；升级 `ShellLaunchService` 为原生 `ShellExecuteExW` 管线，彻底杜绝开机自启进程因 CWD 为 `System32` 导致便携工具/脚本启动失败，支持受损 `.lnk` / `.url` 自愈回退与静默忽略 UAC 取消（`1223`）。
  - **Windows 11 重解析点兼容**：放行 OneDrive 云占位文件与 Store AppExecLink 别名，继续严格拦截符号链接与目录联接点。
- **原生桌面视觉融合与全链路框选多选（[#13](https://github.com/daha1216/TuckPane/pull/13)）**：
  - **三种收缩外观风格（`CompactVisualStyle`）**：除经典毛玻璃卡片外，新增**原生桌面融合（无底框）**（静止零灰框直融壁纸、双层高对比投影文字 + 两行换行、悬停浮现 Fluent 选框）与 **Fluent 立体文件夹**。
  - **真实桌面图标网格对齐（`DesktopGridSnapMath`）**：基于 `SysListView32` 真实图标间距一键对齐或拖动吸附。
  - **桌面蓝色选框联动收纳窗与成组拖动**：Windows 桌面拉框扫过收纳窗自动同步高亮并支持整组锁定偏移同步移动，另支持右键一键“收入桌面已选项目”。
  - **展开窗内橡皮筋框选与批量原生拖出**：支持窗内拉框、`Ctrl`/`Shift`/`Ctrl+A` 多选，经多文件 `IShellItemArray` 一次性拖出到桌面/资源管理器，并支持批量复制、剪切与回收站删除。
- **iOS 18 / iOS 26 自由方格、流体弹簧重排与 `8×8` 容量升级（[#14](https://github.com/daha1216/TuckPane/pull/14)、[#15](https://github.com/daha1216/TuckPane/pull/15)）**：
  - **任意方格自由摆放 & `8×8` 扩容**：普通收纳窗上限从 `6×6` 提升至 **`8×8`（64 格/页）**；基于 `FreeSlotGridLayout` 支持任意空方格落位、留白分区、外部拖入直达鼠标目标格及右键一键“整理方格空位”。
  - **双向最近空位分水岭 + 窗内多选叠卡成堆拖拽**：彻底替代蛇形尾迹算法，空位 0ms 零扰动直落、占用格自动选取最短双向避让链、支持默认或按住 `Shift` 实时一对一直换（`A <-> B`），窗内多选后拖动自动聚拢为 iOS 扇形叠卡 + 蓝色数量角标；搭配精确解析欠阻尼弹簧、距离分级波纹、轻盈有机微晃与真实抬起 `ThemeShadow`。
- **终局生产力套件与架构瘦身（[#12](https://github.com/daha1216/TuckPane/pull/12)、[#15](https://github.com/daha1216/TuckPane/pull/15)）**：
  - 新增三组可配置全局快捷键（快速隐藏、Spotlight 跨收纳窗全局搜索 `--search`、Peek 置顶透视 `--peek`）、展开态即时键入筛选与 5 维排序、悬浮窗一键贴边整齐列队、多显示器拓扑感知布局快照（独立 `layout-snapshots.json`）以及默认关闭可预览/撤销的桌面/下载自动收纳规则引擎；三语（`zh-CN`/`en-US`/`ja-JP`）词条 100% 对等。
  - 将 6,531 行的 `MainWindow.xaml.cs` 按高内聚职责拆出 `MainWindow.TransitionsAndResize.cs` 与 `MainWindow.ItemDragAndDrop.cs`，主文件减重 **51.8%**。
- **验证**：Release x64 0 警告 0 错误构建；4 工程 `--locked-mode` 零新增依赖；无头 `TuckPane.LogicChecks` **61/61**、`UpdateChecks` **4/4**、便携启动器 **6/6**、Node 便签 **3/3** 全部通过；Windows 11 Per-Monitor V2 独立沙盒（以 `C:\Windows\System32` 为 CWD 启动）实机验证 6 贴边窗零黑窗精准落位、`--peek`/`--search`、`8×8` 64 格稀疏常驻展开窗零报错。

## 4.2.7

本版完成全面可靠性加固与分阶段架构重构，修复多项数据丢失竞态、资源泄漏与展开态桌面层级问题，并恢复完整无头 CI 门禁与稳定语义化更新链路（基于 4.2.6，应用内更新只检查本 fork）。

- **修复展开态收纳窗与 Dock 在 `Win+D` 后的桌面层级自愈（已知问题①）**：展开态（非置顶）Floating/Positioned 收纳窗及 Dock 现纳入 `DesktopVisibilityTimer` 监控与受控修复，并在被桌面宿主遮挡后通过 `TryGetDesktopInsertAnchor` 重新插入到桌面宿主正上方，始终保持在桌面壁纸之上且位于普通用户窗口之下，不再误跳到前台窗口上方；同步用 `SHELLDLL_DefView` 进程/类名校验（A2）与独立桌面锚定修复（A9）加固桌面图标视图缓存。
- **修复两处数据丢失与并发竞态**：
  - `.tucktodo` 外部重命名纳入 `PortableDocumentCoordinator` + `NoteRenameTransaction`，在移动文件与保存配置期间锁定 `TodoWindow` 自动保存并解除阻塞后同步路径，防止旧路径覆写导致文件分裂。
  - `.tucknote` 窗口严格等待 WebView2 发出 `"ready"` 消息且 `documentEpoch > 0` 后才允许读取编辑器状态，关闭初始化空窗期隐藏/退出/改名导致空白覆盖磁盘的竞态。
- **可靠性与资源清理（含 2026-10 审计收口）**：
  - 正常托盘退出前落盘防抖中的窗口状态（F2）；`StateStore` 写入启用 `WriteThrough` 与设备刷盘（D1）；`StartupService.Apply` 增加容错保护（M3）；`App.UnhandledException` 对不可恢复的致命 CLR 异常（`OutOfMemoryException` / `AccessViolationException` / `StackOverflowException`）不再吞掉崩溃信号（F6）。
  - `DockRunningService` 改为单常驻后台 STA 线程并确定性释放（消除每小时约 3,600 次线程创建销毁）；`StorageService` 修复 `RegistryKey` 句柄泄漏（K6）、用 `IShellLinkW` 替换 `WScript.Shell`（K3）、统一支持 `AppPaths.DesktopRoot`（K8）与空目录删除失败报错（K5）；`Ctrl` + 拖拽到桌面完整执行复制并通知资源管理器刷新（K1）。
  - `IconCacheService` 升级为宿主共享的有界 2,048 项 LRU 缓存并先查内存再探盘与并发写合并（G1/L3/M5）；`AppUpdateService` 与 `UpdateFiles` 保护全新配置备份（C9）、仅按 `backup.json` 标记清理历史配置备份（C6）且在启动外部更新器后不再误删会话目录（O2）；安装脚本 `RegisterFolderMenu` 补齐副本归属校验（C8）。
- **结构减重与交互细节对齐**：
  - 统一 `NoteWindow` 与 `TodoWindow` 共享标题栏与窗口外壳（补齐待办高对比度主题、`AutomationId`、取消重命名清除错误栏，以及便签 `280×340..1600×1200` 尺寸钳制；节流 20Hz 待办完成刷新并移除退出时 `_saveGate.Dispose()` 竞态）。
  - 合并 `ConsoleWindow` 新建与管理表单为共享 `OrganizerSettingsForm`，收口全局滑块按组刷盘与设置异步门控，预先声明可变本地画刷防止系统深色主题下的 `UnauthorizedAccessException`，并在窗口关闭时退订所有渲染与更新事件；`MainWindow` 统一 5 套交互繁忙守卫，剥离目录、外拖与桌面附着非热路径控制器，保留 60–240 Hz 悬停与 Win32 子类化热路径不动。
- **工程化与发版流水线升级**：引入根目录 `Directory.Build.props` 单源版本号管理与 `build-release.ps1` `-p:Version` 参数透传，7 个工程全部纳入 `TuckPane.slnx` 并启用依赖锁文件；通过禁用 Bootstrapper 弹窗在 GitHub Actions 恢复 `56` 组无头 `LogicChecks`、`UpdateChecks`、`LauncherChecks` 与 3 个 WebView2 JS 校验。

## 4.2.6

本版修复一类“双击图标没反应”的启动失败，并保留 4.2.5 的全部改动；应用内更新只检查本 fork。

- 修复从 Node 或 Electron 宿主启动时连累子进程：TuckPane 若由终端、IDE 运行配置或自动化 shell 拉起，宿主留下的 `ELECTRON_RUN_AS_NODE=1` 会随环境一路继承下去。被 TuckPane 启动的 Electron 程序于是以纯 Node 模式运行——不创建窗口、跑完立刻以退出码 0 结束，用户看到的就是“双击图标没反应”，而日志里点击链路、判定分支和 `Process.Start` 全部正常，只有进程在几十毫秒内消失。现在 `Main` 在注册任何东西之前清除 `ELECTRON_RUN_AS_NODE`、`ELECTRON_NO_ATTACH_CONSOLE`、`ELECTRON_ENABLE_LOGGING`、`ELECTRON_ENABLE_STACK_DUMPING`、`ELECTRON_DEFAULT_ERROR_MODE` 与 `NODE_OPTIONS`（`src/TuckPane/Core/LaunchEnvironment.cs`），子进程不再继承；`PATH`、`TUCKPANE_PERF_TRACE` 等无关变量原样保留，重复清理幂等。
- 新增 `LogicChecks --launch-environment` 用例，覆盖“该清的清掉、该留的留住、重复调用无副作用”。
- 版本元数据同步到 4.2.6：应用、更新器、安装器默认值与发布脚本默认值；`app.manifest` 的 assemblyIdentity 与更新检查 UserAgent 一并更新。
- 验证：Release x64 构建 0 警告 0 错误；`LogicChecks` 八个稳定性区域（storage/queue/lifecycle/drop/launch/watcher/diagnostics/diagnostics-limits）与新增的 `--launch-environment` 全部通过，`UpdateChecks --updates` 4/4 通过；端到端实测在 `ELECTRON_RUN_AS_NODE=1` 且带 `NODE_OPTIONS` 的环境下启动 TuckPane，抽屉里双击 Pencil 正常拉起 5 个进程，同环境下改动前的版本启动即退出。
- 已知问题：① 抽屉处于展开态时按 Win+D，展开面板会落到桌面宿主之下并被壁纸盖住，需点击空白处收起后才恢复（`ProbeDesktopAttachment()` 在展开态直接返回 `Ok`，不参与自愈，4.2.3 及更早版本同样存在）；② 收起动画被打断时，卡片上方可能残留一块放大的名字胶囊（4.2.1 起已存在）；③ Dock 偶发短线未修复。

## 4.2.5

本版合入四个社区贡献的 PR（[#2](https://github.com/daha1216/TuckPane/pull/2)、[#3](https://github.com/daha1216/TuckPane/pull/3)、[#4](https://github.com/daha1216/TuckPane/pull/4)、[#5](https://github.com/daha1216/TuckPane/pull/5)），并保留 4.2.4 的全部改动；应用内更新只检查本 fork。

- 托盘图标在资源管理器重启后恢复（[#3](https://github.com/daha1216/TuckPane/pull/3)，作者 [@qiao-obs](https://github.com/qiao-obs)）：托盘图标此前只在启动时添加一次，explorer.exe 重启后通知区域被重建，图标随之消失且不再回来。现在监听 `TaskbarCreated` 广播并重新添加图标。
- 桌面层自愈的五个后续问题（[#2](https://github.com/daha1216/TuckPane/pull/2)，作者 [@qiao-obs](https://github.com/qiao-obs)）：
  - `IsSafeAnchor()` 把“未知宿主”和“置顶锚点”混为一谈：`HWND_TOP` 的值就是 `IntPtr.Zero`，桌面宿主恰好居顶时锚点被丢弃，`SWP_NOZORDER` 让修复退化成空操作，窗口继续埋在壁纸后面。改为 `TryGetDesktopInsertAnchor(out anchor)`，用返回值区分“没有可用锚点”和“锚点是 `HWND_TOP`”。
  - 修复定时器缺少交互守卫，收起态右键菜单打开后 2–8 秒内的修复 tick 会把 owner 改回桌面图标视图，把菜单压到后面或直接失焦（省电档 8 秒正好落在菜单还开着的时间窗里）。两条定时器的守卫集抽成 `DesktopLayerBusy`，以后只改一处。
  - `RepairDesktopAttachment()` 返回的是修复**前**的状态，日志无法区分“修好了”和“被 shell 反抢回去”。改为 `RepairDesktopAttachment(out remaining)`，日志同时输出 `state=` 与 `remaining=`。
  - 桌面可见性检查硬编码 1000 ms，绕过了性能档位。`PerformanceTuning` 新增 `DesktopVisibilityMilliseconds`（省电 2000 / 平衡 1000 / 高性能 500），切档立即生效。
  - `MainWindow_Closed` 漏停 `_itemTouchHoldTimer`，长按中途关闭窗口时回调仍会落到已关闭的窗口上。
- 文件夹右键菜单与过渡动画（[#4](https://github.com/daha1216/TuckPane/pull/4)，作者 [@qiao-obs](https://github.com/qiao-obs)）：
  - 过渡被新请求取代时，旧的 `finally` 会因为 `IsCurrent()` 为假而跳过 `_animating = false`，新请求又可能在设置它之前就返回，导致 `_animating` 卡在 true。新增 `ReleaseTransitionLock()`，在每个早退路径上只释放当前持有者的锁。
  - Station 的热区显示器只解析一次且从不清理，显示器拓扑变化后热区仍指向已经不存在的显示器。现在在 `WM_DPICHANGED` / `WM_DISPLAYCHANGE` 时丢弃缓存，与 Dock 路径保持一致。
  - 驱动器根与 UNC 共享根：shell 传 `D:\` 时反斜杠转义了闭合引号，应用实际收到 `D:"`，去掉引号后剩 `D:` 不是完整路径而被拒绝。现在恢复被转义的分隔符；右键磁盘不再报参数错误，而是给出“请选择专用子目录”的明确说明（磁盘、网络盘、用户主目录、桌面、文档、下载与 TuckPane 数据根仍然不允许作为收纳窗目录）。
  - `FolderContextMenuChecks` 的 UNC 用例用了逐字字符串，`""` 转义把闭合引号吃掉，导致测试工程编译不过。
- 新增 `docs/audit-2026-10` 审计报告与落地指南（[#5](https://github.com/daha1216/TuckPane/pull/5)）。
- 新增 GitHub Actions 持续集成：push 到 main、提交 PR 与手动触发都会执行 restore（`--locked-mode`）、自包含 publish 与 `UpdateChecks`，PR 阶段即可发现编译问题。`LogicChecks` 不放进 CI：测试进程在无桌面会话的托管 runner 上起不来（八个稳定性区域各给 90 秒全部零输出被杀，同样命令在桌面会话约 1 秒完成），改由发版前在本地执行。
- 版本元数据同步到 4.2.5：应用、更新器、安装器默认值与发布脚本默认值；`app.manifest` 的 assemblyIdentity 与更新检查 UserAgent 一并更新。
- 验证：Release x64 构建 0 警告 0 错误；`LogicChecks` 八个稳定性区域（storage/queue/lifecycle/drop/launch/watcher/diagnostics/diagnostics-limits）、`--folder-context-menu` 与 `UpdateChecks --updates` 4/4 全部通过；CI 在 6c319e8 上 2 分 13 秒通过；真机回归下 Win+D 往返后卡片仍在壁纸之上，重启资源管理器后托盘图标自行恢复。
- 已知问题：① 抽屉处于展开态时按 Win+D，展开面板会落到桌面宿主之下并被壁纸盖住，需点击空白处收起后才恢复（`ProbeDesktopAttachment()` 在展开态直接返回 `Ok`，不参与自愈，4.2.3 及更早版本同样存在）；② 收起动画被打断时，卡片上方可能残留一块放大的名字胶囊（4.2.1 起已存在）；③ Dock 偶发短线未修复。

## 4.2.4

本版合入社区贡献的桌面层加固修复（[#1](https://github.com/daha1216/TuckPane/pull/1)，作者 [@qiao-obs](https://github.com/qiao-obs)），并保留 4.2.3 的全部改动；应用内更新只检查本 fork。

- 修复兜底锚点仍会把收纳窗埋回最底层：`DesktopInsertAnchor()` 在拿不到桌面宿主根窗口时返回 `HWND_BOTTOM`，而 `HWND_BOTTOM` 的值是 `1` 不是 `0`，原来的 `anchor == IntPtr.Zero` 守卫拦不住它，`SWP_NOZORDER` 不会被加上，窗口反而被压到最底层——正是 4.2.3 想消除的状态。现在兜底返回 `IntPtr.Zero`，两个调用方都会因此加上 `SWP_NOZORDER`（即不动 z 序），并新增 `IsSafeAnchor()` 把 `HWND_BOTTOM` 按名字排除，避免以后有人把它塞回来时静默回归。
- 修复收纳窗关闭时漏停计时器：`MainWindow_Closed` 停了 7 个计时器，唯独漏了 `_desktopVisibilityTimer`。收纳窗可被动态销毁，窗口关闭后这个 1 秒计时器仍挂在应用级 UI 队列上继续触发，并持有窗口委托，整个窗口连同可视化树都回收不掉；删掉 N 个收纳窗就是每秒 N 次空转加 N 个窗口对象常驻。现已补上 `Stop()`。
- 版本元数据整理：`app.manifest` 的 assemblyIdentity 版本从 `4.2.1.0` 更新为 `4.2.4.0`；更新检查的 UserAgent 从 `TuckPane-Updater/4.2.0` 更新为 `TuckPane-Updater/4.2.4`。
- 验证：Release x64 构建 0 警告 0 错误；`LogicChecks` 八个稳定性区域（storage/queue/lifecycle/drop/launch/watcher/diagnostics/diagnostics-limits）全部通过，`UpdateChecks` 4/4 通过；真机回归（构建装入正式安装目录、单实例）下展开再收起后卡片仍在壁纸之上（target z=32 vs Progman 33，卡片区域暗度 89.8 而非壁纸 143.7），等待 6 秒保持；Win+D 往返两次后四张卡片均可见。
- 已知问题：① 抽屉处于展开态时按 Win+D，展开面板会落到桌面宿主之下并被壁纸盖住，需点击空白处收起后才恢复（`ProbeDesktopAttachment()` 在展开态直接返回 `Ok`，不参与自愈，4.2.3 及更早版本同样存在）；② 收起动画被打断时，卡片上方可能残留一块放大的名字胶囊（4.2.1 起已存在）。

## 4.2.3

本版修复桌面收纳窗（抽屉）在点击展开再收起后消失的问题，并保留 4.2.2 的全部改动；应用内更新只检查本 fork。

- 修复抽屉消失：收起和自愈此前都用 `HWND_BOTTOM` 把收纳窗插到窗口栈最底层，而显示桌面或点击桌面后 shell 会把桌面宿主 Progman 抬到收纳窗之上，收纳窗因此落到壁纸后面。自愈探针能检测到“被桌面挡住”，但修复动作仍用 `HWND_BOTTOM`，每 2 秒重复埋一次，所以永不恢复。现在改为插到桌面宿主正上方（`GetWindow(桌面根, GW_HWNDPREV)`），收纳窗始终位于壁纸之上、普通窗口之下。
- 修复 shell 隐藏或摘除收纳窗（退出显示桌面、切换虚拟桌面、资源管理器重启等）后的恢复：重新挂到桌面图标视图，并新增 1 秒桌面可见性检查与 4 秒健康检查，必要时隐藏再显示以重建 WinUI 帧。
- 验证：在“Win+D 显示桌面 → 点击抽屉展开 → 点击空白处收起”流程下，修复版收起后窗口仍在 Progman 之上（z=43 vs Progman 44），卡片正常显示；同流程在 4.2.1/4.2.2 构建上会落到壁纸后面（z=286 vs 41）并持续不恢复。Win+D 来回切换、连续展开收起均通过，普通窗口仍在收纳窗之上。
- 构建：Release x64 自包含（.NET 10 + Windows App SDK），FileVersion 4.2.3.0；已执行桌面交互手测（显示桌面、点击展开/收起、Win+D 切换）。
- 已知问题：收起动画被打断时，卡片上方可能残留一块放大的名字胶囊；4.2.1 起已存在，本版未修复。

## 4.2.2

本版是个人维护的 fork 构建（发布源 daha1216/TuckPane），基于上游 4.2.1 合入诊断与运行期维护修复；应用内更新只检查本 fork，不会安装上游构建。

- 新增可选性能日志：设置 `TUCKPANE_PERF_TRACE=1` 时写入 `diagnostics/performance.0.jsonl`（2 MiB 轮转到 `.1`），记录启动与交互耗时样本，单行截断 512 字符、过滤换行；未设置时无额外开销。此前性能日志方法为空实现，日志中没有任何性能数据。
- 构造函数内发生的失败现在能记录代码位置（如 `ConsoleWindow..ctor`），不再因调用方名称为 `.ctor` 而丢成空值。
- 启动日志降噪：宿主缺少高对比度事件（`COMException 0x80070490`）属于可预期降级，改为 info 级并只写可选性能日志，不再写入 `runtime` 失败记录；启动时不再每次留下一条 ERROR。
- 图标缓存启动清扫与上限：后台删除超过 1 小时的 `.tmp`，png 超过 4096 个或 64 MiB 时按最旧优先清理到限额内，跳过 reparse point；只统计与清理缓存 png，不触碰真实文件。
- 更新会话自动清理：启动时删除 `updates/sessions` 下超过 1 小时的历史目录（递归、跳过 reparse point），避免每次应用内更新后留下数百 MB 残留。
- 应用内更新源改为本 fork；同版本或更低版本不会安装。

## 验证
- 非 GUI 自动检查：`LogicChecks --stability` 八个区域（storage/queue/lifecycle/drop/launch/watcher/diagnostics/diagnostics-limits）与 `UpdateChecks --updates` 4/4 全部通过。
- 隔离测试根端到端：种入 4301 个缓存 png 后清理到 4096 个、过期 tmp 删除、旧更新会话删除，启动不再产生 `Runtime/Failed` 记录。
- 构建：Release x64 自包含（.NET 10 + Windows App SDK），FileVersion 4.2.2.0；未执行 GUI 鼠标/键盘手测。

## 已知问题
- Dock 短线：与 4.2.1 相同，本次未修改相关代码，未修复。

## 4.2.1

- 修复便携包双击 `00-启动 TuckPane.exe` 无窗口、无托盘并立即退出的问题。该入口现在独立启动同目录的 `TuckPane.exe`，保留已有快捷方式和命令参数。
- 初始化失败显示不依赖 XAML 的原生错误提示，并返回非零退出码。
- 正式发布和更新验证统一生成启动器，校验包内共享运行时，并加入参数转发、旧入口替换及更新失败回滚专项检查。
- 本版仅基于 4.2.0 合入上述修复。自动检查为无界面替身进程测试；实际首次启动、托盘和错误弹窗仍待手动验收。

# TuckPane 4.2.0

## 新增与改进
- 精简模式拉伸：普通悬浮/定位收纳窗的左右边仅改宽、上下边仅改高，图标与文字行距不缩放；四角同比缩放内容区、图标、文字与留白，标题字号独立。拖边固定对侧边、拖角固定对角，遵守最小尺寸、工作区与对齐限制；尺寸结束即保存，重新展开或重启后恢复，并兼容旧配置。以上规则适用于临时展开与永久展开，图标模式、Station 与 Dock 保持原规则。
- 所有收纳窗口添加项目：悬浮、定位、各边 Station 以及横/竖 Dock 的窗口空白、可见标题和已有收起入口统一使用窗口菜单“添加项目 → 文件…/文件夹…”；文件/图标继续使用项目菜单。复用既有菜单处理链并统一空白/标题/收起入口，避免重复入口；菜单与选择器打开期间保持展开，取消或失败时正确释放保护。
- 文件夹快捷方式：添加项目子菜单可分别选择文件或文件夹，一次一个，Dock 空白加号使用同一入口；使用系统文件/文件夹选择器，在当前收纳目录创建指向真实目标的 .lnk，原目标不动。文件夹名保留点号等完整名称，既有 .lnk 原样复制并保留参数与图标；保留同名避让、暂存发布与失败清理。
- 便签图片剪贴板与撤销：图片右键提供复制图片、剪切图片、粘贴、删除图片，并支持 Ctrl+C/X/V 与 Delete/Backspace，正文文本操作保留。宿主写入真实系统图片数据，可跨便签及支持图片的外部应用粘贴；剪切在写入成功后才删除，失败保留并提示；粘贴优先图片，其次纯文本。图片剪切、粘贴、删除接入原生撤销事务，支持 Ctrl+Z，不引入第二套冲突历史。
- 待办新增入口移到列表末尾，显示低对比度圆角加号；点击原位输入，字号与正文同步。回车新增后恢复加号，失焦保留草稿，Esc 取消。
- 便签与待办统一同步主题；串行处理连续选择、隔离单目标失败并报告未完成同步。主题资源保持稳定引用，避免切换时反复替换资源。
- 便签和待办标题栏高度及图标缩至原来的 3/5，标题文字保持原字号，横线开关分别保留。
- 包含当前工作区已有的 Dock 布局、运行指示和便签改名保存修复。

## 已知问题
- Dock 短线：用户报告的偶发短线原因尚未确定，本次未修改相关代码，未修复。需在复现时记录横/竖 Dock、是否刚悬停/点击弹跳/拖动、移出指针后是否消失，并保留包含整个 Dock 的画面。正常运行圆点不受影响。

## 验证
- 自动检查边界（非 GUI）：已通过便签/待办 4.2 专项非 GUI 检查、本次精简拉伸/添加项目/文件夹快捷方式三组定向 C# 检查、便签图片 Node 定向检查，并完成 Release x64 构建。未重复执行旧功能完整测试集；未运行鼠标/键盘/UI Automation 等界面操控，也未改写用户实际剪贴板。
- GUI 手测边界（自动化未执行，需用户确认）：精简拉伸拖拽手感与锚点、各收纳窗口菜单的显示与裁切、系统文件/文件夹选择器、Dock 短线复现、便签图片右键与快捷键、跨应用图片粘贴、原生撤销，以及实际外观、焦点、主题 COM 异常消除和关闭重开保存，均待用户手动验证。
