using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HomeworkReminder.Services;

/// <summary>
/// 批改网（pigai.org）的登录态文件。
/// <para>
/// 批改网只用 Cookie 认证：实测登录成功后服务端下发 <c>PHPSESSID</c> 与
/// <c>_JKU</c>（另有 <c>_JUKU_USER</c> 等附属项）。其中 <c>_JKU</c> 是可靠的
/// 「已登录」标志——凭据失效时受限页面不返回 302，而是照常返回 200 并渲染
/// 「使用此功能，请先登录」，所以只能靠这个 Cookie 判定，不能看状态码。
/// </para>
/// </summary>
public sealed class PigaiSessionFile
{
    /// <summary>整条 Cookie（含 HttpOnly 项：PHPSESSID 必须带上，否则会话不成立）。</summary>
    [JsonPropertyName("cookie")] public string? CookieHeader { get; set; }

    /// <summary>账号标识（Cookie <c>_JKU</c> 的值，通常是手机号/用户名），仅用于界面显示。</summary>
    [JsonPropertyName("account")] public string? Account { get; set; }

    [JsonPropertyName("savedAt")] public DateTimeOffset SavedAt { get; set; }

    [JsonIgnore]
    public bool HasValue => !string.IsNullOrWhiteSpace(CookieHeader);
}

/// <summary>
/// 批改网登录态的持久化（pigai-session.json）。
/// 与 <see cref="SessionStore"/>（雨课堂）、<see cref="ChaoxingSessionStore"/>（学习通）
/// 同款策略：Windows 用 DPAPI 加密信封，非 Windows 明文；损坏/无法解密按「未登录」处理。
/// </summary>
public sealed class PigaiSessionStore
{
    private readonly string _dir;
    private readonly string _path;

    public PigaiSessionStore(string? directory = null)
    {
        _dir = directory ?? AppPaths.DataDirectory;
        _path = Path.Combine(_dir, "pigai-session.json");
    }

    public string Location => _path;

    public async Task<PigaiSessionFile?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            PigaiSessionFile? file;
            using (var doc = JsonDocument.Parse(text))
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("protected", out var flag)
                    && flag.ValueKind == JsonValueKind.True)
                {
                    if (!OperatingSystem.IsWindows()) return null;
                    var b64 = root.TryGetProperty("data", out var d) ? d.GetString() : null;
                    if (string.IsNullOrEmpty(b64)) return null;
                    var plain = Dpapi.Unprotect(Convert.FromBase64String(b64));
                    file = JsonSerializer.Deserialize(
                        Encoding.UTF8.GetString(plain), AppJsonContext.Default.PigaiSessionFile);
                }
                else
                {
                    file = JsonSerializer.Deserialize(text, AppJsonContext.Default.PigaiSessionFile);
                }
            }

            return file is { HasValue: true } ? file : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or FormatException or Win32Exception or ExternalException)
        {
            // 读不出来（换用户/换机器导致 DPAPI 解不开、文件损坏）时按「未登录」处理，
            // 但必须留下痕迹，否则表现为「登录过却总是未登录」而查不出原因。
            PigaiLoginService.Log($"store: 读取会话失败 {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public async Task SaveAsync(PigaiSessionFile file, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);
        var payload = JsonSerializer.Serialize(file, AppJsonContext.Default.PigaiSessionFile);

        string text;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var blob = Dpapi.Protect(Encoding.UTF8.GetBytes(payload));
                text = JsonSerializer.Serialize(
                    new SessionStore.ProtectedEnvelope { Data = Convert.ToBase64String(blob) },
                    AppJsonContext.Default.ProtectedEnvelope);
            }
            catch (Exception ex) when (ex is Win32Exception or ExternalException)
            {
                text = payload;
            }
        }
        else
        {
            text = payload;
        }

        await File.WriteAllTextAsync(_path, text, ct).ConfigureAwait(false);
        // 落盘与否直接决定「重启后是否还算已登录」，但它在界面上看不出来，
        // 因此在诊断日志里明确记一行（只记路径与长度，不记内容）。
        PigaiLoginService.Log($"store: 已写入 {_path}（加密={text != payload}，{text.Length} 字符）");
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (IOException)
        {
            // 删除失败不影响本次运行。
        }
        return Task.CompletedTask;
    }
}
