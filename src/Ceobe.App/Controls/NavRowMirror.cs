using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Ceobe.App.Controls;

/// <summary>
/// 把「当前模式那一侧的行」增量镜像到导航集合。
///
/// 背景：WinUI 的 <c>NavigationView.MenuItemsSource</c> 从代码里赋值会崩
/// （0xC0000005 崩在 <c>set_MenuItemsSource</c> 内部，与时机无关），
/// 所以它只能由 XAML 绑定一次，之后所有「换一批行」的动作都改成
/// 改集合内容。
///
/// 为什么要增量而不是「清空 + 全量重填」：两个 VM 的 <c>Refresh()</c> 都是
/// 差量更新（Insert / Move / Remove），全量重填会让 NavigationView 整个重建
/// 导航项，丢掉滚动位置与高亮。增量镜像让两侧共用同一个集合时，
/// 行的增删改排序都能原样透传。
///
/// 另一条实测约束：**不要对这个集合调用 <c>Clear()</c>**。它发出
/// <c>Reset</c>，NavigationView 会尝试重建全部容器；在集合里有项（尤其是
/// 正在被 <c>SelectedItem</c> 指着）时切模式，实测直接 AV（0xC0000005）。
/// 一律逐项 <c>RemoveAt</c> —— 与 VM 差量刷新同一条路径，已验证安全。
/// </summary>
public sealed class NavRowMirror
{
    private readonly ObservableCollection<object> _target;
    private INotifyCollectionChanged? _source;

    /// <summary>当前镜像的来源（换模式时换掉）。</summary>
    public INotifyCollectionChanged? Source => _source;

    public NavRowMirror(ObservableCollection<object> target) => _target = target;

    /// <summary>
    /// 换来源。旧的来源退订，新来源的当前内容全量灌入目标集合。
    /// </summary>
    public void Attach(INotifyCollectionChanged source, IEnumerable<object> rows)
    {
        if (ReferenceEquals(_source, source))
        {
            Resync(rows);
            return;
        }

        if (_source is not null) _source.CollectionChanged -= OnSourceChanged;
        _source = source;
        _source.CollectionChanged += OnSourceChanged;

        ClearTarget();
        foreach (var row in rows) _target.Add(row);
    }

    /// <summary>与来源重新对齐（不换来源）。</summary>
    public void Resync(IEnumerable<object> rows)
    {
        ClearTarget();
        foreach (var row in rows) _target.Add(row);
    }

    /// <summary>
    /// 逐项移除而不是 <c>Clear()</c>：后者发出 <c>Reset</c>，NavigationView 会走
    /// 整表重建路径，在切换模式时命中原生层的悬空容器（0xC0000005）。
    /// </summary>
    private void ClearTarget()
    {
        for (var i = _target.Count - 1; i >= 0; i--) _target.RemoveAt(i);
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                var index = e.NewStartingIndex < 0 ? _target.Count : e.NewStartingIndex;
                foreach (var item in e.NewItems ?? (System.Collections.IList)Array.Empty<object>())
                {
                    if (item is null) continue;
                    _target.Insert(index, item);
                    index++;
                }
                break;

            case NotifyCollectionChangedAction.Remove:
                foreach (var item in e.OldItems ?? (System.Collections.IList)Array.Empty<object>())
                {
                    if (item is null) continue;
                    var at = _target.IndexOf(item);
                    if (at >= 0) _target.RemoveAt(at);
                }
                break;

            case NotifyCollectionChangedAction.Move:
                if (e.OldItems is null || e.OldItems.Count == 0) break;
                if (e.OldItems[0] is not { } oldItem) break;
                var from = _target.IndexOf(oldItem);
                var to = e.NewStartingIndex < 0 ? from : e.NewStartingIndex;
                if (from >= 0) _target.Move(from, to);
                break;

            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems is not null)
                {
                    foreach (var item in e.OldItems)
                    {
                        if (item is null) continue;
                        var at = _target.IndexOf(item);
                        if (at >= 0) _target.RemoveAt(at);
                    }
                }
                var replaceAt = e.NewStartingIndex < 0 ? _target.Count : e.NewStartingIndex;
                foreach (var item in e.NewItems ?? (System.Collections.IList)Array.Empty<object>())
                {
                    if (item is null) continue;
                    _target.Insert(replaceAt, item);
                    replaceAt++;
                }
                break;

            case NotifyCollectionChangedAction.Reset:
                // Reset 没有携带具体项，退回全量重填。
                if (sender is System.Collections.IEnumerable enumerable)
                {
                    ClearTarget();
                    foreach (var item in enumerable) _target.Add(item);
                }
                break;
        }
    }
}
