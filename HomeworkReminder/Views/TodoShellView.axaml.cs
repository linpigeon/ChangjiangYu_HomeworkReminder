using Avalonia.Controls;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>
/// 桌面外壳：三栏（导航 / 列表 / 详情）+ 内嵌设置页。
/// 栏宽不用绑定（GridSplitter 拖动会顶掉绑定），由这里显式应用与回写：
/// 视图附着与 ViewModel 宽度属性变化时把宽度写到列上，拖动结束时调
/// <see cref="MainViewModel.PersistPaneWidths"/> 记入设置。
/// </summary>
public partial class TodoShellView : UserControl
{
    private MainViewModel? _vm;

    private ColumnDefinition SidebarCol => ShellGrid.ColumnDefinitions[0];
    private ColumnDefinition ListCol => TodoPane.ColumnDefinitions[0];

    public TodoShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        AttachedToVisualTree += (_, _) => ApplyWidths();
    }

    private void Attach(MainViewModel? vm)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = vm;
        if (_vm is null) return;

        _vm.PropertyChanged += OnVmPropertyChanged;
        ApplyWidths();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SidebarColumnWidth) or nameof(MainViewModel.ListColumnWidth))
            ApplyWidths();
    }

    /// <summary>
    /// 把 ViewModel 的栏宽应用到列定义（初始与收起/展开侧边栏时）。
    /// <para>
    /// 列表栏写成 <c>*</c> 列 + <c>MaxWidth</c>，而不是直接把列宽设成记忆值：
    /// 记忆宽度（默认 400）在 960 这种最小桌面宽度下会把详情栏挤出窗口
    ///（400 + 5 + 详情 MinWidth 280 &gt; 可用 664，详情右边缘落到 1232.8 = 超出 272.8dp）。
    /// <c>*</c> + 上限让它在宽窗口上仍按记忆宽度分配、窄窗口上自动收窄。
    /// </para>
    /// <para>
    /// 侧边栏仍用像素宽度：它是固定的一列，宽度就是用户拖出来的那个值，
    /// 不存在「按比例分配」的语义。
    /// </para>
    /// </summary>
    private void ApplyWidths()
    {
        if (_vm is null) return;
        SidebarCol.Width = _vm.SidebarColumnWidth;

        var list = _vm.ListColumnWidth;
        if (list.GridUnitType == GridUnitType.Pixel) ListCol.MaxWidth = list.Value;
        else ListCol.Width = list;
    }

    /// <summary>拖动结束：把当前栏宽交给 ViewModel 记入设置（读取列的实际宽度）。</summary>
    private void OnSplitterDragCompleted(object? sender, Avalonia.Input.VectorEventArgs e)
    {
        _vm?.PersistPaneWidths(SidebarCol.Width, ListCol.Width);
    }

    /// <summary>登录覆盖层的返回键。</summary>
    private void OnLoginBackClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _vm?.CloseLoginPage();
    }
}
