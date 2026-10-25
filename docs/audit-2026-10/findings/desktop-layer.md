# TuckPane 4.2.4 — 桌面层与窗口管理审查

审查对象：`本仓库`（HEAD `7f58788`）
审查范围：`src/TuckPane/Services/DesktopLayerService.cs` 全文、`MainWindow.xaml.cs` 的桌面层相关片段、`NativeMethods.cs` 的窗口类 P/Invoke。
纯静态审查，未编译、未运行。

## 本域结论

1. 4.2.4 的 `IsSafeAnchor()` 兜底本身**方向正确但不彻底**：它把 `HWND_BOTTOM` 挡在门外，却顺带把 `HWND_TOP`（`IntPtr.Zero`）也挡掉了，于是「桌面宿主是 z-order 最顶层」这一**正是 Win+D 产生的状态**退化成 `SWP_NOZORDER` 空操作——修复在它最想修的场景里不生效。这是 4.2.4 自身引入的回归。
2. 桌面层只有「收起态」有自愈：`ProbeDesktopAttachment()` 和两个定时器都在 `_expanded` 上早退，展开态一旦被壁纸盖住就只能靠用户操作救回来（发布说明已承认）。**但展开态只是两个成因之一**：`DesktopInsertAnchor()` 在第三方壁纸引擎共存时会把面板主动插到壁纸 `WorkerW` 之下，收起态同样中招。
3. 整个自愈体系依赖「定时器轮询 + 单句柄比对」：1Hz 硬编码的可见性定时器绕过了省电档 8000ms 的配置（实际探测频率提高 8 倍），探测走全 z-order 遍历，且 `_lastKnownDesktopIconView` 静态缓存只用 `IsWindow` 校验——Explorer 重启后句柄回收会把面板挂到无关窗口上。
4. 自愈体系内部存在**互相拆台**：`SetInputActivation(true)` 把 owner 置空（`:301`），修复定时器随后把它判成 `OwnerLost` 并抢回去（`:224-227` → `:136`），而修复定时器缺少可见性定时器那道 `IsInputActivationEnabled` 门（对比 `:7029` 与 `:6959`）。右键菜单/内联编辑停留超过一个修复周期即触发。
5. z-order 与 owner 被错误耦合：`if (!ownerChanged || !IsSafeAnchor(anchor)) flags |= SWP_NOZORDER`（`:71`）导致**owner 没变时连位置也不修**，启动首帧和每次拖拽结束后小组件会悬在所有普通窗口之上。
6. 关闭路径只停 3/9 个定时器、全部 `Tick +=` 无对应 `-=`；`Dispose()` 在 `Closed` 末尾且带 `IsWindow` 守卫，`RemoveWindowSubclass` 与 owner 还原实为死代码，静态缓存永不清理。
7. 所有 Win32 返回值被丢弃，`RepairDesktopAttachment()` 又返回修复**前**状态，日志语义反转，现场无法判断修复是否成功。

### 发现索引（18 条：10×P2 / 8×P3）

| # | 级别 | 主题 | 位置 |
| --- | --- | --- | --- |
| 1 | P2 | `IsSafeAnchor` 否掉 `HWND_TOP`，修复退化为空操作 | `DesktopLayerService.cs:173-174` |
| 2 | P2 | 展开态无自愈（三处 `_expanded` 早退） | `DesktopLayerService.cs:203-206`、`MainWindow.xaml.cs:7027` |
| 3 | P3 | `RepairDesktopAttachment` 返回修复前状态，日志反转 | `DesktopLayerService.cs:156` |
| 4 | P2 | 输入态与修复态争写 `GWLP_HWNDPARENT` | `DesktopLayerService.cs:301`、`:136`、`MainWindow.xaml.cs:6959` |
| 5 | P2 | `DesktopInsertAnchor` 取错锚点（壁纸引擎/顶层宿主/虚拟桌面） | `DesktopLayerService.cs:180-192` |
| 6 | P2 | `IsBehindDesktopLayer` 只认单个 `desktopRoot` | `DesktopLayerService.cs:237-253` |
| 7 | P2 | 静态缓存只用 `IsWindow` 校验，句柄回收后挂错窗口 | `DesktopLayerService.cs:441-445` |
| 8 | P2 | `Hidden` 分支 hide/show 同消息内完成，帧不会重建 | `DesktopLayerService.cs:140-154` |
| 9 | P2 | 硬编码 1Hz 定时器绕过省电档配置、与修复定时器重复探测 | `MainWindow.xaml.cs:342-345`、`:6099-6100` |
| 10 | P2 | 关闭只停 3/9 定时器、`Tick` 从不解绑，关闭后回调打进已销毁 XAML 树 | `MainWindow.xaml.cs:7073-7083`、`:329-371` |
| 11 | P3 | `Dispose` 在 `Closed` 末尾 + `IsWindow` 守卫 → 清理是死代码 | `DesktopLayerService.cs:319-326`、`MainWindow.xaml.cs:7116` |
| 12 | P2 | `ownerChanged == false` 连 z-order 一起跳过，启动/拖拽后压在所有窗口之上 | `DesktopLayerService.cs:61-71`、`:85-101` |
| 13 | P3 | `WM_GETMINMAXINFO` 不调 `DefSubclassProc`，死代码 + 隐患 | `DesktopLayerService.cs:390-396` |
| 14 | P3 | `WM_MOUSEACTIVATE` 无条件 `MA_NOACTIVATE`，键盘/触摸/UIA 不可达 | `DesktopLayerService.cs:386-389`、`:341` |
| 15 | P3 | `RaiseAboveNormalWindows` 忽略返回值，可永久 topmost | `DesktopLayerService.cs:354-361` |
| 16 | P3 | ctor 同步发 `0x052C` 等 1000ms，每个收纳窗阻塞 UI 线程 | `DesktopLayerService.cs:32`→`:434` |
| 17 | P3 | `healthChecked` 恒真 + `Ok` 分支每 tick 多余 `Reattach()` | `MainWindow.xaml.cs:7008-7020` |
| 18 | P3 | 图标隐藏时 owner 回退到控制台窗口且探测永远 `Ok` | `MainWindow.xaml.cs:491-493`、`DesktopLayerService.cs:219-222` |

---

### [P2] `IsSafeAnchor()` 把 `HWND_TOP` 一起否掉，桌面宿主位于 z-order 顶端时修复退化为空操作

- **证据**：`src/TuckPane/Services/DesktopLayerService.cs:186-191`
  ```csharp
  if (root == IntPtr.Zero) return IntPtr.Zero;
  // hWndInsertAfter places the window right behind the given window, so the window just in
  // front of the host is the right anchor for "immediately above the host".
  IntPtr above = NativeMethods.GetWindow(root, NativeMethods.GW_HWNDPREV);
  return above == IntPtr.Zero ? NativeMethods.HWND_TOP : above;
  ```
  `src/TuckPane/Services/DesktopLayerService.cs:173-174`
  ```csharp
  private bool IsSafeAnchor(IntPtr anchor) =>
      anchor != IntPtr.Zero && anchor != NativeMethods.HWND_BOTTOM && anchor != _window;
  ```
  `src/TuckPane/Services/NativeMethods.cs:168`：`internal static readonly IntPtr HWND_TOP = IntPtr.Zero;`
  两个调用方：`DesktopLayerService.cs:71`（`Reattach`）与 `:167`（`ReassertDesktopAttachment`），均为 `if (!IsSafeAnchor(anchor)) flags |= SWP_NOZORDER;`

