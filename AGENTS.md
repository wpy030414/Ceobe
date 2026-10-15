# AGENTS.md

ceobe 是 Java 25 编写的 Minecraft 服务器编排系统（Maven 多模块：core 域逻辑 / cli 无头 / gui JavaFX）。本文档帮助 Agent 快速定位项目。

## 概述

- 本项目是什么：本机 MC 服务器多实例管理器——下载服务端 jar（vanilla/paper/fabric/purpur）、可视化编辑 server.properties、Modrinth 装模、CF/Pinggy 穿透、进程生命周期管理；GUI 与 CLI 共享 core。

## 边界与范围

- 范围内：Windows 优先的单机编排；Java 版服务端；文件级状态存储（`~/.ceobe/`）。
- 非目标（明确排除）：Linux 容器化部署、Bedrock 支持面板、多用户/多租户、CurseForge（API 强制 key，暂缓）、计费/商业化。

## Agent 操作指南

- 如何理解本项目：先读 `docs/ARCHITECTURE.md`（模块与外部 API 契约）与 `docs/DECISIONS.md`（已定技术决策，含穿透方案实测依据），再进对应模块 spec。
- 全局规则 / 约定：
  - 构建一律 Maven：`mvn -pl <module> -am package`；测试 `mvn test`。不引入 Gradle。
  - 命名空间：groupId `im.xrl`，Java 包前缀 `im.xrl.ceobe`；资源路径同前缀（如 `/im/xrl/ceobe/gui/shadcn.css`）。
  - 测试分组：需真实下载/联网的用例打 `@Tag("network")`，`mvn test -Dtest.excludedGroups= -Dgroups=network` 才跑。
  - 测试方法名用中文标识符时不得含空格（Java 标识符规则）。
  - 网络下载默认走中国镜像（Maven 仓库用 Aliyun）；外部 MC API 直连。
  - `ceobe-core` 不得依赖 JavaFX / picocli；CLI 与 GUI 只通过 core 的公共 API 工作。
  - 所有对外 HTTP 请求必须带 User-Agent（Modrinth 要求，Fill v3 建议）。
  - git commit 遵循 Conventional Commits，subject ≤72 字符，body 分点。
  - server.properties 写回必须保序、保注释、保转义（`minecraft\:normal` 形式）。

## 目录速查

- `ceobe-core/` — 域核心：实例模型、进程管理、jar 提供者、配置、Modrinth、穿透 SPI
- `ceobe-cli/` — picocli 无头入口（docker 风格语法）
- `ceobe-gui/` — JavaFX 界面（shadcn 风格定制主题）
- `scripts/smoke-cli.sh` — CLI 端到端冒烟（创建→启动→命令→穿透→重启→停止→删除，断言式）
- `docs/PRD.md` — 产品需求；`docs/ARCHITECTURE.md` — 结构与外部契约；`docs/DECISIONS.md` — ADR；`docs/specs/` — 模块规格
