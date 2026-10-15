using System;
using System.IO;
using System.Linq;
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
/// 批改网链路端到端测试（<c>--pigaitest &lt;整条cookie&gt; [秒数]</c>）。
/// <para>
/// 用一条真实的批改网会话 cookie 驱动应用自身的路径：
/// 「建立会话 → 关闭登录覆盖层 → 触发同步 → 待办列表出现未完成作文」。
/// 这一段是「登录后不自动返回、待办里没有作文」最可能的故障区，且**完全不需要
/// 账号密码**——cookie 由调用方在命令行给出，只进内存，不落盘、不写日志。
/// </para>
/// <para>
/// 登录页本身的捕获逻辑（轮询 <c>_JKU</c>）由 PigaiLoginView 的轮询实现保证，
/// 那边无法在无人值守下模拟「人手动输密码」，因此这里只验证会话之后的下游链路。
/// </para>
/// </summary>
internal static class PigaiFlowTest
{
    /// <param name="cookieHeader">模式一：直接给一条会话 cookie，验证下游链路。</param>
    /// <param name="credential">模式二：给账号密码，驱动登录页验证捕获链路。</param>
    public static void Run(string cookieHeader, (string User, string Password)? credential, TimeSpan budget)
    {
        App.DeferInitialLoad = true;
        App.WebViewTestBudget = budget;
        App.PigaiTestHook = (desktop, b) => RunInWindow(desktop, b, cookieHeader, credential);
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(["--pigaitest-runner"]);
    }

    private static void RunInWindow(
        Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop,
        TimeSpan budget,
        string cookieHeader,
        (string User, string Password)? credential)
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        desktop.MainWindow = window;

        var logPath = Path.Combine(AppPaths.DataDirectory, "pigaitest.log");
        try { File.WriteAllText(logPath, string.Empty); } catch { /* 日志失败不影响测试 */ }

