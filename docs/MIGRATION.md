# MIGRATION — Java → C# / WinUI 3

> 依据 ADR-019。本文档是迁移期的唯一施工图；功能集合不变，语义以本仓库 Java 实现为权威参考。
> 基线：tag `pre-csharp-migration`（= `5993956`，版本 `26.10.15-14`）。Java 版自此刻起功能冻结、只修 bug。

## 当前进度（2026-10-21）

| 阶段 / 闸门 | 状态 | 证据 |
|-------------|------|------|
| 阶段一 Core + CLI | 完成 | Core 261 个测试全绿；CLI 顶层命令 + 隐藏 `supervise`，退出码 0/1/2 实测 exe |
| 闸门 A（Java 测试等价搬迁） | 完成 | Java 侧 10 个测试类逐条对照 |
| 闸门 B（双跑对读） | 完成 | `tools/wiregen` 用真实 Jackson 生成 fixture，Java/C# 双向对读；并修掉 Java 侧 `isDark()` 只读属性导致主题丢失的 bug |
| 阶段二 GUI | 完成 | 七个分区齐全；ViewModel 224 个测试、全量 485 个测试全绿；2026-10-22 完成 Fluent 2 外壳重构（ADR-020） |
| 闸门 C（UI 回归） | 按约定口径达成 | 重心在 ViewModel 单测 + UIA 断言；像素基线按本文档既定方案放弃 |
| 阶段三 文档 / CI | 完成 | ARCHITECTURE / PRD / specs 改写为 C# 事实；ADR-001/002/008/016/017/018 标注取代；CI 收敛为 `windows-latest` |
| 阶段三 Java 移除 | 完成 | `ceobe-core/` `ceobe-cli/` `ceobe-gui/` `pom.xml` 已从工作树删除；历史与 tag `pre-csharp-migration` 保留 |
| 端到端真机验收 | 完成（穿透公网侧未验） | 见「端到端验收记录」 |

## 端到端验收记录（2026-10-21 真机实测）

不只看「页签切得动」，而是用 UI Automation 驱动真实窗口跑完整业务链路，服务端为**真实下载的原版 26.3**：

| 步骤 | 结果 | 证据 |
|------|------|------|
| GUI 新建实例（原版 26.3，自动端口） | ✅ | 经 BMCLAPI 下载 62 MB `server.jar`，生成 `instance.json` / `eula.txt` / `server.properties` |
| GUI 启动 | ✅ | `console.log`：`Done (2.528s)! For help, type "help"` |
| GUI 下发命令 | ✅ | 输入 `say ceobe-e2e-ok` → 日志回显 `[Not Secure] [Server] ceobe-e2e-ok` |
| GUI 停止 | ✅ | `.state` = `STOPPED`，日志有 `Stopping server` / `Saving worlds`，`java.exe` 归零 |
| GUI 加白名单 | ✅ | 正版 UUID 与 `api.mojang.com` 直接核对一致（离线公式结果不同，确认走的是正版分支） |
| 服务端接受名单文件 | ✅（修复后） | 修复前启动即抛 `JsonSyntaxException`；修复后 `Done (0.475s)` 零异常 |
| 穿透通道（Pinggy） | ⚠️ 部分验证 | 守卫路径正确（未运行时拒绝开启）；手工执行同一条 ssh 命令可拿到 `tcp://…` 地址（约 50 秒），据此定位并修掉两个真 bug；修完连续重试触发 Pinggy 限流，自动化复验未完成 |

这次端到端抓到并修掉五个单测覆盖不到的真 bug（提交 `bc1b07c`、`d530e96`、`f1a01df`、`8c1ac61`、`198994a`）：

1. **进度回调跨线程改 UI**：Core 的 `IProgressSink` 不做 marshal，下载线程直接写绑定属性 →
   WinUI 抛 `0x8001010E` 并卡死界面。新增 `UiThread` 统一回投。
2. **网络超时崩成未处理异常**：`CreateAsync` 无条件 rethrow `OperationCanceledException`，
   `HttpClient` 超时于是从 `async void` 事件冒到全局处理器——界面看着只是卡住，日志里是崩溃。
