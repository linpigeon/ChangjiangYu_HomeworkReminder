using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace HomeworkReminder.Services;

/// <summary>
/// 学习通（超星）的 WebView 登录：加载统一登录页，登录成功后从 Cookie 捕获会话。
/// 与 <see cref="YktLoginService"/> 同款结构：桌面走 Avalonia 的 CookieManager 抽象，
/// Android 走平台注入的原生读取（Avalonia 的抽象在 Android 上不可用）。
/// </summary>
public static class ChaoxingLoginService
{
    /// <summary>超星统一登录页（新版，含扫码与账号密码两种入口）。</summary>
    public const string LoginUrl =
        "https://passport2.chaoxing.com/login?loginType=4&newversion=true&refer=http://i.chaoxing.com";

    /// <summary>Android 注入的原生 Cookie 读取（返回 chaoxing.com 域的整条 Cookie）。</summary>
    public static Func<Task<string?>>? CookieReaderOverride { get; set; }

    /// <summary>Android 注入的原生 Cookie 清理。</summary>
    public static Func<Task>? CookieClearOverride { get; set; }

    private static bool IsChaoxingDomain(string domain) =>
        domain.EndsWith("chaoxing.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 尝试从 WebView 捕获学习通会话：chaoxing.com 域里有 _uid cookie 即视为已登录。
    /// 未登录返回 null。
    /// </summary>
    public static async Task<ChaoxingSessionFile?> CaptureAsync(NativeWebView webView)
    {
        string? cookieHeader;
        if (CookieReaderOverride is not null)
        {
            cookieHeader = await CookieReaderOverride().ConfigureAwait(true);
        }
        else
        {
            var manager = webView.TryGetCookieManager();
            if (manager is null) return null;

            var cookies = await manager.GetCookiesAsync().ConfigureAwait(true);
            if (cookies is null) return null;

            cookieHeader = string.Join("; ", cookies.Cast<Cookie>()
                .Where(c => IsChaoxingDomain(c.Domain))
                .Select(c => $"{c.Name}={c.Value}"));
        }

        if (string.IsNullOrWhiteSpace(cookieHeader)) return null;

        string? uid = null;
        foreach (var part in cookieHeader.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim() == "_uid")
            {
                uid = kv[1].Trim();
                break;
            }
        }

        return uid is null
            ? null
            : new ChaoxingSessionFile
            {
                CookieHeader = cookieHeader,
                Uid = uid,
                SavedAt = DateTimeOffset.Now,
            };
    }

    /// <summary>清掉 WebView 里 chaoxing.com 域的 Cookie（退出登录）。</summary>
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
            foreach (var c in cookies.Cast<Cookie>().Where(c => IsChaoxingDomain(c.Domain)).ToList())
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
