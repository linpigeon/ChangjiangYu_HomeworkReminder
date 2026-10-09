using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Android.Views;

/// <summary>Android 外壳（抽屉式移动布局）的交互。</summary>
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

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void HookVm(MainViewModel? vm)
    {
        if (vm is null) return;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ExamWebUrl))
                NavigateExamWeb();
        };
    }

    /// <summary>打开应用内答题页：先把会话 cookie 铺到 exam 子域，再导航。</summary>
    private void NavigateExamWeb()
    {
        // WebView 适配器异步创建，未就绪时等 AdapterCreated 再导航。
        if (!_examWebReady || Vm?.ExamWebUrl is not { } url) return;

        // sessionid 是 changjiang.yuketang.cn 的 cookie；移动答题页在 changjiang-exam
        // 子域，域名不同的 host-only cookie 不会自动带过去——显式铺一份。
        var cm = global::Android.Webkit.CookieManager.Instance;
        var raw = cm?.GetCookie("https://changjiang.yuketang.cn");
        if (cm is not null && !string.IsNullOrEmpty(raw))
        {
            cm.SetCookie("https://changjiang-exam.yuketang.cn", raw);
            cm.Flush();
        }

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
