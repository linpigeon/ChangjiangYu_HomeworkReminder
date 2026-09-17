using System;
using System.IO;
using System.Text.Json;

namespace HomeworkReminder.Services;

/// <summary>
/// 应用级设置（学期、学校 id 等），持久化在 <c>settings.json</c>（与会话文件同目录，
/// 见 <see cref="AppPaths.DataDirectory"/>）。
/// <para>
/// 目前没有设置界面：直接编辑该文件即可，下次启动生效。文件不存在或损坏时回落到默认值。
/// 代码里不要再写死学期/学校 id，一律从 <see cref="Current"/> 取。
/// </para>
/// </summary>
public sealed class AppSettings
{
    /// <summary>所属高校 id。长江雨课堂为 3214。</summary>
    public int UniversityId { get; set; } = 3214;

    /// <summary>学期标识。202601 对应 2026-2027 学年第一学期。</summary>
    public int Term { get; set; } = 202601;

    /// <summary>待办界面壁纸图片的本地路径（null/空 = 不用壁纸，显示桌面磨砂）。</summary>
    public string? WallpaperPath { get; set; }

    /// <summary>背景透明度（0–85，越大越透：壁纸或桌面磨砂透出界面的程度）。</summary>
    public double WallpaperOpacityPercent { get; set; } = 30;

    /// <summary>主题：0 = 浅色，1 = 深色，2 = 跟随系统。</summary>
    public int ThemeModeIndex { get; set; } = 2;

    private static readonly Lazy<AppSettings> LazyCurrent = new(Load);

    /// <summary>当前生效的设置。进程生命周期内缓存；修改 settings.json 需重启应用。</summary>
    public static AppSettings Current => LazyCurrent.Value;

    public static string Location => Path.Combine(AppPaths.DataDirectory, "settings.json");

    private static AppSettings Load()
    {
        try
        {
            if (!File.Exists(Location)) return new AppSettings();
            var text = File.ReadAllText(Location);
            return JsonSerializer.Deserialize(text, AppJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 损坏的设置文件不应该让应用起不来，按默认值处理。
            return new AppSettings();
        }
    }

    /// <summary>写回磁盘（供未来的设置界面或测试使用）。</summary>
    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        File.WriteAllText(Location, JsonSerializer.Serialize(this, AppJsonContext.Default.AppSettings));
    }
}
