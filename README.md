# ceobe

本机 Minecraft 服务器编排系统：多实例创建/启动/停止、版本与加载器选配、配置可视化、Mod 选配、内网穿透，JavaFX 图形界面 + docker 风格无头 CLI 双形态。

## 这是什么？

- 定位：单机玩家的"私人服主面板"——在本机一键开多个 MC 服务器，让朋友通过公网地址直连。
- 解决的核心问题：开一个 mod 服要手动下载 jar、改 properties、装 mod、配穿透、记命令行——ceobe 把这条链路压缩为 GUI 三步或 CLI 一行。

## 为什么存在？

- 官方/社区工具（server jar、Modrinth、cloudflared）各自独立，缺少把"版本 → 配置 → mod → 穿透 → 运行"串起来的本机编排层。
- 现有面板（Pterodactyl 等）面向 Linux 服务器/容器，Windows 桌面玩家没有顺手的轻量选择。

## 如何安装和运行？

- 前置要求：JDK 25（MC 26.3 官方要求 Java 运行时 majorVersion 25）、Maven 3.9+、`cloudflared` 在 PATH（Web 控制台穿透）、系统 `ssh` 客户端（Pinggy 玩家通道）。
- 安装步骤：

  ```bash
  mvn package                # 全量构建（core + cli + gui）
  mvn -pl ceobe-cli -am package   # 仅重建 CLI（含依赖模块）
  ```

- 运行 / 启动：

  ```bash
  java -jar ceobe-gui/target/ceobe-gui.jar          # GUI
  java -jar ceobe-cli/target/ceobe.jar ps           # 无头 CLI（docker 风格）
  ```

## 仓库结构（Maven 多模块单仓 / monorepo）

```
ceobe/                聚合 POM（groupId im.xrl，统一版本 0.1.0-SNAPSHOT）
├── ceobe-core/       域核心：实例 / 进程 / provider / 配置 / Mod / 穿透（无 UI 依赖）
├── ceobe-cli/        无头 CLI（picocli，docker 风格语法）
└── ceobe-gui/        JavaFX 界面（shadcn 风格主题）
```

- 包名与坐标：`im.xrl:ceobe-*`，Java 包前缀 `im.xrl.ceobe.*`。
- 依赖方向单向：`cli`/`gui` → `core`；`core` 不感知 UI（保证无头环境可跑）。
- 构建/测试：`mvn test`（默认跳过需真实下载的 `network` 组）；`mvn test -Dtest.excludedGroups= -Dgroups=network` 跑端到端。

## 当前状态

- 阶段：开发中（原型 → MVP）。
- 已知限制：NeoForge 安装器源（maven.neoforged.net）在部分网络不可达，暂不在默认支持集；Pinggy 免费通道 60 分钟会话，地址随续期轮换。

## 核心技术

- Java 25 / Maven 多模块（ceobe-core、ceobe-cli、ceobe-gui）
- JavaFX 25 + AtlantaFX 3（shadcn/ui 风格化定制）、picocli 4.7（CLI）
- Jackson 2.18 / SnakeYAML 2.7（配置与状态）
- 外部 API：Mojang piston-meta、PaperMC Fill v3、Fabric meta v2、Purpur API、Modrinth API v2
