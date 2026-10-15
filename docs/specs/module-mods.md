# Spec — Mod 选配（ceobe-core/mods）

## 要构建什么
- 目标：基于 Modrinth v2 的搜索/安装/卸载/清单，自动过滤服务端可用性。

## 行为
- `search(query, gameVersion, loader, page)`：facets `[["versions:<gv>"],["categories:<loader>"],["project_type:mod"]]`。
- `versions(projectId|slug, gameVersion, loader)`：取匹配版本，选最新 stable。
- `install(instance, version)`：下载 `files[0](primary).url` → `mods/<filename>`；记录到 instance.json 的 mods[]（projectId, versionId, filename, sha1, env）。
- `uninstall(instance, modId)`：删文件 + 记录；`list(instance)`。
- 服务端侧过滤：以 `version.environment`（Modrinth 字符串枚举：`client_only` / `server_only` / `client_and_server` / `client_or_server*` / `unknown`）判定——仅 `client_only` 视为服务端不可用。
- 客户端专属 mod（`version.environment == client_only`）装服务端 → 阻止并解释（注意：`environment` 在 version 对象中是字符串，不是 `{client,server}` 对象）。
- 依赖（`dependencies[]`）仅提示，MVP 不自动安装。

## 输入 / 输出
- 输入：查询/项目/版本；实例。
- 输出：ModEntry 列表与安装结果（含失败原因）。

## 约束
- 请求必须带 User-Agent（Modrinth 强制）；限速出现 429 时指数退避。
- 文件落 `mods/`，`.part` 原子改名；sha1 校验（files[].hashes.sha1）。

## 边界条件
- fabric/neoforge/purpur 有 mods/；vanilla/paper 无 loader → UI 直接禁用 mod 页并说明原因。
- 同名文件重复安装 → 提示替换。

## 整合包（Modrinth `.mrpack`）
- `MrpackInstaller.readIndex(zip)`：解析 `modrinth.index.json` 的 `name` / `versionId` / `dependencies`（`minecraft` 与 `fabric-loader`/`neoforge`/`forge`/`quilt-loader`）。
- `MrpackInstaller.install(instanceDir, zip, progress)`：按 `files[]` 下载（跳过 `env.server = unsupported`；`path` 归一化防穿越；sha1 校验）；先解 `server-overrides/` 再解 `overrides/`（前者优先）。
- CLI：`ceobe modpack search | info <标识> | install <实例> <标识> | new <名称> <标识>`；`new` 依 `dependencies` 自动创建匹配实例（fabric / neoforge）后安装（一键）。GUI 在「整合包」页签提供同样能力（搜索 / 安装到本实例 / 新建并安装）。
- 文件经 `Http` 下载 → 自动走 Modrinth CDN 镜像（见 ADR-012）；加载器 forge/quilt 的整合包暂不支持并明确报错。

## 验收标准
- [ ] 搜索 "fabric api" @26.3+fabric → 安装成功且文件在 mods/。
- [ ] env.server=unsupported 的 mod 被拦截。
- [ ] 卸载后 instance.json 与 mods/ 一致。
- [ ] `modpack new <名> <标识>` 依索引建实例并把 `mods/` 与 overrides 落地；`modpack install` 对不匹配实例报错。

## 完成定义
- GUI mod 页三步可用：搜→装→启服不崩。
