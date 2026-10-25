# 第十轮审查：图标 P/Invoke 参数封送、`IImageList` 边界与启动序列

- 日期：2026-10-25
- 基线：`daha1216/TuckPane` `main` = `7f58788`（v4.2.4）
- 范围：`src/TuckPane/App.xaml.cs`（248 行）、`src/TuckPane/Services/SingleInstanceGuard.cs`（全文）、
  `src/TuckPane/Services/StartupService.cs`（全文）、`src/TuckPane/Services/IconCacheService.cs`
  的 P/Invoke 段（`GetIconCoreAsync` / `RefreshAsync` / `ExtractShellIconPixels` / `GetPreferredIcon` /
  `TryGetResourceIcon` / `TryGetShortcutIcon` / `TryReadShellLinkIconLocation` / `TryExtractShellItemImage` /
  `TryGetJumboIcon` / `GetFallbackIcon` / `DrawIconPixels` / `RepairMissingAlpha` / `NormalizeDockIcon`）、
  `src/TuckPane/AppHost.cs:110-136`（`InitializeAsync`）。
- 方法：静态阅读 + 逐条回读调用方，确认每条结论至少有一条真实可达路径。**未编译、未运行**（本机无 .NET SDK）。

## 结论

6 条：P2 × 1、P3 × 5。

| 编号 | 级别 | 一句话 |
|---|---|---|
| M1 | P3 | `RepairMissingAlpha` 把纯黑像素判成透明，深色图标会出洞 |
| M2 | P3 | `SHIL_JUMBO` 拿不到时不回退，图标退化成 32×32 放大 |
| M3 | P2 | `StartupService.Apply` 无异常保护，注册表写失败会导致整个应用起不来 |
| M4 | P3 | 单实例 `Listen` 注册太晚，快速二次启动会弹错误的"遗留实例"提示 |
| M5 | P3 | 图标 PNG 落盘无 fsync，并发未命中会互相覆盖 |
| M6 | P3 | 启动序列两处 fire-and-forget，且启动即构造整个总控台窗口 |

---

## M1 [P3] `RepairMissingAlpha` 用"非纯黑"推断不透明，深色图标纯黑像素被留成透明

**位置**：`src/TuckPane/Services/IconCacheService.cs:633-650`（`RepairMissingAlpha`）、`:591-631`（`DrawIconPixels`）

**机制**：

```csharp
private static void RepairMissingAlpha(byte[] pixels)
{
    for (int i = 3; i < pixels.Length; i += 4)
    {
        if (pixels[i] != 0) return;      // 只要有一个像素有 alpha，就认为整张图 alpha 完好
    }
    for (int i = 0; i < pixels.Length; i += 4)
    {
        if (pixels[i] == 0 && pixels[i + 1] == 0 && pixels[i + 2] == 0) continue;  // 纯黑 → 保持透明
        pixels[i + 3] = 255;
    }
}
```

这条修复针对的是"`SHDefExtractIcon`/`DrawIconEx` 在部分 Shell 图标上返回全零 alpha"的已知现象：
把有颜色的像素补成不透明。问题是它的判据是"**RGB 全零 = 背景**"，而不是"这是背景"。

深色图标（黑色文件夹、黑底应用图标、暗色主题的 `.ico`）里存在**真实的纯黑前景像素**，
它们与"背景"在 RGB 上不可区分，于是被永久留成 alpha=0：

- 结果：图标上出现一圈/一块"洞"，透过洞能看到桌面壁纸。
- 触发面：只要 Shell 返回的位图 alpha 通道全零（`SHDefExtractIcon` 的常见行为），且图标含纯黑像素。
- 另一条路径 `TryExtractShellItemImage:470-540` 走 `GetDIBits` 拿到的 32 位 BGRA 同样会经过这个函数。

**顺带**：`DrawIconPixels:591-631` 里

```csharp
byte[] pixels = new byte[size * size * 4];      // 已零初始化
...
Marshal.Copy(pixels, 0, bits, pixels.Length);   // 把全零写进刚 CreateDIBSection 出来的 DIB
DrawIconEx(...);
```

