# 第十一轮：更新链执行层（域 N）

审查对象（对二开而言风险最高的一条链——它会下载并执行外部代码）：

| 文件 | 行数 | 职责 |
| --- | --- | --- |
| `src/TuckPane.Updates/ReleaseClient.cs` | 120 | 解析 GitHub Release、下载并校验 |
| `src/TuckPane.Updates/PortablePackage.cs` | 158 | 清单校验、解包、备份、原子替换、回滚 |
| `src/TuckPane.Updates/UpdateFiles.cs` | 60 | 路径守卫、哈希、JSON 原子写、配置备份 |
| `src/TuckPane.Updater/Program.cs` | 88 | 独立更新器进程：握手、静默安装/替换、拉起新版本 |
| `src/TuckPane/Services/AppUpdateService.cs` | 199 | 检查/下载/交接编排、会话清理 |
| `src/TuckPane/ConsoleWindow.Updates.cs` | 45 | 更新页 UI |
| `src/TuckPane/AppHost.cs`（`ExitCoreAsync`） | 90 | 退出交接与落盘顺序 |
| `src/TuckPane/Services/UpdateWindowInput.cs` | 26 | 交接期间禁用窗口输入 |

**方法**：全文逐行阅读上述 8 个文件；对每一条外部输入（GitHub JSON、zip 条目、清单、`request.json`、`result.json`）做"谁能控制它、能控制到什么程度、最坏结果是什么"的推演；核对提权面；核对失败路径的恢复语义。**无 .NET SDK，未编译、未运行**，全部为静态审查。

---

## N1 [P3] 更新页把原始 .NET 异常消息直接展示给用户

**位置**

- `Services/AppUpdateService.cs:66-71`（`CheckAsync` 的 catch）：
  ```csharp
  catch (Exception ex)
  {
      StatusKey = "UpdateCheckFailed";
      Error = ex is OperationCanceledException ? AppStrings.Get("UpdateTimeout") : ex.Message;
  }
  ```
- `Services/AppUpdateService.cs:23-30`（构造函数读上一次结果）：
  ```csharp
  PreviousResult = result.Status == "launched" && result.Version == CurrentVersion
      ? AppStrings.Get("UpdatePreviousSuccess")
      : $"{result.Status}: {result.Message}";
  ```
- `src/TuckPane.Updater/Program.cs:83-87`：`catch (Exception ex) { Report("failed", ex.Message); return 1; }`

**机制**：只有 `OperationCanceledException` 被映射成本地化的 `UpdateTimeout`；其余全部原样透出。而这条链上会抛的异常几乎都带英文原文与内部细节：

| 触发 | 用户看到的内容 |
| --- | --- |
| 断网 / DNS 失败 | `The requested name is valid, but no data of the requested type was found. (api.github.com:443)` |
| GitHub 返回 403（限流） | `Response status code does not indicate success: 403 (rate limit exceeded).` |
| 资产缺失 | `Required release asset is missing or duplicated: TuckPane-4.2.4-win-x64-setup.exe` |
| 校验失败 | `Package checksum or length mismatch.` |
| 更新器超时 | `The operation was canceled.`（`Program.cs` 的 3 分钟 CTS 触发，经 `Report("failed", …)` 落到 `PreviousResult`） |
| 备份失败（C9 场景） | `Could not find file 'C:\Users\<用户名>\AppData\Local\TuckPane\state.json'.` |

`UpdateErrorText`（`ConsoleWindow.Updates.cs:33-34`）与 `UpdateRecoveryText`（`:36-37`）都是直接 `Text = …`，没有任何过滤或映射。

**为什么算问题**：更新页是用户唯一需要"看懂并据此决策"的界面（要不要重试、要不要手动下载、是不是网络问题）。当前实现里除超时外全部是英文原文，且夹带 URL、HTTP 状态码、绝对路径——对本项目的三语本地化（`AppStrings` 429 键）是明显缺口，也让"更新失败"变成无法自助诊断的黑箱。

**修法**：把 `Error` 拆成 `ErrorKey`（本地化）+ `ErrorDetail`（原文，折叠在"详细信息"里）。`AppUpdateService` 里按异常类型映射：`HttpRequestException` → `UpdateNetworkError`、`TaskCanceledException` → `UpdateTimeout`、`IOException`/`UnauthorizedAccessException` → `UpdateFileAccessError`、其余 → `UpdateUnknownError`，原文只进 `AppLogger` 与详情区。`Program.cs` 的 `Report` 同理，改为 `status + code`，由 UI 侧映射。

