using System;
using System.IO;
using System.Text.Json;

namespace HomeworkReminder.Services;

/// <summary>
/// 应用级设置（学期、学校 id 等），持久化在 <c>settings.json</c>（与会话文件同目录，
/// 见 <see cref="AppPaths.DataDirectory"/>）。界面入口是主界面里的「设置」页。
/// 文件不存在或损坏时回落到默认值。
/// 代码里不要再写死学期/学校 id，一律从 <see cref="Current"/> 取。
/// </summary>
public sealed class AppSettings
{
    /// <summary>所属高校 id。长江雨课堂为 3214。</summary>
    public int UniversityId { get; set; } = 3214;

    /// <summary>学期标识。202601 对应 2026-2027 学年第一学期。</summary>
    public int Term { get; set; } = 202601;

    /// <summary>
    /// 学期第一周的周一（<c>yyyy-MM-dd</c>）。空 = 未设置。
    /// <para>
    /// 课表用它把「第 N 周 + 星期几」换算成具体日期。教务系统的课表**只给周次**，
    /// 具体日期必须由这个基准推出来；没有它就只能按周次展示、显示不了日期
    /// （刻意不编造日期——假日期比没有日期更容易误导）。
    /// </para>
    /// </summary>
    public string? TermStartDate { get; set; }

    /// <summary>待办界面壁纸图片的本地路径（null/空 = 不用壁纸，显示桌面磨砂）。</summary>
    public string? WallpaperPath { get; set; }

    /// <summary>背景透明度（0–85，越大越透：壁纸或桌面磨砂透出界面的程度）。</summary>
    public double WallpaperOpacityPercent { get; set; } = 30;

    /// <summary>主题：0 = 浅色，1 = 深色，2 = 跟随系统。</summary>
    public int ThemeModeIndex { get; set; } = 2;

    /// <summary>点窗口「关闭」的行为：0 = 最小化到托盘继续运行（默认），1 = 直接退出程序。</summary>
    public int CloseBehaviorIndex { get; set; }

    /// <summary>桌面三栏：侧边栏宽度（DIP，GridSplitter 拖动后记忆）。</summary>
    public double SidebarWidth { get; set; } = 252;

    /// <summary>桌面三栏：待办列表栏宽度（DIP）。详情栏吃剩余空间，无需记忆。</summary>
    public double ListWidth { get; set; } = 400;

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

    /// <summary>
    /// 上课前多少分钟发提醒。<c>0</c> = 不提醒。
    /// <para>
    /// 依赖「学期第一周的周一」（<see cref="TermStartDate"/>）：课表只有周次，
    /// 没有这个基准就算不出今天第几节是几点，也就无从提醒。
    /// </para>
    /// </summary>
    public int ClassReminderMinutes { get; set; } = DefaultClassReminderMinutes;

    /// <summary>默认提前 15 分钟提醒。</summary>
    public const int DefaultClassReminderMinutes = 15;

    /// <summary>提醒提前量的下限（分钟）。</summary>
    public const int MinClassReminderMinutes = 0;

    /// <summary>提醒提前量的上限（分钟）。一节课也就 45 分钟，超过 2 小时没有意义。</summary>
    public const int MaxClassReminderMinutes = 120;

    /// <summary>提醒提前量归一化：越界一律夹回合法区间（手改 settings.json 也兜得住）。</summary>
    public static int NormalizeClassReminderMinutes(int minutes) =>
        Math.Clamp(minutes, MinClassReminderMinutes, MaxClassReminderMinutes);

    /// <summary>
    /// 是否接收**预发布版**（prerelease）更新。
    /// <para>
    /// 默认 <c>false</c>：预发布版本是给测试用的，不该推给正式版用户。
    /// 打开后「检查更新」会把 prerelease/draft 也纳入候选。
    /// </para>
    /// </summary>
    public bool IncludePrereleaseUpdates { get; set; }

    // ---------------- 个人信息（设置页手动填写，不再从雨课堂拉取） ----------------

    /// <summary>用户自定义姓名。null/空 = 显示「未登录」占位。</summary>
    public string? CustomUserName { get; set; }

    /// <summary>用户自定义副标题（学校 / 学号）。</summary>
    public string? CustomUserSubtitle { get; set; }

    /// <summary>用户自定义头像地址（http(s) 图片链接；本地路径亦可）。</summary>
    public string? CustomAvatarUrl { get; set; }

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
