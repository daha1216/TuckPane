# 第九轮审查：图标缓存与便签/待办窗口

审查对象：`Services/IconCacheService.cs`（746 行）、`TodoWindow.xaml.cs`（919 行）、`NoteWindow.xaml.cs`（830 行）、`Services/NoteStore.cs`（482 行）、`Core/TodoRules.cs`、`Core/NoteRenameTransaction.cs`、`Core/PortableDocumentCatalogChanges.cs`，以及图标加载调用点 `MainWindow.xaml.cs:1634-1662`。

**局限（必须随结论一起引用）**：本机无 .NET SDK，全部结论来自静态阅读，**没有编译、没有运行、没有做性能测量**。下面所有"耗时/开销"都是基于代码结构的推断，不是实测数字。

---

## 9.1 L1 [P3] 待办完成动画用 20 Hz 轮询整张表

`TodoWindow.xaml.cs:87-90`：

```csharp
_completionTimer = DispatcherQueue.CreateTimer();
_completionTimer.Interval = TimeSpan.FromMilliseconds(50);
_completionTimer.IsRepeating = true;
_completionTimer.Tick += CompletionTimer_Tick;
```

`CompletionTimer_Tick`（`:479-496`）每个 tick 做两件事，第二件是正事（把过期行移除），第一件是动画：

```csharp
DateTimeOffset now = DateTimeOffset.UtcNow;
foreach (TodoRow row in _rows) row.RefreshVisual(now);
```

`TodoRow.RefreshVisual`（`:900-905`）：

```csharp
internal void RefreshVisual(DateTimeOffset now)
{
    Opacity = TodoRules.GetOpacity(Task, now);
    OnPropertyChanged(nameof(Done));
    OnPropertyChanged(nameof(TextDecorations));
    OnPropertyChanged(nameof(UndoVisibility));
}
```

问题有三层：

1. **频率**：50 ms = 20 Hz，而淡出总时长只有 `FadeDuration = 260 ms`（`Core/TodoRules.cs:8`）。也就是说整段动画只需约 6 帧，却按 20 Hz 跑满 `CompletionDelay = 3 s` 的全程。
2. **范围**：`foreach (TodoRow row in _rows)` 不带任何过滤。**未完成的行也每 tick 刷一遍**，而 `GetOpacity` 对未完成行恒返回 `1`（`TodoRules.cs:41`），`Done`/`TextDecorations`/`UndoVisibility` 也不会变 —— 全是空刷新。
3. **代价**：每行每 tick 一次 `Opacity` 赋值（依赖属性，走属性系统 + 合成器）+ 3 次 `PropertyChanged`（各触发一次绑定求值）。N 行 × 20 Hz × 4 次刷新 = 80·N 次/秒的绑定活动，全部发生在 UI 线程，持续 3 秒。

一个 200 条的待办清单，勾选一项后 3 秒内会产生约 16000 次绑定刷新，只为把一行从 1 淡到 0。列表越长越卡，且卡顿发生在"勾选完成"这个最需要即时反馈的动作上。

**建议**：把淡出交给 `Storyboard`/`CompositionTarget.Rendering` 只驱动"正在淡出的那一行"，或至少让 `RefreshVisual` 只在 `GetOpacity` 返回值与当前 `Opacity` 不同时才赋值并抛事件：

```csharp
double next = TodoRules.GetOpacity(Task, now);
if (Math.Abs(next - Opacity) < 0.001) return;
Opacity = next;
```

并把 `_completionTimer` 的间隔从 50 ms 提到 100–150 ms（3 秒窗口内仍有 20–30 次检查，过期判定精度足够）。

---

## 9.2 L2 [P3] `TodoWindow_Closed` 释放 `_saveGate`，在飞行的保存会在 `Release()` 上抛异常

`TodoWindow_Closed`（`:783-790`）：

```csharp
_saveTimer.Stop();
_completionTimer.Stop();
_chrome?.Dispose();
_chrome = null;
_saveGate.Dispose();
```

而 `FlushAsync`（`:258-264`）与 `BeginDirectoryRenameAsync`（`:266-277`）都是"先 `await _saveGate.WaitAsync()`，再在 `finally`/失败分支里 `_saveGate.Release()`"的结构：

```csharp
internal async Task FlushAsync()
{
    _saveTimer.Stop();
    await _saveGate.WaitAsync();
    try { await FlushCoreAsync(); }
    finally { _saveGate.Release(); }
}
```

关闭窗口时若有一次 `FlushAsync` 正卡在 `FlushCoreAsync()` 的 `await _store.SaveTodoAsync(...)` 上（磁盘慢、网络盘、杀毒软件扫描都会拉长这个 await），`Closed` 会先执行到 `_saveGate.Dispose()`；随后那次保存恢复执行，`finally` 里的 `Release()` 抛出 `ObjectDisposedException`。这条异常发生在 `async void`（`_saveTimer.Tick += async (_, _) => await FlushAsync();`）里，最终落到 `App.UnhandledException`（`App.xaml.cs:102-109`）被 `args.Handled = true` 吞掉（见 F6）—— 用户看不到，日志里可能只有一行。

