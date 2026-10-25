# TuckPane 4.2.4 全量审查报告（终版）

- **审查对象**：`https://github.com/daha1216/TuckPane`，tag `v4.2.4`，commit `7f58788`
- **对照基线**：上游 `https://github.com/ch998244353/TuckPane`（merge-base `fc3328e`）
- **本机安装**：`D:\TuckPane`，4.2.3（本次审查期间未升级）
- **报告性质**：静态代码审查 + 只读环境核验，**未编译、未运行、未动态调试**
- **合并来源**：本报告合并了两轮独立审查——
  - 第一轮（Lead）：`审查报告-4.2.4.md`（637 行，编号 F1–F19 / N1–N15）
  - 第二轮（独立 agent，盲审 + 交叉核对）：`independent/独立审查.md`（512 行，编号 A/C/D/E/F/G/H）
  - 终版统一采用域编号，编号映射见 §13

---

## §0 结论摘要

### 0.1 一句话结论

**可以继续用。没有后门、没有遥测、没有提权面、没有凭据窃取。** 但有一个风险从 4.2.2 起就存在且到 4.2.4 仍未改变：**应用内更新源指向朋友的个人账号，且安装包没有数字签名**——这是你唯一需要真正权衡的东西，其余都是"能用但不够好"。

### 0.2 4.2.4 这个版本本身是干净的

从 4.2.3 到 4.2.4 共 11 个文件 +43/−24，但**真正改行为的只有 2 个文件**：

| 文件 | 改动 | 性质 |
|---|---|---|
| `src/TuckPane/Services/DesktopLayerService.cs` | +14/−3 | PR #1 原样合入（锚点语义 + 关闭停表） |
| `src/TuckPane/MainWindow.xaml.cs` | +1 | PR #1 原样合入（`_desktopVisibilityTimer.Stop()`） |
| `src/TuckPane/app.manifest` | 1 行 | 版本 4.2.1.0 → 4.2.4.0（首轮 F13） |
| `src/TuckPane.Updates/ReleaseClient.cs` | 1 行 | UserAgent 4.2.0 → 4.2.4（首轮 F14 的一半） |
| 其余 7 个 | 版本号/文档/`.iss`/`.csproj`/`build-release.ps1` | 无行为影响 |

PR #1 的合并质量核实过：`mergedAt=2026-10-25T03:07:35Z`，`mergeCommit=dd501534`，`git diff` 我分支与合并后 main 的这两个文件**输出为空**——一字未改，没有夹带。合并提交里附了完整验证记录（Release 构建 0 警告 0 错误、LogicChecks 八个区域全 PASS、UpdateChecks 4/4 PASS、真机 z-order 与暗度实测）。这个纪律值得肯定。

### 0.3 但首轮 19 项只修了 4 项

| 状态 | 数量 | 编号 |
|---|---|---|
| ✅ 已修 | 2 | F3（计时器未停）、F13（manifest 版本） |
| 🟡 修了但不彻底 | 2 | F1/F2（→ 遗留为 A4）、F14（UserAgent 已改，`PLAN.md` 仍 4.2.1 → H3） |
| ❌ 未修 | 14 | F4、F5、F6、F7、F8、F9、F10、F11、F12、F15、F16、F18、F19 |
| — 判定非问题 | 1 | F17 |

**修复率 4/18。** 其中 F6（安装器元数据仍指上游）是唯一"用户装完直接可见"的——见 C2。

### 0.4 本轮共 77 条：P2 × 15，P3 × 62（另 2 条结论性事实不计入）

分布：

| 域 | 内容 | P2 | P3 | 小计 |
|---|---|---|---|---|
| A | 桌面层 Win32 互操作 | 5 | 10 | 15 |
| C | 更新链与供应链 | 3 | 7 | 10 |
| D | 持久化与数据安全 | 0 | 6 | 6 |
| E | 安全攻击面 | 0 | 4 | 4 |
| F | 并发与生命周期 | 1 | 5 | 6 |
| G | 性能与测试工程化 | 2 | 2 | 4 |
| H | 架构与文档 | 1 | 2 | 3 |
| I | 总控台生命周期与设置持久化 | 0 | 5 | 5 |
| J | 进程级共享（二开特有） | 1 | 0 | 1 |
| K | 文件操作执行层（存储与拖放） | 1 | 7 | 8 |
| L | 图标缓存与便签/待办窗口 | 0 | 5 | 5 |
| M | 图标 P/Invoke 与启动序列 | 1 | 5 | 6 |
| O | 更新链执行层 | 0 | 4 | 4 |
| **合计** | | **15** | **62** | **77** |

> 第三轮（`findings/round3.md`）补入 A12、F6 两条 P2；第四轮（`findings/round4.md`）补入 C8、C9 两条 P3；第五轮（`findings/round5.md`）补入 A13、A14 两条 P3；第六轮（`findings/round6.md`）补入 A15、C10 两条 P3；第七轮（`findings/round7.md`）补入 I1–I5 五条 P3 与 J1 一条 P2；第八轮（`findings/round8.md`）补入 K1 一条 P2 与 K2–K8 七条 P3；第九轮（`findings/round9.md`）补入 L1–L5 五条 P3；第十轮（`findings/round10.md`）补入 M3 一条 P2 与 M1/M2/M4/M5/M6 五条 P3。十轮累计对八十余个此前未系统检查的模块给出正面结论，不计入缺陷数。

### 0.5 两份报告的交叉结论

第二轮独立审查（盲审，未看我的报告）找到 **9 条我漏掉的**：A3、C4、D2、D3、F1、F2、F3、G1、G2。其中 **G1 是本轮唯一新增的 P2**（图标内存缓存无淘汰）。

同时它**纠正了我 6 条**：D1（我写反了备份自愈方向）、A8（我的修法对恒为 NULL 的句柄是空操作）、D6（我以偏概全，主路径其实有提示）、A10（我编造的"假阳性"分支不成立）、A2（我把概率估高了）、以及一条我完全写错的"跨卷退化"（`.tmp` 与目标同目录，永不跨卷，已删）。

**这 6 处已在终版全部改写。** 我自己的错误集中在"机制推演过头、修法没回头验证前提"，这是本轮最该记的教训。

### 0.6 真正该着急的不是桌面层那几条

| 指标 | 数值 |
|---|---|
| `MainWindow` 分部文件 | 18 个，合计 **10355 行** |
| `MainWindow.xaml.cs` 单文件 | **7997 行** |
| `MainWindow` 字段 / 方法（近似） | **~280 / ~379** |
| `AppHost.cs` | 2606 行 |
| `MainWindow` 反向调用 `_host.` | **142 处** |
| `async void` | **86 处** |
| 即发即忘 | **32 处** |
| 二开新增代码 | +472 行，**零伴随测试** |

桌面层那 11 条里有 4 条（A1、A4、A9、A11）根子是同一个：**自愈逻辑长在 `MainWindow` 的交互标志丛里，守卫集已经开始漂移**（H1）。单条修完还会长新的。

---

## §1 审查范围与方法

### 1.1 范围

| 范围 | 覆盖 |
|---|---|
| 源码 | `src/` 全量 164 个 `.cs` / 34536 行；全仓 214 个 `.cs` / 47988 行 |
| 测试 | `tests/` 44 文件 / 12985 行（3 个 C# 检查程序 + 8 个 `.ps1` + `note-*.cjs`） |
| 构建与发布 | `.github/workflows/release.yml`、`scripts/build-release.ps1`、`installer/TuckPane.iss` |
| 依赖 | `packages.lock.json`、`THIRD-PARTY-NOTICES.md`、`global.json` |
| 二进制 | 4.2.3 发布包（逐文件 SHA256 比对）、4.2.4 发布物元数据 |
| 本机环境 | `D:\TuckPane`、`%LOCALAPPDATA%\TuckPane`、`%USERPROFILE%\TuckPane` |

### 1.2 方法

1. 三路 diff：`upstream/main..fork`（二开全量）、`v4.2.3..v4.2.4`（本轮增量）、`upstream` 内部历史。
2. 静态通读重点文件全文（`DesktopLayerService` 448 行、`ReleaseClient` 120 行、`AppUpdateService` 199 行、`PortablePackage` 158 行、`Updater/Program` 88 行、`StateStore` 483 行等）。
3. 交叉核对：对每条主张回到源码取行号与代码块，逐条标注"已核实 / 推演 / 存疑"。
4. 供应链核验：GitHub API 取 release/asset/tag/commit 元数据，下载发布包算 SHA256，与本机逐文件比对。
5. 两轮独立审查交叉验证（见 §13 映射表）。

### 1.3 局限（必须明确）

- **本机没有 .NET 10 SDK**（只有 .NET 8 运行时；`global.json` 要求 `10.0.400`）→ **无法编译、无法运行他们自带的三个检查程序、无法复现任何运行时行为**。
- 所有 Win32 行为结论（z-order、owner、cloaked、WorkerW）属**代码推演 + 文档语义**，未做动态验证。凡属推演的条目在正文中显式标注。
- 无性能实测、无内存实测、无多显示器/高 DPI 实测。
- 未执行安装/卸载/更新端到端流程。

---

## §2 供应链与二进制核验（已完成的硬事实）

这一节全部是**实测结果**，不是推演。

### 2.1 你装的二进制与发布包逐字节一致

| 核验项 | 结果 |
|---|---|
| 下载 `TuckPane-4.2.3-win-x64-portable.zip` | 129930210 B，SHA256 `7164b7da…b9de` |
| 与 GitHub 资产 `digest` 比对 | **一致** |
| 与 `SHA256SUMS.txt` 内容比对 | **一致** |
| ZIP 内 547 个文件 vs `D:\TuckPane` 逐文件 SHA256 | **547/547 全部 MATCH** |
| 本机多出 3 个文件 | `unins000.exe`、`unins000.dat`（Inno Setup 卸载器）、`mscordaccore_amd64_*.dll`（.NET 运行时自带） |
| 结论 | **你装的版本 = 朋友 CI 构建并发布的那个包，没有被中间人替换，也没有夹带私货** |

### 2.2 发布物确实是 CI 从 tag 构建的

| 核验项 | 结果 |
|---|---|
| release 作者 | `github-actions[bot]` |
| 三个资产 uploader | 全部 `github-actions[bot]`，同一时间戳 `2026-10-20T18:07:26Z` |
| tag `v4.2.3` → commit | 注释标签 `3fbdf8a5…` → commit `008779cb…`（= 仓库 HEAD） |
| 对应 CI run | `39471526178`，success，3m29s |
| 结论 | **不是手工上传，是 tag 触发的 CI 构建产物** |

### 2.3 二开改动确实编译进了你装的二进制

用 UTF-16LE 在二进制里搜字符串（ASCII 搜会 0 命中）：

| 文件 | `daha1216/TuckPane` | `ch998244353` |
|---|---|---|
| `TuckPane.Updates.dll`（39936 B） | ✅ 3 处，含完整 `https://api.github.com/repos/daha1216/TuckPane/releases/latest` | — |
| `TuckPane.Updater.exe`（73575790 B） | ✅ 命中 | — |
| `TuckPane.exe`（356352 B） | ❌ | ✅ 2 处（版本资源） |
| `TuckPane.dll`（1597952 B） | ❌ | ✅ 2 处（版本资源） |

**含义**：你机器上的程序，其"检查更新"确实会去查 `daha1216/TuckPane`，不会去查上游。这和 C1/E2 直接相关。

### 2.4 签名状态

| 文件 | Authenticode |
|---|---|
| `D:\TuckPane\TuckPane.exe` | **NotSigned** |
| `D:\TuckPane\00-启动 TuckPane.exe` | **NotSigned** |

上游也是未签名，所以这不是二开引入的；但它是 C1 的前提。

### 2.5 本机更新检查实际是失败的

`%LOCALAPPDATA%\TuckPane\updates\check.json`：

- `LastAttemptUtc = 2026-10-24T16:01:35.8652893+00:00`（本地 10-25 00:01:35，应用启动后 6 分钟）
- `ReleaseJson` 仍是**上游** `ch998244353/TuckPane` 的 release（id `409758829`，tag `v4.2.1`）

说明那次检查请求**失败**了（`api.github.com` 在本机不稳定），而失败路径只更新时间戳、保留旧 JSON（见 C5 的关联分析）。**这不是二开的 bug，但说明"自动更新"这条链路在你的网络环境下本来就不可靠。**

---

## §3 域 A：桌面层与 Shell 互操作（15 条：P2 × 5 / P3 × 10）

这是二开投入最多、也最容易出问题的域。二开在这块加了 `DesktopAttachmentState` 状态机、`RepairDesktopAttachment()`、`ProbeDesktopAttachment()`、`IsBehindDesktopLayer()`、`DesktopInsertAnchor()` 和一条 1 秒定时器。

### A1 [P2] 两个修复定时器职责重叠，守卫集不一致

**位置**：
- `src/TuckPane/MainWindow.xaml.cs:6959`（修复定时器守卫，外层包裹）
- `src/TuckPane/MainWindow.xaml.cs:7027-7029`（可见性定时器守卫）
- `src/TuckPane/MainWindow.xaml.cs:6125-6127`（两者启停条件）

```csharp
// 修复定时器（上游已有，周期 2–8 秒）
if (!_expanded && !_animating && !_pressActive) { ... RepairDesktopAttachment() / Reattach() ... }

// 可见性定时器（二开 7121444 新增，周期 1 秒）
if (_closing || _expanded || _animating || _pressActive || IsContained || !_runtimeVisible) return;
if (_definition.PlacementMode is not (Floating or Positioned)) return;
if (_desktopLayer is null || _desktopLayer.IsInputActivationEnabled) return;      // ← 门 1
if (_widgetDragging || _itemReorderSession is not null || _shellDragActive ||
    _shellContextMenuOpen || _overlayOpenCount > 0) return;                        // ← 门 2
```

**机制**：两个定时器都周期性执行"探测 + 重排 z-order"，但守卫集差 6 项。修复定时器**缺** `IsInputActivationEnabled`、`_shellContextMenuOpen`、`_widgetDragging`、`_shellDragActive`、`_overlayOpenCount`。

对抗链（代码确证）：

1. 用户右键点开收纳窗上下文菜单 → `MainWindow.xaml.cs:6297-6300` 的 `_contextMenuActivated` 分支 → `_desktopLayer?.SetInputActivation(true)`；
2. `DesktopLayerService.cs:294-317` 的 `SetInputActivation(true)` 执行 `SetWindowLongPtr(_window, GWLP_HWNDPARENT, _expandedOwner)`（**`_expandedOwner` 恒为 `IntPtr.Zero`**）+ `SetWindowPos(_window, HWND_TOP, …)` + `SetForegroundWindow`；
3. 2–8 秒内修复定时器触发 → `ownerChanged` 为真（`Zero != iconView`）→ `SetWindowLongPtr` 把 owner **抢回** `_desktopIconView`，同时 `SetWindowPos` 重新定位；
4. 结果：菜单被压回桌面层、或窗口失焦导致内联标题编辑丢字。

省电档（8000 ms）下这个窗口有整整 8 秒，而右键菜单停留 8 秒对桌面工具完全正常。

**另一处不对称**：`RefreshPerformanceSettings` 的 `interactionDragActive`（`:6096`）只含 `_pressActive || _widgetDragging || _itemReorderSession || _shellDragActive`，不含 `_shellContextMenuOpen` → **拖拽/菜单期间两个定时器的启停也不同步**。

**归属**：修复定时器是上游的；可见性定时器是二开 `7121444` 新增 → **不对称由二开引入**（新代码加了门，没回头给老代码补）。

**修法**：把两条定时器合并成一个（见 `架构优化建议书.md` §4 第 7 项），守卫只写一份，物理上不可能再漂移。若暂不合并，把门 1、门 2 原样复制到修复定时器入口。

**置信度**：守卫缺失 = 代码确证；可见症状 = 推演。

---

### A2 [P3] 桌面图标视图静态缓存只用 `IsWindow` 校验，防不住 HWND 复用

**位置**：`src/TuckPane/Services/DesktopLayerService.cs:18`、`:441-445`

```csharp
private static IntPtr _lastKnownDesktopIconView;
internal static IntPtr GetCachedDesktopIconView()
{
    IntPtr cached = Volatile.Read(ref _lastKnownDesktopIconView);
    return cached != IntPtr.Zero && NativeMethods.IsWindow(cached) ? cached : IntPtr.Zero;
}
```

**机制**：HWND 是会被复用的整数。Explorer 重启期间窗口创建密集，旧 `SHELLDLL_DefView` 句柄可能在重新查找之前被别的窗口复用——`IsWindow` 返回真，类名却已不是 `SHELLDLL_DefView`。后果：

- `ProbeDesktopAttachment()`（`:214-227`）判定 `OwnerLost` → `RepairDesktopAttachment()`（`:136`）把面板 owner 设成那个**无关窗口**。`SetWindowLongPtr(GWLP_HWNDPARENT, …)` 跨进程合法，面板会变成某个随机窗口的 owned window（跟随它最小化、被它裁剪、它销毁时面板一起销毁）。
- 同一份缓存被 `ShellDragService.cs:504-512` 用来定位桌面 `SysListView32` 做拖放命中。
- 缓存是 `static`，**一次污染全体中招**。

**归属**：上游已有，二开未引入也未修复。

**定级说明**：第一轮定为 P2，独立审查指出需要"Explorer 重启 + 句柄复用 + `IsWindow` 恰好通过"三重低概率叠加，**接受降为 P3**。

**修法**：缓存带身份 —— 记录 `(IntPtr view, uint pid)`，校验 `IsWindow(view) && GetClassName(view) == "SHELLDLL_DefView"`。并订阅 `RegisterWindowMessage("TaskbarCreated")` 主动清空缓存。`FindDesktopIconView` 内部已有类名校验（`:447-455` 的 `IsDesktopHostClassName`），提取成 `IsValidDesktopIconView(IntPtr)` 复用即可。

---

### A3 [P3] `0x052C` 唤醒 WorkerW 后的后备查找只查 Progman 直接子级

**位置**：`src/TuckPane/Services/DesktopLayerService.cs:430-437`

```csharp
if (!allowWorkerSpawn) return IntPtr.Zero;

IntPtr progman = NativeMethods.FindWindow("Progman", null);
if (progman != IntPtr.Zero)
{
    _ = NativeMethods.SendMessageTimeout(progman, SpawnWorkerMessage, UIntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
    found = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);   // ← 只查直接子级
}
if (found != IntPtr.Zero) Volatile.Write(ref _lastKnownDesktopIconView, found);
return found;
```

**机制**：`0x052C` 的作用正是让 Explorer 把 `SHELLDLL_DefView` 从 `Progman` 搬到**顶层 `WorkerW`**。发完消息后只做 `FindWindowEx(progman, IntPtr.Zero, …)`，**没有重跑 `EnumWindows` 枚举**——而 `FindDesktopIconView` 的主路径（`:414-428`）本来就在做这件事。

结果：一旦 DefView 已被搬到 WorkerW 下，这条后备路径必然返回 `Zero`。虽然外层还会走主路径，但这条 fallback 本身是死代码。

**归属**：上游已有。

**修法**：`SendMessageTimeout` 之后 `return FindDesktopIconView(allowWorkerSpawn: false);` 递归一次，或直接重跑枚举。

**置信度**：已核验（代码确证）。

---

### A4 [P2] `IsSafeAnchor()` 把 `HWND_TOP` 一并否掉，最该修的场景退化成空操作

> **这是我自己 PR #1 引入的缺陷。** 4.2.4 已原样合入，所以它现在是线上代码。

**位置**：
- `src/TuckPane/Services/DesktopLayerService.cs:186-191`（`DesktopInsertAnchor()`）
- `src/TuckPane/Services/DesktopLayerService.cs:173-174`（`IsSafeAnchor()`）
- `src/TuckPane/Services/NativeMethods.cs:168`：`internal static readonly IntPtr HWND_TOP = IntPtr.Zero;`

```csharp
// DesktopInsertAnchor()
IntPtr above = NativeMethods.GetWindow(root, NativeMethods.GW_HWNDPREV);
return above == IntPtr.Zero ? NativeMethods.HWND_TOP : above;

// IsSafeAnchor()
private bool IsSafeAnchor(IntPtr anchor) =>
    anchor != IntPtr.Zero && anchor != NativeMethods.HWND_BOTTOM && anchor != _window;
```

**机制**：`HWND_TOP` 的值**就是** `IntPtr.Zero`。当桌面宿主恰好位于 z-order 最顶端时，`GetWindow(root, GW_HWNDPREV)` 返回 `NULL`，`DesktopInsertAnchor()` 返回 `HWND_TOP`（语义："放到最顶"），而 `IsSafeAnchor` 把它和"未知锚点"混为一谈一并拒绝 → 调用方加 `SWP_NOZORDER` → **z-order 一个字节都没动**。

