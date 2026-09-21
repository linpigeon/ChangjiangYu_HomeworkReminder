using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace HomeworkReminder.Services;

/// <summary>扫码登录的进展。</summary>
public enum LoginPhase
{
    Idle,
    LoadingPage,
    WaitingForScan,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record LoginStatus(LoginPhase Phase, string Message);

/// <summary>
/// 通过内嵌 WebView 完成微信扫码登录。
/// <para>
/// 做法：打开雨课堂的 iframe 登录页。该页面会把
/// <c>/authorize/wx-qrlogin</c>（独立的二维码登录 SPA）嵌进 416×510 的 iframe，
/// 扫码成功后通过 <c>postMessage("login_success")</c> 通知外层并刷新页面。
/// 我们不去复刻它的协议，只在页面上下文里周期性探测课程接口：
/// 返回 200 即说明 sessionid 已下发，此时从 WebView 的 cookie 管理器取出凭据。
/// 之所以不在第一帧就抓 cookie，是因为扫码中间态也会写 cookie，
/// 只有接口 200 才代表登录真正完成。
/// </para>
/// </summary>
public sealed class YktLoginService
{
    private const string LoginUrl = "https://changjiang.yuketang.cn/web?ykt_ai_login";

    /// <summary>
    /// 平台钩子：Avalonia 的 CookieManager 抽象在 Android 上不可用（返回 null），
    /// Android 头会注入原生 CookieManager 的读取实现。
    /// </summary>
    public static Func<Task<YktSession?>>? SessionReaderOverride { get; set; }

    /// <summary>平台钩子：同上，退出登录时的 Cookie 清除。</summary>
    public static Func<Task<CookieClearResult>>? CookieClearOverride { get; set; }

    /// <summary>
    /// 在页面上下文里探测登录态，返回 HTTP 状态码字符串（尚未拿到时返回空串）。
    /// <para>
    /// <b>脚本必须同步返回，不能返回 Promise。</b>实测结论（同一页面内对照）：
    /// </para>
    /// <code>
    /// 'abc'                          -> "abc"    ✓
    /// String(200)                    -> "200"    ✓
    /// (async()=>{return 'xyz'})()    -> {}       ✗
    /// (async()=>{return String(200)})() -> {}    ✗
    /// </code>
    /// <para>
    /// WebView2 的 ExecuteScriptAsync <b>不会等待 Promise</b>，而是把「等待中的 Promise 对象」
    /// 直接 JSON 序列化成 <c>{}</c>。所以这里改成：脚本只负责发起 fetch 并把结果写到
    /// <c>window.__hrLoginStatus</c>，然后同步把这个变量读出来返回；
    /// 下一次轮询再取上一次的结果。这样即使扫码成功后也一定能读到 200。
    /// </para>
    /// </summary>
    public const string CourseProbeScript =
        "(()=>{" +
        "if(window.__hrLoginStatus===undefined){window.__hrLoginStatus='';" +
        "fetch('/v2/api/web/courses/list?identity=2',{credentials:'include',headers:{xtbz:'ykt'}})" +
        ".then(r=>{window.__hrLoginStatus=String(r.status);})" +
        ".catch(()=>{window.__hrLoginStatus='0';});}" +
        "return window.__hrLoginStatus;})()";

    /// <summary>登录页地址，供自检与测试使用。</summary>
    public static string LoginPageUrl => LoginUrl;

    private readonly YktSessionOptions _options;

    public YktLoginService(YktSessionOptions? options = null) => _options = options ?? new YktSessionOptions();

    /// <summary>
    /// 在给定 WebView 上等待扫码完成。
    /// <para>
    /// <b>必须从 UI 线程调用，并且本方法刻意不在 <c>await</c> 上用
    /// <c>ConfigureAwait(false)</c>。</b>
    /// <see cref="NativeWebView"/> 是 COM/STA 控件，所有成员都只能在 UI 线程上访问；
    /// 一旦续体被切到线程池线程上，<c>InvokeScript</c> 将永远不会返回，
    /// 表现为「页面已经登录成功了，但状态一直停在『正在打开登录页』」。
    /// 用 <c>ConfigureAwait(true)</c>（默认值）让循环始终回到 UI 线程。
    /// </para>
    /// </summary>
    /// <param name="webView">已经显示在界面上的 WebView（调用方负责布局与可见性）。</param>
    /// <param name="progress">登录进展回调。</param>
    /// <param name="pollInterval">页面内探测间隔。</param>
    /// <param name="timeout">整体超时。</param>
    /// <param name="onProbe">
    /// 每次探测拿到状态码后回调，返回 true 表示调用方已经处理（例如发现已有登录态并接管），
    /// 循环应立即结束。用于「点按钮立刻检测」时跳过等待间隔。
    /// </param>
    public async Task<YktSession?> WaitForLoginAsync(
        NativeWebView webView,
        IProgress<LoginStatus>? progress = null,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        Func<string, Task<bool>>? onProbe = null,
        CancellationToken ct = default)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(2.5);
        var limit = timeout ?? TimeSpan.FromMinutes(5);
        var deadline = DateTimeOffset.Now + limit;

