# PRD — ceobe Minecraft 服务器编排系统

## 产品目标

- 要达成的目标：让一个普通玩家在 Windows 本机 10 分钟内开出第一个可被公网朋友直连的 mod 服；老服主用一个界面管 N 个实例互不干扰。

## 用户与使用场景

- 目标用户：Java 版玩家/小型朋友圈服主；会装 Java 但不想碰 properties 文件、端口映射、ssh 的人。
- 典型场景：
  1. 小 A 想和朋友玩 fabric 生存：GUI 新建实例 → 选 Fabric/26.3 → 内存 4G → 搜装 3 个 mod → 开服 → 点"开启穿透" → 把 `tcp://host:port` 发群里。
  2. 服主 B 同时维护 paper 创造服 + fabric 模组服：面板里两实例独立启停、独立控制台、独立配置页。
  3. 玩家 C 只有终端（远程/无显示器机器）：`ceobe instance create … && ceobe instance start …`，纯 CLI 跑通全流程。

## 核心问题

- 要解决的关键问题：服务端获取（版本×加载器矩阵）、配置门槛（properties 键多且类型杂）、mod 依赖地狱（客户端/服务端环境标记）、公网可达（家宽无公网 IP）四件事的一站式整合。

## 功能及其意义

| 功能 | 解决什么 | 为什么需要 |
|------|----------|------------|
| 多实例管理（创建/启停/删除/克隆） | 一台机器多服并存 | 每个实例独立目录、端口、内存，互不污染 |
| 版本与加载器选配 | vanilla/paper/fabric/neoforge/purpur × 26.3/1.21.x… | 用户不想自己去五个网站下 jar |
| 配置可视化（server.properties 表单） | 人数/游戏模式/视距/难度/端口等键改不动 | 类型化控件替代文本编辑，写回保序保注释 |
| 实例设置可编辑（名称/内存/端口/JVM） | 创建后改不了内存/端口，只能重建实例 | 停止后即可修改；改端口同步 server.properties |
| 三方鉴权（authlib-injector） | 正版验证把非正版玩家挡在门外 | 填 Yggdrasil API 地址即以 `-javaagent` 注入并自动关闭正版验证 |
| Mod 选配（Modrinth 搜索/安装/卸载） | 手动下 jar 丢 mods/ 且分不清客户端服端 | 按 game version + loader 过滤，按 env 标记只装服务端侧 |
| 整合包一键装（`.mrpack`） | 手动凑整合包（几十个 mod + 配置覆盖）费时易错 | 导入 Modrinth 整合包：按索引批量下载 mod/资源并解包 overrides |
| 运行时 JDK 管理 | 不同 MC 版本要求不同 Java（1.20.4→17、1.21.8→21、26.x→25） | 自动探测所需主版本，缺则提醒并可按平台一键安装（Adoptium + 清华镜像），实例自动选用 |
| 穿透集成 | 家宽无公网 IP，朋友连不进来 | CF Quick Tunnel 穿 Web 控制台 + Pinggy 承载玩家裸 TCP（自动续期） |
| 控制台（日志流 + 命令下发） | 开服后看不到日志、敲不了 say/op | 实时 tail + stdin 直通服务端 |
| 无头 CLI（docker 风格） | 服务器/远程场景无 GUI | `create/start/stop/ps/logs/exec/rm` 语法对齐 docker 心智 |
| Web 控制台（远程管理） | 服主不在家也想看服 | 走 CF 隧道的只读状态+日志页 |

## 功能之间的关系

- 实例管理是根：其余功能（配置/mod/穿透/控制台）都挂载在实例上；CLI 与 GUI 是同一 core 的两个壳；Web 控制台由 core 内嵌 HTTP 服务提供、经 CF 隧道暴露。

## 范围与非目标

- 范围内：上表全部；**Windows 10（1809+）/ 11 专属**——ADR-019 已明确放弃跨平台，产品形态为 Windows 桌面应用 + 无头 CLI；Java 版（Minecraft）服务端 1.20.2+ 至 26.x（运行时 Java 由 ceobe 按版本自动探测/一键安装，不再强制宿主环境版本）。
- 范围外：Bedrock/基岩服、CurseForge mod 源（API key 门槛）、容器化与集群、玩家权限管理编辑器、自动备份（后续版本）。
