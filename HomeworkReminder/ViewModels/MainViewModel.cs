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
            new NavItem("myday", "我的一天", "\uE8C8", "今天到期与已过期的事项"),
            new NavItem("planned", "计划内", "\uE787", "所有有截止时间的事项，按时间排序"),
            new NavItem("homework", "作业", "\uE7C3", "全部作业及其作答进度"),
            new NavItem("announcement", "公告", "\uE7E7", "课程公告，未读的排在前面"),
            new NavItem("completed", "已完成", "\uE73E", "雨课堂判定已完成的作业"),
            new NavItem("all", "全部", "\uE8FD", "作业与公告的全部内容"),
        ];

        _selectedNav = NavItems[0];
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
    }

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
            if (api is null) return null;

            // 网络与解析放后台线程，与 RefreshAsync 保持一致。
            var (result, localState, profile) = await Task.Run(async () =>
            {
                var r = await new SyncService(api).SyncAsync(_cacheFile, null, ct).ConfigureAwait(false);
                var ls = await _localStateStore.LoadAsync(ct).ConfigureAwait(false);
                YktUserProfile? p = null;
                try
                {
                    p = await api.GetUserProfileAsync(ct).ConfigureAwait(false);
                }
                catch (YktAuthExpiredException) { throw; }
                catch (YktApiException) { /* 展示信息，失败无妨 */ }
                return (r, ls, p);
            }, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            // ApplySyncResult 必须在 UI 线程执行（清空/填充 ObservableCollection、触发 Synced）。
            // 自动同步循环跑在线程池上、没有同步上下文可回，必须显式派发到 UI 线程。
            await Dispatcher.UIThread.InvokeAsync(() => ApplySyncResult(result, localState, profile));

            return result;
        }
        catch (YktAuthExpiredException)
        {
            // 与手动同步一致：登录态失效必须浮到界面，而不是只在循环日志里空转。
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                State = AppState.Error;
                ErrorMessage = "登录态已失效，请重新扫码登录。";
                LoginStatusText = "登录已过期";
                _api?.Dispose();
                _api = null;
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

    [ObservableProperty]
    private AppState _state = AppState.NeedsLogin;

    [ObservableProperty]
    private NavItem _selectedNav;

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

    /// <summary>登录页是否可见。</summary>
    public bool ShowLogin => State is AppState.NeedsLogin or AppState.Error;

    /// <summary>
    /// 主内容是否可见。同步中也算「已进入主界面」，否则登录成功后到同步完成之间
    /// 会出现一段空白（登录页已隐藏、主界面还没显示），看起来像卡住。
    /// </summary>
    public bool ShowContent => State is AppState.Syncing or AppState.Ready;

    /// <summary>当前列表的标题，用于中间栏顶部。</summary>
    public string ListTitle => SelectedNav?.Label ?? "待办";

    /// <summary>当前列表的说明文字。</summary>
    public string ListSubtitle
    {
        get
        {
            var count = List.Count;
            var overdue = List.OverdueCount;
            var head = $"{count} 项";
            if (SelectedNav?.Key == "myday")
                return overdue > 0 ? $"{head} · 其中 {overdue} 项已过期" : head;
            if (SelectedNav?.Key == "announcement")
                return $"{head} · 未读 {List.Items.Count(i => !i.IsDone)} 条";
            return head;
        }
    }

    /// <summary>是否显示「隐藏已勾选」开关（仅作业/全部视图有意义）。</summary>
    public bool ShowHideDoneToggle => SelectedNav?.Key is "homework" or "all" or "planned" or "myday";

    // ---------------- 生命周期 ----------------

    public async Task InitializeAsync()
    {
        if (App.ForceLoginPage)
        {
            State = AppState.NeedsLogin;
            LoginStatusText = "尚未登录，请扫码";
            return;
        }

        _localState = await _localStateStore.LoadAsync().ConfigureAwait(true);

        var saved = await _sessionStore.LoadAsync().ConfigureAwait(true);
        if (saved is { HasValue: true })
        {
            // 启动时登录页会先显示，其「后台自动检测」轮询可能已在跑；
            // 会话既已恢复就必须停掉它，否则它会继续把「仍在等待扫码…」写进主界面状态栏。
            _loginCts?.Cancel();

            _session = saved.ToSession();
            _api = new YktApiClient(_session);
            State = AppState.Syncing;
            LoginStatusText = $"已登录 · sessionid …{Tail(_session.SessionId)}";

            // 先用上次缓存的资料把界面填上，避免首屏显示占位；同步成功后会刷新。
            ApplyCachedProfile(saved);

            // 待办也先上缓存：首屏列表秒开，网络同步在后台追平。
            // 缓存加载时已做合法性校验（坏条目在 TodoCacheStore 里被丢弃）。
            _cacheFile = await _todoCacheStore.LoadAsync().ConfigureAwait(true);
            if (_cacheFile is { } cache && (cache.Homework.Count > 0 || cache.Announcements.Count > 0))
            {
                BuildCache(new SyncResult
                {
                    Homework = cache.Homework,
                    Announcements = cache.Announcements,
                    CourseCount = cache.CourseCount,
                    ActivityCount = cache.ActivityCount,
                    UnreadNotificationCount = cache.UnreadNotificationCount,
                    SyncedAt = cache.SyncedAt,
                });
                ApplyFilter();
                RefreshNavState();
                SyncStatus = $"本地缓存 · 上次同步于 {cache.SyncedAt:MM-dd HH:mm}";
                // SyncStatus 马上会被后台同步的进度文本覆盖，缓存提示单独放一条，
                // 在新数据到达前一直可见。
                CacheNote = $"列表是 {cache.SyncedAt:MM-dd HH:mm} 的本地缓存，正在后台同步…";
            }

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
            State = AppState.NeedsLogin;
            LoginStatusText = "尚未登录，请扫码";
            UserName = "未登录";
            UserSubtitle = string.Empty;
            UserAvatarUrl = null;
            UserAvatar = null;
        }
    }

    /// <summary>用会话文件里缓存的资料填充界面。</summary>
    private void ApplyCachedProfile(SessionFile saved)
    {
        UserName = string.IsNullOrWhiteSpace(saved.UserName) ? "（未获取姓名）" : saved.UserName!;
        UserSubtitle = saved.UserSubtitle ?? string.Empty;
        UserAvatarUrl = saved.UserAvatarUrl;
        _ = LoadAvatarAsync(saved.UserAvatarUrl);
    }

    /// <summary>
    /// 下载并解码头像。纯展示信息，任何失败都只是回落到姓名首字。
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
                if (!ct.IsCancellationRequested && State != AppState.Ready)
                {
                    State = AppState.NeedsLogin;
                    LoginStatusText = "尚未检测到登录，请扫码后重试";
                }
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
            State = AppState.Error;
            ErrorMessage = $"登录过程出错：{ex.Message}";
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
            ErrorMessage = ex.Message;
            State = AppState.Error;
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

        await _sessionStore.SaveAsync(SessionFile.From(session)).ConfigureAwait(true);

        ShowCookieInput = false;
        ErrorMessage = string.Empty;
        State = AppState.Syncing;
        LoginStatusText = $"已登录 · sessionid …{Tail(session.SessionId)}";
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
    /// 提供当前登录页里的 WebView，用于退出登录时清除它内部的 Cookie。
    /// 用委托是因为 ViewModel 不该直接依赖视图控件。
    /// </summary>
    public static Func<Avalonia.Controls.NativeWebView?>? WebViewAccessor { get; set; }

    /// <summary>
    /// 系统通知实现。由平台头在启动时注入（桌面端为 <c>WindowsNotifier</c>），
    /// 未注入时退化为 <see cref="NullNotifier"/>，非 Windows 平台也能正常跑。
    /// </summary>
    public static INotifier Notifier { get; set; } = new NullNotifier();

    /// <summary>自动同步与「新作业」通知。</summary>
    public AutoSyncService AutoSync { get; }

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

        State = AppState.NeedsLogin;
        LoginStatusText = "已退出登录，请重新扫码";
        UserName = "未登录";
        UserSubtitle = string.Empty;
        UserAvatarUrl = null;
        UserAvatar = null;
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
        if (_api is null)
        {
            State = AppState.NeedsLogin;
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
            var (result, localState, profile) = await Task.Run(async () =>
            {
                var r = await new SyncService(api).SyncAsync(_cacheFile, progress, ct).ConfigureAwait(false);
                var ls = await _localStateStore.LoadAsync(ct).ConfigureAwait(false);
                // 用户资料只是展示信息：单独取，失败不影响待办同步。
                YktUserProfile? p = null;
                try
                {
                    p = await api.GetUserProfileAsync(ct).ConfigureAwait(false);
                }
                catch (YktAuthExpiredException) { throw; }
                catch (YktApiException) { /* 拿不到就继续用缓存或占位 */ }
                return (r, ls, p);
            }, ct).ConfigureAwait(true);

            ct.ThrowIfCancellationRequested();

            ApplySyncResult(result, localState, profile);

            // 手动同步也要喂给「新作业」基线：否则启动后、首次自动同步之前
            // 手动刷出的新作业会被那次同步的「首次只建基线」静默吞掉，永远收不到通知。
            var fresh = AutoSync.Observe(result);
            if (fresh.Count > 0 && AppSettings.Current.NotifyOnNewHomework)
                AutoSync.Notify(fresh);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // 同步被取消/被新一轮登录取代。ObjectDisposedException 是防御网：
            // 正常路径下 WaitForSyncToStopAsync 已保证不会 Dispose 在途的 _api。
            SyncStatus = "同步已取消";
            State = _cache.Count > 0 ? AppState.Ready : AppState.NeedsLogin;
        }
        catch (YktAuthExpiredException)
        {
            State = AppState.Error;
            ErrorMessage = "登录态已失效，请重新扫码登录。";
            LoginStatusText = "登录已过期";
            _api?.Dispose();
            _api = null;
        }
        catch (YktApiException ex)
        {
            State = AppState.Error;
            ErrorMessage = $"同步失败：{ex.Message}";
        }
        catch (Exception ex)
        {
            State = AppState.Error;
            ErrorMessage = $"发生未预期的错误：{ex.Message}";
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
    /// 应用用户资料，并把登录态文案换成学校信息（比 sessionid 尾号有用得多）。
    /// 拿到新资料时顺带写回会话文件，下次启动即可直接显示。
    /// </summary>
    private void ApplyProfile(YktUserProfile? profile)
    {
        if (profile is null)
        {
            // 没取到就保留已有内容，至少别把缓存的姓名清掉。
            if (string.IsNullOrWhiteSpace(UserSubtitle) && _session is not null)
                LoginStatusText = "已登录";
            return;
        }

        UserName = profile.DisplayName;
        UserSubtitle = profile.Subtitle;
        UserAvatarUrl = profile.AvatarUrl;
        _ = LoadAvatarAsync(profile.AvatarUrl);

        LoginStatusText = string.IsNullOrWhiteSpace(profile.School)
            ? "已登录"
            : $"已登录 · {profile.School}";

        _ = PersistProfileAsync(profile);
    }

    private async Task PersistProfileAsync(YktUserProfile profile)
    {
        if (_session is null) return;
        try
        {
            var file = SessionFile.From(_session);
            file.UserName = profile.DisplayName;
            file.UserSubtitle = profile.Subtitle;
            file.UserAvatarUrl = profile.AvatarUrl;
            await _sessionStore.SaveAsync(file).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 缓存写失败不影响本次运行。
        }
    }

    /// <summary>
    /// 把一次同步的结果落到界面上。
    /// <para>
    /// 手动同步与自动同步共用这一段，保证两条路径的界面表现一致
    /// （导航角标、未读徽标、警告条、统计行）。必须在 UI 线程上调用。
    /// </para>
    /// </summary>
    private void ApplySyncResult(SyncResult result, LocalState localState, YktUserProfile? profile)
    {
        _lastResult = result;
        _localState = localState;
        ApplyProfile(profile);

        // 落盘待办缓存：下次启动秒开列表 + 作为下次增量同步的比对基准。
        // 注意用合并前（HomeworkForCache）的列表：合并会丢课堂归属，被并掉的课程下次无法增量复用。
        _cacheFile = new TodoCacheFile
        {
            SyncedAt = result.SyncedAt,
            Homework = result.HomeworkForCache.ToList(),
            Announcements = result.Announcements.ToList(),
            CourseSignatures = new Dictionary<long, string>(result.CourseSignatures),
            CourseCount = result.CourseCount,
            ActivityCount = result.ActivityCount,
            UnreadNotificationCount = result.UnreadNotificationCount,
        };
        _ = _todoCacheStore.SaveAsync(_cacheFile);

        BuildCache(result);
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
        StatusDetail =
            $"{result.CourseCount} 门课程 · {result.ActivityCount} 条日志 · " +
            $"{result.Homework.Count} 项作业（待完成 {pending}） · {result.Announcements.Count} 条公告" +
            (result.ReusedCourses > 0 ? $" · {result.ReusedCourses} 门课走增量校验" : "");

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
        IEnumerable<TodoItemViewModel> query = SelectedNav?.Key switch
        {
            "myday" => _cache
                .Where(i => i.Kind == TodoKind.Homework && i.Model.Status != TodoStatus.Completed)
                .Where(i => i.IsOverdue || (i.Model.DueAt is { } d && d.Date <= DateTimeOffset.Now.Date))
                .OrderBy(i => i.Model.DueAt ?? DateTimeOffset.MaxValue),

            "planned" => _cache
                .Where(i => i.Model.DueAt is not null && i.Model.Status != TodoStatus.Completed)
                .OrderBy(i => i.Model.DueAt),

            "homework" => _cache
                .Where(i => i.Kind == TodoKind.Homework)
                .OrderBy(i => i.Model.Status == TodoStatus.Completed ? 1 : 0)
                .ThenBy(i => i.Model.DueAt ?? DateTimeOffset.MaxValue),

            "announcement" => _cache
                .Where(i => i.Kind == TodoKind.Announcement)
                .OrderBy(i => i.IsDone ? 1 : 0)
                .ThenByDescending(i => i.Model.PublishedAt),

            "completed" => _cache
                .Where(i => i.Kind == TodoKind.Homework && i.Model.Status == TodoStatus.Completed)
                .OrderByDescending(i => i.Model.DueAt ?? DateTimeOffset.MinValue),

            _ => _cache
                .Where(i => !(i.Kind == TodoKind.Homework && i.Model.Status == TodoStatus.Completed))
                .OrderBy(i => i.Model.DueAt ?? DateTimeOffset.MaxValue)
                .ThenBy(i => i.Kind),
        };

        List.EmptyMessage = SelectedNav?.Key switch
        {
            "myday" => "今天没有到期的事项，轻松一下。",
            "planned" => "没有带截止时间的事项。",
            "announcement" => "没有公告。",
            "completed" => "还没有已完成的作业记录。",
            _ => "这里没有待办事项。",
        };

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
        SelectedItem = null;
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
            i.IsOverdue || (i.Model.DueAt is { } d && d.Date <= DateTimeOffset.Now.Date));

        foreach (var nav in NavItems)
        {
            nav.IsSelected = ReferenceEquals(nav, SelectedNav);

            var count = nav.Key switch
            {
                "myday" => myDay,
                "planned" => pending.Count(i => i.Model.DueAt is not null),
                "homework" => pending.Count,
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
        foreach (var i in _cache) i.IsSelected = ReferenceEquals(i, value);
    }

    partial void OnIsCheckingLoginChanged(bool value) => OnPropertyChanged(nameof(LoginButtonText));

    partial void OnStateChanged(AppState value)
    {
        OnPropertyChanged(nameof(ShowLogin));
        OnPropertyChanged(nameof(ShowContent));
    }

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

    /// <summary>在系统浏览器中打开雨课堂原页面。</summary>
    public void OpenSource(TodoItemViewModel item)
    {
        // 优先用同步时生成的链接；缺失时按课程回落到课程日志页。
        var url = item.SourceUrl
                  ?? YktUrls.CourseLog(item.ClassroomId, _session?.UniversityId ?? AppSettings.Current.UniversityId);
        // 打开 URL 是平台相关操作，转发给平台头注入的实现（Desktop 用 Process.Start），
        // 共享项目不直接依赖平台 API；实现内部自行吞掉异常。
        PlatformServices?.OpenUrl(url);
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