        progress?.Report(new LoginStatus(LoginPhase.LoadingPage, "正在打开长江雨课堂登录页…"));
        try
        {
            webView.Navigate(new Uri(LoginUrl));
        }
        catch (Exception ex)
        {
            progress?.Report(new LoginStatus(LoginPhase.Failed, $"无法打开登录页：{ex.Message}"));
            return null;
        }

        var reported = LoginPhase.LoadingPage;
        var probes = 0;

        while (DateTimeOffset.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            // 回到 UI 线程再碰 WebView。
            await Task.Delay(interval, ct).ConfigureAwait(true);

            var status = await TryProbeAsync(webView, ct).ConfigureAwait(true);
            if (status is null)
            {
                // 页面正在跳转、适配器尚未就绪，或这次探测超时——等下一轮。
                continue;
            }

            probes++;

            if (status == "200")
            {
                // ReadSessionAsync 同样只能在 UI 线程上调用。
                var session = await ReadSessionAsync(webView, _options.UniversityId, _options.Term)
                    .ConfigureAwait(true);
                if (session is null)
                {
                    progress?.Report(new LoginStatus(LoginPhase.Failed,
                        "登录已成功，但没有读到 sessionid cookie，请重试。"));
                    return null;
                }

                progress?.Report(new LoginStatus(LoginPhase.Succeeded, "登录成功，正在读取课程…"));
                return session;
            }

            // 非 200：给调用方一个「立即处理」的机会（用于点按钮时跳过等待间隔）。
            // 返回 true 表示调用方已接管，循环结束。
            if (onProbe is not null && await onProbe(status).ConfigureAwait(true))
            {
                return null;
            }

            if (reported != LoginPhase.WaitingForScan)
            {
                reported = LoginPhase.WaitingForScan;
                // Android 上登录页已自动切到账号表单（手机就是微信本体，扫不了自己的码）。
                progress?.Report(new LoginStatus(LoginPhase.WaitingForScan,
                    OperatingSystem.IsAndroid()
                        ? "请在上方页面登录（手机号/短信/邮箱均可）…"
                        : "请用微信扫描上方二维码完成登录…"));
            }
            else if (probes % 12 == 0)
            {
                // 让等待过程可见，否则界面看起来像卡死了。
                var left = Math.Max(0, (int)(deadline - DateTimeOffset.Now).TotalMinutes);
                progress?.Report(new LoginStatus(LoginPhase.WaitingForScan,
                    OperatingSystem.IsAndroid()
                        ? $"仍在等待登录…（剩余约 {left} 分钟）"
                        : $"仍在等待扫码…（剩余约 {left} 分钟，当前页面返回 {status}）"));
            }
        }

        progress?.Report(new LoginStatus(LoginPhase.Failed,
            $"等待登录超时（{(int)limit.TotalMinutes} 分钟），请重试。"));
        return null;
    }

    /// <summary>单次 InvokeScript 的超时；WebView 未就绪时它可能长时间不返回。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 在 UI 线程上探测一次登录状态。
    /// <para>
    /// 必须是 async 且正常 await：WebView2 执行脚本的结果通过窗口消息回来，
    /// 只有让 UI 线程继续泵消息才会完成。若在这里同步阻塞（哪怕一边 RunJobs），
    /// 回调就递不进来，表现为永不返回。
    /// </para>
    /// <para>
    /// 外层再套一层超时：WebView 未就绪或页面正在跳转时，
    /// <c>InvokeScript</c> 可能长时间不返回，不能让轮询被它拖住。
    /// </para>
    /// </summary>
    /// <returns>拿到状态码时返回该字符串；超时或未就绪时返回 null。</returns>
    public static async Task<string?> TryProbeAsync(NativeWebView webView, CancellationToken ct = default)
    {
        var script = InvokeScriptAsync(webView);

        var finished = await Task.WhenAny(script, Task.Delay(ProbeTimeout, ct)).ConfigureAwait(true);
        if (!ReferenceEquals(finished, script)) return null;

        try
        {
            var raw = await script.ConfigureAwait(true);
            var code = raw?.Trim().Trim('"');
            // 空串表示「脚本刚发起请求，结果还没回来」，交给下一次轮询。
            return string.IsNullOrEmpty(code) ? null : code;
        }
        catch (Exception)
        {
            // 页面正在跳转或适配器尚未就绪。
            return null;
        }
    }

