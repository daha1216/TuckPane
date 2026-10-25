# 第五轮审查：UI 层状态机（展开/收起、模式切换、DPI 与显示器拓扑）

- 审查对象：`fork/`，`origin/main` = `7f58788 Release TuckPane 4.2.4`
- 审查方式：纯静态阅读 `MainWindow.xaml.cs`（7997 行）+ 6 个分部文件 + `ExpansionModeCoordinator.cs` + `AppHost.cs` 相关方法
- 结论：**2 条 P3，无 P2**。UI 状态机的守卫密度明显高于我预期；问题集中在"过渡被取代时的状态复位"这一类，以及"DPI/显示器变化只有 Dock 有处理"。

---

## R5-1 [P3] 过渡在版本号自增之后早退，`_animating` 与悬停放大都不会复位

### 机制

`ExpandTransitionAsync`（`src/TuckPane/MainWindow.xaml.cs:1665-1835`）与 `CollapseTransitionAsync`（`:1837-1930`）都是这个结构：

```csharp
long requestVersion = ++_transitionRequestVersion;   // ← 先自增，并取消上一轮过渡
_transitionCancellation?.Cancel();
bool IsCurrent() => IsCurrentTransitionRequest(requestVersion, modeCancellation);
... 若干 await ...
CancellationTokenSource transition = StartTransition(modeCancellation);  // ← 这里才 ResetHoverWave()
_expanded = true;
_animating = true;
try { ... }
finally
{
    if (IsCurrent() && _transitionCancellation == transition && !transition.IsCancellationRequested)
    {
        _animating = false;      // ← 只有"仍为当前请求"才复位
        ...
    }
}
```

**版本号自增之后、`StartTransition` 之前，一共有 8 个早退点**：

| # | 位置 | 条件 |
|---|------|------|
| 1 | `:1678` | Station 分支 `if (!_stationReady) return;` |
| 2 | `:1691` | Station 分支 `if (!IsCurrent() \|\| !_stationVisible) return;` |
| 3 | `:1710` 后 | `await _host.PrepareToExpandAsync(this, IsCurrent);` 之后 `if (!IsCurrent() \|\| ...) return;` |
| 4 | `:1745` | `if (!IsCurrent()) return;` |
| 5 | `:1798` | 进入 try 前的 `if (!IsCurrent()) return;` |
| 6 | `:1810` | `catch (OperationCanceledException) { ...; return; }`（`finally` 仍会跑，但 `IsCurrent()` 已为 false） |
| 7 | `:1847` | Collapse：`await _host.CollapseContainedChildrenAsync(...)` 之后 `if (!IsCurrent() \|\| StaysExpanded \|\| ...) return;` |
| 8 | `:1905` | Collapse：`catch (OperationCanceledException)` |

任一命中时：
- 被取代的那一轮过渡，其 `finally` 因 `IsCurrent() == false` 跳过 `_animating = false`；
- 新的一轮过渡并没有建立（它在 `StartTransition` 之前就返回了）；
- → **`_animating` 停在 `true`**，`_transitionProgress` 停在中间值。

### 后果

1. **`IsExpanded` 会对宿主说谎**：`internal bool IsExpanded => _expanded || _animating;`（`:7145`）。展开失败时 `_expanded` 仍是 `false`，但 `IsExpanded` 返回 `true` → 宿主把它当成"已展开的对等窗口"，`PrepareToExpandAsync`（`AppHost.cs:1614-1626`）会把它列进 `unrelated` 并对它调 `CollapseForPeerAsync()`；`ReconcileExclusiveExpansionAsync` 也会把它当成候选。视觉上它还是紧凑卡片，逻辑上已经"展开"。
2. **外部点击收起失效**：`ApplyOutsideClickSetting`（`:7180-7188`）要求 `_expanded && !_animating` 才 `_outsideClickHook.Start()`。卡住后点空白处收不起来，只能点标题栏或热键。
3. **交互全被挡住**：`:2348`（条目重排）、`:2375`、`:2441`、`:2985`、`:2996`、`:3756`、`:4166`、`:6047`、`:6179`、`:6209`、`:6959`、`:7027` 都以 `_animating` 为前置条件。
4. **与作者自列的已知问题吻合**：`StartTransition` 里第一句是 `ResetHoverWave()`（`:1976`）。早退路径不经过 `StartTransition`，所以悬停放大不会被复位 → 收起动画被打断后，卡片上方残留一块放大的名字胶囊（4.2.4 RELEASE_NOTES「已知问题 ②」原文）。**这条已知问题很可能就是这个早退路径的可见症状**，不是独立的渲染 bug。

