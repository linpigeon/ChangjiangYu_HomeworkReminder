using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace HomeworkReminder.Services;

/// <summary>
/// 批改网（pigai.org）的 WebView 登录：加载站点首页（账号密码表单就在首页上），
/// 登录成功后从 Cookie 捕获会话。
/// <para>
/// 与 <see cref="YktLoginService"/> / <see cref="ChaoxingLoginService"/> 同款结构：
/// 桌面走 Avalonia 的 CookieManager 抽象，Android 走平台注入的原生读取。
/// </para>
/// <para>
/// 之所以不自己 POST 账号密码：那样就得在应用里保存明文密码。走官网页面的好处是
/// 账号密码只经过批改网自己的表单，应用只拿到会话 Cookie（且用 DPAPI 加密落盘），
/// 顺带还能用首页上的第三方登录入口。
/// </para>
/// </summary>
public static class PigaiLoginService
{
    /// <summary>
    /// 登录入口。直接用首页——批改网的账号密码表单就渲染在首页上
    /// （<c>POST /index.php?a=login</c>，字段 <c>username</c> / <c>password</c> / <c>remember</c>）。
    /// <c>?a=loginForm</c> 未登录时会 302 回首页，没必要单独用它。
    /// </summary>
    public const string LoginUrl = "https://www.pigai.org/";

    /// <summary>未完成作文列表页，登录成功后导航过去以便立刻看到内容。</summary>
    public const string WaitCompleteUrl = "https://www.pigai.org/index.php?c=write&a=waitComplete&bf=4";

    /// <summary>Android 注入的原生 Cookie 读取（返回 pigai.org 域的整条 Cookie）。</summary>
    public static Func<Task<string?>>? CookieReaderOverride { get; set; }

    /// <summary>Android 注入的原生 Cookie 清理。</summary>
    public static Func<Task>? CookieClearOverride { get; set; }

