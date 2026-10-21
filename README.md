# ceobe

本机 Minecraft 服务器编排系统：多实例创建/启动/停止、版本与加载器选配、配置可视化、Mod 与整合包导入、玩家名单、内网穿透。**Windows 桌面 GUI（WinUI 3）+ docker 风格无头 CLI** 双形态，共享同一套域核心。

## 这是什么？

- 定位：单机玩家的「私人服主面板」——在本机一键开多个 MC 服务器，让朋友通过公网地址直连。
- 解决的核心问题：开一个 mod 服要手动下载 jar、改 properties、装 mod、配穿透、记命令行——ceobe 把这条链路压缩为 GUI 几步或 CLI 一行。

## 为什么存在？

- 官方/社区工具（server jar、Modrinth、cloudflared）各自独立，缺少把「版本 → 配置 → mod → 穿透 → 运行」串起来的本机编排层。
- 现有面板（Pterodactyl 等）面向 Linux 服务器/容器，Windows 桌面玩家没有顺手的轻量选择。

## 如何安装和运行？

- 前置要求：**Windows 10 1809+ / 11**，无需安装 .NET（发布产物自包含）。可选：`cloudflared` 在 PATH（Web 控制台穿透）、系统 `ssh` 客户端（Pinggy 玩家通道）。
- 各实例所需的 Java 由 ceobe 按游戏版本自动探测——缺失时提醒，并可 `ceobe jdk install <主版本>` 按平台一键安装（Adoptium + 清华镜像），创建时自动选用。
- 构建：

  ```powershell
  dotnet build ceobe.slnx -c Release
  dotnet test  ceobe.slnx -c Release

  dotnet publish src/Ceobe.App/Ceobe.App.csproj -c Release -r win-x64 --self-contained true -o dist/app
  dotnet publish src/Ceobe.Cli/Ceobe.Cli.csproj -c Release -r win-x64 --self-contained true -o dist/cli
  ```

- 运行：`dist/app/Ceobe.exe`（GUI）或 `dist/cli/ceobe.exe <命令>`（无头 CLI）。

## 仓库结构

```
ceobe/
├── src/Ceobe.Core/            域核心：实例 / 进程 / provider / 配置 / Mod / 玩家 / 穿透（无 UI 依赖）
├── src/Ceobe.App.ViewModels/  全部界面状态与逻辑（零 WinUI 引用，可无窗口单测）
├── src/Ceobe.App/             WinUI 3 界面（XAML + 转换器 + 接线）
├── src/Ceobe.Cli/             无头 CLI（System.CommandLine）
├── tests/                     Core 与 ViewModel 两套测试
├── tools/wiregen/             与 Java 对读用的真值 fixture 生成器
└── docs/                      PRD / ARCHITECTURE / DECISIONS / specs / MIGRATION
```

- 依赖方向单向：`App → App.ViewModels → Core`，`Cli → Core`；`Core` 不感知 UI（保证无头环境可跑）。
- 版本号 **CalVer `YY.MM.DD-HH`**，`Directory.Build.props` 的 `<Version>` 是唯一权威。

## 快速上手（CLI，docker 风格）

```powershell
$J = "dist/cli/ceobe.exe"

& $J doctor                                  # 环境自检（JDK / cloudflared / ssh / 目录）
& $J jdk list                                # 运行期 Java 与 ceobe 自管 JDK
& $J jdk install 17                          # 某版本需要时一键装（清华镜像）
& $J create smp --type paper --version 26.3 --mem 4G --port 25565   # 端口需排他；--port auto 自动选空
& $J start -d smp                            # 后台启动（独立监督进程，可跨终端管理）
& $J ps                                      # 运行中实例；ps -a 含已停止
& $J config set smp view-distance 12         # 改配置（类型校验；运行中需重启生效）
& $J update smp --mem 4G --port auto         # 改实例设置
& $J exec smp "say 大家好啊"                  # 下发服务端命令
& $J logs -f smp                             # 实时跟随日志（Ctrl+C 退出且不杀服）
& $J tunnel up smp --kind pinggy             # 开玩家通道 → 得到 tcp://… 地址发给朋友
& $J stop smp                                # 优雅停止
& $J rm smp --purge                          # 删除实例（含世界数据）
```