3. **名单文件格式臆断**：写成 `{"version","profileList"}`，真实服务端当 JSON 数组解析直接抛异常，
   整份白名单被当成空——界面显示「已写入」，玩家一个都进不来。已改为裸数组，
   键名修正为 `bypassesPlayerLimit`（复数 s），由原版服务端亲自写出的文件确认。
4. **解构建静默跳过 GUI**：`Ceobe.App` 只声明 x64 而解默认 Any CPU，
   `dotnet build ceobe.slnx` 成功但 GUI 产物没更新——排查时会一直跑旧二进制。
5. **启动路径两处硬伤**：冷缓存把 Java 兜底版本（21）当硬性要求，误杀可用的 JDK 25；
   监督进程在 `Start` 之前开控制通道，`ceobe start -d` 一启动就死。

穿透侧另修两处（`198994a`）：就绪超时 45 秒短于 Pinggy 实际分配时间（45～60 秒），
以及 ssh 的 stdin 被子进程继承成 EOF、连上即断会话。

> 教训：这三处都通过了单元测试。单测验证的是「我们的假设内部自洽」，
> 端到端验证的是「假设与真实服务端一致」——两者不可互相替代。

> **关于 Java 源码**：迁移验收完成后，`ceobe-core/`、`ceobe-cli/`、`ceobe-gui/`、`pom.xml`
> 已从工作树**全部移除**（训练员 2026-10-21 决定）。Git 历史与基线 tag `pre-csharp-migration`
> 完整保留，需要考古或回打 hotfix 时从该 tag 取。

## 目标形态

```
ceobe.sln
├─ src/Ceobe.Core/          net10.0            域核心，零 UI 引用（等价 ceobe-core）
├─ src/Ceobe.Cli/           net10.0            System.CommandLine 无头入口（等价 ceobe-cli）
├─ src/Ceobe.App/           net10.0-windows10.0.19041.0   WinUI 3（等价 ceobe-gui）
└─ tests/Ceobe.Core.Tests/  net10.0            xUnit
```

命名空间根 `Ceobe`，替代 `im.xrl.ceobe`；产物名 `ceobe.exe` / `ceobe-cli.exe` 保持小写用户可见。

## 依赖对位

| Java | C# | 说明 |
|------|----|------|
| Jackson databind + jsr310 | `System.Text.Json`（source-gen `JsonSerializerContext`） | 现有模型是公有字段风格，C# 用属性 + `[JsonPropertyName]` 保持 `instance.json` / `settings.json` 字节级兼容 |
| SnakeYAML 2.7 | YamlDotNet | Paper 系 `paper-global.yml` 等读写 |
| picocli 4.7.7 | `System.CommandLine` | docker 风格子命令与 `--format json` 输出不变 |
| JavaFX 25.0.4 + AtlantaFX + 自研 shadcn CSS | WinUI 3 + `CommunityToolkit.WinUI.*` | 三阶段逐页签替换 |
| JDK `HttpClient` | `System.Net.Http.HttpClient` | User-Agent 必带（Modrinth 硬要求），保持单例避免端口耗尽 |
| `Thread.ofVirtual()` 日志泵 | `System.Threading.Channels.Channel<string>` + `IAsyncEnumerable<string>` | 控制台/tail/web 页三路订阅同一源 |
| JUnit 6 + `@Tag("network")` | xUnit + `Trait("Category","network")` | 默认排除，`--filter Category=network` 才跑 |

## 模块映射与体量

