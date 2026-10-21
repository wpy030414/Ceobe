# DECISIONS

## ADR-001：构建系统用 Maven（弃 Gradle）

> **状态：已被 ADR-019 取代（2026-10-21）。** 现役构建为 .NET SDK / MSBuild（`ceobe.slnx`）；Maven 仅存在于归档的 Java 版中，不参与构建。保留本条作决策考古。
- 日期：2026-10-15
- 状态：已采纳
- 背景：仓库原有项目为 Maven；本机未预装任何构建工具。
- 考虑过的方案：Gradle 9.8.1（腾讯镜像可用）、Maven 3.9.16（清华镜像）。
- 决策：Maven 3.9.16 + Aliyun 镜像（`~/.m2/settings.xml`）。
- 为什么选这个：用户明确指定（"gradle 太多问题"）；多模块 + JavaFX（javafx-maven-plugin）+ shade 打包路径成熟。
- 为什么不选其他：Gradle 版本兼容矩阵与 wrapper 引导在本环境增加不确定面。
- 后果：jpackage/jlink 打包走 `maven-shade` + 外层 `jpackage` 脚本。
- 何时重新审视：需要复杂变体构建（多平台 runtime image）时。

## ADR-002：GUI 选 JavaFX（弃 Swing）

> **状态：已被 ADR-019 取代（2026-10-21）。** 现役 GUI 为 WinUI 3 + Windows App SDK；JavaFX 版已冻结。保留本条作决策考古。
- 日期：2026-10-15
- 状态：已采纳
- 背景：要求 shadcn/ui 风格 + JDK 25。
- 考虑过的方案：Swing + FlatLaf；JavaFX + AtlantaFX。
- 决策：JavaFX 25.0.4 + AtlantaFX 3.0.0 + 自定义 CSS。
- 为什么选这个：JavaFX 的 CSS 皮肤体系可直接表达 shadcn 观感（zinc 色板、8px 圆角、1px hairline、focus ring）；AtlantaFX 提供成熟控件皮肤基座。
- 为什么不选其他：Swing 实现同等质感需大量自绘，性价比低。
- 后果：GUI 模块依赖 openjfx win 分类器 artifact，无头 CLI 模块保持零 JavaFX 依赖。
- 何时重新审视：无。

## ADR-003：穿透架构 = CF Quick Tunnel（HTTP）+ Pinggy（TCP）双通道 SPI
- 日期：2026-10-15
- 状态：已采纳
- 背景：需求为"CF 免登录内网穿透，便于其他玩家连接"；需验证技术可行性。
- 考虑过的方案（全部实测）：
  1. CF Quick Tunnel `--url tcp://localhost:25565`：隧道能建立，但对边缘 443/2053/2083/2087/2096/8080/8880 裸 TCP 探测全部返回 `HTTP/1.1 400 Bad Request` —— 边缘只讲 HTTP(S)，原版 MC 客户端无法接入；
  2. bore.pub：中继国内 12s 超时不可达；
  3. playit.gg：需网页 claim（准登录），国内可达性未验证；
  4. CF Named Tunnel：要登录，且玩家侧需 cloudflared access 客户端，违背"其他玩家直连"；
  5. Pinggy `ssh -p 443 -R0:localhost:25565 tcp@a.pinggy.io`：免登录，实测返回 `tcp://<host>:<port>` 玩家可直连；限制 60min 会话。
- 决策：TunnelProvider SPI；默认装配 `CloudflareQuickTunnel`（穿 core 内嵌 Web 控制台，纯 HTTP）+ `PinggyTunnel`（穿 MC 25565，到期前 5 分钟自动重连续期并刷新地址）+ `LanDirect`（局域网直连展示）。
- 为什么选这个：CF 通道保留（满足"CF 集成"的诉求面：Web 控制台穿透），玩家 TCP 用唯一实测可用的免登录方案。
- 后果：Pinggy 免费地址随续期轮换，UI 必须实时展示最新地址并提供一键复制；SPI 预留 playit/named-tunnel 扩展位。
- 何时重新审视：CF Quick Tunnel 开放裸 TCP、或 playit.gg 免登录回归时。

## ADR-004：服务端支持集 = vanilla / paper / fabric / purpur（NeoForge 暂缓）
- 日期：2026-10-15
- 状态：已采纳
- 背景：需确定 provider 首发集合。
- 考虑过的方案：全集（含 NeoForge/Forge/Quilt/Spigot-BuildTools）。
- 决策：首发 vanilla（piston-meta）、paper（Fill v3）、fabric（meta v2 + installer）、purpur（官方 API）；NeoForge 因 `maven.neoforged.net` 本网络连接超时暂缓（SPI 保留位）；Forge/Quilt/BuildTools 不做。
- 为什么选这个：四个源全部实测 200 OK；覆盖"轻量→高性能→模组→魔改"主流谱系。
- 后果：fabric 安装为子进程跑 installer，需处理下载 Mojang server（`-downloadMinecraft`）耗时。
- 修订（同日）：NeoForge 已接入——VPN 下 `maven.neoforged.net` 可达（HTTP 200），启动方式不同（参数文件），见 ADR-010。
- 何时重新审视：NeoForge 源可达或找到镜像（BMCLAPI）时。

