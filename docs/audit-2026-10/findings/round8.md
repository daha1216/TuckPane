# 第八轮：文件操作执行层（StorageService + ShellDragService）

审查对象
- `src/TuckPane/Services/StorageService.cs`（618 行，全文）
- `src/TuckPane/Services/ShellDragService.cs`（733 行，全文）
- 关联：`MainWindow.xaml.cs:5460-5560`（拖出交接）、`AppHost.cs:1382`（导出）、`Core/DropValidator.cs:86-126`、`Core/AppPaths.cs:9-27`、`Core/ItemReorderSession.cs:12,97`

性质：静态审查（本机无 .NET SDK，未编译未运行）。

---

## K1 [P2] Ctrl+拖到桌面 = 复制意图被丢弃，实际执行移动

**证据链**
1. `ShellDragService.cs:447-456` `DesktopAwareDropSource.QueryContinueDrag`：松开左键且命中桌面时记录
   `DesktopCopyRequested = (keyState & MouseKeyControl) != 0;`
2. `ShellDragService.cs:52-61` `ShellDragResult` 第 3 个字段就是 `bool DesktopCopyRequested`，一路带出到调用方。
3. `MainWindow.xaml.cs:5494`、`:5508` 只判断 `result.Outcome == ShellDragOutcome.DesktopRequested`，**从不读 `result.DesktopCopyRequested`**，直接：
   ```csharp
   TransferOutcome moved = await _host.TransferQueue.RunAsync(
       token => _storage.MoveItemToDesktopAsync(dragPath, progress: null, token));
   ```
4. 全仓检索 `DesktopCopyRequested`：除 `ShellDragService.cs` 自身外**零引用**（`ItemReorderSession.cs:12,97` 只有 `DesktopRequested` 状态）。
5. 而配套的 `StorageService.CopyItemToDesktopAsync`（`StorageService.cs:270-282`）**全仓无调用者**——它存在的唯一理由就是这条 Ctrl 分支。

**后果**：用户在收纳窗里按住 Ctrl 把条目拖到桌面，系统光标给的是"复制"反馈（`GiveFeedback` 返回 `DRAGDROP_S_USEDEFAULTCURSORS`，由 OLE 按 Ctrl 画 + 号），松手后条目**从收纳窗消失**、被移动到桌面。用户以为桌面上是多出来的一份副本，实际收纳窗里那份已经没了。数据没丢（在桌面上），但"复制"语义被静默改写成"移动"，属于误导性破坏。

**修法**：`MainWindow.xaml.cs:5508` 分支按 `result.DesktopCopyRequested` 二选一，`CopyItemToDesktopAsync` 直接接上（它已经写好了）；或反过来，若产品决定不支持 Ctrl 复制，则 `QueryContinueDrag` 里不应再记录该位、`ShellDragResult` 不应暴露该字段，避免"看起来支持其实没接"。

---

## K2 [P3] 跨卷移动的"校验通过即删源"只比对文件名+长度，不比对内容

**证据**：`StorageService.cs:585-596` `VerifyEquivalent` → 比对 `TotalBytes` 与 `Entries.SequenceEqual(...)`；`BuildManifest:598-620` 的条目格式是 `D|相对路径` 和 `F|相对路径|长度`。

**后果**：`MovePathAsync`（`:420-449`）在跨卷/被占用降级路径上，`VerifyEquivalent` 通过后就 `PublishedMoveCompletion.Complete(..., () => { Directory.Delete(source, recursive: true); ... })` **删源**。校验只看"路径集合一致 + 每个文件长度一致"，同长度内容损坏（位翻转、复制期间源被并发改写后长度恰好相同、NTFS 压缩/稀疏文件）无法发现 → 静默丢数据。

**评价**：作为"防呆"够用，但它是全仓唯一一处"校验通过即删源"的闸门，而实现强度只是元数据比对。建议至少对小于阈值的文件做哈希，或改成"复制后由源端确认 mtime+size 未变"再删。

---

## K3 [P3] `CreateExecutableShortcut` 不释放 COM 对象（同文件另一个方法做对了）

**证据**：`StorageService.cs:316-337`
```csharp
Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw ...;
dynamic shell = Activator.CreateInstance(shellType)!;
dynamic shortcut = shell.CreateShortcut(destination);
shortcut.TargetPath = source;
shortcut.WorkingDirectory = Path.GetDirectoryName(source)!;
shortcut.Save();
```
`shell`（WScript.Shell）与 `shortcut`（WshShortcut）两个 RCW 都没有 `Marshal.FinalReleaseComObject`/`ReleaseComObject`。

**对照**：同一个文件里的 `AddItemShortcut`（`:110-118`）用 `NativeMethods.ShellLink` + `finally { Marshal.FinalReleaseComObject(instance); }`，`ShellDragService.TryInitializeDragImage`（`:378-406`）也在 `finally` 里 `FinalReleaseComObject(helper)`。