    /// <summary>调用 WebView 执行脚本，异常统一转成 null。</summary>
    private static async Task<string?> InvokeScriptAsync(NativeWebView webView)
    {
        try
        {
            return await webView.InvokeScript(CourseProbeScript).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>清除 WebView 内雨课堂域 Cookie 的结果。</summary>
    public sealed record CookieClearResult(int Found, int Remaining, int Deleted, string? Error)
    {
        /// <summary>雨课堂域的 Cookie 是否已全部清除。</summary>
        public bool Ok => Remaining == 0;
    }

    /// <summary>
    /// 清除 WebView 内雨课堂域的 Cookie，并把页面重新导航到登录页。
    /// <para>
    /// 退出登录必须调用它：WebView 有<b>自己的持久化 Cookie 存储</b>，与应用保存的会话文件
    /// 是两套。只删会话文件的话，WebView 里其实还处于登录状态——界面会继续显示课程列表，
    /// 而且下次启动的自动检测会把这个会话又接管回来，等于退出登录没生效。
    /// </para>
    /// <para>
    /// 删除后再重新导航一次：即使个别 Cookie 没删干净，页面也会重新走鉴权，
    /// 同时避免用户看到上一段登录遗留的课程列表。
    /// </para>
    /// </summary>
    public static async Task<CookieClearResult> ClearCookiesAsync(NativeWebView webView)
    {
        if (CookieClearOverride is not null)
            return await CookieClearOverride().ConfigureAwait(true);

        try
        {
            var manager = webView.TryGetCookieManager();
            if (manager is null) return new CookieClearResult(0, 0, 0, "CookieManager 不可用（WebView 未就绪？）");

            var cookies = await manager.GetCookiesAsync().ConfigureAwait(true);
            if (cookies is null) return new CookieClearResult(0, 0, 0, "GetCookiesAsync 返回 null");

            var targets = cookies.Cast<Cookie>()
                .Where(c => c.Domain.EndsWith("yuketang.cn", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var deleted = 0;
            string? lastError = null;

            foreach (var c in targets)
            {
                try
                {
                    manager.DeleteCookie(c);
                    deleted++;
                }
                catch (Exception ex)
                {
                    lastError = $"{c.Name}@{c.Domain}: {ex.GetType().Name} {ex.Message}";
                }
            }

            // 复核：真的没了吗？剩下的一律再删一遍。
            var remaining = await ReadYktCookiesAsync(manager).ConfigureAwait(true);
            foreach (var c in remaining)
            {
                try
                {
                    manager.DeleteCookie(c);
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            if (remaining.Count > 0)
            {
                remaining = await ReadYktCookiesAsync(manager).ConfigureAwait(true);
            }

            // 重新导航：丢掉旧页面内容并重新走鉴权。
            try { webView.Navigate(new Uri(LoginUrl)); } catch (Exception) { /* 导航失败不影响清理结果 */ }

            return new CookieClearResult(targets.Count, remaining.Count, deleted, lastError);
        }
        catch (Exception ex)
        {
            return new CookieClearResult(0, 0, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>读出当前 WebView 里雨课堂域的 Cookie。</summary>
    public static async Task<List<Cookie>> ReadYktCookiesAsync(NativeWebViewCookieManager manager)
    {
        var cookies = await manager.GetCookiesAsync().ConfigureAwait(true);
        return cookies?.Cast<Cookie>()
            .Where(c => c.Domain.EndsWith("yuketang.cn", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
    }

    /// <summary>
    /// 从 WebView 的 cookie 管理器读取 sessionid / csrftoken。
    /// </summary>
    /// <param name="universityId">高校 id；传 0（默认）表示取 <see cref="AppSettings.Current"/>。</param>
    /// <param name="term">学期标识；传 0（默认）表示取 <see cref="AppSettings.Current"/>。</param>
    public static async Task<YktSession?> ReadSessionAsync(
        NativeWebView webView,
        int universityId = 0,
        int term = 0)
    {
        if (universityId == 0) universityId = AppSettings.Current.UniversityId;
        if (term == 0) term = AppSettings.Current.Term;

        // Android：Avalonia 的 CookieManager 抽象不可用，走平台注入的原生读取。
        if (SessionReaderOverride is not null)
            return await SessionReaderOverride().ConfigureAwait(true);

        var manager = webView.TryGetCookieManager();
        if (manager is null) return null;

        // 与本文件头注释的规则一致：续体必须回到 UI 线程（WebView 是 COM/STA 控件）。
        var cookies = await manager.GetCookiesAsync().ConfigureAwait(true);
        if (cookies is null) return null;

        string? sessionId = null, csrf = null, loginType = null;
        DateTimeOffset? expires = null;

        foreach (Cookie c in cookies)
        {
            if (!c.Domain.EndsWith("yuketang.cn", StringComparison.OrdinalIgnoreCase)) continue;
            switch (c.Name)
            {
                case "sessionid":
                    sessionId = c.Value;
                    if (c.Expires != DateTime.MinValue)
                        expires = new DateTimeOffset(c.Expires.ToUniversalTime());
                    break;
                case "csrftoken":
                    csrf = c.Value;
                    break;
                case "login_type":
                    loginType = c.Value;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        return new YktSession
        {
            SessionId = sessionId,
            CsrfToken = csrf,
            LoginType = loginType,
            ExpiresAt = expires,
            UniversityId = universityId,
            Term = term,
        };
    }
}

/// <summary>
/// 登录/学期相关设置。默认值取自 <see cref="AppSettings.Current"/>（settings.json），
/// 调用方仍可显式覆盖（例如沿用已有会话里的学校/学期）。
/// </summary>
public sealed class YktSessionOptions
{
    public int UniversityId { get; init; } = AppSettings.Current.UniversityId;
    public int Term { get; init; } = AppSettings.Current.Term;
}
