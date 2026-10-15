#!/usr/bin/env bash
#
# ceobe CLI 端到端冒烟：创建 → 后台启动 → 命令下发 → 日志 → 穿透 → 重启 → 停止 → 删除
#
# 用法：
#   bash scripts/smoke-cli.sh [实例名] [端口]
#
# 说明：
#   - 使用独立 CEOBE_HOME（默认 .smoke-home/）以免污染日常实例
#   - 需要网络下载服务端（首次较慢，jar 会缓存复用）
#   - 依赖：JDK 25、已构建的 ceobe-cli/target/ceobe.jar、ssh（Pinggy 通道）
#   - 任一步骤失败即以非 0 退出，便于接入 CI

set -uo pipefail

NAME="${1:-smoke}"
PORT="${2:-25597}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
JAR="$ROOT/ceobe-cli/target/ceobe.jar"
HOME_DIR="$ROOT/.smoke-home"

if [ ! -f "$JAR" ]; then
  echo "缺少 $JAR，请先执行：mvn -pl ceobe-cli -am package -DskipTests" >&2
  exit 2
fi

J="java -Dceobe.home=$HOME_DIR -jar $JAR"
FAILED=0
STEP=0

step() {
  STEP=$((STEP + 1))
  echo
  echo "===== [$STEP] $* ====="
}

expect_ok() {
  local desc="$1"; shift
  if "$@"; then
    echo "  ✓ $desc"
  else
    echo "  ✗ $desc（退出码 $?）" >&2
    FAILED=$((FAILED + 1))
  fi
}

cleanup() {
  step "清理"
  $J stop "$NAME" >/dev/null 2>&1 || true
  $J rm "$NAME" --purge >/dev/null 2>&1 || true
  $J tunnel down "$NAME" >/dev/null 2>&1 || true
}
trap cleanup EXIT

step "环境自检"
expect_ok "doctor 通过" $J doctor

step "创建实例（paper，端口 $PORT）"
expect_ok "create" $J create "$NAME" --type paper --version 26.3 --mem 2G --port "$PORT"

step "后台启动（独立监督进程）"
expect_ok "start -d" $J start -d "$NAME"

step "实例列表"
expect_ok "ps 可见运行中实例" bash -c "$J ps | grep -q '$NAME'"

step "配置读写"
expect_ok "config set" $J config set "$NAME" view-distance 8
expect_ok "config get 回读一致" bash -c "$J config get $NAME view-distance | grep -q '^8$'"

step "控制台下发命令"
expect_ok "exec 送达" $J exec "$NAME" "say smoke-$NAME"
sleep 3
expect_ok "日志出现命令回显" bash -c "$J logs $NAME --tail 200 | grep -q 'smoke-$NAME'"

step "玩家通道（Pinggy 裸 TCP，免登录）"
if $J tunnel up "$NAME" --kind pinggy > /tmp/ceobe-smoke-tunnel.txt 2>&1; then
  if grep -qE 'tcp://[a-z0-9.-]+:[0-9]+' /tmp/ceobe-smoke-tunnel.txt; then
    echo "  ✓ 获得公网 TCP 地址：$(grep -oE 'tcp://[a-z0-9.-]+:[0-9]+' /tmp/ceobe-smoke-tunnel.txt | head -1)"
  else
    echo "  ✗ 未解析到 tcp:// 地址" >&2
    FAILED=$((FAILED + 1))
  fi
else
  echo "  ✗ tunnel up 失败（详见 /tmp/ceobe-smoke-tunnel.txt）" >&2
  FAILED=$((FAILED + 1))
fi
expect_ok "tunnel status 列出通道" bash -c "$J tunnel status $NAME | grep -q 'pinggy\|Pinggy'"
expect_ok "tunnel down" $J tunnel down "$NAME"

step "重启"
expect_ok "restart" $J restart "$NAME"
expect_ok "重启后仍运行中" bash -c "$J ps | grep -q '$NAME'"

step "优雅停止"
expect_ok "stop" $J stop "$NAME"
expect_ok "默认 ps 不再显示（-a 可见已停止）" bash -c "! $J ps | grep -q '$NAME' && $J ps -a | grep -q '$NAME'"

step "删除"
expect_ok "rm --purge" $J rm "$NAME" --purge
expect_ok "实例已不存在" bash -c "! $J ps -a 2>/dev/null | grep -q '$NAME'"

echo
if [ "$FAILED" -eq 0 ]; then
  echo "全部通过 ✓"
  exit 0
else
  echo "存在 $FAILED 项失败 ✗" >&2
  exit 1
fi
