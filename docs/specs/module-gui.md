# Spec — JavaFX GUI（ceobe-gui）

## 要构建什么
- 目标：shadcn/ui 观感的桌面面板：实例列表、创建向导、控制台、配置表单、Mod 市场、穿透面板。

## 行为
- 布局：左侧全高侧栏（仅「Ceobe」标题 + 新建按钮 + 实例卡片列表）占满 100vh；右列为「顶部栏（选中实例名 + 右侧「⚙ 设置」）→ 内容页签 → 底部状态栏」，状态栏仅覆盖主内容区。
- 创建向导：三步（类型×版本 → 内存/端口/JVM → 确认并下载，带进度条与阶段文案）。
- 控制台：日志区（等宽、自动滚底、暂停跟随开关）+ 输入框（↑ 历史）+ 状态徽标（五态色）。
- 两个配置页签：**服务器配置**（实例级：名称/内存/端口/JVM 参数/authlib-injector，写 `instance.json`）与 **游戏配置**（`server.properties` 全部键，按 schema 分组为类型化控件、原子写；保存时重读以只改本面板的键）。改端口会同步 `server.properties`；设 authlib 会自动关闭 `online-mode`。
- 顶部栏右侧「⚙ 设置」弹窗（当前仅暴露主题一项）。
- 开发自检：`-Dceobe.snapshot=<png> [-Dceobe.snapshot.tab=配置]` 启动即自动选中首个实例、切页签并把 Scene 导出为 PNG（无需窗口焦点，便于 UI 回归）。
- Mod 页：搜索框+结果卡（图标/简介/下载量/env 徽标）→ 安装；已装列表可卸载；vanilla/paper 实例显示"无 mods 目录"空态。
- 整合包页：搜索 Modrinth 整合包（可勾选「仅适配本实例」按当前版本/加载器过滤）→「安装到本实例」（校验加载器/版本一致）或「新建实例并安装」（依索引 dependencies 自动建 fabric/neoforge 实例再装）；带进度条与阶段文案。
- 穿透页：每通道开关卡（CF Web 控制台 / Pinggy 玩家连接 / 局域网直连）；地址大字 + 一键复制 + "地址已轮换"横幅；剩余有效期倒计时。
- 主题：**浅色默认**、深色可选；在右侧「⚙ 设置」对话框切换并持久化到 `~/.ceobe/settings.json`（`theme`）。token 驱动（`.theme-light` / `.theme-dark`），8px 圆角、1px hairline 边框、focus ring。字体中文优先「Microsoft YaHei UI」（避免细点位字体的像素感），拉丁回退 Inter（内嵌）。

## 输入 / 输出
- 输入：core API + 事件流。
- 输出：JavaFX Scene；所有耗时操作走后台线程（Virtual Threads），UI 线程仅渲染。

## 约束
- 仅消费 core 公共 API；禁止 GUI 内散落业务逻辑。
- AtlantaFX PrimerLight/PrimerDark 为基座 + `shadcn.css` 覆盖（明暗 token 全走 CSS 变量，挂 `.theme-light`/`.theme-dark` 切换）。
- JavaFX 25.0.4 win 分类器；`javafx-maven-plugin` 跑 `mvn javafx:run`。

## 边界条件
- 实例被 CLI 外部删除 → 列表事件刷新不崩溃。
- 下载中断 → 向导可重试且不产生脏实例（临时目录→完成后 rename）。

## 验收标准
- [ ] PRD 场景 1 全程 GUI 完成，无命令行参与。
- [ ] 事件流驱动的日志/状态在 1s 内反映进程实际变化。
- [ ] 穿透地址复制即用（原版客户端直连成功）。

## 完成定义
- 新用户零文档完成开服+分享地址。
