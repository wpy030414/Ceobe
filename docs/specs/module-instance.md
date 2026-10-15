# Spec — 实例生命周期（ceobe-core/instance + runtime）

## 要构建什么
- 目标：实例的创建、启动、停止、删除、状态查询；进程的可靠托管。

## 行为
- 创建：校验名称（`[a-z0-9_-]{1,32}`）唯一 → 建目录 → 下载 jar（provider）→ 写 `eula.txt`（eula=true）→ 生成默认 `server.properties`（若缺）→ 写 `instance.json`。
- 启动：校验 jar 存在 → `ProcessBuilder` 派生 `java {-Xms -Xmx 用户jvmArgs} -jar <launchJar> nogui`，工作目录=实例目录 → 状态机推进；同时开启控制通道（`.ctl`）。
- 状态机：`CREATED → STARTING →（日志匹配 `Done \(\d+\.\d+s\)!`）→ RUNNING →（`stop` 命令或 destroy）→ STOPPING → STOPPED`；异常退出 → `CRASHED(exitCode)`。
- 停止：向 stdin 写 `stop\n`，宽限 60s（max-tick-time 默认 60s）后 `destroyForcibly()`。
- 控制台：stdin 直写 + 日志行回调（GUI/CLI/Web 订阅）；同时追加落盘 `console.log`。
- 删除：必须先 STOPPED；可选 `--purge` 连同世界数据删除（默认保留 world 归档提示）。

## 输入 / 输出
- 输入：InstanceSpec{name, serverType, gameVersion, loaderVersion?, memoryMb, jvmArgs, port, javaHome?}
- 输出：Instance{meta + liveState}；事件流：LogLine / StateChanged / TunnelAddressChanged。

## 约束
- 同一实例跨进程互斥：同一时刻只允许一个监督进程托管（`ProcessHandle` 存活 + `.state`/`.pid` 判定）；跨进程操作一律经控制通道（`.ctl` token 鉴权）。
- 端口占用预检（bind test）在启动前给出明确错误。
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

## 完成定义
- core JUnit E2E 全绿；CLI `ps` 能展示五态。
