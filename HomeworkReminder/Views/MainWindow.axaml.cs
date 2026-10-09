using System;
using System.ComponentModel;
using Avalonia.Controls;
using HomeworkReminder.Services;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>
/// 应用主窗口。启动即进主界面（雨课堂登录已迁入设置页）。
/// <para>
/// 点「关闭」的行为由设置决定（设置页可改）：默认最小化到系统托盘继续运行
/// （<see cref="Hide"/> 后窗口从任务栏消失，只剩托盘图标），也可选直接退出程序。
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>主界面尺寸，与 MainWindow.axaml 中的声明保持一致。</summary>
    private const double MainWidth = 1180;
    private const double MainHeight = 760;
    private const double MainMinWidth = 960;
    private const double MainMinHeight = 580;

    /// <summary>托盘菜单「退出」等路径要求真正关闭时置位，跳过关闭行为拦截。</summary>
    private bool _allowRealClose;

    private MainViewModel? _vm;

    /// <summary>桌面小组件窗口。按需创建（用户从不开启就不必付出初始化成本）。</summary>
    private WidgetWindow? _widget;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnWindowClosing;
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        Attach(DataContext as MainViewModel);
    }

    /// <summary>真正退出程序（托盘「退出」、关闭行为「直接退出」用）：结束进程。</summary>
    public void QuitForReal()
    {
        _allowRealClose = true;
        PrepareForShutdown();
        // 只 Close() 本窗口不够：小组件窗口还开着时进程不会退出（托盘图标也还在），
        // 之后点托盘 Show() 一个已关闭的窗口会直接崩。Shutdown 会以 ApplicationShutdown
        // 原因关闭所有窗口（小组件的 Closing 拦截对 ApplicationShutdown 放行）。
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Close();
    }

    /// <summary>从托盘恢复窗口。</summary>
    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowRealClose) return;

        // 关闭行为由设置决定：最小化到托盘（默认）或直接退出。
        e.Cancel = true;
        if (AppSettings.Current.CloseBehaviorIndex == 1)
            QuitForReal();
        else
            Hide();
    }

    private void Attach(MainViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.Synced -= OnSynced;
            _vm.WidgetVisibilityRequested -= OnWidgetVisibilityRequested;
        }

        _vm = vm;
        if (_vm is null) return;

        _vm.Synced += OnSynced;
        _vm.WidgetVisibilityRequested += OnWidgetVisibilityRequested;

        SyncWidgetWithSetting();
    }

    /// <summary>同步完成后刷新小组件内容（数据复用主界面缓存，不额外请求）。</summary>
    private void OnSynced(object? sender, EventArgs e) => _widget?.RefreshFromMain();
    private void OnWidgetVisibilityRequested(object? sender, EventArgs e) => SyncWidgetWithSetting();

    /// <summary>
    /// 让小组件的显示状态与设置一致。按需创建，避免用户从不使用也付出初始化成本。
    /// </summary>
    private void SyncWidgetWithSetting()
    {
        if (_vm is null) return;

        if (!_vm.WidgetEnabled)
        {
            _widget?.HideWidget();
            return;
        }

        if (_widget is null)
        {
            _widget = new WidgetWindow(_vm);
            // 点小组件里的「打开主界面」→ 还原主窗口。
            _widget.OpenMainRequested += (_, _) => ShowFromTray();
        }

        _widget.RefreshFromMain();
        _widget.ShowWidget();
    }

    /// <summary>窗口真正退出前收尾（保存小组件位置，避免把「隐藏」误写回设置）。</summary>
    public void PrepareForShutdown() => _widget?.PersistPositionForShutdown();
}