- **影响**：`GW_HWNDPREV` 在「桌面宿主已经是 z-order 最顶层」时返回 `NULL`，方法于是返回 `HWND_TOP`，而 `HWND_TOP` 恰好等于 `IntPtr.Zero`，被 `IsSafeAnchor` 判为不安全 → 调用方加上 `SWP_NOZORDER` → **z-order 一个字节都没动**。而「宿主在最顶层」正是 Win+D / 点击桌面后 shell 抬高桌面的结果（也就是 4.2.4 想修的那个场景）：此时面板被宿主压在下面，修复调用返回后仍然被压在下面。4.2.3 的 `HWND_BOTTOM` 兜底会把它埋到底部（更糟），4.2.4 改成 `IntPtr.Zero` 后不再更糟，但**也没有变好**——只是从「主动埋掉」变成「什么也不做」。另外 `HWND_TOP` 本身是合法的 `hWndInsertAfter` 值，用它绝不会把窗口埋到底部，把它一并拒绝属于过度收紧。由于 `SetWindowPos` 返回值被丢弃（见另一条），整个过程完全静默。

- **建议**：把「未知」和「置顶」区分开，不要让 `IntPtr.Zero` 同时承担两种语义。改成 `private bool TryDesktopInsertAnchor(out IntPtr anchor)`：宿主取不到时返回 `false`（调用方加 `SWP_NOZORDER`）；宿主在最顶层时返回 `true` + `HWND_TOP`；`IsSafeAnchor` 只保留「拒绝 `HWND_BOTTOM`、拒绝等于自身、拒绝非窗口句柄」三条，即
  ```csharp
  private bool IsSafeAnchor(IntPtr a) => a != NativeMethods.HWND_BOTTOM && a != _window && (a == NativeMethods.HWND_TOP || NativeMethods.IsWindow(a));
  ```
  另外在 `GetWindow` 与 `SetWindowPos` 之间加 `IsWindow(above)` 校验，避免锚点窗口在这一瞬间被销毁导致 `SetWindowPos` 直接失败。

---

### [P2] 展开态完全没有自愈路径：探测、两个定时器三处都对 `_expanded` 早退

- **证据**：`src/TuckPane/Services/DesktopLayerService.cs:201-206`
  ```csharp
  private DesktopAttachmentState ProbeDesktopAttachment()
  {
      if (_expanded || _window == IntPtr.Zero || !NativeMethods.IsWindow(_window))
      {
          return DesktopAttachmentState.Ok;
      }
  ```
  `src/TuckPane/MainWindow.xaml.cs:7027`（可见性定时器）
  ```csharp
  if (_closing || _expanded || _animating || _pressActive || IsContained || !_runtimeVisible) return;
  ```
  `src/TuckPane/MainWindow.xaml.cs:6959`（修复定时器整段被 `!_expanded` 包住）
  ```csharp
  if (!_expanded && !_animating && !_pressActive)
  {
  ```
  展开态与桌面层的关系：`src/TuckPane/MainWindow.xaml.cs:533` 与 `:594` 两处构造都是 `new DesktopLayerService(_hwnd, IntPtr.Zero)`，即 `_expandedOwner` 恒为 `IntPtr.Zero`；`DesktopLayerService.cs:44-52` 展开态走 `SetWindowPos(_window, _stayTopmost ? HWND_TOPMOST : HWND_TOP, ...)`，`SetExpanded(true, stayTopmost:false)` 再补一次 `RaiseAboveNormalWindows()`（`:354-361` 的 topmost→notopmost 抖动）。

- **影响**：展开态面板是一个 **owner 为 NULL 的普通顶层窗口**，只靠 `HWND_TOP` / topmost 抖动抢一次 z-order。Win+D 时 shell 把桌面宿主抬到最前，这一次抢占被撤销，此后：`ProbeDesktopAttachment` 直接返回 `Ok`（连 `IsWindowVisible` / `IsBehindDesktopLayer` 都不跑），`DesktopVisibilityTimer_Tick` 第 7027 行早退，`DesktopRepairTimer_Tick` 整个修复块被 `!_expanded` 关掉 → **没有任何定时器能把它救回来**，只能等用户悬停/点击/收起再展开（`SetExpanded` → `RaiseAboveNormalWindows`）。多显示器、常驻展开（Station/Dock 展开、`IsPermanentlyExpanded`）用户会长期看到「面板在壁纸后面」。这也是 4.2.4 发布说明已经承认的已知问题——但代码里没有任何补偿机制，连一条「已知不可修复」的诊断记录都没有。

- **建议**：分三件事。(1) `ProbeDesktopAttachment` 不要对 `_expanded` 返回 `Ok`——`Ok` 的语义是「已确认正常」，而这里实际是「未检查」；新增 `DesktopAttachmentState.ExpandedUnchecked`（或 `Unknown`），让调用方能区分并跳过日志。(2) 展开态的自愈不能用 owner/锚点那套（`_expandedOwner` 是 0，锚点无意义），应走 `RaiseAboveNormalWindows()` 的 topmost 抖动，并把它包成 `RepairExpandedZOrder()`：先 `HWND_TOPMOST` 再 `HWND_NOTOPMOST`（两次都检查返回值，见另一条），在 `_expanded` 时由 `DesktopVisibilityTimer_Tick` 调用，且只在 `IsBehindDesktopLayer()` 为真时调用，避免每秒抖动。(3) 抖动本身会被 Win+D 撤销，长期方案是监听 shell 事件（`SetWinEventHook` 的 `EVENT_SYSTEM_FOREGROUND` / `EVENT_OBJECT_SHOW` 针对 `Progman`/`WorkerW`），或在展开态给面板一个真实的 owner（把 `_expandedOwner` 从 `IntPtr.Zero` 换成展开前的宿主），让 shell 抬高桌面时按 owner 规则把面板一起抬起来。

---

### [P3] `RepairDesktopAttachment()` 返回修复**前**状态，两处日志读起来正好相反

- **证据**：`src/TuckPane/Services/DesktopLayerService.cs:126-129` 与 `:156`
  ```csharp
  internal DesktopAttachmentState RepairDesktopAttachment()
  {
      DesktopAttachmentState state = ProbeDesktopAttachment();
      if (state == DesktopAttachmentState.Ok) return DesktopAttachmentState.Ok;
      ...
      return state;   // ← 修复动作全部做完后，返回的仍是修复前探测到的值
  ```
  `src/TuckPane/MainWindow.xaml.cs:7013-7015`
  ```csharp
  if (attachment != DesktopAttachmentState.Ok)
  {
      AppLogger.Performance($"desktop repair pass fixed layer state={attachment} {desktopLayer.DescribeAttachment()}");
  }
  ```
  `src/TuckPane/MainWindow.xaml.cs:7036-7039`：同样的 `$"desktop layer repaired state={state} ..."`

- **影响**：日志写的是「fixed layer state=BehindDesktop」/「repaired state=Hidden」，字面读起来是「修完之后层处于 BehindDesktop/Hidden」，与实际语义（修之前是那个状态）完全相反。更要紧的是：由于返回值就是入参状态，**调用方永远无法判断修复是否成功**——修复链路上的 `SetWindowLongPtr`（`:136`）、`SetWindowPos`（`:144`、`:153` 经 `ReassertDesktopAttachment` 的 `:168`）返回值全部被 `_ =` 丢弃，`SetWindowPos` 失败（例如锚点窗口刚被销毁、`ERROR_INVALID_WINDOW_HANDLE`）时函数照样返回 `Hidden`，日志照样写「repaired」。现场拿到 perf trace 也推不出「到底修没修好」。

- **建议**：修复后重新探测一次并返回前后两个状态，例如
  ```csharp
  internal readonly record struct RepairResult(DesktopAttachmentState Before, DesktopAttachmentState After, bool Attempted);
  ```
  `After` 用 `ProbeDesktopAttachment()` 复查；日志改成 `desktop repair before={Before} after={After}`。同时把 `SetWindowPos` / `SetWindowLongPtr` 的返回值收起来（失败时 `Marshal.GetLastWin32Error()`），失败至少记一条 `DiagnosticStage.Failed`，别让「修复静默失败」成为默认行为。