## ADR-005：状态存储 = `~/.ceobe/` 文件树 + 每实例文件锁
- 日期：2026-10-15
- 状态：已采纳
- 背景：CLI 与 GUI 可能并发操作同一实例；无守护进程架构（对齐 docker 直觉但避免常驻 daemon）。
- 考虑过的方案：常驻 daemon + 本地 socket（docker 模式）；SQLite；纯文件。
- 决策：纯文件——`~/.ceobe/instances/<name>/`（`instance.json` 元数据 + 服务器目录 + `.state`/`.pid` 运行态 + `.ctl` 控制通道凭据）；实例列表通过扫描目录得出（不维护中央索引，避免索引与目录不一致）。
- 为什么选这个：无 daemon 意味着崩溃后零残留状态、可用任意编辑器抢救；可调试性最高。
- 为什么不选其他：daemon 增加一层生命周期管理；SQLite 对"一个实例一个目录"的直觉不友好。
- 后果：跨进程日志流（`logs -f`）通过 core 统一落盘的 `console.log` 追加实现；强制停止导致控制通道凭据残留时，客户端按「连接被拒」自动清理（见 ADR-007 与 `InstanceControl.call`）。
- 修订（同日）：原方案的 `<dir>/.lock`（FileLock）在实现中由 ADR-007 的监督进程独占 + 控制通道 token 取代——单实例只能有一个监督进程持有进程句柄，互斥语义更强且无需锁文件。
- 何时重新审视：引入远程多机管理时。

## ADR-006：MC 版本策略 = 26.x 新纪年 + 1.21.x 兼容线
- 日期：2026-10-15
- 状态：已采纳
- 背景：Mojang 已切换年份版本号（latest.release=26.3，snapshot=26.4-snapshot-4），且 26.3 起官方要求 Java 25（javaVersion.majorVersion=25，实测 JDK 25.0.1 启动通过）。
- 决策：版本选择器按"26.x / 1.21.x / 1.20.x"分组展示；实例记录 javaHome（默认继承系统 JAVA_HOME）；server.properties 采用 26.3 实测生成的键集为 schema 基线（含新键 `management-server-*`、`pause-when-empty-seconds`、`chat/command-spam-threshold-seconds` 等；`white-list` 生成默认已为 true）。
- 后果：1.20.5 前版本需 Java 17/21 运行时，UI 需提示（MVP 仅支持 1.20.5+）。
- 何时重新审视：Mojang 再改版本语义时。

## ADR-007：进程托管 = 每实例「监督进程 + 本机控制通道」（无全局 daemon）
- 日期：2026-10-15
- 状态：已采纳
- 背景：CLI 语法对齐 docker（`start/stop/exec/logs`），但 docker 依赖常驻 daemon 保存进程句柄；本项目要求无 daemon 也能跨进程优雅停止服务端（stdin 直写 `stop`，避免强杀导致世界损坏）。
- 考虑过的方案：
  1. 纯文件状态 + 强杀停止：简单，但无法优雅停机，有存档损坏风险；
  2. 全局常驻 daemon + 本地 socket：最贴近 docker，但引入 daemon 生命周期、端口占用与崩溃恢复成本；
  3. 每实例监督进程 + 控制通道。
- 决策：方案 3 —— `ceobe start -d` 派生独立 JVM（`Supervisor`），它托管 `ServerProcess` 并在 `127.0.0.1:随机端口` 开放控制通道（`ControlServer`），凭据为随机 token 写入实例目录 `.ctl`；`stop/exec/logs/tunnel` 等由任意 CLI 进程经该通道下发。
- 为什么选这个：无需常驻进程即可跨进程优雅停止；崩溃后零残留状态（无 daemon 需恢复）；GUI 启动实例时同样只是"另一个监督进程"，CLI 与 GUI 可混合使用同一实例。
- 为什么不选其他：方案 1 有数据风险；方案 2 复杂度与故障面显著更高，收益不明显。
- 后果：`Supervisor` 属于 `ceobe-cli` 模块（core 只提供 `ControlServer`/`InstanceControl` 原语）；控制通道仅绑定环回地址，必须保持"本机可信"假设。
- 何时重新审视：需要远程（跨机）管理时，届时控制通道需升级为带认证的网络协议。

## ADR-008：包名与 Maven 坐标 = `im.xrl.ceobe.*` / `im.xrl:ceobe-*`

> **状态：已被 ADR-019 取代（2026-10-21）。** C# 侧命名空间根为 `Ceobe`，工程名 `Ceobe.Core` / `Ceobe.Cli` / `Ceobe.App` / `Ceobe.App.ViewModels`，用户可见产物名仍为小写 `ceobe.exe`。
- 日期：2026-10-15
- 状态：已采纳
- 背景：初始骨架使用 `im.xrl.ceobe` 作为临时命名空间；项目所有者要求改为自有域名反写。
- 决策：groupId `im.xrl`，artifactId `ceobe` / `ceobe-core` / `ceobe-cli` / `ceobe-gui`；Java 包前缀 `im.xrl.ceobe`；资源路径随之迁移（`/im/xrl/ceobe/gui/shadcn.css`）。
- 为什么不选其他：`im.xrl.ceobe` 不是任何真实持有域，作为长期坐标不合适。
- 后果：历史提交已整体重写以保持一致（仓库为新建，无外部引用）。
- 何时重新审视：项目发布到公共仓库并确定最终域名时。