**后果**：批量导入多个 `.exe`（拖一批程序进收纳窗）时，每个文件泄漏 2 个 RCW，只能等 GC 终结器回收；`dynamic` 还会把每次属性赋值走一遍 IDispatch 晚绑定，比 `IShellLinkW` 慢一个量级。同文件内两套写法并存，说明是遗留而非有意。

---

## K4 [P3] 导出目录名与新建文件夹名的校验规则不一致

**证据**
- `ValidateNewFolderName`（`:158-173`）：查非法字符、长度 120、尾点/尾空格、以及 `CON/PRN/AUX/NUL/COM1-9/LPT1-9`（含 `name.Split('.')[0]` 处理 `CON.txt`）。
- `SanitizeName`（`:523-529`）：只把 `Path.GetInvalidFileNameChars()` 里的字符替换成 `_`，截断到 80，**不处理尾点、尾空格、保留设备名**。
- 使用点：`ExportToDesktopAsync:211` `Path.Combine(desktop, AppStrings.Format("ExportFolderFormat", SanitizeName(windowName)))`。

**后果**：窗口名是 `CON`、`PRN`、`a.`、`b ` 时，导出到桌面会 `Directory.CreateDirectory` 失败（`DirectoryNotFoundException`/`IOException`），整个导出返回 `TransferStatus.Failed`，错误信息是系统原文。而同样的名字在"新建文件夹"入口会被友好地拦住并提示 `FolderNameReservedError`/`FolderNameInvalidError`——同一套命名规则两处实现、一严一松。

**修法**：`SanitizeName` 复用 `ValidateNewFolderName` 的判定表（或抽一个 `FolderNameRules.TryNormalize`），把保留名映射成 `CON_`、尾点尾空格剥掉。

---

## K5 [P3] 空目录导出的 `Directory.Delete` 在 try 之外，TOCTOU 会抛出未统一处理的异常

**证据**：`StorageService.cs:195-201`
```csharp
if (!_exportEmptyDirectory && !Directory.EnumerateFileSystemEntries(_itemsRoot).Any())
{
    Directory.Delete(_itemsRoot);          // ← 在下面那个 try 之前
    return PublishedMoveCompletion.Complete(...) with { DestinationPath = null, ... };
}
```
下面的 `try { ... } catch (OperationCanceledException) ... catch (Exception ex) { return Failed }` 从 `:213` 才开始。

**后果**：判空与删除之间只要落进一个文件（拖放队列、OneDrive/坚果云同步、杀毒软件写临时文件），`Directory.Delete` 抛 `IOException`（目录非空）或 `UnauthorizedAccessException`，直接冒泡到 `AppHost.cs:1382` 的 `TransferQueue.RunAsync`，绕过"导出失败要显示 `TransferOutcome.Failed` + 人话消息"的统一路径。极端情况下 `TransferQueue` 的状态机会停在"进行中"。

**修法**：把这段挪进 try，或在 catch 里识别"目录非空"并降级为正常导出。

---

## K6 [P3] 目录刷新对每个文件读一次注册表

**证据**：`StorageService.ReadItems`（`:176-192`）对每个条目调用 `GetDisplayName(relativeName, kind)`；`GetDisplayName`（`:492-497`）对非文件夹/快捷方式/笔记类型走 `ExplorerShowsExtensions()`，而它（`:499-510`）每次都 `Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced")?.GetValue("HideFileExt")`。

**后果**：N 个文件的收纳窗，每次目录刷新 = N 次注册表打开 + N 次 `GetValue`。`RefreshCatalogAsync` 在每次拖放、每个文件系统变化、每次窗口激活后都会跑（`MainWindow.xaml.cs:603,659,885,4021,4053,5522...`）。100 个文件的收纳窗、一次拖放引发 3 次刷新 = 300 次注册表访问，全部在 `Task.Run` 的工作线程上串行完成，直接抬高目录刷新延迟。

**修法**：`ExplorerShowsExtensions()` 的结果在一次 `ReadItems()` 调用内缓存（局部变量传入 `GetDisplayName`），或按进程缓存 + 监听 `WM_SETTINGCHANGE`。

---

## K7 [P3] `ReadUnsupportedNames()` 恒返回空，整条"不支持的项目"提示链是死代码

**证据**
- `StorageService.cs:193` `public IReadOnlyList<string> ReadUnsupportedNames() => [];`
- `MainWindow.xaml.cs:972` 仍然 `Task.Run(() => _storage.ReadUnsupportedNames(), ...)`；
- `:1010-1015` `var unsupported = unsupportedNames.ToHashSet(...); string[] addedUnsupported = unsupported.Except(_lastUnsupported, ...).ToArray(); if (notifyUnsupported && addedUnsupported.Length > 0) ShowMessage(AppStrings.Format("UnsupportedItemsFormat", ...), Warning);`

