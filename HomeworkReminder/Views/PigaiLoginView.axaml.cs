using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using HomeworkReminder.Services;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>
/// 批改网登录页的交互。
/// <para>
/// 严格照搬雨课堂那条已跑通的路径（<see cref="LoginView"/> + <see cref="YktLoginService.WaitForLoginAsync"/>）：
/// 打开覆盖层后启动一个 <b>async 轮询循环</b>，循环里
/// 「<c>Task.Delay</c> → <c>ConfigureAwait(true)</c> 回到 UI 线程 → 探测」，
/// 探测确认已登录后再读 Cookie 建会话。
/// </para>
/// <para>
/// <b>刻意不用 DispatcherTimer。</b>最初我用的是「每秒 Tick 里读 cookie」，
/// 与雨课堂的形状不一致；这里改成同款的 async 循环：
/// 续体始终回到 UI 线程（<see cref="NativeWebView"/> 是 COM/STA 控件，
/// 切到线程池会让 <c>InvokeScript</c> 永不返回），并且先**验证登录真的生效**
/// （探测一个必须登录才能访问的页面），再读 Cookie——而不是只凭 Cookie 在不在。
/// </para>
/// </summary>
public partial class PigaiLoginView : UserControl
{
    private bool _webViewReady;
    private bool _navigated;
    private bool _running;

    /// <summary>取消当前等待循环（覆盖层关闭 / 视图销毁时）。</summary>
    private CancellationTokenSource? _loginCts;

    public PigaiLoginView()
    {
        InitializeComponent();

        PigaiWebView.AdapterCreated += (_, _) =>
        {
            _webViewReady = true;
            WebViewPlaceholder.IsVisible = false;
            PigaiWebView.IsVisible = true;
            PigaiLoginService.Log("AdapterCreated");
            NavigateIfNeeded();
        };
        PigaiWebView.AdapterDestroyed += (_, _) =>
        {
            _webViewReady = false;
            PigaiLoginService.Log("AdapterDestroyed");
        };
        PigaiWebView.NavigationCompleted += (_, e) =>
            PigaiLoginService.Log($"NavigationCompleted success={e.IsSuccess} src={PigaiWebView.Source}");
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // 让 ViewModel 在退出批改网时能清掉这个 WebView 内部的 Cookie。
        // 注意不要复用雨课堂/学习通的 accessor——三个登录页各有自己的 WebView。
        MainViewModel.PigaiWebViewAccessor = () => PigaiWebView;

        if (Vm is { } vm) vm.PropertyChanged += OnVmPropertyChanged;

        NavigateIfNeeded();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Vm is { } vm) vm.PropertyChanged -= OnVmPropertyChanged;
        StopWaiting();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.LoginPageOpen)) return;

        if (Vm is not { PigaiLoginOpen: true })
        {
            // 覆盖层已关闭（含登录成功后的自动关闭）：停掉等待循环。
            StopWaiting();
            return;
        }

        // 覆盖层重新打开时重新导航（_navigated 是一次性标志，与主窗口同生命周期）。
        _navigated = false;
        WebViewPlaceholder.IsVisible = !_webViewReady;
        PigaiWebView.IsVisible = _webViewReady;
        NavigateIfNeeded();

        // 让托管布局重跑一遍，把原生控件按最终边界重新定位
        // （与 LoginView.ShowWebView 同理：原生面在隐藏期间不会随控件边界同步）。
        PigaiWebView.InvalidateMeasure();
        PigaiWebView.InvalidateArrange();

        StartWaiting();
    }

    private void NavigateIfNeeded()
    {
        if (!_webViewReady || _navigated) return;
        _navigated = true;
        try { PigaiWebView.Navigate(new Uri(PigaiLoginService.LoginUrl)); }
        catch (Exception) { /* 导航失败停留在占位 */ }
    }

    private void StartWaiting()
    {
        if (_running) return;
        _running = true;
        _loginCts = new CancellationTokenSource();
        var ct = _loginCts.Token;
        PigaiLoginService.Log("wait: 开始等待登录（async 轮询）");

        // 不在构造/属性回调里 await：这里 fire-and-forget，内部自己兜异常
        // （与 LoginView 里 `_ = vm.CompleteLoginAsync(...)` 的处理一致）。
        _ = WaitForLoginAsync(ct);
    }

    private void StopWaiting()
    {
        _running = false;
        var cts = _loginCts;
        _loginCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* 已经停过 */ }
        cts.Dispose();
        PigaiLoginService.Log("wait: 停止等待");
    }

    private async Task WaitForLoginAsync(CancellationToken ct)
    {
        var vm = Vm;
        if (vm is null) return;

        try
        {
            // 与雨课堂同款：循环内部 Task.Delay + ConfigureAwait(true)，
            // 保证每次探测都回到 UI 线程；探测自身在服务里带 3s 超时。
            var session = await PigaiLoginService
                .WaitForLoginAsync(PigaiWebView, ct: ct)
                .ConfigureAwait(true);

            if (session is null)
            {
                // 超时或被取消；界面留在登录页由用户继续操作。
                return;
            }

            // 防重入：等待期间用户可能已手动关掉覆盖层。
            if (!vm.PigaiLoginOpen) return;
            await vm.AdoptPigaiSessionAsync(session).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 正常停止（覆盖层关闭）。
        }
        catch (Exception ex)
        {
            // 不能从 async void / fire-and-forget 路径逃逸异常：会终止进程。
            PigaiLoginService.Log($"wait: 异常 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _running = false;
        }
    }
}
