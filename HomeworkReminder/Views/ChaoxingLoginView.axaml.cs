using System;
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
        NavigateIfNeeded();
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
