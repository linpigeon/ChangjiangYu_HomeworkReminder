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

    /// <summary>把 ViewModel 的栏宽应用到列定义（初始与收起/展开侧边栏时）。</summary>
    private void ApplyWidths()
    {
        if (_vm is null) return;
        SidebarCol.Width = _vm.SidebarColumnWidth;
        ListCol.Width = _vm.ListColumnWidth;
    }

    /// <summary>拖动结束：把当前栏宽交给 ViewModel 记入设置（读取列的实际宽度）。</summary>
    private void OnSplitterDragCompleted(object? sender, Avalonia.Input.VectorEventArgs e)
    {
        _vm?.PersistPaneWidths(SidebarCol.Width, ListCol.Width);
    }
}
