# Spec — 服务端 jar 提供者（ceobe-core/provider）

## 要构建什么
- 目标：ServerJarProvider SPI + 四实现，屏蔽各官方源的协议差异。

## 行为
- `listVersions()`：分组返回（26.x / 1.21.x / …，仅 release）。
- `resolve(spec) → DownloadPlan{url, checksum(sha1|sha256), size, fileName}`。
- `install(instanceDir, plan)`：下载 `.part` → 校验 → 原子 rename → 返回启动命令描述。
- 各源契约（2026-10-15 实测）：
  - Mojang：manifest v2 → `versions[].url` → 版本 JSON `downloads.server.{url,sha1,size}`。
  - Paper：`fill.papermc.io/v3/projects/paper/versions/{v}/builds/latest` → `downloads["server:default"]{url,checksums.sha256}`；项目含 folia。
  - Fabric：meta v2 取 loader 最新 + installer 1.1.2 → 子进程 `java -jar fabric-installer.jar server -dir <dir> -mcversion <v> -loader <l> -downloadMinecraft`。
  - Purpur：`api.purpurmc.org/v2/purpur` → `versions/{v}/builds` → `{v}/{b}/download`。

## 输入 / 输出
- 输入：ServerType + gameVersion（+ loaderVersion 可空=最新 stable）。
- 输出：DownloadPlan / 安装后的启动器描述（vanilla/paper/purpur：server.jar；fabric：fabric-server-launch.jar）。

## 约束
- 全部请求带 User-Agent `ceobe/<version>`。
- 版本列表缓存 10 分钟（内存），jar 永久去重缓存于 `~/.ceobe/cache/jars/`（同 sha 复用，多实例共享）。
- 失败必须区分：网络失败 / 校验失败 / 版本不存在，错误信息可操作。

## 边界条件
- 26.3-rc-3 等预发布默认隐藏（`--pre` 开启）。
- Fabric installer 子进程失败 → 透传其 stderr 摘要。
- Purpur 无 26.3 构建时降级提示可选 26.2。

## 验收标准
- [ ] 四 provider `listVersions` + `resolve` 集成测试打真实 API（标记 @Tag("network")）。
- [ ] vanilla 下载 sha1 校验通过；断网重试一次后报可读错误。

## 完成定义
- create 流程用任一 provider 全通即达标。