## ADR-009：端口在实例间排他（穿透地址须唯一对应）
- 日期：2026-10-15
- 状态：已采纳
- 背景：本机可并存多个实例，而玩家穿透通道（Pinggy）直接转发到 `meta.port`。若两个实例共用端口，玩家的公网地址会连到错误的服务端，第二个实例启动时也会 bind 失败——表现为"穿透莫名其妙不通"。
- 考虑过的方案：① 不作约束，靠启动时 bind 失败兜底；② 允许共享端口、按实例再区分；③ 强制排他 + 自动选空端口。
- 决策：方案 ③ —— 创建与启动前经 `InstanceManager.assertPortExclusive` 拒绝重复端口（报出占用者名称）；`suggestFreePort` / CLI `--port auto` / GUI 默认值给出空闲端口；启动时另做 OS 层 bind 预检（`ServerProcess.checkPortFree`）。
- 为什么选这个：穿透通道以端口为唯一路由依据，端口唯一性即通道唯一性；把冲突挡在创建阶段（先于任何下载）体验最好。
- 为什么不选其他：方案 ① 报错点滞后到启动，且无法阻止"两个已停止实例都配了 25565"的隐患；方案 ② 需要额外的路由/命名空间，复杂度不成比例。
- 后果：CLI `--port` 由 int 改为接受 `auto`；GUI 新建对话框自动填入空闲端口并显示排他提示。
- 修订（同日）：GUI 主题资源路径 `/dev/ceobe/gui/*` 修正为 `/im/xrl/ceobe/gui/*`——此前 CSS 与字体从未加载（`getResource` 返回 null 静默跳过），界面实为 AtlantaFX 默认外观；同时修复主内容区每轮轮询重建页签导致的"抽搐 + 强制回弹到控制台"。
- 何时重新审视：引入端口映射/反代层（如统一网关）时，可为每个实例再分配一个对外端口。

## ADR-010：接入 NeoForge（参数文件启动 + 版本映射）
- 日期：2026-10-15
- 状态：已采纳
- 背景：ADR-004 因 `maven.neoforged.net` 不可达而暂缓 NeoForge；现 VPN 下可达（metadata 与 installer 实测 200），且 NeoForge 是 1.20.2+ 的主流模组加载器。
- 考虑过的方案：① 继续暂缓；② 接入但复用 `-jar` 启动；③ 接入并引入参数文件启动。
- 决策：方案 ③ —— 新增 `ServerType.NEOFORGE` 与 `NeoForgeProvider`：下载 `neoforge-<v>-installer.jar` 跑 `--installServer`；产物无单一启动 jar，改用 `@libraries/net/neoforged/neoforge/<v>/<win|unix>_args.txt` 启动。为此 `InstanceMeta.launchArgsFile` 非空时 `ServerProcess` 以 `@file` 取代 `-jar`。版本映射：NeoForge 版本 = 归一化 MC 版本 + `.` + build（1.x 去前导 `1.`：1.21.8→21.8；26.x 保留）。
- 为什么选这个：参数文件（官方 run.bat 的方式）内含 `-classpath`/主类/`--fml.*`，最稳；复用 `-jar` 需自造 main，与启动链耦合易碎。
- 为什么不选其他：继续暂缓会缺主流模组生态；自造 main 维护成本高。
- 后果：启动命令不再统一为 `-jar`（`InstanceManager.start` 的存在性检查按 `launchArgsFile` 分支）；`instance.json` 增加 `launchArgsFile` 字段（旧实例为 null，兼容）。
- 何时重新审视：NeoForge 更改启动方式，或引入统一 modloader 抽象时。

## ADR-011：运行时 JDK 由 ceobe 托管（探测 + 一键安装 + 自动选用）
- 日期：2026-10-15
- 状态：已采纳
- 背景：不同 MC 版本要求不同 Java（实测 1.20.4→17、1.21.8→21、26.x→25）；用户机器只装一个 JDK 时，开老版本服可能不兼容。用户要求「需要 JDK 时提醒，并能按平台一键装好，也可自己装」。
- 考虑过的方案：① 只用宿主 JVM，不兼容即报错；② 要求用户自备并手动 `--java-home`；③ ceobe 探测 + 一键安装 + 自动选用。
- 决策：方案 ③ —— 由 piston-meta 的 `javaVersion` 探测所需主版本；自管 JDK 落在 `~/.ceobe/jdk/`（Adoptium Temurin，清华镜像优先）；create 时 auto-select（显式 > 自管命中 > 宿主）；CLI/GUI 提示；`ceobe jdk install <n>` 一键装、`jdk use <实例> <n>` 改指。
- 为什么选这个：把「环境准备」收敛进产品，契合 PRD「10 分钟开出第一个服」；只落主目录、不动系统 PATH/注册表，可整体删除，回退零成本。
- 为什么不选其他：方案① 把兼容负担丢给用户；方案② 体验割裂。
- 后果：引入对 Adoptium API/镜像与归档解压的依赖；`instance.json` 的 `javaHome` 可能是自管路径（非用户原始输入）。
- 何时重新审视：Adoptium 变更 API，或需支持更多架构/发行版时。

