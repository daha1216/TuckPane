# 第四轮：按"数据破坏路径"倒查

- 对象：`fork/` @ `7f58788 Release TuckPane 4.2.4`
- 方法：不按文件读，改按"哪条路径会真正弄丢/弄坏用户数据"倒查——删除、移动、取消、崩溃恢复、安装/卸载、更新前备份
- 结论：**数据破坏面防护到位**（19 项逐一核实，见下表），但查出 2 条新问题，都不在数据路径本身，而在**安装器与更新前置步骤**

---

## R3 [P3] 安装器注册文件夹右键菜单时没有所有权守卫

**位置**：`installer/TuckPane.iss:111-138`（`RegisterFolderMenu`）

**机制**：这段安装脚本只检查用户的偏好开关，然后**无条件覆写**菜单键：

```pascal
procedure RegisterFolderMenu();
var
  Enabled: DWORD;
begin
  if RegQueryDWordValue(HKCU, FolderMenuPreferenceKey, 'Enabled', Enabled) then
    if Enabled = 0 then Exit;
  RegWriteStringValue(HKCU, FolderMenuKey, '', '使用 TuckPane 创建收纳窗');
  RegWriteStringValue(HKCU, FolderMenuKey, 'Icon', ExpandConstant('{app}\TuckPane.exe'));
  RegWriteStringValue(HKCU, FolderMenuKey, 'OwnerExecutable', ExpandConstant('{app}\TuckPane.exe'));
  ...
end;
```

没有 `RegKeyExists` 前置检查，也没有把已存在的 `OwnerExecutable` 与本次安装路径比对。

**对照**：同一文件的 `RegisterDesktopMenu`（`:181-214`）是**对的**：

```pascal
RegQueryStringValue(HKCU, DesktopMenuPreferenceKey, 'OwnerExecutable', Owner);
if (Owner <> '') and (CompareText(Owner, Executable) <> 0) then Exit;   // :192-194
if RegKeyExists(HKCU, DesktopMenuKey) then
  if not DesktopMenuOwnedByThisInstall then Exit;                      // :195-196
```

应用侧的 `FolderContextMenuService` 也是**对的**：`:96-101` 会判 `OtherCopy`，`:107`/`:113-114` 拒绝接管他人菜单，`:127-130` 写入 `OwnerExecutable`。

**后果**：用户同时有便携副本（例如 `D:\TuckPane`）时，跑一次 `setup.exe` 会**静默把文件夹右键菜单改成指向新装的副本**。便携副本打开设置页后显示 `OtherCopy` 并拒绝管理该菜单，用户失去对它的控制；更实际的影响是"新建收纳窗"会落到另一个实例、另一个数据根，用户以为文件丢了。

**归属**：上游遗留（`RegisterDesktopMenu` 有守卫说明作者知道这个问题，文件夹侧漏了）。

**修法**：把 `RegisterDesktopMenu` 的三行守卫照搬到 `RegisterFolderMenu` 开头。

---

## R4 [P3] 更新前备份假定 `state.json` 必然存在，全新配置下更新直接失败

**位置**：`src/TuckPane.Updates/UpdateFiles.cs:40-55`、调用点 `src/TuckPane/Services/AppUpdateService.cs:109`

**机制**：

```csharp
// UpdateFiles.BackupConfiguration
Directory.CreateDirectory(destination);
File.Copy(statePath, Path.Combine(destination, "state.json"));   // ← 无 File.Exists 守卫
File.Copy(statePath + ".bak", ...);                               // 有守卫？没有，但 .bak 缺失是另一回事
```

调用点无条件执行（`exitWithHandoff` 内，下载完成后的第一步）：

```csharp
BackupPath = UpdateFiles.BackupConfiguration(AppPaths.StatePath, Path.Combine(_root, "config-backups"), CurrentVersion);
```

**为什么 `state.json` 可能不存在**——这条链每一环都不落盘：

1. `src/TuckPane/Services/AppPaths.cs:32-41` `EnsureCreated()` 只 `Directory.CreateDirectory`，不建 `state.json`；
2. `src/TuckPane/Services/StateStore.cs:22-32` `LoadAsync` 对新用户 `bool newUser = !File.Exists(_statePath) && !File.Exists(_backupPath);` → 用 `NewUserDefaults.CreateState()`，但**只在 `loaded is { RequiresMigration: true }` 时才 `PersistMigrationAsync`**——新用户 `loaded` 是 `null`，不落盘；
3. `src/TuckPane/Models/NewUserDefaults.cs:7-46` `CreateState()` **只返回 `GlobalSettings`，一个 organizer 都没有**；
4. `src/TuckPane/AppHost.cs:111-141` `InitializeAsync` 里唯一的启动期保存是 `if (NormalizePositionedPlacementsOnStartup() | NormalizeStationPlacementsOnStartup()) await SaveStateAsync();`——没有任何 organizer，两个 Normalize 都返回 false。