**后果**：`notifyUnsupported` 参数、`_lastUnsupported` 字段、`UnsupportedItemsFormat` 字符串、以及每次刷新的那次 `Task.Run` 全部空转。`TryGetKind`（`DropValidator.cs:86-126`）对任何存在的文件都返回 `WidgetItemKind.File`，所以确实没有"不支持"的文件——但这意味着该功能是半成品：真正的不可显示条目（例如 `ReadItems` 里 `TryGetKind` 返回 false 的竞态消失文件、或未来新增的过滤类型）不会以任何方式告知用户，只会静默不出现在画布上。

**评价**：单看影响很小（当前无可触发场景），但它属于"代码里躺着一个永不成立的判断 + 一段永不执行的 UI 文案"，是维护陷阱：以后有人给 `ReadItems` 加过滤条件，会以为提示链还在工作。

---

## K8 [P3] 拖到桌面的目标路径绕过了测试沙箱

**证据**
- `AppPaths.cs:23-25`：`DesktopRoot = TestRoot is null ? Environment.GetFolderPath(SpecialFolder.DesktopDirectory) : Path.Combine(Path.GetFullPath(TestRoot), "Desktop")`；`:27` `IsTestMode => TestRoot is not null`。
- `ExportToDesktopAsync:205` 用的是 `AppPaths.DesktopRoot`（正确）。
- `MoveItemToDesktopAsync:258` 与 `CopyItemToDesktopAsync:272` 用的是 `Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)`（**绕过 `TestRoot`**）。

**后果**：设置 `TUCKPANE_TEST_ROOT` 跑稳定性/逻辑检查时，把条目拖到桌面会写到**真实用户桌面**而不是 `<TestRoot>\Desktop`，测试既污染真实桌面、又测不到隔离后的行为；同时两处"桌面在哪"的答案在测试模式下不一致，`ExportToDesktopAsync` 与 `MoveItemToDesktopAsync` 会落到两个不同目录。

**修法**：两处统一改用 `AppPaths.DesktopRoot`。

---

## 防护到位（本轮复核，不计入问题数）

| 项 | 证据 |
| --- | --- |
| OLE 初始化/反初始化严格配对 | `ShellDragService.cs:326-330` `OleInitialize` 失败即抛且不 `OleUninitialize`；`finally` 里 `OleUninitialize()` 与 `GC.KeepAlive(dropSource)` |
| 拖放 COM 对象释放完整 | `TryInitializeDragImage:401-405` `finally` 里 `FinalReleaseComObject`；`CreateDataObject:411-429` catch 里释放 `IShellItem`；`ShellDataObject.Dispose:617-630` 先 `Marshal.Release(Pointer)` 再释放 `_item`；`ShellDragSession.Dispose:658-672` 幂等且删除 HBITMAP |
| 取消路径不留半成品 | `PrepareAsync:571-593` 取消时用 `ContinueWith` 兜底删除已生成的 HBITMAP；`CopyOneAsync`/`MovePathAsync` 的 catch 都 `TryDelete(staging)` |
| 不覆盖已有文件 | `GetUniquePath`（`:480-491`）+ `File.Move(..., overwrite: false)` + `CopyOneAsync` 的 `FileMode.CreateNew` |
| 拒绝重解析点，防目录穿越 | `BuildManifest:598-620`、`CopyDirectoryAsync:543-566` 对每个目录/文件检查 `FileAttributes.ReparsePoint` 并抛 `UnsupportedReparsePathFormat` |
| 暂存文件不暴露给用户 | 统一 `.glassfolder-staging-<guid>` 前缀，`ReadItems:181-186` 显式跳过，`PortableDocumentCatalogChanges.IsInternalTemporary` 二次过滤 |
| 共享冲突降级不丢源 | `MovePathAsync:391-397` 仅当源仍存在且目标不存在时才降级为"复制+校验+保留源"，并在 `:441-449` 用 `PublishedMoveCompletion` 才删源；取消时返回 `MoveSourceRetained` |
| 设备名/非法字符校验 | `ValidateNewFolderName:158-173` 覆盖 `CON`…`LPT9`（含带扩展名写法）、尾点、尾空格、`Path.GetInvalidFileNameChars()` |

---

## 未覆盖（诚实声明）

- `PortableDocumentCatalogChanges`、`PublishedMoveCompletion`、`DropValidator.ValidateBatch` 的完整实现只做了调用点核对，未逐行审。
- `IconCacheService.CreateDragBitmap` 的 GDI 句柄所有权只确认了"调用方在 `ShellDragSession.Dispose` 里 `DeleteObject`"，未审其内部是否还有第二份句柄。
- 本轮全部结论为静态阅读，**没有编译、没有运行**。