也就是说：4.2.3 在这个场景会主动把窗口埋到底部（更糟），4.2.4 改成"什么都不做"——**不再更糟，但也没变好**。

**为什么真机测试没暴露**：桌面上通常还有其他顶层窗口（任务栏、其他 topmost 窗口），`GW_HWNDPREV` 一般非空，锚点是真实句柄，修复正常工作。朋友的真机验证（`z=32 vs Progman 33`）落在正常路径上，所以通过了。**这是"测试通过 ≠ 逻辑正确"的典型案例。**

**修法**：把"未知"和"置顶"分开表达，不要让 `IntPtr.Zero` 承担两种语义：

```csharp
private bool TryDesktopInsertAnchor(out IntPtr anchor)
{
    anchor = IntPtr.Zero;
    IntPtr root = DesktopHostRoot();
    if (root == IntPtr.Zero) return false;                              // 未知 → 调用方加 SWP_NOZORDER
    IntPtr above = NativeMethods.GetWindow(root, NativeMethods.GW_HWNDPREV);
    anchor = above == IntPtr.Zero ? NativeMethods.HWND_TOP : above;     // 置顶是合法锚点
    return true;
}

private bool IsSafeAnchor(IntPtr anchor) =>
    anchor != NativeMethods.HWND_BOTTOM && anchor != _window &&
    (anchor == NativeMethods.HWND_TOP || NativeMethods.IsWindow(anchor));
```

顺带：`GetWindow` 与 `SetWindowPos` 之间应加 `IsWindow(above)` 校验，避免锚点窗口在这一瞬间被销毁。

---

### A5 [P3] `DwmGetWindowAttribute` 失败被当成"未 cloaked"

**位置**：`src/TuckPane/Services/DesktopLayerService.cs:205-209`

```csharp
if (NativeMethods.DwmGetWindowAttribute(_window, NativeMethods.DWMWA_CLOAKED,
                                        out int cloaked, sizeof(int)) == 0 && cloaked != 0)
{
    return DesktopAttachmentState.Ok;
}
```

**机制**：这是"调用失败 → 条件不成立 → 继续往下走"的写法。`DwmGetWindowAttribute` 失败时（DWM 未运行、窗口正被销毁、句柄瞬时无效），代码当作"未 cloaked"继续，随后可能因为 `IsWindowVisible` 为假或 z-order 判定而落到 `Hidden` / `BehindDesktop`，触发一次不必要的 hide/show 重建。

**正确写法**：HRESULT 非 0 时应视为"状态未知"，直接返回 `Ok`（保守，不动），而不是继续推演。

**归属**：二开 `918dadb` 引入。

**修法**：

```csharp
int hr = NativeMethods.DwmGetWindowAttribute(...);
if (hr != 0) return DesktopAttachmentState.Ok;      // 状态未知 → 不动
if (cloaked != 0) return DesktopAttachmentState.Ok; // 已 cloaked，不归我们管
```

---

### A6 [P3] `Hidden` 分支的 hide/show 往返可能被消息泵合并

**位置**：`src/TuckPane/Services/DesktopLayerService.cs:141-154`

```csharp
if (state == DesktopAttachmentState.Hidden)
{
    // A window hidden by the shell misses WM_SHOWWINDOW, so WinUI keeps its last frame.
    // Force a hide/show round trip so the frame is rebuilt before it is shown again.
    _ = NativeMethods.SetWindowPos(_window, IntPtr.Zero, 0, 0, 0, 0,
        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
    ReassertDesktopAttachment();     // 内部 SWP_SHOWWINDOW
}
```

**机制**：两次 `SetWindowPos` 之间没有消息泵让出（同一次 Tick 内同步执行）。Windows 对同一窗口的连续 `ShowWindow`/`SetWindowPos` 有合并优化——如果 DWM/窗口管理器只看到最终状态（可见），中间那次 `SWP_HIDEWINDOW` 可能根本不产生 `WM_SHOWWINDOW`/帧重建，**"强制重建帧"的意图落空**。

**置信度**：推演，未动态验证。要证实需在两次调用之间插桩观察 `WM_SHOWWINDOW` 是否到达。

**修法**：两次调用之间让出一次消息泵：

```csharp
_ = NativeMethods.SetWindowPos(_window, IntPtr.Zero, 0, 0, 0, 0,
    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
_window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
    ReassertDesktopAttachment);
```

---

### A7 [P3] `RepairDesktopAttachment()` 返回的是修复**前**的状态，从不复探

**位置**：`src/TuckPane/Services/DesktopLayerService.cs:126-156`

```csharp
DesktopAttachmentState state = ProbeDesktopAttachment();
if (state == DesktopAttachmentState.Ok) return DesktopAttachmentState.Ok;
... 修复动作 ...
return state;                    // ← 修复前的状态，修复后没有复探
```

**影响**：
- 调用方 `MainWindow.xaml.cs:7009-7013` 看到 `state != Ok` 就记 `"desktop repair pass fixed layer state=…"` —— **日志说 "fixed"，但实际是否修好完全不知道**。如果修复无效（例如 A4 导致的空操作），日志会每 2–8 秒重复同一条 "fixed"。
- 可见性定时器 `:7032-7039` 同理，每 1 秒记一条。
- 这让"自愈"在诊断层面变成黑盒：无法区分"修好了"和"修不动"。

**修法**：修复动作后复探一次并返回修复后状态；或返回 `(修复前, 修复后)` 二元组，日志据此分级。

---

### A8 [P2] 展开态完全不自愈（4.2.4 RELEASE_NOTES 自己列的已知问题 #1）

**位置**（三处早退，不是一处）：
- `src/TuckPane/Services/DesktopLayerService.cs:201-206`：`if (_expanded || _window == IntPtr.Zero || !NativeMethods.IsWindow(_window)) return Ok;`
- `src/TuckPane/MainWindow.xaml.cs:7027`：可见性定时器在 `_expanded` 时直接 return
- `src/TuckPane/MainWindow.xaml.cs:6959`：修复定时器整段逻辑被 `if (!_expanded && …)` 包住

**关键补充证据**：`_expandedOwner` **恒为 `IntPtr.Zero`** —— 三处构造全部传 `Zero`：

| 构造点 | 代码 |
|---|---|
| `src/TuckPane/MainWindow.xaml.cs:533` | `new DesktopLayerService(_hwnd, IntPtr.Zero)` |
| `src/TuckPane/MainWindow.xaml.cs:594` | 同上 |
| `src/TuckPane/MainWindow.Dock.cs:51` | 同上 |

即展开态面板是一个 **owner 为 NULL 的普通顶层窗口**，只靠 `DesktopLayerService.cs:44-52` 的 `SetWindowPos(_window, _stayTopmost ? HWND_TOPMOST : HWND_TOP, …)` 和 `RaiseAboveNormalWindows()`（`:354-361` 的 topmost→notopmost 抖动）抢一次 z-order。Win+D 时 shell 把桌面宿主抬到最前，这一次抢占被撤销，**此后没有任何定时器能把它救回来**，只能等用户悬停/点击/收起再展开。

**这是设计上的显式排除，不是遗漏**：`ProbeDesktopAttachment()` 在 `_expanded` 时无条件返回 `Ok`（探针根本不检查），`DesktopVisibilityTimer_Tick` 在 `_expanded` 时连探针都不调。两处是同一套假设——"展开态是浮层，由别的机制保序"。

**修法（已改写，第一轮写错了）**：

第一轮我写的是"检查 `_expandedOwner` 是否仍可见""对 `_expandedOwner` 做 `SetWindowPos`"——**`_expandedOwner` 恒为 NULL，这些都是空操作**。正确方向：

1. 新增 `RepairExpandedZOrder()`：`SetWindowPos(_window, HWND_TOPMOST, …SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)` → 短暂延迟 → `SetWindowPos(_window, HWND_NOTOPMOST, …)`（即复用 `RaiseAboveNormalWindows()` 的抖动手法）；
2. **仅在 `IsBehindDesktopLayer()` 为真时触发**，避免每次 Tick 都抖；
3. `ProbeDesktopAttachment()` 的 `_expanded` 分支改为调用 `IsBehindDesktopLayer()` 判断，而不是直接返回 `Ok`；
4. `DesktopVisibilityTimer_Tick` 的 `_expanded` 早退去掉（只探测 + 必要时抖一次，不打断动画）。

---

### A9 [P2] z-order 修复被"owner 是否变化"门控，owner 没变时位置也不修

**位置**：`src/TuckPane/Services/DesktopLayerService.cs:61-71`

```csharp
bool ownerChanged = _desktopIconView != IntPtr.Zero &&
    NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT) != _desktopIconView;
if (ownerChanged)
{
    _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT, _desktopIconView);
}

uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
    NativeMethods.SWP_NOOWNERZORDER;
IntPtr anchor = DesktopInsertAnchor();
if (!ownerChanged || !IsSafeAnchor(anchor)) flags |= NativeMethods.SWP_NOZORDER;   // ← 这里
```

**机制**：把"owner 是否需要改"当成了"z-order 是否需要改"的代理条件。这两件事在 Win32 里是**正交**的：owner 决定窗口跟随谁、被谁裁剪；z-order 决定窗口叠在谁上面。owner 正确**不意味着** z-order 正确。

而 `Reattach()` 是修复链上的主力（`DesktopRepairTimer_Tick` 在探测结果为 `Ok` 时每 2–8 秒调一次，见 `MainWindow.xaml.cs:7019`），owner 一旦设好就再也不变，于是**之后所有 `Reattach()` 调用都退化成"只 `SWP_SHOWWINDOW`、z-order 一字节不动"**。

**触发场景**（代码推演，未真机复现）：
1. **启动首帧**：owner 在 `MainWindow.xaml.cs:491-493` 先设好，构造 `DesktopLayerService` 时 `ownerChanged == false` → 首次 `Reattach()` 不定位 z-order → 窗口停留在创建时的默认层级（普通窗口带顶端）→ **压在普通应用窗口之上**。
2. **交互后**：`SetInputActivation(true)` 把窗口抬到 `HWND_TOP`，`RaiseAboveNormalWindows()` 做 topmost 抖动，都把它留在"非 topmost 带的最顶端"，后续 `Reattach()` 因为 owner 没变不会把它降回桌面层。

**归属**：`!ownerChanged` 子句**在 4.2.3（上游）就已存在**。但二开 `a28199a`"keep desktop organizers above the desktop host"正是改这段 z-order 逻辑，属于二开明确声称负责的范围。

**修法**：拆开两个判断（见 A4 的 `TryDesktopInsertAnchor`）：

```csharp
if (ownerChanged) { _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT, _desktopIconView); }

uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
if (TryDesktopInsertAnchor(out IntPtr anchor)) { /* 用 anchor 定位 */ }
else flags |= SWP_NOZORDER;                        // 只有"锚点未知"才不动 z-order
```

若担心每次都重排 z-order 造成窗口管理器抖动，正确做法是**缓存上次的 z-order 判定结果**，而不是用 owner 变化当代理。

---

### A10 [P3] `IsBehindDesktopLayer()` 用单一句柄当地板线，多 `WorkerW` 下会漏判

**位置**：`src/TuckPane/Services/DesktopLayerService.cs:237-253`

```csharp
IntPtr desktopRoot = NativeMethods.GetAncestor(iconView, NativeMethods.GA_ROOT);
if (desktopRoot == IntPtr.Zero) desktopRoot = iconView;

for (IntPtr walk = NativeMethods.GetTopWindow(IntPtr.Zero); walk != IntPtr.Zero;
     walk = NativeMethods.GetWindow(walk, NativeMethods.GW_HWNDNEXT))
{
    if (walk == _window) return false;
    if (walk == desktopRoot) return true;
}
```

**机制**：Win10 1903+ 的桌面由 `Progman` + **至少一个独立的顶层 `WorkerW`** 共同构成（`0x052C` 之后 `SHELLDLL_DefView` 会被搬进顶层 `WorkerW`，壁纸由另一个 `WorkerW` 绘制，第三方壁纸引擎还会再加一个全屏窗口）。只把"`DefView` 的 `GA_ROOT`"当唯一地板线：

**假阴性（该修不修）**：面板在 `DefView` 宿主之上、但被更靠上的桌面类窗口（壁纸淡入 `WorkerW`、壁纸引擎全屏窗口）盖住时，遍历先遇到 `_window` → 返回 `false` → 判 `Ok` → 不修。**这恰好就是"被壁纸盖住"的视觉现象，而代码认为正常。**

> **第一轮我在这里写错了**：我声称还存在"假阳性（反复修）"分支——面板位于 `Progman` 与 `DefView` 宿主 `WorkerW` 之间时被判 `BehindDesktop`。独立审查指出这**不成立**：owned 窗口被系统强制保持在 owner 之上，遍历会先遇到 `_window` 就返回 `false`。**该分支已删除。**
>
> 但反过来看，第一轮观察到的"每 1–8 秒重复一次无效 `SetWindowPos`"现象**是真的**——病因不是假阳性，而是 **A4/A9 让修复退化成 `SWP_NOZORDER` 空操作，窗口一直埋在下面，探针一直判 `BehindDesktop`，于是每轮都重试一次**。**症状真、病因错**，两条串起来才完整。

**修法**：把"地板线"从单一句柄换成一组（`EnumWindows` + 已有的 `IsDesktopHostClassName` `:447-455`，取 z-order 最靠上的那个）；更稳的做法是**改用点采样**：`WindowFromPoint(面板中心) == _window` 就说明没被盖住——一次调用、不依赖 z-order 结构、天然覆盖多 `WorkerW`，成本从 O(z-order) 降到 O(1)。判定不了时返回 `Unknown` 而不是在 `Ok`/`BehindDesktop` 里二选一。

---

### A11 [P3] 新增的 1 秒定时器硬编码，绕过性能档位

**位置**：`src/TuckPane/MainWindow.xaml.cs:342-345`

```csharp
_desktopVisibilityTimer = DispatcherQueue.CreateTimer();
_desktopVisibilityTimer.Interval = TimeSpan.FromMilliseconds(1000);   // ← 硬编码
_desktopVisibilityTimer.IsRepeating = true;
_desktopVisibilityTimer.Tick += DesktopVisibilityTimer_Tick;
```

对照 `src/TuckPane/Models/AppState.cs:207-212`：

```csharp
PowerSaver       => new(100, 8000, false),
HighPerformance  => new(25,  2000, true),
_                => new(50,  4000, true),      // Balanced
```

`DesktopRepairMilliseconds` 在省电档是 8000 ms，新定时器却是 1000 ms —— **比省电档快 8 倍，且用户无法调**。同时它与修复定时器职责重叠（A1），等于把桌面层探测频率整体抬高了一个量级。

**归属**：二开 `7121444` 新增。

**修法**：给 `PerformanceTuning` 加一个 `DesktopVisibilityMilliseconds` 字段（建议 `PowerSaver 8000 / Balanced 2000 / HighPerformance 1000`），或干脆与 `_desktopRepairTimer` 合并成一个定时器（推荐）。

### A12 [P2] 资源管理器重启后托盘图标不重建

**位置**：`src/TuckPane/Services/TrayIconService.cs:43`（`NIM_ADD` 唯一调用点）、`:49-54`（`ApplyLanguage` 只发 `NIM_MODIFY`）

```csharp
// 构造函数内，全仓仅此一次
bool iconAdded = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _data);
_data.uTimeoutOrVersion = NativeMethods.NOTICON_VERSION_4;
bool versionSet = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref _data);
```

**机制**：`Shell_NotifyIcon(NIM_ADD)` 注册的图标属于**当前 explorer.exe 实例**。资源管理器崩溃或被重启后任务栏重建，所有注册丢失。Windows 的标准做法是用 `RegisterWindowMessage("TaskbarCreated")` 取得广播消息，收到后重新 `NIM_ADD`。

**实测**：`grep -rn "TaskbarCreated\|RegisterWindowMessage" src/ --include=*.cs` **零命中**；`NIM_ADD` 全仓也只有 `TrayIconService.cs:43` 一处。

**后果**：资源管理器重启后托盘图标**永久消失**，只能重启 TuckPane 恢复。用户失去托盘菜单、显示/隐藏收纳窗、退出入口。`ApplyLanguage()` 走 `NIM_MODIFY`，图标已不存在时该调用静默失败（返回值被 `_ =` 丢弃），不会自愈。

**为什么算缺陷而不是取舍**：同一个项目对"资源管理器重启"在桌面层是有恢复逻辑的 —— `DesktopLayerService` 在 `_desktopIconView` 失效时会 `FindDesktopIconView()` 重新枚举（`src/TuckPane/Services/DesktopLayerService.cs:60-64`）。托盘侧缺了对称的那份。

**归属**：上游遗留，非二开引入。

**修法**（约 15 行）：构造函数里 `_taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");`，在已有的 `WindowProc` 子类化回调（`TrayIconService.cs:71` 起）里加一个分支：

```csharp
if (message == _taskbarCreated)
{
    _ = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _data);
    _ = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref _data);
    return IntPtr.Zero;
}
```

---

### A13 [P3] 过渡在版本号自增之后早退，`_animating` 与悬停放大都不会复位

**位置**：`src/TuckPane/MainWindow.xaml.cs:1665-1835`（`ExpandTransitionAsync`）、`:1837-1930`（`CollapseTransitionAsync`）

两个方法都是同一个骨架：

```csharp
long requestVersion = ++_transitionRequestVersion;   // 先自增，并取消上一轮
_transitionCancellation?.Cancel();
bool IsCurrent() => IsCurrentTransitionRequest(requestVersion, modeCancellation);
... 若干 await ...
CancellationTokenSource transition = StartTransition(modeCancellation);  // 这里才 ResetHoverWave()
_expanded = true;
_animating = true;
try { ... }
finally
{
    if (IsCurrent() && _transitionCancellation == transition && !transition.IsCancellationRequested)
    {
        _animating = false;      // 只有"仍为当前请求"才复位
        ...
    }
}
```

**问题**：版本号自增之后、`StartTransition` 之前有 8 个早退点（Expand 6 个：`:1678`、`:1691`、`:1710` 后、`:1745`、`:1798`、`:1810` 的 `catch (OperationCanceledException)`；Collapse 2 个：`:1847`、`:1905`）。任一命中时：

1. 被取代的那一轮过渡，`finally` 因 `IsCurrent() == false` 跳过 `_animating = false`；
2. 新的一轮过渡并没有建立（在 `StartTransition` 之前就返回了）；
3. → **`_animating` 停在 `true`**，`_transitionProgress` 停在中间值。

**后果**：

- `IsExpanded => _expanded || _animating`（`:7145`）会向宿主谎报"已展开"，`AppHost.PrepareToExpandAsync` 会把它当成对等窗口去收拢（`AppHost.cs:1614-1626`）。
- 外部点击收起要求 `_expanded && !_animating`（`:7183`）→ 点空白处收不起来，只能点标题栏或热键。
- 条目重排、拖拽、模式悬停、画布缩放全部被 `_animating` 挡住（`:2348`/`:2375`/`:2441`/`:2985`/`:2996`/`:3756`/`:4166`/`:6047`/`:6179`/`:6209`/`:6959`/`:7027`）。
- `StartTransition` 第一句是 `ResetHoverWave()`（`:1976`）；早退路径不经过它 → 悬停放大不复位。**这很可能就是 4.2.4 RELEASE_NOTES「已知问题 ②：收起动画被打断时卡片上方残留放大的名字胶囊」的成因**，不是独立的渲染 bug。

**可达性**：不是永久卡死 —— `SetRuntimeVisible(false)`（`:7406`）、`SetContained(true)`（`:7481`）、`ShowCompactPlacement`（`:7699`）、`Restore`（`MainWindow.ExpansionMode.cs:108`）都会显式清 `_animating`。真实触发窗口是 `await _host.PrepareToExpandAsync(...)` / `await _host.CollapseContainedChildrenAsync(...)` 这两段（`AppHost.cs:1608-1648`），它们内部会 `await` **其它窗口**的 `CollapseForPeerAsync()`，耗时可达数十到数百毫秒；期间用户再点别的收纳窗就会取代本窗口的请求。

**修法**（约 15 行）：所有早退路径统一走 `AbandonTransition(requestVersion)`（`if (requestVersion != _transitionRequestVersion) return;` 后清 `_animating`、按 `_expanded` 复位 `_transitionProgress`、`ResetHoverWave()`、`NotifyDockOpenStateChanged()`、`ApplyOutsideClickSetting()`）；再给 `_animating` 加 3 秒看门狗兜底。

