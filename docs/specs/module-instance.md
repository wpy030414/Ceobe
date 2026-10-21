# Spec — 实例生命周期（ceobe-core/instance + runtime）

> **实现现状（2026-10-21）**：本文描述产品语义契约；C# 实现见 `src/Ceobe.Core/Runtime`（InstanceManager / ServerProcess / InstanceControl / ControlServer）与 `src/Ceobe.Core/Model`。
> Java 版实现已功能冻结，仅作语义参考；迁移决策见 ADR-019。


## 要构建什么
- 目标：实例的创建、启动、停止、删除、状态查询；进程的可靠托管。

## 行为
- 创建：校验名称（`[a-z0-9_-]{1,32}`）唯一 → **校验端口在实例间排他**（不得与其它实例已登记端口相同，且本机当前可独占绑定）→ 建目录 → 下载 jar（provider）→ 写 `eula.txt`（eula=true）→ 生成默认 `server.properties`（若缺）→ 写 `instance.json`。
- 启动：校验 jar 存在 → `ProcessBuilder` 派生 `java {-Xms -Xmx 用户jvmArgs} -jar <launchJar> nogui`，工作目录=实例目录 → 状态机推进；同时开启控制通道（`.ctl`）。
- 状态机：`CREATED → STARTING →（日志匹配 `Done \(\d+\.\d+s\)!`）→ RUNNING →（`stop` 命令或 destroy）→ STOPPING → STOPPED`；异常退出 → `CRASHED(exitCode)`。
- 停止：向 stdin 写 `stop\n`，宽限 60s（max-tick-time 默认 60s）后 `destroyForcibly()`。
- 控制台：stdin 直写 + 日志行回调（GUI/CLI/Web 订阅）；同时追加落盘 `console.log`。
- 删除：必须先 STOPPED；可选 `--purge` 连同世界数据删除（默认保留 world 归档提示）。
- 修改：`updateInstance(name, memoryMb, port, jvmArgs, authlibUrl)` 与 `rename(old,new)`（均要求已停止）；端口变更同步写回 `server.properties` 的 server-port/query.port；设置 authlib 自动关闭 `online-mode`。
- 三方鉴权：配置 authlib-injector 后，启动以 `-javaagent:authlib-injector.jar=<url>` 注入（jar 缓存在 `~/.ceobe/cache/jars/`）。

## 输入 / 输出
- 输入：InstanceSpec{name, serverType, gameVersion, loaderVersion?, memoryMb, jvmArgs, port, javaHome?}
- 输出：Instance{meta + liveState}；事件流：LogLine / StateChanged / TunnelAddressChanged。

## 约束
- 同一实例跨进程互斥：同一时刻只允许一个监督进程托管（`ProcessHandle` 存活 + `.state`/`.pid` 判定）；跨进程操作一律经控制通道（`.ctl` token 鉴权）。
- 端口排他：`InstanceManager.assertPortExclusive` 在创建/启动前拒绝与其它实例重复的端口并报占用者名称（穿透地址须唯一对应一个服务端）；`suggestFreePort` / CLI `--port auto` / GUI 自动填入空闲端口。启动时另做 OS 层 bind 预检（`ServerProcess.checkPortFree`）。
- core 不准弹任何 UI/交互。

## 边界条件
- java 进程被外部杀死 → 看门狗 `waitFor` 立即感知 → 转 CRASHED 并清理 `.pid`；监督进程被强杀则控制通道失效，下次操作自动清理陈旧 `.ctl`。
- server.properties 缺失/损坏 → 用模板重建并告警，不阻塞启动。
- 磁盘不足/下载中断 → jar 落盘为 `.part`，完成校验 sha1/sha256 后原子改名。

## 验收标准
- [ ] E2E：create(vanilla/26.3) → start → 120s 内达 RUNNING → exec("say hi") 有日志回显 → stop → 优雅退出码 0。
- [ ] 已运行实例再次 start 被拒绝并提示当前状态。
- [ ] 服务端进程被外部结束 → 状态转 CRASHED，可再次启动。
- [ ] 监督进程被强杀后，`stop`/`exec` 给出「控制通道已失效」并自愈。
- [ ] 与其它实例端口冲突时 create/start 被拒绝并给出占用者名称；`--port auto` 选出空闲端口。

## 完成定义
- core JUnit E2E 全绿；CLI `ps` 能展示五态。