---

### [P2] 「输入态」与「修复态」两套逻辑无仲裁地争写 `GWLP_HWNDPARENT`，右键菜单/对话框打开期间 owner 被定时器抢回

- **证据**：`src/TuckPane/Services/DesktopLayerService.cs:299-312`（进入输入态时**主动把 owner 清成 NULL**）
  ```csharp
  if (enabled)
  {
      _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT, _expandedOwner);
      ...
      _ = NativeMethods.SetForegroundWindow(_window);
  }
  ```
  `src/TuckPane/Services/DesktopLayerService.cs:224-227` + `:132-137`（探测把「owner ≠ 图标视图」判成故障，修复把 owner 写回图标视图）
  ```csharp
  if (NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT) != _desktopIconView)
      return DesktopAttachmentState.OwnerLost;
  ```
  `src/TuckPane/MainWindow.xaml.cs:7029`（可见性定时器**有**这道门）vs `:6959`（修复定时器**没有**）
  ```csharp
  if (_desktopLayer is null || _desktopLayer.IsInputActivationEnabled) return;   // :7029
  if (!_expanded && !_animating && !_pressActive)                                 // :6959 没有 IsInputActivationEnabled
  ```
  进入输入态但 `_expanded == false` 的调用点：`MainWindow.xaml.cs:6299`、`MainWindow.OrganizerMenu.cs:87`、`MainWindow.OrganizerTitle.cs:37`

- **影响**：`_expandedOwner` 在三处构造点全部传 `IntPtr.Zero`（`MainWindow.xaml.cs:533`、`:594`、`MainWindow.Dock.cs:51`），所以 `:301` 实际执行的是「把 owner 设为 NULL」——面板从桌面层脱离，变成一个无 owner 的顶层窗口。这是**有意的**（输入态要能拿前台焦点），但修复定时器不知道这件事：它只检查 `!_expanded && !_animating && !_pressActive`，于是在收起态打开右键菜单/内联标题编辑时（此时 `_expanded` 为 false、`_overlayOpenCount > 0` 但修复定时器不看这个计数），2–8 秒后必然发生：
  1. `ProbeDesktopAttachment` 读到 owner == NULL ≠ 图标视图 → `OwnerLost`；
  2. `RepairDesktopAttachment` 把 owner 写回桌面图标视图，并 `SetWindowPos` 重排到 `HWND_TOP`；
  3. 面板被重新挂到 explorer.exe 的窗口下、并被抬到菜单之上 → 用户看到**右键菜单被面板挡住**或菜单瞬间消失；输入态下刚拿到的前台焦点被夺走，内联编辑（`OrganizerTitle.cs:37`）中途失焦，已输入内容丢失（`TextBox` 失焦提交/回滚行为由 WinUI 决定，最坏是整段文字没了）。
  只要用户在右键菜单里停留超过一个修复周期（省电档 8 秒、高性能档 2 秒）就能稳定复现。这是 4.2.4 之前就存在、但被 4.2.4 新增的可见性定时器掩盖的路径（新定时器修了 `Hidden`，却把 `OwnerLost` 留给老定时器去抢）。

- **建议**：把「谁拥有 owner」收敛成单一状态机，别让两个调用方各写各的：`DesktopLayerService` 内部维护 `enum OwnerIntent { Desktop, Input }`，`RepairDesktopAttachment()` 在 `OwnerIntent == Input` 时直接返回 `Ok`（不要报 `OwnerLost`）；或者最小改动——在 `DesktopRepairTimer_Tick` 的门里补上 `&& _overlayOpenCount == 0 && !(_desktopLayer?.IsInputActivationEnabled ?? false)`，与 `:7029` 对齐。同时 `ProbeDesktopAttachment` 应把「owner 为 NULL 且输入态开启」判为 `Ok`，而不是 `OwnerLost`，这样即使将来有新的输入态入口漏了门也不会被误修。

---

### [P2] `DesktopInsertAnchor()` 的 `GW_HWNDPREV` 在壁纸引擎 / 顶层宿主 / 虚拟桌面下取到错误锚点，反而把面板塞进壁纸下面

- **证据**：`src/TuckPane/Services/DesktopLayerService.cs:180-192`
  ```csharp
  IntPtr root = DesktopHostRoot();
  if (root == IntPtr.Zero) return IntPtr.Zero;
  IntPtr above = NativeMethods.GetWindow(root, NativeMethods.GW_HWNDPREV);
  return above == IntPtr.Zero ? NativeMethods.HWND_TOP : above;
  ```
  使用点：`src/TuckPane/Services/DesktopLayerService.cs:70-71`、`:167`（`!IsSafeAnchor(anchor)` 时退回 `SWP_NOZORDER`）

- **影响**：`SetWindowPos` 的 `hWndInsertAfter` 语义是「把本窗口放到该窗口**下面**」，所以取 `GW_HWNDPREV(root)`（紧贴在桌面 root 之上的那个窗口）再插到它下面，确实是「贴着桌面、在所有普通窗口之下」的正确算法——**前提是 root 之下/之上没有别的桌面类窗口**。四种情况下前提不成立：
  1. **第三方壁纸引擎（Wallpaper Engine / Lively / 动态壁纸）**：它们自己创建顶层 `WorkerW` 并插在 `Progman` 之上，于是 `GW_HWNDPREV(Progman)` 返回的是那个壁纸 `WorkerW` → 面板被插到壁纸窗口**下面** → 视觉上「被壁纸盖住」。这正是发布说明里承认的那个现象的**另一个成因**，而且它跟「展开态不修复」无关，收起态也会中招。
  2. **root 位于 z-order 顶端**（干净桌面 / 全屏壁纸）：`GW_HWNDPREV` 返回 NULL → 返回 `HWND_TOP` → 但 `IsSafeAnchor` 恰恰把 `IntPtr.Zero` 判为不安全（`:173-174`），于是退回 `SWP_NOZORDER`，等于**什么都不做**，owner 变了也不重排（见第一条 finding）。
  3. **虚拟桌面切换**：`_window` 被 cloaked 时 `ProbeDesktopAttachment` 直接返回 `Ok`（`:208-212`），修复链整条短路；切回来之后没有任何一次 `Reattach` 会把面板重新排回壁纸之上，于是「切走再切回，面板不见了」。
  4. **Explorer 重启**：`DesktopHostRoot()` 因 `_desktopIconView` 失效返回 `IntPtr.Zero` → `DesktopInsertAnchor` 返回 `IntPtr.Zero` → `IsSafeAnchor` 假 → `SWP_NOZORDER` → 只换 owner 不重排。
  另外 `GetWindow(root, GW_HWNDPREV)` 是跨进程查询，Explorer 繁忙时会阻塞调用线程（这里是 UI 线程）。

- **建议**：不要用「root 的前一个窗口」当锚点，改用**桌面层内部的最后一个子窗口**做锚点：`GetWindow(root, GW_CHILD)` 后一路 `GW_HWNDLAST`/`GW_HWNDPREV` 找到 `root` 子树里最靠下的子窗口，插到它下面——这样永远落在桌面宿主内部，壁纸引擎再叠多少层 `WorkerW` 都不影响。对第 2/4 种情况，`IsSafeAnchor` 应把 `HWND_TOP` 视为**合法**（它正是「root 在顶端」时的正确答案），只把 `HWND_BOTTOM` 和 `_window` 自身判为非法。对第 3 种情况，在 `WM_SETTINGCHANGE`/`WM_DISPLAYCHANGE` 之外再监听 `WM_WTSSESSION_CHANGE` 或干脆在 `DesktopVisibilityTimer_Tick` 里把 `DWMWA_CLOAKED` 从「直接返回 Ok」改成「记录 cloaked，解除 cloaked 后强制 `Reattach()` 一次」。