`CreateDIBSection` 返回的位图内存本来就是零初始化，这次 `Marshal.Copy` 是纯废操作——
每次画图标多一次 `size*size*4` 字节的托管→非托管拷贝（256×256 时是 256 KiB）。删掉即可。

**修法**：
1. 纯黑像素不要一律判透明。可行的判据是"该像素是否为图标包围盒之外的角落颜色"，
   或干脆只用 `DrawIconEx` 的 alpha 结果、不再二次推断；
2. 若必须推断，至少把"纯黑但 alpha 为 0 且四邻域全是背景"与"纯黑但四邻域有前景"区分开；
3. 删掉 `DrawIconPixels` 里那次废拷贝。

**为什么是 P3**：只影响视觉，不影响数据；且只在"Shell 返回全零 alpha + 图标含纯黑"时出现。
但深色图标在 Windows 上很常见，实际命中率不低。

---

## M2 [P3] `SHIL_JUMBO` 取不到时不回退，也不校验 `imageIndex`

**位置**：`src/TuckPane/Services/IconCacheService.cs:546-573`（`TryGetJumboIcon`）

**机制**：

```csharp
if (NativeMethods.SHGetImageList(NativeMethods.SHIL_JUMBO, ref interfaceId, out IImageList imageList) < 0)
{
    return false;                      // ← 直接放弃，不回退 SHIL_EXTRALARGE / SHIL_LARGE
}
try
{
    int imageIndex = shellInfo.IconIndex & 0x00FFFFFF;   // ← 未校验是否 < GetImageCount()
    return imageList.GetIcon(imageIndex, NativeMethods.ILD_TRANSPARENT, out icon) >= 0 && icon != IntPtr.Zero;
}
finally { Marshal.FinalReleaseComObject(imageList); }
```

两个问题：

1. **无回退**。`SHIL_JUMBO`（256×256）在远程桌面会话、部分虚拟显示器驱动、Windows Server
   核心安装、以及系统图标缓存损坏时可能不可用。此时 `GetPreferredIcon:307-337` 直接落到
   `GetFallbackIcon`——`SHGFI_LARGEICON` 的 **32×32**，再由 `NormalizeDockIcon` 拉到 Dock 尺寸。
   256 目标尺寸下等于 8 倍最近邻放大，图标糊成色块。中间档 `SHIL_EXTRALARGE`（48）与
   `SHIL_LARGE`（32）都没试过。
2. **无边界检查**。`SHGetFileInfo` 返回的 `IconIndex` 是"系统图像列表里的索引"，
   理论上应落在 `SHIL_JUMBO` 列表范围内，但当 Shell 状态不一致（图标缓存重建中、
   `SHGFI_SYSICONINDEX` 返回了另一个列表的索引）时，越界索引传给 `IImageList::GetIcon`
   是未定义行为。加一句 `imageList.GetImageCount(out int count); if (imageIndex < 0 || imageIndex >= count) return false;`
   成本一行。

**修法**：
```csharp
private static readonly int[] JumboFallbacks = [SHIL_JUMBO, SHIL_EXTRALARGE, SHIL_LARGE];
foreach (int size in JumboFallbacks)
{
    if (SHGetImageList(size, ref iid, out imageList) < 0) continue;
    ...
}
```

**为什么是 P3**：只在特定宿主环境退化；但退化后的观感很差（8 倍放大），且用户会以为是"这软件图标怎么这么糊"。

---

## M3 [P2] `StartupService.Apply` 无任何异常保护，注册表写失败会让整个应用起不来

**位置**：`src/TuckPane/Services/StartupService.cs`（全文 26 行）、`src/TuckPane/AppHost.cs:126`

**机制**：

```csharp
public static void Apply(bool enabled)
{
    if (AppPaths.IsTestMode) return;

    using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
    key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    if (!enabled)
    {
        key.DeleteValue(ValueName, throwOnMissingValue: false);
        return;
    }

    string executable = Environment.ProcessPath ?? throw new InvalidOperationException(AppStrings.Get("StartupPathUnavailable"));
    key.SetValue(ValueName, $"\"{executable}\" --startup", RegistryValueKind.String);
}
```

