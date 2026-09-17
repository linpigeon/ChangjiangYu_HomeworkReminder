using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeworkReminder.Services;
using HomeworkReminder.ViewModels;
using HomeworkReminder.Views;

namespace HomeworkReminder.Desktop;

/// <summary>
/// 复现「自动检测进行中，用户点『重新检测登录』」这一场景，并断言最终会进入主界面且完成同步。
/// <para>
/// 存在的理由：按钮一度因为自动检测占着 <c>IsBusy</c> 而被当成「取消」，点了没反应；
/// 这类问题只有把「等待适配器 → 点按钮 → 同步完成」整条链路跑一遍才验证得了。
/// </para>
/// </summary>
internal static class LoginFlowTest
{
    public static void Run(TimeSpan budget, bool verifyLogout = false)
    {
        App.DeferInitialLoad = true;
        App.WebViewTestBudget = budget;
        App.ForceLoginPage = true;
        App.WebViewTestHook = (desktop, b) => RunInWindow(desktop, b, verifyLogout);
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(["--logintest-runner"]);
    }

    private static void RunInWindow(
        Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop,
        TimeSpan budget,
        bool verifyLogout)
    {
        var vm = new MainViewModel();
        var view = new LoginView { DataContext = vm };
        var window = new Window
        {
            Title = "登录流程测试",
            Width = 1180,
            Height = 760,
            Content = view,
        };
        // 必须登记为主窗口：否则桌面生命周期认为「没有窗口了」，
        // 窗口一显示就把应用关掉（表现为测试没有任何输出）。
        desktop.MainWindow = window;

        var timeline = new System.Collections.Generic.List<string>();
        var logPath = Path.Combine(AppPaths.DataDirectory, "logintest.log");
        try { File.WriteAllText(logPath, string.Empty); } catch { /* 日志失败不影响测试 */ }

        void Note(string s)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {s}";
            timeline.Add(line);
            Console.WriteLine("  " + line);
            // 同时写文件：进程若意外退出，stdout 可能来不及刷新，文件里能看到进行到哪一步。
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { /* 同上 */ }
        }

        window.Opened += (_, _) =>
        {
            // 等 WebView 适配器就绪：自动检测此时仍在进行中。
            var waitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            var waited = TimeSpan.Zero;
            waitTimer.Tick += async (_, _) =>
            {
                waited += waitTimer.Interval;
                var webView = view.GetVisualDescendants()
                    .OfType<Avalonia.Controls.NativeWebView>().FirstOrDefault();
                var ready = webView?.AdapterInfo is not null;

                if (!ready && waited < TimeSpan.FromSeconds(30)) return;

                waitTimer.Stop();
                Note($"WebView 适配器就绪={ready}（等待 {(int)waited.TotalSeconds}s）");

                if (!ready)
                {
                    Note("结论：WebView 未就绪，无法继续");
                    Environment.Exit(7);
                }

                // 关键一步：自动检测还在跑的时候点按钮。
                // 按钮的可点条件必须与后台检测无关，否则这里就是「点不动」。
                Note($"点击前：IsBusy={vm.IsBusy} IsAutoDetecting={vm.IsAutoDetecting} " +
                     $"按钮可用={!vm.IsCheckingLogin} State={vm.State}");

                if (vm.IsCheckingLogin)
                {
                    Note("结论：按钮在后台自动检测期间处于禁用状态 —— 就是「点不动」的原因 ✗");
                    Environment.Exit(9);
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                await view.TriggerReDetectAsync();
                sw.Stop();
                Note($"点击返回，用时 {sw.ElapsedMilliseconds} ms，State={vm.State}");

                // 同步是点击流程的一部分，等它跑完。
                Note("开始等待同步完成…");
                var syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                var waited2 = TimeSpan.Zero;
                syncTimer.Tick += (_, _) =>
                {
                    waited2 += syncTimer.Interval;
                    // 定期记录，便于判断是否卡在某个状态。
                    if ((int)waited2.TotalSeconds % 10 == 0)
                        Note($"等待中… State={vm.State} IsBusy={vm.IsBusy} 列表={vm.List.Items.Count}");

                    if (vm.State == AppState.Ready || waited2 > TimeSpan.FromMinutes(3))
                    {
                        syncTimer.Stop();

                        if (!verifyLogout)
                        {
                            Report(vm, Note);
                            return;
                        }

                        // 退出登录：断言 WebView 内部的登录态也被清掉。
                        _ = VerifyLogoutAsync(vm, view, Note);
                    }
                };
                syncTimer.Start();
            };
            waitTimer.Start();
        };

        window.Show();
    }

