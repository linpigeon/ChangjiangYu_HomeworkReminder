using System;
using System.ComponentModel;
using Avalonia.Controls;
using HomeworkReminder.Services;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>学习通登录页的交互：导航到超星统一登录页，登录成功自动捕获会话。</summary>
public partial class ChaoxingLoginView : UserControl
{
    private bool _webViewReady;
    private bool _navigated;

    public ChaoxingLoginView()
    {
        InitializeComponent();

        ChaoxingWebView.AdapterCreated += (_, _) =>
        {
            _webViewReady = true;
            WebViewPlaceholder.IsVisible = false;
            ChaoxingWebView.IsVisible = true;
            NavigateIfNeeded();
        };
        ChaoxingWebView.AdapterDestroyed += (_, _) => _webViewReady = false;
        ChaoxingWebView.NavigationCompleted += async (_, _) => await TryCaptureAsync();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // 让 ViewModel 在退出学习通时能清掉这个 WebView 内部的 Cookie。
        // 注意不要复用雨课堂的 MainViewModel.WebViewAccessor——那是另一个 WebView。
        MainViewModel.ChaoxingWebViewAccessor = () => ChaoxingWebView;

        if (Vm is { } vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
        }

        NavigateIfNeeded();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Vm is { } vm) vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.LoginPageOpen)) return;
        if (Vm is not { ChaoxingLoginOpen: true }) return;

        // 覆盖层重新打开时要重新导航。
        // _navigated 原先是一次性标志，而本视图与主窗口同生命周期，导航只发生在
        // AdapterCreated / 首次 OnAttachedToVisualTree —— 与用户是否打开覆盖层无关。
        // 于是关掉再打开（退出登录后尤其明显）看到的是上一次残留的页面，
        // 且不会再触发会话捕获。这里按「打开一次、导航一次」纠正。
        _navigated = false;
        WebViewPlaceholder.IsVisible = !_webViewReady;
        ChaoxingWebView.IsVisible = _webViewReady;
        NavigateIfNeeded();

        // 让托管布局重跑一遍，把原生控件按最终边界重新定位（与 LoginView.ShowWebView 同理）。
        ChaoxingWebView.InvalidateMeasure();
        ChaoxingWebView.InvalidateArrange();
    }

    private void NavigateIfNeeded()
    {
        if (!_webViewReady || _navigated) return;
        _navigated = true;
        try { ChaoxingWebView.Navigate(new Uri(ChaoxingLoginService.LoginUrl)); }
        catch (Exception) { /* 导航失败停留在占位 */ }
    }

    /// <summary>每次导航完成后尝试捕获：登录成功页面跳转后 cookie 里会出现 _uid。</summary>
    private async System.Threading.Tasks.Task TryCaptureAsync()
    {
        if (Vm is not { } vm) return;
        try
        {
            var session = await ChaoxingLoginService.CaptureAsync(ChaoxingWebView);
            if (session is not null)
                await vm.AdoptChaoxingSessionAsync(session);
        }
        catch (Exception)
        {
            // 捕获失败留在登录页，用户可继续操作。
        }
    }
}
