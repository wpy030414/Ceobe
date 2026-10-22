using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ceobe.App.ViewModels;
using Ceobe.Core.Client;

namespace Ceobe.App.Views;

/// <summary>日志页 code-behind：只负责自动滚动到最新一行（否则日志列表停在顶部）。</summary>
public sealed partial class ClientLogPage : UserControl
{
    private ClientLogViewModel? _vm;

    public ClientLogPage() => InitializeComponent();

    public ClientLogViewModel Vm => _vm!;

    public bool HasException => _vm?.ExceptionLine is { Length: > 0 };

    public bool HasActions => _vm is { Actions.Count: > 0 };

    public void Load(ClientLogViewModel vm)
    {
        _vm = vm;
        Bindings.Update();
        vm.Reload();
        vm.Lines.CollectionChanged += OnLinesChanged;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ClientLogViewModel.ExceptionLine)
                or nameof(ClientLogViewModel.CrashHeadline)
                or nameof(ClientLogViewModel.HasCrash))
            {
                Bindings.Update();
            }
        };
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm is not { Follow: true }) return;
        if (LogList.Items.Count == 0) return;
        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _vm?.Clear();
        Bindings.Update();
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm is null) return;
            if (!File.Exists(_vm.LogPath)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_vm.LogPath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
        }
    }
}