调用处 `AppHost.InitializeAsync`：

```csharp
await MigrateLegacyOrganizerNotesAsync();
StartupService.Apply(State.GlobalSettings.StartWithWindows);   // ← 没有 try/catch
Updates = new AppUpdateService();
```

对比同一个方法里紧邻的两次注册表/Shell 初始化——`FolderContextMenu.RepairIfOwned` 与
`DesktopContextMenu.InitializeDesktop`——**都包了 `try/catch` 并只记录日志**。唯独 `StartupService.Apply` 没有。

**可达失败路径**（都不是臆测）：

| 场景 | 抛点 | 后果 |
|---|---|---|
| 组策略禁用 `HKCU\...\Run` 写入（企业/学校机器常见） | `CreateSubKey(writable: true)` → `UnauthorizedAccessException` | 启动中断 |
| 用户配置单元损坏 / 注册表被安全软件锁 | `CreateSubKey` → `IOException` / `SecurityException` | 启动中断 |
| 应用被以某种方式宿主化，`Environment.ProcessPath` 为 null | `throw new InvalidOperationException(...)` | 启动中断 |
| 磁盘满导致注册表 hive 无法扩展 | `SetValue` → `IOException` | 启动中断 |

后果链：`InitializeAsync` 抛出 → `App.OnLaunched` 的 catch → `AppLogger` 记录 + `StartupFailure.Show(ex)` + `Exit()`。
**用户看到的是"TuckPane 启动失败"，而真实原因只是"开机自启"这一个可选功能写不进去。**
这个应用的核心功能（桌面收纳）与 `Run` 键毫无关系。

**附带问题**：`enabled == false` 时也会执行 `CreateSubKey(writable: true)`。
即"关掉开机自启"这个动作反而需要**写权限**——在只读策略下，一个本该是"什么都不做"的分支也会抛。

**修法**（约 8 行）：

```csharp
public static void Apply(bool enabled)
{
    if (AppPaths.IsTestMode) return;
    try
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null && !enabled) return;              // 没这个键且要关 → 无事可做
        key ??= Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        ...
    }
    catch (Exception ex)
    {
        AppLogger.Error("无法更新开机自启项。", ex);       // 只记录，不影响启动
    }
}
```

并把调用处也包进 try/catch，双保险。

**为什么是 P2**：这是**唯一一条能把"局部可选功能失败"放大成"整个应用无法启动"**的路径。
触发条件在受管机器上不算罕见，而后果是完全丧失可用性。

---

## M4 [P3] 单实例 `Listen` 注册在 `InitializeAsync` 之后，快速二次启动会弹错误的"遗留实例"提示

**位置**：`src/TuckPane/App.xaml.cs`（`OnLaunched`）、`src/TuckPane/Services/SingleInstanceGuard.cs`（`SignalPrimary` / `Listen`）

**机制**：

```csharp
_host = new AppHost();
await _host.InitializeAsync();                 // ← 磁盘 IO、读 state、建窗口、建托盘，几十到几百 ms
_singleInstance.Listen(() => _host.OpenConsole());   // ← 监听器在这里才挂上
```

而二次启动那一侧：

```csharp
if (!_singleInstance.SignalPrimary())           // Set 事件后等 ack，默认 4 秒
{
    SingleInstanceGuard.ShowLegacyInstanceMessage();   // ← 超时就弹"检测到旧版 GlassFolder 正在运行"
}
Exit();
```

在 `InitializeAsync` 还没跑完的那段时间里，`_activationEvent` 已经被 Set，但**没有等待者**，
于是 `_activationAcknowledged` 不会被 Set，`SignalPrimary` 走满 4 秒超时，返回 false，
弹出一个**内容完全错误**的对话框（说的是"旧版 GlassFolder 实例"，实际是"新版还在启动"）。

触发方式很日常：双击快捷方式没反应，再双击一次。

**同一文件里另外两处（潜在，非当前可达）**：

1. `Dispose()` 里 `if (_ownsMutex) _mutex.ReleaseMutex();`。`Mutex` 是 `initiallyOwned: true`
   在 UI 线程创建的，`ReleaseMutex` 必须由**拥有它的线程**调用。当前 `App` 不实现 `IDisposable`
   也从不 Dispose，所以是潜在缺陷；一旦将来加"退出时清理"就会踩到 `ApplicationException`。