---

### A14 [P3] DPI / 显示器拓扑变化只处理 Dock，其余收纳窗不重算尺寸与位置

**位置**：`src/TuckPane/MainWindow.xaml.cs:3106-3110`

全仓处理 `WM_DPICHANGED`(0x02E0) / `WM_DISPLAYCHANGE`(0x007E) 的只有这一处：

```csharp
if (message is 0x02E0 or 0x007E) ResetHoverWave();
if (IsDock && message is 0x02E0 or 0x007E)
    DispatcherQueue.TryEnqueue(() => { if (!_closing) { _dockRegionKey = null; ApplyDockLayout(); } });
```

- `grep -rn "DisplaySettingsChanged\|SystemEvents\|WM_SETTINGCHANGE" src/` → **零命中**，宿主级没有监听。
- `WM_DPICHANGED` 的 `lParam`（系统建议的新窗口矩形）**完全没被读取**。
- `_compactBounds` 是物理像素；`ApplyCompactScale()` 只在 `:546`、`:654`、`:7534`、`:7644`、`:7651` 调用，都不在 DPI 路径上。

**后果**：

1. 把收纳窗从 100% 显示器拖到 150% 显示器：只 `ResetHoverWave()`，窗口外框仍是原物理像素尺寸，而 WinUI 内容按新缩放比重排 → 紧凑卡片/展开面板的视觉尺寸与外框不匹配，直到下一次过渡重新 `ApplyBounds`。
2. 拔掉副屏：副屏上的 Floating/Positioned 收纳窗保留绝对坐标，`_compactBounds` 不更新 → 碰撞检测、拖放目标判定、`CalculateExpandedBounds(_compactBounds)` 全部基于过期矩形。
3. `DisplayPlacementService.GetDisplay` 的"找不到回退主屏"只在启动加载时生效，运行期没有等价路径。

**修法**（约 20 行）：`WM_DPICHANGED` 分支里 `ApplyCompactScale(repositionWindow: !_expanded && !_animating)` + 重新 `ApplyBounds`；宿主挂 `SystemEvents.DisplaySettingsChanged`（`Dispose` 里退订），对全部窗口做一次 `ClampToVisibleArea()`。

---

### A15 [P3] 中转站的显示器信息只解析一次，拔屏后热区永久错位

**位置**：`src/TuckPane/MainWindow.xaml.cs:6196`（`StationPointerTimer_Tick` 的收起态分支）

```csharp
DisplayInfo display = _stationDisplay ??= DisplayPlacementService.GetDisplay(_definition.Position?.MonitorDevice);
```

`??=` 只在 `_stationDisplay` 为 null 时解析。而它在 `InitializeCoreAsync` 的 Station 分支（`:527`）就已经被赋值，此后只有 `:3495`、`:6964` 和 `MoveToStationPlacement`（`:7655-7669`）会重写。**用户不主动拖动或切换中转站，显示器拓扑变化就不会刷新这个缓存。**

后果链：

1. `IsStationHotZone(cursor, display, _definition.DockEdge, StationActivationDistanceDip)` 用 `display.Work` 判定光标是否进入边缘热区；
2. 显示器拔掉后 `display.Work` 指向一块不存在的屏幕区域，光标永远落不进去 → **中转站再也无法悬停展开**；
3. 显示器换位或改缩放后，热区落在错误位置 → 表现为“边缘悬停不灵”或“在错误的地方弹出”；
4. 唯一恢复手段是拖动中转站或重启应用，用户无从得知。

**对照**：Dock 走的是另一条路径——`src/TuckPane/MainWindow.xaml.cs:3109` 在 `WM_DISPLAYCHANGE`（`0x007E`）里对 `IsDock` 窗口执行 `_dockRegionKey = null; ApplyDockLayout();`。中转站没有对称处理。这与 A14 同源（都源于“拓扑变化只处理 Dock”），但 A14 讲窗口尺寸/坐标、这条讲热区判定数据，代码路径独立，所以单列。

**修法（推荐 1 行版）**：在 `GestureWindowProc` 的 `0x007E`/`0x02E0` 分支里补一句

```csharp
if (!IsDock && _definition.PlacementMode == OrganizerPlacementMode.Station) _stationDisplay = null;
```

下次 tick 时 `??=` 会自然重解析，与 Dock 的 `_dockRegionKey = null` 完全对称。

**归属**：上游遗留（Station 模式早于二开）。

---

## §4 域 C：更新链与供应链（10 条：P2 × 3 / P3 × 7）

二开在这块把更新源从 `ch998244353/TuckPane` 改成了 `daha1216/TuckPane`（提交 `32781b8`），代码本身写得很扎实——`ReleaseClient.Parse` 强制校验下载 URL 必须等于 `https://github.com/{Repository}/releases/download/{tag}/{asset}`，`PortablePackage` 拒绝 `..`/盘符/DOS 设备名/符号链接条目/未清单条目，`Updater` 校验父进程身份与二次哈希。**问题不在实现，在信任模型和元数据。**

### C1 [P2] 哈希校验防不住发布端被篡改，全链无代码签名

**位置**：`src/TuckPane.Updates/ReleaseClient.cs:79-119`、`src/TuckPane.Updater/Program.cs:50-58`

**机制**：整条链的完整性校验是 SHA256，但**哈希与安装包同源**：

1. `ReleaseClient.DownloadAsync` 先下载 `SHA256SUMS.txt`，再下载包，交叉校验 GitHub 的 `digest` 字段与 sums 文件；
2. 三者（`digest`、sums、包）**全部来自同一个 release**；
3. 校验通过后，`Updater` 直接 `Process.Start(setup.exe, "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART …")` 静默执行。

**这意味着**：哈希只能防"传输损坏"和"半截篡改"，**防不住"整个 release 被替换"**。任何能操作 `daha1216/TuckPane` 账号的人（账号被盗、令牌泄露、恶意协作者），或者能劫持 GitHub API/下载域的人，都可以发布一个哈希自洽的恶意包，客户端会安静地装上去。而两个 exe 都是 `NotSigned`（§2.4），Windows 层面也没有第二道锚。

**归属**：上游就未签名；二开 `32781b8` 把信任根从原作者账号转移到朋友账号，**风险主体变了，但用户未必意识到**。

**修法**（三选一，按代价排序）：
1. **最省事**：在 README 与更新提示里明确写"本 fork 的更新包未签名，信任基于 GitHub 账号安全"，让用户知情（当前 README 只写了 "installer is currently unsigned, SmartScreen may warn"，没说"更新链信任根是个人账号"）；
2. **中等**：自签名证书 + 把公钥指纹硬编码进 `ReleaseClient`，`Updater` 执行前用 `WinVerifyTrust` 校验（自签名证书不需要 CA，只需固定指纹）；
3. **最彻底**：买代码签名证书（OV/EV），走 SmartScreen 信誉。

---

### C2 [P2] fork 保留上游 `AppId` 与发布者身份，两条渠道可互相就地覆盖

**位置**：`installer/TuckPane.iss:19`、`:24-26`、`:48`

```ini
AppId={{2B7D4C50-0148-4D5C-A097-D8D7E5C64FCB}     ; ← 与上游完全相同
AppPublisher=ch998244353                           ; ← 仍指上游
AppPublisherURL=https://github.com/ch998244353/TuckPane
AppSupportURL=https://github.com/ch998244353/TuckPane/issues
VersionInfoCompany=ch998244353                     ; ← 仍是上游
```

**机制（两层）**：

1. **表层（元数据未改）**：用户装完在"应用和功能"里看到的是 `ch998244353`，控制面板的发布者是原作者。这是 4.2.4 **唯一用户装完直接可见**的问题，也是首轮 F6 的原文。4.2.4 只改了 `MyAppVersion`，其余未动。

2. **深层（独立审查多挖出来的）**：`AppId` 相同意味着**两条发布渠道在 Windows 眼里是同一个产品**。后果：
   - 用户可以从上游装 4.2.1、再从 fork 装 4.2.4，Inno Setup 会当成"就地升级"，**不会提示这是两个不同的项目**；
   - 反过来也一样。**更新源会在用户不知情的情况下被静默切换**——装了谁的包，后续自动更新就跟谁走；
   - 卸载时 `[Registry]` 段会去删上游的右键菜单键与 CLSID（`:84-89`），因为键名也是共用的。

**修法（需要先决策，不是纯代码改动）**：
- **不换 `AppId`**：老用户可平滑升级，但必须改 `AppPublisher`/`AppPublisherURL`/`AppSupportURL`/`VersionInfoCompany` 四项（6 行），并在 README 写明"本 fork 与上游共用安装标识，装过上游版本的用户会被视为就地升级"。
- **换 `AppId`**：等于新软件，老用户需要卸载重装，但从此两条渠道彻底隔离。**对"避免静默切换更新源"这个目标，换 `AppId` 才是根治。**

---

### C3 [P2] CI 只在打 tag 时触发，PR 与 main 分支零检查

**位置**：`.github/workflows/release.yml:3-6`

```yaml
on:
  push:
    tags: ["v*"]
```

**问题**：
- 没有 `pull_request` 触发器，没有 `push: branches: [main]`。
- 仓库里有 3 个 C# 检查项目（`TuckPane.LauncherChecks`、`TuckPane.LogicChecks`、`TuckPane.UpdateChecks`）+ 8 个 `.ps1` 手工脚本，**CI 一个都不跑**。
- 直接后果：PR #1 合并前没有任何自动化记录，全靠朋友手工在本地跑。这次他跑了并把结果写进提交信息（值得肯定），但**这是人的纪律，不是系统的保证**。
- 更隐蔽的后果：`build-release.ps1` 里的那些检查（`FileVersion == "$Version.0"`、必需文件清单、私有文件泄漏检查、`update-files.json` 生成、`check-portable-launcher.ps1`）**只有打 tag 时才执行**。一个破坏发布链的改动可以在 main 上活很久，直到某次发版才炸——而那时 tag 已经推了。

**修法**：加 `ci.yml`，成本几乎为零：

```yaml
on: { pull_request: , push: { branches: [main] } }
jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@<sha>
      - uses: actions/setup-dotnet@<sha>
        with: { dotnet-version: '10.0.400' }
      - run: dotnet build -c Release
      - run: dotnet run --project tests/TuckPane.LogicChecks -c Release -- --stability
      - run: dotnet run --project tests/TuckPane.UpdateChecks -c Release -- --updates
```

**如果整个项目只做一件事，就做这一件。** 它是后面所有改进的前提。

---

### C4 [P3] CI actions 与 Inno Setup 未钉版本，发布物不可复现

**位置**：`.github/workflows/release.yml`

- `actions/checkout@v4`、`actions/setup-dotnet@v5` —— 用 tag 而非 commit SHA，tag 可被上游仓库移动（供应链攻击的经典手法）；
- `choco install innosetup --yes --no-progress` —— 未钉版本，每次构建可能拿到不同的 ISCC。

**影响**：同一个 tag 重新构建可能得到不同的二进制，`SHA256SUMS.txt` 也就失去"可复现"意义。对个人项目不致命，但和 C1 叠加时，"我核对过哈希"这件事的保证强度会打折。

**修法**：actions 固定到 commit SHA（注释写版本号），choco 加 `--version=6.3.3`。

---

### C5 [P3] 更新失败直接把英文异常抛到 UI

**位置**：`src/TuckPane/Services/AppUpdateService.cs:60-88`

```csharp
catch (Exception ex)
{
    StatusKey = "UpdateCheckFailed";
    Error = ex is OperationCanceledException ? AppStrings.Get("UpdateTimeout") : ex.Message;   // ← 原始英文异常
}
```

**问题**：只有超时走了本地化字符串，其余全部直接显示 `ex.Message`。用户会看到 `"No such host is known."`、`"The SSL connection could not be established"`、`"Response status code does not indicate success: 403 (rate limit exceeded)"` 这类文本。中文界面里突然冒英文，且没有任何可操作建议（是网络问题？限流？还是包坏了？）。

**修法**：按异常类型映射到 `AppStrings` 条目，至少区分"网络不可达 / TLS 失败 / 限流 / 包校验失败 / 磁盘空间不足"五类，并附一句"可在设置中稍后重试"。

---

### C6 [P3] `updates/config-backups/` 只增不减，无清理逻辑

**位置**：`src/TuckPane.Updates/UpdateFiles.cs:40-52`

每次更新前都会把 `state.json`、`state.json.bak`、`backup.json` 复制到 `updates/config-backups/{yyyyMMdd-HHmmss}-{version}-{guid8}/`。本机已有 `20261013-121723-4.2.0-9f0863d1`。

**问题**：二开新增的 `CleanupSessions()`（`src/TuckPane/Services/AppUpdateService.cs:157-183`）只清理 `updates/sessions/*`，**不清理 `config-backups/*`**。单目录只有几十 KB，但"每次更新留一份"，长期使用会累积几十上百份。

**修法**：`CleanupSessions()` 里按同样策略处理 `config-backups`（保留最近 5 份 + 超过 90 天删除）。

---

### C7 [P3] 更新检查失败会静默把下次检查推后 24 小时

**位置**：`src/TuckPane/Services/AppUpdateService.cs:60-88`

```csharp
_cache = new(DateTimeOffset.UtcNow, _cache?.ReleaseJson);   // ← 先写时间戳
UpdateFiles.WriteJson(check.json, _cache);
string json = await new ReleaseClient(_http).GetLatestJsonAsync(timeout.Token);   // ← 后发请求
```

**机制**：时间戳在请求**之前**就落盘了。请求失败（超时、断网、限流）时只更新了时间戳、保留旧 JSON，而 `AutoCheckDue` 据此判断"24 小时内不用再查"。**在弱网环境下，一次失败就等于一天不检查**，且用户界面上不会提示"检查失败，已推迟"。

**本机实证**：`check.json` 的 `LastAttemptUtc = 2026-10-24T16:01:35Z`，而 `ReleaseJson` 仍是上游 `v4.2.1` 的旧数据——说明那次检查失败了，但时间戳照样前移。

**修法**：失败时不写时间戳（或写一个独立的 `LastSuccessUtc`），并让下次重试走短退避（例如 15 分钟 / 1 小时 / 4 小时）。

---

### C8 [P3] 安装器注册文件夹右键菜单时没有所有权守卫

**位置**：`installer/TuckPane.iss:111-138`（`RegisterFolderMenu`）

**机制**：这段安装脚本只检查用户的偏好开关，然后**无条件覆写**菜单键：

```pascal
if RegQueryDWordValue(HKCU, FolderMenuPreferenceKey, 'Enabled', Enabled) then
  if Enabled = 0 then Exit;
RegWriteStringValue(HKCU, FolderMenuKey, '', '使用 TuckPane 创建收纳窗');
RegWriteStringValue(HKCU, FolderMenuKey, 'OwnerExecutable', ExpandConstant('{app}\TuckPane.exe'));
```

没有 `RegKeyExists` 前置检查，也没有把已存在的 `OwnerExecutable` 与本次安装路径比对。

**对照**：同一文件的 `RegisterDesktopMenu`（`:181-214`）是**对的**——`:192-194` 先读 `OwnerExecutable` 并比对，`:195-196` 再用 `RegKeyExists` + `DesktopMenuOwnedByThisInstall` 双保险。应用侧的 `FolderContextMenuService` 也是**对的**：`:96-101` 判 `OtherCopy`、`:107`/`:113-114` 拒绝接管、`:127-130` 写 `OwnerExecutable`。

**后果**：用户同时有便携副本（例如 `D:\TuckPane`）时，跑一次 `setup.exe` 会**静默把文件夹右键菜单改成指向新装的副本**。便携副本打开设置页后显示 `OtherCopy` 并拒绝管理该菜单；更实际的影响是"新建收纳窗"落到另一个实例、另一个数据根，用户以为文件丢了。

**归属**：上游遗留（桌面菜单有守卫说明作者知道这个问题，文件夹侧漏了）。

**修法**：把 `RegisterDesktopMenu` 的三行守卫照搬到 `RegisterFolderMenu` 开头。

---

### C9 [P3] 更新前备份假定 `state.json` 必然存在，全新配置下更新直接失败

**位置**：`src/TuckPane.Updates/UpdateFiles.cs:40-55`、调用点 `src/TuckPane/Services/AppUpdateService.cs:109`

**机制**：

```csharp
Directory.CreateDirectory(destination);
File.Copy(statePath, Path.Combine(destination, "state.json"));   // ← 无 File.Exists 守卫
```

调用点无条件执行（`exitWithHandoff` 内，下载完成后的第一步）：

```csharp
BackupPath = UpdateFiles.BackupConfiguration(AppPaths.StatePath, Path.Combine(_root, "config-backups"), CurrentVersion);
```

**为什么 `state.json` 可能不存在**——这条链每一环都不落盘：

1. `src/TuckPane/Services/AppPaths.cs:32-41` `EnsureCreated()` 只 `Directory.CreateDirectory`，不建 `state.json`；
2. `src/TuckPane/Services/StateStore.cs:22-32` `LoadAsync` 对新用户用 `NewUserDefaults.CreateState()`，但**只在 `loaded is { RequiresMigration: true }` 时才 `PersistMigrationAsync`**——新用户 `loaded` 是 `null`，不落盘；
3. `src/TuckPane/Models/NewUserDefaults.cs:7-46` `CreateState()` **只返回 `GlobalSettings`，一个 organizer 都没有**；
4. `src/TuckPane/AppHost.cs:111-141` `InitializeAsync` 里唯一的启动期保存是 `if (NormalizePositionedPlacementsOnStartup() | NormalizeStationPlacementsOnStartup()) await SaveStateAsync();`——没有任何 organizer，两个 Normalize 都返回 false。

**后果**：全新安装、装完直接点"检查更新 → 下载 → 安装"的用户，在备份阶段抛 `FileNotFoundException`，UI 上是英文 `Could not find file 'C:\Users\<用户名>\AppData\Local\TuckPane\state.json'.`——**带完整用户路径**。更新彻底无法进行，直到用户先改任意一个设置触发一次保存。`state.json` 被手动删掉、或被杀软隔离时，同样命中。

**归属**：上游遗留（`BackupConfiguration` 与 `LoadAsync` 都是上游代码），二开未触及。

**修法**：

```csharp
bool statePresent = File.Exists(statePath);
if (statePresent) { File.Copy(statePath, ...); if (Hash(statePath) != Hash(dest)) throw ...; }
// backup.json 里记 statePresent: false，恢复时按"无配置可恢复"处理
```

或更简单：`LoadAsync` 里对 `newUser` 强制 `await SaveAsync(state)` 一次。

---

### C10 [P3] 文件夹菜单的 `%1` 遇驱动器根目录会吞掉收尾引号

**位置**：`src/TuckPane/Services/FolderContextMenuService.cs:48-50`

```csharp
internal static string BuildCommand(string executable, bool desktop = false) => desktop
    ? $"\"{Path.GetFullPath(executable)}\" --create-organizer"
    : $"\"{Path.GetFullPath(executable)}\" --create-organizer-in \"%1\"";
```