同一文件里 `_saveTimer` 的 Tick 是 `async void`，`FlushAsync` 又是 `internal async Task`，两者叠加使这个窗口的关闭时序没有可观测的完成点。

**建议**：`Closed` 里不要 `Dispose()` 信号量（`SemaphoreSlim` 不持有非托管资源，不 Dispose 的代价只是不释放一个 `WaitHandle`，而 `WaitHandle` 是惰性创建的，这里从未创建）；或者改成"先 `await` 一个 `_closed` Task 再 Dispose"。对照 `NoteWindow_Closed`（`:816-823`）就没有 Dispose 序列对象，是正确的写法。

---

## 9.3 L3 [P3] 图标身份每次调用都重算，内存缓存命中也不例外

`IconCacheService.GetIconCoreAsync`（`:100-142`）的骨架：

```csharp
AppPaths.EnsureCreated();
string key = Path.GetFullPath(path);
string identity = await Task.Run(() => BuildCacheIdentity(key));
string memoryKey = dockPresentation ? $"dock-visible-v1|{key}" : key;
string cachePath = Path.Combine(AppPaths.IconCacheRoot, $"{Hash(identity)}.png");
if (!File.Exists(cachePath)) await RefreshAsync(...);
...
_memoryCache[memoryKey] = (identity, image);
```

`identity` 只用来算磁盘缓存文件名，**和内存缓存键（`memoryKey`）无关**。但它在 `_memoryCache` 命中之前就被算出来了，而且是 `Task.Run` 到线程池上算的 —— 每个图标每次请求都要付一次线程池调度 + `File.Exists` + `FileInfo.Length` + `LastWriteTimeUtc` 的开销；`.lnk` 还要递归解析目标（最多 `MaximumShortcutIconDepth = 4` 层），`.url` 要读 ini。

`BuildCacheIdentity`（`:150-199`）对目录返回 `$"{fullPath}|directory|0|{directory.LastWriteTimeUtc.Ticks}"`。目录的 `LastWriteTimeUtc` 在**任何**子项增删改名时都会变，所以一个收纳窗目录里每拖进/拖出一个文件，这个目录的图标身份就变一次 → `cachePath` 变 → 重新 `RefreshAsync` 提取并写一个新的 PNG。旧 PNG 不会被删，因为 `SweepAsync`（`:44-86`）只在**启动时**跑一次（`MaximumCachedIcons = 4096`、`MaximumCacheBytes = 64MB` 的裁剪也只在那时生效）。

**后果**：长期运行的会话里，磁盘缓存单调增长到下次重启才被裁剪；目录图标在每次内容变化后都要重新走一遍 shell 提取。

**建议**：把 `identity` 的计算挪到内存缓存未命中之后；目录身份改用"目录自身存在性"而不是 `LastWriteTimeUtc`（目录图标本来就不反映内容）；把 `SweepAsync` 改成启动一次 + 每 N 次写入触发一次轻量裁剪。

---

## 9.4 L4 [P3] 图标加载被全局串行化

唯一调用点 `MainWindow.xaml.cs:1634-1662`：

```csharp
private async Task<BitmapImage?> GetIconThrottledAsync(...)
{
    await _iconLoadGate.WaitAsync();
    try { return await _iconCache.GetIconAsync(...); }
    finally { _iconLoadGate.Release(); }
}
```

`_iconLoadGate` 是 `SemaphoreSlim(1, 1)`。这意味着**整个进程同一时刻只有一个图标在加载**：身份计算、可能的 shell 提取（`ExtractShellIconPixels` 走 `SHGetFileInfo`/`IImageList`）、PNG 解码、`BitmapImage.SetSourceAsync` 全部串行。

图标之间没有任何顺序依赖，也没有共享可变状态（`_memoryCache` 是 `Dictionary`，但写入在 UI 线程恢复后发生，且 `GetIconCoreAsync` 的 await 之后回到 UI 上下文）。串行化的唯一效果是把"并行 4 个 200 ms"变成"串行 4 个 200 ms"。

展开一个 30 项的收纳窗、冷缓存时，这就是 30 次串行往返；每次还叠一个 `Task.Run` 身份计算（L3）。这是"展开动画期间界面发涩"的一个结构性原因。

**建议**：把并发度放到 3–4（`SemaphoreSlim(4, 4)`），或者干脆去掉闸门、只保留 `_memoryCache` 的并发保护（`ConcurrentDictionary` + `Lazy<Task<...>>` 去重同一 key 的并发请求）。

---

## 9.5 L5 [P3] 待办"勾选即删除"：3 秒后不可恢复，且没有已完成区

`Core/TodoRules.cs`：