2. `ShowLegacyInstanceMessage` 用 `MessageBoxW(..., 0x30)`（`MB_ICONWARNING`），
   在 `Exit()` 之前同步阻塞——如果主实例恰好在这 4 秒里完成了初始化，用户仍会先看到错误提示。

**修法**：把 `Listen` 提前到 `_host = new AppHost()` 之前，回调里改成"排队"语义：

```csharp
_singleInstance.Listen(() => _dispatcher.TryEnqueue(() => _host?.OpenConsole()));
```

`AppHost.OpenConsole` 内部本来就有 `_dispatcher.TryEnqueue`，但 `_host` 在初始化完成前是 null，
所以回调要判空 + 延后。或者更简单：`Listen` 里只置一个 `_pendingActivation = true`，
`InitializeAsync` 完成后检查并补一次 `OpenConsole()`。

**为什么是 P3**：只影响二次启动的提示文案与 4 秒延迟，不影响主实例功能。

---

## M5 [P3] 图标 PNG 落盘无 fsync；并发未命中同一 `cachePath` 会互相覆盖

**位置**：`src/TuckPane/Services/IconCacheService.cs:209-238`（`RefreshAsync`）

**机制**：

```csharp
StorageFile temporary = await folder.CreateFileAsync($"{name}.{Guid:N}.tmp", CreationCollisionOption.FailIfExists);
...
await encoder.FlushAsync();
await temporary.MoveAsync(folder, Path.GetFileName(cachePath), NameCollisionOption.ReplaceExisting);
```

1. **无 fsync**：`FlushAsync` 只把编码器缓冲写进文件句柄，没有 `FlushFileBuffers`。
   断电/蓝屏后可能留下一个"文件名正确、内容截断"的 PNG。下一次读到它会
   `BitmapImage.SetSourceAsync` 抛 `Exception` → `GetIconCoreAsync` 的 catch 记录并返回 null →
   **该图标永久显示为空白**，直到用户手动删缓存目录（`SweepAsync` 只删 1 小时以上的 `.tmp`，
   不校验已落盘 PNG 的完整性）。
2. **并发覆盖**：`cachePath` 由 `Hash(identity)` 决定，同一路径/同一目录的两个并发请求会算出
   同一个 `cachePath`，两个 `MoveAsync(ReplaceExisting)` 互相覆盖。当前被 L4 的
   `SemaphoreSlim(1,1)` 全局串行化掩盖——**一旦按 L4 的建议放宽并发，这条立刻变成现实问题**。
   所以 M5 与 L4 是"修一个必须同时修另一个"的关系。

**修法**：写完后 `File.OpenWrite` + `Flush(true)`（或用 `RandomAccess` + `FileOptions.WriteThrough`）；
并在 `GetIconCoreAsync` 读 PNG 失败时删除该文件、重试一次提取。

**为什么是 P3**：需要断电这类外部事件，或需要先修 L4。

---

## M6 [P3] 启动序列两处 fire-and-forget；启动即构造整个总控台窗口

**位置**：`src/TuckPane/AppHost.cs:110-136`（`InitializeAsync`）

**机制**：

```csharp
_ = IconCacheService.SweepAsync();          // ① 不 await、不观察
...
Console = new ConsoleWindow(this);          // ② 启动即建完整总控台
Console.InitializeHostWindow();
...
_ = Updates.CheckAsync(false);              // ③ 不 await、不观察
```

1. **①③ 的异常无人处理**。`SweepAsync` 内部整体 `catch { }`（`IconCacheService.cs:44-86`），
   所以①实际安全；`Updates.CheckAsync` 若在网络层抛出未被内部捕获的异常，
   会走 `TaskScheduler.UnobservedTaskException`——`Program.Main` 只记录不崩溃，
   但用户永远不知道"检查更新从来没成功过"（本机 `check.json` 里躺着上游 v4.2.1 的旧缓存，
   正是这个现象的现场证据）。