    private static bool IsPigaiDomain(string domain) =>
        domain.EndsWith("pigai.org", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 在页面上下文里探测登录态，返回 <c>'in'</c> / <c>'out'</c>（尚未拿到结果时返回空串）。
    /// <para>
    /// <b>脚本必须同步返回，不能返回 Promise。</b>与 <see cref="YktLoginService.CourseProbeScript"/>
    /// 同一个坑：WebView2 的 ExecuteScript 不会等待 Promise，而是把「等待中的 Promise 对象」
    /// 序列化成 <c>{}</c>。所以脚本只负责发起 fetch 并把结果写进
    /// <c>window.__hrPigaiLogin</c>，本次同步读出上一次的结果，下一次轮询再取新的。
    /// </para>
    /// <para>
    /// 探测目标是<b>必须登录才能访问的页面</b>（未完成作文列表）。批改网未登录时该页
    /// 返回 200 但渲染「请先登录」，所以判定必须看<b>响应正文</b>而不是状态码——
    /// 这一点与「受限页面也返回 200」的实测结论一致。
    /// </para>
    /// </summary>
    public const string LoginProbeScript =
        "(()=>{" +
        "if(window.__hrPigaiLogin===undefined){window.__hrPigaiLogin='';" +
        "fetch('/index.php?c=write&a=waitComplete&bf=4',{credentials:'include'})" +
        ".then(r=>r.text())" +
        ".then(t=>{window.__hrPigaiLogin=(t.indexOf('请先登录')>=0)?'out':'in';})" +
        ".catch(()=>{window.__hrPigaiLogin='';});}" +
        "return window.__hrPigaiLogin;})()";

    /// <summary>单次 InvokeScript 的超时；页面跳转或 WebView 未就绪时它可能长时间不返回。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 在 UI 线程上探测一次登录态（与 <see cref="YktLoginService.TryProbeAsync"/> 同款）。
    /// <para>
    /// 必须是 async 且正常 await：WebView2 执行脚本的结果通过窗口消息回来，
    /// 只有让 UI 线程继续泵消息才会完成。外层再套超时：WebView 未就绪或页面正在跳转时
    /// <c>InvokeScript</c> 可能长时间不返回，不能让轮询被它拖住。
    /// </para>
    /// </summary>
    /// <returns>拿到状态时返回 "in"/"out"；超时或未就绪返回 null。</returns>
    public static async Task<string?> TryProbeAsync(NativeWebView webView, CancellationToken ct = default)
    {
        var script = InvokeScriptAsync(webView);

        var finished = await Task.WhenAny(script, Task.Delay(ProbeTimeout, ct)).ConfigureAwait(true);
        if (!ReferenceEquals(finished, script)) return null;

        var raw = await script.ConfigureAwait(true);
        var code = raw?.Trim().Trim('"');
        return string.IsNullOrEmpty(code) ? null : code;
    }

    private static async Task<string?> InvokeScriptAsync(NativeWebView webView)
    {
        try
        {
            return await webView.InvokeScript(LoginProbeScript).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 页面正在跳转或适配器尚未就绪。
            return null;
        }
    }

    /// <summary>
    /// 在给定 WebView 上等待登录完成（严格照搬 <see cref="YktLoginService.WaitForLoginAsync"/> 的形状）。
    /// <para>
    /// <b>必须从 UI 线程调用，且本方法刻意不在 await 上用 ConfigureAwait(false)。</b>
    /// <see cref="NativeWebView"/> 是 COM/STA 控件，所有成员只能在 UI 线程访问；
    /// 续体一旦被切到线程池，<c>InvokeScript</c> 永远不会返回，表现为
    /// 「页面已经登录了，但状态一直不动」。
    /// </para>
    /// <para>
    /// 与雨课堂的差别只有判定方式：雨课堂看接口状态码 200，这里看探测脚本的
    /// <c>'in'/'out'</c>（因为批改网受限页未登录时也返回 200）。
    /// </para>
    /// </summary>
    /// <param name="webView">已经显示在界面上的 WebView。</param>
    /// <param name="pollInterval">页面内探测间隔。</param>
    /// <param name="timeout">整体超时。</param>
    public static async Task<PigaiSessionFile?> WaitForLoginAsync(
        NativeWebView webView,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(2);
        var limit = timeout ?? TimeSpan.FromMinutes(10);
        var deadline = DateTimeOffset.Now + limit;

        while (DateTimeOffset.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            // 回到 UI 线程再碰 WebView。
            await Task.Delay(interval, ct).ConfigureAwait(true);

            var status = await TryProbeAsync(webView, ct).ConfigureAwait(true);
            Log($"probe: {(status ?? "(null)")}");
            if (status is null)
            {
                // 页面正在跳转、适配器尚未就绪，或这次探测超时——等下一轮。
                continue;
            }

            if (status != "in") continue;

            // 探测已确认登录**真的生效**（受限页面能正常访问），这才是权威依据。
            // 这里不再用「cookie 里必须有某个名字」去否决它：
            // 曾要求必须有 _JKU，而真机登录后 cookie 里未必有这个项，
            // 结果是探测说已登录、代码却一路 continue —— 会话永远不落盘，
            // 重启后显示未登录、待办里也没有作文，且完全静默。
            // 现在只要求「读到了 pigai 域的 cookie」，具体哪些 cookie 由服务端决定。
            var cookieHeader = await ReadCookieHeaderAsync(webView).ConfigureAwait(true);
            var session = BuildSessionFromVerifiedLogin(cookieHeader, webView);
            if (session is not null) return session;

            // 连一个 cookie 都没读到：可能是探测的误判（例如页面还没真正带上会话），继续等。
            Log("probe: 探测判定已登录，但没有读到任何 pigai cookie");
        }

        Log($"probe: 等待登录超时（{(int)limit.TotalMinutes} 分钟）");
        return null;
    }

    /// <summary>
    /// 由整条 Cookie 构造会话，<b>不再要求特定 cookie 名</b>。
    /// 登录是否成立由调用方（页面探测 / API 请求）判定，这里只负责「把会话存下来」。
    /// </summary>
    /// <param name="cookieHeader">pigai.org 域的整条 cookie；为空则返回 null。</param>
    /// <param name="webView">用于记录当前页面地址（仅在开启诊断日志时使用，可为 null）。</param>
    /// <remarks>internal 供离线自检（<c>--jsoncheck</c>）验证判定规则，不是另写一份逻辑。</remarks>
    internal static PigaiSessionFile? BuildSessionFromVerifiedLogin(
        string? cookieHeader, NativeWebView? webView = null)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            Log("session: pigai 域内没有任何 cookie");
            return null;
        }

        Log($"session: 采用探测确认过的登录态，cookie 项={DescribeCookies(cookieHeader)}" +
            (webView is null ? string.Empty : $"，页面={webView.Source}"));

