using Ceobe.Core.Model;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 侧栏导航行的最小契约：服务端实例行与客户端实例行都实现它，
/// 这样 NavigationView 只需要**一个**内联 DataTemplate。
///
/// 为什么必须收敛到同一契约：WinUI 3.2.3 里 <c>NavigationView.MenuItemsSource</c>
/// 配 <c>DataTemplateSelector</c> 时，集合项被移除会走容器回收路径并命中原生层，
/// 直接 AV（0xC0000005，故障模块 coreclr，无托管栈）。内联 <c>MenuItemTemplate</c>
/// 是实测稳定的唯一形态，而内联模板只能有一个，行类型就只能统一。
/// 2026-10-22 实测记录，见 docs/MIGRATION.md 的 UI 回归条目。
///
/// 本接口不引用任何 WinUI 类型（闸门 C）。
/// </summary>
public interface INavRow
{
    /// <summary>第一行：显示名。</summary>
    string DisplayName { get; }

    /// <summary>第二行：类型/版本/端口 · 状态（客户端为 版本 · 加载器 · 状态）。</summary>
    string DetailLine { get; }

    /// <summary>右上角状态点。客户端状态已映射到服务端的四态，故控件可以共用。</summary>
    InstanceState DotState { get; }
}
