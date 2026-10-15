# Spec — 配置可视化（ceobe-core/config）

## 要构建什么
- 目标：server.properties 的类型化 schema + 保序保注释读写，为 GUI 表单与 CLI get/set 提供统一后端。

## 行为
- Schema 基线 = vanilla 26.3 实测生成键集（46 键），每键标注：类型（bool/int/enum/string/range）、默认值、枚举值（gamemode: survival/creative/adventure/spectacular；difficulty: peaceful/easy/normal/hard）、中文友好名、分组（基础/游戏规则/网络/性能/安全/RCON）。
- 读：解析全部键值（含 `\:` 转义），未知键保留为"高级"组原文。
- 写：仅回写变更键，保持原文件顺序、行内注释、`#Minecraft server properties` 头与转义格式。
- `get(name,key)` / `set(name,key,value)`（含类型校验与越界提示，如 view-distance 3-32）。
- YAML 侧（paper/purpur）：`paper-global.yml`、`paper-world-defaults.yml`、`spigot.yml`、`bukkit.yml`、`purpur.yml` 提供树形查看 + 原文编辑（SnakeYAML round-trip 或原文编辑器兜底）。

## 输入 / 输出
- 输入：实例目录；变更集 Map<key,value>。
- 输出：PropertiesView（分组+类型化条目）供 GUI 渲染；写回后的文件差异。

## 约束
- 写回必须原子（temp+rename）。
- 服务器 RUNNING 时改配置 → 提示"重启后生效"，不自动重启。

## 边界条件
- motd 含 Unicode/§ 色码 → 原样字节写回。
- `level-type=minecraft\:normal` 冒号转义必须维持。
- 空值键（`level-seed=`）与 `generator-settings={}` 原样保留。

## 验收标准
- [ ] round-trip 测试：读取生成文件→写回→字节级仅变更键所在行不同。
- [ ] schema 覆盖 26.3 全部 46 键 + 1.21.x 兼容（旧键 `spawn-monsters` 等归入"未知/高级"不丢）。

## 完成定义
- GUI 能以表单改 max-players/gamemode/view-distance 并正确落盘。