## ADR-012：下载一律优先国内镜像站（回退上游）
- 日期：2026-10-15
- 状态：已采纳
- 背景：Mojang / Modrinth / Adoptium 等源在国内直连缓慢（实测 vanilla 26.3 服务端 62MB 直连约 57s）。项目所有者要求「任何下载，无论开发期还是运行期，一律走镜像站」。
- 考虑过的方案：① 每个 provider 各自处理镜像；② 在 HTTP 层统一改写 URL 并回退。
- 决策：方案 ② —— 新增 `core/util/Mirrors`，在 `Http.get/getBytes/download` 统一「镜像优先、失败回退上游」。映射（实测可用）：Mojang piston-meta/piston-data/launcher → BMCLAPI（同路径换主机）；Fabric meta → BMCLAPI `/fabric-meta`；Modrinth CDN → `mod.mcimirror.top`；Temurin JDK 已在 `JdkInstaller` 走清华。可用 `-Dceobe.mirror=off` 关闭、`-Dceobe.mirror.<name>=` 覆盖基址。
- 为什么选这个：一处改写覆盖全部下载路径（含未来新 provider 的 `Http` 调用）；回退保证了镜像故障时不阻塞。
- 为什么不选其他：逐 provider 处理会重复逻辑且易漏。
- 后果：镜像可能内容滞后/损坏 → 通过既有 sha1/sha256 校验兜底，校验失败即回退上游重下。**无镜像的源**（PaperMC Fill、Purpur API、`maven.neoforged.net`、`maven.fabricmc.net`、`api.modrinth.com`）仍直连，需在新增源时确认镜像可用性。
- 修订（同日）：Fabric 安装改为「先经镜像预拉原版 server.jar，再让安装器生成本加载器」，避免安装器内部从 Mojang 慢下载（去掉 `-downloadMinecraft`）。
- 何时重新审视：出现更优镜像，或某个镜像长期不可用时。

## ADR-013：整合包（Modrinth `.mrpack`）接入
- 日期：2026-10-15
- 状态：已采纳
- 背景：项目所有者要求「允许接入整合包」。`.mrpack` 实为 zip，含 `modrinth.index.json`（`files[]` + `dependencies` + `overrides/`）。
- 决策：新增 `mods/MrpackInstaller`（解析索引、按 `env.server != unsupported` 下载 files、解包 `server-overrides` 优先再 `overrides`）与 CLI `ceobe modpack search|info|install|new`。`new` 按索引 dependencies（minecraft + fabric-loader/neoforge）自动创建匹配实例再安装（一键）；`install` 校验已有实例的加载器与版本一致后安装。
- 为什么选这个：`new` 给出一键体验；`install` 允许装进既有实例，两种用法互补。
- 后果：仅支持 fabric / neoforge 加载器（forge/quilt 的整合包报错说明）；文件经 `Http` 下载故自动走镜像（Modrinth CDN → mcimirror）。
- 何时重新审视：需支持 forge/quilt 整合包，或 CurseForge 平台时。

## ADR-014：Modrinth 版本选择取「首个 release」
- 日期：2026-10-15
- 状态：已采纳
- 背景：接入整合包时发现 `ModrinthClient.pickPreferred` 取「最后一个 release」，而 Modrinth 版本接口按发布时间**降序**返回 → 实际拿到最老版本（Fabulously Optimized 解析成了 1.12.1 / MC 1.16.5）。
- 决策：改为取**首个** release（最新正式版）；无 release 时取第一个。
- 后果：mod 与整合包的版本解析都偏向最新正式版；用户可用显式版本覆盖。
- 何时重新审视：需按语义化版本而非发布时间排序时。

## ADR-015：实例设置后置可改 + authlib-injector 三方鉴权
- 日期：2026-10-15
- 状态：已采纳
- 背景：此前内存/端口/JVM 参数只能在创建时确定，建成后无法修改（只能重建实例）；且非正版玩家无法接入（正版验证）。
- 决策：新增 `InstanceManager.updateInstance`（内存/端口/JVM/authlib）与 `rename`（改目录名 + 元数据），均要求实例已停止；**改端口同步写回 `server.properties`**（server-port/query.port）以免配置与元数据打架；配置 authlib-injector 时按需下载其 jar（`~/.ceobe/cache/jars/`）并以 `-javaagent:authlib-injector.jar=<Yggdrasil 地址>` 注入，同时自动将 `online-mode=false`。GUI 把实例级项（名称/内存/端口/JVM/authlib）单列「服务器配置」页签，与 `server.properties` 的「游戏配置」页签分开；CLI 提供 `ceobe update`。
- 为什么选这个：把"改配置要重建实例"的痛点消掉；authlib 是接三方登录（如 LittleSkin）的标准做法，注入 + 关正版验证必须成对。
- 为什么不选其他：扩建实例（复制目录）成本高且易错；仅文档说明让用户手改 `instance.json` 不友好。
- 后果：`rename` 会改变实例目录名，GUI 需按新名字重建页签（`MainView.reselect`）；`instance.json` 新增 `authlibInjectorUrl`。
- 何时重新审视：需要热改（不停止实例）内存/端口时。

## ADR-016：GUI 默认浅色 + 跨平台打包（CI 产出 exe/dmg/AppImage）