---

### [P2] `IsBehindDesktopLayer()` 只认一个 `desktopRoot` 句柄，多 WorkerW / 多 Progman 下判定失真

- **证据**：`src/TuckPane/Services/DesktopLayerService.cs:237-253`
  ```csharp
  IntPtr iconView = _desktopIconView;
  if (iconView == IntPtr.Zero) return false;
  IntPtr desktopRoot = NativeMethods.GetAncestor(iconView, NativeMethods.GA_ROOT);
  if (desktopRoot == IntPtr.Zero) desktopRoot = iconView;

  for (IntPtr walk = NativeMethods.GetTopWindow(IntPtr.Zero);
       walk != IntPtr.Zero;
       walk = NativeMethods.GetWindow(walk, NativeMethods.GW_HWNDNEXT))
  {
      if (walk == _window) return false;
      if (walk == desktopRoot) return true;
  }
  ```
  `src/TuckPane/Services/DesktopLayerService.cs:414`：`FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null)` 只取**第一个**命中的 `SHELLDLL_DefView`。

- **影响**：桌面在 Win10 1903+ 上通常是 `Progman` + 至少一个**独立的顶层 `WorkerW`** 共同构成（`0x052C` 之后 `SHELLDLL_DefView` 会被搬到顶层 `WorkerW` 里，壁纸由另一个 `WorkerW` 绘制）。判定只把「`DefView` 的 `GA_ROOT`」当作唯一的地板线，于是两类误判：
  1. **假阴性（该修不修）**：面板位于 `DefView` 宿主之上、但被另一个更靠上的桌面类窗口（壁纸淡入 `WorkerW`、第三方壁纸引擎的全屏窗口）盖住时，遍历先遇到 `_window` → 返回 `false` → `Ok` → 不修。这正好是「被壁纸盖住」的视觉现象，但代码认为正常。
  2. **假阳性（反复修）**：面板位于 `Progman` 与 `DefView` 宿主 `WorkerW` 之间时（此时视觉上完全可见，只是压着壁纸），遍历先遇到 `desktopRoot` → 返回 `true` → 每 1–8 秒判一次 `BehindDesktop`，每次都 `SetWindowPos` 重排 z-order，并可能触发 `Hidden` 分支的 hide/show 抖动（见下条）。稳态下就是持续的窗口管理器活动。
  另外 `FindDesktopIconView` 命中第一个就 `return false` 停止枚举，多桌面宿主（多用户会话、Explorer 双实例、某些虚拟桌面工具）场景下选中的不一定是当前可见的那个。

- **建议**：把「地板线」从单一句柄换成**一组**句柄：先枚举所有 `Progman`/`WorkerW` 顶层窗口（`EnumWindows` + `IsDesktopHostClassName` 已经在 `:447-455` 有了），取其中 z-order **最靠上**的那个作为判定线；或者更稳的做法是直接问 shell——`IVirtualDesktopManager::IsWindowOnCurrentVirtualDesktop` 之外没有官方 API，但可以用 `GetWindowLongPtr` 判断 `_window` 的 owner 链是否仍在桌面宿主子树内，配合 `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` 与 `WindowFromPoint` 采样窗口中心的实际命中窗口，做「点采样」判定而不是全链遍历：`WindowFromPoint(面板中心) == _window` 就说明没被盖住，一次调用、不依赖 z-order 结构、天然覆盖多 WorkerW。判定失败时返回 `Unknown` 而不是 `Ok`/`BehindDesktop` 二选一。

---

### [P2] `_lastKnownDesktopIconView` 静态缓存只用 `IsWindow` 校验，Explorer 重启后句柄回收会把面板挂到无关窗口上

- **证据**：`src/TuckPane/Services/DesktopLayerService.cs:18`、`:441-445`
  ```csharp
  private static IntPtr _lastKnownDesktopIconView;
  ...
  internal static IntPtr GetCachedDesktopIconView()
  {
      IntPtr cached = Volatile.Read(ref _lastKnownDesktopIconView);
      return cached != IntPtr.Zero && NativeMethods.IsWindow(cached) ? cached : IntPtr.Zero;
  }
  ```
  `src/TuckPane/Services/DesktopLayerService.cs:214-227`：探测时同样只判 `IsWindow`，通过后就认为 owner 应等于它
  ```csharp
  if (_desktopIconView == IntPtr.Zero || !NativeMethods.IsWindow(_desktopIconView))
  {
      _desktopIconView = FindDesktopIconView(allowWorkerSpawn: false);
  }
  ...
  if (NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT) != _desktopIconView)
      return DesktopAttachmentState.OwnerLost;
  ```
  修复侧：`src/TuckPane/Services/DesktopLayerService.cs:136` `SetWindowLongPtr(_window, GWLP_HWNDPARENT, iconView)`

- **影响**：HWND 是会被复用的整数。Explorer 崩溃/重启后旧 `SHELLDLL_DefView` 句柄被销毁，`IsWindow` 立刻为假——这是正常路径（会重新查找）。**但如果句柄在重新查找之前被别的窗口复用**（Explorer 重启期间窗口创建非常密集），`IsWindow` 返回真、类名却已经不是 `SHELLDLL_DefView`，代码无法分辨：`ProbeDesktopAttachment` 判定 `OwnerLost` → `RepairDesktopAttachment` 把面板的 owner 设成那个**无关窗口**。`SetWindowLongPtr(GWLP_HWNDPARENT, ...)` 跨进程合法，后果是：面板变成某个随机窗口的 owned window（跟随它最小化、被它裁剪、它销毁时面板一起销毁），且 `DesktopInsertAnchor()` 以它的 root 为地板线，z-order 落到完全错误的位置。同一份缓存还被 `ShellDragService.cs:504-512` 用来定位桌面 `SysListView32` 做拖放命中，句柄错了会把组件拖到错误目标。这个缓存是 `static`，跨所有面板实例共享，一次污染全体中招。

- **建议**：缓存必须带「身份」而不是只有句柄：记录 `GetClassName` + 创建该句柄的进程 ID（`GetWindowThreadProcessId`）+ 桌面宿主 root，校验时三项全对才复用；最省事的是缓存 `(IntPtr view, uint pid)`，校验 `IsWindow(view) && GetClassName(view) == "SHELLDLL_DefView" && pid == GetWindowThreadProcessId(view)`。同时订阅 Explorer 重启信号（`RegisterWindowMessage("TaskbarCreated")`，`TrayIconService` 里通常已经在处理）来主动清空缓存。`FindDesktopIconView` 内部已有类名校验，把同样的校验提取成 `IsValidDesktopIconView(IntPtr)` 供缓存读取路径复用即可。

---

### [P2] `Hidden` 分支的 hide/show 往返在同一个消息里跑完，DWM/WinUI 不会真的重建帧

- **证据**：`src/TuckPane/Services/DesktopLayerService.cs:140-154`
  ```csharp
  if (state == DesktopAttachmentState.Hidden)
  {
      // A window hidden by the shell misses WM_SHOWWINDOW, so WinUI keeps its last frame.
      // Force a hide/show round trip so the frame is rebuilt before it is shown again.
      _ = NativeMethods.SetWindowPos(_window, IntPtr.Zero, 0, 0, 0, 0,
          SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
      ReassertDesktopAttachment();   // ← 紧接着 SWP_SHOWWINDOW，中间没有任何消息泵
  }
  ```
  调用上下文：`src/TuckPane/MainWindow.xaml.cs:7036`（DispatcherQueue 定时器回调，整段同步执行）