这段串被写进 `HKCU\Software\Classes\Directory\shell\...\command`（安装器 `installer/TuckPane.iss:111-138` 的 `RegisterFolderMenu` 也写同一形式）。Explorer 展开 `%1` 时不会去掉尾反斜杠：右键驱动器根，`%1` = `D:\`，拼出来是

```
"C:\Users\...\TuckPane.exe" --create-organizer-in "D:\"
```

按 `CommandLineToArgvW` 规则，`\` 后紧跟 `"` 表示**字面引号**，于是第三个参数变成 `D:"`（带一个引号字符）。`src/TuckPane/Core/FolderOrganizerCreation.cs:14-20` 的 `ParseArguments` 要求 `Path.IsPathFullyQualified(arguments[1])`，而 `Path.IsPathFullyQualified("D:\"")` 为 false → 抛 `FolderMenuInvalidArguments` → 用户看到“参数无效”，收纳窗建不出来。UNC 根（`\\server\share\`）同理。

**影响面**：仅“右键驱动器根 / UNC 根 → 使用 TuckPane 创建收纳窗”。普通文件夹（不以 `\` 结尾）不受影响，故定 P3。

**修法（应用侧 2 行，不动注册表）**：`ParseArguments` 里先剥一个尾引号再校验

```csharp
string candidate = arguments[1];
if (candidate.Length > 1 && candidate.EndsWith('"')) candidate = candidate[..^1];
```

**注意**：不要改 `BuildCommand` 的输出格式。`ReadCommandExecutable`（`:52-63`）是拿注册表里的命令串与 `BuildCommand(...)` 做**全等比较**来判定“这个菜单是不是本程序注册的”；一改格式，老版本注册的 `%1` 形式会被判成“别人的菜单”，卸载时 `UnregisterOwnedFolderMenu`（`installer/TuckPane.iss:140-156`）不会清理，留下指向已删除 exe 的死菜单。

**归属**：上游遗留。

---

## §5 域 D：持久化与数据安全（6 条：P3 × 6）

`state.json` 里装着用户全部收纳窗布局、主题、便签索引。这块的读写逻辑在 `src/TuckPane/Services/StateStore.cs`（483 行），迁移链覆盖 schema 3→16。

### D1 [P3] 全链无 fsync，断电窗口内可能丢最后一次保存

**位置**：`src/TuckPane/Services/StateStore.cs:36-52`

```csharp
string temporary = _statePath + ".tmp";
await File.WriteAllTextAsync(temporary, json);
if (File.Exists(_statePath)) File.Copy(_statePath, _backupPath, overwrite: true);
await MoveIntoPlaceAsync(temporary, _statePath);
```

**机制**：`File.WriteAllTextAsync` 写完即返回，**没有 `FlushToDisk`**。NTFS 上元数据与数据落盘顺序无保证，断电/蓝屏后可能出现 **0 字节或半截** `state.json`。

**第一轮我把这条写错了，已改写**：
- ❌ 原文："下一次保存会用半截 `.bak` 再覆盖一遍，坏文件被固化" —— **反了**。`:44` 是 `File.Copy(state.json → .bak)`，下次保存用**好的** `state.json` 覆盖坏的 `.bak`，备份会**自愈**。
- ❌ 原文："一次断电可能让程序以'全新用户'启动" —— **不成立**。单次故障会回退到 `.bak` 加载成功；`LoadAsync`（`:22-32`）的 `TryLoadAsync(_statePath) ?? TryLoadAsync(_backupPath)` 设计是对的。

**真正的风险链（比原文窄，但仍存在）**：只有**双重故障**才会落到空状态——`state.json` 坏 + 复制 `.bak` 的那一刻又崩。而这条路径**可达的原因**是 `:44` **无条件**把 `state.json` 复制进 `.bak`、**不校验 `state.json` 是否完好**：一个已损坏的 `state.json` 会在下次保存时把好备份冲掉。

**修法**：

```csharp
await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                                         bufferSize: 4096, FileOptions.WriteThrough))
{
    await JsonSerializer.SerializeAsync(stream, Normalize(state), JsonOptions);
    await stream.FlushAsync();
    stream.Flush(flushToDisk: true);
}
```

备份同样走"写临时文件 + `File.Move(overwrite: true)`"，不要用 `File.Copy`；且**复制前先确认当前 `state.json` 能反序列化成功**。

---

### D2 [P3] 跨卷移动的发布前校验只比对名称+长度，不校验内容哈希

**位置**：`src/TuckPane/Services/StorageService.cs:571-580`

`VerifyEquivalent` 比对的是 `TotalBytes` + 条目序列，而 `BuildManifest` 只记 `F|相对路径|长度` —— **没有内容哈希**。跨卷移动（`File.Move` 退化为复制+删除）后，如果复制过程出现静默损坏（位翻转、驱动 bug），长度一致就判定"成功"，源目录随后被删除。

**修法**：`BuildManifest` 改为 `F|相对路径|长度|SHA256`；大文件可只对前 64 KB + 后 64 KB 做哈希以控制成本。

---

### D3 [P3] 回滚到旧版本时，新 schema 字段会被静默丢弃

**位置**：`src/TuckPane/Services/StateStore.cs`（迁移链单向）、`src/TuckPane/Models/AppState.cs:116`（`SchemaVersion = 16`）

**机制**：迁移链只向上（3→16），`Normalize()` 写死 `state.SchemaVersion = 16`。旧版本（例如 4.2.0，schema 15）读取一个 schema 16 的 `state.json` 时，反序列化忽略未知字段，再按旧 schema 重写——**新字段全部丢失且无任何提示**。

**与 C2 联动**：因为共用 `AppId`，回滚路径是**现实可达的**（装回上游旧版即可）。用户不会意识到自己刚刚丢了配置。

**修法**：`LoadAsync` 读到 `SchemaVersion > 当前版本` 时，先备份一份 `state.json.v{future}` 再降级写入，并提示"配置来自更高版本，已备份"。

---

### D4 [P3] `result.json` 里的恢复路径是悬空引用

**位置**：`src/TuckPane/Services/AppUpdateService.cs:157-183`（`CleanupSessions`）

本机 `updates/result.json` 实测：

```json
{"Status":"launched","Version":"4.2.1",
 "Message":"Program files updated; awaiting the new application startup. Program recovery files: C:\\Users\\<用户名>\\AppData\\Local\\TuckPane\\updates\\sessions\\1d9fa093745a46b886b67be148780c3b\\program-backup",
 "BackupDirectory":"C:\\Users\\<用户名>\\AppData\\Local\\TuckPane\\updates\\config-backups\\20261013-121723-4.2.0-9f0863d1"}
```

而 `updates/sessions/` 现在是**空目录** —— `Message` 里指的那份 `program-backup` 已经被二开新增的 `CleanupSessions()` 回收了。

**问题**：UI 若把这个 `Message` 展示给用户（"恢复文件在 …"），用户去找会找不到。二开加自动清理时没同步调整这条消息的措辞。

**修法**：清理成功后把 `result.json` 的 `Message` 改写为"更新完成，临时文件已清理"，或干脆不再在消息里写具体路径。

---

### D5 [P3] `TuckPane.log` 是死文件，无轮转无清理

**位置**：`%LOCALAPPDATA%\TuckPane\TuckPane.log`

实测：177214 字节，**最后写入 2026-09-21 10:55**，内容只有 `DwmSetWindowAttribute(DWMWA_BORDER_COLOR) 完成，HRESULT=0x00000000。` 一类 INFO 行，`grep update` / `grep failed` 均 0 命中。

**问题**：这是一个已经不再写入的历史日志文件（`AppLogger.Info` 现在是空实现，见 E3），但它**留在磁盘上永不清理**。对比 `diagnostics/performance.*.jsonl` 有 2 MB 上限 + 轮转，这个文件完全没有生命周期管理。

**修法**：启动时若发现 `TuckPane.log` 且超过 N 天未写入，删除或迁移进 `diagnostics/`。

---

### D6 [P3] 部分保存路径失败时无 UI 提示

**位置**：`src/TuckPane/AppHost.Dock.cs:19-37`（回滚）、`src/TuckPane/MainWindow.xaml.cs:7128-7138`（有提示的路径）

**第一轮我把这条写成了"完全无提示"，已收窄**：

```csharp
// MainWindow.xaml.cs:7128-7138 —— 主保存路径是有提示的
catch (Exception ex)
{
    AppLogger.Error($"保存配置失败：…", ex);
    ShowMessage(AppStrings.Get("SaveConfigurationError"), InfoBarSeverity.Warning);   // ← 有
}
```

无提示的是 **`AppHost` 内部那些裸 `await SaveStateAsync()` 路径**（约 28 处），异常直接向上抛，最终落到 `AppDomain.UnhandledException` → 只写诊断文件。

**影响**：用户拖了位置、重启后又回去了，无从判断是磁盘满、权限问题还是程序 bug。这条路径覆盖的场景（Dock/停靠相关操作）恰好是用户最常做的。

**修法**：`SaveStateAsync` 失败时统一抛一个可识别的异常类型（如 `ConfigurationSaveException`），由 `AppHost` 集中弹一次非阻塞提示，并做频率限制避免刷屏。

---

## §6 域 E：安全攻击面（4 条 P3 + 2 条结论性事实）

### E1（结论，非缺陷）出网点只有 GitHub 更新链，无遥测

全仓 `grep https?://` 逐条核对：代码中真实网络地址只有两处，都在 `src/TuckPane.Updates/ReleaseClient.cs:14-15`、`:42`，指向 `daha1216/TuckPane`。其余 86 处 `http://` 全部是 XML 命名空间、XAML 命名空间、`licenses/*.txt` 里的许可文本链接。**没有统计、没有崩溃上报、没有第三方域名。**

`NoteWindow` 的 WebView2 也做了限制（`src/TuckPane/NoteWindow.xaml.cs:305-319`）：`AreDefaultContextMenusEnabled = false`、`AreDevToolsEnabled = false`、`NewWindowRequested → Handled = true`。

### E2（事实，非缺陷）更新信任根已从上游作者转移到 fork 作者

`src/TuckPane.Updates/ReleaseClient.cs:12-15`：

```csharp
// This fork ships its own builds; in-app updates must never resolve to upstream releases.
public const string Repository = "daha1216/TuckPane";
public const string LatestUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
```

这是**用户必须知情的事实**，不是缺陷——它是二开该做的事（否则 fork 会被上游包覆盖）。但它决定了"能不能放心用"的答案：**你信任的不再是原作者，而是朋友的 GitHub 账号安全。** 独立审查把它列为 P2，我认为定级串味（事实不该进缺陷表），在此单列为事实。

### E3 [P3] 高对比度订阅失败被降级为 `Info`，而 `Info` 是空实现

**位置**：`src/TuckPane/ConsoleWindow.xaml.cs:112-137`、`src/TuckPane/Services/AppLogger.cs:12`

```csharp
// AppLogger.cs:12 —— 空实现
public static void Info(string message, [CallerMemberName] string caller = "") { }
```

二开把高对比度事件订阅失败从 `AppLogger.Error` 降级为 `AppLogger.Info(...)` + `AppLogger.Performance(...)`。降级本身是对的（`ERROR_NOT_FOUND 0x80070490` 确实是环境差异，注释解释得很清楚），但 **`Info` 是空方法体，所以那条中文说明永远不会出现在任何地方**；只剩 `Performance` 那条，而它默认关闭（`TUCKPANE_PERF_TRACE=1` 才落盘）。

**结果**：这个"预期内的环境差异"在默认配置下**完全不可见**——既不进错误日志，也不进诊断包。真正需要排查辅助功能问题时，没有任何线索。

**修法**：`AppLogger.Info` 要么实现（写入一个有上限的 info 通道），要么这条改用 `Lifecycle("high-contrast-unavailable", …)` 走 `DiagnosticRecord`（那条通道是真实落盘的）。

### E4 [P3] `--create-note-in <path>` 无任何路径校验

**位置**：`src/TuckPane/App.xaml.cs:207-213`

```csharp
int noteFolderIndex = Array.FindIndex(values, v => v.Equals("--create-note-in", StringComparison.OrdinalIgnoreCase));
if (noteFolderIndex >= 0)
{
    string folderPath = …;                              // 直接取下一个参数
    await _host!.CreateExternalNoteAsync(folderPath);   // 无任何校验
    return;
}
```

**问题**：任意本地进程（无需管理员）都能让一个正在运行的 TuckPane 实例在**任意用户可写目录**创建一个便签文件。文件名由程序决定（不覆盖用户文件），内容为空文档。典型 confused deputy：攻击者借 TuckPane 的身份写文件。

实际危害有限（同权限下攻击者本可以自己写），但有两个真实场景：① 写入被安全软件监控的目录（`Startup`、`Desktop`）时，行为看起来像 TuckPane 在投放文件；② 配合 E6 可以把写入位置重定向到任意根。

**修法**：`CreateExternalNoteAsync` 入口加 `Path.GetFullPath` + 拒绝 UNC + 拒绝重解析点 + 白名单（只允许 `AppPaths` 已知收纳根下）。

### E5 [P3] `.tucknote` / `.tucktodo` 参数未拒绝 UNC 路径

**位置**：`src/TuckPane/App.xaml.cs:214-224`

```csharp
string[] notePaths = values
    .Where(v => v.EndsWith(".tucknote", StringComparison.OrdinalIgnoreCase) || …)
    .Select(Path.GetFullPath)            // ← 只做 GetFullPath
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
```

**问题**：只做了 `Path.GetFullPath`，没拒绝 `\\host\share\…`。传入 UNC 路径时 Windows 会发起 SMB 连接并**自动发送当前用户的 NTLM 哈希**——经典的"用文件路径泄露凭据"手法，只需用户双击一个特制文件，无需管理员权限。E4 同样存在。

**修法**：统一在参数解析层加 `IsLocalPath(string)`：`Path.IsPathFullyQualified` + `new Uri(path).IsUnc == false` + 拒绝 `\\?\` 前缀。

### E6 [P3] `TUCKPANE_TEST_ROOT` 在正式构建里仍然生效

**位置**：
- `src/TuckPane/Services/AppPaths.cs:9`：`private static readonly string? TestRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");`
- `:23`（`DesktopRoot`）、`:27`（`IsTestMode`）、`:214-219`（`UserProfile` / `LocalAppData`）
- `src/TuckPane/App.xaml.cs:71`、`:243`（单实例互斥体名加 testRoot 哈希后缀）
- `src/TuckPane/Services/AppUpdateService.cs:115`、`ValidationUpdateHandler.cs:14`

**问题**：上游为自动化测试引入，但**没有用 `#if DEBUG` 或条件编译隔离**，正式发布版同样生效。设置该变量后：数据根、桌面根、`UserProfile`、`LocalAppData` 全部重定向；单实例互斥体名改变 → 可以与真实实例**同时运行两个 TuckPane**。

危害仍限当前用户权限，但这是"正式版不该有的开关"。

**修法**：`#if DEBUG` 包起来，或用 MSBuild 条件属性在 Release 构建里编译掉。

---

## §7 域 F：并发与生命周期（6 条：P2 × 1 / P3 × 5）

### F1 [P3] 更新会话清理的异常过滤器过窄，且失败无任何日志

**位置**：`src/TuckPane/Services/AppUpdateService.cs:157-183`

```csharp
private void CleanupSessions()
{
    try
    {
        foreach (string candidate in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            try { … DeleteSessionEntry(candidate); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // 内层：逐目录
        }
        if (removed > 0) AppLogger.Performance($"update-sessions cleanup removed={removed}");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }           // 外层
}
```

**先肯定一点**：这里**有**逐目录的 try/catch，一个目录失败不会中断其余目录的清理——这个结构是对的，独立审查"静默中断清理"的措辞略重。

**真正的问题有两处**：

1. **过滤器只覆盖 `IOException` / `UnauthorizedAccessException`**。`NotSupportedException`（路径格式）、`SecurityException`、`ArgumentException` 等类型会**穿透两层**，从 `_ = Task.Run(CleanupSessions)` 的即发即忘里逃逸，最终只可能落到 `TaskScheduler.UnobservedTaskException`。实际概率不高（路径都由 `Path.Combine` 构造），但一旦发生就是完全无痕的失败。
2. **两条 catch 都是空的，没有 `AppLogger.Error`**。即使捕获了，用户和诊断包都看不到"清理失败了 3 个目录"。对比同文件里 `removed > 0` 时是记 `Performance` 的——**成功有痕迹、失败没有**，这个不对称会让"清理为什么没生效"变成无法排查的问题。

**修法**：内层 catch 保持窄（预期失败静默跳过合理），但外层改为 `catch (Exception ex) { AppLogger.Error("更新会话清理失败", ex); }`；并在内层累计一个 `failed` 计数，结束时若有失败也记一条。

> **注**：本条与独立审查的 F1 对应。我在起草终版时曾把 `DirectoryNotFoundException` 举为例外——**这是错的**，它继承自 `IOException`，会被捕获。已改正。


### F2 [P3] 正常退出不 flush 位置防抖计时器，350–450 ms 内退出会丢最近一次位置

**位置**：
- `src/TuckPane/MainWindow.xaml.cs:358-361`：`_interactionSaveTimer`，350 ms，一次性
- `src/TuckPane/ConsoleWindow.xaml.cs:147-150`：`_placementTimer`，450 ms，一次性
- `MainWindow_Closed`（`:7042-7118`）只 `Stop()`，`ExitCoreAsync` 不 flush

**机制**：用户拖完窗口立刻按 Alt+F4 或退出托盘，防抖计时器还没到点就被 `Stop()`，**最近一次位置变更丢失**。下次启动窗口回到旧位置。

**修法**：`MainWindow_Closed` 在 `Stop()` 之前检查 `IsRunning`，若为真则同步执行一次保存（或调用一个 `FlushPendingSaves()`）。

### F3 [P3] 二实例激活重定向用无超时 `Wait()`，理论上可永久挂起

**位置**：`src/TuckPane/App.xaml.cs:77-90`

```csharp
completed.Wait();     // ← :87，无超时
```

**机制**：新实例把激活请求投递给已运行实例后同步等待完成信号。若已运行实例卡死（死锁、被调试器挂起、UI 线程阻塞），新实例会**永久挂起且无窗口**——用户看到的是"双击图标没反应"。

**修法**：`completed.Wait(TimeSpan.FromSeconds(3))`，超时则直接退出并（可选）提示"已有实例未响应"。

### F4 [P3] 诊断代码位置白名单漏掉嵌套类型的短名形式

**位置**：`src/TuckPane/Services/DiagnosticRecord.cs:31-48`（`CodeTypes`）、`:87-98`（`SafeCodeLocation`）、`src/TuckPane/Services/AppLogger.cs:67-86`（`OriginFor`）

**机制**：`OriginFor` 在 `.ctor`/`.cctor` 场景用 `StackTrace` 取 `DeclaringType`，`SafeTypeName` 产出的是**只有类型名链、没有命名空间**的形式（例如 `AppLogger.Operation..ctor`）。而 `CodeTypes` 收集的是 `type.Name`（`Operation`）与 `type.FullName.Replace('+','.')`（`TuckPane.Services.AppLogger.Operation`）——**`AppLogger.Operation` 这个中间形式不在集合里**，于是该 origin 被丢弃成 `null`。

**影响**：二开专门为"构造函数失败记录代码位置"加的功能，在嵌套类型上失效。属于自伤，不影响用户。

**修法**：`CodeTypes` 额外加入 `type.FullName` 去掉命名空间后的部分，或让 `OriginFor` 输出 `FullName`。

### F5 [P3] `_itemTouchHoldTimer` 是唯一在关闭时未停止的定时器

**位置**：`src/TuckPane/MainWindow.xaml.cs:368-371`（创建）、`MainWindow_Closed`（未包含）

**核实过程**：本域早期子代理报告称"关闭只停 3/9 个定时器、6 个继续回调"，我逐项数了一遍，**这个数字是错的**：

| 定时器 | 创建点 | 关闭时是否停止 |
|---|---|---|
| `_longPressTimer` | `:326` | ✅ |
| `_externalHoverTimer` | `:331` | ✅ |
| `_desktopRepairTimer` | `:336` | ✅ |
| `_desktopVisibilityTimer` | `:342` | ✅（PR #1 补的） |
| `_stationPointerTimer` | `:347` | ✅ |
| `_watcherDebounceTimer` | `:353` | ✅ |
| `_interactionSaveTimer` | `:358` | ✅ |
| `_canvasResizeInputTimer` | `:363` | ✅ |
| `_itemTouchHoldTimer` | `:368` | ❌ **未停** |
| `_hoverPointerGuard` | `MainWindow.HoverWave.cs:88` | ✅ `DisposeHoverWave()` 里 `Tick -=` 并置 null（`:623-624`） |

即 **9/10 已处理，只有 1 个遗漏**。

**实际风险**：`IsRepeating = false`（一次性），只在长按交互中 `Start()`（`:4209-4210`），交互结束会在 `:4346`、`:5752` 停掉。只有"长按进行中恰好关窗"这一条窄路径会触发。

**修法**：补一行 `_itemTouchHoldTimer.Stop();`。顺便给全部 10 个定时器加统一 `Tick -=` 解绑（目前一个都没有），避免窗口对象被事件委托钉住而延迟回收。

### F6 [P2] 全局 UI 异常处理器无条件吞掉所有异常

**位置**：`src/TuckPane/App.xaml.cs:100-110`

```csharp
UnhandledException += (_, args) =>
{
    AppLogger.Error("Unhandled UI exception", args.Exception);
    AppLogger.Lifecycle("ui-exception", $"type={args.Exception.GetType().FullName} hresult=0x{args.Exception.HResult:X8} handled=true");
    // Keep recoverable async-void/UI callback failures from terminating
    // the process. Native fail-fast crashes are still handled by WER.
    args.Handled = true;
};
```

**机制**：`args.Handled = true` 是**无条件**的，对所有异常类型一视同仁。

**为什么是问题**：