> **状态：部分失效（2026-10-22，见 ADR-020）。**
> 「默认浅色」已被 ADR-020 取代：默认跟随系统，设置提供 浅色 / 深色 / 跟随系统 三态。
> 「跨平台打包」作废——产品已收缩为 Windows 专属，CI 只产出 Windows 自包含产物（ADR-019）。
- 日期：2026-10-15
- 状态：已采纳
- 背景：项目所有者要求默认白色主题（深色手动切换）、侧栏占满 100vh、设置项收敛到弹窗；并要求「main 推送即取消同名旧任务，构建 exe/dmg/AppImage」。
- 决策：主题改为 token 驱动、**浅色默认**、深色可选（`~/.ceobe/settings.json` 持久化，顶部栏「设置」弹窗切换）；字体中文优先「Microsoft YaHei UI」以消除像素感。JavaFX 依赖按构建机平台自动选 classifier（pom profiles），并用 GitHub Actions（`gui-release.yml`，`concurrency.cancel-in-progress`）在 Windows/macOS/Linux 上经 jpackage 产出 `.exe`/`.dmg`/`.AppImage`。
- 为什么选这个：默认浅色符合需求；jpackage 是 JDK 自带的标准打包器，避免引入第三方打包工具链；cancel-in-progress 防止旧构建抢占。
- 为什么不选其他：Electron/打包器（jlink+自写脚本）重复造轮子；仅 app-image 不满足"安装包"诉求（Linux 额外用 appimagetool 包 AppImage）。
- 后果：CI 需要 WiX（Windows 上按需 `choco install wixtoolset`）与 appimagetool（Linux 上现取）；产物未签名（macOS 会有 Gatekeeper 提示）。
- 何时重新审视：需要签名/公证（macOS notarization、Windows 代码签名）或自动发布 Release 时。

## ADR-017：版本号改 CalVer + CI 自动发布 Release

> **状态：部分失效（2026-10-21，见 ADR-019）。**
> CalVer 单点权威仍然有效，权威位置由根 `pom.xml` 改为 `Directory.Build.props` 的 `<Version>`；
> 「安装包版号取前三段 / macOS jpackage 只接受 1–3 段数字」整段作废——不再有 jpackage。
> 新增约束：`<Version>` 必须是点分四段（`26.10.21.01`），因为 NuGet 按 SemVer2 拒绝数字前导零的 prerelease 标识。
- 日期：2026-10-15
- 状态：已采纳
- 背景：项目此前用 `0.1.0-SNAPSHOT`（无发布语义），CI 只上传 Actions artifact、不进 Releases，且安装包版号硬编码 `1.0.0`；同时 CI 报出 actions Node 20 弃用、`setup-java@v4` 停止维护等警告。项目所有者要求：版本号改 CalVer、以根目录 pom 为唯一权威，并在 CI 通过后自动发布 Release（tag `v<CalVer>`，标题与正文复用 commit message）。
- 决策：
  1. 版本号 = **CalVer `YY.MM.DD-HH`**（如 `26.10.15-14`），唯一权威为根 `pom.xml` 的 `<version>`；CI 只读不写；三个子模块的 `<parent>` 版本同步。
  2. jpackage `--app-version` 取 CalVer 去掉 `-HH` 后的**前三段**（`26.10.15`）。依据：macOS 上 jpackage 强制「1–3 段数字 + 不以 0 开头」（JDK-8380634，JDK 24/25/26 均受影响，**JDK 27 才移除**），且 Apple `CFBundleVersion` 只认数字与点——故含 `-` 或 4 段的完整版号无法直接喂 jpackage。
  3. CI 拆为 `version`（读根 pom 版本并断言包版号合法）/ `package`（三平台矩阵）/ `release`（汇总产物发布）三个 job；`release` 用 `gh release create`，tag `v<CalVer>`，**标题 = commit subject、正文 = commit body**，同名 Release 先删后建（幂等收敛）。
  4. actions 升级到各自的 **Node 24 大版本**（`checkout@v5`、`setup-java@v5`、`upload-artifact@v6`、`download-artifact@v7`；注意 upload 与 download 的 Node 24 大版本号并不对齐）以消除 Node 20 弃用警告；`ubuntu-latest` pin 为 `ubuntu-24.04`（避免 Ubuntu 26 迁移 notice）。
- 为什么选这个：CalVer 让"哪次构建"一目了然；根 pom 单点权威避免多处版本漂移；拆开包版号是 macOS 打包硬约束下唯一可行解。
- 为什么不选其他：`${revision}` + flatten 的 CI-friendly 版本会增加构建期不确定性（当前 `mvn package` 已够用）；给 macOS 单独用完整版号会使三平台包版号不一致。
- 后果：发版需手动 bump 根 pom 四处（`mvn versions:set -DnewVersion=<v> -DprocessAllModules`）；安装包内版号（`26.10.15`）与 Release 版号（`v26.10.15-14`）不同；同一小时内多次 push 会撞同一 tag，由「删除重建」收敛。
- 何时重新审视：JDK 27 移除 macOS 版本限制后，可让包版号直接采用完整 CalVer；或需要签名/公证时。

## ADR-018：服务端运行时不得回退到 GUI 打包镜像（jpackage 会剥离 bin/java）

