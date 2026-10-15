# Spec — JavaFX GUI（ceobe-gui）

## 要构建什么
- 目标：shadcn/ui 观感的桌面面板：实例列表、创建向导、控制台、配置表单、Mod 市场、穿透面板。

## 行为
- 布局：左侧窄栏（实例卡片列表 + 新建按钮 + 全局设置）+ 右侧内容区（选中实例的 Tab：控制台/配置/Mod/穿透/文件）。
- 创建向导：三步（类型×版本 → 内存/端口/JVM → 确认并下载，带进度条与阶段文案）。
- 控制台：日志区（等宽、自动滚底、暂停跟随开关）+ 输入框（↑ 历史）+ 状态徽标（五态色）。
- 配置页：按 schema 分组渲染（bool→Switch、enum→ComboBox、int(range)→Spinner、string→TextField）；底部"未保存更改"提示条 + 保存（原子写）。
- Mod 页：搜索框+结果卡（图标/简介/下载量/env 徽标）→ 安装；已装列表可卸载；vanilla/paper 实例显示"无 mods 目录"空态。
- 穿透页：每通道开关卡（CF Web 控制台 / Pinggy 玩家连接 / 局域网直连）；地址大字 + 一键复制 + "地址已轮换"横幅；剩余有效期倒计时。
- 主题：zinc 暗色默认 + 亮色切换；8px 圆角、1px hairline 边框、focus ring、Inter 字体（资源内嵌，缺失回退 Segoe UI）。

## 输入 / 输出
- 输入：core API + 事件流。
- 输出：JavaFX Scene；所有耗时操作走后台线程（Virtual Threads），UI 线程仅渲染。

## 约束
- 仅消费 core 公共 API；禁止 GUI 内散落业务逻辑。
- AtlantaFX PrimerDark 为基座 + `shadcn.css` 覆盖（design tokens 全部走 CSS 变量便于调肤）。
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
