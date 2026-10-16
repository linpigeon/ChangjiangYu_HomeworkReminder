using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using HomeworkReminder.Services;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>
/// 教务课表导入：在**独立弹窗**里打开教务系统，注入悬浮球，抓取当前页并合并导入。
/// <para>
/// <b>为什么用 <see cref="NativeWebDialog"/> 而不是内嵌 <see cref="NativeWebView"/></b>：
/// 实测内嵌方案有两个问题——弹窗与会话内容挤在同一块区域（与待办列表重叠），
/// 且原生面被外层容器裁切。独立弹窗与「长江雨课堂登录」是同款形态：浮在应用之上、不占应用版面。
/// </para>
/// <para>
/// <b>二级页在同一窗口内跳转</b>：教务系统大量用 <c>target="_blank"</c> / <c>window.open</c>
/// 打开功能页（如「个人课表查询」）。默认行为会另开窗口或什么都不发生，
/// 所以拦截 <c>NewWindowRequested</c>（置 <c>Handled = true</c>）并改为在本弹窗导航。
/// </para>
/// </summary>
public partial class JwImportView : UserControl
{
    /// <summary>教务系统常用的窗口尺寸（课表页较宽，默认窗口会挤）。</summary>
    private const int WindowWidth = 1180;

    private const int WindowHeight = 820;

    private NativeWebDialog? _dialog;
    private bool _running;
    private CancellationTokenSource? _ballCts;

    /// <summary>
    /// 页面就绪闸门。**这是修掉「Unable to invoke script before any page was loaded」的关键**：
    /// 弹窗刚 <c>Show()</c> 时还没有任何页面加载完成，此刻 <c>InvokeScript</c> 必定失败
    /// （实测记录见 <see cref="PageReadyGate"/>）。注入循环读它，在此之前一个脚本都不发。
    /// </summary>
    private readonly PageReadyGate _pageGate = new();

    /// <summary>
    /// 是否已经为本弹窗发起过首次导航。
    /// <para>
    /// 首次导航必须等 <c>AdapterCreated</c> 之后再发：<c>Show()</c> 返回时适配器往往还没建好，
    /// 此时 <c>Navigate</c> 可能不被受理，页面就会一直停在空白——而空白页恰恰是
    /// 「脚本不能执行」的状态，于是功能整体不可用。批改网登录页同样是
    /// 「AdapterCreated 之后再 Navigate」（见 <see cref="PigaiLoginView"/>）。
    /// </para>
    /// </summary>
    private bool _initialNavigationStarted;

