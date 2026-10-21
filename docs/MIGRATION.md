# MIGRATION — Java → C# / WinUI 3

> 依据 ADR-019。本文档是迁移期的唯一施工图；功能集合不变，语义以本仓库 Java 实现为权威参考。
> 基线：tag `pre-csharp-migration`（= `5993956`，版本 `26.10.15-14`）。Java 版自此刻起功能冻结、只修 bug。

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
- **闸门 C**：见「UI 回归」小节。
- 已知倒退：WinUI 3 unpackaged 分发的 Windows App SDK 引导、FilePicker 初始化需显式处理；拖拽导入依赖 `DragOver/Drop` + 包内 `DataPackageView`，需实测。

### 阶段三：文档、CI 与归档

- `AGENTS.md` / `README.md` / `docs/ARCHITECTURE.md` / `docs/specs/*` 全面改写到 C# 事实；ADR-001（Maven）、ADR-002（JavaFX）、ADR-016（跨平台打包）、ADR-017 的 jpackage 段、ADR-018 的 jpackage 成因，标记为「已被 ADR-019 取代/部分失效」。
- CI：三平台矩阵 → 单一 `windows-latest`，产出 `.exe`（MSIX 或裸自包含待定，见待决项）。
- 删除 `ceobe-core/`、`ceobe-cli/`、`ceobe-gui/`、`pom.xml`，保留 tag 作为考古入口。

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

1. 分发形态：MSIX（需证书/或旁加载）还是自包含裸 exe + 手动更新？影响阶段三 CI 设计。
2. 长期联机的首选穿透方案：Tailscale（要装客户端、要登录）/ ZeroTier / 自建 frp（有 VPS 才可行）——决定整改项 1 做到哪一档。
3. PCL 整合包样例：待训练员提供实例，据此定嗅探与格式兼容范围。
4. Java 版冻结期间是否允许把整改项 1～5 的 hotfix 打回 Java 版（还是全部只进 C# 版）。