**定级理由**：不影响正确性，不影响安全，纯可用性/本地化缺口，故 P3。

---

## N2 [P3] 取消更新后已下载的安装包不清理，立刻重试会重下 200–300 MB

**位置**

- `Services/AppUpdateService.cs:88-137`（`DownloadAndInstallAsync`）
- `Services/AppUpdateService.cs:139-160`（`CleanupSessions`）

**机制**：

```csharp
string session = Path.Combine(_root, "sessions", Guid.NewGuid().ToString("N"));
...
await DownloadAsync(release, package, progress, _operation.Token);
Downloading = false;
bool exited = await exitWithHandoff(async () => { ... });
if (!exited) StatusKey = "UpdateExitCancelled";
```

`exited == false` 的路径有两条：`_exitPreparation.TryBegin` 返回 null（已有一次退出/更新在准备中），或用户在 `ExitCoreAsync` 的任一 flush 失败点返回 false（主题保存失败、管理页保存失败、某个便签/待办保存失败——后者还会 `ShowAndActivate` 把窗口弹到前面）。

此时 `package`（`sessions\<guid>\TuckPane-4.2.4-win-x64-setup.exe`，实测上游该资产 302 605 692 字节）已经完整落盘并通过哈希校验，但**没有任何代码删除它**。唯一的清理是 `CleanupSessions`：

```csharp
foreach (string directory in Directory.EnumerateDirectories(sessions))
{
    if (Directory.GetLastWriteTimeUtc(directory) >= DateTime.UtcNow.AddHours(-1)) continue;
    ...
}
```

而它只在 `AppUpdateService` 的构造函数里 `_ = Task.Run(CleanupSessions)` 跑一次。所以：

- 用户点"安装更新" → 下载 300 MB → 便签保存失败 → 退出被取消 → 界面显示"更新已取消"；
- 用户以为没下成，再点一次 → **新的 GUID 会话 → 再下 300 MB**；
- 旧会话要等到**下一次启动且超过 1 小时**才会被删。

**为什么算问题**：这不是理论推演——"便签/待办保存失败导致退出取消"是 `ExitCoreAsync` 里明确设计的分支（`window.ShowAndActivate(); return false;`），而更新包是本项目产生的最大的单个文件。慢网用户一次误操作就是几分钟 + 300 MB 流量，且磁盘上会同时堆多个完整包。

**修法**：`if (!exited)` 分支里 `try { Directory.Delete(session, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }`（会话目录里只有下载包与 request/result，删了不影响已发生的备份）。若要保留"重试不重下"，则应改为固定会话名 + 命中已校验包时跳过下载——但那是更大的改动，先做删除更划算。

**定级理由**：资源浪费与体验问题，无数据风险，P3。

---

## N3 [P3] `Dispose()` 之后仍触发 `Changed`，把已释放的服务实例交给 UI 刷新

**位置**

- `Services/AppUpdateService.cs:120-137`（`finally` 块）
- `Services/AppUpdateService.cs:186-190`（`Dispose`）
- `AppHost.cs:2465-2466`（`Updates.Dispose();` 在交接成功后立即调用）
- `ConsoleWindow.Updates.cs:14-17`（`UpdatesChanged`）

**机制**：

`ExitCoreAsync` 在 `await handoff()` 返回后立刻 `Updates.Dispose()`（取消 `_lifetime`、`_http.Dispose()`），然后才关窗口、`Application.Current.Exit()`。但 `DownloadAndInstallAsync` 此时仍在栈上——`exitWithHandoff` 的 `await` 刚返回，函数还要走 `finally`：

```csharp
finally
{
    _operation?.Dispose();
    _operation = null;
    Downloading = false;
    Busy = false;
    Changed?.Invoke();
}
```

`Changed?.Invoke()` → `ConsoleWindow.UpdatesChanged` → `if (!_closingPermanently) DispatcherQueue.TryEnqueue(() => { if (!_closingPermanently) RefreshUpdates(); })`。`_closingPermanently` 在 `Console.ClosePermanently()` 里才置位，而它在 `Updates.Dispose()` **之后**才执行——所以这个回调大概率真的会被排队并执行一次 `RefreshUpdates()`，读一个已 `Dispose` 的 `AppUpdateService`。