1. 全仓 **86 处 `async void`**（`MainWindow.xaml.cs` 26、`ConsoleWindow.xaml.cs` 24、`NoteWindow` 8、`TodoWindow` 5 等）。这个处理器让它们不崩进程 —— 这是设计目的，没错。代价是**任何** UI 回调里的 `NullReferenceException`、`InvalidOperationException`、`ArgumentException` 都被静默记录后继续运行。
2. 应用会**带着可能已损坏的中间状态继续运行**。例如某个 `async void` 在"改了一半设置"时抛异常，后续逻辑基于半成品状态执行，用户看到的是行为异常而不是错误提示。
3. 用户永远看不到错误。`AppLogger.Error` 写的是 `diagnostics/runtime.*.jsonl`，普通用户不会去看。
4. 开发期同样掩盖真实缺陷 —— 这类"吞异常"代码让回归测试和手工测试都发现不了问题。
5. 次要问题：处理器内部调 `AppLogger.Error` → `DiagnosticRecord` 序列化 → 若此时日志写入器正在 `Dispose`（退出流程），二次异常会从这个处理器里逃出，`Handled` 已无意义，进程直接终止。

**归属**：上游遗留，非二开引入。

**修法**：改成白名单 —— 只对明确可恢复的异常类型（如已知的 COM 环境差异 `COMException`、`OperationCanceledException`）置 `Handled = true`，其余记录后不置位，让进程按正常路径崩溃并由 WER 收集。若坚持全吞，至少在置位时给用户一个可见提示（InfoBar / 托盘气泡），让用户知道"刚才那次操作失败了"。

---

## §8 域 G：性能与测试工程化（4 条：P2 × 2 / P3 × 2）

### G1 [P2] 图标**内存**缓存每窗口一份、无容量淘汰

**位置**：
- `src/TuckPane/Services/IconCacheService.cs:20`
- `src/TuckPane/MainWindow.xaml.cs:58`

```csharp
// IconCacheService.cs:20
private readonly Dictionary<string, (string Identity, BitmapImage Image)> _memoryCache =
    new(StringComparer.OrdinalIgnoreCase);

// MainWindow.xaml.cs:58 —— 每个窗口一个实例
private readonly IconCacheService _iconCache = new();
```

**机制**：全文件对该字典只有三处操作——`:107` `TryGetValue`、`:112` `Remove`、`:134` 索引赋值。**没有 `Count` 检查、没有 `Clear`、没有 LRU/Trim、没有总字节上限。**

`BitmapImage` 是解码后的位图对象，一个 256×256 的图标解码后约 256 KB（BGRA）。一个装满的收纳窗有几十上百个条目，乘以用户可能开着的多个收纳窗 → **长会话下内存单调增长，无上限**。

**对比**：二开这轮专门给**磁盘**缓存加了上限（`MaximumCachedIcons = 4096`、`MaximumCacheBytes = 64 MB`、`SweepAsync`），**内存侧完全没动**。这个不对称很典型——磁盘清理是"看得见的问题"，内存增长是"看不见的问题"。

**修法**：
1. 短期：`IconCacheService` 加 `MaximumMemoryEntries`（建议 512）与 `MaximumMemoryBytes`（建议 32 MB），插入时按 `Identity` 计数并淘汰最旧；
2. 更好：**把缓存实例从 `MainWindow` 提到 `AppHost` 单例**，所有窗口共享一份——同一个图标在不同收纳窗里重复解码纯属浪费；
3. 注意 `BitmapImage` 的 `DecodePixelWidth` 应按显示尺寸设置，避免为 32×32 的槽位解码 256×256。

### G2 [P3] 依赖锁定只覆盖主工程，其余 6 个工程依赖浮动

**位置**：`src/TuckPane/packages.lock.json` 是仓库里**唯一**一份。

`src/TuckPane.Launcher`、`src/TuckPane.Updater`、`src/TuckPane.Updates`、`tests/TuckPane.LauncherChecks`、`tests/TuckPane.LogicChecks`、`tests/TuckPane.UpdateChecks` 都没有锁文件，`build-release.ps1` 的 `dotnet restore --locked-mode` 只对主工程真正生效。

**影响**：`TuckPane.Updater.exe`（73 MB，是实际执行安装的那一个）依赖可以漂移，而它是整条更新链里权限最高的组件。

**修法**：给全部 7 个工程启用 `RestorePackagesWithLockFile`。

### G3 [P2] 二开新增 472 行代码，零伴随测试

**位置**：`tests/` 全目录

对二开新增的全部公开/内部符号做全仓 grep，`tests/` 下**零引用**：

| 新增符号 | 位置 | tests 引用数 |
|---|---|---|
| `SweepAsync` | `IconCacheService.cs` | 0 |
| `RepairDesktopAttachment` / `DesktopAttachmentState` | `DesktopLayerService.cs` | 0 |
| `AppLogger.Performance` / `OriginFor` | `AppLogger.cs` | 0 |
| `CleanupSessions` / `DeleteSessionEntry` | `AppUpdateService.cs` | 0 |
| `CodeTypes` / `MaximumCachedIcons` | `DiagnosticRecord.cs` / `IconCacheService.cs` | 0 |

**为什么这是 P2**：`DesktopLayerService` 的新逻辑**恰好是最难手工验证的那一类**（z-order、owner、cloaked、WorkerW 时序）。这一轮 A 域 11 条里有 4 条（A4、A5、A9、A10）都属于"跑起来看着正常、边界上错"——正是单元测试能覆盖而手工测试覆盖不到的。仓库里已经有 `LogicChecks` 这套 `Check(condition, message)` 的平铺断言框架（4946 行），加进去成本很低。

**注意**：`DesktopLayerService` 目前直接 `DllImport` 静态调用，不可注入 → 想测就必须先把 Win32 调用抽成接口。这本身也是架构建议书 §3 阶段 1 的内容。

### G4 [P3] `Performance()` 每次调用都同步做磁盘 IO，且每次都读环境变量

**位置**：`src/TuckPane/Services/AppLogger.cs:28-52`

```csharp
PerformanceTraceEnabled => string.Equals(Environment.GetEnvironmentVariable("TUCKPANE_PERF_TRACE"), "1", StringComparison.Ordinal);

public static void Performance(string message, [CallerMemberName] string caller = "")
{
    if (!PerformanceTraceEnabled) return;                    // ← 每次调用读一次环境变量
    ...
    lock (PerformanceGate)
    {
        Directory.CreateDirectory(diagnostics);               // ← 每次调用
        if (File.Exists(path) && new FileInfo(path).Length > MaximumPerformanceBytes) File.Move(...);
        File.AppendAllText(path, line + Environment.NewLine); // ← 同步 IO，每次调用
    }
}
```

**问题**（都只在 `TUCKPANE_PERF_TRACE=1` 时生效，所以优先级不高）：
1. 每次调用 `Environment.GetEnvironmentVariable` —— 会走一次进程环境块查找，22 个调用点若在高频路径上不划算；
2. `lock` + `Directory.CreateDirectory` + `File.Exists` + `FileInfo.Length` + `AppendAllText` 全在**调用线程**上同步执行。若某个调用点在 UI 线程（`DesktopVisibilityTimer_Tick` 每秒一次、`DesktopRepairTimer_Tick` 每 2–8 秒一次），就变成 UI 线程做同步磁盘 IO；
3. `File.Move` 轮转失败会被外层 `catch { }` 吞掉 → **2 MB 上限不是硬保证**，极端情况下文件会一直长。

**修法**：环境变量读一次缓存到 `static readonly`；落盘改成 `Channel<string>` + 后台写线程；轮转失败时至少记一次到 `runtime.0.jsonl`。

---

## §9 域 H：架构与可维护性（3 条：P2 × 1 / P3 × 2）

完整分析见 `架构优化建议书.md`（248 行，含四阶段重构路线）。这里只列两条进缺陷表。

### H1 [P2] 桌面层自愈逻辑长在 `MainWindow` 的交互标志丛里，守卫集已经开始漂移

**位置**：`src/TuckPane/MainWindow.xaml.cs` 的 `_desktopVisibilityTimer`（`:342`、`:6125-6127`、`:7024-7040`）与 `_desktopRepairTimer`（`:336`、`:6957-7021`）

**机制**：自愈的**状态判断**（何时该修、何时不该修）依赖 `MainWindow` 上的 12 个交互标志：`_closing`、`_expanded`、`_animating`、`_pressActive`、`IsContained`、`_runtimeVisible`、`_widgetDragging`、`_itemReorderSession`、`_shellDragActive`、`_shellContextMenuOpen`、`_overlayOpenCount`、`IsInputActivationEnabled`。

这套判断被**复制了两份**（两条定时器），且二开加新定时器时只给新的那份补了门，老的那份没补 → **A1 就是这么来的**。这不是一次性疏忽，而是结构决定的：只要判断逻辑住在 `MainWindow` 里，每加一条定时器就多一次漂移机会。

**证据**：A1、A4、A9、A11 四条 P2/P3 全部落在这块，根因同一个。

**修法**（架构建议书 §3 阶段 1，2–3 天）：
1. 新建 `DesktopAttachmentSupervisor`，持有两条定时器（或合并成一条），对外只暴露 `NotifyInteractionStarted(kind)` / `NotifyInteractionEnded(kind)`；
2. 把 12 个标志收敛成一个 `InteractionState` 枚举/位标志，由 `MainWindow` 在状态切换处单点更新；
3. 守卫判断只写在 supervisor 里，物理上不可能出现两份。

### H2 [P3] `AppHost` 与 `MainWindow` 双向耦合，服务经属性直取

**位置**：`src/TuckPane/AppHost.cs`（2606 行）、`src/TuckPane/MainWindow.xaml.cs`（`_host.` 出现 **142 处**）

`MainWindow` 通过 `_host` 直接访问 AppHost 上的服务属性（存储、图标缓存、拖放、Dock、控制台…），`AppHost` 又持有全部 `MainWindow` 并直接调它们的 `ClosePermanently()`、`BeginExpansion()` 等方法。**没有接口、没有事件总线、没有依赖注入。**

后果：`MainWindow` 无法脱离 `AppHost` 实例化 → 单测基本不可能；任何一处改动的影响面都要靠人肉追踪 142 个调用点。

**修法**：架构建议书 §3 阶段 3（3–5 天），把 `_host.` 收敛为 4–5 个窄接口（`IStorageHost`、`IWindowHost`、`IDragHost`…），`MainWindow` 只依赖接口。

### H3 [P3] 文档版本漂移：`PLAN.md` 仍停在 4.2.1

**位置**：`PLAN.md:1,4,10,13,15,21,23,24`

内容仍写"已发布：`https://github.com/ch998244353/TuckPane/releases/tag/v4.2.1`"、"Actions `38152182368` 成功"。4.2.2/4.2.3/4.2.4 三次发版都没更新它。

首轮 F14 包含两部分：UserAgent（已在 4.2.4 修好）与 `PLAN.md`（仍未修）。这是仓库里唯一还在指向上游发布页的文档。

**修法**：更新或删除。若 `PLAN.md` 已废弃，删掉比留着误导好。

---

## §10 4.2.4 已知问题总表（77 条）

**状态列**：`新` = 本轮首次发现；`遗留` = 首轮已报、4.2.4 未修；`已修` = 4.2.4 已修（保留记录）；`事实` = 非缺陷但需知情。

### P2（15 条）

| 编号 | 标题 | 位置 | 状态 |
|---|---|---|---|
| A1 | 两个修复定时器守卫集不一致，右键菜单会被抢 owner | `MainWindow.xaml.cs:6959` / `:7027-7029` | 新 |
| A4 | `IsSafeAnchor` 否掉 `HWND_TOP`，最该修的场景退化成空操作 | `DesktopLayerService.cs:186-191` / `:173-174` | 已修（不彻底） |
| A8 | 展开态完全不自愈（Win+D 后面板被壁纸盖住） | `DesktopLayerService.cs:201-206`、`MainWindow.xaml.cs:7027` / `:6959` | 遗留 |
| A9 | z-order 修复被 `ownerChanged` 门控，owner 没变就不修位置 | `DesktopLayerService.cs:61-71` | 遗留 |
| A12 | 资源管理器重启后托盘图标不重建（无 `TaskbarCreated`） | `TrayIconService.cs:43` / `:49-54` | 新（第三轮） |
| C1 | 更新链无独立信任锚，哈希与包同源，两个 exe 均未签名 | `ReleaseClient.cs:79-119`、`Updater/Program.cs:50-58` | 遗留 |
| C2 | fork 与上游共用 `AppId` 与发布者身份，两条渠道可互相覆盖 | `TuckPane.iss:19,24-26,48` | 遗留 |
| C3 | CI 只在打 tag 时触发，PR 与 main 零检查 | `.github/workflows/release.yml:3-6` | 遗留 |
| F6 | 全局 UI 异常处理器无条件 `Handled = true`，所有缺陷静默化 | `App.xaml.cs:100-110` | 新（第三轮） |
| G1 | 图标**内存**缓存每窗口一份、无容量淘汰 | `IconCacheService.cs:20`、`MainWindow.xaml.cs:58` | 新 |
| G3 | 二开新增 472 行代码零伴随测试 | `tests/` | 遗留 |
| H1 | 桌面层自愈逻辑长在 `MainWindow` 交互标志丛里，守卫已漂移 | `MainWindow.xaml.cs:342` / `:6957-7040` | 新 |
| J1 | 二开与上游共用单实例键与状态目录，双版本互斥且互写同一份 `state.json` | `App.xaml.cs:70,241`、`AppPaths.cs:7,20` | 新（第七轮） |
| K1 | Ctrl+拖到桌面：复制意图被丢弃，实际执行移动 | `ShellDragService.cs:447-456`、`MainWindow.xaml.cs:5494-5510` | 新（第八轮） |
| M3 | `StartupService.Apply` 无异常保护，注册表写失败导致整个应用无法启动 | `StartupService.cs`、`AppHost.cs:126` | 新（第十轮） |

### P3（62 条）

| 编号 | 标题 | 位置 | 状态 |
|---|---|---|---|
| A2 | 桌面图标视图静态缓存只用 `IsWindow`，防不住 HWND 复用 | `DesktopLayerService.cs:18` / `:441-445` | 新 |
| A3 | `0x052C` 后备路径只查 Progman 直接子级 | `DesktopLayerService.cs:430-437` | 新 |
| A5 | `DwmGetWindowAttribute` 失败被当成"未 cloaked" | `DesktopLayerService.cs:205-209` | 遗留 |
| A6 | `Hidden` 分支 hide/show 往返可能被消息泵合并 | `DesktopLayerService.cs:141-154` | 遗留 |
| A7 | `RepairDesktopAttachment()` 返回修复前状态，日志说 fixed 但没复探 | `DesktopLayerService.cs:126-156` | 遗留 |
| A10 | `IsBehindDesktopLayer()` 单句柄当地板线，多 `WorkerW` 下漏判 | `DesktopLayerService.cs:237-253` | 遗留 |
| A11 | 新增 1 秒定时器硬编码，绕过性能档位 | `MainWindow.xaml.cs:342-345` | 遗留 |
| A13 | 过渡在版本号自增后早退，`_animating` 与悬停放大都不复位 | `MainWindow.xaml.cs:1678-1810` / `:1847-1905` | 新（第五轮） |
| A14 | DPI / 显示器拓扑变化只处理 Dock，其余收纳窗不重算尺寸与位置 | `MainWindow.xaml.cs:3106-3110` | 新（第五轮） |
| A15 | 中转站显示器信息只解析一次，拔屏后热区永久错位 | `MainWindow.xaml.cs:6196` | 新（第六轮） |
| C4 | CI actions 与 Inno Setup 未钉版本，发布物不可复现 | `.github/workflows/release.yml` | 新 |
| C5 | 更新失败直接把英文异常抛到 UI | `AppUpdateService.cs:60-88` | 遗留 |
| C6 | `updates/config-backups/` 只增不减 | `UpdateFiles.cs:40-52` | 遗留 |
| C7 | 更新检查失败静默把下次检查推后 24 小时 | `AppUpdateService.cs:60-88` | 新 |
| C8 | 安装器注册文件夹右键菜单无所有权守卫，会抢走便携副本的菜单 | `installer/TuckPane.iss:111-138` | 遗留 |
| C9 | 更新前备份假定 `state.json` 存在，全新配置下更新直接失败 | `UpdateFiles.cs:40-55` | 遗留 |
| C10 | 文件夹菜单 `%1` 遇驱动器根尾反斜杠吞掉收尾引号 | `FolderContextMenuService.cs:48-50` | 新（第六轮） |
| D1 | 全链无 fsync，断电窗口内可能丢最后一次保存 | `StateStore.cs:36-52` | 遗留 |
| D2 | 跨卷移动的校验只比名称+长度，不比内容哈希 | `StorageService.cs:571-580` | 新 |
| D3 | 回滚到旧版本时新 schema 字段被静默丢弃 | `StateStore.cs` / `AppState.cs:116` | 新 |
| D4 | `result.json` 里的恢复路径是悬空引用 | `AppUpdateService.cs:157-183` | 遗留 |
| D5 | `TuckPane.log` 是死文件，无轮转无清理 | `%LOCALAPPDATA%\TuckPane\TuckPane.log` | 遗留 |
| D6 | 部分保存路径失败时无 UI 提示（AppHost 内 28 处裸调用） | `AppHost.Dock.cs:19-37` | 遗留 |
| E3 | 高对比度订阅失败降级为 `Info`，而 `Info` 是空实现 | `ConsoleWindow.xaml.cs:112-137`、`AppLogger.cs:12` | 遗留 |
| E4 | `--create-note-in <path>` 无任何路径校验 | `App.xaml.cs:207-213` | 遗留 |
| E5 | `.tucknote`/`.tucktodo` 未拒绝 UNC 路径（NTLM 泄露） | `App.xaml.cs:214-224` | 遗留 |
| E6 | `TUCKPANE_TEST_ROOT` 在正式构建里仍然生效 | `AppPaths.cs:9` 等 6 处 | 遗留 |
| F1 | `CleanupSessions` 异常过滤器过窄，意外异常静默中断清理 | `AppUpdateService.cs:157-195` | 新 |
| F2 | 正常退出不 flush 位置防抖计时器，350–450 ms 内丢位置 | `MainWindow.xaml.cs:358-361`、`ConsoleWindow.xaml.cs:147-150` | 新 |
| F3 | 二实例激活重定向用无超时 `Wait()` | `App.xaml.cs:87` | 新 |
| F4 | 诊断代码位置白名单漏掉嵌套类型短名 | `DiagnosticRecord.cs:31-48` / `:87-98` | 遗留 |
| F5 | `_itemTouchHoldTimer` 是唯一关闭时未停的定时器 | `MainWindow.xaml.cs:368-371` | 新 |
| G2 | 依赖锁定只覆盖主工程，其余 6 个工程依赖浮动 | `src/TuckPane/packages.lock.json` | 新 |
| G4 | `Performance()` 每次调用同步做磁盘 IO 且每次读环境变量 | `AppLogger.cs:28-52` | 遗留 |
| H2 | `AppHost` 与 `MainWindow` 双向耦合，`_host.` 142 处 | `AppHost.cs`、`MainWindow.xaml.cs` | 新 |
| H3 | `PLAN.md` 仍停在 4.2.1，指向上游发布页 | `PLAN.md:1,4,10,13,15,21,23,24` | 遗留 |
| I1 | `ConsoleWindow_Closed` 不解除两处 `CompositionTarget.Rendering` 订阅 | `ConsoleWindow.xaml.cs:489-520` | 新（第七轮） |
| I2 | 关闭按钮指针热路径每次新建 `AccessibilitySettings` | `ConsoleWindow.xaml.cs:385-398` | 新（第七轮） |
| I3 | `RefreshAll()` 无条件重载编辑器，静默丢弃未提交的名字 | `ConsoleWindow.xaml.cs:233-255`、`:1799-1828` | 新（第七轮） |
| I4 | `CaptureManageDraft`/`LoadManageEditor` 的 `.First()` 无兜底，异常抛进渲染帧 | `ConsoleWindow.xaml.cs:1951-1952`、`:1801` | 新（第七轮） |
| I5 | 一次滑条调整触发 5 次全量状态落盘 | `ConsoleWindow.SliderPersistence.cs`、`StateStore.cs:34-51` | 新（第七轮） |
| K2 | 跨卷移动"校验通过即删源"只比对路径+长度 | `StorageService.cs:585-596`、`:441-449` | 新（第八轮） |
| K3 | `CreateExecutableShortcut` 泄漏 COM 对象 | `StorageService.cs:316-337` | 新（第八轮） |
| K4 | 导出目录名与新建文件夹名校验规则不一致 | `StorageService.cs:523-529` / `:158-173` | 新（第八轮） |
| K5 | 空目录导出的 `Directory.Delete` 在 try 之外 | `StorageService.cs:195-201` | 新（第八轮） |
| K6 | 目录刷新对每个文件读一次注册表 | `StorageService.cs:492-510`、`:176-192` | 新（第八轮） |
| K7 | `ReadUnsupportedNames()` 恒返回空，提示链是死代码 | `StorageService.cs:193`、`MainWindow.xaml.cs:972,1010-1015` | 新（第八轮） |
| K8 | 拖到桌面的目标路径绕过测试沙箱 | `StorageService.cs:258,272`、`AppPaths.cs:23-27` | 新（第八轮） |
| L1 | 待办完成动画用 20 Hz 轮询整张表 | `TodoWindow.xaml.cs:87-90,479-496,900-905` | 新（第九轮） |
| L2 | `TodoWindow_Closed` 释放 `_saveGate`，在飞行的保存抛 `ObjectDisposedException` | `TodoWindow.xaml.cs:783-790`、`:258-277` | 新（第九轮） |
| L3 | 图标身份每次调用都重算，内存缓存命中也不例外 | `IconCacheService.cs:100-142,150-199` | 新（第九轮） |
| L4 | 图标加载被 `SemaphoreSlim(1,1)` 全局串行化 | `MainWindow.xaml.cs:1634-1662` | 新（第九轮） |
| L5 | 待办勾选即删除，3 秒后不可恢复且无已完成区 | `Core/TodoRules.cs:7`、`TodoWindow.xaml.cs:479-496` | 新（第九轮） |
| M1 | `RepairMissingAlpha` 把纯黑像素判成透明，深色图标出洞 | `IconCacheService.cs:591-650` | 新（第十轮） |
| M2 | `SHIL_JUMBO` 不可用时不回退，图标退化成 32×32 放大 | `IconCacheService.cs:546-573` | 新（第十轮） |
| M4 | 单实例 `Listen` 注册太晚，快速二次启动弹错误的"遗留实例"提示 | `App.xaml.cs`（`OnLaunched`）、`SingleInstanceGuard.cs` | 新（第十轮） |
| M5 | 图标 PNG 落盘无 fsync，并发未命中同一路径会互相覆盖 | `IconCacheService.cs:209-238` | 新（第十轮） |
| M6 | 启动序列两处 fire-and-forget，且启动即构造整个总控台窗口 | `AppHost.cs:110-136` | 新（第十轮） |
| O1 | 更新页直出英文 .NET 异常原文（含 URL/状态码/路径） | `AppUpdateService.cs:66-71,23-30`、`Program.cs:83-87` | 新（第十一轮） |
| O2 | 取消更新后 200–300 MB 已下载包不清理，重试重下 | `AppUpdateService.cs:88-137,139-160` | 新（第十一轮） |
| O3 | `Dispose()` 后仍 `Changed?.Invoke()`，UI 读已释放实例 | `AppUpdateService.cs:120-137,186-190`、`AppHost.cs:2465` | 新（第十一轮） |
| O4 | `UpdateWindowInput` 构造中途抛异常 → 已禁用窗口永久禁用 | `UpdateWindowInput.cs:11-19`、`AppHost.cs:2422` | 新（第十一轮） |

