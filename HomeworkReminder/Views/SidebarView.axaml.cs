using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>侧边栏（宽屏栏位与窄屏抽屉共用）的交互。</summary>
public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnNavTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not Border { DataContext: NavItem nav }) return;

        vm.SelectedNav = nav;
        vm.OnNavChanged();
        // 抽屉模式下选完导航就收起（覆盖式抽屉的惯例）；宽屏下 DrawerOpen 恒为 false。
        vm.DrawerOpen = false;
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        try
        {
            await vm.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            ReportHandlerFailure(vm, "同步", ex);
        }
    }

    private async void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        try
        {
            if (TopLevel.GetTopLevel(this) is Window w &&
                !await MessageBox.ConfirmAsync(w, "退出登录", "将清除本机保存的雨课堂登录态，需要重新扫码。确定吗？"))
                return;

            await vm.LogoutCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            ReportHandlerFailure(vm, "退出登录", ex);
        }
    }

    /// <summary>
    /// 处理器是 async void：异常一旦逃逸到 UI 线程就会终止进程
    /// （本应用没有全局 UI 异常处理器，只有 AppDomain 级的日志），
    /// 所以每个 async void 处理器都必须自己兜住并给出可见反馈。
    /// </summary>
    internal static void ReportHandlerFailure(MainViewModel vm, string action, Exception ex)
        => vm.SyncStatus = $"{action}失败：{ex.Message}";

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        Vm?.ToggleSettings();
    }
}
