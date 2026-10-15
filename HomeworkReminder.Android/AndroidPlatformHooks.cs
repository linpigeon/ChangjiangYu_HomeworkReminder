using System.Threading.Tasks;
using HomeworkReminder.Services;

namespace HomeworkReminder.Android;

/// <summary>
/// Android 平台钩子：Avalonia 的 WebView CookieManager 抽象在 Android 上不可用
/// （TryGetCookieManager 返回 null），改用原生 Android.Webkit.CookieManager——
/// 进程内所有 WebView 共享同一个 Cookie 存储，登录页写进的 sessionid 就在这里。
/// </summary>
internal static class AndroidPlatformHooks
{
    private const string YktUrl = "https://changjiang.yuketang.cn";

    public static void Register()
    {
        YktLoginService.SessionReaderOverride = ReadSession;
        YktLoginService.CookieClearOverride = ClearCookies;

        // 学习通：Avalonia 的 CookieManager 抽象在 Android 不可用，走原生 CookieManager。
        ChaoxingLoginService.CookieReaderOverride = ReadChaoxingCookie;
        ChaoxingLoginService.CookieClearOverride = ClearChaoxingCookies;

        // 批改网：同上。
        PigaiLoginService.CookieReaderOverride = ReadPigaiCookie;
        PigaiLoginService.CookieClearOverride = ClearPigaiCookies;
    }

    /// <summary>读出 pigai.org 域的整条 Cookie（含 HttpOnly；PHPSESSID 必须在）。</summary>
    private static Task<string?> ReadPigaiCookie()
        => Task.FromResult(global::Android.Webkit.CookieManager.Instance?.GetCookie("https://www.pigai.org"));

    /// <summary>
    /// 原生 CookieManager 没有按域删除：给批改网的会话 cookie 写过期值使其失效
    /// （全清会误伤同库存放的雨课堂/学习通登录态）。
    /// </summary>
    private static Task ClearPigaiCookies()
    {
        var cm = global::Android.Webkit.CookieManager.Instance;
        foreach (var name in new[] { "_JKU", "_JUKU_USER", "PHPSESSID" })
            cm?.SetCookie("https://www.pigai.org", $"{name}=; Expires=Thu, 01 Jan 1970 00:00:00 GMT");
        cm?.Flush();
        return Task.CompletedTask;
    }

    /// <summary>读出 chaoxing.com 域的整条 Cookie（含 HttpOnly）。</summary>
    private static Task<string?> ReadChaoxingCookie()
        => Task.FromResult(global::Android.Webkit.CookieManager.Instance?.GetCookie("https://i.chaoxing.com"));

    /// <summary>
    /// 原生 CookieManager 没有按域删除：给超星常见的会话 cookie 写过期值使其失效
    /// （全清会误伤同库存放的雨课堂登录态）。
    /// </summary>
    private static Task ClearChaoxingCookies()
    {
        var cm = global::Android.Webkit.CookieManager.Instance;
        foreach (var name in new[] { "_uid", "vc3", "uf", "fid", "dsc", "ll", "lv", "_d" })
            cm?.SetCookie("https://i.chaoxing.com", $"{name}=; Expires=Thu, 01 Jan 1970 00:00:00 GMT");
        cm?.Flush();
        return Task.CompletedTask;
    }

    /// <summary>从原生 Cookie 存储读出 sessionid / csrftoken。</summary>
    private static Task<YktSession?> ReadSession()
    {
        var raw = global::Android.Webkit.CookieManager.Instance?.GetCookie(YktUrl);
        YktSession? session = null;
        if (!string.IsNullOrEmpty(raw))
        {
            string? sessionId = null, csrf = null, loginType = null;
            foreach (var part in raw.Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;
                switch (kv[0].Trim())
                {
                    case "sessionid": sessionId = kv[1].Trim(); break;
                    case "csrftoken": csrf = kv[1].Trim(); break;
                    case "login_type": loginType = kv[1].Trim(); break;
                }
            }
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                session = new YktSession
                {
                    SessionId = sessionId,
                    CsrfToken = csrf,
                    LoginType = loginType,
                };
            }
        }
        return Task.FromResult(session);
    }

    /// <summary>退出登录：清空 WebView 的 Cookie 存储（应用内只有雨课堂在用 WebView）。</summary>
    private static Task<YktLoginService.CookieClearResult> ClearCookies()
    {
        var cm = global::Android.Webkit.CookieManager.Instance;
        if (cm is null)
            return Task.FromResult(new YktLoginService.CookieClearResult(0, 0, 0, "CookieManager 不可用"));

        cm.RemoveAllCookies(null);
        cm.Flush();
        return Task.FromResult(new YktLoginService.CookieClearResult(0, 0, 0, null));
    }
}
