# DECISIONS

## ADR-001：构建系统用 Maven（弃 Gradle）
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
- 日期：2026-10-15
- 状态：已采纳
- 背景：项目所有者要求默认白色主题（深色手动切换）、侧栏占满 100vh、设置项收敛到弹窗；并要求「main 推送即取消同名旧任务，构建 exe/dmg/AppImage」。
- 决策：主题改为 token 驱动、**浅色默认**、深色可选（`~/.ceobe/settings.json` 持久化，顶部栏「设置」弹窗切换）；字体中文优先「Microsoft YaHei UI」以消除像素感。JavaFX 依赖按构建机平台自动选 classifier（pom profiles），并用 GitHub Actions（`gui-release.yml`，`concurrency.cancel-in-progress`）在 Windows/macOS/Linux 上经 jpackage 产出 `.exe`/`.dmg`/`.AppImage`。
- 为什么选这个：默认浅色符合需求；jpackage 是 JDK 自带的标准打包器，避免引入第三方打包工具链；cancel-in-progress 防止旧构建抢占。
- 为什么不选其他：Electron/打包器（jlink+自写脚本）重复造轮子；仅 app-image 不满足"安装包"诉求（Linux 额外用 appimagetool 包 AppImage）。
- 后果：CI 需要 WiX（Windows 上按需 `choco install wixtoolset`）与 appimagetool（Linux 上现取）；产物未签名（macOS 会有 Gatekeeper 提示）。
- 何时重新审视：需要签名/公证（macOS notarization、Windows 代码签名）或自动发布 Release 时。
