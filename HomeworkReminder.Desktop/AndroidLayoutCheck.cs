using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeworkReminder.Models;
using HomeworkReminder.ViewModels;
using HomeworkReminder.Views;

namespace HomeworkReminder.Desktop;

/// <summary>
/// Android 手机尺寸的**界面遮挡审计**（<c>--androidcheck &lt;outDir&gt;</c>）。
/// <para>
/// 存在的理由：本项目反复栽在同一类问题上——「某个控件铺满整行时压住了旁边的列」。
/// 实测踩到过的：课表空状态提示压住节次刻度与星期列头、工具栏按钮被挤出屏幕、
/// 列表行徽标被标题盖住。这些**数据层断言不出来，桌面 1180x760 也复现不了**，
/// 只有「在手机宽度下渲染并量坐标」才看得见。
/// </para>
/// <para>
/// 做法：把移动外壳（<see cref="MainView"/> + <see cref="App.MobileShellFactory"/>，
/// 与 Android 头走同一条路径）放进一个指定尺寸的窗口，逐屏渲染成 PNG，
/// 同时遍历可视树量出三类越界：
/// <list type="number">
///   <item>视口越界：控件落在窗口矩形之外（被屏幕裁掉）。</item>
///   <item>祖先裁剪：控件超出最近的 <c>ClipToBounds</c> 祖先（被容器裁掉）。
///       落在 <see cref="ScrollViewer"/> 里的内容不算——那是滚动，不是缺陷。</item>
///   <item>文字截断：<see cref="TextBlock"/> 的实测内容尺寸大于被分配到的尺寸
///       （不换行的长文本会被裁成半句，且没有任何省略号提示）。</item>
/// </list>
/// </para>
/// <para>
/// 与 <c>--selfcheck</c> 的分工：那边验「XAML/绑定能不能建起来」，这边验
/// <b>「在 360dp 宽的手机上谁压住了谁」</b>。
/// </para>
/// </summary>
internal static class AndroidLayoutCheck
{
    // ---------------- 缺陷模型 ----------------

    private enum Severity
    {
        /// <summary>内容被裁掉或超出屏幕：用户看不到完整内容。</summary>
        Error,

        /// <summary>叠在别的元素上：能看到，但读不清。</summary>
        Warning,
    }

    private sealed record Defect(Severity Severity, string State, string Where, string What);

    /// <summary>一节审计（一个屏幕状态）的渲染结果。</summary>
    private sealed record Sheet(int Width, int Height, string State, string Path, IReadOnlyList<Defect> Defects);

    // ---------------- 屏幕状态 ----------------

    /// <summary>
    /// 一个待审计的屏幕状态。
    /// </summary>
    /// <param name="Name">用于文件名与报告，ASCII 短名。</param>
    /// <param name="Description">中文说明，写进报告。</param>
    /// <param name="Apply">把 ViewModel 置成该状态（在窗口显示后调用，与用户操作路径一致）。</param>
    /// <param name="NeedsWebView">该状态会实例化原生 WebView，需要额外等待其适配器就绪。</param>
    private sealed record ScreenState(string Name, string Description, Action<MainViewModel> Apply, bool NeedsWebView = false);

    // ---------------- 入口 ----------------