    /// <summary>
    /// 退出登录后，WebView 内部的 Cookie 必须被清掉。
    /// <para>
    /// 这是「退出登录后登录页仍停留在雨课堂内」那个 bug 的回归测试：
    /// WebView 有自己的持久化 Cookie 存储，只删应用会话文件的话它仍然登录着，
    /// 下次启动的自动检测还会把同一个会话接管回来。
    /// </para>
    /// </summary>
    private static async Task VerifyLogoutAsync(MainViewModel vm, LoginView view, Action<string> note)
    {
        var webView = view.GetVisualDescendants()
            .OfType<Avalonia.Controls.NativeWebView>().First();

        note($"退出前：State={vm.State} 会话文件={File.Exists(Path.Combine(AppPaths.DataDirectory, "session.json"))}");

        await vm.LogoutCommand.ExecuteAsync(null);

        // 再探测一次：Cookie 已清除的话，课程接口应返回 401 而不是 200。
        await Task.Delay(TimeSpan.FromSeconds(2));
        var statusAfter = await YktLoginService.TryProbeAsync(webView);
        var sessionFileExists = File.Exists(Path.Combine(AppPaths.DataDirectory, "session.json"));

        Console.WriteLine();
        Console.WriteLine("=== 退出登录测试结果 ===");
        Console.WriteLine($"  退出后状态     : {vm.State}");
        Console.WriteLine($"  ShowLogin      : {vm.ShowLogin}");
        Console.WriteLine($"  会话文件已删除 : {!sessionFileExists}");
        Console.WriteLine($"  Cookie 清理    : {vm.LogoutCookieReport}");
        Console.WriteLine($"  WebView 探测   : {statusAfter ?? "(null)"}   ← 期望 401");

        var ok = vm.State == AppState.NeedsLogin
                 && vm.ShowLogin
                 && !sessionFileExists
                 && statusAfter != "200";

        Console.WriteLine(ok
            ? "  结论：退出登录同时清掉了 WebView 内的登录态 ✓"
            : "  结论：WebView 仍处于登录状态（登录页会继续停在雨课堂内）✗");

        note(ok ? "PASS" : "FAIL");
        Environment.Exit(ok ? 0 : 10);
    }

    private static void Report(MainViewModel vm, Action<string> note)
    {
        // 默认停留在「我的一天」，当天没有到期项时它就是空的。
        // 真正能说明同步成功的是「作业」视图有内容。
        vm.SelectedNav = vm.NavItems.First(n => n.Key == "homework");
        vm.OnNavChanged();

        Console.WriteLine();
        Console.WriteLine("=== 登录流程测试结果 ===");
        Console.WriteLine($"  最终状态     : {vm.State}");
        Console.WriteLine($"  ShowContent  : {vm.ShowContent}");
        Console.WriteLine($"  用户         : {vm.UserName} / {vm.UserSubtitle}");
        Console.WriteLine($"  是否有头像   : {vm.HasAvatar}");
        Console.WriteLine($"  同步状态     : {vm.SyncStatus}");
        Console.WriteLine($"  统计         : {vm.StatusDetail}");
        Console.WriteLine($"  作业视图条目 : {vm.List.Items.Count}（可见 {vm.List.VisibleItems.Count}）");
        note($"结果：State={vm.State} 用户={vm.UserName} 作业条目={vm.List.Items.Count} 同步={vm.SyncStatus}");

        var ok = vm.State == AppState.Ready
                 && vm.ShowContent
                 && vm.SyncStatus.StartsWith("同步于", StringComparison.Ordinal)
                 && vm.List.Items.Count > 0;

        Console.WriteLine(ok
            ? "  结论：点『重新检测登录』后进入主界面并完成同步 ✓"
            : "  结论：未达到预期 —— 没有进入主界面，或同步没有结果 ✗");

        note(ok ? "PASS" : "FAIL");
        Environment.Exit(ok ? 0 : 8);
    }
}
