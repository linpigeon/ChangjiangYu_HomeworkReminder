using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using HomeworkReminder.Services;
using HomeworkReminder.Views;

namespace HomeworkReminder.Desktop;

sealed class Program
{
    /// <summary>
    /// 启动诊断日志。GUI 应用在 Windows 上没有控制台，崩溃时看不到任何输出，
    /// 所以把启动阶段与未处理异常写到数据目录下的 startup.log，便于排查
    /// 「双击没反应」这类问题。
    /// </summary>
    private static string LogPath => Path.Combine(AppPaths.DataDirectory, "startup.log");

    [STAThread]
    public static void Main(string[] args)
    {
        // 平台服务注入要放在一切模式分支之前：自检/测试模式同样会走到 OpenSource 等调用。
        ViewModels.MainViewModel.PlatformServices = new DesktopPlatformServices();

        // --jsoncheck：JSON 序列化离线自检（为裁剪发布准备）。
        //   裁剪会移除反射元数据，线上表现为「登录后同步即崩」；这里把每个序列化类型
        //   用生产同款 options 往返一遍，无需登录即可在 trim 包上验证。见 JsonSelfTest。
        if (args.Contains("--jsoncheck"))
            Environment.Exit(JsonSelfTest.RunAsync().GetAwaiter().GetResult());

        // --selfcheck [pngPath]
        //   构建窗口、跑一次布局与绑定检查，可选地离屏渲染成 PNG，然后退出。
        //   在无法看到桌面的环境里，这是确认「XAML 与绑定确实能工作」的手段。
        var selfCheckIndex = Array.IndexOf(args, "--selfcheck");
        var renderPath = selfCheckIndex >= 0 && args.Length > selfCheckIndex + 1
            ? args[selfCheckIndex + 1]
            : null;

        // --selfcheck-login [png]：强制停在登录页后自检渲染，用于查看登录界面布局。
        var loginCheckIndex = Array.IndexOf(args, "--selfcheck-login");
        var loginRenderPath = loginCheckIndex >= 0 && args.Length > loginCheckIndex + 1
            ? args[loginCheckIndex + 1]
            : null;
        if (loginCheckIndex >= 0) App.ForceLoginPage = true;

        // 环境变量 HWREMINDER_FORCE_LOGIN=1：正常运行模式下也强制停在登录页，
        // 便于在真实窗口（含 WebView）里验证登录界面。
        if (Environment.GetEnvironmentVariable("HWREMINDER_FORCE_LOGIN") == "1")
            App.ForceLoginPage = true;

        // --logintest [秒数]：复现「自动检测进行中时点『重新检测登录』」并断言进入主界面且完成同步。
        var loginTestIndex = Array.IndexOf(args, "--logintest");

        // --webtest [秒数]
        //   真实窗口 + 真实 WebView2，验证「打开登录页 → 页面内探测」这条链路不会挂死。
        //   这条链路曾经因为 WebView 调用跑在线程池线程上而永久卡住。
        var webTestIndex = Array.IndexOf(args, "--webtest");

        Log($"=== start {DateTimeOffset.Now:O} (selfcheck={selfCheckIndex >= 0}) ===");
        Log($"exe        : {Environment.ProcessPath}");
        Log($"data dir   : {AppPaths.DataDirectory}");
        Log($"OS         : {RuntimeInformation.OSDescription}");
        Log($".NET       : {RuntimeInformation.FrameworkDescription}");

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("UNHANDLED: " + e.ExceptionObject);

        // WebView2 适配器创建失败会以「未观察的任务异常」形式冒到调度器上，
        // 默认行为是直接终止进程——那会让登录页的兜底路径永远没机会出现。
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("UNOBSERVED TASK: " + e.Exception);
            e.SetObserved();
        };

        if (webTestIndex >= 0)
        {
            var seconds = webTestIndex + 1 < args.Length && int.TryParse(args[webTestIndex + 1], out var s) ? s : 30;
            RunWebViewTest(TimeSpan.FromSeconds(seconds));
            return;
        }