- **影响**：`SWP_HIDEWINDOW` 与随后的 `SWP_SHOWWINDOW` 在**同一次 dispatcher 回调**里背靠背执行，中间没有 `WM_PAINT`/`WM_SHOWWINDOW` 的派发机会，也没有一次 present。对 DWM 而言窗口的可见性状态在一次合成周期内被改了又改回去，中间那个「隐藏」状态**从未被合成器观察到**，因此注释所依赖的「隐藏会让 WinUI 丢掉最后一帧、显示时重建」这一前提不成立——最可能的结果是：表面（surface）原样保留，隐藏期间残留的旧帧继续被复用，也就是这次修复**没有效果**，而它又只在 `Hidden` 状态下触发，所以现场表现为「偶发还是白屏/黑块，重启面板才好」。另外两次调用的返回值都被丢弃，即使 `SetWindowPos` 失败也没有痕迹。标 **待实测**。

- **建议**：把往返拆到两个 tick：`Hidden` 时只做隐藏 + 记一个 `_pendingReshow = true`，下一次 tick 再 `ReassertDesktopAttachment()`；或在 hide 之后显式让出（`DwmFlush()` + `RedrawWindow(_window, ..., RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN)`，`NativeMethods` 里两者都已声明，`RDW_UPDATENOW`/`RDW_ALLCHILDREN` 在 `:164-166`）。若目标是让 WinUI 重建 XAML island 的帧，更可靠的是让内容层自己失效（`WindowRoot.InvalidateMeasure()` / 重新 `ApplyBounds`），而不是靠窗口可见性抖动。验证方法：`TUCKPANE_PERF_TRACE=1` 下人为用 `ShowWindow(hwnd, SW_HIDE)` 制造 `Hidden`，观察是否出现「修复后仍花屏」；或加临时计数比较 hide/show 前后 `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` 与截图差异。

---

### [P2] 硬编码 1Hz 的可见性定时器绕过了省电档的 8000ms 配置，且与修复定时器重复探测

- **证据**：`src/TuckPane/MainWindow.xaml.cs:342-345`（构造时写死 1000ms）
  ```csharp
  _desktopVisibilityTimer = DispatcherQueue.CreateTimer();
  _desktopVisibilityTimer.Interval = TimeSpan.FromMilliseconds(1000);
  ```
  `src/TuckPane/MainWindow.xaml.cs:6096-6101`（`RefreshPerformanceSettings` 只更新修复定时器，**从不更新可见性定时器的间隔**）
  ```csharp
  _stationPointerTimer.Interval = TimeSpan.FromMilliseconds(tuning.PointerPollMilliseconds);
  _desktopRepairTimer.Interval = TimeSpan.FromMilliseconds(tuning.DesktopRepairMilliseconds);
  ```
  `src/TuckPane/Models/AppState.cs:208-210`：`PowerSaver => new(100, 8000, false)`、`HighPerformance => new(25, 2000, true)`、`_ => new(50, 4000, true)`
  两个 tick 都调用同一个 `RepairDesktopAttachment()`：`MainWindow.xaml.cs:7011`、`:7036`

- **影响**：省电档下用户期望「桌面层每 8 秒检查一次」，实际是**每秒一次**（可见性定时器）+ 每 8 秒一次（修复定时器），探测频率被提高了 8 倍，而且每次探测都包含一次 `IsBehindDesktopLayer()` 的**全 z-order 遍历**（`DesktopLayerService.cs:244-250`，每个窗口一次 `GetWindow`，桌面上开 150 个窗口就是 150 次 user32 调用，全部在 UI 线程上同步执行）。笔记本电池场景下这是持续的唤醒源：`DispatcherQueueTimer` 每秒唤醒一次 UI 线程，即使什么也没发生；`ProbeDesktopAttachment` 还会在图标视图失效时跑 `EnumWindows` + 每个窗口 `GetClassName`（`:450-455` 每次都 `new StringBuilder(32)`）。另外两条路径功能重叠：`DesktopRepairTimer_Tick` 在 `state == Ok` 时还会额外调一次 `Reattach()`（`:7019`），等于每个 tick 至少一次 `SetWindowPos`。这两条定时器没有任何「上次探测结果缓存/节流」，重复劳动。

- **建议**：把可见性定时器的间隔也纳入 `PerformanceTuning`（例如 `DesktopVisibilityMilliseconds = DesktopRepairMilliseconds / 2`，或省电档直接与修复定时器同频），并在 `RefreshPerformanceSettings` 里一并赋值；更好的做法是**合并成一个定时器**——探测逻辑只保留一份，按「上次探测结果」自适应：`Ok` 时退避到 8s，`BehindDesktop`/`Hidden` 时短间隔重试（1s）直到修好，这样稳态开销降到省电档的水平，异常时又能快速自愈。`IsBehindDesktopLayer` 换成上面建议的 `WindowFromPoint` 点采样后，单次探测成本从 O(z-order) 降到 O(1)，1Hz 也就无所谓了。

---

### [P2] 关闭路径只停了 3 个定时器、所有 `Tick` 都没有解绑，6 个定时器在窗口关闭后继续打进已销毁的 XAML 树

- **证据**：`src/TuckPane/MainWindow.xaml.cs:7073-7083`（`Closed` 里只停三个）
  ```csharp
  _desktopRepairTimer.Stop();
  _desktopVisibilityTimer.Stop();
  ...
  _stationPointerTimer.Stop();
  ```
  同一文件里创建的定时器共 9 个、订阅点全部是 `Tick +=`：`:329` `_longPressTimer`、`:334` `_externalHoverTimer`、`:340` `_desktopRepairTimer`、`:345` `_desktopVisibilityTimer`、`:351` `_stationPointerTimer`、`:356` `_watcherDebounceTimer`、`:361` `_interactionSaveTimer`、`:366` `_canvasResizeInputTimer`、`:371` `_itemTouchHoldTimer`；全仓库**没有任何一处** `Tick -=`。其中 `:361` 是 async void 形态：
  ```csharp
  _interactionSaveTimer.Tick += async (_, _) => await SaveStateAsync();
  ```

- **影响**：`Closed` 之后 `_closing = true`，但 `_longPressTimer` / `_externalHoverTimer` / `_watcherDebounceTimer` / `_interactionSaveTimer` / `_canvasResizeInputTimer` / `_itemTouchHoldTimer` 六个仍处于 `IsRepeating = true` 状态，只要应用主 `DispatcherQueue` 还活着（多收纳窗场景下关闭其中一个窗口、进程继续运行），它们就会继续以各自的间隔回调：
  - `PollCanvasResizeInput()` / `ItemTouchHoldTimer_Tick` / `ExternalHoverTimer_Tick` 会读 `_hwnd`、`_canvas.ActualWidth`、XAML 元素——窗口关闭后这些要么是 `IntPtr.Zero`（`GetWindowRect` 失败、`Marshal.GetLastWin32Error` 得到 `ERROR_INVALID_WINDOW_HANDLE`），要么是已 Dispose 的 WinRT 对象，抛 `COMException (0x80070578)` 或 `InvalidOperationException`；
  - 这些异常发生在 dispatcher 回调里，**没有 try/catch**，`async (_, _) => await SaveStateAsync()` 更是 async void —— 异常直接进 `AppDomain.UnhandledException` / 进程终止。用户看到的是「关掉一个收纳窗，整个 TuckPane 跟着退出」，而且不留崩溃报告（`AppLogger` 只在业务代码里 try/catch）。
  - 即使不抛异常，`DispatcherQueue → Timer → Tick 委托 → MainWindow` 的强引用链在 `Stop()` 之后仍然存在（`DispatcherQueueTimer` 由 `DispatcherQueue` 持有），已关闭窗口的整棵 XAML 树、`_catalog`、`FileSystemWatcher` 都无法回收；反复开关收纳窗会持续抬高内存。标 **待实测**（GC 是否真的被钉住取决于 `DispatcherQueueTimer` 内部是否弱引用，但「关闭后仍回调」这一点由代码直接确定）。

