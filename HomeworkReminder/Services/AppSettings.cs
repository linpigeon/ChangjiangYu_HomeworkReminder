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

    // ---------------- 自动同步与通知 ----------------

    /// <summary>
    /// 自动同步间隔（分钟）。<c>0</c> = 关闭自动同步。
    /// 下限由界面与校验共同保证在 <see cref="MinRefreshMinutes"/>。
    /// </summary>
    public int AutoRefreshMinutes { get; set; } = 30;

    /// <summary>间隔的合法下限（分钟）。太小会频繁请求雨课堂，容易被风控。</summary>
    public const int MinRefreshMinutes = 5;

    /// <summary>间隔的合法上限（分钟）。</summary>
    public const int MaxRefreshMinutes = 24 * 60;

    /// <summary>发现新作业时是否发系统通知。</summary>
    public bool NotifyOnNewHomework { get; set; } = true;

    // ---------------- 桌面小组件 ----------------

    /// <summary>是否显示桌面小组件。</summary>
    public bool WidgetVisible { get; set; }

    /// <summary>小组件位置。null = 使用默认位置（主屏右下角）。</summary>
    public int? WidgetX { get; set; }

    public int? WidgetY { get; set; }

    /// <summary>小组件尺寸（DIP）。null = 默认尺寸。</summary>
    public double? WidgetW { get; set; }

    public double? WidgetH { get; set; }

    /// <summary>小组件当前选中的分组：myday / homework / planned / all。</summary>
    public string WidgetGroup { get; set; } = "myday";

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

    /// <summary>
    /// 归一化自动同步间隔：0（关闭）保持 0，其余夹在
    /// [<see cref="MinRefreshMinutes"/>, <see cref="MaxRefreshMinutes"/>] 之间。
    /// 手改 settings.json 写出离谱数值时，不至于让应用疯狂请求或永不同步。
    /// </summary>
    public static int NormalizeRefreshMinutes(int minutes)
    {
        if (minutes <= 0) return 0;
        return Math.Clamp(minutes, MinRefreshMinutes, MaxRefreshMinutes);
    }

    // UI 线程的直接保存与线程池的防抖保存会并发写同一文件，串行化写盘。
    private static readonly object SaveLock = new();

    /// <summary>写回磁盘。主题/透明度由界面防抖后调用，壁纸与小组件开关直接调用。</summary>
    public void Save()
    {
        var json = JsonSerializer.Serialize(this, AppJsonContext.Default.AppSettings);
        lock (SaveLock)
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            // 先写临时文件再原子替换：进程中断也不会留下半截 JSON。
            var temp = Location + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, Location, overwrite: true);
        }
    }
}
