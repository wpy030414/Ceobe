# Spec — 无头 CLI（Ceobe.Cli）

> **实现现状（2026-10-21）**：`src/Ceobe.Cli`，System.CommandLine 2.0.0-beta5，产物 `ceobe.exe`（自包含单文件）。
> Java 版（picocli）已随迁移退役，源码仅存于 tag `pre-csharp-migration`。

## 要构建什么

- 目标：docker 心智的无头 CLI，覆盖 core 全部能力；脚本化友好。

## 行为

- **命令树**（全部为顶层命令 + 子命令组，`ceobe <cmd> --help` 可查）：
  - `ceobe ps [-a]` — 实例列表（默认仅运行中，`-a` 含已停止；`--format json`）
  - `ceobe create|new <name> --type paper|vanilla|fabric|purpur|neoforge --version <v> [--loader <lv>] [--mem 4G] [--port 25565] [--jvm "args"] [--java-home <dir>]`
  - `ceobe start [--detach] <name>` / `stop` / `restart` / `rm [--purge] <name>`
  - `ceobe logs [-f] [--tail N] [--limit N] <name>`
  - `ceobe exec <name> <cmd...>` — 服务端控制台命令
  - `ceobe config get <name> <key>` / `config set <name> <k> <v>` / `config ls <name> [--group <g>]`
  - `ceobe mod search|install|rm|ls <name> …`
  - `ceobe modpack search|info|install <name> <pack> | new <pack> --name <n> …`
  - `ceobe tunnel up|down|status <name> [--kind pinggy|cloudflare|tailscale|lan]`
  - `ceobe jdk list|install <n>|required <mcver>|use <name> <n>`
  - `ceobe versions --type <t>` — 可用版本
  - `ceobe doctor` — 环境自检；`ceobe web [--port N]` — 无头 Web 控制台
  - `ceobe update <name> [--name] [--mem] [--port] [--jvm] [--authlib]` — 修改实例设置
  - `ceobe supervise <name>` — **隐藏命令**，供 `start --detach` 内部重启自身为监督进程，不面向用户
- **输出**：人类表格默认；`--format json` 全局选项；退出码 **0 成功 / 1 运行失败 / 2 用法错误**；中文消息。
- `logs -f`：tail `console.log` 追加流，Ctrl+C 干净退出（不杀服务器）。

## 输入 / 输出

- 输入：命令行参数/选项。
- 输出：stdout（可管道）/ stderr（诊断）；JSON 模式键名稳定。

## 约束

- 自包含单文件（`PublishSingleFile` + 压缩），运行时不需要目标机装 .NET。
- **必须设 `EnableDefaultExceptionHandler = false`**：否则库会吞掉 action 抛出的异常并打印原始堆栈，退出码契约（CeobeSpec=2 / CeobeException=1 / OperationCanceled=130）永远不会生效。
- 无参调用打印帮助并返回 0（`root.SetAction(pr => HelpAction)`），不是错误。
- 复用 core，禁止 CLI 内实现业务；接线统一走 `CeobeComposition`，与 GUI 完全一致。
- Windows 控制台编码：输出按 UTF-8，不做 chcp 副作用。

## 边界条件

- `logs -f` 时实例被 stop → 流正常结束而非挂死。
- 名称不存在 → 明确错误 + 相似名建议。
- `--jvm ""` / `--authlib ""` 语义是「清空该项」，与「不传参数 = 不改」严格区分；beta5 不接受 `--jvm=` 空值形式，帮助文本已注明用 `--jvm ""`。

## 验收标准

- [ ] 冒烟链路：create → start → exec say → stop → rm，退出码全 0。
- [ ] `ps --format json` 可被 jq 解析。
- [ ] 用法错误返回 2、业务失败返回 1（实测 exe，非单测）。

## 完成定义

- 无 GUI 机器上完成 PRD 场景 3。