| Java 包 | 行数 | C# 归属 | 迁移要点 |
|---------|------|---------|----------|
| `core/model` | ~130 | `Ceobe.Core/Model` | `InstanceMeta` 字段名即 JSON key，**不得改名**（老实例目录要能直接读） |
| `core/store` | ~100 | `Ceobe.Core/Store` | `CeobeHome` 解析优先级：配置项 > 环境变量 `CEOBE_HOME` > `%USERPROFILE%\.ceobe`；本次新增数据目录可改（见整改项 3） |
| `core/util` | ~420 | `Ceobe.Core/Util` | `Mirrors`/`Http`/`Zips`/`Ports`/`Versions`/`Json` 全部为**契约级**代码，逐条对照移植、单测先行 |
| `core/config` | ~480 | `Ceobe.Core/Config` | `ServerPropertiesFile` 的保序/保注释/保 `\:` 转义是最高风险点；`PropSchema` 7 组 60+ 键本次要重分组（见整改项 5） |
| `core/provider` | ~650 | `Ceobe.Core/Providers` | SPI 形态不变：`IServerJarProvider` + `ProviderRegistry`；Mojang/Paper(Fill v3)/Fabric/Purpur/NeoForge |
| `core/runtime` | ~1000 | `Ceobe.Core/Runtime` | `ServerProcess`/`InstanceManager`/`InstanceControl`/`ControlServer`：ADR-007 监督进程 + `.ctl` token + `.state`/`.pid` 语义照搬 |
| `core/jdk` | ~380 | `Ceobe.Core/Jdk` | **优先级最高**：失去宿主 JVM 退路后这是生死线（ADR-018 的教训同样适用） |
| `core/mods` | ~480 | `Ceobe.Core/Mods` | `ModrinthClient`/`MrpackInstaller`；`env.server` 过滤与 overrides 优先级不得改 |
| `core/tunnel` | ~540 | `Ceobe.Core/Tunnel` | `ITunnelProvider` SPI 不变；本次新增 `TailscaleTunnel`（见整改项 1） |
| `core/webconsole` | ~180 | `Ceobe.Core/WebConsole` | JDK `HttpServer` → ASP.NET Core minimal hosting 或 `HttpListener`（后者更贴近「只读状态+日志页」的体量） |
| `core/event` | ~40 | 事件用 C# `event`/`IProgress<T>` | 不再需要自定义 listener 接口 |

## 不得重写的七条硬契约（语义搬运清单）

1. `server.properties` 写回：保序、保注释、保 `\:` 转义（`level-type=minecraft\:normal`）。
2. `Mirrors` URL 改写：Mojang piston-meta/piston-data → BMCLAPI 同路径换主机；Fabric meta → BMCLAPI `/fabric-meta`；Modrinth CDN → `mod.mcimirror.top`；Temurin → 清华镜像；失败自动回退上游。
3. Paper Fill v3 响应契约：`downloads["server:default"].url` + sha256（v2 已 410，一切 provider 必须封在 SPI 后可独立替换）。
4. ADR-007 进程托管：监督进程 + 本机 `.ctl` 随机 token 控制通道 + `.state`/`.pid`，无全局 daemon。
5. 端口跨实例排他：`assertPortExclusive` / `suggestFreePort` / `--port auto`，穿透通道一律指向实例自己的 `meta.port`。
6. `.mrpack`：跳过 `env.server=unsupported` 的文件；`server-overrides/` 优先于 `overrides/`。
7. 版本号：CalVer `YY.MM.DD-HH`，单点权威由 `Ceobe.csproj` 的 `<Version>` 承担，CI 只读不写。

## 阶段划分与验收闸门

### 阶段一：Core + CLI（先行，可独立发布）

- 交付：`Ceobe.Core` + `Ceobe.Cli`，自包含单文件 exe。
- 顺序：`Util`(含单测) → `Store`/`Model` → `Config`(schema 重分组在此发生) → `Jdk` → `Providers` → `Runtime` → `Mods` → `Tunnel` → `WebConsole` → `Cli`。
- **闸门 A**：Java 侧 10 个测试类等价搬迁全绿；`smoke-cli.sh` 的等价断言版全绿。
- **闸门 B（双跑对照）**：同一 `CEOBE_HOME` 下，Java 版创建/配置的实例目录，C# 版能读、能启、能改配置且写回 diff 仅限预期字段；反之亦然。这是语义等价的唯一硬证据。
- 开工前置：本机安装 .NET 10 SDK（当前未安装）。

### 阶段二：WinUI 3 GUI

- 页签顺序：实例列表 → 控制台 → 配置（含整改项 5）→ Mod/整合包（整改项 4）→ **玩家**（整改项 2，新页签）→ 穿透（整改项 1）→ 设置（整改项 3）。
- 旧 GUI 的 14 个 Java 文件 → XAML + code-behind + ViewModel；`PropsPaneBase` 的通用表单渲染器由 `DataTemplateSelector` 按 `PropType` 分派。
- **闸门 C**：UI 回归达标（见下节）。
- 已知倒退：WinUI 3 unpackaged 分发的 Windows App SDK 引导、FilePicker 初始化需显式处理；拖拽导入依赖 `DragOver/Drop` + 包内 `DataPackageView`，需实测。

