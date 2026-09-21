using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HomeworkReminder.ViewModels;
using HomeworkReminder.Views;

namespace HomeworkReminder;

public partial class App : Application
{
    /// <summary>
    /// 应用级 ViewModel。自检/离屏渲染模式需要先把它初始化完再建窗口，
    /// 所以由 App 持有，供 Program 取用。
    /// </summary>
    public MainViewModel? ViewModel { get; private set; }

    /// <summary>启动时是否立刻拉取数据。自检模式下由外部显式控制。</summary>
    public static bool DeferInitialLoad { get; set; }

    /// <summary>WebView 链路测试的时间预算；非 null 时进入测试模式。</summary>
    public static TimeSpan? WebViewTestBudget { get; set; }

    /// <summary>为 true 时不读取会话，强制停在登录页（供自检渲染登录界面）。</summary>
    public static bool ForceLoginPage { get; set; }

    /// <summary>
    /// WebView 链路测试的窗口构建钩子。
    /// 用委托是因为共享项目不能反向引用桌面项目（Desktop 引用了共享项目）。
    /// </summary>
    public static Action<IClassicDesktopStyleApplicationLifetime, TimeSpan>? WebViewTestHook { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // ---------------- 系统托盘 ----------------

    /// <summary>
    /// 桌面平台注入的托盘宿主工厂（Windows 上是 <c>TrayIconHost</c>：左键还原主窗口、
    /// 右键弹自绘菜单）。为 null 则没有托盘——只影响测试/特殊模式。
    /// 用委托是因为共享项目不能反向引用桌面项目。
    /// </summary>
    public static Func<IDisposable>? TrayIconHook { get; set; }

    private IDisposable? _trayIcon;
    private TrayMenuWindow? _trayMenu;

    private static MainWindow? MainWindow =>
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
        ?.MainWindow as MainWindow;

    /// <summary>托盘左键 / 菜单「打开主界面」：还原主窗口。</summary>
    public void ShowMainWindow() => MainWindow?.ShowFromTray();

    /// <summary>在指定屏幕位置弹出自绘托盘菜单。重复弹出时先关掉旧的。</summary>
    public void ShowTrayMenuAt(PixelPoint cursor)
    {
        _trayMenu?.Close();
        if (ViewModel is not { } vm) return;

        var settings = Services.AppSettings.Current;
        var items = new TrayMenuItem[]
        {
            new("打开主界面", ShowMainWindow),
            TrayMenuItem.CreateSeparator(),
            new("显示桌面小组件", () => vm.SetWidgetEnabled(!settings.WidgetVisible),
                isChecked: settings.WidgetVisible),
            new("立即同步一次", vm.RequestManualSync),
            TrayMenuItem.CreateSeparator(),
            new("退出", QuitFromTray),
        };

        var menu = new TrayMenuWindow(items, cursor);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trayMenu, menu)) _trayMenu = null;
        };
        _trayMenu = menu;
        menu.Show();
        menu.Activate();
    }

    /// <summary>托盘「退出」：停自动同步，真正结束进程。</summary>
    private void QuitFromTray()
    {
        // 停掉自动同步并等它在途循环收尾，避免强杀后台线程留下半截写入。
        ViewModel?.AutoSync.Dispose();
        MainWindow?.QuitForReal();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 注意：模板原本会在这里移除 Avalonia 的 DataAnnotations 校验插件，以避免与
        // CommunityToolkit.Mvvm 重复校验。Avalonia 12 已把 BindingPlugins 改为
        // internal，且本应用的 ViewModel 派生自 ObservableObject 而非
        // ObservableValidator，没有需要去重的东西。
        ViewModel = new MainViewModel();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (WebViewTestBudget is { } budget)
            {
                // WebView 链路测试模式：窗口由桌面层通过钩子构建并接管。
                WebViewTestHook?.Invoke(desktop, budget);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            desktop.MainWindow = new MainWindow { DataContext = ViewModel };

            // 托盘图标由桌面层注入（Windows：自绘菜单）；自检等模式不创建。
            if (!DeferInitialLoad)
                _trayIcon = TrayIconHook?.Invoke();

            // 进程退出（关窗/系统注销等不走托盘「退出」的路径）也要停掉自动同步。
            desktop.ShutdownRequested += (_, _) =>
            {
                ViewModel?.AutoSync.Dispose();
                _trayIcon?.Dispose();
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView { DataContext = ViewModel };
        }

        base.OnFrameworkInitializationCompleted();

        if (DeferInitialLoad) return;

        // 读取会话与首次同步都涉及 IO/网络，放到 UI 线程启动之后执行，
        // 让窗口先渲染出来，而不是卡在启动阶段。
        Dispatcher.UIThread.Post(async void () =>
        {
            try
            {
                await ViewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                // 必须切到 Error：只写 ErrorMessage 时 State 仍停在 NeedsLogin，
                // 登录页照常在显示但没有任何「出错了」的语义；Error 同样显示登录页
                // （ShowLogin 含 Error），且错误横幅由 ErrorMessage 驱动。
                ViewModel.State = AppState.Error;
                ViewModel.ErrorMessage = $"初始化失败：{ex.Message}";
            }
        });
    }
}
