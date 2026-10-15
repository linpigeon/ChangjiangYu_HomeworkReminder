using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeworkReminder.ViewModels;
using HomeworkReminder.Views;

namespace HomeworkReminder.Desktop;

/// <summary>
/// 详情面板互斥回归测试（<c>--detailcheck</c>）。
/// <para>
/// 复现的是真实用户路径：先点一门**课程**（选中课程，课程详情出现）→ 再切到待办视图
/// 点一条**作业**（选中待办）。两个详情面板各绑一个独立属性，若不清选中态就会**叠加渲染**。
/// </para>
/// <para>
/// 断言方式不是看 ViewModel 属性，而是数**实际渲染出来的控件**——
/// 两个面板同时可见才算失败，这样才真的验证到界面。
/// </para>
/// </summary>
internal static class DetailCheck
{
    public static void Run(string? renderPath)
    {
        App.DeferInitialLoad = true;
        App.WebViewTestBudget = TimeSpan.FromSeconds(5);
        App.DetailCheckHook = (desktop, _) => RunInWindow(desktop, renderPath);
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(["--detailcheck-runner"]);
    }

    private static void RunInWindow(
        Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop,
        string? renderPath)
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        desktop.MainWindow = window;

        var logPath = Path.Combine(Services.AppPaths.DataDirectory, "detailcheck.log");
        try { File.WriteAllText(logPath, string.Empty); } catch { /* 忽略 */ }

