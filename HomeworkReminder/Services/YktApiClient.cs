using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using HomeworkReminder.Models;

namespace HomeworkReminder.Services;

/// <summary>
/// 长江雨课堂内部接口客户端。
/// <para>
/// 全部端点均为实测：域名 <c>changjiang.yuketang.cn</c>，认证仅需 sessionid cookie
/// （POST 另需 X-CSRFToken 头）。这些不是公开契约接口，雨课堂前端改版可能导致失效，
/// 因此每个调用都做了容错：字段缺失只跳过，不抛异常。
/// </para>
/// </summary>
public interface IYktApi : IDisposable
{
    YktSession Session { get; }

    /// <summary>校验登录态。失效时抛 <see cref="YktAuthExpiredException"/>。</summary>
    Task<IReadOnlyList<YktCourse>> GetCoursesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<YktActivity>> GetLearnLogsAsync(long classroomId, CancellationToken ct = default);

    /// <summary>拉取完成进度。返回 leafId -> 进度 的字典。</summary>
    Task<IReadOnlyDictionary<string, YktLeafProgress>> GetProgressAsync(long classroomId, CancellationToken ct = default);

    Task<YktLeafDetail?> GetLeafDetailAsync(long classroomId, long leafId, CancellationToken ct = default);

    /// <summary>章节树，用于给试卷类作业补截止时间。</summary>
    Task<IReadOnlyList<YktChapter>> GetChaptersAsync(long classroomId, CancellationToken ct = default);

    /// <summary>未读总数。</summary>
    Task<int> GetUnreadCountAsync(CancellationToken ct = default);

    /// <summary>
    /// 当前登录用户的信息（姓名、学号、学校、头像）。
    /// 这是可选的展示信息，失败不应影响同步，因此失败时返回 null。
    /// </summary>
    Task<YktUserProfile?> GetUserProfileAsync(CancellationToken ct = default);