输出支持 `--format json`（键名稳定，可 jq 解析）；退出码 0 成功 / 1 运行失败 / 2 用法错误。

## 内网穿透说明（实测结论）

| 通道 | 用途 | 免登录 | 实测结论 |
|------|------|--------|----------|
| **Cloudflare Quick Tunnel** | Web 控制台（HTTP） | ✅ | 可建立，但边缘节点只承载 HTTP(S)——**无法传递原版 MC 的裸 TCP** |
| **Pinggy** (`ssh tcp@`) | 玩家直连 MC（TCP） | ✅ | 形如 `tcp://xxx.a.pinggy.link:35883`；免费会话 60 分钟，约 55 分钟自动续期并刷新地址 |
| **Tailscale** | 长期联机（推荐） | ❌ 需装客户端并登录 | 地址稳定，不受 60 分钟会话限制 |
| **局域网直连** | 同网段联机 | — | 直接展示本机 `ip:端口` |

- 因此：**CF 隧道用于远程查看状态（Web 控制台），玩家连接走 Pinggy（临时）或 Tailscale（长期）**。依据见 ADR-003。
- 界面上的每条通道都会给出**可达性三态**：已实测可达 / 无法验证 / 未探测——三者措辞严格区分，不把「没测」说成「失败」。

## 下载与镜像（已实测加速）

所有下载优先国内镜像，失败自动回退官方源：

| 上游 | 镜像 |
|------|------|
| Mojang piston-meta / piston-data | BMCLAPI `bmclapi2.bangbang93.com`（同路径换主机） |
| Fabric meta | BMCLAPI `/fabric-meta` |
| Modrinth CDN | `mod.mcimirror.top` |
| Temurin JDK | 清华 `mirrors.tuna.tsinghua.edu.cn/Adoptium` |

- 关闭或改写镜像：`CEOBE_MIRROR=off`、`CEOBE_MIRROR_BMCLAPI=<base>`、`CEOBE_MIRROR_MODRINTH=<base>`。
- 无镜像的源（PaperMC Fill、Purpur API、`maven.neoforged.net`、`api.modrinth.com`）仍直连。
- 数据目录：默认 `%USERPROFILE%\.ceobe`，可在设置里搬走；`CEOBE_HOME` 可覆盖（测试用）。

## 版本号与发布

- 版本号 = **CalVer `YY.MM.DD-HH`**，`Directory.Build.props` 的 `<Version>` 是唯一权威，CI 只读不写。
- 该值写作**点分四段** `26.10.21.01`：NuGet 按 SemVer2 拒绝数字前导零的 prerelease 标识（`26.10.21-01` 会失败），展示形态由 `YY.MM.DD-HH` 派生。
- 发布：推 `v*` 标签 → CI 打包 `ceobe-gui-v*.zip` 与 `ceobe-cli-v*.zip` 并创建 Release。

## 打包与发布（CI）

- `.github/workflows/windows.yml`：单一 `windows-latest`。
  - push `main` / PR → restore + build + test（网络用例默认排除）
  - push `v*` 标签 → 额外发布 GUI/CLI 自包含产物
- GUI 发布必须同时开 `SelfContained` 与 `WindowsAppSDKSelfContained`，否则产物缺 WinUI 原生运行时。

## 验收与测试

```powershell
dotnet test ceobe.slnx                       # 全量（网络用例默认排除）
dotnet test ceobe.slnx --filter "Category=network"   # 真实下载服务端并跑完整生命周期（慢，受网络影响）
```

## 当前状态

- 阶段：Core / CLI / GUI 三件套已迁完；余下是训练员对新界面的验收与 Java 源码归档确认（见 `docs/MIGRATION.md`）。
- 已知限制：NeoForge 安装器源（`maven.neoforged.net`）在部分网络不可达；Pinggy 免费通道 60 分钟会话，地址随续期轮换。

## 核心技术

- .NET 10 / C#（`Ceobe.Core`、`Ceobe.App.ViewModels`、`Ceobe.App`、`Ceobe.Cli`）
- WinUI 3 + Windows App SDK 2.5.1（非打包自包含）、CommunityToolkit.Mvvm
- System.CommandLine 2.0-beta5、System.Text.Json
- 外部 API：Mojang piston-meta、PaperMC Fill v3、Fabric meta v2、NeoForge maven、Purpur API、Modrinth API v2、Adoptium