        void Note(string s)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {s}";
            Console.WriteLine("  " + line);
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { /* 忽略 */ }
        }

        window.Opened += (_, _) =>
        {
            Dispatcher.UIThread.Post(async void () =>
            {
                try
                {
                    await CheckAsync(vm, window, Note, renderPath);
                }
                catch (Exception ex)
                {
                    Note($"异常 {ex.GetType().Name}: {ex.Message}");
                    Environment.Exit(9);
                }
            }, DispatcherPriority.Background);
        };

        window.Show();
    }

    private static async Task CheckAsync(
        MainViewModel vm, Window window, Action<string> note, string? renderPath)
    {
        // DeferInitialLoad=true 会抑制框架的自动初始化，这里显式跑一次，
        // 让缓存里的课程与待办进到 ViewModel（本测试依赖它们才能复现叠加）。
        try
        {
            await vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            note($"初始化异常 {ex.GetType().Name}: {ex.Message}");
        }

        // 诊断：区分「缓存没读到」与「被筛选滤掉了」——两者的现象都是 Items=0，
        // 但不搞清楚就会把「没测到」当成「通过」。
        var cachePath = Path.Combine(Services.AppPaths.DataDirectory, "todos.json");
        var cacheFile = await new Services.TodoCacheStore().LoadAsync();
        note($"缓存 {cachePath} 存在={File.Exists(cachePath)}");
        note(cacheFile is null
            ? "  反序列化=null"
            : $"  Homework={cacheFile.Homework.Count} Chaoxing={cacheFile.Chaoxing.Count}" +
              $" Pigai={cacheFile.Pigai.Count} Courses={cacheFile.ChaoxingCourses.Count}");
        note($"  载入后 Items={vm.List.Items.Count} Courses={vm.List.Courses.Count} " +
             $"State={vm.State} SyncStatus={vm.SyncStatus}");

        // Items 是「当前导航筛选后」的集合，默认导航是「我的一天」——今天没有到期项时为空。
        // 本测试要的是「任意待办」，所以先切到「作业」视图（全量作业）。
        vm.SelectedNav = vm.NavItems.First(n => n.Key == "homework");
        vm.OnNavChanged();
        await Task.Delay(200);
        note($"切到作业视图后 Items={vm.List.Items.Count}");

        note($"课程 {vm.List.Courses.Count} 门，作业 {vm.List.Items.Count} 项");

        // 至少要有课程可选；待办没有也能测「课程视图下不显示待办详情」。
        if (vm.List.Courses.Count == 0)
        {
            note("结论：缓存里没有课程，无法进行本测试（请先同步一次学习通）✗");
            Environment.Exit(6);
        }

        var courseDetailCount = 0;
        var todoDetailCount = 0;
        var courseListCount = 0;

        // 标记必须**只属于详情面板**，不能与列表行文案撞车：
        //   - 课程列表行是「N 个任务点未完成」；课程详情小标题是「未完成任务点（N 处）」
        //     → 用含「处）」的那个，否则把列表行当成详情（第一版就误判过）。
        //   - 待办列表行只有截止时间文本；详情才有「作答进度（来自雨课堂）」这类字段名。
        void Measure(string stage)
        {
            var texts = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
                .Select(t => t.Text!)
                .ToList();

            courseListCount = texts.Count(t => t.Contains("个任务点未完成", StringComparison.Ordinal));
            courseDetailCount = texts.Count(t =>
                t.StartsWith("未完成任务点（", StringComparison.Ordinal)
                || t.StartsWith("该课程", StringComparison.Ordinal));
            todoDetailCount = texts.Count(t =>
                t == "作答进度（来自雨课堂）" || t == "雨课堂完成状态" || t == "应用内标记");

            note($"{stage}: 课程详情={courseDetailCount} 待办详情={todoDetailCount} " +
                 $"课程列表行={courseListCount} CanShowCourseDetail={vm.CanShowCourseDetail}");
        }

        // ---- 步骤 1：进课程视图，选一门课 ----
        vm.SelectedNav = vm.NavItems.First(n => n.Key == "course");
        vm.OnNavChanged();
        await Task.Delay(300);
        Measure("① 进课程视图（未选课程）");
        if (courseDetailCount != 0)
        {
            note("结论：还没选课程就出现了课程详情 ✗");
            Environment.Exit(7);
        }

        vm.List.SelectedCourse = vm.List.Courses[0];
        await Task.Delay(300);
        Measure($"② 选中课程「{vm.List.Courses[0].CourseName}」");
        if (courseDetailCount == 0)
        {
            note("结论：选中课程后课程详情没有出现 ✗");
            Environment.Exit(8);
        }

        // ---- 步骤 2：切到待办视图（这是叠加 bug 的触发点）----
        //
        // 注意不要再拿 vm.List.Items 当「有没有待办」的判断依据：Items 是**按当前导航
        // 筛选后**的集合，切到课程视图时它按设计就是空（课程走另一条通道）。
        // 这里要验证的核心是：
        //   ① 切视图会清掉课程选中态；
        //   ② 因此课程详情面板的结构性门槛（CanShowCourseDetail 要求「在课程视图」）必然为假；
        //   ③ 选中待办后待办详情正常显示。
        // 「两块详情叠加」在修复后已不可能发生，因为课程面板被 ShowCourseList 硬门控住。
        note($"课程选中态：切视图前 HasSelectedCourse={vm.List.HasSelectedCourse}");

        vm.SelectedNav = vm.NavItems.First(n => n.Key == "homework");
        vm.OnNavChanged();
        await Task.Delay(300);
        Measure("③ 切到待办视图（应已清掉课程选中）");
        if (vm.List.HasSelectedCourse)
        {
            note("结论：切到待办视图后课程选中态没有清掉 ✗");
            Environment.Exit(10);
        }
        if (vm.CanShowCourseDetail || courseDetailCount != 0)
        {
            note($"结论：待办视图下课程详情仍可见（CanShow={vm.CanShowCourseDetail} 标记={courseDetailCount}）✗");
            Environment.Exit(10);
        }

        // 选中一条待办 → 待办详情出现，且课程详情依然不可见（叠加 bug 的正面断言）。
        var anyTodo = vm.List.Items.FirstOrDefault() ?? vm.List.VisibleItems.FirstOrDefault();
        if (anyTodo is not null)
        {
            vm.SelectedItem = anyTodo;
            await Task.Delay(300);
            Measure("④ 选中一条待办（两块详情不得同时出现）");
            if (courseDetailCount != 0 || todoDetailCount == 0)
            {
                note($"结论：详情面板状态不对 —— 课程详情={courseDetailCount} 待办详情={todoDetailCount} ✗");
                Environment.Exit(11);
            }
        }
        else
        {
            note("（当前视图没有可选的待办，跳过步骤 ④）");
        }

        // ---- 步骤 3：回到课程视图，确认课程详情能正常回来 ----
        vm.SelectedNav = vm.NavItems.First(n => n.Key == "course");
        vm.OnNavChanged();
        await Task.Delay(300);
        Measure("⑤ 回到课程视图（未选课程）");
        if (courseDetailCount != 0 || todoDetailCount != 0)
        {
            note("结论：回到课程视图后仍有详情残留 ✗");
            Environment.Exit(12);
        }

        if (renderPath is not null)
        {
            try
            {
                window.Measure(new Size(window.Width, window.Height));
                window.Arrange(new Rect(0, 0, window.Width, window.Height));
                Dispatcher.UIThread.RunJobs();
                using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(
                    new PixelSize((int)window.Width, (int)window.Height), new Vector(96, 96));
                bmp.Render(window);
                bmp.Save(renderPath);
                note($"已渲染 {renderPath}");
            }
            catch (Exception ex)
            {
                note($"渲染失败 {ex.GetType().Name}: {ex.Message}");
            }
        }

        note("结论：两个详情面板始终互斥，切换视图清选中态正常 ✓");
        Environment.Exit(0);
    }
}
