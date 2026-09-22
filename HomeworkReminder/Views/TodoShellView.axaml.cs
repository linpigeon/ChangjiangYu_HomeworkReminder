using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>
/// 主界面外壳：宽屏三栏（导航 / 列表 / 详情），窄屏（<700 DIP，手机竖屏）切换为
/// 「列表全屏 + 覆盖式抽屉 + 详情二级页」。
/// </summary>
public partial class TodoShellView : UserControl
{
    public TodoShellView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>按宽度切宽屏/窄屏布局（窄屏自动收起成抽屉）。</summary>
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (Vm is { } vm)
            vm.IsCompactLayout = Bounds.Width < 700;
    }

    private void OnToggleSidebarClick(object? sender, RoutedEventArgs e)
    {
        // 窄屏切抽屉、宽屏切收起，模式判断在 VM 里。
        Vm?.ToggleSidebar();
    }

    /// <summary>点抽屉遮罩：收起抽屉。</summary>
    private void OnDrawerScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm) vm.DrawerOpen = false;
    }

    /// <summary>窄屏详情二级页的返回键。</summary>
    private void OnDetailBackClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.SelectedItem = null;
    }

    private async void OnMarkAllReadClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (TopLevel.GetTopLevel(this) is Window w &&
            !await MessageBox.ConfirmAsync(w, "全部标记为已读",
                "雨课堂只提供「全部已读」接口，没有单条已读。这会把通知中心里所有未读一次清空。继续吗？"))
            return;

        var ok = await vm.MarkAllReadAsync();
        if (!ok && TopLevel.GetTopLevel(this) is Window w2)
        {
            await MessageBox.ShowAsync(w2, "标记已读失败",
                "雨课堂没有接受这次「全部已读」请求。可能是登录态已失效，或该接口有变动。");
        }
    }

    private void OnCheckboxClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        // 勾选框所在行的 DataContext 就是所属事项——列表行与详情页都是如此。
        if (sender is Control { DataContext: TodoItemViewModel item })
        {
            _ = vm.ToggleDoneAsync(item);
        }
    }

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not Border { DataContext: TodoItemViewModel item }) return;

        // 点在勾选框上时不切换选中项，否则勾选会顺带把右侧详情换掉。
        if (e.Source is Visual v && v.FindAncestorOfType<CheckBox>() is not null) return;

        vm.SelectedItem = item;
    }

    private void OnOpenSourceClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        var item = (sender as Control)?.DataContext as TodoItemViewModel ?? vm.SelectedItem;
        if (item is not null) vm.OpenSource(item);
    }
}
