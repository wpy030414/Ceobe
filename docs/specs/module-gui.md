# Spec — WinUI 3 GUI（Ceobe.App）

> **实现现状（2026-10-22）**：`src/Ceobe.App`（XAML 视图 + 设计令牌 + 接线）+ `src/Ceobe.App.ViewModels`（全部状态与逻辑）。
> JavaFX 版已随迁移退役；2026-10-22 完成 Fluent 2 外壳重构（ADR-020）。

## 要构建什么

- 目标：Windows 原生桌面面板，覆盖实例列表、创建、控制台、两类配置、Mod、整合包、玩家、穿透、设置。
- 视觉基线：Windows 11 / Fluent 2。使用官方 `TitleBar`、`NavigationView`、`SelectorBar`、`SettingsCard`、`InfoBar` 等控件；不手绘仿制。

## 行为

- **主壳**：
  - 内置 `TitleBar`（`ExtendsContentIntoTitleBar`）：像素立方体图标 + `Ceobe`，caption buttons 跟随应用主题；
  - `NavigationView`（`PaneDisplayMode=Left`）：侧栏顶部是「服务端 / 客户端」模式 tab bar（客户端暂禁用），同一行放「只看运行中 / 刷新」；下方是实例列表，选中项带状态点与「类型 版本 · 端口 · 状态」；
  - `PaneFooter`：新建实例 / 设置（不参与 NavigationView 的菜单模板，避免被 `MenuItemTemplate` 误渲染）；
  - 右侧工作区：实例头 → `SelectorBar`（七分区）→ 页面；底部状态栏显示实例计数。
- **工作区实例头**：状态点 + 实例名 + 状态文字 + **单个启停按钮（文案承载 启动/停止/启动中/停止中）** + 更多（删除，二次确认）。
- **分区（固定顺序）**：
  1. **控制台**：日志卡（虚拟化 ListView、可选择文本、跟随/复制/清空）+ 命令输入；启停/删除已上移到实例头；
  2. **服务器配置**：`SettingsCard` 表单（名称/内存/端口/JVM/authlib），运行中只读；保存为唯一 Accent；
  3. **游戏配置**：三级视图「常用 / 分类 / 原始」+ 分组导航 + 搜索；字段按类型只实例化一个输入控件（Bool/Int/Enum/String 模板选择器）；**资源包与数据包面板**（4 个 `resource-pack*` 键 + `initial-enabled-packs` / `initial-disabled-packs` 多选）；保存为唯一 Accent；
  4. **Mod**：Modrinth 搜索/安装/卸载 + 已安装表 + 一键导入（拖入 `.jar`/`.mrpack`/`.zip`，识别→预览→确认）；卸载二次确认；
  5. **整合包**：搜索后「装到当前实例」或「按包新建实例」两条路径并排；
  6. **玩家**：白名单安全卡置顶，白名单/OP/封禁玩家/封禁 IP 四表分段编辑，支持批量导入与「从 OP 导入」；
  7. **穿透**：每条通道卡片显示地址、三态结论（已实测可达 / 无法验证 / 未探测）、诊断详情；`server-ip` 绑定冲突在开启前警告。
- **创建实例**：单页表单；服务端类型为 原版 / Paper / Purpur / Fabric / NeoForge；游戏版本是下拉，条目显示版本号 + 发布日期（Mojang `releaseTime`），默认最新正式版，加载失败可重试；带内联错误与创建进度；不再使用三步向导。
- **设置弹窗**：主题三态（浅色/深色/跟随系统，立即预览）、新建实例默认白名单、数据目录、关于（版本 + GitHub 链接，默认折叠）。
  - 数据目录用**文件管理器选择**（只读路径 + 浏览按钮），不再要求手填；点「保存」时若目录变了，内部自动预演并搬家（失败回滚），不再暴露预演/执行/放弃按钮。
  - 「关于」的版本与 GitHub 地址只在展开抽屉里显示。
- **主题**：`settings.json.theme` 支持 `light | dark | system`，**默认 system**；运行期切换走窗口根元素的 `RequestedTheme`。`ContentDialog` 位于 PopupRoot，不继承根主题，打开前显式设置 `RequestedTheme`。
- **Mica**：仅「跟随系统」且 `MicaController.IsSupported()` 时启用 `MicaBackdrop`；显式浅色/深色或 Win10 使用纯色 `PageBackground`，避免 Mica tint 与应用主题不一致。
- **分区刷新**：切回只读页时按 `IReloadable` 重读磁盘；带未保存编辑的页（服务器配置/游戏配置）不实现，避免冲掉输入。

## 设计令牌与规范

