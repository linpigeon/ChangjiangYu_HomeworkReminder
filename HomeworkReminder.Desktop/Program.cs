using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeworkReminder.Models;
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

        // --selfcheck-widget [png]：渲染桌面小组件并做绑定自检。
        var widgetCheckIndex = Array.IndexOf(args, "--selfcheck-widget");
        var widgetRenderPath = widgetCheckIndex >= 0 && args.Length > widgetCheckIndex + 1
            ? args[widgetCheckIndex + 1]
            : null;

        // --selfcheck-login [png]：强制停在登录页后自检渲染，用于查看登录界面布局。
        var loginCheckIndex = Array.IndexOf(args, "--selfcheck-login");
        var loginRenderPath = loginCheckIndex >= 0 && args.Length > loginCheckIndex + 1
            ? args[loginCheckIndex + 1]
            : null;
        if (loginCheckIndex >= 0) App.ForceLoginPage = true;

        // --selfcheck-settings [png]：不登录直接渲染设置页（主界面内嵌页），
        // 用于查看设置页布局；数据与登录态无关，不做网络初始化。
        var settingsCheckIndex = Array.IndexOf(args, "--selfcheck-settings");
        var settingsRenderPath = settingsCheckIndex >= 0 && args.Length > settingsCheckIndex + 1
            ? args[settingsCheckIndex + 1]
            : null;

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

        // --jwcheck [秒数]：真实弹出教务独立弹窗（NativeWebDialog），验证
        //   「弹窗能开 → 页面能加载 → 悬浮球能注入 → InvokeScript 能返回」这条链路。
        //   教务导入改用独立弹窗后，这几个环节任一失效都会让功能整体不可用，
        //   而它们在无窗口的离屏自检里根本测不到。
        var jwCheckIndex = Array.IndexOf(args, "--jwcheck");

        Log($"=== start {DateTimeOffset.Now:O} (selfcheck={selfCheckIndex >= 0}) ===");
        Log($"args       : [{string.Join(", ", args)}]");
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

        // --toastcheck：只验证通知链路（AUMID 注册、快捷方式、toast XML、WinRT 投递），
        //   然后退出。不依赖登录，可在无账号环境下诊断「为什么没弹通知」。
        //   故意放在异常兜底与启动日志初始化之后：自检工具自身抛异常时也能落 startup.log。
        if (args.Contains("--toastcheck"))
        {
            Environment.Exit(ToastCheck.Run());
        }

        if (webTestIndex >= 0)
        {
            var seconds = webTestIndex + 1 < args.Length && int.TryParse(args[webTestIndex + 1], out var s) ? s : 30;
            RunWebViewTest(TimeSpan.FromSeconds(seconds));
            return;
        }

        // --cookietest [秒数]：单独验证 WebView 的 Cookie 读取与清除。
        var cookieTestIndex = Array.IndexOf(args, "--cookietest");

        // --detailcheck [png]：详情面板互斥回归测试
        //   （先选课程再选待办，确认两块详情不叠加渲染）。
        var detailCheckIndex = Array.IndexOf(args, "--detailcheck");
        if (detailCheckIndex >= 0)
        {
            var png = args.Length > detailCheckIndex + 1 && !args[detailCheckIndex + 1].StartsWith('-')
                ? args[detailCheckIndex + 1]
                : null;
            DetailCheck.Run(png);
            return;
        }

        // --updatecheck [--prerelease] [--target <exe>]：检查更新，并把「用户已同意」视为真，
        //   走与界面**完全相同**的下载 → 校验 → 替换 → 重启路径。
        //   存在的理由：更新流程只能在真实发布版上验证，而 GUI 弹窗无法在无头环境点。
        //   --prerelease 临时开启预发布通道（不写 settings.json，只影响本次运行）。
        //   --target 用于把替换目标指向临时沙箱里的一份副本，避免动到真实安装目录。
        if (args.Contains("--updatecheck"))
        {
            var targetIdx = Array.IndexOf(args, "--target");
            var targetOverride = targetIdx >= 0 && args.Length > targetIdx + 1 ? args[targetIdx + 1] : null;
            if (args.Contains("--prerelease")) AppSettings.Current.IncludePrereleaseUpdates = true;
            Environment.Exit(RunUpdateCheck(targetOverride));
        }

        // --cxcheck：学习通任务点在线自检（用本机已保存的学习通会话真实跑一遍
        //   课程列表 → 逐课任务点，打印未完成任务点数与明细）。只读，不改远端状态。
        if (args.Contains("--cxcheck"))
        {
            Environment.Exit(ChaoxingCheck.Run());
        }

        // --pigaitest "<整条cookie>" [秒数]
        //   批改网下游链路端到端测试：建立会话 → 关覆盖层 → 同步 → 待办出现未完成作文。
        // --pigailogin <账号> <密码> [秒数]
        //   批改网登录捕获链路测试：打开登录页 → 填表提交 → 轮询捕获 → 自动返回。
        //   两种模式下凭据/cookie 都只从命令行读进内存，不落盘、不写日志。
        var pigaiTestIndex = Array.IndexOf(args, "--pigaitest");
        var pigaiLoginIndex = Array.IndexOf(args, "--pigailogin");

        if (jwCheckIndex >= 0)
        {
            // 教务弹窗链路测试：真实弹窗 + 真实页面，不需要账号（只验证链路通不通）。
            var secs = args.Length > jwCheckIndex + 1 && int.TryParse(args[jwCheckIndex + 1], out var jwSecs)
                ? jwSecs
                : 45;
            RunJwDialogCheck(TimeSpan.FromSeconds(secs));
            return;
        }
        if (pigaiTestIndex >= 0 || pigaiLoginIndex >= 0)
        {
            if (pigaiTestIndex >= 0)
            {
                if (args.Length < pigaiTestIndex + 2)
                {
                    Console.WriteLine("用法：--pigaitest \"<整条cookie>\" [秒数]");
                    Environment.Exit(2);
                }

                var secs = args.Length > pigaiTestIndex + 2
                           && int.TryParse(args[pigaiTestIndex + 2], out var s1) ? s1 : 120;
                PigaiFlowTest.Run(args[pigaiTestIndex + 1], null, TimeSpan.FromSeconds(secs));
                return;
            }

            if (args.Length < pigaiLoginIndex + 3)
            {
                Console.WriteLine("用法：--pigailogin <账号> <密码> [秒数]");
                Environment.Exit(2);
            }

            var secs2 = args.Length > pigaiLoginIndex + 3
                        && int.TryParse(args[pigaiLoginIndex + 3], out var s2) ? s2 : 150;
            PigaiFlowTest.Run(string.Empty, (args[pigaiLoginIndex + 1], args[pigaiLoginIndex + 2]),
                TimeSpan.FromSeconds(secs2));
            return;
        }

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

        // --androidcheck <outDir> [webview] [desktop]：Android 手机尺寸的界面遮挡审计。
        //   把移动外壳逐屏渲染成 PNG，并量出「视口越界 / 祖先裁剪 / 文字截断」三类缺陷。
        //   存在的理由：本项目的界面缺陷绝大多数是「某个控件铺满整行压住了旁边的列」，
        //   桌面 1180x760 复现不了，只有手机宽度下的实测坐标能证明。
        //   webview = 连登录页/教务导入页/答题页一起审（会实例化原生 WebView，慢）；
        //   desktop = 顺带量一遍 960 宽的最小桌面三栏，确认窄屏切换没有回归。
        var androidCheckIndex = Array.IndexOf(args, "--androidcheck");
        if (androidCheckIndex >= 0)
        {
            var outDir = args.Length > androidCheckIndex + 1 && !args[androidCheckIndex + 1].StartsWith('-')
                ? args[androidCheckIndex + 1]
                : Path.Combine(Path.GetTempPath(), "hr-androidcheck");
            Environment.Exit(AndroidLayoutCheck.Run(
                outDir,
                includeWebViewStates: args.Contains("webview"),
                includeDesktopShell: args.Contains("desktop")));
        }

        // --homeworkreminder-ttcheck handled below
        // --ttcheck [png] [otherweeks]：把课表页单独渲染出来（几何/配色/布局必须看图确认）。
        var ttCheckIndex = Array.IndexOf(args, "--ttcheck");
        if (ttCheckIndex >= 0)
        {
            var png = args.Length > ttCheckIndex + 1 && !args[ttCheckIndex + 1].StartsWith('-')
                ? args[ttCheckIndex + 1]
                : null;
            RunTimetableSelfCheck(png, useOtherWeeks: args.Contains("otherweeks"));
            return;
        }

        if (widgetCheckIndex >= 0)
        {
            RunWidgetSelfCheck(widgetRenderPath);
            return;
        }

        if (loginCheckIndex >= 0)
        {
            RunSelfCheck(loginRenderPath);
            return;
        }

        if (settingsCheckIndex >= 0)
        {
            // --selfcheck-settings <png> [account|timetable|about]：渲染设置页，可指定选项卡。
            var tab = args.Contains("about") ? 3
                : args.Contains("timetable") ? 2
                : args.Contains("account") ? 1
                : 0;
            RunSettingsSelfCheck(settingsRenderPath, tab);
            return;
        }

        if (selfCheckIndex >= 0)
        {
            RunSelfCheck(renderPath);
            return;
        }

        // 系统通知：构造时自注册 AUMID 并补开始菜单快捷方式（写注册表 + 开始菜单）。
        // 只在正常运行模式下做——--jsoncheck/--selfcheck 等纯自检不该改用户系统状态。
        // 注册失败也不影响主流程——ViewModel 会把它显示成「系统通知不可用：原因」。
        // 平台守卫让分析器满意，也表达「仅 Windows 有系统通知」这一事实。
        ViewModels.MainViewModel.Notifier = OperatingSystem.IsWindows()
            ? new WindowsNotifier()
            : new NullNotifier();

        // 托盘：Windows 上用自绘菜单的托盘宿主（原生 NativeMenu 无法改样式）。
        if (OperatingSystem.IsWindows())
            App.TrayIconHook = TrayIconHost.Attach;

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

    /// <summary>
    /// 教务独立弹窗链路测试（<c>--jwcheck</c>）。
    /// <para>
    /// 教务导入用的是 <see cref="Avalonia.Controls.NativeWebDialog"/>（独立弹窗）。
    /// 这条链路有几个环节在离屏自检里**测不到**，而任一失效功能就整体不可用：
    /// 弹窗能否创建并显示、页面能否加载、悬浮球能否注入、<c>InvokeScript</c> 能否返回。
    /// 所以这里开真实弹窗逐项验证，不需要教务账号（只验证链路，不验证解析结果）。
    /// </para>
    /// </summary>
    private static void RunJwDialogCheck(TimeSpan budget)
    {
        App.DeferInitialLoad = true;
        App.WebViewTestHook = RunJwDialogCheckInWindow;
        App.WebViewTestBudget = budget;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(["--jwcheck-runner"]);
    }

    /// <summary>由 App 在桌面生命周期就绪后调用：开一个教务弹窗，逐项验证链路。</summary>
    internal static void RunJwDialogCheckInWindow(
        Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop,
        TimeSpan budget)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var window = new Window
        {
            Title = "教务弹窗链路测试（宿主窗口）",
            Width = 520,
            Height = 320,
        };
        desktop.MainWindow = window;

        var loginUrl = Environment.GetEnvironmentVariable("HWREMINDER_JW_URL");
        if (string.IsNullOrWhiteSpace(loginUrl))
        {
            Console.WriteLine("  未设置 HWREMINDER_JW_URL，使用内置示例地址（仅验证弹窗与脚本链路）。");
            loginUrl = "https://example.com/";
        }

        Avalonia.Controls.NativeWebDialog? dialog = null;
        var adapterCreated = false;
        var navCompleted = 0;
        var sawNewWindow = 0;
        var clickedLink = false;
        var injected = false;
        string? scriptResult = null;
        Exception? failure = null;

        // 页面就绪闸门：与生产代码（JwImportView）同一套接线方式，
        // 这样这一项检查测的就是真实链路，而不是另写一份模拟。
        var gate = new Services.PageReadyGate();

        window.Opened += (_, _) =>
        {
            try
            {
                dialog = new Avalonia.Controls.NativeWebDialog
                {
                    Title = "教务系统 · 链路测试",
                    CanUserResize = true,
                    ShowFocused = true,
                };
                dialog.Resize(1100, 800); // 必须在 Show 之前设置

                dialog.AdapterCreated += (_, _) =>
                {
                    adapterCreated = true;
                    gate.MarkAdapterCreated();
                    Console.WriteLine("  [事件] AdapterCreated");
                };
                dialog.NavigationStarted += (_, e) =>
                {
                    gate.MarkNavigating();
                    Console.WriteLine($"  [事件] NavigationStarted {e.Request}");
                };
                dialog.NavigationCompleted += (_, e) =>
                {
                    navCompleted++;
                    gate.MarkReady();
                    Console.WriteLine($"  [事件] NavigationCompleted success={e.IsSuccess} {e.Request}");
                };
                dialog.NewWindowRequested += (_, e) =>
                {
                    sawNewWindow++;
                    Console.WriteLine($"  [事件] NewWindowRequested {e.Request}");
                    e.Handled = true;
                    try
                    {
                        if (e.Request is not null) dialog!.Navigate(e.Request);
                        Console.WriteLine("         → 已改为同窗口导航（Handled=true）");
                    }
                    catch (Exception ex) { Console.WriteLine("         → 导航失败: " + ex.Message); }
                };

                Console.WriteLine($"  [步骤] 创建弹窗并 Show，目标 {loginUrl}");
                dialog.Show(window);
                Console.WriteLine("  [步骤] Show 返回（未抛异常）");

                // 本地页面模式下用一个临时 HTML 文件：离线即可验证「悬浮球注入」与
                // 「二级页在同一窗口内跳转」两件事，不依赖任何外部站点。
                if (Environment.GetEnvironmentVariable("HWREMINDER_JW_LOCAL") == "1")
                {
                    // 页面刻意做得像正方的课表查询页：有课表结构，便于同时确认
                    // 悬浮球叠加正常、以及抓取链路能解析出内容。
                    var page = "<html><head><meta charset='utf-8'><title>个人课表查询</title></head>"
                        + "<body style='font:14px sans-serif;margin:0;background:#f5f6f8'>"
                        + "<div style='background:#2564CF;color:#fff;padding:14px 24px;font-size:18px'>"
                        + "珠海科技学院 · 教学综合服务平台</div>"
                        + "<div style='padding:18px 24px'>"
                        + "<h3 style='margin:0 0 12px'>个人课表查询（本地模拟页）</h3>"
                        + "<p style='color:#444'>用于验证：弹窗为独立窗口、二级页在同一窗口内跳转、悬浮球正常注入。</p>"
                        + "<p><a id='go' href='second.html' target='_blank' style='color:#2564CF'>"
                        + "→ 点我打开二级页（target=_blank，应在本窗口内跳转）</a></p>"
                        + "<table border='1' cellspacing='0' cellpadding='8' "
                        + "style='border-collapse:collapse;margin-top:16px;background:#fff'>"
                        + "<tr style='background:#eef3fb'><th>节次</th><th>星期一</th><th>星期二</th>"
                        + "<th>星期三</th><th>星期四</th><th>星期五</th></tr>"
                        + "<tr><td>1-2</td><td>高等数学<br>张三<br>1-16周<br>教一楼101</td>"
                        + "<td></td><td>大学物理<br>李四<br>1-8,10-16周<br>理科楼203</td>"
                        + "<td></td><td>大学英语<br>王五<br>1-15周(单)<br>外语楼305</td></tr>"
                        + "<tr><td>3-4</td><td></td><td>程序设计基础<br>赵六<br>1-16周<br>机房A</td>"
                        + "<td></td><td>体育<br>1-16周(双)<br>田径场</td><td></td></tr>"
                        + "</table></div></body></html>";
                    var second = "<html><head><meta charset='utf-8'><title>二级页</title></head>"
                        + "<body style='font:15px sans-serif;padding:34px'>"
                        + "<h2>二级页已打开</h2>"
                        + "<p>如果你看到本页出现在<b>同一个弹窗里</b>（而不是新窗口），说明拦截生效。</p>"
                        + "</body></html>";

                    var dir = Path.Combine(Path.GetTempPath(), "hr-jw-local");
                    Directory.CreateDirectory(dir);
                    var pagePath = Path.Combine(dir, "first.html");
                    File.WriteAllText(pagePath, page, System.Text.Encoding.UTF8);
                    File.WriteAllText(Path.Combine(dir, "second.html"), second, System.Text.Encoding.UTF8);

                    dialog.Navigate(new Uri(pagePath));
                    Console.WriteLine($"  [步骤] 已加载本地测试页 {pagePath}");
                }
                else
                {
                    dialog.Navigate(new Uri(loginUrl));
                }

                // ===== 回归断言：页面就绪闸门 + 返回值拆壳 =====
                // 两个真实踩过的坑，都在这里钉住：
                //   ① 弹窗刚 Show() 就注入 → InvokeScript 必定失败：
                //      "Unable to invoke script before any page was loaded. Listen for NavigationCompleted event."
                //   ② InvokeScript 把返回值序列化成 JSON 字符串 → 抓到的整页 HTML 变成
                //      「满篇 \u003C、一个尖括号都没有」，解析器一条课都读不出来（且没有警告）。
                var probeLog = Path.Combine(Path.GetTempPath(), "hr-jw-probe.log");
                File.WriteAllText(probeLog, $"=== jwcheck {DateTimeOffset.Now:O} ==={Environment.NewLine}");

                void Note(string s)
                {
                    try { File.AppendAllText(probeLog, s + Environment.NewLine); } catch { }
                }

                // A. 裸调用一次，确认「未加载完就调用必定失败」这个前提仍然成立。
                try
                {
                    var preLoad = dialog.InvokeScript("(()=>{return 'pre';})()");
                    if (preLoad.IsCompleted && preLoad.Exception is { } ex)
                        Note($"[A-前提] 页面未加载即调用 -> {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
                    else
                        Note($"[A-前提] 未复现（status={preLoad.Status}）");
                }
                catch (Exception ex)
                {
                    Note($"[A-前提] 同步抛出 {ex.GetType().Name}: {ex.Message}");
                }

                // B/C/D. 注入循环只在开闸后下发；闸门关闭时不碰 InvokeScript。
                gate.ResetCounters();
                var ct = new CancellationTokenSource();
                _ = Services.JwCaptureService.RunBallLoopAsync(
                    dialog,
                    gate,
                    () => Task.FromResult("(测试不点击)"),
                    ct.Token);

                DispatcherTimer.RunOnce(() =>
                {
                    Note($"[B-注入循环] 下发次数={gate.InvokeAttempts} 因未就绪被拦下={gate.SkippedNotReady}"
                         + $" PageReady={gate.IsReady} Source={dialog.Source}");
                    Note(gate.InvokeAttempts > 0
                        ? "[B-注入循环] PASS：页面就绪后确实下发了脚本"
                        : "[B-注入循环] FAIL：页面就绪后仍然没有下发过脚本");

                    var closedGate = new Services.PageReadyGate();
                    closedGate.ResetCounters();
                    var blocked = Services.JwCaptureService.CapturePageAsync(dialog, closedGate);
                    Note($"[C-抓取闸门] 未就绪时抓取 -> IsCompleted={blocked.IsCompleted}"
                         + $" Result={(blocked.IsCompleted ? blocked.Result ?? "(null)" : "(pending)")}"
                         + $" 下发次数={closedGate.InvokeAttempts}（期望 0）");
                    Note(closedGate.InvokeAttempts == 0 && closedGate.SkippedNotReady > 0
                        ? "[C-抓取闸门] PASS：未就绪时一次都没有下发 InvokeScript"
                        : "[C-抓取闸门] FAIL：未就绪时仍然下发了脚本");

                    Note($"[D-闸门语义] 全新闸门 IsReady={new Services.PageReadyGate().IsReady}（期望 False）");

                    // E. 值拆壳：真机返回值形态必须还原成脚本真正返回的文本。
                    _ = ProbeValueUnwrapAsync(dialog, Note);
                }, TimeSpan.FromSeconds(7));

                // 必须 async + await：InvokeScript 的结果通过窗口消息回来，同步阻塞会把它饿死。
                async Task ProbeValueUnwrapAsync(Avalonia.Controls.NativeWebDialog d, Action<string> note)
                {
                    static string Repr(string? s) => s is null
                        ? "(null)"
                        : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

                    // 真机实测：脚本返回值被序列化成 JSON 字符串；带引号/换行的内容会被转义。
                    const string html = "<div class=\"x\">中</div>";
                    var got = await Services.JwCaptureService.TryEvalAsync(
                        d, "(()=>{return " + System.Text.Json.JsonSerializer.Serialize(html) + ";})()")
                        .ConfigureAwait(true);
                    note($"[E-拆壳] 脚本返回 {Repr(html)} -> C# 侧拿到 {Repr(got)}");
                    note(got == html
                        ? "[E-拆壳] PASS：返回值已还原成脚本真正返回的文本"
                        : "[E-拆壳] FAIL：返回值仍带 JSON 壳（解析器会一条课都读不出来）");
                    note("=== jwcheck end ===");
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                Console.WriteLine($"  [失败] 创建/显示弹窗异常：{ex.GetType().Name}: {ex.Message}");
            }
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) =>
        {
            if (dialog is null) return;
            try
            {
                if (Environment.GetEnvironmentVariable("HWREMINDER_JW_LOCAL") == "1" && !clickedLink)
                {
                    // 真的点一下 target="_blank" 链接：只有真点击才会触发 NewWindowRequested，
                    // 这是验证「二级页在同一窗口内跳转」的唯一可靠办法。
                    clickedLink = true;
                    var r = await Services.JwCaptureService.TryEvalAsync(
                        dialog, "(()=>{var a=document.getElementById('go');if(!a)return 'no-link';a.click();return 'clicked';})()");
                    Console.WriteLine($"  [步骤] 触发二级页点击 → {r}");
                }

                // 先注入悬浮球，再读回它的**真实布局矩形**。
                //（顺序不能反——没注入就读，永远只会拿到 no-ball，那是假失败。）
                // 只查 DOM 存在不够：元素被 CSS 隐藏或尺寸为 0 时也「存在」，
                // 所以这里读 getBoundingClientRect 的宽高与屏幕位置，等价于确认真能看见。
                await Services.JwCaptureService.InjectBallAsync(dialog);
                var raw = await Services.JwCaptureService.TryEvalAsync(
                    dialog,
                    "(()=>{var b=document.getElementById('__hrJwBall');if(!b)return 'no-ball';"
                    + "var r=b.getBoundingClientRect();"
                    + "return 'ball '+Math.round(r.width)+'x'+Math.round(r.height)"
                    + "+' right='+Math.round(innerWidth-r.right)+' bottom='+Math.round(innerHeight-r.bottom)"
                    + "+' text='+JSON.stringify(b.textContent);})()");
                scriptResult = raw;
                injected = (raw ?? string.Empty).StartsWith("ball ", StringComparison.Ordinal)
                           && !(raw ?? string.Empty).StartsWith("ball 0x0", StringComparison.Ordinal);

                if (scriptResult is not null)
                {
                    Console.WriteLine($"  [探测] InvokeScript 返回={scriptResult.Trim()} Source={dialog.Source}");
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        };

        var ended = false;
        var deadline = new DispatcherTimer { Interval = budget };
        deadline.Tick += (_, _) =>
        {
            if (ended) return;
            ended = true;
            timer.Stop();
            deadline.Stop();

            Console.WriteLine();
            Console.WriteLine("  === 教务弹窗链路结论 ===");
            Console.WriteLine($"    弹窗创建并 Show      : {(dialog is not null ? "是" : "否")}");
            Console.WriteLine($"    适配器创建(AdapterCreated): {(adapterCreated ? "是" : "否")}");
            Console.WriteLine($"    页面加载完成次数      : {navCompleted}");
            Console.WriteLine($"    拦截新窗口次数        : {sawNewWindow}");
            Console.WriteLine($"    InvokeScript 可返回  : {(scriptResult is not null ? "是" : "否")}");
            Console.WriteLine($"    悬浮球可见(有尺寸且贴右下): {(injected ? "是" : "否")}");
            if (scriptResult is not null) Console.WriteLine($"    悬浮球实测          : {scriptResult.Trim()}");

            var ok = dialog is not null && adapterCreated && navCompleted > 0
                     && scriptResult is not null && failure is null;
            Console.WriteLine($"    结论：{(ok ? "通过 —— 教务弹窗链路可用" : "失败 —— 见上方输出")}");
            if (failure is not null)
                Console.WriteLine($"    异常：{failure.GetType().Name}: {failure.Message}");

            try { dialog?.Close(); dialog?.Dispose(); } catch (Exception) { /* 收尾尽力而为 */ }
            Environment.Exit(ok ? 0 : 1);
        };

        timer.Start();
        deadline.Start();
    }

    /// <summary>由 App 在桌面生命周期就绪后调用，接管窗口并执行断言。</summary>
    internal static void RunWebViewTestInWindow(        Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop,
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

    /// <summary>
    /// 桌面小组件自检：构造 <see cref="WidgetWindow"/>、跑布局与绑定检查，
    /// 可选离屏渲染成 PNG（小组件是深色半透明面板，靠肉眼确认可读性最直接）。
    /// </summary>
    private static void RunWidgetSelfCheck(string? renderPath)
    {
        App.DeferInitialLoad = true;

        var builder = BuildAvaloniaApp();
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

        // 不显示窗口，只渲染内容：小组件窗口是无边框置顶窗口，
        // 在自检环境里显示它反而会干扰。
        var widget = new Views.WidgetWindow(vm);

        // 默认分组「我的一天」在无当日到期项时是空的，渲染出来只有空状态。
        // 切到「所有」才能同时检查列表行、勾选框、逾期着色的真实表现。
        widget.SelectGroupForCheck("all");

        var code = SelfCheck.Run(widget, renderPath);
        Environment.Exit(code);
    }

    /// <summary>
    /// 课表页自检（<c>--ttcheck &lt;png&gt;</c>）：把课表页单独渲染出来。
    /// <para>
    /// 存在的理由：课表网格是**绝对定位的时间轴**，「1-2 节的课块是否真的占两行、
    /// 第 3 节是否紧接其下」这类问题在数据层断言不出来（几何对了也可能被布局吃掉），
    /// 必须看图。而走 <c>--selfcheck</c> 需要登录态与真实初始化，
    /// 这里直接把课表数据喂给页面，离线即可渲染。
    /// </para>
    /// <para>
    /// 数据来源：优先用当前学期已导入的课表；没有则用一组内置样本，
    /// 保证任何环境下都能渲染出一张有内容的表（含跨节次、单双周、同格冲突）。
    /// </para>
    /// </summary>
    private static void RunTimetableSelfCheck(string? renderPath, bool useOtherWeeks)
    {
        // 自检进度写文件：这些模式下窗口在离屏渲染，stdout 可能被缓冲到进程退出才刷出来，
        // 一旦中途卡住就完全看不到进度（排查这种问题只能靠落盘的痕迹）。
        var trace = Path.Combine(Path.GetTempPath(), "hr-ttcheck.log");
        void Trace(string step)
        {
            try { File.AppendAllText(trace, $"[{DateTime.Now:HH:mm:ss.fff}] {step}{Environment.NewLine}"); }
            catch (IOException) { /* 日志失败不影响自检 */ }
        }

        try { File.WriteAllText(trace, $"=== ttcheck {DateTimeOffset.Now:O} ==={Environment.NewLine}"); }
        catch (IOException) { /* 同上 */ }

        // 详细几何（每个课块的矩形）只在需要时输出：默认只留一行摘要，
        // 但「课块被摆到哪去了」这类问题没有它就无从判断，所以保留这个开关。
        var verbose = Environment.GetEnvironmentVariable("TTCHECK_VERBOSE") == "1";

        App.DeferInitialLoad = true;

        var builder = BuildAvaloniaApp();
        builder.SetupWithoutStarting();

        var app = (App)Application.Current!;
        var vm = app.ViewModel ?? throw new InvalidOperationException("App did not create a view model");

        // 优先加载真实导入的课表；失败就用样本，不让自检因为「没导入过」而没得看。
        // 同步等待用 Task.Run 兜住：这几个 store 走的是 ConfigureAwait(false)，
        // 但在 UI 线程上直接阻塞仍可能把消息泵饿死。
        var file = Task.Run(() => new ScheduleStore().LoadAsync()).GetAwaiter().GetResult();

        // 样本模式（TTCHECK_SAMPLE=1）：不看本机课表，用内置样本渲染，
        // 这样课表的几种形状（跨节次/单双周/同格冲突/周六有课）在任何机器上都能看到。
        // TTCHECK_SELECT=1：选中第一个课块，用来验证「课程详情面板」的绑定与布局
        //（没选中时它整块不实例化，所以不选中就根本测不到它）。
        var useSample = Environment.GetEnvironmentVariable("TTCHECK_SAMPLE") == "1";
        var selectBlock = Environment.GetEnvironmentVariable("TTCHECK_SELECT") == "1";
        var source = "样本";
        if (file.Items.Count == 0 || useSample)
        {
            file = SampleSchedule();
        }
        else
        {
            source = "已导入的课表";
        }

        // 没有学期基准也能渲染，但列头日期、「今天」高亮、「下一节课」都出不来，
        // 所以自检固定注入「今天」的日期，保证这三处始终被覆盖到。
        var today = DateTime.Now;
        vm.Timetable.TodayDate = today.ToString("yyyy-MM-dd");

        vm.LoadScheduleForCheck(file);
        vm.Timetable.ShowOtherWeeks = useOtherWeeks;
        vm.OpenTimetable();

        if (selectBlock && vm.Timetable.Blocks.Count > 0)
        {
            // 选中第一个课块：详情面板没选中时整块不实例化，不选中就根本测不到它的绑定与布局。
            vm.Timetable.SelectedBlock = vm.Timetable.Blocks[0];
            Trace($"已选中课块: {vm.Timetable.SelectedBlock?.Name} HasSelection={vm.Timetable.HasSelection}");
        }

        Trace(
            $"课表自检: 来源={source} 条目={file.Items.Count} 节次行={vm.Timetable.RowCount} "
            + $"课块={vm.Timetable.Blocks.Count} 本周={vm.Timetable.BlockCount} "
            + $"周次={vm.Timetable.WeekNumber} 本周标记={vm.Timetable.IsCurrentWeekDisplayed} "
            + $"网格高={vm.Timetable.GridHeight:0} 天区宽={vm.Timetable.DaysWidth:0} "
            + $"下一节={vm.Timetable.NextClassText}");

        if (verbose)
        {
            foreach (var blk in vm.Timetable.Blocks)
                Trace($"  block {blk.Name} day={blk.Model.Weekday} "
                      + $"x={blk.X:0} y={blk.Y:0} w={blk.Width:0} h={blk.Height:0} 本周={blk.IsCurrentWeek}");
            foreach (var p in vm.Timetable.Periods)
                Trace($"  period {p.Period} top={p.Top:0} h={p.Height:0} time={p.TimeText}");
        }

        var window = new MainWindow { DataContext = vm };

        // 窗口尺寸可选（TTCHECK_SIZE=WxH）：课表要能在小窗口里正确滚动/对齐，
        // 而默认 1180x760 恰好放得下，问题不会暴露。
        var sizeText = Environment.GetEnvironmentVariable("TTCHECK_SIZE");
        if (!string.IsNullOrWhiteSpace(sizeText) && sizeText.Split('x') is [var ws, var hs]
            && double.TryParse(ws, out var w) && double.TryParse(hs, out var h))
        {
            window.Width = w;
            window.Height = h;
            Trace($"窗口尺寸设为 {w}x{h}");
        }

        var code = SelfCheck.Run(window, renderPath);

        // 对齐自检（TTCHECK_ALIGN=1）：把「节次刻度」与「课块」的实际屏幕位置量出来对比。
        // 「课块没跟时间表对齐」这类问题只看截图容易被字体的视觉重心骗到，
        // 必须拿真实坐标判断——两者必须落在同一套 Y 上。
        if (Environment.GetEnvironmentVariable("TTCHECK_ALIGN") == "1")
        {
            var view = window.GetVisualDescendants().OfType<Views.TimetableView>().FirstOrDefault();
            var periodRows = view?.GetVisualDescendants().OfType<Border>()
                .Where(x => x.Classes.Contains("tt-period")).ToList();
            var blocks = view?.GetVisualDescendants().OfType<Border>()
                .Where(x => x.Classes.Contains("tt-block")).ToList();

            if (view is not null && periodRows is not null && blocks is not null)
            {
                // 量各区块的横向边界：节次刻度、列头、课块区必须落在同一套 X 上。
                void BoundsOf(string label, Visual? v)
                {
                    if (v is null) { Trace($"  {label}: 找不到"); return; }
                    var tl = v.TranslatePoint(new Avalonia.Point(0, 0), view);
                    Trace($"  {label}: X={tl?.X:0.##} Y={tl?.Y:0.##} W={v.Bounds.Width:0.##} H={v.Bounds.Height:0.##}");
                }

                var dayHeaderItems = view.GetVisualDescendants().OfType<ItemsControl>()
                    .FirstOrDefault(c => c.Name == "DayHeaderItems");
                var emptyText = view.GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(t => t.Classes.Contains("faint"));
                BoundsOf("节次列(PeriodScroll)", view.GetVisualDescendants().OfType<ScrollViewer>()
                    .FirstOrDefault(s => s.Name == "PeriodScroll"));
                BoundsOf("列头(DayHeaderItems)", dayHeaderItems);
                BoundsOf("课块区(GridScroll)", view.GetVisualDescendants().OfType<ScrollViewer>()
                    .FirstOrDefault(s => s.Name == "GridScroll"));
                BoundsOf("空状态文字", emptyText);

                // 列头里每个文字的**实际**上下边界：表头高度不够时文字会画到行外，
                // 与下面的空状态提示叠在一起（实测用户截图就是这样）。
                if (dayHeaderItems is not null)
                {
                    var texts = dayHeaderItems.GetVisualDescendants().OfType<TextBlock>().ToList();
                    Trace($"  列头文字数={texts.Count}（列头自身 H={dayHeaderItems.Bounds.Height:0.##}）");
                    foreach (var t in texts.Take(4))
                    {
                        var tl = t.TranslatePoint(new Avalonia.Point(0, 0), view);
                        Trace($"    「{t.Text}」X={tl?.X:0.##} Y={tl?.Y:0.##} W={t.Bounds.Width:0.##} H={t.Bounds.Height:0.##} 裁剪={t.ClipToBounds}");
                    }
                    if (texts.Count > 0)
                    {
                        var maxBottom = texts.Max(t => (t.TranslatePoint(new Avalonia.Point(0, 0), view)?.Y ?? 0) + t.Bounds.Height);
                        var headerBottom = (dayHeaderItems.TranslatePoint(new Avalonia.Point(0, 0), view)?.Y ?? 0) + dayHeaderItems.Bounds.Height;
                        Trace($"  列头文字最低点={maxBottom:0.##} 列头底={headerBottom:0.##} "
                              + (maxBottom > headerBottom + 0.5 ? "→ 溢出（会压到下面的内容）" : "→ 未溢出"));
                    }
                }

                // 第一个节次刻度与第一列列头的 X —— 两者都靠左时应当接近。
                var leftmostPeriod = periodRows.FirstOrDefault();
                var firstHeader = dayHeaderItems?.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(x => x.Classes.Contains("tt-dayhead"));
                if (leftmostPeriod is not null && firstHeader is not null)
                {
                    Trace($"  第 1 节刻度 X={leftmostPeriod.TranslatePoint(new Avalonia.Point(0, 0), view)?.X:0.##}");
                    Trace($"  第 1 个列头 X={firstHeader.TranslatePoint(new Avalonia.Point(0, 0), view)?.X:0.##}");
                }

                Trace($"对齐自检：节次行数={periodRows.Count} 课块数={blocks.Count}");
                foreach (var p in periodRows.Take(4))
                    Trace($"  节次 屏幕Y={p.TranslatePoint(new Avalonia.Point(0, 0), view)?.Y:0} 高={p.Bounds.Height:0}");

                foreach (var b in blocks.OrderBy(x => x.TranslatePoint(new Avalonia.Point(0, 0), view)?.Y ?? 0).Take(4))
                    Trace($"  课块 屏幕Y={b.TranslatePoint(new Avalonia.Point(0, 0), view)?.Y:0} 高={b.Bounds.Height:0}");

                var firstBlock = blocks.OrderBy(x => x.TranslatePoint(new Avalonia.Point(0, 0), view)?.Y ?? 0).FirstOrDefault();
                var firstPeriod = periodRows.OrderBy(x => x.TranslatePoint(new Avalonia.Point(0, 0), view)?.Y ?? 0).FirstOrDefault();
                if (firstBlock is not null && firstPeriod is not null)
                {
                    var blockY = firstBlock.TranslatePoint(new Avalonia.Point(0, 0), view)?.Y ?? 0;
                    var periodY = firstPeriod.TranslatePoint(new Avalonia.Point(0, 0), view)?.Y ?? 0;
                    Trace($"  第 1 节课块 Y={blockY:0}，第 1 节刻度 Y={periodY:0}，差={blockY - periodY:0.#}");
                    Trace(Math.Abs(blockY - periodY) < 1
                        ? "  对齐自检：PASS —— 课块与节次刻度同一条基准线"
                        : "  对齐自检：FAIL —— 课块与节次刻度错位");
                }
            }
        }

        // 滚动对齐自检（TTCHECK_HSCROLL / TTCHECK_VSCROLL，可同时给）：
        // 把课表滚到指定位置后**重新截图**并核对三处偏移是否一致。
        // 「课程和星期/节次对不上」只在滚动后才暴露——不滚的话永远测不到。
        var hScroll = double.TryParse(Environment.GetEnvironmentVariable("TTCHECK_HSCROLL"), out var hx) ? hx : 0;
        var vScroll = double.TryParse(Environment.GetEnvironmentVariable("TTCHECK_VSCROLL"), out var vy) ? vy : 0;
        if ((hScroll > 0 || vScroll > 0) && renderPath is not null)
        {
            var view = window.GetVisualDescendants().OfType<Views.TimetableView>().FirstOrDefault();
            if (view is null)
            {
                Trace("滚动自检：找不到 TimetableView");
            }
            else
            {
                ScrollViewer? Find(string name) => view.GetVisualDescendants().OfType<ScrollViewer>()
                    .FirstOrDefault(s => s.Name == name);

                var gridScroll = Find("GridScroll");
                if (gridScroll is not null)
                {
                    var target = gridScroll.Offset.WithX(hScroll).WithY(vScroll);
                    gridScroll.Offset = target;
                    // 让 ScrollChanged 里的同步逻辑跑完（列头/节次列都是在那里跟上的）。
                    Dispatcher.UIThread.RunJobs();
                    Dispatcher.UIThread.RunJobs();

                    Trace($"滚动到 ({gridScroll.Offset.X:0},{gridScroll.Offset.Y:0})"
                          + $" 范围 {gridScroll.Extent.Width:0}x{gridScroll.Extent.Height:0}");

                    var headerScroll = Find("DayHeaderScroll");
                    var periodScroll = Find("PeriodScroll");
                    var headerX = headerScroll?.Offset.X ?? double.NaN;
                    var periodY = periodScroll?.Offset.Y ?? double.NaN;

                    Trace($"  列头横向偏移={headerX:0}（应={gridScroll.Offset.X:0}）");
                    Trace($"  节次列纵向偏移={periodY:0}（应={gridScroll.Offset.Y:0}）");
                    Trace(Math.Abs(headerX - gridScroll.Offset.X) < 0.5 && Math.Abs(periodY - gridScroll.Offset.Y) < 0.5
                        ? "  对齐自检：PASS —— 列头随横向滚动、节次列随纵向滚动"
                        : "  对齐自检：FAIL —— 偏移不一致，课块会落在错误的星期/节次下");

                    Dispatcher.UIThread.RunJobs();
                    var size = new Avalonia.PixelSize((int)window.Width, (int)window.Height);
                    using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96, 96));
                    bitmap.Render(window);
                    bitmap.Save(renderPath);
                    Trace("  已按滚动后的状态重新截图");
                }
            }
        }

        // TTCHECK_NAV=N：模拟连点「上一周 / 下一周」N 次，验证周次真的会变。
        // 「箭头点了没反应」这种问题在数据层看不出来，得让按钮的处理逻辑真跑一遍。
        var navTimes = int.TryParse(Environment.GetEnvironmentVariable("TTCHECK_NAV"), out var n) ? n : 0;
        if (navTimes > 0)
        {
            var before = vm.Timetable.WeekNumber;
            for (var i = 0; i < navTimes; i++)
            {
                vm.ShiftTimetableWeek(1);
                Trace($"  下一周[{i + 1}] -> WeekNumber={vm.Timetable.WeekNumber} WeekLabel={vm.Timetable.WeekLabel} 课块={vm.Timetable.Blocks.Count}");
            }
            for (var i = 0; i < navTimes; i++)
            {
                vm.ShiftTimetableWeek(-1);
                Trace($"  上一周[{i + 1}] -> WeekNumber={vm.Timetable.WeekNumber}");
            }
            Trace($"导航自检: 起始={before} 前进{navTimes}次再后退{navTimes}次 -> 结束={vm.Timetable.WeekNumber}");

            vm.GotoCurrentTimetableWeek();
            Trace($"  回本周 -> WeekNumber={vm.Timetable.WeekNumber}");
        }

        // TTCHECK_EMPTY=1：切到「整周没课」的周次并**重新截图**，用来看空状态提示的位置
        //（有课的时候它是隐藏的，看不到）。
        if (Environment.GetEnvironmentVariable("TTCHECK_EMPTY") == "1" && renderPath is not null)
        {
            vm.Timetable.WeekNumber = Services.Timetable.MaxWeek;
            vm.Timetable.SelectedBlock = null;
            Dispatcher.UIThread.RunJobs();
            Trace($"空状态自检：周次={vm.Timetable.WeekNumber} 本周课块={vm.Timetable.BlockCount} 空状态可见={!vm.Timetable.HasBlocks}");

            var size3 = new Avalonia.PixelSize((int)window.Width, (int)window.Height);
            using var bitmap3 = new Avalonia.Media.Imaging.RenderTargetBitmap(size3, new Avalonia.Vector(96, 96));
            bitmap3.Render(window);
            bitmap3.Save(renderPath);
            Trace("  已按空状态重新截图");
        }

        if (verbose && renderPath is not null)
        {
            // 渲染后核对控件的**实际**位置：Canvas.Left/Top 绑定不生效时，
            // 每个课块都会落在 (0,0)，而截图看起来只是「排得奇怪」，不容易一眼看穿。
            var view = window.GetVisualDescendants().OfType<Views.TimetableView>().FirstOrDefault();
            if (view is not null)
            {
                Trace($"渲染后 HasSelection={vm.Timetable.HasSelection} Selected={vm.Timetable.SelectedBlock?.Name}");
                foreach (var b in view.GetVisualDescendants().OfType<Border>()
                             .Where(x => x.Classes.Contains("tt-block")).Take(6))
                {
                    var pos = b.TranslatePoint(new Avalonia.Point(0, 0), view);
                    Trace($"  实测 tt-block canvas=({Canvas.GetLeft(b)},{Canvas.GetTop(b)}) 屏幕={pos}");
                }

                var detail = view.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(x => x.Classes.Contains("tt-detail"));
                Trace(detail is null
                    ? "  详情面板：未实例化"
                    : $"  详情面板：IsVisible={detail.IsVisible} 可见={detail.IsEffectivelyVisible} "
                      + $"位置={detail.TranslatePoint(new Avalonia.Point(0, 0), view)} 尺寸={detail.Bounds.Size}");
            }
        }

        Trace($"selfcheck 完成 code={code}");
        Environment.Exit(code);
    }

    /// <summary>
    /// 内置课表样本：刻意覆盖真实课表的几种形状——
    /// 跨节次（1-2 / 3-4）、单双周、同一天同一节两门课（分栏）、
    /// 只有前半学期上的课（用来验证「非本周」弱化）、周六有课。
    /// </summary>
    private static ScheduleFile SampleSchedule()
    {
        var periods = Timetable.CreateDefaultPeriods();
        var items = new List<ScheduleItem>
        {
            Make("高等数学A", 1, 1, 2, 1, 16, WeekParity.Every, "明德楼B107", "张三"),
            Make("大学英语Ⅲ", 1, 3, 4, 1, 16, WeekParity.Every, "外语楼305", "李四"),
            Make("体育（篮球）", 1, 7, 8, 1, 16, WeekParity.Every, "田径场", "王五"),
            Make("程序设计基础", 2, 1, 2, 1, 16, WeekParity.Every, "机房A", "赵六"),
            Make("线性代数", 2, 3, 4, 1, 8, WeekParity.Every, "博文楼203", "钱七"),
            Make("大学物理", 2, 5, 6, 1, 15, WeekParity.Odd, "理科楼401", "孙八"),
            Make("数据结构", 3, 1, 2, 1, 16, WeekParity.Every, "信息楼501", "周九"),
            Make("数据结构实验", 3, 1, 2, 2, 16, WeekParity.Every, "机房B", "周九"),
            Make("概率论与数理统计", 3, 5, 6, 1, 16, WeekParity.Every, "博文楼E203", "吴十"),
            Make("模拟电子技术", 4, 3, 4, 1, 16, WeekParity.Every, "六教M305", "宋刚"),
            Make("信号与系统", 4, 7, 8, 1, 16, WeekParity.Every, "六教M211", "邱杨"),
            Make("中国近现代史纲要", 5, 1, 2, 1, 14, WeekParity.Every, "明德楼阶梯B107", "郑雨"),
            Make("人工智能与法律", 5, 5, 6, 9, 16, WeekParity.Every, "博文楼A201", "陈一"),
            Make("创新创业实践", 6, 3, 4, 1, 16, WeekParity.Even, "创业园", "林二"),
        };

        // 时间由节次表推导，与生产路径口径一致。
        foreach (var item in items)
        {
            var time = Timetable.ResolveSessionTime(item, periods);
            item.StartTime = time.StartTime;
            item.EndTime = time.EndTime;
        }

        return new ScheduleFile { Items = items, Periods = periods };
    }

    private static ScheduleItem Make(
        string name, int weekday, int startPeriod, int endPeriod, int startWeek, int endWeek,
        WeekParity parity, string location, string teacher) => new()
    {
        Id = $"{name}|{weekday}|{startPeriod}|{endPeriod}|{startWeek}|{endWeek}|{parity}",
        Name = name,
        Weekday = weekday,
        StartPeriod = startPeriod,
        EndPeriod = endPeriod,
        StartWeek = startWeek,
        EndWeek = endWeek,
        WeekParity = parity,
        Location = location,
        Teacher = teacher,
        Term = AppSettings.Current.Term,
    };

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
        // --selfcheck <png> course：改为渲染「课程」视图（课程维度任务点汇总）。
        if (vm.State == ViewModels.AppState.Ready)
        {
            var navKey = Environment.GetEnvironmentVariable("HWREMINDER_SELFCHECK_NAV") ?? "homework";
            vm.SelectedNav = vm.NavItems.FirstOrDefault(n => n.Key == navKey) ?? vm.NavItems[0];
            vm.OnNavChanged();
        }

        // 自检同时输出关键计数：排查「视图渲染为空」这类问题时，
        // 没有这行就只能靠猜（本轮课表页空白就是靠它定位到「网格没重建」）。
        Console.WriteLine(
            $"自检: state={vm.State} nav={vm.SelectedNav?.Key} items={vm.ScheduleItemCount} "
            + $"week={vm.Timetable.WeekNumber} periods={vm.Timetable.RowCount} "
            + $"blocks={vm.Timetable.Blocks.Count} thisWeek={vm.Timetable.BlockCount}");

        var window = new MainWindow { DataContext = vm };
        var code = SelfCheck.Run(window, renderPath);
        Environment.Exit(code);
    }

    /// <summary>
    /// 设置页自检：不读会话、不同步（设置页内容与登录态无关），
    /// 直接把主窗口切到设置页后跑布局与绑定检查、离屏渲染成 PNG。
    /// </summary>
    private static void RunSettingsSelfCheck(string? renderPath, int settingsTab)
    {
        App.DeferInitialLoad = true;

        var builder = BuildAvaloniaApp();
        builder.SetupWithoutStarting();

        var app = (App)Application.Current!;
        var vm = app.ViewModel ?? throw new InvalidOperationException("App did not create a view model");

        // 有会话就做真实初始化（离线/无会话也能渲染，只是没有待办数据）：
        // 设置页盖在待办区上，有数据才能验证淡出是否把下面的列表藏掉。
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

        // 伪造成已进入主界面：设置项本身不依赖待办数据。
        if (vm.State != ViewModels.AppState.Ready)
            vm.State = ViewModels.AppState.Ready;

        var window = new MainWindow { DataContext = vm };
        // 设置页在窗口显示后再打开（与用户点齿轮的运行时路径一致），
        // 才能验证「打开设置 → 待办区淡出」的过渡在运行时确实执行。
        window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            vm.OpenSettings();
            if (settingsTab != 0) vm.ShowSettingsTab(settingsTab);

            // SETTINGS_BAD_PERIODS=1：故意把时间表改错并触发保存，
            // 用来确认「校验失败要显眼」这条真的生效（只渲染正常状态是测不到错误提示的）。
            if (Environment.GetEnvironmentVariable("SETTINGS_BAD_PERIODS") == "1"
                && vm.PeriodsEditor.Rows.Count >= 2)
            {
                vm.PeriodsEditor.Rows[0].StartTime = "10:00";
                vm.PeriodsEditor.Rows[0].EndTime = "10:45";
                vm.PeriodsEditor.Rows[1].StartTime = "10:30";
                vm.PeriodsEditor.Rows[1].EndTime = "11:15";
                _ = vm.PeriodsEditor.SaveAsync();
            }
        });
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

    /// <summary>
    /// 检查更新并（视为用户已同意）执行更新。返回进程退出码。
    /// <para>
    /// 与界面走**同一条路径**（<see cref="ViewModels.MainViewModel.RunUpdateCheckAsync"/>），
    /// 区别只在「谁来回答那个确认框」：界面弹窗问用户，这里直接当作同意。
    /// 这样自检验证过的流程就是用户实际会走的流程。
    /// </para>
    /// </summary>
    /// <param name="targetExeOverride">
    /// 替换目标覆盖（仅供沙箱演练）：指向临时目录里的一份 exe 副本，
    /// 这样演练「替换 → 重启」时不会动到真实安装目录。
    /// </param>
    private static int RunUpdateCheck(string? targetExeOverride)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ViewModels.MainViewModel.PlatformServices = new DesktopPlatformServices();

        Console.WriteLine($"当前版本      : {Services.UpdateService.CurrentVersion}");
        Console.WriteLine($"替换目标      : {targetExeOverride ?? "(本进程自身)"}");
        Console.WriteLine("正在检查更新…");

        App.DeferInitialLoad = true;
        try
        {
            // 更新不需要完整应用初始化（不读会话、不同步），只要一个 ViewModel 承载流程。
            var vm = new ViewModels.MainViewModel();
            vm.ConfirmUpdateRequested = (_, _) =>
            {
                // 无头模式下没有窗口可弹确认框，这里代为回答「是」。
                Console.WriteLine("确认框        : （无头模式，视为用户同意）");
                return Task.FromResult(true);
            };

            vm.RunUpdateCheckAsync(autoConfirmSource: "自检", targetExeOverride: targetExeOverride)
              .GetAwaiter().GetResult();

            Console.WriteLine($"状态          : {vm.UpdateStatus}");
            if (!string.IsNullOrEmpty(vm.UpdateDownloadProgress))
                Console.WriteLine($"下载/进度     : {vm.UpdateDownloadProgress}");

            // 若走到了替换流程，应用已请求退出，这里不会再有输出。
            // 未替换则正常返回 0（已是最新 / 用户未同意 / 无适用包）。
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"自检异常：{ex.GetType().Name}: {ex.Message}");
            return 5;
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
