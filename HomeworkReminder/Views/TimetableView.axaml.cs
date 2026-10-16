using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>
/// 课表页交互：周次切换、显示开关、课块选中、打开教务导入、关闭返回待办。
/// <para>
/// 左侧节次刻度与右侧网格是两个 <see cref="ScrollViewer"/>，纵向必须同步，
/// 否则滚动后「第 3 节」的时间会跟课块错位——这比不滚还糟，所以在这里做联动。
/// </para>
/// </summary>
public partial class TimetableView : UserControl
{
    public TimetableView()
    {
        InitializeComponent();

        // 只同步纵向偏移：横向滚动属于「看周六周日」，不该带动节次列。
        PeriodScroll.ScrollChanged += (_, _) => SyncVertical(PeriodScroll, GridScroll);
        GridScroll.ScrollChanged += (_, _) =>
        {
            SyncVertical(GridScroll, PeriodScroll);
            SyncGridHorizontal();
        };

        // 课块是绝对定位的，画布宽度必须先知道 → 宽度变化时告诉 ViewModel 重算。
        // 用课块滚动区的**可视区宽度**（= 控件宽度 - 节次刻度列）而不是整个容器宽度：
        // 那才是真正留给 7 天的宽度，也才与 DaysWidth 的口径一致。
        GridScroll.SizeChanged += (_, _) => SetGridWidth(GridScroll.Viewport.Width);

        // 空状态提示与详情面板的边界都按实测值写（原因见 ApplyEmptyStateBounds），
        // 并加一条设备日志：这类几何在真机上出错时，只有实测量能定位。
        LayoutUpdated += (_, _) =>
        {
            ApplyEmptyStateBounds();
            ApplyDetailCardWidth();
        };

        // **绝对定位必须在这里显式设置**：Canvas.Left/Top 是附加属性，
        // 在 DataTemplate 里绑定它们实测不生效——所有课块都会堆在 (0,0)，
        // 表现为「课表只显示最后一节、上面一片空白」。
        // 容器由 ItemsControl 生成，ContainerPrepared 是我们唯一能可靠拿到它的时机
        //（该事件只给 Container 与 Index，所以用 Index 回查数据项）。
        PeriodItems.ContainerPrepared += (_, e) =>
        {
            var periods = Vm?.Timetable.Periods;
            if (e.Container is Control c && periods is not null && e.Index >= 0 && e.Index < periods.Count)
            {
                // 横向占满刻度列（宽度在 XAML 里定死），内容由内层 StackPanel 居中——
                // ContainerPrepared 触发时 c.Bounds 还是 0，量不到宽度，不能靠它算居中偏移。
                Canvas.SetLeft(c, 0);
                Canvas.SetTop(c, periods[e.Index].Top);
            }
        };
        BlockItems.ContainerPrepared += (_, e) =>
        {
            var blocks = Vm?.Timetable.Blocks;
            if (e.Container is Control c && blocks is not null && e.Index >= 0 && e.Index < blocks.Count)
            {
                Canvas.SetLeft(c, blocks[e.Index].X);
                Canvas.SetTop(c, blocks[e.Index].Y);
            }
        };

        // 点空白处收起详情：详情面板本身与网格同级，所以在整个控件上监听。
        AddHandler(PointerPressedEvent, OnSurfacePressed, RoutingStrategies.Tunnel);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>
    /// 空状态提示的左右边界：**用实测 Bounds 直接写**，不用绑定。
    /// <para>
    /// 为什么不用绑定（这是真机实测的结论，不是偏好）：
    /// 先写过固定 <c>Margin="94"</c> —— 94 是手算的「26 外边距 + 68 刻度列」，
    /// 而刻度列实际占位是 74（68 + 6 间距），于是左边多伸出 6dp 切进刻度列；
    /// 再改成 <c>ElementName=PeriodScroll</c> 绑刻度列宽度 —— **在 Android 真机上没生效**：
    /// 设备实测叠加层左边缘停在 x=308px（77dp），而节次块占 x=104..375px（26..94dp），
    /// 横向压住 17dp，第 3、4 节的时间被切成「10:00-10:4」「10:55-11:4」。
    /// </para>
    /// <para>
    /// 绑定的求值时机不可靠：刻度列自身宽度的变化不一定能把外层这个绑定重新求值。
    /// 这里改为在 <see cref="LayoutUpdated"/> 里读两边**已经布置完**的 Bounds：
    /// 左边界 = 刻度列右边界（自带 6dp 间距），右边界 = 课块区右边界 - 2dp。
    /// 值没变就不写，避免 LayoutUpdated 自激。
    /// </para>
    /// </summary>
    private void ApplyEmptyStateBounds()
    {
        if (!EmptyStateBox.IsVisible) return;

        var left = PeriodScroll.Bounds.Width + PeriodScroll.Margin.Right;
        var right = Bounds.Width - GridScroll.Bounds.Width - GridScroll.Margin.Right + 2;
        if (left <= 0 || right <= 0 || Bounds.Width <= 0) return;

        var thickness = new Thickness(left, 0, right, 0);
        if (EmptyStateBox.Margin == thickness) return;

        EmptyStateBox.Margin = thickness;
        TimetableLayoutTrace($"空状态边界: 本视图宽={Bounds.Width:0.#} 刻度列={PeriodScroll.Bounds.Width:0.#} "
                             + $"课块区={GridScroll.Bounds.Width:0.#} → margin=({thickness.Left:0.#},0,{thickness.Right:0.#},0)");
    }

    private Border? _detailCard;

    /// <summary>
    /// 课程详情面板的宽度：同样按实测值写，窄屏要能压窄。
    /// <para>
    /// 写死 296 时，320dp 手机上左边缘落到 -10dp、整块被切（裁剪 4.8dp）；
    /// 改成绑定课块区宽度后与空状态那条一样在真机上不生效。
    /// 这里取「课块区宽度 − 右外边距（34）」，下限 200 保证内容仍可读，
    /// 上限由 XAML 的 <c>MaxWidth=296</c> 兜住（宽屏仍是原设计版式）。
    /// </para>
    /// <para>
    /// 面板在 <c>DataTemplate</c> 里，**<c>x:Name</c> 不会生成字段**（模板是独立命名域），
    /// 所以按样式类 <c>tt-detail</c> 从可视树里找一次并缓存。
    /// </para>
    /// </summary>
    private void ApplyDetailCardWidth()
    {
        _detailCard ??= this.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("tt-detail"));
        if (_detailCard is null) return;

        var width = Math.Clamp(GridScroll.Bounds.Width - 34, 200, 296);
        if (Math.Abs(_detailCard.Width - width) < 0.5) return;

        _detailCard.Width = width;
        TimetableLayoutTrace($"详情面板宽度: 课块区={GridScroll.Bounds.Width:0.#} → width={width:0.#}");
    }

