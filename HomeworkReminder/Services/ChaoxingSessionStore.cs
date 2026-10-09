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

/// <summary>学习通（超星）的登录态文件。</summary>
public sealed class ChaoxingSessionFile
{
    /// <summary>登录成功后从 WebView 读出的整条 Cookie。</summary>
    [JsonPropertyName("cookie")] public string? CookieHeader { get; set; }

    /// <summary>超星用户 id（cookie 里的 _uid）。</summary>
    [JsonPropertyName("uid")] public string? Uid { get; set; }

    [JsonPropertyName("savedAt")] public DateTimeOffset SavedAt { get; set; }

    [JsonIgnore]
    public bool HasValue => !string.IsNullOrWhiteSpace(CookieHeader);
}

/// <summary>
/// 学习通登录态的持久化（chaoxing-session.json）。
/// 与雨课堂 <see cref="SessionStore"/> 同款策略：Windows 用 DPAPI 加密信封，
/// 非 Windows 明文；损坏/无法解密按「未登录」处理。
/// </summary>
public sealed class ChaoxingSessionStore
{
    private readonly string _dir;
    private readonly string _path;

    public ChaoxingSessionStore(string? directory = null)
    {
        _dir = directory ?? AppPaths.DataDirectory;
        _path = Path.Combine(_dir, "chaoxing-session.json");
    }

    public string Location => _path;

    public async Task<ChaoxingSessionFile?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            ChaoxingSessionFile? file;
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
                        Encoding.UTF8.GetString(plain), AppJsonContext.Default.ChaoxingSessionFile);
                }
                else
                {
                    file = JsonSerializer.Deserialize(text, AppJsonContext.Default.ChaoxingSessionFile);
                }
            }

            return file is { HasValue: true } ? file : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or FormatException or Win32Exception or ExternalException)
        {
            return null;
        }
    }

    public async Task SaveAsync(ChaoxingSessionFile file, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);
        var payload = JsonSerializer.Serialize(file, AppJsonContext.Default.ChaoxingSessionFile);

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
