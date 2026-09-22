using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>待办列表（桌面中栏与移动端全屏列表共用）的交互。</summary>
public partial class TodoListView : UserControl
{
    public TodoListView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// true = 移动端宿主：头部按钮切覆盖式抽屉；false（默认）= 宽屏：切侧边栏收起。
    /// 由宿主的 axaml 设置（移动端 MobileShellView 置 true），与布局切换逻辑解耦。
    /// </summary>
    public static readonly StyledProperty<bool> DrawerModeProperty =
        AvaloniaProperty.Register<TodoListView, bool>(nameof(DrawerMode));

    public bool DrawerMode
    {
        get => GetValue(DrawerModeProperty);
        set => SetValue(DrawerModeProperty, value);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnToggleSidebarClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (DrawerMode) vm.DrawerOpen = !vm.DrawerOpen;
        else vm.ToggleSidebar();
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

        // 点在勾选框上时不切换选中项，否则勾选会顺带把详情换掉。
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
