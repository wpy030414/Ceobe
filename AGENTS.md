# AGENTS.md

> **当前形态**：C# / .NET 10 + WinUI 3，**Windows 专属**（ADR-019）。
> 迁移施工图与进度见 `docs/MIGRATION.md`；技术决策见 `docs/DECISIONS.md`。
> **Java 版（`ceobe-core/`、`ceobe-cli/`、`ceobe-gui/`、`pom.xml`）自 2026-10-21 起功能冻结、只修 bug**，
> 不参与构建，保留作语义参考与 hotfix 回打基线；基线 tag `pre-csharp-migration`。

ceobe 是本机 Minecraft 服务器多实例管理器：下载服务端 jar（vanilla/paper/fabric/neoforge/purpur）、可视化编辑 server.properties、Modrinth 装模与整合包导入、CF/Pinggy/Tailscale 穿透、进程生命周期管理。GUI 与 CLI 共享同一套域核心。

## 边界与范围

- 范围内：Windows 10（1809+）/ 11 单机编排；Java 版（Minecraft）服务端；文件级状态存储（数据目录，默认 `%USERPROFILE%\.ceobe`，可在设置里改）。
- 非目标（明确排除）：跨平台（macOS/Linux 已放弃）、Linux 容器化部署、Bedrock 支持、多用户/多租户、CurseForge（API 强制 key，暂缓）、计费/商业化。

## Agent 操作指南

### 构建与测试

```powershell
# 若 dotnet 未在 PATH（本机装在 %LOCALAPPDATA%\Microsoft\dotnet）
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build ceobe.slnx -c Debug
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test  ceobe.slnx -c Debug

# 发布（自包含，目标机无需装 .NET）
dotnet publish src/Ceobe.App/Ceobe.App.csproj -c Release -r win-x64 --self-contained true -o dist/app
dotnet publish src/Ceobe.Cli/Ceobe.Cli.csproj -c Release -r win-x64 --self-contained true -o dist/cli
```

- **网络用例默认排除**（`Trait("Category","network")`，在 `Ceobe.Core.Tests.csproj` 里用 `VSTestTestCaseFilter` 落实）：它们会真实下载服务端 jar，几分钟起步。需要时显式加 filter `Category=network`。
- 测试集禁用并行（`xunit.runner.json` 的 `parallelizeTestCollections: false`）：进程级静态（`CeobeHome.Override`）会串扰。

### 约定

- 版本号 CalVer `YY.MM.DD-HH`，**`Directory.Build.props` 的 `<Version>` 是唯一权威**，CI 只读不写。该值必须是点分四段（`26.10.21.01`）——NuGet 按 SemVer2 拒绝数字前导零的 prerelease 标识，且错误会被 MSBuild 吞成 `MSB4181`。
- 命名空间根 `Ceobe`；用户可见产物名保持小写 `ceobe.exe`。
- **所有下载（开发期与运行期）一律优先国内镜像**：运行期经 `core/Util/Mirrors` 改写 URL（Mojang → BMCLAPI、Fabric meta → BMCLAPI、Modrinth CDN → mcimirror、Temurin → 清华），失败自动回退上游。新增第三方源前先确认镜像可用。
- 所有对外 HTTP 请求必须带 User-Agent（Modrinth 硬要求）。
- git commit 遵循 Conventional Commits：type/scope 用英文，描述用简体中文；subject ≤72 字符；body 分点。**训练员指定本仓库提交不需要 GPG 签名**（已设 repo-local `commit.gpgsign=false`）。
- ViewModel 层**不得引用 WinUI 类型**：这是闸门 C 的前提，保证逻辑可无窗口单测。

### 不得重写的硬契约

1. `server.properties` 写回保序、保注释、保转义（`level-type=minecraft\:normal`）。
2. 镜像 URL 改写与失败回退。
3. Paper Fill v3 响应契约（`downloads["server:default"].url` + sha256）。
4. ADR-007 进程托管：监督进程 + `.ctl` 随机 token + `.state`/`.pid`，无全局 daemon。
5. 端口在实例间排他（`assertPortExclusive` / `suggestFreePort`），穿透通道一律指向实例自己的 `meta.port`。
6. `.mrpack`：跳过 `env.server=unsupported` 的文件；`server-overrides/` 优先于 `overrides/`。
7. CalVer 单点权威。

### 已知易错点（踩过）

- `Ceobe.App` 发布必须同时开 `SelfContained` **和** `WindowsAppSDKSelfContained`：前者带 .NET 运行时，后者带 WinUI 原生部分（`Microsoft.ui.xaml.dll`、`resources.pri`）。只开前者在开发机上能跑，在用户机上开窗即崩。
- 非打包 WinUI 的 `FileOpenPicker` 需要 HWND，`UserControl` 拿不到 → 导入一律走**拖放 + 手填路径**。
- `Window` 不是 `FrameworkElement`：转换器 / 主题 / 某些 x:Bind 需要把内容放进 `UserControl`，主题切换要设 `Window.Content` 的 `RequestedTheme`。
- `System.CommandLine` 必须设 `EnableDefaultExceptionHandler = false`，否则退出码契约失效。
- XAML 的 `x:Bind` 在 `DataTemplate` 里默认 `OneTime`：会变的属性必须显式 `Mode=OneWay`。

## 目录速查

- `src/Ceobe.Core/` — 域核心：模型、存储、配置、providers、运行时、JDK、Mods、玩家名单、隧道、内嵌 Web 控制台
- `src/Ceobe.App.ViewModels/` — 全部界面状态与逻辑（零 WinUI 引用）
- `src/Ceobe.App/` — WinUI 3 壳：XAML 视图、转换器、接线
- `src/Ceobe.Cli/` — System.CommandLine 无头入口
- `tests/` — `Ceobe.Core.Tests`（域核心 + 闸门 A/B）、`Ceobe.App.ViewModels.Tests`（闸门 C）
- `tools/wiregen/` — 闸门 B 的真值 fixture 生成器（真实 Jackson + 真实 Java 模型类）
- `docs/PRD.md` — 产品需求；`docs/ARCHITECTURE.md` — 结构与外部契约；`docs/DECISIONS.md` — ADR；`docs/specs/` — 模块规格；`docs/MIGRATION.md` — 迁移施工图与进度
- `ceobe-core/`、`ceobe-cli/`、`ceobe-gui/`、`pom.xml` — **冻结的 Java 实现**，只作语义参考，不参与构建
- `.github/workflows/windows.yml` — CI：Windows 编译 + 测试；推 `v*` 标签时打包发布 Release