当前 `RefreshUpdates` 只读缓存属性（`CurrentVersion`、`Available`、`StatusKey`、`Progress`…），不碰 `_http`，所以**不会崩**。但这是一条"靠字段恰好是缓存值"才成立的隐式契约：任何人以后在 `RefreshUpdates` 里加一句 `update.RefreshSomething()` 或让某个属性变成惰性求值，就会变成 `ObjectDisposedException`，且发生在 `TryEnqueue` 的 UI 回调里 → 被 `App.UnhandledException` 吞（F6）→ 表现为"更新页白屏/不刷新"，无从诊断。

**修法**：`Dispose()` 里先 `_disposed = true`，`Changed?.Invoke()` 前判 `if (_disposed) return;`；或让 `UpdatesChanged` 同时检查 `_host.IsPreparingUpdate`（该标志在 `attempt.Commit()` 之后、`Application.Current.Exit()` 之前仍为 true）。最小改动是前者，3 行。

**定级理由**：当前无可观察故障，属"下一次改动就会踩"的脆弱契约，P3。

---

## N4 [P3] `UpdateWindowInput` 构造中途抛异常会让已禁用的窗口永久禁用

**位置**：`Services/UpdateWindowInput.cs:11-19`

```csharp
public UpdateWindowInput(IEnumerable<Window> windows)
{
    foreach (Window window in windows)
    {
        IntPtr handle = WindowNative.GetWindowHandle(window);
        _windows.Add((handle, IsWindowEnabled(handle)));
        EnableWindow(handle, false);
    }
}
```

**机制**：构造函数逐个 `GetWindowHandle` 并立即 `EnableWindow(handle, false)`。若其中某个窗口在枚举到它时还没有 HWND（`WindowNative.GetWindowHandle` 对未实现的窗口会抛），异常从构造函数抛出 → 调用点 `frozen = new UpdateWindowInput(...)`（`AppHost.cs:2422`）**赋值不成立**，`frozen` 保持 `null` → `finally` 里的 `frozen?.Dispose()` 什么也不做 → **前面已经 `EnableWindow(false)` 的那些窗口永久失去输入**，只能靠重启进程恢复。

窗口集合是 `_windows.Values` + `_noteWindows.Values` + `_externalNoteWindows.Values` + `_externalTodoWindows.Values` + `Console`，其中 `_externalNoteWindows`/`_externalTodoWindows` 是"便携文档"打开的外部窗口，生命周期不完全由 `AppHost` 掌握，正是最可能出现"对象还在字典里但 HWND 已失效"的一类。

**为什么算问题**：`UpdateWindowInput` 的设计意图（注释写着"HWND disabling also suspends keyboard input inside WebView2 while documents are flushed"）是对的，但"边禁用边构造"没有回滚。这是典型的 RAII 半成品：`Dispose` 只在构造成功时可用。

**修法**：两段式——先只收集 `(handle, enabled)` 不做禁用，全部收集成功后再统一 `EnableWindow(false)`；或构造函数体内 `try { … } catch { RestoreAll(); throw; }`。

**定级理由**：触发条件窄（需要某个窗口在那一刻没有 HWND），但后果是"整个应用失去输入、必须重启"，故给 P3 上限；若能确证外部便签窗口存在 HWND 未建成的窗口期，可升 P2。

---

## 防护到位（本轮复核，不计入编号）

更新链是本仓库加固程度最高的部分。以下 18 项逐条核对通过：