### 事实（2 条，不计入缺陷数）

| 编号 | 内容 |
|---|---|
| E1 | 出网点只有 GitHub 更新链，无遥测、无统计、无崩溃上报、无第三方域名 |
| E2 | 更新信任根已从上游作者转移到 fork 作者 —— 这是二开该做的事，但用户必须知情 |

---

## §11 修复优先级

### 立刻（半天内可完成，风险极低）

| 项 | 改动量 | 收益 |
|---|---|---|
| **C3** 加 `ci.yml` | 新增 1 文件 | 从此 PR 有自动检查，是后续一切改进的前提 |
| **A4** 锚点语义拆分（`TryDesktopInsertAnchor`） | ~20 行 | 补上我自己 PR 的边界缺陷 |
| **M3** `StartupService.Apply` 包 try/catch | ~8 行 | 把"可选功能失败"与"应用起不来"解耦 |
| **A1** 修复定时器补三道门 | ~4 行 | 消除"开菜单后被抢 owner"的可见 bug |
| **A7** 修复后复探 | ~3 行 | 让"自愈"在日志里可验证 |
| **F5** 补 `_itemTouchHoldTimer.Stop()` | 1 行 | 补全关闭路径 |
| **A11** 定时器走性能档 | ~5 行 | 省电档不再被绕过 8 倍 |
| **D1** StateStore 加 fsync | ~15 行 | 消除断电丢配置 |
| **E5** 拒绝 UNC | ~10 行 | 消除凭据泄露向量 |
| **C5** 异常映射到本地化文案 | ~20 行 | 中文界面不再冒英文堆栈 |
| **A12** 订阅 `TaskbarCreated` 重建托盘图标 | ~15 行 | 资源管理器重启后图标不再永久消失（第三轮） |

### 近期（一周内）

| 项 | 说明 |
|---|---|
| **A8** 展开态探针 + `RepairExpandedZOrder()` | 消掉 4.2.4 自己列的已知问题 #1 |
| **A9** 拆分 owner 与 z-order 判断 | 修掉"压在普通窗口之上" |
| **A10** 改用 `WindowFromPoint` 点采样 | 同时解决多 `WorkerW` 漏判与 O(z-order) 成本 |
| **A2** 图标视图缓存加身份校验 | 消除 Explorer 重启后的 owner 错挂 |
| **A3** 后备路径重跑枚举 | 2 行，把死代码救活 |
| **A5** HRESULT 非 0 视为未知 | 3 行 |
| **G1** 图标内存缓存加上限 + 提到 `AppHost` 单例 | 长会话内存不再单调增长 |
| **C6** `config-backups` 清理 | 复用现有 `CleanupSessions` 模式 |
| **L1** 待办淡出改按行驱动 | 20 行，勾选时不再全表刷新 |
| **L4** 图标加载并发度放到 4 | 2 行，展开时不再逐项排队 |
| **C7** 失败时不写时间戳 + 短退避 | 弱网下不再"一天只查一次" |
| **C8** 文件夹菜单加所有权守卫 | 照搬 `RegisterDesktopMenu` 的三行，2 分钟 |
| **C9** 备份前判 `File.Exists` | 3 行，消掉"全新配置装不了更新" |
| **D6** 保存失败统一提示 | 需要设计一个非阻塞 UI 提示 |
| **F2** 退出前 flush 防抖计时器 | ~10 行 |
| **F3** `Wait(3s)` | 2 行 |
| **C2** `.iss` 身份 | 只改 6 行，但**必须先决定**是否换 `AppId`（换了等于新软件，老用户无法平滑升级；不换则两条渠道仍可互相覆盖） |
| **C1** 更新链信任锚 | 需要决策：自签名证书 + 硬编码指纹 / 或只在 README 明示风险 |
| **F6** 全局异常处理器改白名单 | 需要先定"哪些异常算可恢复"，否则容易改出崩溃回归（第三轮） |
| **A13** 早退路径统一 `AbandonTransition` + 3 秒看门狗 | ~15 行，顺手消掉 release notes 自列的"名字胶囊残留"（第五轮） |
| **A14** DPI/显示器变化重算尺寸与位置 | ~20 行，跨缩放比显示器拖动后不再错位（第五轮） |
| **A15** `0x007E` 分支里把 `_stationDisplay` 置 null | 1 行，和 Dock 的 `_dockRegionKey = null` 完全对称（第六轮） |
| **I1** 关闭时退订 `CompositionTarget.Rendering` | 2 行，重复退订安全（第七轮） |
| **I2** 复用 `_accessibilitySettings` | 3 行（第七轮） |
| **I5** 5 次全量写合并为 1 次 | ~20 行，滑条松手不再连写 5 遍磁盘（第七轮） |
| **J1** 单实例键与状态目录加渠道后缀 | 需先决定是否与上游分家，见 C2（第七轮） |
| **C10** `ParseArguments` 剥离尾引号 | 2 行，驱动器根右键不再报参数无效（第六轮） |

### 中期（重构，见 `架构优化建议书.md`）

- **H1** `DesktopAttachmentSupervisor`（2–3 天）—— A1/A4/A9/A11 的根因
- **H2** `_host.` 接口化（3–5 天）
- **G3** 补测试 —— 前置条件是把 Win32 调用抽成接口
- **A6** 消息泵让出 —— 需先做动态验证确认现象

### 判定为非问题（不修）

| 项 | 结论 |
|---|---|
| 首轮 F17（展开态 `Reattach` 行为变化） | 复核后确认不是回归 |
| 首轮 N9（`File.Move` 跨卷退化） | **我写错了**：`.tmp` 与目标同目录，永不跨卷。已删除 |
| 独立审查 A10 的"假阳性"分支 | 见 §3 A10，owned 窗口不会先遇到 `desktopRoot`，该分支不成立。已删除 |

---

## §12 给你的直接建议

### 12.1 现在要不要升级到 4.2.4？

**可以升，也可以不升，两者差别不大。**

- 4.2.4 相对 4.2.3 是净改进：A4 让边界场景从"主动埋到最底"变成"不动"，F3（定时器未停）真正修好了。
- 但 4.2.4 **仍带着 A4、A8、A9**，而你装的 4.2.3 也有 A8、A9。升级不会让你变差，也不会让你变好很多。
- 如果你在意 A8（展开态被 Win+D 盖住），4.2.4 没修，得等下一版。

### 12.2 要不要担心安全？

**不用担心恶意代码。** 已经验证过：547/547 文件与你装的版本逐字节一致、发布物由 CI 自动上传、全仓无遥测地址、无反射加载、无提权面。

**需要权衡的是信任模型**：你的自动更新源是朋友的 GitHub 账号，包没有签名（C1）。这意味着**朋友账号一旦被盗，攻击者可以让你的机器静默安装任意程序**（更新是 `/VERYSILENT` 自动执行的）。

三个可选动作，按代价排序：
1. **什么都不做** —— 接受"信任朋友"这个前提。对个人项目来说这是合理选择。
2. **关掉自动检查** —— 需要时手动去 GitHub Releases 下载，自己核对 `SHA256SUMS.txt`。代价是每次手动。
3. **让朋友加签名** —— 自签名证书 + 硬编码指纹就够，不需要买证书。

### 12.3 数据安全：现在就能做的一件事

手动备份一次 `%LOCALAPPDATA%\TuckPane\state.json`。D1 说明断电时最后一次保存可能丢；虽然概率低，但那是你所有收纳窗布局的唯一副本。

### 12.4 要不要把这份报告发给朋友？

建议发。但**按顺序给**：

1. 先给 §11 的"立刻"清单（9 项，都是几行到几十行的低风险改动）；
2. 再给 §10 总表，让他自己挑；
3. C1/C2 这种需要产品决策的，单独聊，不要混在技术清单里——`AppId` 换不换是个真问题，不是"改个 bug"。

### 12.5 下一步我能做的

- **提第二个 PR**：A4 + A1 + F5 + A7 + A11 + C3，约 60 行，全部低风险。其中 A4 是我自己上一轮引入的缺陷，A1 是"新代码加了门、老代码没加"的不对称，这两个我该补。→ **已完成：PR #2**（https://github.com/daha1216/TuckPane/pull/2）
- **提第三个 PR**：A12（托盘图标 `TaskbarCreated`，约 15 行）+ F6（异常白名单，需要先定策略）。A12 是纯增量、零风险；F6 建议先只做"记录 + 可见提示"，不改 `Handled` 语义。→ **A12 已完成：PR #3**（https://github.com/daha1216/TuckPane/pull/3）；F6 未提。
- **提第四个 PR**：C8（文件夹菜单守卫，3 行）+ C9（备份前 `File.Exists`，3 行）+ C6（`config-backups` 清理）。这三条都在安装/更新路径上，改动极小，且 C9 能让"全新配置装不了更新"这个死角消失。
- **提第五个 PR**：A13（早退路径 `AbandonTransition` + 看门狗，约 15 行）+ A14（DPI/显示器变化重算，约 20 行）。这两条是第五轮 UI 状态机的产出，A13 还能顺手消掉作者自己在 release notes 里列的"名字胶囊残留"。风险略高于前两个 PR（动的是过渡主路径），建议单独一个 PR、便于回滚。

---

## §13 编号映射表

### 13.1 首轮报告（F / N）→ 终版

| 首轮 | 终版 | 说明 |
|---|---|---|
| F1 / F2 | **A4** | 已修但不彻底（`HWND_TOP` 语义混淆） |
| F3 | — | ✅ 已修（`_desktopVisibilityTimer.Stop()`） |
| F4 | A11 | |
| F5 | C1 | |
| F6 | C2 | 独立审查补充了 `AppId` 共用这一层 |
| F7 | G3 | |
| F8 | A5 | |
| F9 | A6 | |
| F10 | C5 | |
| F11 | A7 | |
| F12 | E3 | |
| F13 | — | ✅ 已修（`app.manifest` 4.2.4.0） |
| F14 | — / H3 | UserAgent ✅ 已修；`PLAN.md` 未修 → H3 |
| F15 | D4 | |
| F16 | D5 | |
| F17 | — | 判定非问题 |
| F18 | F4 | |
| F19 | G4 | |
| N1 | D1 | **机制已改写**（原文把备份自愈方向写反了） |
| N2 | A8 | **修法已改写**（`_expandedOwner` 恒为 NULL，原修法是空操作） |
| N3 | C3 | |
| N4 | D6 | **范围已收窄**（主保存路径其实有提示） |
| N5 | E4 | |
| N6 | E5 | |
| N7 | E6 | |
| N8 | C6 | |
| N9 | — | **已删除**（`.tmp` 与目标同目录，永不跨卷） |
| N10 | A4 | |
| N11 | A10 | **机制已改写**（"假阳性"分支不成立，删除；保留假阴性） |
| N12 | A2 | **已降级** P2 → P3 |
| N13 | A9 | |
| R3（第四轮） | C8 | 安装器文件夹菜单守卫 |
| R4（第四轮） | C9 | 更新前备份守卫 |
| N14 | A1 | |
| N15 | F5 | |
| R1（第三轮） | F6 | 新增，无首轮对应项 |
| R2（第三轮） | A12 | 新增，无首轮对应项 |
| R5（第五轮） | A13 | 新增，无首轮对应项 |
| R6（第五轮） | A14 | 新增，无首轮对应项 |
| R7（第六轮） | A15 | 新增，无首轮对应项 |
| R8（第六轮） | C10 | 新增，无首轮对应项 |

### 13.2 独立审查（A/C/D/E/F/G/H）→ 终版

| 独立 | 终版 | 说明 |
|---|---|---|
| A1 | A1 | 独立审查的 P3 → 终版 **P2**（其自身已确证危害链：菜单停留 8 秒即被抢 owner） |
| A2 | A2 | 一致 |
| A3 | A3 | 一致 |
| A4 | A4 | 一致 |
| C1 | C1 | 一致 |
| C2 | C2 | 一致，并合并首轮 F6 |
| C3 | C3 | 一致 |
| C4 | C4 | 一致 |
| D1 | D1 | 一致（独立审查也独立发现了 fsync 缺失） |
| D2 | D2 | 一致 |
| D3 | D3 | 一致 |
| E1 | E1 | 一致（事实） |
| E2 | E2 | 改为"事实"而非缺陷（定级串味） |
| E3 | — | 并入 §6 结论性说明（性能日志默认关闭、低敏感、无外传，属正面结论） |
| F1 | F1 | 一致 |
| F2 | F2 | 一致 |
| F3 | F3 | 一致 |
| G1 | G1 | 一致（第四轮唯一新增 P2；第七轮 J1 为另一条新增 P2） |
| G2 | G2 | 一致 |
| G3 | G3 | 一致 |
| H1 | H1 | 独立审查 P3 → 终版 **P2**（它是 A1/A4/A9/A11 的共同根因） |
| H2 | H2 | 一致 |

> 第十轮（`findings/round10.md`）新增 M1–M6，为内部编号，无外部对应。

---

## §14 第七轮增补（域 I：总控台生命周期；域 J：进程级共享）

> 详细证据与代码引用见 `findings/round7.md`。本节只收 F2 未覆盖的总控台侧问题——"退出前 flush 防抖计时器"已由 F2 登记（`MainWindow.xaml.cs:358-361`、`ConsoleWindow.xaml.cs:147-150`），不重复计数。

### 14.1 域 I：总控台生命周期与设置持久化（P3 × 5）

#### I1 [P3] `ConsoleWindow_Closed` 不解除两处 `CompositionTarget.Rendering` 订阅

`ConsoleWindow_Closed`（`:489-520`）的解绑清单很完整：`Loaded`/`SizeChanged`、`AttachConsoleXamlRoot(null)`、`_consoleContentClip`、`_themeBackdrop.DetachAndClose()`、`ThemeChanged`、`_accessibilitySubscription`、6 个可见性 token、**六个 timer 全部 Stop**、`_themeSurface`、`_settingsEdgeSurface`、`Activated`/`Closed`、`_chrome`。

遗漏的是两处"订阅后等下一帧自己退订"的静态事件：

- `ScheduleRuntimeApply`（`:1979-1985`）：`CompositionTarget.Rendering += ApplyPendingRuntimeChanges;`
- `UniformCompactScaleSlider_ValueChanged`（`:1059-1070`）：`CompositionTarget.Rendering += ApplyPendingUniformCompactScaleChanges;`

两者都在回调第一句 `-=`，所以只在"已订阅、未到下一帧"的窗口期关闭才会残留。`CompositionTarget.Rendering` 是进程级静态事件，残留订阅会一直持有 `ConsoleWindow` 及其可视树。

**修法**：`Closed` 里补两行 `CompositionTarget.Rendering -= ...;`（重复退订是空操作）。

#### I2 [P3] 关闭按钮的指针热路径每次新建 `AccessibilitySettings`

`SetCloseButtonState`（`:385-398`）第一句是 `var accessibility = new Windows.UI.ViewManagement.AccessibilitySettings();`，它被 `ConsoleCloseButton_PointerEntered` / `PointerPressed` / `PointerReleased` / `PointerExited` 四个处理器调用——鼠标每次进出/按下/抬起都 new 一个 WinRT 可激活对象。

同文件里已有更省的对照：`SetMinimizeButtonState`（`:364-371`）不新建对象；`UpdateManageListItemSurfaces`（`:1768-1788`）用的是缓存字段 `_accessibilitySettings.HighContrast`。

**修法**：改用 `_accessibilitySettings.HighContrast`。

#### I3 [P3] `RefreshAll()` 会静默丢弃管理页里未提交的名字

`RefreshAll()`（`:233-255`）→ `PopulateManageList(selectId ?? _selectedId)`（`preserveEditor` 默认 `false`）→ `LoadManageEditor(id)`（`:1799-1828`），后者无条件执行：

```csharp
_editing = Clone(source);
_manageNameDirty = false;
ManageNameBox.Text = source.Name;
```

`RefreshAll` 的调用点里多个会在用户编辑管理页时发生：托盘菜单 `TrayCommand.ToggleStartup`（`AppHost.cs:2574`）、收纳窗拖回桌面/移入容器（`AppHost.cs:1881`、`:1937`）、创建/删除收纳窗（`:1497`、`:1504`、`:1572`、`:1604`）。

对照：`ManageList_SelectionChanged`（`:1751-1777`）与 `RootNavigation_SelectionChanged`（`:715-739`）都先 `await FlushPendingManageChangesAsync()`、失败回滚选中项。**只有 `RefreshAll` 跳过 flush。**

后果：名字框里打完字（尚未 `CommitManageNameAsync`），点一下托盘菜单的"开机启动"，输入被还原且无提示。

**修法**：`RefreshAll` 传 `preserveEditor: _editing is not null`；或在重载前判断 `_manageNameDirty || _manageChangeVersion > _savedManageChangeVersion` 时先 flush。

#### I4 [P3] `CaptureManageDraft` / `LoadManageEditor` 的 `.First()` 无兜底，异常会抛进渲染帧

`CaptureManageDraft`（`:1951-1952`）与 `LoadManageEditor`（`:1801`）都用 `State.Organizers.First(item => item.Id == ...)`。

`CaptureManageDraft` 是在 `ApplyPendingRuntimeChanges`（`CompositionTarget.Rendering` 回调，`:1987-2002`）里调用的，而该回调外层没有 try/catch。若 `_editing.Id` 对应的收纳窗在"已排帧、未执行"的窗口期被删除（`AppHost.cs:228` 的 `RemoveAll`），`First()` 抛 `InvalidOperationException` → 冒泡出渲染回调 → 落到 `App.UnhandledException`（`App.xaml.cs:102-109`）被 `args.Handled = true` 吞掉（F6 放大）→ 下一帧再抛一次。

