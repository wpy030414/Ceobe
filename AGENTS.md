# AGENTS.md

> **当前形态**：C# / .NET 10 + WinUI 3，**Windows 专属**（ADR-019）。
> 迁移施工图与进度见 `docs/MIGRATION.md`；技术决策见 `docs/DECISIONS.md`。
> **Java 版已于 2026-10-21 全部移除**——迁移完成即退役，仅存于基线 tag `pre-csharp-migration`
> （需要考古或决定「要不要把 hotfix 回打旧版」时从 tag 取）。

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
# GUI 不在解构建范围内，必须显式编（原因见「已知易错点」）
dotnet build src/Ceobe.App/Ceobe.App.csproj -c Debug

# 发布 —— 两档 GUI 产物（CI 用同一组命令，见 .github/workflows/windows.yml）
# win11 档：框架依赖，目标机需自备 .NET 10 运行时 + Windows App Runtime 2.5.x（约 79M）
dotnet publish src/Ceobe.App/Ceobe.App.csproj -c Release -r win-x64 --self-contained false -p:WindowsAppSDKSelfContained=false -o dist/app-fd
# portable 档：双自包含，免装运行时（约 232M）；两个开关缺一不可，理由见「已知易错点」
dotnet publish src/Ceobe.App/Ceobe.App.csproj -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o dist/app-sc
dotnet publish src/Ceobe.Cli/Ceobe.Cli.csproj -c Release -r win-x64 --self-contained true -o dist/cli

# MSI（负载 = win11 档；per-user 装到 %LOCALAPPDATA%\Programs\ceobe，不触发 UAC）
dotnet tool restore
dotnet wix build -arch x64 -d MsiVersion=26.10.22 -d PayloadDir=<上面的 app-fd 绝对路径> -o ceobe.msi tools/installer/ceobe.wxs
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

- `Ceobe.App` 发布必须同时开 `SelfContained` **和** `WindowsAppSDKSelfContained`：前者带 .NET 运行时，后者带 WinUI 原生部分（`Microsoft.ui.xaml.dll`、`Ceobe.pri`）。只开前者在开发机上能跑，在用户机上开窗即崩。
- 非打包 WinUI 的 `FileOpenPicker` 需要 HWND，`UserControl` 拿不到 → 导入一律走**拖放 + 手填路径**。
- **`NavigationView.MenuItemsSource` 有三条原生禁区**（2026-10-22 实测，崩溃均发生在原生回调里，事件日志只有 `0xc000027b`/`0xc0000005`，没有托管栈）：① 从代码赋值 `MenuItemsSource` → 崩；② 对绑定集合调 `Clear()`（发出 `Reset`）→ 容器整表重建时崩；③ 用 `MenuItemTemplateSelector` 时行类型在模式间切换 → 容器回收时 AV，选择器返回 `null` 还会抛 `Null encountered as data template`（`E_INVALIDARG`）。可行组合只有一种：**XAML 里一个内联 `MenuItemTemplate` + 行类型收敛到 `INavRow` + 集合逐项增删**（见 `Controls/NavRowMirror.cs`）。
- `Window` 不是 `FrameworkElement`：转换器 / 主题 / 某些 x:Bind 需要把内容放进 `UserControl`，主题切换要设 `Window.Content` 的 `RequestedTheme`。
- `System.CommandLine` 必须设 `EnableDefaultExceptionHandler = false`，否则退出码契约失效。
- XAML 的 `x:Bind` 在 `DataTemplate` 里默认 `OneTime`：会变的属性必须显式 `Mode=OneWay`。
- **构建/发布前先关掉正在运行的 `Ceobe.exe`**：它锁住输出目录里的 DLL，MSBuild 要么报 `MSB3027`，
  要么在某些增量路径下跳过复制却仍显示成功——于是你跑的还是旧二进制，端到端会得出错误结论（实测踩过）。
- **GUI 产物有两条路径，别拿错**：`ceobe.slnx` 已把 `Ceobe.App` 映射到 x64，解构建会输出到
  `bin/x64/Release/...`；显式 `dotnet build src/Ceobe.App/Ceobe.App.csproj` 输出到 `bin/Release/...`。
  端到端验证/发布前先确认你运行的是哪一个（发布请用 `dotnet publish ... -o dist/app`，不要手动拼 bin 路径）。
  CI 仍单列 GUI 构建步骤作为护栏。
- **深色主题必须有一层纯色回退**：根元素只设 `Transparent` 而没有任何背景层时，深色下文字变白、窗口底仍白，
  整页不可读。`MainWindow.xaml` 的 `PageBackground` 是纯色回退层；Mica 仅在「跟随系统」且
  `MicaController.IsSupported()` 时启用并隐藏该层，显式浅色/深色与 Win10 一律用纯色。