> **状态：部分失效（2026-10-21，见 ADR-019）。**
> jpackage 相关的成因与处置（剥离 `bin/java`）已不适用——C# 版没有 JRE 打包镜像。
> 但本条的**教训仍然有效且更严**：服务端运行时 Java 必须由 ceobe 自行探测/托管，
> 不得假设宿主环境或任何打包产物自带可用 JRE。
- 日期：2026-10-15
- 状态：已采纳
- 背景：从打包 GUI 启动实例"无法启动"，`console.log` 里只留下一行启动命令行、之后再无任何输出（连报错都没有）。定位：`ServerProcess.buildCommand` 在 `meta.javaHome` 为空（"继承当前 JVM"）时回退到 `System.getProperty("java.home")`；而 jpackage 生成的运行时镜像**剥离了 `bin/java(.exe)`**（GUI 由原生 `Ceobe.exe` 经 `jli.dll`/`server/jvm.dll` 启动，无需该启动器）。于是 `pb.start()` 抛 IOException，异常被 `Fx.background` 捕获后只写进 GUI 文本框、不落 `console.log` → 表现为"静默失败"。同时 `JavaResolver` 仅按主版本比较就把该镜像判为"已就绪"，无任何提示。
- 考虑过的方案：① 把 `java` 启动器打回镜像；② 启动时按"是否含启动器"校验运行时并明确报错，同时解析真正可用的 JDK。
- 决策：方案 ② —— `JavaEnv` 新增 `hasLauncher(Path)` / `hostJavaHome()` / `javaLauncher()`，以「是否存在 `<home>/bin/java`」判定运行时可否用于 fork 子进程；`JavaResolver.resolve` 仅在当前 JVM **确有启动器**时才允许"继承当前 JVM"，否则依次回落自管 JDK、宿主 `JAVA_HOME`，都不满足则给出可执行提示（`ceobe jdk install <n>` / `--java-home`）；`ServerProcess.buildCommand` 与 CLI `spawnSupervisor` 一律经 `JavaEnv` 解析启动器，定位不到即抛明确 `CeobeException`；GUI 新建对话框改用 `JavaResolver` 判定"已就绪"，避免误报。
- 为什么选这个：把"打包运行时镜像不可当 JRE 用"这一约束收敛到一处判定；静默失败改为可诊断的报错；CLI（在真 JDK 下运行）行为不变。
- 为什么不选其他：方案 ① 与 jpackage 打包语义对抗、增大镜像体积，且不解决"用户已在别处装 JDK"的诉求。
- 后果：`java.home` 不再被无条件信任；打包 GUI 首次启动需宿主设有可用 `JAVA_HOME` 或存在自管 JDK，否则新建对话框会给出"可一键安装 Java N"的提示。
- 何时重新审视：jpackage 改为在 app-image 中保留 java 启动器时。

## ADR-019：全线重构为 C# / .NET 10 + WinUI 3（弃 Java/JavaFX），范围收缩为 Windows 专属
- 日期：2026-10-21
- 状态：已采纳
- 背景：项目所有者判定「Minecraft 服务器编排」这类工具的实际用户几乎全在 Windows，跨平台支持是纯成本无收益；同时 Java 栈在本项目上的痛点集中在分发（jpackage 产物体积大、冷启动慢、JavaFX 观感靠自研 CSS 硬撑、CI 需跑三平台矩阵、ADR-017/018 两条后果都是 macOS/jpackage 打包约束逼出来的）。要求功能集合不变，改用 C# + WinUI 3 重构整个 App。
- 考虑过的方案：① WinUI 3 + Windows App SDK；② WPF(.NET 10)；③ Avalonia；④ 只重写 GUI、经进程/IPC 复用 Java core；⑤ 一次性推倒重来。
- 决策：
  1. UI 选 **WinUI 3 + Windows App SDK**（方案 ①）；目标框架 `net10.0-windows10.0.19041.0`。
  2. 节奏选 **渐进三阶段**（方案 ① 的分期版），Java 版自本 ADR 起**功能冻结、只修 bug**，基线 tag `pre-csharp-migration`（= `5993956`）：
     - 阶段一：`Ceobe.Core` + `Ceobe.Cli`（`net10.0`）。验收 = Java 侧 10 个测试类与 `scripts/smoke-cli.sh` 的等价 E2E 全绿，且对同一实例目录树双跑结果一致。
     - 阶段二：`Ceobe.App`（WinUI 3），逐页签替换（实例列表→控制台→配置→Mod/整合包→玩家→穿透→设置）。
     - 阶段三：改写 PRD/ARCHITECTURE/specs 与 CI（单一 Windows 自包含产物），Java 三模块目录归档删除。
  3. **保留独立 CLI**（不并入 GUI 命令面板）：它既是无头/远程场景的交付物，也是阶段一证明 core 语义等价的主要自动化抓手。
  4. 依赖对位：Jackson→`System.Text.Json`（source-gen）、SnakeYAML→YamlDotNet、picocli→`System.CommandLine`、Java HttpClient→`HttpClient`、虚拟线程日志泵→`Channel<T>` + `IAsyncEnumerable`、JavaFX/AtlantaFX/CSS→WinUI 3 XAML + `CommunityToolkit.WinUI`。
  5. **语义原样搬运、不得重写**的七条硬契约：server.properties 保序保注释保 `\:` 转义；`Mirrors` 的 BMCLAPI / Modrinth CDN / Temurin 清华改写与上游回退；Mojang piston-meta 与 Paper Fill v3 的响应契约；ADR-007 的监督进程 + `.ctl` 随机 token + `.state`/`.pid`；端口跨实例排他；`.mrpack` 的 `env.server` 过滤与 overrides/server-overrides 优先级；CalVer `YY.MM.DD-HH` 单点权威（改由 `Ceobe.csproj` 的 `<Version>` 承担）。
