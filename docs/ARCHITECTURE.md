# ARCHITECTURE — ceobe

## 系统概述

```
┌─────────── ceobe-gui (JavaFX 25 + AtlantaFX, shadcn 定制主题) ───────────┐
│  实例列表 / 创建向导 / 控制台 / 配置表单 / Mod 市场 / 穿透面板            │
└──────────────────────────────┬───────────────────────────────────────────┘
                               │ 同进程直调
┌─────────── ceobe-cli (picocli, docker 风格) ─┐
│  ps / create / start / stop / logs / exec …  │
└──────────────────┬───────────────────────────┘
                   │
┌──────────────────▼── ceobe-core（无 UI 依赖的域核心）─────────────────────┐
│ instance   实例模型 + 状态机 + 文件锁 + ~/.ceobe/ 状态存储                │
│ provider   ServerJarProvider SPI: Mojang / Paper(Fill v3) / Fabric / Purpur│
│ runtime    ServerProcess: 进程启停、日志环缓冲、Done 检测、stdin 直通      │
│ config     server.properties 类型化 schema + 保序读写; YAML(Paper 系)     │
│ mods       ModrinthClient: search/versions/download(env 过滤服务端侧)     │
│ tunnel     TunnelProvider SPI: CloudflareQuick(HTTP) / Pinggy(TCP 自动续期)│
│ webconsole 内嵌 HTTP 服务(JDK HttpServer): 状态/日志只读页                │
└────────────────────────────────────────────────────────────────────────────┘
```

## 核心模块

| 模块 | 职责 |
|------|------|
| ceobe-core | 全部业务：实例 CRUD、进程生命周期、jar 获取、配置读写、mod、穿透、内嵌控制台 |
| ceobe-cli | 无头入口；docker 风格子命令；脚本化友好输出（`--format json`） |
| ceobe-gui | JavaFX 桌面界面；AtlantaFX 基础 + 自研 shadcn 风格 CSS；仅消费 core API |

## 模块关系

- gui / cli → core 单向依赖；core 不感知 UI。并发：多进程（CLI+GUI 同时）通过每实例 `.lock` 文件锁 + 中央 `~/.ceobe/instances.json` 索引互斥。

## 数据流

- 创建实例：`cli/gui → core.instance.create() → provider.download(jar) → 落盘 instance.json + eula.txt + server.properties(模板)`。
- 启动：`core.runtime.start() → java -Xmx… -jar server.jar nogui → 日志行流 → 状态机 CREATED→STARTING→(匹配 "Done (…s)!")→RUNNING → 控制台命令 stdin 直写`。
- 穿透：`tunnel.up(instance) → 派生 cloudflared/ssh 子进程 → 解析 stdout URL → 回写 instance.json → GUI/CLI 展示`；Pinggy 到期前 5 分钟自动重连并刷新地址。

## 外部系统（2026-10-15 全部实测验证）

| 系统 | 端点 | 用途 | 关键契约 |
|------|------|------|----------|
| Mojang | `piston-meta.mojang.com/mc/game/version_manifest_v2.json` → 版本 JSON | vanilla jar | latest.release=26.3；`downloads.server.url/sha1`；javaVersion.majorVersion=25 |
| PaperMC Fill v3 | `fill.papermc.io/v3/projects[/{p}/versions/{v}/builds/latest]` | paper/folia jar | `downloads["server:default"].url` + sha256；项目：paper、folia |
| Fabric | `meta.fabricmc.net/v2/versions/{game,installer,loader/{ver}}` | loader + installer | installer 1.1.2；`java -jar fabric-installer.jar server -dir . -mcversion X -loader Y -downloadMinecraft` |
| Purpur | `api.purpurmc.org/v2/purpur{/versions/{v}/builds/latest}` | purpur jar | current=26.2；下载 `{server}/versions/{v}/builds/{b}/download` |
| Modrinth | `api.modrinth.com/v2/{search,project/{id}/version}` | mod 搜索/下载 | facets 过滤 `versions`+`categories`+`project_type:mod`；需 User-Agent；`files[].url`；`env.server` 判服务端侧 |
| cloudflared | 子进程 `cloudflared tunnel --url http://localhost:{webPort}` | Web 控制台穿透 | 日志正则 `https://<slug>.trycloudflare.com`；仅 HTTP |
| Pinggy | 子进程 `ssh -p 443 -R0:localhost:{mcPort} tcp@a.pinggy.io` | 玩家裸 TCP | stdout 正则 `tcp://<host>:<port>`；60min 会话需自动续期 |
| Aliyun Maven | `maven.aliyun.com/repository/public` | 构建依赖镜像 | 已验证 javafx/atlantafx/picocli 等元数据齐全 |

## 重要技术边界

- JDK 25（MC 26.3 官方 Java 25；本机 Microsoft OpenJDK 25.0.1 实测 vanilla 26.3 启动通过 `Done (2.231s)`）。
- JavaFX 25.0.4（win 分类器）；AtlantaFX 3.0.0 做基础皮肤，shadcn 观感靠自定义 CSS（zinc 色板/8px 圆角/1px 边线）。
- core 仅用 JDK + Jackson + SnakeYAML，保证 CLI 无头环境零 JavaFX 依赖。
- HTTP 一律 JDK HttpClient；外部 API 需容错（Fill v3 曾整体迁移，v2 已 410——所有 provider 必须封装在 SPI 后面，可独立替换）。
- server.properties 写回保序保注释，`\:` 转义维持（`level-type=minecraft\:normal`）。
