# TuckPane 二开版审查 · 落地指南

> 一份一页纸的收口：4 个 PR 怎么合、3 件事需要你拍板、v4.2.5 建议做什么。
> 完整报告（77 条，每条含文件:行号、机制、可达路径、修法、定级理由）另附。

---

## 0. 一句话

全量静态审查，共 **77 条**发现（P2 × 15、P3 × 62）。已提 4 个 PR，**#1 已合并**，剩下 3 个待合。没有发现后门、遥测、提权面或数据外泄；问题集中在桌面层自愈、窗口生命周期收尾、安装/更新身份三块。

---

## 1. 三个 PR 的合并顺序

| 顺序 | PR | 内容 | 规模 | 说明 |
|---|---|---|---|---|
| 1 | **#3** | 资源管理器重启后重建托盘图标（`TaskbarCreated`） | 2 文件 +18/−0 | 纯增量，零风险，先合 |
| 2 | **#4** | 过渡被取代后 `_animating` 卡死 + 中转站显示器缓存 + 驱动器根菜单报错 | 3 文件 +49/−8 | 与 #2 无重叠 |
| 3 | **#2** | 桌面层自愈 5 项 + CI | 5 文件 +138/−37 | 最大，且带 CI，放最后 |

**冲突点**：三个 PR 都碰 `MainWindow.xaml.cs`，但区域不重叠（#3 只碰 `TrayIconService`/`NativeMethods`；#4 碰 `ExpandTransitionAsync`/`CollapseTransitionAsync`；#2 碰桌面层修复定时器）。按 #3 → #4 → #2 合并预计零冲突；若先合 #2，#4 需要 rebase 一次。

**关于 #2 里的 CI**：`ci.yml` 里 LogicChecks 设了 `continue-on-error: true`，这是刻意的——我无法验证现有测试套件在干净 checkout 上是否全绿（见 §5）。建议先合、看一轮 CI 结果，再决定要不要改成硬门禁。

---

## 2. 三件需要你拍板的事

### C1 代码签名

现状：发布物未签名（`TuckPane.exe` 与 `00-启动 TuckPane.exe` 的 Authenticode 签名都是 NotSigned），`SHA256SUMS.txt` 与安装包同源同 release——能防传输损坏，**防不了发布端被篡改**。

| 选项 | 成本 | 效果 |
|---|---|---|
| (a) OV/EV 证书 | 每年数千元 | SmartScreen 立刻干净，用户不再看到"未知发布者" |
| (b) Azure Trusted Signing / SignPath | 便宜，需企业资质 | 同上，门槛在资质不在钱 |
| (c) 不做，但建立第二信任锚 | 0 | 把 release 的 SHA256 同步发到 README 之外的固定位置（个人站点/固定 issue），用户可交叉核对 |

建议：先做 (c)，(a)/(b) 看下载量再说。

### C2 + J1 身份分家（建议优先级最高）

现状：fork 与上游共用三样东西——

- `AppId={{2B7D4C50-0148-4D5C-A097-D8D7E5C64FCB}}`（`TuckPane.iss:19`）
- 单实例键 `TuckPane-019d2f2d-0bfb-7ff0-98f5-d93093bb0b5d`（`App.xaml.cs:70`、`:241`）
- 状态目录 `%LOCALAPPDATA%\TuckPane`（`AppPaths.cs:7`）

后果（真实场景，不是理论）：

1. 用户先装原版、再装你的版本 → 两个渠道互相就地覆盖，且**静默切换更新源**；
2. 两个版本同时运行时，后启动者被 `RedirectActivation` 转给先启动者，然后自己退出——用户以为开了两个，其实只有一个；
3. 两者互写同一个 `state.json`，**原版的 `Normalize` 会丢掉 `ContainerStationId` 和 `ExpandedPosition`**——容器里的收纳窗和展开态位置会莫名其妙消失。

建议：改 AppId、改 `ProductDirectoryName`（例如 `TuckPane-Fork`，或你自起的名字）、改单实例键前缀。代价是老用户升级要迁移一次 `state.json`，写个一次性迁移函数即可。**这件事越早做越便宜。**

### F6 异常白名单

现状：`App.xaml.cs:100-110` 的全局 `UnhandledException` 无条件 `args.Handled = true`，所有缺陷静默化——包括本报告里 A/I/J/K/L 多条的触发点。

建议：不要直接改成 `false`（会崩）。改成"记录 + 首次出现时在总控台顶部显示一条可关闭的提示条"，并对已知无害的类型（关闭期的 `ObjectDisposedException` 等）静默。既保留可用性，又不再掩盖问题。

---

## 3. 建议的 v4.2.5 范围

**第一批（约 70 行，零设计决策，可当天做完）**

- **F2** 普通退出前 flush 两个防抖计时器（`_interactionSaveTimer` 350 ms / `_placementTimer` 450 ms）——现在只在"更新交接"分支落盘，普通退出丢最后 350 ms 内的改动
- **C9** 更新前备份先判 `state.json` 是否存在——全新配置点更新会抛 `FileNotFoundException` 并把英文+完整路径显示给用户
- **C8** 安装器注册文件夹菜单前加所有权守卫（对照 `TuckPane.iss:181-214` 已有的写法）
- **D1** `StateStore.SaveAsync` 加 fsync——现在断电可能留下 0 字节或半截 `state.json`
- **M3** `StartupService.Apply` 包 try/catch——注册表写失败现在会让**整个应用起不来**

**第二批（需要真机验证）**

- **A9** z-order 修复不再被 `ownerChanged` 门控
- **A8** 展开态自愈（现在 `_expanded` 直接早退，Win+D 之后不自愈）
- **C6** / **A2**
- **K1** Ctrl+拖到桌面应该是复制——现在无论按不按 Ctrl 都是移动，而 `CopyItemToDesktopAsync` 写好了却全仓零调用

---

## 4. 报告怎么读

`审查报告-4.2.4-终版.md`：

- **§0 结论** → **§10 总表（77 条）** → **§11 优先级**，这三节足够做决策
- §2 是供应链核验（哈希逐文件比对、二进制字符串检索、签名状态）
- §3–§9 是各域详述，§14–§18 是后续五轮增补
- 每条都有：文件:行号、机制、可达路径、修法、定级理由

---

## 5. 关于这份审查的局限

**全部结论都是静态的：没有编译，也没有运行。** 审查机上没有 .NET SDK（`global.json` 要求 10.0.400，本机只有 8.0.11 运行时），所以：

- 没有跑过 `dotnet build`，PR 里的改动未经验证；
- 没有跑过 LogicChecks / UpdateChecks；
- 没有真机验证 Win+D、拔屏、资源管理器重启这些场景。

你在 PR #1 里做的验证流程（Release x64 0 警告 0 错误 + LogicChecks --stability 八区全 PASS + UpdateChecks 4/4 + 真机 z-order 对比）是**正确且必要**的，#2/#3/#4 也请照做。

报告里凡是"我推断但未验证"的地方都单独标了；凡是"查过并确认没问题"的地方（约八十个模块）也单独列了正面结论，没有为了凑数把正常代码写成缺陷。