- **建议**：在 `MainWindow_Closed` 里统一做一次「全部解绑」而不是逐个 `Stop()`：把所有 `DispatcherQueueTimer` 放进一个 `List<DispatcherQueueTimer>` 字段（或抽一个 `StopAndUnsubscribe(timer, handler)` 帮助方法），`Closed` 里遍历 `Stop()` + `Tick -=`；`SetTimerRunning`（`:6138`）已经是唯一的启停入口，顺势让它在 `_closing` 时拒绝启动即可。`:361` 的 async void lambda 应改成 `async void` 内部 `try/catch` 或换成带 `CancellationToken` 的 `Task`，至少不能让异常逃逸到 dispatcher。

---

### [P3] `Dispose()` 在 `Closed` 末尾调用且带 `IsWindow` 守卫，`RemoveWindowSubclass` 与 owner 还原实际上是死代码

- **证据**：`src/TuckPane/MainWindow.xaml.cs:7114-7116`（`Dispose` 是 `Closed` 的最后一句）
  ```csharp
  ...
  _desktopLayer?.Dispose();
  ```
  `src/TuckPane/Services/DesktopLayerService.cs:319-326`
  ```csharp
  public void Dispose()
  {
      if (NativeMethods.IsWindow(_window))
      {
          _ = NativeMethods.RemoveWindowSubclass(_window, _activationGuard, ActivationSubclassId);
          _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT, _expandedOwner);
      }
  }
  ```

- **影响**：WinUI 3 的 `Window.Closed` 在窗口销毁流程里触发，走到 `:7116` 时 `_window`（即 `_hwnd`）通常已经不可用，`IsWindow` 返回 false，于是 `Dispose` 的两个动作都不执行。子类化随窗口销毁自动失效（无害），但由此暴露三个问题：
  1. **静态缓存永不清理**：`_lastKnownDesktopIconView` 是 `static`（`:18`），`Dispose` 里没有清；进程内所有 organizer 窗口共享，窗口全关后它仍指向一个可能已被回收的句柄（与 `GetCachedDesktopIconView` 只做 `IsWindow` 校验叠加，见第 5 条）。
  2. **`Dispose` 非幂等、无「已释放」标志**：`_window` 仍非零，重复调用会重复 `RemoveWindowSubclass`（返回 false，被忽略）；若将来有人在窗口存活时调用 `Dispose`，`SetWindowLongPtr(GWLP_HWNDPARENT, _expandedOwner)` 会把面板 owner 设为 NULL，面板就此脱离桌面层且没有任何定时器会把它救回来（`_desktopLayer` 已置空）。
  3. **`Dispose` 不还原窗口样式**：`ApplyToolWindowStyle`（`:328-352`）剥掉了 `WS_CAPTION`/`WS_THICKFRAME` 并加了 `WS_EX_NOACTIVATE`，`Dispose` 一样都不还原。当前窗口随后即销毁，所以现在无害；但 `_desktopLayer` 的设计是「可反复 Attach/Detach」（`MainWindow` 在 `:533`/`:594` 与 Dock 路径反复构造），一旦将来出现「Dispose 后复用同一个 HWND」的路径（例如把面板在 Station/Dock 之间迁移），窗口会带着无边框 + 不可激活的样式继续存在。
  另外 `MainWindow` 从不把 `_desktopLayer` 置空，`Closed` 之后若还有回调走到 `_desktopLayer?.` 分支（例如上面那条泄漏的定时器），会对着已 Dispose 的对象继续 `SetWindowPos`。

- **建议**：把 `Dispose` 的清理从「依赖窗口仍存活」改成「无条件执行」：`RemoveWindowSubclass` 前不必判 `IsWindow`（失败无害），静态缓存用一个显式的 `ResetCachedDesktopIconView()` 在最后一个窗口关闭时清空；加 `_disposed` 标志保证幂等，并把 `_window` 置 `IntPtr.Zero`；`Dispose` 里还原 `GWL_EXSTYLE`/`GWL_STYLE` 的原始值（在 ctor 里保存快照）。同时 `MainWindow_Closed` 里在 `Dispose()` 之后补一句 `_desktopLayer = null;`，避免关闭后的回调继续操作它。

---

### [P2] `ownerChanged == false` 时连 z-order 一起跳过：启动首帧与每次拖拽结束后，小组件悬在所有普通窗口之上

- **证据**：`src/TuckPane/Services/DesktopLayerService.cs:61-71`
  ```csharp
  bool ownerChanged = _desktopIconView != IntPtr.Zero &&
      NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT) != _desktopIconView;
  ...
  IntPtr anchor = DesktopInsertAnchor();
  if (!ownerChanged || !IsSafeAnchor(anchor)) flags |= NativeMethods.SWP_NOZORDER;
  ```
  `src/TuckPane/Services/DesktopLayerService.cs:85-101`（收起时只把 topmost 降级，随后 `Reattach` 又因 `ownerChanged == false` 跳过重排）
  ```csharp
  if (!expanded && _stayTopmost) { ... HWND_NOTOPMOST ... }
  _expanded = expanded;
  _stayTopmost = expanded && stayTopmost;
  Reattach(showWindow);
  if (expanded && !stayTopmost) RaiseAboveNormalWindows();
  ```
  触发点：`src/TuckPane/MainWindow.xaml.cs:491-493`（启动时 owner 已被设成同一个图标视图）→ `DesktopLayerService.cs:32` 首次 `Reattach`；`MainWindow.xaml.cs:3394-3398`（拖拽开始 `SetExpanded(true, stayTopmost: true)` → 拖拽结束 `SetExpanded(false)`）

- **影响**：`ownerChanged` 只在「owner 与图标视图不一致」时才为真，但**窗口在 z-order 里的位置和 owner 是否变化无关**。两个必然踩中的场景：
  1. **启动首帧**：`InitializeHostWindow` 先把 owner 设成桌面图标视图（`MainWindow.xaml.cs:491-493`），紧接着 `DesktopLayerService` ctor 里的 `Reattach` 发现 owner 已一致 → `SWP_NOZORDER`；而 `ApplyToolWindowStyle` 收尾那次 `SetWindowPos`（`:344-351`）用的是 `hWndInsertAfter = IntPtr.Zero`（= `HWND_TOP`）且**没有** `SWP_NOZORDER`，把刚创建的窗口抬到了非 topmost 带的最顶层。两者叠加的结果是：**启动后小组件盖在所有普通窗口之上**，直到用户手动激活某个窗口。
  2. **每次拖拽结束**：`SetExpanded(true, stayTopmost: true)` 把窗口设成 `HWND_TOPMOST`；结束时 `HWND_NOTOPMOST` 只是取消 topmost 标记，窗口仍落在非 topmost 带的**最顶层**，随后的 `Reattach` 又因 `ownerChanged == false` 跳过重排 → 小组件继续压在浏览器/编辑器之上。之后每个修复周期（2–8s）走 `Ok` 分支再 `Reattach()` 一次，同样跳过 —— 直到某个外部事件把 owner 弄脏才恢复。
  这两条都直接违反「桌面小组件应贴在壁纸之上、普通窗口之下」的产品预期，且不需要任何异常条件即可复现。

