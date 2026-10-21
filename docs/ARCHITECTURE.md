# ARCHITECTURE — ceobe

> **实现语言已迁移**：本文描述的是当前 C# / .NET 10 + WinUI 3 的实际结构（ADR-019）。
> Java 版实现仍留在仓库中作为语义参考，已功能冻结、不参与构建。
> 迁移决策与施工图见 `docs/DECISIONS.md`（ADR-019）与 `docs/MIGRATION.md`。

## 系统概述

```
┌──────────── Ceobe.App（WinUI 3 + Windows App SDK，非打包自包含）────────────┐
│  MainWindow / Views/   内置 TitleBar + NavigationView + 实例工作区 + 七个分区  │
│  Styles/               间距 / 字阶 / 卡片 / 表格行等 Fluent 2 设计令牌           │
│  Controls/             状态点 / 三态图标 / 空状态 / 实例项 / 配置字段行          │
│  Converters/           纯 View 层转换器（bool→Visibility 等）                   │
└───────────────────────┬─────────────────────────────────────────────────────┘
                        │ x:Bind（View 只做绑定与手势转发）
┌───────────────────────▼── Ceobe.App.ViewModels（net10.0，零 WinUI 引用）─────┐
│  InstanceListViewModel / ConsoleViewModel / ServerViewModel / GameConfigViewModel │
│  ModsViewModel / ImportViewModel / ModpacksViewModel / PlayerListViewModel        │
│  TunnelViewModel / SettingsViewModel / CreateViewModel                           │
└───────────────────────┬─────────────────────────────────────────────────────┘
                        │ 同进程直调
┌───────────────────────▼── Ceobe.Core（net10.0，零 UI 依赖）─────────────────┐
│ Model      实例模型 + 状态枚举 + instance.json                              │
│ Store      CeobeHome 数据目录解析 + AppSettings                              │
│ Util       Mirrors / Http / Zips / Ports / Versions / Json / Progress        │
│ Config     server.properties 类型化 schema + 保序保注释读写                   │
│ Providers  IServerJarProvider SPI: Mojang / Paper(Fill v3) / Fabric / NeoForge / Purpur │
│ Runtime    ServerProcess / InstanceManager / InstanceControl / ControlServer  │
│ Jdk        JavaResolver 决策链 + Adoptium 安装（清华镜像）                    │
│ Mods       ModrinthClient / MrpackInstaller                                  │
│ Players    白名单/OP/封禁读写 + 离线 UUID                                     │
│ Tunnel     ITunnelProvider SPI: Pinggy(TCP) / Cloudflare(HTTP) / Tailscale / LAN │
│ WebConsole 内嵌 HTTP：状态/日志只读页                                         │
└─────────────────────────────────────────────────────────────────────────────┘
                        ▲
┌───────────────────────┴── Ceobe.Cli（net10.0，System.CommandLine）──────────┐
│  ps / create / start / stop / restart / rm / logs / exec / config / mod /    │
│  modpack / tunnel / jdk / versions / doctor / web / 隐藏 supervise            │
└─────────────────────────────────────────────────────────────────────────────┘
```

## 工程结构

| 工程 | 目标框架 | 职责 |
|------|----------|------|
| `src/Ceobe.Core` | `net10.0` | 全部业务：实例 CRUD、进程生命周期、jar 获取、配置读写、Mod、玩家名单、穿透、内嵌控制台 |
| `src/Ceobe.App.ViewModels` | `net10.0` | 全部界面状态与交互逻辑，**不引用任何 WinUI 类型**（闸门 C 的前提） |
| `src/Ceobe.App` | `net10.0-windows10.0.19041.0` | WinUI 3 桌面壳：内置 TitleBar + NavigationView + SelectorBar、`Styles/` 设计令牌、`Controls/` 复用控件、XAML 对话框、转换器与接线 |
| `src/Ceobe.Cli` | `net10.0` | 无头入口；docker 风格子命令；退出码契约 0/1/2 |
| `tests/Ceobe.Core.Tests` | `net10.0` | 域核心测试（含闸门 A 等价搬迁、闸门 B 真值 fixture） |
| `tests/Ceobe.App.ViewModels.Tests` | `net10.0` | ViewModel 层测试（闸门 C 的重心） |

## 依赖方向

`App → App.ViewModels → Core`，`Cli → Core`，测试引用对应工程。

Core 内部用**依赖倒置**切断 `Runtime ↔ Providers/Jdk/Tunnel` 的编译期耦合：
`IInstanceHost`、`IJavaLocator`、`IInstanceTunnels` 与 `JarInstaller` 委托定义在 `Runtime`，
具体实现由 `Composition/CeobeComposition` 装配。这样 GUI 与 CLI 共用同一套接线，
产品行为（哪个 `ServerType` 用哪个 provider、JDK 决策链、隧道何时建）不会两边漂移。

## 并发与进程模型