### UI 回归（闸门 C 的具体手段）

Java 版靠 `-Dceobe.snapshot=<png> [-Dceobe.snapshot.tab=配置]` 启动即导出 Scene 快照，无需窗口焦点；WinUI 3 **无等价廉价手段**，这是本次重构明确接受的倒退，替代方案分两层：

1. **逻辑层不靠截图**：把 `PropsPaneBase`/各 Pane 的可观测行为下沉到 ViewModel，用普通单测断言（分组结果、requiresRestart 标记、字段→控件类型分派、导入嗅探结果）。ViewModel 不引用 WinUI 类型，可在无窗口环境跑。
2. **像素层只在关键页做**：`WinAppDriver`/`FlaUI` 驱动 UIA 树，断言控件存在与文本，配合 `Windows.UI.ViewManagement`/`GraphicsCaptureAPI` 抓窗口位图存 baseline；仅在 CI 的 Windows job 里跑，且需要交互式会话（CI 上的 WinUI 3 无头渲染不可靠，遇阻则退化为「人工首版验收 + 之后只信 UIA 断言」）。

**2026-10-22 落地**：新增 `tools/ui-smoke/Invoke-UiSmoke.ps1`（本地交互式运行，不进 GitHub-hosted CI）：独立 `CEOBE_HOME`、UIA 断言实例导航/七个分区/日志区高度/按钮可访问名/两个对话框，浅色与深色两次启动截图并检查中心亮度（深色 < 0.35、浅色 > 0.65）。CI 只做闸门 C grep 与两档产物完整性校验；测试不在 CI 跑（训练员 2026-10-22 决定：CI 只构建），原「测试数量下限 485」守卫随之移除。

结论：像素级回归不追求覆盖率，验收重心放在 ViewModel 层；这一条与 ADR-019「后果」中记录的倒退风险一致。

### 阶段三：文档、CI 与归档

- `AGENTS.md` / `README.md` / `docs/ARCHITECTURE.md` / `docs/specs/*` 全面改写到 C# 事实；ADR-001（Maven）、ADR-002（JavaFX）、ADR-016（跨平台打包）、ADR-017 的 jpackage 段、ADR-018 的 jpackage 成因，标记为「已被 ADR-019 取代/部分失效」。
- CI：三平台矩阵 → 单一 `windows-latest`，产出两档 zip（`win11` 框架依赖 / `portable` 自包含）+ MSI（见 ADR-021）。
- Java 源码：已从工作树移除，不参与任何构建；历史与基线 tag `pre-csharp-migration` 保留作考古入口。

## 训练员反馈整改项（2026-10-21）

### 1. Pinggy「只有本地连接正常」

现状代码事实：`PinggyTunnel` 写死 `-R0:127.0.0.1:{port}`，而 `server-ip` 是 GUI 可改项（`PropSchema:62`）。**若把 `server-ip` 填成具体网卡地址，服务端只监听该地址，隧道指向回环即成死通道**，与「本地正常、外部连不上」症状吻合。但缺公网侧实测证据，不据此定罪。整改：

- `up()` 前校验「隧道目标 == 实际绑定」：读取 `server.properties` 的 `server-ip`，不一致则告警并给出修正按钮。
- 隧道就绪判定从「正则解析到 URL」升级为**公网侧回环拨号**：自建连接打到自己的公网地址，发一个 Minecraft Server List Ping（handshake + status），成功才置 RUNNING，全过程写 `tunnel.log`。
- 免费通道 60 分钟 + 地址每次变化是结构性短板（MC 客户端服务器地址不能频繁改）。新增 `TailscaleTunnel`（或自建 frp）作为长期联机首选，Pinggy 降级为「临时给朋友试一次」。SPI 已就位，加实现不动架构。

### 2. 白名单默认开启却无入口

现状代码事实：全仓库无任何 `whitelist.json`/`ops.json`/`banned-*.json` 处理，纯靠控制台敲命令；且 `white-list` 默认值为 `true`。整改：