        return new PigaiSessionFile
        {
            CookieHeader = cookieHeader,
            // 仅用于界面显示；_JKU 在就用它，否则退回页面 URL / 占位。
            Account = ExtractAccount(cookieHeader)
                      ?? webView?.Source?.Host
                      ?? "批改网",
            SavedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>从 cookie 里取 _JKU 作为账号显示名（可能没有，不算错误）。</summary>
    private static string? ExtractAccount(string cookieHeader)
    {
        foreach (var part in cookieHeader.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim() == "_JKU")
            {
                var v = kv[1].Trim();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        return null;
    }

    /// <summary>只列出 cookie 的**名字**（不记值），供诊断日志使用。</summary>
    private static string DescribeCookies(string cookieHeader) =>
        string.Join(",", cookieHeader.Split(';')
            .Select(p => p.Split('=', 2)[0].Trim())
            .Where(n => n.Length > 0));

    /// <summary>
    /// 尝试从 WebView 捕获批改网会话。
    /// <para>
    /// <b>不再用「cookie 里必须有某个名字」来判定是否已登录</b>：
    /// 登录是否成立由页面探测（<see cref="WaitForLoginAsync"/>）或后续 API 请求判定，
    /// 这里只负责把读到的会话交出去。曾经要求必须有 <c>_JKU</c>，
    /// 而真机登录后 cookie 里未必有该项，结果是探测确认已登录、代码却一路跳过——
    /// 会话永不落盘，重启后显示未登录、待办里也没有作文。
    /// </para>
    /// </summary>
    public static async Task<PigaiSessionFile?> CaptureAsync(NativeWebView webView)
    {
        if (CookieReaderOverride is not null)
        {
            var overridden = await CookieReaderOverride().ConfigureAwait(true);
            Log($"cookie(override): {(overridden is null ? "null" : overridden.Length + " chars")}");
            return BuildSessionFromVerifiedLogin(overridden, webView);
        }

        return await ReadSessionAsync(webView).ConfigureAwait(true);
    }

    /// <summary>
    /// 读会话并交给调用方判定（Android 用平台注入通道的版本）。
    /// 只要求「读到了 pigai 域的 cookie」；有效性由调用方的探测或 API 请求负责。
    /// </summary>
    public static async Task<PigaiSessionFile?> ReadSessionAsync(NativeWebView webView)
    {
        var cookieHeader = await ReadCookieHeaderAsync(webView).ConfigureAwait(true);
        return BuildSessionFromVerifiedLogin(cookieHeader, webView);
    }

    /// <summary>
    /// 诊断开关（环境变量 <c>HWREMINDER_PIGAI_DEBUG=1</c>）：把每次导航、Cookie 读取结果
    /// 与捕获判定写进数据目录的 <c>pigai-login.log</c>。
    /// <para>
    /// 存在的理由：登录捕获依赖 WebView2 的 CookieManager，而「Cookie 到底有没有、
    /// 域是什么、导航是否触发」在界面上完全看不出来——出问题时只能靠这个日志定位。
    /// 只记录 Cookie 的**名字**与总长度，不记录值。
    /// </para>
    /// </summary>
    public static bool DebugLoggingEnabled { get; set; } =
        Environment.GetEnvironmentVariable("HWREMINDER_PIGAI_DEBUG") == "1";

    internal static void Log(string message)
    {
        if (!DebugLoggingEnabled) return;
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppPaths.DataDirectory, "pigai-login.log"),
                $"{DateTimeOffset.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 日志失败不能影响登录。
        }
    }

    /// <summary>
    /// 从 WebView 读出 pigai.org 域的整条 Cookie。
    /// <para>
    /// 只负责「读」；是否算已登录交给调用方的探测结果判定
    /// （见 <see cref="WaitForLoginAsync"/> 与 <see cref="BuildSessionFromVerifiedLogin"/>）。
    /// 与 <see cref="YktLoginService.ReadSessionAsync"/> / <see cref="ChaoxingLoginService.CaptureAsync"/>
    /// 的分层保持一致。
    /// </para>
    /// </summary>
    private static async Task<string?> ReadCookieHeaderAsync(NativeWebView webView)
    {
        if (CookieReaderOverride is not null)
        {
            var overridden = await CookieReaderOverride().ConfigureAwait(true);
            Log($"cookie(override): {(overridden is null ? "null" : overridden.Length + " chars")}");
            return overridden;
        }

        var manager = webView.TryGetCookieManager();
        if (manager is null)
        {
            // 适配器尚未就绪时这里就是 null：必须记下来，
            // 否则会表现成「登录了却什么都没发生」。
            Log("cookie: TryGetCookieManager() == null（适配器未就绪？）");
            return null;
        }

        var cookies = await manager.GetCookiesAsync().ConfigureAwait(true);
        if (cookies is null)
        {
            Log("cookie: GetCookiesAsync() == null");
            return null;
        }

        var all = cookies.Cast<Cookie>().ToList();
        Log($"cookie: 共 {all.Count} 个；pigai 域内: " +
            string.Join(",", all.Where(c => IsPigaiDomain(c.Domain)).Select(c => $"{c.Name}@{c.Domain}")));

        return string.Join("; ", all
            .Where(c => IsPigaiDomain(c.Domain))
            .Select(c => $"{c.Name}={c.Value}"));
    }

    /// <summary>清掉 WebView 里 pigai.org 域的 Cookie（退出登录）。</summary>
    public static async Task ClearCookiesAsync(NativeWebView webView)
    {
        if (CookieClearOverride is not null)
        {
            await CookieClearOverride().ConfigureAwait(true);
            return;
        }

        try
        {
            var manager = webView.TryGetCookieManager();
            if (manager is null) return;
            var cookies = await manager.GetCookiesAsync().ConfigureAwait(true);
            if (cookies is null) return;
            foreach (var c in cookies.Cast<Cookie>().Where(c => IsPigaiDomain(c.Domain)).ToList())
            {
                try { manager.DeleteCookie(c); }
                catch (Exception) { /* 单个删除失败继续删其它的 */ }
            }
        }
        catch (Exception)
        {
            // 清理失败不影响本地会话文件的清除。
        }
    }
}
