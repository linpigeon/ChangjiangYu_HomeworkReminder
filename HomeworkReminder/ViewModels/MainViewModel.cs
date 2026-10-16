using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomeworkReminder.Models;
using HomeworkReminder.Services;

namespace HomeworkReminder.ViewModels;

/// <summary>应用整体状态。</summary>
public enum AppState
{
    /// <summary>没有可用登录态，显示登录页。</summary>
    NeedsLogin,

    /// <summary>正在同步。</summary>
    Syncing,

    /// <summary>已有数据。</summary>
    Ready,

    /// <summary>出错（通常是登录态失效）。</summary>
    Error,
}

/// <summary>
/// 应用外壳：负责登录、同步、筛选，并把列表暴露给视图。
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly ISessionStore _sessionStore;
    private readonly ILocalStateStore _localStateStore;
    private readonly ITodoCacheStore _todoCacheStore;

    /// <summary>上次同步落盘的待办缓存（todos.json 的内存副本），增量同步的比对基准。</summary>
    private TodoCacheFile? _cacheFile;
    private readonly YktLoginService _loginService = new();

    /// <summary>
    /// 仅用于下载头像；待办数据一律走 <see cref="IYktApi"/>。
    /// 进程级共享实例：HttpClient 虽实现 IDisposable，但本就为复用设计，
    /// 每个实例各自 new/Dispose 会在高并发下耗尽套接字（socket 残留 TIME_WAIT），
    /// 微软官方推荐整个进程共用一个静态实例。这里一个 ViewModel 一个实例虽不至于出错，
    /// 但静态共享更简单也更符合推荐做法。
    /// </summary>
    private static readonly System.Net.Http.HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private YktSession? _session;
    private IYktApi? _api;
    private LocalState _localState = new();
    private SyncResult? _lastResult;

    /// <summary>同步后的全量事项，筛选只从这里取，切换导航不再打网络。</summary>
    private readonly List<TodoItemViewModel> _cache = [];

    private CancellationTokenSource? _syncCts;

    /// <summary>手动同步与自动同步的互斥锁：同一时刻最多一个同步在用 <see cref="_api"/>。</summary>
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    /// <summary>取消正在等待的登录检测（自动检测可被「重新检测登录」打断）。</summary>
    private CancellationTokenSource? _loginCts;

    /// <summary>
    /// 是否有同步正在进行。
    /// <para>
    /// 刻意与 <see cref="IsBusy"/> 分开：<see cref="IsBusy"/> 是给界面看的（登录检测也会置位），
    /// 而同步的重入判断必须只看同步自己。早先 <see cref="RefreshAsync"/> 用 <c>IsBusy</c> 判断，
    /// 导致「登录成功后立刻同步」时（此时 IsBusy 仍为 true）同步直接把自己取消掉，
    /// 界面永远停在「同步中」且列表为空。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _isSyncing;

    /// <summary>
    /// 正在进行的耗时操作数量。<see cref="IsBusy"/> 由它派生：
    /// 登录与同步会重叠，用单个布尔量会互相把对方的「忙碌」清掉。
    /// </summary>
    private int _busyScope;

    private void EnterBusy()
    {
        _busyScope++;
        IsBusy = _busyScope > 0;
    }

    private void ExitBusy()
    {
        _busyScope = Math.Max(0, _busyScope - 1);
        IsBusy = _busyScope > 0;
    }

    public MainViewModel() : this(new SessionStore(), new LocalStateStore())
    {
    }

    public MainViewModel(ISessionStore sessionStore, ILocalStateStore localStateStore, ITodoCacheStore? todoCacheStore = null)
    {
        _sessionStore = sessionStore;
        _localStateStore = localStateStore;
        _todoCacheStore = todoCacheStore ?? new TodoCacheStore();

        NavItems =
        [
            new NavItem("myday", "我的一天", "\uF185", "今天到期与已过期的事项"),
            new NavItem("planned", "计划内", "\uF133", "所有有截止时间的事项，按时间排序"),
            // 图标：方框加一支笔（勾作业的意象）。**不能与「课程」同图标**——
            // 两者原先都是 \uF19D（学士帽），侧栏上完全分不出来。
            new NavItem("homework", "作业", "\uF044", "全部作业及其作答进度"),
            // 课程维度：学习通每门课的未完成任务点数（粒度到小节，不拆成逐条待办）。
            new NavItem("course", "课程", "\uF19D", "学习通各门课的未完成任务点汇总"),
            // 课表：整页覆盖展示（网格需要宽度），内容来自教务系统导入。
            // 图标用黑板：既与「计划内」的日历区分开，也贴合「上课」的意象。
            new NavItem("timetable", "课表", "\uF51B", "周课表（可从教务系统一键导入）"),
            new NavItem("announcement", "公告", "\uF0A1", "课程公告，未读的排在前面"),
            new NavItem("completed", "已完成", "\uF058", "雨课堂判定已完成的作业"),
            new NavItem("all", "全部", "\uF03A", "作业与公告的全部内容"),
        ];

        _selectedNav = NavItems[0];

        // CanShowCourseDetail 是跨对象合成属性，必须转发 List 上的变化才会重求值。
        List.PropertyChanged += OnListPropertyChanged;

        // 课表周次变化时重算网格（数据在 MainViewModel 手里，Timetable 只负责摆放）。
        Timetable.RebuildRequested = RebuildTimetable;

        // 节次时间表编辑器：保存通道接到这里（编辑器只管校验与编辑，落盘与刷新归 ViewModel）。
        PeriodsEditor.SaveRequested = SavePeriodsAsync;
        PeriodsEditor.Load(_scheduleFile.Periods);
        SyncStatus = "尚未同步";

        // 外观：主题、壁纸与背景透明度（持久化在 settings.json）。
        // 先设 ThemeModeIndex 再 ApplyTheme：RequestedThemeVariant 要在窗口显示前就位。
        var settings = AppSettings.Current;
        _themeModeIndex = settings.ThemeModeIndex;
        _wallpaperOpacityPercent = settings.WallpaperOpacityPercent;
        ApplyTheme();
        LoadWallpaper(settings.WallpaperPath);

        // 自动同步：定时在后台跑同步，发现新作业时发系统通知。
        AutoSync = new AutoSyncService(SyncForAutoAsync, Notifier);
        if (!Notifier.IsAvailable && !string.IsNullOrEmpty(Notifier.UnavailableReason))
        {
            SyncStatus = $"系统通知不可用：{Notifier.UnavailableReason}";
        }

        // 上课提醒：轮询课表，在「上课前 N 分钟」发通知。
        // 读的是本地课表（不依赖登录态），所以只要设了学期基准、导入过课表就能工作。
        ClassReminder = new ClassReminderService(
            () => _scheduleFile,
            () => CurrentTermStartDate,
            () => AppSettings.Current.ClassReminderMinutes,
            Notifier);
        if (ClassReminderEnabled) _ = ClassReminder.StartAsync();
    }

    /// <summary>
    /// 上课提醒是否具备工作条件：提前量大于 0、且设了学期第一周。
    /// <para>
    /// 课表只有周次，没有学期基准就算不出「今天第几节是几点」，也就无从提醒。
    /// </para>
    /// </summary>
    private bool ClassReminderEnabled =>
        AppSettings.Current.ClassReminderMinutes > 0
        && !string.IsNullOrWhiteSpace(CurrentTermStartDate);

    /// <summary>
    /// 自动同步用的同步入口：在后台线程上被 <see cref="AutoSyncService"/> 的循环调用。
    /// 只负责把数据拉回来并落到界面；「新作业」的判定与通知由
    /// <see cref="AutoSyncService.Observe"/> 统一负责（手动同步也走它）。
    /// </summary>
    private async Task<SyncResult?> SyncForAutoAsync(CancellationToken ct)
    {
        // 与手动同步互斥：手动同步在途时跳过本轮、等下一个间隔，
        // 不并发跑同一个 _api。
        if (!await _syncGate.WaitAsync(0, ct).ConfigureAwait(false)) return null;

        try
        {
            var api = _api;
            if (api is null && _chaoxingCookie is null && _pigaiCookie is null) return null;

            // 网络与解析放后台线程，与 RefreshAsync 保持一致。
            var (result, localState) = await Task.Run(async () =>
            {
                var r = api is not null
                    ? await new SyncService(api).SyncAsync(_cacheFile, null, ct).ConfigureAwait(false)
                    : new SyncResult();
                var ls = await _localStateStore.LoadAsync(ct).ConfigureAwait(false);
                return (r, ls);
            }, ct).ConfigureAwait(false);

            // 学习通同步（已登录时）。
            var cxWarnings = new List<string>();
            // 用同一个基准时刻：界面展示的绝对截止时间与落盘的「基准 + 相对量」必须同源。
            var cxSyncedAt = DateTimeOffset.Now;
            var (cxItems, cxForCache, cxCourses) =
                await SyncChaoxingAsync(cxWarnings, cxSyncedAt, ct).ConfigureAwait(false);

            // 批改网同步（已登录时）。
            var pgWarnings = new List<string>();
            var pgItems = await SyncPigaiAsync(pgWarnings, ct).ConfigureAwait(false);

            // 两路一起合并且只调用一次（见 MergeExtraSources 的说明）。
            cxWarnings.AddRange(pgWarnings);
            result = MergeExtraSources(result, cxItems, cxForCache, cxCourses, pgItems, cxWarnings);

            ct.ThrowIfCancellationRequested();

            // ApplySyncResult 必须在 UI 线程执行（清空/填充 ObservableCollection、触发 Synced）。
            // 自动同步循环跑在线程池上、没有同步上下文可回，必须显式派发到 UI 线程。
            await Dispatcher.UIThread.InvokeAsync(() => ApplySyncResult(result, localState));

            return result;
        }
        catch (YktAuthExpiredException)
        {
            // 与手动同步一致：登录态失效必须浮到界面，而不是只在循环日志里空转。
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                LoginStatusText = "登录已过期";
                AuthWarning = "雨课堂登录态已失效，请到 设置 → 账号 重新登录。";
                _api?.Dispose();
                _api = null;
                NotifyAccountChanged();
            });
            // 停掉自动同步。不能在循环里 await StopAsync（它会等待循环自己），
            // 火忘即可：令牌取消后循环本轮抛出、不再进入下一轮。
            _ = AutoSync.StopAsync();
            throw;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    // ---------------- 外观：壁纸与背景透明度 ----------------

    /// <summary>当前壁纸（null = 不用壁纸，窗口显示桌面磨砂）。</summary>
    [ObservableProperty]
    private Bitmap? _wallpaperImage;

    /// <summary>侧边栏是否收起（宽屏手动切换；移动端不用列宽收起，用覆盖式抽屉）。</summary>
    [ObservableProperty]
    private bool _sidebarCollapsed;

    /// <summary>移动端的覆盖式抽屉是否展开（宽屏下恒为 false）。</summary>
    [ObservableProperty]
    private bool _drawerOpen;

    /// <summary>
    /// 侧边栏列宽：收起时归零，展开时取记忆的宽度。
    /// 注意不用绑定驱动 ColumnDefinition.Width——GridSplitter 拖动会用 SetValue
    /// 覆盖本地值，把绑定顶掉；宽度由 TodoShellView 代码后置显式应用。
    /// </summary>
    public Avalonia.Controls.GridLength SidebarColumnWidth =>
        SidebarCollapsed
            ? new Avalonia.Controls.GridLength(0)
            : new Avalonia.Controls.GridLength(AppSettings.Current.SidebarWidth);

    /// <summary>待办列表栏宽（详情栏吃剩余空间）。同样由视图代码后置应用。</summary>
    public Avalonia.Controls.GridLength ListColumnWidth =>
        new(AppSettings.Current.ListWidth);

    partial void OnSidebarCollapsedChanged(bool value) => OnPropertyChanged(nameof(SidebarColumnWidth));

    /// <summary>切换侧边栏收起/展开（宽屏）。</summary>
    public void ToggleSidebar()
    {
        // 被分隔条拖到 0 的情况：直接恢复记忆宽度，而不是先「收起」一轮。
        if (!SidebarCollapsed && AppSettings.Current.SidebarWidth < 1)
        {
            AppSettings.Current.SidebarWidth = 252;
            OnPropertyChanged(nameof(SidebarColumnWidth));
            return;
        }
        SidebarCollapsed = !SidebarCollapsed;
    }

    /// <summary>
    /// GridSplitter 拖动结束后由视图调用：把当前栏宽记入设置。
    /// 侧边栏收起态（0）与过小值不落盘，避免把「隐藏」误存为宽度。
    /// </summary>
    public void PersistPaneWidths(Avalonia.Controls.GridLength sidebar, Avalonia.Controls.GridLength list)
    {
        var changed = false;
        if (!SidebarCollapsed
            && sidebar.GridUnitType == Avalonia.Controls.GridUnitType.Pixel
            && sidebar.Value >= 120
            && Math.Abs(sidebar.Value - AppSettings.Current.SidebarWidth) > 0.5)
        {
            AppSettings.Current.SidebarWidth = sidebar.Value;
            changed = true;
        }

        if (list.GridUnitType == Avalonia.Controls.GridUnitType.Pixel
            && list.Value >= 220
            && Math.Abs(list.Value - AppSettings.Current.ListWidth) > 0.5)
        {
            AppSettings.Current.ListWidth = list.Value;
            changed = true;
        }

        if (changed) SaveSettingsDebounced();
    }

    [ObservableProperty]
    private bool _hasWallpaper;

    /// <summary>白色遮罩的不透明度（1 = 完全不透），由滑块百分比换算，最低 0.15。</summary>
    [ObservableProperty]
    private double _veilOpacity = 0.7;

    /// <summary>背景透明度滑块值（0–85，越大越透）。</summary>
    [ObservableProperty]
    private double _wallpaperOpacityPercent;

    // 三个面板的画刷随滑块与主题联动：透明度越高，面板越透，壁纸/桌面磨砂透得越多。
    // 纯色文字与徽标不受影响，保证可读性。
    [ObservableProperty]
    private SolidColorBrush _sidebarBrush = Translucent(PanelSidebarLight, 0.7);

    [ObservableProperty]
    private SolidColorBrush _listBrush = Translucent(PanelListLight, 0.7);

    [ObservableProperty]
    private SolidColorBrush _surfaceBrush = Translucent(PanelSurfaceLight, 0.7);

    /// <summary>遮罩画刷：浅色主题用白、深色主题用黑（不透明度由滑块控制）。</summary>
    [ObservableProperty]
    private SolidColorBrush _veilBrush = SolidColorBrush.Parse("#FFFFFF");

    /// <summary>主题：0 = 浅色，1 = 深色，2 = 跟随系统。</summary>
    [ObservableProperty]
    private int _themeModeIndex = 2;

    // ---------------- 课表（教务系统导入） ----------------

    private readonly IScheduleStore _scheduleStore = new ScheduleStore();
    private readonly JwProfileStore _jwProfileStore = new JwProfileStore();

    /// <summary>课表文件（条目 + 节次时间表）。字段初始化不能引用实例成员，故此处先留空，
    /// 由 <see cref="InitializeAsync"/> 读盘时填入（读不到时 store 会给出默认值）。</summary>
    private ScheduleFile _scheduleFile = new() { Items = [], Periods = [] };

    /// <summary>教务系统配置（只存网址）。</summary>
    private JwProfile _jwProfile = new();

    /// <summary>课表周视图的状态。</summary>
    public TimetableViewModels Timetable { get; } = new();

    /// <summary>是否显示课表页（与设置页同款：整页覆盖，不是弹窗）。</summary>
    [ObservableProperty]
    private bool _timetableOpen;

    /// <summary>
    /// 待办区（列表 + 详情）是否可见。
    /// <para>
    /// **必须是合成条件**：设置页与课表页都是整页覆盖，任一打开时待办区都要让位。
    /// 只门控其中一个会漏——实测踩到过两次同类问题（先只判 <c>SettingsOpen</c>，
    /// 加了课表页之后待办列表就透在课表下面）。这类「各判各的」只要新增一个覆盖页就会再犯，
    /// 所以统一收在一个属性里表达。
    /// </para>
    /// </summary>
    public bool ShowTodoPane => !SettingsOpen && !TimetableOpen && !JwImportOpen;

    partial void OnTimetableOpenChanged(bool value) => OnPropertyChanged(nameof(ShowTodoPane));

    partial void OnSettingsOpenChanged(bool value) => OnPropertyChanged(nameof(ShowTodoPane));

    partial void OnJwImportOpenChanged(bool value) => OnPropertyChanged(nameof(ShowTodoPane));

    /// <summary>课表页的状态/结果提示（导入结果、错误原因等）。</summary>
    [ObservableProperty]
    private string _timetableStatus = string.Empty;

    /// <summary>教务系统登录页网址（设置页可编辑）。</summary>
    [ObservableProperty]
    private string _jwLoginUrlInput = string.Empty;

    /// <summary>
    /// 学期第一周的周一（设置页可编辑，<c>yyyy-MM-dd</c>）。
    /// <para>改动立即落盘并重建课表网格——「周次 → 日期」的换算完全依赖它。</para>
    /// </summary>
    [ObservableProperty]
    private string _termStartDateInput = AppSettings.Current.TermStartDate ?? string.Empty;

    partial void OnTermStartDateInputChanged(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        // 允许清空；填了就必须是能解析的日期，否则不落盘——写坏基准会让整张课表的日期都错。
        if (trimmed.Length > 0 && Services.Timetable.ParseDate(trimmed) is null) return;

        var normalized = trimmed.Length > 0 ? trimmed : null;
        if (AppSettings.Current.TermStartDate == normalized) return;

        AppSettings.Current.TermStartDate = normalized;
        AppSettings.Current.Save();
        RebuildTimetable();

        // 学期基准是上课提醒的前提：填上就该开始提醒，清掉就该停。
        RaiseClassReminderHint();
        if (ClassReminderEnabled) _ = ClassReminder.StartAsync();
        else _ = ClassReminder.StopAsync();
    }

    /// <summary>是否已配置教务网址（决定「导入课表」能否直接打开窗口）。</summary>
    public bool HasJwProfile => !string.IsNullOrWhiteSpace(_jwProfile.LoginUrl);

    /// <summary>
    /// 教务系统登录页地址的**权威值**（优先用户正在编辑的输入框，回落到已保存值）。
    /// <para>
    /// 两者可能不一致：设置页没打开过时输入框应等于已保存值（<see cref="InitializeAsync"/> 会赋值），
    /// 但初始化异常、或用户清了输入框却没保存时就会分叉。导入窗口要的是「一个能用的地址」，
    /// 所以这里取输入框的当前值、为空再回落到已保存值——否则会出现「窗口开了但一直空白、
    /// 也没有任何提示」这种最难排查的状态。
    /// </para>
    /// </summary>
    public string JwEffectiveLoginUrl =>
        string.IsNullOrWhiteSpace(JwLoginUrlInput) ? _jwProfile.LoginUrl ?? string.Empty : JwLoginUrlInput.Trim();

    private static readonly Color PanelSidebarLight = Color.Parse("#F3F2F1");
    private static readonly Color PanelListLight = Color.Parse("#FAF9F8");
    private static readonly Color PanelSurfaceLight = Color.Parse("#FFFFFF");
    private static readonly Color PanelSidebarDark = Color.Parse("#282828");
    private static readonly Color PanelListDark = Color.Parse("#232323");
    private static readonly Color PanelSurfaceDark = Color.Parse("#2E2E2E");

    private static bool IsDark =>
        (Avalonia.Application.Current?.ActualThemeVariant ?? Avalonia.Styling.ThemeVariant.Light)
        == Avalonia.Styling.ThemeVariant.Dark;

    partial void OnThemeModeIndexChanged(int value)
    {
        AppSettings.Current.ThemeModeIndex = value;
        SaveSettingsDebounced();
        ApplyTheme();
    }

    /// <summary>应用当前主题设置，并重算面板/遮罩画刷。</summary>
    private void ApplyTheme()
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = ThemeModeIndex switch
            {
                0 => Avalonia.Styling.ThemeVariant.Light,
                1 => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default,
            };
        }
        RefreshAppearanceBrushes();
    }

    /// <summary>按当前主题与透明度重算四个外观画刷。</summary>
    private void RefreshAppearanceBrushes()
    {
        var dark = IsDark;
        var w = PercentToVeil(WallpaperOpacityPercent);
        VeilOpacity = w;
        SidebarBrush = Translucent(dark ? PanelSidebarDark : PanelSidebarLight, w);
        ListBrush = Translucent(dark ? PanelListDark : PanelListLight, w);
        SurfaceBrush = Translucent(dark ? PanelSurfaceDark : PanelSurfaceLight, w);
        VeilBrush = SolidColorBrush.Parse(dark ? "#000000" : "#FFFFFF");
    }

    /// <summary>滑块百分比换算成「白度」（面板与遮罩共用的不透明度，最低 0.15）。</summary>
    private static double PercentToVeil(double percent) =>
        Math.Max(0.15, 1.0 - Math.Clamp(percent, 0, 85) / 100.0);

    private static SolidColorBrush Translucent(Color c, double whiteness) =>
        new(new Color((byte)Math.Round(Math.Clamp(whiteness, 0.15, 1.0) * 255), c.R, c.G, c.B));

    partial void OnWallpaperOpacityPercentChanged(double value)
    {
        AppSettings.Current.WallpaperOpacityPercent = value;
        RefreshAppearanceBrushes();
        SaveSettingsDebounced();
    }

    /// <summary>设置壁纸（视图在文件选择后调用）；加载失败保持原状并提示。</summary>
    public void SetWallpaper(string path)
    {
        if (!LoadWallpaper(path))
        {
            SyncStatus = "壁纸加载失败：文件不存在或不是有效的图片";
            return;
        }
        AppSettings.Current.WallpaperPath = path;
        AppSettings.Current.Save();
        SyncStatus = "壁纸已更新";
    }

    /// <summary>清除壁纸，回到桌面磨砂背景。</summary>
    public void ClearWallpaper()
    {
        AppSettings.Current.WallpaperPath = null;
        AppSettings.Current.Save();
        LoadWallpaper(null);
        SyncStatus = "已清除壁纸";
    }

    private bool LoadWallpaper(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            WallpaperImage = null;
            HasWallpaper = false;
            return true;
        }
        try
        {
            WallpaperImage = new Bitmap(path);
            HasWallpaper = true;
            return true;
        }
        catch (Exception)
        {
            // 文件丢失/损坏/格式不支持都按「无壁纸」处理，不影响启动。
            WallpaperImage = null;
            HasWallpaper = false;
            return false;
        }
    }

    // 滑块拖动会连续触发变更，防抖后再落盘，避免每帧写 settings.json。
    private CancellationTokenSource? _settingsSaveCts;

    private void SaveSettingsDebounced()
    {
        _settingsSaveCts?.Cancel();
        var cts = _settingsSaveCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, cts.Token).ConfigureAwait(false);
                AppSettings.Current.Save();
            }
            catch (OperationCanceledException)
            {
                // 被下一次变更取代，由那一轮负责保存。
            }
        });
    }

    // ---------------- 暴露给视图的状态 ----------------

    public IReadOnlyList<NavItem> NavItems { get; }

    public TodoListViewModel List { get; } = new();

    /// <summary>
    /// 把 <see cref="List"/> 上会影响 <see cref="CanShowCourseDetail"/> 的变化转发上来。
    /// <para>
    /// 该属性是跨对象合成（<c>List.ShowCourseList &amp;&amp; List.HasSelectedCourse</c>），
    /// 绑定时只会订阅到 <c>List</c> 这一个属性名——嵌套属性变了不会自动重求值，
    /// 必须显式转发，否则课程详情面板的显隐会滞后一拍。
    /// </para>
    /// </summary>
    private void OnListPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TodoListViewModel.ShowCourseList)
            or nameof(TodoListViewModel.HasSelectedCourse))
        {
            OnPropertyChanged(nameof(CanShowCourseDetail));
        }
    }

    [ObservableProperty]
    private AppState _state = AppState.NeedsLogin;

    [ObservableProperty]
    private NavItem _selectedNav;

    /// <summary>
    /// 选中导航项变化时记下「上一个不是课表的项」。
    /// <para>
    /// <b>必须在变化的那一刻记，不能等 <see cref="OpenTimetable"/></b>：用户点「课表」时
    /// <c>SelectedNav</c> 已经变成课表了，那时再取「上一个」只会拿到课表自己，
    /// 于是关闭时无从恢复（实测栽在这里）。这个钩子在赋值瞬间触发，拿到的才是真正的来路。
    /// </para>
    /// </summary>
    partial void OnSelectedNavChanged(NavItem value)
    {
        if (value?.Key != "timetable") _navBeforeTimetable = value;
    }

    /// <summary>
    /// 打开课表前选中的导航项；关闭课表时回到它。
    /// <para>
    /// <b>为什么需要它</b>：侧边栏「课表」是导航项，<see cref="OpenTimetable"/> 是靠
    /// <c>SelectedNav.Key == "timetable"</c> 触发的。关闭时只把 <c>TimetableOpen</c> 置回 false
    /// 的话，<c>SelectedNav</c> 仍停在「课表」——列表于是显示一个空的课表筛选，
    /// 看起来就像「按了返回却毫无反应」（实测在 Android 上就栽在这里）。
    /// </para>
    /// </summary>
    private NavItem? _navBeforeTimetable;

    [ObservableProperty]
    private TodoItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _syncStatus = string.Empty;

    /// <summary>首屏列表来自本地缓存时的提示（后台同步完成前保持显示）。</summary>
    [ObservableProperty]
    private string _cacheNote = string.Empty;

    [ObservableProperty]
    private string _statusDetail = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// 是否正在「用户主动发起的」登录检测。
    /// <para>
    /// 与 <see cref="IsBusy"/> 分开是必要的：自动检测也会长时间占用 <see cref="IsBusy"/>，
    /// 而按钮曾经绑定 <c>!IsBusy</c> 作为可点条件——自动检测一开始按钮就被禁用，
    /// 用户点上去毫无反应（表现为「按钮点不动」）。自动检测属于后台行为，不该禁用按钮。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _isCheckingLogin;

    /// <summary>正在后台自动检测登录态（用于提示文案，不禁用任何东西）。</summary>
    [ObservableProperty]
    private bool _isAutoDetecting;

    /// <summary>登录按钮文案：检测中给出反馈，否则提示可重新检测。</summary>
    public string LoginButtonText => IsCheckingLogin ? "检测中…" : "重新检测登录";

    [ObservableProperty]
    private bool _hasWarnings;

    [ObservableProperty]
    private string _warningText = string.Empty;

    [ObservableProperty]
    private string _loginStatusText = "尚未登录";

    /// <summary>用户名（实名，拿不到时退回占位）。</summary>
    [ObservableProperty]
    private string _userName = "未登录";

    /// <summary>用户副标题：学校 · 学号。</summary>
    [ObservableProperty]
    private string _userSubtitle = string.Empty;

    /// <summary>头像地址；为空时界面显示姓名首字。</summary>
    [ObservableProperty]
    private string? _userAvatarUrl;

    /// <summary>
    /// 已解码的头像位图。
    /// <para>
    /// 不直接把 URL 绑给 <c>Image.Source</c>：那种写法由控件在渲染时才去异步加载位图，
    /// 首帧必然是空的（离屏渲染截图里永远是空白），而且加载失败没有任何反馈。
    /// 这里自己下载并解码，成功后才让界面显示图片；失败则回落到姓名首字。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _userAvatar;

    /// <summary>是否有头像可显示（决定界面用图片还是首字占位）。</summary>
    public bool HasAvatar => UserAvatar is not null;

    /// <summary>无头像时显示的首字。</summary>
    public string UserInitial => string.IsNullOrWhiteSpace(UserName) ? "作" : UserName[..1];

    partial void OnUserAvatarChanged(Avalonia.Media.Imaging.Bitmap? value)
        => OnPropertyChanged(nameof(HasAvatar));

    partial void OnUserNameChanged(string value) => OnPropertyChanged(nameof(UserInitial));

    [ObservableProperty]
    private string _unreadBadge = string.Empty;

    [ObservableProperty]
    private bool _showUnreadBadge;

    /// <summary>是否改用「粘贴 Cookie」兜底登录（扫码窗口不可用时）。</summary>
    [ObservableProperty]
    private bool _showCookieInput;

    /// <summary>
    /// 登录覆盖层当前打开的账号类型："ykt"（雨课堂）/ "chaoxing"（学习通）/
    /// "pigai"（批改网）/ null（关闭）。
    /// 登录不再是启动门禁，而是从设置页进入的覆盖页。
    /// </summary>
    [ObservableProperty]
    private string? _loginPageOpen;

    /// <summary>雨课堂登录覆盖层是否打开。</summary>
    public bool YktLoginOpen => LoginPageOpen == "ykt";

    /// <summary>学习通登录覆盖层是否打开。</summary>
    public bool ChaoxingLoginOpen => LoginPageOpen == "chaoxing";

    /// <summary>批改网登录覆盖层是否打开。</summary>
    public bool PigaiLoginOpen => LoginPageOpen == "pigai";

    partial void OnLoginPageOpenChanged(string? value)
    {
        OnPropertyChanged(nameof(YktLoginOpen));
        OnPropertyChanged(nameof(ChaoxingLoginOpen));
        OnPropertyChanged(nameof(PigaiLoginOpen));
    }

    /// <summary>打开雨课堂登录页（设置页「账号」）。</summary>
    public void OpenYktLogin() => LoginPageOpen = "ykt";

    /// <summary>打开学习通登录页。</summary>
    public void OpenChaoxingLogin() => LoginPageOpen = "chaoxing";

    /// <summary>打开批改网登录页。</summary>
    public void OpenPigaiLogin() => LoginPageOpen = "pigai";

    /// <summary>关闭登录覆盖层。</summary>
    public void CloseLoginPage() => LoginPageOpen = null;

    /// <summary>雨课堂登录态失效时的提示（列表顶部横幅）。</summary>
    [ObservableProperty]
    private string _authWarning = string.Empty;

    /// <summary>是否还没有雨课堂账号（列表显示「去登录」引导）。</summary>
    public bool NoYktAccount => _api is null;

    /// <summary>会话/API 实例变化后刷新「去登录」引导的可见性。</summary>
    private void NotifyAccountChanged() => OnPropertyChanged(nameof(NoYktAccount));

    /// <summary>打开设置页并切到「账号」选项卡。</summary>
    public void OpenAccountSettings()
    {
        SettingsTabIndex = 1;
        SettingsOpen = true;
        TimetableOpen = false;
        DrawerOpen = false;
    }

    // ---------------- 学习通（超星） ----------------

    // ---------------- 批改网（pigai.org） ----------------

    private readonly PigaiSessionStore _pigaiStore = new();

    /// <summary>当前批改网会话的整条 Cookie（同步用）。null = 未登录。</summary>
    private string? _pigaiCookie;

    [ObservableProperty]
    private string _pigaiStatusText = "未登录";

    /// <summary>是否已有批改网登录态。</summary>
    [ObservableProperty]
    private bool _hasPigaiSession;

    /// <summary>登录成功：保存批改网会话并关闭登录覆盖层。</summary>
    public async Task AdoptPigaiSessionAsync(PigaiSessionFile session)
    {
        PigaiLoginService.Log($"adopt: 保存会话 account={session.Account}");
        await _pigaiStore.SaveAsync(session).ConfigureAwait(true);
        _pigaiCookie = session.CookieHeader;
        HasPigaiSession = true;
        PigaiStatusText = $"已登录（{session.Account} · {session.SavedAt:MM-dd HH:mm}）";
        LoginPageOpen = null;
        // 登录成功顺手同步一次，让未完成作文立刻出现在列表里。
        await RefreshAsync().ConfigureAwait(true);
        PigaiLoginService.Log($"adopt: 同步完成 State={State} SyncStatus={SyncStatus}");
    }

    /// <summary>退出批改网：清本地会话文件，并尽力清掉 WebView 里的 pigai Cookie。</summary>
    public async Task LogoutPigaiAsync()
    {
        await _pigaiStore.ClearAsync().ConfigureAwait(true);
        _pigaiCookie = null;
        HasPigaiSession = false;
        PigaiStatusText = "未登录";

        // 必须清批改网自己的 WebView：三个登录页共用同一个 Cookie 存储，
        // 只删会话文件的话，重新打开登录页会被自动捕获立刻接管回来。
        try
        {
            if (PigaiWebViewAccessor?.Invoke() is { } webView)
                await PigaiLoginService.ClearCookiesAsync(webView).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PigaiStatusText = $"已退出登录（但 WebView Cookie 未清干净：{ex.Message}）";
        }
    }

    /// <summary>启动时恢复批改网登录态的状态行。</summary>
    private async Task RestorePigaiAsync()
    {
        var saved = await _pigaiStore.LoadAsync().ConfigureAwait(true);
        PigaiLoginService.Log($"restore: 会话文件={_pigaiStore.Location} 读到={saved is not null}");
        if (saved is { HasValue: true })
        {
            _pigaiCookie = saved.CookieHeader;
            HasPigaiSession = true;
            PigaiStatusText = $"已登录（{saved.Account} · {saved.SavedAt:MM-dd HH:mm}）";
        }
    }

    /// <summary>
    /// 同步批改网的未完成作文。未登录返回空；失败时把原因写进 warnings
    /// 并在状态行/设置页提示（登录态失效时要求重新登录）。
    /// </summary>
    private async Task<List<TodoItem>> SyncPigaiAsync(ICollection<string> warnings, CancellationToken ct)
    {
        if (_pigaiCookie is null) return [];
        try
        {
            var items = await PigaiSyncService.SyncAsync(_pigaiCookie, ct).ConfigureAwait(false);
            PigaiLoginService.Log($"sync: 批改网返回 {items.Count} 条未完成作文");
            return items.ToList();
        }
        catch (PigaiAuthExpiredException)
        {
            PigaiLoginService.Log("sync: 批改网登录态已失效");
            _pigaiCookie = null;
            HasPigaiSession = false;
            PigaiStatusText = "登录已过期，请到 设置 → 账号 重新登录";
            warnings.Add("批改网登录态已失效，请重新登录。");
            return [];
        }
        catch (PigaiApiException ex)
        {
            PigaiLoginService.Log($"sync: 批改网同步失败 {ex.Message}");
            warnings.Add($"批改网同步失败：{ex.Message}");
            return [];
        }
    }

    // ---------------- 学习通（超星） ----------------

    private readonly ChaoxingSessionStore _chaoxingStore = new();

    /// <summary>当前学习通会话的整条 Cookie（同步用）。null = 未登录。</summary>
    private string? _chaoxingCookie;

    [ObservableProperty]
    private string _chaoxingStatusText = "未登录";

    /// <summary>是否已有学习通登录态。</summary>
    [ObservableProperty]
    private bool _hasChaoxingSession;

    /// <summary>登录成功：保存学习通会话并关闭登录覆盖层。</summary>
    public async Task AdoptChaoxingSessionAsync(ChaoxingSessionFile session)
    {
        await _chaoxingStore.SaveAsync(session).ConfigureAwait(true);
        _chaoxingCookie = session.CookieHeader;
        HasChaoxingSession = true;
        ChaoxingStatusText = $"已登录（uid {session.Uid} · {session.SavedAt:MM-dd HH:mm}）";
        LoginPageOpen = null;
        // 登录成功顺手同步一次，让学习通作业立刻出现在列表里。
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>退出学习通：清本地会话文件，并尽力清掉 WebView 里的 chaoxing Cookie。</summary>
    public async Task LogoutChaoxingAsync()
    {
        // 不用 async void：SettingsView 只在点击时 await 一次即可，
        // async void 会让这里的 IO / CookieManager 异常无法被调用方捕获，
        // 逃逸到 UI 线程后直接终止进程（本应用没有全局 UI 异常处理器）。
        await _chaoxingStore.ClearAsync().ConfigureAwait(true);
        _chaoxingCookie = null;
        HasChaoxingSession = false;
        ChaoxingStatusText = "未登录";

        // 必须清学习通自己的 WebView，不能用 WebViewAccessor（那个指向雨课堂的
        // LoginWebView）。两个登录页共用同一个 WebView2 Cookie 存储：只删会话文件
        // 而留着超星 cookie 的话，重新打开登录页会被 TryCaptureAsync 立刻接管回来，
        // 等于退出登录没生效——正是雨课堂路径在 LogoutAsync 里专门注释警告过的坑。
        try
        {
            if (ChaoxingWebViewAccessor?.Invoke() is { } webView)
                await ChaoxingLoginService.ClearCookiesAsync(webView).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 清 Cookie 失败不影响已经清掉的本地会话；如实告知而不是静默。
            ChaoxingStatusText = $"已退出登录（但 WebView Cookie 未清干净：{ex.Message}）";
        }
    }

    /// <summary>启动时恢复学习通登录态的状态行。</summary>
    private async Task RestoreChaoxingAsync()
    {
        var saved = await _chaoxingStore.LoadAsync().ConfigureAwait(true);
        if (saved is { HasValue: true })
        {
            _chaoxingCookie = saved.CookieHeader;
            HasChaoxingSession = true;
            ChaoxingStatusText = $"已登录（uid {saved.Uid} · {saved.SavedAt:MM-dd HH:mm}）";
        }
    }

    /// <summary>
    /// 同步学习通的作业与考试。未登录返回空；失败时把原因写进 warnings
    /// 并在状态行/设置页提示（登录态失效时要求重新登录）。
    /// </summary>
    /// <param name="syncedAt">
    /// 本次同步的时刻。由调用方传入而不是在这里取 <see cref="DateTimeOffset.Now"/>：
    /// 学习通的截止时间是「同步时刻 + 页面上的相对剩余时间」，界面用的绝对时间与
    /// 缓存用的「基准 + 相对量」必须是同一个基准，否则两者会互相漂移。
    /// </param>
    private async Task<(IReadOnlyList<TodoItem> Items, IReadOnlyList<TodoItem> ForCache,
        IReadOnlyList<ChaoxingCourse> Courses)>
        SyncChaoxingAsync(ICollection<string> warnings, DateTimeOffset syncedAt, CancellationToken ct)
    {
        if (_chaoxingCookie is null) return ([], [], []);
        try
        {
            var (items, forCache) = await ChaoxingSyncService
                .SyncAsync(_chaoxingCookie, syncedAt, ct).ConfigureAwait(false);

            // 课程维度的任务点汇总。它自己内部已容错（单课失败只跳过、不抛），
            // 只有登录态失效才抛出来。
            var (courses, courseWarnings) = await ChaoxingSyncService
                .SyncCoursesAsync(_chaoxingCookie, null, ct).ConfigureAwait(false);
            foreach (var w in courseWarnings) warnings.Add(w);

            return (items, forCache, courses);
        }
        catch (ChaoxingAuthExpiredException)
        {
            _chaoxingCookie = null;
            HasChaoxingSession = false;
            ChaoxingStatusText = "登录已过期，请到 设置 → 账号 重新登录";
            warnings.Add("学习通登录态已失效，请重新登录。");
            return ([], [], []);
        }
        catch (ChaoxingApiException ex)
        {
            warnings.Add($"学习通同步失败：{ex.Message}");
            return ([], [], []);
        }
    }

    /// <summary>
    /// 把「附加数据源」（学习通、批改网）的条目与其告警合并进同步结果
    /// （两路都没有条目、也没有告警时原样返回）。
    /// <para>
    /// 学习通条目同时进入 <see cref="SyncResult.HomeworkForCache"/> 与
    /// <see cref="SyncResult.ChaoxingForCache"/>；批改网同理进入
    /// <see cref="SyncResult.PigaiForCache"/>。前者是待办缓存（todos.json）的
    /// 统一作业列表——不写进去的话，冷启动的「本地缓存秒开」永远缺这两路作业。
    /// </para>
    /// <para>
    /// 两路合并**必须在同一个方法里做完**：分成两次调用的话，后一次会以自己那份
    /// <see cref="SyncResult"/> 为基（各路 ForCache 字段在每次新建时都会被覆盖），
    /// 把先合并进去的那一路的缓存字段丢掉。
    /// </para>
    /// </summary>
    private static SyncResult MergeExtraSources(
        SyncResult result,
        IReadOnlyList<TodoItem> cxItems,
        IReadOnlyList<TodoItem> cxForCache,
        IReadOnlyList<ChaoxingCourse> cxCourses,
        IReadOnlyList<TodoItem> pgItems,
        IReadOnlyList<string> extraWarnings)
    {
        // 即使两路都没条目，课程汇总也可能有内容（例如作业都做完了但还有任务点）。
        if (cxItems.Count == 0 && pgItems.Count == 0 && cxCourses.Count == 0
            && extraWarnings.Count == 0) return result;
        return new SyncResult
        {
            Homework = result.Homework.Concat(cxItems).Concat(pgItems).ToList(),
            HomeworkForCache = result.HomeworkForCache.Concat(cxForCache).Concat(pgItems).ToList(),
            ChaoxingForCache = cxForCache,
            PigaiForCache = pgItems,
            ChaoxingCourses = cxCourses,
            Announcements = result.Announcements,
            CourseCount = result.CourseCount,
            ActivityCount = result.ActivityCount,
            UnreadNotificationCount = result.UnreadNotificationCount,
            SyncedAt = result.SyncedAt,
            Warnings = result.Warnings.Concat(extraWarnings).ToList(),
            CourseSignatures = result.CourseSignatures,
            CourseFullSyncedAt = result.CourseFullSyncedAt,
            ReusedCourses = result.ReusedCourses,
            ArchivedCourses = result.ArchivedCourses,
        };
    }

    /// <summary>打开课程表（按当前日期定位到本周）。</summary>
    public void OpenTimetable()
    {
        SettingsOpen = false;
        TimetableOpen = true;
        GotoCurrentTimetableWeek();
    }

    /// <summary>
    /// 关闭课表，回到打开它之前的那个列表。
    /// <para>
    /// 必须一并把 <c>SelectedNav</c> 从「课表」挪走：只关页面的话列表会停在一个空的课表筛选上，
    /// 用户看到的是「按了返回/关闭却什么都没变」。
    /// </para>
    /// </summary>
    public void CloseTimetable()
    {
        var restore = _navBeforeTimetable;
        _navBeforeTimetable = null;

        // 先切导航项再关页面。这里必须真的把 SelectedNav 从「课表」挪走：
        // 下面的 OnNavChanged 看到 key == "timetable" 就会**重新把课表打开**，
        // 于是「关闭」表现为毫无反应（实测栽在这里）。
        if (restore is not null && !ReferenceEquals(restore, SelectedNav)) SelectedNav = restore;
        else if (SelectedNav?.Key == "timetable")
        {
            // 没记录到来路（例如直接落在课表页）→ 退回第一个列表，别停在课表上。
            SelectedNav = NavItems[0];
        }

        TimetableOpen = false;
        TimetableStatus = string.Empty;
        OnNavChanged();
    }

    /// <summary>周次加减（1 基，夹在 1..<see cref="Services.Timetable.MaxWeek"/>）。</summary>
    public void ShiftTimetableWeek(int delta)
    {
        var next = Math.Clamp(Timetable.WeekNumber + delta, 1, Services.Timetable.MaxWeek);
        if (next == Timetable.WeekNumber)
        {
            // 已经到头：仍重建一次，保证界面与数据一致（不依赖「值变化」这个副作用）。
            RebuildTimetable();
            return;
        }
        Timetable.WeekNumber = next;
    }

    /// <summary>跳到当前日期所在的周（未配置学期第一周时回到第 1 周）。</summary>
    public void GotoCurrentTimetableWeek()
    {
        var week = Services.Timetable.ComputeWeekNumber(
            DateTime.Now.ToString("yyyy-MM-dd"), CurrentTermStartDate);
        var target = Math.Clamp(week ?? 1, 1, Services.Timetable.MaxWeek);

        // 赋值触发重建只在「值真的变了」时才发生（OnWeekNumberChanged）。
        // 首次打开课表时默认周次就是 1，若算出来也是 1 就不会触发，
        // 表现是「课表页一片空白」——实测踩到。所以这里**显式重建**，不依赖副作用。
        Timetable.WeekNumber = target;
        RebuildTimetable();
    }

    /// <summary>
    /// 按当前周次重建网格。
    /// <para>
    /// 未配置「学期第一周」时不编造日期，只按周次展示——见 <see cref="TimetableViewModels.Rebuild"/>。
    /// </para>
    /// </summary>
    private void RebuildTimetable() => Timetable.Rebuild(
        _scheduleFile.Items, Timetable.WeekNumber, CurrentTermStartDate, _scheduleFile.Periods, IsDark);

    /// <summary>学期第一周周一（yyyy-MM-dd）；未配置返回 null。</summary>
    private static string? CurrentTermStartDate =>
        string.IsNullOrWhiteSpace(AppSettings.Current.TermStartDate) ? null : AppSettings.Current.TermStartDate;

    /// <summary>课表条目数（导航角标用）。</summary>
    public int ScheduleItemCount => _scheduleFile.Items.Count;

    /// <summary>
    /// 节次时间表编辑器（设置 → 课表）。
    /// <para>
    /// 时间表是周视图、待办、提醒三处时间的唯一来源，所以校验与落盘都放在
    /// <see cref="SavePeriods"/> 里统一把关：**先校验、再落盘**，非法就抛出具体原因。
    /// </para>
    /// </summary>
    public PeriodsEditorViewModel PeriodsEditor { get; } = new();

    /// <summary>
    /// 保存节次时间表（由编辑器在校验通过后调用）。
    /// <para>
    /// 会再校验一次：编辑器那条路径可能被绕过（以后的批量导入、脚本），
    /// 而写坏时间表会让**整张课表的时间全错**，这道关不能只放在界面层。
    /// </para>
    /// </summary>
    /// <returns>给用户看的结果文案。</returns>
    private async Task<string> SavePeriodsAsync(IReadOnlyList<PeriodSlot> periods)
    {
        // 抛异常而不是返回 false：调用方需要拿到「到底哪里不合法」。
        Services.Timetable.ValidatePeriods(periods);

        _scheduleFile.Periods = periods
            .Select(p => new PeriodSlot { Period = p.Period, StartTime = p.StartTime, EndTime = p.EndTime })
            .ToList();

        // 时间表变了，条目的起止时间要跟着重算，否则课块上还是旧时间。
        foreach (var item in _scheduleFile.Items)
        {
            var time = Services.Timetable.ResolveSessionTime(item, _scheduleFile.Periods);
            item.StartTime = time.StartTime;
            item.EndTime = time.EndTime;
        }

        RebuildTimetable();

        // 落盘失败要说清楚：内存里已经生效、下次启动会回退，这种不一致必须让用户知道。
        // 这里正常 await（不 ConfigureAwait(false)）：续体要回到 UI 线程继续刷界面。
        try
        {
            await _scheduleStore.SaveAsync(_scheduleFile).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"写入课表文件失败（改动只在本次运行内有效）：{ex.Message}", ex);
        }

        var enabled = periods.Count(p => !Services.Timetable.IsPeriodDisabled(p));
        var disabled = periods.Count - enabled;
        return disabled > 0
            ? $"时间表已保存：{periods.Count} 节（{enabled} 节启用、{disabled} 节停用）。"
            : $"时间表已保存：共 {periods.Count} 节。";
    }

    /// <summary>打开教务系统导入（网页抓取）。</summary>
    public void OpenJwImport()
    {
        // 未配置学校网址时先引导去设置，而不是打开一个空白窗口。
        if (!HasJwProfile)
        {
            TimetableStatus = "请先在 设置 → 账号 填写学校教务系统登录页网址，然后再导入。";
            return;
        }

        JwImportOpen = true;
        TimetableStatus = string.Empty;
    }

    /// <summary>教务导入页是否打开。</summary>
    [ObservableProperty]
    private bool _jwImportOpen;

    /// <summary>关闭教务导入页。</summary>
    public void CloseJwImport()
    {
        JwImportOpen = false;
        JwDialogAccessor = null;
    }

    /// <summary>
    /// 教务页弹窗的访问器（由 <see cref="Views.JwImportView"/> 在弹窗创建后注入）。
    /// <para>
    /// 用独立弹窗（<c>NativeWebDialog</c>）而不是内嵌控件：与「长江雨课堂登录」同款形态，
    /// 弹窗浮在应用之上，不与应用内容挤在同一块区域——内嵌时实测会与待办列表重叠、
    /// 原生面还会被外层容器裁切。
    /// </para>
    /// 抓取与悬浮球注入都通过它，ViewModel 不直接依赖视图。
    /// </summary>
    public static Func<Avalonia.Controls.NativeWebDialog?>? JwDialogAccessor { get; set; }

    /// <summary>
    /// 教务弹窗的页面就绪闸门（由 <see cref="Views.JwImportView"/> 在弹窗创建后注入）。
    /// <para>
    /// 存在的理由：WebView 在任何页面加载完成之前执行脚本必定失败
    /// （<c>InvalidOperationException: Unable to invoke script before any page was loaded</c>），
    /// 而「抓取当前页课表」按钮在弹窗刚打开时就能被点到。先问闸门一句，
    /// 把「页面还没加载好」变成一句明确的提示，而不是一次注定失败的调用。
    /// </para>
    /// <para>
    /// 与 <see cref="JwDialogAccessor"/> 同生命周期：由视图在创建/关闭弹窗时一并设置。
    /// </para>
    /// </summary>
    public static PageReadyGate? JwPageGate { get; set; }


    /// <summary>当前课表文件（教务抓取流程读取条目与节次表）。</summary>
    public ScheduleFile CurrentSchedule => _scheduleFile;

    /// <summary>
    /// 直接用给定课表覆盖当前数据并重建网格（**不落盘**）。
    /// <para>
    /// 给桌面自检用：课表页要看的是「真实数据摆出来对不对」，
    /// 而正常路径要求先登录教务系统把课表抓下来——那样就永远无法在无人值守环境里回归界面。
    /// </para>
    /// </summary>
    public void LoadScheduleForCheck(ScheduleFile file)
    {
        Timetable.WeekNumber = Services.Timetable.ComputeWeekNumber(
            DateTime.Now.ToString("yyyy-MM-dd"), CurrentTermStartDate) ?? 1;
        SetScheduleFile(file);
    }

    /// <summary>
    /// 课表的**唯一**写入口：换课表文件 + 刷新所有依赖它的派生状态。
    /// <para>
    /// 为什么必须收敛成一处：课表有三个写入点（启动读盘、教务导入、自检注入），
    /// 各自漏刷一个通知就会出现「程序里明明有课表，设置里却说没有」这类自相矛盾的状态——
    /// 实测踩过：启动读盘漏了 <see cref="RaiseClassReminderHint"/>，
    /// 于是上课提醒一直显示「还没有课表」。
    /// </para>
    /// </summary>
    private void SetScheduleFile(ScheduleFile file)
    {
        _scheduleFile = file;
        OnPropertyChanged(nameof(ScheduleItemCount));
        RebuildTimetable();
        RaiseClassReminderHint();
    }

    /// <summary>当前学期标识（导入时写入条目）。</summary>
    public int CurrentTerm => AppSettings.Current.Term;

    /// <summary>
    /// 导入成功后落盘并刷新周视图。
    /// <para>由教务导入视图在抓取成功后调用：视图负责 WebView，ViewModel 负责数据与界面状态。</para>
    /// </summary>
    public async Task ApplyImportedScheduleAsync(ScheduleFile file, ScheduleMergeResult result)
    {
        SetScheduleFile(file);
        try
        {
            await _scheduleStore.SaveAsync(_scheduleFile).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            TimetableStatus = $"课表已更新，但保存失败：{ex.Message}";
        }

        _jwProfile.LastFetchAt = DateTimeOffset.Now;
        _jwProfile.LastFetchSummary = result.ToString();
        try
        {
            await _jwProfileStore.SaveAsync(_jwProfile).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 抓取记录写不进去不影响课表本身。
        }
    }

    /// <summary>设置课表页的状态提示（教务导入视图反馈结果用）。</summary>
    public void SetTimetableStatus(string message) => TimetableStatus = message;

    /// <summary>教务弹窗的状态文案（由视图维护）。</summary>
    [ObservableProperty]
    private string _jwWindowStateText = "正在打开教务系统窗口…";

    /// <summary>
    /// 手动抓取当前页课表。
    /// <para>
    /// 是悬浮球之外的**备用路径**：万一注入被页面结构挡住，用户仍有办法抓取。
    /// </para>
    /// </summary>
    public async Task FetchJwScheduleNowAsync()
    {
        var dialog = JwDialogAccessor?.Invoke();
        if (dialog is null)
        {
            TimetableStatus = "教务窗口未打开，请点「重新打开教务窗口」。";
            return;
        }

        // 页面还没加载完成时执行脚本必定失败（WebView 的硬约束），先给出可操作的提示，
        // 而不是让用户看到一次注定失败的抓取。服务层还有一道闸门兜着同一个条件。
        var gate = JwPageGate;
        if (gate is null || !gate.IsReady)
        {
            TimetableStatus = "教务页面还在加载中，请等页面显示出来后再抓取。";
            return;
        }

        try
        {
            var result = await JwCaptureService.FetchAndImportAsync(
                dialog,
                gate,
                () => CurrentSchedule,
                (file, merge) => ApplyImportedScheduleAsync(file, merge),
                CurrentTerm).ConfigureAwait(true);
            TimetableStatus = result.Message;
        }
        catch (Exception ex)
        {
            TimetableStatus = $"抓取出错：{ex.Message}";
        }
    }

    /// <summary>保存教务系统网址（设置页调用）。</summary>
    public async Task SaveJwLoginUrlAsync()
    {
        var url = (JwLoginUrlInput ?? string.Empty).Trim();
        if (url.Length > 0 && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // 只允许 https：教务页要输账号口令，明文 http 不可接受。
            TimetableStatus = "教务系统网址必须以 https:// 开头。";
            return;
        }

        _jwProfile.LoginUrl = url;
        await _jwProfileStore.SaveAsync(_jwProfile).ConfigureAwait(true);
        OnPropertyChanged(nameof(HasJwProfile));
        TimetableStatus = url.Length > 0 ? "教务系统网址已保存。" : "已清空教务系统网址。";
    }

    // ---------------- 个人信息（设置页手动填写） ----------------

    [ObservableProperty]
    private string _profileNameInput = AppSettings.Current.CustomUserName ?? string.Empty;

    [ObservableProperty]
    private string _profileSubtitleInput = AppSettings.Current.CustomUserSubtitle ?? string.Empty;

    [ObservableProperty]
    private string _profileAvatarInput = AppSettings.Current.CustomAvatarUrl ?? string.Empty;

    /// <summary>保存手动填写的个人信息并立即刷新侧栏显示。</summary>
    public void SaveProfile()
    {
        static string? Norm(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        AppSettings.Current.CustomUserName = Norm(ProfileNameInput);
        AppSettings.Current.CustomUserSubtitle = Norm(ProfileSubtitleInput);
        AppSettings.Current.CustomAvatarUrl = Norm(ProfileAvatarInput);
        AppSettings.Current.Save();
        ApplyCustomProfile();
        SyncStatus = "个人信息已保存";
    }

    /// <summary>主内容是否可见。启动即进主界面（登录已迁入设置页），恒为 true。</summary>
    public bool ShowContent => true;

    /// <summary>当前列表的标题，用于中间栏顶部。</summary>
    public string ListTitle => SelectedNav?.Label ?? "待办";

    /// <summary>当前列表的说明文字。</summary>
    public string ListSubtitle
    {
        get
        {
            // 课程视图是另一条通道：按「课程数 / 有未完成的课程数 / 任务点合计」描述。
            if (SelectedNav?.Key == "course")
            {
                var withPending = List.CoursesWithPending;
                var points = List.Courses.Sum(c => c.PendingTaskPoints);
                var head = $"{List.CourseCount} 门课程";
                if (withPending == 0) return $"{head} · 任务点已全部完成";
                return $"{head} · {withPending} 门有未完成任务点 · 合计 {points} 个";
            }

            var count = List.Count;
            var overdue = List.OverdueCount;
            var headText = $"{count} 项";
            if (SelectedNav?.Key == "myday")
                return overdue > 0 ? $"{headText} · 其中 {overdue} 项已过期" : headText;
            if (SelectedNav?.Key == "announcement")
                return $"{headText} · 未读 {List.Items.Count(i => !i.IsDone)} 条";
            return headText;
        }
    }

    /// <summary>
    /// 是否显示「隐藏已勾选」开关（仅作业/全部视图有意义）。
    /// </summary>
    public bool ShowHideDoneToggle => SelectedNav?.Key is "homework" or "all" or "planned" or "myday";

    /// <summary>
    /// 是否显示课程详情面板。
    /// <para>
    /// **必须同时要求「在课程视图」且「已选中课程」**：课程详情与待办详情各绑一个
    /// 独立属性（<c>List.SelectedCourse</c> / <c>SelectedItem</c>），两者并不天然互斥。
    /// 只判「已选中课程」的话，用户先选课程、再切到待办视图选一条作业时，
    /// 两块详情会**叠加渲染**（实际踩过这个 bug）。
    /// </para>
    /// </summary>
    public bool CanShowCourseDetail => List.ShowCourseList && List.HasSelectedCourse;

    // ---------------- 生命周期 ----------------

    /// <summary>
    /// 让「随时间变化」的展示重新求值（剩余时间、逾期标红、我的一天、导航角标）。
    /// <para>
    /// 这些值都是读取时现算 <see cref="DateTimeOffset.Now"/> 的派生属性，但
    /// <see cref="ApplyFilter"/> / <see cref="RefreshNavState"/> 原先只在
    /// 导航点击、缓存载入、同步成功这三处被调用。于是只要同步没成功——关掉自动同步
    /// （<c>AutoRefreshMinutes=0</c> 是官方支持的设置）、断网、或登录态失效——
    /// 「剩 3.5 天」和逾期红色就会冻结在上次同步的时刻一直错下去；
    /// 含 <c>DueAt.Date &lt;= Now.Date</c> 的「我的一天」跨过午夜也不会重算，
    /// 昨天的列表会一直留到用户点一次左侧导航。
    /// </para>
    /// <para>
    /// 计时器只做「重新通知」这一件事，数据本身仍以最后一次同步为准。
    /// </para>
    /// </summary>
    private Avalonia.Threading.DispatcherTimer? _clockTimer;

    /// <summary>上次刷新时所在的日期，用于判断是否需要重算按天分组的筛选（我的一天 / 今天到期）。</summary>
    private DateOnly _lastClockDate;

    private void StartClockTimer()
    {
        if (_clockTimer is not null) return;

        _lastClockDate = DateOnly.FromDateTime(DateTimeOffset.Now.Date);
        _clockTimer = new Avalonia.Threading.DispatcherTimer
        {
            // 一分钟粒度足够：最短的剩余时间文案是「剩 N 分钟」。
            Interval = TimeSpan.FromMinutes(1),
        };
        _clockTimer.Tick += (_, _) => RefreshTimeDependentState();
        _clockTimer.Start();
    }

    /// <summary>重新通知时间相关的派生属性；跨天时额外重算筛选与角标。</summary>
    private void RefreshTimeDependentState()
    {
        // 逐项通知：只更新已算好的文本与逾期标记，不动集合，不产生选中态抖动。
        foreach (var item in _cache) item.Refresh();

        var today = DateOnly.FromDateTime(DateTimeOffset.Now.Date);
        if (today == _lastClockDate)
        {
            // 同一天内：只有文案变了，标题栏的「其中 N 项已过期」跟着更新即可。
            OnPropertyChanged(nameof(ListSubtitle));
            return;
        }

        // 跨天：「我的一天」的成员资格与导航角标都会变，需要整表重算。
        _lastClockDate = today;
        ApplyFilter();
        RefreshNavState();
        OnPropertyChanged(nameof(ListTitle));
        OnPropertyChanged(nameof(ListSubtitle));
        OnPropertyChanged(nameof(ShowHideDoneToggle));
    }

    public async Task InitializeAsync()
    {
        // 时间相关文案的周期性刷新：与登录态无关，任何状态下都该跑。
        StartClockTimer();

        // 个人信息展示一律来自设置页的手动填写。
        ApplyCustomProfile();
        // 必须 await：RestoreChaoxingAsync 要读盘 + DPAPI 解密，若与下面的 RefreshAsync
        // 并发，首次同步会因 _chaoxingCookie 仍为 null 而整个跳过学习通
        // （SyncChaoxingAsync 开头就是 `if (_chaoxingCookie is null) return [];`）。
        // 症状：只登录学习通的用户冷启动后列表空白，且学习通作业要等下一次自动同步才出现。
        await RestoreChaoxingAsync().ConfigureAwait(true);
        // 同理必须 await 批改网：否则首次同步会因 _pigaiCookie 仍为 null 整个跳过。
        await RestorePigaiAsync().ConfigureAwait(true);

        if (App.ForceLoginPage)
        {
            // 自检模式：直接打开雨课堂登录覆盖层。
            State = AppState.Ready;
            LoginPageOpen = "ykt";
            return;
        }

        _localState = await _localStateStore.LoadAsync().ConfigureAwait(true);

        // 待办先上缓存：首屏列表秒开，网络同步在后台追平。
        // 缓存加载时已做合法性校验（坏条目在 TodoCacheStore 里被丢弃）。
        //
        // 刻意放在「是否有雨课堂会话」的判断之外：只登录学习通/批改网的用户同样有缓存，
        // 放在雨课堂分支里会让他们的冷启动永远是空列表。
        _cacheFile = await _todoCacheStore.LoadAsync().ConfigureAwait(true);
        if (_cacheFile is { } cachedFile
            && (cachedFile.Homework.Count > 0 || cachedFile.Chaoxing.Count > 0
                || cachedFile.Pigai.Count > 0 || cachedFile.Announcements.Count > 0
                || cachedFile.ChaoxingCourses.Count > 0))
        {
            BuildCache(new SyncResult
            {
                // 学习通条目按上次同步算出的绝对截止时间展示；若同步后过了很久，
                // 紧随其后的网络同步会立刻用新的相对时间纠正。
                Homework = cachedFile.Homework.Concat(cachedFile.Chaoxing).Concat(cachedFile.Pigai).ToList(),
                Announcements = cachedFile.Announcements,
                CourseCount = cachedFile.CourseCount,
                ActivityCount = cachedFile.ActivityCount,
                UnreadNotificationCount = cachedFile.UnreadNotificationCount,
                SyncedAt = cachedFile.SyncedAt,
            });

            // 课程汇总同样从缓存先上，否则「课程」视图在后台同步完成前是空的。
            List.ReplaceCourses(cachedFile.ChaoxingCourses
                .Select(c => new ChaoxingCourseViewModel(c))
                .OrderByDescending(c => c.HasPending)
                .ThenByDescending(c => c.PendingTaskPoints)
                .ThenBy(c => c.CourseName, StringComparer.CurrentCulture));

            ApplyFilter();
            RefreshNavState();
            SyncStatus = $"本地缓存 · 上次同步于 {cachedFile.SyncedAt:MM-dd HH:mm}";
            // SyncStatus 马上会被后台同步的进度文本覆盖，缓存提示单独放一条，
            // 在新数据到达前一直可见。
            CacheNote = $"列表是 {cachedFile.SyncedAt:MM-dd HH:mm} 的本地缓存，正在后台同步…";
        }

        // 课表：读一次即用（导入时才重写），失败按空课表处理。
        try
        {
            var loaded = await _scheduleStore.LoadAsync().ConfigureAwait(true);
            _jwProfile = await _jwProfileStore.LoadAsync().ConfigureAwait(true);
            JwLoginUrlInput = _jwProfile.LoginUrl;
            // 先按当前周建好网格：这样用户点「课表」时不会看到一瞬间的空白。
            // 走统一的写入口——它会一并刷新上课提醒的前提提示
            //（漏了这一步就会出现「明明有课表，却说还没有课表」）。
            SetScheduleFile(loaded);
        }
        catch (Exception)
        {
            // 课表读不出来不该挡住其它功能（store 内部已做坏数据兜底，这里是最后一道）。
        }

        var saved = await _sessionStore.LoadAsync().ConfigureAwait(true);
        if (saved is { HasValue: true })
        {
            // 启动时登录页会先显示，其「后台自动检测」轮询可能已在跑；
            // 会话既已恢复就必须停掉它，否则它会继续把「仍在等待扫码…」写进主界面状态栏。
            _loginCts?.Cancel();

            _session = saved.ToSession();
            _api = new YktApiClient(_session);
            NotifyAccountChanged();
            State = AppState.Syncing;
            LoginStatusText = "已登录";

            // RefreshAsync 自己负责把耗时工作挪到后台线程，这里直接等待即可。
            await RefreshAsync().ConfigureAwait(true);

            // 首次同步成功才启动自动同步：失败（如登录态失效）时 _api 已置空，
            // 不启动可避免循环每个间隔都空转报错。
            // RefreshAsync 内部已把首次结果交给 Observe 建立基线，
            // 不会把「首次看到的全部作业」当成新作业通知一遍。
            if (State == AppState.Ready)
                await AutoSync.StartAsync(AppSettings.Current.AutoRefreshMinutes).ConfigureAwait(true);
        }
        else
        {
            // 没有雨课堂会话也直进主界面：登录入口在设置页。
            State = AppState.Ready;
            LoginStatusText = "未登录";

            // 但可能只登录了学习通/批改网：此时必须同步一次，否则列表永远停在缓存上
            // （RefreshAsync 开头就允许「只有附加数据源账号」这种情况）。
            if (_chaoxingCookie is not null || _pigaiCookie is not null)
            {
                await RefreshAsync().ConfigureAwait(true);
                if (State == AppState.Ready)
                    await AutoSync.StartAsync(AppSettings.Current.AutoRefreshMinutes).ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// 下载并解码头像（http(s) 链接或本地路径）。纯展示信息，任何失败都只是回落到姓名首字。
    /// </summary>
    private async Task LoadAvatarAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            UserAvatar = null;
            return;
        }

        try
        {
            if (File.Exists(url))
            {
                await using var fs = File.OpenRead(url);
                UserAvatar = new Avalonia.Media.Imaging.Bitmap(fs);
                return;
            }
            var bytes = await s_http.GetByteArrayAsync(url).ConfigureAwait(true);
            using var ms = new MemoryStream(bytes);
            UserAvatar = new Avalonia.Media.Imaging.Bitmap(ms);
        }
        catch (Exception)
        {
            UserAvatar = null;
        }
    }

    // ---------------- 登录 ----------------

    /// <summary>
    /// 等待 WebView 中完成登录；成功后建立会话并立即同步一次。
    /// </summary>
    /// <param name="isAutoDetect">
    /// true 表示这是登录页出现后的后台自动检测：它会占用 <see cref="IsBusy"/>，
    /// 但不会让按钮变成不可点，否则用户会觉得「按钮点不动」。
    /// 用户主动点击时传 false，会先取消正在进行的自动检测。
    /// </param>
    /// <param name="quickProbe">true 时先立即探测一次：页面里已有登录态就直接接管并同步。</param>
    public async Task CompleteLoginAsync(
        Avalonia.Controls.NativeWebView webView,
        bool isAutoDetect = false,
        bool quickProbe = false)
    {
        // 用户主动点击可以打断后台自动检测——这是按钮必须始终可点的原因。
        if (isAutoDetect && IsBusy) return;
        if (!isAutoDetect) _loginCts?.Cancel();

        _loginCts = new CancellationTokenSource();
        var loginCts = _loginCts;
        var ct = loginCts.Token;
        EnterBusy();
        IsAutoDetecting = isAutoDetect;
        IsCheckingLogin = !isAutoDetect;
        ErrorMessage = string.Empty;
        try
        {
            var progress = new Progress<LoginStatus>(s => LoginStatusText = s.Message);

            if (quickProbe && await TryAdoptExistingSessionAsync(webView).ConfigureAwait(true))
            {
                return;
            }

            var session = await _loginService
                .WaitForLoginAsync(webView, progress, ct: ct)
                .ConfigureAwait(true);

            if (session is null)
            {
                if (!ct.IsCancellationRequested)
                    LoginStatusText = "尚未检测到登录，请扫码后重试";
                return;
            }

            await AdoptSessionAsync(session).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 被新的检测取代，状态由新的那一轮负责。
        }
        catch (Exception ex)
        {
            LoginStatusText = $"登录过程出错：{ex.Message}";
        }
        finally
        {
            // 只在仍是自己那一轮时才复位共享状态：
            // 用户点击后旧的自动检测会走到这里，不能把新一轮的标志清掉。
            if (ReferenceEquals(_loginCts, loginCts))
            {
                ExitBusy();
                IsAutoDetecting = false;
                IsCheckingLogin = false;
                _loginCts = null;
            }

            loginCts.Dispose();
        }
    }

    /// <summary>
    /// 快速路径：WebView 里若已经有可用的登录态就直接接管并同步。
    /// 用于「重新检测登录」——此时页面通常已经加载好，没必要再等一个轮询间隔。
    /// </summary>
    private async Task<bool> TryAdoptExistingSessionAsync(Avalonia.Controls.NativeWebView webView)
    {
        LoginStatusText = "正在检测登录状态…";

        var status = await YktLoginService.TryProbeAsync(webView).ConfigureAwait(true);
        if (status != "200") return false;

        var session = await YktLoginService
            .ReadSessionAsync(webView,
                _session?.UniversityId ?? AppSettings.Current.UniversityId,
                _session?.Term ?? AppSettings.Current.Term)
            .ConfigureAwait(true);

        if (session is null) return false;

        LoginStatusText = "检测到已有登录态，正在同步…";
        await AdoptSessionAsync(session).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// 兜底登录：直接使用粘贴的 cookie 字符串建立会话。
    /// 适用于 WebView2 运行时缺失、扫码窗口无法显示的机器。
    /// </summary>
    public async Task<bool> UseCookieAsync(string rawCookie)
    {
        try
        {
            var session = YktSession.ParseCookieString(rawCookie);
            return await AdoptSessionAsync(session).ConfigureAwait(true);
        }
        catch (ArgumentException ex)
        {
            LoginStatusText = ex.Message;
            return false;
        }
    }

    private async Task<bool> AdoptSessionAsync(YktSession session)
    {
        // 登录页可能在「后台自动检测」轮询：会话一经接管，那套轮询就是僵尸循环，
        // 会继续把「仍在等待扫码…」写进状态栏（主界面里也看得到）。必须取消。
        // 从 CompleteLoginAsync 自己走进来时这是「取消自己」，无害：
        // 循环已经越过 WaitForLoginAsync，finally 的 ReferenceEquals 判断不受影响。
        _loginCts?.Cancel();

        _session = session;

        // 先停自动同步：它的循环可能正用着旧 _api（自动同步不走 _isSyncing，
        // 仅靠下面的 WaitForSyncToStopAsync 等不到它）。
        await AutoSync.StopAsync().ConfigureAwait(true);

        // 上一轮同步可能还在跑且正用着旧 _api：先取消并等它彻底结束再换。
        // 直接 Dispose 在途的 YktApiClient，下一个节流请求会撞上已释放的
        // SemaphoreSlim，界面报「发生未预期的错误：Cannot access a disposed object」；
        // 而且此时 RefreshAsync 的 _isSyncing 守卫会把新同步降级成「取消」，
        // 最终卡在登录页（State=Error/NeedsLogin）却显示已登录。
        await WaitForSyncToStopAsync().ConfigureAwait(true);

        _api?.Dispose();
        _api = new YktApiClient(session);
        NotifyAccountChanged();

        await _sessionStore.SaveAsync(SessionFile.From(session)).ConfigureAwait(true);

        ShowCookieInput = false;
        ErrorMessage = string.Empty;
        LoginPageOpen = null; // 从设置页的登录覆盖层进来的，登录完成后收起它。
        State = AppState.Syncing;
        LoginStatusText = "已登录";
        await RefreshAsync().ConfigureAwait(true);

        // 登录成功即启动自动同步（与 InitializeAsync 恢复会话的路径一致），
        // 否则本次会话收不到新作业通知；失败则保持停止，避免空转报错。
        if (State == AppState.Ready)
            await AutoSync.StartAsync(AppSettings.Current.AutoRefreshMinutes).ConfigureAwait(true);

        return State == AppState.Ready;
    }

    /// <summary>取消当前同步并等它收尾完成（手动与自动都不在途），供替换/销毁 _api 前调用。</summary>
    private async Task WaitForSyncToStopAsync()
    {
        if (IsSyncing)
        {
            _syncCts?.Cancel();
            // 同步在 Task.Run 里跑、finally 回到 UI 线程收尾；这里 await 让出，
            // 不会死锁。取消在请求间隙生效，通常 200ms 节流间隔内就会停。
            while (IsSyncing)
                await Task.Delay(50).ConfigureAwait(true);
        }

        // 自动同步不走 _isSyncing：拿到互斥锁才确认没有任何同步在途。
        await _syncGate.WaitAsync().ConfigureAwait(true);
        _syncGate.Release();
    }

    /// <summary>
    /// 平台相关操作（打开 URL 等），由各平台头在启动时注入
    /// （Desktop 见 Program.Main 里的 DesktopPlatformServices）。
    /// 静态注入与 <see cref="WebViewAccessor"/> 同款：项目没有 DI 容器，
    /// ViewModel 由 App 直接 new。未注入的平台（Android/Browser/iOS 尚未接入）
    /// 调用会被静默忽略——与原先 Process.Start 失败被吞掉的行为一致。
    /// </summary>
    public static IPlatformServices? PlatformServices { get; set; }

    /// <summary>
    /// 「为安装更新而退出应用」的钩子，由平台头指派（桌面头指到 <c>MainWindow.QuitForReal</c>）。
    /// <para>
    /// 为什么不直接在 <see cref="IPlatformServices"/> 的实现里写 <c>Shutdown()</c>：
    /// 桌面头退出前还要做收尾（<c>PrepareForShutdown</c> 持久化小组件位置），
    /// 绕过它会让小组件位置丢失。而这个钩子必须由桌面头自己接，
    /// 因为共享项目拿不到桌面头的 <c>MainWindow</c> 类型。
    /// </para>
    /// <para>未指派时 <see cref="DesktopPlatformServices"/> 会退回直接关停应用。</para>
    /// </summary>
    public static Action? ExitForUpdate { get; set; }

    /// <summary>
    /// 提供当前登录页里的 WebView，用于退出登录时清除它内部的 Cookie。
    /// 用委托是因为 ViewModel 不该直接依赖视图控件。
    /// </summary>
    public static Func<Avalonia.Controls.NativeWebView?>? WebViewAccessor { get; set; }

    /// <summary>
    /// 学习通登录页里的 WebView（<see cref="WebViewAccessor"/> 是雨课堂那个）。
    /// 两个登录页各有自己的 WebView，退出登录必须清对应的那个。
    /// </summary>
    public static Func<Avalonia.Controls.NativeWebView?>? ChaoxingWebViewAccessor { get; set; }

    /// <summary>批改网登录页里的 WebView（同上，三者互不相同）。</summary>
    public static Func<Avalonia.Controls.NativeWebView?>? PigaiWebViewAccessor { get; set; }

    /// <summary>
    /// 系统通知实现。由平台头在启动时注入（桌面端为 <c>WindowsNotifier</c>），
    /// 未注入时退化为 <see cref="NullNotifier"/>，非 Windows 平台也能正常跑。
    /// </summary>
    public static INotifier Notifier { get; set; } = new NullNotifier();

    /// <summary>自动同步与「新作业」通知。</summary>
    public AutoSyncService AutoSync { get; }

    /// <summary>上课提醒（「上课前 N 分钟」发系统通知）。</summary>
    public ClassReminderService ClassReminder { get; }

    /// <summary>
    /// 同步后的全量事项，供桌面小组件复用。
    /// 小组件据此渲染，不再单独请求网络，从而与主界面始终一致。
    /// </summary>
    public IReadOnlyList<TodoItemViewModel> WidgetSource => _cache;

    /// <summary>
    /// 同步完成（或退出登录清空数据）后触发，供小组件刷新（由桌面头订阅）。
    /// 契约：始终在 UI 线程触发，订阅方可以直接操作窗口与控件。
    /// </summary>
    public event EventHandler? Synced;

    /// <summary>
    /// 用户切换了「显示桌面小组件」。由桌面头订阅并负责真正的显示/隐藏——
    /// 共享项目不该直接操作窗口。
    /// </summary>
    public event EventHandler? WidgetVisibilityRequested;

    /// <summary>小组件是否已启用（持久化在 settings.json）。</summary>
    [ObservableProperty]
    private bool _widgetEnabled = AppSettings.Current.WidgetVisible;

    /// <summary>设置页开关改动（设置页双向绑定 → 走统一的切换路径）。</summary>
    partial void OnWidgetEnabledChanged(bool value)
    {
        if (value != AppSettings.Current.WidgetVisible) SetWidgetEnabled(value);
    }

    /// <summary>点窗口「关闭」的行为（0 = 最小化到托盘，1 = 直接退出；仅桌面生效）。</summary>
    [ObservableProperty]
    private int _closeBehaviorIndex = AppSettings.Current.CloseBehaviorIndex;

    partial void OnCloseBehaviorIndexChanged(int value)
    {
        AppSettings.Current.CloseBehaviorIndex = value;
        AppSettings.Current.Save();
    }

    /// <summary>发现新作业时是否发系统通知（持久化在 settings.json）。</summary>
    [ObservableProperty]
    private bool _notifyOnNewHomework = AppSettings.Current.NotifyOnNewHomework;

    partial void OnNotifyOnNewHomeworkChanged(bool value)
    {
        AppSettings.Current.NotifyOnNewHomework = value;
        AppSettings.Current.Save();
    }

    /// <summary>桌面平台专属的设置项（关闭行为、桌面小组件）是否显示。</summary>
    public bool ShowDesktopSettings => OperatingSystem.IsWindows();

    /// <summary>设置页是否打开（主界面内嵌页面，替代原外观浮层）。</summary>
    [ObservableProperty]
    private bool _settingsOpen;

    // ---------------- 同步与学期设置（设置页） ----------------

    /// <summary>
    /// 自动同步间隔（分钟，0 = 关闭）。改动即保存；已登录时立刻重启定时同步让新间隔生效。
    /// NumericUpDown 已在界面限定了范围，这里仍过一次归一化兜底（手改 settings.json 的路径）。
    /// </summary>
    [ObservableProperty]
    private int _autoRefreshMinutes = AppSettings.Current.AutoRefreshMinutes;

    partial void OnAutoRefreshMinutesChanged(int value)
    {
        var minutes = AppSettings.NormalizeRefreshMinutes(value);
        AppSettings.Current.AutoRefreshMinutes = minutes;
        SaveSettingsDebounced();
        // 只重启正在跑/该跑的自动同步：未登录或同步未就绪时不碰，避免空转。
        if (_api is not null && State == AppState.Ready && AutoSync.IntervalMinutes != minutes)
            _ = AutoSync.StartAsync(minutes);
    }

    /// <summary>
    /// 上课前多少分钟提醒（0 = 不提醒）。改动即保存，并立刻按新值启停提醒轮询。
    /// <para>NumericUpDown 已在界面限定范围，这里仍过一次归一化兜底（手改 settings.json 的路径）。</para>
    /// </summary>
    [ObservableProperty]
    private int _classReminderMinutes = AppSettings.Current.ClassReminderMinutes;

    partial void OnClassReminderMinutesChanged(int value)
    {
        var minutes = AppSettings.NormalizeClassReminderMinutes(value);
        AppSettings.Current.ClassReminderMinutes = minutes;
        SaveSettingsDebounced();

        // 按新值启停：0 或「没设学期基准」都不跑，避免空转。
        if (ClassReminderEnabled) _ = ClassReminder.StartAsync();
        else _ = ClassReminder.StopAsync();
    }

    /// <summary>
    /// 上课提醒为什么没生效（有原因才显示给用户）。
    /// <para>
    /// 光有「提前 N 分钟」的输入框不够——没设学期第一周时它一点用都没有，
    /// 必须把原因说出来，否则用户只会觉得提醒坏了。
    /// </para>
    /// </summary>
    public string? ClassReminderHint
    {
        get
        {
            if (AppSettings.Current.ClassReminderMinutes <= 0) return null;
            if (string.IsNullOrWhiteSpace(CurrentTermStartDate))
                return "上课提醒需要「学期第一周的周一」才能算出今天几点上课，请在上面的「学期」里填写。";
            if (_scheduleFile.Items.Count == 0)
                return "还没有课表，导入后上课提醒才会生效。";
            if (!Notifier.IsAvailable)
                return $"系统通知不可用：{Notifier.UnavailableReason}";
            return null;
        }
    }

    /// <summary>是否显示上课提醒的提示。</summary>
    public bool HasClassReminderHint => ClassReminderHint is not null;

    private void RaiseClassReminderHint()
    {
        OnPropertyChanged(nameof(ClassReminderHint));
        OnPropertyChanged(nameof(HasClassReminderHint));
    }

    /// <summary>学年起始年（学期代码 = 年 × 100 + 学期序号，如 202601 = 2026-2027 学年第一学期）。</summary>
    [ObservableProperty]
    private int _termYear = AppSettings.Current.Term / 100;

    /// <summary>学期序号下标：0 = 第一学期，1 = 第二学期。</summary>
    [ObservableProperty]
    private int _termHalfIndex = Math.Clamp(AppSettings.Current.Term % 100 - 1, 0, 1);

    /// <summary>
    /// 切换学期。课程列表按学期过滤，跨学期的增量签名没有意义，因此切换后
    /// 丢掉待办缓存基准、全量重拉一次。未登录时只保存设置，登录后生效。
    /// </summary>
    [RelayCommand]
    private async Task ApplyTermAsync()
    {
        var term = TermYear * 100 + TermHalfIndex + 1;
        if (term == AppSettings.Current.Term)
        {
            SyncStatus = "学期未变化";
            return;
        }

        AppSettings.Current.Term = term;
        AppSettings.Current.Save();

        if (_session is null)
        {
            SyncStatus = "学期设置已保存，登录后生效";
            return;
        }

        // 与换账号同理：先停自动同步、等在途同步收尾，再换 API 实例。
        await AutoSync.StopAsync().ConfigureAwait(true);
        await WaitForSyncToStopAsync().ConfigureAwait(true);

        var old = _session;
        _session = new YktSession
        {
            SessionId = old.SessionId,
            CsrfToken = old.CsrfToken,
            LoginType = old.LoginType,
            ExpiresAt = old.ExpiresAt,
            UniversityId = old.UniversityId,
            Term = term,
        };
        _api?.Dispose();
        _api = new YktApiClient(_session);
        NotifyAccountChanged();
        await _sessionStore.SaveAsync(SessionFile.From(_session)).ConfigureAwait(true);

        // 清掉增量同步基准：旧学期的签名与按课全量时间戳对新学期无意义。
        _cacheFile = null;

        await RefreshAsync().ConfigureAwait(true);
        if (State == AppState.Ready)
            await AutoSync.StartAsync(AppSettings.Current.AutoRefreshMinutes).ConfigureAwait(true);
    }

    /// <summary>设置页的选项卡：0 = 设置，1 = 账号，2 = 课表，3 = 关于。</summary>
    [ObservableProperty]
    private int _settingsTabIndex;

    public bool SettingsTab0Active => SettingsTabIndex == 0;
    public bool SettingsTab1Active => SettingsTabIndex == 1;
    public bool SettingsTab2Active => SettingsTabIndex == 2;
    public bool SettingsTab3Active => SettingsTabIndex == 3;

    partial void OnSettingsTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SettingsTab0Active));
        OnPropertyChanged(nameof(SettingsTab1Active));
        OnPropertyChanged(nameof(SettingsTab2Active));
        OnPropertyChanged(nameof(SettingsTab3Active));
    }

    /// <summary>切换设置页选项卡（0 = 设置，1 = 账号，2 = 课表，3 = 关于）。</summary>
    public void ShowSettingsTab(int index)
    {
        SettingsTabIndex = index;

        // 进课表选项卡时把编辑器刷成当前时间表：上次编辑没保存的残留不该带进来。
        if (index == 2) PeriodsEditor.Load(_scheduleFile.Periods);
    }

    /// <summary>打开设置页（侧边栏齿轮按钮）。</summary>
    public void OpenSettings()
    {
        SettingsOpen = true;
        // 设置与课表都是整页覆盖，同时打开会互相遮挡。
        TimetableOpen = false;
        DrawerOpen = false;
    }

    /// <summary>打开/关闭设置页（桌面端没有返回键，再点一次齿轮退出设置页）。</summary>
    public void ToggleSettings()
    {
        if (SettingsOpen) SettingsOpen = false;
        else OpenSettings();
    }

    // ---------------- 关于与更新检查 ----------------

    /// <summary>当前版本号（设置页「关于」显示）。</summary>
    public string AppVersionText => $"v{UpdateService.CurrentVersion}";

    [ObservableProperty]
    private bool _isCheckingUpdate;

    /// <summary>检查更新的结果文案。</summary>
    [ObservableProperty]
    private string _updateStatus = string.Empty;

    /// <summary>发现新版本时最新 release 的下载页地址（null = 无更新或未检查）。</summary>
    [ObservableProperty]
    private string? _updateUrl;

    /// <summary>是否显示「前往下载」按钮。</summary>
    public bool HasUpdateUrl => UpdateUrl is not null;

    partial void OnUpdateUrlChanged(string? value) => OnPropertyChanged(nameof(HasUpdateUrl));

    // ---------------- 直接下载并更新 ----------------

    /// <summary>挑中的更新包（检查更新时确定；null = 当前没有可下载的更新）。</summary>
    private UpdateService.PackageChoice? _updatePackage;

    /// <summary>本次更新对应的远端 tag（用于版本兜底校验与文案）。</summary>
    private string? _updateTag;

    /// <summary>下载校验后的包期（含 SHA256 与是否已校验）。</summary>
    private UpdateService.DownloadResult? _downloaded;

    /// <summary>下载中（按钮禁用、显示进度）。</summary>
    [ObservableProperty]
    private bool _isDownloadingUpdate;

    /// <summary>下载进度文案（如「已下载 42%」）。</summary>
    [ObservableProperty]
    private string _updateDownloadProgress = string.Empty;

    /// <summary>是否允许点「下载并更新」。</summary>
    [ObservableProperty]
    private bool _canDownloadUpdate;

    partial void OnCanDownloadUpdateChanged(bool value) => OnPropertyChanged(nameof(ShowUpdateActions));

    /// <summary>是否显示下载/更新相关按钮。</summary>
    public bool ShowUpdateActions => CanDownloadUpdate;

    /// <summary>
    /// 是否接收预发布版更新（写入 settings.json）。默认关闭：
    /// 预发布版是给测试用的，不该推给正式版用户。
    /// </summary>
    public bool IncludePrereleaseUpdates
    {
        get => AppSettings.Current.IncludePrereleaseUpdates;
        set
        {
            if (AppSettings.Current.IncludePrereleaseUpdates == value) return;
            AppSettings.Current.IncludePrereleaseUpdates = value;
            AppSettings.Current.Save();
            OnPropertyChanged();
            // 通道变了，之前那次检查结论就不作数了，提示用户重新检查。
            UpdateStatus = value
                ? "已开启接收预发布版更新。重新点「检查更新」查看预发布版。"
                : "已关闭接收预发布版更新。重新点「检查更新」只看正式版。";
            CanDownloadUpdate = false;
            _updatePackage = null;
        }
    }

    /// <summary>
    /// 「发现有新版本，是否现在更新」——由视图订阅并弹确认框，返回用户是否同意。
    /// <para>
    /// 用委托而不是在 ViewModel 里直接弹窗：弹窗需要一个 Window 作为 owner，
    /// 而 ViewModel 不该持有窗口。视图未订阅时（无头自检等）**默认不同意**，
    /// 也就不会在没有用户确认的情况下擅自更新——这个默认值是安全的那一侧。
    /// </para>
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmUpdateRequested { get; set; }

    /// <summary>
    /// 检查到新版本后询问用户并（在同意时）执行更新。
    /// 抽成一个方法是为了让 GUI 与命令行自检走**完全相同**的路径，
    /// 避免「测过的路径」和「用户实际走的路径」不是同一条。
    /// </summary>
    /// <param name="tag">远端版本 tag。</param>
    /// <param name="source">来源名（GitHub / Gitea）。</param>
    /// <param name="targetExeOverride">仅供命令行自检：覆盖替换目标（沙箱演练用）。</param>
    public async Task<bool> ConfirmAndApplyUpdateAsync(
        string tag, string source, string? targetExeOverride = null)
    {
        if (_updatePackage is null)
        {
            UpdateStatus = $"发现新版本 {tag}，但没有适用于本平台的更新包，请前往 release 页手动下载。";
            return false;
        }

        var confirmed = ConfirmUpdateRequested is { } confirm
            && await confirm(
                "发现新版本",
                $"发现新版本 {tag}（来自 {source}），当前版本 {AppVersionText}。\n\n"
                + "现在下载并更新吗？\n"
                + "更新过程会：下载新版本 → 校验完整性 → 自动重启应用。\n"
                + "你的待办数据与各平台登录状态都会保留。").ConfigureAwait(true);

        if (!confirmed)
        {
            UpdateStatus = $"已跳过更新（新版本 {tag}）。可随时再次点「检查更新」。";
            return false;
        }

        return await DownloadAndApplyAsync(targetExeOverride).ConfigureAwait(true);
    }

    /// <summary>
    /// 下载更新包并校验。校验不通过时**不会**进入替换流程（宁可失败也不装坏包）。
    /// </summary>
    [RelayCommand]
    private Task DownloadUpdateAsync() => DownloadAndApplyAsync();

    /// <summary>下载 → 校验 → 解包 → 启动替换器。</summary>
    private async Task<bool> DownloadAndApplyAsync(string? targetExeOverride = null)
    {
        if (IsDownloadingUpdate || _updatePackage is not { } package) return false;

        IsDownloadingUpdate = true;
        UpdateDownloadProgress = "开始下载…";
        try
        {
            // 每次重新下载前清掉上次的残留（半截文件、旧包）。
            UpdateApplier.CleanDownloadDirectory();

            var progress = new Progress<UpdateService.DownloadProgress>(p =>
            {
                UpdateDownloadProgress = p.Percent is { } pct
                    ? $"已下载 {pct}%（{p.Received / 1024 / 1024.0:0.0} / {p.Total / 1024 / 1024.0:0.0} MB）"
                    : $"已下载 {p.Received / 1024 / 1024.0:0.0} MB";
            });

            _downloaded = await UpdateService.DownloadAsync(
                package, UpdateApplier.DownloadDirectory, progress).ConfigureAwait(true);

            // 解出 exe 并做防降级校验（比较基准是当前版本，不是 tag）。
            UpdateDownloadProgress = "正在校验更新包…";
            var staged = await UpdateApplier.StageAsync(_downloaded.FilePath).ConfigureAwait(true);

            var shaNote = _downloaded.HashVerified ? "SHA256 校验通过" : "无清单，未校验哈希";
            UpdateDownloadProgress = $"已下载并校验完成（{shaNote}）。";

            // 替换必须在 UI 线程外决定、很快返回；随后应用会退出。
            // 注意第二个参数必须是**解出的 exe** 的哈希，不能传 zip 的哈希——
            // 更新器替换后校验的是 exe，传 zip 哈希会让校验必然失败。
            var plan = UpdateApplier.StartReplacement(
                staged.ExePath,
                staged.Sha256,
                restart: true,
                targetExeOverride: targetExeOverride);
            if (!plan.Ready)
            {
                // 开发态 / 无法启动替换进程：如实说明，并回落到「前往下载」。
                UpdateStatus = plan.Message;
                UpdateDownloadProgress = string.Empty;
                return false;
            }

            UpdateStatus = plan.Message;
            UpdateDownloadProgress = "即将退出以完成替换…";
            // 给界面一点时间把提示画出来，再退出（替换器正在等本进程结束）。
            await Task.Delay(600).ConfigureAwait(true);
            // 走平台头的正常退出路径：桌面头会先持久化小组件位置再关停。
            PlatformServices?.RequestExitForUpdate();
            return true;
        }
        catch (Exception ex)
        {
            UpdateDownloadProgress = string.Empty;
            UpdateStatus = $"更新失败：{ex.Message}";
            _downloaded = null;
            return false;
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    /// <summary>取消下载/放弃本次更新：清掉临时文件，回到「检查更新」状态。</summary>
    [RelayCommand]
    private void CancelUpdate()
    {
        if (IsDownloadingUpdate) return;
        UpdateApplier.CleanDownloadDirectory();
        _downloaded = null;
        UpdateDownloadProgress = string.Empty;
        UpdateStatus = "已取消本次更新。";
    }

    /// <summary>
    /// 检查更新：并行查询 GitHub 与 Gitea 的最新 release，取较新的对比。
    /// <para>
    /// 发现新版本时**询问用户**是否现在更新；用户点「是」才下载并拉起替换流程。
    /// 用户拒绝不写任何持久化状态——下次点「检查更新」会再问一次，
    /// 而不是「问过一次就永远静默」，避免用户后悔了却找不到入口。
    /// </para>
    /// </summary>
    [RelayCommand]
    private Task CheckUpdateAsync() => RunUpdateCheckAsync(null, null);

    /// <summary>
    /// 检查更新的实现（<see cref="CheckUpdateAsync"/> 与命令行自检共用）。
    /// </summary>
    /// <param name="autoConfirmSource">
    /// 供命令行自检使用：非 null 时跳过弹窗，直接视为「用户已同意」。
    /// 这样 GUI 与自检走的是同一条更新路径，不会「测过的路径 ≠ 用户走的路径」。
    /// </param>
    /// <param name="targetExeOverride">供命令行自检使用：覆盖替换目标（沙箱演练用）。</param>
    public async Task RunUpdateCheckAsync(string? autoConfirmSource = null, string? targetExeOverride = null)
    {
        if (IsCheckingUpdate) return;
        IsCheckingUpdate = true;
        UpdateStatus = "正在检查更新…";
        UpdateUrl = null;
        CanDownloadUpdate = false;
        _updatePackage = null;
        _updateTag = null;
        _downloaded = null;
        UpdateDownloadProgress = string.Empty;
        try
        {
            var outcome = await UpdateService
                .CheckAsync(AppSettings.Current.IncludePrereleaseUpdates)
                .ConfigureAwait(true);
            var errors = outcome.Errors.ToList();

            if (outcome.Best is { Release: { TagName: { } tag } release } best)
            {
                if (!UpdateService.IsNewer(tag))
                {
                    UpdateStatus = $"已是最新版本（{AppVersionText}；远端最新 {tag}，来自 {best.Source}）。";
                    if (errors.Count > 0)
                        UpdateStatus += $"（另一更新源不可达：{string.Join("；", errors)}）";
                    return;
                }

                UpdateUrl = release.HtmlUrl;
                UpdateStatus = $"发现新版本 {tag}（{best.Source}），当前 {AppVersionText}。";

                // 挑适用于本平台的更新包；挑不到就只留「前往下载」，让用户手动取。
                var choice = UpdateService.ChoosePackage(release, best.Source, release.ManifestJson);
                if (choice is not null)
                {
                    _updatePackage = choice;
                    _updateTag = tag;
                    CanDownloadUpdate = true;
                }

                // 另一个源不可达也要如实告知，免得用户以为两边都查了没问题。
                if (errors.Count > 0)
                    UpdateStatus += $"（另一更新源不可达：{string.Join("；", errors)}）";

                if (choice is null)
                {
                    UpdateStatus += " 未找到适用于本平台的更新包，请前往 release 页手动下载。";
                    return;
                }

                // ---- 由用户决定是否更新 ----
                IsCheckingUpdate = false;   // 弹窗期间允许重试/取消，不把界面锁在「检查中」
                if (autoConfirmSource is not null)
                {
                    // 自检路径：视为用户已同意，直接走下载与替换。
                    await DownloadAndApplyAsync(targetExeOverride).ConfigureAwait(true);
                }
                else
                {
                    await ConfirmAndApplyUpdateAsync(tag, best.Source, targetExeOverride).ConfigureAwait(true);
                }
                return;
            }

            UpdateStatus = $"检查更新失败：{string.Join("；", errors)}";
        }
        catch (Exception ex)
        {
            UpdateStatus = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    /// <summary>打开最新 release 的下载页（由平台头注入的浏览器打开）。</summary>
    public void OpenUpdatePage()
    {
        if (UpdateUrl is { } url) PlatformServices?.OpenUrl(url);
    }

    /// <summary>托盘菜单与界面调用：切换小组件显示。</summary>
    public void SetWidgetEnabled(bool enabled)
    {
        WidgetEnabled = enabled;
        AppSettings.Current.WidgetVisible = enabled;
        AppSettings.Current.Save();
        WidgetVisibilityRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 托盘「立即同步一次」的入口。菜单语义是「发起同步」，与界面按钮的
    /// 「再点一次 = 取消」区分开：手动同步在途时直接忽略这次点击。
    /// </summary>
    public void RequestManualSync()
    {
        if (IsSyncing) return;
        _ = RefreshCommand.ExecuteAsync(null);
    }

    /// <summary>上一次退出登录时 WebView Cookie 的清理结果，便于排查「退出后仍在登录」。</summary>
    [ObservableProperty]
    private string _logoutCookieReport = string.Empty;

    [RelayCommand]
    private async Task LogoutAsync()
    {
        _loginCts?.Cancel();
        // 先停自动同步并清掉基线：否则循环会因 _api 为 null 每个间隔空转报错，
        // 残留的「已见」集合还会污染下一个账号的首次通知。
        await AutoSync.StopAsync().ConfigureAwait(true);
        AutoSync.ResetBaseline();
        // 与 AdoptSessionAsync 同理：等同步彻底停下再 Dispose _api。
        await WaitForSyncToStopAsync().ConfigureAwait(true);
        _api?.Dispose();
        _api = null;
        _session = null;
        NotifyAccountChanged();
        _cache.Clear();
        List.Replace([]);
        _cacheFile = null;
        await _todoCacheStore.ClearAsync().ConfigureAwait(true);
        // 通知小组件刷新：数据已清空，继续展示旧账号待办会误导用户勾选写入。
        Synced?.Invoke(this, EventArgs.Empty);
        await _sessionStore.ClearAsync().ConfigureAwait(true);

        // 关键：WebView 有自己的持久化 Cookie 存储，与应用会话文件是两套。
        // 不清它的话，WebView 仍处于登录状态，界面会继续显示课程列表，
        // 而且下次启动的自动检测会把同一个会话又接管回来，等于退出登录没生效。
        if (WebViewAccessor?.Invoke() is { } webView)
        {
            var cleared = await YktLoginService.ClearCookiesAsync(webView).ConfigureAwait(true);
            // 记录结果而不是吞掉：清不掉时登录页会继续停在雨课堂内，必须能看出原因。
            LogoutCookieReport = cleared.Ok
                ? $"已清除 WebView 内 {cleared.Deleted} 个雨课堂 Cookie"
                : $"WebView Cookie 未清干净（原有 {cleared.Found}，剩余 {cleared.Remaining}）：{cleared.Error ?? "未知原因"}";
        }
        else
        {
            LogoutCookieReport = "登录页尚未创建，未清理 WebView Cookie";
        }

        State = AppState.Ready;
        LoginStatusText = "未登录";
        AuthWarning = string.Empty;
        SyncStatus = "尚未同步";
        CacheNote = string.Empty;
        StatusDetail = string.Empty;
        HasWarnings = false;
        ErrorMessage = string.Empty;
        ShowUnreadBadge = false;
        ShowCookieInput = false;
    }

    // ---------------- 同步 ----------------

    /// <summary>取消当前同步。</summary>
    [RelayCommand]
    private void CancelSync() => _syncCts?.Cancel();

    /// <summary>
    /// 同步一次。
    /// <para>
    /// 网络与解析全部在后台线程完成（<see cref="Task.Run(Func{Task})"/>），
    /// 只有「把结果写进 ObservableCollection」这一步回到 UI 线程。
    /// 早期版本在所有 await 上用 <c>ConfigureAwait(true)</c>，导致约 50 次带节流的
    /// HTTP 请求的续体全部排队到 UI 线程上执行，窗口因此长时间无响应。
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_api is null && _chaoxingCookie is null && _pigaiCookie is null)
        {
            // 三个账号都没登录时刷新按钮无意义：状态栏提示去设置页登录。
            State = AppState.Ready;
            SyncStatus = "未登录雨课堂，请到 设置 → 账号 登录";
            return;
        }

        if (IsSyncing)
        {
            // 再次触发表示「取消当前同步」。
            _syncCts?.Cancel();
            return;
        }

        IsSyncing = true;
        EnterBusy();
        State = AppState.Syncing;
        ErrorMessage = string.Empty;
        _syncCts = new CancellationTokenSource();
        var syncCts = _syncCts;
        var ct = syncCts.Token;

        // 与自动同步互斥：它可能正占着 _api 跑网络，等它这一轮结束再开始。
        // 等待本身可被取消（同步中再点一次按钮）。
        var gateAcquired = false;
        try
        {
            await _syncGate.WaitAsync(ct).ConfigureAwait(true);
            gateAcquired = true;

            // Progress<T> 会捕获创建时的同步上下文，因此即使从后台线程汇报，
            // 回调仍会在 UI 线程上执行——进度文本可以安全更新。
            var progress = new Progress<string>(s => SyncStatus = s);

            var api = _api;
            var (result, localState) = await Task.Run(async () =>
            {
                SyncResult r;
                if (api is not null)
                {
                    r = await new SyncService(api).SyncAsync(_cacheFile, progress, ct).ConfigureAwait(false);
                }
                else
                {
                    // 只登录了学习通：雨课堂部分直接给空结果。
                    r = new SyncResult();
                }
                var ls = await _localStateStore.LoadAsync(ct).ConfigureAwait(false);
                return (r, ls);
            }, ct).ConfigureAwait(true);

            // 学习通同步（已登录时）。失败降级为警告，不影响雨课堂结果。
            var cxWarnings = new List<string>();
            // 与自动同步同理：绝对截止时间与缓存基准必须取同一个时刻。
            var cxSyncedAt = DateTimeOffset.Now;
            var (cxItems, cxForCache, cxCourses) =
                await SyncChaoxingAsync(cxWarnings, cxSyncedAt, ct).ConfigureAwait(true);

            // 批改网同步（已登录时）。失败同样只降级为警告。
            var pgWarnings = new List<string>();
            var pgItems = await SyncPigaiAsync(pgWarnings, ct).ConfigureAwait(true);

            cxWarnings.AddRange(pgWarnings);
            result = MergeExtraSources(result, cxItems, cxForCache, cxCourses, pgItems, cxWarnings);

            ct.ThrowIfCancellationRequested();

            ApplySyncResult(result, localState);

            // 手动同步也要喂给「新作业」基线：否则启动后、首次自动同步之前
            // 手动刷出的新作业会被那次同步的「首次只建基线」静默吞掉，永远收不到通知。
            var fresh = AutoSync.Observe(result);
            if (fresh.Count > 0 && AppSettings.Current.NotifyOnNewHomework)
                AutoSync.Notify(fresh);

            // 启动时同步失败（如断网）不会启动定时同步；手动重试成功后在这里补上
            // （issue #1）。StartAsync 幂等于重启——只在没在跑时才调用，间隔 0 时内部自停。
            if (!AutoSync.IsRunning)
                await AutoSync.StartAsync(AppSettings.Current.AutoRefreshMinutes).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // 同步被取消/被新一轮登录取代。ObjectDisposedException 是防御网：
            // 正常路径下 WaitForSyncToStopAsync 已保证不会 Dispose 在途的 _api。
            SyncStatus = "同步已取消";
            State = AppState.Ready;
        }
        catch (YktAuthExpiredException)
        {
            LoginStatusText = "登录已过期";
            AuthWarning = "雨课堂登录态已失效，请到 设置 → 账号 重新登录。";
            ErrorMessage = string.Empty;
            _api?.Dispose();
            _api = null;
            NotifyAccountChanged();
        }
        catch (YktApiException ex)
        {
            State = AppState.Ready;
            SyncStatus = $"同步失败：{ex.Message}";
        }
        catch (Exception ex)
        {
            State = AppState.Ready;
            SyncStatus = $"发生未预期的错误：{ex.Message}";
        }
        finally
        {
            ExitBusy();
            IsSyncing = false;
            syncCts.Dispose();
            if (ReferenceEquals(_syncCts, syncCts)) _syncCts = null;
            if (gateAcquired) _syncGate.Release();
        }
    }

    /// <summary>
    /// 用设置页手动填写的个人信息刷新侧栏显示（姓名/学校学号/头像）。
    /// 个人信息不再从雨课堂接口拉取——接口给的头像时好时坏，学校信息也不一定准。
    /// </summary>
    private void ApplyCustomProfile()
    {
        var s = AppSettings.Current;
        UserName = string.IsNullOrWhiteSpace(s.CustomUserName) ? "未登录" : s.CustomUserName!;
        UserSubtitle = s.CustomUserSubtitle ?? string.Empty;
        UserAvatarUrl = string.IsNullOrWhiteSpace(s.CustomAvatarUrl) ? null : s.CustomAvatarUrl;
        _ = LoadAvatarAsync(UserAvatarUrl);
    }

    /// <summary>
    /// 把一次同步的结果落到界面上。
    /// <para>
    /// 手动同步与自动同步共用这一段，保证两条路径的界面表现一致
    /// （导航角标、未读徽标、警告条、统计行）。必须在 UI 线程上调用。
    /// </para>
    /// </summary>
    private void ApplySyncResult(SyncResult result, LocalState localState)
    {
        _lastResult = result;
        _localState = localState;
        // 同步成功说明会话有效：清掉「登录已过期」横幅。
        AuthWarning = string.Empty;

        // 落盘待办缓存：下次启动秒开列表 + 作为下次增量同步的比对基准。
        // 注意用合并前（HomeworkForCache）的列表：合并会丢课堂归属，被并掉的课程下次无法增量复用。
        // 学习通 / 批改网条目也在这里（已由 MergeExtraSources 并入 HomeworkForCache），
        // 否则冷启动的秒开列表会永远只有雨课堂作业。
        _cacheFile = new TodoCacheFile
        {
            SyncedAt = result.SyncedAt,
            Homework = result.HomeworkForCache.ToList(),
            Chaoxing = result.ChaoxingForCache.ToList(),
            Pigai = result.PigaiForCache.ToList(),
            ChaoxingCourses = result.ChaoxingCourses.ToList(),
            Announcements = result.Announcements.ToList(),
            CourseSignatures = new Dictionary<long, string>(result.CourseSignatures),
            CourseFullSyncedAt = new Dictionary<long, DateTimeOffset>(result.CourseFullSyncedAt),
            CourseCount = result.CourseCount,
            ActivityCount = result.ActivityCount,
            UnreadNotificationCount = result.UnreadNotificationCount,
        };
        _ = _todoCacheStore.SaveAsync(_cacheFile);

        BuildCache(result);

        // 课程维度汇总：与待办分开一条通道（见 TodoListViewModel.Courses）。
        // 有未完成任务点的排前面，其次按未完成数降序，最后按课程名。
        List.ReplaceCourses(result.ChaoxingCourses
            .Select(c => new ChaoxingCourseViewModel(c))
            .OrderByDescending(c => c.HasPending)
            .ThenByDescending(c => c.PendingTaskPoints)
            .ThenBy(c => c.CourseName, StringComparer.CurrentCulture));

        ApplyFilter();
        // 数据到达后必须刷新导航角标：OnNavChanged 只在点击导航时触发，
        // 首次同步完成时没有人调用它。
        RefreshNavState();

        SyncStatus = $"同步于 {result.SyncedAt:HH:mm}";
        CacheNote = string.Empty;

        var unread = result.Announcements.Count(a => !a.IsRead);
        ShowUnreadBadge = unread > 0;
        UnreadBadge = unread > 99 ? "99+" : unread.ToString();

        HasWarnings = result.Warnings.Count > 0;
        WarningText = string.Join("\n", result.Warnings);

        var pending = result.PendingHomework.Count();
        var taskPoints = result.ChaoxingCourses.Sum(c => c.PendingTaskPoints);
        StatusDetail =
            $"{result.CourseCount} 门课程 · {result.ActivityCount} 条日志 · " +
            $"{result.Homework.Count} 项作业（待完成 {pending}） · {result.Announcements.Count} 条公告" +
            (result.ChaoxingCourses.Count > 0
                ? $" · 学习通 {result.ChaoxingCourses.Count} 门课"
                  + (taskPoints > 0 ? $"（{taskPoints} 个任务点未完成）" : "（任务点已完成）")
                : "") +
            (result.ReusedCourses > 0 ? $" · {result.ReusedCourses} 门课走增量校验" : "") +
            (result.ArchivedCourses > 0 ? $" · 含 {result.ArchivedCourses} 门已归档课程" : "");

        State = AppState.Ready;

        // 通知小组件刷新（数据已就绪）。
        Synced?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把同步结果展开成扁平的 ViewModel 缓存。</summary>
    private void BuildCache(SyncResult result)
    {
        _cache.Clear();

        foreach (var hw in result.Homework)
        {
            _cache.Add(new TodoItemViewModel(hw)
            {
                IsDone = _localState.Done.Contains(hw.Id),
            });
        }

        foreach (var ann in result.Announcements)
        {
            _cache.Add(new TodoItemViewModel(ann)
            {
                IsDone = _localState.ReadAnnouncements.Contains(ann.Id) || ann.IsRead,
            });
        }
    }

    private void ApplyFilter()
    {
        // 「课程」视图走另一条通道（课程汇总），待办集合保持为空。
        var isCourseView = SelectedNav?.Key == "course";

        IEnumerable<TodoItemViewModel> query = isCourseView
            ? []
            : SelectedNav?.Key switch
        {
            "myday" => _cache
                .Where(i => i.Kind == TodoKind.Homework && i.Model.Status != TodoStatus.Completed)
                .Where(i => i.IsOverdue || (i.Model.EffectiveDueAt is { } d && d.Date <= DateTimeOffset.Now.Date))
                .OrderBy(i => i.Model.EffectiveDueAt ?? DateTimeOffset.MaxValue),

            "planned" => _cache
                .Where(i => i.Model.EffectiveDueAt is not null && i.Model.Status != TodoStatus.Completed)
                .OrderBy(i => i.Model.EffectiveDueAt),

            "homework" => _cache
                .Where(i => i.Kind == TodoKind.Homework)
                .OrderBy(i => i.Model.Status == TodoStatus.Completed ? 1 : 0)
                .ThenBy(i => i.Model.EffectiveDueAt ?? DateTimeOffset.MaxValue),

            "announcement" => _cache
                .Where(i => i.Kind == TodoKind.Announcement)
                .OrderBy(i => i.IsDone ? 1 : 0)
                .ThenByDescending(i => i.Model.PublishedAt),

            "completed" => _cache
                .Where(i => i.Kind == TodoKind.Homework && i.Model.Status == TodoStatus.Completed)
                .OrderByDescending(i => i.Model.EffectiveDueAt ?? DateTimeOffset.MinValue),

            _ => _cache
                .Where(i => !(i.Kind == TodoKind.Homework && i.Model.Status == TodoStatus.Completed))
                .OrderBy(i => i.Model.EffectiveDueAt ?? DateTimeOffset.MaxValue)
                .ThenBy(i => i.Kind),
        };

        // 空状态文案。没登录雨课堂时在这里给出登录指引——原先顶部有一条常驻横幅
        // 做这件事，现已移除：它就压在列表上方占地方，而列表通常有内容、不需要提示。
        var noYkt = _api is null;
        List.EmptyMessage = SelectedNav?.Key switch
        {
            "myday" => noYkt
                ? "还没有雨课堂账号。到 设置 → 账号 扫码登录后，这里会显示今天到期的事项。"
                : "今天没有到期的事项，轻松一下。",
            "planned" => noYkt
                ? "还没有雨课堂账号。到 设置 → 账号 扫码登录后，这里会按截止时间排序。"
                : "没有带截止时间的事项。",
            "course" => "没有读到学习通课程。请确认已在 设置 → 账号 登录学习通。",
            "announcement" => noYkt
                ? "还没有雨课堂账号。到 设置 → 账号 扫码登录后，这里会显示课程公告。"
                : "没有公告。",
            "completed" => "还没有已完成的作业记录。",
            _ => noYkt
                ? "还没有雨课堂账号。到 设置 → 账号 扫码登录后，这里会显示作业与公告。"
                : "这里没有待办事项。",
        };

        // 视图据此在「待办列表」与「课程列表」之间切换。
        List.ShowCourseList = isCourseView;

        List.Replace(query);
        OnPropertyChanged(nameof(ListSubtitle));
    }

    // ---------------- 交互 ----------------

    /// <summary>
    /// 由 View 在导航项被点击时调用。视图直接改 <see cref="SelectedNav"/>，
    /// 派生状态（列表标题、角标、筛选结果）统一在这里刷新。
    /// </summary>
    public void OnNavChanged()
    {
        SettingsOpen = false;

        // 「课表」是整页覆盖（网格需要宽度），不是列表筛选：切到它时打开课表页，
        // 切到别的导航项时自动关掉，避免它盖住待办列表。
        if (SelectedNav?.Key == "timetable") OpenTimetable();
        else TimetableOpen = false;

        // 两个详情面板的选中态都要清：它们各绑一个属性，
        // 只清一个会让「先选课程、再选待办」时两块详情叠加显示（实际踩过）。
        SelectedItem = null;
        List.ClearCourseSelection();
        ApplyFilter();
        RefreshNavState();
        OnPropertyChanged(nameof(ListTitle));
        OnPropertyChanged(nameof(ListSubtitle));
        OnPropertyChanged(nameof(ShowHideDoneToggle));
    }

    /// <summary>刷新导航项的选中态与计数角标。</summary>
    private void RefreshNavState()
    {
        var homework = _cache.Where(i => i.Kind == TodoKind.Homework).ToList();
        var pending = homework.Where(i => i.Model.Status != TodoStatus.Completed).ToList();
        var announcements = _cache.Where(i => i.Kind == TodoKind.Announcement).ToList();
        var unreadAnnouncements = announcements.Count(a => !a.IsDone);

        var myDay = pending.Count(i =>
            i.IsOverdue || (i.Model.EffectiveDueAt is { } d && d.Date <= DateTimeOffset.Now.Date));

        foreach (var nav in NavItems)
        {
            nav.IsSelected = ReferenceEquals(nav, SelectedNav);

            var count = nav.Key switch
            {
                "myday" => myDay,
                "planned" => pending.Count(i => i.Model.EffectiveDueAt is not null),
                "homework" => pending.Count,
                // 课程角标 = 有未完成任务点的课程数（不是任务点总数：那是每门课的明细）。
                "course" => List.CoursesWithPending,
                "announcement" => unreadAnnouncements,
                "completed" => homework.Count(i => i.Model.Status == TodoStatus.Completed),
                "all" => pending.Count + unreadAnnouncements,
                _ => 0,
            };

            nav.BadgeText = count > 0 ? count.ToString() : string.Empty;
        }
    }

    // 注意：这里刻意不监听 SelectedNav 的变化。导航状态由视图在点击时统一调用
    // OnNavChanged 刷新，避免「绑定写入 + 显式调用」导致同一套派生逻辑跑两遍。


    partial void OnSelectedItemChanged(TodoItemViewModel? value)
    {
        if (value is null) return;
        // 选中待办时清掉课程选中：两个详情面板各绑一个属性，
        // 不清会让「先选课程、再选待办」时两块详情叠加显示（实际踩过）。
        List.ClearCourseSelection();
        foreach (var i in _cache) i.IsSelected = ReferenceEquals(i, value);
    }

    partial void OnIsCheckingLoginChanged(bool value) => OnPropertyChanged(nameof(LoginButtonText));

    /// <summary>由 View 在复选框被点击时调用，持久化本地勾选。</summary>
    public async Task ToggleDoneAsync(TodoItemViewModel item)
    {
        item.IsDone = !item.IsDone;

        if (item.Kind == TodoKind.Announcement)
        {
            if (item.IsDone) _localState.ReadAnnouncements.Add(item.Id);
            else _localState.ReadAnnouncements.Remove(item.Id);
        }
        else
        {
            if (item.IsDone) _localState.Done.Add(item.Id);
            else _localState.Done.Remove(item.Id);
        }

        await _localStateStore.SaveAsync(_localState).ConfigureAwait(true);
        List.Rebuild();
        OnPropertyChanged(nameof(ListSubtitle));
    }

    /// <summary>「打开」按钮的提示文案：Android 优先拉起雨课堂 App。</summary>
    public string OpenSourceTooltip => OperatingSystem.IsAndroid()
        ? "在雨课堂 App 中打开（未安装则回落浏览器）"
        : "在浏览器中打开：作业直达答题页，公告落到课程日志页";

    /// <summary>应用内 WebView 打开的答题页地址（Android；null = 不显示 WebView 页）。</summary>
    [ObservableProperty]
    private string? _examWebUrl;

    /// <summary>关闭应用内答题页。</summary>
    public void CloseExamWeb() => ExamWebUrl = null;

    /// <summary>在系统浏览器中打开雨课堂原页面。</summary>
    public void OpenSource(TodoItemViewModel item)
    {
        // 优先用同步时生成的链接；缺失时按课程回落到课程日志页。
        var url = item.SourceUrl
                  ?? YktUrls.CourseLog(item.ClassroomId, _session?.UniversityId ?? AppSettings.Current.UniversityId);

        // Android：试卷类作业在应用内 WebView 打开移动答题页（cookie 全局共享、
        // 直接带着登录态），比拉起雨课堂 App 首页的体验好得多。
        if (OperatingSystem.IsAndroid() && TryBuildMobileExamUrl(url) is { } mobileUrl)
        {
            ExamWebUrl = mobileUrl;
            return;
        }

        // 打开 URL 是平台相关操作，转发给平台头注入的实现（Desktop 用 Process.Start），
        // 共享项目不直接依赖平台 API；实现内部自行吞掉异常。
        // Android 优先拉起雨课堂 App（未安装则回落浏览器）。
        if (PlatformServices is { } ps)
        {
            if (!ps.OpenInYktApp(url)) ps.OpenUrl(url);
        }
    }

    /// <summary>
    /// 能在应用内 WebView 打开的页面：v2 web 的试卷/章节作业（同域 cookie），
    /// 以及学习通的作业/考试页（WebView 与登录页共享 cookie 存储）。
    /// 不是这类链接时返回 null（走拉起雨课堂 App / 浏览器）。
    /// 注意不能换成 changjiang-exam 的旧考试系统地址：它是独立 token 会话（401）。
    /// </summary>
    private static string? TryBuildMobileExamUrl(string url)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(url, @"/v2/web/(exam|exercise)/"))
            return url;
        if (url.Contains("chaoxing.com", StringComparison.OrdinalIgnoreCase))
            return url;
        return null;
    }

    /// <summary>
    /// 把公告全部标记为已读。实测雨课堂只有「全部已读」这一个反写接口，
    /// 单条已读只存在于前端状态里。
    /// </summary>
    public async Task<bool> MarkAllReadAsync()
    {
        if (_api is null) return false;
        try
        {
            var ok = await _api.MarkAllReadAsync().ConfigureAwait(true);
            if (!ok) return false;

            foreach (var a in _lastResult?.Announcements ?? [])
            {
                _localState.ReadAnnouncements.Add(a.Id);
            }

            await _localStateStore.SaveAsync(_localState).ConfigureAwait(true);

            foreach (var i in _cache.Where(i => i.Kind == TodoKind.Announcement))
            {
                i.IsDone = true;
            }

            List.Rebuild();
            ShowUnreadBadge = false;
            OnPropertyChanged(nameof(ListSubtitle));
            return true;
        }
        catch (YktApiException)
        {
            return false;
        }
    }

    private static string Tail(string s) => s.Length <= 6 ? s : s[^6..];
}