        void Note(string s)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {s}";
            Console.WriteLine("  " + line);
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { /* 同上 */ }
        }

        window.Opened += (_, _) =>
        {
            if (credential is null)
                _ = RunAsync(vm, Note, cookieHeader, budget);
            else
                _ = DriveLoginAsync(vm, window, Note, credential.Value.User, credential.Value.Password, budget);
        };

        window.Show();
    }

    /// <summary>
    /// 模式二：真实驱动登录页（无人值守下用脚本填表并提交）。
    /// 用来验证「用户在登录页登录后能否被自动捕获并返回」。
    /// </summary>
    private static async Task DriveLoginAsync(
        MainViewModel vm, Window window, Action<string> note, string user, string password, TimeSpan budget)
    {
        vm.OpenAccountSettings();
        vm.OpenPigaiLogin();
        note($"已打开登录覆盖层：PigaiLoginOpen={vm.PigaiLoginOpen}");

        var waitedForm = TimeSpan.Zero;
        var submitted = false;

        // 1) 等表单出现（每 2 秒记一次真实 DOM 概况，便于区分「没加载」与「选择器不对」）。
        while (!submitted && DateTimeOffset.Now < DateTimeOffset.Now + TimeSpan.FromSeconds(90))
        {
            await Task.Delay(2000);
            waitedForm += TimeSpan.FromSeconds(2);

            var webView = window.GetVisualDescendants().OfType<PigaiLoginView>().FirstOrDefault()
                ?.GetVisualDescendants().OfType<NativeWebView>().FirstOrDefault();
            if (webView?.AdapterInfo is null)
            {
                note($"{waitedForm.TotalSeconds:F0}s: 适配器未就绪");
                continue;
            }

            string? dom = null;
            try
            {
                dom = await webView.InvokeScript(
                    "(function(){return [location.href,'inputs='+document.querySelectorAll('input').length," +
                    "'forms='+document.querySelectorAll('form').length," +
                    "'user='+(document.querySelector('#username')?'Y':'N')," +
                    "'pass='+(document.querySelector('#password')?'Y':'N')," +
                    "'title='+document.title].join(' | ');})()");
            }
            catch (Exception ex)
            {
                note($"{waitedForm.TotalSeconds:F0}s: 探测异常 {ex.GetType().Name}");
                continue;
            }

            note($"{waitedForm.TotalSeconds:F0}s: {dom}");
            if (dom is null || !dom.Contains("user=Y") || !dom.Contains("pass=Y")) continue;

            // 2) 填表并提交（password 不进日志）。
            note("表单就绪，填表并提交");
            try
            {
                var r = await webView.InvokeScript(
                    "(function(){var u=document.querySelector('#username');var p=document.querySelector('#password');" +
                    "if(!u||!p)return 'nofield';u.value=" + JsStr(user) + ";p.value=" + JsStr(password) + ";" +
                    "var c=document.querySelector('#rmb_c');if(c)c.checked=true;" +
                    "var f=document.querySelector('#lg_from_w')||u.form;if(!f)return 'noform';" +
                    "f.submit();return 'submitted';})()");
                note($"提交结果={r}");
                submitted = true;
            }
            catch (Exception ex)
            {
                note($"提交异常 {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (!submitted)
        {
            note("结论：90s 内未能提交登录表单 ✗");
            Environment.Exit(14);
        }

        // 3) 等应用自己捕获并接管（这里验证的正是被修的轮询逻辑）。
        var deadline = DateTimeOffset.Now + budget;
        var last = string.Empty;
        while (DateTimeOffset.Now < deadline)
        {
            await Task.Delay(1000);
            var now = $"open={vm.PigaiLoginOpen} has={vm.HasPigaiSession} items={vm.List.Items.Count}";
            if (now != last) { note(now); last = now; }
            if (!vm.PigaiLoginOpen && vm.HasPigaiSession) break;
        }

        Report(vm, note, File.Exists(new PigaiSessionStore().Location));
    }

    private static async Task RunAsync(MainViewModel vm, Action<string> note, string cookie, TimeSpan budget)
    {
        try
        {
            // 1) 模拟「登录页捕获成功后接管」这一步 —— 与 PigaiLoginView 走同一入口。
            note($"注入会话（{cookie.Length} 字符），调用 AdoptPigaiSessionAsync");
            var session = new PigaiSessionFile
            {
                CookieHeader = cookie,
                Account = ExtractAccount(cookie),
                SavedAt = DateTimeOffset.Now,
            };

            vm.OpenAccountSettings();
            vm.OpenPigaiLogin();
            note($"接管前：覆盖层={vm.PigaiLoginOpen}");

            var adopt = vm.AdoptPigaiSessionAsync(session);
            await adopt;
            note($"接管后：覆盖层={vm.PigaiLoginOpen} HasSession={vm.HasPigaiSession} " +
                 $"状态={vm.PigaiStatusText} 同步状态={vm.SyncStatus}");

            // 2) 再等同步收尾（同步由 Adopt 内部触发，可能还在跑）。
            var deadline = DateTimeOffset.Now + budget;
            while (DateTimeOffset.Now < deadline && vm.List.Items.Count == 0)
                await Task.Delay(1000);

            // 3) 会话文件是否真的落盘（下次启动要能恢复）。
            var storePath = new PigaiSessionStore().Location;
            note($"会话文件存在={File.Exists(storePath)} 路径={storePath}");

            Report(vm, note, File.Exists(storePath));
        }
        catch (Exception ex)
        {
            note($"异常 {ex.GetType().Name}: {ex.Message}");
            Environment.Exit(15);
        }
    }

    private static string? ExtractAccount(string cookie)
    {
        foreach (var part in cookie.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim() == "_JKU") return kv[1].Trim();
        }
        return null;
    }

    private static void Report(MainViewModel vm, Action<string> note, bool sessionSaved)
    {
        vm.SelectedNav = vm.NavItems.First(n => n.Key == "homework");
        vm.OnNavChanged();

        var pigaiItems = vm.List.Items
            .Where(i => i.Id.StartsWith("hw-pg-", StringComparison.Ordinal)).ToList();

        Console.WriteLine();
        Console.WriteLine("=== 批改网下游链路测试结果 ===");
        Console.WriteLine($"  覆盖层已关闭     : {!vm.PigaiLoginOpen}");
        Console.WriteLine($"  已建立会话       : {vm.HasPigaiSession}");
        Console.WriteLine($"  会话已落盘       : {sessionSaved}");
        Console.WriteLine($"  状态文字         : {vm.PigaiStatusText}");
        Console.WriteLine($"  同步状态         : {vm.SyncStatus}");
        Console.WriteLine($"  待办总条目       : {vm.List.Items.Count}");
        Console.WriteLine($"  其中批改网条目   : {pigaiItems.Count}");
        foreach (var it in pigaiItems.Take(8))
            Console.WriteLine($"      · {it.DisplaySubject} / {it.DisplayTitle} / 截止 {it.DueText}");

        var ok = !vm.PigaiLoginOpen && vm.HasPigaiSession && sessionSaved && pigaiItems.Count > 0;

        Console.WriteLine();
        Console.WriteLine($"  自动返回（关覆盖层）: {(!vm.PigaiLoginOpen ? "是 ✓" : "否 ✗")}");
        Console.WriteLine($"  待办出现批改网条    : {(pigaiItems.Count > 0 ? "是 ✓" : "否 ✗")}");
        Console.WriteLine($"  会话可跨启动恢复    : {(sessionSaved ? "是 ✓" : "否 ✗")}");
        Console.WriteLine(ok ? "  结论：下游链路全部正确 ✓" : "  结论：存在缺陷 ✗");

        note($"结果：ok={ok} pigaiItems={pigaiItems.Count} 同步={vm.SyncStatus}");
        Environment.Exit(ok ? 0 : 13);
    }

    /// <summary>把字符串安全地嵌进 JS 字面量（只转义，不做任何拼接信任）。</summary>
    private static string JsStr(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
}
