# Spec — 穿透与 Web 控制台（ceobe-core/tunnel + webconsole）

> **实现现状（2026-10-21）**：本文描述产品语义契约；C# 实现见 `src/Ceobe.Core/Tunnel`。
> Java 版实现已功能冻结，仅作语义参考；迁移决策见 ADR-019。


## 要构建什么
- 目标：TunnelProvider SPI，默认装配 CF Quick Tunnel（HTTP）+ Pinggy（TCP 自动续期）+ LanDirect；内嵌只读 Web 控制台作为 CF 隧道的目标。

## 行为
- SPI：`start(instance, TunnelKind) → TunnelSession{kind, publicUrl, expiresAt?}`；`stop(session)`；状态回调。
- CloudflareQuickTunnel：子进程 `cloudflared tunnel --url http://127.0.0.1:<webPort>`；stderr/stdout 正则 `https://[a-z0-9-]+\.trycloudflare\.com`；仅 HTTP（实证见 ADR-003）。
- PinggyTunnel：子进程 `ssh -p 443 -o StrictHostKeyChecking=no -R0:127.0.0.1:<mcPort> tcp@a.pinggy.io`；stdout 正则 `tcp://([^:]+):(\d+)`；会话 60min → 到期前 5 分钟自动重启并广播新地址（StateChanged 事件）；ssh 不在 PATH → 明确报缺。
- LanDirect：枚举本机非环网 IPv4，展示 `ip:port`。
- Web 控制台：JDK com.sun.net.httpserver，端口随机；`GET /`（单页：实例状态卡+最近日志）、`GET /api/instances`、`GET /api/instances/{n}/logs?tail=200`；仅绑定 127.0.0.1（安全边界：公网可达性完全由 CF 隧道承担，地址随机即隐式凭证）。

## 输入 / 输出
- 输入：实例 + 通道类型；mcPort/webPort。
- 输出：TunnelSession（公网地址）；日志/状态 JSON。

## 约束
- 隧道子进程跟随实例停止联动（可选配置 keepAfterStop=false 默认）。
- 地址轮换必须推送到 GUI 事件流 + CLI `tunnel status` 可查。
- 不解析/不存储任何账号凭证（两通道均免登录）。

## 边界条件
- cloudflared/ssh 缺失 → up 前预检报"未安装+指引"。
- trycloudflare URL 出现前日志异常（配额/断网）→ 30s 超时报错。
- Pinggy 续期后旧地址立即可能为他人复用 → UI 提示"地址已轮换，请重新分享"。

## 验收标准
- [ ] 实测：up 后从公网侧 curl CF 地址返回 200；Pinggy tcp 地址外部 `nc` 可达且 PONG 回显。
- [ ] 自动续期：模拟到期（kill ssh）→ 5 分钟窗口内新会话建立并事件通知。
- [ ] stop 后两子进程无残留（Windows 进程树清理验证）。

## 完成定义
- 玩家拿 `host:port` 原版客户端多人游戏直连成功。
