# AGENTS.md

> **迁移中（2026-10-21）**：本项目已决策用 **C# / .NET 10 + WinUI 3** 重构，范围收缩为 Windows 专属。
> 见 `docs/DECISIONS.md` ADR-019 与 **`docs/MIGRATION.md`**（施工图：模块映射、七条不得重写的硬契约、阶段闸门、训练员反馈整改项）。
> **Java 代码（本仓库现存部分）自此刻功能冻结、只修 bug**；基线 tag `pre-csharp-migration`。新功能与新改动一律进 C# 侧，改动前先读 `docs/MIGRATION.md`。

ceobe 是 Java 25 编写的 Minecraft 服务器编排系统（Maven 多模块：core 域逻辑 / cli 无头 / gui JavaFX）。本文档帮助 Agent 快速定位项目。

## 概述

- 本项目是什么：本机 MC 服务器多实例管理器——下载服务端 jar（vanilla/paper/fabric/neoforge/purpur）、可视化编辑 server.properties、Modrinth 装模、CF/Pinggy 穿透、进程生命周期管理；GUI 与 CLI 共享 core。

## 边界与范围

- 范围内：Windows 优先的单机编排；Java 版服务端；文件级状态存储（`~/.ceobe/`）。
- 非目标（明确排除）：Linux 容器化部署、Bedrock 支持面板、多用户/多租户、CurseForge（API 强制 key，暂缓）、计费/商业化。

## Agent 操作指南

- 如何理解本项目：先读 `docs/ARCHITECTURE.md`（模块与外部 API 契约）与 `docs/DECISIONS.md`（已定技术决策，含穿透方案实测依据），再进对应模块 spec。
- 全局规则 / 约定：
  - 构建一律 Maven：`mvn -pl <module> -am package`；测试 `mvn test`。不引入 Gradle。
  - 版本号为 CalVer `YY.MM.DD-HH`（如 `26.10.15-14`），**根 `pom.xml` 的 `<version>` 是唯一权威**，CI 只读不写；三子模块 `<parent>` 版本须同步（bump 用 `mvn versions:set -DnewVersion=<v> -DprocessAllModules`）。安装包版号取前三段（`26.10.15`）——macOS jpackage 仅接受 1–3 段数字且不以 0 开头（ADR-017）。
  - 命名空间：groupId `im.xrl`，Java 包前缀 `im.xrl.ceobe`；资源路径同前缀（如 `/im/xrl/ceobe/gui/shadcn.css`）。
  - 测试分组：需真实下载/联网的用例打 `@Tag("network")`，`mvn test -Dtest.excludedGroups= -Dgroups=network` 才跑。
  - 测试方法名用中文标识符时不得含空格（Java 标识符规则）。
  - **所有下载（开发期与运行期）一律优先国内镜像站**，否则太慢：Maven 依赖用 Aliyun；运行期经 `core/util/Mirrors` 改写 URL——Mojang piston-meta/piston-data → BMCLAPI（同路径换主机）、Fabric meta → BMCLAPI `/fabric-meta`、Modrinth CDN → `mod.mcimirror.top`、Temurin JDK → 清华镜像；镜像失败自动回退上游。新增第三方源前须先确认其镜像可用性。
  - `ceobe-core` 不得依赖 JavaFX / picocli；CLI 与 GUI 只通过 core 的公共 API 工作。
  - 所有对外 HTTP 请求必须带 User-Agent（Modrinth 要求，Fill v3 建议）。
  - git commit 遵循 Conventional Commits，subject ≤72 字符，body 分点。
  - server.properties 写回必须保序、保注释、保转义（`minecraft\:normal` 形式）。
  - 端口在实例间排他：创建/启动经 `InstanceManager.assertPortExclusive` 校验，空闲端口用 `suggestFreePort` / CLI `--port auto`；穿透通道一律指向实例自己的 `meta.port`。
  - 实例所需 Java 由 `im.xrl.ceobe.core.jdk` 探测/托管：`create` 自动解析并写入 `meta.javaHome`（显式 > 自管 JDK 命中 > 宿主）；缺则提醒，`ceobe jdk install <n>` 一键装（Adoptium + 清华镜像）。
  - GUI 自检：`-Dceobe.snapshot=<png> [-Dceobe.snapshot.tab=配置]` 启动即导出 Scene 快照（无需窗口焦点），用于 UI 回归。

## 目录速查

- `ceobe-core/` — 域核心：实例模型、进程管理、jar 提供者、配置、Modrinth、穿透 SPI
- `ceobe-cli/` — picocli 无头入口（docker 风格语法）
- `ceobe-gui/` — JavaFX 界面（shadcn 风格定制主题）
- `scripts/smoke-cli.sh` — CLI 端到端冒烟（创建→启动→命令→穿透→重启→停止→删除，断言式）
- `.github/workflows/gui-release.yml` — CI：main 推送即打包 GUI（Windows .exe / macOS .dmg / Linux AppImage）并发布 Release（tag `v<CalVer>`，标题/正文复用 commit msg），同名旧任务自动取消
- `docs/PRD.md` — 产品需求；`docs/ARCHITECTURE.md` — 结构与外部契约；`docs/DECISIONS.md` — ADR；`docs/specs/` — 模块规格