2. **② 是启动期固定成本**。`ConsoleWindow` 有 2248 行 code-behind、整套 XAML 树、
   `_themeSurface` / `_settingsEdgeSurface` 两个合成表面、`_chrome`、六个 timer，
   并在 `InitializeHostWindow` 里跑 `RestorePlacement`。即使这个会话从头到尾不开总控台，
   这些也全部发生在启动路径上。对"开机自启、常驻托盘"这个主要使用方式来说，
   这是纯浪费——但静态审查无法量化到毫秒，只能指出结构。

**修法**：③ 改成 `await` 但包 try/catch（或在 `CheckAsync` 内部保证不抛并记录结果）；
② 改为首次 `OpenConsole()` 时惰性构造（改动较大，归入架构优化建议书 H1 的重构范围）。

**为什么是 P3**：①③ 是健壮性/可观测性问题；② 是性能问题，无正确性影响。

---

## 本轮"防护到位"复核（不计入缺陷）

| 项 | 结论 |
|---|---|
| `CommandLineToArgvW` 的 `LocalFree` | `App.xaml.cs` `ParseRedirectedArguments` 用 `finally` 释放，正确 |
| `RedirectActivation` 的 STA 阻塞 | 在消息循环启动前 `ManualResetEventSlim.Wait()`，不会死锁 UI 线程 |
| `AppInstance` 测试隔离 | `TUCKPANE_TEST_ROOT` 时单实例键追加 SHA256 前 12 位，测试不干扰正式实例 |
| `IsActivationExecutable` | 拒绝非完全限定路径、拒绝其他 exe 名；catch 返回 false（保守） |
| `SingleInstanceGuard` 遗留实例检测 | 用独立 legacy mutex 而不是猜名字，方向正确 |
| `SHDefExtractIcon` 的 `smallIcon` | 无论成功失败都 `DestroyIcon`，无泄漏 |
| `IShellLinkW` / `IPersistFile` | `finally` 里 `FinalReleaseComObject`，正确 |
| `IImageList` | `finally` 里 `FinalReleaseComObject`，正确 |
| `IShellItemImageFactory` / HBITMAP | `finally` 里 `DeleteObject` + `FinalReleaseComObject`，正确 |
| `CreateDIBSection` / `CreateCompatibleDC` | `finally` 还原 `SelectObject` + `DeleteObject` + `DeleteDC`，正确 |
| `BuildCacheIdentity` 防环 | `HashSet<string> visited` + `MaximumShortcutIconDepth = 4`，且拒绝 `\\` 前缀 |
| `NormalizeShortcutResourcePath` | `ExpandEnvironmentVariables` + 相对路径拼接 + `File.Exists` 校验，正确 |
| 临时文件清理 | `RefreshAsync` 的 `finally` 删残留 `.tmp`，`SweepAsync` 兜底 1 小时以上的 |
| `GetIconCoreAsync` 的失败降级 | 提取失败只记录 + 返回 null，不抛给 UI |
| `InitializeAsync` 的容错分层 | 文件夹菜单/桌面菜单都包了 try/catch（唯独 `StartupService.Apply` 没有，即 M3） |

## 未覆盖

- `SplashScreen`（仓库里没有该文件，启动画面由 Windows App SDK 默认提供）——无从审查。
- `AppUpdateService.CheckAsync` 的内部实现（已在第二轮 C1/C4/C5 覆盖过，本轮未重读）。
- `AppHost.InitializeAsync` 之后的 `HandleArgumentsAsync` 全路径（首轮已覆盖）。
- 所有 P/Invoke 的实际封送行为——**未运行，无法验证 `Marshal.SizeOf<SHFILEINFO>` 与
  结构体 `[StructLayout]` 是否一致**；只能确认代码里两处 `sizeof` 用法自洽。

## 局限

本机无 .NET SDK（`C:\Program Files\dotnet\sdk` 为空，`global.json` 要求 10.0.400），
本轮全部结论来自静态阅读，**未编译、未运行、未在真机验证 M1–M6 的实际触发率**。
M1/M2 的观感影响需要真机截图才能定级；M3 的触发条件需要受管机器才能复现。