- 新增「玩家」页签：白名单增删/批量导入、OP 及权限等级、`banned-players`/`banned-ips` 管理、在线模式关闭时自动生成离线 UUID（`MD5("OfflinePlayer:"+name)` 置 version-3 位，必须与 Java `UUID.nameUUIDFromBytes` 字节一致，否则离线服白名单写进去是废的）。
- 运行中改文件不生效：落盘后必须经 stdin 下发 `whitelist reload` / `op`，UI 做成原子按钮并显示服务端回执。
- 建议推翻默认值：`white-list=true` + 无 UI 入口 = 新手开服后朋友进不来又找不到开关，属「默认值劝退」。改为创建向导里显式询问一次。

### 3. 服务器存放位置无法修改

现状代码事实：`CeobeHome.root()` 只认系统属性 / `CEOBE_HOME` / `~/.ceobe`，`AppSettings` 仅 `theme` 一个字段，GUI 无入口。整改：

- `AppSettings` 增加 `homePath`；设置页做「数据目录」选择。
- 搬家向导：新目录建好 → 逐实例移动 → 校验 → 失败回滚。属不可逆操作，**执行前必须二次确认**（对齐作业原则）。
- Windows 专属新雷：`%USERPROFILE%` 被 OneDrive 已知文件夹重定向接管时，MC 世界高频写入会把云盘打爆；选中该路径需红字警告并建议排除。

### 4. Mod / 整合包改为一键导入（PCL 式）

方向采纳。核心原则：**导入即识别 → 识别后预览 → 确认才落盘**，绝不静默猜测。

- `.jar`：嗅探包内标记 —— `fabric.mod.json` / `META-INF/mods.toml` / `META-INF/neoforge.mods.toml` / `mcmod.info` → mod，进 `mods/`；无标记则视为服务端 jar，走切换服务端流程。顺带识别 `dependencies/`、`libraries/` 并提示缺失依赖。
- `.mrpack`：`MrpackInstaller` 现仅支持下载 URL，需补 `InstallFromLocal(path)` 复用同一解析逻辑。
- `.zip`：顶层含 `modrinth.index.json` → 按 mrpack；含 `mods/` → 按 mod 合集；含 `version.json`/`manifest.yml` → 按 MultiMC/PCL 格式；均不匹配则弹「未知压缩包，请选择用途」。
- 确认页必须展示：识别结果、将写入的目录、文件清单与数量、总大小、以及加载器/版本与当前实例是否匹配。
- 待训练员提供 PCL 实例样例（压缩包 + jar）以校准上述嗅探规则。

### 5. 配置界面分类规划

现状：7 组（基础/游戏规则/世界/网络/性能/安全/高级）平铺 60+ 键，`PropsPaneBase` 单一通用渲染器。整改：

- 三级结构：**常用**（≤12 项，够开服即用）/ **分类**（左侧导航 + 搜索，按 key 名与中文标签模糊匹配）/ **原始**（全量 key-value 表给高级用户）。
- 复合控件：4 个 `resource-pack*` 键合并为「资源包」面板（URL + 提示 + 强制开关 + sha1 自动计算）；`initial-enabled-packs`/`initial-disabled-packs` 逗号字符串改为数据包多选列表。
- `PropKey` 新增元数据 `requiresRestart`：区分「改完需重启」（`server-port`/`motd`/`white-list`）与「可热生效/`reload`」（`max-players`/`pvp`/`difficulty`），界面与日志共用这一标记，避免用户改了没反应。

## 待决事项（需训练员拍板）

1. ~~分发形态：MSIX（需证书/或旁加载）还是自包含裸 exe + 手动更新？~~ **已决（2026-10-22，ADR-021）**：两档 zip + per-user MSI（WiX v6），不走 MSIX。
2. 长期联机的首选穿透方案：Tailscale（要装客户端、要登录）/ ZeroTier / 自建 frp（有 VPS 才可行）——决定整改项 1 做到哪一档。
3. PCL 整合包样例：待训练员提供实例，据此定嗅探与格式兼容范围。
4. Java 版冻结期间是否允许把整改项 1～5 的 hotfix 打回 Java 版（还是全部只进 C# 版）。