### 可达性（诚实评估）

不是永久卡死：`SetRuntimeVisible(false)`（`:7406`）、`SetContained(true)`（`:7481`）、`ShowCompactPlacement`（`:7699`）、`Restore`（`MainWindow.ExpansionMode.cs:108`）、`RecoverFailedTransition`（`:1940`）都会显式清 `_animating`；用户手动点标题栏再收起一次也会走一遍完整过渡把状态带回。所以影响是"**一次交互周期内窗口看起来僵住，且宿主状态判断出错**"，不是"必须重启"。

真正的触发窗口是 `await _host.PrepareToExpandAsync(...)` / `await _host.CollapseContainedChildrenAsync(...)` 这两段——它们内部会 `await` **其它窗口**的 `CollapseForPeerAsync()`（`AppHost.cs:1623`、`:1644`），耗时可达数十毫秒到数百毫秒。在这段等待里，只要用户又点了一下别的收纳窗（独占展开会把本窗口再收一次），或宿主做了批量收拢，本窗口的请求就会被取代。

### 建议修法（约 15 行）

在 `StartTransition` 之前的所有早退路径统一走一个 `AbandonTransition(requestVersion)`：

```csharp
private void AbandonTransition(long requestVersion)
{
    if (requestVersion != _transitionRequestVersion) return;   // 已被更新的请求接管，不要抢
    _animating = false;
    _transitionVelocity = 0;
    _transitionProgress = _expanded ? 1 : 0;
    ResetHoverWave();
    _host.NotifyDockOpenStateChanged();
    ApplyOutsideClickSetting();
}
```

另外给 `_animating` 加一个看门狗（例如 3 秒未归零就 `RecoverFailedTransition`），比逐个补早退点更稳。

---

## R5-2 [P3] DPI / 显示器拓扑变化只处理 Dock，其余收纳窗不重算尺寸与位置

### 证据

全仓只有一处处理这两个消息（`src/TuckPane/MainWindow.xaml.cs:3106-3110`）：

```csharp
if (message is 0x02E0 or 0x007E) // WM_DPICHANGED / WM_DISPLAYCHANGE
    ResetHoverWave();
if (IsDock && message is 0x02E0 or 0x007E)
    DispatcherQueue.TryEnqueue(() => { if (!_closing) { _dockRegionKey = null; ApplyDockLayout(); } });
```

- `grep -rn "DisplaySettingsChanged\|SystemEvents\|WM_SETTINGCHANGE" src/` → **零命中**，没有宿主级监听。
- `WM_DPICHANGED` 的 `lParam`（系统建议的新窗口矩形）**完全没有被读取**，子类化过程返回 `IntPtr.Zero`。
- `_compactBounds` 是 `NativeMethods.RECT`（物理像素），`ApplyCompactScale()` 只在 `:546`、`:654`、`:7534`、`:7644`、`:7651` 被调用，都不在 DPI 变化路径上。
- `DisplayPlacementService.ForBounds(...)` 只在过渡开始时用一次（`PrepareTransitionAnchor`），用于把坐标换算成 DIP。

### 后果