| # | 项 | 位置 |
| --- | --- | --- |
| 1 | zip-slip 全维度拒绝：空白/`\`/`:`/前导 `/`/空段/`.`/`..`/尾点/尾空格/非法字符/保留设备名 | `PortablePackage.Resolve` |
| 2 | 解析后再 `Within(root)` 二次确认，且 `NoLinks` 拒绝任何一级重解析点 | `PortablePackage.Resolve`、`UpdateFiles.NoLinks` |
| 3 | 清单条目数 3–6000、单条目 ≤2 MB、必须含 `TuckPane.exe` 与 `TuckPane.dll` | `ValidateManifest` |
| 4 | 拒绝清单自身、`unins*`、`state.json*`、`.tucknote`、`.tucktodo`、`.WebView2/` 进包 | `ValidateManifest` |
| 5 | 拒绝重复键、未列条目、目录条目、符号链接条目（`ExternalAttributes >> 16 & 0xF000 == 0xA000`） | `Stage` |
| 6 | 累计解压 ≤2.5 GB、条目 ≤6500 | `Stage` |
| 7 | 每个文件解压后立即 SHA256 比对，且 `seen.Count` 与清单条数对账 | `Stage` |
| 8 | 拒降级/同版本（`StableVersion(next) <= StableVersion(previous)`） | `Apply` |
| 9 | 目标目录已存在同名目录、或用户文件占了新程序路径 → 拒绝 | `Apply` |
| 10 | 覆盖前把 `existed` 的文件复制进 backup，写 `replacement-journal.json`，catch 时逆序恢复 | `Apply` |
| 11 | 清单最后安装，作为"已提交版本"标记 | `Apply` |
| 12 | 下载边下边算 `IncrementalHash`，长度超 `Size` 立即抛、结束再比对长度与哈希 | `ReleaseClient.DownloadAsync` |
| 13 | `SHA256SUMS.txt` 与 `asset.digest` 双向交叉校验 | `ReleaseClient.DownloadAsync` |
| 14 | 资产 URL 必须全等 `github.com/daha1216/TuckPane/releases/download/<tag>/<name>`，`size ∈ (0, 1.5 GB]` | `ReadAsset` |
| 15 | 父进程身份三重校验：PID + `StartTime` ticks + `MainModule` 目录 | `Program.cs:25-35` |
| 16 | 包哈希在交接前与 `WaitForExit` 后各校验一次 | `Program.cs:22`、`:48` |
| 17 | 写权限探针在"用户失去正在运行的应用"之前 | `Program.cs:38-44` |
| 18 | `ValidateTarget` 阻止程序目录与受保护数据目录重叠 | `PortablePackage.ValidateTarget` |

**提权面结论**：`src/TuckPane.Updater/TuckPane.Updater.csproj` 无 `app.manifest`，`src/TuckPane/app.manifest:7` 为 `level="asInvoker"`，全仓无 `requireAdministrator`、无 `runas`。更新器以当前用户权限运行，**不存在"构造 `UpdateRequest` 换取管理员代码执行"的本地提权路径**。Inno 静默安装使用 `/DIR=<target>` 装到当前用户可写目录，不触发 UAC。

**关于 `IsInstalledCopy()` 的否证**：`FolderContextMenuService.IsInstalledCopy()`（`:79-88`）读 `HKCU\…\Uninstall\{2B7D4C50-…}_is1` 的 `InstallLocation` 与 `AppContext.BaseDirectory` 比较。本机 `D:\TuckPane` 存在 `unins000.exe`/`unins000.dat`，说明该目录确实被 Inno 安装过，判为 installed 是**正确**行为，不是缺陷。它与 J1/C2 是同一根因（两渠道共用 AppId），但 `IsInstalledCopy` 本身没有逻辑错误。

---

## 未覆盖 / 局限

- 未审查：`src/TuckPane.Updater` 的 `app.manifest`（不存在）、安装包 `.iss` 的 `[Run]`/`[UninstallRun]` 段（已在 C2/C8/C9 覆盖）。
- 未验证：Inno `/VERYSILENT /SUPPRESSMSGBOXES /NOCLOSEAPPLICATIONS /RESTARTEXITCODE=3010` 组合在"目标目录正被占用"时的实际行为——需要真机跑一次更新才能确认；本机没有 .NET SDK，也没有可用的测试签名证书，无法构造端到端演练。
- 未验证：`Process.Start(helper, requestPath)` 在杀软拦截未签名 exe 时的表现（`TuckPane.Updater.exe` 未签名，见 §2 供应链结论）。

**结论**：更新链**没有发现 P2 及以上的缺陷**。四条 P3 全部集中在"错误呈现"、"失败后的资源回收"和"RAII 半成品"三处，不影响"会不会装错包/会不会被投毒"这两个核心问题。这条链的加固水平明显高于仓库其余部分，可以视为二开作者做得最好的模块。
