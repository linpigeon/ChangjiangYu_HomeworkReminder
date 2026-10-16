using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>
/// Android 外壳（抽屉式移动布局）的交互。
/// <para>
/// 位于共享项目（而非 Android 头）：界面遮挡审计 <c>--androidcheck</c> 在桌面头
/// 离屏渲染手机尺寸，必须编译**同一个**外壳才能验到真实的层叠与返回栏。
/// 唯一平台专属的那句（铺答题页 cookie）已抽成
/// <see cref="Services.IPlatformServices.PrepareExamWebSession"/>。
/// </para>
/// </summary>
public partial class MobileShellView : UserControl
{
    public MobileShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookVm(DataContext as MainViewModel);
        ExamWebView.AdapterCreated += (_, _) =>
        {
            _examWebReady = true;
            NavigateExamWeb();
        };
        ExamWebView.AdapterDestroyed += (_, _) => _examWebReady = false;
        ExamWebView.NavigationCompleted += async (_, _) =>
        {
            // 页面是桌面布局（左侧菜单 + 宽卡片），手机屏放不下：把布局视口钉到 1000px，
            // WebView 自动整体缩放适配屏幕；顺手藏掉「浏览器不受支持」提示条。
            try
            {
                await ExamWebView.InvokeScript(
                    "(()=>{var m=document.querySelector('meta[name=\"viewport\"]');" +
                    "if(!m){m=document.createElement('meta');m.name='viewport';(document.head||document.documentElement).appendChild(m);}" +
                    "m.setAttribute('content','width=1000');" +
                    "var h=document.getElementById('ie-hint');if(h)h.style.display='none';" +
                    "return 'ok';})()");
            }
            catch (Exception) { /* 视觉退化不影响使用 */ }
        };
    }

    private bool _examWebReady;

    /// <summary>当前挂上的 PropertyChanged 处理器；退订必须用同一个委托实例。</summary>
    private System.ComponentModel.PropertyChangedEventHandler? _vmHandler;

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>
    /// 订阅主界面的属性变化。
    /// <para>
    /// 必须能在视图销毁时退订，且不能重复订阅：<c>MainViewModel</c> 是应用级单例，
    /// 而 <c>IActivityApplicationLifetime.MainViewFactory</c> 每次 Activity 重建/旋转
    /// 都会 new 一个新的 MobileShellView。原先用匿名 lambda 订阅且从不退订，
    /// 于是每个已销毁的外壳（连同整棵可视树与 NativeWebView）都被永久持有，
    /// 处理器还会累积——一次 ExamWebUrl 变化会在所有僵尸 WebView 上跑 NavigateExamWeb。
    /// </para>
    /// </summary>
    private void HookVm(MainViewModel? vm)
    {
        UnhookVm();
        if (vm is null) return;

        _vmHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ExamWebUrl))
                NavigateExamWeb();
        };
        vm.PropertyChanged += _vmHandler;
    }

    private void UnhookVm()
    {
        if (_vmHandler is null) return;
        if (DataContext is MainViewModel vm) vm.PropertyChanged -= _vmHandler;
        _vmHandler = null;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        UnhookVm();
        UnhookBackRequested();
    }

    private Avalonia.Controls.TopLevel? _hookedTopLevel;

    /// <summary>
    /// 接管系统返回键（<see cref="Avalonia.Controls.TopLevel.BackRequested"/>）：
    /// 由内到外关掉一层全屏覆盖，都关完了才让系统处理（退出应用）。
    /// <para>
    /// <b>为什么必须接管</b>：课表页是整页覆盖，会把抽屉（进设置的唯一入口）一起盖住。
    /// 不接管的话，用户在课表页按返回会直接退出应用——既回不到列表，也进不了设置。
    /// 桌面端有表头上的返回按钮，手机端返回键才是主要返回方式。
    /// </para>
    /// <para>
    /// 必须在 <c>AttachedToVisualTree</c> 里挂：构造函数执行时还没挂上可视树，
    /// <c>TopLevel.GetTopLevel(this)</c> 拿不到宿主窗口。
    /// </para>
    /// </summary>
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UnhookBackRequested();
        if (Avalonia.Controls.TopLevel.GetTopLevel(this) is { } top)
        {
            _hookedTopLevel = top;
            top.BackRequested += OnBackRequested;
        }
    }

    private void UnhookBackRequested()
    {
        if (_hookedTopLevel is null) return;
        _hookedTopLevel.BackRequested -= OnBackRequested;
        _hookedTopLevel = null;
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        // 顺序与叠放层次一致（后加的在上）：登录页 → 答题页 → 设置 → 详情 → 课表 → 抽屉。
        if (vm.LoginPageOpen is not null) { vm.CloseLoginPage(); e.Handled = true; return; }
        if (vm.ExamWebUrl is not null) { vm.CloseExamWeb(); e.Handled = true; return; }
        if (vm.SettingsOpen) { vm.SettingsOpen = false; e.Handled = true; return; }
        if (vm.SelectedItem is not null) { vm.SelectedItem = null; e.Handled = true; return; }
        if (vm.TimetableOpen) { vm.CloseTimetable(); e.Handled = true; return; }
        if (vm.DrawerOpen) { vm.DrawerOpen = false; e.Handled = true; }
    }

    /// <summary>打开应用内答题页：先把会话 cookie 铺到 exam 子域，再导航。</summary>
    private void NavigateExamWeb()
    {
        // WebView 适配器异步创建，未就绪时等 AdapterCreated 再导航。
        if (!_examWebReady || Vm?.ExamWebUrl is not { } url) return;

        // 平台专属动作（Android：changjiang → changjiang-exam 子域铺 cookie）。
        // 放平台钩子里而不是 if (OperatingSystem.IsAndroid())：共享项目里不该出现
        // Android 的 CookieManager，否则本文件又只能编译进 Android 头。
        MainViewModel.PlatformServices?.PrepareExamWebSession();

        ExamWebView.Navigate(new Uri(url));
    }

    /// <summary>点抽屉遮罩：收起抽屉。</summary>
    private void OnDrawerScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm) vm.DrawerOpen = false;
    }

    /// <summary>详情二级页的返回键。</summary>
    private void OnDetailBackClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.SelectedItem = null;
    }

    /// <summary>设置页的返回键。</summary>
    private void OnSettingsBackClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.SettingsOpen = false;
    }

    /// <summary>应用内答题页的返回键。</summary>
    private void OnExamWebBackClick(object? sender, RoutedEventArgs e)
    {
        Vm?.CloseExamWeb();
    }

    /// <summary>登录覆盖层的返回键。</summary>
    private void OnLoginBackClick(object? sender, RoutedEventArgs e)
    {
        Vm?.CloseLoginPage();
    }
}
