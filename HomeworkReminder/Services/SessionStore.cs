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
/// 登录态与本地设置的持久化。
/// <para>
/// 存放在 <c>%LOCALAPPDATA%\HomeworkReminder</c>。会话凭据等价于雨课堂登录凭证，
/// 因此 <c>session.json</c> 的落盘格式按平台区分：
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Windows</b>：用 DPAPI（<c>CryptProtectData</c>，CurrentUser 作用域语义，
/// 见 <see cref="Dpapi"/>）加密后写
/// envelope JSON：<c>{ "protected": true, "data": "&lt;base64&gt;" }</c>。
/// 只有本机当前用户能解密，文件被拷走也无法复原凭据。
/// </item>
/// <item>
/// <b>非 Windows</b>（无 DPAPI）：回落为明文，顶层直接是 <see cref="SessionFile"/> 字段
/// （与加密前的旧格式一致）。在这些平台上请自行依赖 OS 的用户目录权限保护。
/// </item>
/// </list>
/// <para>
/// 向后兼容：<see cref="ISessionStore.LoadAsync"/> 仍能读取加密前的旧明文格式；
/// 读取成功后下一次 <see cref="ISessionStore.SaveAsync"/> 会自动升级为加密格式（Windows）。
/// </para>
/// </summary>
public interface ISessionStore
{
    Task<SessionFile?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(SessionFile file, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    string Location { get; }
}

/// <inheritdoc />
public sealed class SessionStore : ISessionStore
{
    /// <summary>Windows 上落盘的加密信封格式。internal：源生成上下文需要引用它。</summary>
    internal sealed class ProtectedEnvelope
    {
        [JsonPropertyName("protected")] public bool Protected { get; set; } = true;
        [JsonPropertyName("data")] public string Data { get; set; } = string.Empty;
    }

    private readonly string _dir;
    private readonly string _path;

    public SessionStore(string? directory = null)
    {
        _dir = directory ?? AppPaths.DataDirectory;
        _path = Path.Combine(_dir, "session.json");
    }

    public string Location => _path;

    public async Task<SessionFile?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);

            SessionFile? file;
            using (var doc = JsonDocument.Parse(text))
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("protected", out var flag)
                    && flag.ValueKind == JsonValueKind.True)
                {
                    // 加密格式。换用户/换机器解密会失败，按「未登录」处理。
                    if (!OperatingSystem.IsWindows()) return null;
                    var b64 = root.TryGetProperty("data", out var d) ? d.GetString() : null;
                    if (string.IsNullOrEmpty(b64)) return null;
                    var plain = Dpapi.Unprotect(Convert.FromBase64String(b64));
                    file = JsonSerializer.Deserialize(
                        Encoding.UTF8.GetString(plain), AppJsonContext.Default.SessionFile);
                }
                else
                {
                    // 旧格式：顶层直接是 SessionFile 字段的明文 JSON。
                    file = JsonSerializer.Deserialize(text, AppJsonContext.Default.SessionFile);
                }
            }

            return file is { HasValue: true } ? file : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or FormatException or Win32Exception or ExternalException)
        {
            // 损坏或无法解密的会话文件不应该让应用起不来，按「未登录」处理。
            return null;
        }
    }

    public async Task SaveAsync(SessionFile file, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);
        var payload = JsonSerializer.Serialize(file, AppJsonContext.Default.SessionFile);

        string text;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var blob = Dpapi.Protect(Encoding.UTF8.GetBytes(payload));
                text = JsonSerializer.Serialize(
                    new ProtectedEnvelope { Data = Convert.ToBase64String(blob) },
                    AppJsonContext.Default.ProtectedEnvelope);
            }
            catch (Exception ex) when (ex is Win32Exception or ExternalException)
            {
                // DPAPI 不可用（极为罕见，例如受限的会话 0 服务场景）时宁可明文保存，
                // 也不能让「保存登录态」整个失败——明文正是加密改造之前的旧行为。
                text = payload;
            }
        }
        else
        {
            // 非 Windows：无 DPAPI，按旧格式明文保存。
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