**修法**：三处改 `FirstOrDefault(`，null 时早退（`CaptureManageDraft` 返回 null → 回调直接 `return`；`LoadManageEditor` null 时清空 `_editing` 并折叠详情卡）。

#### I5 [P3] 一次滑条调整触发 5 次全量状态落盘

`FlushPendingSliderSavesAsync()` 依次调 `SaveHoverMagnificationSlidersAsync` / `SaveUniformSlidersAsync` / `SaveNameSlidersAsync` / `SaveDelaySlidersAsync` / `SaveThemeAsync`，**每一路都以 `_host.SaveStateAsync()` 落盘**。

每次 `SaveStateAsync` 都走完整 `StateStore.SaveAsync`（`StateStore.cs:34-51`）：`Normalize(state)` → 全量 `JsonSerializer.Serialize` → `File.WriteAllTextAsync(tmp)` → `File.Copy(state.json → state.json.bak)` → `File.Move`，全程持 `_saveGate`，`MoveIntoPlaceAsync` 还带 10×50ms 重试。

即"松开一个滑条" = 5 次全量序列化 + 5 次备份复制 + 5 次原子替换。

**修法**：把 5 个 `SaveXxxAsync` 的"赋值"部分与"落盘"部分拆开，先全部赋值，最后只调一次 `SaveStateAsync`。`SliderSaveSequence` 的快照捕获语义不受影响。

### 14.2 域 J：进程级共享（P2 × 1）

#### J1 [P2] 二开与上游共用单实例键与状态目录

三处硬编码标识**完全沿用上游**：

- `App.xaml.cs:70` / `:241`：`"TuckPane-019d2f2d-0bfb-7ff0-98f5-d93093bb0b5d"`（单实例键，`legacyName` 为 `GlassFolder-…`）；
- `AppPaths.cs:7`：`ProductDirectoryName = "TuckPane"`；
- `AppPaths.cs:20`：`StatePath = Path.Combine(LocalRoot, "state.json")`。

且 `IsActivationExecutable`（`App.xaml.cs:177-195`）把任何名为 `TuckPane.exe` / `00-启动 TuckPane.exe` 的完整路径都认作"自己"。

后果（本用户正是"先装原版、再装二开"）：

1. **运行时互斥**：先启动原版、再双击二开 → `FindOrRegisterForKey` 判定非 current → `RedirectActivation` 把激活转给原版 → 二开 `return 0` 退出。用户看到"点二开没反应，原版反而弹出来"。
2. **状态互写**：两者共用 `%LOCALAPPDATA%\TuckPane\state.json` 及 `Items/`、`Windows/`、`icon-cache/`。二开已给 `AppStateV2` 加了 `ContainerStationId`、`ExpandedPosition` 等字段，原版读后按自己的 `Normalize` 处理，写回时**丢掉不认识的字段**——两版本交替启动一次，容器关系与展开位置就没了。
3. `TUCKPANE_TEST_ROOT` 后缀机制（`App.xaml.cs:71-74`）说明作者考虑过"多实例并存"，但只用于测试，未用于"两个发行渠道并存"。

与 C2（安装器共用 `AppId`，`TuckPane.iss:19,24-26,48`）同根因，但**比 C2 更早触发**：C2 要一次覆盖安装才生效，J1 只要两版本都装着、交替启动就会发生。

**修法**：`CreateInstanceKey` / `CreateSingleInstanceGuard` 的 `name` 加渠道后缀，`ProductDirectoryName` 同步改名并保留一次性迁移；或至少禁止双版本并存并给出明确提示。长期方案见 C2。

### 14.3 本轮"防护到位"复核（10 项）

| # | 模块 | 结论 |
|---|------|------|
| 1 | 语言资源完整性 | `en-US`/`ja-JP`/`zh-CN` 各 **429** 键，三方零缺失零多余；代码引用的 **255** 个键全部存在 |
| 2 | 格式化占位符 | 逐条比对实参个数与 `{n}` 最大下标，**零越界** |
| 3 | 管理页切换 | `ManageList_SelectionChanged` / `RootNavigation_SelectionChanged` 均先 flush、失败回滚 |
| 4 | 滑条并发 | `SliderSaveSequence` 用信号量 + 修订号循环，快照在序列化回调里捕获，快速拖动不写撕裂值 |
| 5 | 开关处理器 | `:897-1002` 五个 Toggle 均有 `_componentReady`/`_loadingXxx` 守卫 + try/catch + 回滚 + `ShowError` |
| 6 | 异常捕获纪律 | 全仓 28 处裸 `catch {`，均为"回滚后 rethrow"/"Dispose 后 rethrow"/"诊断不得影响主流程"，无静默吞业务错误 |
| 7 | 同步阻塞 | 全仓 `.Result`/`.Wait(` 仅 4 处，均为有超时的日志 flush 或已判定完成的 `Task.Result`，无 UI 线程同步等待 |
| 8 | 删除重入 | `DeleteOrganizerButton_Click` 有 `_deleteDialogOpen` + `TransferQueue.IsActive` 双守卫 |
| 9 | 编辑器字段 | `Clone` 少复制 5 个字段，但 `ApplyOrganizerRuntime` 逐字段赋值、从不整体替换，**不是缺陷** |
| 10 | 事件订阅总账 | 除 I1 的 2 处外，其余 `+=`/`-=` 不配对项均为随对象销毁的局部订阅，无静态事件泄漏 |

### 14.4 本轮未覆盖

`TodoWindow.xaml.cs`（919 行）与 `NoteWindow.xaml.cs`（830 行）只做了事件与 `async void` 计数；`StorageService.cs`（618 行）、`ShellDragService.cs`（733 行）、`IconCacheService.cs`（746 行）的并发与错误路径；XAML 层 Tab 顺序与 `AutomationProperties`；全部结论仍未编译、未运行（本机无 .NET SDK）。

---

## §15 第八轮增补（域 K：文件操作执行层）

审查对象：`Services/StorageService.cs`（618 行，全文）、`Services/ShellDragService.cs`（733 行，全文），以及拖出交接调用点 `MainWindow.xaml.cs:5460-5560`。详见 `findings/round8.md`。

### 15.1 K1 [P2] Ctrl+拖到桌面：复制意图被丢弃，实际执行移动

`ShellDragService.cs:447-456` 在松开左键命中桌面时记录了 `DesktopCopyRequested = (keyState & MouseKeyControl) != 0`，`ShellDragResult`（`:52-61`）也把它带出来了；但 `MainWindow.xaml.cs:5494`/`:5508` 只判断 `Outcome == DesktopRequested`，**从不读该字段**，一律走 `MoveItemToDesktopAsync`。配套的 `StorageService.CopyItemToDesktopAsync`（`:270-282`）全仓零调用者 —— 它就是为这条 Ctrl 分支写的，说明功能做到一半。

用户按住 Ctrl 拖到桌面，光标给的是"复制"反馈（`GiveFeedback` 返回 `DRAGDROP_S_USEDEFAULTCURSORS`，由 OLE 画加号），松手后条目却从收纳窗消失。数据还在桌面，但"复制"被静默改写成"移动"。

修法：`:5508` 按 `result.DesktopCopyRequested` 分流到 `CopyItemToDesktopAsync`；若产品上决定不支持，则应同时删掉该字段与 `CopyItemToDesktopAsync`，别留"看起来支持"的假象。

### 15.2 K2 [P3] 跨卷移动"校验通过即删源"只比对路径+长度

`StorageService.cs:585-596` `VerifyEquivalent` 比对 `TotalBytes` 与 `Entries.SequenceEqual`，而 `BuildManifest:598-620` 的条目只有 `D|路径` 与 `F|路径|长度`。`MovePathAsync:441-449` 在这个校验通过后就 `Directory.Delete(source, recursive: true)`。同长度内容损坏、复制期间源被并发改写而长度恰好不变、NTFS 压缩/稀疏差异都无法发现 —— 这是全仓唯一一处"校验通过即删源"的闸门，强度只有元数据级。

### 15.3 K3 [P3] `CreateExecutableShortcut` 泄漏 COM 对象

`StorageService.cs:316-337` 用 `Type.GetTypeFromProgID("WScript.Shell")` + `dynamic`，`shell` 与 `shortcut` 两个 RCW 都没有释放。同一个文件的 `AddItemShortcut`（`:110-118`）在 `finally` 里 `Marshal.FinalReleaseComObject(instance)`，`ShellDragService.TryInitializeDragImage:401-405` 也做对了 —— 只有这一处是遗留写法。批量导入 exe 时每个文件泄漏 2 个 RCW，且 `dynamic` 的 IDispatch 晚绑定比 `IShellLinkW` 慢一个量级。

### 15.4 K4 [P3] 导出目录名与新建文件夹名校验规则不一致

`ValidateNewFolderName`（`:158-173`）拦非法字符、尾点、尾空格、`CON`…`LPT9`（含 `CON.txt` 写法）；`SanitizeName`（`:523-529`）只替换 `Path.GetInvalidFileNameChars()` 并截断 80，**不处理尾点/尾空格/保留设备名**。窗口名是 `CON`、`PRN`、`a.` 时，导出到桌面会在 `:211` 的 `Directory.CreateDirectory` 失败，整个导出返回 `Failed` 并显示系统英文原文 —— 而同样的名字在"新建文件夹"入口会被友好拦住。同一套命名规则两处实现、一严一松。

### 15.5 K5 [P3] 空目录导出的 `Directory.Delete` 在 try 之外

`StorageService.cs:195-201`：`EnumerateFileSystemEntries().Any()` 判空后直接 `Directory.Delete(_itemsRoot)`，而统一错误处理的 `try` 从 `:213` 才开始。判空与删除之间落进一个文件（拖放队列、云同步、杀毒临时文件）就抛 `IOException`，冒泡到 `AppHost.cs:1382` 的 `TransferQueue.RunAsync`，绕过"失败要返回 `TransferOutcome.Failed` + 人话消息"的统一路径。

### 15.6 K6 [P3] 目录刷新对每个文件读一次注册表

`ReadItems`（`:176-192`）逐条目调 `GetDisplayName`，后者（`:492-497`）对普通文件走 `ExplorerShowsExtensions()`，而它（`:499-510`）每次都 `Registry.CurrentUser.OpenSubKey(...Explorer\Advanced).GetValue("HideFileExt")`。N 个文件 = N 次注册表打开，且 `RefreshCatalogAsync` 在每次拖放、文件变化、窗口激活后都会跑（`MainWindow.xaml.cs:603,659,885,4021,4053,5522`）。100 个文件的收纳窗、一次拖放引发 3 次刷新 = 300 次注册表访问，全部串行在工作线程上。

### 15.7 K7 [P3] `ReadUnsupportedNames()` 恒返回空，提示链是死代码

`StorageService.cs:193` `=> []`；但 `MainWindow.xaml.cs:972` 仍每次刷新 `Task.Run` 调它，`:1010-1015` 仍有 `unsupported.Except(_lastUnsupported)` 与 `UnsupportedItemsFormat` 的 `ShowMessage`。`notifyUnsupported` 参数、`_lastUnsupported` 字段、文案键全部空转。当前无可触发场景（`TryGetKind` 对任何存在的文件都返回 `File`），但这是维护陷阱：以后给 `ReadItems` 加过滤条件的人会以为提示链还在工作。

### 15.8 K8 [P3] 拖到桌面的目标路径绕过测试沙箱

`AppPaths.cs:23-27`：`DesktopRoot` 在 `TUCKPANE_TEST_ROOT` 下是 `<TestRoot>\Desktop`。`ExportToDesktopAsync:205` 用的正是它（正确），但 `MoveItemToDesktopAsync:258` 与 `CopyItemToDesktopAsync:272` 用的是 `Environment.GetFolderPath(SpecialFolder.DesktopDirectory)`（绕过）。设了测试根跑稳定性检查时，拖到桌面会写真实桌面；两处"桌面在哪"的答案在测试模式下还不一致。

### 15.9 本轮复核到的防护（不计入）

OLE `Initialize`/`Uninitialize` 严格配对且失败路径不反初始化（`ShellDragService.cs:326-330,366-369`）；`TryInitializeDragImage`、`CreateDataObject`、`ShellDataObject.Dispose`、`ShellDragSession.Dispose` 的 RCW/HBITMAP 释放齐全且 `Dispose` 幂等；`PrepareAsync` 取消时用 `ContinueWith` 兜底删除已生成的位图；`File.Move(..., overwrite: false)` 与 `FileMode.CreateNew` 保证不覆盖；`BuildManifest`/`CopyDirectoryAsync` 对每个条目检查 `ReparsePoint` 并拒绝，防目录穿越；暂存统一 `.glassfolder-staging-<guid>` 前缀且 `ReadItems` 显式跳过；共享冲突只在"源仍在、目标不存在"时降级为复制并保留源。

### 15.10 本轮未覆盖

`PortableDocumentCatalogChanges`、`PublishedMoveCompletion`、`DropValidator.ValidateBatch` 只做调用点核对；`IconCacheService.CreateDragBitmap` 的 GDI 句柄所有权只确认了调用方释放。全部结论仍未编译、未运行。

---

## §16 第九轮增补（域 L：图标缓存与便签/待办窗口）

审查对象：`Services/IconCacheService.cs`（746 行）、`TodoWindow.xaml.cs`（919 行）、`NoteWindow.xaml.cs`（830 行）、`Services/NoteStore.cs`（482 行）、`Core/TodoRules.cs`、`Core/NoteRenameTransaction.cs`、`Core/PortableDocumentCatalogChanges.cs`、图标加载调用点 `MainWindow.xaml.cs:1634-1662`。详见 `findings/round9.md`。

### 16.1 L1 [P3] 待办完成动画用 20 Hz 轮询整张表

`TodoWindow.xaml.cs:87-90` 的 `_completionTimer` 是 50 ms 重复定时器，`CompletionTimer_Tick:479-496` 每 tick 对**所有**行调 `RefreshVisual(now)`；`TodoRow.RefreshVisual:900-905` 每次设 `Opacity` 并抛 `Done`/`TextDecorations`/`UndoVisibility` 三个 `PropertyChanged`。而淡出总时长只有 `FadeDuration = 260 ms`（`Core/TodoRules.cs:8`），未完成行的 `GetOpacity` 恒为 1、三个属性也不会变 —— 全是空刷新。N 行 × 20 Hz × 4 次绑定刷新 = 80·N 次/秒，全部在 UI 线程，持续整个 3 秒窗口；200 条待办勾选一次约产生 16000 次绑定刷新。建议改成只驱动正在淡出的行，并在 `Math.Abs(next - Opacity) < 0.001` 时提前返回。

### 16.2 L2 [P3] `TodoWindow_Closed` 释放 `_saveGate`，在飞行的保存会在 `Release()` 上抛异常

`TodoWindow_Closed:783-790` 在 `_saveTimer.Stop()`、`_completionTimer.Stop()` 之后 `_saveGate.Dispose()`；而 `FlushAsync:258-264` 与 `BeginDirectoryRenameAsync:266-277` 都是 `await _saveGate.WaitAsync()` → `finally { _saveGate.Release(); }` 的结构。若关闭时有一次 `FlushAsync` 正卡在 `_store.SaveTodoAsync` 的 await 上（磁盘慢/网络盘/杀毒扫描都会拉长），`Closed` 会先 Dispose，随后那次保存恢复执行、`Release()` 抛 `ObjectDisposedException`；异常发生在 `_saveTimer.Tick` 的 `async void` 里，被 `App.UnhandledException`（`App.xaml.cs:102-109`）吞掉（F6 放大）。对照 `NoteWindow_Closed:816-823` 不 Dispose 序列对象，是正确写法。

### 16.3 L3 [P3] 图标身份每次调用都重算，内存缓存命中也不例外

`IconCacheService.GetIconCoreAsync:100-142` 在查 `_memoryCache` **之前**就 `string identity = await Task.Run(() => BuildCacheIdentity(key));`，而 `identity` 只用于算磁盘缓存文件名，与内存键 `memoryKey` 无关。于是每个图标每次请求都要付一次线程池调度 + `File.Exists` + `FileInfo.Length` + `LastWriteTimeUtc`；`.lnk` 还要递归解析目标（`MaximumShortcutIconDepth = 4`），`.url` 要读 ini。`BuildCacheIdentity:150-199` 对目录返回 `|directory|0|LastWriteTimeUtc.Ticks` —— 目录内任何子项增删改名都会让身份变、`cachePath` 变、重新 `RefreshAsync` 写一个新 PNG；而 `SweepAsync:44-86` 只在启动时跑一次，旧 PNG 要到下次重启才被裁剪（`MaximumCachedIcons = 4096`、`MaximumCacheBytes = 64MB` 也只在那时生效）。

### 16.4 L4 [P3] 图标加载被全局串行化

`MainWindow.xaml.cs:1634-1662` 的 `GetIconThrottledAsync` 用 `_iconLoadGate`（`SemaphoreSlim(1, 1)`）包住每次 `GetIconAsync`/`GetDockIconAsync`，整个进程同一时刻只有一个图标在加载：身份计算、shell 提取（`SHGetFileInfo`/`IImageList`）、PNG 解码、`BitmapImage.SetSourceAsync` 全部串行。图标之间没有顺序依赖，串行化的唯一效果是把"并行 4 个 200 ms"变成"串行 4 个 200 ms"；冷缓存展开 30 项收纳窗就是 30 次串行往返，每次还叠一个 L3 的 `Task.Run`。建议并发度放到 3–4，或用 `ConcurrentDictionary` + `Lazy<Task<...>>` 做同 key 去重。

### 16.5 L5 [P3] 待办"勾选即删除"：3 秒后不可恢复，且没有已完成区

`Core/TodoRules.cs:7` `CompletionDelay = 3s`；`CompletionTimer_Tick:479-496` 到点后 `_rows.Remove(row)` + `_document.Tasks.Remove(row.Task)` + `ScheduleSave()`；`TodoWindow` 构造函数还会先跑一次 `RemoveExpired` 并 `ScheduleSave()`，所以**上次关窗口前 3 秒内勾掉的任务，下次打开时已经不在了**。`PortableTodoDocument` 没有任何"已完成/归档"字段，磁盘上留不下痕迹，唯一挽回手段是那 3 秒内的行内撤销。这是有意设计（快速清单：勾掉就清掉），但把"误勾"的代价设成了不可恢复。建议把 `CompletionDelay` 提到 10–15 秒、或加"已完成"折叠区、或保留最近 N 条完成记录。

### 16.6 本轮防护到位（10 项，不计入）

HICON 所有权全配对（`CreateDragBitmap:22-35`、`ExtractShellIconPixels:274-305`、`TryGetJumboIcon:546-573`、`GetFallbackIcon:574-589`）；图标写入 `{guid}.tmp` + `File.Move(overwrite:true)` + `finally` 删残留；`NoteStore.SavePortableAsync/SaveTodoAsync` 用 `WritePortableTemporaryAsync`（`FileMode.CreateNew`）+ `File.Replace`，`_gate` 串行；`LoadTodoAsync` 限制 64 MiB 并把 `JsonException` 包成 `InvalidDataException`；`NoteRenameTransaction.RenameAsync:6-49` 保存失败回滚文件、回滚失败时 `rebind(target)` 保证编辑器不指向旧路径占用者、`finally` 必 `endRename()`；`TodoWindow.FlushCoreAsync:285-296` 全 catch 返回 false 不会让闸门泄漏；`PortableDocumentCatalogChanges.IsInternalTemporary` 同时识别 `.tuckpane-document-<guid>.tmp` 与 Office `~RF<hex>.TMP`；`ShouldRefresh` 对已知便携名的 `Changed` 返回 false；全仓无 `GC.Collect`/`Thread.Sleep`/`AllocHGlobal`/`SystemEvents.*`，`DateTime.Now` 仅 2 处且都用于文件名戳；`_memoryCache` 读写都在 UI 线程，无并发写。

### 16.7 本轮未覆盖

`NoteWindow` 的编辑器交互（`Editor.Close()` 之后的 WebView2 生命周期）未逐行走查；`TodoWindow` 的拖拽排序（`TaskList_DragItemsCompleted`）与 `_completionTimer` 的竞态未验证；图标缓存的**实际**内存占用与加载耗时未测量（本机无 SDK，无法启动应用）；XAML 层 `AutomationProperties`、Tab 顺序、高对比度主题仍未覆盖。

---

## §17 第十轮增补（域 M：图标 P/Invoke 与启动序列）