- **`ContentControl` / `TabView` 默认不竖向拉伸**：`ContentControl` 默认 Left/Top，`TabView` 的默认样式
  写死 `VerticalAlignment=Top`；直接承载带 `*` 行的页面会让日志/列表塌成 0 高度。页面宿主必须是
  显式 `HorizontalContentAlignment=Stretch`、`VerticalContentAlignment=Stretch` 的 Grid，分页靠 Visibility。
- **`NavigationView.MenuItemTemplate` 会作用到 `FooterMenuItems`**：静态 footer 项会被实例行模板错误渲染成空白。
  底部动作改用 `PaneFooter` + 普通 Button。
- **`ContentDialog` 在 PopupRoot 里**，不继承 `Window.Content` 的 `RequestedTheme`；打开前必须显式设置
  （`ThemeApplier.ApplyToDialog`），否则深色应用会弹出浅色对话框。
- **`Button` 的 Content 是 StackPanel 时 UIA Name 为空**：图标+文字按钮不会聚合子 TextBlock 的名称，
  必须显式 `AutomationProperties.Name`；图标按钮同理。CI 的 UIA smoke 会抓这类回归。
- **`TitleBar.IsBackButtonVisible` 是 bool，`NavigationView.IsBackButtonVisible` 是枚举**：别套错。
- **WinUI `FontFamily` 不支持 `"Cascadia Mono,Consolas"` 这种逗号回退**：未装 Cascadia 的机器会静默退回 UI 字体。
  等宽文本统一用 `{StaticResource CeobeMonoFont}`（Consolas）。
- **同一单元格叠放多个输入控件会回写坏值**：隐藏控件仍参与 x:Bind 初始化——实测把 `motd` 改成 `0` 并制造假校验错误。
  字段输入必须用 `DataTemplateSelector` 只实例化匹配类型的那一个控件（见 `PropFieldRow`）。
- **测试端口 25565 会被本机运行中的实例占用**：跑 `dotnet test` 前先停掉使用该端口的服务器。
- **BMCLAPI 的 Mojang version_manifest 端点实测 10s+ 或空响应**：版本清单只给镜像 2 秒预算，
  超时/空体立刻回退直连（本机直连约 0.7s），并在进程内缓存 30 分钟；应用启动时后台预热。
- **不要用 UIA 脚本对正在运行的实例做 FindAll / 打开 ContentDialog**：WinUI 3 的 UIA provider 在模态弹窗或布局期间可能死锁，实测会把 Ceobe 卡到无响应。UIA 冒烟只在独立 `CEOBE_HOME` 的临时实例上跑，且不要对用户正在看的实例执行。
- 本地 UI 冒烟：`tools/ui-smoke/Invoke-UiSmoke.ps1`（需交互式桌面与已关闭的 Ceobe.exe；GitHub-hosted CI 跑不了）。

## 目录速查

- `src/Ceobe.Core/` — 域核心：模型、存储、配置、providers、运行时、JDK、Mods、玩家名单、隧道、内嵌 Web 控制台
- `src/Ceobe.App.ViewModels/` — 全部界面状态与逻辑（零 WinUI 引用）
- `src/Ceobe.App/` — WinUI 3 壳：`Views/` 页面与工作区、`Styles/` 设计令牌、`Controls/` 复用控件、XAML 对话框、转换器与接线
- `src/Ceobe.Cli/` — System.CommandLine 无头入口
- `tests/` — `Ceobe.Core.Tests`（域核心 + 闸门 A/B）、`Ceobe.App.ViewModels.Tests`（闸门 C）
- `tools/wiregen/` — 闸门 B 的真值 fixture 生成器（真实 Jackson + 真实 Java 模型类）；Java 源码已移除，重跑前需从 tag `pre-csharp-migration` 取出对应模型
- `tools/ui-smoke/` — 本地交互式 UIA 冒烟（不跑 CI）：导航/分区/日志高度/可访问名/对话框/浅色深色亮度
- `tools/installer/ceobe.wxs` — MSI 定义（WiX v6）；版本经 `dotnet-tools.json` 锁定，CI 用 `dotnet tool restore` 取
- `docs/PRD.md` — 产品需求；`docs/ARCHITECTURE.md` — 结构与外部契约；`docs/DECISIONS.md` — ADR；`docs/specs/` — 模块规格；`docs/MIGRATION.md` — 迁移施工图与进度
- 已退役的 Java 实现不在工作树中：需要对照时从 tag `pre-csharp-migration` 取
- `.github/workflows/windows.yml` — CI：**不跑测试**，只编译 + 闸门 C + 两档产物 + MSI + 完整性校验；
  main 推送即发布 Release，版本只读 `Directory.Build.props`（不读 tag）。测试仍在本地跑。