- 为什么选这个：WinUI 3 是唯一在「原生 Fluent 观感 + 系统级通知/凭据/文件关联」上不留债的选择，且已与「Windows 专属」的范围收缩对齐；保留 Java 版做只修 bug 的热备，使阶段一/二期间用户始终有可用产物，避免 8841 行 + 18 条 ADR 的一次性硬切。CLI 先行是因为 core 的正确性可以用 CLI 廉价地证成，GUI 不行。
- 为什么不选其他：② WPF 工程风险最低（纯自包含单文件 exe、无 Windows App SDK 引导、UI 自动化成熟），但观感上比 WinUI 3 旧一挡，与「界面杂乱」的整改诉求方向不符；③ Avalonia 的跨平台能力正是本次明确放弃的目标，等于为不会用掉的能力付抽象成本；④ 跨语言边界要把日志流、状态机、文件锁全部重造一遍，是自找的复杂度；⑤ 中途无可用版本，且七条硬契约一次性搬迁的回归面无法验证。
- 后果：**失去「宿主 JVM 即运行时」这条免费退路**——Java 版 `InstanceMeta.javaHome=null` 可直接继承当前 JVM，C# 版进程不是 Java，`JavaResolver` + `JdkInstaller`（Adoptium + 清华镜像）从锦上添花升级为生死线，下载失败即一个玩家都开不了服，必须优先做失败可见化与手动指定 JDK 兜底。UI 回归手段倒退：`-Dceobe.snapshot` 那类「启动即导出 Scene 快照」在 WinUI 3 无等价廉价方案，需另建（方案见 docs/MIGRATION.md 的「UI 回归（闸门 C）」小节）。CI 从三平台矩阵收敛为单 Windows job，但需引入 .NET SDK 与（若走 MSIX）证书/PACKAGE 环节。本机当前未安装 dotnet SDK，阶段一开工前需先装。
- 何时重新审视：若 WinUI 3 非打包（unpackaged）分发在实际用户手上反复出问题（Windows App SDK 引导失败、FilePicker 初始化、自包含体积），应立刻降级到 WPF(.NET 10) 复用全部 XAML 语义之外的 core 成果——core/cli 与 UI 框架无关，止损成本仅为一个 UI 层。

## ADR-020：WinUI 3 Fluent 2 外壳重构（NavigationView + SelectorBar + Mica）
- 日期：2026-10-22
- 状态：已采纳
- 背景：2026-10-22 实机评审（截图见评审记录）发现，当前 GUI 虽然使用 WinUI 3 控件，但整体仍是「把 JavaFX 布局搬进 WinUI」：根 Grid 无背景导致深色主题白字白底；`ContentControl` 默认 Top 与 `TabView` 默认 `VerticalAlignment=Top` 让页面 `*` 行塌成 0 高度（控制台日志区与白名单列表整块不可见）；`VmList.Refresh()` 每次 `Rows.Clear()` 会清掉选中实例，而主窗口对每条日志事件都触发它；C# 对话框写死 ARGB，UIA 中图标+文字按钮名称为空。项目所有者要求「全面优化，不计成本达到 WinUI 3 最佳实践」。
- 考虑过的方案：① 保留自定义侧栏 + TabView，只做 Fluent 美化；② 官方 `NavigationView`（实例）+ `SelectorBar`（分区）；③ 自绘侧栏 + 顶部 NavigationView。
- 决策：
  1. 主壳选 **方案 ②**：内置 `TitleBar` + `NavigationView`（实例导航，`PaneFooter` 放新建/设置）+ `SelectorBar`（七个分区）；删除自定义 `InstanceListView` 与 `TabView`。页面宿主用显式 Stretch 的 Grid + 可见性切换，杜绝 `*` 行塌陷。
  2. 主题改 **三态**：`light | dark | system`，默认 `system`；`ContentDialog` 显式设置 `RequestedTheme`（PopupRoot 不继承根主题）。Mica 仅在「跟随系统」且 `MicaController.IsSupported()` 时启用，显式主题与 Win10 使用纯色回退层，避免 Mica tint 与应用主题不一致。
  3. 建立设计令牌：4px 网格、官方字阶、`CardBackground*` 卡片、`ControlCornerRadius`/`OverlayCornerRadius`、`Consolas` 等宽字体；零硬编码颜色。
  4. 新增官方 `CommunityToolkit.WinUI.Controls.SettingsControls 8.2.251219`（仅 App 工程），用 `SettingsCard` / `SettingsExpander` 承载设置与服务器配置。
  5. 新建实例由「三步向导」改为**单页表单**（6 个字段一页完成，补内联校验与进度），同步更新 `docs/specs/module-gui.md`。
  6. 游戏配置的字段输入改为**按类型选择 DataTemplate**：旧的四控件叠放 + Visibility 会让隐藏控件在初始化时回写坏值（实测把 `motd` 写成 `0` 并制造假校验错误）；补上 VM 已建模但 XAML 从未渲染的资源包/数据包面板。
  7. ViewModel：`InstanceListViewModel.Refresh()` 改差量更新并保留选中；新增 `SelectedRow` 供 NavigationView 单向绑定；Core 状态/日志事件统一经 `UiThread.Post` marshal；主窗口不再对每条日志做全量刷新。
- 为什么选这个：`NavigationView` 自带键盘导航、无障碍语义、选中指示与 PaneFooter 结构；`SelectorBar` 是 WinUI 3 官方的页内分区选择器；内置 `TitleBar` 自动处理 caption buttons 与主题。三者组合是 Windows 11 应用的官方结构，而不是控件拼贴。差量刷新与显式主题作用域直接消灭了三条 P0 缺陷。
- 为什么不选其他：① 需要为 `TabView` 的默认 Top 与企业级 `ContentControl` 对齐写一堆 workaround，仍保留「非官方页签」观感；③ 左侧仍是自绘列表，失去 NavigationView 的键盘/无障碍/未来紧凑模式能力，且顶部 NavigationView 在 7 项时会把溢出项收进 More，反而不如 SelectorBar 直观。
- 后果：`InstanceTabs` 从 TabView 改为 SelectorBar，页面刷新仍按 `IReloadable`；`SettingsCard` 增加一个新的 UI 依赖（已用 artifact 检查守护）；深色/浅色/系统三态需要同时验证 Mica 与纯色回退；`TabView` 相关的旧经验（默认 Top、Collect/SelectionChanged）不再适用，已写入 AGENTS 已知易错点。CI 增加闸门 C grep、测试数量下限与发布产物完整性检查；UIA smoke 仅在本地交互式会话运行。
- 何时重新审视：需要 NavigationView 紧凑模式（需为每个实例项提供 `Icon`）或引入跨平台 UI 框架时。