**后果**：全新安装、装完直接点"检查更新 → 下载 → 安装"的用户，在备份阶段抛 `FileNotFoundException`，UI 上是英文 `Could not find file 'C:\Users\<用户名>\AppData\Local\TuckPane\state.json'.`——**带完整用户路径**。更新彻底无法进行，直到用户先改任意一个设置触发一次保存。`state.json` 被用户手动删掉、或被杀软隔离时，同样命中。

**归属**：上游遗留（`BackupConfiguration` 与 `LoadAsync` 都是上游代码），二开未触及。

**修法**：

```csharp
bool statePresent = File.Exists(statePath);
if (statePresent) { File.Copy(statePath, ...); if (Hash(statePath) != Hash(dest)) throw ...; }
// backup.json 里记 statePresent: false，恢复时按"无配置可恢复"处理
```

或更简单：`LoadAsync` 里对 `newUser` 强制 `await SaveAsync(state)` 一次。

---

## 本轮"防护到位"证据表（19 项，均已回代码确认）

| # | 路径 | 位置 | 结论 |
|---|---|---|---|
| 1 | 删除文件/文件夹 | `MainWindow.xaml.cs:3998-4030` | 走 `RecycleOption.SendToRecycleBin`，**不是永久删除**；失败有 `AppLogger.Error` + `ShowMessage` |
| 2 | 重命名 | `MainWindow.xaml.cs:3966-3994` | 保存失败反向 Move 回滚，并恢复 `ItemOrder` |
| 3 | 移动取消 | `StorageService.cs:450-475` | `TryDelete(staging)` + 保留源 + `MoveSourceRetained` 提示 |
| 4 | 导出到桌面 | `StorageService.cs:195-250` | 只在移动成功后 `Directory.Delete(_itemsRoot)` |
| 5 | 删除收纳窗 | `AppHost.cs:1295-1470` | 两种 disposition：导出到桌面 / 原样保留并告知路径；无"直接删"分支 |
| 6 | 卸载 | `installer/TuckPane.iss` | 全文**无 `[UninstallDelete]`**，用户数据与安装目录外的文件不受影响 |
| 7 | 包含关系成环 | `Core/OrganizerContainment.cs:115-130` | `HashSet<Guid> visited` 防环；`:90-111` 五个守卫齐全 |
| 8 | XML 解析 | `WindowsCrashEvents.cs:76-82` | 显式 `DtdProcessing.Prohibit`，无 XXE |
| 9 | 图片解码 | `NoteImageData.cs:6-17` | 长度上限 + MIME 白名单 + 非空校验 |
| 10 | 便携文件名 | `NoteStore.cs:388-400` | 非法字符全替换、`TrimEnd(' ','.')`、120 字符、DOS 设备名加前缀 |
| 11 | 便携文档校验 | `NoteStore.cs:418-458` | Format/Version/枚举/有限性/尺寸/ID 唯一/时间戳一致性；64MB 上限在反序列化**前** |
| 12 | 便签/待办退出保存 | `AppHost.cs:2436-2455` | 逐个 `FlushForExitAsync()`，失败则**取消退出**并激活窗口 |
| 13 | 关窗即隐藏 | `NoteWindow.xaml.cs:809-814`、`TodoWindow.xaml.cs:777-780` | `args.Cancel = true`，宿主保留窗口对象是设计而非泄漏 |
| 14 | 拖拽 COM | `ShellDragService.cs:91/110`、`:118/141`、`:336/366` | `OleInitialize`/`OleUninitialize` 三处全部 try/finally 配对 |
| 15 | 跨进程内存 | `DesktopGridService.cs:296-376` | `VirtualFreeEx` + `CloseHandle` 全在 finally；权限位无 `CREATE_THREAD` |
| 16 | 拖拽标志 | `MainWindow.xaml.cs:5358/5439/5583` | 三处置 true 全部汇入 `FinishShellDragVisualState`/`TryFinish…`，含 finally |
| 17 | 遮罩计数 | `MainWindow.xaml.cs` | `_overlayOpenCount` 靠 `_contextMenuCounted` 幂等 + `RestoreContextMenuHost` 配平 |
| 18 | 进程启动 | `AppUpdateService.cs:118-119`、`Updater/Program.cs:50-54`/`:64` | 全部 `ArgumentList.Add`，无字符串拼接注入 |
| 19 | 诊断包 | `Services/DiagnosticsExporter.cs` | 只写 `runtime.jsonl`/`environment.json`/`README.txt`，5 MiB 上限，无用户文件/路径/用户名 |

另核实：`TuckPane.csproj:22-23` 的 `TUCKPANE_UPDATE_VALIDATION` 只在 `UpdateValidation=true` 时定义，release workflow 不设；`AppUpdateService.cs:36` 的 `new HttpClient(new ValidationUpdateHandler())` 确在 `#if` 内（`:32-38`），生产构建不走测试通道。

---

## 未覆盖

- 仍未做动态验证（无 .NET 10 SDK，无法编译）。
- 安装器脚本只在静态层面核对，未实际运行 `setup.exe` 验证 `RegisterFolderMenu` 的覆写行为。
- WebView2 渲染层只审到"消毒器逻辑"，未做实际 XSS payload 测试。