    /// <summary>
    /// 跑一遍审计。返回进程退出码：0 = 无缺陷，2 = 有缺陷，1 = 抛异常。
    /// </summary>
    public static int Run(string outDir, bool includeWebViewStates, bool includeDesktopShell)
    {
        Console.OutputEncoding = Encoding.UTF8;

        App.DeferInitialLoad = true;
        App.MobileShellFactory = () => new MobileShellView();

        var builder = Program.BuildAvaloniaApp();
        builder.SetupWithoutStarting();

        var app = (App)Application.Current!;
        var vm = app.ViewModel ?? throw new InvalidOperationException("App 没有创建 ViewModel");

        // 用样本数据渲染：审计要看的正是「长课程名 / 多徽标 / 跨节次课块」这些
        // 真正把布局撑破的内容。没有数据时列表只有空状态，等于什么都没测。
        // （实际注入在 ResetState 里逐屏做——见那里的注释。）
        vm.State = AppState.Ready;

        Directory.CreateDirectory(outDir);

        var sizes = ParseSizes();
        var sheets = new List<Sheet>();
        var fatal = new List<string>();

        try
        {
            AuditShell(vm, mobile: true, sizes, outDir, includeWebViewStates, sheets, fatal);
            if (includeDesktopShell)
            {
                // 桌面外壳在同尺寸下不参与（MinWidth=960），但「窄屏该走哪套外壳」
                // 是响应式方案的核心，所以单独量一遍 960 宽的最小桌面尺寸。
                // 注意：审计里**直接构造 TodoShellView**，不走 MainView 的工厂——
                // 工厂是应用启动时读一次的静态字段，同一进程里第二次审计拿到的
                // 还是上一次那个外壳（实测这里踩过：桌面那轮量的是移动外壳）。
                AuditShell(vm, mobile: false, [new Size(960, 760)], outDir, false, sheets, fatal);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("审计过程中抛出异常：");
            Console.WriteLine(ex);
            return 1;
        }

        var report = BuildReport(sheets, fatal);
        var reportPath = Path.Combine(outDir, "android-layout-report.md");
        File.WriteAllText(reportPath, report, Encoding.UTF8);
        Console.WriteLine(report);
        Console.WriteLine($"报告：{reportPath}");

        var errors = sheets.SelectMany(s => s.Defects).Count(d => d.Severity == Severity.Error);
        return errors == 0 && fatal.Count == 0 ? 0 : 2;
    }

    /// <summary>视口尺寸。默认覆盖主流手机竖屏宽度（360 最窄常见、411 主流大屏）。</summary>
    private static IReadOnlyList<Size> ParseSizes()
    {
        var raw = Environment.GetEnvironmentVariable("ANDROIDCHECK_SIZES");
        if (string.IsNullOrWhiteSpace(raw))
            return [new Size(360, 780), new Size(393, 851), new Size(411, 891), new Size(320, 568)];

        var list = new List<Size>();
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var wh = part.Split('x', 'X');
            if (wh.Length == 2
                && double.TryParse(wh[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                && double.TryParse(wh[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
            {
                list.Add(new Size(w, h));
            }
        }

        return list.Count > 0 ? list : [new Size(360, 780)];
    }

    // ---------------- 外壳与逐屏渲染 ----------------

    private static void AuditShell(
        MainViewModel vm, bool mobile, IReadOnlyList<Size> sizes, string outDir,
        bool includeWebViewStates, List<Sheet> sheets, List<string> fatal)
    {
        // 外壳在 MainView 构造时就读掉了工厂，之后改工厂也影响不到已建好的视图。
        // 所以这里**直接把外壳控件构造出来**交给审计：移动端要的是 MainView
        // （Android 上就是它，里面还包着壁纸层与 ShellHost），桌面端要的是
        // TodoShellView 本身——用 MainView 会在同一进程里拿到上一次设的移动外壳。
        Func<MainViewModel, Control> build = mobile
            ? v => new MainView { DataContext = v }
            : v => new TodoShellView { DataContext = v };

        var states = BuildStates(includeWebViewStates);

        foreach (var size in sizes)
        {
            foreach (var state in states)
            {
                try
                {
                    sheets.Add(RenderOne(vm, state, size, outDir, build));
                }
                catch (Exception ex)
                {
                    // 单个状态失败不能终止整轮审计：WebView 状态在无头环境里本来就
                    // 可能建不起来，而其余屏幕仍然要看。
                    fatal.Add($"{(mobile ? "移动" : "桌面")} {size.Width:0}x{size.Height:0} {state.Name}：{ex.GetType().Name} {ex.Message}");
                }
            }
        }
    }

    private static Sheet RenderOne(
        MainViewModel vm, ScreenState state, Size size, string outDir, Func<MainViewModel, Control> build)
    {
        ResetState(vm);
        state.Apply(vm);

        var view = build(vm);

        // **用一个固定尺寸的框把视口钉死**，而不是直接给窗口设 Width/Height。
        // 直接设窗口尺寸会被平台的 DPI 取整改掉（实测 393x851 变成了 393.6x851.2），
        // 于是每个控件都"超出窗口 0.6dp"，报告被噪声淹没。
        // 框自己 Width/Height 固定 + ClipToBounds，量出来就是要求的那个尺寸；
        // 窗口给大一圈，纯粹当外壳。
        var frame = new Border
        {
            Width = size.Width,
            Height = size.Height,
            ClipToBounds = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Background = Brushes.White,
            Child = new Panel { Children = { view } },
            DataContext = vm,
        };

        // 探测框放进一个显式行列的 Grid，而不是直接当窗口内容：
        // 直接当内容时窗口的 ContentPresenter 会**用无穷大去测量**它，
        // 于是框里的 Grid（桌面外壳就是 Grid）按无穷宽度分配列宽，
        // 量出来的布局与真实窗口完全不是一回事
        //（实测：960 宽下 TodoDetailView 被放到 x=952，报「超出视口 272dp」的假缺陷）。
        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{size.Width:0},*"),
            RowDefinitions = new RowDefinitions($"{size.Height:0},*"),
            DataContext = vm,
        };
        root.Children.Add(frame);

        var window = new Window
        {
            Width = size.Width + 80,
            Height = size.Height + 80,
            CanResize = false,
            ShowInTaskbar = false,
            Background = Brushes.White,
            Content = root,
            DataContext = vm,
        };

        try
        {
            window.Show();
            Pump(state.NeedsWebView ? 40 : 6);
            Pump(3);

            var defects = Inspect(frame, state).ToList();

            if (DebugEnabled)
            {
                DumpState(state, size, frame, vm, view);
            }

            var file = Path.Combine(outDir, $"{state.Name}-{(int)size.Width}x{(int)size.Height}.png");
            var pixel = new PixelSize((int)Math.Round(size.Width), (int)Math.Round(size.Height));
            using (var bitmap = new RenderTargetBitmap(pixel, new Vector(96, 96)))
            {
                bitmap.Render(frame);
                bitmap.Save(file);
            }

            return new Sheet((int)size.Width, (int)size.Height, state.Name, file, defects);
        }
        finally
        {
            window.Close();
            Pump(2);
        }
    }

    /// <summary>把 ViewModel 复位到「刚进主界面」的干净状态，避免上一屏的残留串味。</summary>
    private static void ResetState(MainViewModel vm)
    {
        vm.SettingsOpen = false;
        vm.TimetableOpen = false;
        vm.DrawerOpen = false;
        vm.LoginPageOpen = null;
        vm.ExamWebUrl = null;
        vm.JwImportOpen = false;
        vm.SelectedItem = null;
        vm.List.ClearCourseSelection();
        vm.IsBusy = false;
        vm.HasWarnings = false;
        vm.WarningText = string.Empty;
        vm.AuthWarning = string.Empty;

        // **每屏都要重灌样本数据**：`list-empty` 那一屏会把列表清空，
        // 而它跑完之后所有后续屏幕都只剩空状态（实测：只有第一屏有列表行，
        // 其余 12 屏全在测「空列表」——审计看起来跑了 13 屏，实际只测了 1 屏）。
        SeedForAudit(vm);

        // 课表数据同理每次都重灌：网格宽度依赖上一屏的窗口尺寸，
        // 重灌才能保证这一屏是按当前尺寸重算的。
        vm.LoadScheduleForCheck(SampleSchedule());
        vm.Timetable.HasTermAnchor = false;   // 与用户截图一致：未设学期第一周，列头无日期
        vm.Timetable.SelectedBlock = null;
        vm.Timetable.ShowOtherWeeks = false;
        vm.Timetable.ShowWeekend = true;
    }

    private static IReadOnlyList<ScreenState> BuildStates(bool includeWebViewStates)
    {
        var states = new List<ScreenState>
        {
            new("list", "待办列表（有内容：长标题 / 逾期 / 进度徽标 / 已完成）",
                _ => { }),
            new("list-empty", "待办列表（空状态文案）",
                vm => vm.List.Replace([])),
            new("drawer", "抽屉（侧边栏导航 + 遮罩）",
                vm => vm.DrawerOpen = true),
            new("detail", "待办详情二级页（全屏覆盖 + 返回栏）",
                vm => vm.SelectedItem = vm.List.VisibleItems.FirstOrDefault()),
            new("settings-general", "设置页 · 通用",
                vm => vm.OpenSettings()),
            new("settings-account", "设置页 · 账号",
                vm => { vm.OpenSettings(); vm.ShowSettingsTab(1); }),
            new("settings-timetable", "设置页 · 课表（节次时间表编辑器）",
                vm => { vm.OpenSettings(); vm.ShowSettingsTab(2); }),
            new("settings-about", "设置页 · 关于",
                vm => { vm.OpenSettings(); vm.ShowSettingsTab(3); }),
            new("timetable", "课表周视图（有课 + 未设学期基准提示条）",
                vm => vm.OpenTimetable()),
            new("timetable-empty", "课表周视图（本周无课——带空状态提示）",
                vm => { vm.LoadScheduleForCheck(new Services.ScheduleFile()); vm.OpenTimetable(); }),
            new("timetable-detail", "课表周视图 + 课程详情面板",
                vm =>
                {
                    vm.OpenTimetable();
                    vm.Timetable.SelectedBlock = vm.Timetable.Blocks.FirstOrDefault();
                }),
            new("timetable-otherweeks", "课表周视图（显示非本周，最多栏数）",
                vm =>
                {
                    vm.Timetable.ShowOtherWeeks = true;
                    vm.OpenTimetable();
                }),
            new("warning-busy", "列表 + 警告条 + 忙碌条（最拥挤的头部）",
                vm =>
                {
                    vm.HasWarnings = true;
                    vm.WarningText = "批改网登录态已失效，本次未读取到作文任务。";
                    vm.AuthWarning = "雨课堂登录态已失效，请到 设置 → 账号 重新登录。";
                    vm.IsBusy = true;
                    vm.SyncStatus = "正在同步…";
                }),
        };

        if (includeWebViewStates)
        {
            // 原生 WebView 在无头离屏渲染里页面内容不一定出得来，但宿主页的
            // 「返回栏 + 内容区」布局仍要看——它同样是整页覆盖。
            states.Add(new ScreenState("login-ykt", "登录页（雨课堂 WebView 宿主）",
                vm => vm.LoginPageOpen = "ykt", NeedsWebView: true));
            states.Add(new ScreenState("jwimport", "教务导入页（WebView + 悬浮球）",
                vm => vm.OpenJwImport(), NeedsWebView: true));
            states.Add(new ScreenState("examweb", "应用内答题页（WebView 宿主）",
                vm => vm.ExamWebUrl = "https://www.yuketang.cn/", NeedsWebView: true));
        }

        return states;
    }

    // ---------------- 几何检查 ----------------

    private static IEnumerable<Defect> Inspect(Border frame, ScreenState state)
    {
        var windowRect = new Rect(0, 0, frame.Bounds.Width, frame.Bounds.Height);
        var size = windowRect.Size;

        foreach (var control in frame.GetVisualDescendants().OfType<Control>())
        {
            if (!control.IsVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) continue;
            if (control is ScrollBar) continue;
            if (InsideScrollViewer(control)) continue;   // 滚动内容超出容器是正常的

            var topLeft = control.TranslatePoint(new Point(0, 0), frame);
            if (topLeft is not { } tl) continue;
            var rect = new Rect(tl, control.Bounds.Size);

            // ① 视口越界
            if (rect.X < -0.5 || rect.Y < -0.5
                || rect.Right > windowRect.Right + 0.5 || rect.Bottom > windowRect.Bottom + 0.5)
            {
                var over = new[]
                {
                    -rect.X, -rect.Y,
                    rect.Right - windowRect.Right,
                    rect.Bottom - windowRect.Bottom,
                }.Max();
                yield return new Defect(
                    Severity.Error, state.Name, Describe(control),
                    $"超出视口 {over:0.#}dp（控件 {rect.X:0.#},{rect.Y:0.#} {rect.Width:0.#}x{rect.Height:0.#}，"
                    + $"视口 {size.Width:0}x{size.Height:0}）");
            }

            // ② 祖先裁剪
            if (NearestClipper(control) is { } clipper)
            {
                var clip = ClipRectOf(clipper, frame);
                if (clip is { } c)
                {
                    var over = new[]
                    {
                        c.X - rect.X, c.Y - rect.Y,
                        rect.Right - c.Right, rect.Bottom - c.Bottom,
                    }.Max();
                    if (over > 0.5)
                    {
                        yield return new Defect(
                            Severity.Error, state.Name, Describe(control),
                            $"被祖先 {clipper.GetType().Name}{(string.IsNullOrEmpty(clipper.Name) ? string.Empty : $"#{clipper.Name}")} 裁剪 {over:0.#}dp");
                    }
                }
            }

            // ③ 文字截断
            if (control is TextBlock text && TextDefect(text, rect, state) is { } textDefect)
                yield return textDefect;
            // ④ 输入控件里的溢出（TextBlock 已由 ③ 覆盖）
            if (control is TextBox box && TextBoxDefect(box, rect, state) is { } boxDefect)
                yield return boxDefect;
        }
    }

    private static Defect? TextDefect(TextBlock text, Rect rect, ScreenState state)
    {
        if (string.IsNullOrEmpty(text.Text) || text.TextLayout is not { } layout) return null;

        var padding = text.Padding;
        var availableWidth = rect.Width - padding.Left - padding.Right;
        var availableHeight = rect.Height - padding.Top - padding.Bottom;
        if (availableWidth <= 0 || availableHeight <= 0) return null;

        // 带省略号的截断是作者的选择（能看到「…」，用户知道后面还有内容）；
        // 不带省略号的截断是把一句话硬切掉，用户看不出丢了东西——那才是缺陷。
        var deliberate = text.TextTrimming != TextTrimming.None;

        // **横向**：不能拿 TextLayout.Width 比——它本身就是"按约束排完版"的结果，
        // 永远 ≤ 可用宽度，永远判不出溢出（实测这里差点让整套检测形同虚设：
        // 空状态那句「这里没有待办事项。」在 360dp 屏上被硬裁到 108dp，
        // 而 TextLayout.Width 报的就是 108）。必须另算**自然宽度**。
        // 只对不换行的文本判：换行的文本本来就会折行，不是缺陷。
        if (text.TextWrapping == TextWrapping.NoWrap && !deliberate)
        {
            var natural = MeasureNatural(text);
            if (natural is { } n && n.Width > availableWidth + 0.5)
            {
                return new Defect(Severity.Error, state.Name, Describe(text),
                    $"文字被硬裁：不换行需要 {n.Width:0.#}dp 宽，只分到 {availableWidth:0.#}dp（“{Shown(text.Text)}”）");
            }
        }

        // **纵向**：TextLayout.Height 是排版后的真实高度（MaxLines 截断也反映在这里），
        // 它超过布置高度就是真的画不下。
        if (layout.Height > availableHeight + 0.5)
        {
            return new Defect(
                deliberate ? Severity.Warning : Severity.Error, state.Name, Describe(text),
                $"文字被裁：排版需要 {layout.Height:0.#}dp 高，只分到 {availableHeight:0.#}dp（“{Shown(text.Text)}”）");
        }

        return null;
    }

    /// <summary>
    /// 一段文本在**不受约束**时的自然尺寸。只用于判断「硬裁」，不参与任何生产逻辑。
    /// </summary>
    private static Size? MeasureNatural(TextBlock text)
    {
        try
        {
            var formatted = new FormattedText(
                text.Text ?? string.Empty,
                System.Globalization.CultureInfo.CurrentCulture,
                text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                text.FontSize,
                null);
            return new Size(formatted.Width, formatted.Height);
        }
        catch (Exception)
        {
            // 字体不可用时宁可漏报，也不要让审计因为一个探测性调用而中断。
            return null;
        }
    }

    private static string Shown(string? s) =>
        s is null ? string.Empty : s.Length > 24 ? s[..24] + "…" : s;

    private static Defect? TextBoxDefect(TextBox box, Rect rect, ScreenState state)
    {
        if (string.IsNullOrEmpty(box.Text)) return null;
        var desired = box.DesiredSize;
        if (desired.Width <= rect.Width + 0.5 && desired.Height <= rect.Height + 0.5) return null;

        return new Defect(Severity.Error, state.Name, Describe(box),
            $"输入框内容被裁：需要 {desired.Width:0.#}x{desired.Height:0.#}，只有 {rect.Width:0.#}x{rect.Height:0.#}（“{box.Text}”）");
    }

    /// <summary>是否落在滚动区内（含自身）——滚动内容超出视口是正常的。</summary>
    private static bool InsideScrollViewer(Visual visual)
        => visual is ScrollViewer || visual.GetVisualAncestors().OfType<ScrollViewer>().Any();

    /// <summary>最近的一个会裁剪子元素的祖先。</summary>
    private static Control? NearestClipper(Visual visual)
        => visual.GetVisualAncestors().OfType<Control>().FirstOrDefault(c => c.ClipToBounds);

    private static Rect? ClipRectOf(Visual clipper, Visual root)
    {
        var tl = clipper.TranslatePoint(new Point(0, 0), root);
        return tl is { } p ? new Rect(p, clipper.Bounds.Size) : null;
    }

    private static string Describe(Control control)
    {
        var name = control.GetType().Name;
        if (!string.IsNullOrEmpty(control.Name)) name += $"#{control.Name}";
        if (control.Classes.Count > 0) name += $".{string.Join('.', control.Classes)}";
        if (control.DataContext is TodoItemViewModel item) name += $"({item.DisplaySubject})";
        else if (control.DataContext is TimetableBlockViewModel block) name += $"({block.Name})";
        return name;
    }

    /// <summary>
    /// 逐控件打印实测尺寸（<c>ANDROIDCHECK_DEBUG=1</c>）。
    /// <para>
    /// 排查「到底是谁被裁了」时，报告里的聚合信息不够用：得看到每个控件的
    /// 测量尺寸 / 布置尺寸 / 可视性 / 祖先链，才能判断是真缺陷还是判据本身太宽。
    /// 这个开关在定位噪声时救过一次（「节次」表头要 74dp 却只给 68dp，
    /// 但它的实际布置尺寸就是 74dp，根本没被裁——是判据用错了参照量）。
    /// </para>
    /// </summary>
    private static void DumpState(ScreenState state, Size size, Border frame, MainViewModel vm, Control shell)
    {
        Console.WriteLine($"--- {state.Name} {size.Width:0}x{size.Height:0} ---");
        Console.WriteLine($"  frame={frame.Bounds.Width:0.#}x{frame.Bounds.Height:0.#} shell={shell.GetType().Name}");
        foreach (var c in frame.GetVisualDescendants().OfType<Control>()
                     .Where(c => c.Name is "ShellGrid" or "TodoPane" || c is TodoDetailView or MobileShellView or SidebarView or TodoListView)
                     .Take(8))
        {
            var p = c.TranslatePoint(new Point(0, 0), frame);
            Console.WriteLine($"  BOX {Describe(c)} at={p?.X:0.#},{p?.Y:0.#} {c.Bounds.Width:0.#}x{c.Bounds.Height:0.#}");
        }
        Console.WriteLine(
            $"  VM: nav={vm.SelectedNav?.Key} items={vm.List.Items.Count} visible={vm.List.VisibleItems.Count} "
            + $"hasAnyContent={vm.List.HasAnyContent} hasItems={vm.List.HasItems} "
            + $"showCourseList={vm.List.ShowCourseList} hideDone={vm.List.HideLocallyDone} "
            + $"empty='{vm.List.EmptyMessage}'");

        foreach (var text in frame.GetVisualDescendants().OfType<TextBlock>())
        {
            var tl = text.TranslatePoint(new Point(0, 0), frame);
            var ancestors = string.Join(" < ", text.GetVisualAncestors().OfType<Control>().Take(4).Select(Describe));
            Console.WriteLine(
                $"  TB '{Clip(text.Text)}' vis={text.IsVisible} wrap={text.TextWrapping} trim={text.TextTrimming} "
                + $"layout={Layout(text)} "
                + $"desired={text.DesiredSize.Width:0.#}x{text.DesiredSize.Height:0.#} "
                + $"bounds={text.Bounds.Width:0.#}x{text.Bounds.Height:0.#} "
                + $"pad={text.Padding.Left:0.#},{text.Padding.Top:0.#},{text.Padding.Right:0.#},{text.Padding.Bottom:0.#} "
                + $"at={tl?.X:0.#},{tl?.Y:0.#} [{ancestors}]");
        }

        static string Layout(TextBlock t)
            => t.TextLayout is { } l ? $"{l.Width:0.#}x{l.Height:0.#}" : "(null)";

        static string Clip(string? s) => s is null ? string.Empty : s.Length > 22 ? s[..22] + "…" : s;    }

    /// <summary>把「谁是视口 / 谁被裁 / 谁只是文字截断」单独打出来（<c>ANDROIDCHECK_DEBUG=1</c>）。</summary>
    private static bool DebugEnabled => Environment.GetEnvironmentVariable("ANDROIDCHECK_DEBUG") == "1";

    // ---------------- 数据与泵 ----------------

    /// <summary>反复清空调度器队列，让绑定、容器生成、图片加载都跑完。</summary>
    private static void Pump(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(25);
        }
    }

    /// <summary>
    /// 灌一组**刻意刁难布局**的样本数据。
    /// <para>
    /// 取长课程名 / 长标题 / 最长的剩余时间文案 / 三种徽标同时出现：
    /// 遮挡类问题只有在内容把行撑满时才暴露，用「数学作业」这种短标题测不出来。
    /// </para>
    /// </summary>
    private static void SeedForAudit(MainViewModel vm)
    {
        var now = DateTimeOffset.Now;
        var items = new List<TodoItemViewModel>
        {
            Make("概率论与数理统计B（2026秋-计算机2301班）", "第三章 随机变量及其分布 课后习题（共 22 题）",
                TodoStatus.InProgress, 8, 22, now.AddDays(2)),
            Make("大学物理（2026秋-电子信息2302班）", "第十二章 电磁感应 单元测试",
                TodoStatus.NotStarted, 0, 0, now.AddDays(-1)),
            Make("数据结构与算法（2026秋-软件工程2301班）", "实验六 平衡二叉树 AVL 的插入与删除",
                TodoStatus.Completed, 15, 15, now.AddDays(-5)),
            Make("马克思主义基本原理", "关于《资本论》第一卷的读书报告",
                TodoStatus.Unknown, null, null, null),
        };
        vm.List.Replace(items);

        vm.UserName = "张三";
        vm.UserSubtitle = "珠海科技学院 · 20231234567";
        vm.SelectedNav = vm.NavItems.FirstOrDefault(n => n.Key == "homework") ?? vm.NavItems[0];
    }

    private static TodoItemViewModel Make(
        string subject, string title, TodoStatus status, int? done, int? total, DateTimeOffset? due)
        => new(new TodoItem
        {
            Id = $"{subject}|{title}",
            Kind = TodoKind.Homework,
            Title = title,
            CourseName = subject,
            Subject = subject,
            ClassroomId = 1,
            Status = status,
            Done = done,
            Total = total,
            DueAt = due,
        });

    /// <summary>
    /// 内置课表样本。与 <c>--ttcheck</c> 用的是同一组形状：
    /// 跨节次、单双周、同一天同一节两门课（分栏）、周六有课。
    /// </summary>
    private static Services.ScheduleFile SampleSchedule()
    {
        var periods = Services.Timetable.CreateDefaultPeriods();
        var items = new List<ScheduleItem>
        {
            MakeClass("高等数学A", 1, 1, 2, 1, 16, WeekParity.Every, "明德楼B107", "张三"),
            MakeClass("大学英语Ⅲ", 1, 3, 4, 1, 16, WeekParity.Every, "外语楼305", "李四"),
            MakeClass("体育（篮球）", 1, 7, 8, 1, 16, WeekParity.Every, "田径场", "王五"),
            MakeClass("程序设计基础", 2, 1, 2, 1, 16, WeekParity.Every, "机房A", "赵六"),
            MakeClass("线性代数", 2, 3, 4, 1, 8, WeekParity.Every, "博文楼203", "钱七"),
            MakeClass("大学物理", 2, 5, 6, 1, 15, WeekParity.Odd, "理科楼401", "孙八"),
            MakeClass("数据结构", 3, 1, 2, 1, 16, WeekParity.Every, "信息楼501", "周九"),
            MakeClass("数据结构实验", 3, 1, 2, 2, 16, WeekParity.Every, "机房B", "周九"),
            MakeClass("概率论与数理统计", 3, 5, 6, 1, 16, WeekParity.Every, "博文楼E203", "吴十"),
            MakeClass("模拟电子技术", 4, 3, 4, 1, 16, WeekParity.Every, "六教M305", "宋刚"),
            MakeClass("信号与系统", 4, 7, 8, 1, 16, WeekParity.Every, "六教M211", "邱杨"),
            MakeClass("中国近现代史纲要", 5, 1, 2, 1, 14, WeekParity.Every, "明德楼阶梯B107", "郑雨"),
            MakeClass("人工智能与法律", 5, 5, 6, 9, 16, WeekParity.Every, "博文楼A201", "陈一"),
            MakeClass("创新创业实践", 6, 3, 4, 1, 16, WeekParity.Even, "创业园", "林二"),
        };

        foreach (var item in items)
        {
            var time = Services.Timetable.ResolveSessionTime(item, periods);
            item.StartTime = time.StartTime;
            item.EndTime = time.EndTime;
        }

        return new Services.ScheduleFile { Items = items, Periods = periods };
    }

    private static ScheduleItem MakeClass(
        string name, int weekday, int startPeriod, int endPeriod, int startWeek, int endWeek,
        WeekParity parity, string location, string teacher) => new()
    {
        Id = $"{name}|{weekday}|{startPeriod}|{endPeriod}|{startWeek}|{endWeek}|{parity}",
        Name = name,
        Weekday = weekday,
        StartPeriod = startPeriod,
        EndPeriod = endPeriod,
        StartWeek = startWeek,
        EndWeek = endWeek,
        WeekParity = parity,
        Location = location,
        Teacher = teacher,
        Term = Services.AppSettings.Current.Term,
    };

    // ---------------- 报告 ----------------

    private static string BuildReport(IReadOnlyList<Sheet> sheets, IReadOnlyList<string> fatal)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Android 窄屏界面遮挡审计");
        sb.AppendLine();
        sb.AppendLine($"> 生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("> 命令：`HomeworkReminder.Desktop.exe --androidcheck <outDir>`");
        sb.AppendLine();
        sb.AppendLine("窗口采用移动外壳（`MainView` + `MobileShellFactory` → `MobileShellView`），"
                      + "与 Android 头同一条路径；数据为内置样本（含长标题、多徽标、跨节次课块）。");
        sb.AppendLine();

        var total = sheets.Sum(s => s.Defects.Count);
        var errors = sheets.Sum(s => s.Defects.Count(d => d.Severity == Severity.Error));
        var warnings = total - errors;
        sb.AppendLine($"**合计 {total} 处（越界 / 裁剪 / 硬截断 {errors} 处，带省略号的截断 {warnings} 处）**，共 {sheets.Count} 屏。");
        sb.AppendLine();

        if (fatal.Count > 0)
        {
            sb.AppendLine("## 渲染失败");
            sb.AppendLine();
            foreach (var f in fatal) sb.AppendLine($"- {f}");
            sb.AppendLine();
        }

        // 同一类缺陷会在 4 个尺寸上各报一次，按「状态 + 控件」聚合，报告才读得下去。
        var grouped = sheets
            .SelectMany(s => s.Defects.Select(d => (Sheet: s, Defect: d)))
            .GroupBy(x => (x.Defect.State, x.Defect.Where, x.Defect.What))
            .OrderByDescending(g => g.First().Defect.Severity)
            .ThenBy(g => g.First().Defect.State)
            .ToList();

        sb.AppendLine("## 缺陷");
        sb.AppendLine();
        if (grouped.Count == 0)
        {
            sb.AppendLine("（无）");
        }
        else
        {
            foreach (var g in grouped)
            {
                var first = g.First().Defect;
                var widths = string.Join(", ", g.Select(x => $"{x.Sheet.Width}").Distinct().OrderBy(w => w));
                sb.AppendLine($"- **[{first.Severity}] {first.State}** · `{first.Where}`  ");
                sb.AppendLine($"  {first.What}");
                sb.AppendLine($"  出现宽度：{widths}dp");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## 逐屏截图");
        sb.AppendLine();
        sb.AppendLine("| 状态 | 尺寸 | 截图 | 缺陷数 |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var s in sheets.OrderBy(s => s.State).ThenBy(s => s.Width))
        {
            sb.AppendLine($"| {s.State} | {s.Width}x{s.Height} | `{Path.GetFileName(s.Path)}` | {s.Defects.Count} |");
        }

        return sb.ToString();
    }
}