- **建议**：把「owner 是否变化」和「是否需要重排 z-order」解耦。`Reattach` 每次都应该尝试按 `DesktopInsertAnchor()` 定位，只有在 `anchor` 非法时才 `SWP_NOZORDER`；即把 `:71` 改成 `if (!IsSafeAnchor(anchor)) flags |= SWP_NOZORDER;`，并让 `IsSafeAnchor` 接受 `HWND_TOP`（见第 1 条）。`ApplyToolWindowStyle` 收尾那次 `SetWindowPos` 补上 `SWP_NOZORDER`，避免样式变更顺带抬升 z-order。`SetExpanded` 收起时不要只做 `HWND_NOTOPMOST`，而应显式走一次带锚点的重排。

---

### [P3] `WM_GETMINMAXINFO` 分支不调用 `DefSubclassProc`，是一段既无用又有隐患的死代码

- **证据**：`src/TuckPane/Services/DesktopLayerService.cs:390-396`
  ```csharp
  if (message == NativeMethods.WM_GETMINMAXINFO)
  {
      NativeMethods.MINMAXINFO info = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
      info.MinTrackSize = new NativeMethods.POINT { X = 1, Y = 1 };
      Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
      return IntPtr.Zero;
  }
  ```
  样式前提：`src/TuckPane/Services/DesktopLayerService.cs:331-337`（`WS_CAPTION` / `WS_THICKFRAME` 已被剥掉，`WS_POPUP`）

- **影响**：
  1. **当前基本是死代码**：窗口没有 `WS_THICKFRAME`，没有可拖拽的尺寸边框，用户无法发起交互式 resize，`WM_GETMINMAXINFO` 在正常使用中几乎不会被发送（`SetWindowPos`/`AppWindow.Resize` 不触发它）。`MinTrackSize = {1,1}` 想达到的「允许缩到极小」实际由 WinUI 自己控制，与这里无关。
  2. **一旦被触发就会丢默认处理**：直接 `return IntPtr.Zero` 而不调 `DefSubclassProc`，等于完全替代 `DefWindowProc` 对 `WM_GETMINMAXINFO` 的处理（`ptMaxSize`/`ptMaxPosition`/`ptMaxTrackSize` 的 DPI 与工作区修正）。系统在发送前会预填这些字段，所以目前多半「碰巧正确」；但如果将来恢复 `WS_THICKFRAME`（例如做可调整大小的展开态），最大化尺寸/多显示器下的 `ptMaxPosition` 会算错，且这种错误只在双屏 + 高 DPI 下出现，很难定位。
  3. 每次收到该消息都做一次 `PtrToStructure` + `StructureToPtr` 往返封送，纯属浪费。

- **建议**：要么删掉这个分支（当前无边框，无必要），要么改成「改完 `MinTrackSize` 后仍然 `return DefSubclassProc(...)`」——即只做增量修改、保留默认处理。若目的是让面板能缩到 1×1，应在 `AppWindow.Resize` 的调用侧处理，而不是靠 `WM_GETMINMAXINFO`。

---

### [P3] `WM_MOUSEACTIVATE` 无条件返回 `MA_NOACTIVATE`，展开态面板对键盘 / 触摸 / 屏幕阅读器不可达

- **证据**：`src/TuckPane/Services/DesktopLayerService.cs:386-389`
  ```csharp
  if (message == NativeMethods.WM_MOUSEACTIVATE && !_allowActivation)
  {
      return new IntPtr(NativeMethods.MA_NOACTIVATE);
  }
  ```
  `src/TuckPane/Services/DesktopLayerService.cs:341`（`WS_EX_NOACTIVATE` 是常态，只有 `SetInputActivation(true)` 才清，见 `:287`）；`SetExpanded`（`:83-102`）不碰这两者

- **影响**：`_allowActivation` 默认 false，`WS_EX_NOACTIVATE` 常驻，因此**常态下面板永远无法被激活**。`MA_NOACTIVATE` 的语义是「不激活但仍派发鼠标消息」，所以鼠标点击仍能命中控件，但：
  - **键盘**：没有激活就没有键盘焦点。展开态（`_expanded == true`）同样处于 `WS_EX_NOACTIVATE` 状态（`SetExpanded` 不调用 `SetInputActivation`），于是展开后的面板**不能**用 `Tab` / 方向键 / `Esc` 操作，列表项选中后也无法用键盘继续——只能用鼠标。这是可访问性缺口（WCAG 2.1.1 键盘可达），也影响「无鼠标」场景。
  - **触摸/笔**：`MA_NOACTIVATE` 绕过了 Windows「点击未激活窗口先激活它」的惯例，触屏上第一次点击只命中控件而不进入编辑态；软键盘（TabTip）不会自动弹出，因为没有任何可编辑控件获得焦点。
  - **辅助技术**：讲述人/UIA 依赖焦点变化来跟踪位置，一个永不激活的顶层窗口无法把焦点移进去，屏幕阅读器只能读到窗口级信息。
  - **IME**：只有走 `SetInputActivation(true)` 的路径（内联标题编辑 `MainWindow.OrganizerTitle.cs:37`、各类对话框）才能输入中文；若某条路径的 `SetInputActivation(false)`（如 `OrganizerTitle.cs:78`）早于输入法组合结束，候选窗会在组合中途被销毁，已输入的拼音丢失。标 **待实测**。
  注意 `WM_MOUSEACTIVATE` 是发给**即将被激活的顶层窗口**的，所以这个子类化能覆盖 XAML island 里的所有子控件——影响面是整块面板，不是某个按钮。

- **建议**：把拦截条件从「`!_allowActivation`」改成「`!_allowActivation && !_expanded`」：收起成桌面小组件时保持不可激活（这是产品设计），展开后应允许正常激活（返回 `MA_ACTIVATE` 或干脆不拦）。`WS_EX_NOACTIVATE` 同理按 `_expanded` 切换，而不是只按输入态。若确实要求展开态也不抢焦点，至少提供一条显式的键盘入口（例如展开时按 `Tab` 触发 `SetInputActivation(true)`），并在文档里写明「面板仅支持鼠标操作」。

---

### [P3] `RaiseAboveNormalWindows()` 忽略两次 `SetWindowPos` 的返回值，第二次失败会让面板永久 topmost

- **证据**：`src/TuckPane/Services/DesktopLayerService.cs:354-361`
  ```csharp
  const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
      NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW;
  _ = NativeMethods.SetWindowPos(_window, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags);
  _ = NativeMethods.SetWindowPos(_window, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
  ```

- **影响**：这是「先抬到 topmost 再降回来」的经典技巧，用来跨过前台窗口的 z-order 边界。它要求两次调用**都成功**；第二次失败（`SetWindowPos` 返回 false，常见于目标窗口被销毁、跨会话、或 UIPI 限制）时窗口会**停留在 `HWND_TOPMOST` 状态**，也就是变成一个真正的置顶窗口：盖住任务栏、盖住全屏视频、`Alt+Tab` 切换后依然在最上面。而 `RaiseAboveNormalWindows` 在每次展开（`SetExpanded` 的 `:101`）都会调用一次，一旦某次踩中，后续所有 `Reattach` 都只会改 owner 不会取消 topmost（`Reattach` 的收起路径 `:44-52` 只在 `_stayTopmost` 时用 `HWND_TOPMOST`，否则用 `HWND_TOP`——但 `HWND_TOP` 不会清除 topmost 标记），用户只能重启应用。

- **建议**：检查第一次调用的返回值，失败就直接放弃（不要进入第二次）；检查第二次，失败时补一次 `HWND_NOTOPMOST` 并在 `DescribeAttachment()` 里带出 `WS_EX_TOPMOST` 状态便于诊断。更稳的做法是不要用 topmost 往返，而是 `SetWindowPos(HWND_TOP)` + `SetForegroundWindow` 之外的手段，或在展开期间临时置 topmost、收起时无条件 `HWND_NOTOPMOST`（幂等，不依赖配对成功）。

