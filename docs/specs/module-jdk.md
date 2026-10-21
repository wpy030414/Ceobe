# Spec — 运行时 JDK 管理（ceobe-core/jdk）

> **实现现状（2026-10-21）**：本文描述产品语义契约；C# 实现见 `src/Ceobe.Core/Jdk`。
> Java 版实现已功能冻结，仅作语义参考；迁移决策见 ADR-019。


## 要构建什么
- 目标：为每个实例自动确定并准备合适的 Java 运行时——探测所需主版本、缺则提醒、可按平台一键安装、并自动选用。

## 行为
- `JavaRequirement.requiredMajor(mcVersion)`：取自 piston-meta 的 `javaVersion.majorVersion`（vanilla 派生版本通用），失败兜底 21，结果缓存。
- `JavaEnv.runtimeMajor()` / `majorOf(javaHome)`：解析当前 JVM 或指定 JRE 主版本（`release` 文件优先，退化跑 `java -version`）。
- `JavaEnv.hasLauncher(javaHome)` / `hostJavaHome()` / `javaLauncher()`：以「是否存在 `<home>/bin/java`」判定该运行时可否用于 fork 子进程——jpackage 打包的 GUI 运行时会剥离该启动器（GUI 由原生 launcher 直连 jvm.dll），故 `java.home` 不可无条件信任。
- `JdkStore`：`~/.ceobe/jdk/<jdk-…>/` 下的自管 JDK；`list()` / `findMajor(major)`。
- `JdkInstaller.install(major, progress)`：查 Adoptium assets API 取包名 → 优先清华镜像下载（失败回退 Adoptium 官方）→ 解压（zip 用 `java.util.zip`；tar.gz 用系统 `tar`）→ 返回 javaHome。
- `JavaResolver.resolve(gameVersion, explicitJavaHome)`：显式指定 > 当前 JVM（**仅当确有 java 启动器**）> 自管 JDK 命中 > 宿主 `JAVA_HOME`；返回 required/actual/ok/message。
- `ServerProcess.buildCommand` / CLI `spawnSupervisor`：一律经 `JavaEnv` 解析 java 启动器，定位不到即抛明确错误（不得回退到不可用的 `java.home`）。
- 落点：`InstanceManager.create` 用 resolve 写入 `meta.javaHome`；CLI create/start 打印提醒；GUI 新建对话框显示提示并给「安装 Java N」按钮。

## 输入 / 输出
- 输入：MC 版本、可选 javaHome。
- 输出：javaHome（可空 = 继承）、提醒文案；CLI `ceobe jdk list | install <n> | required <ver> | use <实例> <n>`。

## 约束
- 下载优先中国镜像（清华 Adoptium），失败回退官方 GitHub。
- 不修改系统 PATH/注册表；JDK 仅落在 ceobe 主目录，可整体删除。
- zip 解压防目录穿越（entry 归一化后须仍在目标目录内）。

## 边界条件
- 平台：os ∈ {windows, linux, mac}、arch ∈ {x64, aarch64}；非 Windows 用 tar.gz。
- 版本不在 Mojang 清单（如快照）→ 兜底主版本，不阻塞。
- 已装 JDK 与需求同主版本即视为满足（不比较具体小版本）。

## 验收标准
- [ ] `jdk required 26.3 / 1.21.8 / 1.20.4` 分别得 25 / 21 / 17。
- [ ] `jdk install 17` 在 Windows 装出可用 `bin/java.exe`。
- [ ] 需要 Java 17 的实例（运行期 25）create 后 `javaHome` 指向自管 17，start 以该 JRE 启动并达 RUNNING。
- [ ] `JavaEnv.hasLauncher` 对含 `bin/java` 的真 JDK 为真、对剥离启动器的目录为假（`JavaEnvTest`）。
- [ ] 打包 GUI（jpackage runtime 无 `bin/java`）中启动实例不再静默失败：要么正确选用宿主 `JAVA_HOME` / 自管 JDK，要么给出明确报错。

## 完成定义
- 「缺 JDK → 提醒 → 一键安装 → 自动选用」闭环可用；CLI 与 GUI 均可见。