- **间距**：4 / 8 / 12 / 16 / 24；页面外边距 24，卡片内边距 16（`Styles/Spacing.xaml`）。
- **字阶**：只用 `Caption 12` / `Body 14` / `Body Strong 14` / `Subtitle 20` / `Title 28`，经 `Styles/Typography.xaml` 的 `Ceobe*Style` 使用；不手写 11/13/16/18。
- **圆角/表面**：控件 `ControlCornerRadius`(4)，卡片/对话框 `OverlayCornerRadius`(8)；卡片统一 `CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush`。
- **颜色**：零硬编码 ARGB；全部 `ThemeResource`。状态色用 `SystemFillColorSuccess/Caution/CriticalBrush`；次级文字用 `TextFillColorSecondaryBrush`。
- **等宽字体**：`Consolas`（WinUI 的 `FontFamily` 不支持 "Cascadia Mono,Consolas" 这种逗号回退，未装 Cascadia 时会静默退回 UI 字体）。
- **一个表面一个主操作**：每页只保留一个 `AccentButtonStyle`；破坏性操作进 `…` 菜单或二次确认。
- **可访问性**：所有图标按钮/图标+文字按钮显式 `AutomationProperties.Name`（StackPanel 内容不会聚合 UIA Name）；状态点旁必须有文字状态；区块标题 `HeadingLevel=Level2`；状态栏 `LiveSetting=Polite`。
- **键盘**：Ctrl+N 新建、F5 刷新、Ctrl+, 设置、Enter 发送命令。

## 输入 / 输出

- 输入：`Ceobe.Core` 公共 API + `InstanceManager.OnEvent` 事件流。
- 输出：XAML 视觉树；所有耗时操作 `async` 且不阻塞 UI 线程；Core 事件统一经 `UiThread.Post` marshal 回 UI 线程。

## 约束

- **ViewModel 不引用任何 WinUI 类型**：这是闸门 C 的前提。类型判断（Bool/Int/Enum/String）在 VM 层返回纯 `bool`，到 View 层才经 `DataTemplateSelector` 选择输入控件。
- View 只做绑定、手势转发与拖放落文件；业务逻辑一律在 ViewModel。
- 非打包自包含分发（`WindowsPackageType=None` + `SelfContained` + `WindowsAppSDKSelfContained`），免 MSIX 与证书。
- 新增 UI 依赖只允许在 `Ceobe.App`：当前为 `CommunityToolkit.WinUI.Controls.SettingsControls 8.2.251219`（`SettingsCard` / `SettingsExpander`）。

## 边界条件

- 实例被 CLI 外部删除 → 工作区收到事件后刷新侧栏并清空选中，不崩溃。
- 实例被改名 → 刷新侧栏并按新名重建工作区。
- 下载中断 → 创建失败且不产生脏实例（临时目录 → 完成后 rename）。
- 拖入多个文件 → 只识别第一个并明确提示，不静默丢弃其余。
- 非打包 WinUI 的 `FileOpenPicker` / `FolderPicker` 需要 HWND：导入一律走拖放 + 手填路径；设置页数据目录也用手填路径。
- 列表刷新（打开设置、外部创建/删除）必须保留当前选中实例与行对象；`InstanceListViewModel.Refresh()` 采用差量更新，不得 `Rows.Clear()`。

## 验收标准（闸门 C）

- [ ] ViewModel 层单测覆盖全部分支（当前 **224** 个用例全绿）。
- [ ] 全量测试（Core + ViewModel）**485** 个用例全绿。
- [ ] 构建 + 测试 + 发布全绿（CI `windows-latest`），发布产物包含 `Microsoft.ui.xaml.dll`、`Ceobe.pri`、`CommunityToolkit.WinUI.Controls.SettingsControls.dll` 与 `Assets/ceobe.ico`（本 SDK 版本页面 XAML 已合并进 `Ceobe.pri`，发布目录无独立 `.xbf`）。
- [ ] 本地交互式 UIA smoke（`tools/ui-smoke/Invoke-UiSmoke.ps1`）通过：七个分区齐全、日志区高度 > 100、可见按钮均有 UIA Name、设置/新建对话框可开关、浅色/深色中心亮度符合预期。
- [ ] 深色主题下正文可读（不得出现白字白底）；`ContentDialog` 与应用同主题。

> **明确的倒退**：JavaFX 版靠 `-Dceobe.snapshot=<png>` 启动即导出 Scene 快照做像素回归；
> WinUI 3 没有等价廉价手段。替代方案是把可观测行为下沉到 ViewModel 单测，
> 像素层只靠 UIA smoke + 中心亮度启发式，不追求截图基线。
> CI 的 GitHub-hosted runner 无交互桌面，UIA smoke 只能在本地/自托管 runner 运行（docs/MIGRATION.md 已记录）。

## 完成定义

- 新用户零文档完成开服 + 分享地址。
