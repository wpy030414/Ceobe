# Spec — 无头 CLI（ceobe-cli）

## 要构建什么
- 目标：docker 心智的 picocli CLI，覆盖 core 全部能力；脚本化友好。

## 行为
- 命令树：
  - `ceobe ps [-a]` — 实例列表（默认仅运行中，`-a` 含已停止；`--format json`）
  - `ceobe create <name> --type paper|vanilla|fabric|purpur --version <v> [--loader <lv>] [--mem 4G] [--port 25565] [--jvm "args"]`
  - `ceobe start|stop|restart|rm [--purge] <name>`
  - `ceobe logs [-f] [--tail N] <name>`
  - `ceobe exec <name> <cmd...>` — 服务端控制台命令
  - `ceobe config get <name> <key>` / `config set <name> <k> <v>` / `config ls <name>`
  - `ceobe mod search|install|rm|ls <name> ...`
  - `ceobe tunnel up|down|status <name> [--kind pinggy|cloudflare|lan]`
  - `ceobe versions --type <t>` — 可用版本
  - `ceobe doctor` — 环境自检；`ceobe web [--port N]` — 无头 Web 控制台
  - GUI 由独立 jar 提供：`java -jar ceobe-gui/target/ceobe-gui.jar`
- 输出：人类表格默认；`--format json` 全局选项；退出码 0/1/2（成功/失败/用法错误）；中文消息。
- `logs -f`：tail `console.log` 追加流，Ctrl+C 干净退出（不杀服务器）。

## 输入 / 输出
- 输入：命令行参数/选项。
- 输出：stdout（可管道）/stderr（诊断）；JSON 模式键名稳定。

## 约束
- 单 jar 分发（shade）；无 JavaFX 依赖；Windows GBK 控制台需 `-Dfile.encoding=UTF-8` + chcp 处理或输出编码自适应。
- 复用 core，禁止 CLI 内实现业务。

## 边界条件
- `logs -f` 时实例被 stop → 流正常结束而非挂死。
- 名称不存在 → 明确错误 + 相似名建议。

## 验收标准
- [ ] 冒烟脚本：create→start→exec say→stop→rm 全链路退出码全 0。
- [ ] `ps --format json` 可被 jq 解析。

## 完成定义
- 无 GUI 机器上完成 PRD 场景 3。
