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
/// 只验证一件事：WebView 的 Cookie 能不能被读到、又能不能被删掉。
/// <para>
/// 背景：退出登录后登录页仍停留在雨课堂内（WebView 有自己的持久化 Cookie 存储）。
/// 这里把「读 Cookie → 删除 → 再读」单独跑一遍，避免与登录/同步流程混在一起看不清。
/// 结果写文件，因为 WebView2 退出时可能影响 stdout。
/// </para>
/// </summary>
internal static class CookieTest
{
    private static readonly string LogPath = Path.Combine(AppPaths.DataDirectory, "cookietest.log");

    private static void Note(string s)
    {
        try { File.AppendAllText(LogPath, s + Environment.NewLine); } catch { /* ignore */ }
        Console.WriteLine(s);
    }

    public static void Run(TimeSpan budget)
    {
        try { File.WriteAllText(LogPath, string.Empty); } catch { /* ignore */ }
        App.DeferInitialLoad = true;
        App.WebViewTestBudget = budget;
        App.ForceLoginPage = true;
        App.WebViewTestHook = (desktop, b) => RunInWindow(desktop, b);
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .StartWithClassicDesktopLifetime(["--cookietest-runner"]);
    }

    private static void RunInWindow(
        Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop,
        TimeSpan budget)
    {
        var vm = new MainViewModel();
        var view = new LoginView { DataContext = vm };
        var window = new Window { Title = "Cookie 测试", Width = 900, Height = 650, Content = view };
        desktop.MainWindow = window;

        window.Opened += (_, _) =>
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            var waited = TimeSpan.Zero;
            var done = false;

            timer.Tick += async (_, _) =>
            {
                if (done) return;
                waited += timer.Interval;

                var webView = view.GetVisualDescendants().OfType<Avalonia.Controls.NativeWebView>().FirstOrDefault();
                if (webView?.AdapterInfo is null)
                {
                    if (waited < TimeSpan.FromSeconds(30)) return;
                    Note("结论：WebView 未就绪");
                    Environment.Exit(11);
                    return;
                }

                // 让页面真正登录上（登录页会自动接管），再开始验证 Cookie。
                if (vm.State != AppState.Ready && waited < TimeSpan.FromSeconds(90)) return;

                done = true;
                timer.Stop();

                try
                {
                    Note($"=== Cookie 测试 ===（页面已登录={vm.State == AppState.Ready}，等待 {(int)waited.TotalSeconds}s）");

                    var manager = webView.TryGetCookieManager();
                    Note($"TryGetCookieManager: {(manager is null ? "null" : "可用")}");
                    if (manager is null) { Note("结论：拿不到 CookieManager"); Environment.Exit(12); }

                    var before = await manager.GetCookiesAsync();
                    var beforeList = before?.Cast<System.Net.Cookie>().ToList() ?? [];
                    Note($"删除前 Cookie 总数: {beforeList.Count}");
                    foreach (var c in beforeList.Take(25))
                        Note($"   {c.Name} @ {c.Domain}{c.Path}  值长度={c.Value?.Length ?? 0}");

                    var ykt = beforeList.Where(c => c.Domain.EndsWith("yuketang.cn", StringComparison.OrdinalIgnoreCase)).ToList();
                    Note($"其中雨课堂域: {ykt.Count}（{string.Join(", ", ykt.Select(c => c.Name))}）");

                    var result = await YktLoginService.ClearCookiesAsync(webView);
                    Note($"ClearCookiesAsync -> 找到={result.Found} 删除={result.Deleted} 错误={result.Error ?? "无"}");

                    var after = await manager.GetCookiesAsync();
                    var afterList = after?.Cast<System.Net.Cookie>().ToList() ?? [];
                    var afterYkt = afterList.Where(c => c.Domain.EndsWith("yuketang.cn", StringComparison.OrdinalIgnoreCase)).ToList();
                    Note($"删除后 Cookie 总数: {afterList.Count}，其中雨课堂域: {afterYkt.Count}（{string.Join(", ", afterYkt.Select(c => c.Name))}）");

                    var status = await YktLoginService.TryProbeAsync(webView);
                    Note($"重新探测课程接口: {status ?? "(null)"}  ← 期望 401");

                    var ok = afterYkt.Count == 0 && status != "200";
                    Note(ok ? "PASS：Cookie 已清除，WebView 不再处于登录状态" : "FAIL：Cookie 未被清除干净");
                    Environment.Exit(ok ? 0 : 13);
                }
                catch (Exception ex)
                {
                    Note($"异常: {ex}");
                    Environment.Exit(14);
                }
            };

            timer.Start();
        };

        window.Show();
    }
}
