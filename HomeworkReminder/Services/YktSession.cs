using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Serialization;

namespace HomeworkReminder.Services;

/// <summary>
/// 雨课堂登录态。实测：仅凭 <c>sessionid</c> + <c>csrftoken</c> 两个 cookie 就能驱动全部接口，
/// 不需要浏览器参与（浏览器只在获取 cookie 时需要）。
/// </summary>
public sealed class YktSession
{
    public required string SessionId { get; init; }

    /// <summary>POST 请求必须带 X-CSRFToken 头，否则 403。</summary>
    public string? CsrfToken { get; init; }

    public string? LoginType { get; init; }

    /// <summary>会话过期时间（来自 cookie 的 expires，可能为空）。</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>所属高校 id。默认取自 <see cref="AppSettings.Current"/>（settings.json）。</summary>
    public int UniversityId { get; init; } = AppSettings.Current.UniversityId;

    /// <summary>学期标识。默认取自 <see cref="AppSettings.Current"/>（settings.json）。</summary>
    public int Term { get; init; } = AppSettings.Current.Term;

    [JsonIgnore]
    public bool IsExpired => ExpiresAt is { } e && e <= DateTimeOffset.Now;

    /// <summary>拼装 Cookie 请求头。</summary>
    [JsonIgnore]
    public string CookieHeader
    {
        get
        {
            var parts = new List<string> { $"sessionid={SessionId}" };
            if (!string.IsNullOrEmpty(CsrfToken)) parts.Add($"csrftoken={CsrfToken}");
            parts.Add("django_language=zh-cn");
            return string.Join("; ", parts);
        }
    }

    /// <summary>
    /// 从浏览器 F12 里复制的一整条 cookie 字符串中解析出所需字段。
    /// 兼容 "a=1; b=2" 与 "a=1;\nb=2" 两种粘贴形态。
    /// </summary>
    /// <param name="universityId">高校 id；传 0（默认）表示取 <see cref="AppSettings.Current"/>。</param>
    /// <param name="term">学期标识；传 0（默认）表示取 <see cref="AppSettings.Current"/>。</param>
    public static YktSession ParseCookieString(string raw, int universityId = 0, int term = 0)
    {
        if (universityId == 0) universityId = AppSettings.Current.UniversityId;
        if (term == 0) term = AppSettings.Current.Term;

        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("cookie 内容为空", nameof(raw));

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in raw.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = segment.IndexOf('=');
            if (idx <= 0) continue;
            var name = segment[..idx].Trim();
            var value = segment[(idx + 1)..].Trim();
            // 浏览器「复制为 cURL」等形式可能带引号
            value = value.Trim('"');
            if (name.Length > 0) map[name] = value;
        }

        if (!map.TryGetValue("sessionid", out var sessionId) || string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("cookie 里没有找到 sessionid，请确认复制的是 changjiang.yuketang.cn 的请求头", nameof(raw));

        map.TryGetValue("csrftoken", out var csrf);
        map.TryGetValue("login_type", out var loginType);

        return new YktSession
        {
            SessionId = sessionId,
            CsrfToken = csrf,
            LoginType = loginType,
            UniversityId = universityId,
            Term = term,
        };
    }
}

/// <summary>持久化到磁盘的会话文件结构。</summary>
public sealed class SessionFile
{
    public string? SessionId { get; set; }
    public string? CsrfToken { get; set; }
    public string? LoginType { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int UniversityId { get; set; } = AppSettings.Current.UniversityId;
    public int Term { get; set; } = AppSettings.Current.Term;

    // ---- 用户资料缓存 ----
    // 仅用于下次启动时立刻显示姓名/头像，不参与认证；每次同步成功后会刷新。
    public string? UserName { get; set; }
    public string? UserSubtitle { get; set; }
    public string? UserAvatarUrl { get; set; }

    [JsonIgnore]
    public bool HasValue => !string.IsNullOrWhiteSpace(SessionId);

    public YktSession ToSession() => new()
    {
        SessionId = SessionId!,
        CsrfToken = CsrfToken,
        LoginType = LoginType,
        ExpiresAt = ExpiresAt,
        UniversityId = UniversityId,
        Term = Term,
    };

    public static SessionFile From(YktSession s) => new()
    {
        SessionId = s.SessionId,
        CsrfToken = s.CsrfToken,
        LoginType = s.LoginType,
        ExpiresAt = s.ExpiresAt,
        UniversityId = s.UniversityId,
        Term = s.Term,
    };

    /// <summary>
    /// 从 Playwright 风格的 cookie 数组 JSON 构造（上一轮会话保存的就是这种格式），
    /// 便于直接复用已有登录态。
    /// </summary>
    /// <param name="universityId">高校 id；传 0（默认）表示取 <see cref="AppSettings.Current"/>。</param>
    /// <param name="term">学期标识；传 0（默认）表示取 <see cref="AppSettings.Current"/>。</param>
    public static SessionFile? FromCookieArrayJson(string json, int universityId = 0, int term = 0)
    {
        if (universityId == 0) universityId = AppSettings.Current.UniversityId;
        if (term == 0) term = AppSettings.Current.Term;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

            string? sessionId = null, csrf = null, loginType = null;
            DateTimeOffset? expires = null;

            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
                var value = c.TryGetProperty("value", out var v) ? v.GetString() : null;
                switch (name)
                {
                    case "sessionid":
                        sessionId = value;
                        // Playwright 导出的 cookie 用 Unix 秒表示过期时间。
                        if (c.TryGetProperty("expires", out var exp)
                            && exp.ValueKind == System.Text.Json.JsonValueKind.Number
                            && exp.TryGetDouble(out var epoch)
                            && epoch > 0)
                        {
                            expires = DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000));
                        }
                        break;
                    case "csrftoken":
                        csrf = value;
                        break;
                    case "login_type":
                        loginType = value;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(sessionId)) return null;

            return new SessionFile
            {
                SessionId = sessionId,
                CsrfToken = csrf,
                LoginType = loginType,
                ExpiresAt = expires,
                UniversityId = universityId,
                Term = term,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
