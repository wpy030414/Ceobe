using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;

namespace Ceobe.App.Views;

/// <summary>
/// 实例列表 UserControl code-behind。
/// <see cref="VmList"/> 必须在 <c>InitializeComponent</c> 之前赋值——x:Bind 在构造期绑定
/// <c>Rows</c> 集合引用（OneWay 到 ObservableCollection，之后靠集合自身的增删通知刷新）。
/// </summary>
public sealed partial class InstanceListView : UserControl
{
    public InstanceListViewModel VmList { get; }

    public InstanceListView(InstanceListViewModel vm)
    {
        VmList = vm;
        InitializeComponent();
    }

    /// <summary>当前选中的实例名（主窗口据此装配右侧页签）。</summary>
    public string? SelectedName => VmList.SelectedName;
}