---

### [P3] ctor 里同步向 `Progman` 发未文档化的 `0x052C` 并等 1000ms，每个收纳窗都要阻塞一次 UI 线程

- **证据**：`src/TuckPane/Services/DesktopLayerService.cs:32`（ctor 末尾）→ `:56-59`（`FindDesktopIconView()` 未传参，默认 `allowWorkerSpawn: true`）→ `:434`
  ```csharp
  _ = NativeMethods.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
  ```
  `src/TuckPane/Services/DesktopLayerService.cs:24-33`（ctor 全程同步执行）

- **影响**：`0x052C` 是让 Explorer 生成用于壁纸/图标层 `WorkerW` 的未文档化消息，属于社区逆向出来的做法（`FindDesktopIconView` 只在 `EnumWindows` 找不到 `SHELLDLL_DefView` 时才发）。问题在于：
  1. **在 UI 线程同步调用**：`SendMessageTimeout` 会阻塞直到 Explorer 处理完或 1000ms 超时。Explorer 繁忙（正在加载缩略图、刚重启、机械盘）时每次构造 `DesktopLayerService` 都可能卡满 1 秒，而这段代码在 `MainWindow` 构造路径里（`:533` / `:594` / `MainWindow.Dock.cs:51`），表现为「新建/打开收纳窗时界面卡一下」。
  2. **每个收纳窗都发一次**：`0x052C` 的效果是全局的（对整个桌面），不需要每个窗口重复发。开 10 个收纳窗就发 10 次，最坏 10 秒累计阻塞。
  3. **副作用不可控**：该消息会让 Explorer 重组桌面窗口层级，在第三方壁纸引擎共存时可能触发一次壁纸重绘/闪烁。
  4. 注释与实现不一致：ctor 并不需要「确保 WorkerW 存在」——它只需要图标视图句柄，而 `MainWindow.InitializeHostWindow` 已经在 `:491` 先调用过一次 `FindDesktopIconView()`（默认也允许 spawn），ctor 里这次是重复的。

- **建议**：ctor 里改成 `FindDesktopIconView(allowWorkerSpawn: false)`（只查已有宿主，不发消息），把 spawn 收敛到一个显式的、全局只做一次的初始化步骤（静态 `EnsureDesktopHostOnce()`，加 `Interlocked` 保证只发一次，并且放到后台线程用 `SendMessageTimeout` 或干脆 `PostMessage` 不等结果）。`MainWindow.InitializeHostWindow` 与 `DesktopLayerService` ctor 之间也应该共享一次查找结果，别各查一遍。

---

### [P3] `healthChecked` 在可达路径上恒为真；`Ok` 分支每 tick 还额外 `Reattach()` 一次

- **证据**：`src/TuckPane/MainWindow.xaml.cs:7006-7020`
  ```csharp
  bool healthChecked = _definition.PlacementMode is
      OrganizerPlacementMode.Floating or OrganizerPlacementMode.Positioned;
  DesktopAttachmentState attachment = healthChecked
      ? desktopLayer.RepairDesktopAttachment()
      : DesktopAttachmentState.Ok;
  ...
  else
  {
      desktopLayer.Reattach();
  }
  ```

- **影响**：`Station` 分支在 `:6973-6974` 已经 `return`，能走到 `:7008` 的只可能是 `Floating` 或 `Positioned`，所以 `healthChecked` **恒为 true**，三目运算的另一半是死代码。这个变量的存在暗示作者以为这里有第三种模式需要跳过检查，实际上没有——将来若新增 `PlacementMode`（例如 `Docked`），会静默地把它归到「不检查」而不是「检查」，属于易错写法。
  更实际的问题是 `else` 分支：状态为 `Ok` 时仍然调用 `Reattach()`，而 `Reattach` 内部无条件执行一次 `SetWindowPos`（`DesktopLayerService.cs:73-80`）。于是每个修复周期（省电档 8s、高性能档 2s）都有一次完全多余的窗口重排调用，叠加 `DesktopVisibilityTimer` 的 1Hz 探测，稳态下每秒至少一次 `SetWindowPos`。对 DWM 来说这是一次 z-order 变更请求，会让窗口参与合成重算。

- **建议**：删掉 `healthChecked`，直接 `DesktopAttachmentState attachment = desktopLayer.RepairDesktopAttachment();`。`Ok` 分支不要调 `Reattach()`——`RepairDesktopAttachment` 返回 `Ok` 的含义就是「什么都没做且不需要做」；如果确实需要周期性重新对齐（比如宿主位置变了），应该在 `RepairDesktopAttachment` 内部用「上次已知 rect 是否变化」来决定，而不是无条件重排。

---

### [P3] 桌面图标被隐藏时根本没有 `SHELLDLL_DefView`，owner 会静默回退到控制台窗口且探测永远返回 `Ok`

- **证据**：`src/TuckPane/MainWindow.xaml.cs:491-493`
  ```csharp
  IntPtr desktopIconView = DesktopLayerService.FindDesktopIconView();
  IntPtr initialOwner = desktopIconView != IntPtr.Zero ? desktopIconView : _host.Console.Hwnd;
  _ = NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, initialOwner);
  ```
  `src/TuckPane/Services/DesktopLayerService.cs:219-222`
  ```csharp
  if (_desktopIconView == IntPtr.Zero)
  {
      return DesktopAttachmentState.Ok;
  }
  ```

- **影响**：用户关闭「显示桌面图标」后，Explorer 不再创建 `SHELLDLL_DefView`（或它不在任何顶层窗口下），`FindDesktopIconView` 返回 `IntPtr.Zero`，于是 owner 被设成 `_host.Console.Hwnd`——**控制台/管理器窗口**。后果：
  1. 面板成了管理器窗口的 owned window：管理器最小化时面板跟着消失，管理器关闭时面板被销毁，z-order 也跟随管理器而不是桌面。
  2. `ProbeDesktopAttachment` 因为 `_desktopIconView == IntPtr.Zero` **直接返回 `Ok`**，两个定时器都不会尝试修复，`RepairDesktopAttachment` 也永远不会把 owner 纠正回来。用户看到的是「面板行为诡异但日志一切正常」。
  3. 即使后来用户重新打开桌面图标，`_desktopIconView` 只有在 `Reattach`（`:56-59`）或 `ProbeDesktopAttachment`（`:214-216`）里才会重查；而 `ProbeDesktopAttachment` 在 `:219-222` 就 return 了，只有 `Reattach` 会重查——偏偏 `Reattach` 的重查结果只用于 owner 比较，且此时 `ownerChanged` 为真（owner 是控制台 ≠ 图标视图），会真的把 owner 改回桌面，这一点是好的。但 `Hidden`/`BehindDesktop` 的判定在此之前就被短路了。
  4. `DesktopTargetSnapshot.Capture()`（`ShellDragService.cs:504-512`）依赖同一个缓存，图标隐藏时返回空快照，拖放目标判定随之退化。

- **建议**：`ProbeDesktopAttachment` 在 `_desktopIconView == IntPtr.Zero` 时不应返回 `Ok`，而应返回一个新的 `Unknown`/`NoHost` 状态，让调用方决定「重试查找」还是「放弃并记一条诊断」；`FindDesktopIconView` 找不到时不要静默用 `_host.Console.Hwnd` 兜底，宁可不设 owner（保持 `IntPtr.Zero`，面板作为普通顶层窗口），也不要挂到语义完全不同的窗口上。同时订阅 `TaskbarCreated` / `WM_SETTINGCHANGE("ShellState")` 来感知「显示桌面图标」开关变化并重查。

---