1. 把收纳窗从 100% 显示器拖到 150% 显示器：`WM_DPICHANGED` 到达，代码只 `ResetHoverWave()`。窗口外框仍是原来的物理像素尺寸，而 WinUI 内容按新缩放比重排 → 紧凑卡片与展开面板的**视觉尺寸和窗口外框不再匹配**，直到下一次展开/收起过渡重新 `ApplyBounds`。
2. 拔掉副屏：位于副屏上的 Floating/Positioned 收纳窗保留绝对坐标，落在已不存在的区域。Windows 会把窗口"搬"回主屏，但应用侧 `_compactBounds` 没更新 → 之后的碰撞检测、拖放目标判定、`CalculateExpandedBounds(_compactBounds)` 都基于过期矩形。
3. `DisplayPlacementService.GetDisplay` 的"找不到就回退主屏"只在**启动加载**时生效，运行期没有等价路径。

### 建议修法（约 20 行）

在 `GestureWindowProc` 里对 `WM_DPICHANGED` 增加：

```csharp
if (message == 0x02E0)
{
    DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing) return;
        ApplyCompactScale(repositionWindow: !_expanded && !_animating);
        if (_expanded && !_animating) ApplyBounds(CalculateExpandedBounds(_compactBounds), show: true);
        else if (!_animating) ApplyBounds(_compactBounds, show: true);
    });
}
```

并在 `AppHost` 里挂一次 `Microsoft.Win32.SystemEvents.DisplaySettingsChanged`（记得在 `Dispose` 里退订），对全部窗口做一次 `ClampToVisibleArea()`。

---

## 本轮判定为"防护到位"的部分（证据）

| 检查项 | 证据 |
|--------|------|
| 展开模式协调器的生命周期 | `ExpansionModeCoordinator.ExecuteAsync` 的 `finally` 在 `ReferenceEquals(_latest, request)` 时必调 `view?.End()`；`Restore()` 显式清 `_animating`/`_transitionVelocity`（`MainWindow.ExpansionMode.cs:103-138`）。`_changingExpansionMode` 不会泄漏。 |
| 悬停资源释放 | `DisposeHoverWave()` 退订 3 个指针事件 + `ViewChanged`/`StateChanged`/`LayoutUpdated`/`AnimationsEnabledChanged` + 逐项 `SizeChanged` + `_hoverPointerGuard.Tick`，并把两个图层从 `ExpandedContentLayer` 移除。 |
| 外部点击钩子配对 | `Start` 只在 `:7185`（`_expanded && !_animating && !_changingExpansionMode && CollapseOnOutsideClick`），`Stop` 在 `:1848`、`:1947`、`:7187`、`:7660`、`:7695`，`Dispose` 在 `:7100`。 |
| 定时器总数与关闭路径 | 共 10 个；`MainWindow_Closed` 停 8 个，`_hoverPointerGuard` 由 `DisposeHoverWave()` 处理，`_itemTouchHoldTimer` 已在 PR #2 补停。 |
| 设置界面初始化守卫 | `ConsoleWindow.xaml.cs:54-69` 的 15 个 `_loading*` 布尔，5 个 `ValueChanged` 处理器全部带守卫。 |
| 滑块防抖冲刷 | `ConsoleWindow.SliderPersistence.cs:43-57` 在关闭前停 6 个计时器并逐个保存。 |
| 存储目录校验 | `AppPaths.ValidateCustomStoragePath`（`:143-172`）拒绝 UNC、网络驱动器、根目录、用户目录/桌面/文档/下载/TuckPane 数据根，且要求目录已存在。 |

---

## 未覆盖

- 没有真机跑过任何交互（本机无 .NET SDK，无法编译）。
- 触摸/笔输入路径、`Dock` 多屏拼接、`Station` 中转站的拖放状态机只做了静态扫读，没有逐行推演。
- 动画 `RunVisualTransitionAsync` 的帧调度（`CompositionTarget.Rendering` vs `DispatcherQueueTimer`）未展开。