    /// <summary>
    /// 全部标记为已读。这是雨课堂唯一可反写的已读接口——实测前端对单条消息只改本地
    /// Vue 状态（<c>this.$set(t,"is_read",!0)</c>），并没有单条已读的服务端调用。
    /// </summary>
    Task<bool> MarkAllReadAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class YktApiClient : IYktApi, IDisposable
{
    private const string BaseUrl = "https://changjiang.yuketang.cn";
    private const string Xtbz = "ykt";

    /// <summary>实测节流：单账号串行 + 延迟，避免触发风控。</summary>
    private const int ThrottleMs = 200;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public YktApiClient(YktSession session, HttpClient? http = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _ownsHttp = http is null;
        // 用托管的 SocketsHttpHandler 而不是平台原生 handler：Android 上 HttpClientHandler
        // 走 Java HttpURLConnection，雨课堂的 /api/v3 与 /v/ 端点（Envoy 网关）对它一律
        // 返回 UNAUTHENTICATED/web_redirect，v2 系却正常；托管栈与桌面行为一致，没有这个问题。
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
    }

    public YktSession Session { get; }

    public async Task<IReadOnlyList<YktCourse>> GetCoursesAsync(CancellationToken ct = default)
    {
        var data = await GetDataAsync("/v2/api/web/courses/list?identity=2", ct).ConfigureAwait(false);
        var list = data.TryGetProperty("list", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.Deserialize(AppJsonContext.Default.ListYktCourse)
            : null;
        return list ?? [];
    }

    public async Task<IReadOnlyList<YktActivity>> GetLearnLogsAsync(long classroomId, CancellationToken ct = default)
    {
        // 注意：这里的 offset 语义是「每页条数」，不是偏移量（实测）。
        var url = $"/v2/api/web/logs/learn/{classroomId}" +
                  $"?actype=-1&page=0&offset=500&sort=-1&term=latest&uv_id={Session.UniversityId}";
        var data = await GetDataAsync(url, ct).ConfigureAwait(false);
        var acts = data.TryGetProperty("activities", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.Deserialize(AppJsonContext.Default.ListYktActivity)
            : null;
        return acts ?? [];
    }

    public async Task<IReadOnlyDictionary<string, YktLeafProgress>> GetProgressAsync(
        long classroomId, CancellationToken ct = default)
    {
        // /course/schedule 返回同样数据但要会话级 sign 参数，外部脚本拿不到，
        // 所以改用 pub_new_pro：POST + X-CSRFToken，无需 sign。
        var url = $"/mooc-api/v1/lms/learn/course/pub_new_pro" +
                  $"?cid={classroomId}&term=latest&uv_id={Session.UniversityId}&classroom_id={classroomId}";
        var body = new YktProgressRequest
        {
            ClassroomId = classroomId,
            Cid = classroomId,
            UniversityId = Session.UniversityId,
        };

        var data = await PostDataAsync(url, body, AppJsonContext.Default.YktProgressRequest, ct).ConfigureAwait(false);
        var payload = data.Deserialize(AppJsonContext.Default.YktProgressData);
        return payload?.LeafSchedules ?? new Dictionary<string, YktLeafProgress>();
    }

    public async Task<YktLeafDetail?> GetLeafDetailAsync(long classroomId, long leafId, CancellationToken ct = default)
    {
        var url = $"/mooc-api/v1/lms/learn/leaf_info/{classroomId}/{leafId}/";
        try
        {
            // 该端点在缺少 classroom-id 请求头时会返回「Objects does not exist.」，必须带上。
            var data = await GetDataAsync(url, ct, new Dictionary<string, string>
            {
                ["classroom-id"] = classroomId.ToString(),
            }).ConfigureAwait(false);

            return data.Deserialize(AppJsonContext.Default.YktLeafDetail);
        }
        catch (YktAuthExpiredException)
        {
            throw;
        }
        catch (YktApiException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<YktChapter>> GetChaptersAsync(long classroomId, CancellationToken ct = default)
    {
        var url = $"/mooc-api/v1/lms/learn/course/chapter" +
                  $"?cid={classroomId}&term=latest&uv_id={Session.UniversityId}&classroom_id={classroomId}";
        try
        {
            var data = await GetDataAsync(url, ct).ConfigureAwait(false);
            var chapters = data.TryGetProperty("course_chapter", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.Deserialize(AppJsonContext.Default.ListYktChapter)
                : null;
            return chapters ?? [];
        }
        catch (YktAuthExpiredException)
        {
            throw;
        }
        catch (YktApiException)
        {
            return [];
        }
    }

    public async Task<int> GetUnreadCountAsync(CancellationToken ct = default)
    {
        var url = "/smart_education/notification/message_assistant/user_summary/" +
                  $"?term=latest&uv_id={Session.UniversityId}";
        var data = await GetDataAsync(url, ct).ConfigureAwait(false);
        return ReadInt(data, "total_unread") ?? 0;
    }

    public async Task<bool> MarkAllReadAsync(CancellationToken ct = default)    {
        var url = "/smart_education/notification/message_assistant/user_read_all/" +
                  $"?term=latest&uv_id={Session.UniversityId}";
        try
        {
            await PostDataAsync(
                url,
                new YktMarkAllReadRequest { UniversityId = Session.UniversityId },
                AppJsonContext.Default.YktMarkAllReadRequest,
                ct).ConfigureAwait(false);
            return true;
        }
        catch (YktApiException)
        {
            return false;
        }
    }

    public async Task<YktUserProfile?> GetUserProfileAsync(CancellationToken ct = default)
    {
        try
        {
            // 主用 v 系端点：/api/v3/* 在 API 网关（Envoy）后面，
            // 实测 Android 原生 HTTP 栈打过去一律 UNAUTHENTICATED（v2/v 系正常），原因未明。
            var data = await GetDataAsync("/v/course_meta/user_info", ct).ConfigureAwait(false);
            if (data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("user_profile", out var up)
                && up.ValueKind == JsonValueKind.Object)
            {
                var p = up.Deserialize(AppJsonContext.Default.YktUserProfile);
                if (!string.IsNullOrWhiteSpace(p?.DisplayName)) return p;
            }

            // 兜底：老的 v3 端点（桌面端一直可用）。
            var legacy = await GetDataAsync("/api/v3/user/basic-info", ct).ConfigureAwait(false);
            if (legacy.ValueKind != JsonValueKind.Object) return null;

            var profile = legacy.Deserialize(AppJsonContext.Default.YktUserProfile);
            return string.IsNullOrWhiteSpace(profile?.DisplayName) ? null : profile;
        }
        catch (YktAuthExpiredException)
        {
            throw;
        }
        catch (Exception ex) when (ex is YktApiException or System.Text.Json.JsonException)
        {
            // 纯展示信息，拿不到就退回「已登录」文案。
            return null;
        }
    }

    // ---------------- 传输层 ----------------

    private async Task<JsonElement> GetDataAsync(
        string url,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + url);
            ApplyHeaders(req);
            if (extraHeaders is not null)
                foreach (var (k, v) in extraHeaders) req.Headers.TryAddWithoutValidation(k, v);

            var json = await SendAsync(req, ct).ConfigureAwait(false);
            await Task.Delay(ThrottleMs, ct).ConfigureAwait(false);
            return json;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonElement> PostDataAsync<T>(
        string url,
        T body,
        JsonTypeInfo<T> bodyType,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + url)
            {
                // 显式传源生成的 JsonTypeInfo：匿名对象/反射序列化在裁剪发布下会崩。
                Content = JsonContent.Create(body, bodyType),
            };
            ApplyHeaders(req);
            req.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(Session.CsrfToken))
                req.Headers.TryAddWithoutValidation("X-CSRFToken", Session.CsrfToken);

            var json = await SendAsync(req, ct).ConfigureAwait(false);
            await Task.Delay(ThrottleMs, ct).ConfigureAwait(false);
            return json;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ApplyHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("xtbz", Xtbz);
        req.Headers.TryAddWithoutValidation("Cookie", Session.CookieHeader);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
        req.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new YktApiException("请求超时，雨课堂没有在预期时间内响应");
        }
        catch (HttpRequestException ex)
        {
            throw new YktApiException($"网络请求失败：{ex.Message}", inner: ex);
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new YktAuthExpiredException(
                    $"登录态失效（HTTP {(int)resp.StatusCode}），请重新扫码登录", (int)resp.StatusCode);

            if (string.IsNullOrWhiteSpace(text))
                return default;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                // leaf_info 等端点在对象不存在时会返回非 JSON 的提示文本。
                if (!resp.IsSuccessStatusCode)
                    throw new YktApiException($"接口返回 {(int)resp.StatusCode}：{Truncate(text)}", (int)resp.StatusCode);
                return default;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return default;

                // 雨课堂用多种字段名表达错误：errcode / error_code / errorcode。
                var errCode = ReadInt(root, "errcode") ?? ReadInt(root, "error_code") ?? ReadInt(root, "errorcode");
                var explicitFailure = root.TryGetProperty("success", out var s)
                                      && s.ValueKind == JsonValueKind.False;

                if (errCode is 401002 or 401)
                    throw new YktAuthExpiredException("会话已失效（Cookie has no sessionid），请重新登录", errCode);

                if (explicitFailure || (errCode is not null and not 0))
                {
                    var msg = ReadString(root, "errmsg")
                              ?? ReadString(root, "errormsg")
                              ?? ReadString(root, "msg")
                              ?? ReadString(root, "detail")
                              ?? Truncate(text);
                    throw new YktApiException(msg, errCode);
                }

                if (!resp.IsSuccessStatusCode)
                    throw new YktApiException($"接口返回 {(int)resp.StatusCode}：{Truncate(text)}", (int)resp.StatusCode);

                return root.TryGetProperty("data", out var data) ? data.Clone() : root.Clone();
            }
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

    private static int? ReadInt(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s,
            _ => null,
        };
    }

    private static string? ReadString(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public void Dispose()
    {
        _gate.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