> 详细证据与代码引用见 `findings/round10.md`。本轮范围：`App.xaml.cs`（248 行）、`SingleInstanceGuard.cs`（全文）、`StartupService.cs`（全文）、`IconCacheService.cs` 的 P/Invoke 段、`AppHost.InitializeAsync`。

### M1 [P3] `RepairMissingAlpha` 用"非纯黑"推断不透明，深色图标纯黑像素被留成透明

`IconCacheService.cs:633-650` 先扫一遍 alpha 通道，只要有一个非零就整张图放过；否则把"RGB 非全零"的像素 alpha 置 255，**纯黑像素一律跳过**。这条修复针对的是 `SHDefExtractIcon` 在部分 Shell 图标上返回全零 alpha 的已知现象，但判据"RGB 全零 = 背景"不成立：深色图标（黑色文件夹、暗色 `.ico`）里存在真实纯黑前景像素，它们与背景在 RGB 上不可区分，于是被永久留成 `alpha=0` → 图标上出现"洞"，能透过洞看到壁纸。

另一条路径 `TryExtractShellItemImage:470-540` 用 `GetDIBits` 拿到的 32 位 BGRA 同样经过这个函数，命中面不止一个。

顺带：`DrawIconPixels:591-631` 里 `byte[] pixels = new byte[size*size*4]`（已零初始化）后紧接 `Marshal.Copy(pixels, 0, bits, pixels.Length)`，把全零写进刚 `CreateDIBSection` 出来的 DIB —— 纯废操作，256×256 时每次多拷 256 KiB。

修法：不再二次推断 alpha（直接用 `DrawIconEx` 的结果），或至少区分"纯黑且四邻域皆背景"与"纯黑但邻域有前景"；删掉废拷贝。

### M2 [P3] `SHIL_JUMBO` 取不到时不回退，也不校验 `imageIndex`

`IconCacheService.cs:546-573`：

```csharp
if (SHGetImageList(SHIL_JUMBO, ref interfaceId, out IImageList imageList) < 0) return false;  // 不回退
int imageIndex = shellInfo.IconIndex & 0x00FFFFFF;                                            // 不校验
return imageList.GetIcon(imageIndex, ILD_TRANSPARENT, out icon) >= 0 && icon != IntPtr.Zero;
```

`SHIL_JUMBO`（256×256）在远程桌面会话、部分虚拟显示器驱动、系统图标缓存损坏时可能不可用。此时 `GetPreferredIcon:307-337` 直接落到 `GetFallbackIcon`——`SHGFI_LARGEICON` 的 **32×32**，再被 `NormalizeDockIcon` 拉到 Dock 尺寸，等于 8 倍放大，糊成色块。中间档 `SHIL_EXTRALARGE`（48）/`SHIL_LARGE`（32）都没试。

`IconIndex` 来自 `SHGetFileInfo(SHGFI_SYSICONINDEX)`，Shell 状态不一致时可能越界，越界索引传给 `IImageList::GetIcon` 是未定义行为。一行 `GetImageCount` 校验即可。

修法：`[SHIL_JUMBO, SHIL_EXTRALARGE, SHIL_LARGE]` 依次尝试 + 索引边界检查。

### M3 [P2] `StartupService.Apply` 无任何异常保护，注册表写失败会让整个应用起不来

`Services/StartupService.cs` 全文 26 行，无一处 try/catch：

```csharp
using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
if (!enabled) { key.DeleteValue(ValueName, throwOnMissingValue: false); return; }
string executable = Environment.ProcessPath ?? throw new InvalidOperationException(AppStrings.Get("StartupPathUnavailable"));
key.SetValue(ValueName, $"\"{executable}\" --startup", RegistryValueKind.String);
```

调用处 `AppHost.InitializeAsync`（`AppHost.cs:126`）也没有 try/catch——而紧邻的 `FolderContextMenu.RepairIfOwned` 与 `DesktopContextMenu.InitializeDesktop` **都包了 try/catch 并只记录日志**。唯独这一处没有。

可达失败路径：组策略禁用 `HKCU\...\Run` 写入（`UnauthorizedAccessException`）、用户配置单元损坏或被安全软件锁（`IOException`/`SecurityException`）、`Environment.ProcessPath` 为 null（`InvalidOperationException`）、磁盘满导致 hive 无法扩展（`IOException`）。

后果链：`InitializeAsync` 抛出 → `App.OnLaunched` 的 catch → `StartupFailure.Show(ex)` + `Exit()`。**用户看到"TuckPane 启动失败"，真实原因只是"开机自启"这个可选功能写不进去**——核心的桌面收纳功能与 `Run` 键毫无关系。

附带：`enabled == false` 时也执行 `CreateSubKey(writable: true)`，即"关掉开机自启"反而需要写权限；只读策略下这个本该什么都不做的分支也会抛。

修法（约 8 行）：先 `OpenSubKey(writable: true)`，为 null 且 `!enabled` 直接返回；整个方法包 try/catch 只记录；调用处再包一层。

### M4 [P3] 单实例 `Listen` 注册在 `InitializeAsync` 之后，快速二次启动会弹错误的"遗留实例"提示

`App.xaml.cs` `OnLaunched`：

```csharp
_host = new AppHost();
await _host.InitializeAsync();                        // 磁盘 IO、读 state、建窗口、建托盘
_singleInstance.Listen(() => _host.OpenConsole());    // 监听器在这里才挂上
```

二次启动侧 `SignalPrimary()` 会 Set 事件后等 ack（默认 4 秒）。在 `InitializeAsync` 未完成的窗口里，事件已 Set 但**没有等待者**，ack 永远不来 → 走满 4 秒超时 → `ShowLegacyInstanceMessage()` 弹出一个**内容完全错误**的对话框（说"检测到旧版 GlassFolder 正在运行"，实际是"新版还在启动"）。触发方式很日常：双击快捷方式没反应，再双击一次。

同文件另两处（潜在，当前不可达）：`Dispose()` 里 `ReleaseMutex()` 必须由拥有它的线程调用，而互斥体是 `initiallyOwned: true` 在 UI 线程创建的；当前 `App` 不实现 `IDisposable` 也从不 Dispose，一旦将来加"退出清理"就会踩 `ApplicationException`。

修法：`Listen` 提前到 `new AppHost()` 之前，回调只置 `_pendingActivation = true`，`InitializeAsync` 完成后补一次 `OpenConsole()`。

### M5 [P3] 图标 PNG 落盘无 fsync；并发未命中同一 `cachePath` 会互相覆盖

`IconCacheService.cs:209-238` 的 `RefreshAsync` 用 `FlushAsync()` + `MoveAsync(ReplaceExisting)`，没有 `FlushFileBuffers`。断电后可能留下"文件名正确、内容截断"的 PNG，下次 `BitmapImage.SetSourceAsync` 抛异常 → `GetIconCoreAsync` 的 catch 记录并返回 null → **该图标永久空白**，直到用户手动删缓存目录（`SweepAsync` 只删 1 小时以上的 `.tmp`，不校验已落盘 PNG）。

并发覆盖：`cachePath = Hash(identity)`，同一路径的两个并发请求算出同一个 `cachePath`，两个 `MoveAsync(ReplaceExisting)` 互相覆盖。当前被 L4 的 `SemaphoreSlim(1,1)` 掩盖——**修 L4 必须同时修 M5**。

修法：写完 `Flush(true)`/`FileOptions.WriteThrough`；读 PNG 失败时删文件重试一次。

### M6 [P3] 启动序列两处 fire-and-forget；启动即构造整个总控台窗口

`AppHost.cs:110-136`：`_ = IconCacheService.SweepAsync();`（内部整体 `catch { }`，实际安全）、`_ = Updates.CheckAsync(false);`（若抛出未捕获异常只走 `UnobservedTaskException`，`Program.Main` 只记录不崩溃 → 用户永远不知道"检查更新从来没成功过"；本机 `check.json` 里躺着上游 v4.2.1 的旧缓存正是现场证据）。

`Console = new ConsoleWindow(this); Console.InitializeHostWindow();` 在启动路径上构造完整总控台：2248 行 code-behind、整套 XAML 树、`_themeSurface`/`_settingsEdgeSurface` 两个合成表面、`_chrome`、六个 timer，并跑 `RestorePlacement`。即使这个会话从头到尾不开总控台，这些也全部发生。对"开机自启、常驻托盘"这个主要用法是纯浪费；静态审查无法量化到毫秒，只指出结构。

修法：`CheckAsync` 内部保证不抛并记录结果；`ConsoleWindow` 改为首次 `OpenConsole()` 时惰性构造（归入 H1 的重构范围）。

### 本轮防护到位（不计入）

`CommandLineToArgvW` 的 `LocalFree` 在 `finally`；`RedirectActivation` 在消息循环前阻塞不死锁；测试根下单实例键追加 SHA256 前 12 位，不干扰正式实例；`IsActivationExecutable` 拒绝非完全限定路径、catch 返回 false；`SingleInstanceGuard` 用独立 legacy mutex 而非猜名字；`SHDefExtractIcon` 的 `smallIcon` 成功失败都 `DestroyIcon`；`IShellLinkW`/`IPersistFile`/`IImageList`/`IShellItemImageFactory` 全在 `finally` 里 `FinalReleaseComObject`；HBITMAP `DeleteObject`、DIB `DeleteDC` 配对；`BuildCacheIdentity` 用 `HashSet` + 深度 4 防环并拒绝 `\\` 前缀；`NormalizeShortcutResourcePath` 做环境变量展开 + 相对路径 + 存在性校验；`RefreshAsync` 的 `finally` 删残留 tmp；`GetIconCoreAsync` 提取失败只记录返回 null。

### 未覆盖与局限

`SplashScreen` 在仓库中不存在（启动画面由 Windows App SDK 默认提供），无从审查；`AppUpdateService.CheckAsync` 内部已在 C1/C4/C5 覆盖，本轮未重读。**本机无 .NET SDK，M1–M6 全部为静态阅读结论，未编译、未运行、未在真机验证触发率**；M1/M2 的观感影响需真机截图定级，M3 的触发条件需受管机器复现。

---

## §18 第十一轮增补（域 O：更新链执行层）

审查对象是整条"下载并执行外部代码"的链路：`ReleaseClient.cs`（120 行）、`PortablePackage.cs`（158 行）、`UpdateFiles.cs`（60 行）、`TuckPane.Updater/Program.cs`（88 行）、`AppUpdateService.cs`（199 行）、`ConsoleWindow.Updates.cs`（45 行）、`AppHost.ExitCoreAsync`（90 行）、`UpdateWindowInput.cs`（26 行）。

**结论：没有发现 P2 及以上的缺陷。** 新增 4 条 P3，全部落在"错误呈现""失败后资源回收""RAII 半成品"三处，不涉及"会不会装错包/会不会被投毒"这两个核心问题。

### 18.1 O1 [P3] 更新页把原始 .NET 异常消息直接展示给用户

`AppUpdateService.CheckAsync` 的 catch 只把 `OperationCanceledException` 映射为本地化的 `UpdateTimeout`，其余一律 `Error = ex.Message`；构造函数里 `PreviousResult = $"{result.Status}: {result.Message}"`；`Program.cs` 的 `Report("failed", ex.Message)` 同样直出。结果是断网时用户看到 `The requested name is valid, but no data of the requested type was found. (api.github.com:443)`、限流时看到 `Response status code does not indicate success: 403 (rate limit exceeded).`、超时看到 `The operation was canceled.`、C9 场景看到带完整路径的 `Could not find file 'C:\Users\<用户名>\AppData\Local\TuckPane\state.json'.`。

更新页是用户唯一需要"看懂并据此决策"的界面（重试？手动下载？网络问题？），却是本地化最不彻底的一处。修法：`Error` 拆成 `ErrorKey` + `ErrorDetail`，按异常类型映射到本地化键，原文只进 `AppLogger` 与折叠的详情区。

### 18.2 O2 [P3] 取消更新后已下载的安装包不清理

`DownloadAndInstallAsync` 在 `exitWithHandoff` 返回 false 时只置 `StatusKey = "UpdateExitCancelled"`，已下载并通过哈希校验的包（上游 `setup.exe` 实测 302 605 692 字节）留在 `sessions\<guid>\` 里。唯一清理是构造函数里 `_ = Task.Run(CleanupSessions)`，而它只删 `LastWriteTimeUtc` 早于 1 小时的目录。于是"点安装 → 下 300 MB → 便签保存失败 → 退出被取消 → 再点一次 → 再下 300 MB"，旧包要等下次启动且超过 1 小时才消失。修法：`if (!exited)` 分支直接删会话目录。

### 18.3 O3 [P3] `Dispose()` 之后仍触发 `Changed`

`ExitCoreAsync` 在 `await handoff()` 返回后立刻 `Updates.Dispose()`，而 `DownloadAndInstallAsync` 的 `finally` 仍会 `Changed?.Invoke()` → `UpdatesChanged` → `TryEnqueue(RefreshUpdates)`。`_closingPermanently` 此时尚未置位（`Console.ClosePermanently()` 在更后面），所以回调真的会跑一次，读一个已释放的实例。当前 `RefreshUpdates` 只读缓存属性故不崩，但这是"靠字段恰好是缓存值"才成立的隐式契约。修法：`Dispose()` 置 `_disposed`，`Changed?.Invoke()` 前判一次，3 行。

### 18.4 O4 [P3] `UpdateWindowInput` 构造中途抛异常会让窗口永久禁用

构造函数边枚举边 `EnableWindow(handle, false)`；若某个窗口此刻还没有 HWND（`WindowNative.GetWindowHandle` 抛），异常从构造函数冒出 → 调用点 `frozen = new UpdateWindowInput(...)` 赋值不成立 → `finally` 的 `frozen?.Dispose()` 无事可做 → 已禁用的窗口永久失去输入，只能重启。窗口集合里包含 `_externalNoteWindows`/`_externalTodoWindows`（便携文档外部窗口），生命周期不由 `AppHost` 完全掌握，正是最可能出现该窗口期的一类。修法：两段式（先收集、后禁用）或构造函数内 `catch { RestoreAll(); throw; }`。

### 18.5 防护到位（18 项，不计入编号）

zip-slip 全维度拒绝（空白/`\`/`:`/前导 `/`/空段/`.`/`..`/尾点/尾空格/非法字符/保留设备名）→ 再 `Within` 二次确认 → 再 `NoLinks` 拒绝任何一级重解析点；清单条目数 3–6000、单条目 ≤2 MB、必须含 `TuckPane.exe` 与 `TuckPane.dll`；拒绝清单自身、`unins*`、`state.json*`、`.tucknote`、`.tucktodo`、`.WebView2/` 进包；拒绝重复键、未列条目、目录条目、符号链接条目；累计 ≤2.5 GB、条目 ≤6500；每文件解压后立即 SHA256 比对并与清单条数对账；拒绝降级/同版本；目标目录已有同名目录或用户文件占了新路径即拒绝；覆盖前复制进 backup 并写 `replacement-journal.json`、catch 逆序恢复；清单最后安装作"已提交版本"标记；下载边下边算 `IncrementalHash`、超长立即抛、结束再比对长度与哈希；`SHA256SUMS.txt` 与 `asset.digest` 双向交叉校验；资产 URL 必须全等 `github.com/daha1216/TuckPane/releases/download/<tag>/<name>` 且 `size ∈ (0, 1.5 GB]`；父进程身份三重校验（PID + `StartTime` ticks + `MainModule` 目录）；包哈希在交接前与 `WaitForExit` 后各校验一次；写权限探针前置；`ValidateTarget` 阻止程序目录与受保护数据目录重叠。

### 18.6 提权面与两处否证

- **提权面不成立**：`TuckPane.Updater.csproj` 无 `app.manifest`，`src/TuckPane/app.manifest:7` 为 `level="asInvoker"`，全仓无 `requireAdministrator`、无 `runas`。更新器以当前用户权限运行，不存在"构造 `UpdateRequest` 换取管理员代码执行"的本地提权路径；Inno 静默安装用 `/DIR=<target>` 装到用户可写目录，不触发 UAC。
- **`IsInstalledCopy()` 否证**：`FolderContextMenuService.IsInstalledCopy()`（`:79-88`）读 `HKCU\…\Uninstall\{2B7D4C50-…}_is1` 的 `InstallLocation` 与 `AppContext.BaseDirectory` 比较。本机 `D:\TuckPane` 有 `unins000.exe`/`unins000.dat`，说明该目录确实被 Inno 装过，判为 installed 是**正确**行为。它与 C2/J1 同根因（两渠道共用 AppId），但该函数本身无逻辑错误。
- **`.lnk`/`.url` 递归解析的路径伪造**：`TryReadShellLinkIconLocation` 与 `NormalizeShortcutResourcePath` 只用于"读图标"，不参与任何写入或执行决策，伪造图标路径的最坏结果是取到错误图标，不构成攻击面。

### 18.7 局限

未验证 Inno `/VERYSILENT /SUPPRESSMSGBOXES /NOCLOSEAPPLICATIONS /RESTARTEXITCODE=3010` 在"目标目录被占用"时的实际行为；未验证 `Process.Start` 在杀软拦截未签名 exe 时的表现（`TuckPane.Updater.exe` 未签名，见 §2）。两项都需要真机端到端演练，本机无 .NET SDK，无法进行。


## 附录 A：本机状态（4.2.4 未安装）

| 项 | 值 |
|---|---|
| 已安装版本 | 4.2.3（`FileVersion 4.2.3.0`，`ProductVersion 4.2.3+008779cbf2936e49966762cf91fb4ef91234c43d`） |
| `D:\TuckPane\TuckPane.exe` 时间戳 | 2026-10-20 18:05（CI 构建时间） |
| 运行中进程 | PID 10160，启动于 2026/10/24 23:55:48 |
| 安装方式 | Inno Setup（存在 `unins000.exe` / `unins000.dat`） |
| 数字签名 | 两个 exe 均 `NotSigned` |
| 安装目录文件数 | 358 个 / 310706592 字节 |
| 用户数据 | `%LOCALAPPDATA%\TuckPane`（state.json 10523 B）、`%USERPROFILE%\TuckPane`（Items/、Windows/） |

## 附录 B：4.2.4 发布物

| 资产 | 大小 |
|---|---|
| `SHA256SUMS.txt` | 203 B |
| `TuckPane-4.2.4-win-x64-portable.zip` | 129953875 B |
| `TuckPane-4.2.4-win-x64-setup.exe` | 303295438 B |

发布者：`github-actions[bot]`（CI 自动上传），tag `v4.2.4` → commit `7f58788`，发布时间 `2026-10-25T03:33:29Z`。

## 附录 C：本次审查未覆盖的部分

- 运行时行为（未运行程序，本机无 .NET 10 SDK 无法编译）
- 安装器实际安装/卸载流程（未执行）
- 更新流程端到端（未触发真实更新）
- 多显示器 / 高 DPI / 辅助功能实际表现
- 性能实测、内存实测（无 profiling 工具）
- 第三方依赖的实际行为（只核对了版本与许可文本）

## 附录 D：本轮我改写/删除了首轮的哪些结论

诚实记录，供后续核对：

| 首轮结论 | 问题 | 终版处理 |
|---|---|---|
| N1 "坏备份被固化"、"一次断电变全新用户" | 机制方向写反：备份是**自愈**的，单次故障能回退成功 | 改写为"只有双重故障才丢状态"，并补上"`:44` 无条件复制、不校验源文件完好"这个真正的可达原因 |
| N2 修法"检查 `_expandedOwner` 可见性" | `_expandedOwner` 三处构造全传 `IntPtr.Zero`，全是空操作 | 改写为 `RepairExpandedZOrder()` + `IsBehindDesktopLayer()` 触发 |
| N4 "保存失败无提示" | 主保存路径 `MainWindow.xaml.cs:7128-7138` 其实有 `ShowMessage` | 收窄为"AppHost 内 28 处裸调用无提示" |
| N9 "`File.Move` 跨卷退化为复制" | `.tmp` 与目标同目录，永不跨卷 | 整条删除 |
| N11 "假阳性（反复修）"分支 | owned 窗口被系统强制保持在 owner 之上，不会先遇到 `desktopRoot` | 删除该分支；保留假阴性，并说明"反复修"的真因是 A4/A9 让修复失效 |
| N12 定 P2 | 需三重低概率叠加 | 降为 P3（A2） |
| A1（独立审查定 P3） | 其自身已确证"菜单停留 8 秒即被抢 owner" | 升为 P2 |
| H1（独立审查定 P3） | 是 4 条 P2/P3 的共同根因 | 升为 P2 |

---

*报告完成时间：2026-10-25T17:20+08:00*
*审查方式：静态代码审查 + 只读环境核验 + 两轮独立审查交叉验证*
*未编译、未运行、未动态调试*



