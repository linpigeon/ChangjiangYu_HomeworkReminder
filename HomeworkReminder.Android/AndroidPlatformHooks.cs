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