```csharp
internal static readonly TimeSpan CompletionDelay = TimeSpan.FromSeconds(3);
```

`CompletionTimer_Tick`（`:479-496`）到点后：

```csharp
foreach (TodoRow row in expired)
{
    _rows.Remove(row);
    _document.Tasks.Remove(row.Task);
}
if (expired.Length > 0) ScheduleSave();
```

`TodoWindow` 构造函数里还会先跑一次 `RemoveExpired`（`removedExpired` → `ScheduleSave()`），所以**上次关窗口前 3 秒内勾掉的任务，下次打开时已经不在了**。

也就是说：勾选 = 3 秒后永久删除。唯一的挽回手段是那 3 秒内点行内的"撤销"（`UndoVisibility => Done ? Visible : Collapsed`）。`PortableTodoDocument` 里没有任何"已完成/归档"字段，磁盘上也留不下痕迹。

这显然是有意的设计（快速清单：勾掉就清掉），但它把"误勾"的代价设成了不可恢复，而且 3 秒窗口对"勾完就切走"的用法基本等于没有。同一份文档在别处被打开（另一台机器、便携 U 盘）时，`RemoveExpired` 一样会执行 —— 删除是不可逆的。

**建议**（按代价从低到高）：
1. 把 `CompletionDelay` 提到 10–15 秒，给撤销留出真实可用的窗口；
2. 增加"已完成"折叠区，勾选只移动不删除，删除交给用户显式操作；
3. 至少在 `PortableTodoDocument` 里加一个 `Completed` 数组，保留最近 N 条。

---

## 9.6 防护到位（不计入问题数）

| # | 模块 | 结论 |
|---|------|------|
| 1 | HICON 所有权 | `CreateDragBitmap:22-35`、`ExtractShellIconPixels:274-305` 均在 `finally` 里 `DestroyIcon`；`GetFallbackIcon:574-589` 返回自有 HICON 由调用方销毁；`TryGetJumboIcon:546-573` 先 `DestroyIcon(shellInfo.Icon)` 再用 `IImageList.GetIcon`，`finally` 里 `FinalReleaseComObject(imageList)` |
| 2 | 图标写入原子性 | `RefreshAsync:209-238` 写 `{guid}.tmp` 后 `File.Move(overwrite: true)`，`finally` 删残留 tmp |
| 3 | 便携文档写入原子性 | `NoteStore.SavePortableAsync:243-262` 与 `SaveTodoAsync:289-308` 都是 `WritePortableTemporaryAsync`（`FileMode.CreateNew`）+ `File.Replace`，`_gate` 串行，`finally` 删残留 |
| 4 | 便携文档读取防御 | `LoadTodoAsync:264-288` 限制 64 MiB，`JsonException` 包成 `InvalidDataException`；`ValidateTodoDocument` 校验结构 |
| 5 | 重命名事务 | `NoteRenameTransaction.RenameAsync:6-49` 保存失败回滚文件、回滚失败时 `rebind(target)` 保证编辑器不指向旧路径占用者，`finally` 必 `endRename()` |
| 6 | 保存闸门平衡 | `TodoWindow.FlushCoreAsync:285-296` 全 catch 返回 false，不会让 `BeginDirectoryRenameAsync` 的 `_saveGate` 泄漏（L2 是关闭时序，不是泄漏） |
| 7 | 内部临时文件过滤 | `PortableDocumentCatalogChanges.IsInternalTemporary` 同时识别 `.tuckpane-document-<guid>.tmp` 与 Office 的 `~RF<hex>.TMP`，`ReadItems` 显式跳过 |
| 8 | 文件监听去抖 | `ShouldRefresh` 对已知便携名的 `Changed` 返回 false（图标/名字不依赖正文），重命名/删除仍会重建目录 |
| 9 | 跨切面扫描 | 全仓无 `GC.Collect`/`GC.WaitForPendingFinalizers`、无 `Thread.Sleep`、无 `AllocHGlobal`/`AllocCoTaskMem`、无 `SystemEvents.*`；`DateTime.Now` 仅 2 处且都用于文件名戳 |
| 10 | 图标缓存并发 | `_memoryCache` 的读写都在 UI 线程（`await` 后回到 UI 上下文），无并发写风险；`SweepAsync` 与 `RefreshAsync` 操作不同文件 |

---

## 9.7 本轮未覆盖

- `NoteWindow.xaml.cs` 的编辑器交互（`Editor.Close()` 之后的 WebView2 生命周期）只看了 `Closed` 与 `FlushAsync`，未逐行走查；
- `TodoWindow` 的拖拽排序（`TaskList_DragItemsCompleted`）只确认了"重建 `_document.Tasks` + `ScheduleSave`"，未验证与 `_completionTimer` 的竞态；
- 图标缓存的**实际**内存占用与加载耗时未测量（本机无 SDK，也无法启动应用）；
- XAML 层的 `AutomationProperties`、Tab 顺序、高对比度主题仍未覆盖。
