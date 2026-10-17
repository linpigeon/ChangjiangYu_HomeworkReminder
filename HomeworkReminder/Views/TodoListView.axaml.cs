using System;
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

        // async void：整体兜住异常，否则会直接终止进程。
        try
        {
            if (TopLevel.GetTopLevel(this) is Window w &&
                !await MessageBox.ConfirmAsync(w, "全部标记为已读",
                    "雨课堂只提供「全部已读」接口，没有单条已读。这会把通知中心里所有未读一次清空。继续吗？"))
                return;

            var result = await vm.MarkAllReadAsync();
            if (TopLevel.GetTopLevel(this) is not Window w2) return;

            if (!result.LocalApplied)
            {
                await MessageBox.ShowAsync(w2, "标记已读失败",
                    $"本应用的已读状态都没能更新：{result.RemoteFailureReason ?? "未知原因"}");
                return;
            }

            if (result.RemoteSynced) return;   // 两边都成功：不打扰用户

            if (result.AuthExpired)
            {
                // 登录态失效是「要重新登录」，不是「接口坏了」——必须说清楚，
                // 否则用户只会反复点、反复失败。
                await MessageBox.ShowAsync(w2, "已标记，但雨课堂未同步",
                    $"本应用内已标记为已读。但雨课堂登录态已失效（{result.RemoteFailureReason}），"
                    + "那边的通知中心没有清掉。到「设置 → 账号」重新登录后可再试一次。");
                return;
            }

            // 本地已生效、只有远程没接受：用状态栏告知即可，不弹窗打断。
            // 这条信息对排查很重要——把接口的原话留下，别只写「可能接口有变动」。
            vm.SyncStatus = $"已在本地标记为已读；雨课堂未接受同步：{result.RemoteFailureReason}";
        }
        catch (Exception ex)
        {
            SidebarView.ReportHandlerFailure(vm, "标记已读", ex);
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

    /// <summary>点课程行：选中它，详情栏显示未完成任务点明细。</summary>
    private void OnCourseTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not Border { DataContext: ChaoxingCourseViewModel course }) return;
        vm.List.SelectedCourse = course;
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

    /// <summary>「去登录」：打开设置页的账号选项卡。</summary>
    private void OnGoLoginClick(object? sender, RoutedEventArgs e)
    {
        Vm?.OpenAccountSettings();
    }
}