    public JwImportView()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Vm is { } vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            // 视图可能是在 JwImportOpen 已经为 true 之后才挂上可视树（覆盖层与视图同生命周期）。
            if (vm.JwImportOpen) OpenDialog();
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Vm is { } vm) vm.PropertyChanged -= OnVmPropertyChanged;
        CloseDialog();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.JwImportOpen)) return;

        if (Vm is { JwImportOpen: true }) OpenDialog();
        else CloseDialog();
    }

    // ---------------- 弹窗生命周期 ----------------

    private void OpenDialog()
    {
        var vm = Vm;
        if (vm is null) return;

        if (_dialog is not null)
        {
            // 已经开着：只聚焦，不重建（重建会丢掉用户已登录的会话状态）。
            try { _dialog.Focus(); } catch (Exception ex) { JwCaptureService.Log($"Focus 失败: {ex.Message}"); }
            return;
        }

        NativeWebDialog dialog;
        try
        {
            dialog = new NativeWebDialog
            {
                Title = "教务系统 · 登录并进入课表查询页",
                CanUserResize = true,
                ShowFocused = true,
            };
            // 属性与尺寸必须在 Show 之前设置，Show 之后不再生效。
            dialog.Resize(WindowWidth, WindowHeight);
        }
        catch (Exception ex)
        {
            vm.SetTimetableStatus($"无法创建教务窗口：{ex.Message}");
            JwCaptureService.Log($"创建弹窗失败: {ex.Message}");
            return;
        }

        // 每次重开都是新弹窗：首次导航与页面就绪状态都要跟着重置，
        // 否则会带着上一个弹窗的「已就绪」状态去撞新页面的加载期。
        _initialNavigationStarted = false;
        _pageGate.Reset();

        _dialog = dialog;
        MainViewModel.JwDialogAccessor = () => _dialog;
        MainViewModel.JwPageGate = _pageGate;

        dialog.AdapterCreated += (_, _) =>
        {
            JwCaptureService.Log("AdapterCreated");
            vm.SetTimetableStatus(string.Empty);
            _pageGate.MarkAdapterCreated();

            // 适配器就绪后才发首次导航（原先紧跟 Show()，属于抢跑，见字段注释）。
            NavigateToLoginPage(vm);
        };
        dialog.AdapterDestroyed += (_, _) => JwCaptureService.Log("AdapterDestroyed");

        // 跳转开始 → 关闸（此刻在飞的脚本会被销毁上下文，以 TaskCanceledException 结束，
        // 已由 JwCaptureService 收敛）；加载完成 → 开闸，注入循环随即补上悬浮球。
        dialog.NavigationStarted += (_, _) => _pageGate.MarkNavigating();

        dialog.NavigationCompleted += (_, e) =>
        {
            JwCaptureService.Log($"NavigationCompleted success={e.IsSuccess} url={e.Request}");
            vm.JwWindowStateText = e.IsSuccess
                ? $"当前页面：{e.Request}"
                : $"页面加载失败（{e.Request}）：请确认网址与网络后重试。";
            _pageGate.MarkReady();
            // 页面每次加载完成后悬浮球都会被清掉，这里立刻重启循环（内部会重新注入）。
            StartBallLoop();
        };

        // 二级页在同一窗口内跳转：教务菜单多以新窗口打开功能页。
        dialog.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            JwCaptureService.Log($"NewWindowRequested → 改为同窗口导航: {e.Request}");
            try
            {
                if (e.Request is not null) _dialog?.Navigate(e.Request);
            }
            catch (Exception ex)
            {
                JwCaptureService.Log($"同窗口导航失败: {ex.Message}");
            }
        };

        dialog.Closing += (_, _) =>
        {
            // 用户直接关掉弹窗：同步收起导入面板，否则会留下一个没有窗口的空面板。
            JwCaptureService.Log("弹窗被用户关闭");
            StopBallLoop();
            _dialog = null;
            MainViewModel.JwDialogAccessor = null;
            MainViewModel.JwPageGate = null;
            Vm?.CloseJwImport();
        };

        try
        {
            var owner = TopLevel.GetTopLevel(this);
            if (owner is null)
            {
                // 还没挂上可视树就没有 owner 可依附；交给下次可视化时再开。
                vm.SetTimetableStatus("界面尚未就绪，请稍后重试。");
                _dialog = null;
                MainViewModel.JwDialogAccessor = null;
                MainViewModel.JwPageGate = null;
                return;
            }
            dialog.Show(owner);
        }
        catch (Exception ex)
        {
            vm.SetTimetableStatus($"无法打开教务窗口：{ex.Message}");
            JwCaptureService.Log($"Show 失败: {ex.Message}");
            _dialog = null;
            MainViewModel.JwDialogAccessor = null;
            MainViewModel.JwPageGate = null;
            return;
        }

        vm.JwWindowStateText = "窗口已打开，正在加载登录页…";

        // 这里就把注入循环启起来（它会在闸门关闭期间按兵不动），
        // 于是「适配器没建好」「页面没加载完」这些阶段也都还有状态可看、可诊断。
        StartBallLoop();
    }

    /// <summary>
    /// 打开教务登录页。<b>只能在 <c>AdapterCreated</c> 之后调用</b>——抢跑会丢掉这次导航，
    /// 页面停在空白，脚本能力也就无从谈起。
    /// </summary>
    private void NavigateToLoginPage(MainViewModel vm)
    {
        if (_initialNavigationStarted) return;

        var url = vm.JwEffectiveLoginUrl;
        if (url.Length == 0)
        {
            // 走到这里说明连已保存的网址都没有，窗口只会是一片空白——必须给出原因。
            vm.SetTimetableStatus("尚未配置学校教务系统登录页网址，请先到 设置 → 账号 填写。");
            JwCaptureService.Log("Navigate 跳过：没有可用的教务网址");
            return;
        }

        Uri target;
        try
        {
            target = new Uri(url);
        }
        catch (UriFormatException ex)
        {
            vm.SetTimetableStatus($"教务系统网址无法识别：{ex.Message}");
            JwCaptureService.Log($"网址解析失败: {ex.Message}");
            return;
        }

        try
        {
            var target2 = _dialog;
            if (target2 is null)
            {
                // 正常路径不会走到这里（_dialog 在注册事件之前就赋值了）。
                // 真走到了就说明顺序被改坏了——宁可报出来，也不要留一个永远空白的窗口。
                vm.SetTimetableStatus("教务窗口尚未就绪，无法打开登录页。");
                JwCaptureService.Log("Navigate 跳过：_dialog 为空");
                return;
            }

            _initialNavigationStarted = true;
            target2.Navigate(target);
            JwCaptureService.Log($"Navigate → {target}");
        }
        catch (Exception ex)
        {
            // 失败就放开标志：导航事件如果后来又来了，还能再试一次。
            _initialNavigationStarted = false;
            vm.SetTimetableStatus($"无法打开教务系统页面：{ex.Message}");
            JwCaptureService.Log($"Navigate 失败: {ex.Message}");
        }
    }

    private void CloseDialog()
    {
        StopBallLoop();
        var dialog = _dialog;
        _dialog = null;
        MainViewModel.JwDialogAccessor = null;
        MainViewModel.JwPageGate = null;
        if (dialog is null) return;

        try { dialog.Close(); } catch (Exception ex) { JwCaptureService.Log($"Close 失败: {ex.Message}"); }
        try { dialog.Dispose(); } catch (Exception ex) { JwCaptureService.Log($"Dispose 失败: {ex.Message}"); }
    }

    // ---------------- 悬浮球轮询 ----------------

    private void StartBallLoop()
    {
        if (_running || _dialog is null) return;
        if (Vm is not { JwImportOpen: true } vm) return;
        if (!vm.HasJwProfile)
        {
            vm.SetTimetableStatus("请先在 设置 → 账号 填写学校教务系统登录页网址。");
            return;
        }

        _running = true;
        _ballCts = new CancellationTokenSource();
        var ct = _ballCts.Token;

        // fire-and-forget：内部自己兜异常（从 async void 逃逸异常会终止进程）。
        _ = RunAsync(vm, ct);
    }

    private void StopBallLoop()
    {
        _running = false;
        var cts = _ballCts;
        _ballCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* 已停过 */ }
        // 刻意不在这里 Dispose：循环可能正卡在 Task.Delay(..., ct) 上，
        // 此刻 Dispose 会让它抛 ObjectDisposedException 而不是取消异常。
        // 释放交给循环自己的 finally（见 RunAsync）——那时才真的没人再用这个 token 了。
    }

    private async Task RunAsync(MainViewModel vm, CancellationToken ct)
    {
        // 首次导航不在这里发——必须等 AdapterCreated（见 NavigateToLoginPage 的注释）。
        var dialog = _dialog;
        if (dialog is null) { _running = false; return; }

        try
        {
            await JwCaptureService.RunBallLoopAsync(
                dialog,
                _pageGate,
                async () =>
                {
                    var d = _dialog;
                    if (d is null) return "教务窗口已关闭";

                    var result = await JwCaptureService.FetchAndImportAsync(
                        d,
                        _pageGate,
                        () => vm.CurrentSchedule,
                        (file, merge) => vm.ApplyImportedScheduleAsync(file, merge),
                        vm.CurrentTerm,
                        ct).ConfigureAwait(true);

                    // 结果同时写进面板状态：悬浮球文案只够一句话，回面板还能看到完整说明。
                    vm.SetTimetableStatus(result.Message);
                    JwCaptureService.Log($"fetch: success={result.Success} {result.Message}");
                    return result.Message;
                },
                ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch (Exception ex)
        {
            JwCaptureService.Log($"ball loop 异常 {ex.GetType().Name}: {ex.Message}");
            vm.SetTimetableStatus($"抓取出错：{ex.Message}");
        }
        finally
        {
            _running = false;
            // 走到这里循环已经结束，不会再有 Task.Delay(..., ct) 用到这个 token。
            try { _ballCts?.Dispose(); } catch (ObjectDisposedException) { /* 已经释放过 */ }
            _ballCts = null;
        }
    }

    // ---------------- 面板按钮 ----------------

    private void OnReopenWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CloseDialog();
        OpenDialog();
    }

    private async void OnManualFetchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        try
        {
            await vm.FetchJwScheduleNowAsync();
        }
        catch (Exception ex)
        {
            // async void：不能让异常逃逸。
            JwCaptureService.Log($"手动抓取异常: {ex.Message}");
        }
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Vm?.CloseJwImport();
}