## ADR-021：分发形态定为「两档 zip + per-user MSI」，CI 只构建不测试
- 日期：2026-10-22
- 状态：已采纳（结清 `docs/MIGRATION.md` 待决项 1）
- 背景：ADR-019 之后分销形态一直挂着「MSIX 还是裸自包含」未决，而旧的 `v*` tag 触发式 Release 从未跑通（tag `v26.10.15-14` 指向的 commit 里还没有 `windows.yml`，「Publish Release」永远 skipped）。训练员 2026-10-22 明确三点：CI 不需要测试、版本只认 `Directory.Build.props`、产物按「带 SDK / 不带 SDK」区分且 Win11 用户要更小更快的包；同时希望交付物是 MSI 或单体 exe。
- 实测（本机 .NET SDK 10.0.402 / WinUI 3 unpackaged / WindowsAppSDK 2.5.1）：

  | 组合 | 解压后 | 文件数 | zip | 目标机前置 |
  |---|---|---|---|---|
  | `SelfContained=true` + `WindowsAppSDKSelfContained=true`（portable） | 232M | 529 | 89M | 无 |
  | `SelfContained=false` + `WindowsAppSDKSelfContained=false`（win11） | 79M | 60 | 27M | .NET 10 运行时 + Windows App Runtime 2.5.x |
  | `SelfContained=true` + `WindowsAppSDKSelfContained=false` | 156M | 247 | 61M | Windows App Runtime 2.5.x |
  | FD + `PublishSingleFile=true` | 78M | 1 exe | 27M | **启动即退（exit 0），不可用** |
  | SC + `PublishSingleFile=true` | 223M | 1 exe | 84M | 无；能渲染窗口，但 WinUI 未背书单文件 |

  MSI 用 WiX v6 从 win11 档负载构建：23M，per-user 安装到 `%LOCALAPPDATA%\Programs\ceobe`，实测安装→启动（窗口渲染）→卸载零残留。
- 决策：
  1. GUI 出 **两档**：`win11`（框架依赖，推荐）与 `portable`（双自包含，零前置兜底）；CLI 保持单体 exe。
  2. GUI 交付 = **zip 散文件 + MSI**；MSI 负载取 win11 档，per-user、免 UAC、带开始菜单快捷方式与 ARP 卸载项。
  3. **不做 GUI 单体 exe**：FD 单文件实测不可用，SC 单文件虽有可运行证据但属未背书形态，且每次首启要把 232M 解到 `%TEMP%`。
  4. **不做 MSIX**：需证书或旁加载，与 `WindowsPackageType=None` 的免证书前提冲突。
  5. 版本权威仍是 `Directory.Build.props` 的 `<Version>`，CI **只读**；产物名、MSI `ProductVersion`、Release tag 全部由它派生。main 推送即发布（同名 Release 先删后建），不再需要手动打 tag。
  6. **CI 不跑测试**（训练员决定），只保留编译、闸门 C 静态检查与产物完整性校验；`dotnet test` 归本地。原「测试数量下限 485」守卫随之移除。
- 为什么选这个：`win11` 档把 153M 的 .NET 运行时留给系统管理，下载体积降到 portable 的 1/3；MSI 让「应用和功能」里可卸载、有快捷方式，但不改变程序本身的便携数据模型（数据仍在 `%USERPROFILE%\.ceobe`）。版本只认 props 消除了 tag 与产物版号漂移这一类事故。
- 为什么不选其他：MSIX 要证书/旁加载；GUI 单体 exe 在 FD 下直接坏掉，SC 下未获官方支持；把测试留在 CI 与训练员要求相反（代价是 CI 不再是质量闸门，失败只能靠本地测试与 UIA 冒烟发现）。
- 后果：**win11 档的前置条件不是「系统自带」**——实测 inbox 只有 `Microsoft.WindowsAppRuntime.CBS.2` 2.4.1（`NonRemovable=True`），而 ceobe 需要的 `Microsoft.WindowsAppRuntime.2` 2.5.1 是 Store 签名、`NonRemovable=False`（后装）。缺运行时由 .NET apphost 与 WinAppSDK bootstrapper（默认 `OnNoMatch_ShowUI`）弹框提示并给下载链接，不会静默崩溃；发布说明必须写明这一点。另外 `Directory.Build.props` 当前是 3 段 `26.10.22`，与硬契约 7 的「点分四段」不一致，CI 兼容两种形态（4 段 → 展示 `26.10.22-01`、MSI 取前三段）。
- 何时重新审视：Windows 11 把 Windows App Runtime 2.5.x 纳入 inbox 后，「Win11 推荐」的口径可收紧为「系统自带即可」；或者需要企业分发（GPO/SCCM）时再评估 MSIX。
