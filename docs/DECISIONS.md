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
- 何时重新审视：NeoForge 源可达或找到镜像（BMCLAPI）时。

## ADR-005：状态存储 = `~/.ceobe/` 文件树 + 每实例文件锁
- 日期：2026-10-15
- 状态：已采纳
- 背景：CLI 与 GUI 可能并发操作同一实例；无守护进程架构（对齐 docker 直觉但避免常驻 daemon）。
- 考虑过的方案：常驻 daemon + 本地 socket（docker 模式）；SQLite；纯文件。
- 决策：纯文件——`~/.ceobe/instances.json`（索引）+ `~/.ceobe/instances/<name>/`（instance.json + 服务器目录）；运行态互斥用 `<dir>/.lock`（FileLock）+ `<dir>/.pid`。
- 为什么选这个：无 daemon 意味着崩溃后零残留状态、可用任意编辑器抢救；可调试性最高。
- 为什么不选其他：daemon 增加一层生命周期管理；SQLite 对"一个实例一个目录"的直觉不友好。
- 后果：跨进程日志流（`logs -f`）通过读实例 `logs/latest.log` + core 写的 `console.log` 追加实现。
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