        // --cookietest [秒数]：单独验证 WebView 的 Cookie 读取与清除。
        var cookieTestIndex = Array.IndexOf(args, "--cookietest");

        if (cookieTestIndex >= 0)
        {
            var cs = cookieTestIndex + 1 < args.Length && int.TryParse(args[cookieTestIndex + 1], out var cv) ? cv : 120;
            CookieTest.Run(TimeSpan.FromSeconds(cs));
            return;
        }

        if (loginTestIndex >= 0)
        {
            var seconds = loginTestIndex + 1 < args.Length && int.TryParse(args[loginTestIndex + 1], out var ls) ? ls : 90;
            // --logintest --logout：登录流程跑完后再验证退出登录是否清掉了 WebView 的登录态。
            LoginFlowTest.Run(TimeSpan.FromSeconds(seconds), verifyLogout: args.Contains("--logout"));
            return;
        }

        if (loginCheckIndex >= 0)
        {
            RunSelfCheck(loginRenderPath);
            return;
        }

        if (selfCheckIndex >= 0)
        {
            RunSelfCheck(renderPath);
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            Log("=== clean exit ===");
        }
        catch (Exception ex)
        {
            Log("FATAL: " + ex);
            throw;
        }
    }

    /// <summary>
    /// 真实窗口下的 WebView 链路测试。
    /// <para>
    /// 只验证「能否打开登录页并在页面里拿到状态码」——不要求真的扫码。
    /// 关键断言是 <b>在超时内拿到任何状态码</b>：拿得到说明 WebView 调用确实在 UI 线程上
    /// 正常完成；拿不到就说明又回到了「InvokeScript 永不返回」的死锁状态。
    /// </para>
    /// <para>
    /// 走正常的桌面生命周期（<c>StartWithClassicDesktopLifetime</c>），
    /// 因为 WebView2 与窗口都需要平台与消息泵真正跑起来；
    /// <c>SetupWithoutStarting</c> 那套只适合不需要窗口的离屏渲染。
    /// </para>
    /// </summary>
    private static void RunWebViewTest(TimeSpan budget)
    {
        App.DeferInitialLoad = true;
        App.WebViewTestHook = RunWebViewTestInWindow;
        App.WebViewTestBudget = budget;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(["--webtest-runner"]);
    }

    /// <summary>由 App 在桌面生命周期就绪后调用，接管窗口并执行断言。</summary>
    internal static void RunWebViewTestInWindow(
        Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop,
        TimeSpan budget)
    {
        var webView = new Avalonia.Controls.NativeWebView();
        var window = new Window
        {
            Title = "WebView 链路测试",
            Width = 900,
            Height = 650,
            Content = webView,
        };
        desktop.MainWindow = window;

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var adapterCreated = false;
        webView.AdapterCreated += (_, _) =>
        {
            adapterCreated = true;
            Console.WriteLine("  [事件] AdapterCreated");
        };
        webView.NavigationStarted += (_, e) =>
            Console.WriteLine($"  [事件] NavigationStarted {e.Request}");
        webView.NavigationCompleted += (_, e) =>
            Console.WriteLine($"  [事件] NavigationCompleted success={e.IsSuccess}");

        string? lastStatus = null;
        var probes = 0;
        Exception? failure = null;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) =>
        {
            try
            {
                var status = await YktLoginService.TryProbeAsync(webView);
                probes++;
                // 只在拿到状态码时输出：页面加载初期会连续返回 null（脚本刚发起请求），
                // 每次都打印会把日志刷满。
                if (status is not null)
                {
                    lastStatus = status;
                    Console.WriteLine($"  [探测 {probes}] Source={webView.Source} 返回={status}");
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        };

        // 打开登录页，并在预算用尽后给出结论。
        window.Opened += (_, _) =>
        {
            try
            {
                webView.Navigate(new Uri(YktLoginService.LoginPageUrl));
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            timer.Start();

            // 先摸清 InvokeScript 的返回值到底怎么编码的——这决定了探测脚本该怎么写。
            DispatcherTimer.RunOnce(async () =>
            {
                Console.WriteLine("=== InvokeScript 返回值形态 ===");
                foreach (var (label, script) in new (string, string)[]
                {
                    ("普通表达式返回字符串", "'abc'"),
                    ("async IIFE 返回字符串", "(async()=>{return 'xyz';})()"),
                    ("async IIFE 返回数字字符串", "(async()=>{return String(200);})()"),
                    ("同步 return String(200)", "String(200)"),
                    ("await 后直接返回数字", "(async()=>{await Promise.resolve();return 200;})()"),
                })
                {
                    try
                    {
                        var raw = await webView.InvokeScript(script);
                        Console.WriteLine($"  {label,-28} -> {raw ?? "(null)"}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  {label,-28} -> 异常 {ex.GetType().Name}");
                    }
                }
            }, TimeSpan.FromSeconds(6));

            DispatcherTimer.RunOnce(() =>
            {
                timer.Stop();
                Console.WriteLine("=== WebView 链路测试 ===");
                Console.WriteLine($"  AdapterCreated : {adapterCreated}");
                Console.WriteLine($"  探测成功次数   : {probes}");
                Console.WriteLine($"  最后一次状态码 : {lastStatus ?? "(从未拿到)"}");
                var cookies = webView.TryGetCookieManager();
                Console.WriteLine($"  CookieManager  : {(cookies is null ? "不可用" : "可用")}");
                if (failure is not null) Console.WriteLine($"  异常           : {failure}");

                var ok = probes > 0 && lastStatus is not null && failure is null;
                Console.WriteLine(ok
                    ? "  结论：WebView 调用在 UI 线程上正常返回 —— 链路通畅。"
                    : "  结论：失败 —— 仍可能在 WebView 调用上挂死或环境缺 WebView2。");
                Environment.Exit(ok ? 0 : 5);
            }, budget);
        };

        window.Show();
    }

    private static void RunSelfCheck(string? renderPath)
    {
        App.DeferInitialLoad = true;

        var builder = BuildAvaloniaApp();
        // SetupWithoutStarting 不会进入消息循环，所以下面的初始化必须自己泵调度器，
        // 否则 InvokeAsync 排进去的回调永远不会执行（表现为挂死）。
        builder.SetupWithoutStarting();

        var app = (App)Application.Current!;
        var vm = app.ViewModel ?? throw new InvalidOperationException("App did not create a view model");

        var initTask = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                await vm.InitializeAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"数据初始化失败（继续做界面自检）：{ex.Message}");
            }
        });

        PumpUntil(initTask, TimeSpan.FromMinutes(4));

        // 自检时切换到「作业」视图：它同时覆盖列表行、圆形勾选框、进度徽标、
        // 导航计数与右侧详情，比默认的「我的一天」（在无当日到期项时为空白）信息量大。
        // 登录页渲染模式下没有数据，跳过。
        if (vm.State == ViewModels.AppState.Ready)
        {
            vm.SelectedNav = vm.NavItems.First(n => n.Key == "homework");
            vm.OnNavChanged();
        }

        var window = new MainWindow { DataContext = vm };
        var code = SelfCheck.Run(window, renderPath);
        Environment.Exit(code);
    }

    /// <summary>
    /// 反复清空调度器队列，直到 <paramref name="task"/> 完成或超时。
    /// </summary>
    private static void PumpUntil(Task task, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (!task.IsCompleted && DateTimeOffset.Now < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(20);
        }

        if (!task.IsCompleted)
        {
            Console.WriteLine("自检：等待数据初始化超时，继续做界面自检。");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, message + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception)
        {
            // 日志本身失败不应影响启动。
        }
    }
}