    private string? _tracePath;
    /// <summary>
    /// 写一条课表布局诊断日志（数据目录下 <c>timetable-layout.log</c>）。
    /// <para>
    /// 存在的理由：手机上没有控制台，而这类「几何量在真机上和预期不符」的问题
    /// 只能靠实测数字定位——本轮就是靠它把「叠加层左边缘 77dp、刻度列 26..94dp」
    /// 这两条实测值拿出来，才判定绑定没生效。日志只在值变化时写，不会刷屏。
    /// </para>
    /// </summary>
    private void TimetableLayoutTrace(string message)
    {
        try
        {
            _tracePath ??= System.IO.Path.Combine(Services.AppPaths.DataDirectory, "timetable-layout.log");
            System.IO.File.AppendAllText(
                _tracePath,
                $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 诊断日志写不进去不该影响界面。
        }
    }

    /// <summary>避免两个 ScrollViewer 互相触发导致抖动。</summary>
    private bool _syncing;

    private void SetGridWidth(double width)
    {
        if (Vm is not { } vm || width <= 0) return;
        if (Math.Abs(vm.Timetable.GridWidth - width) < 0.5) return;
        vm.Timetable.GridWidth = width;
    }

    private void SyncVertical(ScrollViewer source, ScrollViewer target)
    {
        if (_syncing) return;

        var y = source.Offset.Y;
        if (Math.Abs(target.Offset.Y - y) < 0.5) return;   // 已经对齐，别再写回去（否则会互相触发）

        _syncing = true;
        try
        {
            target.Offset = target.Offset.WithY(y);
        }
        catch (Exception)
        {
            // 布局尚未完成时 Offset 可能短暂非法；下一帧会再同步一次。
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// 列头跟着课块一起横向滚动。
    /// <para>
    /// 列头与课块是同一个 <c>DaysWidth</c> 宽的两条独立内容，若不同步，
    /// 网格滚到周六时列头还停在周一，**课块就会落在错误的星期下面**——
    /// 这比「看不见」严重得多（实测：周三的课显示在周一列头下）。
    /// </para>
    /// </summary>
    private void SyncGridHorizontal()
    {
        if (_syncing) return;

        var x = GridScroll.Offset.X;
        if (Math.Abs(DayHeaderScroll.Offset.X - x) < 0.5) return;

        _syncing = true;
        try
        {
            DayHeaderScroll.Offset = DayHeaderScroll.Offset.WithX(x);
        }
        catch (Exception)
        {
            // 同上：布局中途 Offset 可能短暂非法。
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// 点课块 → 选中它；点其它任何位置 → 收起详情。
    /// <para>
    /// 用隧道阶段统一处理：课块自己在 <see cref="OnBlockPressed"/> 里标记已处理，
    /// 因此这里只在「没点到课块」时收起，不必给详情面板之外的所有元素都挂事件。
    /// </para>
    /// </summary>
    private void OnSurfacePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || !vm.Timetable.HasSelection) return;

        // 详情面板内部的点击不收起（否则点面板里的关闭按钮之外的地方会闪一下）。
        if (e.Source is Visual visual && visual.FindAncestorOfType<Border>(true) is { } border
            && border.Classes.Contains("tt-detail"))
        {
            return;
        }

        vm.Timetable.SelectedBlock = null;
    }

    /// <summary>点课块：选中并在详情面板里展示。</summary>
    private void OnBlockPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: TimetableBlockViewModel block }) return;
        if (Vm is not { } vm) return;

        vm.Timetable.SelectedBlock = block;
        e.Handled = true;
    }

    private void OnCloseDetailClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.Timetable.SelectedBlock = null;
    }

    private void OnPrevWeekClick(object? sender, RoutedEventArgs e) => Vm?.ShiftTimetableWeek(-1);

    private void OnNextWeekClick(object? sender, RoutedEventArgs e) => Vm?.ShiftTimetableWeek(1);

    private void OnCurrentWeekClick(object? sender, RoutedEventArgs e) => Vm?.GotoCurrentTimetableWeek();

    private void OnImportClick(object? sender, RoutedEventArgs e) => Vm?.OpenJwImport();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Vm?.CloseTimetable();
}