- 实例列表由扫描数据目录下的 `instances/*` 得出，**无中央索引**，因此 CLI/GUI 可同时运行。
- 运行态互斥靠「监督进程 + 控制通道」：`.ctl` 文件里是本机随机 token，`.state`/`.pid` 记录状态与进程号（ADR-007）。无全局 daemon。
- GUI 进程本身即所启动实例的监督进程：`CeobeComposition.StartAppServices` 建隧道管理器与内嵌 Web 控制台。
- CLI `start -d` 起独立监督子进程，GUI 只能经控制通道旁观（日志按文件偏移增量跟随）。

## 数据流

- 创建实例：`CreateAsync → provider.InstallAsync（下 jar）→ 落盘 instance.json + eula.txt + server.properties(模板)`。
- 启动：`StartAsync → java -Xmx… -jar server.jar nogui → 日志行流 → CREATED→STARTING→（匹配 "Done (…s)!"）→RUNNING → 控制台命令直写 stdin`。
- 穿透：`UpAsync → 派生 cloudflared/ssh 子进程 → 解析 stdout URL → 公网侧 MC status 探测 → 写回 instance.json → 界面展示三态结论`。

## 外部系统（2026-10-15 全部实测验证）

| 系统 | 端点 | 用途 | 关键契约 |
|------|------|------|----------|
| Mojang | `piston-meta.mojang.com/mc/game/version_manifest_v2.json` | vanilla jar | `downloads.server.url/sha1`；`javaVersion.majorVersion` |
| PaperMC Fill v3 | `fill.papermc.io/v3/projects[/{p}/versions/{v}/builds/latest]` | paper/folia jar | `downloads["server:default"].url` + sha256（v2 已 410） |
| Fabric | `meta.fabricmc.net/v2/versions/…` | loader + installer | `java -jar fabric-installer.jar server -dir . -mcversion X -loader Y` |
| Purpur | `api.purpurmc.org/v2/purpur…` | purpur jar | `{server}/versions/{v}/builds/{b}/download` |
| NeoForge | `maven.neoforged.net` + installer jar | neoforge | 安装器产出 `run.sh`/args 文件，启动用 `@argsfile` |
| Modrinth | `api.modrinth.com/v2/{search,project/{id}/version}` | mod/整合包 | 需 User-Agent；`files[].url`；`env.server` 判服务端侧 |
| Adoptium | `api.adoptium.net` | JDK 安装 | 经清华镜像改写，失败回退上游 |
| cloudflared | 子进程 `--url http://localhost:{webPort}` | Web 控制台穿透 | 日志正则 `https://<slug>.trycloudflare.com`；仅 HTTP |
| Pinggy | 子进程 `ssh -p 443 -R0:127.0.0.1:{mcPort} tcp@a.pinggy.io` | 玩家裸 TCP | stdout 正则 `tcp://<host>:<port>`；60min 会话需续期 |

## 重要技术边界

- **.NET 10**（SDK 10.0.402）。`Directory.Build.props` 的 `<Version>` 是 CalVer 唯一权威；`<Version>` 必须是点分四段（`26.10.21.01`），因为 NuGet 按 SemVer2 拒绝数字前导零的 prerelease 标识。
- **WinUI 3 + Windows App SDK 2.5.1**，非打包（`WindowsPackageType=None`）自包含分发。发布必须同时开 `SelfContained` 与 `WindowsAppSDKSelfContained`：前者带 .NET 运行时，后者带 WinUI 原生部分（`Microsoft.ui.xaml.dll`、`Ceobe.pri`；本 SDK 版本把页面 XAML 合并进 `Ceobe.pri`，发布目录无独立 `.xbf`）。
- **GUI 结构（ADR-020）**：主壳是内置 `TitleBar`（`ExtendsContentIntoTitleBar`）+ `NavigationView`（实例，`PaneFooter` 放新建/设置）+ `SelectorBar`（七个分区）；Mica 仅在跟随系统且 `MicaController.IsSupported()` 时启用，否则用纯色 `PageBackground`。`Styles/Spacing|Typography|Controls.xaml` 是间距/字阶/卡片/表格行的唯一来源；UI 依赖只允许出现在 `Ceobe.App`（`CommunityToolkit.WinUI.Controls.SettingsControls 8.2.251219`）。
- **MVVM**：`CommunityToolkit.Mvvm` 源生成器。ViewModel 不引用 WinUI 类型，可在无窗口环境单测——这是 JavaFX `-Dceobe.snapshot` 快照回归的替代方案（闸门 C）。Core 事件经 `ViewModels/UiThread.Post` marshal 回 UI 线程；实例列表刷新是差量更新，必须保留选中。
- **JSON**：`System.Text.Json`，camelCase 策略。与 Java 版 Jackson 只保证**字段级**兼容（Jackson 写 `"k" : v`、STJ 写 `"k": v`，不追求字节相同）。
- **HTTP**：`System.Net.Http.HttpClient` 单例；所有请求带 User-Agent（Modrinth 硬要求）。
- **下载镜像**：`Mirrors` 改写 URL（Mojang→BMCLAPI、Fabric meta→BMCLAPI、Modrinth CDN→mcimirror、Temurin→清华），失败自动回退上游。
- `server.properties` 写回保序、保注释、保 `\:` 转义（`level-type=minecraft\:normal`）。
